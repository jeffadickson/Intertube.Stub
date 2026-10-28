using System.Collections.Concurrent;
using System.Net;
using Intertube.Stub.Internal;
using Intertube.Stub.Sftp.Ssh;

namespace Intertube.Stub.Sftp;

/// <summary>An SFTP request, as recorded in <see cref="SftpServer.Requests"/> and passed to <see cref="SftpServer.Intercept"/>.</summary>
public enum SftpOperation
{
    Open,
    Close,
    Read,
    Write,
    Stat,
    LStat,
    FStat,
    SetStat,
    FSetStat,
    OpenDir,
    ReadDir,
    Remove,
    MkDir,
    RmDir,
    RealPath,
    Rename,

    /// <summary>The <c>posix-rename@openssh.com</c> extension: rename, replacing any existing target.</summary>
    PosixRename,
    ReadLink,
    Symlink,

    /// <summary>The <c>fsync@openssh.com</c> extension.</summary>
    Fsync,

    /// <summary>Any other extended request; <see cref="SftpRequest.Path"/> holds its name.</summary>
    Extended,
}

/// <summary>
/// One SFTP request. <see cref="Path"/> is normalised and absolute (for handle operations, the path
/// the handle was opened with). <see cref="Connection"/> is the TCP connection it arrived on, so an
/// interceptor can <see cref="Connection.Abort"/> it mid-transfer. To stall a request, delay the
/// interceptor (for example <c>Task.Delay(Timeout.Infinite, request.Connection.CancellationToken)</c>);
/// do not call <see cref="Connection.HangAsync"/>, which would read the raw socket out from under SSH.
/// </summary>
public sealed record SftpRequest(SftpOperation Operation, string Path, string User, Connection Connection)
{
    /// <summary>Rename target.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Byte offset of a READ or WRITE.</summary>
    public long? Offset { get; init; }

    /// <summary>Requested length of a READ, or data length of a WRITE.</summary>
    public int? Length { get; init; }

    /// <summary>OPEN flags (SSH_FXF_READ = 1, WRITE = 2, APPEND = 4, CREAT = 8, TRUNC = 0x10, EXCL = 0x20).</summary>
    public uint? OpenFlags { get; init; }

    public int ConnectionId => Connection.Id;

    public override string ToString() => Operation switch
    {
        SftpOperation.Rename or SftpOperation.PosixRename or SftpOperation.Symlink => $"{Operation.ToString().ToUpperInvariant()} {Path} -> {TargetPath}",
        SftpOperation.Read or SftpOperation.Write => $"{Operation.ToString().ToUpperInvariant()} {Path} offset={Offset} length={Length}",
        SftpOperation.Open => $"OPEN {Path} flags=0x{OpenFlags:x}",
        _ => $"{Operation.ToString().ToUpperInvariant()} {Path}",
    };
}

/// <summary>A login attempt. <see cref="Method"/> is <c>publickey</c>, <c>password</c> or <c>none</c>.</summary>
public sealed record SshAuthAttempt(string User, string Method, bool Succeeded, string? KeyFingerprint, int ConnectionId)
{
    public override string ToString() =>
        $"AUTH {Method} user={User}{(KeyFingerprint is null ? "" : " key=" + KeyFingerprint)} {(Succeeded ? "accepted" : "rejected")}";
}

/// <summary>An account the server accepts.</summary>
public sealed record SftpUser(string Name)
{
    public string? Password { get; init; }

    public IReadOnlyList<SshPublicKey> AuthorizedKeys { get; init; } = [];

    public static SftpUser WithPassword(string name, string password) => new(name) { Password = password };

    public static SftpUser WithKey(string name, params SshPublicKey[] keys) => new(name) { AuthorizedKeys = keys };

    /// <summary>A user whose authorized keys are OpenSSH lines, such as the contents of an <c>id_ed25519.pub</c> file.</summary>
    public static SftpUser WithKey(string name, string authorizedKeyLine) => WithKey(name, SshPublicKey.Parse(authorizedKeyLine));
}

/// <summary>Settings for <see cref="SftpServer.Start(SftpServerOptions?)"/>.</summary>
public sealed record SftpServerOptions
{
    /// <summary>The files served. Defaults to a new <see cref="InMemorySftpFileSystem"/>.</summary>
    public ISftpFileSystem? FileSystem { get; init; }

    /// <summary>Accounts allowed to log in. When empty, any user name with any password or any (validly signed) key is accepted.</summary>
    public IReadOnlyList<SftpUser> Users { get; init; } = [];

