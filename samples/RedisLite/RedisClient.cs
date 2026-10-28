using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace RedisLite;

/// <summary>When <see cref="RedisClient.SetAsync(string, string, TimeSpan?, SetCondition, CancellationToken)"/> should write.</summary>
public enum SetCondition
{
    /// <summary>Always write.</summary>
    Always,

    /// <summary>Only if the key does not exist (<c>NX</c>).</summary>
    IfNotExists,

    /// <summary>Only if the key already exists (<c>XX</c>).</summary>
    IfExists,
}

/// <summary>A message delivered to a subscription.</summary>
/// <param name="Channel">The channel it was published to.</param>
/// <param name="Payload">The message bytes.</param>
public sealed record RedisMessage(string Channel, byte[] Payload)
{
    /// <summary>The payload decoded as UTF-8.</summary>
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// A resilient Redis client. Commands from concurrent callers share one pipelined connection,
/// which is reopened after failures. Idempotent commands are retried; non-idempotent ones
/// (such as <c>INCRBY</c> and <c>PUBLISH</c>) are retried only if the failure happened before
/// they could have been sent.
/// </summary>
/// <example>
/// <code>
/// await using var redis = new RedisClient("redis://localhost");
/// await redis.SetAsync("greeting", "hello", expiry: TimeSpan.FromMinutes(5));
/// var greeting = await redis.GetAsync("greeting");
///
/// await foreach (var message in redis.SubscribeAsync(["news"], cancellationToken))
///     Console.WriteLine($"{message.Channel}: {message.Text}");
/// </code>
/// </example>
public sealed class RedisClient : IAsyncDisposable
{
    private static readonly HashSet<string> Idempotent = new(StringComparer.OrdinalIgnoreCase)
    {
        "PING", "ECHO", "GET", "SET", "DEL", "EXISTS", "TTL", "PTTL", "EXPIRE", "PEXPIRE", "MGET", "STRLEN", "TYPE", "KEYS", "DBSIZE",
    };

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private RedisConnection? _connection;
    private int _connectionsOpened;

    /// <summary>Create a client; it connects on first use.</summary>
    /// <param name="options">Connection settings.</param>
    public RedisClient(RedisOptions options) => Options = options;

    /// <summary>Create a client from a URL; see <see cref="RedisOptions.Parse"/>.</summary>
    /// <param name="url">For example <c>redis://localhost:6379/0</c>.</param>
    public RedisClient(string url) : this(RedisOptions.Parse(url))
    {
    }

    /// <summary>The settings this client uses.</summary>
    public RedisOptions Options { get; }

    /// <summary>How many connections have been opened so far, counting reconnects.</summary>
    public int ConnectionsOpened => Volatile.Read(ref _connectionsOpened);

    /// <summary>Send <c>PING</c>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The reply, normally <c>PONG</c>.</returns>
    public async Task<string> PingAsync(CancellationToken cancellationToken = default) =>
        ExpectText(await ExecuteAsync(["PING"], cancellationToken).ConfigureAwait(false));

    /// <summary>Get a string value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value, or null if the key does not exist.</returns>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        (await GetBytesAsync(key, cancellationToken).ConfigureAwait(false)) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Get a binary value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes, or null if the key does not exist.</returns>
    public async Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(["GET", key], cancellationToken).ConfigureAwait(false) switch
        {
            RespBulkString bulk => bulk.Value,
            var other => throw Unexpected(other),
        };

    /// <summary>Set a string value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="expiry">Time to live, rounded to milliseconds; none when null.</param>
    /// <param name="condition">Whether to write only when the key does or does not exist.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>False if <paramref name="condition"/> prevented the write.</returns>
    public async Task<bool> SetAsync(string key, string value, TimeSpan? expiry = null, SetCondition condition = SetCondition.Always, CancellationToken cancellationToken = default)
    {
        List<string> command = ["SET", key, value];
        if (expiry is { } ttl) command.AddRange(["PX", Math.Max(1, (long)ttl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)]);
        if (condition != SetCondition.Always) command.Add(condition == SetCondition.IfNotExists ? "NX" : "XX");
        return await ExecuteAsync(command, cancellationToken).ConfigureAwait(false) switch
        {
            RespSimpleString { Value: "OK" } => true,
            RespBulkString { IsNil: true } => false,
            var other => throw Unexpected(other),
        };
    }

    /// <summary>Delete keys.</summary>
    /// <param name="keys">The keys.</param>
    /// <returns>How many keys existed.</returns>
    public Task<long> DeleteAsync(params string[] keys) => DeleteAsync(keys, CancellationToken.None);

