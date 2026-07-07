using System.IO;
using Microsoft.Win32;

namespace NativeCodexAssistant.App.Services;

public sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string? initialPath = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select project folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
