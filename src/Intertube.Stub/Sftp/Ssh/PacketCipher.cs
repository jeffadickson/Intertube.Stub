using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Intertube.Stub.Sftp.Ssh;

/// <summary>Reads exactly this many bytes from the connection.</summary>
internal delegate ValueTask<byte[]> ReadExactly(int count);

/// <summary>
/// Binary packet protocol for one direction (RFC 4253 section 6): framing, padding, encryption and
/// integrity. A new instance takes over after every NEWKEYS.
/// </summary>
internal abstract class PacketCipher : IDisposable
{
    /// <summary>Largest packet accepted; OpenSSH's own limit is 256 KiB.</summary>
    public const int MaxPacketLength = 256 * 1024 + 64;

    public static readonly string[] Ciphers = ["aes128-gcm@openssh.com", "aes256-gcm@openssh.com", "aes128-ctr", "aes192-ctr", "aes256-ctr"];
    public static readonly string[] Macs = ["hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com", "hmac-sha2-256", "hmac-sha2-512"];

    public static PacketCipher None { get; } = new PlainCipher();

    protected abstract int BlockSize { get; }

    /// <summary>True when the length field is sent in the clear (encrypt-then-MAC and AEAD modes).</summary>
    protected abstract bool PlainLength { get; }

    public static bool IsAead(string cipher) => cipher.EndsWith("-gcm@openssh.com", StringComparison.Ordinal);

    public static (int KeySize, int IvSize) Sizes(string cipher) => cipher switch
    {
        "aes128-gcm@openssh.com" => (16, 12),
        "aes256-gcm@openssh.com" => (32, 12),
        "aes128-ctr" => (16, 16),
        "aes192-ctr" => (24, 16),
        "aes256-ctr" => (32, 16),
        _ => throw new SshProtocolException($"Unsupported cipher {cipher}."),
    };

    public static int MacKeySize(string mac) => mac.StartsWith("hmac-sha2-512", StringComparison.Ordinal) ? 64 : 32;

    public static PacketCipher Create(string cipher, string mac, byte[] key, byte[] iv, byte[] macKey) =>
        IsAead(cipher) ? new GcmCipher(key, iv) : new CtrCipher(key, iv, mac, macKey);

    /// <summary>Frame, pad, encrypt and authenticate one payload.</summary>
    public abstract byte[] Seal(uint sequence, ReadOnlySpan<byte> payload);

    /// <summary>Read one packet and return its payload.</summary>
    public abstract ValueTask<byte[]> OpenAsync(uint sequence, ReadExactly read);

    public virtual void Dispose()
    {
    }

