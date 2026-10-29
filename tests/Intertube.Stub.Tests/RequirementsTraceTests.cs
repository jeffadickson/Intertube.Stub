using System.Text.RegularExpressions;

namespace Intertube.Stub.Tests;

/// <summary>
/// Keeps the StrictDoc requirements in docs/requirements honest: every UID is unique, every Active
/// requirement says how it is verified, and every test it names exists in the repository.
/// </summary>
public partial class RequirementsTraceTests
{
    private static readonly string Root = FindRoot();
    private static readonly IReadOnlyList<Requirement> Requirements = Load();

    private sealed record Requirement(string File, string Uid, string Status, IReadOnlyList<string> VerifiedBy, IReadOnlyList<string> Parents);

    [Fact]
    public void UidsAreUniqueAndWellFormed()
    {
        Assert.NotEmpty(Requirements);
        Assert.All(Requirements, r => Assert.Matches(UidPattern(), r.Uid));
        Assert.All(Requirements, r => Assert.Contains(r.Status, (string[])["Active", "Deferred", "Rejected"]));
        var duplicates = Requirements.GroupBy(r => r.Uid).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, $"duplicate UIDs: {string.Join(", ", duplicates)}");

        var uids = Requirements.Select(r => r.Uid).ToHashSet();
        var dangling = Requirements.SelectMany(r => r.Parents.Where(p => !uids.Contains(p)).Select(p => $"{r.Uid} -> {p}")).ToList();
        Assert.True(dangling.Count == 0, $"parents that do not exist: {string.Join(", ", dangling)}");
    }

    [Fact]
    public void EveryActiveRequirementSaysHowItIsVerified()
    {
        var missing = Requirements.Where(r => r.Status == "Active" && r.VerifiedBy.Count == 0).Select(r => r.Uid).ToList();
        Assert.True(missing.Count == 0, $"Active requirements without VERIFIED_BY: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryVerifiedByEntryNamesARealTest()
    {
        var sources = new[] { "tests", "examples", "samples" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(Root, dir), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".fs", StringComparison.Ordinal))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Select(File.ReadAllText)
            .ToList();

        var unresolved = new List<string>();
        foreach (var requirement in Requirements)
        {
            foreach (var entry in requirement.VerifiedBy)
            {
                if (entry.StartsWith("Review:", StringComparison.Ordinal))
                {
                    var target = entry["Review:".Length..].Trim().Split(' ')[0].TrimEnd(',', ';', '.');
                    if (target.Contains('/') && !File.Exists(Path.Combine(Root, target)) && !Directory.Exists(Path.Combine(Root, target)))
                        unresolved.Add($"{requirement.Uid}: {entry} (no such file)");
                    continue;
                }

                var dot = entry.IndexOf('.');
                if (dot <= 0 || !sources.Any(source => Declares(source, entry[..dot], entry[(dot + 1)..])))
                    unresolved.Add($"{requirement.Uid}: {entry}");
            }
        }
        Assert.True(unresolved.Count == 0, "VERIFIED_BY entries that name no test:\n" + string.Join("\n", unresolved));
    }

    /// <summary>A C# class with the method, or an F# module with the ``backticked`` test.</summary>
    private static bool Declares(string source, string type, string member) =>
        (Regex.IsMatch(source, $@"\bclass\s+{Regex.Escape(type)}\b") && Regex.IsMatch(source, $@"\b{Regex.Escape(member)}\s*\("))
        || (Regex.IsMatch(source, $@"^module\s+(?:[\w.]+\.)?{Regex.Escape(type)}\s*$", RegexOptions.Multiline) && source.Contains($"``{member}``", StringComparison.Ordinal));

    private static IReadOnlyList<Requirement> Load()
    {
        var result = new List<Requirement>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "docs", "requirements"), "*.sdoc"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i] != "[REQUIREMENT]") continue;
                string uid = "", status = "";
                var verifiedBy = new List<string>();
                var parents = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith('['); i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("UID: ", StringComparison.Ordinal)) uid = line[5..].Trim();
                    else if (line.StartsWith("STATUS: ", StringComparison.Ordinal)) status = line[8..].Trim();
                    else if (line.StartsWith("  VALUE: ", StringComparison.Ordinal)) parents.Add(line[9..].Trim());
                    else if (line == "VERIFIED_BY: >>>")
                    {
                        for (i++; i < lines.Length && lines[i] != "<<<"; i++)
                            if (lines[i].StartsWith("- ", StringComparison.Ordinal)) verifiedBy.Add(lines[i][2..].Trim());
                    }
                    else if (line.EndsWith(">>>", StringComparison.Ordinal))
                    {
                        while (i + 1 < lines.Length && lines[i + 1] != "<<<") i++; // skip other text blocks, which may contain "[" lines
                        i++;
                    }
                }
                i--;
                result.Add(new Requirement(Path.GetFileName(file), uid, status, verifiedBy, parents));
            }
        }
        return result;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Intertube.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Intertube.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^ITS-[A-Z]+-\d{3}$")]
    private static partial Regex UidPattern();
}
