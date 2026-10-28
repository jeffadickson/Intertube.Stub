using System.Globalization;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

public sealed record ImapSettings(string Host, int Port, string User, string Password)
{
    public System.Net.Security.RemoteCertificateValidationCallback? CertificateValidation { get; init; }
}

/// <summary>
/// Polls an IMAPS inbox (MailKit) for unseen messages, hands each to a handler and only then flags
/// it \Seen. A message is never marked before it was handled, so a dropped connection or a failing
/// handler means it is delivered again on the next poll: at-least-once, never lost.
/// </summary>
public sealed class MailboxPoller(ImapSettings settings)
{
    public async Task<int> PollAsync(Func<MimeKit.MimeMessage, Task> handle, CancellationToken cancellationToken = default)
    {
        using var client = new ImapClient { ServerCertificateValidationCallback = settings.CertificateValidation, Timeout = 10_000 };
        await client.ConnectAsync(settings.Host, settings.Port, MailKit.Security.SecureSocketOptions.SslOnConnect, cancellationToken);
        await client.AuthenticateAsync(settings.User, settings.Password, cancellationToken);
        await client.Inbox.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var handled = 0;
        foreach (var uid in await client.Inbox.SearchAsync(SearchQuery.NotSeen, cancellationToken))
        {
            var message = await client.Inbox.GetMessageAsync(uid, cancellationToken);
            await handle(message);
            await client.Inbox.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, cancellationToken);
            handled++;
        }
        await client.DisconnectAsync(quit: true, cancellationToken);
        return handled;
    }
}

// ---- A fake IMAP server -------------------------------------------------------------------------

/// <summary>A message in <see cref="FakeImapServer"/>'s inbox.</summary>
public sealed class ImapMessage(uint uid, byte[] raw)
{
    public uint Uid { get; } = uid;

    public byte[] Raw { get; } = raw;

    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Seen => Flags.Contains(@"\Seen");
}

/// <summary>
/// One IMAP4rev1 inbox over implicit TLS: enough of LOGIN, LIST, SELECT, UID SEARCH, UID FETCH and
/// UID STORE for a real client. Commands and responses are recorded as transcript messages
/// ("UID FETCH 2 (BODY.PEEK[])", "FETCH 2 1834 bytes"), and <see cref="FailNext"/> answers a
/// command with a canned NO, BAD or BYE.
/// </summary>
public sealed class FakeImapServer : IAsyncDisposable
{
    private readonly List<ImapMessage> _inbox = [];
    private readonly List<(LineMatcher Command, string Reply)> _rules = [];
    private readonly HashSet<uint> _dropDuringFetch = [];
    private uint _nextUid = 1;

    public FakeImapServer(TlsOptions tls, string user, string password)
    {
        Tls = tls;
        User = user;
        Password = password;
        Tcp = TcpServer.Listen(0, connection => new Session(this, connection).RunAsync());
    }

    public TlsOptions Tls { get; }

    public string User { get; }

    public string Password { get; }

    public TcpServer Tcp { get; }

    public IReadOnlyList<ImapMessage> Inbox
    {
        get { lock (_inbox) return [.. _inbox]; }
    }

    public ImapMessage Deliver(string from, string subject, string body, bool seen = false)
    {
        var raw = Encoding.UTF8.GetBytes(
            $"From: {from}\r\nTo: inbox@shop.test\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@test>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}\r\n");
        lock (_inbox)
        {
            var message = new ImapMessage(_nextUid++, raw);
            if (seen) message.Flags.Add(@"\Seen");
            _inbox.Add(message);
            return message;
        }
    }

    /// <summary>Answer the next command matching <paramref name="command"/> (a glob, without the tag) with <paramref name="reply"/>, e.g. "NO [INUSE] Mailbox locked" or "BYE Shutting down".</summary>
    public void FailNext(LineMatcher command, string reply)
    {
        lock (_rules) _rules.Add((command, reply));
    }

    /// <summary>Cut the connection half way through sending the body of message <paramref name="uid"/>, once.</summary>
    public void DropDuringFetchOf(uint uid)
    {
        lock (_dropDuringFetch) _dropDuringFetch.Add(uid);
    }

    public ValueTask DisposeAsync() => Tcp.DisposeAsync();

    private sealed class Session(FakeImapServer server, Connection connection)
    {
        private bool _authenticated;
        private bool _selected;

