using System.Globalization;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class TaskGoalsViewModel : ObservableObject
{
    private readonly IGoalManagementActions? actions;
    private readonly Func<string?> conversationThreadId;
    private readonly RelayCommand beginGoalEditCommand;
    private readonly RelayCommand cancelGoalEditCommand;
    private readonly AsyncRelayCommand saveGoalCommand;
    private readonly AsyncRelayCommand toggleGoalStatusCommand;
    private readonly AsyncRelayCommand clearGoalCommand;
    private CodexThreadGoal? goal;
    private string goalDraft = string.Empty;
    private string goalError = string.Empty;
    private bool isGoalFeatureAvailable;
    private bool isGoalSupported;
    private bool isGoalLoading;
    private bool isGoalEditing;
    private bool isGoalBusy;

    public TaskGoalsViewModel(
        IGoalManagementActions? actions,
        Func<string?> conversationThreadId)
    {
        this.actions = actions;
        this.conversationThreadId = conversationThreadId;
        BeginGoalEditCommand = beginGoalEditCommand = new RelayCommand(BeginGoalEdit, CanBeginGoalEdit);
        CancelGoalEditCommand = cancelGoalEditCommand = new RelayCommand(CancelGoalEdit, () => IsGoalEditing && !IsGoalBusy);
        SaveGoalCommand = saveGoalCommand = new AsyncRelayCommand(SaveGoalAsync, CanSaveGoal);
        ToggleGoalStatusCommand = toggleGoalStatusCommand = new AsyncRelayCommand(ToggleGoalStatusAsync, CanToggleGoalStatus);
        ClearGoalCommand = clearGoalCommand = new AsyncRelayCommand(ClearGoalAsync, CanClearGoal);
    }

    public ICommand BeginGoalEditCommand { get; }

    public ICommand CancelGoalEditCommand { get; }

    public ICommand SaveGoalCommand { get; }

    public ICommand ToggleGoalStatusCommand { get; }

    public ICommand ClearGoalCommand { get; }

    public CodexThreadGoal? Goal => goal;

    public bool IsGoalFeatureAvailable
    {
        get => isGoalFeatureAvailable;
        private set => SetProperty(ref isGoalFeatureAvailable, value);
    }

    public bool IsGoalSupported
    {
        get => isGoalSupported;
        private set
        {
            if (SetProperty(ref isGoalSupported, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsGoalLoading
    {
        get => isGoalLoading;
        private set
        {
            if (SetProperty(ref isGoalLoading, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsGoalEditing
    {
        get => isGoalEditing;
        private set
        {
            if (SetProperty(ref isGoalEditing, value))
            {
                OnPropertyChanged(nameof(GoalEditorValidationMessage));
                RaiseCommandStates();
            }
        }
    }

    public bool IsGoalBusy
    {
        get => isGoalBusy;
        private set
        {
            if (SetProperty(ref isGoalBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string GoalDraft
    {
        get => goalDraft;
        set
        {
            if (SetProperty(ref goalDraft, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(GoalEditorValidationMessage));
                OnPropertyChanged(nameof(GoalCharacterCount));
                saveGoalCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string GoalError
    {
        get => goalError;
        private set
        {
            if (SetProperty(ref goalError, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasGoalError));
            }
        }
    }

    public bool HasGoal => Goal is not null;

    public bool HasGoalError => !string.IsNullOrWhiteSpace(GoalError);

    public string GoalObjective => Goal?.Objective ?? string.Empty;

    public string GoalStatusLabel => Goal?.Status.ToDisplayName() ?? string.Empty;

    public string GoalStatusAutomationName => HasGoal ? $"Goal status: {GoalStatusLabel}" : "No goal set";

    public string GoalToggleActionLabel => Goal?.Status == CodexThreadGoalStatus.Active ? "Pause" : "Resume";

    public string GoalUsageSummary
    {
        get
        {
            if (Goal is null)
            {
                return string.Empty;
            }

            var tokens = Goal.TokenBudget is > 0
                ? $"{FormatCompactTokenCount(Goal.TokensUsed)}/{FormatCompactTokenCount(Goal.TokenBudget.Value)} tokens"
                : $"{FormatCompactTokenCount(Goal.TokensUsed)} tokens";
            return $"{tokens} | {FormatGoalDuration(Goal.TimeUsedSeconds)}";
        }
    }

    public string GoalCharacterCount => $"{GoalDraft.Length:N0}/4,000";

    public string GoalEditorValidationMessage => !IsGoalEditing
        ? string.Empty
        : string.IsNullOrWhiteSpace(GoalDraft)
            ? "Enter a goal objective."
            : GoalDraft.Length > 4_000
                ? "Goal objectives must be 4,000 characters or fewer."
                : string.Empty;

    public void ResetContext(bool isCodexThread)
    {
        goal = null;
        goalDraft = string.Empty;
        goalError = string.Empty;
        isGoalLoading = false;
        isGoalEditing = false;
        isGoalBusy = false;
        isGoalFeatureAvailable = isCodexThread;
        isGoalSupported = isCodexThread;
        RaisePropertiesChanged();
    }

    public void SetLoading()
    {
        if (!IsGoalFeatureAvailable)
        {
            return;
        }

        GoalError = string.Empty;
        IsGoalLoading = true;
    }

    public void Apply(CodexThreadGoal? value)
    {
        goal = value;
        isGoalSupported = true;
        if (!IsGoalEditing)
        {
            goalDraft = value?.Objective ?? string.Empty;
        }
        GoalError = string.Empty;
        IsGoalLoading = false;
        RaisePropertiesChanged();
    }

    public void SetLoadError(string message)
    {
        IsGoalLoading = false;
        GoalError = message;
        RaiseCommandStates();
    }

    public void SetUnsupported(string message)
    {
        IsGoalLoading = false;
        IsGoalSupported = false;
        GoalError = message;
    }

    public void RaiseCommandStates()
    {
        beginGoalEditCommand.RaiseCanExecuteChanged();
        cancelGoalEditCommand.RaiseCanExecuteChanged();
        saveGoalCommand.RaiseCanExecuteChanged();
        toggleGoalStatusCommand.RaiseCanExecuteChanged();
        clearGoalCommand.RaiseCanExecuteChanged();
    }

    private void BeginGoalEdit()
    {
        GoalDraft = Goal?.Objective ?? string.Empty;
        GoalError = string.Empty;
        IsGoalEditing = true;
    }

    private bool CanBeginGoalEdit() =>
        IsGoalFeatureAvailable &&
        IsGoalSupported &&
        actions?.CanManageGoal() == true &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        !IsGoalEditing;

    private void CancelGoalEdit()
    {
        GoalDraft = Goal?.Objective ?? string.Empty;
        GoalError = string.Empty;
        IsGoalEditing = false;
    }

    private bool CanSaveGoal() =>
        IsGoalEditing &&
        IsGoalSupported &&
        actions?.CanManageGoal() == true &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        string.IsNullOrEmpty(GoalEditorValidationMessage) &&
        !string.Equals(GoalDraft.Trim(), Goal?.Objective, StringComparison.Ordinal);

    private async Task SaveGoalAsync()
    {
        if (!CanSaveGoal() || actions is null)
        {
            return;
        }

        var objective = GoalDraft.Trim();
        var isNewGoal = Goal is null;
        var contextThreadId = conversationThreadId();
        IsGoalBusy = true;
        GoalError = string.Empty;
        try
        {
            var saved = await actions.SetGoalAsync(objective).ConfigureAwait(true);
            if (!MatchesContext(contextThreadId))
            {
                return;
            }
            Apply(saved);
            IsGoalEditing = false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (MatchesContext(contextThreadId))
            {
                GoalError = $"Could not save the goal: {exception.Message}";
            }
            return;
        }
        finally
        {
            if (MatchesContext(contextThreadId))
            {
                IsGoalBusy = false;
            }
        }

        if (!isNewGoal || string.IsNullOrWhiteSpace(contextThreadId) || !MatchesContext(contextThreadId))
        {
            return;
        }

        try
        {
            await actions.StartGoalWorkAsync(contextThreadId, objective).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GoalError = $"The goal was saved, but work could not start: {exception.Message}";
        }
    }

    private bool CanToggleGoalStatus() =>
        actions?.CanManageGoal() == true &&
        IsGoalSupported &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        Goal?.Status is CodexThreadGoalStatus.Active or CodexThreadGoalStatus.Paused;

    private async Task ToggleGoalStatusAsync()
    {
        if (!CanToggleGoalStatus() || actions is null || Goal is null)
        {
            return;
        }

        var status = Goal.Status == CodexThreadGoalStatus.Active
            ? CodexThreadGoalStatus.Paused
            : CodexThreadGoalStatus.Active;
        var contextThreadId = conversationThreadId();
        IsGoalBusy = true;
        GoalError = string.Empty;
        try
        {
            var updated = await actions.SetGoalStatusAsync(status).ConfigureAwait(true);
            if (!MatchesContext(contextThreadId))
            {
                return;
            }
            Apply(updated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (MatchesContext(contextThreadId))
            {
                GoalError = $"Could not {GoalToggleActionLabel.ToLowerInvariant()} the goal: {exception.Message}";
            }
        }
        finally
        {
            if (MatchesContext(contextThreadId))
            {
                IsGoalBusy = false;
            }
        }
    }

    private bool CanClearGoal() =>
        actions?.CanManageGoal() == true &&
        IsGoalSupported &&
        !IsGoalLoading &&
        !IsGoalBusy &&
        HasGoal;

    private async Task ClearGoalAsync()
    {
        if (!CanClearGoal() || actions is null)
        {
            return;
        }

        var contextThreadId = conversationThreadId();
        IsGoalBusy = true;
        GoalError = string.Empty;
        try
        {
            await actions.ClearGoalAsync().ConfigureAwait(true);
            if (!MatchesContext(contextThreadId))
            {
                return;
            }
            Apply(null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (MatchesContext(contextThreadId))
            {
                GoalError = $"Could not clear the goal: {exception.Message}";
            }
        }
        finally
        {
            if (MatchesContext(contextThreadId))
            {
                IsGoalBusy = false;
            }
        }
    }

    private bool MatchesContext(string? expected) =>
        string.Equals(expected, conversationThreadId(), StringComparison.Ordinal);

    private void RaisePropertiesChanged()
    {
        OnPropertyChanged(nameof(Goal));
        OnPropertyChanged(nameof(IsGoalFeatureAvailable));
        OnPropertyChanged(nameof(IsGoalSupported));
        OnPropertyChanged(nameof(IsGoalLoading));
        OnPropertyChanged(nameof(IsGoalEditing));
        OnPropertyChanged(nameof(IsGoalBusy));
        OnPropertyChanged(nameof(GoalDraft));
        OnPropertyChanged(nameof(GoalCharacterCount));
        OnPropertyChanged(nameof(GoalEditorValidationMessage));
        OnPropertyChanged(nameof(GoalError));
        OnPropertyChanged(nameof(HasGoalError));
        OnPropertyChanged(nameof(HasGoal));
        OnPropertyChanged(nameof(GoalObjective));
        OnPropertyChanged(nameof(GoalStatusLabel));
        OnPropertyChanged(nameof(GoalStatusAutomationName));
        OnPropertyChanged(nameof(GoalToggleActionLabel));
        OnPropertyChanged(nameof(GoalUsageSummary));
        RaiseCommandStates();
    }

    private static string FormatGoalDuration(long totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (duration.TotalHours >= 1)
        {
            return $"{(long)duration.TotalHours}h {duration.Minutes}m";
        }

        return duration.TotalMinutes >= 1
            ? $"{(long)duration.TotalMinutes}m"
            : $"{duration.Seconds}s";
    }

    private static string FormatCompactTokenCount(long value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}m";
        }

        return value >= 1_000
            ? $"{value / 1_000d:0.#}k"
            : value.ToString(CultureInfo.InvariantCulture);
    }
}
