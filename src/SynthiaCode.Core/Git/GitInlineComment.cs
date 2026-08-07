using System.Text;
using System.Text.Json.Serialization;

namespace SynthiaCode.Core.Git;

public enum GitDiffSide
{
    Old,
    New
}

public sealed class GitInlineComment
{
    public const int MaximumComments = 100;
    public const int MaximumBodyBytes = 16 * 1024;
    public const int MaximumAggregateBytes = 64 * 1024;
    public const int MaximumLineTextBytes = 4 * 1024;
    private const int MaximumPathBytes = 32 * 1024;

    public string Id { get; set; } = string.Empty;

    public string RepositoryRoot { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string? OriginalFilePath { get; set; }

    public GitDiffSide Side { get; set; }

    public int LineNumber { get; set; }

    public string LineText { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public string SideLabel => Side == GitDiffSide.Old ? "Old line" : "New line";

    [JsonIgnore]
    public string TargetFilePath => Side == GitDiffSide.Old && !string.IsNullOrWhiteSpace(OriginalFilePath)
        ? OriginalFilePath
        : FilePath;

    [JsonIgnore]
    public string AbsoluteFilePath => Path.GetFullPath(Path.Combine(
        RepositoryRoot,
        TargetFilePath.Replace('/', Path.DirectorySeparatorChar)));

    [JsonIgnore]
    public string DisplayLocation => $"{TargetFilePath}:L{LineNumber} ({SideLabel})";

    [JsonIgnore]
    public string AutomationName => $"Inline review comment at {DisplayLocation}: {Body}";

    public static GitInlineComment Create(
        string repositoryRoot,
        string filePath,
        string? originalFilePath,
        GitDiffSide side,
        int lineNumber,
        string lineText,
        string body,
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        return Normalize(
            new GitInlineComment
            {
                Id = Guid.NewGuid().ToString("N"),
                RepositoryRoot = repositoryRoot,
                FilePath = filePath,
                OriginalFilePath = originalFilePath,
                Side = side,
                LineNumber = lineNumber,
                LineText = lineText,
                Body = body,
                CreatedAt = now,
                UpdatedAt = now
            },
            requireExistingIdentity: true);
    }

    public GitInlineComment WithBody(string body, DateTimeOffset? timestamp = null)
    {
        var updated = Clone();
        updated.Body = body;
        updated.UpdatedAt = timestamp ?? DateTimeOffset.UtcNow;
        return Normalize(updated, requireExistingIdentity: true);
    }

    public GitInlineComment Clone() => new()
    {
        Id = Id,
        RepositoryRoot = RepositoryRoot,
        FilePath = FilePath,
        OriginalFilePath = OriginalFilePath,
        Side = Side,
        LineNumber = LineNumber,
        LineText = LineText,
        Body = Body,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };

    public static IReadOnlyList<GitInlineComment> NormalizeRestored(
        IEnumerable<GitInlineComment>? comments) => NormalizeMany(comments, throwOnInvalid: false);

    public static IReadOnlyList<GitInlineComment> NormalizeForSubmission(
        IEnumerable<GitInlineComment>? comments) => NormalizeMany(comments, throwOnInvalid: true);

    private static IReadOnlyList<GitInlineComment> NormalizeMany(
        IEnumerable<GitInlineComment>? comments,
        bool throwOnInvalid)
    {
        var result = new List<GitInlineComment>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var aggregateBytes = 0;
        foreach (var source in comments ?? [])
        {
            GitInlineComment normalized;
            try
            {
                normalized = Normalize(source, requireExistingIdentity: true);
            }
            catch (Exception exception) when (
                !throwOnInvalid &&
                exception is ArgumentException or InvalidDataException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!ids.Add(normalized.Id))
            {
                continue;
            }
            var itemBytes = GetPayloadBytes(normalized);
            if (result.Count >= MaximumComments || aggregateBytes + itemBytes > MaximumAggregateBytes)
            {
                if (throwOnInvalid)
                {
                    throw new InvalidDataException(
                        $"Inline review comments can contain at most {MaximumComments} entries and {MaximumAggregateBytes / 1024} KiB of text.");
                }
                break;
            }
            aggregateBytes += itemBytes;
            result.Add(normalized);
        }
        return result;
    }

    private static GitInlineComment Normalize(GitInlineComment source, bool requireExistingIdentity)
    {
        ArgumentNullException.ThrowIfNull(source);
        var id = source.Id?.Trim() ?? string.Empty;
        if (requireExistingIdentity && !Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidDataException("An inline review comment has an invalid identity.");
        }

        var root = NormalizeRoot(source.RepositoryRoot);
        var filePath = NormalizeRelativePath(root, source.FilePath, "file");
        var originalPath = string.IsNullOrWhiteSpace(source.OriginalFilePath)
            ? null
            : NormalizeRelativePath(root, source.OriginalFilePath, "original file");
        if (!Enum.IsDefined(source.Side))
        {
            throw new InvalidDataException("An inline review comment has an invalid diff side.");
        }
        if (source.LineNumber <= 0)
        {
            throw new InvalidDataException("An inline review comment line number must be positive.");
        }

        var lineText = NormalizeLineText(source.LineText);
        var body = NormalizeBody(source.Body);
        if (source.CreatedAt == default || source.UpdatedAt == default || source.UpdatedAt < source.CreatedAt)
        {
            throw new InvalidDataException("An inline review comment has invalid timestamps.");
        }

        return new GitInlineComment
        {
            Id = id,
            RepositoryRoot = root,
            FilePath = filePath,
            OriginalFilePath = originalPath,
            Side = source.Side,
            LineNumber = source.LineNumber,
            LineText = lineText,
            Body = body,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private static string NormalizeRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("An inline review comment repository root must be absolute.");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        if (Encoding.UTF8.GetByteCount(root) > MaximumPathBytes)
        {
            throw new InvalidDataException("An inline review comment repository root is too long.");
        }
        return root;
    }

    private static string NormalizeRelativePath(string root, string value, string label)
    {
        var normalized = value?.Trim().Replace('\\', '/') ?? string.Empty;
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        if (string.IsNullOrWhiteSpace(normalized) ||
            Path.IsPathFullyQualified(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..") ||
            Encoding.UTF8.GetByteCount(normalized) > MaximumPathBytes)
        {
            throw new InvalidDataException($"An inline review comment {label} path must stay inside its repository.");
        }

        var absolute = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"An inline review comment {label} path must stay inside its repository.");
        }
        return normalized;
    }

    private static string NormalizeLineText(string value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (Encoding.UTF8.GetByteCount(normalized) > MaximumLineTextBytes)
        {
            throw new InvalidDataException(
                $"An inline review comment diff line cannot exceed {MaximumLineTextBytes / 1024} KiB.");
        }
        return normalized;
    }

    private static string NormalizeBody(string value)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var bytes = Encoding.UTF8.GetByteCount(normalized);
        if (bytes == 0)
        {
            throw new InvalidDataException("Enter an inline review comment before saving.");
        }
        if (bytes > MaximumBodyBytes)
        {
            throw new InvalidDataException(
                $"An inline review comment cannot exceed {MaximumBodyBytes / 1024} KiB.");
        }
        return normalized;
    }

    private static int GetPayloadBytes(GitInlineComment comment) =>
        Encoding.UTF8.GetByteCount(comment.RepositoryRoot) +
        Encoding.UTF8.GetByteCount(comment.FilePath) +
        Encoding.UTF8.GetByteCount(comment.OriginalFilePath ?? string.Empty) +
        Encoding.UTF8.GetByteCount(comment.LineText) +
        Encoding.UTF8.GetByteCount(comment.Body);
}

public static class GitInlineCommentPromptFormatter
{
    private const string Header = "Inline review comments from the user:";

