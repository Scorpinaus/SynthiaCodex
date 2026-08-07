using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;

internal static class StructuredReviewFindingsTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("structured review parses official plain-text findings", ParsesOfficialPlainTextAsync),
        ("structured review parses validated JSON findings", ParsesValidatedJsonAsync),
        ("structured review derives latest findings from persisted turns", DerivesLatestPersistedFindingsAsync),
        ("structured review parses unified diff line numbers", ParsesUnifiedDiffLinesAsync),
        ("structured review anchors findings in the Git inspector", AnchorsFindingsInGitInspectorAsync),
        ("structured review renders accessible inline finding cards", RendersAccessibleInlineCardsAsync)
    ];

    private static Task ParsesOfficialPlainTextAsync()
    {
        const string review = """
            The patch has two actionable issues.

            Full review comments:

            - [P1] Prevent the out-of-range read — C:\repo\src\App.cs:42-43
              The loop reaches `Length`, so the final access throws for every non-empty input.
              Keep the upper bound exclusive.

            - [P3] Retain the metadata marker — /repo/src/Metadata.cs:7-7
              Removing this marker breaks the generated manifest in release builds.
            """;

        var findings = CodexReviewFindingParser.Parse(review);

        Assert(findings.Count == 2, "both official plain-text findings are parsed");
        Assert(findings[0].Priority == CodexReviewPriority.P1, "P1 priority is typed");
        Assert(findings[0].PriorityLabel == "P1", "P1 has a non-color label");
        Assert(findings[0].AbsoluteFilePath == @"C:\repo\src\App.cs", "Windows drive-letter path is preserved");
        Assert(findings[0].StartLine == 42 && findings[0].EndLine == 43, "line range is parsed");
        Assert(findings[0].Body.Contains("Keep the upper bound exclusive.", StringComparison.Ordinal), "indented body lines are preserved");
        Assert(findings[1].Priority == CodexReviewPriority.P3, "P3 priority is typed");
        Assert(findings[1].AbsoluteFilePath == "/repo/src/Metadata.cs", "Unix path is preserved");
        Assert(findings.All(item => !item.Body.Contains("patch has", StringComparison.OrdinalIgnoreCase)), "overall explanation is not a finding body");
        return Task.CompletedTask;
    }

    private static Task ParsesValidatedJsonAsync()
    {
        const string review = """
            reviewer output:
            {
              "findings": [
                {
                  "title": "[P2] Dispose the response stream",
                  "body": "The retry path leaks the previous response stream.",
                  "confidence_score": 0.91,
                  "priority": 2,
                  "code_location": {
                    "absolute_file_path": "C:\\repo\\src\\Client.cs",
                    "line_range": { "start": 18, "end": 18 }
                  }
                },
                {
                  "title": "[P2] Dispose the response stream",
                  "body": "The retry path leaks the previous response stream.",
                  "confidence_score": 0.91,
                  "priority": 2,
                  "code_location": {
                    "absolute_file_path": "C:\\repo\\src\\Client.cs",
                    "line_range": { "start": 18, "end": 18 }
                  }
                },
                {
                  "title": "[P9] Invalid priority",
                  "body": "This record must be ignored.",
                  "confidence_score": 4,
                  "priority": 9,
                  "code_location": {
                    "absolute_file_path": "C:\\repo\\src\\Client.cs",
                    "line_range": { "start": 0, "end": 1 }
                  }
                }
              ],
              "overall_correctness": "patch is incorrect",
              "overall_explanation": "One issue remains.",
              "overall_confidence_score": 0.9
            }
            end reviewer output
            """;

        var findings = CodexReviewFindingParser.Parse(review);

        Assert(findings.Count == 1, "duplicate and malformed JSON records are removed");
        Assert(findings[0].Priority == CodexReviewPriority.P2, "numeric JSON priority is preserved");
        Assert(findings[0].ConfidenceScore == 0.91, "JSON confidence is preserved");
        Assert(findings[0].StartLine == 18 && findings[0].EndLine == 18, "JSON location is parsed");
        return Task.CompletedTask;
    }

    private static Task DerivesLatestPersistedFindingsAsync()
    {
        var older = ReviewTurn(
            "review-1",
            @"- [P1] Fix the old issue — C:\repo\src\Old.cs:4-4" + "\n  Old body.",
            CodexTurnStatus.Completed);
        var latest = ReviewTurn("review-2", string.Empty, CodexTurnStatus.Running);

        Assert(CodexReviewFindingProjection.GetLatest([older, latest]).Count == 0, "a newer running review clears stale findings");

        latest.AssistantResponse = @"- [P2] Fix the current issue — C:\repo\src\Current.cs:9-9" + "\n  Current body.";
        latest.Status = CodexTurnStatus.Completed;
        var projected = CodexReviewFindingProjection.GetLatest([older, latest]);
        Assert(projected.Count == 1 && projected[0].Title.Contains("current", StringComparison.Ordinal), "latest review replaces older findings");

        var restored = CodexConversationTurn.FromSnapshot(latest.ToSnapshot());
        Assert(restored.ReviewFindings.Count == 1, "existing snapshot response restores typed findings without duplicate fields");

        latest.IsSuperseded = true;
        projected = CodexReviewFindingProjection.GetLatest([older, latest]);
        Assert(projected.Count == 1 && projected[0].Title.Contains("old", StringComparison.Ordinal), "superseded reviews are ignored");
        return Task.CompletedTask;
    }

    private static Task ParsesUnifiedDiffLinesAsync()
    {
        const string diff = """
            diff --git a/src/App.cs b/src/App.cs
            index 1111111..2222222 100644
            --- a/src/App.cs
            +++ b/src/App.cs
            @@ -10,3 +20,4 @@ public void Run()
            -old value
             shared value
            +new value
             tail value
            @@ -30,1 +40,0 @@ cleanup
            -removed tail
            \ No newline at end of file
            """;

        var rows = GitUnifiedDiffParser.Parse(diff.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert(rows[0].Kind == GitDiffLineKind.Header, "diff header is classified");
        Assert(rows.Single(row => row.Text == "-old value") is { Kind: GitDiffLineKind.Removal, OldLineNumber: 10, NewLineNumber: null }, "removal advances only old lines");
        Assert(rows.Single(row => row.Text == " shared value") is { Kind: GitDiffLineKind.Context, OldLineNumber: 11, NewLineNumber: 20 }, "context advances both sides");
        Assert(rows.Single(row => row.Text == "+new value") is { Kind: GitDiffLineKind.Addition, OldLineNumber: null, NewLineNumber: 21 }, "addition advances only new lines");
        Assert(rows.Single(row => row.Text == " tail value") is { OldLineNumber: 12, NewLineNumber: 22 }, "line counters remain aligned");
        Assert(rows.Single(row => row.Text == "-removed tail").OldLineNumber == 30, "second hunk resets old counter");
        Assert(rows[^1].Kind == GitDiffLineKind.Metadata, "no-newline marker remains visible metadata");
        return Task.CompletedTask;
    }

    private static async Task AnchorsFindingsInGitInspectorAsync()
    {
        using var workspace = TempWorkspace.Create();
        var root = workspace.CreateDirectory("review-repo");
        var changedFile = new GitChangedFile("src/App.cs", "src/LegacyApp.cs", ' ', 'M');
        const string diff = """
            diff --git a/src/App.cs b/src/App.cs
            --- a/src/App.cs
            +++ b/src/App.cs
            @@ -1,2 +1,3 @@
             first line
            +added line
             final line
            @@ -5,1 +6,0 @@
            -removed line
            """;
        var git = new ReviewGitService(root, changedFile, diff);
        var viewModel = new GitViewModel(
            git,
            new FakeUserInteractionService(),
            new SilentLogger(),
            () => new GitContext(root, root, [root]),
            () => false,
            _ => { });

        await viewModel.RefreshAsync();
        await WaitUntilAsync(() => viewModel.SelectedDiffLines.Any(row => row.NewLineNumber == 2), "structured diff loaded");

        var findings = CodexReviewFindingParser.Parse($"""
            Full review comments:

            - [P1] Fix the added line — {Path.Combine(root, "src", "App.cs")}:2-2
              Added-line body.

            - [P2] Explain the removed line — {Path.Combine(root, "src", "LegacyApp.cs")}:5-5
              Removed-line body.

            - [P3] Keep the distant contract — {Path.Combine(root, "src", "App.cs")}:99-99
              This valid finding cannot anchor in the loaded side.
            """);
        viewModel.SetReviewFindings(findings);

        Assert(viewModel.SelectedDiffLines.Single(row => row.NewLineNumber == 2).ReviewFindings.Single().Priority == CodexReviewPriority.P1, "new-side finding anchors inline");
        Assert(viewModel.SelectedDiffLines.Single(row => row.OldLineNumber == 5).ReviewFindings.Single().Priority == CodexReviewPriority.P2, "renamed-path deletion finding anchors on old side");
        Assert(viewModel.UnmatchedReviewFindings.Count == 1 && viewModel.HasUnmatchedReviewFindings, "unanchored selected-file finding remains visible");

        viewModel.SetReviewFindings([]);
        Assert(viewModel.SelectedDiffLines.All(row => row.ReviewFindings.Count == 0), "new review state replaces old annotations");
        Assert(!viewModel.HasUnmatchedReviewFindings, "replacing review clears unmatched findings");
    }

    private static Task RendersAccessibleInlineCardsAsync()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Views", "GitView.xaml"));
        var mainViewModel = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "ViewModels", "MainViewModel.cs"));

        Assert(xaml.Contains("ItemsSource=\"{Binding Git.SelectedDiffLines}\"", StringComparison.Ordinal), "Git diff binds structured rows");
        Assert(xaml.Contains("VirtualizingStackPanel.VirtualizationMode=\"Recycling\"", StringComparison.Ordinal), "structured diff recycles rows");
        Assert(xaml.Contains("ItemsSource=\"{Binding ReviewFindings}\"", StringComparison.Ordinal), "findings render beneath their anchor row");
        Assert(xaml.Contains("Text=\"{Binding PriorityLabel}\"", StringComparison.Ordinal), "priority has a textual label");
        Assert(xaml.Contains("AutomationProperties.Name=\"{Binding AutomationName}\"", StringComparison.Ordinal), "finding cards expose automation names");
        Assert(xaml.Contains("Git.UnmatchedReviewFindings", StringComparison.Ordinal), "unmatched findings have an explicit region");
        Assert(!xaml.Contains("Text=\"{Binding Git.SelectedDiff, Mode=OneWay}\"", StringComparison.Ordinal), "plain diff textbox is replaced");
        Assert(mainViewModel.Contains("Git.SetReviewFindings(CodexReviewFindingProjection.GetLatest", StringComparison.Ordinal), "active chat drives Git review projection");
        return Task.CompletedTask;
    }

    private static CodexConversationTurn ReviewTurn(string id, string response, CodexTurnStatus status) => new()
    {
        TurnId = id,
        UserPrompt = "Review current changes",
        AssistantResponse = response,
        Status = status,
        IsCodeReview = true
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
        Assert(condition(), label);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "SynthiaCode.sln")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ReviewGitService(
        string repositoryRoot,
        GitChangedFile changedFile,
        string diff) : IGitService
    {
        public Task<GitRepositoryState> GetRepositoryStateAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryState(true, repositoryRoot, "review/findings", [changedFile], null));

        public Task<GitReviewCatalog> GetReviewCatalogAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitReviewCatalog(repositoryRoot, "review/findings", [], []));

        public Task<string> GetDiffAsync(
            string requestedRepositoryRoot,
            GitChangedFile file,
            bool staged,
            CancellationToken cancellationToken = default) => Task.FromResult(diff);

        public Task ApplyHunkAsync(
            string requestedRepositoryRoot,
            GitDiffHunkPatch patch,
            GitHunkOperation operation,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StageAsync(
            string requestedRepositoryRoot,
            IReadOnlyCollection<string> paths,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UnstageAsync(
            string requestedRepositoryRoot,
            IReadOnlyCollection<string> paths,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RevertAsync(
            string requestedRepositoryRoot,
            IReadOnlyCollection<GitChangedFile> files,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GitCommitResult> CommitAsync(
            string requestedRepositoryRoot,
            string message,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitCommitResult("abc1234", message));
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
