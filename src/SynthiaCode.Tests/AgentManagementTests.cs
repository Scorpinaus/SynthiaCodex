using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure.Codex;

[Trait("Category", TestCategories.Wpf)]
[Collection(TestCategories.WpfCollection)]
public sealed class AgentManagementTests
{


    [Fact(DisplayName = "collaboration events project active and done agents")]
    public Task CollaborationEventsProjectAgentGroupsAsync()
    {
        var viewModel = CreateViewModel(new AgentActionStub());
        viewModel.ApplyConversationSnapshot(CreateAgentSnapshot());

        Assert(viewModel.HasAgents, "agent panel becomes visible when collaboration agents exist");
        Assert(viewModel.ActiveAgents.Count == 1, "running collaboration agent is grouped under Active");
        Assert(viewModel.DoneAgents.Count == 1, "completed collaboration agent is grouped under Done");
        Assert(viewModel.ActiveAgents[0].ThreadId == "agent-active", "active agent keeps its receiver thread id");
        Assert(viewModel.ActiveAgents[0].Prompt == "Inspect the protocol.", "spawn prompt is retained for the agent row");
        Assert(viewModel.ActiveAgents[0].StatusLabel == "Running", "canonical running status has a friendly label");
        Assert(viewModel.DoneAgents[0].ThreadId == "agent-done", "done agent keeps its receiver thread id");
        Assert(viewModel.DoneAgents[0].StatusLabel == "Completed", "canonical completion status has a friendly label");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "thread read retains collaboration agents for restored panels")]
    public async Task ThreadReadRetainsCollaborationAgentsAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("agent_tests", "Agent Tests", "1.0"));
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
        await transport.WaitForClientMessageCountAsync(2);

        var read = client.ReadThreadAsync(new CodexThreadReadRequest("parent-thread"));
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend(
            """
            {"id":1,"result":{"thread":{"id":"parent-thread","turns":[{"id":"parent-turn","status":"completed","items":[{"id":"spawn-restored","type":"collabAgentToolCall","tool":"spawnAgent","status":"completed","senderThreadId":"parent-thread","receiverThreadIds":["agent-restored"],"prompt":"Restore me.","model":"gpt-5","agentsStates":{"agent-restored":{"status":"completed","message":"Done."}}}] }]}}}
            """);

        var activity = (await read).Turns.Single().Activity.Single();
        Assert(activity.Kind == CodexTimelineItemKind.Collaboration, "thread/read restores collaboration activity");
        Assert(activity.CollaborationReceiverThreadIds.Single() == "agent-restored", "restored activity keeps receiver identity");
        Assert(activity.CollaborationAgentStates.Single().Status == "completed", "restored activity keeps agent status");
        Assert(activity.CollaborationPrompt == "Restore me.", "restored activity keeps the spawn prompt");
    }

    [Fact(DisplayName = "agent transcript open steer and stop controls target the subagent")]
    public async Task AgentControlsTargetSubagentAsync()
    {
        var actions = new AgentActionStub
        {
            Read = threadId => Task.FromResult(new CodexThreadReadResult(
                threadId,
                [
                    new CodexConversationTurnSnapshot
                    {
                        TurnId = "agent-turn",
                        UserPrompt = "Inspect the protocol.",
                        AssistantResponse = "Reading the collaboration schema.",
                        Status = CodexTurnStatus.Running
                    }
                ]))
        };
        var viewModel = CreateViewModel(actions);
        viewModel.ApplyConversationSnapshot(CreateAgentSnapshot());
        var agent = viewModel.ActiveAgents.Single();

        await ((AsyncRelayCommand)viewModel.OpenAgentCommand).ExecuteAsync(agent);

        Assert(viewModel.SelectedAgent == agent, "open selects the requested agent");
        Assert(viewModel.IsAgentTranscriptOpen, "open reveals the subagent transcript");
        Assert(agent.Transcript.Count == 1, "thread/read turns populate the selected transcript");
        Assert(agent.Transcript[0].AssistantResponse == "Reading the collaboration schema.", "subagent response is preserved");
        Assert(agent.ActiveTurnId == "agent-turn", "running subagent turn is discovered from the transcript");

        agent.SteeringText = "Check the notification lifecycle too.";
        await ((AsyncRelayCommand)viewModel.SteerAgentCommand).ExecuteAsync(agent);
        Assert(actions.Steers.Single() == (
            "agent-active",
            "agent-turn",
            "Check the notification lifecycle too."), "steer targets the selected subagent's active turn");
        Assert(agent.SteeringText.Length == 0, "successful steering clears transient guidance");

        await ((AsyncRelayCommand)viewModel.StopAgentCommand).ExecuteAsync(agent);
        Assert(actions.Stops.Single() == ("agent-active", "agent-turn"), "stop interrupts the selected subagent turn");
        Assert(agent.StatusLabel == "Interrupted", "successful stop immediately moves the agent out of active state");
        Assert(viewModel.ActiveAgents.Count == 0 && viewModel.DoneAgents.Count == 2 && viewModel.DoneAgents.Contains(agent), "stopped agent moves from Active to Done");

        viewModel.CloseAgentTranscriptCommand.Execute(null);
        Assert(!viewModel.IsAgentTranscriptOpen && viewModel.SelectedAgent is null, "transcript closes without discarding agent history");
    }

    [Fact(DisplayName = "task view renders accessible agent groups controls and transcript")]
    public Task TaskViewRendersAgentManagementAsync() => WpfTestHost.RunAsync(() =>
    {
        Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        Application.Current.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        var viewModel = CreateViewModel(new AgentActionStub());
        viewModel.ApplyConversationSnapshot(CreateAgentSnapshot());
        var view = new TaskView { DataContext = new TaskContext(viewModel) };
        view.ApplyTemplate();
        view.Measure(new Size(980, 760));
        view.Arrange(new Rect(0, 0, 980, 760));
        view.UpdateLayout();

        var panel = WpfTestHost.FindNamedDescendant<FrameworkElement>(view, "AgentPanel")
            ?? throw new InvalidOperationException("agent panel was not found");
        var active = WpfTestHost.FindNamedDescendant<ItemsControl>(view, "ActiveAgentsList")
            ?? throw new InvalidOperationException("active agent list was not found");
        var done = WpfTestHost.FindNamedDescendant<ItemsControl>(view, "DoneAgentsList")
            ?? throw new InvalidOperationException("done agent list was not found");
        var transcript = WpfTestHost.FindNamedDescendant<FrameworkElement>(view, "AgentTranscriptPanel")
            ?? throw new InvalidOperationException("agent transcript panel was not found");

        Assert(panel.Visibility == Visibility.Visible, "agent panel is visible for delegated work");
        Assert(AutomationProperties.GetName(panel) == "Agent management", "agent panel has an accessible name");
        Assert(active.Items.Count == 1 && done.Items.Count == 1, "rendered panel exposes Active and Done groups");
        Assert(transcript.Visibility == Visibility.Collapsed, "transcript starts closed until an agent is opened");

        var buttons = FindDescendants<Button>(panel).ToList();
        Assert(buttons.Any(button => AutomationProperties.GetName(button) == "Open agent transcript"), "agent row exposes an accessible Open control");
        Assert(buttons.Any(button => AutomationProperties.GetName(button) == "Steer active agent"), "active agent exposes an accessible Steer control");
        Assert(buttons.Any(button => AutomationProperties.GetName(button) == "Stop active agent"), "active agent exposes an accessible Stop control");
    });

    private static ConversationWorkspaceSnapshot CreateAgentSnapshot()
    {
        var service = new CodexThreadService();
        service.Restore("parent-thread", null, null, null);
        service.BeginTurn("Delegate protocol inspection.");
        service.BindPendingTurn("parent-turn");
        service.ApplyNotification(Notification(
            "item/started",
            """
            {
              "threadId":"parent-thread",
              "turnId":"parent-turn",
              "item":{
                "id":"spawn-active",
                "type":"collabAgentToolCall",
                "tool":"spawnAgent",
                "status":"inProgress",
                "senderThreadId":"parent-thread",
                "receiverThreadIds":["agent-active"],
                "prompt":"Inspect the protocol.",
                "model":"gpt-5",
                "agentsStates":{"agent-active":{"status":"running","message":null}}
              }
            }
            """));
        service.ApplyNotification(Notification(
            "item/completed",
            """
            {
              "threadId":"parent-thread",
              "turnId":"parent-turn",
              "item":{
                "id":"spawn-done",
                "type":"collabAgentToolCall",
                "tool":"spawnAgent",
                "status":"completed",
                "senderThreadId":"parent-thread",
                "receiverThreadIds":["agent-done"],
                "prompt":"Review the tests.",
                "model":"gpt-5",
                "agentsStates":{"agent-done":{"status":"completed","message":"Tests reviewed."}}
              }
            }
            """));
        return WorkspaceActionStubs.Snapshot(service);
    }

    private static TaskViewModel CreateViewModel(AgentActionStub agentActions)
    {
        var actions = new TaskConversationActionStub();
        return new TaskViewModel(actions, actions, actions, actions, agentActions);
    }

    private static CodexAppServerNotification Notification(string method, string parameters) =>
        CodexAppServerNotification.Decode(new AppServerNotification(method, JsonNode.Parse(parameters)!.AsObject()));

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record TaskContext(TaskViewModel TaskWorkspace);

    private sealed class AgentActionStub : IAgentManagementActions
    {
        public Func<string, Task<CodexThreadReadResult>> Read { get; init; } = threadId =>
            Task.FromResult(new CodexThreadReadResult(threadId, []));

        public List<(string ThreadId, string TurnId, string Message)> Steers { get; } = [];

        public List<(string ThreadId, string TurnId)> Stops { get; } = [];

        public Task<CodexThreadReadResult> ReadAgentThreadAsync(string threadId)
            => Read(threadId);

        public Task SteerAgentAsync(string threadId, string turnId, string message)
        {
            Steers.Add((threadId, turnId, message));
            return Task.CompletedTask;
        }

        public Task StopAgentAsync(string threadId, string turnId)
        {
            Stops.Add((threadId, turnId));
            return Task.CompletedTask;
        }
    }
}
