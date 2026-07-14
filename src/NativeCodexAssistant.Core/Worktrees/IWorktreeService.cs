namespace NativeCodexAssistant.Core.Worktrees;

public interface IWorktreeService
{
    Task<AssistantWorktree> CreateAsync(
        WorktreeCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssistantWorktree>> ListAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string repositoryRoot,
        string worktreePath,
        CancellationToken cancellationToken = default);
}

public sealed record WorktreeCreateRequest(
    string RepositoryRoot,
    string TaskName,
    string? ThreadId = null,
    string? StartPoint = null);

public sealed record AssistantWorktree(
    string RepositoryRoot,
    string Path,
    string Branch,
    string TaskId,
    string? ThreadId,
    DateTimeOffset CreatedAt);
