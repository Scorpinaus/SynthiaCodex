using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Worktrees;
using System.IO;

namespace SynthiaCode.App.Services;

/// <summary>
/// Application use cases that change durable harness-conversation lifecycle state. It has no
/// WPF dependency; callers project the returned state into navigation and transcript UI.
/// </summary>
public sealed class ThreadLifecycleUseCaseService
{
    private readonly IHarnessOperations harnesses;
    private readonly IGitService git;
    private readonly IWorktreeService worktrees;
    private readonly ThreadStore threadStore;
    private readonly CodexThreadWorkspace threadWorkspace;
    private readonly ISettingsStore settingsStore;

    public ThreadLifecycleUseCaseService(
        IHarnessOperations harnesses,
        IGitService git,
        IWorktreeService worktrees,
        ThreadStore threadStore,
        CodexThreadWorkspace threadWorkspace,
        ISettingsStore settingsStore)
    {
        this.harnesses = harnesses;
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
        var started = await harnesses.StartConversationAsync(
            request.HarnessId,
            request.ConnectionOptions,
            request.StartCommand,
            cancellationToken).ConfigureAwait(false);
        var threadId = started.Address.RemoteId ?? started.Address.LocalId.ToString();
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
                threadId), cancellationToken).ConfigureAwait(false);
        }

        var created = await CreateAsync(new ThreadCreateRequest(
            request.Settings,
            request.Scope,
            threadId,
            request.Title,
            worktree?.Path ?? request.WorkspacePath,
            worktree?.Branch,
            request.Instructions,
            request.IsTitlePlaceholder,
            started.Address), cancellationToken).ConfigureAwait(false);
        return new ThreadStartUseCaseResult(created.State, worktree);
    }

    public async Task<ThreadResumeUseCaseResult> ResumeAsync(
        ThreadResumeUseCaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var resumed = await harnesses.ResumeConversationAsync(
            request.ConnectionOptions,
            request.Command,
            cancellationToken).ConfigureAwait(false);
        var service = threadWorkspace.GetRequired(request.ThreadId);
        service.ReconcileHistory(resumed.Turns.Select(ToLegacySnapshot));
        return new ThreadResumeUseCaseResult(request.ThreadId, service.SnapshotConversation());
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
                request.HarnessId,
                request.ConnectionOptions,
                request.ReplacementStartCommand,
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
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default)
    {
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted?.IsTitlePlaceholder != true)
        {
            return false;
        }

        await harnesses.SetConversationNameAsync(
            connectionOptions,
            persisted.GetConversationAddress(),
            title,
            cancellationToken).ConfigureAwait(false);
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

        var forked = await harnesses.ForkConversationAsync(
            request.ConnectionOptions,
            request.ForkCommand,
            cancellationToken).ConfigureAwait(false);
        var forkThreadId = forked.Address.RemoteId ?? forked.Address.LocalId.ToString();
        if (rollbackCount > 0)
        {
            var rollback = await harnesses.RollbackConversationAsync(
                request.ConnectionOptions,
                new RollbackConversationCommand(forked.Address, rollbackCount),
                cancellationToken).ConfigureAwait(false);
            if (rollback.Address.LocalId != forked.Address.LocalId ||
                rollback.Address.HarnessId != forked.Address.HarnessId)
            {
                throw new InvalidOperationException("The harness returned a different conversation while creating the fork.");
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
                $"fork-{forkThreadId}",
                forkThreadId,
                request.Source.WorktreeBranch ?? "HEAD"), cancellationToken).ConfigureAwait(false);
        }

        var state = CreateState(
            request.Source.ScopeKey,
            forkThreadId,
            $"Fork of {request.Source.DisplayTitle}",
            worktree?.Path ?? request.WorkspacePath,
            worktree?.Branch,
            request.Instructions,
            isTitlePlaceholder: false,
            forked.Address);
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
            request.IsTitlePlaceholder,
            request.Address);
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

    public async Task ArchiveAsync(
        AppSettings settings,
        string threadId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default)
    {
        var address = GetAddress(settings, threadId);
        await harnesses.SetConversationArchivedAsync(
            connectionOptions, address, archived: true, cancellationToken).ConfigureAwait(false);
        threadStore.SetArchived(settings, threadId, archived: true);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnarchiveAsync(
        AppSettings settings,
        string threadId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default)
    {
        var address = GetAddress(settings, threadId);
        await harnesses.SetConversationArchivedAsync(
            connectionOptions, address, archived: false, cancellationToken).ConfigureAwait(false);
        threadStore.SetArchived(settings, threadId, archived: false);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetPinnedAsync(AppSettings settings, string threadId, bool pinned, CancellationToken cancellationToken = default)
    {
        threadStore.SetPinned(settings, threadId, pinned);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return pinned;
    }

    public async Task RenameAsync(
        AppSettings settings,
        string threadId,
        string title,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default)
    {
        await harnesses.SetConversationNameAsync(
            connectionOptions, GetAddress(settings, threadId), title, cancellationToken).ConfigureAwait(false);
        threadStore.Rename(settings, threadId, title);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        AppSettings settings,
        string threadId,
        bool archiveFirst,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default)
    {
        var existing = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Chat '{threadId}' was not found.");
        var restore = SettingsStorageMapper.ToPresentation(existing);
        if (archiveFirst)
        {
            await harnesses.SetConversationArchivedAsync(
                connectionOptions,
                existing.GetConversationAddress(),
                archived: true,
                cancellationToken).ConfigureAwait(false);
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
        ThreadInstructionSnapshot instructions, bool isTitlePlaceholder, ConversationAddress? address = null)
        => new()
    {
        ScopeKind = scope.Kind,
        ProjectPath = scope.ProjectPath ?? string.Empty,
        ThreadId = threadId,
        ConversationId = address?.LocalId.Value ??
            AppSettingsHarnessMigration.CreateDeterministicConversationId(KnownHarnessIds.Codex, threadId),
        HarnessId = address?.HarnessId.Value ?? KnownHarnessIds.Codex,
        RemoteConversationId = address?.RemoteId ?? threadId,
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

    private static ConversationAddress GetAddress(AppSettings settings, string threadId) =>
        settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal))?.GetConversationAddress()
        ?? throw new InvalidOperationException($"Chat '{threadId}' was not found.");

    private static CodexConversationTurnSnapshot CloneConversationTurn(CodexConversationTurnSnapshot source) => new()
    {
        TurnId = source.TurnId,
        UserPrompt = source.UserPrompt,
        AssistantResponse = source.AssistantResponse,
        Status = source.Status,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        IsSuperseded = source.IsSuperseded,
        IsCodeReview = source.IsCodeReview,
        ReviewScope = source.ReviewScope,
        Activity = [.. source.Activity],
        UserAttachments = [.. source.UserAttachments.Select(attachment => attachment.Clone())],
        GeneratedImagePaths = [.. source.GeneratedImagePaths]
    };

    private static CodexConversationTurnSnapshot ToLegacySnapshot(ConversationTurnSnapshot source) => new()
    {
        TurnId = source.RemoteTurnId ?? string.Empty,
        UserPrompt = source.UserPrompt,
        AssistantResponse = source.AssistantResponse,
        Status = source.Status switch
        {
            ConversationTurnStatus.Idle => CodexTurnStatus.Idle,
            ConversationTurnStatus.Running => CodexTurnStatus.Running,
            ConversationTurnStatus.Completed => CodexTurnStatus.Completed,
            ConversationTurnStatus.Failed => CodexTurnStatus.Failed,
            ConversationTurnStatus.Cancelled => CodexTurnStatus.Cancelled,
            _ => CodexTurnStatus.Failed
        },
        StartedAt = source.StartedAt ?? DateTimeOffset.UtcNow,
        CompletedAt = source.CompletedAt,
        IsSuperseded = source.IsSuperseded,
        Activity = [.. source.Activity.Select(item => new CodexTimelineItem(
            item.Kind == ActivityKind.Error ? CodexTimelineItemKind.Error : CodexTimelineItemKind.Raw,
            item.Title,
            item.Detail,
            "harness/activity",
            item.Timestamp)
        {
            ItemId = item.Id,
            ActivityKey = item.Id
        })],
        UserAttachments = [.. source.UserAttachments.Select(attachment => attachment.Clone())],
        GeneratedImagePaths = [.. source.GeneratedImagePaths]
    };
}

public sealed record ThreadForkRequest(
    AppSettings Settings,
    ProjectThreadState Source,
    string WorkspacePath,
    HarnessConnectionOptions ConnectionOptions,
    ForkConversationCommand ForkCommand,
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
    bool IsTitlePlaceholder,
    ConversationAddress? Address = null);
public sealed record ThreadCreateResult(ProjectThreadState State, string? PreviousActiveThreadId);
public readonly record struct ThreadInstructionSnapshot(string? DeveloperInstructions, string? BaseInstructions);

public sealed record ThreadStartUseCaseRequest(
    AppSettings Settings,
    ThreadScopeKey Scope,
    string Title,
    string WorkspacePath,
    HarnessId HarnessId,
    HarnessConnectionOptions ConnectionOptions,
    StartConversationCommand StartCommand,
    ThreadInstructionSnapshot Instructions,
    bool IsTitlePlaceholder,
    bool CreateWorktree,
    string WorktreeTaskId);
public sealed record ThreadStartUseCaseResult(ProjectThreadState State, AssistantWorktree? Worktree);
public sealed record ThreadResumeUseCaseResult(
    string ThreadId,
    IReadOnlyList<CodexConversationTurnSnapshot> Turns);
public sealed record ThreadResumeUseCaseRequest(
    string ThreadId,
    HarnessConnectionOptions ConnectionOptions,
    ResumeConversationCommand Command);
public sealed record ThreadActivationUseCaseRequest(
    AppSettings Settings,
    ThreadScopeKey Scope,
    string WorkspacePath,
    HarnessId HarnessId,
    HarnessConnectionOptions ConnectionOptions,
    ThreadResumeUseCaseRequest ResumeRequest,
    StartConversationCommand ReplacementStartCommand,
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
