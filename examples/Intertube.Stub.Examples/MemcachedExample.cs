using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Intertube.Stub.Examples;

// ---- System under test -----------------------------------------------------------------------

/// <summary>A memcached text-protocol client.</summary>
public sealed class MemcacheClient(string host, int port) : IDisposable
{
    private readonly TcpClient _tcp = new(host, port);

    public async Task<bool> SetAsync(string key, string value, TimeSpan ttl)
    {
        var data = Encoding.UTF8.GetBytes(value);
        await WriteAsync($"set {key} 0 {(int)ttl.TotalSeconds} {data.Length}\r\n{value}\r\n");
        return await ReadLineAsync() == "STORED";
    }

    public async Task<string?> GetAsync(string key)
    {
        await WriteAsync($"get {key}\r\n");
        var header = await ReadLineAsync();
        if (header == "END") return null;
        var length = int.Parse(header.Split(' ')[3], CultureInfo.InvariantCulture);
        var data = new byte[length + 2];
        await _tcp.GetStream().ReadExactlyAsync(data);
        if (await ReadLineAsync() != "END") throw new InvalidDataException("Missing END.");
        return Encoding.UTF8.GetString(data, 0, length);
    }

    private Task WriteAsync(string text) => _tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    // Byte-wise on purpose: a StreamReader would buffer past the line and swallow the data block.
    private async Task<string> ReadLineAsync()
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (await _tcp.GetStream().ReadAsync(one) == 1)
        {
            if (one[0] == '\n') return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
            line.Add(one[0]);
        }
        throw new EndOfStreamException();
    }

    public void Dispose() => _tcp.Dispose();
}

// ---- Tests -----------------------------------------------------------------------------------

/// <summary>
/// A small working fake built with Intertube.Stub's mixed framing helpers: a line for the command,
/// exactly N bytes for the data block. The test then pins the whole exchange as a golden transcript.
/// </summary>
public class MemcachedExample
{
    private static Func<Connection, Task> FakeMemcached()
    {
        var store = new Dictionary<string, string>();
        return async connection =>
        {
            await foreach (var line in connection.ReadLinesAsync())
            {
                var parts = line.Split(' ');
                switch (parts[0])
                {
                    case "set":
                        var data = await connection.ReadExactlyAsync(int.Parse(parts[4]) + 2);
                        lock (store) store[parts[1]] = Encoding.UTF8.GetString(data, 0, data.Length - 2);
                        await connection.WriteLineAsync("STORED");
                        break;
                    case "get":
                        string? value;
                        lock (store) value = store.GetValueOrDefault(parts[1]);
                        if (value is not null)
                            await connection.WriteLinesAsync([$"VALUE {parts[1]} 0 {Encoding.UTF8.GetByteCount(value)}", value]);
                        await connection.WriteLineAsync("END");
                        break;
                    default:
                        await connection.WriteLineAsync("ERROR");
                        break;
                }
            }
        };
    }

    [Fact]
    public async Task SetThenGet_GoldenTranscript()
    {
        await using var server = TcpServer.Listen(0, FakeMemcached());
        using (var cache = new MemcacheClient(server.Host, server.Port))
        {
            Assert.True(await cache.SetAsync("user:1", "Ada Lovelace", TimeSpan.FromMinutes(5)));
            Assert.Equal("Ada Lovelace", await cache.GetAsync("user:1"));
            Assert.Null(await cache.GetAsync("user:2"));
        }
        await server.Transcript.WaitForClosedConnectionsAsync(1);

        var traffic = server.Transcript.ToString().Split(Environment.NewLine).Where(l => l.Contains("<-") || l.Contains("->"));
        Assert.Equal(
        [
            "#1 <- set user:1 0 300 12",
            "#1 <- Ada Lovelace",
            "#1 -> STORED",
            "#1 <- get user:1",
            "#1 -> VALUE user:1 0 12",
            "#1 -> Ada Lovelace",
            "#1 -> END",
            "#1 <- get user:2",
            "#1 -> END",
        ], traffic);
    }

    [Fact]
    public async Task ValuesContainingTheTerminator_SurviveBecauseTheLengthGovernsFraming()
    {
        await using var server = TcpServer.Listen(0, FakeMemcached());
        using var cache = new MemcacheClient(server.Host, server.Port);

        Assert.True(await cache.SetAsync("tricky", "line one\r\nEND\r\nline three", TimeSpan.Zero));
        Assert.Equal("line one\r\nEND\r\nline three", await cache.GetAsync("tricky"));
    }
}
