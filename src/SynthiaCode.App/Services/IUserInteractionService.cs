namespace SynthiaCode.App.Services;

public interface IUserInteractionService
{
    bool ConfirmDestructiveAction(string title, string message);

    string? PromptForText(string title, string message, string initialValue);

    void OpenInEditor(string path);

    void OpenExternalUri(Uri uri);

    void RevealInExplorer(string path);
}
