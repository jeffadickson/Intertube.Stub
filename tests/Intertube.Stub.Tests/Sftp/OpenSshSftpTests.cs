using System.Security.Cryptography;
using Intertube.Stub.Sftp;
using Xunit.Abstractions;

namespace Intertube.Stub.Tests.Sftp;

/// <summary>
/// The server against the real OpenSSH client. These are the scenarios of CodeCourier's paramiko
/// fixture (ed25519 key login, pinned host key, a write that fails once, wrong user, wrong host key)
/// plus what paramiko could not easily do: every cipher and key exchange, forced rekeying, and
/// faults below and inside the SFTP layer.
/// </summary>
public sealed class OpenSshSftpTests(ITestOutputHelper output) : IAsyncLifetime
{
    private OpenSsh _client = null!;

    public async Task InitializeAsync() => _client = await OpenSsh.CreateAsync();

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private SftpServer Start(ISftpFileSystem? files = null, ServerOptions? server = null) => SftpServer.Start(new SftpServerOptions
    {
        FileSystem = files,
        Users = [SftpUser.WithKey("test", _client.PublicKey)],
        Server = (server ?? new ServerOptions { RecordTranscript = false }) with { Log = line => output.WriteLine(line) },
    });

    private async Task<string> SucceedAsync(SftpServer server, string batch, params string[] options)
    {
        var (exit, text) = await _client.SftpAsync(server, batch, options: options);
        output.WriteLine(text);
        Assert.True(exit == 0, $"sftp exited with {exit}:\n{text}");
        server.ThrowIfFaulted();
        return text;
    }

    [OpenSshFact]
    public async Task PutAndGet_RoundTripThroughMemory()
    {
        var files = new InMemorySftpFileSystem().AddDirectory("/incoming");
        await using var server = Start(files);
        var payload = RandomNumberGenerator.GetBytes(100_000);
        await File.WriteAllBytesAsync(_client.PathOf("payload.bin"), payload);

        var text = await SucceedAsync(server, """
            put payload.bin /incoming/payload.bin
            get /incoming/payload.bin back.bin
            ls -l /incoming
            """);

        Assert.Equal(payload, files.ReadAllBytes("/incoming/payload.bin"));
        Assert.Equal(payload, await File.ReadAllBytesAsync(_client.PathOf("back.bin")));
        Assert.Contains("-rw-r--r--", text);
        Assert.Contains("payload.bin", text);
        var login = Assert.Single(server.AuthAttempts, a => a.Succeeded);
        Assert.Equal(("test", "publickey", _client.PublicKey.Fingerprint), (login.User, login.Method, login.KeyFingerprint));
        Assert.NotEmpty(server.RequestsOf(SftpOperation.Write, "/incoming/payload.bin"));
        Assert.NotEmpty(server.RequestsOf(SftpOperation.Read, "/incoming/payload.bin"));
        server.Tcp.Transcript.AssertReceivedMessagesInOrder("AUTH publickey user=test * accepted", "OPEN /incoming/payload.bin flags=0x1a", "WRITE /incoming/payload.bin offset=0 *", "CLOSE /incoming/payload.bin");
        server.Tcp.Transcript.AssertSentMessage("DATA * bytes");
    }

    /// <summary>The Python fixture's "fail-once" marker file, as one call.</summary>
    [OpenSshFact]
    public async Task FailNext_FailsOneUpload_ThenTheRetrySucceeds()
    {
        var files = new InMemorySftpFileSystem().AddDirectory("/incoming");
        await using var server = Start(files);
        await File.WriteAllTextAsync(_client.PathOf("note.txt"), "hello");
        server.FailNext(SftpOperation.Write, "/incoming/*");

        var (exit, text) = await _client.SftpAsync(server, "put note.txt /incoming/note.txt");
        Assert.NotEqual(0, exit);
        Assert.Contains("Failure", text);
        server.Tcp.Transcript.AssertSentMessage("STATUS Failure Injected failure*");

        await SucceedAsync(server, "put note.txt /incoming/note.txt");
        Assert.Equal("hello", files.ReadAllText("/incoming/note.txt"));
        Assert.Equal(2, server.RequestsOf(SftpOperation.Write).Count);
    }

