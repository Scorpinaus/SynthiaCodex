namespace SynthiaCode.Core.Git;

public interface IGitService
{
    Task<GitRepositoryState> GetRepositoryStateAsync(string workingDirectory, CancellationToken cancellationToken = default);

    Task<GitReviewCatalog> GetReviewCatalogAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);

    async Task<GitBranchCatalog> GetBranchCatalogAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var reviewCatalog = await GetReviewCatalogAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        var branches = new[] { reviewCatalog.CurrentBranch }
            .Concat(reviewCatalog.BaseBranches)
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var currentBranch = branches.FirstOrDefault(branch =>
            string.Equals(branch, reviewCatalog.CurrentBranch, StringComparison.Ordinal));
        return new GitBranchCatalog(
            reviewCatalog.RepositoryRoot,
            currentBranch,
            branches,
            currentBranch is not null || reviewCatalog.Commits.Count > 0);
    }

    Task<string> GetDiffAsync(
        string repositoryRoot,
        GitChangedFile file,
        bool staged,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitDiffDocument>> GetComparisonDiffAsync(
        string repositoryRoot,
        GitComparisonTarget target,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Historical Git comparisons are not supported by this service.");

    Task ApplyHunkAsync(
        string repositoryRoot,
        GitDiffHunkPatch patch,
        GitHunkOperation operation,
        CancellationToken cancellationToken = default);

    Task StageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    Task UnstageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    Task RevertAsync(
        string repositoryRoot,
        IReadOnlyCollection<GitChangedFile> files,
        CancellationToken cancellationToken = default);

    Task<GitCommitResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default);
}
