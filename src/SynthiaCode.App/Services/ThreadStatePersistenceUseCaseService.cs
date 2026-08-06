using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.Services;

/// <summary>Persists the bounded transcript projection for a thread in one place.</summary>
public sealed class ThreadStatePersistenceUseCaseService
{
    private readonly ISettingsStore settingsStore;
    private readonly ThreadStore threadStore;
    private readonly CodexThreadWorkspace threadWorkspace;

    public ThreadStatePersistenceUseCaseService(
        ISettingsStore settingsStore,
        ThreadStore threadStore,
        CodexThreadWorkspace threadWorkspace)
    {
        this.settingsStore = settingsStore;
        this.threadStore = threadStore;
        this.threadWorkspace = threadWorkspace;
    }

    public Task<ThreadStateSaveResult?> SaveAsync(
        AppSettings settings,
        string threadId,
        CancellationToken cancellationToken = default) =>
        SaveAsync(settings, threadId, threadWorkspace.GetRequired(threadId), cancellationToken);

    public Task<ThreadStateSaveResult> SaveActiveAsync(
        AppSettings settings,
        ProjectThreadState? selectedThread,
        ThreadScopeKey scope,
        string threadId,
        string workspacePath,
        string title,
        CancellationToken cancellationToken = default) =>
        SaveActiveAsync(
            settings,
            selectedThread,
            scope,
            threadId,
            workspacePath,
            title,
            threadWorkspace.GetRequired(threadId),
            cancellationToken);

    public async Task<ThreadStateSaveResult?> SaveAsync(
        AppSettings settings,
        string threadId,
        CodexThreadService service,
        CancellationToken cancellationToken = default)
    {
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted is null)
        {
            return null;
        }

        CopyTranscript(persisted, service);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return new ThreadStateSaveResult(persisted, persisted.UpdatedAt);
    }

    public async Task<ThreadStateSaveResult> SaveActiveAsync(
        AppSettings settings,
        ProjectThreadState? selectedThread,
        ThreadScopeKey scope,
        string threadId,
        string workspacePath,
        string title,
        CodexThreadService service,
        CancellationToken cancellationToken = default)
    {
        var persisted = selectedThread ?? threadStore.GetActive(settings, scope);
        if (persisted is null)
        {
            persisted = new ProjectThreadState
            {
                ScopeKind = scope.Kind,
                ProjectPath = scope.ProjectPath ?? string.Empty,
                ThreadId = threadId,
                Title = title,
                IsTitlePlaceholder = true,
                Mode = scope.Kind == ThreadScopeKind.General ? "general" : "local",
                WorkspacePath = workspacePath
            };
        }

        persisted.ThreadId = threadId;
        CopyTranscript(persisted, service);
        threadStore.Upsert(settings, persisted);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        var stored = settings.ProjectThreads.First(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        return new ThreadStateSaveResult(stored, stored.UpdatedAt);
    }

    public Task SaveSelectionAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        settingsStore.SaveAsync(settings, cancellationToken);

    private static void CopyTranscript(PersistedProjectThread target, CodexThreadService service)
    {
        target.FinalResponse = service.FinalResponse;
        target.TimelineItems = [.. service.TimelineItems.TakeLast(100)];
        target.RawEvents = [.. service.RawEvents.TakeLast(100)];
        target.ConversationTurns = service.SnapshotConversation().Select(CloneTurn).ToList();
        target.ContextTokensUsed = service.ContextTokensUsed;
        target.ContextWindowTokens = service.ContextWindowTokens;
        target.ContextCompactionCount = service.ContextCompactionCount;
        target.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void CopyTranscript(ProjectThreadState target, CodexThreadService service)
    {
        target.FinalResponse = service.FinalResponse;
        target.TimelineItems = [.. service.TimelineItems.TakeLast(100)];
        target.RawEvents = [.. service.RawEvents.TakeLast(100)];
        target.ConversationTurns = service.SnapshotConversation().Select(CloneTurn).ToList();
        target.ContextTokensUsed = service.ContextTokensUsed;
        target.ContextWindowTokens = service.ContextWindowTokens;
        target.ContextCompactionCount = service.ContextCompactionCount;
        target.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static CodexConversationTurnSnapshot CloneTurn(CodexConversationTurnSnapshot source) => new()
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
}

public sealed record ThreadStateSaveResult(PersistedProjectThread State, DateTimeOffset UpdatedAt);
