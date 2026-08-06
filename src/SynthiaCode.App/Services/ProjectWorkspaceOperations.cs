using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Worktrees;
using SynthiaCode.Core.Workspaces;

namespace SynthiaCode.App.Services;

/// <summary>
/// Project-level integrations that are independent from a conversation's runtime
/// state.  Keeping them here prevents the conversation controller becoming a
/// facade for Git, worktree and recent-project infrastructure.
/// </summary>
public sealed class ProjectWorkspaceOperations
{
    private readonly IGitService gitService;
    private readonly IWorktreeService worktreeService;
    private readonly IRecentProjectService recentProjectService;
    private readonly IGeneralWorkspaceService generalWorkspaceService;

    public ProjectWorkspaceOperations(
        IGitService gitService,
        IWorktreeService worktreeService,
        IRecentProjectService recentProjectService,
        IGeneralWorkspaceService generalWorkspaceService)
    {
        this.gitService = gitService;
        this.worktreeService = worktreeService;
        this.recentProjectService = recentProjectService;
        this.generalWorkspaceService = generalWorkspaceService;
    }

    public string EnsureGeneralWorkspace() => generalWorkspaceService.EnsureWorkspace();

    public Task<GitRepositoryState> GetRepositoryStateAsync(string projectPath, CancellationToken cancellationToken = default) =>
        gitService.GetRepositoryStateAsync(projectPath, cancellationToken);

    public Task<AssistantWorktree> CreateWorktreeAsync(WorktreeCreateRequest request, CancellationToken cancellationToken = default) =>
        worktreeService.CreateAsync(request, cancellationToken);

    public void AddRecentProject(AppSettings settings, string projectPath) =>
        recentProjectService.AddRecentProject(settings, projectPath);

    public ProjectFolderUpdateResult UpdateProjectFolders(
        AppSettings settings,
        ProjectFolderUpdateRequest request) =>
        recentProjectService.UpdateProjectFolders(settings, request);

    public GitViewModel CreateGitViewModel(
        IUserInteractionService userInteractionService,
        IAppLogger logger,
        Func<GitContext> contextProvider,
        Func<bool> isShuttingDown,
        Action<string> reportStatus) =>
        new(gitService, userInteractionService, logger, contextProvider, isShuttingDown, reportStatus);
}