    public static string AppendToPrompt(
        string? prompt,
        IEnumerable<GitInlineComment>? comments)
    {
        var normalizedPrompt = (prompt ?? string.Empty).Trim();
        var normalizedComments = GitInlineComment.NormalizeForSubmission(comments);
        if (normalizedComments.Count == 0)
        {
            return normalizedPrompt;
        }

        var builder = new StringBuilder();
        if (normalizedPrompt.Length > 0)
        {
            builder.AppendLine(normalizedPrompt);
            builder.AppendLine();
        }
        builder.AppendLine(Header);
        for (var index = 0; index < normalizedComments.Count; index++)
        {
            var comment = normalizedComments[index];
            builder.Append(index + 1)
                .Append(". ")
                .Append(comment.AbsoluteFilePath)
                .Append(" (")
                .Append(comment.Side == GitDiffSide.Old ? "old" : "new")
                .Append(" side, line ")
                .Append(comment.LineNumber);
            if (comment.Side == GitDiffSide.Old &&
                !string.IsNullOrWhiteSpace(comment.OriginalFilePath) &&
                !string.Equals(comment.OriginalFilePath, comment.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append("; current path ")
                    .Append(Path.GetFullPath(Path.Combine(
                        comment.RepositoryRoot,
                        comment.FilePath.Replace('/', Path.DirectorySeparatorChar))));
            }
            builder.AppendLine(")");
            if (!string.IsNullOrWhiteSpace(comment.LineText))
            {
                builder.Append("   Diff line: ").AppendLine(comment.LineText);
            }
            builder.AppendLine("   Comment:");
            foreach (var line in comment.Body.Split('\n'))
            {
                builder.Append("   ").AppendLine(line);
            }
        }
        return builder.ToString().TrimEnd();
    }
}
