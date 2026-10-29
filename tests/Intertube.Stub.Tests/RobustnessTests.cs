using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Intertube.Stub.Tests;

/// <summary>Error paths in the core: user callbacks that throw, faults mid-close, TLS failures, resets.</summary>
public class RobustnessTests
{
    [Fact]
    public async Task ALogCallbackThatThrows_DoesNotStopTheServer()
    {
        // xUnit's ITestOutputHelper throws once its test has finished; the server must shrug that off.
        var server = TcpServer.Listen(0, Handler.Lines(line => line),
            new ServerOptions { Log = _ => throw new InvalidOperationException("There is no currently active test.") });

        for (var i = 0; i < 2; i++)
        {
            await using var client = await server.ConnectAsync();
            await client.WriteLineAsync("ping");
            Assert.Equal("ping", await client.ReadLineAsync());
        }

        await server.DisposeAsync(); // must not throw
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task DisconnectAfterViaOutput_StillRecordsTheClose()
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            connection.Output.Write(Encoding.ASCII.GetBytes("0123456789"));
            await connection.Output.FlushAsync();
        }, new ServerOptions { Faults = FaultOptions.DisconnectAfter(4, DisconnectMode.Close) });
        await using var client = await server.ConnectAsync();

        Assert.Equal("0123", Encoding.ASCII.GetString(await client.ReadToEndAsync()));
        await server.Transcript.WaitForClosedConnectionsAsync(1, TimeSpan.FromSeconds(5));
        Assert.Single(server.Transcript[0].Events, e => e.StartsWith("fault: disconnect", StringComparison.Ordinal));
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task Hang_IgnoresReadTimeout()
    {
        await using var server = TcpServer.Listen(0, Handler.Hang, new ServerOptions { ReadTimeout = TimeSpan.FromMilliseconds(100) });
        await using var client = await server.ConnectAsync();
        await client.WriteLineAsync("anyone there?");

        await Task.Delay(400);

        Assert.Empty(server.Errors);
        Assert.False(server.Transcript[0].IsClosed);
    }

    [Fact]
    public async Task Corrupt_SameOptionsGiveTheSameBytesOnEveryServer()
    {
        var faults = FaultOptions.Corrupt(0.1, seed: 7); // one instance, reused: it must not carry state across servers

        async Task<byte[]> Fetch()
        {
            await using var server = TcpServer.Listen(0, Handler.Send(new string('A', 200)), new ServerOptions { Faults = faults });
            await using var client = await server.ConnectAsync();
            return await client.ReadToEndAsync();
        }

        Assert.Equal(await Fetch(), await Fetch());
    }

    [Fact]
    public async Task WildcardBind_ClientsConnectThroughLoopback()
    {
        await using var server = TcpServer.Listen(new IPEndPoint(IPAddress.Any, 0), Handler.Send("hi\r\n"));

        Assert.Equal("127.0.0.1", server.Host);
        await using var client = await server.ConnectAsync();
        Assert.Equal("hi", await client.ReadLineAsync());
    }

    [Fact]
    public async Task FailedTlsHandshake_IsAnEvent_NotAHandlerError()
    {
        await using var server = TcpServer.Listen(0, Handler.Send("secret\r\n"), new ServerOptions { Tls = TlsOptions.SelfSigned() });
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, server.Port);
            await plain.GetStream().WriteAsync("GET / HTTP/1.0\r\n\r\n"u8.ToArray());
        }

        await server.Transcript.WaitForClosedConnectionsAsync(1, TimeSpan.FromSeconds(5));
        Assert.Contains(server.Transcript[0].Events, e => e.StartsWith("TLS handshake failed", StringComparison.Ordinal));
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task PeerResetDuringAWrite_IsRecordedAsAReset()
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            var chunk = new byte[64 * 1024];
            while (true) await connection.WriteAsync(chunk); // until the client's reset breaks the write
        });
        var client = await server.ConnectAsync();
        await server.Transcript.WaitForConnectionsAsync(1);
        await client.ReadExactlyAsync(1); // the server is writing
        client.Abort();

        await server.Transcript.WaitForClosedConnectionsAsync(1, TimeSpan.FromSeconds(5));
        Assert.Contains("reset by peer", server.Transcript[0].Events);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task Conversation_OnMismatchAppliesToComputedReplies()
    {
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .OnMismatch("500 what?")
            .Expect("ECHO *", line => line[5..]));
        await using var client = await server.ConnectAsync();

        await client.WriteLineAsync("HELLO");

        Assert.Equal("500 what?", await client.ReadLineAsync());
        await server.WaitForErrorsAsync();
    }

    [Fact]
    public async Task Stop_ResetsAHandlerThatIgnoresCancellation_AfterShutdownTimeout()
    {
        var server = TcpServer.Listen(0, _ => Task.Delay(Timeout.Infinite, CancellationToken.None),
            new ServerOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(200) });
        await using var client = await server.ConnectAsync();
        await server.Transcript.WaitForConnectionsAsync(1);

        await server.DisposeAsync();

        Assert.Contains("reset", server.Transcript[0].Events);
        var read = await Record.ExceptionAsync(async () => Assert.Null(await client.ReadLineAsync()));
        Assert.True(read is null or IOException or SocketException, $"unexpected {read}");
    }

    [Fact]
    public async Task MaxConcurrency_LimitsHandlersRunningAtOnce()
    {
        var running = 0;
        var peak = 0;
        await using var server = TcpServer.Listen(0, async connection =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(100);
            Interlocked.Decrement(ref running);
            await connection.WriteLineAsync("done");
        }, new ServerOptions { MaxConcurrency = 2 });

        var clients = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => server.ConnectAsync()));
        foreach (var client in clients)
        {
            Assert.Equal("done", await client.ReadLineAsync());
            await client.DisposeAsync();
        }

        Assert.Equal(2, peak);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
