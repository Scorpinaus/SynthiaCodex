using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SynthiaCode.Core.Codex.AppServer;

public enum CodexReviewPriority
{
    P0 = 0,
    P1 = 1,
    P2 = 2,
    P3 = 3
}

public sealed record CodexReviewFinding(
    string Title,
    string Body,
    CodexReviewPriority Priority,
    double? ConfidenceScore,
    string AbsoluteFilePath,
    int StartLine,
    int EndLine)
{
    public string PriorityLabel => Priority.ToString();

    public string DisplayTitle => PriorityPrefixRegex.Replace(Title, string.Empty).Trim();

    public string LocationDisplay => StartLine == EndLine
        ? $"{AbsoluteFilePath}:{StartLine}"
        : $"{AbsoluteFilePath}:{StartLine}-{EndLine}";

    public string AutomationName => $"{PriorityLabel} review finding: {DisplayTitle}. {LocationDisplay}";

    private static readonly Regex PriorityPrefixRegex = new(
        @"^\[P[0-3]\]\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

public static class CodexReviewFindingParser
{
    private const int MaximumReviewCharacters = 1_000_000;
    private const int MaximumFindings = 100;
    private const int MaximumTitleCharacters = 512;
    private const int MaximumBodyCharacters = 64 * 1024;
    private const int MaximumPathCharacters = 4096;
    private const string LocationSeparator = " — ";

    private static readonly Regex PriorityRegex = new(
        @"^\[P(?<priority>[0-3])\]\s+(?<title>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocationRegex = new(
        @"^(?<path>.+):(?<start>[1-9][0-9]*)-(?<end>[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<CodexReviewFinding> Parse(string? review)
    {
        if (string.IsNullOrWhiteSpace(review))
        {
            return [];
        }

        var bounded = review.Length <= MaximumReviewCharacters
            ? review
            : review[..MaximumReviewCharacters];
        if (TryParseJsonReview(bounded, out var jsonFindings))
        {
            return jsonFindings;
        }

        return ParsePlainText(bounded);
    }

    private static bool TryParseJsonReview(
        string review,
        out IReadOnlyList<CodexReviewFinding> findings)
    {
        findings = [];
        if (TryParseJsonDocument(review, out var document) ||
            TryParseEmbeddedJsonDocument(review, out document))
        {
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("findings", out var findingArray) ||
                    findingArray.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                findings = Deduplicate(findingArray
                    .EnumerateArray()
                    .Take(MaximumFindings)
                    .Select(ParseJsonFinding)
                    .Where(item => item is not null)
                    .Cast<CodexReviewFinding>());
                return true;
            }
        }

        return false;
    }

    private static bool TryParseJsonDocument(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static bool TryParseEmbeddedJsonDocument(string text, out JsonDocument document)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            document = null!;
            return false;
        }

        return TryParseJsonDocument(text[start..(end + 1)], out document);
    }

    private static CodexReviewFinding? ParseJsonFinding(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !TryReadString(item, "title", out var title) ||
            !TryReadString(item, "body", out var body) ||
            !item.TryGetProperty("code_location", out var location) ||
            location.ValueKind != JsonValueKind.Object ||
            !TryReadString(location, "absolute_file_path", out var path) ||
            !location.TryGetProperty("line_range", out var lineRange) ||
            lineRange.ValueKind != JsonValueKind.Object ||
            !TryReadPositiveInt(lineRange, "start", out var startLine) ||
            !TryReadPositiveInt(lineRange, "end", out var endLine))
        {
            return null;
        }

        CodexReviewPriority priority;
        if (item.TryGetProperty("priority", out var priorityElement) &&
            priorityElement.ValueKind != JsonValueKind.Null)
        {
            if (!priorityElement.TryGetInt32(out var priorityValue) ||
                !TryCreatePriority(priorityValue, out priority))
            {
                return null;
            }
        }
        else if (!TryReadPriorityFromTitle(title, out priority))
        {
            return null;
        }

        double? confidence = null;
        if (item.TryGetProperty("confidence_score", out var confidenceElement) &&
            confidenceElement.ValueKind != JsonValueKind.Null)
        {
            if (!confidenceElement.TryGetDouble(out var confidenceValue) ||
                !double.IsFinite(confidenceValue) ||
                confidenceValue is < 0 or > 1)
            {
                return null;
            }
            confidence = confidenceValue;
        }

        return TryCreateFinding(title, body, priority, confidence, path, startLine, endLine);
    }

    private static IReadOnlyList<CodexReviewFinding> ParsePlainText(string review)
    {
        var lines = review
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var parsedHeaders = new List<(int Index, PlainFindingHeader Header)>();
        for (var index = 0; index < lines.Length && parsedHeaders.Count < MaximumFindings; index++)
        {
            if (TryParsePlainHeader(lines[index], out var header))
            {
                parsedHeaders.Add((index, header));
            }
        }

        var findings = new List<CodexReviewFinding>(parsedHeaders.Count);
        for (var index = 0; index < parsedHeaders.Count; index++)
        {
            var current = parsedHeaders[index];
            var end = index + 1 < parsedHeaders.Count ? parsedHeaders[index + 1].Index : lines.Length;
            var bodyLines = new List<string>();
            for (var lineIndex = current.Index + 1; lineIndex < end; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.StartsWith("  ", StringComparison.Ordinal))
                {
                    bodyLines.Add(line[2..]);
                }
                else if (string.IsNullOrWhiteSpace(line) && bodyLines.Count > 0)
                {
                    bodyLines.Add(string.Empty);
                }
            }

            while (bodyLines.Count > 0 && string.IsNullOrWhiteSpace(bodyLines[^1]))
            {
                bodyLines.RemoveAt(bodyLines.Count - 1);
            }

            var body = string.Join('\n', bodyLines);
            var finding = TryCreateFinding(
                current.Header.Title,
                body,
                current.Header.Priority,
                null,
                current.Header.Path,
                current.Header.StartLine,
                current.Header.EndLine);
            if (finding is not null)
            {
                findings.Add(finding);
            }
        }

        return Deduplicate(findings);
    }

    private static bool TryParsePlainHeader(string line, out PlainFindingHeader header)
    {
        header = default;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("- [P", StringComparison.Ordinal))
        {
            return false;
        }

        var content = trimmed[2..];
        var separator = content.LastIndexOf(LocationSeparator, StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var title = content[..separator].Trim();
        var locationMatch = LocationRegex.Match(content[(separator + LocationSeparator.Length)..].Trim());
        if (!locationMatch.Success ||
            !TryReadPriorityFromTitle(title, out var priority) ||
            !int.TryParse(locationMatch.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var startLine) ||
            !int.TryParse(locationMatch.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var endLine))
        {
            return false;
        }

        header = new PlainFindingHeader(
            title,
            priority,
            locationMatch.Groups["path"].Value.Trim(),
            startLine,
            endLine);
        return true;
    }

    private static CodexReviewFinding? TryCreateFinding(
        string title,
        string body,
        CodexReviewPriority priority,
        double? confidence,
        string path,
        int startLine,
        int endLine)
    {
        title = title.Trim();
        body = body.Trim();
        path = path.Trim();
        if (title.Length is 0 or > MaximumTitleCharacters ||
            body.Length is 0 or > MaximumBodyCharacters ||
            path.Length is 0 or > MaximumPathCharacters ||
            startLine <= 0 ||
            endLine < startLine)
        {
            return null;
        }

        return new CodexReviewFinding(title, body, priority, confidence, path, startLine, endLine);
    }

    private static IReadOnlyList<CodexReviewFinding> Deduplicate(IEnumerable<CodexReviewFinding> findings)
    {
        var unique = new HashSet<CodexReviewFinding>();
        var result = new List<CodexReviewFinding>();
        foreach (var finding in findings)
        {
            if (unique.Add(finding))
            {
                result.Add(finding);
            }
        }
        return result;
    }

    private static bool TryReadPriorityFromTitle(string title, out CodexReviewPriority priority)
    {
        priority = default;
        var match = PriorityRegex.Match(title.Trim());
        return match.Success &&
            int.TryParse(match.Groups["priority"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
            TryCreatePriority(value, out priority);
    }

    private static bool TryCreatePriority(int value, out CodexReviewPriority priority)
    {
        if (value is >= 0 and <= 3)
        {
            priority = (CodexReviewPriority)value;
            return true;
        }

        priority = default;
        return false;
    }

    private static bool TryReadString(JsonElement parent, string propertyName, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadPositiveInt(JsonElement parent, string propertyName, out int value)
    {
        value = 0;
        return parent.TryGetProperty(propertyName, out var element) &&
            element.TryGetInt32(out value) &&
            value > 0;
    }

    private readonly record struct PlainFindingHeader(
        string Title,
        CodexReviewPriority Priority,
        string Path,
        int StartLine,
        int EndLine);
}

public static class CodexReviewFindingProjection
{
    public static IReadOnlyList<CodexReviewFinding> GetLatest(
        IEnumerable<CodexConversationTurn> conversationTurns)
    {
        ArgumentNullException.ThrowIfNull(conversationTurns);
        return conversationTurns
            .Where(turn => turn.IsCodeReview && !turn.IsSuperseded)
            .LastOrDefault()
            ?.ReviewFindings ?? [];
    }
}
