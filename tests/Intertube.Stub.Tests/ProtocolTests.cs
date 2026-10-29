using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Intertube.Stub.Tests;

public class ProtocolTests
{
    private static Conversation Smtp() => Conversation.Create()
        .Send("220 mail.test ESMTP Intertube.Stub")
        .Expect("EHLO *", line => $"250 mail.test greets {line[5..]}")
        .Expect("MAIL FROM:<*>", "250 OK")
        .Expect("RCPT TO:<*>", "250 OK")
        .Expect("DATA", "354 End data with <CR><LF>.<CR><LF>")
        .ExpectUntil("\r\n.\r\n", body => Assert.Contains("Subject: hi", body))
        .Send("250 Queued")
        .Expect("QUIT", "221 Bye")
        .Close();

    [Fact]
    public async Task Conversation_ScriptsAnSmtpSession()
    {
        await using var server = TcpServer.Listen(0, Smtp());
        await using var client = await server.ConnectAsync();

        Assert.StartsWith("220", await client.ReadLineAsync());
        await client.WriteLineAsync("EHLO client.test");
        Assert.Equal("250 mail.test greets client.test", await client.ReadLineAsync());
        await client.WriteLineAsync("MAIL FROM:<a@test>");
        Assert.Equal("250 OK", await client.ReadLineAsync());
        await client.WriteLineAsync("RCPT TO:<b@test>");
        Assert.Equal("250 OK", await client.ReadLineAsync());
        await client.WriteLineAsync("DATA");
        Assert.StartsWith("354", await client.ReadLineAsync());
        await client.WriteAsync("Subject: hi\r\n\r\nbody\r\n.\r\n");
        Assert.Equal("250 Queued", await client.ReadLineAsync());
        await client.WriteLineAsync("QUIT");
        Assert.Equal("221 Bye", await client.ReadLineAsync());
        Assert.Null(await client.ReadLineAsync());

        await server.Transcript.WaitForClosedConnectionsAsync(1);
        server.ThrowIfFaulted();
        server.Transcript.AssertReceivedLinesInOrder("EHLO *", "MAIL FROM:*", "RCPT TO:*", "DATA", "QUIT");
    }

    [Fact]
    public async Task Conversation_MismatchIsReportedWithStep()
    {
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .Send("220 ready")
            .OnMismatch("500 Unrecognized")
            .Expect("EHLO *", "250 hi"));
        await using var client = await server.ConnectAsync();

        await client.ReadLineAsync();
        await client.WriteLineAsync("HELO old-client");
        Assert.Equal("500 Unrecognized", await client.ReadLineAsync());

