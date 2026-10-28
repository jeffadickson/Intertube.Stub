using System.Text;
using Intertube.Stub.Sftp;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

/// <summary>Where to publish, and the host key fingerprint (<c>SHA256:...</c>) to insist on.</summary>
public sealed record SftpTarget(string Host, int Port, string User, string PrivateKeyPem, string HostKeyFingerprint)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Publishes reports to a partner's SFTP drop box with SSH.NET: upload to <c>name.partial</c>, then
/// rename atomically, so the partner never picks up half a file. Transient failures are retried on a
/// fresh connection; permission problems and an unexpected host key are not.
/// </summary>
public sealed class ReportPublisher(SftpTarget target, int maxAttempts = 3)
{
    public async Task PublishAsync(string name, byte[] content, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = await ConnectAsync(cancellationToken);
                var partial = $"/outgoing/{name}.partial";
                using (var stream = new MemoryStream(content)) await client.UploadFileAsync(stream, partial, cancellationToken);
                client.RenameFile(partial, $"/outgoing/{name}", isPosix: true);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>Download every file in <c>/inbox</c>, deleting each once it is safely read.</summary>
    public async Task<IReadOnlyDictionary<string, string>> CollectInboxAsync(CancellationToken cancellationToken = default)
    {
        using var client = await ConnectAsync(cancellationToken);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await foreach (var file in client.ListDirectoryAsync("/inbox", cancellationToken))
        {
            if (!file.IsRegularFile) continue;
            using var buffer = new MemoryStream();
            await client.DownloadFileAsync(file.FullName, buffer, cancellationToken);
            result[file.Name] = Encoding.UTF8.GetString(buffer.ToArray());
            await client.DeleteFileAsync(file.FullName, cancellationToken);
        }
        return result;
    }

    private async Task<SftpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        using var key = new PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(target.PrivateKeyPem)));
        var info = new ConnectionInfo(target.Host, target.Port, target.User, new PrivateKeyAuthenticationMethod(target.User, key)) { Timeout = target.Timeout };
        var client = new SftpClient(info) { OperationTimeout = target.Timeout };
        client.HostKeyReceived += (_, e) => e.CanTrust = "SHA256:" + e.FingerPrintSHA256 == target.HostKeyFingerprint;
        try
        {
            await client.ConnectAsync(cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        // SSH.NET reports a host key we refused as a failed key exchange: retrying cannot help.
        SshConnectionException { DisconnectReason: DisconnectReason.HostKeyNotVerifiable or DisconnectReason.KeyExchangeFailed } => false,
        SshAuthenticationException or SftpPermissionDeniedException or SftpPathNotFoundException => false,
        SshException or IOException or System.Net.Sockets.SocketException => true,
        _ => false,
    };
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// SSH.NET against Intertube.Stub's SFTP server: a real SSH handshake on an ephemeral port, an
/// in-memory file system the test seeds and inspects, and failures injected at exactly the
/// request where they hurt. The paramiko equivalent needs a Python process and a marker file per fault.
/// </summary>
public sealed class SftpExample : IAsyncDisposable
{
    private readonly SshKeyPair _clientKey = SshKeyPair.CreateEcdsa();
    private readonly InMemorySftpFileSystem _files = new InMemorySftpFileSystem().AddDirectory("/outgoing").AddDirectory("/inbox");
    private readonly SftpServer _server;

    public SftpExample() => _server = SftpServer.Start(new SftpServerOptions
    {
        FileSystem = _files,
        Users = [SftpUser.WithKey("partner", _clientKey.PublicKey)],
    });

    private SftpTarget Target => new(_server.Host, _server.Port, "partner", _clientKey.PrivateKeyPem, _server.HostKey.Fingerprint);

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _clientKey.Dispose();
    }

    [Fact]
    public async Task Publishes_ViaPartialFileThenAtomicRename()
    {
        await new ReportPublisher(Target).PublishAsync("daily.csv", "id,total\n1,42\n"u8.ToArray());

        Assert.Equal("id,total\n1,42\n", _files.ReadAllText("/outgoing/daily.csv"));
        Assert.Equal(["/outgoing/daily.csv"], _files.AllFiles);
        var writes = _server.RequestsOf(SftpOperation.Write);
        Assert.All(writes, w => Assert.Equal("/outgoing/daily.csv.partial", w.Path));
        var rename = Assert.Single(_server.RequestsOf(SftpOperation.PosixRename));
        Assert.Equal("/outgoing/daily.csv", rename.TargetPath);
        Assert.True(_server.Requests.ToList().IndexOf(rename) > _server.Requests.ToList().IndexOf(writes[^1]));
        _server.ThrowIfFaulted();
    }

    [Fact]
    public async Task FailedWrite_IsRetriedOnANewConnection()
    {
        _server.FailNext(SftpOperation.Write, "*.partial");

        await new ReportPublisher(Target).PublishAsync("daily.csv", "data"u8.ToArray());

        Assert.Equal("data", _files.ReadAllText("/outgoing/daily.csv"));
        Assert.Equal(2, _server.AuthAttempts.Count(a => a.Succeeded));
        Assert.Equal([1, 2], _server.RequestsOf(SftpOperation.Write).Select(w => w.ConnectionId).Distinct());
    }

    [Fact]
    public async Task ConnectionCutMidUpload_IsRetried_AndLeavesNoFinalFileBehind()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        _server.Intercept = request =>
        {
            if (request is { Operation: SftpOperation.Write, ConnectionId: 1, Offset: > 0 }) request.Connection.Abort();
            return ValueTask.FromResult<SftpStatus?>(null);
        };

        await new ReportPublisher(Target).PublishAsync("export.bin", payload);

        Assert.Equal(payload, _files.ReadAllBytes("/outgoing/export.bin"));
        Assert.Equal(["/outgoing/export.bin"], _files.AllFiles); // the second attempt overwrote the torn .partial and renamed it
    }

    [Fact]
    public async Task PermissionDenied_IsNotRetried()
    {
        _server.FailAlways(SftpOperation.Open, "/outgoing/*");

        await Assert.ThrowsAsync<SftpPermissionDeniedException>(() => new ReportPublisher(Target).PublishAsync("daily.csv", "x"u8.ToArray()));

        Assert.Single(_server.AuthAttempts);
    }

    [Fact]
    public async Task ImpostorHost_IsRefusedBeforeAnyCredentialsAreSent()
    {
        using var otherHostKey = SshKeyPair.CreateEcdsa();

        var target = Target with { HostKeyFingerprint = otherHostKey.PublicKey.Fingerprint };
        var ex = await Assert.ThrowsAsync<SshConnectionException>(() => new ReportPublisher(target).PublishAsync("daily.csv", "x"u8.ToArray()));

        Assert.Equal(DisconnectReason.KeyExchangeFailed, ex.DisconnectReason);
        Assert.Empty(_server.AuthAttempts);
        _server.Tcp.Transcript.AssertConnectionCount(1); // not retried
    }

    [Fact]
    public async Task UnknownKey_FailsAuthentication()
    {
        using var strangerKey = SshKeyPair.CreateEcdsa();

        await Assert.ThrowsAsync<SshAuthenticationException>(() =>
            new ReportPublisher(Target with { PrivateKeyPem = strangerKey.PrivateKeyPem }).PublishAsync("daily.csv", "x"u8.ToArray()));

        Assert.All(_server.AuthAttempts, a => Assert.False(a.Succeeded));
        Assert.Contains(_server.AuthAttempts, a => a.KeyFingerprint == strangerKey.PublicKey.Fingerprint);
    }

    [Fact]
    public async Task CollectInbox_DownloadsThenDeletes()
    {
        _files.AddFile("/inbox/a.json", "{\"id\":1}").AddFile("/inbox/b.json", "{\"id\":2}").AddDirectory("/inbox/archive");

        var collected = await new ReportPublisher(Target).CollectInboxAsync();

        Assert.Equal(["a.json", "b.json"], collected.Keys);
        Assert.Equal("{\"id\":2}", collected["b.json"]);
        Assert.False(_files.FileExists("/inbox/a.json"));
        Assert.True(_files.DirectoryExists("/inbox/archive"));
    }

    /// <summary>A delete that fails must not lose the file: it stays for the next run.</summary>
    [Fact]
    public async Task CollectInbox_DeleteFailure_KeepsTheFile()
    {
        _files.AddFile("/inbox/a.json", "{}");
        _server.FailNext(SftpOperation.Remove);

        await Assert.ThrowsAnyAsync<SshException>(() => new ReportPublisher(Target).CollectInboxAsync());

        Assert.True(_files.FileExists("/inbox/a.json"));
    }

    [Fact]
    public async Task HungServer_HitsTheOperationTimeout()
    {
        _files.AddFile("/inbox/a.json", "{}");
        _server.Intercept = async request =>
        {
            if (request.Operation == SftpOperation.Read) await Task.Delay(Timeout.Infinite, request.Connection.CancellationToken); // stall the reply, not the socket
            return null;
        };

        await Assert.ThrowsAsync<SshOperationTimeoutException>(() =>
            new ReportPublisher(Target with { Timeout = TimeSpan.FromMilliseconds(500) }).CollectInboxAsync());
    }

    [Fact]
    public async Task PasswordLogin_WithSshNet()
    {
        await using var server = SftpServer.Start(new SftpServerOptions
        {
            Users = [SftpUser.WithPassword("bob", "s3cret")],
            FileSystem = new InMemorySftpFileSystem().AddFile("/motd.txt", "hello bob"),
        });
        using var client = new SftpClient(server.Host, server.Port, "bob", "s3cret");
        client.HostKeyReceived += (_, e) => e.CanTrust = "SHA256:" + e.FingerPrintSHA256 == server.HostKey.Fingerprint;

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("hello bob", client.ReadAllText("/motd.txt"));
        Assert.Equal(("bob", "password", true), (server.AuthAttempts[0].User, server.AuthAttempts[0].Method, server.AuthAttempts[0].Succeeded));
    }
}
