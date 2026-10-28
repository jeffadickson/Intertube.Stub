using System.Buffers;
using System.Collections.Concurrent;

namespace RedisLite;

/// <summary>
/// A single pipelined connection. Any number of callers can issue commands concurrently: each
/// request is written as soon as it arrives, without waiting for earlier replies, and a background
/// loop hands replies back in order. Any failure (I/O error, protocol error, timeout) fails every
/// outstanding command and leaves the connection permanently closed; <see cref="RedisClient"/> then
/// opens a new one.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly RedisTransport _transport;
    private readonly RedisOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentQueue<TaskCompletionSource<RespValue>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _readLoop;
    private Exception? _failure;

    private RedisConnection(RedisTransport transport, RedisOptions options)
    {
        _transport = transport;
        _options = options;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>False once the connection has failed or been disposed.</summary>
    public bool IsConnected => Volatile.Read(ref _failure) is null;

    /// <summary>Connect and run the handshake described by <paramref name="options"/>.</summary>
    /// <param name="options">Connection settings.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <returns>An open connection.</returns>
    /// <exception cref="RedisConnectionException">The server could not be reached.</exception>
    /// <exception cref="RedisServerException">The handshake was rejected, for example with a wrong password.</exception>
    public static async Task<RedisConnection> ConnectAsync(RedisOptions options, CancellationToken cancellationToken = default)
    {
        var transport = await RedisTransport.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        return new RedisConnection(transport, options);
    }

    /// <summary>Wrap an already-open stream (test seam for failure modes a socket cannot produce on demand).</summary>
    internal static RedisConnection Create(Stream stream, RedisOptions options) => new(new RedisTransport(stream), options);

    /// <summary>Send one command and await its reply. Error replies are returned, not thrown.</summary>
    /// <param name="command">Command name and arguments.</param>
    /// <param name="cancellationToken">Stops waiting; a reply that arrives later is discarded.</param>
    /// <returns>The reply.</returns>
    /// <exception cref="RedisConnectionException">The connection failed before the reply arrived.</exception>
    public async Task<RespValue> ExecuteAsync(IReadOnlyList<string> command, CancellationToken cancellationToken = default) =>
        (await PipelineAsync([command], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Send several commands in a single write and await all their replies, in order.</summary>
    /// <param name="commands">The commands.</param>
    /// <param name="cancellationToken">Stops waiting; replies that arrive later are discarded.</param>
    /// <returns>One reply per command.</returns>
    /// <exception cref="RedisConnectionException">The connection failed before every reply arrived.</exception>
    public async Task<RespValue[]> PipelineAsync(IReadOnlyList<IReadOnlyList<string>> commands, CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var command in commands) RespWriter.WriteCommand(buffer, command);
        var replies = commands.Select(_ => new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFailed();
            // Enqueue before writing so the reader can never see a reply without its waiter.
            foreach (var reply in replies) _pending.Enqueue(reply);
            await _transport.WriteAsync(buffer.WrittenMemory, _closing.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException || (ex is OperationCanceledException && _closing.IsCancellationRequested))
        {
            throw Fail(new RedisConnectionException($"Sending to the server failed: {ex.Message}", ex));
        }
        finally
        {
            _writeLock.Release();
        }

        // If the connection failed between our check and our enqueue, nobody else will fail our waiters.
        if (Volatile.Read(ref _failure) is { } failure) FailPending(failure);

        try
        {
            return await Task.WhenAll(replies.Select(r => r.Task)).WaitAsync(_options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw Fail(new RedisTimeoutException($"No reply within {_options.CommandTimeout.TotalMilliseconds:0} ms."));
        }
    }

    /// <summary>Close the connection, failing any outstanding commands.</summary>
    public async ValueTask DisposeAsync()
    {
        Fail(new RedisConnectionException("The connection was closed."));
        await _readLoop.ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var value = await _transport.ReadAsync(_closing.Token).ConfigureAwait(false);
                if (value is null)
                {
                    Fail(new RedisConnectionException("The server closed the connection."));
                    return;
                }
                if (!_pending.TryDequeue(out var waiter))
                {
                    Fail(new RespProtocolException($"Unsolicited reply {value}."));
                    return;
                }
                waiter.TrySetResult(value);
            }
        }
        catch (Exception ex)
        {
            Fail(ex switch
            {
                RedisException redis => redis,
                OperationCanceledException => new RedisConnectionException("The connection was closed.", ex),
                _ => new RedisConnectionException($"Reading from the server failed: {ex.Message}", ex),
            });
        }
    }

    private Exception Fail(Exception failure)
    {
        if (Interlocked.CompareExchange(ref _failure, failure, null) is null)
            _closing.Cancel();
        var actual = Volatile.Read(ref _failure)!;
        FailPending(actual);
        return actual;
    }

    private void FailPending(Exception failure)
    {
        while (_pending.TryDequeue(out var waiter)) waiter.TrySetException(failure);
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure) throw failure;
    }
}
