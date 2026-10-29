using System.Text;
using Intertube.Stub.Smtp;

namespace Intertube.Stub.Tests;

/// <summary>Protocol edge cases and MIME robustness for <see cref="SmtpServer"/>, driven over a raw connection.</summary>
public class SmtpProtocolTests
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static async Task<string> SayAsync(Connection client, string line)
    {
        await client.WriteLineAsync(line);
        return await ReplyAsync(client);
    }

    /// <summary>The final line of the next reply, or "(closed)".</summary>
    private static async Task<string> ReplyAsync(Connection client)
    {
        while (await client.ReadLineAsync() is { } reply)
            if (reply.Length < 4 || reply[3] != '-') return reply;
        return "(closed)";
    }

    private static async Task<Connection> GreetedAsync(SmtpServer smtp)
    {
        var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);
        Assert.StartsWith("250", await SayAsync(client, "EHLO test"));
        return client;
    }

    /// <summary>Send MAIL, RCPT and DATA, then raw message text ending in CRLF.CRLF, and return the reply to it.</summary>
    private static async Task<string> TransactAsync(Connection client, string data, string from = "a@x.test")
    {
        Assert.StartsWith("250", await SayAsync(client, $"MAIL FROM:<{from}>"));
        Assert.StartsWith("250", await SayAsync(client, "RCPT TO:<b@x.test>"));
        Assert.StartsWith("354", await SayAsync(client, "DATA"));
        await client.WriteAsync(data);
        return await ReplyAsync(client);
    }

    /// <summary>Deliver raw message bytes (already dot-stuffed, ending in CRLF.CRLF) and return what was captured.</summary>
    private static async Task<SmtpMessage> DeliverAsync(byte[] data)
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await GreetedAsync(smtp);
        Assert.StartsWith("250", await SayAsync(client, "MAIL FROM:<a@x.test>"));
        Assert.StartsWith("250", await SayAsync(client, "RCPT TO:<b@x.test>"));
        Assert.StartsWith("354", await SayAsync(client, "DATA"));
        await client.WriteAsync(data);
        Assert.StartsWith("250", await ReplyAsync(client));
        return Assert.Single(smtp.Messages);
    }

    private static Task<SmtpMessage> DeliverAsync(string text) => DeliverAsync(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Mime_MalformedEncodingsAreLeftAsSent_NeverThrown()
    {
        var message = await DeliverAsync(
            "Subject: =?utf-8?B?!!not base64!!?= and =?x-no-such-charset?Q?abc?=\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            "%%% not base64 %%%\r\n.\r\n");

        Assert.Equal("=?utf-8?B?!!not base64!!?= and =?x-no-such-charset?Q?abc?=", message.Subject);
        Assert.Contains("not base64", message.TextBody);
        Assert.Contains("a@x.test", message.ToString());
    }

    [Fact]
    public async Task Mime_FoldedHeaderKeepsItsWhitespace_AndEncodedWordsDecode()
    {
        var message = await DeliverAsync(
            "Subject: =?iso-8859-1?Q?caf=E9?=\r\n" +
            "X-Folded: one\r\n\ttwo\r\n" +
            "\r\nbody\r\n.\r\n");

        Assert.Equal("café", message.Subject);
        Assert.Equal("one\ttwo", message.Header("X-Folded"));
    }

    [Fact]
    public async Task Mime_EightBitBodyIsDecodedInItsDeclaredCharset()
    {
        var head = Encoding.ASCII.GetBytes("Content-Type: text/plain; charset=windows-1252\r\nContent-Transfer-Encoding: 8bit\r\n\r\nPrice: ");
        var message = await DeliverAsync([.. head, 0x80, .. "5\r\n.\r\n"u8]);

        Assert.Equal("Price: €5\r\n", message.TextBody);
    }

    [Fact]
    public async Task Mime_TextBodySkipsAttachments_AndSearchesNestedParts()
    {
        var message = await DeliverAsync(
            "Content-Type: multipart/mixed; boundary=\"outer\"\r\n" +
            "\r\n" +
            "preamble\r\n" +
            "--outer\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Disposition: attachment; filename=notes.txt\r\n" +
            "\r\n" +
            "attached notes\r\n" +
            "--outer\r\n" +
            "Content-Type: multipart/alternative; boundary=inner\r\n" +
            "\r\n" +
            "--inner\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>html</p>\r\n" +
            "--inner\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n" +
            "\r\n" +
            "the --outer text is not a boundary; caf=C3=A9\r\n" +
            "--inner--\r\n" +
            "--outer--\r\n" +
            ".\r\n");

        Assert.Equal("the --outer text is not a boundary; café", message.TextBody);
    }

    [Fact]
    public async Task Data_OnlyCrLfDotCrLfEndsTheMessage_AndLineEndingsAreKept()
    {
        var message = await DeliverAsync("Subject: lf\r\n\r\nbare\n.\nstill data\r\n.\r\n");

        Assert.Equal("Subject: lf\r\n\r\nbare\n.\nstill data\r\n", message.Text);
    }

    [Fact]
    public async Task Mail_SizeParameter_IsCheckedRobustly()
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await GreetedAsync(smtp);

        Assert.StartsWith("552", await SayAsync(client, "MAIL FROM:<a@x.test> SIZE=99999999999999999999999"));
        Assert.StartsWith("501", await SayAsync(client, "MAIL FROM:<a@x.test> SIZE=lots"));
        Assert.StartsWith("501", await SayAsync(client, "MAIL FROM:"));
        Assert.StartsWith("250", await SayAsync(client, "MAIL FROM:<SIZE=99999999999@x.test>")); // not a parameter
        smtp.ThrowIfFaulted();
    }

    [Fact]
    public async Task Greeting_NeedsADomain()
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);

        Assert.StartsWith("501", await SayAsync(client, "EHLO"));
        Assert.StartsWith("501", await SayAsync(client, "HELO"));
    }

    [Fact]
    public async Task Auth_FollowsRfc4954()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Users = new Dictionary<string, string> { ["bob"] = "pw" } });
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);

        Assert.StartsWith("503", await SayAsync(client, "AUTH PLAIN " + B64("\0bob\0pw"))); // before EHLO
        Assert.StartsWith("250", await SayAsync(client, "EHLO test"));
        Assert.StartsWith("334", await SayAsync(client, "AUTH LOGIN"));
        Assert.StartsWith("501 5.7.0", await SayAsync(client, "*")); // cancelled
        Assert.StartsWith("501 5.5.2", await SayAsync(client, "AUTH PLAIN %%%")); // not base64
        Assert.StartsWith("235", await SayAsync(client, "AUTH PLAIN " + B64("\0bob\0pw")));
        smtp.ThrowIfFaulted();
    }

    [Fact]
    public async Task StartTls_WithPipelinedCommands_IsRefusedAndClosed()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Tls = TlsOptions.SelfSigned() });
        await using var client = await GreetedAsync(smtp);

        await client.WriteAsync("STARTTLS\r\nMAIL FROM:<a@x.test>\r\n");

        Assert.StartsWith("554", await ReplyAsync(client));
        Assert.Equal("(closed)", await ReplyAsync(client));
        Assert.Contains("commands pipelined after STARTTLS", smtp.Tcp.Transcript[0].Events);
        smtp.ThrowIfFaulted();
    }

    [Fact]
    public async Task OverlongLine_GetsA500AndTheConnectionCloses_WithoutAHandlerError()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Server = new ServerOptions { MaxLineLength = 100 } });
        await using var client = await GreetedAsync(smtp);

        await client.WriteLineAsync("NOOP " + new string('x', 500));

        Assert.StartsWith("500 5.5.2", await ReplyAsync(client));
        Assert.Equal("(closed)", await ReplyAsync(client));
        smtp.ThrowIfFaulted();
    }

    [Fact]
    public async Task FailNext_MatchesTheCanonicalFormOfTheCommand()
    {
        await using var smtp = SmtpServer.Start();
        smtp.FailNext("RCPT TO:<eve@blocked.test>", "550 5.1.1 No such user");
        await using var client = await GreetedAsync(smtp);
        Assert.StartsWith("250", await SayAsync(client, "mail from: <a@x.test>"));

        Assert.StartsWith("550", await SayAsync(client, "rcpt to: <eve@blocked.test>"));
    }

    [Fact]
    public async Task Mime_BareLfLineEndings_SplitHeadersFromBody_AndDataIsKept()
    {
        // The line before the final "." must end in CRLF, or it would not end the data.
        const string text = "Subject: bare lf\nX-Folded: one\n\ttwo\n\nfirst line\nsecond line\r\n";
        var message = await DeliverAsync(text + ".\r\n");

        Assert.Equal("bare lf", message.Subject);
        Assert.Equal("one\ttwo", message.Header("X-Folded"));
        Assert.Equal("first line\nsecond line\r\n", message.TextBody);
        Assert.Equal(text, message.Text);
    }

    [Fact]
    public async Task Mime_BareLfMultipart_FindsThePlainTextPart()
    {
        var message = await DeliverAsync(
            "Content-Type: multipart/alternative; boundary=b\n" +
            "\n" +
            "--b\n" +
            "Content-Type: text/html\n" +
            "\n" +
            "<p>html</p>\n" +
            "--b\n" +
            "Content-Type: text/plain; charset=utf-8\n" +
            "Content-Transfer-Encoding: quoted-printable\n" +
            "\n" +
            "soft=\n" +
            "break caf=C3=A9\n" +
            "--b--\r\n" +
            ".\r\n");

        Assert.Equal("softbreak café", message.TextBody);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task Mime_PartStartingWithABlankLine_HasNoHeaders_AndIsPlainText(string eol)
    {
        var message = await DeliverAsync(string.Join(eol,
            "Content-Type: multipart/mixed; boundary=b",
            "",
            "--b",
            "Content-Type: text/html",
            "",
            "<p>html</p>",
            "--b",
            "",
            "Content-Type: not a header",
            "",
            "last line",
            "--b--") + "\r\n.\r\n");

        Assert.Equal($"Content-Type: not a header{eol}{eol}last line", message.TextBody);
    }

    [Fact]
    public async Task Mime_AdjacentDelimiters_MakeAnEmptyPart_WithoutThrowing()
    {
        var message = await DeliverAsync(
            "Content-Type: multipart/mixed; boundary=b\r\n" +
            "\r\n" +
            "--b\r\n" +
            "--b\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>html</p>\r\n" +
            "--b--\r\n" +
            ".\r\n");

        Assert.Equal("", message.TextBody); // the empty first part has no headers, so it is text/plain
    }

    [Fact]
    public async Task Commands_OutOfOrder_GetA503_AndLeaveTheTransactionAlone()
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);

        Assert.Equal("503 5.5.1 Say EHLO first", await SayAsync(client, "MAIL FROM:<a@x.test>"));
        Assert.StartsWith("250", await SayAsync(client, "EHLO test"));
        Assert.Equal("503 5.5.1 Need MAIL first", await SayAsync(client, "RCPT TO:<b@x.test>"));
        Assert.Equal("503 5.5.1 Need RCPT first", await SayAsync(client, "DATA"));
        Assert.Equal("250 2.1.0 OK", await SayAsync(client, "MAIL FROM:<a@x.test>"));
        Assert.Equal("503 5.5.1 Need RCPT first", await SayAsync(client, "DATA"));
        Assert.Equal("503 5.5.1 Nested MAIL command", await SayAsync(client, "MAIL FROM:<other@x.test>"));
        Assert.Equal("250 2.1.5 OK", await SayAsync(client, "RCPT TO:<b@x.test>"));
        Assert.StartsWith("354", await SayAsync(client, "DATA"));
        await client.WriteAsync("Subject: hi\r\n\r\nbody\r\n.\r\n");
        Assert.StartsWith("250", await ReplyAsync(client));

        Assert.Equal("a@x.test", Assert.Single(smtp.Messages).From);
        smtp.ThrowIfFaulted();
    }

    [Fact]
    public async Task Mail_NullReversePath_IsAccepted()
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await GreetedAsync(smtp);

        Assert.StartsWith("250", await TransactAsync(client, "Subject: bounce\r\n\r\nbody\r\n.\r\n", from: ""));

        Assert.Equal("", Assert.Single(smtp.Messages).From);
    }

    [Theory]
    [InlineData("RCPT")]
    [InlineData("RCPT TO:")]
    [InlineData("RCPT TO:<>")]
    [InlineData("RCPT TO:<b@x.test")]
    [InlineData("RCPT FOR:<b@x.test>")]
    public async Task Rcpt_MalformedPath_Gets501_AndAddsNoRecipient(string command)
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await GreetedAsync(smtp);
        Assert.StartsWith("250", await SayAsync(client, "MAIL FROM:<a@x.test>"));

        Assert.Equal("501 5.5.4 Syntax: RCPT TO:<address>", await SayAsync(client, command));
        Assert.Equal("503 5.5.1 Need RCPT first", await SayAsync(client, "DATA"));
    }

    [Fact]
    public async Task Transaction_IsResetAfterData_WhateverTheOutcome()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { MaxMessageSize = 100 });
        smtp.FailNext(".", "554 5.7.1 Message rejected as spam");
        await using var client = await GreetedAsync(smtp);

        Assert.StartsWith("554", await TransactAsync(client, "Subject: spam\r\n\r\nbuy\r\n.\r\n"));
        Assert.Equal("503 5.5.1 Need MAIL first", await SayAsync(client, "RCPT TO:<b@x.test>"));
        Assert.StartsWith("552", await TransactAsync(client, new string('x', 500) + "\r\n.\r\n"));
        Assert.Equal("503 5.5.1 Need MAIL first", await SayAsync(client, "RCPT TO:<b@x.test>"));
        Assert.StartsWith("250", await TransactAsync(client, "Subject: ok\r\n\r\nfine\r\n.\r\n"));
        Assert.Equal("503 5.5.1 Need MAIL first", await SayAsync(client, "RCPT TO:<b@x.test>"));

        Assert.Equal("ok", Assert.Single(smtp.Messages).Subject);
    }

    [Theory]
    [InlineData("RSET", "250 2.0.0 OK")]
    [InlineData("EHLO again", "250 AUTH PLAIN LOGIN")]
    [InlineData("HELO again", "250 smtp.intertube.test")]
    public async Task ResetCommands_DiscardTheTransaction_ButKeepTheLogin(string command, string reply)
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Users = new Dictionary<string, string> { ["bob"] = "pw" } });
        await using var client = await GreetedAsync(smtp);
        Assert.StartsWith("235", await SayAsync(client, "AUTH PLAIN " + B64("\0bob\0pw")));
        Assert.StartsWith("250", await SayAsync(client, "MAIL FROM:<a@x.test>"));
        Assert.StartsWith("250", await SayAsync(client, "RCPT TO:<b@x.test>"));

        Assert.Equal(reply, await SayAsync(client, command));

        Assert.Equal("503 5.5.1 Need RCPT first", await SayAsync(client, "DATA"));
        Assert.Equal("503 5.5.1 Need MAIL first", await SayAsync(client, "RCPT TO:<b@x.test>"));
        Assert.Equal("250 2.1.0 OK", await SayAsync(client, "MAIL FROM:<a@x.test>")); // still greeted and logged in
    }

    [Theory]
    [InlineData("NOOP", "250 2.0.0 OK")]
    [InlineData("noop anything", "250 2.0.0 OK")]
    [InlineData("VRFY ada@x.test", "252 2.1.5 Cannot verify, but will accept")]
    [InlineData("HELP", "214 2.0.0 See RFC 5321")]
    [InlineData("BOGUS", "500 5.5.1 Command not recognised")]
    [InlineData("", "500 5.5.1 Command not recognised")]
    public async Task OtherCommands_AreAnswered_AndLeaveTheTransactionAlone(string command, string reply)
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await GreetedAsync(smtp);
        Assert.StartsWith("250", await SayAsync(client, "MAIL FROM:<a@x.test>"));
        Assert.StartsWith("250", await SayAsync(client, "RCPT TO:<b@x.test>"));

        Assert.Equal(reply, await SayAsync(client, command));

        Assert.StartsWith("354", await SayAsync(client, "DATA"));
        await client.WriteAsync("Subject: still here\r\n\r\nbody\r\n.\r\n");
        Assert.StartsWith("250", await ReplyAsync(client));
        Assert.Equal(["b@x.test"], Assert.Single(smtp.Messages).Recipients);
    }

    [Fact]
    public async Task Quit_Says221_AndClosesTheConnection()
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await GreetedAsync(smtp);

        Assert.Equal("221 2.0.0 Bye", await SayAsync(client, "QUIT"));
        Assert.Equal("(closed)", await ReplyAsync(client));
        smtp.ThrowIfFaulted();
    }

    [Fact]
    public async Task StartTls_WithoutACertificate_IsNotAvailable()
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await GreetedAsync(smtp);

        Assert.Equal("502 5.5.1 STARTTLS not available", await SayAsync(client, "STARTTLS"));
        Assert.Equal("250 2.0.0 OK", await SayAsync(client, "NOOP"));
    }

    [Fact]
    public async Task StartTls_WithAnArgument_Gets501_AndOnceOnTlsIsNotAvailable()
    {
        var tls = TlsOptions.SelfSigned();
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Tls = tls });
        await using var client = await GreetedAsync(smtp);

        Assert.Equal("501 5.5.4 Syntax: STARTTLS", await SayAsync(client, "STARTTLS now"));
        Assert.Equal("250 2.0.0 OK", await SayAsync(client, "NOOP")); // still plaintext
        Assert.StartsWith("220", await SayAsync(client, "STARTTLS"));
        await client.UpgradeToTlsAsync(tls);
        Assert.StartsWith("250", await SayAsync(client, "EHLO test"));

        Assert.Equal("502 5.5.1 STARTTLS not available", await SayAsync(client, "STARTTLS"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Auth_WithNoUsers_IsNotEnabled_AndMailNeedsNoLogin(bool emptyDictionary)
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Users = emptyDictionary ? new Dictionary<string, string>() : null });
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);

        Assert.Equal("250 ENHANCEDSTATUSCODES", await SayAsync(client, "EHLO test")); // AUTH, if advertised, would be last
        Assert.Equal("503 5.5.1 Authentication not enabled", await SayAsync(client, "AUTH PLAIN " + B64("\0bob\0pw")));
        Assert.Equal("250 2.1.0 OK", await SayAsync(client, "MAIL FROM:<a@x.test>"));
    }

    [Theory]
    [InlineData("AUTH")]
    [InlineData("AUTH CRAM-MD5")]
    [InlineData("AUTH XOAUTH2 dXNlcj1ib2I=")]
    public async Task Auth_UnknownOrMissingMechanism_Gets504(string command)
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Users = new Dictionary<string, string> { ["bob"] = "pw" } });
        await using var client = await GreetedAsync(smtp);

        Assert.Equal("504 5.5.4 Unrecognised authentication type", await SayAsync(client, command));
        Assert.StartsWith("235", await SayAsync(client, "auth plain " + B64("\0bob\0pw"))); // mechanism is case-insensitive
    }

    [Fact]
    public async Task Auth_OnceAuthenticated_IsRefused_IncludingDuringATransaction()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Users = new Dictionary<string, string> { ["bob"] = "pw" } });
        await using var client = await GreetedAsync(smtp);
        Assert.StartsWith("235", await SayAsync(client, "AUTH PLAIN " + B64("\0bob\0pw")));

        Assert.Equal("503 5.5.1 Already authenticated", await SayAsync(client, "AUTH LOGIN")); // no 334 challenge
        Assert.StartsWith("250", await SayAsync(client, "MAIL FROM:<a@x.test>"));
        // A transaction needs a login first, so AUTH during one always finds the session authenticated.
        Assert.Equal("503 5.5.1 Already authenticated", await SayAsync(client, "AUTH PLAIN " + B64("\0bob\0pw")));
        Assert.StartsWith("250", await SayAsync(client, "RCPT TO:<b@x.test>")); // the transaction carries on
    }

    [Fact]
    public async Task Auth_EqualsSignIsAnEmptyResponse()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Users = new Dictionary<string, string> { ["bob"] = "" } });
        await using var client = await GreetedAsync(smtp);

        Assert.StartsWith("535", await SayAsync(client, "AUTH PLAIN =")); // empty, so not three fields: invalid, but not undecodable
        Assert.StartsWith("334", await SayAsync(client, "AUTH LOGIN"));
        Assert.StartsWith("334", await SayAsync(client, B64("bob")));
        Assert.Equal("235 2.7.0 Authentication successful", await SayAsync(client, "="));
    }
}
