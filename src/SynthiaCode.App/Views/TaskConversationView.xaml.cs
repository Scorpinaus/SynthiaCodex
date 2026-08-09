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

public partial class TaskConversationView : UserControl
{
    private readonly ConversationScrollCoordinator scrollCoordinator = new();
    private ObservableCollection<CodexConversationTurn>? observedTurns;
    private TaskViewModel? taskViewModel;
    private ScrollViewer? conversationScroller;
    private DispatcherOperation? pendingFollowLatest;
    private string? observedThreadId;

    public TaskConversationView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
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
