using SynthiaCode.Core.Settings;

namespace SynthiaCode.Core.Projects;

public interface IRecentProjectService
{
    IReadOnlyList<RecentProject> AddRecentProject(AppSettings settings, string projectPath);
}
