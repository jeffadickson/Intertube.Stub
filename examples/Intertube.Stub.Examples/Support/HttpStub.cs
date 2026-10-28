using System.Text;

namespace Intertube.Stub.Examples.Support;

/// <summary>Just enough HTTP/1.1 to stub an API with Intertube.Stub; no framework, so every byte stays under the test's control.</summary>
public static class HttpStub
{
    public sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers, string Body);

    public static async Task<Request?> ReadRequestAsync(Connection connection)
    {
        var requestLine = await connection.ReadLineAsync();
        if (string.IsNullOrEmpty(requestLine)) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await connection.ReadLineAsync() is { Length: > 0 } line)
        {
            var colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        var body = headers.TryGetValue("Content-Length", out var length)
            ? Encoding.UTF8.GetString(await connection.ReadExactlyAsync(int.Parse(length)))
            : "";
        var parts = requestLine.Split(' ');
        return new Request(parts[0], parts[1], headers, body);
    }

    public static string Response(int status, string body = "", string contentType = "application/json", params string[] extraHeaders)
    {
        var reason = status switch { 200 => "OK", 404 => "Not Found", 500 => "Internal Server Error", 503 => "Service Unavailable", _ => "Status" };
        var headers = string.Concat(extraHeaders.Select(h => h + "\r\n"));
        return $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{headers}\r\n{body}";
    }

    /// <summary>Answer one request per connection with whatever <paramref name="respond"/> returns.</summary>
    public static Func<Connection, Task> Respond(Func<Request, string> respond) => async connection =>
    {
        if (await ReadRequestAsync(connection) is { } request)
            await connection.WriteAsync(respond(request));
    };
}
