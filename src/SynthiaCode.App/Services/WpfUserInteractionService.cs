using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

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

    public string? PromptForText(string title, string message, string initialValue)
    {
        var input = new TextBox
        {
            Text = initialValue,
            MinWidth = 360,
            Padding = new Thickness(8, 6, 8, 6)
        };
        AutomationProperties.SetName(input, "Chat name");

        var validation = new TextBlock
        {
            Text = "Enter a name for the chat.",
            Foreground = System.Windows.Media.Brushes.IndianRed,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var accept = new Button
        {
            Content = "Rename",
            IsDefault = true,
            MinWidth = 88
        };
        AutomationProperties.SetName(accept, "Rename chat");

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        actions.Children.Add(cancel);
        actions.Children.Add(accept);

        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        content.Children.Add(input);
        content.Children.Add(validation);
        content.Children.Add(actions);

        var dialog = new Window
        {
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "PanelBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "InkBrush");
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        accept.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        accept.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                validation.Visibility = Visibility.Visible;
                input.Focus();
                return;
            }

            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

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
