using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Git;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Core.Terminal;
using NativeCodexAssistant.Core.Worktrees;
using NativeCodexAssistant.Infrastructure.Auth;
using NativeCodexAssistant.Infrastructure.Codex;
using NativeCodexAssistant.Infrastructure.Git;
using NativeCodexAssistant.Infrastructure.Logging;
using NativeCodexAssistant.Infrastructure.Projects;
using NativeCodexAssistant.Infrastructure.Settings;
using NativeCodexAssistant.Infrastructure.Terminal;
using NativeCodexAssistant.Infrastructure.Worktrees;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("recent projects are deduped and capped", TestRecentProjectsAsync),
    ("settings round trip to json", TestSettingsRoundTripAsync),
    ("view model applies and persists selected theme", TestViewModelAppliesAndPersistsThemeAsync),
    ("codex utility runner executes doctor", TestCodexUtilityRunnerExecutesDoctorAsync),
    ("view model surfaces codex doctor diagnostics", TestViewModelSurfacesCodexDoctorDiagnosticsAsync),
    ("auth detection reports file cache without reading token", TestAuthDetectionAsync),
    ("codex discovery skips unusable path candidates", TestCodexDiscoverySkipsUnusablePathCandidatesAsync),
    ("codex discovery checks OpenAI local app bin", TestCodexDiscoveryChecksOpenAiLocalAppBinAsync),
    ("app-server client writes initialize handshake", TestAppServerInitializeWritesHandshakeAsync),
    ("app-server client serializes initialize writes", TestAppServerClientSerializesInitializeWritesAsync),
    ("app-server client starts thread and turn", TestAppServerStartsThreadAndTurnAsync),
    ("app-server client sends model and reasoning overrides", TestAppServerSendsModelAndReasoningOverridesAsync),
    ("app-server client resumes thread", TestAppServerResumesThreadAsync),
    ("app-server client sends lifecycle requests", TestAppServerLifecycleRequestsAsync),
    ("app-server initialize advertises notification opt outs", TestAppServerInitializeCapabilitiesAsync),
    ("app-server client reports connection failure", TestAppServerConnectionFailureAsync),
    ("app-server client lists models", TestAppServerListsModelsAsync),
    ("app-server notifications update thread state", TestAppServerNotificationsUpdateThreadStateAsync),
    ("app-server v2 notifications update thread state", TestAppServerV2NotificationsUpdateThreadStateAsync),
    ("app-server error notifications show detail", TestAppServerErrorNotificationsShowDetailAsync),
    ("thread store keeps multiple project threads", TestThreadStoreKeepsMultipleProjectThreadsAsync),
    ("thread workspace routes parallel notifications", TestThreadWorkspaceRoutesParallelNotificationsAsync),
    ("view model preserves prompt after auth failed turn", TestViewModelPreservesPromptAfterAuthFailedTurnAsync),
    ("view model cancellation sends active thread and turn", TestViewModelCancellationSendsActiveThreadAndTurnAsync),
    ("view model restores persisted thread and resumes it", TestViewModelRestoresPersistedThreadAndResumesItAsync),
    ("view model sends selected model and reasoning", TestViewModelSendsSelectedModelAndReasoningAsync),
    ("view model loads model options", TestViewModelLoadsModelOptionsAsync),
    ("view model manages multiple threads", TestViewModelManagesMultipleThreadsAsync),
    ("view model forks archives and unarchives threads", TestViewModelForksArchivesAndUnarchivesThreadsAsync),
    ("view model steers an active turn", TestViewModelSteersActiveTurnAsync),
    ("view model runs parallel project threads", TestViewModelRunsParallelProjectThreadsAsync),
    ("view model restarts app-server after crash", TestViewModelRestartsAppServerAfterCrashAsync),
    ("view model exit command requests close", TestViewModelExitCommandRequestsCloseAsync),
    ("view model shutdown cancels running turn and disposes transport", TestViewModelShutdownCancelsRunningTurnAndDisposesTransportAsync),
    ("git service reads status and diffs", TestGitServiceReadsStatusAndDiffsAsync),
    ("git service stages commits and reverts", TestGitServiceStagesCommitsAndRevertsAsync),
    ("git service refuses non-repository folders", TestGitServiceRefusesNonRepositoryFoldersAsync),
    ("worktree service creates isolated sibling worktree", TestWorktreeServiceCreatesIsolatedSiblingAsync),
    ("worktree service lists only assistant worktrees", TestWorktreeServiceListsOnlyAssistantWorktreesAsync),
    ("worktree service refuses unowned cleanup", TestWorktreeServiceRefusesUnownedCleanupAsync),
    ("worktree service removes owned clean worktree", TestWorktreeServiceRemovesOwnedCleanWorktreeAsync),
    ("view model starts worktree task in isolated cwd", TestViewModelStartsWorktreeTaskInIsolatedCwdAsync),
    ("conpty terminal starts powershell in requested cwd", TestConPtyTerminalStartsInRequestedCwdAsync),
    ("view model starts terminal in active worktree", TestViewModelStartsTerminalInActiveWorktreeAsync),
    ("view model keeps terminal output isolated by thread", TestViewModelKeepsTerminalOutputIsolatedByThreadAsync),
    ("view model terminal actions and shutdown own sessions", TestViewModelTerminalActionsAndShutdownOwnSessionsAsync),
    ("app-server cancellation sends turn interrupt", TestAppServerCancellationSendsInterruptAsync),
    ("live codex app-server initializes when enabled", TestLiveCodexAppServerInitializesWhenEnabledAsync)
};

var failures = 0;
var testFilter = Environment.GetEnvironmentVariable("NCA_TEST_FILTER");
foreach (var test in tests.Where(test =>
             string.IsNullOrWhiteSpace(testFilter) ||
             test.Name.Contains(testFilter, StringComparison.OrdinalIgnoreCase)))
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}");
        Console.Error.WriteLine(ex);
    }
}

if (failures > 0)
{
    return 1;
}

Console.WriteLine($"All {tests.Count} tests passed.");
return 0;

static Task TestRecentProjectsAsync()
{
    using var temp = TempWorkspace.Create();
    var settings = new AppSettings();
    var service = new RecentProjectService();

    for (var i = 0; i < 12; i++)
    {
        var path = temp.CreateDirectory($"Project{i}");
        service.AddRecentProject(settings, path);
    }

    AssertEqual(10, settings.RecentProjects.Count, "recent project cap");

    var duplicate = settings.RecentProjects[4].Path;
    service.AddRecentProject(settings, duplicate);

    AssertEqual(10, settings.RecentProjects.Count, "dedupe preserves cap");
    AssertEqual(duplicate, settings.RecentProjects[0].Path, "duplicate moves to top");
    AssertEqual(1, settings.RecentProjects.Count(project => string.Equals(project.Path, duplicate, StringComparison.OrdinalIgnoreCase)), "duplicate count");

    return Task.CompletedTask;
}

static async Task TestSettingsRoundTripAsync()
{
    using var temp = TempWorkspace.Create();
    var logger = new FileAppLogger(temp.Root);
    var store = new JsonSettingsStore(temp.Root, logger);
    var settings = new AppSettings
    {
        Theme = "Dark",
        PreferredCodexPath = @"C:\Tools\codex.exe",
        LastModelOverride = "gpt-test",
        LastReasoningEffortOverride = "high"
    };
    settings.RecentProjects.Add(new(temp.CreateDirectory("Repo"), "Repo", DateTimeOffset.UtcNow));
    settings.ProjectThreads.Add(new ProjectThreadState
    {
        ProjectPath = temp.CreateDirectory("ThreadRepo"),
        ThreadId = "thr_saved",
        Mode = "worktree",
        WorkspacePath = temp.CreateDirectory("ThreadWorkspace"),
        FinalResponse = "Saved final response",
        TimelineItems =
        [
            new CodexTimelineItem(
                CodexTimelineItemKind.AgentMessage,
                "Item completed",
                "Saved final response",
                "item/completed",
                DateTimeOffset.UtcNow)
        ],
        RawEvents = ["item/completed: {}"],
        UpdatedAt = DateTimeOffset.UtcNow
    });

    await store.SaveAsync(settings);
    var loaded = await store.LoadAsync();

    AssertEqual("Dark", loaded.Theme, "theme");
    AssertEqual(settings.PreferredCodexPath, loaded.PreferredCodexPath, "preferred codex path");
    AssertEqual(settings.LastModelOverride, loaded.LastModelOverride, "last model override");
    AssertEqual(settings.LastReasoningEffortOverride, loaded.LastReasoningEffortOverride, "last reasoning override");
    AssertEqual(1, loaded.RecentProjects.Count, "recent project count");
    AssertEqual(1, loaded.ProjectThreads.Count, "project thread count");
    AssertEqual("thr_saved", loaded.ProjectThreads[0].ThreadId, "project thread id");
    AssertEqual("worktree", loaded.ProjectThreads[0].Mode, "project thread mode");
    AssertEqual(settings.ProjectThreads[0].WorkspacePath, loaded.ProjectThreads[0].WorkspacePath, "project thread workspace path");
    AssertEqual("Saved final response", loaded.ProjectThreads[0].FinalResponse, "project thread final response");
    AssertTrue(File.Exists(store.SettingsPath), "settings file exists");
}

static async Task TestViewModelAppliesAndPersistsThemeAsync()
{
    using var temp = TempWorkspace.Create();
    var settingsStore = new FakeSettingsStore(new AppSettings { Theme = "Dark" });
    var themeService = new FakeThemeService();
    var viewModel = CreateMainViewModel(
        new FakeAppServerTransport(),
        temp.Root,
        AuthReadiness.LikelySignedIn,
        settingsStore,
        themeService);

    await viewModel.InitializeAsync();

    AssertEqual("Dark", viewModel.SelectedTheme, "persisted theme selection");
    AssertEqual("Dark", themeService.AppliedTheme, "persisted theme applied");

    viewModel.SelectedTheme = "Light";
    await WaitUntilAsync(() => settingsStore.SavedSettings.Theme == "Light", "theme setting saved");

    AssertEqual("Light", themeService.AppliedTheme, "changed theme applied");
}

