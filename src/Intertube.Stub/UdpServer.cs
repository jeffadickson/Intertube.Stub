using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Intertube.Stub.Internal;

namespace Intertube.Stub;

/// <summary>Settings for a <see cref="UdpServer"/>.</summary>
public sealed record UdpServerOptions
{
    public static UdpServerOptions Default { get; } = new();

    /// <summary>
    /// Handlers running at once. Defaults to 1, so datagrams are handled in arrival order;
    /// raise it to process them in parallel.
    /// </summary>
    public int MaxConcurrency { get; init; } = 1;

    public Encoding Encoding { get; init; } = new UTF8Encoding(false);

    /// <summary>Fraction (0 to 1) of incoming datagrams silently dropped, simulating packet loss.</summary>
    public double DropRate { get; init; }

    /// <summary>Delay before each reply is sent.</summary>
    public TimeSpan ReplyDelay { get; init; }

    /// <summary>Seed for <see cref="DropRate"/>, so loss patterns are reproducible.</summary>
    public int Seed { get; init; }

    /// <summary>
    /// Largest datagram accepted. A larger one is discarded on every OS, without reaching the handler or
    /// the transcript, and logged as "datagram larger than MaxDatagramSize".
    /// </summary>
    public int MaxDatagramSize { get; init; } = 65_527;

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public Action<string>? Log { get; init; }
}

/// <summary>A received datagram and a way to answer it.</summary>
public sealed class Datagram
{
    private readonly UdpServer _server;

    internal Datagram(UdpServer server, byte[] data, EndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        _server = server;
        Data = data;
        RemoteEndPoint = remoteEndPoint;
        CancellationToken = cancellationToken;
    }

    public ReadOnlyMemory<byte> Data { get; }

    public EndPoint RemoteEndPoint { get; }

    /// <summary>The payload decoded with <see cref="UdpServerOptions.Encoding"/>.</summary>
    public string Text => _server.Options.Encoding.GetString(Data.Span);

    /// <summary>Cancelled when the server shuts down.</summary>
    public CancellationToken CancellationToken { get; }

    public ValueTask ReplyAsync(ReadOnlyMemory<byte> data) => _server.SendAsync(data, RemoteEndPoint, CancellationToken);

    public ValueTask ReplyAsync(string text) => ReplyAsync(_server.Options.Encoding.GetBytes(text));
}

/// <summary>One datagram recorded by a <see cref="UdpServer"/>.</summary>
public sealed record DatagramRecord(TranscriptDirection Direction, EndPoint RemoteEndPoint, byte[] Data, bool Dropped, DateTimeOffset Timestamp);

/// <summary>Datagrams a <see cref="UdpServer"/> received and sent.</summary>
public sealed class UdpTranscript
{
    private readonly List<DatagramRecord> _records = [];
    private readonly ChangeSignal _signal = new();
    private readonly Encoding _encoding;

    internal UdpTranscript(Encoding encoding) => _encoding = encoding;

    public IReadOnlyList<DatagramRecord> Records
    {
        get { lock (_records) return [.. _records]; }
    }

    /// <summary>Datagrams delivered to the handler (dropped ones excluded).</summary>
    public IReadOnlyList<DatagramRecord> Received => [.. Records.Where(r => r.Direction == TranscriptDirection.Received && !r.Dropped)];

    public IReadOnlyList<DatagramRecord> Sent => [.. Records.Where(r => r.Direction == TranscriptDirection.Sent)];

    public IReadOnlyList<string> ReceivedTexts => [.. Received.Select(r => _encoding.GetString(r.Data))];

    public IReadOnlyList<string> SentTexts => [.. Sent.Select(r => _encoding.GetString(r.Data))];

    public void AssertReceived(LineMatcher matcher)
    {
        if (!ReceivedTexts.Any(matcher.IsMatch))
            throw new TranscriptAssertionException($"Expected to receive a datagram matching {matcher}.{Environment.NewLine}{this}");
    }

    public Task WaitForReceivedAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => Received.Count >= count, $"{count} received datagram(s)", timeout, cancellationToken);

    public Task WaitForSentAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        WaitUntilAsync(() => Sent.Count >= count, $"{count} sent datagram(s)", timeout, cancellationToken);

    public async Task WaitUntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await _signal.WaitUntilAsync(condition, timeout, () => description, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}{Environment.NewLine}{this}", ex);
        }
    }

    internal void Add(DatagramRecord record)
    {
        lock (_records) _records.Add(record);
        _signal.Pulse();
    }

    internal void Pulse() => _signal.Pulse();

    public override string ToString() => string.Join(Environment.NewLine, Records.Select(r =>
        $"{(r.Direction == TranscriptDirection.Received ? "<-" : "->")} {r.RemoteEndPoint} " +
        $"{TranscriptBase.Escape(_encoding.GetString(r.Data))}{(r.Dropped ? " (dropped)" : "")}"));
}