    [OpenSshFact]
    public async Task UnknownUser_IsRejected()
    {
        await using var server = Start();

        var (exit, text) = await _client.SftpAsync(server, "ls", user: "wrong");

        Assert.NotEqual(0, exit);
        Assert.Contains("Permission denied", text);
        Assert.All(server.AuthAttempts, a => Assert.False(a.Succeeded));
        Assert.Contains(server.AuthAttempts, a => a.User == "wrong");
        Assert.Empty(server.Requests);
    }

    [OpenSshFact]
    public async Task UnauthorizedKey_IsRejected()
    {
        await using var server = SftpServer.Start(new SftpServerOptions { Users = [SftpUser.WithKey("test", SshKeyPair.CreateEcdsa().PublicKey)] });

        var (exit, _) = await _client.SftpAsync(server, "ls");

        Assert.NotEqual(0, exit);
        Assert.Empty(server.Requests);
    }

    [OpenSshFact]
    public async Task PinnedHostKey_Mismatch_IsRefusedByTheClient()
    {
        await using var server = Start();
        using var impostor = SshKeyPair.CreateEcdsa();

        var (exit, text) = await _client.SftpAsync(server.Port, $"[127.0.0.1]:{server.Port} {impostor.PublicKey}", "ls");

        Assert.NotEqual(0, exit);
        Assert.Contains("HOST IDENTIFICATION HAS CHANGED", text);
        Assert.Empty(server.AuthAttempts);
    }

    [OpenSshTheory]
    [InlineData("aes128-gcm@openssh.com", "hmac-sha2-256", "ecdh-sha2-nistp256")]
    [InlineData("aes256-gcm@openssh.com", "hmac-sha2-256", "ecdh-sha2-nistp384")]
    [InlineData("aes128-ctr", "hmac-sha2-256", "ecdh-sha2-nistp521")]
    [InlineData("aes192-ctr", "hmac-sha2-512", "ecdh-sha2-nistp256")]
    [InlineData("aes256-ctr", "hmac-sha2-256-etm@openssh.com", "ecdh-sha2-nistp256")]
    [InlineData("aes256-ctr", "hmac-sha2-512-etm@openssh.com", "ecdh-sha2-nistp384")]
    public async Task EveryCipherMacAndKeyExchange_Works(string cipher, string mac, string kex)
    {
        var files = new InMemorySftpFileSystem();
        await using var server = Start(files);
        var payload = RandomNumberGenerator.GetBytes(70_000);
        await File.WriteAllBytesAsync(_client.PathOf("payload.bin"), payload);

        await SucceedAsync(server, "put payload.bin\nget payload.bin back.bin", "-c", cipher, "-o", "MACs=" + mac, "-o", "KexAlgorithms=" + kex);

        Assert.Equal(payload, files.ReadAllBytes("/payload.bin"));
        Assert.Equal(payload, await File.ReadAllBytesAsync(_client.PathOf("back.bin")));
    }

    /// <summary>A 4 MB transfer with a rekey every 256 KB, over a connection that dribbles out 1000-byte fragments.</summary>
    [OpenSshFact]
    public async Task LargeTransfer_SurvivesRekeyingAndFragmentation()
    {
        var files = new InMemorySftpFileSystem();
        await using var server = Start(files, new ServerOptions { RecordTranscript = false, Faults = FaultOptions.Fragment(1000) });
        var payload = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        await File.WriteAllBytesAsync(_client.PathOf("big.bin"), payload);

        await SucceedAsync(server, "put big.bin\nget big.bin back.bin", "-o", "RekeyLimit=256K");

        Assert.Equal(payload, files.ReadAllBytes("/big.bin"));
        Assert.Equal(payload, await File.ReadAllBytesAsync(_client.PathOf("back.bin")));
    }

    [OpenSshFact]
    public async Task DirectoryFileSystem_UploadThenAtomicRename_LikeCodeCourier()
    {
        var root = _client.PathOf("remote");
        Directory.CreateDirectory(Path.Combine(root, "outgoing"));
        await File.WriteAllTextAsync(Path.Combine(root, "outgoing", "old.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(root, "outgoing", "packet"), "previous");
        await File.WriteAllTextAsync(_client.PathOf("packet"), "new contents");
        await using var server = Start(new DirectorySftpFileSystem(root));

        var text = await SucceedAsync(server, """
            mkdir incoming
            put packet incoming/packet.partial
            rename incoming/packet.partial incoming/packet
            rename outgoing/packet incoming/packet
            ls incoming
            rm outgoing/old.txt
            rmdir outgoing
            """);

        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(root, "incoming", "packet")));
        Assert.False(File.Exists(Path.Combine(root, "incoming", "packet.partial")));
        Assert.False(Directory.Exists(Path.Combine(root, "outgoing")));
        Assert.Contains(server.Requests, r => r is { Operation: SftpOperation.PosixRename, Path: "/incoming/packet.partial", TargetPath: "/incoming/packet" });
    }

