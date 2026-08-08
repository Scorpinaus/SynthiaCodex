namespace SynthiaCode.Core.Git;

public sealed record GitChangedFile(
    string Path,
    string? OriginalPath,
    char IndexStatus,
    char WorkTreeStatus,
    string? StatusSummaryOverride = null)
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
            if (!string.IsNullOrWhiteSpace(StatusSummaryOverride))
            {
                return StatusSummaryOverride;
            }

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

public sealed record GitReviewCommit(string Sha, string ShortSha, string Title)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Title)
        ? ShortSha
        : $"{ShortSha}  {Title}";
}

public sealed record GitReviewCatalog(
    string RepositoryRoot,
    string CurrentBranch,
    IReadOnlyList<string> BaseBranches,
    IReadOnlyList<GitReviewCommit> Commits);

public sealed record GitBranchCatalog(
    string RepositoryRoot,
    string? CurrentBranch,
    IReadOnlyList<string> Branches,
    bool HasHead)
{
    public string DefaultStartPoint => CurrentBranch ?? "HEAD";
}

public enum GitDiffScope
{
    Unstaged,
    Staged,
    Commit,
    Branch,
    LastTurn
}

public sealed record GitComparisonTarget(GitDiffScope Scope, string Revision)
{
    public static GitComparisonTarget Commit(string sha) =>
        new(GitDiffScope.Commit, RequireRevision(sha, "commit"));

    public static GitComparisonTarget Branch(string branch) =>
        new(GitDiffScope.Branch, RequireRevision(branch, "branch"));

    private static string RequireRevision(string value, string label) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"Select a {label} to compare.", nameof(value))
            : value.Trim();
}

public sealed record GitDiffDocument(GitChangedFile File, string Diff);
