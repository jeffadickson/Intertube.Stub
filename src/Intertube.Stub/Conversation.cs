namespace Intertube.Stub;

/// <summary>Thrown when a client strays from a scripted <see cref="Conversation"/>.</summary>
public sealed class ConversationException(string message) : Exception(message);

/// <summary>
/// A scripted, in-order exchange: greet, expect a line, reply, and so on. A mismatch fails the
/// handler, which shows up in <see cref="StreamServer.Errors"/>.
/// <code>
/// Conversation.Create()
///     .Send("220 mail.test ESMTP")
///     .Expect("EHLO *", "250 mail.test")
///     .Expect("QUIT", "221 Bye")
///     .Close();
/// </code>
/// </summary>
public sealed class Conversation : IConnectionHandler
{
    private readonly List<(string Description, Func<Connection, Task> Run)> _steps = [];
    private string? _mismatchReply;

    public static Conversation Create() => new();

    /// <summary>Send a line.</summary>
    public Conversation Send(string line) =>
        Step($"send \"{line}\"", c => c.WriteLineAsync(line).AsTask());

    /// <summary>Send several lines in one write.</summary>
    public Conversation Send(params string[] lines) =>
        Step($"send {lines.Length} lines", c => c.WriteLinesAsync(lines).AsTask());

    /// <summary>Send text or bytes exactly as given, without a line terminator.</summary>
    public Conversation SendRaw(string text) =>
        Step($"send raw \"{TranscriptBase.Escape(text)}\"", c => c.WriteAsync(text).AsTask());

    public Conversation SendRaw(ReadOnlyMemory<byte> data) =>
        Step($"send {data.Length} raw bytes", c => c.WriteAsync(data).AsTask());

    /// <summary>Read the next line and require it to match.</summary>
    public Conversation Expect(LineMatcher matcher) =>
        Step($"expect {matcher}", async connection =>
        {
            var line = await connection.ReadLineAsync().ConfigureAwait(false);
            if (line is not null && matcher.IsMatch(line)) return;
            if (line is not null && _mismatchReply is not null) await connection.WriteLineAsync(_mismatchReply).ConfigureAwait(false);
            throw new ConversationException(line is null
                ? $"Expected a line matching {matcher} but the client closed the connection."
                : $"Expected a line matching {matcher} but received \"{TranscriptBase.Escape(line)}\".");
        });

    /// <summary>Expect a line, then reply.</summary>
    public Conversation Expect(LineMatcher matcher, string reply) => Expect(matcher).Send(reply);

    /// <summary>Expect a line, then reply with something computed from it.</summary>
    public Conversation Expect(LineMatcher matcher, Func<string, string> reply) =>
        Step($"expect {matcher} and reply", async connection =>
        {
            var line = await connection.ReadLineAsync().ConfigureAwait(false);
            if (line is null || !matcher.IsMatch(line))
                throw new ConversationException($"Expected a line matching {matcher} but received {(line is null ? "end of stream" : $"\"{TranscriptBase.Escape(line)}\"")}.");
            await connection.WriteLineAsync(reply(line)).ConfigureAwait(false);
        });

    /// <summary>Read and require an exact run of bytes, such as a binary preamble.</summary>
    public Conversation ExpectBytes(ReadOnlyMemory<byte> expected) =>
        Step($"expect {expected.Length} bytes", async connection =>
        {
            var actual = await connection.ReadExactlyAsync(expected.Length).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(expected.Span))
                throw new ConversationException($"Expected bytes {Convert.ToHexString(expected.Span)} but received {Convert.ToHexString(actual)}.");
        });

    /// <summary>Read until a delimiter (for example <c>"\r\n.\r\n"</c> after SMTP DATA) and optionally check the content.</summary>
    public Conversation ExpectUntil(string delimiter, Action<string>? verify = null) =>
        Step($"read until \"{TranscriptBase.Escape(delimiter)}\"", async connection =>
        {
            var content = await connection.ReadUntilAsync(delimiter).ConfigureAwait(false)
                ?? throw new ConversationException($"Expected data ending in \"{TranscriptBase.Escape(delimiter)}\" but the stream ended.");
            verify?.Invoke(content);
        });

    /// <summary>Pause.</summary>
    public Conversation Delay(TimeSpan delay) =>
        Step($"delay {delay}", c => Task.Delay(delay, c.CancellationToken));

    /// <summary>Switch to TLS (STARTTLS) using the server's <see cref="ServerOptions.Tls"/> or the given options.</summary>
    public Conversation StartTls(TlsOptions? options = null) =>
        Step("start TLS", c => c.UpgradeToTlsAsync(options));

    /// <summary>Change the connection's faults from this point on.</summary>
    public Conversation WithFaults(FaultOptions faults) =>
        Step("change faults", c =>
        {
            c.Faults = faults;
            return Task.CompletedTask;
        });

    /// <summary>Run arbitrary code.</summary>
    public Conversation Do(Func<Connection, Task> action, string description = "custom step") => Step(description, action);

    /// <summary>Close gracefully. Without this the server closes once the script ends anyway.</summary>
    public Conversation Close() => Step("close", c => c.CloseAsync());

    /// <summary>Reset the connection.</summary>
    public Conversation Reset() =>
        Step("reset", c =>
        {
            c.Abort();
            return Task.CompletedTask;
        });

    /// <summary>Stop replying and wait for the client to give up.</summary>
    public Conversation Hang() => Step("hang", c => c.HangAsync());

    /// <summary>Reply with this line before failing on an unexpected line (for example <c>"500 Unrecognized command"</c>).</summary>
    public Conversation OnMismatch(string reply)
    {
        _mismatchReply = reply;
        return this;
    }

    public async Task HandleAsync(Connection connection)
    {
        for (var i = 0; i < _steps.Count; i++)
        {
            try
            {
                await _steps[i].Run(connection).ConfigureAwait(false);
            }
            catch (ConversationException ex)
            {
                throw new ConversationException($"Step {i + 1} ({_steps[i].Description}): {ex.Message}");
            }
        }
    }

    private Conversation Step(string description, Func<Connection, Task> run)
    {
        _steps.Add((description, run));
        return this;
    }
}
