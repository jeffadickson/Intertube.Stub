using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Intertube.Stub.Internal;

namespace Intertube.Stub.Smtp;

/// <summary>Settings for <see cref="SmtpServer.Start(SmtpServerOptions?)"/>.</summary>
public sealed record SmtpServerOptions
{
    /// <summary>The name in the greeting and EHLO reply.</summary>
    public string Domain { get; init; } = "smtp.intertube.test";

    /// <summary>Certificate for STARTTLS, which is advertised when this is set. See <see cref="TlsOptions.SelfSigned"/>.</summary>
    public TlsOptions? Tls { get; init; }

    /// <summary>Use TLS from the first byte (port 465 style) instead of STARTTLS. Requires <see cref="Tls"/>.</summary>
    public bool ImplicitTls { get; init; }

    /// <summary>Refuse MAIL until the client has switched to TLS.</summary>
    public bool RequireTls { get; init; }

    /// <summary>Accounts for AUTH PLAIN and AUTH LOGIN, user name to password. When set, MAIL requires authentication.</summary>
    public IReadOnlyDictionary<string, string>? Users { get; init; }

    /// <summary>Largest message accepted, advertised as SIZE.</summary>
    public long MaxMessageSize { get; init; } = 10 * 1024 * 1024;

    /// <summary>TCP-level options: faults, concurrency, logging and transcripts (which show plaintext, even after STARTTLS).</summary>
    public ServerOptions Server { get; init; } = ServerOptions.Default;

    public IPAddress Address { get; init; } = IPAddress.Loopback;

    public int Port { get; init; }
}

/// <summary>
/// One SMTP command as an interceptor sees it. <see cref="Line"/> is the full command (for the end of a
/// message, <c>.</c>, with <see cref="Message"/> set); <see cref="Verb"/> is its upper-cased first word.
/// </summary>
public sealed record SmtpCommand(string Line, string Verb, Connection Connection)
{
    /// <summary>The message just received, for the end-of-data command <c>.</c>.</summary>
    public SmtpMessage? Message { get; init; }
}

/// <summary>
/// An embedded SMTP server that captures mail for assertions: EHLO, STARTTLS, AUTH PLAIN/LOGIN,
/// SIZE, PIPELINING, 8BITMIME and SMTPUTF8, on an ephemeral loopback port.
/// <code>
/// await using var smtp = SmtpServer.Start();
/// smtp.FailNext("RCPT TO:&lt;*&gt;*", "451 4.7.1 Greylisted, try again later");
/// // ... code under test sends mail to smtp.Host:smtp.Port ...
/// var mail = await smtp.WaitForMessageAsync(m => m.Recipients.Contains("ada@example.test"));
/// Assert.Equal("Welcome!", mail.Subject);
/// </code>
/// Unlike a scripted <see cref="Conversation"/>, it accepts any valid dialogue, so the test is about
/// the mail, not the client's exact command order.
/// </summary>
public sealed partial class SmtpServer : IAsyncDisposable
{
    private readonly ConcurrentQueue<SmtpMessage> _messages = new();
    private readonly ConcurrentQueue<ReplyRule> _rules = new();
    private readonly ChangeSignal _signal = new();

    private SmtpServer(SmtpServerOptions options)
    {
        if (options.ImplicitTls && options.Tls is null) throw new ArgumentException("ImplicitTls requires Tls.", nameof(options));
        Options = options;
        var server = options.ImplicitTls ? options.Server with { Tls = options.Tls } : options.Server;
        Tcp = TcpServer.Listen(new IPEndPoint(options.Address, options.Port), connection => new Session(this, connection).RunAsync(), server);
    }

    public static SmtpServer Start(SmtpServerOptions? options = null) => new(options ?? new SmtpServerOptions());

    public SmtpServerOptions Options { get; }

    /// <summary>The underlying TCP server: its transcript holds the whole plaintext dialogue.</summary>
    public TcpServer Tcp { get; }

    public int Port => Tcp.Port;

    public string Host => Tcp.Host;

    /// <summary>Every accepted message, in order.</summary>
    public IReadOnlyList<SmtpMessage> Messages => [.. _messages];

