using System.Text;
using System.Text.RegularExpressions;

namespace Intertube.Stub.Smtp;

/// <summary>
/// A message an <see cref="SmtpServer"/> accepted: the envelope, the raw data (dot-unstuffed, otherwise
/// exactly as transmitted) and a light MIME reading of it. For full MIME parsing, hand <see cref="Data"/>
/// to MimeKit. The MIME reading never throws: anything malformed is left as it was sent.
/// </summary>
public sealed partial class SmtpMessage
{
    // Latin-1 maps each byte to one char and back, so the MIME structure can be parsed as text while
    // every part keeps its original bytes for decoding in its own charset.
    private static readonly Encoding Bytes = Encoding.Latin1;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Lazy<Entity> _parsed;

    internal SmtpMessage(string from, IReadOnlyList<string> recipients, byte[] data, string? user, bool tls, int connectionId)
    {
        From = from;
        Recipients = recipients;
        Data = data;
        User = user;
        Tls = tls;
        ConnectionId = connectionId;
        ReceivedAt = DateTimeOffset.UtcNow;
        _parsed = new(() => Entity.Parse(Bytes.GetString(Data)));
    }

    /// <summary>The <c>MAIL FROM</c> address, without angle brackets (empty for a bounce).</summary>
    public string From { get; }

    /// <summary>The accepted <c>RCPT TO</c> addresses, including any not named in the headers (Bcc).</summary>
    public IReadOnlyList<string> Recipients { get; }

    /// <summary>The message exactly as transmitted, after dot-unstuffing (line endings are not normalised).</summary>
    public byte[] Data { get; }

    /// <summary>The authenticated user, if the client logged in.</summary>
    public string? User { get; }

    /// <summary>True if the message arrived over TLS (STARTTLS or implicit).</summary>
    public bool Tls { get; }

    /// <summary>The id of the connection it arrived on, as in the server's transcript.</summary>
    public int ConnectionId { get; }

    public DateTimeOffset ReceivedAt { get; }

    /// <summary><see cref="Data"/> as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Data);

    /// <summary>Header fields in order, unfolded, with RFC 2047 encoded words decoded.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers => _parsed.Value.Headers;

    /// <summary>The first header with this name (case-insensitive), or null.</summary>
    public string? Header(string name) => _parsed.Value.Header(name);

    public string? Subject => Header("Subject");

    /// <summary>
    /// The plain-text body, decoded from quoted-printable or base64 and from its declared charset. For a
    /// multipart message, the first <c>text/plain</c> part that is not an attachment, searching nested
    /// parts in order (or else the first part).
    /// </summary>
    public string TextBody => _parsed.Value.TextBody();

    public override string ToString() => $"{From} -> {string.Join(", ", Recipients)}: {Subject}";

    /// <summary>A MIME entity: headers plus a body held as Latin-1 text (one char per original byte).</summary>
    private sealed class Entity(List<KeyValuePair<string, string>> headers, string body)
    {
        public List<KeyValuePair<string, string>> Headers { get; } = headers;

        public string? Header(string name) =>
            Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

        private string ContentType => Header("Content-Type") ?? "text/plain";

        private bool IsAttachment => (Header("Content-Disposition") ?? "").TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);

