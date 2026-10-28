using System.Globalization;
using System.Net.Security;
using System.Text;

namespace RedisLite;

/// <summary>Where and how to connect.</summary>
/// <remarks>
/// URL forms understood by <see cref="Parse"/>:
/// <list type="bullet">
/// <item><c>redis://[user[:password]@]host[:port][/db][?name=client]</c></item>
/// <item><c>rediss://...</c>, the same over TLS</item>
/// <item><c>unix:///path/to/redis.sock[?db=N&amp;user=U&amp;password=P&amp;name=client]</c></item>
/// </list>
/// User names, passwords and client names are percent-encoded.
/// </remarks>
public sealed record RedisOptions
{
    /// <summary>The default Redis port.</summary>
    public const int DefaultPort = 6379;

    /// <summary>Host name or IP address. Ignored when <see cref="UnixSocketPath"/> is set.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>TCP port.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Connect to this Unix domain socket instead of TCP.</summary>
    public string? UnixSocketPath { get; init; }

    /// <summary>Wrap the connection in TLS.</summary>
    public bool UseTls { get; init; }

    /// <summary>Certificate validation for TLS; the operating system's validation when null.</summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; init; }

    /// <summary>ACL user name, sent with <c>AUTH</c> when <see cref="Password"/> is set.</summary>
    public string? User { get; init; }

    /// <summary>Password for <c>AUTH</c>; no authentication when null.</summary>
    public string? Password { get; init; }

    /// <summary>Database index chosen with <c>SELECT</c>.</summary>
    public int Database { get; init; }

    /// <summary>Name announced with <c>CLIENT SETNAME</c>.</summary>
    public string? ClientName { get; init; }

    /// <summary>Limit for establishing a connection, including TLS and the handshake.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Limit for each reply. A timeout breaks the connection, since replies would no longer line up.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Extra attempts after a connection failure. Failed connects are always retried; commands
    /// that may have reached the server are retried only when idempotent.
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Delay before the first retry; it doubles for each further attempt.</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Parse a <c>redis://</c>, <c>rediss://</c> or <c>unix://</c> URL.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The options it describes; settings a URL cannot express keep their defaults.</returns>
    /// <exception cref="FormatException">The URL is malformed.</exception>
    /// <example>
    /// <code>var options = RedisOptions.Parse("rediss://app:s3cret@cache.internal:6380/2?name=billing");</code>
    /// </example>
    public static RedisOptions Parse(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) throw new FormatException($"\"{url}\" has no scheme; expected redis://, rediss:// or unix://.");
        var scheme = url[..schemeEnd].ToLowerInvariant();
        var rest = url[(schemeEnd + 3)..];

        var queryStart = rest.IndexOf('?');
        var query = ParseQuery(queryStart < 0 ? "" : rest[(queryStart + 1)..]);
        if (queryStart >= 0) rest = rest[..queryStart];

        var options = new RedisOptions();
        switch (scheme)
        {
            case "unix":
                if (rest.Length == 0) throw new FormatException("A unix:// URL needs a socket path.");
                options = options with
                {
                    UnixSocketPath = Uri.UnescapeDataString(rest),
                    User = query.GetValueOrDefault("user"),
                    Password = query.GetValueOrDefault("password"),
                };
                break;

            case "redis" or "rediss":
            {
                var slash = rest.IndexOf('/');
                var authority = slash < 0 ? rest : rest[..slash];
                var path = slash < 0 ? "" : rest[(slash + 1)..];

                var at = authority.LastIndexOf('@');
                if (at >= 0)
                {
                    var userInfo = authority[..at];
                    authority = authority[(at + 1)..];
                    var colon = userInfo.IndexOf(':');
                    var user = Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]);
                    options = options with
                    {
                        User = user.Length == 0 ? null : user,
                        Password = colon < 0 ? null : Uri.UnescapeDataString(userInfo[(colon + 1)..]),
                    };
                }

                var (host, port) = SplitHostPort(authority);
                options = options with { Host = host, Port = port, UseTls = scheme == "rediss" };
                if (path.Length > 0) options = options with { Database = ParseNumber(path, "database") };
                break;
            }

            default:
                throw new FormatException($"Unsupported scheme \"{scheme}\"; expected redis://, rediss:// or unix://.");
        }

        if (query.TryGetValue("db", out var db)) options = options with { Database = ParseNumber(db, "database") };
        if (query.TryGetValue("name", out var name)) options = options with { ClientName = name };
        return options;
    }

    /// <summary>Format these options as a URL that <see cref="Parse"/> turns back into equivalent options.</summary>
    /// <returns>The URL. Timeouts, retries and certificate validation are not included.</returns>
    public string ToUrl()
    {
        var builder = new StringBuilder();
        var query = new List<string>();
        if (UnixSocketPath is not null)
        {
            builder.Append("unix://").Append(UnixSocketPath.Replace("%", "%25").Replace("?", "%3F").Replace("#", "%23"));
            if (Database != 0) query.Add($"db={Database.ToString(CultureInfo.InvariantCulture)}");
            if (User is not null) query.Add($"user={Uri.EscapeDataString(User)}");
            if (Password is not null) query.Add($"password={Uri.EscapeDataString(Password)}");
        }
        else
        {
            builder.Append(UseTls ? "rediss://" : "redis://");
            if (User is not null || Password is not null)
            {
                builder.Append(Uri.EscapeDataString(User ?? ""));
                if (Password is not null) builder.Append(':').Append(Uri.EscapeDataString(Password));
                builder.Append('@');
            }
            builder.Append(Host.Contains(':') ? $"[{Host}]" : Host);
            if (Port != DefaultPort) builder.Append(':').Append(Port.ToString(CultureInfo.InvariantCulture));
            if (Database != 0) builder.Append('/').Append(Database.ToString(CultureInfo.InvariantCulture));
        }
        if (ClientName is not null) query.Add($"name={Uri.EscapeDataString(ClientName)}");
        if (query.Count > 0) builder.Append('?').AppendJoin('&', query);
        return builder.ToString();
    }

    private static (string Host, int Port) SplitHostPort(string authority)
    {
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) throw new FormatException($"Unclosed IPv6 address in \"{authority}\".");
            var host = authority[1..close];
            var after = authority[(close + 1)..];
            if (after.Length == 0) return (host, DefaultPort);
            if (after[0] != ':') throw new FormatException($"Unexpected \"{after}\" after IPv6 address.");
            return (host, ParsePort(after[1..]));
        }

        var colon = authority.LastIndexOf(':');
        if (colon < 0) return (RequireHost(authority), DefaultPort);
        return (RequireHost(authority[..colon]), ParsePort(authority[(colon + 1)..]));
    }

    private static string RequireHost(string host) =>
        host.Length > 0 ? host : throw new FormatException("The URL has no host.");

    private static int ParsePort(string text)
    {
        var port = ParseNumber(text, "port");
        return port is >= 1 and <= 65535 ? port : throw new FormatException($"Port {port} is out of range.");
    }

    private static int ParseNumber(string text, string what) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"Invalid {what} \"{text}\".");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) throw new FormatException($"Query parameter \"{pair}\" has no value.");
            values[pair[..equals]] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return values;
    }
}
