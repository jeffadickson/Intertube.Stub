using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Intertube.Stub.Sftp.Ssh;

/// <summary>Thrown when a peer breaks the SSH or SFTP wire format.</summary>
internal sealed class SshProtocolException(string message, uint reason = SshProtocolException.ProtocolError) : Exception(message)
{
    public const uint ProtocolError = 2;
    public const uint KeyExchangeFailed = 3;
    public const uint MacError = 5;
    public const uint ServiceNotAvailable = 7;
    public const uint NoMoreAuthMethods = 14;

    public uint Reason { get; } = reason;
}

/// <summary>Builds an SSH wire-format message (RFC 4251 section 5).</summary>
internal sealed class SshWriter
{
    private byte[] _buffer;
    private int _length;

    public SshWriter(int capacity = 256) => _buffer = new byte[capacity];

    public SshWriter(byte messageType, int capacity = 256) : this(capacity) => Byte(messageType);

    public int Length => _length;

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

    public byte[] ToArray() => Span.ToArray();

    public SshWriter Byte(byte value)
    {
        Grow(1)[0] = value;
        return this;
    }

    public SshWriter Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    public SshWriter UInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Grow(4), value);
        return this;
    }

    public SshWriter UInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(Grow(8), value);
        return this;
    }

    public SshWriter Raw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Grow(bytes.Length));
        return this;
    }

    public SshWriter String(ReadOnlySpan<byte> bytes) => UInt32((uint)bytes.Length).Raw(bytes);

    public SshWriter String(string text) => String(Encoding.UTF8.GetBytes(text));

    public SshWriter NameList(IEnumerable<string> names) => String(string.Join(',', names));

    /// <summary>Unsigned big-endian magnitude, written as a positive mpint.</summary>
    public SshWriter MPInt(ReadOnlySpan<byte> unsignedBigEndian)
    {
        var start = 0;
        while (start < unsignedBigEndian.Length && unsignedBigEndian[start] == 0) start++;
        var magnitude = unsignedBigEndian[start..];
        if (magnitude.IsEmpty) return UInt32(0);
        var pad = (magnitude[0] & 0x80) != 0;
        UInt32((uint)(magnitude.Length + (pad ? 1 : 0)));
        if (pad) Byte(0);
        return Raw(magnitude);
    }

    public SshWriter MPInt(BigInteger value) => MPInt(value.ToByteArray(isUnsigned: true, isBigEndian: true));

    private Span<byte> Grow(int count)
    {
        if (_length + count > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }
}

/// <summary>Reads an SSH wire-format message; every overrun is a protocol error.</summary>
internal ref struct SshReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly int Remaining => _data.Length - _position;

    public readonly bool AtEnd => _position == _data.Length;

    public byte Byte() => Take(1)[0];

    public bool Bool() => Byte() != 0;

    public uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    public ReadOnlySpan<byte> String()
    {
        var length = UInt32();
        return length <= (uint)Remaining ? Take((int)length) : throw new SshProtocolException("Message is shorter than its fields.");
    }

    public ReadOnlySpan<byte> Bytes(int count) => Take(count);

    public string Utf8() => Encoding.UTF8.GetString(String());

    public string[] NameList()
    {
        var text = Utf8();
        return text.Length == 0 ? [] : text.Split(',');
    }

    public BigInteger MPInt() => new(String(), isUnsigned: false, isBigEndian: true);

    /// <summary>
    /// A positive, minimally encoded mpint's magnitude, left-padded to <paramref name="size"/> bytes.
    /// Zero, negative and non-minimal encodings are protocol errors.
    /// </summary>
    public byte[] UnsignedFixed(int size)
    {
        var value = String();
        if (value.IsEmpty || (value[0] & 0x80) != 0) throw new SshProtocolException("Expected a positive integer.");
        if (value[0] == 0)
        {
            if (value.Length == 1 || (value[1] & 0x80) == 0) throw new SshProtocolException("Integer is not minimally encoded.");
            value = value[1..];
        }
        if (value.Length > size) throw new SshProtocolException("Integer too large for its field.");
        var result = new byte[size];
        value.CopyTo(result.AsSpan(size - value.Length));
        return result;
    }

    public ReadOnlySpan<byte> Rest() => Take(Remaining);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new SshProtocolException("Message is shorter than its fields.");
        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }
}
