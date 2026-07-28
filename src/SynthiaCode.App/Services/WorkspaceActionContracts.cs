using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using SynthiaCode.App.ViewModels;

namespace SynthiaCode.App.Services;

/// <summary>
/// Application actions requested by the task workspace.  Keeping this contract cohesive
/// prevents the presentation model from knowing which view model owns each workflow.
/// </summary>
public interface ITurnExecutionActions
{
    Task SubmitAsync();
    Task CancelAsync();
    Task SteerAsync();
    bool CanCancelTurn();
    bool CanSteerTurn();
}

public interface IFollowUpManagementActions
{
    void OpenExternalUri(Uri uri);
    Task SendAlternateFollowUpAsync();
    Task PersistFollowUpQueueAsync(IReadOnlyList<QueuedFollowUpSnapshot> snapshots);
    Task SendQueuedFollowUpAsync(string followUpId);
}

public interface IConversationHistoryActions
{
    Task<bool> EditPromptAsync(CodexConversationTurn turn, string editedPrompt);
    Task ForkConversationAsync(string turnId);
}

public interface IComposerSupportActions
{
    Task LoadModelsAsync();
    void ShowImagePreview(string path);
    Task EditGeneratedImageAsync(string path);
    Task<ComposerSkillLoadResult> LoadComposerSkillsAsync(CancellationToken cancellationToken);
}


/// <summary>
/// Application actions requested by the project and thread navigation workspace.
/// </summary>
public interface IProjectNavigationActions
{
    Task BrowseProjectAsync();
    Task OpenRecentProjectAsync(object? parameter);
    Task CreateThreadAsync();
    Task CreateGeneralThreadAsync();
    Task CreateProjectThreadAsync();
    bool CanCreateThread();
    bool CanCreateGeneralThread();
    void SelectedThreadChanged(ProjectThreadState? state);
}

public interface IThreadLifecycleActions
{
    Task ResumeThreadAsync();
    Task ForkThreadAsync();
    Task ArchiveThreadAsync();
    Task UnarchiveThreadAsync();
    Task RemoveWorktreeAsync();
    bool CanUseSelectedThread();
    bool CanArchiveSelectedThread();
    bool CanUnarchiveSelectedThread();
    bool CanRemoveSelectedWorktree();
    Task TogglePinThreadAsync();
    Task DeleteThreadAsync();
    bool CanTogglePinThread();
    bool CanDeleteThread();
    Task RenameThreadAsync();
    bool CanRenameThread();
}
