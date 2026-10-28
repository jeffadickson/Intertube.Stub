namespace Intertube.Stub;

/// <summary>Ready-made connection handlers.</summary>
public static class Handler
{
    /// <summary>Echo every byte back until the client closes.</summary>
    public static Func<Connection, Task> Echo { get; } = async connection =>
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await connection.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            await connection.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
    };

    /// <summary>Accept, then never reply. For client timeout tests.</summary>
    public static Func<Connection, Task> Hang { get; } = connection => connection.HangAsync();

    /// <summary>Accept, then reset the connection at once.</summary>
    public static Func<Connection, Task> Reset { get; } = connection =>
    {
        connection.Abort();
        return Task.CompletedTask;
    };

    /// <summary>Accept, then close the connection at once without sending anything.</summary>
    public static Func<Connection, Task> Close { get; } = connection => connection.CloseAsync();

    /// <summary>Send fixed text (exactly as given, no terminator added) and close. Handy for canned or malformed responses.</summary>
    public static Func<Connection, Task> Send(string text) => async connection =>
    {
        await connection.WriteAsync(text).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
    };

    /// <summary>Send fixed bytes and close.</summary>
    public static Func<Connection, Task> Send(ReadOnlyMemory<byte> data) => async connection =>
    {
        await connection.WriteAsync(data).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
    };

    /// <summary>Answer each line with the returned line; return null to stay silent.</summary>
    public static Func<Connection, Task> Lines(Func<string, string?> respond) =>
        LinesAsync(line => ValueTask.FromResult(respond(line)));

    /// <summary>Answer each line with the awaited line; return null to stay silent.</summary>
    public static Func<Connection, Task> LinesAsync(Func<string, ValueTask<string?>> respond) => async connection =>
    {
        await foreach (var line in connection.ReadLinesAsync().ConfigureAwait(false))
            if (await respond(line).ConfigureAwait(false) is { } reply)
                await connection.WriteLineAsync(reply).ConfigureAwait(false);
    };
}
