using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Intertube.Stub.Internal;

namespace Intertube.Stub;

/// <summary>
/// Thrown when a fault deliberately cut the connection. Servers treat it as a normal end of the handler.
/// </summary>
public sealed class ConnectionTerminatedException(string message) : IOException(message);

/// <summary>
/// One accepted (or, via <c>ConnectAsync</c>, client-side) stream connection with buffered line,
/// delimiter and exact-length reads that never impose a framing of their own. All traffic is
/// recorded in <see cref="Transcript"/>. Like <see cref="Stream"/>, a connection supports one reader
/// and one writer at a time.
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

    private readonly Socket _socket;
    private readonly ServerOptions _options;
    private readonly bool _isServer;
    private Stream _transport;
    private TapStream _tap;
    private PipeReader _reader;
    private PipeWriter? _writer;
    private long _bytesWritten;
    private readonly CancellationTokenSource _terminatedSource = new();
    private int _terminated;

    internal Connection(Socket socket, bool isServer, ServerOptions options, ConnectionTranscript transcript, CancellationToken lifetime)
    {
        _socket = socket;
        _isServer = isServer;
        _options = options;
        Transcript = transcript;
        CancellationToken = lifetime;
        Faults = isServer ? options.Faults : FaultOptions.None;
        RemoteEndPoint = socket.RemoteEndPoint;
        LocalEndPoint = socket.LocalEndPoint;
        _transport = new NetworkStream(socket, ownsSocket: false);
        (_tap, _reader) = CreateReader(_transport);
    }

    /// <summary>Connection number, starting at 1 for each server.</summary>
    public int Id => Transcript.Id;

    public EndPoint? RemoteEndPoint { get; }

    public EndPoint? LocalEndPoint { get; }

    /// <summary>Cancelled when the owning server shuts down. Reads observe it automatically.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Faults for this connection; starts as <see cref="ServerOptions.Faults"/> and can be changed at any time.</summary>
    public FaultOptions Faults { get; set; }

    /// <summary>Traffic recorded on this connection.</summary>
    public ConnectionTranscript Transcript { get; }

    /// <summary>True once TLS is active, either implicitly or after <see cref="UpgradeToTlsAsync"/>.</summary>
    public bool IsTls => _transport is SslStream;

    /// <summary>The underlying socket, for options this API does not cover.</summary>
    public Socket Socket => _socket;

    /// <summary>
    /// The buffered input as a <see cref="PipeReader"/>, for parsing binary protocols with
    /// <see cref="SequenceReader{T}"/>. It shares its buffer with the Read* helpers, so they can be mixed.
    /// A TLS upgrade replaces it.
    /// </summary>
    public PipeReader Input => _reader;

    /// <summary>Output as a <see cref="PipeWriter"/>; writes go through faults and the transcript when flushed.</summary>
    public PipeWriter Output => _writer ??= PipeWriter.Create(Stream, new StreamPipeWriterOptions(leaveOpen: true));

    /// <summary>
    /// A <see cref="System.IO.Stream"/> view sharing this connection's buffer, faults and transcript,
    /// so it can be handed to StreamReader, BinaryReader, protocol libraries and so on. Disposing it does nothing.
    /// </summary>
    public Stream Stream => field ??= new ConnectionStream(this);

    // ---- Reading -------------------------------------------------------------------------------

    /// <summary>Read one line terminated by LF or CRLF, without the terminator. Returns null at end of stream.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFrameAsync("\n"u8.ToArray(), returnPartialAtEnd: true, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        var length = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
        return _options.Encoding.GetString(bytes, 0, length);
    }

    /// <summary>Read lines until the peer closes the connection.</summary>
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            yield return line;
    }

    /// <summary>
    /// Read up to (not including) <paramref name="delimiter"/> and consume the delimiter.
    /// Returns null if the stream ends first; any unterminated data stays buffered.
    /// </summary>
    public ValueTask<byte[]?> ReadUntilAsync(ReadOnlyMemory<byte> delimiter, CancellationToken cancellationToken = default)
    {
        if (delimiter.IsEmpty) throw new ArgumentException("Delimiter must not be empty.", nameof(delimiter));
        return ReadFrameAsync(delimiter, returnPartialAtEnd: false, cancellationToken);
    }

    /// <summary>Text form of <see cref="ReadUntilAsync(ReadOnlyMemory{byte}, CancellationToken)"/>, for example <c>"\r\n.\r\n"</c> for SMTP DATA.</summary>
    public async ValueTask<string?> ReadUntilAsync(string delimiter, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadUntilAsync(_options.Encoding.GetBytes(delimiter), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : _options.Encoding.GetString(bytes);
    }

    /// <summary>Read exactly <paramref name="count"/> bytes, or throw <see cref="EndOfStreamException"/>.</summary>
    public async ValueTask<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0) return [];
        while (true)
        {
            var result = await ReadPipeAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= count)
            {
                var bytes = buffer.Slice(0, count).ToArray();
                _reader.AdvanceTo(buffer.GetPosition(count));
                return bytes;
            }
            _reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
                throw new EndOfStreamException($"Expected {count} bytes but the stream ended after {buffer.Length}.");
        }
    }

    /// <summary>Read whatever is available (at least one byte), or 0 at end of stream.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.IsEmpty) return 0;
        var result = await ReadPipeAsync(cancellationToken).ConfigureAwait(false);
        var buffer = result.Buffer;
        var count = (int)Math.Min(buffer.Length, destination.Length);
        buffer.Slice(0, count).CopyTo(destination.Span);
        _reader.AdvanceTo(buffer.GetPosition(count));
        return count;
    }

    /// <summary>Read until the peer closes its side.</summary>
    public async ValueTask<byte[]> ReadToEndAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await ReadPipeAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsCompleted)
            {
                var bytes = result.Buffer.ToArray();
                _reader.AdvanceTo(result.Buffer.End);
                return bytes;
            }
            _reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    // ---- Writing -------------------------------------------------------------------------------

    /// <summary>Write bytes, applying <see cref="Faults"/>.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var faults = Faults;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
        var token = linked.Token;

        if (faults.WriteDelay > TimeSpan.Zero) await Task.Delay(faults.WriteDelay, token).ConfigureAwait(false);
        if (faults.TransformWrite is { } transform) data = transform(data);

        var chunkSize = faults.FragmentSize is > 0 and var size ? size : Math.Max(data.Length, 1);
        for (var offset = 0; offset < data.Length;)
        {
            var piece = data.Slice(offset, Math.Min(chunkSize, data.Length - offset));
            var cut = faults.DisconnectAfterBytesWritten is { } limit && _bytesWritten + piece.Length >= limit;
            if (cut) piece = piece[..(int)Math.Max(0, faults.DisconnectAfterBytesWritten!.Value - _bytesWritten)];

            if (!piece.IsEmpty)
            {
                await _tap.WriteAsync(piece, token).ConfigureAwait(false);
                await _tap.FlushAsync(token).ConfigureAwait(false);
                _bytesWritten += piece.Length;
                offset += piece.Length;
            }

            if (cut)
            {
                Transcript.RecordEvent($"fault: disconnect after {_bytesWritten} bytes written");
                if (faults.DisconnectMode == DisconnectMode.Reset) Abort();
                else await CloseAsync().ConfigureAwait(false);
                throw new ConnectionTerminatedException($"Fault injection disconnected the connection after {_bytesWritten} bytes.");
            }

            if (offset < data.Length && faults.FragmentDelay > TimeSpan.Zero)
                await Task.Delay(faults.FragmentDelay, token).ConfigureAwait(false);
        }
    }

    /// <summary>Write text in <see cref="ServerOptions.Encoding"/> with no terminator.</summary>
    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default) =>
        WriteAsync(_options.Encoding.GetBytes(text), cancellationToken);

    /// <summary>Write text followed by <see cref="ServerOptions.NewLine"/>.</summary>
    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default) =>
        WriteAsync(line + _options.NewLine, cancellationToken);

    /// <summary>Write several lines in a single write.</summary>
    public ValueTask WriteLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default) =>
        WriteAsync(string.Concat(lines.Select(l => l + _options.NewLine)), cancellationToken);

    // ---- Lifecycle -----------------------------------------------------------------------------

    /// <summary>
    /// Switch to TLS mid-connection (STARTTLS). The server side uses <paramref name="options"/> or
    /// <see cref="ServerOptions.Tls"/>; a client connection trusts the server's certificate. No unread
    /// input may be buffered.
    /// </summary>
    public async Task UpgradeToTlsAsync(TlsOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= _options.Tls ?? throw new InvalidOperationException("No TlsOptions given and none configured on the server.");
        if (IsTls) throw new InvalidOperationException("The connection is already using TLS.");
        if (_reader.TryRead(out var pending))
        {
            var buffered = pending.Buffer.Length;
            _reader.AdvanceTo(pending.Buffer.Start);
            if (buffered > 0)
                throw new InvalidOperationException($"Cannot start TLS with {buffered} unread bytes buffered.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
        var ssl = new SslStream(_transport, leaveInnerStreamOpen: false);
        if (_isServer) await ssl.AuthenticateAsServerAsync(options.ToServerOptions(), linked.Token).ConfigureAwait(false);
        else await ssl.AuthenticateAsClientAsync(options.ToClientOptions("localhost"), linked.Token).ConfigureAwait(false);

        await _reader.CompleteAsync().ConfigureAwait(false);
        _transport = ssl;
        (_tap, _reader) = CreateReader(ssl);
        _writer = null;
        Transcript.RecordEvent($"TLS established ({ssl.SslProtocol})");
    }

    /// <summary>
    /// Read and discard input until the peer disconnects or the server stops, never replying.
    /// Simulates a server that accepts and then hangs, for client timeout tests.
    /// </summary>
    public async Task HangAsync(CancellationToken cancellationToken = default)
    {
        Transcript.RecordEvent("hanging");
        while (true)
        {
            var result = await ReadPipeAsync(cancellationToken).ConfigureAwait(false);
            _reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) return;
        }
    }

    /// <summary>
    /// Close gracefully: finish TLS, send FIN, then briefly drain input so unread client data does
    /// not turn the close into a reset that could discard the last response.
    /// </summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        SignalTerminated();
        try
        {
            if (_writer is not null) await _writer.FlushAsync().ConfigureAwait(false);
            if (_transport is SslStream ssl) await ssl.ShutdownAsync().ConfigureAwait(false);
            _socket.Shutdown(SocketShutdown.Send);
            Transcript.RecordEvent("closed", closed: true);

            using var drain = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            drain.CancelAfter(DrainTimeout);
            while (true)
            {
                var result = await _reader.ReadAsync(drain.Token).ConfigureAwait(false);
                _reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort: the peer may already be gone.
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Drop the connection immediately with a TCP reset.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        SignalTerminated();
        try
        {
            _socket.LingerState = new LingerOption(true, 0);
        }
        catch (SocketException)
        {
            // Not supported on Unix domain sockets; a plain close is the closest equivalent.
        }
        Transcript.RecordEvent("reset", closed: true);
        Release();
    }

    /// <summary>Same as <see cref="CloseAsync"/>.</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>
    /// True once this side has closed or reset the connection: the handler, a fault, an interceptor or
    /// server shutdown. A protocol layer checks it to stop acting on input it has already buffered.
    /// The peer closing does not count; reads report that as end of stream.
    /// </summary>
    public bool IsTerminated => Volatile.Read(ref _terminated) != 0;

    /// <summary>Cancelled when <see cref="IsTerminated"/> becomes true, so pending work can stop instead of polling.</summary>
    public CancellationToken Terminated => _terminatedSource.Token;

    private void SignalTerminated()
    {
        try
        {
            _terminatedSource.Cancel();
        }
        catch (AggregateException)
        {
            // A registered callback threw; termination itself still stands.
        }
    }

    internal void OnReceived(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty) Transcript.Record(TranscriptDirection.Received, data);
        else if (!IsTerminated) Transcript.RecordEvent("closed by peer", closed: true);
        else Transcript.RecordEvent("peer finished closing");
    }

    internal void OnPeerReset()
    {
        if (!IsTerminated) Transcript.RecordEvent("reset by peer", closed: true);
    }

    internal static bool IsPeerReset(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown } => true,
        IOException { InnerException: { } inner } => IsPeerReset(inner),
        _ => false,
    };

    private (TapStream, PipeReader) CreateReader(Stream transport)
    {
        var tap = new TapStream(transport, this);
        return (tap, PipeReader.Create(tap, new StreamPipeReaderOptions(leaveOpen: true)));
    }

    private async ValueTask<ReadResult> ReadPipeAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
        if (_options.ReadTimeout is { } timeout) linked.CancelAfter(timeout);
        try
        {
            return await _reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !CancellationToken.IsCancellationRequested)
        {
            Transcript.RecordEvent($"read timed out after {_options.ReadTimeout}");
            throw new TimeoutException($"No data received within {_options.ReadTimeout}.");
        }
        catch (Exception ex) when (IsPeerReset(ex))
        {
            OnPeerReset();
            throw;
        }
    }

    private async ValueTask<byte[]?> ReadFrameAsync(ReadOnlyMemory<byte> delimiter, bool returnPartialAtEnd, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await ReadPipeAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (TryReadTo(ref buffer, delimiter.Span, out var frame))
            {
                _reader.AdvanceTo(buffer.Start);
                return frame;
            }
            if (buffer.Length > _options.MaxLineLength)
            {
                _reader.AdvanceTo(buffer.Start, buffer.End);
                throw new InvalidDataException($"No delimiter within {_options.MaxLineLength} bytes.");
            }
            if (result.IsCompleted)
            {
                if (returnPartialAtEnd && !buffer.IsEmpty)
                {
                    var rest = buffer.ToArray();
                    _reader.AdvanceTo(buffer.End);
                    return rest;
                }
                _reader.AdvanceTo(buffer.Start, buffer.End);
                return null;
            }
            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryReadTo(ref ReadOnlySequence<byte> buffer, ReadOnlySpan<byte> delimiter, out byte[] frame)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(out ReadOnlySequence<byte> found, delimiter, advancePastDelimiter: true))
        {
            frame = found.ToArray();
            buffer = buffer.Slice(reader.Position);
            return true;
        }
        frame = [];
        return false;
    }

    private void Release()
    {
        try { _reader.Complete(); } catch (InvalidOperationException) { }
        _transport.Dispose();
        _socket.Dispose();
    }

    private sealed class ConnectionStream(Connection connection) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            connection.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            connection.WriteAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
