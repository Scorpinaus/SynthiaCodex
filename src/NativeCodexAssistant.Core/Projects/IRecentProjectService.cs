using NativeCodexAssistant.Core.Settings;

namespace NativeCodexAssistant.Core.Projects;

public interface IRecentProjectService
{
    IReadOnlyList<RecentProject> AddRecentProject(AppSettings settings, string projectPath);
}
