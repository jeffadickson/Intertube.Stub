using System.Text;
using System.Net.Mail;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

public sealed class Mailer(string host, int port)
{
    public async Task SendWelcomeAsync(string to, string name)
    {
        using var smtp = new SmtpClient(host, port) { Timeout = 5000 };
        using var message = new MailMessage("noreply@shop.test", to, "Welcome!", $"Hi {name}, thanks for signing up.");
        await smtp.SendMailAsync(message);
    }
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// The BCL's own SmtpClient against a scripted SMTP conversation. The script doubles as an
/// executable spec of the dialogue, and a client that strays fails with the step it broke.
/// </summary>
public class SmtpExample
{
    private static Conversation SmtpServer(string rcptReply = "250 OK") => Conversation.Create()
        .Send("220 mail.test ESMTP Intertube.Stub")
        .Expect("EHLO *").Send("250-mail.test", "250 SIZE 10485760")
        .Expect("MAIL FROM:<noreply@shop.test>*", "250 OK")
        .Expect("RCPT TO:<*>", rcptReply)
        .Expect("DATA", "354 End data with <CR><LF>.<CR><LF>")
        .ExpectUntil("\r\n.\r\n")
        .Send("250 2.0.0 Queued as 42")
        .Expect("QUIT", "221 Bye");

    [Fact]
    public async Task SendsWelcomeMail()
    {
        await using var server = TcpServer.Listen(0, SmtpServer());

        await new Mailer(server.Host, server.Port).SendWelcomeAsync("ada@example.test", "Ada");

        await server.Transcript.WaitForClosedConnectionsAsync(1);
        server.ThrowIfFaulted();
        server.Transcript.AssertReceivedLine("RCPT TO:<ada@example.test>");
        server.Transcript.AssertReceivedLine("Subject: Welcome!");
        server.Transcript.AssertReceived("Hi Ada");
    }

    [Fact]
    public async Task RejectedRecipient_Throws()
    {
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .Send("220 mail.test ESMTP")
            .Expect("EHLO *", "250 mail.test")
            .Expect("MAIL FROM:*", "250 OK")
            .Expect("RCPT TO:*", "550 5.1.1 No such user")
            .Hang());

        var ex = await Assert.ThrowsAsync<SmtpFailedRecipientException>(() =>
            new Mailer(server.Host, server.Port).SendWelcomeAsync("ghost@example.test", "Ghost"));
        Assert.Equal(SmtpStatusCode.MailboxUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task GreylistingServer_SurfacesTransientFailure()
    {
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .Send("421 4.7.0 Try again later")
            .Close());

        var ex = await Assert.ThrowsAsync<SmtpException>(() => new Mailer(server.Host, server.Port).SendWelcomeAsync("a@b.test", "A"));
        Assert.Equal(SmtpStatusCode.ServiceNotAvailable, ex.StatusCode);
    }
}

// ---- A second system under test: MailKit, STARTTLS, auth and retries ------------------------

public sealed record MailSettings(string Host, int Port, string User, string Password)
{
    /// <summary>For tests: how to trust the server's certificate.</summary>
    public System.Net.Security.RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Sends order confirmations with MailKit: STARTTLS is mandatory, the invoice is attached, and
/// temporary (4xx) failures such as greylisting are retried. Permanent ones are not.
/// </summary>
public sealed class OrderNotifier(MailSettings settings, int maxAttempts = 3)
{
    public async Task SendConfirmationAsync(string to, int orderId, string invoiceCsv, CancellationToken cancellationToken = default)
    {
        var message = new MimeKit.MimeMessage();
        message.From.Add(MimeKit.MailboxAddress.Parse("orders@shop.test"));
        message.To.Add(MimeKit.MailboxAddress.Parse(to));
        message.Subject = $"Order {orderId} confirmed";
        var body = new MimeKit.BodyBuilder { TextBody = $"Thanks! Order {orderId} is on its way.", HtmlBody = $"<p>Thanks! Order <b>{orderId}</b> is on its way.</p>" };
        body.Attachments.Add($"invoice-{orderId}.csv", Encoding.UTF8.GetBytes(invoiceCsv), new MimeKit.ContentType("text", "csv"));
        message.Body = body.ToMessageBody();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = new MailKit.Net.Smtp.SmtpClient { ServerCertificateValidationCallback = settings.CertificateValidation, Timeout = 10_000 };
                await client.ConnectAsync(settings.Host, settings.Port, MailKit.Security.SecureSocketOptions.StartTls, cancellationToken);
                await client.AuthenticateAsync(settings.User, settings.Password, cancellationToken);
                await client.SendAsync(message, cancellationToken);
                await client.DisconnectAsync(quit: true, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTemporary(ex))
            {
                await Task.Delay(settings.RetryDelay, cancellationToken);
            }
        }
    }

