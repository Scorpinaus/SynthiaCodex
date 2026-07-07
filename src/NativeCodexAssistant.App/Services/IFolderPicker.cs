namespace NativeCodexAssistant.App.Services;

public interface IFolderPicker
{
    string? PickFolder(string? initialPath = null);
}
