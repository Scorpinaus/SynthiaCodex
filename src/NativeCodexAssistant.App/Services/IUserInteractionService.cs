namespace NativeCodexAssistant.App.Services;

public interface IUserInteractionService
{
    bool ConfirmDestructiveAction(string title, string message);

    void OpenInEditor(string path);

    void OpenExternalUri(Uri uri);

    void RevealInExplorer(string path);
}
