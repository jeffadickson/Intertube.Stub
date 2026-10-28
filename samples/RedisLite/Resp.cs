using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace RedisLite;

/// <summary>Encodes <see cref="RespValue"/>s and commands in RESP2 wire format.</summary>
public static class RespWriter
{
    /// <summary>Encode a single value.</summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The wire bytes.</returns>
    /// <exception cref="ArgumentException">A simple string or error contains CR or LF.</exception>
    public static byte[] Encode(RespValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Encode a command as an array of bulk strings.</summary>
    /// <param name="parts">The command name followed by its arguments.</param>
    /// <returns>The wire bytes.</returns>
    /// <example>
    /// <code>RespWriter.EncodeCommand("SET", "k", "v")   // "*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\nv\r\n"</code>
    /// </example>
    public static byte[] EncodeCommand(params IReadOnlyList<string> parts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteCommand(buffer, parts);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Append a command, encoded as an array of bulk strings, to <paramref name="output"/>.</summary>
    /// <param name="output">Destination buffer.</param>
    /// <param name="parts">The command name followed by its arguments.</param>
    public static void WriteCommand(IBufferWriter<byte> output, IReadOnlyList<string> parts)
    {
        WriteHeader(output, '*', parts.Count);
        foreach (var part in parts)
        {
            var length = Encoding.UTF8.GetByteCount(part);
            WriteHeader(output, '$', length);
            Encoding.UTF8.GetBytes(part, output);
            WriteCrLf(output);
        }
    }

    /// <summary>Append <paramref name="value"/> to <paramref name="output"/>.</summary>
    /// <param name="output">Destination buffer.</param>
    /// <param name="value">The value to encode.</param>
    /// <exception cref="ArgumentException">A simple string or error contains CR or LF.</exception>
    public static void Write(IBufferWriter<byte> output, RespValue value)
    {
        switch (value)
        {
            case RespSimpleString s:
                WriteLine(output, '+', s.Value);
                break;
            case RespError e:
                WriteLine(output, '-', e.Message);
                break;
            case RespInteger i:
                WriteHeader(output, ':', i.Value);
                break;
            case RespBulkString { Value: null }:
                WriteHeader(output, '$', -1);
                break;
            case RespBulkString { Value: { } bytes }:
                WriteHeader(output, '$', bytes.Length);
                output.Write(bytes);
                WriteCrLf(output);
                break;
            case RespArray { Items: null }:
                WriteHeader(output, '*', -1);
                break;
            case RespArray { Items: { } items }:
                WriteHeader(output, '*', items.Count);
                foreach (var item in items) Write(output, item);
                break;
            default:
                throw new ArgumentException($"Unknown RESP value {value.GetType().Name}.", nameof(value));
        }
    }

    private static void WriteLine(IBufferWriter<byte> output, char prefix, string text)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException("Simple strings and errors cannot contain CR or LF.", nameof(text));
        output.Write([(byte)prefix]);
        Encoding.UTF8.GetBytes(text, output);
        WriteCrLf(output);
    }

    private static void WriteHeader(IBufferWriter<byte> output, char prefix, long number)
    {
        output.Write([(byte)prefix]);
        Encoding.ASCII.GetBytes(number.ToString(CultureInfo.InvariantCulture), output);
        WriteCrLf(output);
    }

    private static void WriteCrLf(IBufferWriter<byte> output) => output.Write("\r\n"u8);
}

/// <summary>
/// An incremental RESP2 parser over <see cref="ReadOnlySequence{T}"/>, suitable for
/// <see cref="System.IO.Pipelines.PipeReader"/>. It never consumes a partial value, and it rejects
/// malformed input with <see cref="RespProtocolException"/> instead of guessing.
/// </summary>
public static class RespReader
{
    /// <summary>Deepest array nesting accepted.</summary>
    public const int MaxDepth = 64;

    /// <summary>Largest bulk string accepted (Redis' own limit is 512 MB).</summary>
    public const int MaxBulkLength = 512 * 1024 * 1024;

    /// <summary>Longest header or simple-string line accepted.</summary>
    public const int MaxLineLength = 64 * 1024;

