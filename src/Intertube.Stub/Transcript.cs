using System.Net;
using System.Text;
using Intertube.Stub.Internal;

namespace Intertube.Stub;

/// <summary>Which way recorded traffic travelled, from the recording side's point of view.</summary>
public enum TranscriptDirection
{
    Received,
    Sent,

    /// <summary>A connection event such as "connected", "TLS established" or "reset by peer".</summary>
    Event,
}

/// <summary>
/// One recorded chunk of traffic, one event, or one decoded protocol message (see
/// <see cref="ConnectionTranscript.RecordMessage"/>).
/// </summary>
public sealed record TranscriptEntry(TranscriptDirection Direction, byte[] Data, string? Description, DateTimeOffset Timestamp)
{
    /// <summary>True for a decoded message recorded by a protocol layer rather than raw bytes.</summary>
    public bool IsMessage => Direction != TranscriptDirection.Event && Description is not null;

    public override string ToString() => Direction switch
    {
        TranscriptDirection.Event => $"-- {Description}",
        _ when IsMessage => $"{(Direction == TranscriptDirection.Received ? "<=" : "=>")} {Description}",
        _ => $"{(Direction == TranscriptDirection.Received ? "<-" : "->")} {Data.Length} bytes",
    };
}

/// <summary>Thrown by transcript assertions; the message includes the full transcript.</summary>
public sealed class TranscriptAssertionException(string message) : Exception(message);

/// <summary>Shared query, wait and assertion helpers for recorded traffic.</summary>
public abstract class TranscriptBase
{
    private protected TranscriptBase(ChangeSignal signal) => Signal = signal;

    private protected ChangeSignal Signal { get; }

    /// <summary>Complete lines received (LF or CRLF terminated, terminator removed).</summary>
    public abstract IReadOnlyList<string> ReceivedLines { get; }

    /// <summary>Complete lines sent.</summary>
    public abstract IReadOnlyList<string> SentLines { get; }

    /// <summary>Everything received, decoded as text.</summary>
    public abstract string ReceivedText { get; }

    /// <summary>Everything sent, decoded as text.</summary>
    public abstract string SentText { get; }

    /// <summary>
    /// Decoded messages received, as recorded by a protocol layer with <see cref="ConnectionTranscript.RecordMessage"/>.
    /// For encrypted or binary protocols these are what the line assertions are for plain text ones.
    /// </summary>
    public abstract IReadOnlyList<string> ReceivedMessages { get; }

    /// <summary>Decoded messages sent.</summary>
    public abstract IReadOnlyList<string> SentMessages { get; }

    /// <summary>A readable dump of the traffic, one line per protocol line.</summary>
    public abstract override string ToString();

    public bool HasReceivedLine(LineMatcher matcher) => ReceivedLines.Any(matcher.IsMatch);

    public bool HasSentLine(LineMatcher matcher) => SentLines.Any(matcher.IsMatch);

    public void AssertReceivedLine(LineMatcher matcher) =>
        Check(HasReceivedLine(matcher), $"Expected to receive a line matching {matcher}");

    public void AssertNotReceivedLine(LineMatcher matcher) =>
        Check(!HasReceivedLine(matcher), $"Expected not to receive a line matching {matcher}");

    public void AssertSentLine(LineMatcher matcher) =>
        Check(HasSentLine(matcher), $"Expected to send a line matching {matcher}");

    /// <summary>Assert the received lines contain these matches in order (other lines may come between).</summary>
    public void AssertReceivedLinesInOrder(params LineMatcher[] matchers) =>
        AssertInOrder(ReceivedLines, matchers, "receive");

    /// <summary>Assert the sent lines contain these matches in order (other lines may come between).</summary>
    public void AssertSentLinesInOrder(params LineMatcher[] matchers) =>
        AssertInOrder(SentLines, matchers, "send");

    public bool HasReceivedMessage(LineMatcher matcher) => ReceivedMessages.Any(matcher.IsMatch);

    public bool HasSentMessage(LineMatcher matcher) => SentMessages.Any(matcher.IsMatch);

    public void AssertReceivedMessage(LineMatcher matcher) =>
        Check(HasReceivedMessage(matcher), $"Expected to receive a message matching {matcher}");

    public void AssertNotReceivedMessage(LineMatcher matcher) =>
        Check(!HasReceivedMessage(matcher), $"Expected not to receive a message matching {matcher}");

    public void AssertSentMessage(LineMatcher matcher) =>
        Check(HasSentMessage(matcher), $"Expected to send a message matching {matcher}");

    /// <summary>Assert the received messages contain these matches in order (others may come between).</summary>
    public void AssertReceivedMessagesInOrder(params LineMatcher[] matchers) =>
        AssertInOrder(ReceivedMessages, matchers, "receive messages");

    public Task WaitForReceivedMessageAsync(LineMatcher matcher, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => HasReceivedMessage(matcher), $"a received message matching {matcher}", timeout, cancellationToken);

    public Task WaitForSentMessageAsync(LineMatcher matcher, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => HasSentMessage(matcher), $"a sent message matching {matcher}", timeout, cancellationToken);