        public async Task RunAsync()
        {
            await connection.UpgradeToTlsAsync(server.Tls);
            await WriteAsync("* OK [CAPABILITY IMAP4rev1] Intertube.Stub IMAP ready");
            while (await connection.ReadLineAsync() is { } line)
            {
                var space = line.IndexOf(' ');
                if (space <= 0)
                {
                    await WriteAsync("* BAD Missing tag");
                    continue;
                }
                var tag = line[..space];
                var command = line[(space + 1)..];
                connection.Transcript.RecordMessage(TranscriptDirection.Received, command.StartsWith("LOGIN ", StringComparison.OrdinalIgnoreCase) ? "LOGIN ***" : command);
                if (Override(command) is { } injected)
                {
                    if (injected.StartsWith("BYE", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync("* " + injected);
                        return;
                    }
                    await WriteAsync($"{tag} {injected}");
                    continue;
                }
                if (!await HandleAsync(tag, Tokenize(command))) return;
            }
        }

        private string? Override(string command)
        {
            lock (server._rules)
            {
                var index = server._rules.FindIndex(r => r.Command.IsMatch(command));
                if (index < 0) return null;
                var reply = server._rules[index].Reply;
                server._rules.RemoveAt(index);
                return reply;
            }
        }

        private async Task<bool> HandleAsync(string tag, List<string> words)
        {
            var verb = words[0].ToUpperInvariant();
            if (verb == "UID" && words.Count > 1)
            {
                verb = "UID " + words[1].ToUpperInvariant();
                words.RemoveAt(0);
            }

            switch (verb)
            {
                case "CAPABILITY":
                    await WriteAsync("* CAPABILITY IMAP4rev1");
                    return await Ok(tag, "CAPABILITY completed");
                case "NOOP":
                    return await Ok(tag, "NOOP completed");
                case "LOGOUT":
                    await WriteAsync("* BYE Logging out");
                    await Ok(tag, "LOGOUT completed");
                    return false;
                case "LOGIN":
                    _authenticated = words.Count == 3 && words[1] == server.User && words[2] == server.Password;
                    connection.Transcript.RecordMessage(TranscriptDirection.Event, $"LOGIN {words.ElementAtOrDefault(1)} {(_authenticated ? "accepted" : "rejected")}");
                    return _authenticated
                        ? await Ok(tag, "[CAPABILITY IMAP4rev1] Logged in")
                        : await Reply(tag, "NO [AUTHENTICATIONFAILED] Invalid credentials");
            }

            if (!_authenticated) return await Reply(tag, "NO Log in first");
            switch (verb)
            {
                case "LIST":
                    if (words.Count > 2 && words[2].Length == 0) await WriteAsync("* LIST (\\Noselect) \"/\" \"\"");
                    else await WriteAsync("* LIST (\\HasNoChildren) \"/\" INBOX");
                    return await Ok(tag, "LIST completed");
                case "SELECT" or "EXAMINE":
                {
                    if (!words.ElementAtOrDefault(1)?.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ?? true)
                        return await Reply(tag, "NO [NONEXISTENT] No such mailbox");
                    var messages = server.Inbox;
                    _selected = true;
                    await WriteAsync(@"* FLAGS (\Answered \Flagged \Deleted \Seen \Draft)");
                    await WriteAsync(@"* OK [PERMANENTFLAGS (\Answered \Flagged \Deleted \Seen \Draft)] Limited");
                    await WriteAsync($"* {messages.Count} EXISTS");
                    await WriteAsync("* 0 RECENT");
                    await WriteAsync("* OK [UIDVALIDITY 1] UIDs valid");
                    await WriteAsync($"* OK [UIDNEXT {server._nextUid}] Predicted next UID");
                    return await Ok(tag, verb == "SELECT" ? "[READ-WRITE] SELECT completed" : "[READ-ONLY] EXAMINE completed");
                }
            }

            if (!_selected) return await Reply(tag, "NO Select a mailbox first");
            switch (verb)
            {
                case "UID SEARCH":
                {
                    var criteria = string.Join(' ', words.Skip(1)).ToUpperInvariant();
                    var uids = server.Inbox.Where(m => criteria switch
                    {
                        "UNSEEN" => !m.Seen,
                        "SEEN" => m.Seen,
                        _ => true,
                    }).Select(m => m.Uid.ToString(CultureInfo.InvariantCulture));
                    await WriteAsync("* SEARCH" + string.Concat(uids.Select(u => " " + u)));
                    return await Ok(tag, "SEARCH completed");
                }
                case "UID FETCH":
                    foreach (var (message, sequence) in Matching(words[1]))
                        if (!await FetchAsync(message, sequence, words[2].ToUpperInvariant())) return false;
                    return await Ok(tag, "FETCH completed");
                case "UID STORE":
                {
                    var operation = words[2].ToUpperInvariant();
                    var flags = words[3].Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var (message, sequence) in Matching(words[1]))
                    {
                        lock (server._inbox)
                        {
                            if (operation.StartsWith('+')) message.Flags.UnionWith(flags);
                            else if (operation.StartsWith('-')) message.Flags.ExceptWith(flags);
                            else
                            {
                                message.Flags.Clear();
                                message.Flags.UnionWith(flags);
                            }
                        }
                        if (!operation.EndsWith(".SILENT")) await WriteAsync($"* {sequence} FETCH (UID {message.Uid} FLAGS ({string.Join(' ', message.Flags)}))");
                    }
                    return await Ok(tag, "STORE completed");
                }
                case "CLOSE" or "UNSELECT":
                    _selected = false;
                    return await Ok(tag, $"{verb} completed");
                default:
                    return await Reply(tag, "BAD Command not supported");
            }
        }

