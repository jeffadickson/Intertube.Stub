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
}
