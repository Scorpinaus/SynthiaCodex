using System.Text.Json.Serialization;

namespace SynthiaCode.Core.Projects;

public sealed record RecentProject(
    string Path,
    string Name,
    DateTimeOffset LastOpenedUtc,
    IReadOnlyList<string>? AdditionalFolderPaths = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> FolderPaths => ProjectFolderSet.NormalizePersisted(Path, AdditionalFolderPaths);
}

public sealed record ProjectFolderUpdateRequest(
    string CurrentPrimaryPath,
    string PrimaryPath,
    IReadOnlyList<string> FolderPaths);

public sealed record ProjectFolderUpdateResult(
    RecentProject Project,
    string PreviousPrimaryPath);

public static class ProjectFolderSet
{
    public static IReadOnlyList<string> NormalizePersisted(
        string primaryPath,
        IEnumerable<string>? additionalFolderPaths)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var result = new List<string>();
        var seen = new HashSet<string>(comparer);
        AddIfValid(primaryPath, result, seen);
        foreach (var path in additionalFolderPaths ?? [])
        {
            AddIfValid(path, result, seen);
        }
        return result;
    }

    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void AddIfValid(string? path, ICollection<string> result, ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }
}
