using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Core.Settings;

public sealed class ThreadStore
{
    public IReadOnlyList<ProjectThreadState> GetProjectThreads(
        AppSettings settings,
        string projectPath,
        bool includeArchived = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedProject = NormalizePath(projectPath);
        return settings.ProjectThreads
            .Where(thread => PathsEqual(thread.ProjectPath, normalizedProject))
            .Where(thread => includeArchived || !thread.IsArchived)
            .OrderByDescending(thread => thread.IsPinned)
            .ThenByDescending(thread => thread.UpdatedAt)
            .Select(ToPresentation)
            .ToList();
    }

    public ProjectThreadState? GetActive(AppSettings settings, string projectPath) =>
        GetProjectThreads(settings, projectPath).FirstOrDefault(thread => thread.IsActive)
        ?? GetProjectThreads(settings, projectPath, includeArchived: false).FirstOrDefault();

    public ProjectThreadState Upsert(AppSettings settings, ProjectThreadState state)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        state.ProjectPath = NormalizePath(state.ProjectPath);
        var existing = settings.ProjectThreads.FirstOrDefault(thread =>
            PathsEqual(thread.ProjectPath, state.ProjectPath) &&
            string.Equals(thread.ThreadId, state.ThreadId, StringComparison.Ordinal));
        if (existing is null)
        {
            settings.ProjectThreads.Add(ToPersisted(state));
            return state;
        }

        existing.Title = state.Title;
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
        existing.CreatedAt = state.CreatedAt;
        existing.UpdatedAt = state.UpdatedAt;
        return ToPresentation(existing);
    }

    public void SetActive(AppSettings settings, string projectPath, string threadId)
    {
        var normalizedProject = NormalizePath(projectPath);
        var threads = settings.ProjectThreads.Where(thread => PathsEqual(thread.ProjectPath, normalizedProject));
        foreach (var thread in threads)
        {
            thread.IsActive = string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal);
        }
    }

    public void SetArchived(AppSettings settings, string projectPath, string threadId, bool archived)
    {
        var normalizedProject = NormalizePath(projectPath);
        var thread = settings.ProjectThreads
            .Where(item => PathsEqual(item.ProjectPath, normalizedProject))
            .FirstOrDefault(item => string.Equals(item.ThreadId, threadId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Thread '{threadId}' was not found for this project.");
        thread.IsArchived = archived;
        thread.IsActive = thread.IsActive && !archived;
        thread.UpdatedAt = DateTimeOffset.UtcNow;
    }

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
        ProjectPath = source.ProjectPath,
        ThreadId = source.ThreadId,
        Title = source.Title,
        Preview = source.Preview,
        IsArchived = source.IsArchived,
        IsPinned = source.IsPinned,
        IsActive = source.IsActive,
        IsRunning = source.IsRunning,
        TurnStatus = source.TurnStatus,
        Mode = source.Mode,
        WorkspacePath = source.WorkspacePath,
        WorktreeBranch = source.WorktreeBranch,
        CreatedAt = source.CreatedAt,
        FinalResponse = source.FinalResponse,
        TimelineItems = [.. source.TimelineItems],
        RawEvents = [.. source.RawEvents],
        ConversationTurns = CloneTurns(source.ConversationTurns),
        QueuedFollowUps = CloneQueuedFollowUps(source.QueuedFollowUps),
        UpdatedAt = source.UpdatedAt
    };

    private static PersistedProjectThread ToPersisted(ProjectThreadState source) => new()
    {
        ProjectPath = source.ProjectPath,
        ThreadId = source.ThreadId,
        Title = source.Title,
        Preview = source.Preview,
        IsArchived = source.IsArchived,
        IsPinned = source.IsPinned,
        IsActive = source.IsActive,
        IsRunning = source.IsRunning,
        TurnStatus = source.TurnStatus,
        Mode = source.Mode,
        WorkspacePath = source.WorkspacePath,
        WorktreeBranch = source.WorktreeBranch,
        CreatedAt = source.CreatedAt,
        FinalResponse = source.FinalResponse,
        TimelineItems = [.. source.TimelineItems],
        RawEvents = [.. source.RawEvents],
        ConversationTurns = CloneTurns(source.ConversationTurns),
        QueuedFollowUps = CloneQueuedFollowUps(source.QueuedFollowUps),
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
