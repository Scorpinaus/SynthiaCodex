using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Attachments;

namespace SynthiaCode.App.Views;

public partial class TaskComposerView : UserControl
{
    private TaskViewModel? taskViewModel;

    public TaskComposerView()
    {
        InitializeComponent();
        ComposerDropTarget.AddHandler(DragOverEvent, new DragEventHandler(OnComposerDragOver), handledEventsToo: true);
        ComposerDropTarget.AddHandler(DropEvent, new DragEventHandler(OnComposerDrop), handledEventsToo: true);
        Loaded += (_, _) => AttachToViewModel();
        DataContextChanged += (_, _) => AttachToViewModel();
    }

    public void FocusComposer(bool isTurnRunning)
    {
        var composer = isTurnRunning ? GuidanceBox : PromptBox;
        composer.Focus();
        composer.CaretIndex = composer.Text.Length;
    }

    private void AttachToViewModel()
    {
        taskViewModel = (DataContext as MainViewModel)?.TaskWorkspace;
    }

    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private async void OnAttachImagesClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Attach images",
            Filter = "Supported images|*.png;*.jpg;*.jpeg;*.gif;*.webp|PNG images|*.png|JPEG images|*.jpg;*.jpeg|GIF images|*.gif|WebP images|*.webp|All files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            if (DataContext is MainViewModel main)
            {
                await main.AddImageFilesAsync(picker.FileNames).ConfigureAwait(true);
            }
        }
    }

    private async void OnAttachFilesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }
        var picker = new OpenFileDialog
        {
            Title = "Attach files",
            Filter = "All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(main.ActiveWorkspacePath) ? main.ActiveWorkspacePath : null
        };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            await main.AddWorkspaceFilesAsync(picker.FileNames).ConfigureAwait(true);
        }
    }

    private async void OnAttachFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }
        var picker = new OpenFolderDialog
        {
            Title = "Attach a folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(main.ActiveWorkspacePath) ? main.ActiveWorkspacePath : null
        };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            await main.AddWorkspaceFolderAsync(picker.FolderName).ConfigureAwait(true);
        }
    }

    private void OnOpenAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AttachmentReference attachment } ||
            DataContext is not MainViewModel main)
        {
            return;
        }
        try
        {
            main.OpenAttachment(attachment);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            main.ReportAttachmentError(ex.Message);
        }
    }

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await ImportAttachmentPathsAsync(paths).ConfigureAwait(true);
        }
        e.Handled = true;
    }

    private async void OnComposerPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || DataContext is not MainViewModel main)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                await main.AddAttachmentPathsAsync(Clipboard.GetFileDropList().Cast<string>()).ConfigureAwait(true);
                e.Handled = true;
                return;
            }

            if (!Clipboard.ContainsImage())
            {
                return;
            }

            var bitmap = Clipboard.GetImage();
            if (bitmap is null)
            {
                return;
            }
            await using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            stream.Position = 0;
            await main.AddPastedImageAsync(stream).ConfigureAwait(true);
            e.Handled = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            main.ReportAttachmentError(ex.Message);
            e.Handled = true;
        }
    }

    private async void OnComposerKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox composer ||
            taskViewModel is null ||
            taskViewModel.SkillSelector.IsOpen)
        {
            return;
        }

        var token = ComposerSkillToken.Find(composer.Text, composer.CaretIndex);
        if (token is null)
        {
            return;
        }

        await taskViewModel.SkillSelector.OpenAsync(token).ConfigureAwait(true);
    }

    private void OnSkillsPopupOpened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                SkillsSearchBox.Focus();
                SkillsSearchBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void OnSkillsPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (taskViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            taskViewModel.SkillSelector.IsOpen = false;
            FocusComposer(taskViewModel.IsTurnRunning);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            var item = ComposerSkillsList.SelectedItem
                ?? taskViewModel.SkillSelector.FilteredSkills.FirstOrDefault();
            if (item is not null &&
                taskViewModel.SkillSelector.SelectCommand.CanExecute(item))
            {
                taskViewModel.SkillSelector.SelectCommand.Execute(item);
                FocusComposer(taskViewModel.IsTurnRunning);
                e.Handled = true;
            }
            return;
        }

        if (e.Key is not (Key.Down or Key.Up) ||
            taskViewModel.SkillSelector.FilteredSkills.Count == 0)
        {
            return;
        }

        var current = ComposerSkillsList.SelectedIndex;
        ComposerSkillsList.SelectedIndex = e.Key == Key.Down
            ? Math.Min(current + 1, taskViewModel.SkillSelector.FilteredSkills.Count - 1)
            : Math.Max(current <= 0 ? 0 : current - 1, 0);
        ComposerSkillsList.ScrollIntoView(ComposerSkillsList.SelectedItem);
        e.Handled = true;
    }

    private async Task ImportAttachmentPathsAsync(IEnumerable<string> paths)
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }
        await main.AddAttachmentPathsAsync(paths).ConfigureAwait(true);
    }
}
