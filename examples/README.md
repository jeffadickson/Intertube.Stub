# Intertube.Stub examples

Each file contains a small, realistic **system under test** and the tests that mock its network
dependency with Intertube.Stub. The systems under test use ordinary .NET networking (HttpClient,
SmtpClient, MailKit, FluentFTP, SSH.NET, TcpClient, UdpClient, SslStream) and know nothing about Intertube.Stub.

| Example | Mocked dependency | Intertube.Stub features shown |
|---|---|---|
| [HttpApiExample](Intertube.Stub.Examples/HttpApiExample.cs) | REST API behind `HttpClient` | request assertions, 503 then retry, truncated body, garbage status line, `Handler.Hang` vs `HttpClient.Timeout`, byte-at-a-time responses |
| [DockerUnixSocketExample](Intertube.Stub.Examples/DockerUnixSocketExample.cs) | Docker Engine API on a Unix socket | `UnixServer` with a temporary path, HTTP over `SocketsHttpHandler.ConnectCallback` |
| [SmtpExample](Intertube.Stub.Examples/SmtpExample.cs) | SMTP server for the BCL's `SmtpClient` | `Conversation` scripts, multi-line replies, `ExpectUntil("\r\n.\r\n")`, rejected recipient, greylisting | OrderNotifier (MailKit) against the library's `SmtpServer`: STARTTLS required, AUTH, the attachment parsed with MimeKit, greylisting retried, a connection dropped before acceptance, permanent rejection not retried |
| [UdpTelemetryExample](Intertube.Stub.Examples/UdpTelemetryExample.cs) | syslog collector and StatsD | `UdpServer`, RFC 5424 format assertion, seeded packet loss, absent collector |
| [InstrumentExample](Intertube.Stub.Examples/InstrumentExample.cs) | SCPI lab multimeter | `LineResponder`, `NewLine = "\n"`, live log to `ITestOutputHelper`, a **fault matrix** theory |
| [ResilienceExamples](Intertube.Stub.Examples/ResilienceExamples.cs) | quote service, TLS banner, binary RPC | reset-by-connection-id retries, certificate pinning against `TlsOptions.SelfSigned`, a length-prefixed protocol parsed with `ReadExactlyAsync`, `TransformWrite` corrupting a length prefix |
| [MemcachedExample](Intertube.Stub.Examples/MemcachedExample.cs) | memcached text protocol | a working fake with mixed line and exact-length framing, a **golden transcript** |
| [SftpExample](Intertube.Stub.Examples/SftpExample.cs) | partner SFTP drop box behind SSH.NET | `SftpServer` with an in-memory file system, a key pair made on the fly, host key pinning, `FailNext`/`FailAlways`, a connection cut mid-upload, a hung read against `OperationTimeout`, and checks on partial-then-rename ordering |
| [FtpExample](Intertube.Stub.Examples/FtpExample.cs) | bank FTPS server behind FluentFTP | a fake FTP server over the SFTP in-memory file system, explicit TLS on control **and** data connections, one Intertube.Stub listener per passive data connection, `DataFaults` breaking a download, a server that silently truncates uploads caught by a SIZE check, no TLS downgrade, certificate pinning |
| [ImapExample](Intertube.Stub.Examples/ImapExample.cs) | IMAPS inbox behind MailKit's `ImapClient` | a fake IMAP4rev1 server (LOGIN, SELECT, UID SEARCH/FETCH/STORE, literals), at-least-once processing when the connection drops mid-FETCH or the handler fails, `NO [INUSE]` and `BYE`, the password kept out of the transcript |
| [Hl7MllpExample](Intertube.Stub.Examples/Hl7MllpExample.cs) | HL7 v2 receiver over MLLP | `ReadUntilAsync` on the MLLP trailer, ACK codes AA/AE/AR, lost ACKs, ACKs for the wrong control ID, byte-at-a-time ACKs, assertions on decoded transcript messages |
| [DnsExample](Intertube.Stub.Examples/DnsExample.cs) | authoritative name server | UDP and TCP on the same port, truncation (TC) forcing a TCP retry, CNAME chains, NXDOMAIN, packet loss, SERVFAIL and a silent primary failing over to a secondary, a spoofed reply with the wrong ID ignored |
| [ProxyExample](Intertube.Stub.Examples/ProxyExample.cs) | SOCKS5 and HTTP CONNECT proxies for `HttpClient` | a relaying fake proxy, username/password auth and 407 challenges, refused and unreachable destinations, a tunnel cut mid-response, the proxy's view of each CONNECT as transcript messages |

## F#

[Intertube.Stub.FSharp](Intertube.Stub.FSharp) uses the library from F#: a config-service client written with
`task { }` and tested with a `LineResponder` rule table, a `Conversation` script, a reset on the
first connection, a hung service, a reply cut mid-line (which caught a real bug: `StreamReader`
hands back a partial line at end of stream), the ready-made `SmtpServer`, and an FsCheck property
that round-trips random values through a working fake.

For a complete application tested this way, with FsCheck, Hedgehog and Stryker, see [`samples/`](../samples/README.md).

```
dotnet test examples/Intertube.Stub.Examples
```