    [OpenSshFact]
    public async Task PathsOutsideTheRoot_AreClampedToIt()
    {
        var root = _client.PathOf("jail");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(_client.PathOf("secret.txt"), "outside");
        await using var server = Start(new DirectorySftpFileSystem(root));

        var (exit, _) = await _client.SftpAsync(server, "get ../secret.txt stolen.txt");

        Assert.NotEqual(0, exit);
        Assert.False(File.Exists(_client.PathOf("stolen.txt")));
        Assert.Contains(server.Requests, r => r.Path == "/secret.txt");
    }

    [OpenSshFact]
    public async Task ConnectionDroppedMidUpload_ClientFails_ServerStaysHealthy()
    {
        var files = new InMemorySftpFileSystem();
        await using var server = Start(files);
        await File.WriteAllBytesAsync(_client.PathOf("big.bin"), RandomNumberGenerator.GetBytes(1024 * 1024));
        var writes = 0;
        server.Intercept = request =>
        {
            if (request.Operation == SftpOperation.Write && Interlocked.Increment(ref writes) == 5) request.Connection.Abort();
            return ValueTask.FromResult<SftpStatus?>(null);
        };

        int exit;
        try
        {
            // OpenSSH 10 sends the file in 255 KB writes, so the abort can land after the last one, with
            // the client idle and waiting for replies. On a busy macOS machine the client was seen to
            // ignore the server's (valid) reset in that state; a keepalive makes it send something
            // and get reset again, rather than wait forever.
            (exit, _) = await _client.SftpAsync(server, "put big.bin", "test", "-o", "ServerAliveInterval=1", "-o", "ServerAliveCountMax=3");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}\nServer transcript:\n{server.Tcp.Transcript}", ex);
        }

        Assert.NotEqual(0, exit);
        Assert.True(files.ReadAllBytes("/big.bin").Length < 1024 * 1024);
        Assert.Empty(server.Errors);
    }

    [OpenSshFact]
    public async Task SlowServer_InterceptCanDelayRequests()
    {
        await using var server = Start(new InMemorySftpFileSystem().AddFile("/a.txt", "a"));
        server.Intercept = async request =>
        {
            if (request.Operation == SftpOperation.Open) await Task.Delay(300);
            return null;
        };

        var started = DateTime.UtcNow;
        await SucceedAsync(server, "get a.txt");

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(300));
    }

    [OpenSshFact]
    public async Task PermissionDenied_FromFailAlways_IsReportedPerFile()
    {
        var files = new InMemorySftpFileSystem().AddFile("/ok.txt", "ok").AddFile("/locked/secret.txt", "no");
        await using var server = Start(files);
        server.FailAlways(SftpOperation.Open, "/locked/*");

        var text = await SucceedAsync(server, "get ok.txt\n-get locked/secret.txt");

        Assert.Contains("Permission denied", text);
        Assert.True(File.Exists(_client.PathOf("ok.txt")));
        Assert.False(File.Exists(_client.PathOf("secret.txt")));
    }

    [OpenSshFact]
    public async Task EcdsaIdentity_MadeWithoutSshKeygen()
    {
        using var key = SshKeyPair.CreateEcdsa();
        key.WritePrivateKey(_client.PathOf("id_ecdsa"));
        _client.Identity = _client.PathOf("id_ecdsa");
        await using var server = SftpServer.Start(new SftpServerOptions
        {
            Users = [SftpUser.WithKey("test", key.PublicKey)],
            FileSystem = new InMemorySftpFileSystem().AddFile("/hello.txt", "hi"),
        });

        await SucceedAsync(server, "get hello.txt");

        Assert.Equal("hi", await File.ReadAllTextAsync(_client.PathOf("hello.txt")));
    }

    [OpenSshFact]
    public async Task RsaHostKey_IsAccepted()
    {
        using var hostKey = SshKeyPair.CreateRsa(2048);
        await using var server = SftpServer.Start(new SftpServerOptions { HostKey = hostKey, Users = [SftpUser.WithKey("test", _client.PublicKey)] });

        await SucceedAsync(server, "ls");

        Assert.Equal("ssh-rsa", server.HostKey.Algorithm);
    }
}