        private async Task<bool> FetchAsync(ImapMessage message, int sequence, string items)
        {
            var parts = new List<string> { $"UID {message.Uid}" };
            if (items.Contains("FLAGS")) parts.Add($"FLAGS ({string.Join(' ', message.Flags)})");
            if (items.Contains("RFC822.SIZE")) parts.Add($"RFC822.SIZE {message.Raw.Length}");
            if (!items.Contains("BODY[]") && !items.Contains("BODY.PEEK[]"))
            {
                await WriteAsync($"* {sequence} FETCH ({string.Join(' ', parts)})");
                return true;
            }

            if (items.Contains("BODY[]")) lock (server._inbox) message.Flags.Add(@"\Seen"); // a non-PEEK fetch marks it read
            await connection.WriteAsync($"* {sequence} FETCH ({string.Join(' ', parts)} BODY[] {{{message.Raw.Length}}}\r\n");
            bool drop;
            lock (server._dropDuringFetch) drop = server._dropDuringFetch.Remove(message.Uid);
            if (drop)
            {
                await connection.WriteAsync(message.Raw.AsMemory(0, message.Raw.Length / 2));
                connection.Transcript.RecordMessage(TranscriptDirection.Event, $"dropped connection during FETCH of {message.Uid}");
                connection.Abort();
                return false;
            }
            await connection.WriteAsync(message.Raw);
            await connection.WriteAsync(")\r\n");
            connection.Transcript.RecordMessage(TranscriptDirection.Sent, $"FETCH {message.Uid} {message.Raw.Length} bytes");
            return true;
        }

        /// <summary>The messages a UID set such as <c>2</c>, <c>2:4,7</c> or <c>1:*</c> names, with their sequence numbers.</summary>
        private IEnumerable<(ImapMessage Message, int Sequence)> Matching(string set)
        {
            var messages = server.Inbox;
            var ranges = set.Split(',').Select(range =>
            {
                var ends = range.Split(':');
                uint Parse(string s) => s == "*" ? uint.MaxValue : uint.Parse(s, CultureInfo.InvariantCulture);
                return (Low: Parse(ends[0]), High: Parse(ends[^1]));
            }).ToList();
            for (var i = 0; i < messages.Count; i++)
                if (ranges.Any(r => messages[i].Uid >= Math.Min(r.Low, r.High) && messages[i].Uid <= Math.Max(r.Low, r.High)))
                    yield return (messages[i], i + 1);
        }

        /// <summary>Split a command into atoms, quoted strings (unquoted) and parenthesised lists (kept whole).</summary>
        private static List<string> Tokenize(string command)
        {
            var words = new List<string>();
            for (var i = 0; i < command.Length;)
            {
                if (command[i] == ' ')
                {
                    i++;
                    continue;
                }
                if (command[i] == '"')
                {
                    var word = new StringBuilder();
                    for (i++; i < command.Length && command[i] != '"'; i++)
                        word.Append(command[i] == '\\' ? command[++i] : command[i]);
                    words.Add(word.ToString());
                    i++;
                    continue;
                }
                var start = i;
                var depth = 0;
                for (; i < command.Length && (depth > 0 || command[i] != ' '); i++)
                {
                    if (command[i] is '(' or '[') depth++;
                    else if (command[i] is ')' or ']') depth--;
                }
                words.Add(command[start..i]);
            }
            return words;
        }

