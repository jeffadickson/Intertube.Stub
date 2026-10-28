using System.Net;
using System.Net.Mail;
using System.Text;
using Intertube.Stub.Smtp;

namespace Intertube.Stub.Tests;

public class SmtpServerTests
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>Send lines and return the server's reply lines up to the final (non-continuation) one.</summary>
    private static async Task<List<string>> SayAsync(Connection client, string line)
    {
        await client.WriteLineAsync(line);
        return await ReplyAsync(client);
    }

    private static async Task<List<string>> ReplyAsync(Connection client)
    {
        var lines = new List<string>();
        while (await client.ReadLineAsync() is { } reply)
        {
            lines.Add(reply);
            if (reply.Length < 4 || reply[3] != '-') break;
        }
        return lines;
    }

    [Fact]
    public async Task BclSmtpClient_MessageIsCaptured_WithDecodedSubjectAndBody()
    {
        await using var smtp = SmtpServer.Start();
        using var client = new SmtpClient(smtp.Host, smtp.Port);
        using var mail = new MailMessage("shop@example.test", "ada@example.test", "Grüße – order 42", "Hi Ada,\nyour order shipped. Total: 12 €");
        mail.Bcc.Add("audit@example.test");

        await client.SendMailAsync(mail);

        var message = Assert.Single(smtp.Messages);
        Assert.Equal("shop@example.test", message.From);
        Assert.Equal(["ada@example.test", "audit@example.test"], message.Recipients);
        Assert.Equal("Grüße – order 42", message.Subject);
        Assert.Contains("Total: 12 €", message.TextBody);
        Assert.Null(message.Header("Bcc"));
        smtp.Tcp.Transcript.AssertReceivedMessage("MESSAGE shop@example.test -> *");
    }

    [Fact]
    public async Task DotStuffing_IsUndone_AndPipelinedCommandsAreAnswered()
    {
        await using var smtp = SmtpServer.Start();
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);
        Assert.Contains("250 ENHANCEDSTATUSCODES", await SayAsync(client, "EHLO test"));

        await client.WriteAsync("MAIL FROM:<a@x.test>\r\nRCPT TO:<b@x.test>\r\nDATA\r\n");
        Assert.StartsWith("250", (await ReplyAsync(client))[0]);
        Assert.StartsWith("250", (await ReplyAsync(client))[0]);
        Assert.StartsWith("354", (await ReplyAsync(client))[0]);
        await client.WriteAsync("Subject: dots\r\n\r\n..leading dot\r\n.\r\n");
        Assert.StartsWith("250 2.0.0", (await ReplyAsync(client))[0]);

        Assert.Equal("Subject: dots\r\n\r\n.leading dot\r\n", smtp.Messages[0].Text);
    }

    [Fact]
    public async Task Auth_PlainAndLogin_AndMailRequiresIt()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Users = new Dictionary<string, string> { ["bob"] = "pw" } });
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);
        Assert.Contains("250 AUTH PLAIN LOGIN", await SayAsync(client, "EHLO test"));

        Assert.StartsWith("530", (await SayAsync(client, "MAIL FROM:<bob@x.test>"))[0]);
        Assert.StartsWith("535", (await SayAsync(client, "AUTH PLAIN " + B64("\0bob\0wrong")))[0]);
        Assert.StartsWith("334 VXNlcm5hbWU6", (await SayAsync(client, "AUTH LOGIN"))[0]);
        Assert.StartsWith("334 UGFzc3dvcmQ6", (await SayAsync(client, B64("bob")))[0]);
        Assert.StartsWith("235", (await SayAsync(client, B64("pw")))[0]);
        Assert.StartsWith("250", (await SayAsync(client, "MAIL FROM:<bob@x.test>"))[0]);

        smtp.Tcp.Transcript.AssertReceivedMessagesInOrder("AUTH PLAIN user=bob rejected", "AUTH LOGIN user=bob accepted");
    }

    [Fact]
    public async Task OversizedMessage_IsRefusedUpFrontOrAfterData()
    {
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { MaxMessageSize = 100 });
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);
        await SayAsync(client, "EHLO test");

        Assert.StartsWith("552", (await SayAsync(client, "MAIL FROM:<a@x.test> SIZE=5000"))[0]);
        await SayAsync(client, "MAIL FROM:<a@x.test>");
        await SayAsync(client, "RCPT TO:<b@x.test>");
        await SayAsync(client, "DATA");
        await client.WriteAsync(new string('x', 500) + "\r\n.\r\n");
        Assert.StartsWith("552", (await ReplyAsync(client))[0]);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task FailNext_Greylisting_ThenTheRetryIsDelivered()
    {
        await using var smtp = SmtpServer.Start();
        smtp.FailNext("RCPT TO:<*>*", "451 4.7.1 Greylisted, try again later");
        using var client = new SmtpClient(smtp.Host, smtp.Port);

        var ex = await Assert.ThrowsAnyAsync<SmtpException>(() => client.SendMailAsync("a@x.test", "b@x.test", "hi", "body"));
        Assert.Equal(SmtpStatusCode.LocalErrorInProcessing, ex.StatusCode); // 451
        await client.SendMailAsync("a@x.test", "b@x.test", "hi", "body");

        Assert.Single(smtp.Messages);
    }

    [Fact]
    public async Task FailAlways_RejectsOneDomain_AndEndOfDataCanBeRefused()
    {
        await using var smtp = SmtpServer.Start();
        smtp.FailAlways("RCPT TO:<*@blocked.test>*", "550 5.1.1 No such user");
        smtp.FailNext(".", "554 5.7.1 Message rejected as spam");
        using var client = new SmtpClient(smtp.Host, smtp.Port);

        await Assert.ThrowsAsync<SmtpFailedRecipientException>(() => client.SendMailAsync("a@x.test", "eve@blocked.test", "hi", "body"));
        var spam = await Assert.ThrowsAsync<SmtpException>(() => client.SendMailAsync("a@x.test", "b@x.test", "hi", "body"));
        Assert.Equal(SmtpStatusCode.TransactionFailed, spam.StatusCode);
        await client.SendMailAsync("a@x.test", "b@x.test", "hi again", "body");

        Assert.Equal("hi again", Assert.Single(smtp.Messages).Subject);
    }

    [Fact]
    public async Task Intercept_421_ClosesTheConnection()
    {
        await using var smtp = SmtpServer.Start();
        smtp.Intercept = command => ValueTask.FromResult(command.Verb == "MAIL" ? "421 4.3.2 Service shutting down" : null);
        using var client = new SmtpClient(smtp.Host, smtp.Port);

        var ex = await Assert.ThrowsAsync<SmtpException>(() => client.SendMailAsync("a@x.test", "b@x.test", "hi", "body"));

        Assert.Equal(SmtpStatusCode.ServiceNotAvailable, ex.StatusCode);
        await smtp.Tcp.Transcript[0].WaitForCloseAsync();
    }

    [Fact]
    public async Task ClientVanishingMidData_DeliversNothing()
    {
        await using var smtp = SmtpServer.Start();
        await using (var client = await smtp.Tcp.ConnectAsync())
        {
            await ReplyAsync(client);
            await SayAsync(client, "HELO test");
            await SayAsync(client, "MAIL FROM:<a@x.test>");
            await SayAsync(client, "RCPT TO:<b@x.test>");
            await SayAsync(client, "DATA");
            await client.WriteAsync("Subject: half\r\n\r\nand then");
        }

        await smtp.Tcp.Transcript[0].WaitForCloseAsync();
        Assert.Empty(smtp.Messages);
        smtp.ThrowIfFaulted();
    }

    [Fact]
    public async Task StartTls_RequiredBeforeMail_AndTranscriptStaysReadable()
    {
        var tls = TlsOptions.SelfSigned();
        await using var smtp = SmtpServer.Start(new SmtpServerOptions { Tls = tls, RequireTls = true });
        await using var client = await smtp.Tcp.ConnectAsync();
        await ReplyAsync(client);
        Assert.Contains("250 STARTTLS", await SayAsync(client, "EHLO test"));
        Assert.StartsWith("530", (await SayAsync(client, "MAIL FROM:<a@x.test>"))[0]);

        Assert.StartsWith("220", (await SayAsync(client, "STARTTLS"))[0]);
        await client.UpgradeToTlsAsync(tls);
        Assert.DoesNotContain("250 STARTTLS", await SayAsync(client, "EHLO test"));
        Assert.StartsWith("250", (await SayAsync(client, "MAIL FROM:<a@x.test>"))[0]);
        await SayAsync(client, "RCPT TO:<b@x.test>");
        await SayAsync(client, "DATA");
        await client.WriteAsync("Subject: secret\r\n\r\nhi\r\n.\r\n");
        await ReplyAsync(client);

        Assert.True(Assert.Single(smtp.Messages).Tls);
        smtp.Tcp.Transcript.AssertReceivedLine("Subject: secret");
    }

    [Fact]
    public async Task WaitForMessage_ReturnsTheMatchingMail()
    {
        await using var smtp = SmtpServer.Start();
        using var client = new SmtpClient(smtp.Host, smtp.Port);
        var sending = Task.Run(async () =>
        {
            await client.SendMailAsync("a@x.test", "first@x.test", "one", "1");
            await client.SendMailAsync("a@x.test", "second@x.test", "two", "2");
        });

        var second = await smtp.WaitForMessageAsync(m => m.Recipients.Contains("second@x.test"));

        Assert.Equal("two", second.Subject);
        await sending;
    }
}
