using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.Views;

public partial class TaskView : UserControl
{
    private const double FollowLatestThreshold = 24;

    private ObservableCollection<CodexConversationTurn>? observedTurns;
    private TaskViewModel? taskViewModel;
    private ScrollViewer? conversationScroller;
    private bool followLatest = true;

    public TaskView()
    {
        InitializeComponent();
        ComposerDropTarget.AddHandler(DragOverEvent, new DragEventHandler(OnComposerDragOver), handledEventsToo: true);
        ComposerDropTarget.AddHandler(DropEvent, new DragEventHandler(OnComposerDrop), handledEventsToo: true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    public void FocusComposer(bool isTurnRunning)
    {
        var composer = isTurnRunning ? GuidanceBox : PromptBox;
        composer.Focus();
        composer.CaretIndex = composer.Text.Length;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AttachToViewModel();

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachFromViewModel();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachToViewModel();
        }
    }

    private void AttachToViewModel()
    {
        DetachFromViewModel();
        if (DataContext is not MainViewModel main)
        {
            return;
        }

        taskViewModel = main.TaskWorkspace;
        taskViewModel.PropertyChanged += OnTaskViewModelPropertyChanged;
        ObserveTurns(taskViewModel.ConversationTurns);
        FollowLatest();
    }

    private void DetachFromViewModel()
    {
        if (taskViewModel is not null)
        {
            taskViewModel.PropertyChanged -= OnTaskViewModelPropertyChanged;
            taskViewModel = null;
        }
        ObserveTurns(null);
    }

    private void OnTaskViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskViewModel.ConversationTurns) && taskViewModel is not null)
        {
            ObserveTurns(taskViewModel.ConversationTurns);
            FollowLatest();
        }
    }

    private void ObserveTurns(ObservableCollection<CodexConversationTurn>? turns)
    {
        if (observedTurns is not null)
        {
            observedTurns.CollectionChanged -= OnTurnsChanged;
            foreach (var turn in observedTurns)
            {
                turn.PropertyChanged -= OnTurnPropertyChanged;
            }
        }

        observedTurns = turns;
        if (observedTurns is null)
        {
            return;
        }

        observedTurns.CollectionChanged += OnTurnsChanged;
        foreach (var turn in observedTurns)
        {
            turn.PropertyChanged += OnTurnPropertyChanged;
        }
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CodexConversationTurn turn in e.OldItems)
            {
                turn.PropertyChanged -= OnTurnPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (CodexConversationTurn turn in e.NewItems)
            {
                turn.PropertyChanged += OnTurnPropertyChanged;
            }
        }
        FollowLatest();
    }

    private void OnTurnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CodexConversationTurn.AssistantResponse) or nameof(CodexConversationTurn.Status))
        {
            FollowLatest();
        }
    }

    private void FollowLatest()
    {
        if (!followLatest || observedTurns is null || observedTurns.Count == 0)
        {
            return;
        }

        var turns = observedTurns;
        var latest = turns[^1];
        Dispatcher.BeginInvoke(
            () =>
            {
                if (!ReferenceEquals(observedTurns, turns) ||
                    turns.Count == 0 ||
                    !ReferenceEquals(turns[^1], latest) ||
                    !followLatest)
                {
                    return;
                }

                conversationScroller ??= FindVisualDescendant<ScrollViewer>(ConversationList);
                if (conversationScroller is null)
                {
                    ConversationList.ScrollIntoView(latest);
                    return;
                }

                conversationScroller.ScrollToVerticalOffset(conversationScroller.ScrollableHeight);
            },
            DispatcherPriority.Background);
    }

    private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if ((e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) && followLatest)
        {
            FollowLatest();
            return;
        }

        followLatest = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - FollowLatestThreshold;
        JumpLatestButton.Visibility = followLatest ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnJumpLatestClick(object sender, RoutedEventArgs e)
    {
        followLatest = true;
        JumpLatestButton.Visibility = Visibility.Collapsed;
        FollowLatest();
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
            Title = "Attach files from the active workspace",
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
            Title = "Attach a folder from the active workspace",
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

    private async Task ImportAttachmentPathsAsync(IEnumerable<string> paths)
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }
        await main.AddAttachmentPathsAsync(paths).ConfigureAwait(true);
    }

    private void OnModelOptionsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (taskViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            taskViewModel.IsOptionsFlyoutOpen = false;
            ModelOptionsButton.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.BrowserBack &&
            taskViewModel.OptionsPage != ComposerOptionsPage.Main)
        {
            taskViewModel.OptionsPage = ComposerOptionsPage.Main;
            e.Handled = true;
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
