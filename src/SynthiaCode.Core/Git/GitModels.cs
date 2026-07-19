namespace SynthiaCode.Core.Git;

public sealed record GitChangedFile(
    string Path,
    string? OriginalPath,
    char IndexStatus,
    char WorkTreeStatus)
{
    public bool IsStaged => IndexStatus is not ' ' and not '?';

    public bool HasWorkingTreeChanges => WorkTreeStatus is not ' ';

    public bool IsUntracked => IndexStatus == '?' && WorkTreeStatus == '?';

    public string DisplayPath => string.IsNullOrWhiteSpace(OriginalPath)
        ? Path
        : $"{OriginalPath} -> {Path}";

    public string StatusCode => $"{IndexStatus}{WorkTreeStatus}";

    public string StatusSummary
    {
        get
        {
            if (IsUntracked)
            {
                return "Untracked";
            }

            if (IsStaged && HasWorkingTreeChanges)
            {
                return "Staged + working tree";
            }

            return IsStaged ? "Staged" : "Working tree";
        }
    }
}

public sealed record GitRepositoryState(
    bool IsRepository,
    string? RootPath,
    string? Branch,
    IReadOnlyList<GitChangedFile> ChangedFiles,
    string? ErrorMessage)
{
    public static GitRepositoryState NotRepository(string message) =>
        new(false, null, null, [], message);
}

public sealed record GitCommitResult(string CommitId, string Summary);
