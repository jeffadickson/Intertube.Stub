using System.Diagnostics;
using Intertube.Stub;

namespace RedisLite.Tests;

/// <summary>
/// Retry and failure semantics, driven by Intertube.Stub's per-connection fault injection. This is
/// where a stub server pays off: no real Redis can be told to reset the first connection after
/// its third GET.
/// </summary>
public class ResilienceTests
{
    [Fact]
    public async Task IdempotentCommand_IsRetriedAfterConnectionReset()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);
        await redis.SetAsync("k", "v");

        fake.Intercept = (connection, command) =>
        {
            if (connection.Id == 1 && command[0] == "GET") connection.Abort();
            return Task.FromResult(connection.Id == 1 && command[0] == "GET");
        };

        Assert.Equal("v", await redis.GetAsync("k"));
        Assert.Equal(2, redis.ConnectionsOpened);
        Assert.Equal(2, fake.CountOf("GET"));
    }

    /// <summary>
    /// INCRBY that might have run must not be repeated. The server got it, the connection dropped
    /// before the reply, and the client has to admit it does not know the outcome.
    /// </summary>
    [Fact]
    public async Task NonIdempotentCommand_IsNotRetriedOnceSent()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);
        fake.Intercept = (connection, command) =>
        {
            var drop = connection.Id == 1 && command[0] == "INCRBY";
            if (drop) connection.Abort();
            return Task.FromResult(drop);
        };

        await Assert.ThrowsAnyAsync<RedisConnectionException>(() => redis.IncrementAsync("counter"));
        Assert.Equal(1, fake.CountOf("INCRBY"));

        Assert.Equal(1, await redis.IncrementAsync("counter")); // the dropped one never applied
        Assert.Equal(2, redis.ConnectionsOpened);
    }

    [Fact]
    public async Task FailureDuringHandshake_IsRetriedEvenForNonIdempotentCommands()
    {
        await using var fake = FakeRedis.Start(password: "pw");
        await using var redis = new RedisClient(fake.ClientOptions);
        fake.Intercept = (connection, command) =>
        {
            var drop = connection.Id == 1 && command[0] == "AUTH";
            if (drop) connection.Abort();
            return Task.FromResult(drop);
        };

        Assert.Equal(1, await redis.IncrementAsync("counter"));
        Assert.Equal(2, fake.CountOf("AUTH"));
        Assert.Equal(1, fake.CountOf("INCRBY"));
    }

    [Fact]
    public async Task HungServer_TimesOut_ThenRecoversOnANewConnection()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions with { CommandTimeout = TimeSpan.FromMilliseconds(200) });
        fake.Intercept = async (connection, command) =>
        {
            if (connection.Id != 1 || command[0] != "GET") return false;
            await connection.HangAsync();
            return true;
        };

        var watch = Stopwatch.StartNew();
        Assert.Null(await redis.GetAsync("k"));
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(180), TimeSpan.FromSeconds(3));
        Assert.Equal(2, redis.ConnectionsOpened);
    }

    [Fact]
    public async Task HungServer_WithoutRetries_RaisesTimeout()
    {
        await using var fake = FakeRedis.Start();
        fake.Intercept = async (connection, _) =>
        {
            await connection.HangAsync();
            return true;
        };
        await using var redis = new RedisClient(fake.ClientOptions with { CommandTimeout = TimeSpan.FromMilliseconds(100), MaxRetries = 0 });

        await Assert.ThrowsAsync<RedisTimeoutException>(() => redis.GetAsync("k"));
    }

    [Fact]
    public async Task ConnectionCutMidReply_IsDetected()
    {
        // "+OK\r\n" (5 bytes) for SET, then the GET reply is cut after 6 more bytes: "$5\r\nhe".
        await using var fake = FakeRedis.Start(new ServerOptions { Faults = FaultOptions.DisconnectAfter(11, DisconnectMode.Close) });
        await using var redis = new RedisClient(fake.ClientOptions with { MaxRetries = 0 });

        await redis.SetAsync("k", "hello");
        var ex = await Assert.ThrowsAnyAsync<RedisConnectionException>(() => redis.GetAsync("k"));
        Assert.Contains("middle of a reply", ex.Message);
        Assert.Equal("+OK\r\n$5\r\nhe", fake.Server.Transcript.SentText);
    }

    [Fact]
    public async Task ByteAtATimeReplies_AreReassembled()
    {
        await using var fake = FakeRedis.Start(new ServerOptions { Faults = FaultOptions.Fragment(1) });
        await using var redis = new RedisClient(fake.ClientOptions);
        var big = string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i}\r\n"));

        await redis.SetAsync("big", big);
        Assert.Equal(big, await redis.GetAsync("big"));
    }

    [Fact]
    public async Task SlowServer_BeyondCommandTimeout_TimesOut()
    {
        await using var fake = FakeRedis.Start(new ServerOptions { Faults = FaultOptions.Latency(TimeSpan.FromMilliseconds(400)) });
        await using var redis = new RedisClient(fake.ClientOptions with { CommandTimeout = TimeSpan.FromMilliseconds(150), MaxRetries = 0 });

        await Assert.ThrowsAsync<RedisTimeoutException>(() => redis.PingAsync());
    }

    /// <summary>
    /// Fifty callers in flight when the shared connection is reset: every outstanding
    /// command fails over to the new connection and nobody hangs or gets someone else's reply.
    /// </summary>
    [Fact]
    public async Task ResetUnderLoad_AllCallersComplete()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions with { MaxRetries = 3 });
        for (var i = 0; i < 10; i++) await redis.SetAsync($"k{i}", $"v{i}");

        var gets = 0;
        fake.Intercept = (connection, command) =>
        {
            var drop = connection.Id == 1 && command[0] == "GET" && Interlocked.Increment(ref gets) == 20;
            if (drop) connection.Abort();
            return Task.FromResult(drop);
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => redis.GetAsync($"k{i % 10}")))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results.Select((value, i) => (value, i)), r => Assert.Equal($"v{r.i % 10}", r.value));
        Assert.Equal(2, redis.ConnectionsOpened);
    }

    [Fact]
    public async Task UnreachableServer_IsReportedAfterRetries()
    {
        int port;
        await using (var temporary = TcpServer.Listen(0, Handler.Close)) port = temporary.Port; // a port nobody listens on now
        await using var redis = new RedisClient(new RedisOptions { Host = "127.0.0.1", Port = port, MaxRetries = 2, RetryBackoff = TimeSpan.FromMilliseconds(1) });

        var ex = await Assert.ThrowsAsync<RedisConnectionException>(() => redis.PingAsync());
        Assert.Contains("Could not connect", ex.Message);
    }
}
