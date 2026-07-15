namespace NativeCodexAssistant.Core.Settings;

public static class AppSettingsSnapshot
{
    public static AppSettings Create(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AppSettings
        {
            Theme = source.Theme,
            PreferredCodexPath = source.PreferredCodexPath,
            LastModelOverride = source.LastModelOverride,
            LastReasoningEffortOverride = source.LastReasoningEffortOverride,
            IsProjectRailOpen = source.IsProjectRailOpen,
            IsDetailsPaneOpen = source.IsDetailsPaneOpen,
            RecentProjects = [.. source.RecentProjects],
            ProjectThreads = [.. source.ProjectThreads.Select(CloneThread)]
        };
    }

    private static PersistedProjectThread CloneThread(PersistedProjectThread source) => new()
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
        UpdatedAt = source.UpdatedAt
    };
}
