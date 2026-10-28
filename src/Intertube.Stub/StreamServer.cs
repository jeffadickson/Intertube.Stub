using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Intertube.Stub;

/// <summary>Handles one connection. Implemented by <see cref="Conversation"/> and <see cref="LineResponder"/>.</summary>
public interface IConnectionHandler
{
    Task HandleAsync(Connection connection);
}

/// <summary>
/// A listening stream socket that runs a handler per connection, records every connection and shuts
/// down cleanly on <c>await using</c>. Base of <see cref="TcpServer"/> and <see cref="UnixServer"/>.
/// </summary>
public abstract class StreamServer : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly Func<Connection, Task> _handler;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, (Connection Connection, Task Task)> _active = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly SemaphoreSlim? _gate;
    private readonly Lazy<Task> _stop;
    private Task _acceptLoop = Task.CompletedTask;
    private int _nextId;

    private protected StreamServer(Socket listener, Func<Connection, Task> handler, ServerOptions? options)
    {
        Options = options ?? ServerOptions.Default;
        if (Options.MaxConcurrency is < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        _listener = listener;
        _handler = handler;
        _gate = Options.MaxConcurrency is { } max ? new SemaphoreSlim(max, max) : null;
        _stop = new(StopCoreAsync);
    }

    /// <summary>Settings this server was started with.</summary>
    public ServerOptions Options { get; }

    /// <summary>Every connection accepted so far and its traffic.</summary>
    public ServerTranscript Transcript { get; } = new();

    /// <summary>Exceptions thrown by handlers, excluding disconnects and shutdown.</summary>
    public IReadOnlyList<Exception> Errors => [.. _errors];

    /// <summary>Number of connections whose handler is still running.</summary>
    public int ActiveConnections => _active.Count;

    /// <summary>Cancelled when the server begins shutting down.</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>Throw an <see cref="AggregateException"/> if any handler failed.</summary>
    public void ThrowIfFaulted()
    {
        if (!_errors.IsEmpty) throw new AggregateException("A Intertube.Stub handler failed.", _errors);
    }

    /// <summary>Wait until <paramref name="count"/> handler errors have been recorded.</summary>
    public Task WaitForErrorsAsync(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        Transcript.WaitUntilAsync(() => _errors.Count >= count, $"{count} handler error(s)", timeout, cancellationToken);

    /// <summary>Open a client connection to this server, trusting its TLS certificate if it uses one.</summary>
    public async Task<Connection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var socket = CreateClientSocket();
        try
        {
            await socket.ConnectAsync(_listener.LocalEndPoint!, cancellationToken).ConfigureAwait(false);
            var transcript = new ConnectionTranscript(0, socket.RemoteEndPoint, Options.Encoding, Options.RecordTranscript, null, new());
            var connection = new Connection(socket, isServer: false, Options with { Faults = FaultOptions.None }, transcript, CancellationToken.None);
            if (Options.Tls is { } tls) await connection.UpgradeToTlsAsync(tls, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stop accepting, cancel <see cref="Stopping"/>, give running handlers
    /// <see cref="ServerOptions.ShutdownTimeout"/> to finish, then reset whatever is left.
    /// </summary>
    public Task StopAsync() => _stop.Value;

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private protected abstract Socket CreateClientSocket();

    private protected virtual void OnStopped()
    {
    }

    private protected void Start()
    {
        _listener.Listen(Options.Backlog);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // A client gave up before we accepted it.
            }

            if (socket.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) socket.NoDelay = true;
            var id = Interlocked.Increment(ref _nextId);
            var connection = new Connection(socket, isServer: true, Options, Transcript.Add(id, socket.RemoteEndPoint, Options), token);
            connection.Transcript.RecordEvent($"connected from {socket.RemoteEndPoint?.ToString() ?? "(unnamed)"}");

            var task = Task.Run(() => RunAsync(connection));
            _active[id] = (connection, task);
            _ = task.ContinueWith(_ => _active.TryRemove(id, out var _), TaskScheduler.Default);
        }
    }

    private async Task RunAsync(Connection connection)
    {
        var token = _stopping.Token;
        var entered = false;
        try
        {
            if (_gate is not null)
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                entered = true;
            }
            if (connection.Faults.ConnectDelay > TimeSpan.Zero)
                await Task.Delay(connection.Faults.ConnectDelay, token).ConfigureAwait(false);
            if (Options.Tls is { } tls)
                await connection.UpgradeToTlsAsync(tls, token).ConfigureAwait(false);

            await _handler(connection).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpected(ex, connection))
        {
        }
        catch (Exception ex)
        {
            _errors.Enqueue(ex);
            connection.Transcript.RecordEvent($"handler failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (entered) _gate!.Release();
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private bool IsExpected(Exception ex, Connection connection) =>
        _stopping.IsCancellationRequested
        || ex is ConnectionTerminatedException
        || connection.IsTerminated
        || Connection.IsPeerReset(ex);

    private async Task StopCoreAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        await _acceptLoop.ConfigureAwait(false);

        var running = _active.Values.ToArray();
        var all = Task.WhenAll(running.Select(r => r.Task));
        try
        {
            await all.WaitAsync(Options.ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            foreach (var (connection, _) in running) connection.Abort();
            // A handler ignoring cancellation (say, an untokened Task.Delay) is abandoned after a second grace period.
            await Task.WhenAny(all, Task.Delay(Options.ShutdownTimeout)).ConfigureAwait(false);
        }

        // _stopping and _gate are deliberately not disposed: an abandoned handler may still touch them.
        OnStopped();
    }

    private protected static Func<Connection, Task> Adapt(IConnectionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return handler.HandleAsync;
    }
}

/// <summary>A stub TCP server. Binds to loopback; pass port 0 for an ephemeral port.</summary>
public sealed class TcpServer : StreamServer
{
    private TcpServer(Socket listener, Func<Connection, Task> handler, ServerOptions? options)
        : base(listener, handler, options)
    {
        EndPoint = (IPEndPoint)listener.LocalEndPoint!;
        Start();
    }

    /// <summary>The bound address and port.</summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>The bound port; useful when listening on port 0.</summary>
    public int Port => EndPoint.Port;

    /// <summary>Host string for clients, such as "127.0.0.1".</summary>
    public string Host => EndPoint.Address.ToString();

    /// <summary>Listen on 127.0.0.1. Port 0 picks a free port.</summary>
    public static TcpServer Listen(int port, Func<Connection, Task> handler, ServerOptions? options = null) =>
        Listen(new IPEndPoint(IPAddress.Loopback, port), handler, options);

    public static TcpServer Listen(int port, IConnectionHandler handler, ServerOptions? options = null) =>
        Listen(port, Adapt(handler), options);

    /// <summary>Listen on a specific address, for example <see cref="IPAddress.IPv6Loopback"/> or <see cref="IPAddress.Any"/>.</summary>
    public static TcpServer Listen(IPEndPoint endPoint, Func<Connection, Task> handler, ServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ArgumentNullException.ThrowIfNull(handler);
        var listener = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(endPoint);
            return new TcpServer(listener, handler, options);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    public static TcpServer Listen(IPEndPoint endPoint, IConnectionHandler handler, ServerOptions? options = null) =>
        Listen(endPoint, Adapt(handler), options);

    private protected override Socket CreateClientSocket() =>
        new(EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

    public override string ToString() => $"tcp://{EndPoint}";
}

/// <summary>A stub server on a Unix domain socket (Linux, macOS and Windows 10+).</summary>
public sealed class UnixServer : StreamServer
{
    private readonly bool _ownsPath;

    private UnixServer(Socket listener, string path, bool ownsPath, Func<Connection, Task> handler, ServerOptions? options)
        : base(listener, handler, options)
    {
        Path = path;
        _ownsPath = ownsPath;
        Start();
    }

    /// <summary>The socket file's path.</summary>
    public string Path { get; }

    /// <summary>
    /// Listen on <paramref name="path"/>, or on a fresh temporary path when null (the Unix equivalent of
    /// an ephemeral port). A temporary socket file is deleted when the server stops.
    /// </summary>
    public static UnixServer Listen(string? path, Func<Connection, Task> handler, ServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var ownsPath = path is null;
        path ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"intertube-{Guid.NewGuid():N}"[..22] + ".sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            return new UnixServer(listener, path, ownsPath, handler, options);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    public static UnixServer Listen(string? path, IConnectionHandler handler, ServerOptions? options = null) =>
        Listen(path, Adapt(handler), options);

    private protected override Socket CreateClientSocket() => new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    private protected override void OnStopped()
    {
        if (!_ownsPath) return;
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public override string ToString() => $"unix://{Path}";
}