        private Task<bool> Ok(string tag, string text) => Reply(tag, "OK " + text);

        private async Task<bool> Reply(string tag, string text)
        {
            await WriteAsync($"{tag} {text}");
            return true;
        }

        private ValueTask WriteAsync(string line) => connection.WriteAsync(line + "\r\n");
    }
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// MailKit's ImapClient against a fake IMAPS inbox. The interesting property is at-least-once
/// processing: whatever breaks, a message is either handled and flagged, or still unseen.
/// </summary>
public sealed class ImapExample : IAsyncDisposable
{
    private static readonly TlsOptions Tls = TlsOptions.SelfSigned();
    private readonly FakeImapServer _imap = new(Tls, "orders", "s3cret");
    private readonly List<string> _handled = [];

    private MailboxPoller Poller(string password = "s3cret") =>
        new(new ImapSettings(_imap.Tcp.Host, _imap.Tcp.Port, "orders", password) { CertificateValidation = Tls.TrustServerCertificate });

    private Task Handle(MimeKit.MimeMessage message)
    {
        lock (_handled) _handled.Add(message.Subject ?? "");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _imap.DisposeAsync();

    [Fact]
    public async Task UnseenMessages_AreHandled_ThenFlaggedSeen()
    {
        _imap.Deliver("ada@example.test", "Order 1", "one");
        _imap.Deliver("bob@example.test", "Old news", "read already", seen: true);
        _imap.Deliver("cy@example.test", "Order 2", "two");

        Assert.Equal(2, await Poller().PollAsync(Handle));

        Assert.Equal(["Order 1", "Order 2"], _handled);
        Assert.All(_imap.Inbox, m => Assert.True(m.Seen));
        _imap.Tcp.Transcript.AssertReceivedMessagesInOrder("LOGIN ***", "SELECT INBOX", "UID SEARCH UNSEEN", "UID FETCH 1 (BODY.PEEK[])", "UID STORE 1 +FLAGS.SILENT (\\Seen)", "UID FETCH 3 *", "LOGOUT");
        _imap.Tcp.Transcript.AssertNotReceivedMessage("LOGIN orders*"); // the password stays out of the transcript
    }

    [Fact]
    public async Task ConnectionDroppedMidFetch_LeavesTheMessageUnseen_ForTheNextPoll()
    {
        _imap.Deliver("ada@example.test", "Order 1", "one");
        var big = _imap.Deliver("bob@example.test", "Order 2", new string('x', 20_000));
        _imap.DropDuringFetchOf(big.Uid);

        await Assert.ThrowsAnyAsync<Exception>(() => Poller().PollAsync(Handle));
        Assert.Equal(["Order 1"], _handled);
        Assert.False(big.Seen);

        Assert.Equal(1, await Poller().PollAsync(Handle));
        Assert.Equal(["Order 1", "Order 2"], _handled);
    }

    [Fact]
    public async Task FailingHandler_LeavesTheMessageUnseen()
    {
        var poison = _imap.Deliver("ada@example.test", "Poison", "boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Poller().PollAsync(_ => throw new InvalidOperationException("handler failed")));

        Assert.False(poison.Seen);
        _imap.Tcp.Transcript.AssertNotReceivedMessage("UID STORE *");
    }

    [Fact]
    public async Task WrongPassword_IsAnAuthenticationError()
    {
        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => Poller(password: "wrong").PollAsync(Handle));

        _imap.Tcp.Transcript.AssertNotReceivedMessage("SELECT *");
    }

    [Fact]
    public async Task LockedMailbox_IsReported_AndNothingIsTouched()
    {
        _imap.Deliver("ada@example.test", "Order 1", "one");
        _imap.FailNext("SELECT *", "NO [INUSE] Mailbox is locked by another session");

        var ex = await Assert.ThrowsAsync<ImapCommandException>(() => Poller().PollAsync(Handle));

        Assert.Contains("locked", ex.ResponseText);
        Assert.Empty(_handled);
        Assert.False(_imap.Inbox[0].Seen);
    }

    [Fact]
    public async Task ServerShuttingDown_MidSession_IsAnError_NotAnEmptyInbox()
    {
        _imap.Deliver("ada@example.test", "Order 1", "one");
        _imap.FailNext("UID SEARCH *", "BYE Server shutting down");

        await Assert.ThrowsAnyAsync<Exception>(() => Poller().PollAsync(Handle));

        Assert.Empty(_handled);
    }
}