        await server.WaitForErrorsAsync();
        var error = Assert.IsType<ConversationException>(Assert.Single(server.Errors));
        Assert.Contains("Step 2", error.Message);
        Assert.Contains("HELO old-client", error.Message);
    }

    [Fact]
    public async Task LineResponder_AnswersByRule()
    {
        await using var server = TcpServer.Listen(0, new LineResponder()
            .Greeting("+OK ready")
            .On("PING", "+PONG")
            .On(new Regex("^GET (\\w+)$"), m => $"$value-of-{m.Groups[1].Value}")
            .On(LineMatcher.Glob("set *", ignoreCase: true), "+OK")
            .OnAndClose("QUIT", "+OK bye")
            .Otherwise(line => $"-ERR unknown '{line}'"));
        await using var client = await server.ConnectAsync();

        Assert.Equal("+OK ready", await client.ReadLineAsync());
        await client.WriteLinesAsync(["PING", "GET key", "SET a 1", "FLY", "QUIT"]);
        Assert.Equal("+PONG", await client.ReadLineAsync());
        Assert.Equal("$value-of-key", await client.ReadLineAsync());
        Assert.Equal("+OK", await client.ReadLineAsync());
        Assert.Equal("-ERR unknown 'FLY'", await client.ReadLineAsync());
        Assert.Equal("+OK bye", await client.ReadLineAsync());
        Assert.Null(await client.ReadLineAsync());
    }

    [Fact]
    public async Task ImplicitTls_WithSelfSignedCertificate()
    {
        var tls = TlsOptions.SelfSigned();
        await using var server = TcpServer.Listen(0, Handler.Lines(l => $"secure {l}"), new ServerOptions { Tls = tls });

        // A real SslStream client trusting just this certificate.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port);
        await using var ssl = new SslStream(tcp.GetStream(), false, tls.TrustServerCertificate);
        await ssl.AuthenticateAsClientAsync("localhost");
        using var reader = new StreamReader(ssl);
        await ssl.WriteAsync("hello\r\n"u8.ToArray());
        Assert.Equal("secure hello", await reader.ReadLineAsync());

        // The transcript holds plaintext.
        await server.Transcript.WaitForReceivedLineAsync("hello");
        Assert.Contains(server.Transcript[0].Events, e => e.StartsWith("TLS established"));
    }

    [Fact]
    public async Task ConnectAsync_TrustsTheServersTlsCertificate()
    {
        await using var server = TcpServer.Listen(0, Handler.Echo, new ServerOptions { Tls = TlsOptions.SelfSigned() });
        await using var client = await server.ConnectAsync();
        Assert.True(client.IsTls);
        await client.WriteLineAsync("round trip");
        Assert.Equal("round trip", await client.ReadLineAsync());
    }

    [Fact]
    public async Task StartTls_UpgradesMidConversation()
    {
        var tls = TlsOptions.SelfSigned();
        await using var server = TcpServer.Listen(0, Conversation.Create()
            .Send("220 ready")
            .Expect("STARTTLS", "220 go ahead")
            .StartTls(tls)
            .Expect("EHLO *", "250 secure hello"));
        await using var client = await server.ConnectAsync();

        await client.ReadLineAsync();
        await client.WriteLineAsync("STARTTLS");
        Assert.Equal("220 go ahead", await client.ReadLineAsync());
        await client.UpgradeToTlsAsync(tls);
        await client.WriteLineAsync("EHLO me");
        Assert.Equal("250 secure hello", await client.ReadLineAsync());

        await server.Transcript.WaitForClosedConnectionsAsync(1);
        server.ThrowIfFaulted();
    }

    [Fact]
    public async Task UpgradeToTls_ChecksItsPreconditions()
    {
        var tls = TlsOptions.SelfSigned();
        Exception? noOptions = null, buffered = null, twice = null;
        await using var server = TcpServer.Listen(0, async connection =>
        {
            noOptions = await Record.ExceptionAsync(() => connection.UpgradeToTlsAsync()); // none given, none on the server
            var pending = await connection.Input.ReadAsync();
            connection.Input.AdvanceTo(pending.Buffer.Start); // leave the byte unread
            buffered = await Record.ExceptionAsync(() => connection.UpgradeToTlsAsync(tls));
            await connection.ReadExactlyAsync(1);
            await connection.WriteLineAsync("go");
            await connection.UpgradeToTlsAsync(tls);
            twice = await Record.ExceptionAsync(() => connection.UpgradeToTlsAsync(tls));
            await connection.WriteLineAsync("done");
        });
        await using var client = await server.ConnectAsync();

        await client.WriteAsync("x");
        Assert.Equal("go", await client.ReadLineAsync());
        await client.UpgradeToTlsAsync(tls);
        Assert.Equal("done", await client.ReadLineAsync());

        Assert.Contains("No TlsOptions", Assert.IsType<InvalidOperationException>(noOptions).Message);
        Assert.Contains("1 unread bytes", Assert.IsType<InvalidOperationException>(buffered).Message);
        Assert.Contains("already using TLS", Assert.IsType<InvalidOperationException>(twice).Message);
        server.ThrowIfFaulted();
    }

    [Fact]
    public void LineMatcher_ExactTreatsWildcardsAsText()
    {
        var matcher = LineMatcher.Exact("GET *");

        Assert.True(matcher.IsMatch("GET *"));
        Assert.False(matcher.IsMatch("GET /index.html"));
        Assert.False(matcher.IsMatch("get *"));
        Assert.False(matcher.IsMatch("GET * "));
        Assert.True(LineMatcher.Exact("get ?", StringComparison.OrdinalIgnoreCase).IsMatch("GET ?"));
        Assert.False(LineMatcher.Exact("get ?", StringComparison.OrdinalIgnoreCase).IsMatch("GET x"));
    }

    [Fact]
    public void LineMatcher_WhereAndAny()
    {
        var digits = LineMatcher.Where(line => line.Length > 0 && line.All(char.IsAsciiDigit), "digits");

        Assert.True(digits.IsMatch("2024"));
        Assert.False(digits.IsMatch("20x4"));
        Assert.Equal("digits", digits.ToString());
        Assert.True(LineMatcher.Any.IsMatch(""));
        Assert.True(LineMatcher.Any.IsMatch("anything at all *"));
    }

    [Fact]
    public async Task UnixSocket_UsesTemporaryPathAndCleansUp()
    {
        string path;
        await using (var server = UnixServer.Listen(null, Handler.Lines(l => $"unix {l}")))
        {
            path = server.Path;
            Assert.True(File.Exists(path));

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            await using var stream = new NetworkStream(socket);
            using var reader = new StreamReader(stream);
            await stream.WriteAsync("hi\n"u8.ToArray());
            Assert.Equal("unix hi", await reader.ReadLineAsync());
        }
        Assert.False(File.Exists(path));
    }
}