static async Task TestCodexUtilityRunnerExecutesDoctorAsync()
{
    using var temp = TempWorkspace.Create();
    var executable = Path.Combine(temp.Root, "fake-codex.cmd");
    await File.WriteAllTextAsync(
        executable,
        "@echo off\r\nif \"%1\"==\"doctor\" (\r\n  echo DOCTOR_OK\r\n  echo diagnostic warning 1>&2\r\n  exit /b 0\r\n)\r\nexit /b 9\r\n");
    var installation = new CodexInstallation(true, executable, "codex-test", "Codex test", "Test installation");
    var runner = new CodexCliUtilityRunner(new TestLogger());

    var result = await runner.RunDoctorAsync(installation);

    AssertEqual(0, result.ExitCode, "doctor exit code");
    AssertTrue(result.StandardOutput.Contains("DOCTOR_OK", StringComparison.Ordinal), "doctor stdout captured");
    AssertTrue(result.StandardError.Contains("diagnostic warning", StringComparison.Ordinal), "doctor stderr captured");
    AssertTrue(result.Succeeded, "doctor success state");
}

static async Task TestAppServerInitializeCapabilitiesAsync()
{
    var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());

    var initializeTask = client.InitializeAsync(new CodexInitializeOptions(
        ExperimentalApi: false,
        OptOutNotificationMethods: ["thread/tokenUsage/updated"]));
    await transport.WaitForClientMessageCountAsync(2);

    var initialize = ParseMessage(transport.ClientMessages[0]);
    AssertEqual(false, ResolvePath(initialize, "params.capabilities.experimentalApi")!.GetValue<bool>(), "experimental capability");
    AssertJsonString(
        "thread/tokenUsage/updated",
        initialize,
        "params.capabilities.optOutNotificationMethods.0",
        "notification opt out");

    transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
    await initializeTask;
}

static async Task TestAppServerLifecycleRequestsAsync()
{
    var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());
    await CompleteInitializeAsync(client, transport);

    var listTask = client.ListThreadsAsync(new CodexThreadListRequest(@"C:\Repo", Archived: false, Limit: 25));
    await transport.WaitForClientMessageCountAsync(3);
    var list = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("thread/list", list, "method", "thread list method");
    AssertJsonString(@"C:\Repo", list, "params.cwd", "thread list cwd");
    transport.ServerSend(
        """
        {"id":1,"result":{"data":[{"id":"thr_a","name":"First thread","preview":"First prompt","cwd":"C:\\Repo","createdAt":100,"updatedAt":200,"status":{"type":"idle"}}],"nextCursor":"next"}}
        """);
    var page = await listTask;
    AssertEqual(1, page.Threads.Count, "listed thread count");
    AssertEqual("thr_a", page.Threads[0].ThreadId, "listed thread id");
    AssertEqual("First thread", page.Threads[0].Title, "listed thread title");
    AssertEqual("next", page.NextCursor, "thread list cursor");

    var forkTask = client.ForkThreadAsync(new CodexThreadForkRequest("thr_a", @"C:\Repo", CodexSandbox.WorkspaceWrite));
    await transport.WaitForClientMessageCountAsync(4);
    var fork = ParseMessage(transport.ClientMessages[3]);
    AssertJsonString("thread/fork", fork, "method", "thread fork method");
    AssertJsonString("thr_a", fork, "params.threadId", "fork source id");
    transport.ServerSend("""{"id":2,"result":{"thread":{"id":"thr_fork"}}}""");
    AssertEqual("thr_fork", (await forkTask).ThreadId, "forked thread id");

    var archiveTask = client.ArchiveThreadAsync("thr_a");
    await transport.WaitForClientMessageCountAsync(5);
    AssertJsonString("thread/archive", ParseMessage(transport.ClientMessages[4]), "method", "archive method");
    transport.ServerSend("""{"id":3,"result":{}}""");
    await archiveTask;

    var unarchiveTask = client.UnarchiveThreadAsync("thr_a");
    await transport.WaitForClientMessageCountAsync(6);
    AssertJsonString("thread/unarchive", ParseMessage(transport.ClientMessages[5]), "method", "unarchive method");
    transport.ServerSend("""{"id":4,"result":{"thread":{"id":"thr_a"}}}""");
    await unarchiveTask;

    var steerTask = client.SteerTurnAsync(new CodexTurnSteerRequest("thr_a", "turn_1", "Focus on tests."));
    await transport.WaitForClientMessageCountAsync(7);
    var steer = ParseMessage(transport.ClientMessages[6]);
    AssertJsonString("turn/steer", steer, "method", "turn steer method");
    AssertJsonString("turn_1", steer, "params.expectedTurnId", "steer turn precondition");
    AssertJsonString("Focus on tests.", steer, "params.input.0.text", "steer text");
    transport.ServerSend("""{"id":5,"result":{"turnId":"turn_1"}}""");
    AssertEqual("turn_1", (await steerTask).TurnId, "steered turn id");
}

static async Task TestAppServerConnectionFailureAsync()
{
    var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());
    await CompleteInitializeAsync(client, transport);
    AppServerConnectionFailedEventArgs? failure = null;
    client.ConnectionFailed += (_, args) => failure = args;

    var pending = client.ListThreadsAsync(new CodexThreadListRequest(@"C:\Repo"));
    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerFail(new IOException("fake app-server crash"));

    var requestFailed = false;
    try
    {
        await pending;
    }
    catch (IOException ex) when (ex.Message.Contains("fake app-server crash", StringComparison.Ordinal))
    {
        requestFailed = true;
    }

    await WaitUntilAsync(() => failure is not null, "connection failure event");
    AssertTrue(requestFailed, "pending request failed after connection crash");
    AssertTrue(!client.IsHealthy, "client health after connection crash");
    AssertTrue(failure!.Exception.Message.Contains("fake app-server crash", StringComparison.Ordinal), "connection failure detail");
}

static Task TestThreadStoreKeepsMultipleProjectThreadsAsync()
{
    var settings = new AppSettings();
    var store = new ThreadStore();
    var first = new ProjectThreadState
    {
        ProjectPath = @"C:\Repo",
        ThreadId = "thr_1",
        Title = "First",
        CreatedAt = DateTimeOffset.Parse("2026-07-13T01:00:00Z")
    };
    var second = new ProjectThreadState
    {
        ProjectPath = @"C:\Repo",
        ThreadId = "thr_2",
        Title = "Second",
        CreatedAt = DateTimeOffset.Parse("2026-07-13T02:00:00Z")
    };

    store.Upsert(settings, first);
    store.Upsert(settings, second);
    store.SetActive(settings, @"C:\Repo", "thr_2");
    store.SetArchived(settings, @"C:\Repo", "thr_1", archived: true);

    var threads = store.GetProjectThreads(settings, @"C:\Repo", includeArchived: true);
    AssertEqual(2, threads.Count, "multi-thread count");
    AssertEqual("thr_2", store.GetActive(settings, @"C:\Repo")?.ThreadId, "active project thread");
    AssertTrue(threads.Single(thread => thread.ThreadId == "thr_1").IsArchived, "archived state");
    AssertEqual(1, store.GetProjectThreads(settings, @"C:\Repo", includeArchived: false).Count, "archived filter");
    return Task.CompletedTask;
}

static Task TestThreadWorkspaceRoutesParallelNotificationsAsync()
{
    var workspace = new CodexThreadWorkspace();
    workspace.Restore(new ProjectThreadState { ProjectPath = @"C:\Repo", ThreadId = "thr_a" });
    workspace.Restore(new ProjectThreadState { ProjectPath = @"C:\Repo", ThreadId = "thr_b" });

    workspace.ApplyNotification(Notification(
        "item/agentMessage/delta",
        """{"threadId":"thr_a","turnId":"turn_a","delta":"alpha"}"""));
    workspace.ApplyNotification(Notification(
        "item/agentMessage/delta",
        """{"threadId":"thr_b","turnId":"turn_b","delta":"beta"}"""));

    AssertEqual("alpha", workspace.GetRequired("thr_a").FinalResponse, "first parallel response");
    AssertEqual("beta", workspace.GetRequired("thr_b").FinalResponse, "second parallel response");
    AssertEqual(1, workspace.GetRequired("thr_a").RawEvents.Count, "first parallel event count");
    AssertEqual(1, workspace.GetRequired("thr_b").RawEvents.Count, "second parallel event count");
    return Task.CompletedTask;
}

static async Task TestViewModelSurfacesCodexDoctorDiagnosticsAsync()
{
    using var temp = TempWorkspace.Create();
    var utilityRunner = new FakeCodexCliUtilityRunner(new CodexCliUtilityResult(
        "doctor",
        0,
        "Doctor reports healthy",
        string.Empty));
    var viewModel = CreateMainViewModel(
        new FakeAppServerTransport(),
        temp.Root,
        AuthReadiness.LikelySignedIn,
        cliUtilityRunner: utilityRunner);

    await viewModel.InitializeAsync();
    viewModel.RunCodexDoctorCommand.Execute(null);
    await WaitUntilAsync(
        () => viewModel.Diagnostics.Any(line => line.Contains("Doctor reports healthy", StringComparison.Ordinal)),
        "doctor output shown");

    AssertEqual(1, utilityRunner.RunCount, "doctor invocation count");
}

static async Task TestViewModelManagesMultipleThreadsAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var settingsStore = new FakeSettingsStore();
    var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn, settingsStore);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "multi-thread project selected");

    viewModel.NewThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_one"}}}""");
    await WaitUntilAsync(() => viewModel.ProjectThreads.Count == 1, "first thread created");
    await WaitUntilAsync(() => viewModel.NewThreadCommand.CanExecute(null), "new thread command ready again");

    viewModel.NewThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(4);
    transport.ServerSend("""{"id":2,"result":{"thread":{"id":"thr_two"}}}""");
    await WaitUntilAsync(() => viewModel.ProjectThreads.Count == 2, "second thread created");
    AssertEqual("thr_two", viewModel.SelectedThread?.ThreadId, "newest thread selected");

    viewModel.SelectedThread = viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_one");
    viewModel.ResumeThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(5);
    AssertJsonString("thread/resume", ParseMessage(transport.ClientMessages[4]), "method", "resume selected thread method");
    transport.ServerSend("""{"id":3,"result":{"thread":{"id":"thr_one"}}}""");
    await WaitUntilAsync(() => viewModel.StatusMessage.Contains("resumed", StringComparison.OrdinalIgnoreCase), "selected thread resumed");

    AssertEqual(2, settingsStore.SavedSettings.ProjectThreads.Count, "multiple threads persisted");
    await viewModel.DisposeAsync();
}

