using SynthiaCode.Core.Projects;

namespace SynthiaCode.App.Services;

public sealed record ProjectFolderEditSelection(
    string PrimaryPath,
    IReadOnlyList<string> FolderPaths);

public interface IUserInteractionService
{
    bool ConfirmDestructiveAction(string title, string message);

    string? PromptForText(string title, string message, string initialValue);

    void OpenInEditor(string path);

    void OpenExternalUri(Uri uri);

    void ShowImagePreview(string path);

    GeneratedImageEditSelection? SelectGeneratedImageEdit(string path);

    ProjectFolderEditSelection? EditProjectFolders(RecentProject project);

    void RevealInExplorer(string path);
}
