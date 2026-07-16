using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.App.Views;

public partial class TaskView : UserControl
{
    private ObservableCollection<CodexConversationTurn>? observedTurns;
    private TaskViewModel? taskViewModel;
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

        Dispatcher.BeginInvoke(() => ConversationList.ScrollIntoView(observedTurns[^1]), DispatcherPriority.Background);
    }

    private void OnConversationScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 && followLatest)
        {
            FollowLatest();
            return;
        }

        followLatest = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 24;
        JumpLatestButton.Visibility = followLatest ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnJumpLatestClick(object sender, RoutedEventArgs e)
    {
        followLatest = true;
        JumpLatestButton.Visibility = Visibility.Collapsed;
        FollowLatest();
    }
}
