using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;

internal static class InlineReviewCommentsTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("inline review comments format deterministic prompt context", FormatsDeterministicPromptContextAsync),
        ("inline review comments validate and normalize restored records", ValidatesAndNormalizesRecordsAsync),
        ("inline review comments support diff row authoring and captured clearing", SupportsDiffRowAuthoringAsync),
        ("inline review comments persist beside attachment drafts per chat", PersistBesideAttachmentDraftsAsync),
        ("inline review comments survive queued follow-up snapshots", SurviveQueuedFollowUpSnapshotsAsync),
        ("inline review comments wire start steer and queue lifecycle", WiresSubmissionLifecycleAsync),
        ("inline review comments render accessible review and queue surfaces", RendersAccessibleSurfacesAsync)
    ];

    private static Task FormatsDeterministicPromptContextAsync()
    {
        var root = Path.GetFullPath(@"D:\Repo");
        var now = DateTimeOffset.Parse("2026-08-07T12:00:00Z");
        var added = GitInlineComment.Create(
            root,
            "src/App.cs",
            null,
            GitDiffSide.New,
            12,
            "return result;",
            "  Guard the null result before returning.  ",
            now);
        var removed = GitInlineComment.Create(
            root,
            "src/Renamed.cs",
            "src/Legacy.cs",
            GitDiffSide.Old,
            7,
            "legacyCall();",
            "Keep the compatibility call.\nIt is still used by upgrades.",
            now.AddMinutes(1));

        var effective = GitInlineCommentPromptFormatter.AppendToPrompt(
            "Fix the issues I called out.",
            [added, removed, added.Clone()]);
        var commentOnly = GitInlineCommentPromptFormatter.AppendToPrompt(string.Empty, [added]);

        Assert(effective.StartsWith("Fix the issues I called out.", StringComparison.Ordinal), "typed prompt stays first");
        Assert(Count(effective, "Inline review comments from the user:") == 1, "comment section is serialized once");
        Assert(Count(effective, "Guard the null result before returning.") == 1, "duplicate records are serialized once");
        Assert(effective.Contains(Path.Combine(root, "src", "App.cs"), StringComparison.Ordinal), "current absolute path is present");
        Assert(effective.Contains(Path.Combine(root, "src", "Legacy.cs"), StringComparison.Ordinal), "renamed old path is present");
        Assert(effective.Contains("old", StringComparison.OrdinalIgnoreCase) && effective.Contains("line 7", StringComparison.OrdinalIgnoreCase), "old-side line coordinate is explicit");
        Assert(effective.Contains("It is still used by upgrades.", StringComparison.Ordinal), "multiline body is preserved");
        Assert(!string.IsNullOrWhiteSpace(commentOnly) && commentOnly.Contains("Inline review comments", StringComparison.Ordinal), "comment-only prompt is valid");
        return Task.CompletedTask;
    }

    private static Task ValidatesAndNormalizesRecordsAsync()
    {
        var root = Path.GetFullPath(@"D:\Repo");
        var valid = GitInlineComment.Create(
            root, "src/App.cs", null, GitDiffSide.New, 2, "+value", "Use the parsed value.");
        var duplicate = valid.Clone();
        var invalid = valid.Clone();
        invalid.Id = string.Empty;
        invalid.FilePath = "../escape.cs";
        invalid.LineNumber = 0;
        invalid.Body = string.Empty;

        var restored = GitInlineComment.NormalizeRestored([valid, duplicate, invalid]);

        Assert(restored.Count == 1, "restore filters invalid and duplicate records");
        Assert(restored[0].Body == "Use the parsed value.", "interactive body is normalized");
        Assert(restored[0].DisplayLocation.Contains("src/App.cs", StringComparison.Ordinal), "location is readable");
        AssertThrows<InvalidDataException>(() => GitInlineComment.Create(
            root, "../escape.cs", null, GitDiffSide.New, 1, "line", "comment"), "escaping path is rejected");
        AssertThrows<InvalidDataException>(() => GitInlineComment.Create(
            root, "src/App.cs", null, GitDiffSide.New, 1, "line", new string('x', GitInlineComment.MaximumBodyBytes + 1)), "oversized body is rejected");
        return Task.CompletedTask;
    }

    private static async Task SupportsDiffRowAuthoringAsync()
    {
        using var workspace = TempWorkspace.Create();
        var root = workspace.CreateDirectory("inline-comment-repo");
        var changed = new GitChangedFile("src/App.cs", "src/LegacyApp.cs", ' ', 'M');
        const string diff = """
            diff --git a/src/LegacyApp.cs b/src/App.cs
            --- a/src/LegacyApp.cs
            +++ b/src/App.cs
            @@ -1,2 +1,3 @@
            -removed line
             shared line
            +added line
            """;
        var viewModel = new GitViewModel(
            new CommentGitService(root, changed, diff),
            new FakeUserInteractionService(),
            new SilentLogger(),
            () => new GitContext(root, root, [root]),
            () => false,
            _ => { });
        var mutationCount = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GitViewModel.ReviewComments)) mutationCount++;
        };

        await viewModel.RefreshAsync();
        await WaitUntilAsync(() => viewModel.SelectedDiffLines.Any(row => row.Kind == GitDiffLineKind.Addition), "commentable diff loaded");

        var addition = viewModel.SelectedDiffLines.Single(row => row.Kind == GitDiffLineKind.Addition);
        viewModel.BeginAddCommentCommand.Execute(addition);
        addition.CommentDraft = "Check the added value.";
        viewModel.SaveCommentCommand.Execute(addition);

        var removal = viewModel.SelectedDiffLines.Single(row => row.Kind == GitDiffLineKind.Removal);
        viewModel.BeginAddCommentCommand.Execute(removal);
        removal.CommentDraft = "Preserve this behavior.";
        viewModel.SaveCommentCommand.Execute(removal);

        Assert(viewModel.ReviewComments.Count == 2 && viewModel.HasReviewComments, "saved comments enter the pending collection");
        Assert(viewModel.ReviewComments.Single(item => item.Body.Contains("added", StringComparison.Ordinal)).Side == GitDiffSide.New, "addition uses new side");
        Assert(viewModel.ReviewComments.Single(item => item.Body.Contains("Preserve", StringComparison.Ordinal)).Side == GitDiffSide.Old, "removal uses old side");
        Assert(viewModel.ReviewComments.Single(item => item.Side == GitDiffSide.Old).OriginalFilePath == "src/LegacyApp.cs", "renamed old-side path is retained");
        Assert(addition.UserComments.Count == 1 && removal.UserComments.Count == 1, "comments project inline on exact rows");

        var captured = viewModel.CaptureReviewComments();
        viewModel.BeginAddCommentCommand.Execute(addition);
        addition.CommentDraft = "Added while submission is starting.";
        viewModel.SaveCommentCommand.Execute(addition);
        viewModel.RemoveReviewComments(captured.Select(item => item.Id));

        Assert(viewModel.ReviewComments.Count == 1, "acknowledgement removes only captured IDs");
        var remaining = viewModel.ReviewComments.Single();
        viewModel.BeginEditCommentCommand.Execute(remaining);
        remaining.EditText = "Edited after capture.";
        viewModel.SaveEditedCommentCommand.Execute(remaining);
        Assert(remaining.Body == "Edited after capture.", "pending comments are editable");
        viewModel.RemoveCommentCommand.Execute(remaining);
        Assert(!viewModel.HasReviewComments, "remove clears the final pending comment");
        Assert(mutationCount >= 5, "committed mutations notify shell persistence");
    }

    private static Task PersistBesideAttachmentDraftsAsync()
    {
        var root = Path.GetFullPath(@"D:\Repo");
        var comment = GitInlineComment.Create(
            root, "src/App.cs", null, GitDiffSide.New, 3, "value", "Review this value.");
        var laterComment = GitInlineComment.Create(
            root, "src/App.cs", null, GitDiffSide.New, 4, "nextValue", "Keep this later comment.");
        var attachment = new AttachmentReference
        {
            Id = "attachment-1",
            Kind = AttachmentKind.File,
            SourceKind = AttachmentSourceKind.WorkspaceReference,
            WorkspaceRootPath = root,
            WorkspaceRelativePath = "notes.txt",
            DisplayName = "notes.txt"
        };
        var settings = new AppSettings
        {
            ComposerAttachmentDrafts =
            [
                new ComposerAttachmentDraftSnapshot
                {
                    ScopeKind = ThreadScopeKind.Project,
                    ProjectPath = root,
                    ThreadId = "thread-a",
                    Attachments = [attachment]
                }
            ]
        };

        ComposerReviewCommentDraftStore.Capture(settings, root, "thread-a", [comment]);
        ComposerReviewCommentDraftStore.Capture(settings, root, "thread-b", [comment, laterComment]);
        var restoredA = ComposerReviewCommentDraftStore.Restore(settings, root, "thread-a");
        var restoredB = ComposerReviewCommentDraftStore.Restore(settings, root, "thread-b");
        Assert(restoredA.Count == 1 && restoredB.Count == 2, "comments restore per thread");
        Assert(settings.ComposerAttachmentDrafts.Single(item => item.ThreadId == "thread-a").Attachments.Count == 1, "comment capture preserves attachments");

        ComposerReviewCommentDraftStore.Remove(settings, root, "thread-b", [comment.Id]);
        Assert(
            ComposerReviewCommentDraftStore.Restore(settings, root, "thread-b").Single().Id == laterComment.Id,
            "acknowledgement removes only captured IDs from an inactive thread draft");

        var snapshot = AppSettingsSnapshot.Create(settings);
        comment.Body = "mutated source";
        Assert(snapshot.ComposerAttachmentDrafts.Single(item => item.ThreadId == "thread-a").ReviewComments.Single().Body == "Review this value.", "settings snapshot deep-copies comments");

        ComposerReviewCommentDraftStore.Capture(settings, root, "thread-a", []);
        Assert(settings.ComposerAttachmentDrafts.Any(item => item.ThreadId == "thread-a"), "empty comments do not erase an attachment draft");
        ComposerReviewCommentDraftStore.Capture(settings, root, "thread-b", []);
        Assert(settings.ComposerAttachmentDrafts.All(item => item.ThreadId != "thread-b"), "fully empty draft is removed");
        return Task.CompletedTask;
    }

    private static Task SurviveQueuedFollowUpSnapshotsAsync()
    {
        var root = Path.GetFullPath(@"D:\Repo");
        var comment = GitInlineComment.Create(
            root, "src/App.cs", null, GitDiffSide.New, 8, "value", "Use the safe value.");
        var queue = new CodexFollowUpQueue();
        var queued = queue.Enqueue(
            string.Empty,
            Options(root),
            reviewComments: [comment]);

        Assert(queued.HasReviewComments && queued.ReviewCommentSummary == "1 inline comment", "comment-only queue item is visible");
        var snapshot = queued.Snapshot();
        comment.Body = "mutated source";
        Assert(snapshot.ReviewComments.Single().Body == "Use the safe value.", "queue snapshot deep-copies comment");

        var restored = new CodexFollowUpQueue();
        restored.Restore([snapshot]);
        Assert(restored.Items.Single().ReviewComments.Single().Body == "Use the safe value.", "queue restore retains comment");
        var effective = GitInlineCommentPromptFormatter.AppendToPrompt(
            restored.Items.Single().Text,
            restored.Items.Single().ReviewComments);
        Assert(effective.Contains("Use the safe value.", StringComparison.Ordinal), "queued dispatch can format exact context");
        return Task.CompletedTask;
    }

    private static Task WiresSubmissionLifecycleAsync()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "ViewModels", "MainViewModel.cs"));
        var queue = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Services", "FollowUpQueueUseCaseService.cs"));
        var enqueueStart = main.IndexOf("new FollowUpEnqueueUseCaseRequest(", StringComparison.Ordinal);
        var enqueueEnd = enqueueStart < 0
            ? -1
            : main.IndexOf(")).ConfigureAwait", enqueueStart, StringComparison.Ordinal);
        var enqueueBlock = enqueueStart >= 0 && enqueueEnd > enqueueStart
            ? main[enqueueStart..enqueueEnd]
            : string.Empty;

        Assert(Count(main, "Git.CaptureReviewComments()") >= 3, "start steer and queue capture immutable comments");
        Assert(Count(main, "GitInlineCommentPromptFormatter.AppendToPrompt") >= 3, "start steer and queue build deterministic context");
        Assert(Count(main, "AcknowledgeReviewCommentsAsync(") >= 4, "successful start steer and queue acknowledge captured IDs through the origin-aware helper");
        Assert(main.Contains("ComposerReviewCommentDraftStore.Remove", StringComparison.Ordinal), "acknowledgement can clear an inactive origin draft");
        Assert(enqueueBlock.Contains("capturedComments", StringComparison.Ordinal), "queue enqueue receives typed comments");
        Assert(queue.Contains("snapshot.ReviewComments", StringComparison.Ordinal), "background queue dispatch uses captured comments");
        Assert(queue.Contains("prepared.UserPrompt", StringComparison.Ordinal), "local queued transcript uses the prepared effective prompt");
        return Task.CompletedTask;
    }

    private static Task RendersAccessibleSurfacesAsync()
    {
        var root = FindRepositoryRoot();
        var gitXaml = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Views", "GitView.xaml"));
        var taskXaml = File.ReadAllText(Path.Combine(root, "src", "SynthiaCode.App", "Views", "TaskView.xaml"));

        Assert(gitXaml.Contains("Git.BeginAddCommentCommand", StringComparison.Ordinal), "diff rows expose Add comment command");
        Assert(gitXaml.Contains("Git.SaveCommentCommand", StringComparison.Ordinal), "row editor exposes Save comment command");
        Assert(gitXaml.Contains("Git.CancelAddCommentCommand", StringComparison.Ordinal), "row editor exposes Cancel command");
        Assert(gitXaml.Contains("Git.ReviewComments", StringComparison.Ordinal), "pending comment summary is rendered");
        Assert(gitXaml.Contains("AutomationProperties.Name=\"Add inline review comment\"", StringComparison.Ordinal), "add action has an accessible name");
        Assert(gitXaml.Contains("SideLabel", StringComparison.Ordinal) && gitXaml.Contains("DisplayLocation", StringComparison.Ordinal), "side and location are non-color labels");
        Assert(taskXaml.Contains("ReviewCommentSummary", StringComparison.Ordinal), "queued cards disclose captured comments");
        return Task.CompletedTask;
    }

    private static QueuedTurnOptionsSnapshot Options(string workspacePath) => new()
    {
        WorkspacePath = workspacePath,
        PermissionMode = CodexPermissionMode.AskForApproval,
        Sandbox = CodexSandbox.WorkspaceWrite,
        ApprovalPolicy = CodexApprovalPolicy.OnRequest,
        ApprovalsReviewer = CodexApprovalsReviewer.User
    };

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
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

    private sealed class CommentGitService(
        string repositoryRoot,
        GitChangedFile changedFile,
        string diff) : IGitService
    {
        public Task<GitRepositoryState> GetRepositoryStateAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryState(true, repositoryRoot, "review/comments", [changedFile], null));

        public Task<GitReviewCatalog> GetReviewCatalogAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitReviewCatalog(repositoryRoot, "review/comments", [], []));

        public Task<string> GetDiffAsync(
            string requestedRepositoryRoot,
            GitChangedFile file,
            bool staged,
            CancellationToken cancellationToken = default) => Task.FromResult(diff);

        public Task StageAsync(string requestedRepositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnstageAsync(string requestedRepositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevertAsync(string requestedRepositoryRoot, IReadOnlyCollection<GitChangedFile> files, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GitCommitResult> CommitAsync(string requestedRepositoryRoot, string message, CancellationToken cancellationToken = default) =>
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
