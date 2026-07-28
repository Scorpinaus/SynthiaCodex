using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Worktrees;
using System.IO;

namespace SynthiaCode.App.Services;

/// <summary>
/// Application use cases that change durable Codex-thread lifecycle state.  It has no
/// WPF dependency; callers project the returned state into navigation and transcript UI.
/// </summary>
public sealed class ThreadLifecycleUseCaseService
{
    private readonly IAppServerSessionCoordinator appServer;
    private readonly IGitService git;
    private readonly IWorktreeService worktrees;
    private readonly ThreadStore threadStore;
    private readonly CodexThreadWorkspace threadWorkspace;
    private readonly ISettingsStore settingsStore;

    public ThreadLifecycleUseCaseService(
        IAppServerSessionCoordinator appServer,
        IGitService git,
        IWorktreeService worktrees,
        ThreadStore threadStore,
        CodexThreadWorkspace threadWorkspace,
        ISettingsStore settingsStore)
    {
        this.appServer = appServer;
        this.git = git;
        this.worktrees = worktrees;
        this.threadStore = threadStore;
        this.threadWorkspace = threadWorkspace;
        this.settingsStore = settingsStore;
    }

    public async Task<ThreadStartUseCaseResult> StartAsync(
        ThreadStartUseCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = await appServer.StartThreadAsync(request.StartOptions, cancellationToken).ConfigureAwait(false);
        AssistantWorktree? worktree = null;
        if (request.CreateWorktree)
        {
            if (request.Scope.Kind != ThreadScopeKind.Project || string.IsNullOrWhiteSpace(request.Scope.ProjectPath))
            {
                throw new InvalidOperationException("Only project threads can create a Git worktree.");
            }

            var repository = await git.GetRepositoryStateAsync(request.Scope.ProjectPath, cancellationToken).ConfigureAwait(false);
            if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.RootPath))
            {
                throw new InvalidOperationException("A new worktree requires a detected Git repository.");
            }
            worktree = await worktrees.CreateAsync(new WorktreeCreateRequest(
                repository.RootPath,
                request.WorktreeTaskId,
                started.ThreadId), cancellationToken).ConfigureAwait(false);
        }

        var created = await CreateAsync(new ThreadCreateRequest(
            request.Settings,
            request.Scope,
            started.ThreadId,
            request.Title,
            worktree?.Path ?? request.WorkspacePath,
            worktree?.Branch,
            request.Instructions,
            request.IsTitlePlaceholder), cancellationToken).ConfigureAwait(false);
        return new ThreadStartUseCaseResult(created.State, worktree);
    }

    public async Task<ThreadResumeUseCaseResult> ResumeAsync(
        CodexThreadResumeRequest request,
        CancellationToken cancellationToken = default)
    {
        var resumed = await appServer.ResumeThreadAsync(request, cancellationToken).ConfigureAwait(false);
        var service = threadWorkspace.GetRequired(resumed.ThreadId);
        service.ReconcileHistory(resumed.Turns ?? []);
        return new ThreadResumeUseCaseResult(resumed.ThreadId, service.SnapshotConversation());
    }

    public async Task<ThreadActivationUseCaseResult> ResumeOrReplaceAsync(
        ThreadActivationUseCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var resumed = await ResumeAsync(request.ResumeRequest, cancellationToken).ConfigureAwait(false);
            return ThreadActivationUseCaseResult.Resumed(resumed.ThreadId, resumed.Turns);
        }
        catch (Exception resumeError)
        {
            var replacement = await StartAsync(new ThreadStartUseCaseRequest(
                request.Settings,
                request.Scope,
                request.ReplacementTitle,
                request.WorkspacePath,
                request.ReplacementStartOptions,
                request.Instructions,
                IsTitlePlaceholder: true,
                CreateWorktree: false,
                WorktreeTaskId: string.Empty), cancellationToken).ConfigureAwait(false);
            return ThreadActivationUseCaseResult.Replaced(
                replacement.State.ThreadId,
                resumeError);
        }
    }

    public async Task<bool> RenameIfPlaceholderAsync(
        AppSettings settings,
        string threadId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted?.IsTitlePlaceholder != true)
        {
            return false;
        }

        await appServer.SetThreadNameAsync(threadId, title, cancellationToken).ConfigureAwait(false);
        persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted?.IsTitlePlaceholder != true)
        {
            return false;
        }

        threadStore.Rename(settings, threadId, title);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ThreadForkResult> ForkAsync(
        ThreadForkRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceService = threadWorkspace.GetRequired(request.Source.ThreadId);
        var sourceConversation = sourceService.SnapshotConversation();
        var forkPoint = string.IsNullOrWhiteSpace(request.ForkPointTurnId)
            ? null
            : sourceService.ConversationTurns.FirstOrDefault(turn =>
                string.Equals(turn.TurnId, request.ForkPointTurnId, StringComparison.Ordinal));
        var forkPointIndex = forkPoint is null
            ? sourceConversation.Count - 1
            : sourceService.ConversationTurns.IndexOf(forkPoint);
        if (!string.IsNullOrWhiteSpace(request.ForkPointTurnId) && forkPoint is null)
        {
            throw new InvalidOperationException("The selected assistant response is no longer part of this chat.");
        }

        var rollbackCount = forkPoint is null
            ? 0
            : sourceService.GetActiveRollbackTurnCount(forkPoint) - 1;
        if (rollbackCount < 0)
        {
            throw new InvalidOperationException("Only an active completed response can be forked.");
        }

        var forked = await appServer.ForkThreadAsync(request.ForkOptions, cancellationToken).ConfigureAwait(false);
        if (rollbackCount > 0)
        {
            var rollback = await appServer.RollbackThreadAsync(
                new CodexThreadRollbackRequest(forked.ThreadId, rollbackCount), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(rollback.ThreadId, forked.ThreadId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex returned a different thread while creating the conversation fork.");
            }
        }

        AssistantWorktree? worktree = null;
        if (request.CreateWorktree)
        {
            var repository = await git.GetRepositoryStateAsync(request.Source.ProjectPath, cancellationToken).ConfigureAwait(false);
            if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.RootPath))
            {
                throw new InvalidOperationException("The source project is no longer a Git repository.");
            }
            worktree = await worktrees.CreateAsync(new WorktreeCreateRequest(
                repository.RootPath,
                $"fork-{forked.ThreadId}",
                forked.ThreadId,
                request.Source.WorktreeBranch ?? "HEAD"), cancellationToken).ConfigureAwait(false);
        }

        var state = CreateState(
            request.Source.ScopeKey,
            forked.ThreadId,
            $"Fork of {request.Source.DisplayTitle}",
            worktree?.Path ?? request.WorkspacePath,
            worktree?.Branch,
            request.Instructions,
            isTitlePlaceholder: false);
        state.Preview = forkPoint?.UserPrompt ?? request.Source.Preview;
        state.FinalResponse = forkPoint?.AssistantResponse ?? sourceService.FinalResponse;
        state.ConversationTurns = sourceConversation
            .Take(forkPoint is null ? sourceConversation.Count : forkPointIndex + 1)
            .Select(CloneConversationTurn)
            .ToList();
        state.ContextTokensUsed = rollbackCount == 0 ? sourceService.ContextTokensUsed : 0;
        state.ContextWindowTokens = sourceService.ContextWindowTokens;
        state.ContextCompactionCount = rollbackCount == 0 ? sourceService.ContextCompactionCount : 0;
        threadStore.Upsert(request.Settings, state);
        threadStore.SetActive(request.Settings, state.ScopeKey, state.ThreadId);
        threadWorkspace.Restore(state);
        await settingsStore.SaveAsync(request.Settings, cancellationToken).ConfigureAwait(false);
        return new ThreadForkResult(state, worktree, rollbackCount);
    }

    public async Task<ThreadCreateResult> CreateAsync(ThreadCreateRequest request, CancellationToken cancellationToken = default)
    {
        var state = CreateState(
            request.Scope,
            request.ThreadId,
            request.Title,
            request.WorkspacePath,
            request.WorktreeBranch,
            request.Instructions,
            request.IsTitlePlaceholder);
        var priorActiveId = threadStore.GetActive(request.Settings, request.Scope)?.ThreadId;
        try
        {
            threadStore.Upsert(request.Settings, state);
            var persistedState = request.Settings.ProjectThreads.First(thread =>
                string.Equals(thread.ThreadId, state.ThreadId, StringComparison.Ordinal));
            persistedState.IsTitlePlaceholder = request.IsTitlePlaceholder;
            threadStore.SetActive(request.Settings, request.Scope, state.ThreadId);
            await settingsStore.SaveAsync(request.Settings, cancellationToken).ConfigureAwait(false);
            threadWorkspace.Restore(state);
            return new ThreadCreateResult(SettingsStorageMapper.ToPresentation(SettingsStorageMapper.ToPersisted(state)), priorActiveId);
        }
        catch
        {
            threadStore.Delete(request.Settings, state.ThreadId);
            if (!string.IsNullOrWhiteSpace(priorActiveId))
            {
                threadStore.SetActive(request.Settings, request.Scope, priorActiveId);
            }
            throw;
        }
    }

    public async Task ArchiveAsync(AppSettings settings, string threadId, CancellationToken cancellationToken = default)
    {
        await appServer.ArchiveThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        threadStore.SetArchived(settings, threadId, archived: true);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnarchiveAsync(AppSettings settings, string threadId, CancellationToken cancellationToken = default)
    {
        await appServer.UnarchiveThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        threadStore.SetArchived(settings, threadId, archived: false);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetPinnedAsync(AppSettings settings, string threadId, bool pinned, CancellationToken cancellationToken = default)
    {
        threadStore.SetPinned(settings, threadId, pinned);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return pinned;
    }

    public async Task RenameAsync(AppSettings settings, string threadId, string title, CancellationToken cancellationToken = default)
    {
        await appServer.SetThreadNameAsync(threadId, title, cancellationToken).ConfigureAwait(false);
        threadStore.Rename(settings, threadId, title);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(AppSettings settings, string threadId, bool archiveFirst, CancellationToken cancellationToken = default)
    {
        var existing = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Chat '{threadId}' was not found.");
        var restore = SettingsStorageMapper.ToPresentation(existing);
        if (archiveFirst)
        {
            await appServer.ArchiveThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        }
        threadStore.Delete(settings, threadId);
        try
        {
            await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The remote archive is irreversible from this transaction's point of
            // view, so restore the local record as archived rather than claiming it
            // still represents a live remote thread.
            restore.IsArchived = archiveFirst || restore.IsArchived;
            restore.IsActive = false;
            threadStore.Upsert(settings, restore);
            throw;
        }
    }

    public async Task RemoveWorktreeAsync(
        AppSettings settings,
        ProjectThreadState thread,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(thread.WorkspacePath))
        {
            throw new InvalidOperationException("The selected worktree has no workspace path.");
        }

        var repository = await git.GetRepositoryStateAsync(projectPath, cancellationToken).ConfigureAwait(false);
        if (!repository.IsRepository || string.IsNullOrWhiteSpace(repository.RootPath))
        {
            throw new InvalidOperationException("The selected project is no longer a Git repository.");
        }

        await worktrees.RemoveAsync(repository.RootPath, Path.GetFullPath(thread.WorkspacePath), cancellationToken).ConfigureAwait(false);
        var persisted = settings.ProjectThreads.FirstOrDefault(item =>
            string.Equals(item.ThreadId, thread.ThreadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Chat '{thread.ThreadId}' was not found.");
        persisted.Mode = "worktree-removed";
        persisted.TurnStatus = "Workspace removed";
        persisted.UpdatedAt = DateTimeOffset.UtcNow;
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    private static ProjectThreadState CreateState(
        ThreadScopeKey scope, string threadId, string title, string workspacePath, string? worktreeBranch,
        ThreadInstructionSnapshot instructions, bool isTitlePlaceholder) => new()
    {
        ScopeKind = scope.Kind,
        ProjectPath = scope.ProjectPath ?? string.Empty,
        ThreadId = threadId,
        Title = title,
        IsTitlePlaceholder = isTitlePlaceholder,
        Preview = string.Empty,
        IsArchived = false,
        IsPinned = false,
        IsActive = true,
        IsRunning = false,
        TurnStatus = "Idle",
        Mode = scope.Kind == ThreadScopeKind.General ? "general" : worktreeBranch is null ? "local" : "worktree",
        WorkspacePath = workspacePath,
        WorktreeBranch = worktreeBranch,
        AppliedDeveloperInstructions = instructions.DeveloperInstructions,
        AppliedBaseInstructions = instructions.BaseInstructions,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static CodexConversationTurnSnapshot CloneConversationTurn(CodexConversationTurnSnapshot source) => new()
    {
        TurnId = source.TurnId,
        UserPrompt = source.UserPrompt,
        AssistantResponse = source.AssistantResponse,
        Status = source.Status,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        IsSuperseded = source.IsSuperseded,
        Activity = [.. source.Activity],
        UserAttachments = [.. source.UserAttachments.Select(attachment => attachment.Clone())],
        GeneratedImagePaths = [.. source.GeneratedImagePaths]
    };
}

public sealed record ThreadForkRequest(
    AppSettings Settings,
    ProjectThreadState Source,
    string WorkspacePath,
    CodexThreadForkRequest ForkOptions,
    ThreadInstructionSnapshot Instructions,
    string? ForkPointTurnId,
    bool CreateWorktree);
public sealed record ThreadForkResult(ProjectThreadState State, AssistantWorktree? Worktree, int RollbackCount);
public sealed record ThreadCreateRequest(
    AppSettings Settings,
    ThreadScopeKey Scope,
    string ThreadId,
    string Title,
    string WorkspacePath,
    string? WorktreeBranch,
    ThreadInstructionSnapshot Instructions,
    bool IsTitlePlaceholder);
public sealed record ThreadCreateResult(ProjectThreadState State, string? PreviousActiveThreadId);
public readonly record struct ThreadInstructionSnapshot(string? DeveloperInstructions, string? BaseInstructions);

public sealed record ThreadStartUseCaseRequest(
    AppSettings Settings,
    ThreadScopeKey Scope,
    string Title,
    string WorkspacePath,
    CodexThreadStartOptions StartOptions,
    ThreadInstructionSnapshot Instructions,
    bool IsTitlePlaceholder,
    bool CreateWorktree,
    string WorktreeTaskId);
public sealed record ThreadStartUseCaseResult(ProjectThreadState State, AssistantWorktree? Worktree);
public sealed record ThreadResumeUseCaseResult(
    string ThreadId,
    IReadOnlyList<CodexConversationTurnSnapshot> Turns);
public sealed record ThreadActivationUseCaseRequest(
    AppSettings Settings,
    ThreadScopeKey Scope,
    string WorkspacePath,
    CodexThreadResumeRequest ResumeRequest,
    CodexThreadStartOptions ReplacementStartOptions,
    ThreadInstructionSnapshot Instructions,
    string ReplacementTitle);
public sealed record ThreadActivationUseCaseResult(
    string ThreadId,
    bool ReplacedThread,
    IReadOnlyList<CodexConversationTurnSnapshot> Turns,
    Exception? ResumeError)
{
    public static ThreadActivationUseCaseResult Resumed(
        string threadId,
        IReadOnlyList<CodexConversationTurnSnapshot> turns) =>
        new(threadId, false, turns, null);

    public static ThreadActivationUseCaseResult Replaced(string threadId, Exception resumeError) =>
        new(threadId, true, [], resumeError);
}
