# Intertube.Stub

[![CI](https://github.com/jeffadickson/Intertube.Stub/actions/workflows/ci.yml/badge.svg)](https://github.com/jeffadickson/Intertube.Stub/actions/workflows/ci.yml)

Tiny embedded servers for tests: think **WireMock for arbitrary TCP protocols**. It's inspired by
Python's `socketserver`: bind a port, write a handler, and let the library deal with the accept
loop and shutdown. Intertube.Stub adds the things test doubles need, such as transcripts, assertions,
waiting helpers and fault injection.

```csharp
await using var server = TcpServer.Listen(0, async connection =>
{
    var request = await connection.ReadLineAsync();
    await connection.WriteLineAsync($"Received: {request}");
});

Console.WriteLine(server.Port);   // ephemeral port on 127.0.0.1
```

- **Transports:** TCP, UDP, Unix domain sockets, implicit TLS and STARTTLS (`UpgradeToTlsAsync`), with self-signed certificates generated on the fly
- **Framing helpers that don't impose a format:** `ReadLineAsync`, `ReadLinesAsync` (`await foreach`), `ReadUntilAsync(delimiter)`, `ReadExactlyAsync(n)`, `ReadToEndAsync`. All of them share one `PipeReader` buffer, so you can mix them freely, and you can also drop down to `connection.Input` (`PipeReader`), `connection.Output` (`PipeWriter`) or `connection.Stream`.
- **Lifecycle:** `await using`. Shutdown cancels `connection.CancellationToken`, gives handlers a grace period and then resets any connections that are still open.
- **Concurrency:** concurrent by default; use `ServerOptions.Sequential` or `MaxConcurrency = n` to limit it.
- **Transcripts and assertions:** every byte is recorded per connection (TLS traffic is recorded as plaintext). Assertions and wait helpers throw with the full transcript in the message.
- **Fault injection:** latency, connect delay, fragmentation, disconnect after N bytes (RST or FIN), deterministic corruption, hanging and refusal.
- **Protocol helpers:** `Conversation` for scripted, in-order exchanges and `LineResponder` for rule tables.
- **Ready-made servers:** SFTP (`SftpServer`, over SSH) and SMTP (`SmtpServer`, with STARTTLS and AUTH), each capturing what clients send.

## Install

```
dotnet add package Intertube.Stub --prerelease
```

The core servers live in the `Intertube.Stub` namespace; the ready-made SMTP and SFTP servers are in
`Intertube.Stub.Smtp` and `Intertube.Stub.Sftp`. The package targets net8.0 and net10.0.

## Transcripts and waiting

```csharp
await using var server = TcpServer.Listen(0, Handler.Lines(line => line == "PING" ? "PONG" : null));

// ... exercise the client under test ...

await server.Transcript.WaitForReceivedLineAsync("PING");          // default timeout 5s
server.Transcript.AssertReceivedLinesInOrder("HELLO *", "PING");   // glob patterns, Regex, or LineMatcher.Where(...)
server.Transcript.AssertConnectionCount(1);
server.ThrowIfFaulted();                                           // surface handler exceptions
Console.WriteLine(server.Transcript);                              // #1 <- PING / #1 -> PONG ...
```

`ServerOptions.Log` streams the same traffic live, for example to xUnit's `ITestOutputHelper.WriteLine`.

A fake that understands its protocol can also record **messages**: decoded, protocol-level entries
next to the raw bytes. They are kept even with `RecordTranscript = false`, they print as `<=`/`=>`
in dumps, and they have their own assertions:

```csharp
connection.Transcript.RecordMessage(TranscriptDirection.Received, $"ADT^A01 {controlId}");
server.Transcript.AssertReceivedMessagesInOrder("ADT^A01 1001", "ADT^A01 1002");
server.Transcript.AssertSentMessage("ACK AA 1002");
await server.Transcript.WaitForReceivedMessageAsync("AUTH * accepted");
```

`connection.IsTerminated` and the `connection.Terminated` token tell a handler that this side has
closed or reset the connection (for example from an interceptor calling `Abort()`), so work that
was already buffered is not applied after the cut. A close by the peer does not count.

## Scripted conversations

```csharp
await using var smtp = TcpServer.Listen(0, Conversation.Create()
    .Send("220 mail.test ESMTP")
    .Expect("EHLO *", "250 mail.test")
    .Expect("STARTTLS", "220 go ahead").StartTls(TlsOptions.SelfSigned())
    .Expect("MAIL FROM:<*>", "250 OK")
    .Expect("DATA", "354 go on")
    .ExpectUntil("\r\n.\r\n", body => Assert.Contains("Subject:", body))
    .Send("250 Queued")
    .Expect("QUIT", "221 Bye")
    .OnMismatch("500 Unrecognized"));
```

A client that strays from the script fails the handler with a `ConversationException`. The exception names the step that failed and shows up in `server.Errors`.

## Rule tables

```csharp
await using var redis = TcpServer.Listen(0, new LineResponder()
    .On("PING", "+PONG")
    .On(new Regex(@"^GET (\w+)$"), m => $"$value-of-{m.Groups[1].Value}")
    .OnAndClose("QUIT", "+OK")
    .Otherwise(line => $"-ERR unknown '{line}'"));
```

## Faults

```csharp
var options = new ServerOptions { Faults = FaultOptions.Fragment(1, TimeSpan.FromMilliseconds(5)) };   // byte-at-a-time
var options = new ServerOptions { Faults = FaultOptions.DisconnectAfter(100) };                        // RST mid-response
var options = new ServerOptions { Faults = FaultOptions.Corrupt(0.01, seed: 1) with { WriteDelay = TimeSpan.FromMilliseconds(50) } };

TcpServer.Listen(0, Handler.Hang);    // accept, never answer: client timeout tests
TcpServer.Listen(0, Handler.Reset);   // accept, then reset
TcpServer.Listen(0, Handler.Send("HTTP/1.1 200 OK\r\nContent-Length: 99\r\n\r\nshort"));  // malformed

// Or change the faults mid-connection:
connection.Faults = FaultOptions.Latency(TimeSpan.FromSeconds(2));
```

Note that after a reset (RST), the client's OS may discard data that the client hadn't read yet; Windows does. If a test needs to check exactly what was sent before the disconnect, use `DisconnectMode.Close`, or check the server transcript.

## UDP

```csharp
await using var server = UdpServer.Listen(0, d => d.ReplyAsync($"ack {d.Text}").AsTask(),
    new UdpServerOptions { DropRate = 0.2, Seed = 42 });   // reproducible packet loss
```

Received datagrams go through a `Channel<T>` that is drained by `Parallel.ForEachAsync`. Handlers run one at a time, in arrival order, by default; set `MaxConcurrency` to run them in parallel.

## Unix sockets

```csharp
await using var server = UnixServer.Listen(null, handler);   // null: temporary socket file, deleted on dispose
```

## SFTP

`Intertube.Stub.Sftp` is a real SFTP server for tests. It does a full SSH handshake with no extra
dependencies and serves an in-memory file system or a real directory:

```csharp
var files = new InMemorySftpFileSystem().AddDirectory("/incoming");
await using var sftp = SftpServer.Start(new SftpServerOptions
{
    FileSystem = files,                                                   // or new DirectorySftpFileSystem(path)
    Users = [SftpUser.WithKey("test", File.ReadAllText("id_ed25519.pub")), SftpUser.WithPassword("bob", "pw")],
});
File.WriteAllText("known_hosts", sftp.KnownHostsLine);                   // [127.0.0.1]:port ecdsa-sha2-nistp256 AAAA...

sftp.FailNext(SftpOperation.Write, "/incoming/*");                       // one failed upload, then normal service
sftp.FailAlways(SftpOperation.Open, "/locked/*", SftpStatus.PermissionDenied);
sftp.Intercept = async r =>                                              // or anything else, per request
{
    if (r is { Operation: SftpOperation.Write, ConnectionId: 1, Offset: > 0 }) r.Connection.Abort();
    return null;
};

// ... run the code under test ...
Assert.Equal("payload", files.ReadAllText("/incoming/report.csv"));
Assert.Single(sftp.RequestsOf(SftpOperation.PosixRename));
Assert.All(sftp.AuthAttempts, a => Assert.True(a.Succeeded));
```

- **SSH:**
  - key exchange: ecdh-sha2-nistp256/384/521
  - ciphers: aes128/256-gcm@openssh.com, and aes128/192/256-ctr with hmac-sha2-256/512 (plain or `-etm`)
  - extras: rekeying, OpenSSH strict key exchange and `server-sig-algs`
- **Logins:** public keys (ssh-ed25519, ECDSA and RSA) and passwords. With no `Users` configured, any login is accepted.
- **Host keys:** a fresh ECDSA key by default. For a fixed key, pass an `SshKeyPair` (ECDSA or RSA).
- **SFTP:** version 3 with posix-rename, fsync and limits, which is what OpenSSH, SSH.NET and paramiko use.
- **Faults:** `Server = new ServerOptions { Faults = ... }` applies transport faults to the encrypted stream.
- **Transcript:** each login, request and reply is recorded as a transcript message (`"OPEN /in/a.bin flags=0x1a"`, `"STATUS Failure ..."`), so `AssertReceivedMessagesInOrder` works on SFTP operations.
- **Client keys:** `SshKeyPair.CreateEcdsa()` gives a test a client key without running `ssh-keygen`.

It is tested against OpenSSH 9.5 and 10.6 (`tests/.../Sftp`) and SSH.NET (`examples/.../SftpExample.cs`).
It is not a production server: it has no rate limiting, it does no privilege separation, and its
Ed25519 verification is not constant-time.

> **Keep it on loopback.** `SftpServer` binds `127.0.0.1` by default; don't change `Address` to a
> non-loopback address. With no `Users` configured it accepts any login, and with a
> `DirectorySftpFileSystem` that means anyone who can reach the port can read and write that
> directory.

## SMTP

`Intertube.Stub.Smtp.SmtpServer` accepts any valid SMTP dialogue and captures the mail, so tests
assert on the message rather than on the client's exact command sequence:

```csharp
await using var smtp = SmtpServer.Start(new SmtpServerOptions
{
    Tls = TlsOptions.SelfSigned(), RequireTls = true,                     // STARTTLS; or ImplicitTls = true
    Users = new Dictionary<string, string> { ["shop"] = "s3cret" },      // AUTH PLAIN and LOGIN
});

smtp.FailNext("RCPT TO:*", "451 4.7.1 Greylisted, try again later");    // one transient failure
smtp.FailAlways("RCPT TO:<*@nowhere.test>*", "550 5.1.1 No such user");
smtp.Intercept = c => { if (c.Verb == ".") c.Connection.Abort(); return default; };   // vanish before accepting

var mail = await smtp.WaitForMessageAsync(m => m.Recipients.Contains("ada@example.test"));
Assert.True(mail.Tls);
Assert.Equal("Order 42 confirmed", mail.Subject);                      // RFC 2047 decoded
Assert.Contains("on its way", mail.TextBody);                          // quoted-printable/base64 and multipart handled
```

It advertises SIZE, PIPELINING, 8BITMIME, SMTPUTF8 and ENHANCEDSTATUSCODES, enforces
`MaxMessageSize`, undoes dot-stuffing, and records `MESSAGE from -> rcpts: subject` and
`AUTH mech user=x accepted` as transcript messages. For MIME parts deeper than the text body,
hand `mail.Data` to MimeKit.

## Layout

- `src/Intertube.Stub` contains the library (net8.0 and net10.0)
- `tests/Intertube.Stub.Tests` contains the xUnit tests
- `examples/Intertube.Stub.FSharp` shows the same from F#, with `task { }` and an FsCheck property
- `examples/` contains realistic systems under test (HttpClient, SmtpClient, MailKit SMTP and IMAP, FluentFTP, SSH.NET SFTP, HL7 MLLP, DNS, SOCKS5/HTTP proxies, Docker over a Unix socket, syslog/StatsD, SCPI, TLS pinning, binary RPC, memcached), each mocked with Intertube.Stub; see [examples/README.md](https://github.com/jeffadickson/Intertube.Stub/blob/main/examples/README.md)
- `samples/` contains RedisLite, a complete Redis client and CLI tested with Intertube.Stub, FsCheck, Hedgehog and Stryker; see [samples/README.md](https://github.com/jeffadickson/Intertube.Stub/blob/main/samples/README.md)

```
dotnet test
```

## License

MIT. See [LICENSE](https://github.com/jeffadickson/Intertube.Stub/blob/main/LICENSE).
For reporting vulnerabilities, see [SECURITY.md](https://github.com/jeffadickson/Intertube.Stub/blob/main/SECURITY.md);
for building and testing, see [CONTRIBUTING.md](https://github.com/jeffadickson/Intertube.Stub/blob/main/CONTRIBUTING.md).
