using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Intertube.Stub.Tests;

public class FaultTests
{
    [Fact]
    public async Task Latency_DelaysWrites()
    {
        await using var server = TcpServer.Listen(0, Handler.Send("slow\n"),
            new ServerOptions { Faults = FaultOptions.Latency(TimeSpan.FromMilliseconds(200)) });
        var watch = Stopwatch.StartNew();
        await using var client = await server.ConnectAsync();

        Assert.Equal("slow", await client.ReadLineAsync());
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(150), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Fragment_SplitsWritesIntoSeparateSegments()
    {
        await using var server = TcpServer.Listen(0, Handler.Send("abcdefgh\n"),
            new ServerOptions { Faults = FaultOptions.Fragment(3, TimeSpan.FromMilliseconds(20)) });
        await using var client = await server.ConnectAsync();

        Assert.Equal("abcdefgh", await client.ReadLineAsync());
        await server.Transcript.WaitForClosedConnectionsAsync(1);
        var sent = server.Transcript[0].Entries.Where(e => e.Direction == TranscriptDirection.Sent).Select(e => e.Data.Length);
        Assert.Equal([3, 3, 3], sent);
    }

    [Fact]
    public async Task DisconnectAfter_ResetsMidMessage()
    {
        await using var server = TcpServer.Listen(0, async c =>
        {
            await c.WriteLineAsync("first line");
            await c.WriteLineAsync("second line");
            Assert.Fail("unreachable: the fault should have cut the connection");
        }, new ServerOptions { Faults = FaultOptions.DisconnectAfter(15) });

        using var client = new TcpClient();
        await client.ConnectAsync(server.Host, server.Port);
        var received = new MemoryStream();
        try
        {
            await client.GetStream().CopyToAsync(received);
        }
        catch (IOException)
        {
            // Reset by peer is the point.
        }

        // A reset lets the OS discard data the client had not read yet (Windows does), so the
        // client sees some prefix; the transcript records exactly what went on the wire.
        Assert.StartsWith(Encoding.UTF8.GetString(received.ToArray()), "first line\r\nsec");
        await server.Transcript.WaitForClosedConnectionsAsync(1);
        Assert.Equal("first line\r\nsec", server.Transcript.SentText);
        Assert.Empty(server.Errors);
        Assert.Contains("reset", server.Transcript[0].Events);
    }

    [Fact]
    public async Task DisconnectAfter_CloseMode_SendsEndOfStream()
    {
        await using var server = TcpServer.Listen(0, async c => await c.WriteAsync("0123456789"),
            new ServerOptions { Faults = FaultOptions.DisconnectAfter(4, DisconnectMode.Close) });
        await using var client = await server.ConnectAsync();

        Assert.Equal("0123", Encoding.UTF8.GetString(await client.ReadToEndAsync()));
    }

    [Fact]
    public async Task Corrupt_IsDeterministicForASeed()
    {
        async Task<byte[]> Fetch()
        {
            await using var server = TcpServer.Listen(0, Handler.Send(new string('A', 200)),
                new ServerOptions { Faults = FaultOptions.Corrupt(0.1, seed: 7) });
            await using var client = await server.ConnectAsync();
            return await client.ReadToEndAsync();
        }

        var first = await Fetch();
        var second = await Fetch();
        Assert.Equal(first, second);
        Assert.Contains(first, b => b != (byte)'A');
        Assert.Equal(200, first.Length);
    }

    [Fact]
    public async Task ConnectDelay_HoldsTheGreeting()
    {
        await using var server = TcpServer.Listen(0, Handler.Send("220 hi\r\n"),
            new ServerOptions { Faults = new() { ConnectDelay = TimeSpan.FromMilliseconds(200) } });
        var watch = Stopwatch.StartNew();
        await using var client = await server.ConnectAsync();

        await client.ReadLineAsync();
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task FaultsCanChangePerConnection()
    {
        await using var server = TcpServer.Listen(0, async c =>
        {
            await c.WriteLineAsync("fine");
            c.Faults = FaultOptions.DisconnectAfter(0, DisconnectMode.Close);
            await c.WriteLineAsync("never arrives");
        });
        await using var client = await server.ConnectAsync();

        Assert.Equal("fine", await client.ReadLineAsync());
        Assert.Null(await client.ReadLineAsync());
        await server.Transcript.WaitForClosedConnectionsAsync(1);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task Hang_LetsClientTimeOut()
    {
        await using var server = TcpServer.Listen(0, Handler.Hang);
        await using var client = await server.ConnectAsync();
        await client.WriteLineAsync("anyone there?");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.ReadLineAsync(timeout.Token));
        await server.Transcript.WaitForReceivedLineAsync("anyone there?");
    }

    [Fact]
    public async Task Reset_RefusesService()
    {
        await using var server = TcpServer.Listen(0, Handler.Reset);
        await using var client = await server.ConnectAsync();

        await Assert.ThrowsAnyAsync<IOException>(async () => await client.ReadLineAsync());
    }
}