    /// <summary>Lay out <c>uint32 length, byte padding, payload, padding</c> plus <paramref name="trailer"/> spare bytes.</summary>
    protected byte[] Frame(ReadOnlySpan<byte> payload, int trailer)
    {
        var covered = PlainLength ? 1 + payload.Length : 5 + payload.Length;
        var padding = BlockSize - covered % BlockSize;
        if (padding < 4) padding += BlockSize;
        var packetLength = 1 + payload.Length + padding;
        var packet = new byte[4 + packetLength + trailer];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)packetLength);
        packet[4] = (byte)padding;
        payload.CopyTo(packet.AsSpan(5));
        RandomNumberGenerator.Fill(packet.AsSpan(5 + payload.Length, padding));
        return packet;
    }

    protected int CheckLength(uint length)
    {
        var aligned = PlainLength ? length : length + 4;
        if (length < 5 || length > MaxPacketLength || aligned % BlockSize != 0)
            throw new SshProtocolException($"Bad packet length {length}.");
        return (int)length;
    }

    /// <summary>The payload of a decrypted <c>byte padding, payload, padding</c> region.</summary>
    protected static byte[] Payload(ReadOnlySpan<byte> region)
    {
        var padding = region[0];
        if (padding < 4 || padding >= region.Length) throw new SshProtocolException($"Bad padding length {padding}.");
        return region[1..^padding].ToArray();
    }

    private sealed class PlainCipher : PacketCipher
    {
        protected override int BlockSize => 8;

        protected override bool PlainLength => false;

        public override byte[] Seal(uint sequence, ReadOnlySpan<byte> payload) => Frame(payload, 0);

        public override async ValueTask<byte[]> OpenAsync(uint sequence, ReadExactly read)
        {
            var length = CheckLength(BinaryPrimitives.ReadUInt32BigEndian(await read(4).ConfigureAwait(false)));
            return Payload(await read(length).ConfigureAwait(false));
        }
    }

    /// <summary>AES-GCM as OpenSSH defines it: the length is the associated data and the nonce counts packets.</summary>
    private sealed class GcmCipher(byte[] key, byte[] iv) : PacketCipher
    {
        private const int TagSize = 16;
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _nonce = iv;

        protected override int BlockSize => 16;

        protected override bool PlainLength => true;

        public override byte[] Seal(uint sequence, ReadOnlySpan<byte> payload)
        {
            var packet = Frame(payload, TagSize);
            var body = packet.AsSpan(4, packet.Length - 4 - TagSize);
            _aes.Encrypt(_nonce, body, body, packet.AsSpan(packet.Length - TagSize), packet.AsSpan(0, 4));
            NextNonce();
            return packet;
        }

        public override async ValueTask<byte[]> OpenAsync(uint sequence, ReadExactly read)
        {
            var header = await read(4).ConfigureAwait(false);
            var length = CheckLength(BinaryPrimitives.ReadUInt32BigEndian(header));
            var rest = await read(length + TagSize).ConfigureAwait(false);
            var body = rest.AsSpan(0, length);
            try
            {
                _aes.Decrypt(_nonce, body, rest.AsSpan(length), body, header);
            }
            catch (CryptographicException)
            {
                throw new SshProtocolException("Message authentication failed.", SshProtocolException.MacError);
            }
            NextNonce();
            return Payload(body);
        }

        private void NextNonce() =>
            BinaryPrimitives.WriteUInt64BigEndian(_nonce.AsSpan(4), BinaryPrimitives.ReadUInt64BigEndian(_nonce.AsSpan(4)) + 1);

        public override void Dispose() => _aes.Dispose();
    }

    /// <summary>AES-CTR with HMAC-SHA2, either MAC-then-encrypt (RFC 4253) or OpenSSH's encrypt-then-MAC.</summary>
    private sealed class CtrCipher : PacketCipher
    {
        private readonly Aes _aes;
        private readonly byte[] _counter;
        private readonly IncrementalHash _mac;
        private readonly int _macSize;
        private readonly bool _encryptThenMac;

        public CtrCipher(byte[] key, byte[] iv, string mac, byte[] macKey)
        {
            _aes = Aes.Create();
            _aes.Key = key;
            _counter = iv;
            _encryptThenMac = mac.EndsWith("-etm@openssh.com", StringComparison.Ordinal);
            var algorithm = mac.StartsWith("hmac-sha2-512", StringComparison.Ordinal) ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256;
            _mac = IncrementalHash.CreateHMAC(algorithm, macKey);
            _macSize = _mac.HashLengthInBytes;
        }

        protected override int BlockSize => 16;

        protected override bool PlainLength => _encryptThenMac;

        public override byte[] Seal(uint sequence, ReadOnlySpan<byte> payload)
        {
            var packet = Frame(payload, _macSize);
            var data = packet.AsSpan(0, packet.Length - _macSize);
            if (_encryptThenMac)
            {
                Transform(data[4..]);
                Mac(sequence, data, packet.AsSpan(data.Length));
            }
            else
            {
                Mac(sequence, data, packet.AsSpan(data.Length));
                Transform(data);
            }
            return packet;
        }

        public override async ValueTask<byte[]> OpenAsync(uint sequence, ReadExactly read)
        {
            if (_encryptThenMac)
            {
                var header = await read(4).ConfigureAwait(false);
                var length = CheckLength(BinaryPrimitives.ReadUInt32BigEndian(header));
                var rest = await read(length + _macSize).ConfigureAwait(false);
                var packet = new byte[4 + length];
                header.CopyTo(packet, 0);
                rest.AsSpan(0, length).CopyTo(packet.AsSpan(4));
                Verify(sequence, packet, rest.AsSpan(length));
                Transform(packet.AsSpan(4));
                return Payload(packet.AsSpan(4));
            }
            else
            {
                var first = await read(BlockSize).ConfigureAwait(false);
                Transform(first);
                var length = CheckLength(BinaryPrimitives.ReadUInt32BigEndian(first));
                var rest = await read(4 + length - BlockSize + _macSize).ConfigureAwait(false);
                var packet = new byte[4 + length];
                first.CopyTo(packet, 0);
                rest.AsSpan(0, packet.Length - BlockSize).CopyTo(packet.AsSpan(BlockSize));
                Transform(packet.AsSpan(BlockSize));
                Verify(sequence, packet, rest.AsSpan(packet.Length - BlockSize));
                return Payload(packet.AsSpan(4));
            }
        }

        private void Mac(uint sequence, ReadOnlySpan<byte> data, Span<byte> destination)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(number, sequence);
            _mac.AppendData(number);
            _mac.AppendData(data);
            _mac.GetHashAndReset(destination);
        }

        private void Verify(uint sequence, ReadOnlySpan<byte> data, ReadOnlySpan<byte> received)
        {
            Span<byte> expected = stackalloc byte[_macSize];
            Mac(sequence, data, expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, received))
                throw new SshProtocolException("Message authentication failed.", SshProtocolException.MacError);
        }

        /// <summary>XOR with the keystream. SSH packets are whole blocks, so the counter stays block-aligned.</summary>
        private void Transform(Span<byte> data)
        {
            var counters = new byte[data.Length];
            for (var offset = 0; offset < data.Length; offset += 16)
            {
                _counter.CopyTo(counters, offset);
                for (var i = 15; i >= 0 && ++_counter[i] == 0; i--)
                {
                }
            }
            var keystream = _aes.EncryptEcb(counters, PaddingMode.None);
            for (var i = 0; i < data.Length; i++) data[i] ^= keystream[i];
        }

        public override void Dispose()
        {
            _aes.Dispose();
            _mac.Dispose();
        }
    }
}
