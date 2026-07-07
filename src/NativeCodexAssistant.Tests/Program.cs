using NativeCodexAssistant.App.Services;
using NativeCodexAssistant.App.ViewModels;
using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure.Auth;
using NativeCodexAssistant.Infrastructure.Codex;
using NativeCodexAssistant.Infrastructure.Logging;
using NativeCodexAssistant.Infrastructure.Projects;
using NativeCodexAssistant.Infrastructure.Settings;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("recent projects are deduped and capped", TestRecentProjectsAsync),
    ("settings round trip to json", TestSettingsRoundTripAsync),
    ("auth detection reports file cache without reading token", TestAuthDetectionAsync),
    ("codex discovery skips unusable path candidates", TestCodexDiscoverySkipsUnusablePathCandidatesAsync),
    ("codex discovery checks OpenAI local app bin", TestCodexDiscoveryChecksOpenAiLocalAppBinAsync),
    ("app-server client writes initialize handshake", TestAppServerInitializeWritesHandshakeAsync),
    ("app-server client serializes initialize writes", TestAppServerClientSerializesInitializeWritesAsync),
    ("app-server client starts thread and turn", TestAppServerStartsThreadAndTurnAsync),
    ("app-server notifications update thread state", TestAppServerNotificationsUpdateThreadStateAsync),
    ("app-server v2 notifications update thread state", TestAppServerV2NotificationsUpdateThreadStateAsync),
    ("app-server error notifications show detail", TestAppServerErrorNotificationsShowDetailAsync),
    ("view model preserves prompt after auth failed turn", TestViewModelPreservesPromptAfterAuthFailedTurnAsync),
    ("view model cancellation sends active thread and turn", TestViewModelCancellationSendsActiveThreadAndTurnAsync),
    ("app-server cancellation sends turn interrupt", TestAppServerCancellationSendsInterruptAsync),
    ("live codex app-server initializes when enabled", TestLiveCodexAppServerInitializesWhenEnabledAsync)
};

var failures = 0;
foreach (var test in tests)
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
        PreferredCodexPath = @"C:\Tools\codex.exe"
    };
    settings.RecentProjects.Add(new(temp.CreateDirectory("Repo"), "Repo", DateTimeOffset.UtcNow));

    await store.SaveAsync(settings);
    var loaded = await store.LoadAsync();

    AssertEqual("Dark", loaded.Theme, "theme");
    AssertEqual(settings.PreferredCodexPath, loaded.PreferredCodexPath, "preferred codex path");
    AssertEqual(1, loaded.RecentProjects.Count, "recent project count");
    AssertTrue(File.Exists(store.SettingsPath), "settings file exists");
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
            CodexSandbox.WorkspaceWrite));

        AssertTrue(!string.IsNullOrWhiteSpace(turn.TurnId), "live app-server turn id");
        await turnCompleted.Task.WaitAsync(TimeSpan.FromMinutes(3));
        AssertEqual(CodexTurnStatus.Completed, threadService.ActiveTurnStatus, "live app-server turn completed");
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
    AuthReadiness readiness)
{
    var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");
    return new MainViewModel(
        new FakeSettingsStore(),
        new FakeCodexDiscoveryService(installation),
        new FakeCodexProcessService(transport),
        new FakeAuthService(new AuthenticationState(readiness, readiness.ToString(), "Test auth state.", @"C:\Users\Test\.codex")),
        new RecentProjectService(),
        new FakeFolderPicker(projectPath),
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
    public string SettingsPath => Path.Combine(Path.GetTempPath(), "NativeCodexAssistant.Tests", "settings.json");

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AppSettings());
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

internal sealed class FakeAppServerTransport : IAppServerTransport
{
    private readonly Queue<string> serverMessages = new();
    private readonly SemaphoreSlim serverMessageSignal = new(0);
    private readonly SemaphoreSlim clientMessageSignal = new(0);
    private bool isCompleted;
    private bool isDisposed;

    public IReadOnlyList<string> ClientMessages => clientMessages;

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