    /// <summary>
    /// Called for each command before the server handles it. Return a reply line (such as
    /// <c>"421 4.3.2 Shutting down"</c>) to send it instead, or null to carry on. A 421 reply also
    /// closes the connection, as a real server does. An injected reply replaces the command entirely: the
    /// session's state does not change, so an injected 250 to MAIL does not start a transaction.
    /// </summary>
    public Func<SmtpCommand, ValueTask<string?>>? Intercept { get; set; }

    public IReadOnlyList<Exception> Errors => Tcp.Errors;

    /// <summary>
    /// Answer the next <paramref name="times"/> commands matching the glob <paramref name="command"/>
    /// (matched against the whole line, e.g. <c>"RCPT TO:&lt;*@blocked.test&gt;*"</c>, or <c>"."</c> for
    /// the end of a message) with <paramref name="reply"/>. A rule matches the line as sent or its canonical
    /// form, with the verb upper-cased and no space after <c>FROM:</c> or <c>TO:</c>, so the pattern
    /// <c>"RCPT TO:&lt;a@b&gt;"</c> also matches <c>"rcpt to: &lt;a@b&gt;"</c>. As with <see cref="Intercept"/>,
    /// the session's state is left unchanged.
    /// </summary>
    public void FailNext(LineMatcher command, string reply, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(times);
        _rules.Enqueue(new ReplyRule(command, reply, times));
    }

    /// <summary>Answer every matching command with <paramref name="reply"/>, e.g. to reject a domain.</summary>
    public void FailAlways(LineMatcher command, string reply) => _rules.Enqueue(new ReplyRule(command, reply, int.MaxValue));

    /// <summary>Wait for a message matching <paramref name="predicate"/> (any message if null) and return it.</summary>
    public async Task<SmtpMessage> WaitForMessageAsync(Func<SmtpMessage, bool>? predicate = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        SmtpMessage? found = null;
        await _signal.WaitUntilAsync(() => (found = _messages.FirstOrDefault(predicate ?? (_ => true))) is not null, timeout,
            () => $"a matching message. Received {_messages.Count}:\n" + string.Join("\n", _messages), cancellationToken).ConfigureAwait(false);
        return found!;
    }

    /// <summary>Wait until at least <paramref name="count"/> messages have arrived.</summary>
    public Task WaitForMessagesAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _signal.WaitUntilAsync(() => _messages.Count >= count, timeout, () => $"{count} messages (have {_messages.Count})", cancellationToken);

    public void ThrowIfFaulted() => Tcp.ThrowIfFaulted();

    /// <summary>Stop accepting and close every session; see <see cref="StreamServer.StopAsync"/>.</summary>
    public Task StopAsync() => Tcp.StopAsync();

    public ValueTask DisposeAsync() => Tcp.DisposeAsync();

    public override string ToString() => $"smtp://{Host}:{Port}";

    private async ValueTask<string?> OverrideAsync(SmtpCommand command)
    {
        foreach (var rule in _rules)
            if (rule.TryConsume(command.Line, Canonical(command)))
                return rule.Reply;
        return Intercept is { } intercept ? await intercept(command).ConfigureAwait(false) : null;
    }

    /// <summary>The command as a rule is most likely written, such as <c>MAIL FROM:&lt;a@b&gt;</c>.</summary>
    private static string Canonical(SmtpCommand command)
    {
        var rest = command.Line.Length > command.Verb.Length ? command.Line[command.Verb.Length..] : "";
        if (command.Verb is "MAIL" or "RCPT" && rest.IndexOf(':') is >= 0 and var colon)
            rest = " " + rest[..colon].Trim().ToUpperInvariant() + ":" + rest[(colon + 1)..].TrimStart();
        return command.Verb + rest;
    }

    private void Add(SmtpMessage message)
    {
        _messages.Enqueue(message);
        _signal.Pulse();
    }

    private sealed class ReplyRule(LineMatcher command, string reply, int times)
    {
        private int _remaining = times;

        public string Reply => reply;

        public bool TryConsume(string line, string canonical)
        {
            if (!command.IsMatch(line) && !command.IsMatch(canonical)) return false;
            while (true)
            {
                var remaining = Volatile.Read(ref _remaining);
                if (remaining == 0) return false;
                if (remaining == int.MaxValue || Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) == remaining) return true;
            }
        }
    }

}
