using System.Globalization;
using System.Text;

namespace Intertube.Stub.Smtp;

public sealed partial class SmtpServer
{
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
            try
            {
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
            catch (InvalidDataException)
            {
                // A line past ServerOptions.MaxLineLength: the client is broken or hostile, so answer and hang up.
                connection.Transcript.RecordMessage(TranscriptDirection.Event, "line too long");
                await ReplyAsync("500 5.5.2 Line too long").ConfigureAwait(false);
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
                    if (argument.Length == 0) return await ReplyTrueAsync("501 5.5.4 Syntax: EHLO domain");
                    Reset();
                    _greeted = true;
                    List<string> extensions = [$"SIZE {_options.MaxMessageSize}", "PIPELINING", "8BITMIME", "SMTPUTF8", "ENHANCEDSTATUSCODES"];
                    if (_options.Tls is not null && !connection.IsTls) extensions.Add("STARTTLS");
                    if (AuthRequired) extensions.Add("AUTH PLAIN LOGIN");
                    await ReplyAsync([$"250-{_options.Domain} greets {argument}", .. extensions.Select((e, i) => (i == extensions.Count - 1 ? "250 " : "250-") + e)]).ConfigureAwait(false);
                    return true;
                }
                case "HELO":
                    if (argument.Length == 0) return await ReplyTrueAsync("501 5.5.4 Syntax: HELO domain");
                    Reset();
                    _greeted = true;
                    return await ReplyTrueAsync($"250 {_options.Domain}");

                case "STARTTLS":
                    if (_options.Tls is null || connection.IsTls) return await ReplyTrueAsync("502 5.5.1 STARTTLS not available");
                    if (argument.Length > 0) return await ReplyTrueAsync("501 5.5.4 Syntax: STARTTLS");
                    if (connection.HasBufferedInput(out _))
                    {
                        // Commands sent behind STARTTLS would otherwise be acted on as if they came over TLS (CVE-2011-0411).
                        connection.Transcript.RecordMessage(TranscriptDirection.Event, "commands pipelined after STARTTLS");
                        await ReplyAsync("554 5.5.0 Improper command pipelining after STARTTLS").ConfigureAwait(false);
                        return false;
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
                    if (!TryParsePath(argument, "FROM:", out var from, out var parameters)) return await ReplyTrueAsync("501 5.5.4 Syntax: MAIL FROM:<address>");
                    if (Parameter(parameters, "SIZE") is { } sizeText)
                    {
                        if (sizeText.Length == 0 || !sizeText.All(char.IsAsciiDigit)) return await ReplyTrueAsync("501 5.5.4 Syntax: SIZE=<number>");
                        // A size too large for a long is certainly over the limit.
                        if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > _options.MaxMessageSize)
                            return await ReplyTrueAsync("552 5.3.4 Message size exceeds fixed limit");
                    }
                    _from = from;
                    return await ReplyTrueAsync("250 2.1.0 OK");
                }

                case "RCPT":
                    if (_from is null) return await ReplyTrueAsync("503 5.5.1 Need MAIL first");
                    if (!TryParsePath(argument, "TO:", out var to, out _) || to.Length == 0) return await ReplyTrueAsync("501 5.5.4 Syntax: RCPT TO:<address>");
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

