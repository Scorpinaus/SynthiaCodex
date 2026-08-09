using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.Codex;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Workspaces;

[Trait("Category", TestCategories.Wpf)]
[Collection(TestCategories.WpfCollection)]
public sealed class GoalModeTests
{


    [Fact(DisplayName = "goal protocol sets gets clears and decodes notifications")]
    public async Task GoalProtocolRoundTripsAsync()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("goal_tests", "Goal tests", "1.0"));
        await InitializeAsync(client, transport);

        var setTask = client.SetThreadGoalAsync(new CodexThreadGoalSetRequest(
            "thread-1",
            "Finish the migration and keep tests green",
            CodexThreadGoalStatus.Active,
            TokenBudget: 40_000,
            IncludeTokenBudget: true));
        await transport.WaitForClientMessageCountAsync(3);
        var setRequest = Parse(transport.ClientMessages[2]);
        Assert(ReadString(setRequest, "method") == "thread/goal/set", "set uses thread/goal/set");
        Assert(ReadString(setRequest, "params.threadId") == "thread-1", "set sends the owning thread");
        Assert(ReadString(setRequest, "params.objective") == "Finish the migration and keep tests green", "set sends the objective");
        Assert(ReadString(setRequest, "params.status") == "active", "set sends the active status");
        Assert(ReadLong(setRequest, "params.tokenBudget") == 40_000, "set sends an explicit token budget");
        transport.ServerSend(
            """
            {"id":1,"result":{"goal":{"threadId":"thread-1","objective":"Finish the migration and keep tests green","status":"active","tokenBudget":40000,"tokensUsed":1200,"timeUsedSeconds":65,"createdAt":10,"updatedAt":20}}}
            """);
        var saved = await setTask;
        Assert(saved.ThreadId == "thread-1" && saved.Status == CodexThreadGoalStatus.Active, "set parses goal identity and status");
        Assert(saved.TokenBudget == 40_000 && saved.TokensUsed == 1_200 && saved.TimeUsedSeconds == 65, "set parses goal accounting");

        var getTask = client.GetThreadGoalAsync("thread-1");
        await transport.WaitForClientMessageCountAsync(4);
        var getRequest = Parse(transport.ClientMessages[3]);
        Assert(ReadString(getRequest, "method") == "thread/goal/get", "get uses thread/goal/get");
        transport.ServerSend("""{"id":2,"result":{"goal":null}}""");
        Assert(await getTask is null, "get accepts a missing goal");

        var clearTask = client.ClearThreadGoalAsync("thread-1");
        await transport.WaitForClientMessageCountAsync(5);
        var clearRequest = Parse(transport.ClientMessages[4]);
        Assert(ReadString(clearRequest, "method") == "thread/goal/clear", "clear uses thread/goal/clear");
        Assert(ReadString(clearRequest, "params.threadId") == "thread-1", "clear sends the owning thread");
        transport.ServerSend("""{"id":3,"result":{"cleared":true}}""");
        Assert(await clearTask, "clear parses its result");

        var updated = CodexAppServerNotification.Decode(new AppServerNotification(
            "thread/goal/updated",
            Parse("""{"threadId":"thread-1","goal":{"threadId":"thread-1"}}""")));
        var cleared = CodexAppServerNotification.Decode(new AppServerNotification(
            "thread/goal/cleared",
            Parse("""{"threadId":"thread-1"}""")));
        Assert(updated.Kind == CodexAppServerNotificationKind.ThreadGoalUpdated, "updated notification is classified");
        Assert(cleared.Kind == CodexAppServerNotificationKind.ThreadGoalCleared, "cleared notification is classified");
    }

    [Fact(DisplayName = "goal view model sets edits pauses resumes and clears")]
    public async Task GoalViewModelManagesLifecycleAsync()
    {
        var started = new List<(string ThreadId, string Objective)>();
        var setObjectives = new List<string>();
        var statuses = new List<CodexThreadGoalStatus>();
        var clearCount = 0;
        var actions = new TaskConversationActionStub
        {
            SetGoal = objective =>
            {
                setObjectives.Add(objective);
                return Task.FromResult(Goal("thread-1", objective, CodexThreadGoalStatus.Active));
            },
            SetGoalStatus = status =>
            {
                statuses.Add(status);
                return Task.FromResult(Goal("thread-1", "Ship Goal mode", status));
            },
            ClearGoal = () =>
            {
                clearCount++;
                return Task.FromResult(true);
            },
            StartGoal = (threadId, objective) =>
            {
                started.Add((threadId, objective));
                return Task.CompletedTask;
            }
        };
        await using var viewModel = WorkspaceActionStubs.CreateTaskViewModel(actions);
        viewModel.ApplyConversationSnapshot(Snapshot("thread-1"));
        viewModel.ResetGoalContext(isCodexThread: true);

        viewModel.BeginGoalEditCommand.Execute(null);
        viewModel.GoalDraft = "  Ship Goal mode  ";
        Assert(viewModel.SaveGoalCommand.CanExecute(null), "a valid new objective can be saved");
        viewModel.SaveGoalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.HasGoal && started.Count == 1, "new goal saved and started");
        Assert(setObjectives.SequenceEqual(["Ship Goal mode"]), "new objective is trimmed and persisted once");
        Assert(started.Single() == ("thread-1", "Ship Goal mode"), "new goal starts as work on the owning chat");
        Assert(viewModel.GoalUsageSummary.Contains("200/1k tokens", StringComparison.Ordinal), "usage shows budget progress");
        Assert(viewModel.GoalUsageSummary.EndsWith("1m", StringComparison.Ordinal), "usage shows elapsed time");

        viewModel.ToggleGoalStatusCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.Goal?.Status == CodexThreadGoalStatus.Paused, "goal paused");
        viewModel.ToggleGoalStatusCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.Goal?.Status == CodexThreadGoalStatus.Active, "goal resumed");
        Assert(statuses.SequenceEqual([CodexThreadGoalStatus.Paused, CodexThreadGoalStatus.Active]), "pause and resume use status-only updates");

        viewModel.BeginGoalEditCommand.Execute(null);
        viewModel.GoalDraft = "Ship Goal mode with tests";
        viewModel.SaveGoalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.Goal?.Objective == "Ship Goal mode with tests", "goal objective edited");
        Assert(started.Count == 1, "editing an existing goal does not fabricate another prompt");

        viewModel.ClearGoalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => !viewModel.HasGoal, "goal cleared");
        Assert(clearCount == 1, "clear is sent once");

        viewModel.BeginGoalEditCommand.Execute(null);
        viewModel.GoalDraft = new string('x', 4_001);
        Assert(!viewModel.SaveGoalCommand.CanExecute(null), "an oversized objective is rejected before protocol dispatch");
        Assert(viewModel.GoalEditorValidationMessage.Contains("4,000", StringComparison.Ordinal), "objective limit is explained");
    }

    [Fact(DisplayName = "goal view model rejects a stale result after chat switching")]
    public async Task GoalViewModelRejectsStaleResultAsync()
    {
        var firstPending = new TaskCompletionSource<CodexThreadGoal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPending = new TaskCompletionSource<CodexThreadGoal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var actions = new TaskConversationActionStub
        {
            SetGoal = _ => firstPending.Task,
            SetGoalStatus = _ => secondPending.Task,
            StartGoal = (_, _) =>
            {
                started++;
                return Task.CompletedTask;
            }
        };
        await using var viewModel = WorkspaceActionStubs.CreateTaskViewModel(actions);
        viewModel.ApplyConversationSnapshot(Snapshot("thread-1"));
        viewModel.ResetGoalContext(isCodexThread: true);
        viewModel.BeginGoalEditCommand.Execute(null);
        viewModel.GoalDraft = "Thread one goal";
        var firstSave = ((AsyncRelayCommand)viewModel.SaveGoalCommand).ExecuteAsync();
        await StateProbe.WaitForAsync(() => viewModel.IsGoalBusy, "first chat goal request started");

        viewModel.ApplyConversationSnapshot(Snapshot("thread-2"));
        viewModel.ResetGoalContext(isCodexThread: true);
        viewModel.ApplyGoal(Goal("thread-2", "Thread two goal", CodexThreadGoalStatus.Active));
        var secondStatusChange = ((AsyncRelayCommand)viewModel.ToggleGoalStatusCommand).ExecuteAsync();
        await StateProbe.WaitForAsync(() => viewModel.IsGoalBusy, "second chat status request started");

        firstPending.SetResult(Goal("thread-1", "Thread one goal", CodexThreadGoalStatus.Active));
        await firstSave;

        Assert(viewModel.ConversationThreadId == "thread-2", "second chat remains selected");
        Assert(viewModel.Goal?.ThreadId == "thread-2" && string.IsNullOrEmpty(viewModel.GoalError), "late first-chat result does not leak into the second chat");
        Assert(viewModel.IsGoalBusy, "late first-chat completion cannot clear the second chat busy state");
        Assert(started == 0, "late first-chat result cannot start work in the second chat");

        secondPending.SetResult(Goal("thread-2", "Thread two goal", CodexThreadGoalStatus.Paused));
        await secondStatusChange;
        Assert(viewModel.Goal?.Status == CodexThreadGoalStatus.Paused, "second chat status completed");
    }

    [Fact(DisplayName = "main workflow loads routes and starts a selected chat goal")]
    public async Task MainWorkflowOwnsSelectedGoalAsync()
    {
        using var temp = TempWorkspace.Create();
        var projectPath = temp.CreateDirectory("GoalRepo");
        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var installation = new CodexInstallation(
            true,
            @"C:\Tools\codex.exe",
            "codex test",
            "Codex test",
            "Test installation");
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("goal_workflow", "Goal workflow", "1.0"));
        await using var viewModel = WorkspaceActionStubs.CreateMainViewModel(
            new FakeSettingsStore(),
            new FakeCodexDiscoveryService(installation),
            coordinator,
            new FakeAuthService(new AuthenticationState(
                AuthReadiness.LikelySignedIn,
                "Likely signed in",
                "Test auth state.",
                @"C:\Users\Test\.codex")),
            new FakeGitService(projectPath),
            new FakeWorktreeService(projectPath, Path.Combine(projectPath, ".test-worktree")),
            new RecentProjectService(),
            new FakeFolderPicker(projectPath),
            new FakeUserInteractionService(),
            new FakeThemeService(),
            new FakeCodexCliUtilityRunner(),
            new ThreadStore(),
            new CodexThreadWorkspace(),
            new FakeTerminalService(),
            logger,
            new GeneralWorkspaceService(Path.Combine(projectPath, ".synthiacode-test-data")),
            enableGoalMode: true);

        await viewModel.InitializeAsync();
        await ((AsyncRelayCommand)viewModel.BrowseProjectCommand).ExecuteAsync();
        viewModel.NewThreadCommand.Execute(null);

        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"goal-tests","platformFamily":"windows","platformOs":"windows"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        Assert(ReadString(Parse(transport.ClientMessages[2]), "method") == "thread/start", "workflow creates a Codex thread");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-goal"}}}""");

        await transport.WaitForClientMessageCountAsync(4);
        var get = Parse(transport.ClientMessages[3]);
        Assert(ReadString(get, "method") == "thread/goal/get", "selecting a Codex chat loads its server-owned goal");
        Assert(ReadString(get, "params.threadId") == "thread-goal", "goal load is scoped to the selected chat");
        transport.ServerSend("""{"id":2,"result":{"goal":null}}""");
        await StateProbe.WaitForAsync(
            () => viewModel.TaskWorkspace.IsGoalFeatureAvailable && !viewModel.TaskWorkspace.IsGoalLoading,
            "selected goal load completed");

        viewModel.TaskWorkspace.BeginGoalEditCommand.Execute(null);
        viewModel.TaskWorkspace.GoalDraft = "Complete the parity slice";
        viewModel.TaskWorkspace.SaveGoalCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(5);
        var set = Parse(transport.ClientMessages[4]);
        Assert(ReadString(set, "method") == "thread/goal/set", "workflow persists the new goal");
        transport.ServerSend(
            """
            {"id":3,"result":{"goal":{"threadId":"thread-goal","objective":"Complete the parity slice","status":"active","tokensUsed":0,"timeUsedSeconds":0,"createdAt":10,"updatedAt":10}}}
            """);

        await transport.WaitForClientMessageCountAsync(6);
        var turn = Parse(transport.ClientMessages[5]);
        Assert(ReadString(turn, "method") == "turn/start", "a new goal starts normal Codex work");
        Assert(ReadString(turn, "params.threadId") == "thread-goal", "goal work starts in the owning chat");
        Assert(
            ReadString(turn, "params.input.0.text") == "Complete the parity slice",
            $"goal objective is the first prompt: {turn.ToJsonString()}");
        transport.ServerSend("""{"id":4,"result":{"turn":{"id":"turn-goal"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.TaskWorkspace.HasGoal, "new goal shown");

        transport.ServerSend(
            """
            {"method":"thread/goal/updated","params":{"threadId":"thread-goal","goal":{"threadId":"thread-goal","objective":"Complete the parity slice","status":"paused","tokenBudget":10000,"tokensUsed":900,"timeUsedSeconds":125,"createdAt":10,"updatedAt":20}}}
            """);
        await StateProbe.WaitForAsync(
            () => viewModel.TaskWorkspace.Goal?.Status == CodexThreadGoalStatus.Paused,
            "matching goal notification routed");
        Assert(viewModel.TaskWorkspace.Goal?.TokensUsed == 900, "notification refreshes goal accounting");

        transport.ServerSend("""{"method":"thread/goal/cleared","params":{"threadId":"thread-goal"}}""");
        await StateProbe.WaitForAsync(() => !viewModel.TaskWorkspace.HasGoal, "matching clear notification routed");
    }

    [Fact(DisplayName = "task view renders an accessible responsive goal row")]
    public Task TaskViewRendersGoalRowAsync() => WpfTestHost.RunAsync(() =>
    {
        var resources = Application.Current.Resources;
        resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        resources["Card"] = new Style(typeof(Border));
        resources["PaneSurface"] = new Style(typeof(Border));
        resources["StatePill"] = new Style(typeof(Border));
        resources["ConversationTurnCard"] = new Style(typeof(Border));
        resources["ConversationUserSurface"] = new Style(typeof(Border));
        resources["ConversationAssistantSurface"] = new Style(typeof(Border));
        resources["CompactButton"] = new Style(typeof(Button));
        resources["PrimaryButton"] = new Style(typeof(Button));
        resources["RunTaskButton"] = new Style(typeof(Button));
        resources["IconButton"] = new Style(typeof(Button));
        resources["SectionLabel"] = new Style(typeof(TextBlock));
        resources["ConversationBodyText"] = new Style(typeof(TextBlock));
        resources["ConversationRoleText"] = new Style(typeof(TextBlock));
        resources["ConversationMetadataText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityTitleText"] = new Style(typeof(TextBlock));
        resources["ConversationActivityDetailText"] = new Style(typeof(TextBlock));
        var view = new TaskView { Width = 620, Height = 520 };
        var row = WpfTestHost.FindNamedDescendant<Border>(view, "GoalProgressRow");
        var prompt = WpfTestHost.FindNamedDescendant<Grid>(view, "PromptInputPanel");
        var editor = WpfTestHost.FindNamedDescendant<TextBox>(view, "GoalObjectiveEditor");
        var objective = WpfTestHost.FindNamedDescendant<TextBlock>(view, "GoalObjectiveText");
        var set = WpfTestHost.FindNamedDescendant<Button>(view, "SetGoalButton");
        var toggle = WpfTestHost.FindNamedDescendant<Button>(view, "GoalToggleButton");
        var edit = WpfTestHost.FindNamedDescendant<Button>(view, "GoalEditButton");
        var clear = WpfTestHost.FindNamedDescendant<Button>(view, "GoalClearButton");
        var save = WpfTestHost.FindNamedDescendant<Button>(view, "SaveGoalButton");

        Assert(row is not null && prompt is not null, "goal row and prompt are rendered");
        Assert(Grid.GetRow(row!) < Grid.GetRow(prompt!), "goal progress appears above the prompt");
        Assert(AutomationProperties.GetName(row) == "Goal progress", "goal row has an accessible name");
        Assert(editor is { AcceptsReturn: true, MaxLength: 4_000 } && editor.TextWrapping == TextWrapping.Wrap, "goal editor is bounded and multiline");
        Assert(objective?.TextWrapping == TextWrapping.Wrap, "long goal objectives wrap");
        Assert(AutomationProperties.GetName(set) == "Set chat goal", "set action is accessible");
        Assert(AutomationProperties.GetName(toggle) == "Pause or resume goal", "pause or resume action is accessible");
        Assert(AutomationProperties.GetName(edit) == "Edit goal", "edit action is accessible");
        Assert(AutomationProperties.GetName(clear) == "Clear goal", "clear action is accessible");
        Assert(AutomationProperties.GetName(save) == "Save chat goal", "save action is accessible");
    });

    private static ConversationWorkspaceSnapshot Snapshot(string threadId) => new(
        threadId,
        null,
        CodexTurnStatus.Idle,
        string.Empty,
        false,
        0,
        0,
        0,
        [],
        [],
        [],
        []);

    private static CodexThreadGoal Goal(string threadId, string objective, CodexThreadGoalStatus status) =>
        new(threadId, objective, status, 200, 65, 10, 20, 1_000);

    private static async Task InitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"goal-tests","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
        await transport.WaitForClientMessageCountAsync(2);
    }

    private static JsonObject Parse(string value) => JsonNode.Parse(value)!.AsObject();

    private static string? ReadString(JsonObject value, string path) => ReadNode(value, path)?.GetValue<string>();

    private static long? ReadLong(JsonObject value, string path) => ReadNode(value, path)?.GetValue<long>();

    private static JsonNode? ReadNode(JsonObject value, string path)
    {
        JsonNode? current = value;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                JsonObject currentObject => currentObject[segment],
                JsonArray currentArray when int.TryParse(segment, out var index) && index >= 0 && index < currentArray.Count => currentArray[index],
                _ => null
            };
        }
        return current;
    }


    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
