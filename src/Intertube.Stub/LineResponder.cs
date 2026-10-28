using System.Text.RegularExpressions;

namespace Intertube.Stub;

/// <summary>
/// A rule table for line protocols: each incoming line is answered by the first matching rule,
/// in any order, until the client disconnects or a rule closes the connection.
/// <code>
/// new LineResponder()
///     .Greeting("+OK ready")
///     .On("PING", "+PONG")
///     .On(new Regex("^GET (.+)$"), m => $"$value-of-{m.Groups[1].Value}")
///     .OnAndClose("QUIT", "+OK bye")
///     .Otherwise(line => $"-ERR unknown '{line}'");
/// </code>
/// </summary>
public sealed class LineResponder : IConnectionHandler
{
    private readonly List<Rule> _rules = [];
    private readonly List<string> _greeting = [];
    private Func<string, string?>? _fallback;

    private sealed record Rule(LineMatcher Matcher, Func<string, ValueTask<string?>> Reply, bool Close);

    /// <summary>Lines sent as soon as a client connects.</summary>
    public LineResponder Greeting(params string[] lines)
    {
        _greeting.AddRange(lines);
        return this;
    }

    /// <summary>Reply with a fixed line (null for silence).</summary>
    public LineResponder On(LineMatcher matcher, string? reply) => Add(matcher, _ => new(reply), close: false);

    /// <summary>Reply with a line computed from the request.</summary>
    public LineResponder On(LineMatcher matcher, Func<string, string?> reply) => Add(matcher, line => new(reply(line)), close: false);

    /// <summary>Reply asynchronously, for example after consulting test state.</summary>
    public LineResponder On(LineMatcher matcher, Func<string, ValueTask<string?>> reply) => Add(matcher, reply, close: false);

    /// <summary>Reply using regex capture groups.</summary>
    public LineResponder On(Regex regex, Func<Match, string?> reply) =>
        Add(LineMatcher.FromRegex(regex), line => new(reply(regex.Match(line))), close: false);

    /// <summary>Reply (or not), then close the connection.</summary>
    public LineResponder OnAndClose(LineMatcher matcher, string? reply = null) => Add(matcher, _ => new(reply), close: true);

    /// <summary>Reply to lines no rule matched. Without a fallback, unmatched lines are ignored.</summary>
    public LineResponder Otherwise(string? reply) => Otherwise(_ => reply);

    public LineResponder Otherwise(Func<string, string?> reply)
    {
        _fallback = reply;
        return this;
    }

    public async Task HandleAsync(Connection connection)
    {
        if (_greeting.Count > 0) await connection.WriteLinesAsync(_greeting).ConfigureAwait(false);

        await foreach (var line in connection.ReadLinesAsync().ConfigureAwait(false))
        {
            var rule = _rules.Find(r => r.Matcher.IsMatch(line));
            var reply = rule is not null ? await rule.Reply(line).ConfigureAwait(false) : _fallback?.Invoke(line);
            if (reply is not null) await connection.WriteLineAsync(reply).ConfigureAwait(false);
            if (rule?.Close == true)
            {
                await connection.CloseAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private LineResponder Add(LineMatcher matcher, Func<string, ValueTask<string?>> reply, bool close)
    {
        _rules.Add(new(matcher, reply, close));
        return this;
    }
}
