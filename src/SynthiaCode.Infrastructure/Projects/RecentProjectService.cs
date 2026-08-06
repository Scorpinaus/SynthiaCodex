using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
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

        var existingIndex = settings.RecentProjects.FindIndex(project =>
            ProjectFolderSet.PathsEqual(project.Path, fullPath));
        if (existingIndex >= 0)
        {
            settings.RecentProjects[existingIndex] = settings.RecentProjects[existingIndex] with
            {
                Name = name,
                LastOpenedUtc = DateTimeOffset.UtcNow
            };
        }
        else
        {
            settings.RecentProjects.Insert(0, new RecentProject(fullPath, name, DateTimeOffset.UtcNow));
        }

        if (settings.RecentProjects.Count > MaxRecentProjects)
        {
            settings.RecentProjects.RemoveRange(
                MaxRecentProjects,
                settings.RecentProjects.Count - MaxRecentProjects);
        }

        return settings.RecentProjects;
    }

    public ProjectFolderUpdateResult UpdateProjectFolders(
        AppSettings settings,
        ProjectFolderUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);

        var currentPrimary = NormalizeRequiredPath(request.CurrentPrimaryPath, "The current primary folder is required.");
        var primary = NormalizeRequiredPath(request.PrimaryPath, "A primary folder is required.");
        var folders = NormalizeFolderSet(request.FolderPaths);
        if (!folders.Any(path => ProjectFolderSet.PathsEqual(path, primary)))
        {
            throw new InvalidOperationException("The primary folder must be attached to the project.");
        }

        var projectIndex = settings.RecentProjects.FindIndex(project =>
            ProjectFolderSet.PathsEqual(project.Path, currentPrimary));
        if (projectIndex < 0)
        {
            throw new InvalidOperationException("The project is no longer available.");
        }
        if (settings.RecentProjects.Where((_, index) => index != projectIndex).Any(project =>
                ProjectFolderSet.PathsEqual(project.Path, primary)))
        {
            throw new InvalidOperationException("That primary folder already belongs to another saved project.");
        }

        var orderedFolders = new[] { primary }
            .Concat(folders.Where(path => !ProjectFolderSet.PathsEqual(path, primary)))
            .ToArray();
        var name = new DirectoryInfo(primary).Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = primary;
        }
        var updated = new RecentProject(
            primary,
            name,
            DateTimeOffset.UtcNow,
            orderedFolders.Skip(1).ToArray());

        var affectedThreads = settings.ProjectThreads
            .Where(thread =>
                thread.ScopeKind == ThreadScopeKind.Project &&
                ProjectFolderSet.PathsEqual(thread.ProjectPath, currentPrimary))
            .ToArray();
        var previousWorkspaceByThread = affectedThreads
            .Where(thread => !string.IsNullOrWhiteSpace(thread.ThreadId))
            .GroupBy(thread => thread.ThreadId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => GetPreviousWorkspace(group.First(), currentPrimary),
                StringComparer.Ordinal);

        settings.RecentProjects[projectIndex] = updated;
        foreach (var thread in affectedThreads)
        {
            var previousWorkspace = GetPreviousWorkspace(thread, currentPrimary);
            StampThreadAttachmentRoots(thread, previousWorkspace);
            thread.ProjectPath = primary;
            if (!string.Equals(thread.Mode, "worktree", StringComparison.OrdinalIgnoreCase))
            {
                thread.WorkspacePath = primary;
            }

            var currentWorkspace = string.IsNullOrWhiteSpace(thread.WorkspacePath)
                ? primary
                : Path.GetFullPath(thread.WorkspacePath);
            var roots = MergeRoots(currentWorkspace, orderedFolders);
            foreach (var queued in thread.QueuedFollowUps)
            {
                StampAttachmentRoots(queued.Attachments, previousWorkspace);
                queued.Options ??= new QueuedTurnOptionsSnapshot();
                queued.Options.WorkspacePath = currentWorkspace;
                queued.Options.WorkspaceRoots = [.. roots];
            }
        }

        foreach (var draft in settings.ComposerAttachmentDrafts.Where(draft =>
                     draft.ScopeKind == ThreadScopeKind.Project &&
                     ProjectFolderSet.PathsEqual(draft.ProjectPath, currentPrimary)))
        {
            var previousWorkspace = !string.IsNullOrWhiteSpace(draft.ThreadId) &&
                previousWorkspaceByThread.TryGetValue(draft.ThreadId, out var threadWorkspace)
                    ? threadWorkspace
                    : currentPrimary;
            StampAttachmentRoots(draft.Attachments, previousWorkspace);
            draft.ProjectPath = primary;
        }

        return new ProjectFolderUpdateResult(updated, currentPrimary);
    }

    private static IReadOnlyList<string> NormalizeFolderSet(IReadOnlyList<string>? requestedPaths)
    {
        if (requestedPaths is null || requestedPaths.Count == 0)
        {
            throw new InvalidOperationException("A project must contain at least one folder.");
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var result = new List<string>(requestedPaths.Count);
        foreach (var requestedPath in requestedPaths)
        {
            var path = NormalizeRequiredPath(requestedPath, "Project folders cannot be empty.");
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"The project folder no longer exists: {path}");
            }
            if (!seen.Add(path))
            {
                throw new InvalidOperationException($"The project contains the same folder more than once: {path}");
            }
            result.Add(path);
        }
        return result;
    }

    private static string NormalizeRequiredPath(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static IReadOnlyList<string> MergeRoots(string workspacePath, IReadOnlyList<string> projectFolders) =>
        ProjectFolderSet.NormalizePersisted(
            workspacePath,
            projectFolders.Where(path => !ProjectFolderSet.PathsEqual(path, workspacePath)));

    private static string GetPreviousWorkspace(PersistedProjectThread thread, string fallback) =>
        string.IsNullOrWhiteSpace(thread.WorkspacePath)
            ? fallback
            : Path.GetFullPath(thread.WorkspacePath);

    private static void StampThreadAttachmentRoots(PersistedProjectThread thread, string workspaceRoot)
    {
        foreach (var turn in thread.ConversationTurns)
        {
            StampAttachmentRoots(turn.UserAttachments, workspaceRoot);
        }
        foreach (var queued in thread.QueuedFollowUps)
        {
            StampAttachmentRoots(queued.Attachments, workspaceRoot);
        }
    }

    private static void StampAttachmentRoots(IEnumerable<AttachmentReference> attachments, string workspaceRoot)
    {
        foreach (var attachment in attachments.Where(attachment =>
                     attachment.SourceKind == AttachmentSourceKind.WorkspaceReference &&
                     string.IsNullOrWhiteSpace(attachment.WorkspaceRootPath)))
        {
            attachment.WorkspaceRootPath = workspaceRoot;
        }
    }
}
