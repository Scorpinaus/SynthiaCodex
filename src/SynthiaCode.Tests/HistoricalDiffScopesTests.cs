using System.Diagnostics;
using System.Text.Json.Nodes;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Logging;
using SynthiaCode.Harnesses.Codex;
using SynthiaCode.Infrastructure.Git;

[Trait("Category", TestCategories.InfrastructureIntegration)]
[Collection(TestCategories.NativeCollection)]
public sealed class HistoricalDiffScopesTests
{
    private const string LatestTurnDiff = """
        diff --git a/src/latest.cs b/src/latest.cs
        index 1111111..2222222 100644
        --- a/src/latest.cs
        +++ b/src/latest.cs
        @@ -1 +1 @@
        -old
        +new
        """;



    [Fact(DisplayName = "historical diff: aggregate diffs split into per-file comparison documents")]
    public Task ParsesAggregateDiffDocumentsAsync()
    {
        var diff = """
            diff --git a/old.cs b/new.cs
            similarity index 100%
            rename from old.cs
            rename to new.cs
            diff --git a/added.txt b/added.txt
            new file mode 100644
            --- /dev/null
            +++ b/added.txt
            @@ -0,0 +1 @@
            +added
            diff --git a/image.png b/image.png
            index 1111111..2222222 100644
            Binary files a/image.png and b/image.png differ
            """;

        var documents = GitUnifiedDiffDocumentParser.Parse(diff, "Commit");

        Assert(documents.Count == 3, "text, rename, and binary documents are parsed");
        Assert(documents[0].File.Path == "new.cs" && documents[0].File.OriginalPath == "old.cs", "rename paths are preserved");
        Assert(documents[1].File.Path == "added.txt", "new-file path is normalized");
        Assert(documents[2].File.Path == "image.png", "binary path falls back to the diff header");
        Assert(documents.All(document => document.File.StatusSummary == "Commit"), "scope status labels are projected");
        Assert(documents.All(document => document.Diff.StartsWith("diff --git ", StringComparison.Ordinal)), "each document retains a valid file header");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "historical diff: git commit and branch scopes use exact comparisons")]
    public async Task LoadsExactCommitAndMergeBaseBranchAsync()
    {
        using var workspace = TempWorkspace.Create();
        var root = workspace.CreateDirectory("historical-scope-repo");
        await RunGitAsync(root, "init", "-q");
        await RunGitAsync(root, "config", "user.email", "tests@example.com");
        await RunGitAsync(root, "config", "user.name", "SynthiaCode Tests");
        await RunGitAsync(root, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base\n");
        await RunGitAsync(root, "add", "--", "base.txt");
        await RunGitAsync(root, "commit", "-m", "base");
        await RunGitAsync(root, "branch", "-M", "main");
        await RunGitAsync(root, "switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(root, "feature.txt"), "feature\n");
        await RunGitAsync(root, "add", "--", "feature.txt");
        await RunGitAsync(root, "commit", "-m", "feature");
        var head = (await RunGitAsync(root, "rev-parse", "HEAD")).Trim();

        var service = new GitService(new SilentLogger());
        var commit = await service.GetComparisonDiffAsync(root, GitComparisonTarget.Commit(head));
        var branch = await service.GetComparisonDiffAsync(root, GitComparisonTarget.Branch("main"));

        Assert(commit.Count == 1 && commit[0].File.Path == "feature.txt", "commit scope shows exactly the selected commit");
        Assert(branch.Count == 1 && branch[0].File.Path == "feature.txt", "branch scope compares merge base to HEAD");
        Assert(commit[0].File.StatusSummary == "Commit" && branch[0].File.StatusSummary == "Branch", "comparison labels identify their scopes");
    }

    [Fact(DisplayName = "historical diff: app-server turn diffs survive bounded latest-turn persistence")]
    public Task PersistsLatestTurnDiffAsync()
    {
        var notification = CodexAppServerNotification.Decode(new AppServerNotification(
            CodexAppServerNotificationMethods.TurnDiffUpdated,
            new JsonObject
            {
                ["threadId"] = "thread-diff",
                ["turnId"] = "turn-one",
                ["diff"] = LatestTurnDiff
            }));
        var translated = CodexHarnessEventTranslator.Translate(notification).Single();
        Assert(translated is TurnDiffChangedEvent, "turn/diff/updated translates to a harness event");

        var service = new CodexThreadService();
        service.Restore("thread-diff", null, null, null);
        service.BeginTurn("First");
        service.BindPendingTurn("turn-one");
        service.ApplyEvent(translated);
        service.BeginTurn("Second");
        service.BindPendingTurn("turn-two");
        service.ApplyEvent(new TurnDiffChangedEvent(
            HarnessId.Codex,
            "thread-diff",
            "turn-two",
            LatestTurnDiff.Replace("latest.cs", "second.cs", StringComparison.Ordinal),
            DateTimeOffset.UtcNow));

        var snapshot = service.SnapshotConversation();
        Assert(string.IsNullOrEmpty(snapshot[0].Diff), "older turn diffs are omitted from persisted state");
        Assert(snapshot[1].Diff.Contains("second.cs", StringComparison.Ordinal), "latest turn diff is persisted");

        var restored = new CodexThreadService();
        restored.Restore("thread-diff", null, null, null, conversationTurns: snapshot);
        Assert(restored.ConversationTurns[^1].Diff == snapshot[^1].Diff, "latest turn diff survives restoration");
        restored.ConversationTurns[^1].Diff = new string('x', CodexConversationTurn.MaximumDiffCharacters + 1);
        Assert(restored.ConversationTurns[^1].Diff.Length == 0, "oversized diffs are rejected without truncation");
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "historical diff: scopes render through the changes view model read-only")]
    public async Task RoutesHistoricalScopesReadOnlyAsync()
    {
        using var workspace = TempWorkspace.Create();
        var root = workspace.CreateDirectory("historical-view-model-repo");
        var changed = new GitChangedFile("working.cs", null, 'M', 'M');
        var service = new RecordingGitService(root, changed);
        var viewModel = new GitViewModel(
            service,
            new FakeUserInteractionService(),
            new SilentLogger(),
            () => new GitContext(root, root, [root]),
            () => false,
            _ => { });
        viewModel.SetLastTurnDiff(LatestTurnDiff);
        await viewModel.RefreshAsync();

        Assert(viewModel.SelectedDiffScope.Scope == GitDiffScope.Unstaged, "unstaged remains the default scope");
        viewModel.SelectedDiffScope = viewModel.DiffScopes.Single(scope => scope.Scope == GitDiffScope.Commit);
        await StateProbe.WaitForAsync(() => !viewModel.IsBusy && viewModel.ChangedFiles.Any(file => file.Path == "commit.cs"));
        Assert(!viewModel.StageCommand.CanExecute(null) && !viewModel.DiscardCommand.CanExecute(null), "commit comparisons are read-only");

        viewModel.SelectedDiffScope = viewModel.DiffScopes.Single(scope => scope.Scope == GitDiffScope.LastTurn);
        Assert(!viewModel.ShowsRepositorySelector, "last turn uses the all-repositories presentation");
        Assert(viewModel.ChangedFiles.Single().Path == "src/latest.cs", "last turn reuses aggregate diff parsing");
        Assert(viewModel.SelectedDiffLines.Any(line => line.Kind == GitDiffLineKind.Addition), "last turn reuses the existing line renderer");
        Assert(!viewModel.StageCommand.CanExecute(null) && !viewModel.UnstageCommand.CanExecute(null), "last turn comparisons are read-only");
    }

    private static async Task<string> RunGitAsync(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git.exe",
                WorkingDirectory = root,
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
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        }
        return output;
    }


    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingGitService(string root, GitChangedFile changed) : IGitService
    {
        private readonly GitReviewCommit commit = new(new string('a', 40), "aaaaaaa", "selected commit");

        public Task<GitRepositoryState> GetRepositoryStateAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryState(true, root, "feature", [changed], null));

        public Task<GitReviewCatalog> GetReviewCatalogAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitReviewCatalog(root, "feature", ["main"], [commit]));

        public Task<string> GetDiffAsync(string repositoryRoot, GitChangedFile file, bool staged, CancellationToken cancellationToken = default) =>
            Task.FromResult(LatestTurnDiff.Replace("src/latest.cs", file.Path, StringComparison.Ordinal));

        public Task<IReadOnlyList<GitDiffDocument>> GetComparisonDiffAsync(
            string repositoryRoot,
            GitComparisonTarget target,
            CancellationToken cancellationToken = default)
        {
            var file = new GitChangedFile("commit.cs", null, ' ', ' ', target.Scope.ToString());
            return Task.FromResult<IReadOnlyList<GitDiffDocument>>(
                [new(file, LatestTurnDiff.Replace("src/latest.cs", file.Path, StringComparison.Ordinal))]);
        }

        public Task ApplyHunkAsync(string repositoryRoot, GitDiffHunkPatch patch, GitHunkOperation operation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnstageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevertAsync(string repositoryRoot, IReadOnlyCollection<GitChangedFile> files, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GitCommitResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitCommitResult("aaaaaaa", message));
    }

    private sealed class SilentLogger : IAppLogger
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
}
