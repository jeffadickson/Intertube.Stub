using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Exceptions;
using Intertube.Stub.Sftp;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

public sealed record FtpsSettings(string Host, int Port, string User, string Password, Func<X509Certificate, bool> TrustCertificate)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Exchanges files with a bank over explicit FTPS (AUTH TLS, encrypted data connections) using
/// FluentFTP. Uploads go to a <c>.tmp</c> name, are checked with SIZE, then renamed; a server that
/// silently truncates an upload is caught rather than trusted. Transient failures are retried.
/// </summary>
public sealed class BankFileExchange(FtpsSettings settings, int maxAttempts = 3)
{
    public async Task UploadAsync(string name, byte[] content, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var ftp = await ConnectAsync(cancellationToken);
                var temporary = $"/upload/{name}.tmp";
                var status = await ftp.UploadBytes(content, temporary, FtpRemoteExists.Overwrite, createRemoteDir: false, progress: null, cancellationToken);
                if (status != FtpStatus.Success) throw new IOException($"Upload of {name} returned {status}.");
                var size = await ftp.GetFileSize(temporary, -1, cancellationToken);
                if (size != content.Length) throw new IOException($"The server stored {size} of {content.Length} bytes.");
                await ftp.MoveFile(temporary, $"/upload/{name}", FtpRemoteExists.Overwrite, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>Download and then delete everything in <c>/download</c>.</summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> CollectAsync(CancellationToken cancellationToken = default)
    {
        await using var ftp = await ConnectAsync(cancellationToken);
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in await ftp.GetListing("/download", cancellationToken))
        {
            if (item.Type != FtpObjectType.File) continue;
            result[item.Name] = await ftp.DownloadBytes(item.FullName, cancellationToken);
            await ftp.DeleteFile(item.FullName, cancellationToken);
        }
        return result;
    }

    private async Task<AsyncFtpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var ftp = new AsyncFtpClient(settings.Host, settings.User, settings.Password, settings.Port);
        ftp.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        ftp.Config.DataConnectionEncryption = true;
        ftp.Config.ConnectTimeout = ftp.Config.ReadTimeout = ftp.Config.DataConnectionConnectTimeout = ftp.Config.DataConnectionReadTimeout = (int)settings.Timeout.TotalMilliseconds;
        ftp.ValidateCertificate += (_, e) => e.Accept = settings.TrustCertificate(e.Certificate);
        try
        {
            await ftp.Connect(cancellationToken);
            return ftp;
        }
        catch
        {
            await ftp.DisposeAsync();
            throw;
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        FtpAuthenticationException or FtpSecurityNotAvailableException or System.Security.Authentication.AuthenticationException => false,
        FtpCommandException command => command.CompletionCode?.StartsWith('4') == true,
        FtpException { InnerException: { } inner } => IsTransient(inner),
        IOException or TimeoutException or System.Net.Sockets.SocketException => true,
        _ => false,
    };
}

// ---- A fake FTP/FTPS server ---------------------------------------------------------------------

/// <summary>
/// An FTP server (RFC 959, with AUTH TLS, PBSZ/PROT, EPSV/PASV, MLSD, SIZE and RNFR/RNTO) over any
/// <see cref="ISftpFileSystem"/>, so the in-memory file system from the SFTP server is reused.
/// Each passive data connection gets its own ephemeral Intertube.Stub listener, and
/// <see cref="DataFaults"/> applies faults to data connections only.
/// </summary>
public sealed class FakeFtpServer : IAsyncDisposable
{
    private readonly List<ReplyRule> _rules = [];

    public FakeFtpServer(ISftpFileSystem files, TlsOptions? tls, IReadOnlyDictionary<string, string> users)
    {
        Files = files;
        Tls = tls;
        Users = users;
        Control = TcpServer.Listen(0, connection => new Session(this, connection).RunAsync());
    }

    public ISftpFileSystem Files { get; }

    public TlsOptions? Tls { get; }

    public IReadOnlyDictionary<string, string> Users { get; }

    public TcpServer Control { get; }

