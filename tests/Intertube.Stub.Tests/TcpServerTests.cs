using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace Intertube.Stub.Tests;

public class TcpServerTests
{
    [Fact]
    public async Task ReadmeExample_RepliesOnEphemeralPort()
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            var request = await connection.ReadLineAsync();
            await connection.WriteLineAsync($"Received: {request}");
        });

        Assert.NotEqual(0, server.Port);

        using var client = new TcpClient();
        await client.ConnectAsync(server.Host, server.Port);
        using var reader = new StreamReader(client.GetStream());
        await client.GetStream().WriteAsync("hello\r\n"u8.ToArray());

        Assert.Equal("Received: hello", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync()); // server closed after the handler returned

        await server.Transcript.WaitForClosedConnectionsAsync(1);
        server.Transcript.AssertReceivedLine("hello");
        server.Transcript.AssertSentLine("Received: *");
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task ReadLinesAsync_StreamsUntilClientCloses()
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            var count = 0;
            await foreach (var line in connection.ReadLinesAsync())
                await connection.WriteLineAsync($"{++count}:{line.ToUpperInvariant()}");
        });

        await using var client = await server.ConnectAsync();
        await client.WriteAsync("a\nb\r\nc\n");
        Assert.Equal("1:A", await client.ReadLineAsync());
        Assert.Equal("2:B", await client.ReadLineAsync());
        Assert.Equal("3:C", await client.ReadLineAsync());
    }

    [Fact]
    public async Task MixedFraming_LinesDelimitersAndExactBytesShareOneBuffer()
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            var header = await connection.ReadLineAsync();          // "LEN 5"
            var length = int.Parse(header!.Split(' ')[1]);
            var body = await connection.ReadExactlyAsync(length);  // "hello"
            var tail = await connection.ReadUntilAsync("<END>");    // "world"
            await connection.WriteLineAsync($"{Encoding.UTF8.GetString(body)}|{tail}");
        });

        await using var client = await server.ConnectAsync();
        // Everything in one write, so the server must split it out of a single buffer.
        await client.WriteAsync("LEN 5\r\nhelloworld<END>");
        Assert.Equal("hello|world", await client.ReadLineAsync());
    }

    [Fact]
    public async Task Stream_CanBeUsedWithStandardReaders()
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            using var reader = new BinaryReader(connection.Stream, Encoding.UTF8, leaveOpen: true);
            var value = reader.ReadInt32();
            await connection.WriteLineAsync((value * 2).ToString());
        });

        await using var client = await server.ConnectAsync();
        await client.WriteAsync(BitConverter.GetBytes(21));
        Assert.Equal("42", await client.ReadLineAsync());
        await server.Transcript.WaitForSentLineAsync("42");
    }

    [Fact]
    public async Task Concurrent_HandlesClientsInParallel()
    {
        var gate = new TaskCompletionSource();
        await using var server = TcpServer.Listen(0, async connection =>
        {
            await connection.ReadLineAsync();
            await gate.Task.WaitAsync(connection.CancellationToken);
            await connection.WriteLineAsync("done");
        });

        await using var a = await server.ConnectAsync();
        await using var b = await server.ConnectAsync();
        await a.WriteLineAsync("x");
        await b.WriteLineAsync("y");
        await server.Transcript.WaitUntilAsync(() => server.Transcript.ReceivedLines.Count == 2, "both requests");

        Assert.Equal(2, server.ActiveConnections);
        gate.SetResult();
        Assert.Equal("done", await a.ReadLineAsync());
        Assert.Equal("done", await b.ReadLineAsync());
    }

    [Fact]
    public async Task Sequential_HandlesOneConnectionAtATime()
    {
        var running = 0;
        var maxRunning = 0;
        await using var server = TcpServer.Listen(0, async connection =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(50);
            await connection.WriteLineAsync("ok");
            Interlocked.Decrement(ref running);
        }, ServerOptions.Sequential);

        var clients = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => server.ConnectAsync()));
        foreach (var client in clients)
        {
            Assert.Equal("ok", await client.ReadLineAsync());
            await client.DisposeAsync();
        }

        Assert.Equal(1, maxRunning);
        server.Transcript.AssertConnectionCount(4);
    }

    [Fact]
    public async Task HandlerErrors_AreCapturedNotLost()
    {
        await using var server = TcpServer.Listen(0, _ => throw new InvalidOperationException("boom"));
        await using var client = await server.ConnectAsync();

        await server.WaitForErrorsAsync(1);
        var error = Assert.Single(server.Errors);
        Assert.Equal("boom", error.Message);
        Assert.Throws<AggregateException>(server.ThrowIfFaulted);
    }

    [Fact]
    public async Task Dispose_StopsPromptlyEvenWithBlockedHandlers()
    {
        var server = TcpServer.Listen(0, Handler.Hang);
        await using var client = await server.ConnectAsync();
        await server.Transcript.WaitForConnectionsAsync(1);

        var started = DateTime.UtcNow;
        await server.DisposeAsync();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5));
        Assert.Empty(server.Errors);

        // The client sees the connection go away.
        Assert.Null(await client.ReadLineAsync());
    }

    [Fact]
    public async Task ReadTimeout_FailsTheHandler()
    {
        await using var server = TcpServer.Listen(0, async c => await c.ReadLineAsync(),
            new ServerOptions { ReadTimeout = TimeSpan.FromMilliseconds(100) });
        await using var client = await server.ConnectAsync();

        await server.WaitForErrorsAsync(1);
        Assert.IsType<TimeoutException>(Assert.Single(server.Errors));
    }

    [Fact]
    public async Task ClientReset_IsRecordedButNotAnError()
    {
        await using var server = TcpServer.Listen(0, Handler.Echo);
        var client = await server.ConnectAsync();
        await client.WriteLineAsync("hi");
        Assert.Equal("hi", await client.ReadLineAsync());
        client.Abort();

        await server.Transcript.WaitForClosedConnectionsAsync(1);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task Log_ReceivesLiveTraffic()
    {
        var log = new List<string>();
        await using var server = TcpServer.Listen(0, Handler.Lines(l => l),
            new ServerOptions { Log = line => { lock (log) log.Add(line); } });
        await using var client = await server.ConnectAsync();
        await client.WriteLineAsync("ping");
        await client.ReadLineAsync();
        await server.Transcript.WaitForSentLineAsync("ping"); // the log fires when the transcript records, which can trail the client's read

        lock (log)
        {
            Assert.Contains(log, l => l == @"#1 <- ping\r\n");
            Assert.Contains(log, l => l == @"#1 -> ping\r\n");
        }
    }

    [Fact]
    public async Task Transcript_ToString_IsReadable()
    {
        await using var server = TcpServer.Listen(0, Handler.Lines(l => $"echo {l}"));
        await using (var client = await server.ConnectAsync())
        {
            await client.WriteLineAsync("one");
            await client.ReadLineAsync();
        }
        await server.Transcript.WaitForClosedConnectionsAsync(1);

        var text = server.Transcript.ToString();
        Assert.Contains("#1 <- one", text);
        Assert.Contains("#1 -> echo one", text);
        Assert.Contains("#1 -- connected from", text);
    }

    [Fact]
    public async Task FailedAssertion_IncludesTranscript()
    {
        await using var server = TcpServer.Listen(0, Handler.Lines(_ => null));
        await using var client = await server.ConnectAsync();
        await client.WriteLineAsync("HELO there");
        await server.Transcript.WaitForReceivedLineAsync("HELO *");

        var ex = Assert.Throws<TranscriptAssertionException>(() => server.Transcript.AssertReceivedLine("EHLO *"));
        Assert.Contains("#1 <- HELO there", ex.Message);
    }

    [Fact]
    public async Task WaitFor_TimesOutWithTranscript()
    {
        await using var server = TcpServer.Listen(0, Handler.Hang);
        await using var client = await server.ConnectAsync();
        await client.WriteLineAsync("nope");

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            server.Transcript.WaitForReceivedLineAsync("yes", TimeSpan.FromMilliseconds(200)));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task PipeReaderInput_SupportsBinaryParsing()
    {
        await using var server = TcpServer.Listen(0, async connection =>
        {
            // Length-prefixed frames parsed straight off the PipeReader.
            while (true)
            {
                var result = await connection.Input.ReadAsync();
                var buffer = result.Buffer;
                while (buffer.Length >= 2 && buffer.Length >= 2 + buffer.FirstSpan[0])
                {
                    var length = buffer.FirstSpan[0];
                    var payload = buffer.Slice(2, length).ToArray();
                    await connection.WriteLineAsync(Encoding.ASCII.GetString(payload));
                    buffer = buffer.Slice(2 + length);
                }
                connection.Input.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted) return;
            }
        });

        await using var client = await server.ConnectAsync();
        await client.WriteAsync(new byte[] { 3, 0, (byte)'a', (byte)'b', (byte)'c', 2, 0, (byte)'x' });
        await client.WriteAsync(new byte[] { (byte)'y' });
        Assert.Equal("abc", await client.ReadLineAsync());
        Assert.Equal("xy", await client.ReadLineAsync());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