static async Task TestViewModelForksArchivesAndUnarchivesThreadsAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "fork project selected");

    viewModel.NewThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend("""{"id":0,"result":{}}""");
    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_source"}}}""");
    await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "thr_source", "source thread created");

    viewModel.ForkThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(4);
    AssertJsonString("thread/fork", ParseMessage(transport.ClientMessages[3]), "method", "view model fork method");
    transport.ServerSend("""{"id":2,"result":{"thread":{"id":"thr_forked"}}}""");
    await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "thr_forked", "fork selected");

    viewModel.ArchiveThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(5);
    transport.ServerSend("""{"id":3,"result":{}}""");
    await WaitUntilAsync(() => viewModel.SelectedThread?.IsArchived == true, "thread archived");

    viewModel.UnarchiveThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(6);
    transport.ServerSend("""{"id":4,"result":{"thread":{"id":"thr_forked"}}}""");
    await WaitUntilAsync(() => viewModel.SelectedThread?.IsArchived == false, "thread unarchived");
    await viewModel.DisposeAsync();
}

static async Task TestViewModelSteersActiveTurnAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "steering project selected");

    viewModel.PromptText = "Start work.";
    viewModel.SubmitPromptCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend("""{"id":0,"result":{}}""");
    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_steer"}}}""");
    await transport.WaitForClientMessageCountAsync(4);
    transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_steer"}}}""");
    await WaitUntilAsync(() => viewModel.IsTurnRunning, "steer turn running");

    viewModel.SteeringText = "Concentrate on regression tests.";
    await WaitUntilAsync(() => viewModel.SteerTurnCommand.CanExecute(null), "steer command enabled");
    viewModel.SteerTurnCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(5);
    var steer = ParseMessage(transport.ClientMessages[4]);
    AssertJsonString("turn/steer", steer, "method", "view model steer method");
    AssertJsonString("turn_steer", steer, "params.expectedTurnId", "view model steer turn id");
    transport.ServerSend("""{"id":3,"result":{"turnId":"turn_steer"}}""");
    await WaitUntilAsync(() => string.IsNullOrWhiteSpace(viewModel.SteeringText), "steering composer cleared");

    transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_steer","turn":{"id":"turn_steer","status":"completed"}}}""");
    await WaitUntilAsync(() => !viewModel.IsTurnRunning, "steered turn completed");
    await viewModel.DisposeAsync();
}

static async Task TestViewModelRestartsAppServerAfterCrashAsync()
{
    using var temp = TempWorkspace.Create();
    await using var firstTransport = new FakeAppServerTransport();
    await using var secondTransport = new FakeAppServerTransport();
    var processService = new SequenceCodexProcessService(firstTransport, secondTransport);
    var viewModel = CreateMainViewModel(
        firstTransport,
        temp.Root,
        AuthReadiness.LikelySignedIn,
        processService: processService);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "recovery project selected");

    viewModel.NewThreadCommand.Execute(null);
    await firstTransport.WaitForClientMessageCountAsync(2);
    firstTransport.ServerSend("""{"id":0,"result":{}}""");
    await firstTransport.WaitForClientMessageCountAsync(3);
    firstTransport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_recover"}}}""");
    await WaitUntilAsync(() => viewModel.AppServerHealth == "Healthy", "initial app-server healthy");

    firstTransport.ServerFail(new IOException("simulated crash"));
    await WaitUntilAsync(() => viewModel.AppServerHealth == "Recovering", "app-server recovering");

    viewModel.LoadModelsCommand.Execute(null);
    await secondTransport.WaitForClientMessageCountAsync(2);
    secondTransport.ServerSend("""{"id":0,"result":{}}""");
    await secondTransport.WaitForClientMessageCountAsync(3);
    secondTransport.ServerSend("""{"id":1,"result":{"data":[]}}""");
    await WaitUntilAsync(() => viewModel.AppServerHealth == "Healthy", "app-server recovered");
    AssertEqual(2, processService.StartCount, "app-server restart count");
    await viewModel.DisposeAsync();
}

static async Task TestViewModelRunsParallelProjectThreadsAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "parallel project selected");

    viewModel.PromptText = "First parallel task";
    viewModel.SubmitPromptCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend("""{"id":0,"result":{}}""");
    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_parallel_a"}}}""");
    await transport.WaitForClientMessageCountAsync(4);
    transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_parallel_a"}}}""");
    await WaitUntilAsync(() => viewModel.IsTurnRunning, "first parallel turn running");

    viewModel.NewThreadCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(5);
    transport.ServerSend("""{"id":3,"result":{"thread":{"id":"thr_parallel_b"}}}""");
    await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "thr_parallel_b", "second parallel thread selected");
    AssertTrue(!viewModel.IsTurnRunning, "second thread composer remains available");

    viewModel.PromptText = "Second parallel task";
    viewModel.SubmitPromptCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(6);
    transport.ServerSend("""{"id":4,"result":{"turn":{"id":"turn_parallel_b"}}}""");
    await WaitUntilAsync(() => viewModel.ProjectThreads.All(thread => thread.IsRunning), "parallel running indicators");

    transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_parallel_a","turn":{"id":"turn_parallel_a","status":"completed"}}}""");
    await WaitUntilAsync(
        () => !viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_parallel_a").IsRunning,
        "first parallel indicator completed");
    AssertTrue(viewModel.IsTurnRunning, "second parallel turn remains active");

    transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_parallel_b","turn":{"id":"turn_parallel_b","status":"completed"}}}""");
    await WaitUntilAsync(() => !viewModel.IsTurnRunning, "second parallel turn completed");
    await viewModel.DisposeAsync();
}

static async Task TestAuthDetectionAsync()
{
    using var temp = TempWorkspace.Create();
    var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    Environment.SetEnvironmentVariable("CODEX_HOME", temp.Root);

    try
    {
        var logger = new TestLogger();
        var service = new CodexAuthService(logger);
        var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");

        var missing = await service.GetAuthenticationStateAsync(installation);
        AssertEqual(AuthReadiness.Unknown, missing.Readiness, "missing auth readiness");

        File.WriteAllText(Path.Combine(temp.Root, "auth.json"), "{\"access_token\":\"do-not-read\"}");
        var detected = await service.GetAuthenticationStateAsync(installation);

        AssertEqual(AuthReadiness.LikelySignedIn, detected.Readiness, "detected auth readiness");
        AssertTrue(!detected.Detail.Contains("do-not-read", StringComparison.Ordinal), "token is not surfaced");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
    }
}

static async Task TestCodexDiscoverySkipsUnusablePathCandidatesAsync()
{
    using var temp = TempWorkspace.Create();
    var brokenDir = temp.CreateDirectory("BrokenCli");
    var workingDir = temp.CreateDirectory("WorkingCli");
    var brokenCodex = Path.Combine(brokenDir, "codex.cmd");
    var workingCodex = Path.Combine(workingDir, "codex.cmd");
    var previousPath = Environment.GetEnvironmentVariable("PATH");

    File.WriteAllText(
        brokenCodex,
        """
        @echo off
        echo broken codex 1>&2
        exit /b 5
        """);
    File.WriteAllText(
        workingCodex,
        """
        @echo off
        echo codex-cli test-version
        exit /b 0
        """);

    try
    {
        Environment.SetEnvironmentVariable("PATH", brokenDir + Path.PathSeparator + workingDir);
        var logger = new TestLogger();
        var service = new CodexDiscoveryService(logger);

        var detected = await service.DetectAsync();

        AssertTrue(detected.IsFound, "working codex is found");
        AssertEqual(Path.GetFullPath(workingCodex), detected.ExecutablePath, "working codex path");
        AssertEqual("codex-cli test-version", detected.Version, "working codex version");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
    }
}

static async Task TestCodexDiscoveryChecksOpenAiLocalAppBinAsync()
{
    using var temp = TempWorkspace.Create();
    var localAppData = temp.CreateDirectory("LocalAppData");
    var codexBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
    Directory.CreateDirectory(codexBin);
    var codexPath = Path.Combine(codexBin, "codex.cmd");
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

    File.WriteAllText(
        codexPath,
        """
        @echo off
        echo codex-cli local-app-bin
        exit /b 0
        """);

    try
    {
        Environment.SetEnvironmentVariable("PATH", temp.CreateDirectory("EmptyPath"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
        var logger = new TestLogger();
        var service = new CodexDiscoveryService(logger);

        var detected = await service.DetectAsync();

        AssertTrue(detected.IsFound, "local app bin codex is found");
        AssertEqual(Path.GetFullPath(codexPath), detected.ExecutablePath, "local app bin codex path");
        AssertEqual("codex-cli local-app-bin", detected.Version, "local app bin codex version");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
    }
}

static async Task TestAppServerInitializeWritesHandshakeAsync()
{
    await using var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());

    var initializeTask = client.InitializeAsync();
    await transport.WaitForClientMessageCountAsync(2);

    var initialize = ParseMessage(transport.ClientMessages[0]);
    AssertJsonString("initialize", initialize, "method", "initialize method");
    AssertJsonInt(0, initialize, "id", "initialize id");
    AssertJsonString("native_codex_assistant", initialize, "params.clientInfo.name", "client info name");
    AssertJsonString("Native Codex Assistant", initialize, "params.clientInfo.title", "client info title");

    var initialized = ParseMessage(transport.ClientMessages[1]);
    AssertJsonString("initialized", initialized, "method", "initialized method");
    AssertTrue(!initialized.AsObject().ContainsKey("id"), "initialized notification has no id");

    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    var session = await initializeTask;
    AssertEqual("codex-test", session.UserAgent, "initialize user agent");
    AssertEqual("windows", session.PlatformFamily, "initialize platform family");
}

static async Task TestAppServerClientSerializesInitializeWritesAsync()
{
    await using var transport = new SlowWriteAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());

    var initializeTask = client.InitializeAsync();
    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    var session = await initializeTask;

    AssertEqual("codex-test", session.UserAgent, "serialized initialize user agent");
    AssertTrue(!transport.OverlappingWriteDetected, "transport writes did not overlap");
}

