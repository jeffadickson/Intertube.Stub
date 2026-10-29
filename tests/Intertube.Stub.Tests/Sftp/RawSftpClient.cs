using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Intertube.Stub.Sftp;
using Intertube.Stub.Sftp.Ssh;

namespace Intertube.Stub.Tests.Sftp;

/// <summary>
/// A bare SSH client (ecdh-sha2-nistp256, aes128-gcm, password login) that opens the <c>sftp</c>
/// subsystem and then sends whatever SFTP packets a test builds, malformed or not, one at a time.
/// Real clients never send a bad handle or a truncated request; this one does.
/// </summary>
internal sealed class RawSftpClient : IAsyncDisposable
{
    public const byte Open = 3, Close = 4, Read = 5, Write = 6, FStat = 8, FSetStat = 10, OpenDir = 11, ReadDir = 12,
        RealPath = 16, Stat = 17, Extended = 200;

    public const uint FlagRead = 1;

    private const byte Init = 1, Version = 2;
    private const string Cipher = "aes128-gcm@openssh.com";
    private const string ClientVersion = "SSH-2.0-RawSftpClient";

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
    private readonly List<byte> _inbound = [];
    private PacketCipher _send = PacketCipher.None;
    private PacketCipher _receive = PacketCipher.None;
    private uint _sendSequence;
    private uint _receiveSequence;
    private uint _channel;
    private uint _nextId = 1;

    private RawSftpClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>The cipher list of the server's KEXINIT.</summary>
    public string[] OfferedCiphers { get; private set; } = [];

    /// <summary>The MAC list of the server's KEXINIT.</summary>
    public string[] OfferedMacs { get; private set; } = [];