    /// <summary>
    /// Try to parse one complete value from the start of <paramref name="buffer"/>. On success,
    /// <paramref name="buffer"/> is advanced past it; otherwise it is left unchanged and more data is needed.
    /// </summary>
    /// <param name="buffer">Input; advanced past the value on success.</param>
    /// <param name="value">The parsed value.</param>
    /// <returns>True if a whole value was parsed.</returns>
    /// <exception cref="RespProtocolException">The input is not valid RESP.</exception>
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] out RespValue? value)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!TryReadValue(ref reader, 0, out value) || value is null) return false;
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    /// <summary>Parse every complete value in <paramref name="bytes"/>.</summary>
    /// <param name="bytes">Input bytes.</param>
    /// <param name="remainder">Number of trailing bytes that did not form a complete value.</param>
    /// <returns>The values in order.</returns>
    /// <exception cref="RespProtocolException">The input is not valid RESP.</exception>
    public static IReadOnlyList<RespValue> ReadAll(ReadOnlyMemory<byte> bytes, out int remainder)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);
        var values = new List<RespValue>();
        while (TryRead(ref buffer, out var value)) values.Add(value);
        remainder = (int)buffer.Length;
        return values;
    }

    private static bool TryReadValue(ref SequenceReader<byte> reader, int depth, out RespValue? value)
    {
        value = null;
        if (depth > MaxDepth) throw new RespProtocolException($"Arrays nested deeper than {MaxDepth}.");
        if (!reader.TryRead(out var prefix)) return false;

        switch (prefix)
        {
            case (byte)'+':
                if (!TryReadLine(ref reader, out var status)) return false;
                value = new RespSimpleString(Encoding.UTF8.GetString(status));
                return true;

            case (byte)'-':
                if (!TryReadLine(ref reader, out var error)) return false;
                value = new RespError(Encoding.UTF8.GetString(error));
                return true;

            case (byte)':':
                if (!TryReadLine(ref reader, out var integer)) return false;
                value = new RespInteger(ParseInteger(integer));
                return true;

            case (byte)'$':
            {
                if (!TryReadLine(ref reader, out var header)) return false;
                var length = ParseInteger(header);
                if (length == -1)
                {
                    value = RespValue.Nil;
                    return true;
                }
                if (length is < 0 or > MaxBulkLength) throw new RespProtocolException($"Invalid bulk string length {length}.");
                if (reader.Remaining < length + 2) return false;
                var bytes = new byte[length];
                reader.TryCopyTo(bytes);
                reader.Advance(length);
                if (!reader.IsNext("\r\n"u8, advancePast: true))
                    throw new RespProtocolException("Bulk string is not followed by CRLF.");
                value = new RespBulkString(bytes);
                return true;
            }

            case (byte)'*':
            {
                if (!TryReadLine(ref reader, out var header)) return false;
                var count = ParseInteger(header);
                if (count == -1)
                {
                    value = new RespArray(null);
                    return true;
                }
                if (count is < 0 or > int.MaxValue) throw new RespProtocolException($"Invalid array length {count}.");
                // Never trust a length enough to preallocate from it.
                var items = new List<RespValue>((int)Math.Min(count, 1024));
                for (var i = 0; i < count; i++)
                {
                    if (!TryReadValue(ref reader, depth + 1, out var item)) return false;
                    items.Add(item!);
                }
                value = new RespArray(items);
                return true;
            }

            default:
                throw new RespProtocolException($"Unexpected type byte 0x{prefix:X2}.");
        }
    }

    private static bool TryReadLine(ref SequenceReader<byte> reader, out byte[] line)
    {
        if (!reader.TryReadTo(out ReadOnlySequence<byte> found, "\r\n"u8, advancePastDelimiter: true))
        {
            if (reader.Remaining > MaxLineLength) throw new RespProtocolException($"No CRLF within {MaxLineLength} bytes.");
            line = [];
            return false;
        }
        line = found.ToArray();
        if (line.AsSpan().IndexOfAny((byte)'\r', (byte)'\n') >= 0) throw new RespProtocolException("Stray CR or LF inside a line.");
        return true;
    }

    private static long ParseInteger(ReadOnlySpan<byte> text)
    {
        var digits = text.Length > 0 && text[0] == '-' ? text[1..] : text;
        if (digits.IsEmpty || digits.Length > 19 || digits.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0
            || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            throw new RespProtocolException($"Invalid integer \"{Encoding.ASCII.GetString(text)}\".");
        return number;
    }
}
