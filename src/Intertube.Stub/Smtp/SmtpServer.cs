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
public sealed class SmtpServer : IAsyncDisposable
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
    /// closes the connection, as a real server does.
    /// </summary>
    public Func<SmtpCommand, ValueTask<string?>>? Intercept { get; set; }

    public IReadOnlyList<Exception> Errors => Tcp.Errors;

    /// <summary>
    /// Answer the next <paramref name="times"/> commands matching the glob <paramref name="command"/>
    /// (matched against the whole line, e.g. <c>"RCPT TO:&lt;*@blocked.test&gt;*"</c>, or <c>"."</c> for
    /// the end of a message) with <paramref name="reply"/>.
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

    public ValueTask DisposeAsync() => Tcp.DisposeAsync();

    public override string ToString() => $"smtp://{Host}:{Port}";

    private async ValueTask<string?> OverrideAsync(SmtpCommand command)
    {
        foreach (var rule in _rules)
            if (rule.TryConsume(command.Line))
                return rule.Reply;
        return Intercept is { } intercept ? await intercept(command).ConfigureAwait(false) : null;
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

        public bool TryConsume(string line)
        {
            if (!command.IsMatch(line)) return false;
            while (true)
            {
                var remaining = Volatile.Read(ref _remaining);
                if (remaining == 0) return false;
                if (remaining == int.MaxValue || Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) == remaining) return true;
            }
        }
    }

    /// <summary>The state machine for one connection (RFC 5321).</summary>
    private sealed class Session(SmtpServer server, Connection connection)
    {
        private readonly SmtpServerOptions _options = server.Options;
        private bool _greeted;
        private string? _user;
        private string? _from;
        private readonly List<string> _recipients = [];

        private bool AuthRequired => _options.Users is { Count: > 0 };

        public async Task RunAsync()
        {
            await ReplyAsync($"220 {_options.Domain} ESMTP Intertube.Stub").ConfigureAwait(false);
            while (await connection.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                if (await server.OverrideAsync(new SmtpCommand(line, verb, connection)).ConfigureAwait(false) is { } injected)
                {
                    if (connection.IsTerminated) return;
                    await ReplyAsync(injected).ConfigureAwait(false);
                    if (injected.StartsWith("421", StringComparison.Ordinal)) return;
                    continue;
                }
                if (connection.IsTerminated) return;
                if (!await HandleAsync(line, verb).ConfigureAwait(false)) return;
            }
        }

        /// <summary>Handle one command; false ends the session.</summary>
        private async Task<bool> HandleAsync(string line, string verb)
        {
            var argument = line.Length > verb.Length ? line[(verb.Length + 1)..].Trim() : "";
            switch (verb)
            {
                case "EHLO":
                {
                    Reset();
                    _greeted = true;
                    List<string> extensions = [$"SIZE {_options.MaxMessageSize}", "PIPELINING", "8BITMIME", "SMTPUTF8", "ENHANCEDSTATUSCODES"];
                    if (_options.Tls is not null && !connection.IsTls) extensions.Add("STARTTLS");
                    if (AuthRequired) extensions.Add("AUTH PLAIN LOGIN");
                    await ReplyAsync([$"250-{_options.Domain} greets {argument}", .. extensions.Select((e, i) => (i == extensions.Count - 1 ? "250 " : "250-") + e)]).ConfigureAwait(false);
                    return true;
                }
                case "HELO":
                    Reset();
                    _greeted = true;
                    await ReplyAsync($"250 {_options.Domain}").ConfigureAwait(false);
                    return true;

                case "STARTTLS":
                    if (_options.Tls is null || connection.IsTls)
                    {
                        await ReplyAsync("502 5.5.1 STARTTLS not available").ConfigureAwait(false);
                        return true;
                    }
                    await ReplyAsync("220 2.0.0 Ready to start TLS").ConfigureAwait(false);
                    await connection.UpgradeToTlsAsync(_options.Tls).ConfigureAwait(false);
                    Reset();
                    _greeted = false;
                    _user = null;
                    return true;

                case "AUTH":
                    await AuthenticateAsync(argument).ConfigureAwait(false);
                    return true;

                case "MAIL":
                {
                    if (!_greeted) return await ReplyTrueAsync("503 5.5.1 Say EHLO first");
                    if (_options.RequireTls && !connection.IsTls) return await ReplyTrueAsync("530 5.7.0 Must issue a STARTTLS command first");
                    if (AuthRequired && _user is null) return await ReplyTrueAsync("530 5.7.0 Authentication required");
                    if (_from is not null) return await ReplyTrueAsync("503 5.5.1 Nested MAIL command");
                    if (Address(argument, "FROM:") is not { } from) return await ReplyTrueAsync("501 5.5.4 Syntax: MAIL FROM:<address>");
                    var size = System.Text.RegularExpressions.Regex.Match(argument, @"\bSIZE=(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (size.Success && long.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > _options.MaxMessageSize)
                        return await ReplyTrueAsync("552 5.3.4 Message size exceeds fixed limit");
                    _from = from;
                    return await ReplyTrueAsync("250 2.1.0 OK");
                }

                case "RCPT":
                    if (_from is null) return await ReplyTrueAsync("503 5.5.1 Need MAIL first");
                    if (Address(argument, "TO:") is not { Length: > 0 } to) return await ReplyTrueAsync("501 5.5.4 Syntax: RCPT TO:<address>");
                    _recipients.Add(to);
                    return await ReplyTrueAsync("250 2.1.5 OK");

                case "DATA":
                    if (_recipients.Count == 0) return await ReplyTrueAsync("503 5.5.1 Need RCPT first");
                    await ReplyAsync("354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);
                    return await ReceiveDataAsync().ConfigureAwait(false);

                case "RSET":
                    Reset();
                    return await ReplyTrueAsync("250 2.0.0 OK");

                case "NOOP":
                    return await ReplyTrueAsync("250 2.0.0 OK");

                case "VRFY":
                    return await ReplyTrueAsync("252 2.1.5 Cannot verify, but will accept");

                case "HELP":
                    return await ReplyTrueAsync("214 2.0.0 See RFC 5321");

                case "QUIT":
                    await ReplyAsync("221 2.0.0 Bye").ConfigureAwait(false);
                    return false;

                default:
                    return await ReplyTrueAsync("500 5.5.1 Command not recognised");
            }
        }

        private async Task<bool> ReceiveDataAsync()
        {
            var data = new MemoryStream();
            var size = 0L;
            while (true)
            {
                var line = await connection.ReadUntilAsync("\n"u8.ToArray()).ConfigureAwait(false);
                if (line is null) return false; // the client went away mid-message: nothing is delivered
                var length = line.Length > 0 && line[^1] == '\r' ? line.Length - 1 : line.Length;
                if (length == 1 && line[0] == '.') break;
                var start = length > 0 && line[0] == '.' ? 1 : 0; // dot-unstuffing
                size += length - start + 2;
                if (size <= _options.MaxMessageSize)
                {
                    data.Write(line, start, length - start);
                    data.Write("\r\n"u8);
                }
            }

            var message = new SmtpMessage(_from!, [.. _recipients], data.ToArray(), _user, connection.IsTls, connection.Id);
            Reset();
            if (size > _options.MaxMessageSize) return await ReplyTrueAsync("552 5.3.4 Message size exceeds fixed limit");
            if (await server.OverrideAsync(new SmtpCommand(".", ".", connection) { Message = message }).ConfigureAwait(false) is { } injected)
            {
                if (connection.IsTerminated) return false;
                await ReplyAsync(injected).ConfigureAwait(false);
                return !injected.StartsWith("421", StringComparison.Ordinal);
            }
            if (connection.IsTerminated) return false;
            server.Add(message);
            connection.Transcript.RecordMessage(TranscriptDirection.Received, $"MESSAGE {message}");
            return await ReplyTrueAsync($"250 2.0.0 OK: queued as {server._messages.Count}");
        }

        private async Task AuthenticateAsync(string argument)
        {
            if (!AuthRequired)
            {
                await ReplyAsync("503 5.5.1 Authentication not enabled").ConfigureAwait(false);
                return;
            }
            if (_user is not null)
            {
                await ReplyAsync("503 5.5.1 Already authenticated").ConfigureAwait(false);
                return;
            }
            var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string? user = null, password = null;
            switch (parts.FirstOrDefault()?.ToUpperInvariant())
            {
                case "PLAIN":
                {
                    var response = parts.Length > 1 ? parts[1] : await ChallengeAsync("").ConfigureAwait(false);
                    if (Decode(response) is { } decoded && decoded.Split('\0') is [_, var u, var p])
                        (user, password) = (u, p);
                    break;
                }
                case "LOGIN":
                {
                    user = parts.Length > 1 ? Decode(parts[1]) : Decode(await ChallengeAsync("VXNlcm5hbWU6").ConfigureAwait(false));
                    if (user is not null) password = Decode(await ChallengeAsync("UGFzc3dvcmQ6").ConfigureAwait(false));
                    break;
                }
                default:
                    await ReplyAsync("504 5.5.4 Unrecognised authentication type").ConfigureAwait(false);
                    return;
            }

            var accepted = user is not null && password is not null && _options.Users!.TryGetValue(user, out var expected) && expected == password;
            connection.Transcript.RecordMessage(TranscriptDirection.Received, $"AUTH {parts[0].ToUpperInvariant()} user={user} {(accepted ? "accepted" : "rejected")}");
            if (accepted) _user = user;
            await ReplyAsync(accepted ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication credentials invalid").ConfigureAwait(false);
        }

        private async Task<string> ChallengeAsync(string challenge)
        {
            await ReplyAsync("334 " + challenge).ConfigureAwait(false);
            return await connection.ReadLineAsync().ConfigureAwait(false) ?? "";
        }

        private static string? Decode(string base64)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>The address in <c>FROM:&lt;a@b&gt; params</c>; null when malformed. The null sender <c>&lt;&gt;</c> is "".</summary>
        private static string? Address(string argument, string prefix)
        {
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var rest = argument[prefix.Length..].TrimStart();
            if (!rest.StartsWith('<')) return rest.Split(' ')[0];
            var end = rest.IndexOf('>');
            return end < 0 ? null : rest[1..end];
        }

        private void Reset()
        {
            _from = null;
            _recipients.Clear();
        }

        private async Task<bool> ReplyTrueAsync(string line)
        {
            await ReplyAsync(line).ConfigureAwait(false);
            return true;
        }

        private ValueTask ReplyAsync(string line) => connection.WriteAsync(line + "\r\n");

        private ValueTask ReplyAsync(IEnumerable<string> lines) => connection.WriteAsync(string.Concat(lines.Select(l => l + "\r\n")));
    }
}
