using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Projects;

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

    public bool ConfirmAction(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
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
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
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

    public ProjectTrustDecision PromptForProjectTrust(string projectPath)
    {
        var decision = ProjectTrustDecision.Cancel;
        var trust = new Button
        {
            Content = "Trust project",
            IsDefault = true,
            MinWidth = 112,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var openUntrusted = new Button
        {
            Content = "Open untrusted",
            MinWidth = 120,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80
        };
        AutomationProperties.SetName(trust, "Trust project");
        AutomationProperties.SetName(openUntrusted, "Open untrusted");

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        actions.Children.Add(trust);
        actions.Children.Add(openUntrusted);
        actions.Children.Add(cancel);

        var content = new StackPanel
        {
            Margin = new Thickness(22),
            MaxWidth = 560
        };
        content.Children.Add(new TextBlock
        {
            Text = "Project-scoped Codex configuration, rules, hooks, and credentials can affect how Codex runs. Only trust projects whose contents you know.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = projectPath,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Open untrusted skips project-local Codex configuration, hooks, and rules.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        });
        content.Children.Add(actions);

        var dialog = new Window
        {
            Title = "Trust this project?",
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "PanelBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "InkBrush");
        trust.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        openUntrusted.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        trust.Click += (_, _) =>
        {
            decision = ProjectTrustDecision.TrustProject;
            dialog.DialogResult = true;
        };
        openUntrusted.Click += (_, _) =>
        {
            decision = ProjectTrustDecision.OpenUntrusted;
            dialog.DialogResult = true;
        };

        dialog.ShowDialog();
        return decision;
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

    public void ShowImagePreview(string path)
    {
        var viewer = new GeneratedImagePreviewWindow(path);
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            viewer.Owner = owner;
        }
        else
        {
            viewer.ShowInTaskbar = true;
            viewer.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        viewer.ShowDialog();
    }

    public GeneratedImageEditSelection? SelectGeneratedImageEdit(string path)
    {
        var editor = new GeneratedImageEditWindow(path);
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            editor.Owner = owner;
        }
        else
        {
            editor.ShowInTaskbar = true;
            editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return editor.ShowDialog() == true ? editor.Selection : null;
    }

    public CodexReviewTarget? SelectCodeReviewTarget(GitReviewCatalog catalog)
    {
        var picker = new CodeReviewWindow(catalog);
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            picker.Owner = owner;
        }
        else
        {
            picker.ShowInTaskbar = true;
            picker.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return picker.ShowDialog() == true ? picker.Selection : null;
    }

    public string? SelectWorktreeStartPoint(GitBranchCatalog catalog)
    {
        var startPoints = catalog.Branches.ToList();
        if (!startPoints.Contains(catalog.DefaultStartPoint, StringComparer.Ordinal))
        {
            startPoints.Insert(0, catalog.DefaultStartPoint);
        }

        var selector = new ComboBox
        {
            ItemsSource = startPoints,
            SelectedItem = catalog.DefaultStartPoint,
            MinWidth = 380,
            MaxDropDownHeight = 320,
            Padding = new Thickness(8, 5, 8, 5),
            IsTextSearchEnabled = true
        };
        AutomationProperties.SetName(selector, "Starting branch");

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var accept = new Button
        {
            Content = "Create worktree chat",
            IsDefault = true,
            MinWidth = 144
        };
        AutomationProperties.SetName(accept, "Create worktree chat");

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        actions.Children.Add(cancel);
        actions.Children.Add(accept);

        var content = new StackPanel
        {
            Margin = new Thickness(22),
            MaxWidth = 560
        };
        content.Children.Add(new TextBlock
        {
            Text = "Choose the Git branch to use as the starting point for the new worktree.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(selector);
        content.Children.Add(new TextBlock
        {
            Text = catalog.RepositoryRoot,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });
        content.Children.Add(actions);

        var dialog = new Window
        {
            Title = "New chat in worktree",
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
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => selector.Focus();
        return dialog.ShowDialog() == true ? selector.SelectedItem as string : null;
    }

    public ProjectFolderEditSelection? EditProjectFolders(RecentProject project)
    {
        var editor = new ProjectFoldersWindow(project, new WpfFolderPicker());
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            editor.Owner = owner;
        }
        else
        {
            editor.ShowInTaskbar = true;
            editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return editor.ShowDialog() == true ? editor.Selection : null;
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
