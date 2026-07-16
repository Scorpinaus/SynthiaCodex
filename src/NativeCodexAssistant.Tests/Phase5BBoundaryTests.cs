using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure.Settings;
using NativeCodexAssistant.Infrastructure.Codex;

internal static class Phase5BBoundaryTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("app-server coordinator owns typed session lifecycle", AppServerCoordinatorOwnsLifecycleAsync),
        ("terminal view model owns sessions and commands", TerminalViewModelOwnsSessionsAsync),
        ("diagnostics view model publishes environment state", DiagnosticsViewModelPublishesEnvironmentAsync),
        ("git view model consumes explicit project context", GitViewModelConsumesContextAsync),
        ("project thread view model owns selection state", ProjectThreadViewModelOwnsSelectionAsync),
        ("task view model owns composer and response state", TaskViewModelOwnsStateAsync),
        ("legacy thread settings load into storage DTOs", LegacyThreadSettingsRemainCompatibleAsync),
        ("cancelled utility process cannot outlive its request", CancelledUtilityProcessIsTerminatedAsync)
    ];

    private static async Task AppServerCoordinatorOwnsLifecycleAsync()
    {
        await using var transport = new FakeAppServerTransport();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            new TestLogger(),
            new CodexAppServerClientMetadata("phase_5b_tests", "Phase 5B Tests", "1.0.0"));
        var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");
        var received = new List<AppServerNotification>();
        coordinator.NotificationReceived += (_, notification) => received.Add(notification);

        var connect = coordinator.EnsureConnectedAsync(installation);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test","platformFamily":"windows","platformOs":"windows"}}""");
        await connect;
        Assert(coordinator.State == AppServerSessionState.Connected, "coordinator reaches connected state");

        transport.ServerSend("""{"method":"item/agentMessage/delta","params":{"threadId":"t","turnId":"u","itemId":"i","delta":"hello"}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"u","status":"completed","items":[]}}}""");
        await WaitUntilAsync(() => received.Count == 2, "batched notifications forwarded");
        Assert(received[0].Method == "item/agentMessage/delta" && received[1].Method == "turn/completed", "notification order is preserved");

        await coordinator.DisposeAsync();
        Assert(coordinator.State == AppServerSessionState.Disposed, "coordinator reaches disposed state");
        Assert(transport.IsDisposed, "coordinator disposes transport through client");
    }

    private static async Task TerminalViewModelOwnsSessionsAsync()
    {
        using var temp = TempWorkspace.Create();
        var workspace = temp.CreateDirectory("TerminalFeature");
        var terminalService = new FakeTerminalService();
        var statuses = new List<string>();
        var selected = false;
        var viewModel = new TerminalViewModel(
            terminalService,
            new TestLogger(),
            () => new TerminalContext("thread:test", workspace),
            () => false,
            statuses.Add,
            () => selected = true);

        viewModel.StartCommand.Execute(null);
        await WaitUntilAsync(() => terminalService.Sessions.Count == 1, "terminal session created");
        Assert(viewModel.IsRunning, "terminal reports running");
        Assert(viewModel.IsVisible && selected, "terminal requests its workspace when started");

        viewModel.Input = "pwd";
        viewModel.SendInputCommand.Execute(null);
        await WaitUntilAsync(() => terminalService.Sessions[0].Inputs.Count == 1, "terminal input written");
        Assert(terminalService.Sessions[0].Inputs[0] == "pwd\r\n", "terminal appends a newline to input");

        await viewModel.ShutdownAsync();
        Assert(viewModel.SessionCount == 0 && terminalService.Sessions[0].IsDisposed, "terminal view model owns disposal");
    }

    private static async Task DiagnosticsViewModelPublishesEnvironmentAsync()
    {
        var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");
        var changed = false;
        var viewModel = new DiagnosticsViewModel(
            new FakeCodexDiscoveryService(installation),
            new FakeAuthService(new AuthenticationState(AuthReadiness.LikelySignedIn, "Signed in", "Ready", @"C:\Users\Test\.codex")),
            new FakeCodexCliUtilityRunner(),
            new TestLogger(),
            () => null,
            () => false,
            _ => { },
            @"C:\settings.json");
        viewModel.EnvironmentChanged += (_, _) => changed = true;

        await viewModel.RefreshAsync();
        Assert(changed, "diagnostics publishes environment changes");
        Assert(viewModel.Installation.IsFound && viewModel.Authentication.Readiness == AuthReadiness.LikelySignedIn, "diagnostics exposes typed state");
        Assert(viewModel.Lines.Any(line => line.Contains("Codex test", StringComparison.Ordinal)), "diagnostics builds presentation lines");
    }

    private static async Task GitViewModelConsumesContextAsync()
    {
        using var temp = TempWorkspace.Create();
        var workspace = temp.CreateDirectory("GitFeature");
        var viewModel = new GitViewModel(
            new FakeGitService(workspace),
            new FakeUserInteractionService(),
            new TestLogger(),
            () => new GitContext(workspace, workspace),
            () => false,
            _ => { });

        await viewModel.RefreshAsync();
        Assert(viewModel.IsRepository, "git view model reports repository state");
        Assert(viewModel.Branch == "main", "git view model reports branch");
        Assert(viewModel.RefreshCommand.CanExecute(null), "git command eligibility uses explicit context");
    }

    private static Task ProjectThreadViewModelOwnsSelectionAsync()
    {
        ProjectThreadState? selected = null;
        var viewModel = new ProjectThreadViewModel(
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => true,
            () => true,
            () => true,
            () => true,
            () => true,
            state => selected = state);
        viewModel.SetSelectedProjectPath(@"C:\Repo");
        viewModel.ReplaceThreads(
            [new ProjectThreadState { ProjectPath = @"C:\Repo", ThreadId = "thread-one", Title = "One" }],
            "thread-one");

        Assert(viewModel.SelectedProjectName == "Repo", "project presentation owns project name");
        Assert(selected?.ThreadId == "thread-one", "selection callback is explicit");
        Assert(viewModel.SelectedThreadTitle == "One", "workspace header presents only the selected thread title");
        Assert(viewModel.ActiveWorkspaceLabel == "Current checkout", "workspace presentation is derived locally");
        return Task.CompletedTask;
    }

    private static Task TaskViewModelOwnsStateAsync()
    {
        var viewModel = new TaskViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => true,
            () => true);
        var service = new CodexThreadService();
        service.Restore("thread-one", "Persisted response", [], []);
        viewModel.UseThreadService(service);
        viewModel.Prompt = "Run this";
        viewModel.SubmittedPrompt = viewModel.Prompt;
        viewModel.IsTurnRunning = true;
        viewModel.SteeringText = "Adjust it";
        viewModel.IsTurnRunning = false;

        Assert(viewModel.FinalResponse == "Persisted response", "task presentation owns response state");
        Assert(viewModel.Prompt == "Run this", "task presentation owns the composer");
        Assert(viewModel.SubmittedPromptDisplay == "Run this", "task transcript retains the submitted prompt");
        Assert(viewModel.SteeringText.Length == 0, "completed turn clears transient guidance");
        return Task.CompletedTask;
    }

    private static async Task LegacyThreadSettingsRemainCompatibleAsync()
    {
        using var temp = TempWorkspace.Create();
        var settingsPath = Path.Combine(temp.Root, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "theme": "Dark",
              "projectThreads": [
                {
                  "projectPath": "C:/LegacyRepo",
                  "threadId": "legacy-thread",
                  "title": "Legacy",
                  "isActive": true,
                  "turnStatus": "Completed",
                  "mode": "local",
                  "finalResponse": "Legacy response",
                  "timelineItems": [],
                  "rawEvents": []
                }
              ]
            }
            """);
        var settings = await new JsonSettingsStore(temp.Root, new TestLogger()).LoadAsync();
        Assert(settings.ProjectThreads.Single() is PersistedProjectThread, "settings use a storage-only thread DTO");
        var projected = new ThreadStore().GetProjectThreads(settings, @"C:\LegacyRepo").Single();
        Assert(projected.ThreadId == "legacy-thread" && projected.FinalResponse == "Legacy response", "legacy JSON projects into presentation state");
    }

    private static async Task CancelledUtilityProcessIsTerminatedAsync()
    {
        using var temp = TempWorkspace.Create();
        var marker = Path.Combine(temp.Root, "should-not-exist.txt");
        var executable = Path.Combine(temp.Root, "slow-codex.cmd");
        await File.WriteAllTextAsync(
            executable,
            $"@echo off\r\nif \"%1\"==\"doctor\" (\r\n  ping 127.0.0.1 -n 4 >nul\r\n  echo survived>\"{marker}\"\r\n)\r\n");
        var runner = new CodexCliUtilityRunner(new TestLogger());
        var installation = new CodexInstallation(true, executable, "test", "test", "test");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await runner.RunDoctorAsync(installation, cancellation.Token);
            throw new InvalidOperationException("Expected utility cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        await Task.Delay(2300);
        Assert(!File.Exists(marker), "cancelled utility process tree is terminated");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }
}
