using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Core.Settings;

public sealed class ThreadStore
{
    public IReadOnlyList<ProjectThreadState> GetProjectThreads(
        AppSettings settings,
        string projectPath,
        bool includeArchived = true) =>
        GetThreads(settings, ThreadScopeKey.ForProject(projectPath), includeArchived);

    public IReadOnlyList<ProjectThreadState> GetThreads(
        AppSettings settings,
        ThreadScopeKey scope,
        bool includeArchived = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ProjectThreads
            .Where(thread => ScopeMatches(thread.ScopeKind, thread.ProjectPath, scope))
            .Where(thread => includeArchived || !thread.IsArchived)
            .OrderByDescending(thread => thread.IsPinned)
            .ThenByDescending(thread => thread.UpdatedAt)
            .Select(ToPresentation)
            .ToList();
    }

    public ProjectThreadState? GetActive(AppSettings settings, string projectPath) =>
        GetActive(settings, ThreadScopeKey.ForProject(projectPath));

    public ProjectThreadState? GetActive(AppSettings settings, ThreadScopeKey scope) =>
        GetThreads(settings, scope).FirstOrDefault(thread => thread.IsActive)
        ?? GetThreads(settings, scope, includeArchived: false).FirstOrDefault();

    public ProjectThreadState Upsert(AppSettings settings, ProjectThreadState state)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        NormalizeAndValidate(state);
        var existing = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, state.ThreadId, StringComparison.Ordinal));
        if (existing is null)
        {
            settings.ProjectThreads.Add(ToPersisted(state));
            return state;
        }

        if (existing.ScopeKind != state.ScopeKind ||
            (state.ScopeKind == ThreadScopeKind.Project && !PathsEqual(existing.ProjectPath, state.ProjectPath)))
        {
            throw new InvalidOperationException($"Thread '{state.ThreadId}' cannot be moved to a different scope.");
        }

        existing.ScopeKind = state.ScopeKind;
        existing.ProjectPath = state.ProjectPath;
        existing.Title = state.Title;
        existing.IsTitlePlaceholder = state.IsTitlePlaceholder;
        existing.Preview = state.Preview;
        existing.IsArchived = state.IsArchived;
        existing.IsPinned = state.IsPinned;
        existing.IsRunning = state.IsRunning;
        existing.TurnStatus = state.TurnStatus;
        existing.Mode = state.Mode;
        existing.WorkspacePath = state.WorkspacePath;
        existing.WorktreeBranch = state.WorktreeBranch;
        existing.FinalResponse = state.FinalResponse;
        existing.TimelineItems = state.TimelineItems;
        existing.RawEvents = state.RawEvents;
        existing.ConversationTurns = CloneTurns(state.ConversationTurns);
        existing.QueuedFollowUps = CloneQueuedFollowUps(state.QueuedFollowUps);
        existing.ContextTokensUsed = state.ContextTokensUsed;
        existing.ContextWindowTokens = state.ContextWindowTokens;
        existing.ContextCompactionCount = state.ContextCompactionCount;
        existing.CreatedAt = state.CreatedAt;
        existing.UpdatedAt = state.UpdatedAt;
        return ToPresentation(existing);
    }

    public void SetActive(AppSettings settings, string projectPath, string threadId)
        => SetActive(settings, ThreadScopeKey.ForProject(projectPath), threadId);

    public void SetActive(AppSettings settings, ThreadScopeKey scope, string threadId)
    {
        var threads = settings.ProjectThreads.Where(thread => ScopeMatches(thread.ScopeKind, thread.ProjectPath, scope));
        foreach (var thread in threads)
        {
            thread.IsActive = string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal);
        }
    }

    public void SetArchived(AppSettings settings, string projectPath, string threadId, bool archived)
    {
        var thread = settings.ProjectThreads
            .Where(item => ScopeMatches(item.ScopeKind, item.ProjectPath, ThreadScopeKey.ForProject(projectPath)))
            .FirstOrDefault(item => string.Equals(item.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found for this project.");
        SetArchived(thread, archived);
    }

    public void SetArchived(AppSettings settings, string threadId, bool archived)
    {
        var thread = settings.ProjectThreads.FirstOrDefault(item =>
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found.");
        SetArchived(thread, archived);
    }

    public void SetPinned(AppSettings settings, string threadId, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var thread = settings.ProjectThreads.FirstOrDefault(item =>
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found.");
        thread.IsPinned = pinned;
        thread.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Rename(AppSettings settings, string threadId, string title)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            throw new ArgumentException("Thread title is required.", nameof(title));
        }

        var thread = settings.ProjectThreads.FirstOrDefault(item =>
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found.");
        thread.Title = normalizedTitle;
        thread.IsTitlePlaceholder = false;
        thread.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool Delete(AppSettings settings, string threadId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var thread = settings.ProjectThreads.FirstOrDefault(item =>
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal));
        return thread is not null && settings.ProjectThreads.Remove(thread);
    }

    private static void SetArchived(PersistedProjectThread thread, bool archived)
    {
        thread.IsArchived = archived;
        thread.IsActive = thread.IsActive && !archived;
        thread.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void NormalizeAndValidate(ProjectThreadState state)
    {
        if (state.ScopeKind == ThreadScopeKind.General)
        {
            state.ProjectPath = string.Empty;
            if (string.IsNullOrWhiteSpace(state.WorkspacePath) || !Path.IsPathFullyQualified(state.WorkspacePath))
            {
                throw new InvalidOperationException("A General thread requires an absolute workspace path.");
            }
            state.WorkspacePath = NormalizePath(state.WorkspacePath);
            return;
        }

        state.ProjectPath = NormalizePath(state.ProjectPath);
        if (!string.IsNullOrWhiteSpace(state.WorkspacePath))
        {
            state.WorkspacePath = NormalizePath(state.WorkspacePath);
        }
    }

    private static bool ScopeMatches(ThreadScopeKind kind, string projectPath, ThreadScopeKey scope) =>
        kind == scope.Kind &&
        (kind == ThreadScopeKind.General || PathsEqual(projectPath, scope.ProjectPath ?? string.Empty));

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectThreadState ToPresentation(PersistedProjectThread source) => new()
    {
        ScopeKind = source.ScopeKind,
        ProjectPath = source.ProjectPath,
        ThreadId = source.ThreadId,
        Title = source.Title,
        IsTitlePlaceholder = source.IsTitlePlaceholder,
        Preview = source.Preview,
        IsArchived = source.IsArchived,
        IsPinned = source.IsPinned,
        IsActive = source.IsActive,
        IsRunning = source.IsRunning,
        TurnStatus = source.TurnStatus,
        Mode = source.Mode,
        WorkspacePath = source.WorkspacePath,
        WorktreeBranch = source.WorktreeBranch,
        AppliedDeveloperInstructions = source.AppliedDeveloperInstructions,
        AppliedBaseInstructions = source.AppliedBaseInstructions,
        CreatedAt = source.CreatedAt,
        FinalResponse = source.FinalResponse,
        TimelineItems = [.. source.TimelineItems],
        RawEvents = [.. source.RawEvents],
        ConversationTurns = CloneTurns(source.ConversationTurns),
        QueuedFollowUps = CloneQueuedFollowUps(source.QueuedFollowUps),
        ContextTokensUsed = source.ContextTokensUsed,
        ContextWindowTokens = source.ContextWindowTokens,
        ContextCompactionCount = source.ContextCompactionCount,
        UpdatedAt = source.UpdatedAt
    };

    private static PersistedProjectThread ToPersisted(ProjectThreadState source) => new()
    {
        ScopeKind = source.ScopeKind,
        ProjectPath = source.ProjectPath,
        ThreadId = source.ThreadId,
        Title = source.Title,
        IsTitlePlaceholder = source.IsTitlePlaceholder,
        Preview = source.Preview,
        IsArchived = source.IsArchived,
        IsPinned = source.IsPinned,
        IsActive = source.IsActive,
        IsRunning = source.IsRunning,
        TurnStatus = source.TurnStatus,
        Mode = source.Mode,
        WorkspacePath = source.WorkspacePath,
        WorktreeBranch = source.WorktreeBranch,
        AppliedDeveloperInstructions = source.AppliedDeveloperInstructions,
        AppliedBaseInstructions = source.AppliedBaseInstructions,
        CreatedAt = source.CreatedAt,
        FinalResponse = source.FinalResponse,
        TimelineItems = [.. source.TimelineItems],
        RawEvents = [.. source.RawEvents],
        ConversationTurns = CloneTurns(source.ConversationTurns),
        QueuedFollowUps = CloneQueuedFollowUps(source.QueuedFollowUps),
        ContextTokensUsed = source.ContextTokensUsed,
        ContextWindowTokens = source.ContextWindowTokens,
        ContextCompactionCount = source.ContextCompactionCount,
        UpdatedAt = source.UpdatedAt
    };

    private static List<CodexConversationTurnSnapshot> CloneTurns(
        IEnumerable<CodexConversationTurnSnapshot> turns) =>
        turns.Select(turn => new CodexConversationTurnSnapshot
        {
            TurnId = turn.TurnId,
            UserPrompt = turn.UserPrompt,
            AssistantResponse = turn.AssistantResponse,
            Status = turn.Status,
            StartedAt = turn.StartedAt,
            CompletedAt = turn.CompletedAt,
            Activity = [.. turn.Activity],
            UserAttachments = [.. turn.UserAttachments.Select(attachment => attachment.Clone())]
        }).ToList();

    private static List<QueuedFollowUpSnapshot> CloneQueuedFollowUps(
        IEnumerable<QueuedFollowUpSnapshot> queuedFollowUps) =>
        queuedFollowUps.Select(item => item.Clone()).ToList();
}
