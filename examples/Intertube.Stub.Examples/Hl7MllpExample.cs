using System.Net.Sockets;
using System.Text;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

/// <summary>Thrown when the receiving system rejects a message outright (MSA AR): resending will not help.</summary>
public sealed class Hl7RejectedException(string controlId, string? text) : Exception($"Message {controlId} was rejected: {text}");

/// <summary>
/// Sends HL7 v2 messages over MLLP (<c>0x0B message 0x1C 0x0D</c>) and waits for the matching
/// acknowledgement, the way a hospital interface engine does. It keeps one connection open and
/// resends on a negative (AE) or missing ACK, on a new connection, because after a lost ACK the
/// stream may hold a late reply. An ACK must echo the message's control ID.
/// </summary>
public sealed class MllpSender(string host, int port, TimeSpan ackTimeout, int maxAttempts = 3) : IAsyncDisposable
{
    private const byte StartBlock = 0x0B, EndBlock = 0x1C, CarriageReturn = 0x0D;
    private TcpClient? _client;

    public int ConnectionsOpened { get; private set; }

    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        var controlId = Field(message, "MSH", 10) ?? throw new ArgumentException("MSH-10 (control ID) is missing.", nameof(message));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var stream = await StreamAsync(cancellationToken);
                await stream.WriteAsync((byte[])[StartBlock, .. Encoding.UTF8.GetBytes(message.Replace("\n", "\r")), EndBlock, CarriageReturn], cancellationToken);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ackTimeout);
                var ack = await ReadFrameAsync(stream, timeout.Token);
                if (Field(ack, "MSA", 2) != controlId) throw new IOException($"ACK for {Field(ack, "MSA", 2)} while waiting for {controlId}.");
                switch (Field(ack, "MSA", 1))
                {
                    case "AA" or "CA":
                        return;
                    case "AR" or "CR":
                        throw new Hl7RejectedException(controlId, Field(ack, "MSA", 3));
                    default:
                        throw new IOException($"Negative ACK {Field(ack, "MSA", 1)}: {Field(ack, "MSA", 3)}");
                }
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException or SocketException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                Disconnect();
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
    }

    private async Task<NetworkStream> StreamAsync(CancellationToken cancellationToken)
    {
        if (_client is { Connected: true }) return _client.GetStream();
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, cancellationToken);
        ConnectionsOpened++;
        return _client.GetStream();
    }

    private static async Task<string> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var frame = new List<byte>();
        var buffer = new byte[1];
        var started = false;
        while (true)
        {
            if (await stream.ReadAsync(buffer, cancellationToken) == 0) throw new IOException("Connection closed while waiting for the ACK.");
            var b = buffer[0];
            if (!started)
            {
                started = b == StartBlock;
                continue;
            }
            if (b == CarriageReturn && frame.Count > 0 && frame[^1] == EndBlock)
                return Encoding.UTF8.GetString(frame.ToArray(), 0, frame.Count - 1);
            frame.Add(b);
        }
    }

    /// <summary>Field <paramref name="index"/> of the first segment named <paramref name="segment"/> (HL7 numbering).</summary>
    public static string? Field(string message, string segment, int index)
    {
        var line = message.Split('\r', '\n').FirstOrDefault(s => s.StartsWith(segment + "|", StringComparison.Ordinal));
        if (line is null) return null;
        var fields = line.Split('|');
        var i = segment == "MSH" ? index - 1 : index; // MSH-1 is the separator itself
        return i < fields.Length ? fields[i] : null;
    }

    private void Disconnect()
    {
        _client?.Dispose();
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}

// ---- A reusable fake receiver ------------------------------------------------------------------

/// <summary>
/// An MLLP receiver for tests. <see cref="Respond"/> picks the acknowledgement code per message
/// (AA, AE, AR), or null to never answer. Every received message and ACK is recorded as a decoded
/// transcript message, so tests can assert in HL7 terms rather than on framed bytes.
/// </summary>
public sealed class FakeMllpReceiver : IAsyncDisposable
{
    private readonly List<string> _messages = [];

    public FakeMllpReceiver(ServerOptions? options = null) =>
        Server = TcpServer.Listen(0, HandleAsync, options);

    public TcpServer Server { get; }

    /// <summary>The ACK code for a message: "AA" by default. Return null to leave it unacknowledged.</summary>
    public Func<string, int, string?> Respond { get; set; } = (_, _) => "AA";

    /// <summary>Rewrite the ACK's control ID (MSA-2), to simulate a confused receiver.</summary>
    public Func<string, string>? MangleControlId { get; set; }

    public IReadOnlyList<string> Messages
    {
        get { lock (_messages) return [.. _messages]; }
    }

