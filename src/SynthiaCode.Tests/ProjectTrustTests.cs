using System.Text.Json;
using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Codex.Configuration;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Workspaces;

internal static class ProjectTrustTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("project trust reads remembered trusted and untrusted config", ReadsRememberedTrustAsync),
        ("project trust serializes normalized Windows config key paths", SerializesWindowsTrustPathAsync),
        ("project trust persists and remembers trusted decisions", PersistsAndRemembersTrustedDecisionAsync),
        ("project trust persists and remembers untrusted decisions", PersistsAndRemembersUntrustedDecisionAsync),
        ("project trust cancel and protocol failures fail closed", CancelAndFailuresFailClosedAsync),
        ("project trust gates browse recent and startup activation paths", GatesEveryProjectActivationPathAsync),
        ("project trust cancel preserves selection and settings", CancelPreservesSelectionAndSettingsAsync)
    ];

    private static async Task ReadsRememberedTrustAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await InitializeAsync(client, transport);

        var trustedTask = client.ReadProjectTrustAsync(@"c:\WORK\Alpha\");
        await transport.WaitForClientMessageCountAsync(3);
        var trustedRequest = Parse(transport.ClientMessages[2]);
        AssertEqual("config/read", ReadString(trustedRequest, "method"), "trusted read method");
        Assert(trustedRequest["params"]?["cwd"] is null, "trust read does not supply a project cwd");
        AssertEqual(false, trustedRequest["params"]?["includeLayers"]?.GetValue<bool>(), "trust read excludes project layers");
        Respond(
            transport,
            trustedRequest,
            new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["projects"] = new JsonObject
                    {
                        [@"C:\Work\Alpha"] = new JsonObject { ["trust_level"] = "trusted" }
                    }
                },
                ["origins"] = new JsonObject()
            });
        AssertEqual(CodexProjectTrustLevel.Trusted, await trustedTask, "case-insensitive normalized trusted path");

        var unicodePath = @"C:\Work\项目 Beta";
        var untrustedTask = client.ReadProjectTrustAsync(unicodePath);
        await transport.WaitForClientMessageCountAsync(4);
        var untrustedRequest = Parse(transport.ClientMessages[3]);
        Respond(
            transport,
            untrustedRequest,
            new JsonObject
            {
                ["config"] = new JsonObject
                {
                    ["projects"] = new JsonObject
                    {
                        [unicodePath] = new JsonObject { ["trust_level"] = "untrusted" }
                    }
                },
                ["origins"] = new JsonObject()
            });
        AssertEqual(CodexProjectTrustLevel.Untrusted, await untrustedTask, "unicode untrusted path");
    }

    private static async Task SerializesWindowsTrustPathAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = CreateClient(transport);
        await InitializeAsync(client, transport);

        var writeTask = client.WriteProjectTrustAsync(
            @"C:\Work\Repo.With Space\",
            CodexProjectTrustLevel.Untrusted);
        await transport.WaitForClientMessageCountAsync(3);
        var request = Parse(transport.ClientMessages[2]);
        AssertEqual("config/value/write", ReadString(request, "method"), "trust write method");
        AssertEqual(
            "projects.\"C:\\\\Work\\\\Repo.With Space\".trust_level",
            ReadString(request, "params.keyPath"),
            "normalized TOML-quoted project key path");
        AssertEqual("untrusted", ReadString(request, "params.value"), "trust wire value");
        AssertEqual("upsert", ReadString(request, "params.mergeStrategy"), "trust merge strategy");
        Assert(request["params"]?.AsObject().ContainsKey("filePath") == false, "trust write targets the user config by default");
        Respond(
            transport,
            request,
            new JsonObject
            {
                ["filePath"] = @"C:\Users\Test\.codex\config.toml",
                ["status"] = "ok",
                ["version"] = "1"
            });
        await writeTask;
    }

    private static async Task PersistsAndRemembersTrustedDecisionAsync()
    {
        var session = new FakeProjectTrustSession();
        var interaction = new FakeUserInteractionService { TrustDecision = ProjectTrustDecision.TrustProject };
        var service = new ProjectTrustService(session, interaction, new TestLogger());

        var first = await service.AuthorizeAsync(@"C:\Work\Trusted\", Installation);
        var second = await service.AuthorizeAsync(@"c:\work\TRUSTED", Installation);

        Assert(first.IsAuthorized && first.TrustLevel == CodexProjectTrustLevel.Trusted, "trusted decision authorizes activation");
        Assert(second.IsAuthorized && second.TrustLevel == CodexProjectTrustLevel.Trusted, "remembered trusted decision authorizes activation");
        AssertEqual(1, interaction.ProjectTrustPromptPaths.Count, "trusted decision is prompted once");
        AssertSequenceEqual([CodexProjectTrustLevel.Trusted], session.Writes, "trusted decision is persisted once");
        AssertEqual(@"C:\Work\Trusted", first.NormalizedPath, "trusted path is normalized before prompting");
    }

    private static async Task PersistsAndRemembersUntrustedDecisionAsync()
    {
        var session = new FakeProjectTrustSession();
        var interaction = new FakeUserInteractionService { TrustDecision = ProjectTrustDecision.OpenUntrusted };
        var service = new ProjectTrustService(session, interaction, new TestLogger());

        var first = await service.AuthorizeAsync(@"C:\Work\Untrusted", Installation);
        var second = await service.AuthorizeAsync(@"C:\Work\Untrusted", Installation);

        Assert(first.IsAuthorized && first.TrustLevel == CodexProjectTrustLevel.Untrusted, "untrusted decision authorizes restricted activation");
        Assert(second.IsAuthorized && second.TrustLevel == CodexProjectTrustLevel.Untrusted, "remembered untrusted decision authorizes restricted activation");
        AssertEqual(1, interaction.ProjectTrustPromptPaths.Count, "untrusted decision is prompted once");
        AssertSequenceEqual([CodexProjectTrustLevel.Untrusted], session.Writes, "untrusted decision is persisted once");
    }

    private static async Task CancelAndFailuresFailClosedAsync()
    {
        var cancelSession = new FakeProjectTrustSession();
        var cancelInteraction = new FakeUserInteractionService { TrustDecision = ProjectTrustDecision.Cancel };
        var canceled = await new ProjectTrustService(cancelSession, cancelInteraction, new TestLogger())
            .AuthorizeAsync(@"C:\Work\Canceled", Installation);
        Assert(!canceled.IsAuthorized && canceled.IsCanceled, "cancel denies activation");
        AssertEqual(0, cancelSession.Writes.Count, "cancel does not persist trust");

        var failureCases = new[]
        {
            new FakeProjectTrustSession { EnsureFailure = new IOException("connect failed") },
            new FakeProjectTrustSession { ReadFailure = new CodexAppServerProtocolException("read failed") },
            new FakeProjectTrustSession { WriteFailure = new CodexAppServerProtocolException("write failed") },
            new FakeProjectTrustSession { PersistWrites = false }
        };
        foreach (var session in failureCases)
        {
            var interaction = new FakeUserInteractionService { TrustDecision = ProjectTrustDecision.TrustProject };
            var result = await new ProjectTrustService(session, interaction, new TestLogger())
                .AuthorizeAsync(@"C:\Work\Failure", Installation);
            Assert(!result.IsAuthorized && !result.IsCanceled, "connection, read, write, and verification failures deny activation");
            AssertEqual(CodexProjectTrustLevel.Unknown, result.TrustLevel, "failure never reports trusted execution");
        }
    }

    private static async Task GatesEveryProjectActivationPathAsync()
    {
        using var temp = TempWorkspace.Create();
        var browsePath = temp.CreateDirectory("browse");
        var browseTrust = new RecordingProjectTrustService();
        await using (var browseViewModel = CreateViewModel(
                         temp,
                         new FakeSettingsStore(),
                         browsePath,
                         browseTrust))
        {
            await browseViewModel.InitializeAsync();
            await ((AsyncRelayCommand)browseViewModel.BrowseProjectCommand).ExecuteAsync();
            AssertEqual(Path.GetFullPath(browsePath), browseViewModel.SelectedProjectPath, "browse activates only after trust");
            AssertSequenceEqual([Path.GetFullPath(browsePath)], browseTrust.Paths, "browse uses trust gate");
        }

        var recentPath = temp.CreateDirectory("recent");
        var recentTrust = new RecordingProjectTrustService();
        var recentSettings = new FakeSettingsStore(new AppSettings
        {
            RecentProjects = [new RecentProject(recentPath, "Recent", DateTimeOffset.UtcNow)]
        });
        await using (var recentViewModel = CreateViewModel(temp, recentSettings, recentPath, recentTrust))
        {
            await recentViewModel.InitializeAsync();
            await ((AsyncRelayCommand)recentViewModel.OpenRecentProjectCommand).ExecuteAsync(recentPath);
            AssertEqual(Path.GetFullPath(recentPath), recentViewModel.SelectedProjectPath, "recent project activates only after trust");
            AssertSequenceEqual([Path.GetFullPath(recentPath)], recentTrust.Paths, "recent project uses trust gate");
        }

        var restoredPath = temp.CreateDirectory("restored");
        var startupTrust = new RecordingProjectTrustService();
        var startupSettings = new FakeSettingsStore(new AppSettings
        {
            LastSelectedProjectPath = restoredPath,
            RecentProjects = [new RecentProject(restoredPath, "Restored", DateTimeOffset.UtcNow)]
        });
        await using (var startupViewModel = CreateViewModel(temp, startupSettings, restoredPath, startupTrust))
        {
            await startupViewModel.InitializeAsync();
            AssertEqual(Path.GetFullPath(restoredPath), startupViewModel.SelectedProjectPath, "startup restoration activates only after trust");
            AssertSequenceEqual([Path.GetFullPath(restoredPath)], startupTrust.Paths, "startup restoration uses trust gate");
        }
    }

    private static async Task CancelPreservesSelectionAndSettingsAsync()
    {
        using var temp = TempWorkspace.Create();
        var firstPath = temp.CreateDirectory("first");
        var secondPath = temp.CreateDirectory("second");
        var settingsStore = new FakeSettingsStore(new AppSettings
        {
            RecentProjects =
            [
                new RecentProject(firstPath, "First", DateTimeOffset.UtcNow.AddMinutes(-1)),
                new RecentProject(secondPath, "Second", DateTimeOffset.UtcNow)
            ]
        });
        var trust = new RecordingProjectTrustService();
        await using var viewModel = CreateViewModel(temp, settingsStore, firstPath, trust);
        await viewModel.InitializeAsync();
        await ((AsyncRelayCommand)viewModel.OpenRecentProjectCommand).ExecuteAsync(firstPath);
        var settingsBeforeCancel = JsonSerializer.Serialize(SettingsStorageMapper.Clone(settingsStore.SavedSettings));

        trust.Authorize = false;
        await ((AsyncRelayCommand)viewModel.OpenRecentProjectCommand).ExecuteAsync(secondPath);

        AssertEqual(Path.GetFullPath(firstPath), viewModel.SelectedProjectPath, "cancel retains the active project");
        AssertEqual(settingsBeforeCancel, JsonSerializer.Serialize(settingsStore.SavedSettings), "cancel leaves settings unchanged");
        AssertEqual(firstPath, settingsStore.SavedSettings.LastSelectedProjectPath, "cancel retains startup restoration path");
    }

    private static MainViewModel CreateViewModel(
        TempWorkspace temp,
        FakeSettingsStore settingsStore,
        string pickerPath,
        IProjectTrustService trustService)
    {
        var logger = new TestLogger();
        var transport = new FakeAppServerTransport();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("project_trust_tests", "Project Trust Tests", "1.0.0"));
        var generalPath = temp.CreateDirectory($"general-{Guid.NewGuid():N}");
        return WorkspaceActionStubs.CreateMainViewModel(
            settingsStore,
            new FakeCodexDiscoveryService(Installation),
            coordinator,
            new FakeAuthService(new AuthenticationState(
                AuthReadiness.LikelySignedIn,
                "Ready",
                "Test auth",
                @"C:\Users\Test\.codex")),
            new FakeGitService(pickerPath),
            new FakeWorktreeService(pickerPath, Path.Combine(temp.Root, $"worktree-{Guid.NewGuid():N}")),
            new RecentProjectService(),
            new FakeFolderPicker(pickerPath),
            new FakeUserInteractionService(),
            new FakeThemeService(),
            new FakeCodexCliUtilityRunner(),
            new ThreadStore(),
            new SynthiaCode.Core.Codex.AppServer.CodexThreadWorkspace(),
            new FakeTerminalService(),
            logger,
            new GeneralWorkspaceService(generalPath),
            projectTrustService: trustService);
    }

    private static CodexAppServerClient CreateClient(FakeAppServerTransport transport) =>
        new(transport, new CodexAppServerClientMetadata("project_trust_tests", "Project Trust Tests", "1.0.0"));

    private static async Task InitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend(
            """
            {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
            """);
        await initialize;
    }

    private static void Respond(FakeAppServerTransport transport, JsonObject request, JsonObject result)
    {
        transport.ServerSend(new JsonObject
        {
            ["id"] = request["id"]?.DeepClone(),
            ["result"] = result
        }.ToJsonString());
    }

    private static JsonObject Parse(string message) =>
        JsonNode.Parse(message)?.AsObject()
        ?? throw new InvalidOperationException("App-server message was not a JSON object.");

    private static string? ReadString(JsonObject value, string path)
    {
        JsonNode? current = value;
        foreach (var segment in path.Split('.'))
        {
            current = current?[segment];
        }
        return current?.GetValue<string>();
    }

    private static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string label)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{label}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', actual '{actual}'.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeProjectTrustSession : IProjectTrustSession
    {
        public CodexProjectTrustLevel TrustLevel { get; private set; } = CodexProjectTrustLevel.Unknown;

        public Exception? EnsureFailure { get; init; }

        public Exception? ReadFailure { get; init; }

        public Exception? WriteFailure { get; init; }

        public bool PersistWrites { get; init; } = true;

        public List<CodexProjectTrustLevel> Writes { get; } = [];

        public Task EnsureProjectTrustSessionConnectedAsync(
            CodexInstallation installation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EnsureFailure is null ? Task.CompletedTask : Task.FromException(EnsureFailure);
        }

        public Task<CodexProjectTrustLevel> ReadProjectTrustAsync(
            string projectPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadFailure is null
                ? Task.FromResult(TrustLevel)
                : Task.FromException<CodexProjectTrustLevel>(ReadFailure);
        }

        public Task WriteProjectTrustAsync(
            string projectPath,
            CodexProjectTrustLevel trustLevel,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteFailure is not null)
            {
                return Task.FromException(WriteFailure);
            }

            Writes.Add(trustLevel);
            if (PersistWrites)
            {
                TrustLevel = trustLevel;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProjectTrustService : IProjectTrustService
    {
        public bool Authorize { get; set; } = true;

        public List<string> Paths { get; } = [];

        public Task<ProjectTrustAuthorizationResult> AuthorizeAsync(
            string projectPath,
            CodexInstallation installation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
            Paths.Add(normalizedPath);
            return Task.FromResult(Authorize
                ? ProjectTrustAuthorizationResult.Authorized(normalizedPath, CodexProjectTrustLevel.Trusted)
                : ProjectTrustAuthorizationResult.Canceled(normalizedPath));
        }
    }

    private static readonly CodexInstallation Installation = new(
        IsFound: true,
        ExecutablePath: @"C:\Tools\codex.exe",
        Version: "codex test",
        Summary: "Codex test",
        Detail: "Test installation");
}
