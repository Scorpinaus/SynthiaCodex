using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;
using SynthiaCode.Harnesses.Codex;
using SynthiaCode.Infrastructure.Codex;
using SynthiaCode.Infrastructure.Git;
using SynthiaCode.Infrastructure.Projects;
using SynthiaCode.Infrastructure.Workspaces;

internal static class CodeReviewWorkflowTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("code review protocol sends every official target", ProtocolSendsOfficialTargetsAsync),
        ("code review protocol restores dedicated reviewer history", ProtocolRestoresReviewerHistoryAsync),
        ("code review reducer projects and persists findings", ReducerProjectsAndPersistsFindingsAsync),
        ("code review use case owns pending and failed transitions", UseCaseOwnsTransitionsAsync),
        ("code review Git catalog discovers branches and commits", GitCatalogDiscoversTargetsAsync),
        ("code review main workflow streams findings through review start", MainWorkflowStreamsFindingsAsync),
        ("code review composer routes exact slash command", ComposerRoutesExactSlashCommandAsync),
        ("code review picker renders accessible target controls", PickerRendersAccessibleTargetsAsync)
    ];

    private static async Task ProtocolSendsOfficialTargetsAsync()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("review_tests", "Review tests", "1.0"));
        await InitializeAsync(client, transport);

        var cases = new[]
        {
            new ReviewProtocolCase(
                CodexReviewTarget.UncommittedChanges(),
                "uncommittedChanges",
                null,
                null,
                null),
            new ReviewProtocolCase(
                CodexReviewTarget.BaseBranch("origin/main"),
                "baseBranch",
                "origin/main",
                null,
                null),
            new ReviewProtocolCase(
                CodexReviewTarget.Commit("1234567deadbeef", "Fix parser edge case"),
                "commit",
                null,
                "1234567deadbeef",
                null),
            new ReviewProtocolCase(
                CodexReviewTarget.Custom("Focus on cancellation races."),
                "custom",
                null,
                null,
                "Focus on cancellation races.")
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var test = cases[index];
            var start = client.StartReviewAsync(new CodexReviewStartRequest(
                "thread-review",
                test.Target,
                CodexReviewDelivery.Inline));
            await transport.WaitForClientMessageCountAsync(3 + index);
            var request = Parse(transport.ClientMessages[2 + index]);
            Assert(ReadString(request, "method") == "review/start", "review uses review/start");
            Assert(ReadString(request, "params.threadId") == "thread-review", "review sends the owning thread");
            Assert(ReadString(request, "params.delivery") == "inline", "review sends inline delivery");
            Assert(ReadString(request, "params.target.type") == test.Type, $"{test.Type} target type");
            Assert(ReadString(request, "params.target.branch") == test.Branch, $"{test.Type} branch field");
            Assert(ReadString(request, "params.target.sha") == test.Sha, $"{test.Type} sha field");
            Assert(ReadString(request, "params.target.instructions") == test.Instructions, $"{test.Type} instructions field");
            if (test.Type == "commit")
            {
                Assert(ReadString(request, "params.target.title") == "Fix parser edge case", "commit title is preserved");
            }

            transport.ServerSend(new JsonObject
            {
                ["id"] = index + 1,
                ["result"] = new JsonObject
                {
                    ["turn"] = new JsonObject { ["id"] = $"turn-review-{index}" },
                    ["reviewThreadId"] = "thread-review"
                }
            }.ToJsonString());
            var result = await start;
            Assert(result.TurnId == $"turn-review-{index}", "review parses the turn id");
            Assert(result.ReviewThreadId == "thread-review", "review parses the delivery thread");
        }

        var missingReviewThread = client.StartReviewAsync(new CodexReviewStartRequest(
            "thread-review",
            CodexReviewTarget.UncommittedChanges(),
            CodexReviewDelivery.Inline));
        await transport.WaitForClientMessageCountAsync(7);
        transport.ServerSend("""{"id":5,"result":{"turn":{"id":"turn-review-missing-thread"}}}""");
        await AssertThrowsAsync<CodexAppServerProtocolException>(
            () => missingReviewThread,
            "review results without reviewThreadId are rejected");

        var missingTurn = client.StartReviewAsync(new CodexReviewStartRequest(
            "thread-review",
            CodexReviewTarget.UncommittedChanges(),
            CodexReviewDelivery.Inline));
        await transport.WaitForClientMessageCountAsync(8);
        transport.ServerSend("""{"id":6,"result":{"reviewThreadId":"thread-review"}}""");
        await AssertThrowsAsync<CodexAppServerProtocolException>(
            () => missingTurn,
            "review results without turn.id are rejected");

        AssertThrows<ArgumentException>(
            () => CodexReviewTarget.BaseBranch("  "),
            "empty base branches are rejected");
        AssertThrows<ArgumentException>(
            () => CodexReviewTarget.Custom(string.Empty),
            "empty custom instructions are rejected");
    }

    private static async Task ProtocolRestoresReviewerHistoryAsync()
    {
        var transport = new FakeAppServerTransport();
        await using var client = new CodexAppServerClient(
            transport,
            new CodexAppServerClientMetadata("review_history", "Review history", "1.0"));
        await InitializeAsync(client, transport);

        var resume = client.ResumeThreadAsync(new CodexThreadResumeRequest("thread-review", @"C:\repo", null));
        await transport.WaitForClientMessageCountAsync(3);
        transport.ServerSend(
            """
            {"id":1,"result":{"thread":{"id":"thread-review","turns":[{"id":"turn-review","status":"completed","items":[{"type":"userMessage","id":"user-review","content":[{"type":"text","text":"Review changes against 'main'"}]},{"type":"enteredReviewMode","id":"review-enter","review":"changes against 'main'"},{"type":"exitedReviewMode","id":"review-exit","review":"[P1] Guard the null path — src/App.cs:42"}]}]}}}
            """);

        var restored = await resume;
        var turn = restored.Turns!.Single();
        Assert(turn.IsCodeReview, "history identifies a review turn");
        Assert(turn.ReviewScope == "changes against 'main'", "history restores the review scope");
        Assert(turn.AssistantResponse.Contains("[P1]", StringComparison.Ordinal), "history restores final findings");
    }

    private static Task ReducerProjectsAndPersistsFindingsAsync()
    {
        var service = new CodexThreadService();
        service.Restore("thread-review", null, null, null);
        service.BeginTurn("Review uncommitted changes");
        service.ApplyNotification(Notification(
            "item/started",
            """
            {"threadId":"thread-review","turnId":"turn-review","item":{"type":"enteredReviewMode","id":"review-enter","review":"uncommitted changes"}}
            """));
        service.ApplyNotification(Notification(
            "item/completed",
            """
            {"threadId":"thread-review","turnId":"turn-review","item":{"type":"exitedReviewMode","id":"review-exit","review":"[P1] Avoid data loss — src/Store.cs:88"}}
            """));
        service.ApplyNotification(Notification(
            "turn/completed",
            """
            {"threadId":"thread-review","turn":{"id":"turn-review","status":"completed","items":[]}}
            """));

        var review = service.ConversationTurns.Single();
        Assert(review.IsCodeReview, "live review is marked as code review");
        Assert(review.ReviewScope == "uncommitted changes", "live review exposes its scope");
        Assert(review.AssistantResponse.Contains("src/Store.cs:88", StringComparison.Ordinal), "final review is the assistant response");
        Assert(service.TimelineItems.Any(item => item.Kind == CodexTimelineItemKind.CodeReview), "review lifecycle is first-class activity");

        var restored = new CodexThreadService();
        restored.Restore("thread-review", service.FinalResponse, service.TimelineItems, service.RawEvents,
            conversationTurns: service.SnapshotConversation());
        var restoredReview = restored.ConversationTurns.Single();
        Assert(restoredReview.IsCodeReview && restoredReview.ReviewScope == "uncommitted changes", "snapshot preserves review metadata");
        Assert(restoredReview.AssistantResponse == review.AssistantResponse, "snapshot preserves findings");
        return Task.CompletedTask;
    }

    private static async Task UseCaseOwnsTransitionsAsync()
    {
        var threadWorkspace = new CodexThreadWorkspace();
        threadWorkspace.GetOrCreate("thread-review");
        var conversations = new ConversationWorkflowController(
            new ThreadStore(),
            threadWorkspace,
            new CodexFollowUpQueueWorkspace());
        conversations.Select("thread-review");
        var feature = new FakeReviewFeature(request => Task.FromResult(
            new CodexReviewStartResult("turn-review", request.ThreadId)));
        var useCase = new CodeReviewUseCaseService(feature, conversations);

        var started = await useCase.StartAsync(new CodeReviewExecutionRequest(
            "thread-review",
            CodexReviewTarget.UncommittedChanges()));
        Assert(started.TurnId == "turn-review", "use case returns the bound review turn");
        Assert(started.Snapshot.ConversationTurns.Single().TurnId == "turn-review", "pending review is bound");
        Assert(conversations.IsRunning("thread-review"), "review is registered as running");

        var failedWorkspace = new CodexThreadWorkspace();
        failedWorkspace.GetOrCreate("thread-failed");
        var failedConversations = new ConversationWorkflowController(
            new ThreadStore(),
            failedWorkspace,
            new CodexFollowUpQueueWorkspace());
        var failedUseCase = new CodeReviewUseCaseService(
            new FakeReviewFeature(_ => Task.FromException<CodexReviewStartResult>(new InvalidOperationException("review unavailable"))),
            failedConversations);
        await AssertThrowsAsync<InvalidOperationException>(
            () => failedUseCase.StartAsync(new CodeReviewExecutionRequest(
                "thread-failed",
                CodexReviewTarget.Custom("Check disposal."))),
            "startup failure is propagated");
        var failed = failedConversations.GetSnapshot("thread-failed").ConversationTurns.Single();
        Assert(failed.Status == CodexTurnStatus.Failed, "failed pending review is closed");
        Assert(failed.AssistantResponse.Contains("review unavailable", StringComparison.Ordinal), "failed review explains the error");
        Assert(!failedConversations.IsRunning("thread-failed"), "failed review is not left running");
    }

    private static async Task GitCatalogDiscoversTargetsAsync()
    {
        using var temp = TempWorkspace.Create();
        var repository = temp.CreateDirectory("ReviewRepo");
        await RunGitAsync(repository, "init", "-b", "main");
        await RunGitAsync(repository, "config", "user.name", "SynthiaCode Tests");
        await RunGitAsync(repository, "config", "user.email", "tests@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "first");
        await RunGitAsync(repository, "add", "README.md");
        await RunGitAsync(repository, "commit", "-m", "Initial review fixture");
        await RunGitAsync(repository, "branch", "release/next");
        await File.AppendAllTextAsync(Path.Combine(repository, "README.md"), Environment.NewLine + "second");
        await RunGitAsync(repository, "add", "README.md");
        await RunGitAsync(repository, "commit", "-m", "Exercise review targets");

        var catalog = await new GitService(new TestLogger()).GetReviewCatalogAsync(repository);
        Assert(catalog.RepositoryRoot == Path.GetFullPath(repository), "catalog resolves repository root");
        Assert(catalog.CurrentBranch == "main", "catalog identifies the current branch");
        Assert(catalog.BaseBranches.Contains("release/next", StringComparer.Ordinal), "catalog includes alternate base branches");
        Assert(!catalog.BaseBranches.Contains("main", StringComparer.Ordinal), "catalog excludes the current branch");
        Assert(catalog.Commits.Count >= 2, "catalog includes recent commits");
        Assert(catalog.Commits[0].Title == "Exercise review targets", "commits are newest first");
        Assert(catalog.Commits.All(commit => commit.Sha.Length == 40 && commit.ShortSha.Length > 0), "commit identities are selectable");
    }

    private static async Task ComposerRoutesExactSlashCommandAsync()
    {
        var normalSubmitCount = 0;
        var conversation = new TaskConversationActionStub
        {
            Submit = () =>
            {
                normalSubmitCount++;
                return Task.CompletedTask;
            }
        };
        var reviews = new ReviewActionStub();
        await using var viewModel = new TaskViewModel(
            conversation,
            conversation,
            conversation,
            conversation,
            conversation,
            goalActions: conversation,
            codeReviewActions: reviews);

        viewModel.Prompt = " /review ";
        viewModel.SubmitCommand.Execute(null);
        await WaitUntilAsync(() => reviews.StartCount == 1, "exact review command routed");
        Assert(reviews.StartCount == 1 && normalSubmitCount == 0, "exact slash command avoids a normal turn");

        viewModel.Prompt = "/review focus on tests";
        viewModel.SubmitCommand.Execute(null);
        await WaitUntilAsync(() => normalSubmitCount == 1, "non-exact command submitted normally");
        Assert(viewModel.StartCodeReviewCommand.CanExecute(null), "visible review action shares review availability");
    }

    private static async Task MainWorkflowStreamsFindingsAsync()
    {
        using var temp = TempWorkspace.Create();
        var projectPath = temp.CreateDirectory("ReviewWorkflow");
        await using var transport = new FakeAppServerTransport();
        var logger = new TestLogger();
        var coordinator = new AppServerSessionCoordinator(
            new FakeCodexProcessService(transport),
            logger,
            new CodexAppServerClientMetadata("review_workflow", "Review workflow", "1.0"));
        await using var viewModel = WorkspaceActionStubs.CreateMainViewModel(
            new FakeSettingsStore(),
            new FakeCodexDiscoveryService(new CodexInstallation(
                true,
                @"C:\Tools\codex.exe",
                "codex test",
                "Codex test",
                "Test installation")),
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
            new FakeUserInteractionService
            {
                ReviewTargetSelection = CodexReviewTarget.UncommittedChanges()
            },
            new FakeThemeService(),
            new FakeCodexCliUtilityRunner(),
            new ThreadStore(),
            new CodexThreadWorkspace(),
            new FakeTerminalService(),
            logger,
            new GeneralWorkspaceService(Path.Combine(projectPath, ".synthiacode-test-data")));

        await viewModel.InitializeAsync();
        await ((AsyncRelayCommand)viewModel.BrowseProjectCommand).ExecuteAsync();
        viewModel.TaskWorkspace.Prompt = "/review";
        viewModel.TaskWorkspace.SubmitCommand.Execute(null);

        await transport.WaitForClientMessageCountAsync(2);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"review-tests","platformFamily":"windows","platformOs":"windows"}}""");
        await transport.WaitForClientMessageCountAsync(3);
        var threadStart = Parse(transport.ClientMessages[2]);
        Assert(ReadString(threadStart, "method") == "thread/start", "review creates a chat when needed");
        transport.ServerSend("""{"id":1,"result":{"thread":{"id":"thread-review"}}}""");

        await transport.WaitForClientMessageCountAsync(4);
        var reviewStart = Parse(transport.ClientMessages[3]);
        Assert(ReadString(reviewStart, "method") == "review/start", "main workflow uses the dedicated review method");
        Assert(ReadString(reviewStart, "params.target.type") == "uncommittedChanges", "picker target reaches app-server");
        transport.ServerSend("""{"id":2,"result":{"turn":{"id":"turn-review"},"reviewThreadId":"thread-review"}}""");
        await WaitUntilAsync(() => viewModel.IsTurnRunning, "review turn running");

        transport.ServerSend(
            """
            {"method":"item/started","params":{"threadId":"thread-review","turnId":"turn-review","item":{"type":"enteredReviewMode","id":"review-enter","review":"uncommitted changes"}}}
            """);
        transport.ServerSend(
            """
            {"method":"item/completed","params":{"threadId":"thread-review","turnId":"turn-review","item":{"type":"exitedReviewMode","id":"review-exit","review":"[P1] Preserve the transaction — src/Store.cs:88"}}}
            """);
        transport.ServerSend(
            """
            {"method":"turn/completed","params":{"threadId":"thread-review","turn":{"id":"turn-review","status":"completed","items":[]}}}
            """);
        await WaitUntilAsync(() => !viewModel.IsTurnRunning, "review turn completed");
        await WaitUntilAsync(
            () => viewModel.TaskWorkspace.ConversationTurns.SingleOrDefault()?.AssistantResponse.Contains("[P1]", StringComparison.Ordinal) == true,
            "review findings projected");

        var review = viewModel.TaskWorkspace.ConversationTurns.Single();
        Assert(review.IsCodeReview && review.ReviewScope == "uncommitted changes", "transcript labels the streamed review");
        Assert(
            review.Activity.Count == 2 && review.Activity.All(item => item.Kind == CodexTimelineItemKind.CodeReview),
            "review lifecycle is not duplicated as generic tool activity");
        Assert(string.IsNullOrEmpty(viewModel.TaskWorkspace.Prompt), "successful slash review clears the composer");
    }

    private static Task PickerRendersAccessibleTargetsAsync() => WpfTestHost.RunAsync(() =>
    {
        var resources = Application.Current.Resources;
        resources["CompactButton"] = new Style(typeof(Button));
        resources["PrimaryButton"] = new Style(typeof(Button));
        var catalog = new GitReviewCatalog(
            @"C:\repo",
            "feature/review",
            ["main", "release/next"],
            [new GitReviewCommit("1234567890abcdef1234567890abcdef12345678", "1234567", "Fix review UI")]);
        var window = new CodeReviewWindow(catalog);

        Assert(AutomationProperties.GetName(window) == "Start code review", "picker window has an accessible name");
        Assert(window.FindName("UncommittedReviewTarget") is RadioButton, "uncommitted target is rendered");
        Assert(window.FindName("BaseBranchReviewTarget") is RadioButton, "base branch target is rendered");
        Assert(window.FindName("CommitReviewTarget") is RadioButton, "commit target is rendered");
        Assert(window.FindName("CustomReviewTarget") is RadioButton, "custom target is rendered");
        Assert(window.FindName("BaseBranchSelector") is ComboBox, "base branch selector is rendered");
        Assert(window.FindName("CommitSelector") is ComboBox, "commit selector is rendered");
        Assert(window.FindName("CustomReviewInstructions") is TextBox { AcceptsReturn: true }, "custom instructions are multiline");
        Assert(AutomationProperties.GetName(window.FindName("StartCodeReviewButton") as Button) == "Start code review", "start action is accessible");
        window.Close();
    });

    private static async Task InitializeAsync(CodexAppServerClient client, FakeAppServerTransport transport)
    {
        var initialize = client.InitializeAsync();
        await transport.WaitForClientMessageCountAsync(1);
        transport.ServerSend("""{"id":0,"result":{"userAgent":"review-tests","platformFamily":"windows","platformOs":"windows"}}""");
        await initialize;
        await transport.WaitForClientMessageCountAsync(2);
    }

    private static CodexAppServerNotification Notification(string method, string json) =>
        CodexAppServerNotification.Decode(new AppServerNotification(method, Parse(json)));

    private static JsonObject Parse(string value) => JsonNode.Parse(value)!.AsObject();

    private static string? ReadString(JsonObject value, string path) => ReadNode(value, path)?.GetValue<string>();

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

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
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
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}{output}");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
        Assert(condition(), label);
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ReviewProtocolCase(
        CodexReviewTarget Target,
        string Type,
        string? Branch,
        string? Sha,
        string? Instructions);

    private sealed class FakeReviewFeature(
        Func<CodexReviewStartRequest, Task<CodexReviewStartResult>> start) : ICodexReviewFeature
    {
        public Task<CodexReviewStartResult> StartReviewAsync(
            CodexReviewStartRequest request,
            CancellationToken cancellationToken = default) => start(request);
    }

    private sealed class ReviewActionStub : ICodeReviewActions
    {
        public int StartCount { get; private set; }

        public Task StartCodeReviewAsync()
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public bool CanStartCodeReview() => true;
    }
}
