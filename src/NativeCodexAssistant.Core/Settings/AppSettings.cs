using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.Core.Settings;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";

    public string? PreferredCodexPath { get; set; }

    public string? LastModelOverride { get; set; }

    public string? LastReasoningEffortOverride { get; set; }

    public List<RecentProject> RecentProjects { get; set; } = [];

    public List<ProjectThreadState> ProjectThreads { get; set; } = [];
}

public sealed class ProjectThreadState
{
    public string ProjectPath { get; set; } = string.Empty;

    public string ThreadId { get; set; } = string.Empty;

    public string FinalResponse { get; set; } = string.Empty;

    public List<CodexTimelineItem> TimelineItems { get; set; } = [];

    public List<string> RawEvents { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