static async Task TestAppServerStartsThreadAndTurnAsync()
{
    await using var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());
    await CompleteInitializeAsync(client, transport);

    var threadTask = client.StartThreadAsync(CodexThreadStartOptions.Default);
    await transport.WaitForClientMessageCountAsync(3);

    var threadRequest = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("thread/start", threadRequest, "method", "thread start method");
    AssertJsonInt(1, threadRequest, "id", "thread start id");
    AssertTrue(threadRequest["params"] is not null, "thread start params object");

    transport.ServerSend(
        """
        {"id":1,"result":{"thread":{"id":"thr_123"}}}
        """);

    var thread = await threadTask;
    AssertEqual("thr_123", thread.ThreadId, "thread id");

    var cwd = Path.Combine("D:\\", "Repo With Space");
    var turnTask = client.StartTurnAsync(new CodexTurnStartRequest(
        thread.ThreadId,
        "Summarize this repo.",
        cwd,
        CodexSandbox.WorkspaceWrite));

    await transport.WaitForClientMessageCountAsync(4);

    var turnRequest = ParseMessage(transport.ClientMessages[3]);
    AssertJsonString("turn/start", turnRequest, "method", "turn start method");
    AssertJsonInt(2, turnRequest, "id", "turn start id");
    AssertJsonString("thr_123", turnRequest, "params.threadId", "turn thread id");
    AssertJsonString(cwd, turnRequest, "params.cwd", "turn cwd");
    AssertJsonString("workspaceWrite", turnRequest, "params.sandboxPolicy.type", "turn sandbox policy");
    AssertTrue(ResolvePath(turnRequest, "params.sandbox") is null, "turn sandbox legacy field is omitted");
    AssertJsonString("text", turnRequest, "params.input.0.type", "turn input type");
    AssertJsonString("Summarize this repo.", turnRequest, "params.input.0.text", "turn input text");

    transport.ServerSend(
        """
        {"id":2,"result":{"turn":{"id":"turn_456"}}}
        """);

    var turn = await turnTask;
    AssertEqual("turn_456", turn.TurnId, "turn id");
}

static async Task TestAppServerSendsModelAndReasoningOverridesAsync()
{
    await using var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());
    await CompleteInitializeAsync(client, transport);

    var turnTask = client.StartTurnAsync(new CodexTurnStartRequest(
        "thr_123",
        "Summarize this repo.",
        Path.Combine("D:\\", "Repo"),
        CodexSandbox.WorkspaceWrite,
        "gpt-test",
        CodexReasoningEffort.High));

    await transport.WaitForClientMessageCountAsync(3);

    var turnRequest = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("turn/start", turnRequest, "method", "override turn method");
    AssertJsonString("gpt-test", turnRequest, "params.model", "turn model override");
    AssertJsonString("high", turnRequest, "params.effort", "turn reasoning effort");

    transport.ServerSend(
        """
        {"id":1,"result":{"turn":{"id":"turn_456"}}}
        """);

    var turn = await turnTask;
    AssertEqual("turn_456", turn.TurnId, "override turn id");
}

static async Task TestAppServerResumesThreadAsync()
{
    await using var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());
    await CompleteInitializeAsync(client, transport);

    var cwd = Path.Combine("D:\\", "Repo With Space");
    var resumeTask = client.ResumeThreadAsync(new CodexThreadResumeRequest(
        "thr_existing",
        cwd,
        CodexSandbox.WorkspaceWrite));

    await transport.WaitForClientMessageCountAsync(3);

    var resumeRequest = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("thread/resume", resumeRequest, "method", "thread resume method");
    AssertJsonInt(1, resumeRequest, "id", "thread resume id");
    AssertJsonString("thr_existing", resumeRequest, "params.threadId", "resume thread id");
    AssertJsonString(cwd, resumeRequest, "params.cwd", "resume cwd");
    AssertJsonString("workspace-write", resumeRequest, "params.sandbox", "resume sandbox");

    transport.ServerSend(
        """
        {"id":1,"result":{"thread":{"id":"thr_existing"}}}
        """);

    var resumed = await resumeTask;
    AssertEqual("thr_existing", resumed.ThreadId, "resumed thread id");
}

static async Task TestAppServerListsModelsAsync()
{
    await using var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());
    await CompleteInitializeAsync(client, transport);

    var modelsTask = client.ListModelsAsync();
    await transport.WaitForClientMessageCountAsync(3);

    var modelsRequest = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("model/list", modelsRequest, "method", "model list method");
    AssertJsonInt(1, modelsRequest, "id", "model list id");

    transport.ServerSend(
        """
        {"id":1,"result":{"data":[{"id":"default","model":"gpt-default","displayName":"GPT Default","isDefault":true,"supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"Balanced"}]},{"id":"fast","model":"gpt-fast","displayName":"GPT Fast","isDefault":false,"supportedReasoningEfforts":[{"reasoningEffort":"minimal","description":"Fast"}]}]}}
        """);

    var models = await modelsTask;
    AssertEqual(2, models.Count, "model count");
    AssertEqual("gpt-default", models[0].Model, "first model id");
    AssertEqual("GPT Default", models[0].DisplayName, "first model display");
    AssertTrue(models[0].IsDefault, "first model default");
    AssertEqual("medium", models[0].SupportedReasoningEfforts[0], "first model effort");
}

static Task TestAppServerNotificationsUpdateThreadStateAsync()
{
    var service = new CodexThreadService();

    service.ApplyNotification(Notification(
        "turn/started",
        """
        {"turn":{"id":"turn_456"}}
        """));
    service.ApplyNotification(Notification(
        "item/started",
        """
        {"item":{"type":"command","command":"dotnet test"}}
        """));
    service.ApplyNotification(Notification(
        "item/agentMessage/delta",
        """
        {"delta":"Hello "}
        """));
    service.ApplyNotification(Notification(
        "item/agentMessage/delta",
        """
        {"delta":"world"}
        """));
    service.ApplyNotification(Notification(
        "turn/completed",
        """
        {"turn":{"id":"turn_456"},"status":"completed"}
        """));

    AssertEqual(CodexTurnStatus.Completed, service.ActiveTurnStatus, "turn status");
    AssertEqual("turn_456", service.ActiveTurnId, "active turn id");
    AssertEqual("Hello world", service.FinalResponse, "final response");
    AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.TurnStarted), "turn started timeline item");
    AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.CommandStarted), "command started timeline item");
    AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.AgentMessageDelta), "agent delta timeline item");
    AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.TurnCompleted), "turn completed timeline item");

    return Task.CompletedTask;
}

static Task TestAppServerV2NotificationsUpdateThreadStateAsync()
{
    var service = new CodexThreadService();

    service.ApplyNotification(Notification(
        "item/completed",
        """
        {"item":{"type":"agentMessage","text":"PHASE1_SMOKE_OK"},"threadId":"thr_123","turnId":"turn_456"}
        """));
    service.ApplyNotification(Notification(
        "turn/completed",
        """
        {"threadId":"thr_123","turn":{"id":"turn_456","status":"failed","error":{"message":"stream disconnected before completion","additionalDetails":"missing auth"},"items":[]}}
        """));

    AssertEqual(CodexTurnStatus.Failed, service.ActiveTurnStatus, "v2 failed turn status");
    AssertEqual("turn_456", service.ActiveTurnId, "v2 active turn id");
    AssertEqual("PHASE1_SMOKE_OK", service.FinalResponse, "v2 final response");
    AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.AgentMessage), "v2 agent message timeline item");
    AssertTrue(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.TurnCompleted && item.Detail.Contains("stream disconnected", StringComparison.Ordinal)), "v2 failed turn detail");

    return Task.CompletedTask;
}

static Task TestAppServerErrorNotificationsShowDetailAsync()
{
    var service = new CodexThreadService();

    service.ApplyNotification(Notification(
        "error",
        """
        {"error":{"message":"Reconnecting... 2/5","additionalDetails":"unexpected status 401 Unauthorized"},"willRetry":true,"threadId":"thr_123","turnId":"turn_456"}
        """));

    var error = service.TimelineItems.Single(item => item.Kind == CodexTimelineItemKind.Error);
    AssertTrue(error.Detail.Contains("Reconnecting", StringComparison.Ordinal), "error detail includes message");
    AssertTrue(error.Detail.Contains("401", StringComparison.Ordinal), "error detail includes additional details");

    return Task.CompletedTask;
}

static async Task TestViewModelPreservesPromptAfterAuthFailedTurnAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var projectPath = temp.CreateDirectory("Repo");
    var prompt = "Summarize this repo.";
    var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);

    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

    viewModel.PromptText = prompt;
    viewModel.SubmitPromptCommand.Execute(null);

    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend(
        """
        {"id":1,"result":{"thread":{"id":"thr_123"}}}
        """);

    await transport.WaitForClientMessageCountAsync(4);
    transport.ServerSend(
        """
        {"id":2,"result":{"turn":{"id":"turn_456"}}}
        """);

    await WaitUntilAsync(() => viewModel.IsTurnRunning, "turn running");
    AssertEqual(prompt, viewModel.PromptText, "prompt remains visible while turn runs");

    transport.ServerSend(
        """
        {"method":"error","params":{"error":{"message":"Reconnecting... 5/5","codexErrorInfo":{"responseStreamDisconnected":{"httpStatusCode":401}},"additionalDetails":"unexpected status 401 Unauthorized"},"willRetry":false,"threadId":"thr_123","turnId":"turn_456"}}
        """);
    transport.ServerSend(
        """
        {"method":"turn/completed","params":{"threadId":"thr_123","turn":{"id":"turn_456","status":"failed","error":{"message":"stream disconnected before completion","additionalDetails":"unexpected status 401 Unauthorized"},"items":[]}}}
        """);

    await WaitUntilAsync(() => !viewModel.IsTurnRunning, "turn stopped");

    AssertEqual(prompt, viewModel.PromptText, "prompt is preserved after auth failure");
    AssertTrue(viewModel.StatusMessage.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
        viewModel.StatusMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase), "auth failure status is actionable");

    await viewModel.DisposeAsync();
}

static async Task TestAppServerCancellationSendsInterruptAsync()
{
    await using var transport = new FakeAppServerTransport();
    await using var client = new CodexAppServerClient(transport, TestClientMetadata());
    await CompleteInitializeAsync(client, transport);

    var cancelTask = client.CancelTurnAsync("thr_123", "turn_456");
    await transport.WaitForClientMessageCountAsync(3);

    var cancelRequest = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("turn/interrupt", cancelRequest, "method", "cancel method");
    AssertJsonInt(1, cancelRequest, "id", "cancel id");
    AssertJsonString("thr_123", cancelRequest, "params.threadId", "cancel thread id");
    AssertJsonString("turn_456", cancelRequest, "params.turnId", "cancel turn id");

    transport.ServerSend(
        """
        {"id":1,"result":{"ok":true}}
        """);

    await cancelTask;
}

