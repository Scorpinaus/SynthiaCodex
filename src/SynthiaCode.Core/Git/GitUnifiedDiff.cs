using System.Globalization;
using System.Text.RegularExpressions;

namespace SynthiaCode.Core.Git;

public enum GitDiffLineKind
{
    Header,
    Hunk,
    Context,
    Addition,
    Removal,
    Metadata
}

public sealed record GitDiffLine(
    string Text,
    GitDiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber)
{
    public string OldLineDisplay => OldLineNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string NewLineDisplay => NewLineNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string Prefix => Kind switch
    {
        GitDiffLineKind.Addition => "+",
        GitDiffLineKind.Removal => "-",
        GitDiffLineKind.Context => " ",
        _ => string.Empty
    };

    public string Content => Kind is GitDiffLineKind.Addition or GitDiffLineKind.Removal or GitDiffLineKind.Context
        ? Text.Length > 0 ? Text[1..] : string.Empty
        : Text;
}

public static class GitUnifiedDiffParser
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@ -(?<old>[0-9]+)(?:,[0-9]+)? \+(?<new>[0-9]+)(?:,[0-9]+)? @@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<GitDiffLine> Parse(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return [];
        }

        var lines = diff
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        var result = new List<GitDiffLine>(lines.Length);
        var insideHunk = false;
        var oldLine = 0;
        var newLine = 0;
        foreach (var line in lines)
        {
            var hunk = HunkHeaderRegex.Match(line);
            if (hunk.Success &&
                int.TryParse(hunk.Groups["old"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out oldLine) &&
                int.TryParse(hunk.Groups["new"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out newLine))
            {
                insideHunk = true;
                result.Add(new GitDiffLine(line, GitDiffLineKind.Hunk, null, null));
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                insideHunk = false;
                result.Add(new GitDiffLine(line, GitDiffLineKind.Header, null, null));
                continue;
            }

            if (!insideHunk)
            {
                var kind = IsHeader(line) ? GitDiffLineKind.Header : GitDiffLineKind.Metadata;
                result.Add(new GitDiffLine(line, kind, null, null));
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                result.Add(new GitDiffLine(line, GitDiffLineKind.Addition, null, newLine));
                newLine++;
                continue;
            }

            if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                result.Add(new GitDiffLine(line, GitDiffLineKind.Removal, oldLine, null));
                oldLine++;
                continue;
            }

            if (line.StartsWith(' '))
            {
                result.Add(new GitDiffLine(line, GitDiffLineKind.Context, oldLine, newLine));
                oldLine++;
                newLine++;
                continue;
            }

            result.Add(new GitDiffLine(line, GitDiffLineKind.Metadata, null, null));
        }

        return result;
    }

    private static bool IsHeader(string line) =>
        line.StartsWith("index ", StringComparison.Ordinal) ||
        line.StartsWith("--- ", StringComparison.Ordinal) ||
        line.StartsWith("+++ ", StringComparison.Ordinal) ||
        line.StartsWith("new file mode ", StringComparison.Ordinal) ||
        line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
        line.StartsWith("similarity index ", StringComparison.Ordinal) ||
        line.StartsWith("rename from ", StringComparison.Ordinal) ||
        line.StartsWith("rename to ", StringComparison.Ordinal);
}
