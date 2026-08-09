using System.Collections.ObjectModel;
using System.Windows.Input;
using SynthiaCode.App.Services;
using SynthiaCode.Application.Conversations;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.App.ViewModels;

public sealed class TaskAgentsViewModel : ObservableObject
{
    private readonly IAgentManagementActions actions;
    private readonly Dictionary<string, AgentThreadViewModel> agentsByThread = new(StringComparer.Ordinal);
    private readonly AsyncRelayCommand openAgentCommand;
    private readonly AsyncRelayCommand steerAgentCommand;
    private readonly AsyncRelayCommand stopAgentCommand;
    private readonly RelayCommand closeAgentTranscriptCommand;
    private string? conversationThreadId;
    private AgentThreadViewModel? selectedAgent;
    private bool isAgentTranscriptOpen;
    private bool isRefreshingAgents;

    public TaskAgentsViewModel(IAgentManagementActions actions)
    {
        this.actions = actions;
        OpenAgentCommand = openAgentCommand = new AsyncRelayCommand(OpenAgentAsync, CanOpenAgent);
        SteerAgentCommand = steerAgentCommand = new AsyncRelayCommand(SteerAgentAsync, CanSteerAgent);
        StopAgentCommand = stopAgentCommand = new AsyncRelayCommand(StopAgentAsync, CanStopAgent);
        CloseAgentTranscriptCommand = closeAgentTranscriptCommand = new RelayCommand(CloseAgentTranscript);
    }

    public ObservableCollection<AgentThreadViewModel> ActiveAgents { get; } = [];

    public ObservableCollection<AgentThreadViewModel> DoneAgents { get; } = [];

    public bool HasAgents => ActiveAgents.Count > 0 || DoneAgents.Count > 0;

    public bool HasActiveAgents => ActiveAgents.Count > 0;

    public bool HasDoneAgents => DoneAgents.Count > 0;

    public AgentThreadViewModel? SelectedAgent
    {
        get => selectedAgent;
        private set => SetProperty(ref selectedAgent, value);
    }

    public bool IsAgentTranscriptOpen
    {
        get => isAgentTranscriptOpen;
        private set => SetProperty(ref isAgentTranscriptOpen, value);
    }

    public ICommand OpenAgentCommand { get; }

    public ICommand SteerAgentCommand { get; }

    public ICommand StopAgentCommand { get; }

    public ICommand CloseAgentTranscriptCommand { get; }

    public void ApplySnapshot(ConversationWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var conversationChanged = !string.Equals(conversationThreadId, snapshot.ThreadId, StringComparison.Ordinal);
        conversationThreadId = snapshot.ThreadId;
        isRefreshingAgents = true;
        try
        {
            if (conversationChanged)
            {
                agentsByThread.Clear();
                CloseAgentTranscript();
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var activity in snapshot.ConversationTurns
                         .SelectMany(turn => turn.Activity)
                         .Where(item => item.Kind == CodexTimelineItemKind.Collaboration))
            {
                foreach (var threadId in activity.CollaborationReceiverThreadIds)
                {
                    var agent = GetOrCreateAgent(threadId);
                    seen.Add(threadId);
                    if (string.Equals(activity.CollaborationTool, "spawnAgent", StringComparison.Ordinal))
                    {
                        if (!string.IsNullOrWhiteSpace(activity.CollaborationPrompt))
                        {
                            agent.Prompt = activity.CollaborationPrompt;
                        }

                        if (!string.IsNullOrWhiteSpace(activity.CollaborationModel))
                        {
                            agent.Model = activity.CollaborationModel;
                        }
                    }
                }

                foreach (var state in activity.CollaborationAgentStates)
                {
                    var agent = GetOrCreateAgent(state.ThreadId);
                    seen.Add(state.ThreadId);
                    agent.SetStatus(state.Status, state.Message);
                }
            }

            foreach (var threadId in agentsByThread.Keys.Where(threadId => !seen.Contains(threadId)).ToArray())
            {
                if (ReferenceEquals(SelectedAgent, agentsByThread[threadId]))
                {
                    CloseAgentTranscript();
                }

                agentsByThread.Remove(threadId);
            }
        }
        finally
        {
            isRefreshingAgents = false;
        }

        RefreshGroups();
    }