    /// <summary>Delete keys.</summary>
    /// <param name="keys">The keys.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many keys existed.</returns>
    public async Task<long> DeleteAsync(IEnumerable<string> keys, CancellationToken cancellationToken) =>
        ExpectInteger(await ExecuteAsync(["DEL", .. keys], cancellationToken).ConfigureAwait(false));

    /// <summary>Atomically add to an integer value (<c>INCRBY</c>). Not retried once it may have been sent.</summary>
    /// <param name="key">The key; a missing key counts as 0.</param>
    /// <param name="by">The amount to add.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new value.</returns>
    public async Task<long> IncrementAsync(string key, long by = 1, CancellationToken cancellationToken = default) =>
        ExpectInteger(await ExecuteAsync(["INCRBY", key, by.ToString(CultureInfo.InvariantCulture)], cancellationToken).ConfigureAwait(false));

    /// <summary>Publish a message. Not retried once it may have been sent, to avoid duplicates.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many subscribers received it.</returns>
    public async Task<long> PublishAsync(string channel, string message, CancellationToken cancellationToken = default) =>
        ExpectInteger(await ExecuteAsync(["PUBLISH", channel, message], cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Subscribe on a dedicated connection and stream messages until the caller stops enumerating,
    /// cancels, or the server closes the connection.
    /// </summary>
    /// <param name="channels">Channels to subscribe to.</param>
    /// <param name="cancellationToken">Ends the subscription.</param>
    /// <returns>The messages, in delivery order.</returns>
    /// <exception cref="RedisConnectionException">The subscription connection failed.</exception>
    public async IAsyncEnumerable<RedisMessage> SubscribeAsync(IReadOnlyList<string> channels, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (channels.Count == 0) throw new ArgumentException("Subscribe to at least one channel.", nameof(channels));
        await using var transport = await RedisTransport.ConnectAsync(Options, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _connectionsOpened);
        await transport.WriteAsync(RespWriter.EncodeCommand(["SUBSCRIBE", .. channels]), cancellationToken).ConfigureAwait(false);

        while (await ReadPushAsync(transport, cancellationToken).ConfigureAwait(false) is { } push)
        {
            switch (push)
            {
                case RespArray { Items: [RespBulkString { Text: "message" }, RespBulkString { Text: { } channel }, RespBulkString { Value: { } payload }] }:
                    yield return new RedisMessage(channel, payload);
                    break;
                case RespArray { Items: [RespBulkString { Text: "subscribe" }, ..] }:
                    break; // Confirmation of one channel.
                case RespError error:
                    throw new RedisServerException(error);
                default:
                    throw Unexpected(push);
            }
        }
    }

    /// <summary>Run any command. Error replies are thrown as <see cref="RedisServerException"/>.</summary>
    /// <param name="command">Command name and arguments, for example <c>["HSET", "h", "f", "v"]</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The reply.</returns>
    public async Task<RespValue> ExecuteAsync(IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        if (command.Count == 0) throw new ArgumentException("A command needs a name.", nameof(command));
        var idempotent = Idempotent.Contains(command[0]);
        for (var attempt = 0; ; attempt++)
        {
            RedisConnection connection;
            try
            {
                connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RedisConnectionException) when (attempt < Options.MaxRetries)
            {
                await BackOffAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue; // Nothing was sent, so any command can be retried.
            }

            RespValue reply;
            try
            {
                reply = await connection.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            catch (RedisConnectionException) when (idempotent && attempt < Options.MaxRetries)
            {
                await BackOffAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return reply is RespError error ? throw new RedisServerException(error) : reply;
        }
    }

    /// <summary>Close the shared connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<RedisConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) is { IsConnected: true } current) return current;
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsConnected: true } existing) return existing;
            if (_connection is { } broken) await broken.DisposeAsync().ConfigureAwait(false);
            _connection = await RedisConnection.ConnectAsync(Options, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _connectionsOpened);
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private Task BackOffAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(Options.RetryBackoff * Math.Pow(2, attempt), cancellationToken);

    private async ValueTask<RespValue?> ReadPushAsync(RedisTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            return await transport.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new RedisConnectionException($"The subscription connection failed: {ex.Message}", ex);
        }
    }

    private static string ExpectText(RespValue reply) => reply switch
    {
        RespSimpleString s => s.Value,
        RespBulkString { Text: { } text } => text,
        _ => throw Unexpected(reply),
    };

    private static long ExpectInteger(RespValue reply) =>
        reply is RespInteger i ? i.Value : throw Unexpected(reply);

    private static RespProtocolException Unexpected(RespValue reply) => new($"Unexpected reply {reply}.");
}
