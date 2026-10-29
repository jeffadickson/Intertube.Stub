using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Intertube.Stub.Tests;

public class UdpServerTests
{
    [Fact]
    public async Task Replies_ToEachDatagram()
    {
        await using var server = UdpServer.Listen(0, d => d.ReplyAsync($"ack {d.Text}").AsTask());

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, server.Port);
        foreach (var text in new[] { "one", "two" })
        {
            await client.SendAsync(Encoding.UTF8.GetBytes(text));
            var reply = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal($"ack {text}", Encoding.UTF8.GetString(reply.Buffer));
        }

        await server.Transcript.WaitForSentAsync(2);
        Assert.Equal(["one", "two"], server.Transcript.ReceivedTexts);
        server.Transcript.AssertReceived("t*");
    }

    [Fact]
    public async Task DropRate_LosesDatagramsReproducibly()
    {
        async Task<IReadOnlyList<string>> Run()
        {
            await using var server = UdpServer.Listen(0, _ => Task.CompletedTask, new UdpServerOptions { DropRate = 0.5, Seed = 42 });
            using var client = new UdpClient();
            client.Connect(IPAddress.Loopback, server.Port);
            for (var i = 0; i < 20; i++) await client.SendAsync(Encoding.UTF8.GetBytes(i.ToString()));
            await server.Transcript.WaitUntilAsync(() => server.Transcript.Records.Count == 20, "20 datagrams");
            return server.Transcript.ReceivedTexts;
        }

        var first = await Run();
        Assert.InRange(first.Count, 1, 19);
        Assert.Equal(first, await Run());
    }

    [Fact]
    public async Task HandlerErrors_AreCaptured()
    {
        await using var server = UdpServer.Listen(0, _ => throw new InvalidDataException("bad packet"));
        using var client = new UdpClient();
        await client.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, server.Port));

        await server.Transcript.WaitUntilAsync(() => server.Errors.Count == 1, "an error");
        Assert.IsType<InvalidDataException>(server.Errors[0]);
    }

    [Fact]
    public async Task OversizedDatagrams_AreDiscardedOnEveryOs()
    {
        var log = new ConcurrentQueue<string>();
        await using var server = UdpServer.Listen(0, _ => Task.CompletedTask, new UdpServerOptions { MaxDatagramSize = 100, Log = log.Enqueue });
        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, server.Port);

        await client.SendAsync(new byte[101]);                      // one over: fills the spare byte (truncated on Unix)
        await client.SendAsync(new byte[1000]);                     // well over: Windows fails the receive outright
        await client.SendAsync(Encoding.ASCII.GetBytes(new string('x', 100)));   // exactly the limit
        await server.Transcript.WaitForReceivedAsync(1);

        // Loopback delivers in order, so the two oversized datagrams were dealt with first.
        Assert.Equal(new string('x', 100), Assert.Single(server.Transcript.ReceivedTexts));
        Assert.Single(server.Transcript.Records);
        Assert.Equal(2, log.Count(line => line.Contains("datagram larger than MaxDatagramSize (100) discarded", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WildcardBind_HostIsLoopback()
    {
        await using var server = UdpServer.Listen(new IPEndPoint(IPAddress.Any, 0), _ => Task.CompletedTask);
        Assert.Equal("127.0.0.1", server.Host);

        using var client = new UdpClient();
        await client.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse(server.Host), server.Port));
        await server.Transcript.WaitForReceivedAsync(1);

        if (Socket.OSSupportsIPv6)
        {
            await using var v6 = UdpServer.Listen(new IPEndPoint(IPAddress.IPv6Any, 0), _ => Task.CompletedTask);
            Assert.Equal("::1", v6.Host);
        }
    }

    [Fact]
    public async Task ReplyToAVanishedClient_DoesNotStopTheReceiveLoop()
    {
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = UdpServer.Listen(0, async d =>
        {
            if (d.Text == "first") await gone.Task; // reply only once the sender has closed, provoking ICMP port unreachable
            await d.ReplyAsync($"ack {d.Text}");
        });

        using (var vanished = new UdpClient())
            await vanished.SendAsync(Encoding.UTF8.GetBytes("first"), new IPEndPoint(IPAddress.Loopback, server.Port));
        await server.Transcript.WaitForReceivedAsync(1);
        gone.SetResult();
        await server.Transcript.WaitForSentAsync(1);

        using var client = new UdpClient();
        client.Connect(IPAddress.Loopback, server.Port);
        await client.SendAsync(Encoding.UTF8.GetBytes("second"));
        var reply = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ack second", Encoding.UTF8.GetString(reply.Buffer));
        Assert.Empty(server.Errors);
    }
}
