using System.Globalization;
using System.Text;

namespace RedisLite.Cli;

/// <summary>
/// The <c>redis-lite</c> command line, kept separate from <c>Main</c> so tests can drive it with
/// in-memory readers and writers.
/// <code>
/// redis-lite [--url URL] COMMAND [ARGS...]    run one command
/// redis-lite [--url URL] SUBSCRIBE CHANNEL... stream messages until Ctrl+C
/// redis-lite [--url URL]                      interactive prompt
/// </code>
/// </summary>
public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        var url = Environment.GetEnvironmentVariable("REDIS_URL") ?? "redis://localhost";
        var rest = args.ToList();
        if (rest.Count >= 2 && rest[0] is "--url" or "-u")
        {
            url = rest[1];
            rest.RemoveRange(0, 2);
        }

        RedisOptions options;
        try
        {
            options = RedisOptions.Parse(url);
        }
        catch (FormatException ex)
        {
            await output.WriteLineAsync($"(error) {ex.Message}");
            return 2;
        }

        await using var client = new RedisClient(options);
        if (rest.Count == 0) return await ReplAsync(client, input, output, cancellationToken);
        return await RunCommandAsync(client, rest, output, cancellationToken) ? 0 : 1;
    }

    private static async Task<int> ReplAsync(RedisClient client, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        var prompt = $"{client.Options.UnixSocketPath ?? $"{client.Options.Host}:{client.Options.Port}"}> ";
        while (!cancellationToken.IsCancellationRequested)
        {
            await output.WriteAsync(prompt);
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null || line.Trim() is "quit" or "exit") return 0;
            IReadOnlyList<string> command;
            try
            {
                command = Tokenizer.Split(line);
            }
            catch (FormatException ex)
            {
                await output.WriteLineAsync($"(error) {ex.Message}");
                continue;
            }
            if (command.Count > 0) await RunCommandAsync(client, command, output, cancellationToken);
        }
        return 0;
    }

    private static async Task<bool> RunCommandAsync(RedisClient client, IReadOnlyList<string> command, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            if (command[0].Equals("SUBSCRIBE", StringComparison.OrdinalIgnoreCase))
            {
                await foreach (var message in client.SubscribeAsync([.. command.Skip(1)], cancellationToken))
                    await output.WriteLineAsync($"{message.Channel}: {message.Text}");
                return true;
            }

            await output.WriteLineAsync(Format(await client.ExecuteAsync(command, cancellationToken)));
            return true;
        }
        catch (RedisServerException ex)
        {
            await output.WriteLineAsync($"(error) {ex.Message}");
        }
        catch (RedisException ex)
        {
            await output.WriteLineAsync($"(error) {ex.GetType().Name}: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        return false;
    }

    /// <summary>Render a reply the way <c>redis-cli</c> does.</summary>
    public static string Format(RespValue value) => Format(value, "");

    private static string Format(RespValue value, string indent) => value switch
    {
        RespSimpleString s => s.Value,
        RespError e => $"(error) {e.Message}",
        RespInteger i => $"(integer) {i.Value.ToString(CultureInfo.InvariantCulture)}",
        RespBulkString { Text: null } => "(nil)",
        RespBulkString { Text: { } text } => Tokenizer.Quote(text),
        RespArray { Items: null } => "(nil)",
        RespArray { Items: [] } => "(empty array)",
        RespArray { Items: { } items } => FormatArray(items, indent),
        _ => value.ToString(),
    };

    private static string FormatArray(IReadOnlyList<RespValue> items, string indent)
    {
        var builder = new StringBuilder();
        var width = items.Count.ToString(CultureInfo.InvariantCulture).Length;
        for (var i = 0; i < items.Count; i++)
        {
            var label = $"{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)}) ";
            if (i > 0) builder.AppendLine().Append(indent);
            builder.Append(label).Append(Format(items[i], indent + new string(' ', label.Length)));
        }
        return builder.ToString();
    }
}

/// <summary>Splits and quotes command lines with redis-cli rules: spaces separate, double quotes group, backslash escapes.</summary>
public static class Tokenizer
{
    public static IReadOnlyList<string> Split(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken) tokens.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else if (c == '"')
            {
                inToken = true;
                for (i++; ; i++)
                {
                    if (i >= line.Length) throw new FormatException("Unbalanced quotes.");
                    if (line[i] == '"') break;
                    if (line[i] == '\\')
                    {
                        if (++i >= line.Length) throw new FormatException("Dangling backslash.");
                        current.Append(line[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var other => other });
                    }
                    else
                    {
                        current.Append(line[i]);
                    }
                }
                if (i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1]))
                    throw new FormatException("A closing quote must be followed by a space.");
            }
            else
            {
                inToken = true;
                current.Append(c);
            }
        }
        if (inToken) tokens.Add(current.ToString());
        return tokens;
    }

    public static string Quote(string token)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in token)
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => @"\\",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => c.ToString(),
            });
        return builder.Append('"').ToString();
    }
}
