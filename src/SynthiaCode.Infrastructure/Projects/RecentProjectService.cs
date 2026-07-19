using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Infrastructure.Projects;

public sealed class RecentProjectService : IRecentProjectService
{
    private const int MaxRecentProjects = 10;

    public IReadOnlyList<RecentProject> AddRecentProject(AppSettings settings, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return settings.RecentProjects;
        }

        var fullPath = Path.GetFullPath(projectPath);
        var name = new DirectoryInfo(fullPath).Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fullPath;
        }

        var updated = new RecentProject(fullPath, name, DateTimeOffset.UtcNow);
        var existingIndex = settings.RecentProjects.FindIndex(project =>
            string.Equals(project.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            settings.RecentProjects[existingIndex] = updated;
        }
        else
        {
            settings.RecentProjects.Insert(0, updated);
        }

        if (settings.RecentProjects.Count > MaxRecentProjects)
        {
            settings.RecentProjects.RemoveRange(
                MaxRecentProjects,
                settings.RecentProjects.Count - MaxRecentProjects);
        }

        return settings.RecentProjects;
    }
}
