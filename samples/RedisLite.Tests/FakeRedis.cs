using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Intertube.Stub;

namespace RedisLite.Tests;

/// <summary>
/// An in-memory Redis built on Intertube.Stub, used as the test double for RedisLite. It speaks real
/// RESP, so the client under test is exercised end to end over a socket. Intertube.Stub adds what a fake
/// cannot do alone: transcripts, fault injection, TLS, Unix sockets and per-connection misbehaviour.
/// </summary>
public sealed class FakeRedis : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(int Db, string Key), (byte[] Value, DateTimeOffset? ExpiresAt)> _data = [];
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Session, byte>> _channels = new();
    private readonly ConcurrentQueue<string[]> _commands = new();
    private readonly string? _user;
    private readonly string? _password;
    private readonly TlsOptions? _tls;

    private FakeRedis(string? user, string? password, TlsOptions? tls)
    {
        _user = user;
        _password = password;
        _tls = tls;
    }

    /// <summary>The Intertube.Stub server, for transcripts, errors and connection counts.</summary>
    public StreamServer Server { get; private set; } = null!;

    /// <summary>Client options pointing at this server (with the password and certificate trust filled in).</summary>
    public RedisOptions ClientOptions { get; private set; } = null!;

    /// <summary>Every command received, decoded as text, in arrival order.</summary>
    public IReadOnlyList<string[]> Commands => [.. _commands];

    /// <summary>
    /// Runs before each command. Return true to stop serving this connection (after, say,
    /// <see cref="Connection.Abort"/> or <see cref="Connection.HangAsync"/>).
    /// </summary>
    public Func<Connection, string[], Task<bool>>? Intercept { get; set; }

    public static FakeRedis Start(ServerOptions? options = null, string? user = null, string? password = null, bool unixSocket = false)
    {
        var fake = new FakeRedis(user, password, options?.Tls);
        if (unixSocket)
        {
            var server = UnixServer.Listen(null, fake.HandleAsync, options);
            fake.Server = server;
            fake.ClientOptions = new RedisOptions { UnixSocketPath = server.Path };
        }
        else
        {
            var server = TcpServer.Listen(0, fake.HandleAsync, options);
            fake.Server = server;
            fake.ClientOptions = new RedisOptions { Host = server.Host, Port = server.Port };
        }

        fake.ClientOptions = fake.ClientOptions with
        {
            User = user,
            Password = password,
            UseTls = fake._tls is not null,
            ServerCertificateValidation = fake._tls?.TrustServerCertificate,
            RetryBackoff = TimeSpan.FromMilliseconds(5),
            CommandTimeout = TimeSpan.FromSeconds(3),
        };
        return fake;
    }

    public int CountOf(string command) => Commands.Count(c => c[0].Equals(command, StringComparison.OrdinalIgnoreCase));

    public ValueTask DisposeAsync() => Server.DisposeAsync();

    private async Task HandleAsync(Connection connection)
    {
        var session = new Session(connection, authenticated: _password is null);
        try
        {
            while (true)
            {
                var result = await connection.Input.ReadAsync(connection.CancellationToken);
                var buffer = result.Buffer;
                var replies = new ArrayBufferWriter<byte>();
                var stop = false;

                while (!stop && TryReadRequest(ref buffer, replies, out var command))
                {
                    if (command is null)
                    {
                        stop = true; // Protocol error: the error reply is already queued.
                        break;
                    }
                    _commands.Enqueue(command.Select(Text).ToArray());

                    if (Intercept is { } intercept)
                    {
                        await session.FlushAsync(replies);
                        if (await intercept(connection, command.Select(Text).ToArray()))
                            return;
                    }

                    var reply = await ExecuteAsync(session, command);
                    RespWriter.Write(replies, reply);
                    stop = Text(command[0]).Equals("QUIT", StringComparison.OrdinalIgnoreCase);
                }

                connection.Input.AdvanceTo(buffer.Start, buffer.End);
                await session.FlushAsync(replies);
                if (stop || result.IsCompleted) return;
            }
        }
        finally
        {
            foreach (var subscribers in _channels.Values) subscribers.TryRemove(session, out _);
        }
    }

    private static bool TryReadRequest(ref System.Buffers.ReadOnlySequence<byte> buffer, IBufferWriter<byte> replies, out byte[][]? command)
    {
        command = null;
        try
        {
            if (!RespReader.TryRead(ref buffer, out var request)) return false;
            if (request is RespArray { Items: { Count: > 0 } items } && items.All(i => i is RespBulkString { IsNil: false }))
            {
                command = [.. items.Cast<RespBulkString>().Select(b => b.Value!)];
                return true;
            }
            RespWriter.Write(replies, new RespError("ERR Protocol error: expected an array of bulk strings"));
            return true;
        }
        catch (RespProtocolException ex)
        {
            RespWriter.Write(replies, new RespError($"ERR Protocol error: {ex.Message}"));
            buffer = buffer.Slice(buffer.End);
            return true;
        }
    }

    private async ValueTask<RespValue> ExecuteAsync(Session session, byte[][] args)
    {
        var name = Text(args[0]).ToUpperInvariant();
        if (!session.Authenticated && name is not ("AUTH" or "QUIT"))
            return new RespError("NOAUTH Authentication required.");

        switch (name, args.Length)
        {
            case ("PING", 1): return new RespSimpleString("PONG");
            case ("PING", 2): return new RespBulkString(args[1]);
            case ("ECHO", 2): return new RespBulkString(args[1]);
            case ("QUIT", _): return new RespSimpleString("OK");

            case ("AUTH", 2 or 3):
            {
                var user = args.Length == 3 ? Text(args[1]) : "default";
                var password = Text(args[^1]);
                if (_password is null) return new RespError("ERR AUTH <password> called without any password configured for the default user.");
                if (password != _password || user != (_user ?? "default"))
                    return new RespError("WRONGPASS invalid username-password pair or user is disabled.");
                session.Authenticated = true;
                return Ok;
            }

            case ("SELECT", 2):
                if (!int.TryParse(Text(args[1]), CultureInfo.InvariantCulture, out var db) || db is < 0 or > 15)
                    return new RespError("ERR DB index is out of range");
                session.Database = db;
                return Ok;

            case ("CLIENT", 3) when Text(args[1]).Equals("SETNAME", StringComparison.OrdinalIgnoreCase):
                session.Name = Text(args[2]);
                return Ok;

            case ("GET", 2):
                lock (_gate) return Get(session, Text(args[1])) is { } value ? new RespBulkString(value) : RespValue.Nil;

            case ("SET", >= 3):
                return Set(session, args);

            case ("DEL", >= 2):
                lock (_gate) return new RespInteger(args.Skip(1).Count(k => Get(session, Text(k)) is not null && _data.Remove((session.Database, Text(k)))));

            case ("EXISTS", >= 2):
                lock (_gate) return new RespInteger(args.Skip(1).Count(k => Get(session, Text(k)) is not null));

            case ("INCR", 2):
                return IncrementBy(session, Text(args[1]), "1");

            case ("INCRBY", 3):
                return IncrementBy(session, Text(args[1]), Text(args[2]));

            case ("DBSIZE", 1):
                lock (_gate) return new RespInteger(_data.Keys.Count(k => k.Db == session.Database));

            case ("PUBLISH", 3):
                return new RespInteger(await PublishAsync(Text(args[1]), args[2]));

            case ("SUBSCRIBE", >= 2):
            {
                var confirmations = new List<RespValue>();
                foreach (var channel in args.Skip(1).Select(Text))
                {
                    _channels.GetOrAdd(channel, _ => new()).TryAdd(session, 0);
                    session.Subscriptions.Add(channel);
                    confirmations.Add(new RespArray([RespValue.Bulk("subscribe"), RespValue.Bulk(channel), new RespInteger(session.Subscriptions.Count)]));
                }
                // Each confirmation is its own push message; write all but the last directly.
                foreach (var confirmation in confirmations.SkipLast(1)) await session.WriteAsync(RespWriter.Encode(confirmation));
                return confirmations[^1];
            }

            default:
                return new RespError($"ERR unknown command '{Text(args[0])}', with args beginning with: {string.Join(" ", args.Skip(1).Take(3).Select(a => $"'{Text(a)}'"))}");
        }
    }

    private RespValue Set(Session session, byte[][] args)
    {
        var key = Text(args[1]);
        TimeSpan? ttl = null;
        bool nx = false, xx = false;
        for (var i = 3; i < args.Length; i++)
        {
            switch (Text(args[i]).ToUpperInvariant())
            {
                case "NX": nx = true; break;
                case "XX": xx = true; break;
                case "EX" or "PX" when i + 1 < args.Length && long.TryParse(Text(args[i + 1]), CultureInfo.InvariantCulture, out var amount) && amount > 0:
                    ttl = Text(args[i]).Equals("EX", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(amount) : TimeSpan.FromMilliseconds(amount);
                    i++;
                    break;
                default:
                    return new RespError("ERR syntax error");
            }
        }
        if (nx && xx) return new RespError("ERR syntax error");

        lock (_gate)
        {
            var exists = Get(session, key) is not null;
            if ((nx && exists) || (xx && !exists)) return RespValue.Nil;
            _data[(session.Database, key)] = (args[2], ttl is { } t ? DateTimeOffset.UtcNow + t : null);
            return Ok;
        }
    }

    private RespValue IncrementBy(Session session, string key, string amountText)
    {
        if (!long.TryParse(amountText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
            return new RespError("ERR value is not an integer or out of range");
        lock (_gate)
        {
            long current = 0;
            if (Get(session, key) is { } existing && !long.TryParse(Encoding.UTF8.GetString(existing), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out current))
                return new RespError("ERR value is not an integer or out of range");
            long next;
            try
            {
                next = checked(current + amount);
            }
            catch (OverflowException)
            {
                return new RespError("ERR increment or decrement would overflow");
            }
            _data[(session.Database, key)] = (Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture)), _data.GetValueOrDefault((session.Database, key)).ExpiresAt);
            return new RespInteger(next);
        }
    }

    private async Task<int> PublishAsync(string channel, byte[] payload)
    {
        if (!_channels.TryGetValue(channel, out var subscribers)) return 0;
        var message = RespWriter.Encode(new RespArray([RespValue.Bulk("message"), RespValue.Bulk(channel), new RespBulkString(payload)]));
        var delivered = 0;
        foreach (var subscriber in subscribers.Keys)
        {
            try
            {
                await subscriber.WriteAsync(message);
                delivered++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ConnectionTerminatedException)
            {
                subscribers.TryRemove(subscriber, out _);
            }
        }
        return delivered;
    }

    // Callers hold _gate.
    private byte[]? Get(Session session, string key)
    {
        if (!_data.TryGetValue((session.Database, key), out var entry)) return null;
        if (entry.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            _data.Remove((session.Database, key));
            return null;
        }
        return entry.Value;
    }

    private static readonly RespSimpleString Ok = new("OK");

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private sealed class Session(Connection connection, bool authenticated)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public bool Authenticated { get; set; } = authenticated;
        public int Database { get; set; }
        public string? Name { get; set; }
        public HashSet<string> Subscriptions { get; } = [];

        public async Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            await _writeLock.WaitAsync();
            try
            {
                await connection.WriteAsync(data);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task FlushAsync(ArrayBufferWriter<byte> pending)
        {
            if (pending.WrittenCount == 0) return;
            await WriteAsync(pending.WrittenMemory.ToArray());
            pending.ResetWrittenCount();
        }
    }
}
