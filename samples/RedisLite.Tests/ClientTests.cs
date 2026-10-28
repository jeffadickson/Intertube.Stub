using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Intertube.Stub;

namespace RedisLite.Tests;

public class ClientTests
{
    [Fact]
    public async Task BasicCommands_RoundTrip()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);

        Assert.Equal("PONG", await redis.PingAsync());
        Assert.True(await redis.SetAsync("greeting", "hello"));
        Assert.Equal("hello", await redis.GetAsync("greeting"));
        Assert.False(await redis.SetAsync("greeting", "again", condition: SetCondition.IfNotExists));
        Assert.Equal(5, await redis.IncrementAsync("n", 5));
        Assert.Equal(2, await redis.DeleteAsync("greeting", "n", "missing"));
        Assert.Null(await redis.GetAsync("greeting"));
    }

    [Property(MaxTest = 100, Arbitrary = [typeof(Generators)])]
    public bool AnyUnicodeValue_RoundTripsOverTheWire(NonNull<string> raw)
    {
        var value = new string(raw.Get.Where(c => !char.IsSurrogate(c)).ToArray());
        return RoundTrip(value).GetAwaiter().GetResult() == value;

        static async Task<string?> RoundTrip(string value)
        {
            await using var fake = FakeRedis.Start();
            await using var redis = new RedisClient(fake.ClientOptions);
            await redis.SetAsync("k", value);
            return await redis.GetAsync("k");
        }
    }

    [Fact]
    public async Task ServerErrors_SurfaceAsRedisServerException()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);

        var ex = await Assert.ThrowsAsync<RedisServerException>(() => redis.ExecuteAsync(["NOPE", "x"]));
        Assert.Equal("ERR", ex.Code);
        Assert.Contains("unknown command 'NOPE'", ex.Message);

        await redis.SetAsync("text", "abc");
        await Assert.ThrowsAsync<RedisServerException>(() => redis.IncrementAsync("text"));
    }

    [Fact]
    public async Task Expiry_IsSentInMilliseconds()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);

        await redis.SetAsync("session", "x", expiry: TimeSpan.FromMinutes(1));
        Assert.Equal(["SET", "session", "x", "PX", "60000"], fake.Commands[^1]);
        Assert.Equal("x", await redis.GetAsync("session"));

        await redis.SetAsync("flash", "y", expiry: TimeSpan.FromMilliseconds(50));
        await Task.Delay(300);
        Assert.Null(await redis.GetAsync("flash"));
    }

    /// <summary>
    /// Pipelining proven at the wire level: this scripted server reads all three commands as one
    /// exact byte sequence before replying to any. A client that waited for each reply would deadlock here.
    /// </summary>
    [Fact]
    public async Task Pipeline_WritesEveryCommandBeforeAnyReply()
    {
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .ExpectBytes(Encoding.ASCII.GetBytes(
                "*3\r\n$3\r\nSET\r\n$1\r\na\r\n$1\r\n1\r\n" +
                "*2\r\n$6\r\nINCRBY\r\n$1\r\na\r\n" +   // deliberately invalid arity, answered with an error
                "*2\r\n$3\r\nGET\r\n$1\r\na\r\n"))
            .SendRaw("+OK\r\n-ERR wrong number of arguments\r\n$1\r\n1\r\n"));

        await using var connection = await RedisConnection.ConnectAsync(new RedisOptions { Host = server.Host, Port = server.Port });
        var replies = await connection.PipelineAsync([["SET", "a", "1"], ["INCRBY", "a"], ["GET", "a"]]);

        Assert.Equal<RespValue>([new RespSimpleString("OK"), new RespError("ERR wrong number of arguments"), RespValue.Bulk("1")], replies);
        server.ThrowIfFaulted();
    }

    /// <summary>Concurrent callers share one connection, and every reply reaches the caller that asked for it.</summary>
    [Fact]
    public async Task ConcurrentCallers_ShareOnePipelinedConnection()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);
        for (var i = 0; i < 50; i++) await redis.SetAsync($"key{i}", $"value{i}");

        var results = await Task.WhenAll(Enumerable.Range(0, 500).Select(i => redis.GetAsync($"key{i % 50}")));

        Assert.All(results.Select((value, i) => (value, i)), r => Assert.Equal($"value{r.i % 50}", r.value));
        fake.Server.Transcript.AssertConnectionCount(1);
    }

    [Fact]
    public async Task Handshake_AuthSelectAndName_ArePipelinedBeforeFirstCommand()
    {
        await using var fake = FakeRedis.Start(user: "app", password: "s3cret");
        await using var redis = new RedisClient(fake.ClientOptions with { Database = 3, ClientName = "billing" });

        await redis.SetAsync("k", "in db 3");

        Assert.Equal(["AUTH", "SELECT", "CLIENT", "SET"], fake.Commands.Select(c => c[0]));
        Assert.Equal(["AUTH", "app", "s3cret"], fake.Commands[0]);

        await using var db0 = new RedisClient(fake.ClientOptions);
        Assert.Null(await db0.GetAsync("k"));
    }

    [Fact]
    public async Task WrongPassword_FailsFastWithoutRetrying()
    {
        await using var fake = FakeRedis.Start(password: "right");
        await using var redis = new RedisClient(fake.ClientOptions with { Password = "wrong" });

        var ex = await Assert.ThrowsAsync<RedisServerException>(() => redis.PingAsync());
        Assert.Equal("WRONGPASS", ex.Code);
        Assert.Equal(1, fake.CountOf("AUTH"));
    }

    [Fact]
    public async Task MissingPassword_GetsNoAuth()
    {
        await using var fake = FakeRedis.Start(password: "right");
        await using var redis = new RedisClient(fake.ClientOptions with { Password = null });

        Assert.Equal("NOAUTH", (await Assert.ThrowsAsync<RedisServerException>(() => redis.GetAsync("k"))).Code);
    }

    [Fact]
    public async Task Tls_WithAnOnTheFlyCertificate()
    {
        await using var fake = FakeRedis.Start(new ServerOptions { Tls = TlsOptions.SelfSigned() });
        await using var redis = new RedisClient(fake.ClientOptions);

        await redis.SetAsync("secret", "value");
        Assert.Equal("value", await redis.GetAsync("secret"));

        // Intertube.Stub records plaintext above the TLS layer, so the transcript stays readable.
        fake.Server.Transcript.AssertReceivedLine("secret");
        Assert.Contains(fake.Server.Transcript[0].Events, e => e.StartsWith("TLS established"));
    }

    [Fact]
    public async Task Tls_UntrustedCertificate_IsRejected()
    {
        await using var fake = FakeRedis.Start(new ServerOptions { Tls = TlsOptions.SelfSigned() });
        await using var redis = new RedisClient(fake.ClientOptions with { ServerCertificateValidation = null, MaxRetries = 1 });

        var ex = await Assert.ThrowsAsync<RedisConnectionException>(() => redis.PingAsync());
        Assert.IsAssignableFrom<System.Security.Authentication.AuthenticationException>(ex.InnerException);
        Assert.Equal(2, fake.Server.Transcript.Connections.Count); // first attempt plus one retry
    }

    [Fact]
    public async Task UnixSocket_Works()
    {
        await using var fake = FakeRedis.Start(unixSocket: true);
        await using var redis = new RedisClient(RedisOptions.Parse(fake.ClientOptions.ToUrl()));

        await redis.SetAsync("via", "unix");
        Assert.Equal("unix", await redis.GetAsync("via"));
    }

    [Fact]
    public async Task PubSub_StreamsMessagesAsAsyncEnumerable()
    {
        await using var fake = FakeRedis.Start();
        await using var redis = new RedisClient(fake.ClientOptions);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var received = new List<string>();
        var subscriber = Task.Run(async () =>
        {
            await foreach (var message in redis.SubscribeAsync(["news", "sport"], stop.Token))
            {
                received.Add($"{message.Channel}:{message.Text}");
                if (received.Count == 3) break;
            }
        });

        // RESP is line-shaped enough for Intertube.Stub's line transcript to show when the subscription is live.
        await fake.Server.Transcript.WaitForReceivedLineAsync("sport");
        await fake.Server.Transcript.WaitForSentLineAsync("sport");

        Assert.Equal(1, await redis.PublishAsync("news", "hello"));
        Assert.Equal(0, await redis.PublishAsync("weather", "ignored"));
        Assert.Equal(1, await redis.PublishAsync("sport", "goal"));
        Assert.Equal(1, await redis.PublishAsync("news", "bye"));
        await subscriber;

        Assert.Equal(["news:hello", "sport:goal", "news:bye"], received);
    }

    /// <summary>A scripted server that pushes a message and then hangs up ends the enumeration cleanly.</summary>
    [Fact]
    public async Task PubSub_EndsWhenServerCloses()
    {
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .ExpectBytes(RespWriter.EncodeCommand("SUBSCRIBE", "alerts"))
            .SendRaw("*3\r\n$9\r\nsubscribe\r\n$6\r\nalerts\r\n:1\r\n")
            .SendRaw("*3\r\n$7\r\nmessage\r\n$6\r\nalerts\r\n$4\r\nfire\r\n")
            .Close());
        await using var redis = new RedisClient(new RedisOptions { Host = server.Host, Port = server.Port });

        var messages = new List<string>();
        await foreach (var message in redis.SubscribeAsync(["alerts"]))
            messages.Add(message.Text);

        Assert.Equal(["fire"], messages);
    }

    [Theory]
    [InlineData("!oops\r\n")]
    [InlineData("$3\r\nhello\r\n")]
    [InlineData(":not-a-number\r\n")]
    public async Task MalformedReplies_AreProtocolErrors_AndTheConnectionIsReplaced(string garbage)
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            if (connection.Id == 1)
            {
                await connection.ReadAsync(new byte[1024]);
                await connection.WriteAsync(garbage);
                await connection.HangAsync();
            }
            else
            {
                await Handler.Lines(_ => null)(connection); // read the next PING...
            }
        });
        await using var redis = new RedisClient(new RedisOptions { Host = server.Host, Port = server.Port, CommandTimeout = TimeSpan.FromMilliseconds(500), MaxRetries = 0 });

        await Assert.ThrowsAsync<RespProtocolException>(() => redis.PingAsync());
        await Assert.ThrowsAsync<RedisTimeoutException>(() => redis.PingAsync()); // ...on a fresh connection
        Assert.Equal(2, redis.ConnectionsOpened);
    }
}