    /// <summary>Faults for data connections, e.g. <c>FaultOptions.DisconnectAfter(1000)</c> to break a download.</summary>
    public FaultOptions DataFaults { get; set; } = FaultOptions.None;

    /// <summary>Keep only this many bytes of each upload but still report success: a server that lies.</summary>
    public int? TruncateUploadsTo { get; set; }

    /// <summary>Answer the next matching command line (a glob) with <paramref name="reply"/> instead of handling it.</summary>
    public void FailNext(LineMatcher command, string reply)
    {
        lock (_rules) _rules.Add(new ReplyRule(command, reply));
    }

    private string? Override(string line)
    {
        lock (_rules)
        {
            var rule = _rules.FirstOrDefault(r => r.Command.IsMatch(line));
            if (rule is null) return null;
            _rules.Remove(rule);
            return rule.Reply;
        }
    }

    private sealed record ReplyRule(LineMatcher Command, string Reply);

    public ValueTask DisposeAsync() => Control.DisposeAsync();

    private sealed class Session(FakeFtpServer server, Connection control)
    {
        private string? _user;
        private bool _loggedIn;
        private bool _protectData;
        private string _cwd = "/";
        private string? _renameFrom;
        private long _restart;
        private TcpServer? _dataListener;
        private TaskCompletionSource<Connection>? _dataConnection;
        private TaskCompletionSource? _dataDone;

        public async Task RunAsync()
        {
            try
            {
                await ReplyAsync("220 Intertube.Stub FTP ready");
                while (await control.ReadLineAsync() is { } line)
                {
                    if (server.Override(line) is { } injected)
                    {
                        await ReplyAsync(injected);
                        continue;
                    }
                    if (!await HandleAsync(line)) return;
                }
            }
            finally
            {
                await CloseDataListenerAsync();
            }
        }

