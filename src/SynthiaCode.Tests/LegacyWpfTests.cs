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

[Trait("Category", TestCategories.Wpf)]
[Collection(TestCategories.WpfCollection)]
public sealed class LegacyWpfTests : LegacyRuntimeTestSupport
{
    [Fact(DisplayName = "view model applies and persists selected theme")]
    public async Task TestViewModelAppliesAndPersistsThemeAsync()
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
        await StateProbe.WaitForAsync(() => settingsStore.SavedSettings.Theme == "Light", "theme setting saved");

        AssertEqual("Light", themeService.AppliedTheme, "changed theme applied");
    }

    [Fact(DisplayName = "view model prepares whole-image and marked-region imagegen edits")]
    public async Task TestViewModelPreparesGeneratedImageEditsAsync()
    {
        using var temp = TempWorkspace.Create();
        var sourcePath = Path.Combine(temp.Root, "generated-image.png");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z6JkAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(sourcePath, png);

        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var interaction = new FakeUserInteractionService();
        var attachmentStore = new LocalAttachmentStore(
            Path.Combine(temp.Root, "attachment-store"),
            logger);
        await using var viewModel = CreateMainViewModel(
            transport,
            temp.Root,
            AuthReadiness.LikelySignedIn,
            logger: logger,
            userInteractionService: interaction,
            attachmentStore: attachmentStore);

        await ((AsyncRelayCommand)viewModel.TaskWorkspace.EditGeneratedImageCommand)
            .ExecuteAsync(sourcePath);

        AssertEqual(sourcePath, interaction.SelectedImageEditPath, "whole-image editor source path");
        AssertEqual(1, viewModel.TaskWorkspace.Attachments.Count, "whole-image edit attachment count");
        AssertTrue(
            viewModel.PromptText.StartsWith("$imagegen Edit the attached image", StringComparison.Ordinal),
            "whole-image edit prepares an imagegen prompt");

        viewModel.TaskWorkspace.ClearAttachments();
        viewModel.PromptText = string.Empty;
        var regionGuidePng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        interaction.ImageEditSelection = new GeneratedImageEditSelection(regionGuidePng);

        await ((AsyncRelayCommand)viewModel.TaskWorkspace.EditGeneratedImageCommand)
            .ExecuteAsync(sourcePath);

        AssertEqual(2, viewModel.TaskWorkspace.Attachments.Count, "region edit source and guide attachments");
        AssertTrue(
            viewModel.TaskWorkspace.Attachments[1].DisplayName.EndsWith("-edit-region.png", StringComparison.Ordinal),
            "region guide has a descriptive attachment name");
        AssertTrue(
            viewModel.PromptText.Contains("translucent red mark", StringComparison.Ordinal) &&
            viewModel.PromptText.Contains("Preserve everything outside", StringComparison.Ordinal),
            "region edit prompt explains the guide and preservation boundary");
        AssertEqual(
            "Generated image and marked region attached. Describe the edit, then send.",
            viewModel.StatusMessage,
            "region edit readiness status");
    }

    [Fact(DisplayName = "view model dispatches image attachment cleanup to its captured UI context")]
    public async Task TestViewModelDispatchesImageAttachmentCleanupAsync()
    {
        using var temp = TempWorkspace.Create();
        var projectPath = temp.CreateDirectory("ImageEditRepo");
        var sourcePath = Path.Combine(projectPath, "generated-image.png");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z6JkAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(sourcePath, png);

        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var attachmentStore = new LocalAttachmentStore(
            Path.Combine(temp.Root, "attachment-store"),
            logger);
        var context = new InlineTrackingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        MainViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = CreateMainViewModel(
                transport,
                projectPath,
                AuthReadiness.LikelySignedIn,
                logger: logger,
                attachmentStore: attachmentStore);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        try
        {
            await viewModel.InitializeAsync();
            viewModel.BrowseProjectCommand.Execute(null);
            await StateProbe.WaitForAsync(
                () => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase),
                "image edit project selection");
            await viewModel.AddImageFilesAsync([sourcePath]);
            AssertEqual(1, viewModel.TaskWorkspace.Attachments.Count, "image edit turn starts with an attachment");

            viewModel.PromptText = "$imagegen Change the marked area.";
            var submit = ((AsyncRelayCommand)viewModel.SubmitPromptCommand).ExecuteAsync();
            await transport.WaitForClientMessageCountAsync(2);
            transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
            await transport.WaitForClientMessageCountAsync(3);
            transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-image-edit"}}}""");
            await transport.WaitForClientMessageCountAsync(4);
            transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-image-edit"}}}""");

            await StateProbe.WaitForAsync(
                () => context.SendCount > 0 && viewModel.TaskWorkspace.Attachments.Count == 0,
                "image attachment cleanup dispatched to captured context");
            await CompleteAutomaticThreadRenameAsync(transport, "thread-image-edit");
            await submit;

            AssertTrue(context.SendCount > 0, "turn-start projection marshals through the captured context");
            transport.ServerSend(
                """{"method":"turn/completed","params":{"threadId":"thread-image-edit","turn":{"id":"turn-image-edit","status":"completed","items":[]}}}""");
            await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "image edit turn completed");
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [Fact(DisplayName = "view model validates and persists custom Codex instructions")]
    public async Task TestViewModelPersistsCustomInstructionsAsync()
    {
        using var temp = TempWorkspace.Create();
        var settingsStore = new FakeSettingsStore(new AppSettings
        {
            CustomDeveloperInstructionsEnabled = true,
            CustomDeveloperInstructions = "Use tests first.",
            CustomBaseInstructionsEnabled = false,
            CustomBaseInstructions = "Preserved disabled base draft."
        });
        var viewModel = CreateMainViewModel(
            new FakeAppServerTransport(),
            temp.Root,
            AuthReadiness.LikelySignedIn,
            settingsStore);

        await viewModel.InitializeAsync();

        AssertTrue(viewModel.DeveloperInstructionsEnabled, "developer instruction enable state restored");
        AssertEqual("Use tests first.", viewModel.DeveloperInstructions, "developer instruction draft restored");
        AssertTrue(!viewModel.BaseInstructionsEnabled, "base instruction enable state restored");
        AssertEqual("Preserved disabled base draft.", viewModel.BaseInstructions, "disabled base draft restored");

        viewModel.DeveloperInstructions = " ";
        AssertTrue(!viewModel.SaveInstructionSettingsCommand.CanExecute(null),
            "blank enabled developer instructions cannot be saved");
        AssertTrue(!string.IsNullOrWhiteSpace(viewModel.InstructionSettingsValidationMessage),
            "invalid instruction settings explain the error");

        viewModel.DeveloperInstructions = "Run focused tests before the full suite.";
        viewModel.BaseInstructionsEnabled = true;
        viewModel.BaseInstructions = "You are a careful coding agent.";
        AssertTrue(viewModel.SaveInstructionSettingsCommand.CanExecute(null), "valid changed instructions can be saved");
        viewModel.SaveInstructionSettingsCommand.Execute(null);

        await StateProbe.WaitForAsync(
            () => settingsStore.SavedSettings.CustomBaseInstructionsEnabled &&
                  settingsStore.SavedSettings.CustomBaseInstructions == "You are a careful coding agent.",
            "custom instruction settings saved");
        AssertEqual(
            "Run focused tests before the full suite.",
            settingsStore.SavedSettings.CustomDeveloperInstructions,
            "developer instruction setting saved");

        viewModel.ResetInstructionSettingsCommand.Execute(null);
        AssertTrue(!viewModel.DeveloperInstructionsEnabled && !viewModel.BaseInstructionsEnabled,
            "reset disables custom instructions");
        AssertEqual(string.Empty, viewModel.DeveloperInstructions, "reset clears developer instructions");
        AssertEqual(string.Empty, viewModel.BaseInstructions, "reset clears base instructions");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model persists responsive shell state")]
    public async Task TestViewModelPersistsResponsiveShellStateAsync()
    {
        using var temp = TempWorkspace.Create();
        var settingsStore = new FakeSettingsStore(new AppSettings
        {
            IsProjectRailOpen = true,
            IsDetailsPaneOpen = true
        });
        var viewModel = CreateMainViewModel(
            new FakeAppServerTransport(),
            temp.Root,
            AuthReadiness.LikelySignedIn,
            settingsStore);

        await viewModel.InitializeAsync();
        AssertTrue(viewModel.IsProjectRailOpen, "persisted project rail restored");
        AssertTrue(viewModel.IsDetailsPaneOpen, "persisted details pane restored");

        viewModel.UpdateViewportWidth(900);
        AssertTrue(viewModel.IsCompactLayout, "compact layout detected");
        AssertTrue(!viewModel.IsMediumLayout && !viewModel.IsWideLayout, "compact layout is the exclusive shell state");
        AssertTrue(viewModel.IsProjectRailOpen, "project rail retained in compact conflict");
        AssertTrue(!viewModel.IsDetailsPaneOpen, "details pane closes in compact conflict");
        AssertTrue(viewModel.IsProjectRailOverlayVisible, "compact project rail is hosted as a drawer");
        AssertTrue(!viewModel.IsProjectRailPersistentVisible, "compact project rail does not consume center width");

        viewModel.ToggleDetailsPaneCommand.Execute(null);
        AssertTrue(viewModel.IsDetailsPaneOpen, "details pane opens in compact layout");
        AssertTrue(!viewModel.IsProjectRailOpen, "details pane replaces project rail in compact layout");
        AssertTrue(viewModel.IsInspectorOverlayVisible, "compact inspector is hosted as a drawer");
        AssertTrue(settingsStore.SavedSettings.IsDetailsPaneOpen, "details pane preference saved");
        AssertTrue(!settingsStore.SavedSettings.IsProjectRailOpen, "project rail compact preference saved");

        viewModel.ToggleProjectRailCommand.Execute(null);
        AssertTrue(viewModel.IsProjectRailOpen, "project rail reopens in compact layout");
        AssertTrue(!viewModel.IsDetailsPaneOpen, "project rail replaces details pane in compact layout");

        viewModel.UpdateViewportWidth(1200);
        AssertTrue(viewModel.IsMediumLayout, "medium layout detected");
        AssertTrue(!viewModel.IsCompactLayout && !viewModel.IsWideLayout, "medium layout is the exclusive shell state");
        AssertTrue(viewModel.IsProjectRailPersistentVisible, "medium project rail remains persistent");

        viewModel.ToggleDetailsPaneCommand.Execute(null);
        AssertTrue(viewModel.IsInspectorOverlayVisible, "medium inspector opens as a drawer");
        AssertTrue(!viewModel.IsInspectorPersistentVisible, "medium inspector does not consume center width");

        viewModel.UpdateViewportWidth(1500);
        AssertTrue(viewModel.IsWideLayout, "wide layout detected");
        AssertTrue(!viewModel.IsCompactLayout && !viewModel.IsMediumLayout, "wide layout is the exclusive shell state");
        AssertTrue(viewModel.IsProjectRailPersistentVisible, "wide project rail remains persistent");
        AssertTrue(viewModel.IsInspectorPersistentVisible, "wide inspector becomes persistent");
        AssertTrue(!viewModel.IsInspectorOverlayVisible, "wide inspector drawer closes");
    }

    [Fact(DisplayName = "view model terminal toggle selects terminal workspace")]
    public async Task TestViewModelTerminalToggleSelectsTerminalWorkspaceAsync()
    {
        using var temp = TempWorkspace.Create();
        var viewModel = CreateMainViewModel(
            new FakeAppServerTransport(),
            temp.Root,
            AuthReadiness.LikelySignedIn);

        await viewModel.InitializeAsync();
        AssertEqual(0, viewModel.SelectedWorkspaceTabIndex, "task workspace selected initially");

        viewModel.ToggleTerminalCommand.Execute(null);

        AssertTrue(viewModel.IsTerminalVisible, "terminal made visible");
        AssertEqual(1, viewModel.SelectedWorkspaceTabIndex, "terminal workspace selected");
    }

    [Fact(DisplayName = "view model surfaces codex doctor diagnostics")]
    public async Task TestViewModelSurfacesCodexDoctorDiagnosticsAsync()
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
        await StateProbe.WaitForAsync(
            () => viewModel.Diagnostics.Any(line => line.Contains("Doctor reports healthy", StringComparison.Ordinal)),
            "doctor output shown");

        AssertEqual(1, utilityRunner.RunCount, "doctor invocation count");
    }

    [Fact(DisplayName = "view model warms app-server after diagnostics")]
    public async Task TestViewModelWarmsAppServerAfterDiagnosticsAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var processService = new SequenceCodexProcessService(transport);
        var viewModel = CreateMainViewModel(
            transport,
            temp.Root,
            AuthReadiness.LikelySignedIn,
            processService: processService);

        await viewModel.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);

        AssertEqual(1, processService.StartCount, "background app-server start count");
        AssertEqual("Codex connecting", viewModel.AppServerHealth, "background connecting state");

        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await StateProbe.WaitForAsync(() => viewModel.AppServerHealth == "Codex connected", "background app-server connected");

        AssertEqual("Ready", viewModel.StatusMessage, "warm-up does not replace ready status");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model skips warm-up when sign-in is needed")]
    public async Task TestViewModelSkipsWarmUpWhenSignInIsNeededAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var processService = new SequenceCodexProcessService(transport);
        var viewModel = CreateMainViewModel(
            transport,
            temp.Root,
            AuthReadiness.NotSignedIn,
            processService: processService);

        await viewModel.InitializeAsync();

        AssertEqual("Sign-in needed", viewModel.AppServerHealth, "sign-in connection state");
        AssertEqual(0, processService.StartCount, "app-server not started without sign-in");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model preserves prompt after auth failed turn")]
    public async Task TestViewModelPreservesPromptAfterAuthFailedTurnAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("Repo");
        var prompt = "Summarize this repo.";
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

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

        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "turn running");
        AssertEqual(prompt, viewModel.PromptText, "prompt remains visible while turn runs");

        transport.ServerSend(
            """
            {"method":"error","params":{"error":{"message":"Reconnecting... 5/5","codexErrorInfo":{"responseStreamDisconnected":{"httpStatusCode":401}},"additionalDetails":"unexpected status 401 Unauthorized"},"willRetry":false,"threadId":"thr_123","turnId":"turn_456"}}
            """);
        transport.ServerSend(
            """
            {"method":"turn/completed","params":{"threadId":"thr_123","turn":{"id":"turn_456","status":"failed","error":{"message":"stream disconnected before completion","additionalDetails":"unexpected status 401 Unauthorized"},"items":[]}}}
            """);

        await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "turn stopped");

        AssertEqual(prompt, viewModel.PromptText, "prompt is preserved after auth failure");
        AssertTrue(viewModel.StatusMessage.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
            viewModel.StatusMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase), "auth failure status is actionable");

        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model runs follow-up turns on the same thread")]
    public async Task TestViewModelRunsFollowUpOnSameThreadAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("Repo");
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

        viewModel.PromptText = "First question";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-1"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-1"}}}""");
        await CompleteAutomaticThreadRenameAsync(transport, "thread-1");
        transport.ServerSend("""{"method":"item/completed","params":{"threadId":"thread-1","turnId":"turn-1","item":{"type":"agentMessage","text":"First answer"}}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-1","status":"completed","items":[]}}}""");
        await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "first turn completed");
        await StateProbe.WaitForAsync(() => viewModel.SubmitPromptCommand.CanExecute(null), "first submit command completed");

        viewModel.PromptText = "Follow-up question";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(6);
        var followUpRequest = ParseMessage(transport.ClientMessages[5]);
        AssertJsonString("turn/start", followUpRequest, "method", "follow-up method");
        AssertJsonString("thread-1", followUpRequest, "params.threadId", "follow-up reuses thread");
        transport.ServerSend("""{"id":4,"result":{"turn":{"id":"turn-2"}}}""");
        transport.ServerSend("""{"method":"item/completed","params":{"threadId":"thread-1","turnId":"turn-2","item":{"type":"agentMessage","text":"Second answer"}}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-1","turn":{"id":"turn-2","status":"completed","items":[]}}}""");
        await StateProbe.WaitForAsync(() =>
            !viewModel.IsTurnRunning &&
            viewModel.TaskWorkspace.ConversationTurns.Count == 2 &&
            string.Equals(
                viewModel.TaskWorkspace.ConversationTurns[1].AssistantResponse,
                "Second answer",
                StringComparison.Ordinal),
            "follow-up completed");

        AssertEqual(2, viewModel.TaskWorkspace.ConversationTurns.Count, "two visible turns");
        AssertEqual("First question", viewModel.TaskWorkspace.ConversationTurns[0].UserPrompt, "first prompt retained");
        AssertEqual("First answer", viewModel.TaskWorkspace.ConversationTurns[0].AssistantResponse, "first answer retained");
        AssertEqual("Follow-up question", viewModel.TaskWorkspace.ConversationTurns[1].UserPrompt, "follow-up retained");
        AssertEqual("Second answer", viewModel.TaskWorkspace.ConversationTurns[1].AssistantResponse, "follow-up answer retained");

        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model queues active follow-up and drains after completion")]
    public async Task TestViewModelQueuesAndDrainsFollowUpAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("QueueRepo");
        var settingsStore = new FakeSettingsStore(new AppSettings { FollowUpBehavior = "queue" });
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "queue project selection");

        viewModel.PromptText = "Initial queued-follow-up task";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-queue"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-queue-1"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "queue initial turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thread-queue");

        viewModel.SteeringText = "Run the focused queue tests next";
        viewModel.SteerTurnCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.TaskWorkspace.QueuedFollowUps.Count == 1, "active message queued");
        AssertEqual(5, transport.ClientMessages.Count, "queueing makes no app-server request");
        AssertTrue(string.IsNullOrWhiteSpace(viewModel.SteeringText), "queueing clears active composer");
        AssertEqual("Run the focused queue tests next", settingsStore.SavedSettings.ProjectThreads.Single().QueuedFollowUps.Single().Text, "queue persists immediately");

        var preflightStart = transport.ClientMessages.Count;
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-queue","turn":{"id":"turn-queue-1","status":"completed","items":[]}}}""");
        var queuedTurn = await CompleteQueuedDispatchPreflightAsync(
            transport,
            projectPath,
            preflightStart,
            () => $"status={viewModel.StatusMessage}; queue={string.Join(",", viewModel.TaskWorkspace.QueuedFollowUps.Select(item => $"{item.State}:{item.LastError}"))}");
        AssertJsonString("turn/start", queuedTurn, "method", "queued follow-up starts a new turn");
        AssertJsonString("thread-queue", queuedTurn, "params.threadId", "queued follow-up stays on its thread");
        AssertJsonString("Run the focused queue tests next", queuedTurn, "params.input.0.text", "queued follow-up prompt");
        transport.ServerSend($"{{\"id\":{queuedTurn["id"]!.ToJsonString()},\"result\":{{\"turn\":{{\"id\":\"turn-queue-2\"}}}}}}");

        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning && viewModel.TaskWorkspace.QueuedFollowUps.Count == 0, "queued follow-up acknowledged");
        AssertEqual(2, viewModel.TaskWorkspace.ConversationTurns.Count, "queued follow-up creates a separate conversation turn");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model alternate follow-up inverts behavior once")]
    public async Task TestViewModelAlternateFollowUpInvertsOnceAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("AlternateRepo");
        var settingsStore = new FakeSettingsStore(new AppSettings { FollowUpBehavior = "queue" });
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "alternate project selection");
        viewModel.PromptText = "Initial alternate task";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-alternate"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-alternate"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "alternate initial turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thread-alternate");

        viewModel.SteeringText = "Steer this once";
        viewModel.TaskWorkspace.AlternateFollowUpCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(6);
        var steer = ParseMessage(transport.ClientMessages[5]);
        AssertJsonString("turn/steer", steer, "method", "alternate queue action steers once");
        transport.ServerSend("""{"id":4,"result":{"turnId":"turn-alternate"}}""");
        await StateProbe.WaitForAsync(() => string.IsNullOrWhiteSpace(viewModel.SteeringText), "alternate steer acknowledged");

        AssertEqual(FollowUpBehavior.Queue, viewModel.TaskWorkspace.FollowUpBehavior, "alternate action does not change preference");
        AssertEqual("queue", settingsStore.SavedSettings.FollowUpBehavior, "alternate action does not rewrite setting");
        AssertEqual(0, viewModel.TaskWorkspace.QueuedFollowUps.Count, "alternate steer does not enqueue");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-alternate","turn":{"id":"turn-alternate","status":"completed","items":[]}}}""");
        await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "alternate turn completed");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model pauses queued follow-ups after failed turn")]
    public async Task TestViewModelPausesQueuedFollowUpsAfterFailedTurnAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("FailedQueueRepo");
        var settingsStore = new FakeSettingsStore(new AppSettings { FollowUpBehavior = "queue" });
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "failed queue project selection");
        viewModel.PromptText = "Initial task that will fail";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-failed-queue"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-failed-queue"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "failed queue initial turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thread-failed-queue");

        viewModel.SteeringText = "Do not auto-run after failure";
        viewModel.SteerTurnCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.TaskWorkspace.QueuedFollowUps.Count == 1, "failed turn follow-up queued");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-failed-queue","turn":{"id":"turn-failed-queue","status":"failed","error":{"message":"Expected test failure"},"items":[]}}}""");
        await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "failed turn completed");
        await settingsStore.Saves.WaitForAsync(
            saved => saved.ProjectThreads.Any(thread =>
                thread.ThreadId == "thread-failed-queue" && thread.QueuedFollowUps.Count == 1),
            "failed turn queue persistence");

        AssertEqual(5, transport.ClientMessages.Count, "failed turn does not auto-start queued work");
        AssertEqual(1, viewModel.TaskWorkspace.QueuedFollowUps.Count, "failed turn retains queued work");
        AssertEqual(QueuedFollowUpState.Pending, viewModel.TaskWorkspace.QueuedFollowUps[0].State, "failed turn leaves queued work pending for explicit retry");
        AssertTrue(!viewModel.ArchiveThreadCommand.CanExecute(null), "thread with queued work cannot be archived");
        viewModel.TaskWorkspace.DeleteQueuedFollowUpCommand.Execute(viewModel.TaskWorkspace.QueuedFollowUps[0]);
        await StateProbe.WaitForAsync(() => viewModel.TaskWorkspace.QueuedFollowUps.Count == 0, "failed queue item deleted");
        await StateProbe.WaitForAsync(() => viewModel.ArchiveThreadCommand.CanExecute(null), "archive re-enabled after queue deletion");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model drains queued follow-up on background thread")]
    public async Task TestViewModelDrainsQueuedFollowUpOnBackgroundThreadAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("BackgroundQueueRepo");
        var settingsStore = new FakeSettingsStore(new AppSettings { FollowUpBehavior = "queue" });
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "background queue project selection");
        viewModel.PromptText = "First thread task";
        var firstSubmit = ((AsyncRelayCommand)viewModel.SubmitPromptCommand).ExecuteAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-background-a"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-background-a-1"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "background first turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thread-background-a");
        await firstSubmit;

        viewModel.SteeringText = "Queued work for the first thread";
        viewModel.SteerTurnCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.TaskWorkspace.QueuedFollowUps.Count == 1, "background follow-up queued");

        var newThread = ((AsyncRelayCommand)viewModel.NewThreadCommand).ExecuteAsync();
        await transport.WaitForClientMessageCountAsync(6);
        transport.ServerSend("""{"id":4,"result":{"thread":{"id":"thread-background-b"}}}""");
        await newThread;
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thread-background-b", "background second thread selected");
        viewModel.PromptText = "Second thread task";
        var secondSubmit = ((AsyncRelayCommand)viewModel.SubmitPromptCommand).ExecuteAsync();
        await transport.WaitForClientMessageCountAsync(7);
        transport.ServerSend("""{"id":5,"result":{"turn":{"id":"turn-background-b-1"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "background second turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thread-background-b");
        await secondSubmit;

        var preflightStart = transport.ClientMessages.Count;
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-background-a","turn":{"id":"turn-background-a-1","status":"completed","items":[]}}}""");
        var queuedTurn = await CompleteQueuedDispatchPreflightAsync(
            transport,
            projectPath,
            preflightStart,
            () => $"status={viewModel.StatusMessage}; queue={string.Join(",", settingsStore.SavedSettings.ProjectThreads.Single(thread => thread.ThreadId == "thread-background-a").QueuedFollowUps.Select(item => $"{item.State}:{item.LastError}"))}");
        AssertJsonString("turn/start", queuedTurn, "method", "background queued follow-up starts a turn");
        AssertJsonString("thread-background-a", queuedTurn, "params.threadId", "background queued follow-up stays on its owning thread");
        AssertJsonString("Queued work for the first thread", queuedTurn, "params.input.0.text", "background queued prompt");
        transport.ServerSend($"{{\"id\":{queuedTurn["id"]!.ToJsonString()},\"result\":{{\"turn\":{{\"id\":\"turn-background-a-2\"}}}}}}");

        await StateProbe.WaitForAsync(
            () => settingsStore.SavedSettings.ProjectThreads.Single(thread => thread.ThreadId == "thread-background-a").QueuedFollowUps.Count == 0,
            "background queued follow-up acknowledged and persisted");
        AssertEqual("thread-background-b", viewModel.SelectedThread?.ThreadId, "background drain does not change selection");
        AssertTrue(viewModel.IsTurnRunning, "selected second thread remains running");
        await StateProbe.WaitForAsync(
            () => viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thread-background-a").IsRunning,
            "background queued turn has running indicator");

        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-background-a","turn":{"id":"turn-background-a-2","status":"completed","items":[]}}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-background-b","turn":{"id":"turn-background-b-1","status":"completed","items":[]}}}""");
        await StateProbe.WaitForAsync(() => viewModel.ProjectThreads.All(thread => !thread.IsRunning), "background turns completed");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model cancellation sends active thread and turn")]
    public async Task TestViewModelCancellationSendsActiveThreadAndTurnAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("Repo");
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

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

        await StateProbe.WaitForAsync(() => viewModel.CancelTurnCommand.CanExecute(null), "cancel enabled");
        await CompleteAutomaticThreadRenameAsync(transport, "thr_123");

        viewModel.CancelTurnCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(6);

        var cancelRequest = ParseMessage(transport.ClientMessages[5]);
        AssertJsonString("turn/interrupt", cancelRequest, "method", "view model cancel method");
        AssertJsonString("thr_123", cancelRequest, "params.threadId", "view model cancel thread id");
        AssertJsonString("turn_456", cancelRequest, "params.turnId", "view model cancel turn id");

        transport.ServerSend(
            """
            {"id":4,"result":{"ok":true}}
            """);

        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model restores persisted thread and resumes it")]
    public async Task TestViewModelRestoresPersistedThreadAndResumesItAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("Repo");
        var settings = new AppSettings();
        settings.ProjectThreads.Add(new PersistedProjectThread
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
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.FinalResponse, "Earlier answer", StringComparison.Ordinal), "thread snapshot restore");

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

        await settingsStore.Saves.WaitForAsync(saved => saved.ProjectThreads.Any(thread =>
            string.Equals(thread.ThreadId, "thr_existing", StringComparison.Ordinal) &&
            string.Equals(thread.FinalResponse, "Updated answer", StringComparison.Ordinal)));

        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model sends selected model and reasoning")]
    public async Task TestViewModelSendsSelectedModelAndReasoningAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("Repo");
        var settingsStore = new FakeSettingsStore();
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn, settingsStore);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");

        viewModel.ModelOverride = "gpt-test";
        viewModel.ReasoningEffortOverride = "xhigh";
        viewModel.TaskWorkspace.ServiceTierSelection = CodexServiceTierSelection.Fast;
        viewModel.PromptText = "Use selected overrides.";
        viewModel.SubmitPromptCommand.Execute(null);

        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend(
            """
            {"id":0,"result":{"userAgent":"codex-test","platformFamily":"windows","platformOs":"windows"}}
            """);

        await transport.WaitForClientMessageCountAsync(3);
        var selectedModelThreadRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("gpt-test", selectedModelThreadRequest, "params.model", "new thread model override");
        transport.ServerSend(
            """
            {"id":1,"result":{"thread":{"id":"thr_123"}}}
            """);

        await transport.WaitForClientMessageCountAsync(4);
        var turnRequest = ParseMessage(transport.ClientMessages[3]);
        AssertJsonString("gpt-test", turnRequest, "params.model", "view model model override");
        AssertJsonString("xhigh", turnRequest, "params.effort", "view model reasoning effort");
        AssertJsonString("fast", turnRequest, "params.serviceTier", "view model service tier");

        transport.ServerSend(
            """
            {"id":2,"result":{"turn":{"id":"turn_456"}}}
            """);
        transport.ServerSend(
            """
            {"method":"turn/completed","params":{"threadId":"thr_123","turn":{"id":"turn_456","status":"completed","items":[]}}}
            """);

        await StateProbe.WaitForAsync(() => string.Equals(settingsStore.SavedSettings.LastModelOverride, "gpt-test", StringComparison.Ordinal) &&
            string.Equals(settingsStore.SavedSettings.LastReasoningEffortOverride, "xhigh", StringComparison.Ordinal) &&
            string.Equals(settingsStore.SavedSettings.LastServiceTierOverride, "fast", StringComparison.Ordinal), "override save");

        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model loads model options")]
    public async Task TestViewModelLoadsModelOptionsAsync()
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
        var accountRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString("account/read", accountRequest, "method", "view model account read method");
        transport.ServerSend(
            """
            {"id":1,"result":{"account":{"type":"chatgpt","email":"developer@example.com","planType":"plus"},"requiresOpenaiAuth":true}}
            """);

        await transport.WaitForClientMessageCountAsync(4);
        var modelRequest = ParseMessage(transport.ClientMessages[3]);
        AssertJsonString("model/list", modelRequest, "method", "view model model list method");

        transport.ServerSend(
            """
            {"id":2,"result":{"data":[{"id":"default","model":"gpt-default","displayName":"GPT Default","description":"Default model","isDefault":true,"hidden":false,"defaultReasoningEffort":"medium","supportedReasoningEfforts":[{"reasoningEffort":"medium","description":"Balanced"}],"serviceTiers":[{"id":"fast","name":"Fast","description":"Faster responses"}]},{"id":"default-duplicate","model":"gpt-default","displayName":"GPT Default Duplicate","description":"Duplicate","isDefault":false,"hidden":false,"defaultReasoningEffort":"medium","supportedReasoningEfforts":[]},{"id":"fast","model":"gpt-fast","displayName":"GPT Fast","description":"Fast model","isDefault":false,"hidden":false,"defaultReasoningEffort":"minimal","supportedReasoningEfforts":[{"reasoningEffort":"minimal","description":"Fast"}],"serviceTiers":[]}]}}
            """);

        await StateProbe.WaitForAsync(
            () => viewModel.ModelOptions.Count == 2 && viewModel.TaskWorkspace.SelectedReasoning is not null,
            "model options and default reasoning load");
        AssertTrue(viewModel.ModelOptions.Contains("gpt-default"), "default model option");
        AssertTrue(viewModel.ModelOptions.Contains("gpt-fast"), "fast model option");
        AssertEqual("GPT Default", viewModel.TaskWorkspace.SelectedModel?.DisplayName, "default display model selected");
        AssertEqual(CodexReasoningEffort.Medium, viewModel.TaskWorkspace.SelectedReasoning?.Effort, "default reasoning selected");
        AssertTrue(viewModel.TaskWorkspace.IsFastModeAvailable, "Fast availability comes from model catalog");
        AssertEqual("ChatGPT Plus", viewModel.TaskWorkspace.AccountPlanLabel, "plan label is contextual");

        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model manages multiple threads")]
    public async Task TestViewModelManagesMultipleThreadsAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new FakeSettingsStore();
        var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn, settingsStore);
        await viewModel.InitializeAsync();
        await ((AsyncRelayCommand)viewModel.BrowseProjectCommand).ExecuteAsync();
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "multi-thread project selected");

        viewModel.NewThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_one"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.ProjectThreads.Count == 1, "first thread created");
        await StateProbe.WaitForAsync(() => viewModel.NewThreadCommand.CanExecute(null), "new thread command ready again");

        viewModel.NewThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"thread":{"id":"thr_two"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.ProjectThreads.Count == 2, "second thread created");
        await StateProbe.WaitForAsync(
            () => string.Equals(viewModel.SelectedThread?.ThreadId, "thr_two", StringComparison.Ordinal),
            "newest thread selection completed");
        AssertEqual("thr_two", viewModel.SelectedThread?.ThreadId, "newest thread selected");

        viewModel.SelectedThread = viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_one");
        viewModel.ResumeThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(5);
        AssertJsonString("thread/resume", ParseMessage(transport.ClientMessages[4]), "method", "resume selected thread method");
        transport.ServerSend("""{"id":3,"result":{"thread":{"id":"thr_one"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.StatusMessage.Contains("resumed", StringComparison.OrdinalIgnoreCase), "selected thread resumed");

        AssertEqual(2, settingsStore.SavedSettings.ProjectThreads.Count, "multiple threads persisted");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model captures custom instructions per thread")]
    public async Task TestViewModelCapturesInstructionsPerThreadAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new FakeSettingsStore(new AppSettings
        {
            CustomDeveloperInstructionsEnabled = true,
            CustomDeveloperInstructions = "Original developer instructions.",
            CustomBaseInstructionsEnabled = true,
            CustomBaseInstructions = "Original base instructions."
        });
        var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn, settingsStore);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "instruction project selected");

        viewModel.NewThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        var startRequest = ParseMessage(transport.ClientMessages[2]);
        AssertJsonString(
            "Original developer instructions.",
            startRequest,
            "params.developerInstructions",
            "new thread developer instructions");
        AssertJsonString(
            "Original base instructions.",
            startRequest,
            "params.baseInstructions",
            "new thread base instructions");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_instruction_source"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thr_instruction_source",
            "instruction source thread created");
        AssertEqual(
            "Original developer instructions.",
            settingsStore.SavedSettings.ProjectThreads.Single().AppliedDeveloperInstructions,
            "thread captures developer instructions");
        AssertEqual(
            "Original base instructions.",
            settingsStore.SavedSettings.ProjectThreads.Single().AppliedBaseInstructions,
            "thread captures base instructions");

        viewModel.DeveloperInstructions = "Changed global developer instructions.";
        viewModel.BaseInstructions = "Changed global base instructions.";
        viewModel.SaveInstructionSettingsCommand.Execute(null);
        await StateProbe.WaitForAsync(
            () => settingsStore.SavedSettings.CustomDeveloperInstructions == "Changed global developer instructions.",
            "changed instruction defaults saved");

        viewModel.ResumeThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(4);
        var resumeRequest = ParseMessage(transport.ClientMessages[3]);
        AssertJsonString(
            "Original developer instructions.",
            resumeRequest,
            "params.developerInstructions",
            "resume keeps captured developer instructions");
        AssertJsonString(
            "Original base instructions.",
            resumeRequest,
            "params.baseInstructions",
            "resume keeps captured base instructions");
        transport.ServerSend("""{"id":2,"result":{"thread":{"id":"thr_instruction_source","turns":[]}}}""");
        await StateProbe.WaitForAsync(
            () => viewModel.StatusMessage.Contains("resumed", StringComparison.OrdinalIgnoreCase),
            "instruction source resumed");

        viewModel.ForkThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(5);
        var forkRequest = ParseMessage(transport.ClientMessages[4]);
        AssertJsonString(
            "Original developer instructions.",
            forkRequest,
            "params.developerInstructions",
            "fork inherits developer instructions");
        AssertJsonString(
            "Original base instructions.",
            forkRequest,
            "params.baseInstructions",
            "fork inherits base instructions");
        transport.ServerSend("""{"id":3,"result":{"thread":{"id":"thr_instruction_fork"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thr_instruction_fork",
            "instruction fork created");
        AssertEqual(
            "Original developer instructions.",
            viewModel.SelectedThread?.AppliedDeveloperInstructions,
            "forked state inherits developer instructions");
        AssertEqual(
            "Original base instructions.",
            viewModel.SelectedThread?.AppliedBaseInstructions,
            "forked state inherits base instructions");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model keeps legacy threads on Codex instruction defaults")]
    public async Task TestViewModelKeepsLegacyThreadInstructionDefaultsAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var settings = new AppSettings
        {
            CustomDeveloperInstructionsEnabled = true,
            CustomDeveloperInstructions = "New global developer instructions.",
            CustomBaseInstructionsEnabled = true,
            CustomBaseInstructions = "New global base instructions."
        };
        settings.ProjectThreads.Add(new PersistedProjectThread
        {
            ProjectPath = temp.Root,
            ThreadId = "thr_legacy_instructions",
            Title = "Legacy chat",
            IsActive = true,
            WorkspacePath = temp.Root
        });
        var settingsStore = new FakeSettingsStore(settings);
        var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn, settingsStore);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(
            () => viewModel.SelectedThread?.ThreadId == "thr_legacy_instructions",
            "legacy instruction thread selected");

        viewModel.ResumeThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        var resumeRequest = ParseMessage(transport.ClientMessages[2]);
        AssertTrue(ResolvePath(resumeRequest, "params.developerInstructions") is null,
            "legacy thread does not inherit new developer instructions");
        AssertTrue(ResolvePath(resumeRequest, "params.baseInstructions") is null,
            "legacy thread keeps the model's default base instructions");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_legacy_instructions","turns":[]}}}""");
        await StateProbe.WaitForAsync(
            () => viewModel.StatusMessage.Contains("resumed", StringComparison.OrdinalIgnoreCase),
            "legacy instruction thread resumed");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model forks archives and unarchives threads")]
    public async Task TestViewModelForksArchivesAndUnarchivesThreadsAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "fork project selected");

        viewModel.NewThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_source"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thr_source", "source thread created");

        viewModel.ForkThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(4);
        AssertJsonString("thread/fork", ParseMessage(transport.ClientMessages[3]), "method", "view model fork method");
        transport.ServerSend("""{"id":2,"result":{"thread":{"id":"thr_forked"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thr_forked", "fork selected");

        viewModel.ArchiveThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(5);
        transport.ServerSend("""{"id":3,"result":{}}""");
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.IsArchived == true, "thread archived");

        viewModel.UnarchiveThreadCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(6);
        transport.ServerSend("""{"id":4,"result":{"thread":{"id":"thr_forked"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.IsArchived == false, "thread unarchived");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model steers an active turn")]
    public async Task TestViewModelSteersActiveTurnAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var settingsStore = new FakeSettingsStore(new AppSettings { FollowUpBehavior = "steer" });
        var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn, settingsStore);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "steering project selected");

        viewModel.PromptText = "Start work.";
        var firstSubmit = ((AsyncRelayCommand)viewModel.SubmitPromptCommand).ExecuteAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_steer"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_steer"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "steer turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thr_steer");

        viewModel.SteeringText = "Concentrate on regression tests.";
        await StateProbe.WaitForAsync(() => viewModel.SteerTurnCommand.CanExecute(null), "steer command enabled");
        viewModel.SteerTurnCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(6);
        var steer = ParseMessage(transport.ClientMessages[5]);
        AssertJsonString("turn/steer", steer, "method", "view model steer method");
        AssertJsonString("turn_steer", steer, "params.expectedTurnId", "view model steer turn id");
        transport.ServerSend("""{"id":4,"result":{"turnId":"turn_steer"}}""");
        await StateProbe.WaitForAsync(() => string.IsNullOrWhiteSpace(viewModel.SteeringText), "steering composer cleared");

        viewModel.SteeringText = "Unsent follow-up guidance.";
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_steer","turn":{"id":"turn_steer","status":"completed"}}}""");
        await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "steered turn completed");
        AssertTrue(string.IsNullOrWhiteSpace(viewModel.SteeringText), "completed turn clears guidance draft");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model runs parallel project threads")]
    public async Task TestViewModelRunsParallelProjectThreadsAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var viewModel = CreateMainViewModel(transport, temp.Root, AuthReadiness.LikelySignedIn);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "parallel project selected");

        viewModel.PromptText = "First parallel task";
        var firstSubmit = ((AsyncRelayCommand)viewModel.SubmitPromptCommand).ExecuteAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_parallel_a"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_parallel_a"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "first parallel turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thr_parallel_a");
        await firstSubmit;

        viewModel.SteeringText = "Guidance for the first thread.";
        var newThread = ((AsyncRelayCommand)viewModel.NewThreadCommand).ExecuteAsync();
        await transport.WaitForClientMessageCountAsync(6);
        transport.ServerSend("""{"id":4,"result":{"thread":{"id":"thr_parallel_b"}}}""");
        await newThread;
        await StateProbe.WaitForAsync(
            () => viewModel.SelectedThread?.ThreadId == "thr_parallel_b" && !viewModel.IsTurnRunning,
            "second parallel thread selected and idle");
        AssertTrue(!viewModel.IsTurnRunning, "second thread composer remains available");
        AssertTrue(string.IsNullOrWhiteSpace(viewModel.SteeringText), "thread switch clears guidance draft");

        viewModel.PromptText = "Second parallel task";
        var secondSubmit = ((AsyncRelayCommand)viewModel.SubmitPromptCommand).ExecuteAsync();
        await transport.WaitForClientMessageCountAsync(7);
        transport.ServerSend("""{"id":5,"result":{"turn":{"id":"turn_parallel_b"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.ProjectThreads.All(thread => thread.IsRunning), "parallel running indicators");
        await CompleteAutomaticThreadRenameAsync(transport, "thr_parallel_b");
        await secondSubmit;

        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_parallel_a","turn":{"id":"turn_parallel_a","status":"completed"}}}""");
        await StateProbe.WaitForAsync(
            () => !viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_parallel_a").IsRunning,
            "first parallel indicator completed");
        AssertTrue(viewModel.IsTurnRunning, "second parallel turn remains active");

        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_parallel_b","turn":{"id":"turn_parallel_b","status":"completed"}}}""");
        await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "second parallel turn completed");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model restarts app-server after crash")]
    public async Task TestViewModelRestartsAppServerAfterCrashAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var firstTransport = new FakeAppServerTransport();
        await using var secondTransport = new FakeAppServerTransport();
        var processService = new SequenceCodexProcessService(firstTransport, secondTransport);
        var logger = new TestLogger();
        var viewModel = CreateMainViewModel(
            firstTransport,
            temp.Root,
            AuthReadiness.LikelySignedIn,
            processService: processService,
            logger: logger);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "recovery project selected");

        viewModel.NewThreadCommand.Execute(null);
        await firstTransport.WaitForClientMessageCountAsync(2);
        firstTransport.ServerSend("""{"id":0,"result":{}}""");
        await firstTransport.WaitForClientMessageCountAsync(3);
        firstTransport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_recover"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.AppServerHealth == "Codex connected", "initial app-server connected");

        firstTransport.ServerFail(new IOException("simulated crash"));
        await StateProbe.WaitForAsync(() => viewModel.AppServerHealth == "Codex reconnecting", "app-server recovering");

        var healthChanges = new MessageProbe<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.AppServerHealth))
            {
                healthChanges.Publish(viewModel.AppServerHealth);
            }
        };
        viewModel.LoadModelsCommand.Execute(null);
        await secondTransport.WaitForClientMessageCountAsync(2);
        secondTransport.ServerSend("""{"id":0,"result":{}}""");
        await secondTransport.WaitForClientMessageCountAsync(3);
        secondTransport.ServerSend("""{"id":1,"result":{"data":[]}}""");
        await healthChanges.WaitForAsync(
            health => health == "Codex connected",
            "app-server recovered");
        AssertEqual(2, processService.StartCount, "app-server restart count");
        var recoveryMetric = logger.Entries.Single(entry => entry.EventName == "app_server_recovered");
        AssertTrue(long.Parse(recoveryMetric.Properties?["elapsedMilliseconds"] ?? "-1") >= 0, "app-server recovery duration metric");
        Console.WriteLine($"METRIC app-server recovery: {recoveryMetric.Properties?["elapsedMilliseconds"]} ms");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model exit command requests close")]
    public async Task TestViewModelExitCommandRequestsCloseAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("Repo");
        var viewModel = CreateMainViewModel(transport, projectPath, AuthReadiness.LikelySignedIn);
        var requested = false;
        viewModel.CloseRequested += (_, _) => requested = true;

        viewModel.ExitApplicationCommand.Execute(null);

        await StateProbe.WaitForAsync(() => requested, "close requested");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model shutdown cancels running turn and disposes transport")]
    public async Task TestViewModelShutdownCancelsRunningTurnAndDisposesTransportAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("Repo");
        var settingsStore = new FakeSettingsStore();
        var terminals = new FakeTerminalService();
        var logger = new TestLogger();
        var viewModel = CreateMainViewModel(
            transport,
            projectPath,
            AuthReadiness.LikelySignedIn,
            settingsStore,
            terminalService: terminals,
            logger: logger);

        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase), "project selection");
        viewModel.StartTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.Sessions.Count == 1, "shutdown terminal started");

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

        await StateProbe.WaitForAsync(() => viewModel.IsTurnRunning, "turn running");
        await CompleteAutomaticThreadRenameAsync(transport, "thr_123");

        var shutdownTask = viewModel.ShutdownAsync();
        await transport.WaitForClientMessageCountAsync(6);

        var cancelRequest = ParseMessage(transport.ClientMessages[5]);
        AssertJsonString("turn/interrupt", cancelRequest, "method", "shutdown cancel method");
        AssertJsonString("thr_123", cancelRequest, "params.threadId", "shutdown cancel thread id");
        AssertJsonString("turn_456", cancelRequest, "params.turnId", "shutdown cancel turn id");

        transport.ServerSend(
            """
            {"id":4,"result":{"ok":true}}
            """);

        await shutdownTask;

        AssertTrue(!viewModel.IsTurnRunning, "shutdown clears running flag");
        AssertTrue(transport.IsDisposed, "shutdown disposes transport");
        AssertTrue(terminals.Sessions[0].IsDisposed, "shutdown disposes active terminal");
        AssertEqual("thr_123", settingsStore.SavedSettings.ProjectThreads.Single().ThreadId, "shutdown saves thread id");
        var shutdownMetric = logger.Entries.Single(entry => entry.EventName == "shutdown_completed");
        AssertEqual("1", shutdownMetric.Properties?["activeTurnsAtStart"], "shutdown active turn metric");
        AssertEqual("1", shutdownMetric.Properties?["terminalSessionsAtStart"], "shutdown terminal session metric");
        AssertTrue(long.Parse(shutdownMetric.Properties?["elapsedMilliseconds"] ?? "-1") >= 0, "shutdown duration metric");
        Console.WriteLine(
            $"METRIC shutdown: {shutdownMetric.Properties?["elapsedMilliseconds"]} ms with " +
            $"{shutdownMetric.Properties?["activeTurnsAtStart"]} active turn and " +
            $"{shutdownMetric.Properties?["terminalSessionsAtStart"]} terminal session");
    }

    [Fact(DisplayName = "view model starts worktree task in isolated cwd")]
    public async Task TestViewModelStartsWorktreeTaskInIsolatedCwdAsync()
    {
        using var temp = TempWorkspace.Create();
        var repository = temp.CreateDirectory("Repo");
        var worktreePath = temp.CreateDirectory("Repo.worktrees\\thr-worktree");
        var transport = new FakeAppServerTransport();
        var worktrees = new FakeWorktreeService(repository, worktreePath);
        var git = new FakeGitService(repository) { Branches = ["main", "feature/branch-picker"] };
        var interaction = new FakeUserInteractionService { WorktreeStartPointSelection = "feature/branch-picker" };
        var viewModel = CreateMainViewModel(
            transport,
            repository,
            AuthReadiness.LikelySignedIn,
            worktreeService: worktrees,
            userInteractionService: interaction,
            gitService: git);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "worktree project selected");

        viewModel.ProjectWorkspace.NewWorktreeThreadForProjectCommand.Execute(repository);
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thr_worktree"}}}""");
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thr_worktree", "worktree thread created");

        AssertEqual("worktree", viewModel.SelectedThread!.Mode, "thread worktree mode");
        AssertEqual(Path.GetFullPath(worktreePath), viewModel.ActiveWorkspacePath, "active worktree path label");
        AssertEqual("main", interaction.WorktreeBranchCatalogs.Single().DefaultStartPoint, "current branch is the picker default");
        AssertEqual("feature/branch-picker", worktrees.CreateRequests.Single().StartPoint, "selected start point reaches worktree creation");
        AssertEqual(2, git.BranchCatalogRequestCount, "branch catalog is refreshed once for stale-selection validation");
        viewModel.PromptText = "Make an isolated change.";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(4);
        var startTurn = ParseMessage(transport.ClientMessages[3]);

        AssertJsonString(Path.GetFullPath(worktreePath), startTurn, "params.cwd", "worktree turn cwd");
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn_worktree"}}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thr_worktree","turn":{"id":"turn_worktree","status":"completed"}}}""");
        await StateProbe.WaitForAsync(() => !viewModel.IsTurnRunning, "worktree turn completed");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "canceling worktree branch selection has no creation side effects")]
    public async Task TestViewModelCancelsWorktreeBranchSelectionAsync()
    {
        using var temp = TempWorkspace.Create();
        var repository = temp.CreateDirectory("Repo");
        var transport = new FakeAppServerTransport();
        var settingsStore = new FakeSettingsStore();
        var worktrees = new FakeWorktreeService(repository, Path.Combine(temp.Root, "Repo.worktrees", "canceled"));
        var git = new FakeGitService(repository);
        var interaction = new FakeUserInteractionService { CancelWorktreeStartPointSelection = true };
        var viewModel = CreateMainViewModel(
            transport,
            repository,
            AuthReadiness.LikelySignedIn,
            settingsStore: settingsStore,
            worktreeService: worktrees,
            userInteractionService: interaction,
            gitService: git);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "canceled worktree project selected");
        var recentProjectCount = settingsStore.SavedSettings.RecentProjects.Count;
        var threadStartCount = transport.ClientMessages.Count(message =>
            ResolvePath(ParseMessage(message), "method")?.GetValue<string>() == "thread/start");

        viewModel.ProjectWorkspace.NewWorktreeThreadForProjectCommand.Execute(repository);
        await StateProbe.WaitForAsync(
            () => viewModel.StatusMessage.Contains("canceled", StringComparison.OrdinalIgnoreCase),
            "worktree branch selection canceled");

        AssertEqual(0, worktrees.CreateRequests.Count, "cancellation creates no worktree");
        AssertEqual(0, settingsStore.SavedSettings.ProjectThreads.Count, "cancellation persists no thread");
        AssertEqual(recentProjectCount, settingsStore.SavedSettings.RecentProjects.Count, "cancellation does not change project settings");
        AssertEqual(
            threadStartCount,
            transport.ClientMessages.Count(message =>
                ResolvePath(ParseMessage(message), "method")?.GetValue<string>() == "thread/start"),
            "cancellation starts no Codex thread");
        AssertEqual(1, interaction.WorktreeBranchCatalogs.Count, "cancellation occurs in the branch picker");
        AssertEqual("Current checkout", viewModel.NewThreadWorkspaceMode, "cancellation restores current-checkout creation mode");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model starts terminal in active worktree")]
    public async Task TestViewModelStartsTerminalInActiveWorktreeAsync()
    {
        using var temp = TempWorkspace.Create();
        var project = temp.CreateDirectory("Repo");
        var worktree = temp.CreateDirectory("Repo.worktrees\\terminal-thread");
        var settings = new AppSettings
        {
            ProjectThreads =
            [
                new PersistedProjectThread
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
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thr_terminal", "terminal worktree thread selected");

        viewModel.StartTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.StartRequests.Count == 1, "terminal session started");

        AssertEqual(Path.GetFullPath(worktree), terminals.StartRequests[0].WorkingDirectory, "terminal worktree cwd");
        AssertEqual(Path.GetFullPath(worktree), viewModel.TerminalWorkingDirectory, "terminal cwd indicator");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model keeps terminal output isolated by thread")]
    public async Task TestViewModelKeepsTerminalOutputIsolatedByThreadAsync()
    {
        using var temp = TempWorkspace.Create();
        var project = temp.CreateDirectory("Repo");
        var settings = new AppSettings
        {
            ProjectThreads =
            [
                new PersistedProjectThread { ProjectPath = project, ThreadId = "thr_one", Title = "One", IsActive = true, WorkspacePath = project },
                new PersistedProjectThread { ProjectPath = project, ThreadId = "thr_two", Title = "Two", WorkspacePath = project }
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
        await StateProbe.WaitForAsync(() => viewModel.SelectedThread?.ThreadId == "thr_one", "first terminal thread selected");
        viewModel.StartTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.Sessions.Count == 1, "first terminal started");
        terminals.Sessions[0].EmitOutput("ONE_OUTPUT");
        await StateProbe.WaitForAsync(() => viewModel.TerminalOutput.Contains("ONE_OUTPUT", StringComparison.Ordinal), "first terminal output shown");

        viewModel.SelectedThread = viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_two");
        viewModel.StartTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.Sessions.Count == 2, "second terminal started");
        terminals.Sessions[1].EmitOutput("TWO_OUTPUT");
        await StateProbe.WaitForAsync(() => viewModel.TerminalOutput.Contains("TWO_OUTPUT", StringComparison.Ordinal), "second terminal output shown");
        AssertTrue(!viewModel.TerminalOutput.Contains("ONE_OUTPUT", StringComparison.Ordinal), "first output hidden from second thread");

        viewModel.SelectedThread = viewModel.ProjectThreads.Single(thread => thread.ThreadId == "thr_one");
        AssertTrue(viewModel.TerminalOutput.Contains("ONE_OUTPUT", StringComparison.Ordinal), "first output restored");
        AssertTrue(!viewModel.TerminalOutput.Contains("TWO_OUTPUT", StringComparison.Ordinal), "second output isolated");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model batches terminal presentation updates")]
    public async Task TestViewModelBatchesTerminalPresentationUpdatesAsync()
    {
        using var temp = TempWorkspace.Create();
        var terminals = new FakeTerminalService();
        var logger = new TestLogger();
        var viewModel = CreateMainViewModel(
            new FakeAppServerTransport(),
            temp.Root,
            AuthReadiness.LikelySignedIn,
            terminalService: terminals,
            logger: logger);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "batched terminal project selected");
        viewModel.StartTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.Sessions.Count == 1, "batched terminal started");

        for (var index = 0; index < 100; index++)
        {
            terminals.Sessions[0].EmitOutput("x");
        }

        await StateProbe.WaitForAsync(() => viewModel.TerminalOutput.Length == 100, "batched terminal output presented");
        viewModel.KillTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => logger.Entries.Any(entry => entry.EventName == "terminal_output_metrics"), "terminal metrics recorded");

        var metrics = logger.Entries.Single(entry => entry.EventName == "terminal_output_metrics");
        AssertEqual("100", metrics.Properties?["receivedChunks"], "terminal received chunk metric");
        AssertEqual("100", metrics.Properties?["receivedCharacters"], "terminal received character metric");
        AssertTrue(int.Parse(metrics.Properties?["presentationUpdates"] ?? "0") <= 2, "terminal burst is presented in at most two updates");
        Console.WriteLine(
            $"METRIC terminal presentation: {metrics.Properties?["receivedChunks"]} chunks -> " +
            $"{metrics.Properties?["presentationUpdates"]} UI updates");
        await viewModel.DisposeAsync();
    }

    [Fact(DisplayName = "view model terminal actions and shutdown own sessions")]
    public async Task TestViewModelTerminalActionsAndShutdownOwnSessionsAsync()
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
        await StateProbe.WaitForAsync(() => viewModel.SelectedProjectPath is not null, "terminal project selected");
        viewModel.StartTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.Sessions.Count == 1, "project terminal started");

        viewModel.TerminalInput = "Get-Date";
        viewModel.SendTerminalInputCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.Sessions[0].Inputs.Count == 1, "terminal input sent");
        AssertEqual("Get-Date\r\n", terminals.Sessions[0].Inputs[0], "terminal command newline");
        terminals.Sessions[0].EmitOutput("CLEAR_ME");
        await StateProbe.WaitForAsync(() => viewModel.TerminalOutput.Contains("CLEAR_ME", StringComparison.Ordinal), "terminal output before clear");
        viewModel.ClearTerminalCommand.Execute(null);
        AssertEqual(string.Empty, viewModel.TerminalOutput, "terminal output cleared");

        viewModel.KillTerminalCommand.Execute(null);
        await StateProbe.WaitForAsync(() => terminals.Sessions[0].StopCount == 1, "terminal killed");
        await viewModel.DisposeAsync();
        AssertTrue(terminals.Sessions.All(session => session.IsDisposed), "all terminal sessions disposed on shutdown");
    }

}