    /// <summary>The host key. Defaults to a fresh ECDSA P-256 key per server.</summary>
    public SshKeyPair? HostKey { get; init; }

    /// <summary>Ciphers offered, most preferred first. Restrict it to make clients exercise a particular one.</summary>
    public IReadOnlyList<string> Ciphers { get; init; } = PacketCipher.Ciphers;

    /// <summary>MACs offered for the non-AEAD ciphers, most preferred first.</summary>
    public IReadOnlyList<string> Macs { get; init; } = PacketCipher.Macs;

    /// <summary>
    /// TCP-level options: transport faults (latency, fragmentation, disconnects, corruption of the
    /// encrypted stream), concurrency and the <see cref="ServerOptions.Log"/> callback, which receives
    /// one line per login and SFTP request. Transcripts are off by default, since the traffic is encrypted.
    /// </summary>
    public ServerOptions Server { get; init; } = new() { RecordTranscript = false };

    /// <summary>Address to listen on. Defaults to IPv4 loopback.</summary>
    public IPAddress Address { get; init; } = IPAddress.Loopback;

    /// <summary>Port to listen on; 0 picks a free one.</summary>
    public int Port { get; init; }
}

/// <summary>
/// An embedded SFTP server for tests: real SSH (ECDH key exchange, AES-GCM or AES-CTR with HMAC,
/// public-key and password logins) over an ephemeral loopback port, serving an in-memory or
/// on-disk file system. Every login and request is recorded, and requests can be failed, delayed or
/// cut off to test how a client copes.
/// <code>
/// await using var sftp = SftpServer.Start(new() { Users = [SftpUser.WithKey("test", File.ReadAllText("id_ed25519.pub"))] });
/// File.WriteAllText("known_hosts", sftp.KnownHostsLine);
/// sftp.FailNext(SftpOperation.Write, "/incoming/*");
/// </code>
/// Not a production server: it trusts its clients with memory and does no rate limiting.
/// </summary>
public sealed class SftpServer : IAsyncDisposable
{
    private readonly ConcurrentQueue<SftpRequest> _requests = new();
    private readonly ConcurrentQueue<SshAuthAttempt> _attempts = new();
    private readonly ConcurrentQueue<FailureRule> _rules = new();
    private readonly ChangeSignal _signal = new();
    private readonly bool _ownsHostKey;

    private SftpServer(SftpServerOptions options)
    {
        Options = options;
        FileSystem = options.FileSystem ?? new InMemorySftpFileSystem();
        _ownsHostKey = options.HostKey is null;
        HostKeyPair = options.HostKey ?? SshKeyPair.CreateEcdsa(comment: null);
        foreach (var cipher in options.Ciphers) PacketCipher.Sizes(cipher);
        Algorithms = new SshAlgorithms(HostKeyPair, options.Ciphers, options.Macs);
        Tcp = TcpServer.Listen(new IPEndPoint(options.Address, options.Port), connection => new SshSession(this, connection).RunAsync(), options.Server);
    }

    /// <summary>Start an SFTP server on an ephemeral loopback port.</summary>
    public static SftpServer Start(SftpServerOptions? options = null) => new(options ?? new SftpServerOptions());

    /// <summary>Start an SFTP server that serves <paramref name="rootDirectory"/> as <c>/</c>.</summary>
    public static SftpServer Start(string rootDirectory, params SftpUser[] users) =>
        Start(new SftpServerOptions { FileSystem = new DirectorySftpFileSystem(rootDirectory), Users = users });

    public SftpServerOptions Options { get; }

    /// <summary>The underlying TCP server, for its connection count, errors and faults.</summary>
    public TcpServer Tcp { get; }

    public ISftpFileSystem FileSystem { get; }

    public int Port => Tcp.Port;

    public string Host => Tcp.Host;

    /// <summary>The public host key, for pinning.</summary>
    public SshPublicKey HostKey => HostKeyPair.PublicKey;

    /// <summary>A <c>known_hosts</c> line for this server, e.g. <c>[127.0.0.1]:51234 ecdsa-sha2-nistp256 AAAA...</c>.</summary>
    public string KnownHostsLine => $"[{Host}]:{Port} {HostKey}";

    /// <summary>
    /// Called before each SFTP request is carried out. Return a status to send it instead of doing the
    /// work, or null to proceed. It can also delay, or abort or hang <see cref="SftpRequest.Connection"/>.
    /// </summary>
    public Func<SftpRequest, ValueTask<SftpStatus?>>? Intercept { get; set; }