        /// <summary>
        /// Receive the message after 354. Only <c>CRLF . CRLF</c> ends it: a "." line ended by a bare LF, or
        /// following one, is data, so a sloppy client cannot smuggle a second message past a stricter relay.
        /// The data is kept with its line endings as sent.
        /// </summary>
        private async Task<bool> ReceiveDataAsync()
        {
            var data = new MemoryStream();
            var size = 0L;
            var atLineStart = true; // after DATA's own CRLF, or a line that ended in CRLF
            while (true)
            {
                var line = await connection.ReadUntilAsync("\n"u8.ToArray()).ConfigureAwait(false);
                if (line is null) return false; // the client went away mid-message: nothing is delivered
                var crlf = line.Length > 0 && line[^1] == '\r';
                if (atLineStart && crlf && line.Length == 2 && line[0] == '.') break;
                var start = atLineStart && line.Length > 0 && line[0] == '.' ? 1 : 0; // dot-unstuffing
                size += line.Length + 1 - start;
                if (size <= _options.MaxMessageSize)
                {
                    data.Write(line, start, line.Length - start);
                    data.WriteByte((byte)'\n');
                }
                atLineStart = crlf;
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
            // RFC 4954 §4: only after EHLO and only once. It also forbids AUTH during a mail transaction, but
            // with users configured MAIL needs a login first, so a transaction implies "already authenticated".
            var refusal = _user is not null ? "503 5.5.1 Already authenticated"
                : !_greeted ? "503 5.5.1 Say EHLO first"
                : null;
            if (refusal is not null)
            {
                await ReplyAsync(refusal).ConfigureAwait(false);
                return;
            }

            var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var mechanism = parts.FirstOrDefault()?.ToUpperInvariant();
            string? user = null, password = null;
            try
            {
                switch (mechanism)
                {
                    case "PLAIN":
                    {
                        var response = parts.Length > 1 ? parts[1] : await ChallengeAsync("").ConfigureAwait(false);
                        if (Decode(response).Split('\0') is [_, var u, var p])
                            (user, password) = (u, p);
                        break;
                    }
                    case "LOGIN":
                        user = Decode(parts.Length > 1 ? parts[1] : await ChallengeAsync("VXNlcm5hbWU6").ConfigureAwait(false));
                        password = Decode(await ChallengeAsync("UGFzc3dvcmQ6").ConfigureAwait(false));
                        break;
                    default:
                        await ReplyAsync("504 5.5.4 Unrecognised authentication type").ConfigureAwait(false);
                        return;
                }
            }
            catch (AuthAbortedException ex)
            {
                connection.Transcript.RecordMessage(TranscriptDirection.Received, $"AUTH {mechanism} {ex.Message}");
                if (ex.Reply is { } reply) await ReplyAsync(reply).ConfigureAwait(false);
                return;
            }

            var accepted = user is not null && password is not null && _options.Users!.TryGetValue(user, out var expected) && expected == password;
            connection.Transcript.RecordMessage(TranscriptDirection.Received, $"AUTH {mechanism} user={user} {(accepted ? "accepted" : "rejected")}");
            if (accepted) _user = user;
            await ReplyAsync(accepted ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication credentials invalid").ConfigureAwait(false);
        }

        /// <summary>Send a 334 challenge and read the response; <c>*</c> cancels the exchange (RFC 4954 §4).</summary>
        private async Task<string> ChallengeAsync(string challenge)
        {
            await ReplyAsync("334 " + challenge).ConfigureAwait(false);
            var response = await connection.ReadLineAsync().ConfigureAwait(false) ?? throw new AuthAbortedException("aborted: connection closed", null);
            return response.Trim() == "*" ? throw new AuthAbortedException("cancelled", "501 5.7.0 Authentication cancelled") : response;
        }

        /// <summary>A SASL response: base64, or <c>=</c> for an empty one.</summary>
        private static string Decode(string base64)
        {
            base64 = base64.Trim();
            if (base64 == "=") return "";
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                throw new AuthAbortedException("malformed response", "501 5.5.2 Cannot decode response");
            }
        }

        private sealed class AuthAbortedException(string message, string? reply) : Exception(message)
        {
            public string? Reply => reply;
        }

        /// <summary>
        /// Split <c>FROM:&lt;a@b&gt; SIZE=10</c> into the address and the parameters after it. The null
        /// sender <c>&lt;&gt;</c> is ""; a missing address or an unclosed bracket is malformed.
        /// </summary>
        private static bool TryParsePath(string argument, string prefix, out string address, out string parameters)
        {
            address = parameters = "";
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var rest = argument[prefix.Length..].TrimStart();
            if (rest.StartsWith('<'))
            {
                var end = rest.IndexOf('>');
                if (end < 0) return false;
                address = rest[1..end];
                parameters = rest[(end + 1)..];
                return true;
            }
            var parts = rest.Split(' ', 2); // lenient: some clients leave out the brackets
            if (parts[0].Length == 0) return false;
            address = parts[0];
            parameters = parts.Length > 1 ? parts[1] : "";
            return true;
        }

        /// <summary>The value of an ESMTP parameter such as <c>SIZE=1000</c> ("" if it has none), or null if absent.</summary>
        private static string? Parameter(string parameters, string name) =>
            parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .FirstOrDefault(p => p[0].Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match
                ? match.Length > 1 ? match[1] : ""
                : null;

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
