using NativeCodexAssistant.Core.Projects;
using NativeCodexAssistant.Core.Settings;

namespace NativeCodexAssistant.Infrastructure.Projects;

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

        settings.RecentProjects = settings.RecentProjects
            .Where(project => !string.Equals(project.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(new RecentProject(fullPath, name, DateTimeOffset.UtcNow))
            .Take(MaxRecentProjects)
            .ToList();

        return settings.RecentProjects;
    }
}
