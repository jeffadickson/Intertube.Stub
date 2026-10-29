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
}