    /// <summary>Connect, log in with a password (the server must accept it) and start the sftp subsystem.</summary>
    public static async Task<RawSftpClient> ConnectAsync(SftpServer server, string user = "test", string password = "secret")
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port);
        var client = new RawSftpClient(tcp);
        try
        {
            await client.HandshakeAsync(server.HostKey);
            await client.SendSshAsync(new SshWriter(SshMessage.ServiceRequest).String("ssh-userauth"));
            await client.ExpectAsync(SshMessage.ServiceAccept);
            await client.SendSshAsync(new SshWriter(SshMessage.UserAuthRequest).String(user).String("ssh-connection").String("password").Bool(false).String(password));
            await client.ExpectAsync(SshMessage.UserAuthSuccess);
            await client.SendSshAsync(new SshWriter(SshMessage.ChannelOpen).String("session").UInt32(0).UInt32(int.MaxValue).UInt32(SshChannel.LocalMaxPacket));
            client._channel = Fields(await client.ExpectAsync(SshMessage.ChannelOpenConfirmation), (ref r) => { r.UInt32(); return r.UInt32(); });
            await client.SendSshAsync(new SshWriter(SshMessage.ChannelRequest).UInt32(client._channel).String("subsystem").Bool(true).String("sftp"));
            await client.ExpectAsync(SshMessage.ChannelSuccess);
            await client.SendAsync(new SshWriter(Init).UInt32(3).ToArray());
            Assert.Equal(Version, (await client.ReceiveAsync())[0]);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Send a request of <paramref name="type"/> with a fresh id and the given fields, and return the reply.</summary>
    public async Task<Reply> RequestAsync(byte type, Action<SshWriter>? fields = null)
    {
        var id = _nextId++;
        var packet = new SshWriter(type).UInt32(id);
        fields?.Invoke(packet);
        await SendAsync(packet.ToArray());
        var reply = await ReceiveAsync();
        Assert.Equal(id, BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(1)));
        return new Reply(reply);
    }

    /// <summary>OPEN a file and return its handle, failing the test if the server refuses.</summary>
    public async Task<string> OpenFileAsync(string path, uint flags = FlagRead) =>
        (await RequestAsync(Open, w => w.String(path).UInt32(flags).UInt32(0))).Handle;

    /// <summary>OPENDIR a directory and return its handle, failing the test if the server refuses.</summary>
    public async Task<string> OpenDirectoryAsync(string path) => (await RequestAsync(OpenDir, w => w.String(path))).Handle;

    /// <summary>Send one SFTP packet (without its length prefix) exactly as given.</summary>
    public async Task SendAsync(byte[] packet)
    {
        var framed = new SshWriter().String(packet).Span;
        await SendSshAsync(new SshWriter(SshMessage.ChannelData).UInt32(_channel).String(framed));
    }

    /// <summary>The next SFTP packet from the server, without its length prefix.</summary>
    public async Task<byte[]> ReceiveAsync()
    {
        while (true)
        {
            if (_inbound.Count >= 4)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(_inbound.GetRange(0, 4).ToArray());
                if (_inbound.Count >= 4 + length)
                {
                    var packet = _inbound.GetRange(4, length).ToArray();
                    _inbound.RemoveRange(0, 4 + length);
                    return packet;
                }
            }
            var message = await ReceiveSshAsync();
            if (message[0] == SshMessage.ChannelData) _inbound.AddRange(Fields(message, (ref r) => { r.UInt32(); return r.String().ToArray(); }));
            else if (message[0] != SshMessage.ChannelWindowAdjust) Assert.Fail($"Unexpected SSH message {message[0]} on the SFTP channel.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _send.Dispose();
        _receive.Dispose();
        _tcp.Dispose();
        _timeout.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>An SFTP reply.</summary>
    public sealed class Reply(byte[] packet)
    {
        public byte Type => packet[0];

        public SftpStatus Status => Type == 101 ? (SftpStatus)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(5)) : SftpStatus.Ok;

        public string Message => Type == 101 ? Fields(packet, (ref r) => { r.UInt32(); r.UInt32(); return r.Utf8(); }) : "";

        public string Handle => Type == 102
            ? Fields(packet, (ref r) => { r.UInt32(); return r.Utf8(); })
            : throw new Xunit.Sdk.XunitException($"Expected a HANDLE reply but got type {Type}: {Status} {Message}");
    }

    // ---- SSH transport -------------------------------------------------------------------------

    private async Task HandshakeAsync(SshPublicKey hostKey)
    {
        var token = _timeout.Token;
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(ClientVersion + "\r\n"), token);
        var serverVersion = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            await _stream.ReadExactlyAsync(one, token);
            if (one[0] == '\n') break;
            if (one[0] != '\r') serverVersion.Append((char)one[0]);
        }

        var clientKexInit = new SshWriter(SshMessage.KexInit).Raw(RandomNumberGenerator.GetBytes(16))
            .NameList(["ecdh-sha2-nistp256"]).NameList([hostKey.Algorithm])
            .NameList([Cipher]).NameList([Cipher]).NameList(PacketCipher.Macs).NameList(PacketCipher.Macs)
            .NameList(["none"]).NameList(["none"]).NameList([]).NameList([]).Bool(false).UInt32(0).ToArray();
        await SendSshAsync(clientKexInit);
        var serverKexInit = await ExpectAsync(SshMessage.KexInit);
        (OfferedCiphers, OfferedMacs) = Fields(serverKexInit, (ref r) =>
        {
            r.Bytes(16);
            r.NameList();
            r.NameList();
            var ciphers = r.NameList();
            r.NameList();
            return (ciphers, r.NameList());
        });

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdh.ExportParameters(false).Q;
        byte[] clientPoint = [4, .. q.X!, .. q.Y!];
        await SendSshAsync(new SshWriter(SshMessage.KexEcdhInit).String(clientPoint));
        var reply = await ExpectAsync(SshMessage.KexEcdhReply);
        var (hostKeyBlob, serverPoint, signature) = Fields(reply, (ref r) => (r.String().ToArray(), r.String().ToArray(), r.String().ToArray()));
        byte[] sharedSecret;
        using (var peer = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverPoint[1..33], Y = serverPoint[33..] } }))
            sharedSecret = ecdh.DeriveRawSecretAgreement(peer.PublicKey);

        var secret = new SshWriter().MPInt(sharedSecret).ToArray();
        var exchangeHash = SHA256.HashData(new SshWriter(1024)
            .String(ClientVersion).String(serverVersion.ToString())
            .String(clientKexInit).String(serverKexInit)
            .String(hostKeyBlob).String(clientPoint).String(serverPoint)
            .Raw(secret).Span);
        Assert.Equal(hostKey.Blob.ToArray(), hostKeyBlob);
        Assert.True(hostKey.Verify(exchangeHash, signature, hostKey.Algorithm), "The host key signature does not verify.");

        byte[] Derive(char letter, int length)
        {
            var key = SHA256.HashData([.. secret, .. exchangeHash, (byte)letter, .. exchangeHash]);
            while (key.Length < length) key = [.. key, .. SHA256.HashData([.. secret, .. exchangeHash, .. key])];
            return key[..length];
        }

        await ExpectAsync(SshMessage.NewKeys);
        await SendSshAsync(new[] { SshMessage.NewKeys });
        var (keySize, ivSize) = PacketCipher.Sizes(Cipher);
        _send = PacketCipher.Create(Cipher, "", Derive('C', keySize), Derive('A', ivSize), []);
        _receive = PacketCipher.Create(Cipher, "", Derive('D', keySize), Derive('B', ivSize), []);
    }

    private Task SendSshAsync(SshWriter message) => SendSshAsync(message.ToArray());

    private async Task SendSshAsync(byte[] payload)
    {
        var packet = _send.Seal(_sendSequence++, payload);
        await _stream.WriteAsync(packet, _timeout.Token);
    }

    private async Task<byte[]> ReceiveSshAsync()
    {
        var payload = await _receive.OpenAsync(_receiveSequence++, async count =>
        {
            var buffer = new byte[count];
            await _stream.ReadExactlyAsync(buffer, _timeout.Token);
            return buffer;
        });
        return payload;
    }

    /// <summary>The next SSH message, skipping window adjustments and IGNORE/DEBUG; it must be <paramref name="type"/>.</summary>
    private async Task<byte[]> ExpectAsync(byte type)
    {
        while (true)
        {
            var message = await ReceiveSshAsync();
            if (message[0] is SshMessage.ChannelWindowAdjust or SshMessage.Ignore or SshMessage.Debug) continue;
            Assert.True(message[0] == type, $"Expected SSH message {type} but got {message[0]}.");
            return message;
        }
    }

    private delegate T ReaderFunc<T>(ref SshReader reader);

    /// <summary>Read fields after a message's type byte.</summary>
    private static T Fields<T>(byte[] message, ReaderFunc<T> read)
    {
        var reader = new SshReader(message.AsSpan(1));
        return read(ref reader);
    }
}
