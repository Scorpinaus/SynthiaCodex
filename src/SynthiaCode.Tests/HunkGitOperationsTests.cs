using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.App.Views;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Infrastructure.Git;

internal static class HunkGitOperationsTests
{
    private const string TwoHunkDiff = """
        diff --git a/notes.txt b/notes.txt
        index 1111111..2222222 100644
        --- a/notes.txt
        +++ b/notes.txt
        @@ -1,4 +1,4 @@
         line 01
        -line 02
        +changed first
         line 03
         line 04
        @@ -33,5 +33,5 @@
         line 33
         line 34
        -line 35
        +changed second
         line 36
         line 37
        """;

    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("git diff parser extracts independent hunk patches", ExtractsIndependentHunkPatchesAsync),
        ("git service stages unstages and discards one hunk", MutatesOnlySelectedHunkAsync),
        ("git view model routes eligible hunk actions with confirmation", RoutesEligibleHunkActionsAsync),
        ("git review renders accessible hunk actions", RendersAccessibleHunkActionsAsync)
    ];

    private static Task ExtractsIndependentHunkPatchesAsync()
    {
        var hunks = GitUnifiedDiffParser.ParseHunks(TwoHunkDiff.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert(hunks.Count == 2, "two hunks are extracted");
        Assert(hunks[0].Header.StartsWith("@@ -1,4 +1,4 @@", StringComparison.Ordinal), "first hunk keeps its header");
        Assert(hunks[0].Patch.StartsWith("diff --git a/notes.txt b/notes.txt\n", StringComparison.Ordinal), "patch retains the file header");
        Assert(hunks[0].Patch.Contains("+changed first\n", StringComparison.Ordinal), "first patch contains its change");
        Assert(!hunks[0].Patch.Contains("changed second", StringComparison.Ordinal), "first patch excludes the later hunk");
        Assert(hunks[1].Patch.Contains("+changed second\n", StringComparison.Ordinal), "second patch contains its change");
        Assert(!hunks[1].Patch.Contains("changed first", StringComparison.Ordinal), "second patch excludes the earlier hunk");
        Assert(hunks.All(hunk => hunk.Patch.EndsWith('\n')), "patches are newline terminated for git apply");
        return Task.CompletedTask;
    }

    private static async Task MutatesOnlySelectedHunkAsync()
    {
        using var workspace = TempWorkspace.Create();
        var root = workspace.CreateDirectory("hunk-service-repo");
        await InitializeRepositoryAsync(root);
        var path = Path.Combine(root, "notes.txt");
        var original = Enumerable.Range(1, 40).Select(index => $"line {index:00}").ToArray();
        await File.WriteAllTextAsync(path, string.Join('\n', original) + "\n");
        await RunGitAsync(root, "add", "--", "notes.txt");
        await RunGitAsync(root, "commit", "-m", "initial");

        var changed = original.ToArray();
        changed[1] = "changed first";
        changed[34] = "changed second";
        await File.WriteAllTextAsync(path, string.Join('\n', changed) + "\n");

        var service = new GitService(new SilentLogger());
        var state = await service.GetRepositoryStateAsync(root);
        var file = state.ChangedFiles.Single();
        var workingHunks = GitUnifiedDiffParser.ParseHunks(await service.GetDiffAsync(root, file, staged: false));
        Assert(workingHunks.Count == 2, "real Git diff contains two independent hunks");

        await service.ApplyHunkAsync(root, workingHunks[0], GitHunkOperation.Stage);
        state = await service.GetRepositoryStateAsync(root);
        file = state.ChangedFiles.Single();
        var stagedAfterFirst = await service.GetDiffAsync(root, file, staged: true);
        var workingAfterFirst = await service.GetDiffAsync(root, file, staged: false);
        Assert(stagedAfterFirst.Contains("changed first", StringComparison.Ordinal) && !stagedAfterFirst.Contains("changed second", StringComparison.Ordinal), "stage affects only the selected hunk");
        Assert(!workingAfterFirst.Contains("changed first", StringComparison.Ordinal) && workingAfterFirst.Contains("changed second", StringComparison.Ordinal), "other working hunk remains unstaged");

        await service.StageAsync(root, ["notes.txt"]);
        state = await service.GetRepositoryStateAsync(root);
        file = state.ChangedFiles.Single();
        var stagedHunks = GitUnifiedDiffParser.ParseHunks(await service.GetDiffAsync(root, file, staged: true));
        await service.ApplyHunkAsync(root, stagedHunks[0], GitHunkOperation.Unstage);
        state = await service.GetRepositoryStateAsync(root);
        file = state.ChangedFiles.Single();
        var stagedAfterUnstage = await service.GetDiffAsync(root, file, staged: true);
        var workingAfterUnstage = await service.GetDiffAsync(root, file, staged: false);
        Assert(!stagedAfterUnstage.Contains("changed first", StringComparison.Ordinal) && stagedAfterUnstage.Contains("changed second", StringComparison.Ordinal), "unstage affects only the selected cached hunk");
        Assert(workingAfterUnstage.Contains("changed first", StringComparison.Ordinal) && !workingAfterUnstage.Contains("changed second", StringComparison.Ordinal), "unstaged hunk returns to the working diff");

        var discardHunk = GitUnifiedDiffParser.ParseHunks(workingAfterUnstage).Single();
        await service.ApplyHunkAsync(root, discardHunk, GitHunkOperation.Discard);
        var finalLines = (await File.ReadAllLinesAsync(path)).ToArray();
        Assert(finalLines[1] == "line 02", "discard restores only the selected working-tree hunk");
        Assert(finalLines[34] == "changed second", "discard preserves the separately staged hunk");
    }

    private static async Task RoutesEligibleHunkActionsAsync()
    {
        using var workspace = TempWorkspace.Create();
        var root = workspace.CreateDirectory("hunk-view-model-repo");
        var file = new GitChangedFile("notes.txt", null, 'M', 'M');
        var service = new RecordingGitService(root, file, TwoHunkDiff);
        var interactions = new RecordingInteractionService();
        var viewModel = new GitViewModel(
            service,
            interactions,
            new SilentLogger(),
            () => new GitContext(root, root, [root]),
            () => false,
            _ => { });

        await viewModel.RefreshAsync();
        await WaitUntilAsync(() => viewModel.SelectedDiffLines.Count(row => row.Kind == GitDiffLineKind.Hunk) == 2, "working hunks load");
        var workingHunk = viewModel.SelectedDiffLines.First(row => row.Kind == GitDiffLineKind.Hunk);
        Assert(workingHunk.CanStageHunk && workingHunk.CanDiscardHunk && !workingHunk.CanUnstageHunk, "working modified hunks expose stage and discard");

        await ((AsyncRelayCommand)viewModel.StageHunkCommand).ExecuteAsync(workingHunk);
        Assert(service.Operations.SequenceEqual([GitHunkOperation.Stage]), "stage command routes the exact hunk operation");

        await ((AsyncRelayCommand)viewModel.ShowStagedDiffCommand).ExecuteAsync();
        var stagedHunk = viewModel.SelectedDiffLines.First(row => row.Kind == GitDiffLineKind.Hunk);
        Assert(!stagedHunk.CanStageHunk && !stagedHunk.CanDiscardHunk && stagedHunk.CanUnstageHunk, "staged modified hunks expose only unstage");
        await ((AsyncRelayCommand)viewModel.UnstageHunkCommand).ExecuteAsync(stagedHunk);
        Assert(service.Operations[^1] == GitHunkOperation.Unstage, "unstage command routes the cached reverse operation");

        await ((AsyncRelayCommand)viewModel.ShowWorkingDiffCommand).ExecuteAsync();
        workingHunk = viewModel.SelectedDiffLines.First(row => row.Kind == GitDiffLineKind.Hunk);
        interactions.ConfirmResult = false;
        await ((AsyncRelayCommand)viewModel.DiscardHunkCommand).ExecuteAsync(workingHunk);
        Assert(service.Operations.Count == 2, "declining discard does not mutate Git");
        Assert(interactions.LastConfirmation?.Contains(workingHunk.Content, StringComparison.Ordinal) == true, "discard confirmation identifies the selected hunk");
        Assert(viewModel.StatusMessage == "Discard hunk cancelled", "declined discard is reported");

        interactions.ConfirmResult = true;
        await ((AsyncRelayCommand)viewModel.DiscardHunkCommand).ExecuteAsync(workingHunk);
        Assert(service.Operations[^1] == GitHunkOperation.Discard, "confirmed discard routes reverse working-tree apply");

        var unsupported = new GitViewModel(
            new RecordingGitService(root, new GitChangedFile("added.txt", null, 'A', ' '), TwoHunkDiff),
            interactions,
            new SilentLogger(),
            () => new GitContext(root, root, [root]),
            () => false,
            _ => { });
        await unsupported.RefreshAsync();
        await WaitUntilAsync(() => unsupported.SelectedDiffLines.Any(row => row.Kind == GitDiffLineKind.Hunk), "unsupported diff loads");
        var unsupportedHunk = unsupported.SelectedDiffLines.First(row => row.Kind == GitDiffLineKind.Hunk);
        Assert(!unsupportedHunk.CanStageHunk && !unsupportedHunk.CanUnstageHunk && !unsupportedHunk.CanDiscardHunk, "file-level Git metadata disables ambiguous hunk actions");
    }

    private static async Task RendersAccessibleHunkActionsAsync()
    {
        using var workspace = TempWorkspace.Create();
        var root = workspace.CreateDirectory("hunk-render-repo");
        GitViewModel CreateViewModel(GitChangedFile file) => new(
            new RecordingGitService(root, file, TwoHunkDiff),
            new RecordingInteractionService(),
            new SilentLogger(),
            () => new GitContext(root, root, [root]),
            () => false,
            _ => { });
        var working = CreateViewModel(new GitChangedFile("notes.txt", null, ' ', 'M'));
        var staged = CreateViewModel(new GitChangedFile("notes.txt", null, 'M', ' '));
        await working.RefreshAsync();
        await staged.RefreshAsync();
        await WaitUntilAsync(() => !working.IsBusy && !staged.IsBusy &&
            working.SelectedDiffLines.Any(row => row.CanStageHunk) &&
            staged.SelectedDiffLines.Any(row => row.CanUnstageHunk), "hunk views load");

        await WpfTestHost.RunAsync(() =>
        {
            Application.Current!.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            Application.Current.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
            VerifyRenderedActions(working, ["Stage hunk", "Discard hunk"], ["Unstage hunk"]);
            VerifyRenderedActions(staged, ["Unstage hunk"], ["Stage hunk", "Discard hunk"]);
        });
    }

    private static void VerifyRenderedActions(
        GitViewModel viewModel,
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> absent)
    {
        var view = new GitView
        {
            DataContext = new GitViewHost(viewModel),
            Width = 900,
            Height = 900
        };
        var window = new Window
        {
            Content = view,
            Width = 900,
            Height = 900,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        try
        {
            window.Show();
            view.UpdateLayout();
            var visibleActions = Descendants<Button>(view)
                .Where(button => button.IsVisible)
                .ToArray();
            foreach (var name in expected)
            {
                var button = visibleActions.FirstOrDefault(candidate =>
                    string.Equals(AutomationProperties.GetName(candidate), name, StringComparison.Ordinal));
                Assert(button is not null, $"{name} is rendered");
                Assert(button is { IsEnabled: true, CommandParameter: GitDiffLineViewModel }, $"{name} targets an eligible hunk");
            }
            foreach (var name in absent)
            {
                Assert(!visibleActions.Any(button =>
                    string.Equals(AutomationProperties.GetName(button), name, StringComparison.Ordinal)), $"{name} is hidden in this diff view");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task InitializeRepositoryAsync(string root)
    {
        await RunGitAsync(root, "init", "-q");
        await RunGitAsync(root, "config", "user.email", "tests@example.com");
        await RunGitAsync(root, "config", "user.name", "SynthiaCode Tests");
        await RunGitAsync(root, "config", "core.autocrlf", "false");
    }

    private static async Task RunGitAsync(string root, params string[] arguments)
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingGitService(
        string root,
        GitChangedFile file,
        string diff) : IGitService
    {
        public List<GitHunkOperation> Operations { get; } = [];

        public Task<GitRepositoryState> GetRepositoryStateAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryState(true, root, "main", [file], null));

        public Task<GitReviewCatalog> GetReviewCatalogAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitReviewCatalog(root, "main", [], []));

        public Task<string> GetDiffAsync(string repositoryRoot, GitChangedFile changedFile, bool staged, CancellationToken cancellationToken = default) =>
            Task.FromResult(diff);

        public Task ApplyHunkAsync(string repositoryRoot, GitDiffHunkPatch patch, GitHunkOperation operation, CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            return Task.CompletedTask;
        }

        public Task StageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnstageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevertAsync(string repositoryRoot, IReadOnlyCollection<GitChangedFile> files, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GitCommitResult> CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitCommitResult("abc1234", message));
    }

    private sealed record GitViewHost(GitViewModel Git);

    private sealed class RecordingInteractionService : IUserInteractionService
    {
        public bool ConfirmResult { get; set; } = true;
        public string? LastConfirmation { get; private set; }

        public bool ConfirmDestructiveAction(string title, string message)
        {
            LastConfirmation = message;
            return ConfirmResult;
        }

        public string? PromptForText(string title, string message, string initialValue) => null;
        public void OpenInEditor(string path) { }
        public void OpenExternalUri(Uri uri) { }
        public void ShowImagePreview(string path) { }
        public GeneratedImageEditSelection? SelectGeneratedImageEdit(string path) => null;
        public SynthiaCode.Core.Codex.AppServer.CodexReviewTarget? SelectCodeReviewTarget(GitReviewCatalog catalog) => null;
        public ProjectFolderEditSelection? EditProjectFolders(RecentProject project) => null;
        public void RevealInExplorer(string path) { }
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
