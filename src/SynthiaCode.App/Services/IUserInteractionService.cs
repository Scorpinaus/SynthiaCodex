using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Projects;

namespace SynthiaCode.App.Services;

public sealed record ProjectFolderEditSelection(
    string PrimaryPath,
    IReadOnlyList<string> FolderPaths);

public enum ProjectTrustDecision
{
    TrustProject,
    OpenUntrusted,
    Cancel
}

public interface IUserInteractionService
{
    bool ConfirmDestructiveAction(string title, string message);

    bool ConfirmAction(string title, string message) => ConfirmDestructiveAction(title, message);

    string? PromptForText(string title, string message, string initialValue);

    void OpenInEditor(string path);

    void OpenExternalUri(Uri uri);

    void ShowImagePreview(string path);

    GeneratedImageEditSelection? SelectGeneratedImageEdit(string path);

    CodexReviewTarget? SelectCodeReviewTarget(GitReviewCatalog catalog);

    string? SelectWorktreeStartPoint(GitBranchCatalog catalog) => catalog.DefaultStartPoint;

    ProjectTrustDecision PromptForProjectTrust(string projectPath);

    ProjectFolderEditSelection? EditProjectFolders(RecentProject project);

    void RevealInExplorer(string path);
}