        public static Entity Parse(string text)
        {
            // A part that starts with a blank line has no headers (RFC 2046: a default text/plain part).
            if (text.StartsWith("\r\n", StringComparison.Ordinal)) return new([], text[2..]);
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var headerText = split < 0 ? text : text[..split];
            var body = split < 0 ? "" : text[(split + 4)..];

            var headers = new List<KeyValuePair<string, string>>();
            foreach (var line in headerText.Split("\r\n"))
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && headers.Count > 0)
                {
                    headers[^1] = new(headers[^1].Key, headers[^1].Value + line); // unfold: drop the CRLF, keep the whitespace
                    continue;
                }
                var colon = line.IndexOf(':');
                if (colon > 0) headers.Add(new(line[..colon].Trim(), line[(colon + 1)..]));
            }
            for (var i = 0; i < headers.Count; i++)
                headers[i] = new(headers[i].Key, DecodeWords(FromHeaderBytes(headers[i].Value).Trim()));
            return new(headers, body);
        }

        public string TextBody() => FindText(this) ?? FirstLeaf(this)?.Decode() ?? "";

        private static string? FindText(Entity entity)
        {
            if (entity.Children() is { } children)
            {
                foreach (var child in children)
                    if (FindText(child) is { } text) return text;
                return null;
            }
            return entity.ContentType.TrimStart().StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) && !entity.IsAttachment ? entity.Decode() : null;
        }

        private static Entity? FirstLeaf(Entity entity) =>
            entity.Children() is { } children ? children.Select(FirstLeaf).FirstOrDefault(c => c is not null) : entity;

        /// <summary>The parts of a multipart entity, split on boundary lines (RFC 2046 §5.1.1); null otherwise.</summary>
        private List<Entity>? Children()
        {
            if (!ContentType.TrimStart().StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) || Parameter(ContentType, "boundary") is not { Length: > 0 } boundary)
                return null;
            var delimiter = "--" + boundary;
            var parts = new List<Entity>();
            var text = "\r\n" + body; // the CRLF before a delimiter belongs to it, including for the first one
            var start = -1;
            var index = 0;
            while ((index = text.IndexOf("\r\n" + delimiter, index, StringComparison.Ordinal)) >= 0)
            {
                var after = index + 2 + delimiter.Length;
                var closing = string.CompareOrdinal(text, after, "--", 0, 2) == 0;
                var lineEnd = text.IndexOf("\r\n", after, StringComparison.Ordinal);
                if (start >= 0) parts.Add(Parse(text[start..index]));
                if (closing || lineEnd < 0) break;
                start = lineEnd + 2;
                index = start - 2;
            }
            return parts;
        }

        private string Decode()
        {
            var charset = Parameter(ContentType, "charset") is { } name ? GetEncoding(name) ?? Encoding.UTF8 : Encoding.UTF8;
            var bytes = (Header("Content-Transfer-Encoding") ?? "").Trim().ToLowerInvariant() switch
            {
                "base64" => FromBase64(string.Concat(body.Where(c => !char.IsWhiteSpace(c)))) ?? Bytes.GetBytes(body),
                "quoted-printable" => QuotedPrintable(body, header: false),
                _ => Bytes.GetBytes(body),
            };
            return charset.GetString(bytes);
        }
    }

    /// <summary>A raw header value is UTF-8 when the client used SMTPUTF8; anything else is kept byte for byte.</summary>
    private static string FromHeaderBytes(string latin1)
    {
        var bytes = Bytes.GetBytes(latin1);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return latin1;
        }
    }

    private static string? Parameter(string headerValue, string name)
    {
        var match = Regex.Match(headerValue, $@";\s*{name}\s*=\s*(""(?<v>[^""]*)""|(?<v>[^;\s]+))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    [GeneratedRegex(@"=\?(?<charset>[^?]+)\?(?<encoding>[BbQq])\?(?<text>[^?]*)\?=(\s+(?==\?))?")]
    private static partial Regex EncodedWord();

    /// <summary>Decode RFC 2047 encoded words; one that is malformed or in an unknown charset stays as it was (§6.3).</summary>
    private static string DecodeWords(string value) => EncodedWord().Replace(value, m =>
    {
        if (GetEncoding(m.Groups["charset"].Value) is not { } encoding) return m.Value;
        var text = m.Groups["text"].Value;
        var bytes = m.Groups["encoding"].Value is "B" or "b" ? FromBase64(text) : QuotedPrintable(text, header: true);
        return bytes is null ? m.Value : encoding.GetString(bytes);
    });

    private static byte[]? FromBase64(string text)
    {
        var padded = text.Length % 4 == 0 ? text : text.PadRight(text.Length + 4 - text.Length % 4, '='); // some encoders drop the padding
        var buffer = new byte[padded.Length * 3 / 4];
        return Convert.TryFromBase64String(padded, buffer, out var written) ? buffer[..written] : null;
    }

    /// <summary>Quoted-printable over Latin-1 text: literal characters are original bytes.</summary>
    private static byte[] QuotedPrintable(string text, bool header)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=' && i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n')
                i += 2; // soft line break
            else if (c == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '_' && header)
                bytes.Add((byte)' ');
            else if (c <= 0xFF)
                bytes.Add((byte)c);
            else
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); // a decoded UTF-8 header value
        }
        return [.. bytes];
    }

    /// <summary>The named charset, including Windows and ISO code pages (without registering them process-wide); null if unknown.</summary>
    private static Encoding? GetEncoding(string name)
    {
        name = name.Trim().Trim('"');
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name);
        }
    }
}
