using System.Text;

namespace Intertube.Stub;

/// <summary>Settings for a stream server (<see cref="TcpServer"/> or <see cref="UnixServer"/>).</summary>
public sealed record ServerOptions
{
    /// <summary>Defaults: concurrent handlers, UTF-8, CRLF line endings, no TLS, no faults.</summary>
    public static ServerOptions Default { get; } = new();

    /// <summary>Handle one connection at a time, like Python's plain <c>TCPServer</c>.</summary>
    public static ServerOptions Sequential { get; } = new() { MaxConcurrency = 1 };

    /// <summary>
    /// Maximum number of handlers running at once. <c>null</c> (the default) is unlimited.
    /// Connections beyond the limit are accepted and recorded, then wait their turn.
    /// </summary>
    public int? MaxConcurrency { get; init; }

    /// <summary>Wrap every accepted connection in TLS before the handler runs (implicit TLS).</summary>
    public TlsOptions? Tls { get; init; }

    /// <summary>Text encoding for line and string helpers. Defaults to UTF-8 without a BOM.</summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(false);

    /// <summary>Terminator written by <see cref="Connection.WriteLineAsync(string, CancellationToken)"/>. Reads accept LF or CRLF.</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>Longest line or delimited frame a read will buffer before failing. Defaults to 1 MiB.</summary>
    public int MaxLineLength { get; init; } = 1024 * 1024;

    /// <summary>Fail a read with <see cref="TimeoutException"/> if no data arrives within this time.</summary>
    public TimeSpan? ReadTimeout { get; init; }

    /// <summary>Faults applied to every connection; a handler can override them via <see cref="Connection.Faults"/>.</summary>
    public FaultOptions Faults { get; init; } = FaultOptions.None;

    /// <summary>How long shutdown waits for handlers to finish before resetting their connections.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Listen backlog.</summary>
    public int Backlog { get; init; } = 512;

    /// <summary>Record sent and received bytes. Connection events are always recorded.</summary>
    public bool RecordTranscript { get; init; } = true;

    /// <summary>Receives a live, human-readable log of traffic and events (for example a test output helper).</summary>
    public Action<string>? Log { get; init; }
}
