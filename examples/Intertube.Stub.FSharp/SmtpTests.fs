module Intertube.Stub.FSharp.SmtpTests

open System.Net.Mail
open Xunit
open Intertube.Stub
open Intertube.Stub.Smtp

/// The system under test: sends a password-reset mail with the BCL SmtpClient.
let sendReset (host: string) (port: int) (address: string) (code: string) =
    task {
        use smtp = new SmtpClient(host, port, Timeout = 5000)
        use mail = new MailMessage("noreply@shop.test", address, "Your reset code", $"Your code is {code}. It expires in 10 minutes.")
        do! smtp.SendMailAsync mail
    }

[<Fact>]
let ``the ready-made SMTP server captures the mail`` () =
    task {
        use smtp = SmtpServer.Start()

        do! sendReset smtp.Host smtp.Port "ada@example.test" "493817"

        let mail = Assert.Single smtp.Messages
        Assert.Equal("noreply@shop.test", mail.From)
        Assert.Equal<string seq>([ "ada@example.test" ], mail.Recipients)
        Assert.Equal("Your reset code", mail.Subject)
        Assert.Contains("493817", mail.TextBody)
    }

[<Fact>]
let ``a rejected recipient surfaces as SmtpFailedRecipientException`` () =
    task {
        use smtp = SmtpServer.Start()
        smtp.FailAlways(LineMatcher.Glob "RCPT TO:<*@blocked.test>*", "550 5.1.1 No such user")

        let! ex = Assert.ThrowsAsync<SmtpFailedRecipientException>(fun () -> sendReset smtp.Host smtp.Port "eve@blocked.test" "1")

        Assert.Equal(SmtpStatusCode.MailboxUnavailable, ex.StatusCode)
        Assert.Empty smtp.Messages
    }
