using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.App.Views;

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
