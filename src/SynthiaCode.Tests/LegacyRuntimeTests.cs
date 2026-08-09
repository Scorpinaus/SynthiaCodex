using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Terminal;
using SynthiaCode.Core.Worktrees;
using SynthiaCode.Infrastructure.Auth;
using SynthiaCode.Infrastructure.Attachments;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Git;
using SynthiaCode.Infrastructure.Logging;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Settings;
using SynthiaCode.Infrastructure.Terminal;
using SynthiaCode.Infrastructure.Worktrees;
using SynthiaCode.Infrastructure.Workspaces;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Reflection;

public abstract class LegacyRuntimeTestSupport
{
    private protected static void AssertUtf8ProtocolEncoding(Encoding? encoding, string streamName)
    {
        AssertTrue(encoding is not null, $"{streamName} encoding is explicit");
        AssertEqual("utf-8", encoding!.WebName, $"{streamName} encoding");
        AssertEqual(0, encoding.GetPreamble().Length, $"{streamName} BOM length");
        AssertTrue(encoding.DecoderFallback is DecoderExceptionFallback, $"{streamName} rejects invalid UTF-8");
    }

    private protected static async Task<string> CreateCommittedRepositoryAsync(TempWorkspace temp, string name)
    {
        var repository = temp.CreateDirectory(name);
        await InitializeGitRepositoryAsync(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "initial\n");
        await RunGitAsync(repository, "add", "--", "README.md");
        await RunGitAsync(repository, "commit", "-m", "initial");
        return repository;
    }

    private protected static async Task InitializeGitRepositoryAsync(string repository)
    {
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.name", "SynthiaCode Tests");
        await RunGitAsync(repository, "config", "user.email", "tests@example.invalid");
    }

    private protected static async Task RunGitAsync(string workingDirectory, params string[] arguments)
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

