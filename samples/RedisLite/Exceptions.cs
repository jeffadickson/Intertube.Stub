namespace RedisLite;

/// <summary>Base class for every RedisLite failure.</summary>
/// <param name="message">What went wrong.</param>
/// <param name="inner">The underlying cause, if any.</param>
public class RedisException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The connection failed, closed or timed out. Commands that failed this way may or may not have
/// run on the server, which is why only idempotent commands are retried automatically.
/// </summary>
/// <param name="message">What went wrong.</param>
/// <param name="inner">The underlying cause, if any.</param>
public class RedisConnectionException(string message, Exception? inner = null) : RedisException(message, inner);

/// <summary>No reply arrived within <see cref="RedisOptions.CommandTimeout"/>.</summary>
/// <param name="message">What went wrong.</param>
public sealed class RedisTimeoutException(string message) : RedisConnectionException(message);

/// <summary>The server sent bytes that are not valid RESP. The connection is unusable afterwards.</summary>
/// <param name="message">What was wrong with the input.</param>
public sealed class RespProtocolException(string message) : RedisException(message);

/// <summary>The server answered with an error reply.</summary>
/// <param name="error">The error reply.</param>
public sealed class RedisServerException(RespError error) : RedisException(error.Message)
{
    /// <summary>The error code, for example <c>ERR</c>, <c>WRONGPASS</c> or <c>NOAUTH</c>.</summary>
    public string Code => Error.Code;

    /// <summary>The raw error reply.</summary>
    public RespError Error { get; } = error;
}
