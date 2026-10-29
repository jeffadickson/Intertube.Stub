using System.Text.RegularExpressions;

namespace Intertube.Stub;

/// <summary>
/// A test applied to one line of text. Strings convert implicitly and are glob patterns matching the
/// whole line: <c>*</c> matches any run of characters and <c>?</c> exactly one. A string without
/// wildcards is therefore an exact match; use <see cref="Exact"/> for text that contains them.
/// </summary>
public sealed class LineMatcher
{
    private readonly Func<string, bool> _isMatch;

    private LineMatcher(Func<string, bool> isMatch, string description)
    {
        _isMatch = isMatch;
        Description = description;
    }

    /// <summary>Readable form used in failure messages.</summary>
    public string Description { get; }

    public bool IsMatch(string line) => _isMatch(line);

    /// <summary>Match the whole line against a glob pattern.</summary>
    public static LineMatcher Glob(string pattern, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var options = RegexOptions.Singleline | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0);
        var regex = new Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", options);
        return new(regex.IsMatch, $"\"{pattern}\"");
    }

    /// <summary>Match the whole line literally, so <c>*</c> and <c>?</c> are ordinary characters.</summary>
    public static LineMatcher Exact(string text, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(line => string.Equals(line, text, comparison), $"exactly \"{text}\"");
    }

    /// <summary>Search the line with a regular expression (anchor it yourself for a full match).</summary>
    public static LineMatcher FromRegex(Regex regex)
    {
        ArgumentNullException.ThrowIfNull(regex);
        return new(regex.IsMatch, $"/{regex}/");
    }

    /// <summary>Match lines satisfying a predicate.</summary>
    public static LineMatcher Where(Func<string, bool> predicate, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new(predicate, description ?? "a line matching the predicate");
    }

    /// <summary>Match any line.</summary>
    public static LineMatcher Any { get; } = new(_ => true, "any line");

    public static implicit operator LineMatcher(string pattern) => Glob(pattern);

    public static implicit operator LineMatcher(Regex regex) => FromRegex(regex);

    public override string ToString() => Description;
}
