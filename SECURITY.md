# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub Security Advisories:
<https://github.com/jeffadickson/Intertube.Stub/security/advisories/new>

Don't open a public issue for a security problem. Include the version, the target framework, and a
minimal reproduction if you can.

## Scope

Intertube.Stub is a **test double**: a set of embedded servers meant to run inside a test process,
on loopback, for the lifetime of a test. It is not a production server and is not hardened like
one. The SSH/SFTP and SMTP servers have no rate limiting, no privilege separation, and the SSH
code's Ed25519 verification is not constant-time.

Reports are in scope when they affect that intended use, for example:

- a client under test being able to crash or hang the test host in a way the library should prevent
- the library reading or writing outside the root of a `DirectorySftpFileSystem`
- a default that exposes a server beyond loopback

Weaknesses that only matter for an internet-facing production deployment (denial of service under
load, timing side channels and so on) are generally out of scope.

## Keep the servers on loopback

Every server binds `127.0.0.1` by default. **Never bind a non-loopback address** (`IPAddress.Any`,
a LAN address, `0.0.0.0`, `::`) on a machine reachable by anyone you don't trust.

This matters most for `SftpServer`: with no `Users` configured it **accepts any login**, and with a
`DirectorySftpFileSystem` it serves a real directory. Together, on a non-loopback address, that
gives anyone who can reach the port read and write access to that directory. The same caution
applies to `SmtpServer` and to any `TcpServer` handler that touches the file system or other
resources.
