using System.Buffers;
using System.Text;
using FsCheck;
using FsCheck.Xunit;

namespace RedisLite.Tests;

/// <summary>FsCheck properties for the RESP encoder and the incremental parser.</summary>
[Properties(Arbitrary = [typeof(Generators)], MaxTest = 300)]
public class RespCodecProperties
{
    [Property]
    public bool EncodeThenParse_RoundTrips(RespValue value)
    {
        var buffer = new ReadOnlySequence<byte>(RespWriter.Encode(value));
        return RespReader.TryRead(ref buffer, out var parsed) && parsed.Equals(value) && buffer.IsEmpty;
    }

    /// <summary>
    /// The property behind tolerating TCP fragmentation: however the bytes are split across
    /// buffer segments, the parser sees the same value.
    /// </summary>
    [Property]
    public bool Parse_IsIndependentOfSegmentBoundaries(RespValue value, int[] cuts)
    {
        var buffer = Generators.Chunked(RespWriter.Encode(value), cuts);
        return RespReader.TryRead(ref buffer, out var parsed) && parsed.Equals(value) && buffer.IsEmpty;
    }

    /// <summary>A strict prefix is never mistaken for a whole value, and nothing is consumed.</summary>
    [Property]
    public bool StrictPrefix_NeedsMoreData(RespValue value, NonNegativeInt cut)
    {
        var encoded = RespWriter.Encode(value);
        var prefix = new ReadOnlySequence<byte>(encoded, 0, cut.Get % encoded.Length);
        var before = prefix.Length;
        return !RespReader.TryRead(ref prefix, out _) && prefix.Length == before;
    }

    [Property]
    public bool Concatenated_ValuesParseInOrder(RespValue[] values)
    {
        var bytes = values.SelectMany(RespWriter.Encode).ToArray();
        var parsed = RespReader.ReadAll(bytes, out var remainder);
        return remainder == 0 && parsed.SequenceEqual(values);
    }

    /// <summary>Arbitrary bytes either parse, need more data, or fail with <see cref="RespProtocolException"/>; nothing else escapes.</summary>
    [Property(MaxTest = 1000)]
    public void Garbage_OnlyEverFailsWithProtocolException(byte[] bytes)
    {
        try
        {
            RespReader.ReadAll(bytes, out _);
        }
        catch (RespProtocolException)
        {
        }
    }

    /// <summary>One flipped byte inside a valid reply (the kind of damage a buggy server or proxy causes) is handled cleanly.</summary>
    [Property(MaxTest = 1000)]
    public void SingleByteCorruption_OnlyEverFailsWithProtocolException(RespValue value, NonNegativeInt position, byte replacement)
    {
        var bytes = RespWriter.Encode(value);
        bytes[position.Get % bytes.Length] = replacement;
        try
        {
            RespReader.ReadAll(bytes, out _);
        }
        catch (RespProtocolException)
        {
        }
    }

    [Property]
    public bool Integers_UseCanonicalDecimal(long number) =>
        Encoding.ASCII.GetString(RespWriter.Encode(new RespInteger(number))) == $":{number}\r\n";

    [Fact]
    public void SimpleStrings_RejectLineBreaks() =>
        Assert.Throws<ArgumentException>(() => RespWriter.Encode(new RespSimpleString("a\r\nb")));

    [Theory]
    [InlineData("$3\r\nhello\r\n", "not followed by CRLF")]
    [InlineData(":12a\r\n", "Invalid integer")]
    [InlineData(":+5\r\n", "Invalid integer")]
    [InlineData(":99999999999999999999\r\n", "Invalid integer")]
    [InlineData("$-2\r\n", "Invalid bulk string length")]
    [InlineData("!oops\r\n", "Unexpected type byte 0x21")]
    [InlineData("+a\rb\r\n", "Stray CR")]
    public void MalformedInput_IsRejectedWithAReason(string wire, string reason)
    {
        var ex = Assert.Throws<RespProtocolException>(() => RespReader.ReadAll(Encoding.ASCII.GetBytes(wire), out _));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void DeepNesting_IsRejected()
    {
        var wire = string.Concat(Enumerable.Repeat("*1\r\n", RespReader.MaxDepth + 2)) + ":1\r\n";
        Assert.Throws<RespProtocolException>(() => RespReader.ReadAll(Encoding.ASCII.GetBytes(wire), out _));
    }
}

/// <summary>FsCheck properties for connection URLs.</summary>
[Properties(Arbitrary = [typeof(Generators)], MaxTest = 500)]
public class RedisOptionsProperties
{
    [Property]
    public bool Url_RoundTrips(RedisOptions options) => RedisOptions.Parse(options.ToUrl()) == options;

    [Theory]
    [InlineData("redis://localhost", "localhost", 6379, 0, false)]
    [InlineData("rediss://cache.internal:6380/2", "cache.internal", 6380, 2, true)]
    [InlineData("redis://[::1]:7000", "::1", 7000, 0, false)]
    [InlineData("REDIS://h/3?name=app", "h", 6379, 3, false)]
    public void Parses_CommonForms(string url, string host, int port, int db, bool tls)
    {
        var options = RedisOptions.Parse(url);
        Assert.Equal((host, port, db, tls), (options.Host, options.Port, options.Database, options.UseTls));
    }

    [Fact]
    public void Parses_CredentialsAndUnixSockets()
    {
        var tcp = RedisOptions.Parse("redis://app:p%40ss%3Aword@h");
        Assert.Equal(("app", "p@ss:word"), (tcp.User, tcp.Password));

        var unix = RedisOptions.Parse("unix:///var/run/redis.sock?db=4&password=x");
        Assert.Equal(("/var/run/redis.sock", 4, "x"), (unix.UnixSocketPath, unix.Database, unix.Password));
    }

    [Theory]
    [InlineData("localhost:6379")]
    [InlineData("http://localhost")]
    [InlineData("redis://:6379")]
    [InlineData("redis://h:0")]
    [InlineData("redis://h:70000")]
    [InlineData("redis://h/x")]
    [InlineData("redis://[::1")]
    [InlineData("unix://")]
    public void Rejects_MalformedUrls(string url) => Assert.Throws<FormatException>(() => RedisOptions.Parse(url));
}
