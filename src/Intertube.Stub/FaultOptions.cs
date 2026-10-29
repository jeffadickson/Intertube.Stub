namespace Intertube.Stub;

/// <summary>How a connection is torn down when a fault disconnects it.</summary>
public enum DisconnectMode
{
    /// <summary>Send a TCP RST; the client sees "connection reset".</summary>
    Reset,

    /// <summary>Send a FIN; the client sees end of stream.</summary>
    Close,
}

/// <summary>
/// Misbehaviour injected into a connection's reads and writes, for testing how clients cope with
/// slow, chatty, truncated or corrupt servers. Combine helpers with <c>with</c> expressions. Under TLS,
/// faults apply to the plaintext: byte counts are application bytes, and the handshake is not affected.
/// </summary>
public sealed record FaultOptions
{
    /// <summary>No faults.</summary>
    public static FaultOptions None { get; } = new();

    /// <summary>Delay after accepting a connection before the handler runs (the server is silent meanwhile).</summary>
    public TimeSpan ConnectDelay { get; init; }

    /// <summary>Delay before each read from the socket.</summary>
    public TimeSpan ReadDelay { get; init; }

    /// <summary>Delay before each write.</summary>
    public TimeSpan WriteDelay { get; init; }

    /// <summary>Split every write into chunks of at most this many bytes.</summary>
    public int? FragmentSize { get; init; }

    /// <summary>Pause between fragments when <see cref="FragmentSize"/> is set.</summary>
    public TimeSpan FragmentDelay { get; init; }

    /// <summary>Disconnect once this many bytes have been written, even mid-message.</summary>
    public long? DisconnectAfterBytesWritten { get; init; }

    /// <summary>How <see cref="DisconnectAfterBytesWritten"/> disconnects.</summary>
    public DisconnectMode DisconnectMode { get; init; } = DisconnectMode.Reset;

    /// <summary>Rewrite each outgoing buffer before it is sent (to produce malformed responses).</summary>
    public Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>? TransformWrite { get; init; }

    /// <summary>Delay every write.</summary>
    public static FaultOptions Latency(TimeSpan delay) => new() { WriteDelay = delay };

    /// <summary>Dribble writes out in small pieces, exercising the client's reassembly.</summary>
    public static FaultOptions Fragment(int size, TimeSpan delay = default) =>
        new() { FragmentSize = size, FragmentDelay = delay };

    /// <summary>
    /// Cut the connection after <paramref name="bytes"/> bytes have been written. With
    /// <see cref="DisconnectMode.Reset"/> and a server that writes first, a Linux or macOS client
    /// can see the reset as a failed connect, and any OS may discard bytes the client had not yet read.
    /// </summary>
    public static FaultOptions DisconnectAfter(long bytes, DisconnectMode mode = DisconnectMode.Reset) =>
        new() { DisconnectAfterBytesWritten = bytes, DisconnectMode = mode };

    /// <summary>
    /// Fraction (0 to 1) of written bytes that get one bit flipped; see <see cref="Corrupt"/>. Anything
    /// else throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public double CorruptRate
    {
        get;
        init
        {
            if (value is not (>= 0 and <= 1)) throw new ArgumentOutOfRangeException(nameof(CorruptRate), value, "CorruptRate must be between 0 and 1.");
            field = value;
        }
    }

    /// <summary>Seed for <see cref="CorruptRate"/>.</summary>
    public int CorruptSeed { get; init; }

    /// <summary>
    /// Flip one random bit in roughly <paramref name="rate"/> of written bytes. Each connection draws from
    /// its own generator, seeded from <paramref name="seed"/> and the connection id, so the corruption is
    /// the same on every run however connections interleave.
    /// </summary>
    public static FaultOptions Corrupt(double rate, int seed = 0)
    {
        if (rate is not (>= 0 and <= 1)) throw new ArgumentOutOfRangeException(nameof(rate));
        return new() { CorruptRate = rate, CorruptSeed = seed };
    }
}