        private async Task<bool> HandleAsync(string line)
        {
            var space = line.IndexOf(' ');
            var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var argument = space < 0 ? "" : line[(space + 1)..];
            if (!_loggedIn && verb is not ("USER" or "PASS" or "AUTH" or "FEAT" or "QUIT" or "SYST" or "PBSZ" or "PROT" or "OPTS" or "NOOP"))
                return await Reply("530 Please log in");

            try
            {
                switch (verb)
                {
                    case "AUTH":
                        if (server.Tls is null || control.IsTls) return await Reply("502 TLS not available");
                        await ReplyAsync("234 AUTH TLS successful");
                        await control.UpgradeToTlsAsync(server.Tls);
                        return true;
                    case "USER":
                        _user = argument;
                        _loggedIn = false;
                        return await Reply("331 Password required");
                    case "PASS":
                        _loggedIn = _user is not null && server.Users.TryGetValue(_user, out var password) && password == argument;
                        return await Reply(_loggedIn ? "230 Logged in" : "530 Login incorrect");
                    case "FEAT":
                        await ReplyAsync("211-Features:\r\n MLST type*;size*;modify*;\r\n SIZE\r\n MDTM\r\n EPSV\r\n PASV\r\n UTF8\r\n REST STREAM\r\n" + (server.Tls is null ? "" : " AUTH TLS\r\n PBSZ\r\n PROT\r\n") + "211 End");
                        return true;
                    case "PBSZ":
                        return await Reply("200 PBSZ=0");
                    case "PROT":
                        _protectData = argument.Equals("P", StringComparison.OrdinalIgnoreCase);
                        if (_protectData && !control.IsTls) return await Reply("503 Use AUTH TLS first");
                        return await Reply($"200 Protection level set to {argument}");
                    case "OPTS":
                        return await Reply("200 OK");
                    case "SYST":
                        return await Reply("215 UNIX Type: L8");
                    case "NOOP":
                        return await Reply("200 OK");
                    case "TYPE" or "MODE" or "STRU":
                        return await Reply("200 OK");
                    case "PWD":
                        return await Reply($"257 \"{_cwd}\" is the current directory");
                    case "CWD":
                    {
                        var path = Resolve(argument);
                        if (!server.Files.GetAttributes(path).IsDirectory) return await Reply("550 Not a directory");
                        _cwd = path;
                        return await Reply("250 OK");
                    }
                    case "CDUP":
                        _cwd = SftpPath.Parent(_cwd);
                        return await Reply("250 OK");
                    case "EPSV":
                        await OpenDataListenerAsync();
                        return await Reply($"229 Entering Extended Passive Mode (|||{_dataListener!.Port}|)");
                    case "PASV":
                        await OpenDataListenerAsync();
                        return await Reply($"227 Entering Passive Mode (127,0,0,1,{_dataListener!.Port >> 8},{_dataListener.Port & 0xFF})");
                    case "REST":
                        _restart = long.Parse(argument, CultureInfo.InvariantCulture);
                        return await Reply($"350 Restarting at {_restart}");
                    case "SIZE":
                    {
                        var attributes = server.Files.GetAttributes(Resolve(argument));
                        return await Reply(attributes.IsDirectory ? "550 Not a file" : $"213 {attributes.Size}");
                    }
                    case "MDTM":
                        return await Reply($"213 {server.Files.GetAttributes(Resolve(argument)).LastWriteTime:yyyyMMddHHmmss}");
                    case "MLST":
                    {
                        var path = Resolve(argument.Length == 0 ? "." : argument);
                        await ReplyAsync($"250-Listing {path}\r\n {Facts(server.Files.GetAttributes(path))} {path}\r\n250 End");
                        return true;
                    }
                    case "MLSD" or "LIST" or "NLST":
                    {
                        var path = Resolve(argument.StartsWith('-') ? "." : argument.Length == 0 ? "." : argument);
                        var entries = server.Files.ListDirectory(path).ToList();
                        var text = string.Concat(entries.Select(e => verb switch
                        {
                            "MLSD" => $"{Facts(e.Attributes)} {e.Name}\r\n",
                            "NLST" => e.Name + "\r\n",
                            _ => $"{(e.Attributes.IsDirectory ? 'd' : '-')}rw-r--r-- 1 ftp ftp {e.Attributes.Size,10} {e.Attributes.LastWriteTime:MMM dd HH:mm} {e.Name}\r\n",
                        }));
                        return await TransferAsync($"{verb} {path}", async data => await data.WriteAsync(text));
                    }
                    case "RETR":
                    {
                        var path = Resolve(argument);
                        await using var file = server.Files.OpenFile(path, FileMode.Open, FileAccess.Read);
                        file.Position = _restart;
                        _restart = 0;
                        var content = new MemoryStream();
                        await file.CopyToAsync(content);
                        return await TransferAsync($"RETR {path} {content.Length} bytes", async data => await data.WriteAsync(content.ToArray()));
                    }
                    case "STOR" or "APPE":
                    {
                        var path = Resolve(argument);
                        return await TransferAsync($"{verb} {path}", async data =>
                        {
                            var bytes = await data.ReadToEndAsync();
                            if (server.TruncateUploadsTo is { } limit && bytes.Length > limit) bytes = bytes[..limit];
                            await using var file = server.Files.OpenFile(path, verb == "APPE" ? FileMode.Append : FileMode.Create, FileAccess.Write);
                            await file.WriteAsync(bytes);
                            control.Transcript.RecordMessage(TranscriptDirection.Received, $"stored {path} {bytes.Length} bytes");
                        });
                    }
                    case "DELE":
                        server.Files.DeleteFile(Resolve(argument));
                        return await Reply("250 Deleted");
                    case "MKD":
                        server.Files.CreateDirectory(Resolve(argument));
                        return await Reply($"257 \"{Resolve(argument)}\" created");
                    case "RMD":
                        server.Files.DeleteDirectory(Resolve(argument));
                        return await Reply("250 Removed");
                    case "RNFR":
                        server.Files.GetAttributes(Resolve(argument));
                        _renameFrom = Resolve(argument);
                        return await Reply("350 Ready for RNTO");
                    case "RNTO":
                        if (_renameFrom is null) return await Reply("503 RNFR first");
                        server.Files.Rename(_renameFrom, Resolve(argument), overwrite: true);
                        _renameFrom = null;
                        return await Reply("250 Renamed");
                    case "QUIT":
                        await ReplyAsync("221 Bye");
                        return false;
                    default:
                        return await Reply("502 Command not implemented");
                }
            }
            catch (SftpException ex) when (ex.Status == SftpStatus.NoSuchFile)
            {
                return await Reply("550 No such file or directory");
            }
            catch (SftpException ex)
            {
                return await Reply($"550 {ex.Message}");
            }
        }

