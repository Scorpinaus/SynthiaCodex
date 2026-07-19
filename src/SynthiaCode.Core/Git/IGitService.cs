namespace SynthiaCode.Core.Git;

public interface IGitService
{
    Task<GitRepositoryState> GetRepositoryStateAsync(string workingDirectory, CancellationToken cancellationToken = default);

    Task<string> GetDiffAsync(
        string repositoryRoot,
        GitChangedFile file,
        bool staged,
        CancellationToken cancellationToken = default);

    Task StageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    Task UnstageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    Task RevertAsync(
        string repositoryRoot,
        IReadOnlyCollection<GitChangedFile> files,
        CancellationToken cancellationToken = default);

    Task<GitCommitResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default);
}