static async Task TestViewModelCancellationSendsActiveThreadAndTurnAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var projectPath = temp.CreateDirectory("Repo");
    var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);

    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

    viewModel.PromptText = "Run a long task.";
    viewModel.SubmitPromptCommand.Execute(null);

    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend(
        """
        {"id":1,"result":{"thread":{"id":"thr_123"}}}
        """);

    await transport.WaitForClientMessageCountAsync(4);
    transport.ServerSend(
        """
        {"id":2,"result":{"turn":{"id":"turn_456"}}}
        """);

    await WaitUntilAsync(() => viewModel.CancelTurnCommand.CanExecute(null), "cancel enabled");

    viewModel.CancelTurnCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(5);

    var cancelRequest = ParseMessage(transport.ClientMessages[4]);
    AssertJsonString("turn/interrupt", cancelRequest, "method", "view model cancel method");
    AssertJsonString("thr_123", cancelRequest, "params.threadId", "view model cancel thread id");
    AssertJsonString("turn_456", cancelRequest, "params.turnId", "view model cancel turn id");

    transport.ServerSend(
        """
        {"id":3,"result":{"ok":true}}
        """);

    await viewModel.DisposeAsync();
}

static async Task TestViewModelRestoresPersistedThreadAndResumesItAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var projectPath = temp.CreateDirectory("Repo");
    var settings = new AppSettings();
    settings.ProjectThreads.Add(new ProjectThreadState
    {
        ProjectPath = projectPath,
        ThreadId = "thr_existing",
        FinalResponse = "Earlier answer",
        TimelineItems =
        [
            new CodexTimelineItem(
                CodexTimelineItemKind.AgentMessage,
                "Item completed",
                "Earlier answer",
                "item/completed",
                DateTimeOffset.UtcNow)
        ],
        RawEvents = ["item/completed: {}"],
        UpdatedAt = DateTimeOffset.UtcNow
    });
    var settingsStore = new FakeSettingsStore(settings);
    var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => string.Equals(viewModel.FinalResponse, "Earlier answer", StringComparison.Ordinal), "thread snapshot restore");

    viewModel.PromptText = "Continue the same thread.";
    viewModel.SubmitPromptCommand.Execute(null);

    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    await transport.WaitForClientMessageCountAsync(3);
    var resumeRequest = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("thread/resume", resumeRequest, "method", "view model resume method");
    AssertJsonString("thr_existing", resumeRequest, "params.threadId", "view model resume thread id");
    AssertJsonString(projectPath, resumeRequest, "params.cwd", "view model resume cwd");

    transport.ServerSend(
        """
        {"id":1,"result":{"thread":{"id":"thr_existing"}}}
        """);

    await transport.WaitForClientMessageCountAsync(4);
    var turnRequest = ParseMessage(transport.ClientMessages[3]);
    AssertJsonString("turn/start", turnRequest, "method", "view model turn method");
    AssertJsonString("thr_existing", turnRequest, "params.threadId", "view model turn thread id");

    transport.ServerSend(
        """
        {"id":2,"result":{"turn":{"id":"turn_456"}}}
        """);
    transport.ServerSend(
        """
        {"method":"item/completed","params":{"item":{"type":"agentMessage","text":"Updated answer"},"threadId":"thr_existing","turnId":"turn_456"}}
        """);
    transport.ServerSend(
        """
        {"method":"turn/completed","params":{"threadId":"thr_existing","turn":{"id":"turn_456","status":"completed","items":[]}}}
        """);

    await WaitUntilAsync(() => settingsStore.SavedSettings.ProjectThreads.Any(thread =>
        string.Equals(thread.ThreadId, "thr_existing", StringComparison.Ordinal) &&
        string.Equals(thread.FinalResponse, "Updated answer", StringComparison.Ordinal)), "thread snapshot save");

    await viewModel.DisposeAsync();
}

static async Task TestViewModelSendsSelectedModelAndReasoningAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var projectPath = temp.CreateDirectory("Repo");
    var settingsStore = new FakeSettingsStore();
    var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

    viewModel.ModelOverride = "gpt-test";
    viewModel.ReasoningEffortOverride = "xhigh";
    viewModel.PromptText = "Use selected overrides.";
    viewModel.SubmitPromptCommand.Execute(null);

    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend(
        """
        {"id":1,"result":{"thread":{"id":"thr_123"}}}
        """);

    await transport.WaitForClientMessageCountAsync(4);
    var turnRequest = ParseMessage(transport.ClientMessages[3]);
    AssertJsonString("gpt-test", turnRequest, "params.model", "view model model override");
    AssertJsonString("xhigh", turnRequest, "params.effort", "view model reasoning effort");

    transport.ServerSend(
        """
        {"id":2,"result":{"turn":{"id":"turn_456"}}}
        """);
    transport.ServerSend(
        """
        {"method":"turn/completed","params":{"threadId":"thr_123","turn":{"id":"turn_456","status":"completed","items":[]}}}
        """);

    await WaitUntilAsync(() => string.Equals(settingsStore.SavedSettings.LastModelOverride, "gpt-test", StringComparison.Ordinal) &&
        string.Equals(settingsStore.SavedSettings.LastReasoningEffortOverride, "xhigh", StringComparison.Ordinal), "override save");

    await viewModel.DisposeAsync();
}

static async Task TestViewModelLoadsModelOptionsAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var projectPath = temp.CreateDirectory("Repo");
    var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);

    await viewModel.InitializeAsync();
    viewModel.LoadModelsCommand.Execute(null);

    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    await transport.WaitForClientMessageCountAsync(3);
    var modelRequest = ParseMessage(transport.ClientMessages[2]);
    AssertJsonString("model/list", modelRequest, "method", "view model model list method");

    transport.ServerSend(
        """
        {"id":1,"result":{"data":[{"id":"default","model":"gpt-default","displayName":"GPT Default","isDefault":true,"supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"Balanced"}]},{"id":"default-duplicate","model":"gpt-default","displayName":"GPT Default Duplicate","isDefault":false,"supportedReasoningEfforts":[]},{"id":"fast","model":"gpt-fast","displayName":"GPT Fast","isDefault":false,"supportedReasoningEfforts":[{"reasoningEffort":"minimal","description":"Fast"}]}]}}
        """);

    await WaitUntilAsync(() => viewModel.ModelOptions.Count == 2, "model options load");
    AssertTrue(viewModel.ModelOptions.Contains("gpt-default"), "default model option");
    AssertTrue(viewModel.ModelOptions.Contains("gpt-fast"), "fast model option");

    await viewModel.DisposeAsync();
}

static async Task TestViewModelExitCommandRequestsCloseAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var projectPath = temp.CreateDirectory("Repo");
    var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);
    var requested = false;
    viewModel.CloseRequested += (_, _) => requested = true;

    viewModel.ExitApplicationCommand.Execute(null);

    await WaitUntilAsync(() => requested, "close requested");
    await viewModel.DisposeAsync();
}

static async Task TestViewModelShutdownCancelsRunningTurnAndDisposesTransportAsync()
{
    using var temp = TempWorkspace.Create();
    await using var transport = new FakeAppServerTransport();
    var projectPath = temp.CreateDirectory("Repo");
    var settingsStore = new FakeSettingsStore();
    var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

    viewModel.PromptText = "Run until shutdown.";
    viewModel.SubmitPromptCommand.Execute(null);

    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);

    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend(
        """
        {"id":1,"result":{"thread":{"id":"thr_123"}}}
        """);

    await transport.WaitForClientMessageCountAsync(4);
    transport.ServerSend(
        """
        {"id":2,"result":{"turn":{"id":"turn_456"}}}
        """);

    await WaitUntilAsync(() => viewModel.IsTurnRunning, "turn running");

    var shutdownTask = viewModel.ShutdownAsync();
    await transport.WaitForClientMessageCountAsync(5);

    var cancelRequest = ParseMessage(transport.ClientMessages[4]);
    AssertJsonString("turn/interrupt", cancelRequest, "method", "shutdown cancel method");
    AssertJsonString("thr_123", cancelRequest, "params.threadId", "shutdown cancel thread id");
    AssertJsonString("turn_456", cancelRequest, "params.turnId", "shutdown cancel turn id");

    transport.ServerSend(
        """
        {"id":3,"result":{"ok":true}}
        """);

    await shutdownTask;

    AssertTrue(!viewModel.IsTurnRunning, "shutdown clears running flag");
    AssertTrue(transport.IsDisposed, "shutdown disposes transport");
    AssertEqual("thr_123", settingsStore.SavedSettings.ProjectThreads.Single().ThreadId, "shutdown saves thread id");
}

static async Task TestGitServiceReadsStatusAndDiffsAsync()
{
    using var temp = TempWorkspace.Create();
    var repository = temp.CreateDirectory("Repo with spaces");
    await InitializeGitRepositoryAsync(repository);
    var trackedPath = Path.Combine(repository, "tracked file.txt");
    await File.WriteAllTextAsync(trackedPath, "original\n");
    await RunGitAsync(repository, "add", "--", "tracked file.txt");
    await RunGitAsync(repository, "commit", "-m", "initial");

    await File.WriteAllTextAsync(trackedPath, "original\nworking change\n");
    await File.WriteAllTextAsync(Path.Combine(repository, "new file.txt"), "new content\n");

    var service = new GitService(new TestLogger());
    var state = await service.GetRepositoryStateAsync(repository);

    AssertTrue(state.IsRepository, "git repository detected");
    AssertEqual(Path.GetFullPath(repository), state.RootPath, "git repository root");
    AssertEqual(2, state.ChangedFiles.Count, "git changed file count");
    var tracked = state.ChangedFiles.Single(file => file.Path == "tracked file.txt");
    var untracked = state.ChangedFiles.Single(file => file.Path == "new file.txt");
    AssertTrue(tracked.HasWorkingTreeChanges, "tracked working-tree change");
    AssertTrue(untracked.IsUntracked, "untracked status");

    var trackedDiff = await service.GetDiffAsync(repository, tracked, staged: false);
    var untrackedDiff = await service.GetDiffAsync(repository, untracked, staged: false);
    AssertTrue(trackedDiff.Contains("+working change", StringComparison.Ordinal), "tracked diff content");
    AssertTrue(untrackedDiff.Contains("+new content", StringComparison.Ordinal), "untracked diff content");

    File.Delete(Path.Combine(repository, "new file.txt"));
    await RunGitAsync(repository, "restore", "--", "tracked file.txt");
    File.Move(trackedPath, Path.Combine(repository, "renamed file.txt"));
    await RunGitAsync(repository, "add", "-A");
    var rename = (await service.GetRepositoryStateAsync(repository)).ChangedFiles.Single();
    AssertEqual("renamed file.txt", rename.Path, "rename destination path");
    AssertEqual("tracked file.txt", rename.OriginalPath, "rename original path");
}