/// <summary>
/// A stub UDP server. A receive loop feeds a channel that <see cref="Parallel.ForEachAsync{TSource}(IAsyncEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
/// drains with <see cref="UdpServerOptions.MaxConcurrency"/> workers.
/// </summary>
public sealed class UdpServer : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly Func<Datagram, Task> _handler;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Channel<Datagram> _inbox = Channel.CreateUnbounded<Datagram>(new() { SingleWriter = true });
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly Random _random;
    private readonly Task _receiveLoop;
    private readonly Task _dispatchLoop;
    private readonly Lazy<Task> _stop;

    private UdpServer(Socket socket, Func<Datagram, Task> handler, UdpServerOptions? options)
    {
        Options = options ?? UdpServerOptions.Default;
        if (Options.MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency must be at least 1.");
        if (Options.DropRate is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(options), "DropRate must be between 0 and 1.");
        _socket = socket;
        _handler = handler;
        _random = new Random(Options.Seed);
        EndPoint = (IPEndPoint)socket.LocalEndPoint!;
        Transcript = new UdpTranscript(Options.Encoding);
        _stop = new(StopCoreAsync);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    public UdpServerOptions Options { get; }

    public IPEndPoint EndPoint { get; }

    public int Port => EndPoint.Port;

    /// <summary>Host string for clients, such as "127.0.0.1" (loopback for a wildcard bind).</summary>
    public string Host => (EndPoint.Address.Equals(IPAddress.Any) ? IPAddress.Loopback : EndPoint.Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : EndPoint.Address).ToString();

    public UdpTranscript Transcript { get; }

    public IReadOnlyList<Exception> Errors => [.. _errors];

    public void ThrowIfFaulted()
    {
        if (!_errors.IsEmpty) throw new AggregateException("An Intertube.Stub handler failed.", _errors);
    }

    /// <summary>Listen on 127.0.0.1. Port 0 picks a free port.</summary>
    public static UdpServer Listen(int port, Func<Datagram, Task> handler, UdpServerOptions? options = null) =>
        Listen(new IPEndPoint(IPAddress.Loopback, port), handler, options);

    public static UdpServer Listen(IPEndPoint endPoint, Func<Datagram, Task> handler, UdpServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ArgumentNullException.ThrowIfNull(handler);
        var socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // SIO_UDP_CONNRESET off: otherwise an ICMP "port unreachable" from one client breaks the receive loop.
                const int SioUdpConnReset = -1744830452;
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            socket.Bind(endPoint);
            return new UdpServer(socket, handler, options);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Send a datagram, for replies or unsolicited messages.</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, EndPoint remoteEndPoint, CancellationToken cancellationToken = default)
    {
        if (Options.ReplyDelay > TimeSpan.Zero) await Task.Delay(Options.ReplyDelay, cancellationToken).ConfigureAwait(false);
        await _socket.SendToAsync(data, SocketFlags.None, remoteEndPoint, cancellationToken).ConfigureAwait(false);
        Transcript.Add(new(TranscriptDirection.Sent, remoteEndPoint, data.ToArray(), false, DateTimeOffset.UtcNow));
        Log($"-> {remoteEndPoint} {TranscriptBase.Escape(Options.Encoding.GetString(data.Span))}");
    }

    public Task StopAsync() => _stop.Value;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReceiveLoopAsync()
    {
        var token = _stopping.Token;
        // One byte to spare, so a datagram over the limit is seen as such: Unix truncates silently to the
        // buffer size, while Windows fails the receive with MessageSize once a datagram does not fit.
        var buffer = new byte[Options.MaxDatagramSize + 1];
        EndPoint any = EndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (!token.IsCancellationRequested)
                {
                    // An oversized datagram or an ICMP error left over from an earlier send concerns one
                    // datagram: carry on. Anything else may persist, so back off a little rather than spin.
                    if (ex.SocketErrorCode == SocketError.MessageSize) LogOversized();
                    else if (ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.NetworkReset))
                        await Task.Delay(10, token).ConfigureAwait(false);
                    continue;
                }

                if (result.ReceivedBytes > Options.MaxDatagramSize)
                {
                    LogOversized();
                    continue;
                }

                var data = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                var dropped = Options.DropRate > 0 && _random.NextDouble() < Options.DropRate;
                Transcript.Add(new(TranscriptDirection.Received, result.RemoteEndPoint, data, dropped, DateTimeOffset.UtcNow));
                Log($"<- {result.RemoteEndPoint} {TranscriptBase.Escape(Options.Encoding.GetString(data))}{(dropped ? " (dropped)" : "")}");
                if (!dropped) await _inbox.Writer.WriteAsync(new Datagram(this, data, result.RemoteEndPoint, token), token).ConfigureAwait(false);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _inbox.Writer.TryComplete();
        }
    }

    private Task DispatchLoopAsync() =>
        Parallel.ForEachAsync(_inbox.Reader.ReadAllAsync(), new ParallelOptions { MaxDegreeOfParallelism = Options.MaxConcurrency }, async (datagram, _) =>
        {
            if (_stopping.IsCancellationRequested) return; // queued before the stop: drop it
            try
            {
                await _handler(datagram).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _errors.Enqueue(ex);
                Transcript.Pulse();
                Log($"handler failed: {ex.GetType().Name}: {ex.Message}");
            }
        });

    private async Task StopCoreAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        await _receiveLoop.ConfigureAwait(false);
        // The inbox is complete now; datagrams still queued are skipped.
        await Task.WhenAny(_dispatchLoop, Task.Delay(Options.ShutdownTimeout)).ConfigureAwait(false);
    }

    private void LogOversized() => Log($"<- datagram larger than MaxDatagramSize ({Options.MaxDatagramSize}) discarded");

    private void Log(string line)
    {
        if (Options.Log is { } log) ConnectionTranscript.SafeLog(log, line);
    }

    public override string ToString() => $"udp://{EndPoint}";
}
