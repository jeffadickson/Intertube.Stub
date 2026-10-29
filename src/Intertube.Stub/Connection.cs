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
/// Thrown when a TLS handshake fails: the peer rejected the certificate, spoke plaintext or went away. A
/// server records it as a connection event rather than a handler error, since it is the client's failure.
/// </summary>
public sealed class TlsHandshakeException(string message, Exception innerException) : IOException(message, innerException);

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
    private int _released;
    private Random? _corruptRandom;

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

    /// <summary>
    /// Cancelled when the owning server shuts down (it is the server-wide token, not per connection; see
    /// <see cref="Terminated"/> for this connection ending). Reads and writes observe it automatically.
    /// </summary>
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
        if (faults.CorruptRate > 0) data = Corrupt(data, faults);

        var chunkSize = faults.FragmentSize is > 0 and var size ? size : Math.Max(data.Length, 1);
        for (var offset = 0; offset < data.Length;)
        {
            var piece = data.Slice(offset, Math.Min(chunkSize, data.Length - offset));
            var cut = faults.DisconnectAfterBytesWritten is { } limit && _bytesWritten + piece.Length >= limit;
            if (cut) piece = piece[..(int)Math.Max(0, faults.DisconnectAfterBytesWritten!.Value - _bytesWritten)];

            if (!piece.IsEmpty)
            {
                try
                {
                    await _tap.WriteAsync(piece, token).ConfigureAwait(false);
                    await _tap.FlushAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsPeerReset(ex))
                {
                    OnPeerReset();
                    throw;
                }
                _bytesWritten += piece.Length;
                offset += piece.Length;
            }

            if (cut)
            {
                Transcript.RecordEvent($"fault: disconnect after {_bytesWritten} bytes written");
                if (faults.DisconnectMode == DisconnectMode.Reset) Abort();
                else await CloseCoreAsync(flushOutput: false).ConfigureAwait(false); // this write may be Output's own flush
                throw new ConnectionTerminatedException($"Fault injection disconnected the connection after {_bytesWritten} bytes.");
            }

            if (offset < data.Length && faults.FragmentDelay > TimeSpan.Zero)
                await Task.Delay(faults.FragmentDelay, token).ConfigureAwait(false);
        }
    }

    private ReadOnlyMemory<byte> Corrupt(ReadOnlyMemory<byte> data, FaultOptions faults)
    {
        var random = _corruptRandom ??= new Random(unchecked(faults.CorruptSeed * 397 + Id));
        var copy = data.ToArray();
        for (var i = 0; i < copy.Length; i++)
            if (random.NextDouble() < faults.CorruptRate)
                copy[i] ^= (byte)(1 << random.Next(8));
        return copy;
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
    /// input may be buffered; anything written to <see cref="Output"/> is flushed first, in plaintext.
    /// A failed handshake throws <see cref="TlsHandshakeException"/>.
    /// </summary>
    public async Task UpgradeToTlsAsync(TlsOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= _options.Tls ?? throw new InvalidOperationException("No TlsOptions given and none configured on the server.");
        if (IsTls) throw new InvalidOperationException("The connection is already using TLS.");
        if (HasBufferedInput(out var buffered))
            throw new InvalidOperationException($"Cannot start TLS with {buffered} unread bytes buffered.");
        if (_writer is not null) await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
        var ssl = new SslStream(_transport, leaveInnerStreamOpen: false);
        try
        {
            if (_isServer) await ssl.AuthenticateAsServerAsync(options.ToServerOptions(), linked.Token).ConfigureAwait(false);
            else await ssl.AuthenticateAsClientAsync(options.ToClientOptions("localhost"), linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !linked.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            Transcript.RecordEvent($"TLS handshake failed: {ex.GetBaseException().Message}");
            throw new TlsHandshakeException($"TLS handshake failed: {ex.GetBaseException().Message}", ex);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await _reader.CompleteAsync().ConfigureAwait(false);
        _transport = ssl;
        (_tap, _reader) = CreateReader(ssl);
        Transcript.RecordEvent($"TLS established ({ssl.SslProtocol})");
    }

    /// <summary>
    /// Read and discard input until the peer disconnects or the server stops, never replying.
    /// Simulates a server that accepts and then hangs, for client timeout tests.
    /// <see cref="ServerOptions.ReadTimeout"/> does not apply: a hung server waits forever.
    /// </summary>
    public async Task HangAsync(CancellationToken cancellationToken = default)
    {
        Transcript.RecordEvent("hanging");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken);
        while (true)
        {
            ReadResult result;
            try
            {
                result = await _reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPeerReset(ex))
            {
                OnPeerReset();
                throw;
            }
            catch (Exception ex) when (IsTerminated && IsTeardownArtefact(ex))
            {
                throw ClosedException(ex);
            }
            _reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) return;
        }
    }

    /// <summary>
    /// Close gracefully: finish TLS, send FIN, then briefly drain input so unread client data does
    /// not turn the close into a reset that could discard the last response. Reads and writes after a
    /// close or <see cref="Abort"/> fail with <see cref="ObjectDisposedException"/> or <see cref="IOException"/>.
    /// </summary>
    public Task CloseAsync() => CloseCoreAsync(flushOutput: true);

    private async Task CloseCoreAsync(bool flushOutput)
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0) return;
        SignalTerminated();
        try
        {
            // Each step is best effort, and each is bounded so Abort (or the server's stop) can still cut in.
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            closing.CancelAfter(DrainTimeout);
            if (flushOutput && _writer is not null) await BestEffort(_writer.FlushAsync(closing.Token).AsTask()).ConfigureAwait(false);
            if (_transport is SslStream ssl) await BestEffort(ssl.ShutdownAsync().WaitAsync(closing.Token)).ConfigureAwait(false);
            try
            {
                _socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
            if (Volatile.Read(ref _released) == 0) Transcript.RecordEvent("closed", closed: true);

            using var drain = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            drain.CancelAfter(DrainTimeout);
            await BestEffort(DrainAsync(drain.Token)).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) return;
        }
    }

    /// <summary>Await a closing step, ignoring the ways it fails when the peer is gone or time is up.</summary>
    private static async Task BestEffort(Task step)
    {
        try
        {
            await step.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Drop the connection immediately with a TCP reset, including one that is part way through closing.</summary>
    public void Abort()
    {
        var closing = Interlocked.Exchange(ref _terminated, 1) != 0;
        if (closing && Volatile.Read(ref _released) != 0) return;
        SignalTerminated();
        try
        {
            _socket.LingerState = new LingerOption(true, 0);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Not supported on Unix domain sockets; a plain close is the closest equivalent.
        }
        if (!Transcript.IsClosed) Transcript.RecordEvent(closing ? "reset (graceful close did not finish)" : "reset", closed: true);
        Release();
    }

    /// <summary>True if input has arrived that no read has consumed yet.</summary>
    internal bool HasBufferedInput(out long count)
    {
        count = 0;
        ReadResult pending;
        try
        {
            if (!_reader.TryRead(out pending)) return false;
        }
        catch (Exception ex) when (IsTerminated && IsTeardownArtefact(ex))
        {
            throw ClosedException(ex);
        }
        count = pending.Buffer.Length;
        _reader.AdvanceTo(pending.Buffer.Start);
        return count > 0;
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
        catch (Exception ex) when (IsTerminated && IsTeardownArtefact(ex))
        {
            throw ClosedException(ex);
        }
    }

    /// <summary>
    /// What a read throws once this side has closed or reset the connection. The pipe has been completed and
    /// the stream disposed, possibly under a read in progress, so the pipe can fail with its own exceptions
    /// (<see cref="InvalidOperationException"/>, or <see cref="ArgumentOutOfRangeException"/> from a buffer
    /// torn down mid-read), which would look like a bug in the handler.
    /// </summary>
    private static ObjectDisposedException ClosedException(Exception inner) => new("The connection has been closed.", inner);

    /// <summary>A pipe failure caused by the teardown rather than the I/O failures callers already expect.</summary>
    private static bool IsTeardownArtefact(Exception ex) => ex is not (IOException or ObjectDisposedException or OperationCanceledException);

    private async ValueTask<byte[]?> ReadFrameAsync(ReadOnlyMemory<byte> delimiter, bool returnPartialAtEnd, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await ReadPipeAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            var start = buffer.Start;
            if (TryReadTo(ref buffer, delimiter.Span, out var frame))
            {
                if (frame.Length > _options.MaxLineLength)
                {
                    _reader.AdvanceTo(start, buffer.Start);
                    throw new InvalidDataException($"No delimiter within {_options.MaxLineLength} bytes.");
                }
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
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
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
