using System.Buffers;
using System.Diagnostics;
using System.Text;
using Intertube.Stub;

namespace RedisLite.Tests;

/// <summary>
/// Tests added after a Stryker run. Each one kills mutants that survived the original suite,
/// and the comments name the mutation. These are the gaps that coverage numbers alone don't show.
/// </summary>
public class MutationTargetedTests
{
    // ---- Retry classification: removing any entry from the idempotent set survived. -----------

    public static TheoryData<string[]> IdempotentCommands => new()
    {
        new[] { "PING" }, new[] { "ECHO", "x" }, new[] { "GET", "k" }, new[] { "SET", "k", "v" }, new[] { "DEL", "k" },
        new[] { "EXISTS", "k" }, new[] { "TTL", "k" }, new[] { "PTTL", "k" }, new[] { "EXPIRE", "k", "9" },
        new[] { "PEXPIRE", "k", "9" }, new[] { "MGET", "k" }, new[] { "STRLEN", "k" }, new[] { "TYPE", "k" },
        new[] { "KEYS", "*" }, new[] { "DBSIZE" },
    };

    public static TheoryData<string[]> NonIdempotentCommands => new()
    {
        new[] { "INCR", "k" }, new[] { "INCRBY", "k", "2" }, new[] { "PUBLISH", "c", "m" }, new[] { "LPUSH", "l", "x" },
    };

    private static FakeRedis ResetFirstAttemptOf(string command)
    {
        var fake = FakeRedis.Start();
        fake.Intercept = (connection, received) =>
        {
            var drop = connection.Id == 1 && received[0] == command;
            if (drop) connection.Abort();
            return Task.FromResult(drop);
        };
        return fake;
    }

    [Theory]
    [MemberData(nameof(IdempotentCommands))]
    public async Task IdempotentCommands_AreRetried(string[] command)
    {
        await using var fake = ResetFirstAttemptOf(command[0]);
        await using var redis = new RedisClient(fake.ClientOptions);

        try
        {
            await redis.ExecuteAsync(command);
        }
        catch (RedisServerException)
        {
            // FakeRedis does not implement every command; what matters is the second attempt.
        }
        Assert.Equal(2, fake.CountOf(command[0]));
    }

    [Theory]
    [MemberData(nameof(NonIdempotentCommands))]
    public async Task NonIdempotentCommands_AreNotRetried(string[] command)
    {
        await using var fake = ResetFirstAttemptOf(command[0]);
        await using var redis = new RedisClient(fake.ClientOptions);

        await Assert.ThrowsAnyAsync<RedisConnectionException>(() => redis.ExecuteAsync(command));
        Assert.Equal(1, fake.CountOf(command[0]));
    }

    // ---- SET conditions: "NX" : "XX" collapsed to always-NX survived. --------------------------