        /// <summary>Run one transfer on the pending passive data connection: 150, the transfer, then 226 (or 426).</summary>
        private async Task<bool> TransferAsync(string description, Func<Connection, Task> transfer)
        {
            if (_dataConnection is null) return await Reply("425 Use EPSV or PASV first");
            Connection data;
            try
            {
                data = await _dataConnection.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                await CloseDataListenerAsync();
                return await Reply("425 No data connection");
            }

            await ReplyAsync("150 Opening data connection");
            try
            {
                if (_protectData) await data.UpgradeToTlsAsync(server.Tls!);
                control.Transcript.RecordMessage(TranscriptDirection.Event, $"data connection{(data.IsTls ? " (TLS)" : "")}: {description}");
                data.Faults = server.DataFaults;
                await transfer(data);
                await data.CloseAsync();
                await ReplyAsync("226 Transfer complete");
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                data.Abort();
                await ReplyAsync("426 Connection closed; transfer aborted");
            }
            finally
            {
                await CloseDataListenerAsync();
            }
            return true;
        }

        private async Task OpenDataListenerAsync()
        {
            await CloseDataListenerAsync();
            var pending = new TaskCompletionSource<Connection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _dataConnection = pending;
            _dataDone = done;
            _dataListener = TcpServer.Listen(0, async connection =>
            {
                if (!pending.TrySetResult(connection)) return; // one connection per PASV
                await done.Task;
            }, new ServerOptions { RecordTranscript = false });
        }

        private async Task CloseDataListenerAsync()
        {
            _dataDone?.TrySetResult();
            if (_dataListener is { } listener) await listener.DisposeAsync();
            _dataListener = null;
            _dataConnection = null;
            _dataDone = null;
        }

        private string Resolve(string path) => SftpPath.Normalize(path.StartsWith('/') ? path : SftpPath.Combine(_cwd, path));

        private static string Facts(SftpAttributes a) =>
            $"type={(a.IsDirectory ? "dir" : "file")};size={a.Size ?? 0};modify={a.LastWriteTime?.UtcDateTime:yyyyMMddHHmmss};";

        private async Task<bool> Reply(string line)
        {
            await ReplyAsync(line);
            return true;
        }

        private ValueTask ReplyAsync(string text) => control.WriteAsync(text + "\r\n");
    }
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// FluentFTP against a fake FTPS server: control and data connections both encrypted, the second
/// connection per transfer that makes FTP awkward to fake, and failures on either connection.
/// </summary>
public sealed class FtpExample : IAsyncDisposable
{
    private static readonly TlsOptions Tls = TlsOptions.SelfSigned("ftp.bank.test");
    private readonly InMemorySftpFileSystem _files = new InMemorySftpFileSystem().AddDirectory("/upload").AddDirectory("/download");
    private readonly FakeFtpServer _ftp;

    public FtpExample() => _ftp = new FakeFtpServer(_files, Tls, new Dictionary<string, string> { ["acme"] = "pw" });

    private FtpsSettings Settings => new(_ftp.Control.Host, _ftp.Control.Port, "acme", "pw",
        certificate => certificate.GetCertHashString() == Tls.Certificate.GetCertHashString());

    public ValueTask DisposeAsync() => _ftp.DisposeAsync();