    /// <summary>Assert the received text contains <paramref name="text"/>, ignoring line structure.</summary>
    public void AssertReceived(string text) =>
        Check(ReceivedText.Contains(text, StringComparison.Ordinal), $"Expected to receive \"{Escape(text)}\"");

    public Task WaitForReceivedLineAsync(LineMatcher matcher, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => HasReceivedLine(matcher), $"a received line matching {matcher}", timeout, cancellationToken);

    public Task WaitForSentLineAsync(LineMatcher matcher, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => HasSentLine(matcher), $"a sent line matching {matcher}", timeout, cancellationToken);

    /// <summary>
    /// Wait until <paramref name="condition"/> holds, re-checking whenever traffic is recorded.
    /// Throws <see cref="TimeoutException"/> (with the transcript) after <paramref name="timeout"/>, default 5 seconds.
    /// </summary>
    public async Task WaitUntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await Signal.WaitUntilAsync(condition, timeout, () => description, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}{Environment.NewLine}{Dump()}", ex);
        }
    }

    private protected void Check(bool condition, string message)
    {
        if (!condition) throw new TranscriptAssertionException($"{message}.{Environment.NewLine}{Dump()}");
    }

    private void AssertInOrder(IReadOnlyList<string> lines, LineMatcher[] matchers, string verb)
    {
        var next = 0;
        foreach (var line in lines)
            if (next < matchers.Length && matchers[next].IsMatch(line))
                next++;
        Check(next == matchers.Length,
            $"Expected to {verb} lines in order: {string.Join(", ", matchers.Select(m => m.Description))}; " +
            $"no match for {(next < matchers.Length ? matchers[next].Description : "")} after the earlier ones");
    }

    private string Dump()
    {
        var text = ToString();
        return text.Length == 0 ? "Transcript is empty." : $"Transcript:{Environment.NewLine}{text}";
    }

    internal static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r': builder.Append(@"\r"); break;
                case '\n': builder.Append(@"\n"); break;
                case '\t': builder.Append(@"\t"); break;
                case '\\': builder.Append(@"\\"); break;
                default:
                    if (char.IsControl(c)) builder.Append($"\\x{(int)c:X2}");
                    else builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    internal static List<string> SplitLines(string text, bool includePartial)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }
        if (includePartial && start < text.Length) lines.Add(text[start..]);
        return lines;
    }
}

/// <summary>Everything one connection sent and received, in order.</summary>
public sealed class ConnectionTranscript : TranscriptBase
{
    private readonly List<TranscriptEntry> _entries = [];
    private readonly bool _recordData;
    private readonly Action<string>? _log;

    internal ConnectionTranscript(int id, EndPoint? remoteEndPoint, Encoding encoding, bool recordData, Action<string>? log, ChangeSignal signal)
        : base(signal)
    {
        Id = id;
        RemoteEndPoint = remoteEndPoint;
        Encoding = encoding;
        _recordData = recordData;
        _log = log;
        StartedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Connection number, starting at 1 for each server.</summary>
    public int Id { get; }

    public EndPoint? RemoteEndPoint { get; }

    public DateTimeOffset StartedAt { get; }

    public Encoding Encoding { get; }

    /// <summary>True once the connection has been closed by either side.</summary>
    public bool IsClosed { get; private set; }

    public IReadOnlyList<TranscriptEntry> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    /// <summary>Descriptions of the recorded events, such as "connected" or "closed by peer".</summary>
    public IReadOnlyList<string> Events => [.. Entries.Where(e => e.Direction == TranscriptDirection.Event).Select(e => e.Description!)];

    public byte[] ReceivedBytes => Concat(TranscriptDirection.Received);

    public byte[] SentBytes => Concat(TranscriptDirection.Sent);

    public override string ReceivedText => Encoding.GetString(ReceivedBytes);

    public override string SentText => Encoding.GetString(SentBytes);

    /// <summary>Complete received lines; once the connection is closed, a trailing unterminated line is included too.</summary>
    public override IReadOnlyList<string> ReceivedLines => SplitLines(ReceivedText, IsClosed);

    public override IReadOnlyList<string> SentLines => SplitLines(SentText, IsClosed);

    public override IReadOnlyList<string> ReceivedMessages => Messages(TranscriptDirection.Received);

    public override IReadOnlyList<string> SentMessages => Messages(TranscriptDirection.Sent);

    public Task WaitForCloseAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => IsClosed, $"connection #{Id} to close", timeout, cancellationToken);

