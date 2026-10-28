using RedisLite.Cli;

namespace RedisLite.Tests;

/// <summary>The redis-lite command line, end to end against FakeRedis.</summary>
public class CliTests
{
    private static async Task<(int Exit, string Output)> Run(FakeRedis fake, string input, params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var exit = await CliApp.RunAsync(["--url", fake.ClientOptions.ToUrl(), .. args], new StringReader(input), output);
        return (exit, output.ToString());
    }

    [Fact]
    public async Task OneShotCommands_PrintLikeRedisCli()
    {
        await using var fake = FakeRedis.Start();

        Assert.Equal((0, "OK\n"), await Run(fake, "", "SET", "greeting", "hello world"));
        Assert.Equal((0, "\"hello world\"\n"), await Run(fake, "", "GET", "greeting"));
        Assert.Equal((0, "(integer) 1\n"), await Run(fake, "", "INCR", "n"));
        Assert.Equal((0, "(nil)\n"), await Run(fake, "", "GET", "missing"));

        var (exit, output) = await Run(fake, "", "FLY");
        Assert.Equal(1, exit);
        Assert.StartsWith("(error) ERR unknown command 'FLY'", output);
    }

    [Fact]
    public async Task Repl_RunsQuotedCommandsUntilQuit()
    {
        await using var fake = FakeRedis.Start();
        var (exit, output) = await Run(fake, "set \"my key\" \"two\\nlines\"\nget \"my key\"\nset broken \"quote\nquit\n");

        Assert.Equal(0, exit);
        Assert.Contains("OK", output);
        Assert.Contains("\"two\\nlines\"", output);
        Assert.Contains("(error) Unbalanced quotes.", output);
        Assert.Equal(["set", "my key", "two\nlines"], fake.Commands[0]);
    }

    [Fact]
    public async Task BadUrl_ExitsWithUsageError()
    {
        var output = new StringWriter();
        Assert.Equal(2, await CliApp.RunAsync(["--url", "nope", "PING"], TextReader.Null, output));
        Assert.Contains("no scheme", output.ToString());
    }

    [Fact]
    public void Format_NestedArrays()
    {
        var value = new RespArray([RespValue.Bulk("a"), new RespInteger(2), new RespArray([RespValue.Nil, new RespArray([])])]);
        Assert.Equal("1) \"a\"\n2) (integer) 2\n3) 1) (nil)\n   2) (empty array)", CliApp.Format(value).ReplaceLineEndings("\n"));
    }
}