    private protected static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initializeTask = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend(
            """
        {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
        """);
        await initializeTask;
    }

    private protected static CodexAppServerClientMetadata TestClientMetadata()
    {
        return new CodexAppServerClientMetadata("synthiacode", "SynthiaCode", "0.1.0");
    }

    private protected static CodexAppServerNotification Notification(string method, string jsonParams)
    {
        return CodexAppServerNotification.Decode(new AppServerNotification(method, JsonNode.Parse(jsonParams)!.AsObject()));
    }

    private protected static JsonObject ParseMessage(string line)
    {
        var node = JsonNode.Parse(line) as JsonObject;
        if (node is null)
        {
            throw new InvalidOperationException($"Message was not a JSON object: {line}");
        }

        return node;
    }

    private protected static MainViewModel CreateMainViewModel(
        FakeAppServerTransport transport,
        string projectPath,
        AuthReadiness readiness,
        FakeSettingsStore? settingsStore = null,
        IThemeService? themeService = null,
        ICodexCliUtilityRunner? cliUtilityRunner = null,
        ICodexProcessService? processService = null,
        IWorktreeService? worktreeService = null,
        ITerminalService? terminalService = null,
        IAppLogger? logger = null,
        IUserInteractionService? userInteractionService = null,
        IAttachmentStore? attachmentStore = null,
        IGitService? gitService = null)
    {
        var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");
        var effectiveLogger = logger ?? new TestLogger();
        var effectiveProcessService = processService ?? new FakeCodexProcessService(transport);
        var appServerSessionCoordinator = new AppServerSessionCoordinator(
            effectiveProcessService,
            effectiveLogger,
            new CodexAppServerClientMetadata("synthiacode_tests", "SynthiaCode Tests", "1.0.0"));
        return WorkspaceActionStubs.CreateMainViewModel(
            settingsStore ?? new FakeSettingsStore(),
            new FakeCodexDiscoveryService(installation),
            appServerSessionCoordinator,
            new FakeAuthService(new AuthenticationState(readiness, readiness.ToString(), "Test auth state.", @"C:\Users\Test\.codex")),
            gitService ?? new FakeGitService(projectPath),
            worktreeService ?? new FakeWorktreeService(projectPath, Path.Combine(projectPath, ".test-worktree")),
            new RecentProjectService(),
            new FakeFolderPicker(projectPath),
            userInteractionService ?? new FakeUserInteractionService(),
            themeService ?? new FakeThemeService(),
            cliUtilityRunner ?? new FakeCodexCliUtilityRunner(),
            new ThreadStore(),
            new CodexThreadWorkspace(),
            terminalService ?? new FakeTerminalService(),
            effectiveLogger,
            new GeneralWorkspaceService(Path.Combine(projectPath, ".synthiacode-test-data")),
            attachmentStore);
    }

    private protected static async Task CompleteAutomaticThreadRenameAsync(
        FakeAppServerTransport transport,
        string threadId)
    {
        var line = await transport.ClientMessageProbe.WaitForAsync(
            message =>
                ResolvePath(ParseMessage(message), "method")?.GetValue<string>() == "thread/name/set" &&
                ResolvePath(ParseMessage(message), "params.threadId")?.GetValue<string>() == threadId,
            $"automatic rename for {threadId}");
        var request = ParseMessage(line);
        var requestId = request["id"]?.ToJsonString()
            ?? throw new InvalidOperationException($"Automatic rename for '{threadId}' did not include an id.");
        transport.ServerSend($"{{\"id\":{requestId},\"result\":{{}}}}");
    }

    private protected static async Task<JsonObject> CompleteQueuedDispatchPreflightAsync(
        FakeAppServerTransport transport,
        string workspacePath,
        int nextMessage,
        Func<string>? describeState = null)
    {
        var modelRequest = await WaitForNextRequestAsync("model/list");
        Respond(
            modelRequest,
            """{"data":[{"id":"gpt-queued","model":"gpt-queued","displayName":"GPT Queued","description":"Queue test model","isDefault":true,"hidden":false,"supportedReasoningEfforts":[],"serviceTiers":[]}]}""");

        var requirementsRequest = await WaitForNextRequestAsync("configRequirements/read");
        Respond(
            requirementsRequest,
            """{"requirements":{"allowedApprovalPolicies":["on-request"],"allowedApprovalsReviewers":["user"],"allowedPermissionProfiles":[":workspace"]}}""");

        var configRequest = await WaitForNextRequestAsync("config/read");
        AssertJsonString(workspacePath, configRequest, "params.cwd", "queued preflight config workspace");
        Respond(
            configRequest,
            """{"config":{"sandbox_mode":"workspace-write","approval_policy":"on-request","approvals_reviewer":"user"},"origins":{}}""");

        var profilesRequest = await WaitForNextRequestAsync("permissionProfile/list");
        AssertJsonString(workspacePath, profilesRequest, "params.cwd", "queued preflight permission-profile workspace");
        Respond(
            profilesRequest,
            """{"data":[{"id":":workspace","description":"Workspace boundary","allowed":true}]}""");

        try
        {
            return await WaitForNextRequestAsync("turn/start");
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException(
                $"Queued dispatch did not send turn/start. {describeState?.Invoke() ?? "No state diagnostics were supplied."}",
                ex);
        }

        void Respond(JsonObject request, string result) =>
            transport.ServerSend($"{{\"id\":{request["id"]!.ToJsonString()},\"result\":{result}}}");

        async Task<JsonObject> WaitForNextRequestAsync(string expectedMethod)
        {
            await transport.WaitForClientMessageCountAsync(nextMessage + 1);
            var request = ParseMessage(transport.ClientMessages[nextMessage++]);
            AssertJsonString(expectedMethod, request, "method", $"queued preflight {expectedMethod} method");
            return request;
        }
    }

    private protected static void AssertJsonString(string expected, JsonNode node, string path, string label)
    {
        var actualNode = ResolvePath(node, path);
        AssertTrue(actualNode is not null, $"{label} exists");
        AssertEqual(expected, actualNode!.GetValue<string>(), label);
    }

    private protected static void AssertJsonInt(int expected, JsonNode node, string path, string label)
    {
        var actualNode = ResolvePath(node, path);
        AssertTrue(actualNode is not null, $"{label} exists");
        AssertEqual(expected, actualNode!.GetValue<int>(), label);
    }

    private protected static JsonNode? ResolvePath(JsonNode node, string path)
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

    private protected static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
        }
    }

    private protected static void AssertTrue(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{label}: expected true.");
        }
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
        var root = Path.Combine(Path.GetTempPath(), "SynthiaCode.Tests", Guid.NewGuid().ToString("N"));
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
    private readonly object syncRoot = new();

    public List<TestLogEntry> Entries { get; } = [];

    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? properties = null,
        Exception? exception = null)
    {
        lock (syncRoot)
        {
            Entries.Add(new TestLogEntry(level, eventName, message, properties, exception));
        }
    }
}

internal sealed record TestLogEntry(
    AppLogLevel Level,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, string?>? Properties,
    Exception? Exception);

internal sealed class FakeSettingsStore : ISettingsStore
{
    public FakeSettingsStore(AppSettings? initialSettings = null)
    {
        SavedSettings = initialSettings ?? new AppSettings();
    }

    public string SettingsPath => Path.Combine(Path.GetTempPath(), "SynthiaCode.Tests", "settings.json");

    public AppSettings SavedSettings { get; private set; }