static async Task TestGitServiceStagesCommitsAndRevertsAsync()
{
    using var temp = TempWorkspace.Create();
    var service = new GitService(new TestLogger());
    var unbornRepository = temp.CreateDirectory("UnbornRepo");
    await InitializeGitRepositoryAsync(unbornRepository);
    var firstFilePath = Path.Combine(unbornRepository, "first.txt");
    await File.WriteAllTextAsync(firstFilePath, "first commit candidate\n");
    await service.StageAsync(unbornRepository, ["first.txt"]);
    await service.UnstageAsync(unbornRepository, ["first.txt"]);
    AssertTrue((await service.GetRepositoryStateAsync(unbornRepository)).ChangedFiles.Single().IsUntracked, "file unstaged before first commit");
    await service.StageAsync(unbornRepository, ["first.txt"]);
    await service.RevertAsync(unbornRepository, (await service.GetRepositoryStateAsync(unbornRepository)).ChangedFiles);
    AssertTrue(!File.Exists(firstFilePath), "confirmed discard removes staged file before first commit");

    var repository = temp.CreateDirectory("Repo");
    await InitializeGitRepositoryAsync(repository);
    var trackedPath = Path.Combine(repository, "tracked.txt");
    await File.WriteAllTextAsync(trackedPath, "original\n");
    await RunGitAsync(repository, "add", "--", "tracked.txt");
    await RunGitAsync(repository, "commit", "-m", "initial");

    await File.WriteAllTextAsync(trackedPath, "committed change\n");
    await service.StageAsync(repository, ["tracked.txt"]);
    var stagedState = await service.GetRepositoryStateAsync(repository);
    var stagedFile = stagedState.ChangedFiles.Single();
    AssertTrue(stagedFile.IsStaged, "file staged");
    AssertTrue((await service.GetDiffAsync(repository, stagedFile, staged: true)).Contains("+committed change", StringComparison.Ordinal), "staged diff content");

    await service.UnstageAsync(repository, ["tracked.txt"]);
    var unstagedState = await service.GetRepositoryStateAsync(repository);
    AssertTrue(!unstagedState.ChangedFiles.Single().IsStaged, "file unstaged");

    await service.StageAsync(repository, ["tracked.txt"]);
    var commit = await service.CommitAsync(repository, "phase two commit");
    AssertTrue(!string.IsNullOrWhiteSpace(commit.CommitId), "commit id returned");
    AssertEqual(0, (await service.GetRepositoryStateAsync(repository)).ChangedFiles.Count, "working tree clean after commit");

    await File.WriteAllTextAsync(trackedPath, "discard me\n");
    var untrackedPath = Path.Combine(repository, "discard-new.txt");
    await File.WriteAllTextAsync(untrackedPath, "discard me too\n");
    var stagedNewPath = Path.Combine(repository, "staged-new.txt");
    await File.WriteAllTextAsync(stagedNewPath, "staged then discarded\n");
    await service.StageAsync(repository, ["staged-new.txt"]);
    var dirtyState = await service.GetRepositoryStateAsync(repository);
    await service.RevertAsync(repository, dirtyState.ChangedFiles);

    AssertEqual("committed change\n", (await File.ReadAllTextAsync(trackedPath)).Replace("\r\n", "\n"), "tracked file restored");
    AssertTrue(!File.Exists(untrackedPath), "untracked file deleted after confirmed service call");
    AssertTrue(!File.Exists(stagedNewPath), "staged new file deleted after confirmed service call");
    AssertEqual(0, (await service.GetRepositoryStateAsync(repository)).ChangedFiles.Count, "working tree clean after revert");
}

static async Task TestGitServiceRefusesNonRepositoryFoldersAsync()
{
    using var temp = TempWorkspace.Create();
    var service = new GitService(new TestLogger());
    var state = await service.GetRepositoryStateAsync(temp.Root);

    AssertTrue(!state.IsRepository, "non-repository rejected");
    AssertEqual(0, state.ChangedFiles.Count, "non-repository has no changes");

    var actionRefused = false;
    try
    {
        await service.StageAsync(temp.Root, ["outside.txt"]);
    }
    catch (InvalidOperationException)
    {
        actionRefused = true;
    }

    AssertTrue(actionRefused, "git action outside repository refused");
}

static async Task TestWorktreeServiceCreatesIsolatedSiblingAsync()
{
    using var temp = TempWorkspace.Create();
    var repository = await CreateCommittedRepositoryAsync(temp, "Primary Repo");
    var service = new WorktreeService(new TestLogger());

    var worktree = await service.CreateAsync(new WorktreeCreateRequest(repository, "Fix unsafe: path?", "thr_isolated"));

    var expectedContainer = Path.Combine(temp.Root, "Primary Repo.worktrees");
    AssertTrue(worktree.Path.StartsWith(expectedContainer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "sibling worktree layout");
    AssertTrue(worktree.Branch.StartsWith("codex/", StringComparison.Ordinal), "assistant branch prefix");
    AssertEqual("thr_isolated", worktree.ThreadId, "worktree thread association");

    await File.WriteAllTextAsync(Path.Combine(worktree.Path, "isolated.txt"), "worktree only\n");
    AssertTrue(!File.Exists(Path.Combine(repository, "isolated.txt")), "main checkout remains unchanged");

    File.Delete(Path.Combine(worktree.Path, "isolated.txt"));
    await service.RemoveAsync(repository, worktree.Path);
}

static async Task TestWorktreeServiceListsOnlyAssistantWorktreesAsync()
{
    using var temp = TempWorkspace.Create();
    var repository = await CreateCommittedRepositoryAsync(temp, "Repo");
    var service = new WorktreeService(new TestLogger());
    var owned = await service.CreateAsync(new WorktreeCreateRequest(repository, "assistant task", "thr_owned"));
    var userPath = Path.Combine(temp.Root, "user-worktree");
    await RunGitAsync(repository, "worktree", "add", "-b", "user/worktree", userPath, "HEAD");

    var listed = await service.ListAsync(repository);

    AssertEqual(1, listed.Count, "only assistant worktree listed");
    AssertEqual(Path.GetFullPath(owned.Path), listed[0].Path, "assistant worktree path listed");

    await RunGitAsync(repository, "worktree", "remove", userPath);
    await service.RemoveAsync(repository, owned.Path);
}

static async Task TestWorktreeServiceRefusesUnownedCleanupAsync()
{
    using var temp = TempWorkspace.Create();
    var repository = await CreateCommittedRepositoryAsync(temp, "Repo");
    var service = new WorktreeService(new TestLogger());
    var userPath = Path.Combine(temp.Root, "user-worktree");
    await RunGitAsync(repository, "worktree", "add", "-b", "user/worktree", userPath, "HEAD");

    var refused = false;
    try
    {
        await service.RemoveAsync(repository, userPath);
    }
    catch (InvalidOperationException ex)
    {
        refused = ex.Message.Contains("assistant-created", StringComparison.OrdinalIgnoreCase);
    }

    AssertTrue(refused, "unowned worktree cleanup refused");
    AssertTrue(Directory.Exists(userPath), "user worktree remains present");
    await RunGitAsync(repository, "worktree", "remove", userPath);
}

static async Task TestWorktreeServiceRemovesOwnedCleanWorktreeAsync()
{
    using var temp = TempWorkspace.Create();
    var repository = await CreateCommittedRepositoryAsync(temp, "Repo");
    var service = new WorktreeService(new TestLogger());
    var owned = await service.CreateAsync(new WorktreeCreateRequest(repository, "completed task", "thr_done"));

    await service.RemoveAsync(repository, owned.Path);

    AssertTrue(!Directory.Exists(owned.Path), "owned worktree directory removed");
    AssertEqual(0, (await service.ListAsync(repository)).Count, "ownership record removed");
}

static async Task TestViewModelStartsWorktreeTaskInIsolatedCwdAsync()
{
    using var temp = TempWorkspace.Create();
    var repository = temp.CreateDirectory("Repo");
    var worktreePath = temp.CreateDirectory("Repo.worktrees\\thr-worktree");
    var transport = new FakeAppServerTransport();
    var worktrees = new FakeWorktreeService(repository, worktreePath);
    var viewModel = CreateMainViewModel(
        transport,
        repository,
        AuthReadiness.LikelySignedIn,
        worktreeService: worktrees);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "worktree project selected");

    viewModel.NewThreadWorkspaceMode = "New worktree";
    viewModel.PromptText = "Make an isolated change.";
    viewModel.SubmitPromptCommand.Execute(null);
    await transport.WaitForClientMessageCountAsync(1);
    transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
    await transport.WaitForClientMessageCountAsync(3);
    transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_worktree"}}}""");
    await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "thr_worktree", "worktree thread created");

    AssertEqual("worktree", viewModel.SelectedThread!.Mode, "thread worktree mode");
    AssertEqual(Path.GetFullPath(worktreePath), viewModel.ActiveWorkspacePath, "active worktree path label");
    await transport.WaitForClientMessageCountAsync(4);
    var startTurn = ParseMessage(transport.ClientMessages[3]);

    AssertJsonString(Path.GetFullPath(worktreePath), startTurn, "params.cwd", "worktree turn cwd");
    transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_worktree"}}}""");
    transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_worktree","turn":{"id":"turn_worktree","status":"completed"}}}""");
    await WaitUntilAsync(() => !viewModel.IsTurnRunning, "worktree turn completed");
    await viewModel.DisposeAsync();
}

static async Task TestConPtyTerminalStartsInRequestedCwdAsync()
{
    using var temp = TempWorkspace.Create();
    var output = new StringBuilder();
    var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var service = new WindowsConPtyTerminalService(new TestLogger());
    await using var session = await service.StartSessionAsync(new TerminalStartRequest(temp.Root, 100, 30));
    session.OutputReceived += (_, args) => output.Append(args.Text);
    session.Exited += (_, args) => exited.TrySetResult(args.ExitCode);

    await session.WriteInputAsync("Write-Output 'PHASE5_CONPTY_OK'; Write-Output (Get-Location).Path; exit\r\n");
    var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));

    AssertEqual(0, exitCode, "PowerShell terminal exit code");
    AssertTrue(output.ToString().Contains("PHASE5_CONPTY_OK", StringComparison.Ordinal), "PowerShell output streamed");
    AssertTrue(output.ToString().Contains(temp.Root, StringComparison.OrdinalIgnoreCase), "PowerShell terminal cwd");
}

