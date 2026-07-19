using System.Text;
using SynthiaCode.Core.Attachments;

namespace SynthiaCode.Infrastructure.Attachments;

public sealed class WorkspaceAttachmentResolver
{
    public bool IsWithinWorkspace(string workspaceRoot, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) || IsContained(root, candidate);
    }

    public AttachmentReference Resolve(
        string workspaceRoot,
        string candidatePath,
        AttachmentKind expectedKind)
    {
        if (expectedKind is not (AttachmentKind.File or AttachmentKind.Folder))
        {
            throw new ArgumentException("Workspace references must be files or folders.", nameof(expectedKind));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        if (candidatePath.IndexOfAny(['*', '?']) >= 0)
        {
            throw new InvalidDataException("Workspace attachment paths cannot contain wildcards.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The active workspace is unavailable: {root}");
        }
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Attach a folder inside the workspace rather than the workspace root.");
        }
        if (!IsContained(root, candidate))
        {
            throw new InvalidDataException("Files and folders must be inside the active workspace.");
        }
        if (HasAlternateDataStream(candidate))
        {
            throw new InvalidDataException("Alternate data streams cannot be attached.");
        }

        var exists = expectedKind == AttachmentKind.Folder
            ? Directory.Exists(candidate)
            : File.Exists(candidate);
        if (!exists)
        {
            throw expectedKind == AttachmentKind.Folder
                ? new DirectoryNotFoundException("The selected folder no longer exists.")
                : new FileNotFoundException("The selected file no longer exists.", candidate);
        }

        ValidateReparseContainment(root, candidate);
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, candidate));
        if (Encoding.UTF8.GetByteCount(relativePath) > AttachmentLimits.MaximumWorkspacePathBytes)
        {
            throw new InvalidDataException($"The workspace path exceeds the {AttachmentLimits.MaximumWorkspacePathBytes / 1024} KiB limit.");
        }

        var info = expectedKind == AttachmentKind.Folder
            ? (FileSystemInfo)new DirectoryInfo(candidate)
            : new FileInfo(candidate);
        return new AttachmentReference
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = expectedKind,
            SourceKind = AttachmentSourceKind.WorkspaceReference,
            DisplayName = SanitizeDisplayName(info.Name, expectedKind),
            MediaType = expectedKind == AttachmentKind.Folder ? "inode/directory" : InferMediaType(info.Extension),
            ByteLength = info is FileInfo file ? file.Length : 0,
            WorkspaceRelativePath = relativePath,
            ManagedPath = candidate
        };
    }

    public AttachmentReference Revalidate(string workspaceRoot, AttachmentReference attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.SourceKind != AttachmentSourceKind.WorkspaceReference ||
            attachment.Kind is not (AttachmentKind.File or AttachmentKind.Folder) ||
            string.IsNullOrWhiteSpace(attachment.WorkspaceRelativePath) ||
            Path.IsPathRooted(attachment.WorkspaceRelativePath))
        {
            throw new InvalidDataException("The workspace attachment reference is invalid.");
        }
        var candidate = Path.Combine(
            Path.GetFullPath(workspaceRoot),
            attachment.WorkspaceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var resolved = Resolve(workspaceRoot, candidate, attachment.Kind);
        resolved.Id = attachment.Id;
        return resolved;
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool HasAlternateDataStream(string path)
    {
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        return path.AsSpan(rootLength).Contains(':');
    }

    private static void ValidateReparseContainment(string root, string candidate)
    {
        var resolvedRoot = ResolveLinkIfNeeded(new DirectoryInfo(root));
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!info.Exists)
            {
                break;
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }
            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new InvalidDataException("The selected path contains a broken filesystem link.");
            var resolvedTarget = Path.GetFullPath(target.FullName);
            if (!IsContained(resolvedRoot, resolvedTarget) &&
                !string.Equals(resolvedRoot, resolvedTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The selected path resolves outside the active workspace.");
            }
        }
    }

    private static string ResolveLinkIfNeeded(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return Path.GetFullPath(info.FullName);
        }
        return Path.GetFullPath(
            info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? throw new InvalidDataException("The workspace root contains a broken filesystem link."));
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("The workspace attachment path is invalid.");
        }
        return normalized;
    }

    private static string SanitizeDisplayName(string value, AttachmentKind kind)
    {
        var cleaned = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = kind == AttachmentKind.Folder ? "Folder" : "File";
        }
        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    private static string InferMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" or ".md" or ".cs" or ".xaml" or ".json" or ".xml" or ".yaml" or ".yml" or
        ".js" or ".ts" or ".tsx" or ".jsx" or ".css" or ".html" or ".htm" or ".py" or
        ".rs" or ".go" or ".java" or ".cpp" or ".c" or ".h" => "text/plain",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };
}