    public MessageProbe<AppSettings> Saves { get; } = new();

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SavedSettings);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SavedSettings = settings;
        Saves.Publish(settings);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSettingsStore : ISettingsStore
{
    private readonly object syncRoot = new();

    public string SettingsPath => Path.Combine(Path.GetTempPath(), "SynthiaCode.Tests", "recorded-settings.json");

    public int SaveCount { get; private set; }

    public AppSettings SavedSettings { get; private set; } = new();

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            return Task.FromResult(AppSettingsSnapshot.Create(SavedSettings));
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (syncRoot)
        {
            SaveCount++;
            SavedSettings = AppSettingsSnapshot.Create(settings);
        }

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
    public string? CurrentBranch { get; set; } = "main";

    public IReadOnlyList<string> Branches { get; set; } = ["main"];

    public bool HasHead { get; set; } = true;

    public int BranchCatalogRequestCount { get; private set; }

    public Queue<GitBranchCatalog> BranchCatalogResponses { get; } = [];

    public Task<GitRepositoryState> GetRepositoryStateAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GitRepositoryState(true, repositoryRoot, CurrentBranch, [], null));
    }

    public Task<GitReviewCatalog> GetReviewCatalogAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GitReviewCatalog(
            repositoryRoot,
            CurrentBranch ?? string.Empty,
            Branches.Where(branch => !string.Equals(branch, CurrentBranch, StringComparison.Ordinal)).ToArray(),
            []));

    public Task<GitBranchCatalog> GetBranchCatalogAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BranchCatalogRequestCount++;
        if (BranchCatalogResponses.TryDequeue(out var response))
        {
            return Task.FromResult(response);
        }
        return Task.FromResult(new GitBranchCatalog(repositoryRoot, CurrentBranch, Branches, HasHead));
    }

    public Task<string> GetDiffAsync(string repositoryRoot, GitChangedFile file, bool staged, CancellationToken cancellationToken = default) =>
        Task.FromResult("test diff");

    public Task ApplyHunkAsync(string repositoryRoot, GitDiffHunkPatch patch, GitHunkOperation operation, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

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

    public List<WorktreeCreateRequest> CreateRequests { get; } = [];

    public List<(string RepositoryRoot, string WorktreePath)> RemoveRequests { get; } = [];

    public Exception? CreateError { get; set; }

    public Task<AssistantWorktree> CreateAsync(
        WorktreeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateRequests.Add(request);
        if (CreateError is not null)
        {
            return Task.FromException<AssistantWorktree>(CreateError);
        }
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
        RemoveRequests.Add((requestedRepositoryRoot, requestedWorktreePath));
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
    public GeneratedImageEditSelection? ImageEditSelection { get; set; } =
        GeneratedImageEditSelection.EntireImage;

    public string? SelectedImageEditPath { get; private set; }

    public ProjectFolderEditSelection? ProjectFolderSelection { get; set; }

    public CodexReviewTarget? ReviewTargetSelection { get; set; } =
        CodexReviewTarget.UncommittedChanges();

    public string? WorktreeStartPointSelection { get; set; }

    public bool CancelWorktreeStartPointSelection { get; set; }

    public List<GitBranchCatalog> WorktreeBranchCatalogs { get; } = [];

    public ProjectTrustDecision TrustDecision { get; set; } = ProjectTrustDecision.Cancel;

    public List<string> ProjectTrustPromptPaths { get; } = [];

    public bool ConfirmDestructiveAction(string title, string message) => true;

    public string? PromptForText(string title, string message, string initialValue) => null;

    public void OpenInEditor(string path)
    {
    }

    public void OpenExternalUri(Uri uri)
    {
    }

    public void ShowImagePreview(string path)
    {
    }

    public GeneratedImageEditSelection? SelectGeneratedImageEdit(string path)
    {
        SelectedImageEditPath = path;
        return ImageEditSelection;
    }

    public CodexReviewTarget? SelectCodeReviewTarget(GitReviewCatalog catalog) =>
        ReviewTargetSelection;

    public string? SelectWorktreeStartPoint(GitBranchCatalog catalog)
    {
        WorktreeBranchCatalogs.Add(catalog);
        return CancelWorktreeStartPointSelection
            ? null
            : WorktreeStartPointSelection ?? catalog.DefaultStartPoint;
    }

    public ProjectTrustDecision PromptForProjectTrust(string projectPath)
    {
        ProjectTrustPromptPaths.Add(projectPath);
        return TrustDecision;
    }

    public ProjectFolderEditSelection? EditProjectFolders(RecentProject project) =>
        ProjectFolderSelection;

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

internal sealed class InlineTrackingSynchronizationContext : SynchronizationContext
{
    private int sendCount;

    public int SendCount => Volatile.Read(ref sendCount);

    public override void Send(SendOrPostCallback callback, object? state)
    {
        Interlocked.Increment(ref sendCount);
        callback(state);
    }

    public override void Post(SendOrPostCallback callback, object? state) => callback(state);
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

internal sealed class FakeAppServerTransport : IAppServerTransport, ITestSignal
{
    private readonly Queue<string> serverMessages = new();
    private readonly SemaphoreSlim serverMessageSignal = new(0);
    private readonly SemaphoreSlim clientMessageSignal = new(0);
    private bool isCompleted;
    private bool isDisposed;
    private Exception? serverFailure;

    public IReadOnlyList<string> ClientMessages => clientMessages;

    public MessageProbe<string> ClientMessageProbe { get; } = new();

    public event EventHandler? Signaled;

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
        ClientMessageProbe.Publish(line);
        Signaled?.Invoke(this, EventArgs.Empty);
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

    public async Task WaitForClientMessageCountAsync(int expectedCount, TimeSpan? timeoutOverride = null)
    {
        using var timeout = new CancellationTokenSource(timeoutOverride ?? TimeSpan.FromSeconds(15));
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
            await Task.Yield();
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
