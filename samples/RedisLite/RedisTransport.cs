using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace RedisLite;

/// <summary>One socket (TCP, TLS or Unix) with RESP reading and writing. Not thread-safe.</summary>
internal sealed class RedisTransport : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly PipeReader _reader;

    internal RedisTransport(Stream stream)
    {
        _stream = stream;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    }

    /// <summary>Connect, negotiate TLS and run the AUTH/SELECT/SETNAME handshake as one pipelined write.</summary>
    public static async Task<RedisTransport> ConnectAsync(RedisOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        var token = timeout.Token;

        Socket socket;
        try
        {
            if (options.UnixSocketPath is { } path)
            {
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await ConnectOrDisposeAsync(socket, s => s.ConnectAsync(new UnixDomainSocketEndPoint(path), token)).ConfigureAwait(false);
            }
            else
            {
                socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await ConnectOrDisposeAsync(socket, s => s.ConnectAsync(options.Host, options.Port, token)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RedisConnectionException($"Could not connect to {Describe(options)}: {ex.Message}", ex);
        }

        Stream stream = new NetworkStream(socket, ownsSocket: true);
        var transport = default(RedisTransport);
        try
        {
            if (options.UseTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, options.ServerCertificateValidation);
                stream = ssl;
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = options.Host }, token).ConfigureAwait(false);
            }
            transport = new RedisTransport(stream);
            await transport.HandshakeAsync(options, token).ConfigureAwait(false);
            return transport;
        }
        catch (Exception ex)
        {
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
            else await stream.DisposeAsync().ConfigureAwait(false);
            if (ex is RedisException || cancellationToken.IsCancellationRequested) throw;
            throw ex is AuthenticationException or IOException or OperationCanceledException
                ? new RedisConnectionException($"Could not set up the connection to {Describe(options)}: {ex.Message}", ex)
                : ex;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read the next complete value, or null if the server closed the connection cleanly between values.</summary>
    public async ValueTask<RespValue?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            try
            {
                if (RespReader.TryRead(ref buffer, out var value))
                {
                    _reader.AdvanceTo(buffer.Start);
                    return value;
                }
            }
            catch (RespProtocolException)
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                throw;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
                return buffer.IsEmpty ? null : throw new RedisConnectionException($"The server closed the connection in the middle of a reply ({buffer.Length} bytes received).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.CompleteAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    internal static string Describe(RedisOptions options) =>
        options.UnixSocketPath ?? $"{options.Host}:{options.Port}";

    private async Task HandshakeAsync(RedisOptions options, CancellationToken cancellationToken)
    {
        var commands = new List<string[]>();
        if (options.Password is { } password)
            commands.Add(options.User is { } user ? ["AUTH", user, password] : ["AUTH", password]);
        if (options.Database != 0) commands.Add(["SELECT", options.Database.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.ClientName is { } name) commands.Add(["CLIENT", "SETNAME", name]);
        if (commands.Count == 0) return;

        var buffer = new ArrayBufferWriter<byte>();
        foreach (var command in commands) RespWriter.WriteCommand(buffer, command);
        await WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        // Read every reply before judging any, so the stream stays in sync even if one fails.
        var replies = new List<RespValue>();
        foreach (var _ in commands)
            replies.Add(await ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new RedisConnectionException("The server closed the connection during the handshake."));
        foreach (var reply in replies)
            if (reply is RespError error) throw new RedisServerException(error);
    }

    private static async Task ConnectOrDisposeAsync(Socket socket, Func<Socket, ValueTask> connect)
    {
        try
        {
            await connect(socket).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
