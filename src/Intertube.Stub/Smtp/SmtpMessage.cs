using System.Text;
using System.Text.RegularExpressions;

namespace Intertube.Stub.Smtp;

/// <summary>
/// A message an <see cref="SmtpServer"/> accepted: the envelope, the raw data (dot-unstuffed, CRLF line
/// endings) and a light MIME reading of it. For full MIME parsing, hand <see cref="Data"/> to MimeKit.
/// </summary>
public sealed partial class SmtpMessage
{
    private readonly Lazy<(List<KeyValuePair<string, string>> Headers, string Body)> _parsed;

    internal SmtpMessage(string from, IReadOnlyList<string> recipients, byte[] data, string? user, bool tls, int connectionId)
    {
        From = from;
        Recipients = recipients;
        Data = data;
        User = user;
        Tls = tls;
        ConnectionId = connectionId;
        ReceivedAt = DateTimeOffset.UtcNow;
        _parsed = new(() => Parse(Text));
    }

    /// <summary>The <c>MAIL FROM</c> address, without angle brackets (empty for a bounce).</summary>
    public string From { get; }

    /// <summary>The accepted <c>RCPT TO</c> addresses, including any not named in the headers (Bcc).</summary>
    public IReadOnlyList<string> Recipients { get; }

    /// <summary>The message exactly as transmitted, after dot-unstuffing.</summary>
    public byte[] Data { get; }

    /// <summary>The authenticated user, if the client logged in.</summary>
    public string? User { get; }

    /// <summary>True if the message arrived over TLS (STARTTLS or implicit).</summary>
    public bool Tls { get; }

    public int ConnectionId { get; }

    public DateTimeOffset ReceivedAt { get; }

    /// <summary><see cref="Data"/> as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Data);

    /// <summary>Header fields in order, unfolded, with RFC 2047 encoded words decoded.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers => _parsed.Value.Headers;

    /// <summary>The first header with this name (case-insensitive), or null.</summary>
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public string? Subject => Header("Subject");

    /// <summary>
    /// The plain-text body, decoded from quoted-printable or base64. For a multipart message, the
    /// first <c>text/plain</c> part (or else the first part).
    /// </summary>
    public string TextBody => TextOf(Headers, _parsed.Value.Body);

    public override string ToString() => $"{From} -> {string.Join(", ", Recipients)}: {Subject}";

    private static (List<KeyValuePair<string, string>>, string) Parse(string text)
    {
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerText = split < 0 ? text : text[..split];
        var body = split < 0 ? "" : text[(split + 4)..];
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in headerText.Split("\r\n"))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && headers.Count > 0)
            {
                var last = headers[^1];
                headers[^1] = new(last.Key, last.Value + " " + line.Trim());
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon > 0) headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        for (var i = 0; i < headers.Count; i++) headers[i] = new(headers[i].Key, DecodeWords(headers[i].Value));
        return (headers, body);
    }

    private static string TextOf(IReadOnlyList<KeyValuePair<string, string>> headers, string body)
    {
        string? Get(string name) => headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var contentType = Get("Content-Type") ?? "text/plain";
        if (contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && Parameter(contentType, "boundary") is { } boundary)
        {
            var parts = body.Split("--" + boundary)
                .Skip(1)
                .TakeWhile(p => !p.StartsWith("--", StringComparison.Ordinal))
                .Select(p => Parse(p.TrimStart('\r', '\n')))
                .ToList();
            var chosen = parts.FirstOrDefault(p => (p.Item1.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "text/plain")
                .StartsWith("text/plain", StringComparison.OrdinalIgnoreCase));
            if (chosen.Item1 is null && parts.Count > 0) chosen = parts[0];
            return chosen.Item1 is null ? "" : TextOf(chosen.Item1, chosen.Item2.TrimEnd('\r', '\n'));
        }

        var charset = Parameter(contentType, "charset") is { } name ? GetEncoding(name) : Encoding.UTF8;
        return (Get("Content-Transfer-Encoding") ?? "").Trim().ToLowerInvariant() switch
        {
            "base64" => charset.GetString(Convert.FromBase64String(string.Concat(body.Where(c => !char.IsWhiteSpace(c))))),
            "quoted-printable" => charset.GetString(QuotedPrintable(body, header: false)),
            _ => body,
        };
    }

    private static string? Parameter(string headerValue, string name)
    {
        var match = Regex.Match(headerValue, $@";\s*{name}\s*=\s*(""(?<v>[^""]*)""|(?<v>[^;\s]+))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    [GeneratedRegex(@"=\?(?<charset>[^?]+)\?(?<encoding>[BbQq])\?(?<text>[^?]*)\?=(\s+(?==\?))?")]
    private static partial Regex EncodedWord();

    private static string DecodeWords(string value) => EncodedWord().Replace(value, m =>
    {
        var encoding = GetEncoding(m.Groups["charset"].Value);
        var text = m.Groups["text"].Value;
        return m.Groups["encoding"].Value is "B" or "b"
            ? encoding.GetString(Convert.FromBase64String(text))
            : encoding.GetString(QuotedPrintable(text, header: true));
    });

    private static byte[] QuotedPrintable(string text, bool header)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=' && i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n')
            {
                i += 2; // soft line break
            }
            else if (c == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '_' && header)
            {
                bytes.Add((byte)' ');
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }
        return [.. bytes];
    }

    private static Encoding GetEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
