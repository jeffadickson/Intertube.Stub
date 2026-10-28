using System.Security.Cryptography;

namespace Intertube.Stub.Sftp.Ssh;

/// <summary>SSH message numbers used by the server.</summary>
internal static class SshMessage
{
    public const byte Disconnect = 1;
    public const byte Ignore = 2;
    public const byte Unimplemented = 3;
    public const byte Debug = 4;
    public const byte ServiceRequest = 5;
    public const byte ServiceAccept = 6;
    public const byte ExtInfo = 7;
    public const byte KexInit = 20;
    public const byte NewKeys = 21;
    public const byte KexEcdhInit = 30;
    public const byte KexEcdhReply = 31;
    public const byte UserAuthRequest = 50;
    public const byte UserAuthFailure = 51;
    public const byte UserAuthSuccess = 52;
    public const byte UserAuthPkOk = 60;
    public const byte GlobalRequest = 80;
    public const byte RequestFailure = 82;
    public const byte ChannelOpen = 90;
    public const byte ChannelOpenConfirmation = 91;
    public const byte ChannelOpenFailure = 92;
    public const byte ChannelWindowAdjust = 93;
    public const byte ChannelData = 94;
    public const byte ChannelExtendedData = 95;
    public const byte ChannelEof = 96;
    public const byte ChannelClose = 97;
    public const byte ChannelRequest = 98;
    public const byte ChannelSuccess = 99;
    public const byte ChannelFailure = 100;
}

/// <summary>Algorithms a server offers, most preferred first.</summary>
internal sealed record SshAlgorithms(SshKeyPair HostKey, IReadOnlyList<string> Ciphers, IReadOnlyList<string> Macs)
{
    public static readonly string[] KeyExchanges = ["ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521"];
}

/// <summary>
/// The server side of the SSH transport layer (RFC 4253): version exchange, ECDH key exchange,
/// packet encryption, rekeying, OpenSSH's strict key exchange and the server-sig-algs extension.
/// One task reads; any task may send.
/// </summary>
internal sealed class SshTransport(Connection connection, SshAlgorithms algorithms) : IDisposable
{
    public const string ServerVersion = "SSH-2.0-Intertube.Stub_0.1";

    private const string StrictClient = "kex-strict-c-v00@openssh.com";
    private const string StrictServer = "kex-strict-s-v00@openssh.com";
    private const string ExtInfoClient = "ext-info-c";

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private PacketCipher _receive = PacketCipher.None;
    private PacketCipher _send = PacketCipher.None;
    private uint _receiveSequence;
    private uint _sendSequence;
    private bool _strict;
    private bool _extInfo;

    public string ClientVersion { get; private set; } = "";

    public byte[] SessionId { get; private set; } = [];

    public string? NegotiatedCipher { get; private set; }

    public string? NegotiatedMac { get; private set; }

    public string? NegotiatedKeyExchange { get; private set; }