    /// <summary>Every SFTP request, in arrival order.</summary>
    public IReadOnlyList<SftpRequest> Requests => [.. _requests];

    /// <summary>Every login attempt, in order.</summary>
    public IReadOnlyList<SshAuthAttempt> AuthAttempts => [.. _attempts];

    /// <summary>Exceptions from connection handlers: SSH protocol violations, or bugs.</summary>
    public IReadOnlyList<Exception> Errors => Tcp.Errors;

    internal SshKeyPair HostKeyPair { get; }

    internal SshAlgorithms Algorithms { get; }

    /// <summary>
    /// Answer the next <paramref name="times"/> matching requests with <paramref name="status"/> instead
    /// of carrying them out. <paramref name="path"/> is a glob (<c>*</c>, <c>?</c>) on the normalised path.
    /// </summary>
    public void FailNext(SftpOperation operation, string? path = null, SftpStatus status = SftpStatus.Failure, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(times);
        _rules.Enqueue(new FailureRule(operation, path is null ? null : LineMatcher.Glob(path), status, times));
    }

    /// <summary>Answer every matching request with <paramref name="status"/> from now on.</summary>
    public void FailAlways(SftpOperation operation, string? path = null, SftpStatus status = SftpStatus.PermissionDenied) =>
        _rules.Enqueue(new FailureRule(operation, path is null ? null : LineMatcher.Glob(path), status, int.MaxValue));

    /// <summary>Requests of one kind, optionally only those whose path matches the glob <paramref name="path"/>.</summary>
    public IReadOnlyList<SftpRequest> RequestsOf(SftpOperation operation, string? path = null)
    {
        var matcher = path is null ? null : LineMatcher.Glob(path);
        return [.. _requests.Where(r => r.Operation == operation && (matcher is null || matcher.IsMatch(r.Path)))];
    }

    /// <summary>Wait until a request matching <paramref name="predicate"/> has arrived, and return it.</summary>
    public async Task<SftpRequest> WaitForRequestAsync(Func<SftpRequest, bool> predicate, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        SftpRequest? found = null;
        await _signal.WaitUntilAsync(() => (found = _requests.FirstOrDefault(predicate)) is not null, timeout,
            () => "a matching SFTP request. Requests so far:\n" + string.Join("\n", _requests), cancellationToken).ConfigureAwait(false);
        return found!;
    }

    /// <summary>Wait for a request of one kind whose path matches the glob <paramref name="path"/>.</summary>
    public Task<SftpRequest> WaitForRequestAsync(SftpOperation operation, string? path = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var matcher = path is null ? null : LineMatcher.Glob(path);
        return WaitForRequestAsync(r => r.Operation == operation && (matcher is null || matcher.IsMatch(r.Path)), timeout, cancellationToken);
    }

    /// <summary>Throw if any connection handler failed, with the first failure as the inner exception.</summary>
    public void ThrowIfFaulted() => Tcp.ThrowIfFaulted();

    public Task StopAsync() => Tcp.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await Tcp.DisposeAsync().ConfigureAwait(false);
        if (_ownsHostKey) HostKeyPair.Dispose();
    }

    public override string ToString() => $"sftp://{Host}:{Port}";

    internal void Record(SshAuthAttempt attempt)
    {
        _attempts.Enqueue(attempt);
        _signal.Pulse();
    }

    /// <summary>Record a request and decide whether to fail it.</summary>
    internal async ValueTask<SftpStatus?> AdmitAsync(SftpRequest request)
    {
        _requests.Enqueue(request);
        request.Connection.Transcript.RecordMessage(TranscriptDirection.Received, request.ToString());
        _signal.Pulse();

        foreach (var rule in _rules)
            if (rule.TryConsume(request))
                return rule.Status;

        return Intercept is { } intercept ? await intercept(request).ConfigureAwait(false) : null;
    }

    private sealed class FailureRule(SftpOperation operation, LineMatcher? path, SftpStatus status, int times)
    {
        private int _remaining = times;

        public SftpStatus Status => status;

        public bool TryConsume(SftpRequest request)
        {
            if (request.Operation != operation || (path is not null && !path.IsMatch(request.Path))) return false;
            while (true)
            {
                var remaining = Volatile.Read(ref _remaining);
                if (remaining == 0) return false;
                if (remaining == int.MaxValue || Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) == remaining) return true;
            }
        }
    }
}