static async Task TestViewModelStartsTerminalInActiveWorktreeAsync()
{
    using var temp = TempWorkspace.Create();
    var project = temp.CreateDirectory("Repo");
    var worktree = temp.CreateDirectory("Repo.worktrees\\terminal-thread");
    var settings = new AppSettings
    {
        ProjectThreads =
        [
            new ProjectThreadState
            {
                ProjectPath = project,
                ThreadId = "thr_terminal",
                Title = "Terminal thread",
                IsActive = true,
                Mode = "worktree",
                WorkspacePath = worktree
            }
        ]
    };
    var terminals = new FakeTerminalService();
    var viewModel = CreateMainViewModel(
        new FakeAppServerTransport(),
        project,
        AuthReadiness.LikelySignedIn,
        new FakeSettingsStore(settings),
        terminalService: terminals);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "thr_terminal", "terminal worktree thread selected");

    viewModel.StartTerminalCommand.Execute(null);
    await WaitUntilAsync(() => terminals.StartRequests.Count == 1, "terminal session started");

    AssertEqual(Path.GetFullPath(worktree), terminals.StartRequests[0].WorkingDirectory, "terminal worktree cwd");
    AssertEqual(Path.GetFullPath(worktree), viewModel.TerminalWorkingDirectory, "terminal cwd indicator");
    await viewModel.DisposeAsync();
}

static async Task TestViewModelKeepsTerminalOutputIsolatedByThreadAsync()
{
    using var temp = TempWorkspace.Create();
    var project = temp.CreateDirectory("Repo");
    var settings = new AppSettings
    {
        ProjectThreads =
        [
            new ProjectThreadState { ProjectPath = project, ThreadId = "thr_one", Title = "One", IsActive = true, WorkspacePath = project },
            new ProjectThreadState { ProjectPath = project, ThreadId = "thr_two", Title = "Two", WorkspacePath = project }
        ]
    };
    var terminals = new FakeTerminalService();
    var viewModel = CreateMainViewModel(
        new FakeAppServerTransport(),
        project,
        AuthReadiness.LikelySignedIn,
        new FakeSettingsStore(settings),
        terminalService: terminals);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedThread?.ThreadId == "thr_one", "first terminal thread selected");
    viewModel.StartTerminalCommand.Execute(null);
    await WaitUntilAsync(() => terminals.Sessions.Count == 1, "first terminal started");
    terminals.Sessions[0].EmitOutput("ONE_OUTPUT");
    await WaitUntilAsync(() => viewModel.TerminalOutput.Contains("ONE_OUTPUT", StringComparison.Ordinal), "first terminal output shown");

    viewModel.SelectedThread = viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_two");
    viewModel.StartTerminalCommand.Execute(null);
    await WaitUntilAsync(() => terminals.Sessions.Count == 2, "second terminal started");
    terminals.Sessions[1].EmitOutput("TWO_OUTPUT");
    await WaitUntilAsync(() => viewModel.TerminalOutput.Contains("TWO_OUTPUT", StringComparison.Ordinal), "second terminal output shown");
    AssertTrue(!viewModel.TerminalOutput.Contains("ONE_OUTPUT", StringComparison.Ordinal), "first output hidden from second thread");

    viewModel.SelectedThread = viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_one");
    AssertTrue(viewModel.TerminalOutput.Contains("ONE_OUTPUT", StringComparison.Ordinal), "first output restored");
    AssertTrue(!viewModel.TerminalOutput.Contains("TWO_OUTPUT", StringComparison.Ordinal), "second output isolated");
    await viewModel.DisposeAsync();
}

static async Task TestViewModelTerminalActionsAndShutdownOwnSessionsAsync()
{
    using var temp = TempWorkspace.Create();
    var terminals = new FakeTerminalService();
    var viewModel = CreateMainViewModel(
        new FakeAppServerTransport(),
        temp.Root,
        AuthReadiness.LikelySignedIn,
        terminalService: terminals);
    await viewModel.InitializeAsync();
    viewModel.BrowseProjectCommand.Execute(null);
    await WaitUntilAsync(() => viewModel.SelectedProjectPath is not null, "terminal project selected");
    viewModel.StartTerminalCommand.Execute(null);
    await WaitUntilAsync(() => terminals.Sessions.Count == 1, "project terminal started");

    viewModel.TerminalInput = "Get-Date";
    viewModel.SendTerminalInputCommand.Execute(null);
    await WaitUntilAsync(() => terminals.Sessions[0].Inputs.Count == 1, "terminal input sent");
    AssertEqual("Get-Date\r\n", terminals.Sessions[0].Inputs[0], "terminal command newline");
    terminals.Sessions[0].EmitOutput("CLEAR_ME");
    await WaitUntilAsync(() => viewModel.TerminalOutput.Contains("CLEAR_ME", StringComparison.Ordinal), "terminal output before clear");
    viewModel.ClearTerminalCommand.Execute(null);
    AssertEqual(string.Empty, viewModel.TerminalOutput, "terminal output cleared");

    viewModel.KillTerminalCommand.Execute(null);
    await WaitUntilAsync(() => terminals.Sessions[0].StopCount == 1, "terminal killed");
    await viewModel.DisposeAsync();
    AssertTrue(terminals.Sessions.All(session => session.IsDisposed), "all terminal sessions disposed on shutdown");
}

static async Task<string> CreateCommittedRepositoryAsync(TempWorkspace temp, string name)
{
    var repository = temp.CreateDirectory(name);
    await InitializeGitRepositoryAsync(repository);
    await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "initial\n");
    await RunGitAsync(repository, "add", "--", "README.md");
    await RunGitAsync(repository, "commit", "-m", "initial");
    return repository;
}

static async Task InitializeGitRepositoryAsync(string repository)
{
    await RunGitAsync(repository, "init", "-b", "main");
    await RunGitAsync(repository, "config", "user.name", "Native Codex Assistant Tests");
    await RunGitAsync(repository, "config", "user.email", "tests@example.invalid");
}

static async Task RunGitAsync(string workingDirectory, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }
    };
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    process.Start();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {await error}");
    }

    await output;
}

static async Task TestLiveCodexAppServerInitializesWhenEnabledAsync()
{
    if (!string.Equals(Environment.GetEnvironmentVariable("NCA_RUN_LIVE_CODEX_SMOKE"), "1", StringComparison.Ordinal))
    {
        Console.WriteLine("SKIP live codex app-server smoke test; set NCA_RUN_LIVE_CODEX_SMOKE=1 to run it.");
        return;
    }

    var logger = new TestLogger();
    var discovery = new CodexDiscoveryService(logger);
    var installation = await discovery.DetectAsync();

    AssertTrue(installation.IsFound, "live codex installation is found");
    AssertTrue(!string.IsNullOrWhiteSpace(installation.ExecutablePath), "live codex executable path");
    AssertTrue(!string.IsNullOrWhiteSpace(installation.Version), "live codex version");

    var processService = new CodexProcessService(logger);
    var transport = await processService.StartAppServerTransportAsync(installation);
    await using var client = new CodexAppServerClient(
        transport,
        new CodexAppServerClientMetadata("native_codex_assistant_test", "Native Codex Assistant Test", "0.1.0"));

    var session = await client.InitializeAsync();

    AssertEqual("windows", session.PlatformFamily, "live app-server platform family");
    AssertEqual("windows", session.PlatformOs, "live app-server platform os");
    AssertTrue(session.UserAgent?.Contains("native_codex_assistant_test", StringComparison.Ordinal) == true, "live app-server user agent includes client");

    var thread = await client.StartThreadAsync(CodexThreadStartOptions.Default);
    AssertTrue(!string.IsNullOrWhiteSpace(thread.ThreadId), "live app-server thread id");

    if (string.Equals(Environment.GetEnvironmentVariable("NCA_RUN_LIVE_CODEX_TURN_SMOKE"), "1", StringComparison.Ordinal))
    {
        using var temp = TempWorkspace.Create();
        var models = await client.ListModelsAsync();
        var requestedModel = Environment.GetEnvironmentVariable("NCA_LIVE_CODEX_MODEL");
        var liveModel = !string.IsNullOrWhiteSpace(requestedModel)
            ? requestedModel
            : models.FirstOrDefault(model => string.Equals(model.Model, "gpt-5.4", StringComparison.OrdinalIgnoreCase))?.Model
              ?? models.FirstOrDefault(model => !model.Model.Contains("5.6", StringComparison.OrdinalIgnoreCase))?.Model;
        Console.WriteLine($"LIVE model options: {string.Join(", ", models.Select(model => model.Model))}");
        Console.WriteLine($"LIVE selected model: {liveModel ?? "Codex default"}");
        var threadService = new CodexThreadService();
        var turnCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, notification) =>
        {
            threadService.ApplyNotification(notification);
            if (notification.Method == "turn/completed")
            {
                turnCompleted.TrySetResult();
            }
        };

        var turn = await client.StartTurnAsync(new CodexTurnStartRequest(
            thread.ThreadId,
            "Reply with exactly PHASE1_SMOKE_OK. Do not edit files or run commands.",
            temp.Root,
            CodexSandbox.WorkspaceWrite,
            liveModel));

        AssertTrue(!string.IsNullOrWhiteSpace(turn.TurnId), "live app-server turn id");
        await turnCompleted.Task.WaitAsync(TimeSpan.FromMinutes(3));
        AssertEqual(
            CodexTurnStatus.Completed,
            threadService.ActiveTurnStatus,
            $"live app-server turn completed; detail: {threadService.LastErrorDetail ?? "none"}");
    }
}

