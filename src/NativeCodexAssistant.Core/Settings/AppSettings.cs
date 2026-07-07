using NativeCodexAssistant.Core.Projects;

namespace NativeCodexAssistant.Core.Settings;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";

    public string? PreferredCodexPath { get; set; }

    public List<RecentProject> RecentProjects { get; set; } = [];
}
