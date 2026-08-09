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
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

[Trait("Category", TestCategories.InfrastructureIntegration)]
[Collection(TestCategories.NativeCollection)]
public sealed class LegacyInfrastructureIntegrationTests : LegacyRuntimeTestSupport
{
    [Fact(DisplayName = "recent projects are deduped and capped")]
    public Task TestRecentProjectsAsync()
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

        var duplicateIndex = 4;
        var duplicate = settings.RecentProjects[duplicateIndex].Path;
        var previousTimestamp = settings.RecentProjects[duplicateIndex].LastOpenedUtc;
        service.AddRecentProject(settings, duplicate);

        AssertEqual(10, settings.RecentProjects.Count, "dedupe preserves cap");
        AssertEqual(duplicate, settings.RecentProjects[duplicateIndex].Path, "existing project keeps its position");
        AssertTrue(settings.RecentProjects[duplicateIndex].LastOpenedUtc >= previousTimestamp, "existing project refreshes its timestamp in place");
        AssertEqual(1, settings.RecentProjects.Count(project => string.Equals(project.Path, duplicate, StringComparison.OrdinalIgnoreCase)), "duplicate count");

        return Task.CompletedTask;
    }

    [Fact(DisplayName = "settings round trip to json")]
    public async Task TestSettingsRoundTripAsync()
    {
        using var temp = TempWorkspace.Create();
        var logger = new TestLogger();
        var store = new JsonSettingsStore(temp.Root, logger);
        var settings = new AppSettings
        {
            Theme = "Dark",
            PreferredCodexPath = @"C:\Tools\codex.exe",
            LastModelOverride = "gpt-test",
            LastReasoningEffortOverride = "high",
            LastServiceTierOverride = "fast",
            CustomDeveloperInstructionsEnabled = true,
            CustomDeveloperInstructions = "Prefer focused tests.",
            CustomBaseInstructionsEnabled = true,
            CustomBaseInstructions = "You are a SynthiaCode coding agent.",
            IsProjectRailOpen = false,
            IsDetailsPaneOpen = true
        };
        settings.RecentProjects.Add(new(temp.CreateDirectory("Repo"), "Repo", DateTimeOffset.UtcNow));
        settings.ProjectThreads.Add(new PersistedProjectThread
        {
            ProjectPath = temp.CreateDirectory("ThreadRepo"),
            ThreadId = "thr_saved",
            Mode = "worktree",
            WorkspacePath = temp.CreateDirectory("ThreadWorkspace"),
            AppliedDeveloperInstructions = "Prefer focused tests.",
            AppliedBaseInstructions = "You are a SynthiaCode coding agent.",
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
            ConversationTurns =
            [
                new CodexConversationTurnSnapshot
                {
                    TurnId = "turn_saved",
                    UserPrompt = "Saved prompt",
                    AssistantResponse = "Saved final response",
                    Status = CodexTurnStatus.Completed,
                    Activity =
                    [
                        new CodexTimelineItem(
                            CodexTimelineItemKind.CommandCompleted,
                            "Ran command",
                            "dotnet test",
                            "item/commandExecution",
                            DateTimeOffset.UtcNow)
                        {
                            ItemId = "command_saved",
                            ActivityKey = "command:command_saved"
                        }
                    ]
                }
            ],
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        AssertEqual("Dark", loaded.Theme, "theme");
        AssertEqual(settings.PreferredCodexPath, loaded.PreferredCodexPath, "preferred codex path");
        AssertEqual(settings.LastModelOverride, loaded.LastModelOverride, "last model override");
        AssertEqual(settings.LastReasoningEffortOverride, loaded.LastReasoningEffortOverride, "last reasoning override");
        AssertEqual(settings.LastServiceTierOverride, loaded.LastServiceTierOverride, "last service tier override");
        AssertTrue(loaded.CustomDeveloperInstructionsEnabled, "developer instructions enabled");
        AssertEqual(settings.CustomDeveloperInstructions, loaded.CustomDeveloperInstructions, "developer instructions");
        AssertTrue(loaded.CustomBaseInstructionsEnabled, "base instructions enabled");
        AssertEqual(settings.CustomBaseInstructions, loaded.CustomBaseInstructions, "base instructions");
        AssertEqual(settings.IsProjectRailOpen, loaded.IsProjectRailOpen, "project rail preference");
        AssertEqual(settings.IsDetailsPaneOpen, loaded.IsDetailsPaneOpen, "details pane preference");
        AssertEqual(1, loaded.RecentProjects.Count, "recent project count");
        AssertEqual(1, loaded.ProjectThreads.Count, "project thread count");
        AssertEqual("thr_saved", loaded.ProjectThreads[0].ThreadId, "project thread id");
        AssertEqual("worktree", loaded.ProjectThreads[0].Mode, "project thread mode");
        AssertEqual(settings.ProjectThreads[0].WorkspacePath, loaded.ProjectThreads[0].WorkspacePath, "project thread workspace path");
        AssertEqual("Prefer focused tests.", loaded.ProjectThreads[0].AppliedDeveloperInstructions, "thread developer instructions");
        AssertEqual("You are a SynthiaCode coding agent.", loaded.ProjectThreads[0].AppliedBaseInstructions, "thread base instructions");
        AssertEqual("Saved final response", loaded.ProjectThreads[0].FinalResponse, "project thread final response");
        var loadedActivity = loaded.ProjectThreads[0].ConversationTurns.Single().Activity.Single();
        AssertEqual("command_saved", loadedActivity.ItemId, "activity item identity survives JSON persistence");
        AssertEqual("command:command_saved", loadedActivity.ActivityKey, "activity upsert key survives JSON persistence");
        AssertEqual("Command", loadedActivity.CategoryLabel, "activity category is recomputed after JSON persistence");
        AssertTrue(File.Exists(store.SettingsPath), "settings file exists");
        var saveMetric = logger.Entries.Single(entry => entry.EventName == "settings_saved");
        AssertTrue(long.Parse(saveMetric.Properties?["serializedBytes"] ?? "0") > 0, "settings save byte metric");
        AssertTrue(long.Parse(saveMetric.Properties?["elapsedMilliseconds"] ?? "-1") >= 0, "settings save duration metric");

        var snapshot = AppSettingsSnapshot.Create(settings);
        settings.CustomDeveloperInstructions = "Mutated after snapshot";
        settings.ProjectThreads[0].AppliedBaseInstructions = "Mutated after snapshot";
        AssertEqual("Prefer focused tests.", snapshot.CustomDeveloperInstructions, "settings snapshot isolates developer instructions");
        AssertEqual("You are a SynthiaCode coding agent.", snapshot.ProjectThreads[0].AppliedBaseInstructions,
            "settings snapshot isolates per-thread base instructions");

        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")
            ?? throw new InvalidOperationException("legacy settings did not deserialize");
        AssertTrue(!legacy.CustomDeveloperInstructionsEnabled && !legacy.CustomBaseInstructionsEnabled,
            "legacy settings inherit Codex instruction defaults");
    }

    [Fact(DisplayName = "settings saves are snapshotted and coalesced")]
    public async Task TestSettingsSavesAreSnapshottedAndCoalescedAsync()
    {
        var inner = new RecordingSettingsStore();
        var logger = new TestLogger();
        var clock = new ManualTimeProvider();
        var store = new CoalescingSettingsStore(
            inner,
            logger,
            TimeSpan.FromMilliseconds(25),
            clock);
        var settings = new AppSettings();
        var saves = new List<Task>();

        for (var index = 0; index < 20; index++)
        {
            settings.Theme = $"Theme {index}";
            saves.Add(store.SaveAsync(settings));
        }

        settings.Theme = "Mutation after queueing";
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await Task.WhenAll(saves);

        AssertEqual(1, inner.SaveCount, "coalesced physical settings writes");
        AssertEqual("Theme 19", inner.SavedSettings.Theme, "latest queued snapshot is persisted");
        var batchMetric = logger.Entries.Single(entry => entry.EventName == "settings_save_batch_completed");
        AssertEqual("20", batchMetric.Properties?["requestCount"], "settings batch request metric");
        AssertEqual("19", batchMetric.Properties?["coalescedCount"], "settings batch coalesced metric");
        Console.WriteLine("METRIC settings persistence: 20 logical requests -> 1 physical write");
    }

    [Fact(DisplayName = "settings recover interrupted atomic writes")]
    public async Task TestSettingsRecoverInterruptedAtomicWritesAsync()
    {
        using var temp = TempWorkspace.Create();
        var logger = new TestLogger();
        var store = new JsonSettingsStore(temp.Root, logger);
        var interrupted = new AppSettings { Theme = "Interrupted" };
        await store.SaveAsync(interrupted);

        var tempPath = store.SettingsPath + ".tmp";
        File.Move(store.SettingsPath, tempPath, overwrite: true);
        var recoveredMissingPrimary = await store.LoadAsync();
        AssertEqual("Interrupted", recoveredMissingPrimary.Theme, "missing primary recovers temporary settings");
        AssertTrue(File.Exists(store.SettingsPath), "recovered temporary settings promoted to primary");

        var recoverable = new AppSettings { Theme = "Recoverable" };
        await store.SaveAsync(recoverable);
        File.Copy(store.SettingsPath, tempPath, overwrite: true);
        await File.WriteAllTextAsync(store.SettingsPath, "{ invalid settings json");
        File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow.AddSeconds(1));
        var recoveredCorruptPrimary = await store.LoadAsync();
        AssertEqual("Recoverable", recoveredCorruptPrimary.Theme, "corrupt primary recovers valid temporary settings");
        AssertEqual(2, logger.Entries.Count(entry => entry.EventName == "settings_recovered_from_temporary_file"), "settings recovery metric count");
    }

    [Fact(DisplayName = "codex utility runner executes doctor")]
    public async Task TestCodexUtilityRunnerExecutesDoctorAsync()
    {
        using var temp = TempWorkspace.Create();
        var executable = Path.Combine(temp.Root, "fake-codex.cmd");
        await File.WriteAllTextAsync(
            executable,
            "@echo off\r\nif \"%1\"==\"doctor\" (\r\n  echo DOCTOR_OK\r\n  echo CODEX_HOME=%CODEX_HOME%\r\n  echo diagnostic warning 1>&2\r\n  exit /b 0\r\n)\r\nexit /b 9\r\n");
        var installation = new CodexInstallation(true, executable, "codex-test", "Codex test", "Test installation");
        var runtimeEnvironment = new CodexRuntimeEnvironment(Path.Combine(temp.Root, "isolated-home"));
        var runner = new CodexCliUtilityRunner(new TestLogger(), runtimeEnvironment);

        var result = await runner.RunDoctorAsync(installation);

        AssertEqual(0, result.ExitCode, "doctor exit code");
        AssertTrue(result.StandardOutput.Contains("DOCTOR_OK", StringComparison.Ordinal), "doctor stdout captured");
        AssertTrue(
            result.StandardOutput.Contains($"CODEX_HOME={runtimeEnvironment.HomePath}", StringComparison.Ordinal),
            "doctor uses isolated CODEX_HOME");
        AssertTrue(result.StandardError.Contains("diagnostic warning", StringComparison.Ordinal), "doctor stderr captured");
        AssertTrue(result.Succeeded, "doctor success state");
    }

    [Fact(DisplayName = "auth detection reports file cache without reading token")]
    public async Task TestAuthDetectionAsync()
    {
        using var temp = TempWorkspace.Create();
        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var globalCodexHome = temp.CreateDirectory("GlobalCodexHome");
        var runtimeEnvironment = new CodexRuntimeEnvironment(Path.Combine(temp.Root, "SynthiaCodeCodexHome"));
        Environment.SetEnvironmentVariable("CODEX_HOME", globalCodexHome);

        try
        {
            var logger = new TestLogger();
            File.WriteAllText(Path.Combine(globalCodexHome, "auth.json"), "{\"access_token\":\"global-token\"}");
            var service = new CodexAuthService(logger, runtimeEnvironment);
            var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");

            var missing = await service.GetAuthenticationStateAsync(installation);
            AssertEqual(AuthReadiness.Unknown, missing.Readiness, "missing auth readiness");
            AssertEqual(runtimeEnvironment.HomePath, missing.CodexHome, "auth checks isolated codex home");

            var startInfoFactory = typeof(CodexAuthService).GetMethod(
                "CreateStartInfo",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("auth start-info factory was not found");
            var startInfo = (ProcessStartInfo?)startInfoFactory.Invoke(service, [@"C:\Tools\codex.exe", "login"])
                ?? throw new InvalidOperationException("auth start-info factory returned null");
            AssertEqual(runtimeEnvironment.HomePath, startInfo.Environment["CODEX_HOME"], "login child CODEX_HOME");
            AssertTrue(!startInfo.UseShellExecute, "login process supports an isolated environment");

            File.WriteAllText(Path.Combine(runtimeEnvironment.HomePath, "auth.json"), "{\"access_token\":\"do-not-read\"}");
            var detected = await service.GetAuthenticationStateAsync(installation);

            AssertEqual(AuthReadiness.LikelySignedIn, detected.Readiness, "detected auth readiness");
            AssertTrue(!detected.Detail.Contains("do-not-read", StringComparison.Ordinal), "token is not surfaced");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
        }
    }

    [Fact(DisplayName = "codex runtime environment creates and applies isolated home")]
    public Task TestCodexRuntimeEnvironmentAsync()
    {
        using var temp = TempWorkspace.Create();
        var homePath = Path.Combine(temp.Root, "SynthiaCode", "codex-home");
        var processCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var runtimeEnvironment = new CodexRuntimeEnvironment(homePath);
        var startInfo = new ProcessStartInfo("codex.exe");

        runtimeEnvironment.ApplyTo(startInfo);

        AssertTrue(Directory.Exists(homePath), "isolated codex home is created");
        AssertEqual(Path.GetFullPath(homePath), runtimeEnvironment.HomePath, "isolated codex home is normalized");
        AssertEqual(runtimeEnvironment.HomePath, startInfo.Environment["CODEX_HOME"], "child CODEX_HOME is isolated");
        AssertEqual(processCodexHome, Environment.GetEnvironmentVariable("CODEX_HOME"), "process CODEX_HOME is unchanged");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "codex diagnostic SQLite cleanup is bounded and targeted")]
    public async Task TestCodexDiagnosticStoreMaintenanceAsync()
    {
        AssertEqual(32L * 1024 * 1024, CodexDiagnosticStoreMaintenance.DefaultMaximumBytes, "default diagnostic store limit");
        using var temp = TempWorkspace.Create();
        var codexHome = temp.CreateDirectory("codex-home");
        var logger = new TestLogger();
        var logDatabase = Path.Combine(codexHome, "logs_2.sqlite");
        var logWal = Path.Combine(codexHome, "logs_2.sqlite-wal");
        var logShm = Path.Combine(codexHome, "logs_2.sqlite-shm");
        var stateDatabase = Path.Combine(codexHome, "state_5.sqlite");
        var unrelatedDatabase = Path.Combine(codexHome, "logs_notes.sqlite");
        await File.WriteAllBytesAsync(logDatabase, new byte[8]);
        await File.WriteAllBytesAsync(logWal, new byte[8]);
        await File.WriteAllBytesAsync(logShm, new byte[8]);
        await File.WriteAllBytesAsync(stateDatabase, new byte[32]);
        await File.WriteAllBytesAsync(unrelatedDatabase, new byte[32]);

        var cleanup = CodexDiagnosticStoreMaintenance.TrimOversizedStore(codexHome, logger, maximumBytes: 20);

        AssertEqual(24L, cleanup.ObservedBytes, "diagnostic cleanup observed bytes");
        AssertEqual(24L, cleanup.RemovedBytes, "diagnostic cleanup removed bytes");
        AssertEqual(3, cleanup.RemovedFileCount, "diagnostic cleanup removed file count");
        AssertEqual(0, cleanup.FailedFileCount, "diagnostic cleanup failure count");
        AssertTrue(!File.Exists(logDatabase), "oversized log database is removed");
        AssertTrue(!File.Exists(logWal), "oversized log WAL is removed");
        AssertTrue(!File.Exists(logShm), "oversized log shared memory is removed");
        AssertTrue(File.Exists(stateDatabase), "Codex state database is preserved");
        AssertTrue(File.Exists(unrelatedDatabase), "unrelated similarly named database is preserved");
        AssertTrue(
            logger.Entries.Any(entry => entry.EventName == "codex_diagnostic_store_trimmed"),
            "diagnostic cleanup is logged");

        var boundedLogDatabase = Path.Combine(codexHome, "logs_3.sqlite");
        await File.WriteAllBytesAsync(boundedLogDatabase, new byte[8]);
        var boundedCleanup = CodexDiagnosticStoreMaintenance.TrimOversizedStore(codexHome, logger, maximumBytes: 20);

        AssertEqual(8L, boundedCleanup.ObservedBytes, "bounded diagnostic store observed bytes");
        AssertEqual(0, boundedCleanup.RemovedFileCount, "bounded diagnostic store is not removed");
        AssertTrue(File.Exists(boundedLogDatabase), "bounded log database is preserved");
    }

    [Fact(DisplayName = "codex discovery skips unusable path candidates")]
    public async Task TestCodexDiscoverySkipsUnusablePathCandidatesAsync()
    {
        using var temp = TempWorkspace.Create();
        var brokenDir = temp.CreateDirectory("BrokenCli");
        var workingDir = temp.CreateDirectory("WorkingCli");
        var brokenCodex = Path.Combine(brokenDir, "codex.cmd");
        var workingCodex = Path.Combine(workingDir, "codex.cmd");
        var runtimeEnvironment = new CodexRuntimeEnvironment(Path.Combine(temp.Root, "SynthiaCodeCodexHome"));
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

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
            echo codex-cli test-version:%CODEX_HOME%
            exit /b 0
            """);

        try
        {
            Environment.SetEnvironmentVariable("PATH", brokenDir + Path.PathSeparator + workingDir);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", temp.CreateDirectory("LocalAppData"));
            var logger = new TestLogger();
            var service = new CodexDiscoveryService(logger, runtimeEnvironment);

            var detected = await service.DetectAsync();

            AssertTrue(detected.IsFound, "working codex is found");
            AssertEqual(Path.GetFullPath(workingCodex), detected.ExecutablePath, "working codex path");
            AssertEqual($"codex-cli test-version:{runtimeEnvironment.HomePath}", detected.Version, "working codex version");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
        }
    }

    [Fact(DisplayName = "codex discovery selects the newest automatic installation")]
    public async Task TestCodexDiscoverySelectsNewestAutomaticInstallationAsync()
    {
        using var temp = TempWorkspace.Create();
        var localAppData = temp.CreateDirectory("LocalAppData");
        var appData = temp.CreateDirectory("AppData");
        var standaloneBin = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin");
        var npmBin = Path.Combine(appData, "npm");
        var emptyPath = temp.CreateDirectory("EmptyPath");
        Directory.CreateDirectory(standaloneBin);
        Directory.CreateDirectory(npmBin);
        var standaloneCodex = Path.Combine(standaloneBin, "codex.cmd");
        var npmCodex = Path.Combine(npmBin, "codex.cmd");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousAppData = Environment.GetEnvironmentVariable("APPDATA");
        var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

        File.WriteAllText(
            standaloneCodex,
            """
            @echo off
            echo codex-cli 0.146.0
            exit /b 0
            """);
        File.WriteAllText(
            npmCodex,
            """
            @echo off
            echo codex-cli 0.147.0
            exit /b 0
            """);

        try
        {
            Environment.SetEnvironmentVariable("APPDATA", appData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
            Environment.SetEnvironmentVariable("PATH", emptyPath);
            var logger = new TestLogger();
            var service = new CodexDiscoveryService(logger);

            var detected = await service.DetectAsync();

            AssertTrue(detected.IsFound, "newest codex is found");
            AssertEqual(Path.GetFullPath(npmCodex), detected.ExecutablePath, "newest codex path");
            AssertEqual("codex-cli 0.147.0", detected.Version, "newest codex version");

            var explicitlyConfigured = await service.DetectAsync(standaloneCodex);

            AssertEqual(Path.GetFullPath(standaloneCodex), explicitlyConfigured.ExecutablePath, "explicit codex path");
            AssertEqual("codex-cli 0.146.0", explicitlyConfigured.Version, "explicit codex version");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("APPDATA", previousAppData);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
        }
    }

    [Fact(DisplayName = "codex discovery checks OpenAI local app bin")]
    public async Task TestCodexDiscoveryChecksOpenAiLocalAppBinAsync()
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

    [Fact(DisplayName = "thread store keeps multiple project threads")]
    public Task TestThreadStoreKeepsMultipleProjectThreadsAsync()
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

    [Fact(DisplayName = "git service reads status and diffs")]
    public async Task TestGitServiceReadsStatusAndDiffsAsync()
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

    [Fact(DisplayName = "git service stages commits and reverts")]
    public async Task TestGitServiceStagesCommitsAndRevertsAsync()
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

    [Fact(DisplayName = "git service refuses non-repository folders")]
    public async Task TestGitServiceRefusesNonRepositoryFoldersAsync()
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

    [Fact(DisplayName = "worktree service creates isolated sibling worktree")]
    public async Task TestWorktreeServiceCreatesIsolatedSiblingAsync()
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

    [Fact(DisplayName = "worktree service lists only assistant worktrees")]
    public async Task TestWorktreeServiceListsOnlyAssistantWorktreesAsync()
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

    [Fact(DisplayName = "worktree service refuses unowned cleanup")]
    public async Task TestWorktreeServiceRefusesUnownedCleanupAsync()
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

    [Fact(DisplayName = "worktree service removes owned clean worktree")]
    public async Task TestWorktreeServiceRemovesOwnedCleanWorktreeAsync()
    {
        using var temp = TempWorkspace.Create();
        var repository = await CreateCommittedRepositoryAsync(temp, "Repo");
        var service = new WorktreeService(new TestLogger());
        var owned = await service.CreateAsync(new WorktreeCreateRequest(repository, "completed task", "thr_done"));

        await service.RemoveAsync(repository, owned.Path);

        AssertTrue(!Directory.Exists(owned.Path), "owned worktree directory removed");
        AssertEqual(0, (await service.ListAsync(repository)).Count, "ownership record removed");
    }

    [Fact(DisplayName = "conpty terminal starts powershell in requested cwd")]
    public async Task TestConPtyTerminalStartsInRequestedCwdAsync()
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

    [Fact(DisplayName = "bounded text buffer retains newest terminal output")]
    public Task TestBoundedTextBufferRetainsNewestOutputAsync()
    {
        var buffer = new BoundedTextBuffer(10);
        buffer.Append("012345");
        buffer.Append("6789ABCDE");

        AssertEqual(10, buffer.Length, "bounded terminal length");
        AssertEqual("56789ABCDE", buffer.Snapshot(), "bounded terminal newest output");
        buffer.Clear();
        AssertEqual(0, buffer.Length, "bounded terminal clear length");
        AssertEqual(string.Empty, buffer.Snapshot(), "bounded terminal clear snapshot");

        var stressBuffer = new BoundedTextBuffer(250_000);
        stressBuffer.Append(new string('a', 200_000));
        stressBuffer.Append(new string('b', 100_000));
        var stressSnapshot = stressBuffer.Snapshot();
        AssertEqual(250_000, stressSnapshot.Length, "representative terminal stress bound");
        AssertTrue(stressSnapshot.StartsWith(new string('a', 150_000), StringComparison.Ordinal), "terminal stress retains newest tail of first chunk");
        AssertTrue(stressSnapshot.EndsWith(new string('b', 100_000), StringComparison.Ordinal), "terminal stress retains latest chunk");

        var throughputBuffer = new BoundedTextBuffer(250_000);
        var throughputChunk = new string('x', 4096);
        const int throughputChunkCount = 10_000;
        var throughputTimer = Stopwatch.StartNew();
        for (var index = 0; index < throughputChunkCount; index++)
        {
            throughputBuffer.Append(throughputChunk);
        }

        _ = throughputBuffer.Snapshot();
        throughputTimer.Stop();
        var appendedMegabytes = throughputChunk.Length * throughputChunkCount / (1024d * 1024d);
        Console.WriteLine(
            $"METRIC terminal buffer: {appendedMegabytes:F2} MiB in {throughputTimer.Elapsed.TotalMilliseconds:F2} ms " +
            $"({appendedMegabytes / throughputTimer.Elapsed.TotalSeconds:F2} MiB/s), retained {throughputBuffer.Length} chars");

        return Task.CompletedTask;
    }

}