    public void RaiseCommandStates()
    {
        openAgentCommand.RaiseCanExecuteChanged();
        steerAgentCommand.RaiseCanExecuteChanged();
        stopAgentCommand.RaiseCanExecuteChanged();
        closeAgentTranscriptCommand.RaiseCanExecuteChanged();
    }

    private AgentThreadViewModel GetOrCreateAgent(string threadId)
    {
        if (agentsByThread.TryGetValue(threadId, out var existing))
        {
            return existing;
        }

        var created = new AgentThreadViewModel(threadId);
        created.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentThreadViewModel.IsActive) && !isRefreshingAgents)
            {
                RefreshGroups();
            }

            RaiseCommandStates();
        };
        agentsByThread.Add(threadId, created);
        return created;
    }

    private void RefreshGroups()
    {
        ActiveAgents.Clear();
        DoneAgents.Clear();
        foreach (var agent in agentsByThread.Values)
        {
            (agent.IsActive ? ActiveAgents : DoneAgents).Add(agent);
        }

        OnPropertyChanged(nameof(ActiveAgents));
        OnPropertyChanged(nameof(DoneAgents));
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(HasActiveAgents));
        OnPropertyChanged(nameof(HasDoneAgents));
        RaiseCommandStates();
    }

    private static bool CanOpenAgent(object? parameter) =>
        parameter is AgentThreadViewModel agent && agent.CanOpen;

    private async Task OpenAgentAsync(object? parameter)
    {
        if (parameter is not AgentThreadViewModel agent)
        {
            return;
        }

        SelectedAgent = agent;
        IsAgentTranscriptOpen = true;
        await LoadAgentTranscriptAsync(agent).ConfigureAwait(true);
    }

    private static bool CanSteerAgent(object? parameter) =>
        parameter is AgentThreadViewModel agent && agent.CanSteer;

    private async Task SteerAgentAsync(object? parameter)
    {
        if (parameter is not AgentThreadViewModel agent)
        {
            return;
        }

        var message = agent.SteeringText.Trim();
        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            await LoadAgentTranscriptAsync(agent).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            agent.ErrorMessage = "The agent no longer has a running turn to steer.";
            return;
        }

        agent.IsBusy = true;
        agent.ErrorMessage = string.Empty;
        try
        {
            await actions.SteerAgentAsync(agent.ThreadId, agent.ActiveTurnId, message).ConfigureAwait(true);
            agent.SteeringText = string.Empty;
        }
        catch (Exception exception)
        {
            agent.ErrorMessage = $"Could not steer agent: {exception.Message}";
        }
        finally
        {
            agent.IsBusy = false;
        }
    }

    private static bool CanStopAgent(object? parameter) =>
        parameter is AgentThreadViewModel agent && agent.CanStop;

    private async Task StopAgentAsync(object? parameter)
    {
        if (parameter is not AgentThreadViewModel agent)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            await LoadAgentTranscriptAsync(agent).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(agent.ActiveTurnId))
        {
            agent.ErrorMessage = "The agent no longer has a running turn to stop.";
            return;
        }

        agent.IsBusy = true;
        agent.ErrorMessage = string.Empty;
        try
        {
            await actions.StopAgentAsync(agent.ThreadId, agent.ActiveTurnId).ConfigureAwait(true);
            agent.SetStatus("interrupted", "Stopped by user.");
        }
        catch (Exception exception)
        {
            agent.ErrorMessage = $"Could not stop agent: {exception.Message}";
        }
        finally
        {
            agent.IsBusy = false;
        }
    }

    private async Task LoadAgentTranscriptAsync(AgentThreadViewModel agent)
    {
        agent.IsBusy = true;
        agent.ErrorMessage = string.Empty;
        try
        {
            var result = await actions.ReadAgentThreadAsync(agent.ThreadId).ConfigureAwait(true);
            agent.ReplaceTranscript(result.Turns);
        }
        catch (Exception exception)
        {
            agent.ErrorMessage = $"Could not open agent transcript: {exception.Message}";
        }
        finally
        {
            agent.IsBusy = false;
        }
    }

    private void CloseAgentTranscript()
    {
        IsAgentTranscriptOpen = false;
        SelectedAgent = null;
    }
}