    private async Task HandleAsync(Connection connection)
    {
        while (await connection.ReadUntilAsync(new byte[] { 0x1C, 0x0D }) is { } frame)
        {
            var text = Encoding.UTF8.GetString(frame.AsSpan(frame.AsSpan().IndexOf((byte)0x0B) + 1));
            int count;
            lock (_messages)
            {
                _messages.Add(text);
                count = _messages.Count;
            }
            var controlId = MllpSender.Field(text, "MSH", 10) ?? "";
            connection.Transcript.RecordMessage(TranscriptDirection.Received, $"{MllpSender.Field(text, "MSH", 9)} {controlId}");

            var code = Respond(text, count);
            if (code is null)
            {
                connection.Transcript.RecordMessage(TranscriptDirection.Event, $"no ACK for {controlId}");
                continue;
            }
            var echoed = MangleControlId?.Invoke(controlId) ?? controlId;
            var ack = $"MSH|^~\\&|LAB|HOSP|SENDER|HOSP|{DateTime.UtcNow:yyyyMMddHHmmss}||ACK^A01|ACK{controlId}|P|2.5\rMSA|{code}|{echoed}|{(code == "AA" ? "" : "Rejected by test")}\r";
            await connection.WriteAsync((byte[])[0x0B, .. Encoding.UTF8.GetBytes(ack), 0x1C, 0x0D]);
            connection.Transcript.RecordMessage(TranscriptDirection.Sent, $"ACK {code} {echoed}");
        }
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// MLLP is a framing every hospital integration gets subtly wrong: lost ACKs, late ACKs, negative
/// ACKs and partial frames. Each is one line of setup here.
/// </summary>
public sealed class Hl7MllpExample : IAsyncDisposable
{
    private readonly FakeMllpReceiver _receiver = new();

    private static string Admit(string controlId) =>
        $"MSH|^~\\&|EHR|HOSP|LAB|HOSP|20260101120000||ADT^A01|{controlId}|P|2.5\rPID|1||12345^^^HOSP||Lovelace^Ada\r";

    private MllpSender Sender(TimeSpan? timeout = null) =>
        new(_receiver.Server.Host, _receiver.Server.Port, timeout ?? TimeSpan.FromSeconds(5));

    public ValueTask DisposeAsync() => _receiver.DisposeAsync();

    [Fact]
    public async Task Messages_AreAcknowledged_OnOneConnection()
    {
        await using var sender = Sender();

        await sender.SendAsync(Admit("1001"));
        await sender.SendAsync(Admit("1002"));

        Assert.Equal(1, sender.ConnectionsOpened);
        _receiver.Server.Transcript.AssertReceivedMessagesInOrder("ADT^A01 1001", "ADT^A01 1002");
        _receiver.Server.Transcript.AssertSentMessage("ACK AA 1002");
        Assert.Equal("Lovelace^Ada", MllpSender.Field(_receiver.Messages[0], "PID", 5));
    }

    [Fact]
    public async Task NegativeAck_IsResent()
    {
        _receiver.Respond = (_, count) => count == 1 ? "AE" : "AA";
        await using var sender = Sender();

        await sender.SendAsync(Admit("2001"));

        Assert.Equal(2, _receiver.Messages.Count);
        Assert.Equal(2, sender.ConnectionsOpened);
    }

    [Fact]
    public async Task Reject_IsNotResent()
    {
        _receiver.Respond = (_, _) => "AR";
        await using var sender = Sender();

        await Assert.ThrowsAsync<Hl7RejectedException>(() => sender.SendAsync(Admit("3001")));

        Assert.Single(_receiver.Messages);
    }

    [Fact]
    public async Task LostAck_TimesOut_AndIsResentOnAFreshConnection()
    {
        _receiver.Respond = (_, count) => count == 1 ? null : "AA";
        await using var sender = Sender(TimeSpan.FromMilliseconds(200));

        await sender.SendAsync(Admit("4001"));

        Assert.Equal(2, sender.ConnectionsOpened);
        Assert.Equal(["4001", "4001"], _receiver.Messages.Select(m => MllpSender.Field(m, "MSH", 10)));
    }

    [Fact]
    public async Task AckForTheWrongMessage_IsNotTrusted()
    {
        _receiver.MangleControlId = id => _receiver.Messages.Count == 1 ? "9999" : id;
        await using var sender = Sender();

        await sender.SendAsync(Admit("5001"));

        Assert.Equal(2, _receiver.Messages.Count);
        _receiver.Server.Transcript.AssertSentMessage("ACK AA 9999");
    }

    [Fact]
    public async Task AckDribbledOutOneByteAtATime_IsReassembled()
    {
        await using var receiver = new FakeMllpReceiver(new ServerOptions { Faults = FaultOptions.Fragment(1) });
        await using var sender = new MllpSender(receiver.Server.Host, receiver.Server.Port, TimeSpan.FromSeconds(5));

        await sender.SendAsync(Admit("6001"));

        Assert.Equal(1, sender.ConnectionsOpened);
    }

    [Fact]
    public async Task ReceiverThatResetsEveryConnection_GivesUpAfterMaxAttempts()
    {
        await using var receiver = TcpServer.Listen(0, Handler.Reset);
        await using var sender = new MllpSender(receiver.Host, receiver.Port, TimeSpan.FromSeconds(5), maxAttempts: 2);

        await Assert.ThrowsAnyAsync<IOException>(() => sender.SendAsync(Admit("7001")));

        Assert.Equal(2, sender.ConnectionsOpened);
    }
}