    internal void Record(TranscriptDirection direction, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        // Log first, so whatever a test sees in the transcript has already reached the log.
        if (_log is not null)
            _log($"#{Id} {(direction == TranscriptDirection.Received ? "<-" : "->")} {Escape(Encoding.GetString(data))}");
        if (_recordData) Add(new(direction, data.ToArray(), null, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Record a decoded protocol message, such as <c>OPEN /incoming/a.csv</c> or <c>ACK AA 42</c>. Protocol
    /// layers built on a <see cref="Connection"/> use this so tests can assert on what was said rather than
    /// on raw (possibly encrypted) bytes. Messages are kept even when <see cref="ServerOptions.RecordTranscript"/> is off.
    /// </summary>
    public void RecordMessage(TranscriptDirection direction, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (direction == TranscriptDirection.Event)
        {
            RecordEvent(message);
            return;
        }
        _log?.Invoke($"#{Id} {(direction == TranscriptDirection.Received ? "<=" : "=>")} {message}");
        Add(new(direction, [], message, DateTimeOffset.UtcNow));
    }

    internal void RecordEvent(string description, bool closed = false)
    {
        if (closed) IsClosed = true;
        _log?.Invoke($"#{Id} -- {description}");
        Add(new(TranscriptDirection.Event, [], description, DateTimeOffset.UtcNow));
    }

    private void Add(TranscriptEntry entry)
    {
        lock (_entries) _entries.Add(entry);
        Signal.Pulse();
    }

    private IReadOnlyList<string> Messages(TranscriptDirection direction)
    {
        lock (_entries) return [.. _entries.Where(e => e.Direction == direction && e.IsMessage).Select(e => e.Description!)];
    }

    private byte[] Concat(TranscriptDirection direction)
    {
        lock (_entries)
        {
            var result = new byte[_entries.Where(e => e.Direction == direction).Sum(e => e.Data.Length)];
            var offset = 0;
            foreach (var entry in _entries.Where(e => e.Direction == direction))
            {
                entry.Data.CopyTo(result, offset);
                offset += entry.Data.Length;
            }
            return result;
        }
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        var entries = Entries;
        var i = 0;
        while (i < entries.Count)
        {
            var entry = entries[i];
            if (entry.Direction == TranscriptDirection.Event)
            {
                builder.Append($"#{Id} -- {entry.Description}").AppendLine();
                i++;
                continue;
            }
            if (entry.IsMessage)
            {
                builder.Append($"#{Id} {entry}").AppendLine();
                i++;
                continue;
            }

            // Merge a run of same-direction chunks so lines split across reads print whole.
            var run = new List<byte>();
            var direction = entry.Direction;
            for (; i < entries.Count && entries[i].Direction == direction && !entries[i].IsMessage; i++) run.AddRange(entries[i].Data);
            var arrow = direction == TranscriptDirection.Received ? "<-" : "->";
            var text = Encoding.GetString(run.ToArray());
            foreach (var line in SplitLines(text, includePartial: false))
                builder.Append($"#{Id} {arrow} {Escape(line)}").AppendLine();
            var tail = text.EndsWith('\n') ? "" : text[(text.LastIndexOf('\n') + 1)..];
            if (tail.Length > 0) builder.Append($"#{Id} {arrow} {Escape(tail)} (no newline)").AppendLine();
        }
        return builder.ToString();
    }
}

/// <summary>All connections a stream server has accepted, in order.</summary>
public sealed class ServerTranscript : TranscriptBase
{
    private readonly List<ConnectionTranscript> _connections = [];

    internal ServerTranscript() : base(new ChangeSignal())
    {
    }

    public IReadOnlyList<ConnectionTranscript> Connections
    {
        get { lock (_connections) return [.. _connections]; }
    }

    /// <summary>The <paramref name="index"/>th connection, zero-based.</summary>
    public ConnectionTranscript this[int index] => Connections[index];

    public override IReadOnlyList<string> ReceivedLines => [.. Connections.SelectMany(c => c.ReceivedLines)];

    public override IReadOnlyList<string> SentLines => [.. Connections.SelectMany(c => c.SentLines)];

    public override IReadOnlyList<string> ReceivedMessages => [.. Connections.SelectMany(c => c.ReceivedMessages)];

    public override IReadOnlyList<string> SentMessages => [.. Connections.SelectMany(c => c.SentMessages)];

    public override string ReceivedText => string.Concat(Connections.Select(c => c.ReceivedText));

    public override string SentText => string.Concat(Connections.Select(c => c.SentText));

    public void AssertConnectionCount(int expected)
    {
        var actual = Connections.Count;
        Check(actual == expected, $"Expected {expected} connection(s) but saw {actual}");
    }

    public Task WaitForConnectionsAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => Connections.Count >= count, $"{count} connection(s)", timeout, cancellationToken);

    /// <summary>Wait until <paramref name="count"/> connections have been accepted and all of them have closed.</summary>
    public Task WaitForClosedConnectionsAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => Connections is var c && c.Count >= count && c.All(x => x.IsClosed),
            $"{count} closed connection(s)", timeout, cancellationToken);

    internal ConnectionTranscript Add(int id, EndPoint? remoteEndPoint, ServerOptions options)
    {
        var transcript = new ConnectionTranscript(id, remoteEndPoint, options.Encoding, options.RecordTranscript, options.Log, Signal);
        lock (_connections) _connections.Add(transcript);
        Signal.Pulse();
        return transcript;
    }

    internal void Pulse() => Signal.Pulse();

    public override string ToString() => string.Concat(Connections.Select(c => c.ToString()));
}