static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
{
    var initializeTask = client.InitializeAsync();
    await transport.WaitForClientMessageCountAsync(2);
    transport.ServerSend(
        """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);
    await initializeTask;
}

static CodexAppServerClientMetadata TestClientMetadata()
{
    return new CodexAppServerClientMetadata("native_codex_assistant", "Native Codex Assistant", "0.1.0");
}

static AppServerNotification Notification(string method, string jsonParams)
{
    return new AppServerNotification(method, JsonNode.Parse(jsonParams)!.AsObject());
}

static JsonObject ParseMessage(string line)
{
    var node = JsonNode.Parse(line) as JsonObject;
    if (node is null)
    {
        throw new InvalidOperationException($"Message was not a JSON object: {line}");
    }

    return node;
}

static MainViewModel CreateMainViewModel(
    FakeAppServerTransport transport,
    string projectPath,
    AuthReadiness readiness,
    FakeSettingsStore? settingsStore = null,
    IThemeService? themeService = null,
    ICodexCliUtilityRunner? cliUtilityRunner = null,
    ICodexProcessService? processService = null,
    IWorktreeService? worktreeService = null,
    ITerminalService? terminalService = null)
{
    var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");
    return new MainViewModel(
        settingsStore ?? new FakeSettingsStore(),
        new FakeCodexDiscoveryService(installation),
        processService ?? new FakeCodexProcessService(transport),
        new FakeAuthService(new AuthenticationState(readiness, readiness.ToString(), "Test auth state.", @"C:\Users\Test\.codex")),
        new FakeGitService(projectPath),
        worktreeService ?? new FakeWorktreeService(projectPath, Path.Combine(projectPath, ".test-worktree")),
        new RecentProjectService(),
        new FakeFolderPicker(projectPath),
        new FakeUserInteractionService(),
        themeService ?? new FakeThemeService(),
        cliUtilityRunner ?? new FakeCodexCliUtilityRunner(),
        new ThreadStore(),
        new CodexThreadWorkspace(),
        terminalService ?? new FakeTerminalService(),
        new TestLogger());
}

static async Task WaitUntilAsync(Func<bool> condition, string label)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition())
    {
        await Task.Delay(20, timeout.Token);
    }
}

static void AssertJsonString(string expected, JsonNode node, string path, string label)
{
    var actualNode = ResolvePath(node, path);
    AssertTrue(actualNode is not null, $"{label} exists");
    AssertEqual(expected, actualNode!.GetValue<string>(), label);
}

static void AssertJsonInt(int expected, JsonNode node, string path, string label)
{
    var actualNode = ResolvePath(node, path);
    AssertTrue(actualNode is not null, $"{label} exists");
    AssertEqual(expected, actualNode!.GetValue<int>(), label);
}

static JsonNode? ResolvePath(JsonNode node, string path)
{
    var current = node;
    foreach (var segment in path.Split('.'))
    {
        if (current is JsonObject obj)
        {
            current = obj[segment];
            continue;
        }

        if (current is JsonArray array && int.TryParse(segment, out var index))
        {
            current = index >= 0 && index < array.Count ? array[index] : null;
            continue;
        }

        return null;
    }

    return current;
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{label}: expected true.");
    }
}

internal sealed class TempWorkspace : IDisposable
{
    private TempWorkspace(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public static TempWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "NativeCodexAssistant.Tests", Guid.NewGuid().ToString("N"));
        return new TempWorkspace(root);
    }

    public string CreateDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class TestLogger : IAppLogger
{
    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? properties = null,
        Exception? exception = null)
    {
    }
}

internal sealed class FakeSettingsStore : ISettingsStore
{
    public FakeSettingsStore(AppSettings? initialSettings = null)
    {
        SavedSettings = initialSettings ?? new AppSettings();
    }

    public string SettingsPath => Path.Combine(Path.GetTempPath(), "NativeCodexAssistant.Tests", "settings.json");

    public AppSettings SavedSettings { get; private set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SavedSettings);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SavedSettings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class FakeCodexDiscoveryService(CodexInstallation installation) : ICodexDiscoveryService
{
    public Task<CodexInstallation> DetectAsync(string? preferredExecutablePath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(installation);
    }
}

internal sealed class FakeCodexProcessService(FakeAppServerTransport transport) : ICodexProcessService
{
    public Task<IAppServerTransport> StartAppServerTransportAsync(CodexInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAppServerTransport>(transport);
    }
}

internal sealed class SequenceCodexProcessService(params FakeAppServerTransport[] transports) : ICodexProcessService
{
    private int nextTransport;

    public int StartCount { get; private set; }

    public Task<IAppServerTransport> StartAppServerTransportAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (nextTransport >= transports.Length)
        {
            throw new InvalidOperationException("No fake app-server transport remains.");
        }

        StartCount++;
        return Task.FromResult<IAppServerTransport>(transports[nextTransport++]);
    }
}

internal sealed class FakeAuthService(AuthenticationState state) : IAuthService
{
    public Task<AuthenticationState> GetAuthenticationStateAsync(CodexInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state);
    }

    public Task<bool> StartLoginAsync(CodexInstallation installation, LoginMethod method, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public Task<bool> StartLogoutAsync(CodexInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

internal sealed class FakeFolderPicker(string projectPath) : IFolderPicker
{
    public string? PickFolder(string? initialPath = null) => projectPath;
}

internal sealed class FakeGitService(string repositoryRoot) : IGitService
{
    public Task<GitRepositoryState> GetRepositoryStateAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GitRepositoryState(true, repositoryRoot, "main", [], null));
    }

    public Task<string> GetDiffAsync(string repositoryRoot, GitChangedFile file, bool staged, CancellationToken cancellationToken = default) =>
        Task.FromResult("test diff");

    public Task StageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UnstageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RevertAsync(string repositoryRoot, IReadOnlyCollection<GitChangedFile> files, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<GitCommitResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GitCommitResult("abc1234", "test commit"));
}

internal sealed class FakeWorktreeService(string repositoryRoot, string worktreePath) : IWorktreeService
{
    private readonly List<AssistantWorktree> worktrees = [];

    public Task<AssistantWorktree> CreateAsync(
        WorktreeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = new AssistantWorktree(
            Path.GetFullPath(repositoryRoot),
            Path.GetFullPath(worktreePath),
            "codex/test-worktree",
            "test-worktree",
            request.ThreadId,
            DateTimeOffset.UtcNow);
        worktrees.Add(created);
        return Task.FromResult(created);
    }

    public Task<IReadOnlyList<AssistantWorktree>> ListAsync(
        string requestedRepositoryRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AssistantWorktree>>([.. worktrees]);
    }

    public Task RemoveAsync(
        string requestedRepositoryRoot,
        string requestedWorktreePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        worktrees.RemoveAll(item => string.Equals(item.Path, requestedWorktreePath, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }
}

internal sealed class FakeTerminalService : ITerminalService
{
    public List<TerminalStartRequest> StartRequests { get; } = [];

    public List<FakeTerminalSession> Sessions { get; } = [];

    public Task<ITerminalSession> StartSessionAsync(
        TerminalStartRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartRequests.Add(request);
        var session = new FakeTerminalSession(request.WorkingDirectory);
        Sessions.Add(session);
        return Task.FromResult<ITerminalSession>(session);
    }
}

internal sealed class FakeTerminalSession(string workingDirectory) : ITerminalSession
{
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public event EventHandler<TerminalExitedEventArgs>? Exited;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string WorkingDirectory { get; } = Path.GetFullPath(workingDirectory);

    public bool IsRunning { get; private set; } = true;

    public List<string> Inputs { get; } = [];

    public int StopCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task WriteInputAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inputs.Add(text);
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        if (IsRunning)
        {
            IsRunning = false;
            Exited?.Invoke(this, new TerminalExitedEventArgs(0));
        }

        return Task.CompletedTask;
    }

    public void EmitOutput(string text) => OutputReceived?.Invoke(this, new TerminalOutputEventArgs(text));

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        await StopAsync();
        IsDisposed = true;
    }
}

internal sealed class FakeUserInteractionService : IUserInteractionService
{
    public bool ConfirmDestructiveAction(string title, string message) => true;

    public void OpenInEditor(string path)
    {
    }

    public void RevealInExplorer(string path)
    {
    }
}

internal sealed class FakeThemeService : IThemeService
{
    public string AppliedTheme { get; private set; } = string.Empty;

    public void ApplyTheme(string theme)
    {
        AppliedTheme = theme;
    }
}

internal sealed class FakeCodexCliUtilityRunner(CodexCliUtilityResult? result = null) : ICodexCliUtilityRunner
{
    private readonly CodexCliUtilityResult result = result ?? new CodexCliUtilityResult("doctor", 0, "Doctor OK", string.Empty);

    public int RunCount { get; private set; }

    public Task<CodexCliUtilityResult> RunDoctorAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunCount++;
        return Task.FromResult(result);
    }
}

internal sealed class FakeAppServerTransport : IAppServerTransport
{
    private readonly Queue<string> serverMessages = new();
    private readonly SemaphoreSlim serverMessageSignal = new(0);
    private readonly SemaphoreSlim clientMessageSignal = new(0);
    private bool isCompleted;
    private bool isDisposed;
    private Exception? serverFailure;

    public IReadOnlyList<string> ClientMessages => clientMessages;

    public bool IsDisposed => isDisposed;

    private readonly List<string> clientMessages = [];

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        clientMessages.Add(line);
        clientMessageSignal.Release();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!isCompleted)
        {
            await serverMessageSignal.WaitAsync(cancellationToken);

            if (serverFailure is not null)
            {
                throw serverFailure;
            }

            while (serverMessages.Count > 0)
            {
                yield return serverMessages.Dequeue();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isDisposed)
        {
            return Task.CompletedTask;
        }

        isCompleted = true;
        serverMessageSignal.Release();
        return Task.CompletedTask;
    }

    public void ServerSend(string line)
    {
        serverMessages.Enqueue(line);
        serverMessageSignal.Release();
    }

    public void ServerFail(Exception exception)
    {
        serverFailure = exception;
        serverMessageSignal.Release();
    }

    public async Task WaitForClientMessageCountAsync(int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (clientMessages.Count < expectedCount)
        {
            await clientMessageSignal.WaitAsync(timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        await StopAsync();
        isDisposed = true;
        serverMessageSignal.Dispose();
        clientMessageSignal.Dispose();
    }
}

internal sealed class SlowWriteAppServerTransport : IAppServerTransport
{
    private readonly Queue<string> serverMessages = new();
    private readonly SemaphoreSlim serverMessageSignal = new(0);
    private readonly SemaphoreSlim clientMessageSignal = new(0);
    private readonly List<string> clientMessages = [];
    private int activeWrites;
    private bool isCompleted;
    private bool isDisposed;

    public bool OverlappingWriteDetected { get; private set; }

    public bool IsDisposed => isDisposed;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref activeWrites) > 1)
        {
            OverlappingWriteDetected = true;
        }

        try
        {
            await Task.Delay(50, cancellationToken);
            clientMessages.Add(line);
            clientMessageSignal.Release();
        }
        finally
        {
            Interlocked.Decrement(ref activeWrites);
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!isCompleted)
        {
            await serverMessageSignal.WaitAsync(cancellationToken);
            while (serverMessages.Count > 0)
            {
                yield return serverMessages.Dequeue();
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isDisposed)
        {
            return Task.CompletedTask;
        }

        isCompleted = true;
        serverMessageSignal.Release();
        return Task.CompletedTask;
    }

    public void ServerSend(string line)
    {
        serverMessages.Enqueue(line);
        serverMessageSignal.Release();
    }

    public async Task WaitForClientMessageCountAsync(int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (clientMessages.Count < expectedCount)
        {
            await clientMessageSignal.WaitAsync(timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }

        await StopAsync();
        isDisposed = true;
        serverMessageSignal.Dispose();
        clientMessageSignal.Dispose();
    }
}
