using System.Text.Json.Nodes;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;
using SynthiaCode.Core.Workspaces;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Workspaces;

internal static class PromptEditingTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("app-server client rolls back prompt history", AppServerClientRollsBackPromptHistoryAsync),
        ("thread service retains previous prompt versions", ThreadServiceRetainsPreviousPromptVersionsAsync),
        ("task view model edits and resubmits a prompt", TaskViewModelEditsAndResubmitsPromptAsync),
        ("prompt edit rolls back and continues the selected thread", PromptEditRollsBackAndContinuesSelectedThreadAsync),
        ("task transcript exposes prompt edit controls", TaskTranscriptExposesPromptEditControlsAsync)
    ];

    private static async Task AppServerClientRollsBackPromptHistoryAsync()
    {
        await using var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("prompt_edit_tests", "Prompt Edit Tests", "1.0.0"));
        await CompleteInitializeAsync(client, transport);

        var rollbackTask = client.RollbackThreadAsync(new CodexThreadRollbackRequest("thread-edit", 2));
        await transport.WaitForClientMessageCountAsync(3);
        var request = ParseMessage(transport.ClientMessages[2]);
        Assert(ReadString(request, "method") == "thread/rollback", "rollback method");
        Assert(ReadString(request, "params.threadId") == "thread-edit", "rollback thread id");
        Assert(ReadInt(request, "params.numTurns") == 2, "rollback count");

        transport.ServerSend(
            """
            {"id":1,"result":{"thread":{"id":"thread-edit","turns":[{"id":"turn-1","status":"completed","items":[{"type":"userMessage","content":[{"type":"text","text":"Keep this prompt"}]},{"type":"agentMessage","text":"Keep this answer"}]}]}}}
            """);
        var result = await rollbackTask;

        Assert(result.ThreadId == "thread-edit", "rollback result thread id");
        Assert(result.Turns.Single().UserPrompt == "Keep this prompt", "rollback parses retained prompt");
        Assert(result.Turns.Single().AssistantResponse == "Keep this answer", "rollback parses retained response");
    }

    private static Task ThreadServiceRetainsPreviousPromptVersionsAsync()
    {
        var service = new CodexThreadService();
        service.Restore(
            "thread-edit",
            "Second answer",
            null,
            null,
            conversationTurns:
            [
                CompletedTurn("turn-1", "Original prompt", "Original answer"),
                CompletedTurn("turn-2", "Later prompt", "Later answer")
            ]);

        var original = service.ConversationTurns[0];
        Assert(service.GetActiveRollbackTurnCount(original) == 2, "editing the first prompt rolls back it and later active turns");
        service.SupersedeTurnsFrom(original);
        var edited = service.BeginTurn("Edited prompt");

        Assert(service.ConversationTurns.Count == 3, "previous and edited turns remain visible");
        Assert(original.UserPrompt == "Original prompt", "previous prompt remains unchanged");
        Assert(original.AssistantResponse == "Original answer", "previous response remains unchanged");
        Assert(original.IsSuperseded, "edited source is marked as a previous version");
        Assert(service.ConversationTurns[1].IsSuperseded, "later rolled-back turn is retained as a previous version");
        Assert(!edited.IsSuperseded && edited.UserPrompt == "Edited prompt", "edited prompt is the active version");

        var restored = new CodexThreadService();
        restored.Restore("thread-edit", string.Empty, null, null, conversationTurns: service.SnapshotConversation());
        Assert(restored.ConversationTurns[0].IsSuperseded, "previous-version state survives persistence");
        Assert(restored.ConversationTurns[0].AssistantResponse == "Original answer", "previous response survives persistence");
        return Task.CompletedTask;
    }

    private static async Task TaskViewModelEditsAndResubmitsPromptAsync()
    {
        CodexConversationTurn? submittedTurn = null;
        string? submittedText = null;
        var viewModel = new TaskViewModel(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => false,
            () => false,
            editPrompt: (turn, text) =>
            {
                submittedTurn = turn;
                submittedText = text;
                return Task.FromResult(true);
            });
        var service = new CodexThreadService();
        service.Restore(
            "thread-edit",
            "Original answer",
            null,
            null,
            conversationTurns: [CompletedTurn("turn-1", "Original prompt", "Original answer")]);
        viewModel.UseThreadService(service);
        var turn = service.ConversationTurns.Single();

        viewModel.BeginPromptEditCommand.Execute(turn);
        Assert(turn.IsPromptEditing, "edit mode opens");
        Assert(turn.EditedPrompt == "Original prompt", "editor starts with the submitted prompt");
        turn.EditedPrompt = "Edited prompt";
        viewModel.SubmitPromptEditCommand.Execute(turn);
        await WaitUntilAsync(() => !turn.IsPromptEditing, "prompt edit submission");

        Assert(ReferenceEquals(submittedTurn, turn), "the selected turn is resubmitted");
        Assert(submittedText == "Edited prompt", "trimmed edited text is resubmitted");
    }

    private static async Task PromptEditRollsBackAndContinuesSelectedThreadAsync()
    {
        using var temp = TempWorkspace.Create();
        await using var transport = new FakeAppServerTransport();
        var projectPath = temp.CreateDirectory("PromptEditRepo");
        var viewModel = CreateViewModel(transport, projectPath);
        await viewModel.InitializeAsync();
        viewModel.BrowseProjectCommand.Execute(null);
        await WaitUntilAsync(
            () => string.Equals(viewModel.SelectedProjectPath, projectPath, StringComparison.OrdinalIgnoreCase),
            "prompt edit project selection");

        viewModel.PromptText = "Original prompt";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-edit"}}}""");
        await transport.WaitForClientMessageCountAsync(4);
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-original"}}}""");
        await WaitUntilAsync(() => viewModel.IsTurnRunning, "original prompt running");
        await CompleteAutomaticThreadRenameAsync(transport, "thread-edit");
        transport.ServerSend("""{"method":"item/agentMessage/delta","params":{"threadId":"thread-edit","turnId":"turn-original","itemId":"answer-original","delta":"Original answer"}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-edit","turn":{"id":"turn-original","status":"completed","items":[]}}}""");
        await WaitUntilAsync(() => !viewModel.IsTurnRunning, "original prompt completed");

        viewModel.PromptText = "Later prompt";
        viewModel.SubmitPromptCommand.Execute(null);
        await transport.WaitForClientMessageCountAsync(6);
        transport.ServerSend("""{"id":4,"result":{"turn":{"id":"turn-later"}}}""");
        await WaitUntilAsync(() => viewModel.IsTurnRunning, "later prompt running");
        transport.ServerSend("""{"method":"item/agentMessage/delta","params":{"threadId":"thread-edit","turnId":"turn-later","itemId":"answer-later","delta":"Later answer"}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-edit","turn":{"id":"turn-later","status":"completed","items":[]}}}""");
        await WaitUntilAsync(() => !viewModel.IsTurnRunning, "later prompt completed");

        var original = viewModel.TaskWorkspace.ConversationTurns[0];
        viewModel.TaskWorkspace.BeginPromptEditCommand.Execute(original);
        original.EditedPrompt = "Edited prompt";
        viewModel.TaskWorkspace.SubmitPromptEditCommand.Execute(original);
        await transport.WaitForClientMessageCountAsync(7);
        var rollback = ParseMessage(transport.ClientMessages[6]);
        Assert(ReadString(rollback, "method") == "thread/rollback", "editing uses thread rollback");
        Assert(ReadInt(rollback, "params.numTurns") == 2, "editing removes the selected and later active turns from server history");
        transport.ServerSend("""{"id":5,"result":{"thread":{"id":"thread-edit","turns":[]}}}""");

        await transport.WaitForClientMessageCountAsync(8);
        var editedStart = ParseMessage(transport.ClientMessages[7]);
        Assert(ReadString(editedStart, "method") == "turn/start", "edited prompt starts a replacement turn");
        Assert(ReadString(editedStart, "params.input.0.text") == "Edited prompt", "replacement turn uses edited text");
        transport.ServerSend("""{"id":6,"result":{"turn":{"id":"turn-edited"}}}""");
        await WaitUntilAsync(() => viewModel.IsTurnRunning, "edited prompt running");

        Assert(viewModel.TaskWorkspace.ConversationTurns.Count == 3, "old prompts and edited prompt remain in the transcript");
        Assert(viewModel.TaskWorkspace.ConversationTurns[0].IsSuperseded, "original prompt is a previous version");
        Assert(viewModel.TaskWorkspace.ConversationTurns[0].AssistantResponse == "Original answer", "original response is retained");
        Assert(viewModel.TaskWorkspace.ConversationTurns[1].IsSuperseded, "rolled-back follow-up is retained");
        Assert(viewModel.TaskWorkspace.ConversationTurns[1].AssistantResponse == "Later answer", "rolled-back response is retained");
        Assert(viewModel.TaskWorkspace.ConversationTurns[2].UserPrompt == "Edited prompt", "edited prompt is visible");
        transport.ServerSend("""{"method":"item/agentMessage/delta","params":{"threadId":"thread-edit","turnId":"turn-edited","itemId":"answer-edited","delta":"Edited answer"}}""");
        transport.ServerSend("""{"method":"turn/completed","params":{"threadId":"thread-edit","turn":{"id":"turn-edited","status":"completed","items":[]}}}""");
        await WaitUntilAsync(() => !viewModel.IsTurnRunning, "edited prompt completed");
        Assert(viewModel.TaskWorkspace.ConversationTurns[2].AssistantResponse == "Edited answer", "edited response is visible beside previous responses");
        await viewModel.DisposeAsync();
    }

    private static Task TaskTranscriptExposesPromptEditControlsAsync()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Views", "TaskView.xaml"));
        Assert(xaml.Contains("BeginPromptEditCommand", StringComparison.Ordinal), "transcript has an edit action");
        Assert(xaml.Contains("EditedPrompt", StringComparison.Ordinal), "transcript binds an inline prompt editor");
        Assert(xaml.Contains("SubmitPromptEditCommand", StringComparison.Ordinal), "transcript has an edited prompt submit action");
        Assert(xaml.Contains("Previous version", StringComparison.Ordinal), "transcript labels retained previous versions");
        return Task.CompletedTask;
    }

    private static CodexConversationTurnSnapshot CompletedTurn(string turnId, string prompt, string response) => new()
    {
        TurnId = turnId,
        UserPrompt = prompt,
        AssistantResponse = response,
        Status = CodexTurnStatus.Completed,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        CompletedAt = DateTimeOffset.UtcNow
    };

    private static MainViewModel CreateViewModel(FakeAppServerTransport transport, string projectPath)
    {
        var logger = new TestLogger();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("prompt_edit_tests", "Prompt Edit Tests", "1.0.0"));
        return new MainViewModel(
            new FakeSettingsStore(),
            new FakeCodexDiscoveryService(new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation")),
            coordinator,
            new FakeAuthService(new AuthenticationState(AuthReadiness.LikelySignedIn, "Ready", "Test auth", @"C:\Users\Test\.codex")),
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
            new GeneralWorkspaceService(Path.Combine(projectPath, ".synthiacode-test-data")));
    }

    private static async Task CompleteInitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"codex-test"}}""");
        await initialize;
    }

    private static JsonObject ParseMessage(string line) =>
        JsonNode.Parse(line) as JsonObject ?? throw new InvalidOperationException($"Message was not an object: {line}");

    private static string? ReadString(JsonNode node, string path) => ResolvePath(node, path)?.GetValue<string>();

    private static int? ReadInt(JsonNode node, string path) => ResolvePath(node, path)?.GetValue<int>();

    private static JsonNode? ResolvePath(JsonNode node, string path)
    {
        JsonNode? current = node;
        foreach (var segment in path.Split('.'))
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
        }
        return current;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            try
            {
                await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException($"Timed out waiting for {label}.");
            }
        }
    }

    private static async Task CompleteAutomaticThreadRenameAsync(
        FakeAppServerTransport transport,
        string threadId)
    {
        await WaitUntilAsync(
            () => transport.ClientMessages.Any(message =>
                ReadString(ParseMessage(message), "method") == "thread/name/set" &&
                ReadString(ParseMessage(message), "params.threadId") == threadId),
            $"automatic rename for {threadId}");
        var request = transport.ClientMessages
            .Select(ParseMessage)
            .Single(message =>
                ReadString(message, "method") == "thread/name/set" &&
                ReadString(message, "params.threadId") == threadId);
        var requestId = request["id"]?.ToJsonString()
            ?? throw new InvalidOperationException($"Automatic rename for '{threadId}' did not include an id.");
        transport.ServerSend($"{{\"id\":{requestId},\"result\":{{}}}}");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SynthiaCode.sln")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
