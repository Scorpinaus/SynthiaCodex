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

public enum GitHunkOperation
{
    Stage,
    Unstage,
    Discard
}

public sealed record GitDiffHunkPatch(string Header, string Patch);

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

        var lines = NormalizeLines(diff);

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

    public static IReadOnlyList<GitDiffHunkPatch> ParseHunks(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return [];
        }

        var lines = NormalizeLines(diff);
        var result = new List<GitDiffHunkPatch>();
        var fileHeader = new List<string>();
        var collectingFileHeader = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                fileHeader.Clear();
                fileHeader.Add(line);
                collectingFileHeader = true;
                continue;
            }

            if (!HunkHeaderRegex.IsMatch(line))
            {
                if (collectingFileHeader)
                {
                    fileHeader.Add(line);
                }
                continue;
            }

            if (fileHeader.Count == 0)
            {
                continue;
            }
            collectingFileHeader = false;

            var hunkLines = new List<string> { line };
            for (var next = index + 1; next < lines.Length; next++)
            {
                if (HunkHeaderRegex.IsMatch(lines[next]) ||
                    lines[next].StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    break;
                }
                hunkLines.Add(lines[next]);
                index = next;
            }

            result.Add(new GitDiffHunkPatch(
                line,
                string.Join('\n', fileHeader.Concat(hunkLines)) + "\n"));
        }

        return result;
    }

    private static string[] NormalizeLines(string diff)
    {
        var lines = diff
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0
            ? lines[..^1]
            : lines;
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

public static class GitUnifiedDiffDocumentParser
{
    private static readonly Regex DiffHeaderRegex = new(
        """^diff --git (?<old>"(?:\\.|[^"])*"|\S+) (?<new>"(?:\\.|[^"])*"|\S+)$""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<GitDiffDocument> Parse(string? diff, string statusSummary)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            return [];
        }

        var normalized = diff
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var starts = new List<int>();
        var position = 0;
        while (position < normalized.Length)
        {
            if ((position == 0 || normalized[position - 1] == '\n') &&
                normalized.AsSpan(position).StartsWith("diff --git ", StringComparison.Ordinal))
            {
                starts.Add(position);
            }
            var next = normalized.IndexOf('\n', position);
            position = next < 0 ? normalized.Length : next + 1;
        }

        var documents = new List<GitDiffDocument>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1] : normalized.Length;
            var section = normalized[start..end].TrimEnd('\n') + "\n";
            if (TryCreateDocument(section, statusSummary, out var document))
            {
                documents.Add(document);
            }
        }
        return documents;
    }

    private static bool TryCreateDocument(
        string section,
        string statusSummary,
        out GitDiffDocument document)
    {
        var lines = section.Split('\n');
        var oldPath = ReadPath(lines, "rename from ") ?? ReadPath(lines, "--- ");
        var newPath = ReadPath(lines, "rename to ") ?? ReadPath(lines, "+++ ");
        var header = DiffHeaderRegex.Match(lines[0]);
        if (header.Success)
        {
            oldPath ??= Unquote(header.Groups["old"].Value);
            newPath ??= Unquote(header.Groups["new"].Value);
        }
        var isAdded = string.Equals(oldPath, "/dev/null", StringComparison.Ordinal);
        var isDeleted = string.Equals(newPath, "/dev/null", StringComparison.Ordinal);
        oldPath = isAdded ? null : NormalizePath(oldPath);
        newPath = isDeleted ? null : NormalizePath(newPath);
        var path = newPath ?? oldPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            document = null!;
            return false;
        }

        var originalPath = oldPath is not null && newPath is not null &&
            !string.Equals(oldPath, newPath, StringComparison.Ordinal)
                ? oldPath
                : null;
        document = new GitDiffDocument(
            new GitChangedFile(path, originalPath, ' ', ' ', statusSummary),
            section);
        return true;
    }

    private static string? ReadPath(IEnumerable<string> lines, string prefix)
    {
        var line = lines.FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? null : Unquote(line[prefix.Length..].Trim());
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path, "/dev/null", StringComparison.Ordinal))
        {
            return null;
        }
        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;
    }

    private static string Unquote(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }
        return path[1..^1]
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}
