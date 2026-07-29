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
    private readonly ConversationScrollCoordinator scrollCoordinator = new();
    private ObservableCollection<CodexConversationTurn>? observedTurns;
    private TaskViewModel? taskViewModel;
    private ScrollViewer? conversationScroller;
    private DispatcherOperation? pendingFollowLatest;
    private string? observedThreadId;

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

    public void FocusFindInChat()
    {
        var workspace = taskViewModel ?? (DataContext as MainViewModel)?.TaskWorkspace;
        workspace?.OpenFindInChatCommand.Execute(null);
        Dispatcher.BeginInvoke(
            () =>
            {
                FindInChatBox.Focus();
                FindInChatBox.SelectAll();
            },
            DispatcherPriority.Input);
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
        observedThreadId = taskViewModel.ConversationThreadId;
        scrollCoordinator.ResetForConversation();
        ObserveTurns(taskViewModel.ConversationTurns);
        UpdateJumpLatestVisibility();
        FollowLatest();
    }

    private void DetachFromViewModel()
    {
        if (pendingFollowLatest?.Status == DispatcherOperationStatus.Pending)
        {
            pendingFollowLatest.Abort();
        }
        pendingFollowLatest = null;
        conversationScroller = null;
        observedThreadId = null;
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
        else if (e.PropertyName == nameof(TaskViewModel.ConversationThreadId) &&
                 taskViewModel is not null &&
                 !string.Equals(observedThreadId, taskViewModel.ConversationThreadId, StringComparison.Ordinal))
        {
            observedThreadId = taskViewModel.ConversationThreadId;
            scrollCoordinator.ResetForConversation();
            UpdateJumpLatestVisibility();
            FollowLatest();
        }
        else if (e.PropertyName == nameof(TaskViewModel.CurrentFindInChatTurn) &&
                 taskViewModel?.CurrentFindInChatTurn is { } turn)
        {
            scrollCoordinator.Pause();
            UpdateJumpLatestVisibility();
            ConversationList.ScrollIntoView(turn);
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
        if (!scrollCoordinator.IsFollowingLatest ||
            observedTurns is null ||
            observedTurns.Count == 0 ||
            pendingFollowLatest?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
        {
            return;
        }

        var turns = observedTurns;
        pendingFollowLatest = Dispatcher.BeginInvoke(
            () =>
            {
                try
                {
                    if (!ReferenceEquals(observedTurns, turns) ||
                        turns.Count == 0 ||
                        !scrollCoordinator.IsFollowingLatest)
                    {
                        return;
                    }

                    conversationScroller ??= FindVisualDescendant<ScrollViewer>(ConversationList);
                    if (conversationScroller is null)
                    {
                        ConversationList.ScrollIntoView(turns[^1]);
                        return;
                    }

                    conversationScroller.ScrollToVerticalOffset(conversationScroller.ScrollableHeight);
                }
                finally
                {
                    pendingFollowLatest = null;
                }
            },
            DispatcherPriority.Background);
    }

    private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var shouldFollow = scrollCoordinator.UpdateFromScroll(
            e.VerticalOffset,
            e.ExtentHeight,
            e.ViewportHeight,
            e.VerticalChange,
            e.ExtentHeightChange,
            e.ViewportHeightChange);
        UpdateJumpLatestVisibility();
        if (shouldFollow)
        {
            FollowLatest();
        }
    }

    private void OnConversationPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta <= 0)
        {
            return;
        }

        conversationScroller ??= FindVisualDescendant<ScrollViewer>(ConversationList);
        if (conversationScroller is not { VerticalOffset: > 0 })
        {
            return;
        }

        scrollCoordinator.Pause();
        UpdateJumpLatestVisibility();
    }

    private void OnJumpLatestClick(object sender, RoutedEventArgs e)
    {
        scrollCoordinator.FollowLatest();
        UpdateJumpLatestVisibility();
        FollowLatest();
    }

    private void UpdateJumpLatestVisibility() =>
        JumpLatestButton.Visibility = scrollCoordinator.IsFollowingLatest
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OnCopyMessageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string message })
        {
            CopyMessageToClipboard(message);
        }

        e.Handled = true;
    }

    private void OnUserMessageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CodexConversationTurn turn } &&
            !turn.IsPromptEditing)
        {
            CopyMessageToClipboard(turn.UserPrompt);
        }

        e.Handled = true;
    }

    private static void CopyMessageToClipboard(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Clipboard.SetText(message);
        }
    }

    private void OnFindInChatPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (taskViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            taskViewModel.CloseFindInChatCommand.Execute(null);
            ConversationList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var command = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? taskViewModel.FindPreviousCommand
                : taskViewModel.FindNextCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
            e.Handled = true;
        }
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