    private static bool IsTemporary(Exception ex) => ex switch
    {
        MailKit.Net.Smtp.SmtpCommandException command => (int)command.StatusCode is >= 400 and < 500,
        MailKit.Net.Smtp.SmtpProtocolException or IOException or System.Net.Sockets.SocketException => true,
        _ => false,
    };
}

/// <summary>
/// MailKit against <see cref="Smtp.SmtpServer"/>: a server that accepts any valid dialogue and
/// captures the mail, so these tests assert on the message and on failure handling, not on the
/// client's exact command sequence.
/// </summary>
public sealed class OrderNotifierTests : IAsyncDisposable
{
    private static readonly TlsOptions Tls = TlsOptions.SelfSigned();

    private readonly Smtp.SmtpServer _smtp = Smtp.SmtpServer.Start(new Smtp.SmtpServerOptions
    {
        Tls = Tls,
        RequireTls = true,
        Users = new Dictionary<string, string> { ["shop"] = "s3cret" },
    });

    private MailSettings Settings => new(_smtp.Host, _smtp.Port, "shop", "s3cret")
    {
        CertificateValidation = Tls.TrustServerCertificate,
        RetryDelay = TimeSpan.FromMilliseconds(10),
    };

    public ValueTask DisposeAsync() => _smtp.DisposeAsync();

    [Fact]
    public async Task Confirmation_GoesOverTls_Authenticated_WithTheInvoiceAttached()
    {
        await new OrderNotifier(Settings).SendConfirmationAsync("ada@example.test", 42, "item,price\nbook,12\n");

        var mail = Assert.Single(_smtp.Messages);
        Assert.True(mail.Tls);
        Assert.Equal("shop", mail.User);
        Assert.Equal("Order 42 confirmed", mail.Subject);
        Assert.Equal("Thanks! Order 42 is on its way.", mail.TextBody.Trim());

        // For anything deeper than headers and text, hand the raw data to MimeKit.
        var parsed = MimeKit.MimeMessage.Load(new MemoryStream(mail.Data));
        var invoice = Assert.Single(parsed.Attachments.OfType<MimeKit.MimePart>());
        Assert.Equal("invoice-42.csv", invoice.FileName);
        using var content = new MemoryStream();
        await invoice.Content!.DecodeToAsync(content);
        Assert.Equal("item,price\nbook,12\n", Encoding.UTF8.GetString(content.ToArray()).ReplaceLineEndings("\n")); // text parts travel with CRLF
    }

    [Fact]
    public async Task Greylisting_IsRetried_AndDeliversExactlyOnce()
    {
        _smtp.FailNext("RCPT TO:*", "451 4.7.1 Greylisted, try again later");

        await new OrderNotifier(Settings).SendConfirmationAsync("ada@example.test", 1, "");

        Assert.Single(_smtp.Messages);
        _smtp.Tcp.Transcript.AssertConnectionCount(2);
    }

    [Fact]
    public async Task ConnectionDroppedBeforeTheServerAccepted_IsRetried()
    {
        var dropped = 0;
        _smtp.Intercept = command =>
        {
            if (command.Verb == "." && Interlocked.Exchange(ref dropped, 1) == 0) command.Connection.Abort();
            return ValueTask.FromResult<string?>(null);
        };

        await new OrderNotifier(Settings).SendConfirmationAsync("ada@example.test", 7, "");

        Assert.Single(_smtp.Messages);
    }

    [Fact]
    public async Task PermanentRejection_IsNotRetried()
    {
        _smtp.FailAlways("RCPT TO:<*@nowhere.test>*", "550 5.1.1 No such user");

        var ex = await Assert.ThrowsAsync<MailKit.Net.Smtp.SmtpCommandException>(() =>
            new OrderNotifier(Settings).SendConfirmationAsync("ghost@nowhere.test", 9, ""));

        Assert.Equal(MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable, ex.StatusCode);
        _smtp.Tcp.Transcript.AssertConnectionCount(1);
    }

    [Fact]
    public async Task WrongPassword_FailsWithoutSendingCredentialsInTheClear()
    {
        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() =>
            new OrderNotifier(Settings with { Password = "wrong" }).SendConfirmationAsync("ada@example.test", 3, ""));

        Assert.Empty(_smtp.Messages);
        // The transcript records plaintext even after STARTTLS; the AUTH happened after "TLS established".
        Assert.Contains("TLS established", string.Join("\n", _smtp.Tcp.Transcript[0].Events));
        _smtp.Tcp.Transcript.AssertReceivedMessage("AUTH * user=shop rejected");
    }
}
