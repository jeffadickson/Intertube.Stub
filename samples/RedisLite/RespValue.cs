using System.Text;

namespace RedisLite;

/// <summary>
/// A value in the Redis serialization protocol (RESP2). Values compare by content, including
/// the bytes of bulk strings and the items of arrays, so they can be used directly in assertions.
/// </summary>
public abstract record RespValue
{
    private protected RespValue()
    {
    }

    /// <summary>The null bulk string, which Redis uses for a missing key.</summary>
    public static RespBulkString Nil { get; } = new((byte[]?)null);

    /// <summary>A bulk string holding UTF-8 text.</summary>
    /// <param name="text">The text to encode.</param>
    public static RespBulkString Bulk(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>An array of bulk strings, which is how every command is sent.</summary>
    /// <param name="parts">The command name followed by its arguments.</param>
    public static RespArray Command(params string[] parts) => new([.. parts.Select(Bulk)]);
}

/// <summary>A status reply such as <c>+OK</c>. The text cannot contain CR or LF.</summary>
/// <param name="Value">The status text.</param>
public sealed record RespSimpleString(string Value) : RespValue
{
    /// <inheritdoc />
    public override string ToString() => $"+{Value}";
}

/// <summary>An error reply such as <c>-ERR unknown command</c>.</summary>
/// <param name="Message">The error text, whose first word is conventionally the error code.</param>
public sealed record RespError(string Message) : RespValue
{
    /// <summary>The leading error code, for example <c>ERR</c>, <c>WRONGPASS</c> or <c>NOAUTH</c>.</summary>
    public string Code => Message.Split(' ', 2)[0];

    /// <inheritdoc />
    public override string ToString() => $"-{Message}";
}

/// <summary>An integer reply such as <c>:42</c>.</summary>
/// <param name="Value">The integer.</param>
public sealed record RespInteger(long Value) : RespValue
{
    /// <inheritdoc />
    public override string ToString() => $":{Value}";
}

/// <summary>A binary-safe string, or nil when <see cref="Value"/> is null.</summary>
/// <param name="Value">The bytes, or null for nil.</param>
public sealed record RespBulkString(byte[]? Value) : RespValue
{
    /// <summary>True for the nil bulk string.</summary>
    public bool IsNil => Value is null;

    /// <summary>The bytes decoded as UTF-8, or null for nil.</summary>
    public string? Text => Value is null ? null : Encoding.UTF8.GetString(Value);

    /// <summary>Compares bytes rather than array references.</summary>
    /// <param name="other">The value to compare with.</param>
    public bool Equals(RespBulkString? other) =>
        other is not null && (Value is null ? other.Value is null : other.Value is not null && Value.AsSpan().SequenceEqual(other.Value));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (Value is null) return -1;
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Value is null ? "$nil" : $"${Value.Length} \"{Text}\"";
}

/// <summary>An array of values, or a null array when <see cref="Items"/> is null.</summary>
/// <param name="Items">The items, or null for a null array.</param>
public sealed record RespArray(IReadOnlyList<RespValue>? Items) : RespValue
{
    /// <summary>Compares items in order rather than list references.</summary>
    /// <param name="other">The value to compare with.</param>
    public bool Equals(RespArray? other) =>
        other is not null && (Items is null ? other.Items is null : other.Items is not null && Items.SequenceEqual(other.Items));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (Items is null) return -1;
        var hash = new HashCode();
        foreach (var item in Items) hash.Add(item);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Items is null ? "*nil" : $"*{Items.Count} [{string.Join(", ", Items)}]";
}