    [Fact]
    public async Task Upload_IsEncryptedEndToEnd_VerifiedThenRenamed()
    {
        await new BankFileExchange(Settings).UploadAsync("payments.xml", "<payments/>"u8.ToArray());

        Assert.Equal("<payments/>", _files.ReadAllText("/upload/payments.xml"));
        Assert.False(_files.FileExists("/upload/payments.xml.tmp"));
        var control = _ftp.Control.Transcript;
        control.AssertReceivedLinesInOrder("AUTH TLS", "USER acme", "PROT P", "STOR /upload/payments.xml.tmp", "SIZE /upload/payments.xml.tmp", "RNFR /upload/payments.xml.tmp", "RNTO /upload/payments.xml");
        Assert.Contains("data connection (TLS): STOR /upload/payments.xml.tmp", control.Connections.SelectMany(c => c.Events));
    }

    [Fact]
    public async Task Collect_DownloadsThenDeletes()
    {
        _files.AddFile("/download/statement-1.csv", "a").AddFile("/download/statement-2.csv", "bb").AddDirectory("/download/archive");

        var files = await new BankFileExchange(Settings).CollectAsync();

        Assert.Equal(["statement-1.csv", "statement-2.csv"], files.Keys);
        Assert.Equal("bb"u8.ToArray(), files["statement-2.csv"]);
        Assert.Equal([], _files.AllFiles);
    }

    [Fact]
    public async Task DiskFullOnce_IsRetried()
    {
        _ftp.FailNext("STOR *", "452 Insufficient storage space");

        await new BankFileExchange(Settings).UploadAsync("payments.xml", "<payments/>"u8.ToArray());

        Assert.True(_files.FileExists("/upload/payments.xml"));
        _ftp.Control.Transcript.AssertConnectionCount(2);
    }

    [Fact]
    public async Task ServerThatSilentlyTruncatesUploads_IsCaughtBySizeCheck()
    {
        _ftp.TruncateUploadsTo = 5;
        var content = new byte[5000];

        var ex = await Assert.ThrowsAsync<IOException>(() => new BankFileExchange(Settings).UploadAsync("big.bin", content));

        Assert.Contains("stored 5 of 5000 bytes", ex.Message);
        Assert.False(_files.FileExists("/upload/big.bin"));
        _ftp.Control.Transcript.AssertConnectionCount(3);
    }

    [Fact]
    public async Task DataConnectionDroppedMidDownload_IsAnError_NotATruncatedFile()
    {
        _files.AddFile("/download/big.csv", new byte[200_000]);
        _ftp.DataFaults = FaultOptions.DisconnectAfter(50_000);

        await Assert.ThrowsAnyAsync<Exception>(() => new BankFileExchange(Settings).CollectAsync());

        Assert.True(_files.FileExists("/download/big.csv"), "a failed download must not delete the remote file");
        _ftp.Control.Transcript.AssertSentLine("426 *");
    }

    [Fact]
    public async Task ServerWithoutTls_IsRefused_NotDowngraded()
    {
        await using var plain = new FakeFtpServer(_files, tls: null, new Dictionary<string, string> { ["acme"] = "pw" });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new BankFileExchange(Settings with { Port = plain.Control.Port }).UploadAsync("payments.xml", "x"u8.ToArray()));

        plain.Control.Transcript.AssertNotReceivedLine("USER *");
        plain.Control.Transcript.AssertConnectionCount(1);
    }

    [Fact]
    public async Task ImpostorCertificate_IsRejected()
    {
        var other = TlsOptions.SelfSigned("ftp.bank.test");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new BankFileExchange(Settings with { TrustCertificate = c => c.GetCertHashString() == other.Certificate.GetCertHashString() })
                .UploadAsync("payments.xml", "x"u8.ToArray()));

        _ftp.Control.Transcript.AssertNotReceivedLine("USER *");
    }

    [Fact]
    public async Task WrongPassword_IsNotRetried()
    {
        await Assert.ThrowsAsync<FtpAuthenticationException>(() =>
            new BankFileExchange(Settings with { Password = "wrong" }).UploadAsync("payments.xml", "x"u8.ToArray()));

        _ftp.Control.Transcript.AssertConnectionCount(1);
    }
}