    /// <summary>Exchange versions and keys. Afterwards the connection is encrypted.</summary>
    public async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        await connection.WriteAsync(ServerVersion + "\r\n", cancellationToken).ConfigureAwait(false);
        for (var lines = 0; ; lines++)
        {
            var line = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The client closed the connection before sending its version.");
            if (line.StartsWith("SSH-", StringComparison.Ordinal))
            {
                if (!line.StartsWith("SSH-2.0-", StringComparison.Ordinal) && !line.StartsWith("SSH-1.99-", StringComparison.Ordinal))
                    throw new SshProtocolException($"Unsupported protocol version \"{line}\".");
                ClientVersion = line;
                break;
            }
            if (lines > 20) throw new SshProtocolException("No SSH version line from the client.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var serverKexInit = await SendKexInitAsync(cancellationToken).ConfigureAwait(false);
            var clientKexInit = await ReadPacketAsync().ConfigureAwait(false);
            if (clientKexInit[0] != SshMessage.KexInit) throw new SshProtocolException($"Expected KEXINIT but got message {clientKexInit[0]}.");
            await KeyExchangeAsync(clientKexInit, serverKexInit, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The next message for the layers above, or null once the client disconnects.</summary>
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[] payload;
            try
            {
                payload = await ReadPacketAsync().ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
            switch (payload[0])
            {
                case SshMessage.Disconnect:
                    return null;
                case SshMessage.Ignore or SshMessage.Debug or SshMessage.Unimplemented:
                    continue;
                case SshMessage.KexInit:
                    await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var serverKexInit = await SendKexInitAsync(cancellationToken).ConfigureAwait(false);
                        await KeyExchangeAsync(payload, serverKexInit, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                    continue;
                default:
                    return payload;
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendLockedAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask SendAsync(SshWriter message, CancellationToken cancellationToken) => SendAsync(message.Memory, cancellationToken);

    /// <summary>Tell the client why we are hanging up. Best effort: the connection may already be gone.</summary>
    public async Task DisconnectAsync(uint reason, string description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendAsync(new SshWriter(SshMessage.Disconnect).UInt32(reason).String(description).String(""), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async ValueTask<byte[]> ReadPacketAsync()
    {
        var payload = await _receive.OpenAsync(_receiveSequence, count => connection.ReadExactlyAsync(count)).ConfigureAwait(false);
        _receiveSequence++;
        if (payload.Length == 0) throw new SshProtocolException("Empty message.");
        return payload;
    }

    private async ValueTask SendLockedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var packet = _send.Seal(_sendSequence, payload.Span);
        _sendSequence++;
        await connection.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> SendKexInitAsync(CancellationToken cancellationToken)
    {
        var kex = SessionId.Length == 0 ? [.. SshAlgorithms.KeyExchanges, StrictServer] : SshAlgorithms.KeyExchanges;
        var message = new SshWriter(SshMessage.KexInit)
            .Raw(RandomNumberGenerator.GetBytes(16))
            .NameList(kex)
            .NameList(algorithms.HostKey.SignatureAlgorithms)
            .NameList(algorithms.Ciphers).NameList(algorithms.Ciphers)
            .NameList(algorithms.Macs).NameList(algorithms.Macs)
            .NameList(["none"]).NameList(["none"])
            .NameList([]).NameList([])
            .Bool(false)
            .UInt32(0);
        await SendLockedAsync(message.Memory, cancellationToken).ConfigureAwait(false);
        return message.ToArray();
    }

    private async Task KeyExchangeAsync(byte[] clientKexInit, byte[] serverKexInit, CancellationToken cancellationToken)
    {
        // Skip the message number and cookie. The reader is scoped because a ref struct cannot live across an await.
        string[] clientKex, clientHostKey, cipherIn, cipherOut, macIn, macOut, compressionIn, compressionOut;
        bool guessFollows;
        {
            var r = new SshReader(clientKexInit.AsSpan(17));
            clientKex = r.NameList();
            clientHostKey = r.NameList();
            cipherIn = r.NameList();
            cipherOut = r.NameList();
            macIn = r.NameList();
            macOut = r.NameList();
            compressionIn = r.NameList();
            compressionOut = r.NameList();
            r.NameList();
            r.NameList();
            guessFollows = r.Bool();
        }

        var first = SessionId.Length == 0;
        if (first)
        {
            _strict = clientKex.Contains(StrictClient);
            _extInfo = clientKex.Contains(ExtInfoClient);
        }

        var kex = Choose("key exchange", clientKex, SshAlgorithms.KeyExchanges);
        var hostKeyAlgorithm = Choose("host key", clientHostKey, algorithms.HostKey.SignatureAlgorithms);
        var receiveCipher = Choose("cipher", cipherIn, algorithms.Ciphers);
        var sendCipher = Choose("cipher", cipherOut, algorithms.Ciphers);
        var receiveMac = PacketCipher.IsAead(receiveCipher) ? "" : Choose("MAC", macIn, algorithms.Macs);
        var sendMac = PacketCipher.IsAead(sendCipher) ? "" : Choose("MAC", macOut, algorithms.Macs);
        Choose("compression", compressionIn, ["none"]);
        Choose("compression", compressionOut, ["none"]);
        NegotiatedKeyExchange = kex;
        NegotiatedCipher = receiveCipher;
        NegotiatedMac = receiveMac.Length == 0 ? null : receiveMac;

        // A client may send its first key exchange packet early, guessing our choices; a wrong guess is discarded.
        var guessedRight = clientKex[0] == kex && clientHostKey[0] == hostKeyAlgorithm;
        var init = await ReadPacketAsync().ConfigureAwait(false);
        if (guessFollows && !guessedRight) init = await ReadPacketAsync().ConfigureAwait(false);
        while (!_strict && init[0] is SshMessage.Ignore or SshMessage.Debug) init = await ReadPacketAsync().ConfigureAwait(false);
        if (init[0] != SshMessage.KexEcdhInit) throw new SshProtocolException($"Expected KEX_ECDH_INIT but got message {init[0]}.", SshProtocolException.KeyExchangeFailed);
        var clientPoint = ReadClientPoint(init);

        var curveName = kex["ecdh-sha2-".Length..];
        var (curve, size, hash) = SshPublicKey.EcdsaCurve(curveName);
        byte[] sharedSecret, serverPoint;
        using (var ecdh = ECDiffieHellman.Create(curve))
        {
            ECDiffieHellmanPublicKey clientKey;
            try
            {
                using var peer = ECDiffieHellman.Create(new ECParameters { Curve = curve, Q = SshPublicKey.DecodePoint(clientPoint, size) });
                clientKey = peer.PublicKey;
            }
            catch (CryptographicException)
            {
                throw new SshProtocolException("The client's ECDH point is not on the curve.", SshProtocolException.KeyExchangeFailed);
            }
            using (clientKey) sharedSecret = ecdh.DeriveRawSecretAgreement(clientKey);
            var q = ecdh.ExportParameters(false).Q;
            serverPoint = [4, .. q.X!, .. q.Y!];
        }

        var secret = new SshWriter().MPInt(sharedSecret).ToArray();
        var hostKeyBlob = algorithms.HostKey.PublicKey.Blob.Span;
        var exchange = new SshWriter(1024)
            .String(ClientVersion).String(ServerVersion)
            .String(clientKexInit).String(serverKexInit)
            .String(hostKeyBlob)
            .String(clientPoint).String(serverPoint)
            .Raw(secret);
        var exchangeHash = Hash(hash, exchange.Span);
        if (first) SessionId = exchangeHash;

        var reply = new SshWriter(SshMessage.KexEcdhReply)
            .String(hostKeyBlob)
            .String(serverPoint)
            .String(algorithms.HostKey.Sign(hostKeyAlgorithm, exchangeHash));
        await SendLockedAsync(reply.Memory, cancellationToken).ConfigureAwait(false);
        await SendLockedAsync(new byte[] { SshMessage.NewKeys }, cancellationToken).ConfigureAwait(false);

        byte[] Derive(char letter, int length)
        {
            var key = Hash(hash, [.. secret, .. exchangeHash, (byte)letter, .. SessionId]);
            while (key.Length < length) key = [.. key, .. Hash(hash, [.. secret, .. exchangeHash, .. key])];
            return key[..length];
        }

        var (sendKeySize, sendIvSize) = PacketCipher.Sizes(sendCipher);
        var oldSend = _send;
        _send = PacketCipher.Create(sendCipher, sendMac, Derive('D', sendKeySize), Derive('B', sendIvSize), Derive('F', PacketCipher.MacKeySize(sendMac)));
        oldSend.Dispose();
        if (_strict) _sendSequence = 0;

        var newKeys = await ReadPacketAsync().ConfigureAwait(false);
        if (newKeys[0] != SshMessage.NewKeys) throw new SshProtocolException($"Expected NEWKEYS but got message {newKeys[0]}.", SshProtocolException.KeyExchangeFailed);
        var (receiveKeySize, receiveIvSize) = PacketCipher.Sizes(receiveCipher);
        var oldReceive = _receive;
        _receive = PacketCipher.Create(receiveCipher, receiveMac, Derive('C', receiveKeySize), Derive('A', receiveIvSize), Derive('E', PacketCipher.MacKeySize(receiveMac)));
        oldReceive.Dispose();
        if (_strict) _receiveSequence = 0;

        if (first && _extInfo)
        {
            var signatureAlgorithms = new[] { "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-512", "rsa-sha2-256" };
            await SendLockedAsync(new SshWriter(SshMessage.ExtInfo).UInt32(1).String("server-sig-algs").NameList(signatureAlgorithms).Memory, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] ReadClientPoint(byte[] init)
    {
        var reader = new SshReader(init);
        reader.Byte();
        return reader.String().ToArray();
    }

    private static string Choose(string what, string[] client, IReadOnlyList<string> server) =>
        client.FirstOrDefault(server.Contains)
        ?? throw new SshProtocolException($"No common {what} algorithm. Client offered {string.Join(",", client)}; server supports {string.Join(",", server)}.", SshProtocolException.KeyExchangeFailed);

    private static byte[] Hash(HashAlgorithmName name, ReadOnlySpan<byte> data) =>
        name == HashAlgorithmName.SHA256 ? SHA256.HashData(data)
        : name == HashAlgorithmName.SHA384 ? SHA384.HashData(data)
        : SHA512.HashData(data);

    public void Dispose()
    {
        _receive.Dispose();
        _send.Dispose();
    }
}