    [Fact]
    public async Task SetIfExists_SendsXx_AndOnlyOverwrites()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);

        Assert.False(await redis.SetAsync("k", "v", condition: SetCondition.IfExists));
        Assert.Equal(["SET", "k", "v", "XX"], fake.Commands[^1]);
        await redis.SetAsync("k", "v1");
        Assert.True(await redis.SetAsync("k", "v2", condition: SetCondition.IfExists));
        Assert.Equal("v2", await redis.GetAsync("k"));
    }

    // ---- Backoff: RetryBackoff / 2^n instead of * 2^n survived. --------------------------------

    [Fact]
    public async Task Backoff_DoublesBetweenAttempts()
    {
        // Every attempt must fail fast, or backoff is lost in the noise: connecting to a closed port
        // takes 1-2 s on Windows (SYN retries), which let the "/" mutant survive a timing check.
        // Accept-then-reset fails each attempt in about a millisecond.
        await using var server = TcpServer.Listen(0, Handler.Reset);
        await using var redis = new RedisClient(new RedisOptions { Host = server.Host, Port = server.Port, MaxRetries = 2, RetryBackoff = TimeSpan.FromMilliseconds(150) });

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<RedisConnectionException>(() => redis.PingAsync());
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(430), $"150 + 300 ms of backoff expected, took {watch.Elapsed}");
        server.Transcript.AssertConnectionCount(3);
    }

    // ---- Subscriptions count as connections: removing the increment survived. -----------------

    [Fact]
    public async Task Subscribe_CountsItsDedicatedConnection()
    {
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .ExpectBytes(RespWriter.EncodeCommand("SUBSCRIBE", "c"))
            .Close());
        await using var redis = new RedisClient(new RedisOptions { Host = server.Host, Port = server.Port });

        await foreach (var _ in redis.SubscribeAsync(["c"])) { }
        Assert.Equal(1, redis.ConnectionsOpened);
    }

    // ---- Connection failure paths ---------------------------------------------------------------

    /// <summary>
    /// The precedence bug from earlier, made deterministic: a write that fails with IOException
    /// must surface as RedisConnectionException. The load test only hit this by luck, so its
    /// mutant survived.
    /// </summary>
    [Fact]
    public async Task WriteFailure_IsAConnectionException()
    {
        await using var connection = RedisConnection.Create(new BrokenStream(), new RedisOptions { CommandTimeout = TimeSpan.FromSeconds(5) });

        var ex = await Assert.ThrowsAsync<RedisConnectionException>(() => connection.ExecuteAsync(["PING"]));
        Assert.StartsWith("Sending to the server failed", ex.Message);
        Assert.False(connection.IsConnected);
        Assert.Same(ex, await Assert.ThrowsAsync<RedisConnectionException>(() => connection.ExecuteAsync(["PING"])));
    }

    [Fact]
    public async Task ServerClosingBetweenReplies_FailsPendingCommandsImmediately()
    {
        await using var server = TcpServer.Listen(0, async c =>
        {
            await c.ReadAsync(new byte[64]);
            await c.CloseAsync();
        });
        await using var connection = await RedisConnection.ConnectAsync(new RedisOptions { Host = server.Host, Port = server.Port, CommandTimeout = TimeSpan.FromSeconds(10) });

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RedisConnectionException>(() => connection.ExecuteAsync(["PING"]));
        Assert.Equal("The server closed the connection.", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "must not wait for the command timeout");
    }

    [Fact]
    public async Task Dispose_FailsOutstandingCommands()
    {
        await using var server = TcpServer.Listen(0, Handler.Hang);
        var connection = await RedisConnection.ConnectAsync(new RedisOptions { Host = server.Host, Port = server.Port });

        var pending = connection.ExecuteAsync(["PING"]);
        await server.Transcript.WaitForReceivedLineAsync("PING");
        await connection.DisposeAsync();

        Assert.Equal("The connection was closed.", (await Assert.ThrowsAsync<RedisConnectionException>(() => pending)).Message);
    }

    // ---- RESP boundaries --------------------------------------------------------------------------

    [Theory]
    [InlineData("\rleading")]
    [InlineData("\nleading")]
    [InlineData("trailing\r")]
    public void SimpleStrings_RejectLineBreaksAnywhere(string text) =>
        Assert.Throws<ArgumentException>(() => RespWriter.Encode(new RespSimpleString(text)));

    [Fact]
    public void Nesting_ExactlyAtTheLimit_IsAccepted()
    {
        var wire = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("*1\r\n", RespReader.MaxDepth)) + ":1\r\n");
        Assert.Single(RespReader.ReadAll(wire, out _));

        var tooDeep = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("*1\r\n", RespReader.MaxDepth + 1)) + ":1\r\n");
        Assert.Throws<RespProtocolException>(() => RespReader.ReadAll(tooDeep, out _));
    }

    [Fact]
    public void LineWithoutCrLf_WaitsUpToTheLimit_ThenFails()
    {
        var atLimit = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("+" + new string('a', RespReader.MaxLineLength)));
        Assert.False(RespReader.TryRead(ref atLimit, out _));

        var overLimit = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes("+" + new string('a', RespReader.MaxLineLength + 1)));
        Assert.Throws<RespProtocolException>(() => RespReader.TryRead(ref overLimit, out _));
    }

    /// <summary>A hostile array length must not be trusted for preallocation (Math.Min to Math.Max survived).</summary>
    [Fact]
    public void HugeArrayHeader_DoesNotPreallocate()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes($"*{int.MaxValue}\r\n:1\r\n"));
        Assert.False(RespReader.TryRead(ref buffer, out _)); // waits for more items instead of allocating 16 GB
    }

    [Fact]
    public void StrayCarriageReturn_AtStartOfLine_IsRejected() =>
        Assert.Throws<RespProtocolException>(() => RespReader.ReadAll(Encoding.ASCII.GetBytes("+\rx\r\n"), out _));

    [Fact]
    public void ValueEquality_DistinguishesNilEmptyAndNull()
    {
        Assert.NotEqual(new RespBulkString([]), RespValue.Nil);
        Assert.NotEqual(RespValue.Nil, new RespBulkString([]));
        Assert.NotEqual(new RespArray([]), new RespArray(null));
        Assert.NotEqual(new RespArray(null), new RespArray([]));
        Assert.False(new RespBulkString([1]).Equals((RespBulkString?)null));
        Assert.False(new RespArray([]).Equals((RespArray?)null));
        Assert.Equal("+OK", new RespSimpleString("OK").ToString());
    }

    // ---- URL canonical form and boundaries (the round-trip property cannot see these) ------------

    [Theory]
    [InlineData("localhost", 6379, "redis://localhost")]
    [InlineData("::1", 7000, "redis://[::1]:7000")]
    [InlineData("::1", 6379, "redis://[::1]")]
    public void ToUrl_IsCanonical(string host, int port, string expected) =>
        Assert.Equal(expected, new RedisOptions { Host = host, Port = port }.ToUrl());

    [Fact]
    public void Defaults_PointAtLocalhost()
    {
        Assert.Equal("redis://localhost", new RedisOptions().ToUrl());
        Assert.Throws<ArgumentNullException>(() => RedisOptions.Parse(null!));
    }

    [Theory]
    [InlineData("redis://h:1", 1)]
    [InlineData("redis://h:65535", 65535)]
    public void Ports_AtTheBoundaries_AreAccepted(string url, int port) => Assert.Equal(port, RedisOptions.Parse(url).Port);

    [Theory]
    [InlineData("redis:///2")]
    [InlineData("unix://?db=1")]
    public void MissingHostOrPath_IsRejected(string url) => Assert.Throws<FormatException>(() => RedisOptions.Parse(url));

    [Fact]
    public void EmptyUserInfo_MeansNoCredentials()
    {
        var options = RedisOptions.Parse("redis://@h");
        Assert.Equal(("h", null, null), (options.Host, options.User, options.Password));
    }

    /// <summary>Reads block until cancelled; writes fail as if the peer had reset.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Connection reset by peer"));

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Connection reset by peer");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
