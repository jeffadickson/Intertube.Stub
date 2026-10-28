namespace Intertube.Stub.Tests;

/// <summary>Decoded protocol messages in transcripts, and <see cref="Connection.Terminated"/>.</summary>
public class MessageTranscriptTests
{
    /// <summary>A toy binary protocol: one length byte, then the payload, answered with its length.</summary>
    private static async Task LengthEcho(Connection connection)
    {
        while (true)
        {
            byte[] header;
            try
            {
                header = await connection.ReadExactlyAsync(1);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            var payload = await connection.ReadExactlyAsync(header[0]);
            connection.Transcript.RecordMessage(TranscriptDirection.Received, $"PAYLOAD {payload.Length}");
            await connection.WriteAsync(new[] { header[0] });
            connection.Transcript.RecordMessage(TranscriptDirection.Sent, $"LENGTH {header[0]}");
        }
    }

    [Fact]
    public async Task Messages_AreKeptApartFromBytes_AndAssertable()
    {
        await using var server = TcpServer.Listen(0, LengthEcho);
        await using (var client = await server.ConnectAsync())
        {
            await client.WriteAsync(new byte[] { 3, 1, 2, 3, 1, 9 });
            await client.ReadExactlyAsync(2);
        }

        var transcript = server.Transcript;
        await transcript.WaitForSentMessageAsync("LENGTH 1");
        Assert.Equal(["PAYLOAD 3", "PAYLOAD 1"], transcript.ReceivedMessages);
        transcript.AssertReceivedMessagesInOrder("PAYLOAD 3", "PAYLOAD *");
        transcript.AssertNotReceivedMessage("PAYLOAD 2");
        Assert.Equal(6, transcript[0].ReceivedBytes.Length);
        Assert.DoesNotContain(transcript.ReceivedLines, l => l.Contains("PAYLOAD"));

        var dump = transcript.ToString();
        Assert.Contains("#1 <= PAYLOAD 3", dump);
        Assert.Contains("#1 => LENGTH 1", dump);
    }

    [Fact]
    public async Task Messages_AreRecorded_EvenWithByteRecordingOff()
    {
        var log = new List<string>();
        await using var server = TcpServer.Listen(0, LengthEcho, new ServerOptions { RecordTranscript = false, Log = l => { lock (log) log.Add(l); } });
        await using (var client = await server.ConnectAsync())
        {
            await client.WriteAsync(new byte[] { 2, 7, 7 });
            await client.ReadExactlyAsync(1);
        }

        await server.Transcript.WaitForSentMessageAsync("LENGTH 2");
        Assert.Empty(server.Transcript[0].ReceivedBytes);
        Assert.Contains("#1 <= PAYLOAD 2", log);
    }

    [Fact]
    public async Task FailedMessageAssertion_ShowsTheMessagesInTheDump()
    {
        await using var server = TcpServer.Listen(0, LengthEcho);
        await using (var client = await server.ConnectAsync())
        {
            await client.WriteAsync(new byte[] { 1, 0 });
            await client.ReadExactlyAsync(1);
        }
        await server.Transcript.WaitForReceivedMessageAsync("PAYLOAD 1");

        var ex = Assert.Throws<TranscriptAssertionException>(() => server.Transcript.AssertReceivedMessage("PAYLOAD 99"));
        Assert.Contains("<= PAYLOAD 1", ex.Message);
    }

    [Fact]
    public async Task Terminated_FiresOnAbort_ButNotWhenThePeerCloses()
    {
        var aborted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TcpServer.Listen(0, async connection =>
        {
            var line = await connection.ReadLineAsync();
            if (line == "abort")
            {
                using var registration = connection.Terminated.Register(() => aborted.TrySetResult(connection.IsTerminated));
                connection.Abort();
                return;
            }
            await connection.ReadToEndAsync();
            peerClosed.TrySetResult(connection.IsTerminated || connection.Terminated.IsCancellationRequested);
        });

        await using (var first = await server.ConnectAsync()) await first.WriteLineAsync("abort");
        Assert.True(await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var second = await server.ConnectAsync();
        await second.WriteLineAsync("stay");
        await second.CloseAsync();
        Assert.False(await peerClosed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
