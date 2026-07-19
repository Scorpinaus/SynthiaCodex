using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SynthiaCode.App.Services;

public sealed class WpfUserInteractionService : IUserInteractionService
{
    public bool ConfirmDestructiveAction(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void OpenInEditor(string path)
    {
        var target = File.Exists(path) || Directory.Exists(path)
            ? path
            : throw new InvalidOperationException("The selected path no longer exists.");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                UseShellExecute = true,
                ArgumentList = { target }
            });
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
    }

    public void OpenExternalUri(Uri uri)
    {
        if (!ExternalUriPolicy.IsSupported(uri))
        {
            throw new InvalidOperationException("Only HTTP and HTTPS links can be opened.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    public void RevealInExplorer(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };

        if (File.Exists(path))
        {
            startInfo.ArgumentList.Add($"/select,{path}");
        }
        else if (Directory.Exists(path))
        {
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            throw new InvalidOperationException("The selected path no longer exists.");
        }

        Process.Start(startInfo);
    }
}
