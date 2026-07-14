namespace NativeCodexAssistant.Core.Settings;

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
            settings.ProjectThreads.Add(state);
            return state;
        }

        existing.Title = state.Title;
        existing.Preview = state.Preview;
        existing.IsArchived = state.IsArchived;
        existing.IsPinned = state.IsPinned;
        existing.IsRunning = state.IsRunning;
        existing.TurnStatus = state.TurnStatus;
        existing.Mode = state.Mode;
        existing.FinalResponse = state.FinalResponse;
        existing.TimelineItems = state.TimelineItems;
        existing.RawEvents = state.RawEvents;
        existing.CreatedAt = state.CreatedAt;
        existing.UpdatedAt = state.UpdatedAt;
        return existing;
    }

    public void SetActive(AppSettings settings, string projectPath, string threadId)
    {
        var threads = GetProjectThreads(settings, projectPath);
        foreach (var thread in threads)
        {
            thread.IsActive = string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal);
        }
    }

    public void SetArchived(AppSettings settings, string projectPath, string threadId, bool archived)
    {
        var thread = GetProjectThreads(settings, projectPath)
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
}
