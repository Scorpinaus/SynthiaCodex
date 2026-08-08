using System.Diagnostics;
using SynthiaCode.App.Services;
using SynthiaCode.App.ViewModels;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Projects;
using SynthiaCode.Infrastructure.Git;

internal static class GitPushTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("git push uses an existing upstream", PushesToExistingUpstreamAsync),
        ("git push creates the first upstream with one remote", CreatesFirstUpstreamAsync),
        ("git push reports a missing remote", ReportsMissingRemoteAsync),
        ("git push refuses ambiguous remotes", RefusesAmbiguousRemotesAsync),
        ("git push refuses detached HEAD", RefusesDetachedHeadAsync),
        ("git push preserves slash branch names", PreservesSlashBranchNamesAsync),
        ("canceling git push performs no mutation", CancelsWithoutMutationAsync),
        ("git push reports a sanitized rejection", ReportsSanitizedRejectionAsync),
        ("git push targets the displayed repository root", TargetsDisplayedRepositoryRootAsync),
        ("git push refreshes repository state after success", RefreshesStateAfterSuccessAsync),
        ("git push command tracks named branch and busy state", TracksCommandStateAsync)
    ];

    private static async Task PushesToExistingUpstreamAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = await CreateRepositoryAsync(workspace, "existing-upstream");
        var remote = await CreateBareRemoteAsync(workspace, "existing-upstream.git");
        await RunGitAsync(repository, "remote", "add", "origin", remote);
        var service = new GitService(new TestLogger());

        var firstPlan = await service.GetPushPlanAsync(repository);
        await service.PushAsync(repository, firstPlan);
        var expectedHead = await CommitChangeAsync(repository, "second commit");

        var plan = await service.GetPushPlanAsync(repository);
        Assert(!plan.CreatesUpstream, "existing upstream is detected");
        Assert(plan.Remote == "origin", "existing upstream remote is selected");
        Assert(plan.RemoteRef == "refs/heads/main", "existing upstream branch is selected");

        var result = await service.PushAsync(repository, plan);
        var remoteHead = (await RunGitAsync(remote, "rev-parse", "refs/heads/main")).Trim();
        Assert(!result.CreatedUpstream, "existing upstream is not recreated");
        Assert(remoteHead == expectedHead, "existing upstream receives the new commit");
    }

    private static async Task CreatesFirstUpstreamAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = await CreateRepositoryAsync(workspace, "first-upstream");
        var remote = await CreateBareRemoteAsync(workspace, "first-upstream.git");
        await RunGitAsync(repository, "remote", "add", "origin", remote);
        var service = new GitService(new TestLogger());

        var plan = await service.GetPushPlanAsync(repository);
        Assert(plan.CreatesUpstream, "sole remote requires upstream creation");
        Assert(plan.Remote == "origin" && plan.RemoteBranch == "main", "sole remote target is explicit");

        var result = await service.PushAsync(repository, plan);
        Assert(result.CreatedUpstream, "typed result reports upstream creation");
        Assert((await RunGitAsync(repository, "config", "--get", "branch.main.remote")).Trim() == "origin", "remote tracking config is set");
        Assert((await RunGitAsync(repository, "config", "--get", "branch.main.merge")).Trim() == "refs/heads/main", "merge tracking ref is set");
        Assert(!string.IsNullOrWhiteSpace(await RunGitAsync(remote, "rev-parse", "refs/heads/main")), "remote branch is created");
    }

    private static async Task ReportsMissingRemoteAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = await CreateRepositoryAsync(workspace, "no-remote");
        var service = new GitService(new TestLogger());

        var exception = await AssertThrowsAsync<InvalidOperationException>(() => service.GetPushPlanAsync(repository));
        Assert(exception.Message.Contains("No Git remotes", StringComparison.Ordinal), "missing remote error is actionable");
    }

    private static async Task RefusesAmbiguousRemotesAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = await CreateRepositoryAsync(workspace, "ambiguous-remotes");
        var origin = await CreateBareRemoteAsync(workspace, "origin.git");
        var backup = await CreateBareRemoteAsync(workspace, "backup.git");
        await RunGitAsync(repository, "remote", "add", "origin", origin);
        await RunGitAsync(repository, "remote", "add", "backup", backup);

        var exception = await AssertThrowsAsync<InvalidOperationException>(
            () => new GitService(new TestLogger()).GetPushPlanAsync(repository));
        Assert(exception.Message.Contains("multiple remotes", StringComparison.OrdinalIgnoreCase), "ambiguous remotes are explained");
        Assert(exception.Message.Contains("Configure an upstream", StringComparison.Ordinal), "ambiguous remotes require explicit configuration");
    }

    private static async Task RefusesDetachedHeadAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = await CreateRepositoryAsync(workspace, "detached-head");
        await RunGitAsync(repository, "checkout", "--detach", "HEAD");
        var service = new GitService(new TestLogger());

        var state = await service.GetRepositoryStateAsync(repository);
        Assert(state.IsDetachedHead && !state.HasNamedBranch, "repository state identifies detached HEAD");
        var exception = await AssertThrowsAsync<InvalidOperationException>(() => service.GetPushPlanAsync(repository));
        Assert(exception.Message.Contains("detached HEAD", StringComparison.Ordinal), "detached HEAD error is explicit");
    }

    private static async Task PreservesSlashBranchNamesAsync()
    {
        using var workspace = TempWorkspace.Create();
        const string branch = "feature/native-push";
        var repository = await CreateRepositoryAsync(workspace, "slash-branch", branch);
        var remote = await CreateBareRemoteAsync(workspace, "slash-branch.git");
        await RunGitAsync(repository, "remote", "add", "origin", remote);
        var service = new GitService(new TestLogger());

        var plan = await service.GetPushPlanAsync(repository);
        Assert(plan.Branch == branch && plan.RemoteBranch == branch, "slash branch is preserved in the plan");
        var result = await service.PushAsync(repository, plan);

        Assert(result.Branch == branch, "slash branch is preserved in the result");
        Assert(!string.IsNullOrWhiteSpace(await RunGitAsync(remote, "rev-parse", $"refs/heads/{branch}")), "slash branch is created remotely");
    }

    private static async Task CancelsWithoutMutationAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = workspace.CreateDirectory("cancel-push");
        var service = new RecordingPushGitService(
            new GitRepositoryState(true, repository, "feature/cancel", [], null));
        var interactions = new RecordingInteractionService { ConfirmResult = false };
        var viewModel = CreateViewModel(service, interactions, repository, [repository]);
        await viewModel.RefreshAsync();

        Assert(viewModel.PushCommand.CanExecute(null), "named branch enables push");
        await ((AsyncRelayCommand)viewModel.PushCommand).ExecuteAsync();

        Assert(service.PushRoots.Count == 0, "canceling does not invoke the Git mutation");
        Assert(viewModel.StatusMessage == "Push cancelled", "canceling is reported");
        Assert(interactions.LastConfirmation?.Contains("Branch: feature/cancel", StringComparison.Ordinal) == true, "confirmation names the branch");
        Assert(interactions.LastConfirmation?.Contains("Remote: origin", StringComparison.Ordinal) == true, "confirmation names the remote");
        Assert(interactions.LastConfirmation?.Contains("Upstream: will be created", StringComparison.Ordinal) == true, "confirmation explains upstream creation");
    }

    private static async Task ReportsSanitizedRejectionAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = await CreateRepositoryAsync(workspace, "rejected-local");
        var remote = await CreateBareRemoteAsync(workspace, "rejected.git");
        await RunGitAsync(repository, "remote", "add", "origin", remote);
        var service = new GitService(new TestLogger());
        await service.PushAsync(repository, await service.GetPushPlanAsync(repository));

        var other = workspace.CreateDirectory("rejected-other");
        await RunGitAsync(workspace.Root, "clone", "-q", "--branch", "main", remote, other);
        await ConfigureIdentityAsync(other);
        await CommitChangeAsync(other, "remote advancement");
        await RunGitAsync(other, "push", "origin", "main");
        await CommitChangeAsync(repository, "local divergence");

        var plan = await service.GetPushPlanAsync(repository);
        var exception = await AssertThrowsAsync<InvalidOperationException>(() => service.PushAsync(repository, plan));
        Assert(exception.Message.Contains("Push was rejected", StringComparison.Ordinal), "rejection is actionable");
        Assert(!exception.Message.Contains(remote, StringComparison.OrdinalIgnoreCase), "rejection does not expose the remote URL or path");
    }

    private static async Task TargetsDisplayedRepositoryRootAsync()
    {
        using var workspace = TempWorkspace.Create();
        var primary = workspace.CreateDirectory("primary-repository");
        var secondary = workspace.CreateDirectory("secondary-repository");
        var service = new RecordingPushGitService(
            new GitRepositoryState(true, primary, "main", [], null),
            new GitRepositoryState(true, secondary, "feature/secondary", [], null));
        var interactions = new RecordingInteractionService();
        var viewModel = CreateViewModel(service, interactions, primary, [primary, secondary]);
        await viewModel.RefreshAsync();
        viewModel.SelectedRepository = viewModel.Repositories.Single(option => PathsEqual(option.RootPath, secondary));

        await ((AsyncRelayCommand)viewModel.PushCommand).ExecuteAsync();

        Assert(service.PushRoots.Count == 1, "one push mutation occurs");
        Assert(PathsEqual(service.PushRoots[0], secondary), "push uses the displayed repository root");
    }

    private static async Task RefreshesStateAfterSuccessAsync()
    {
        using var workspace = TempWorkspace.Create();
        var repository = workspace.CreateDirectory("refresh-after-push");
        var service = new RecordingPushGitService(new GitRepositoryState(true, repository, "main", [], null));
        var viewModel = CreateViewModel(service, new RecordingInteractionService(), repository, [repository]);
        await viewModel.RefreshAsync();
        var requestsBeforePush = service.GetStateRequestCount(repository);

        await ((AsyncRelayCommand)viewModel.PushCommand).ExecuteAsync();

        Assert(service.GetStateRequestCount(repository) > requestsBeforePush, "successful push refreshes repository state");
        Assert(viewModel.StatusMessage.Contains("Pushed main", StringComparison.Ordinal), "successful push is reported after refresh");
    }

    private static async Task TracksCommandStateAsync()
    {
        using var workspace = TempWorkspace.Create();
        var namedRoot = workspace.CreateDirectory("named-branch");
        var namedService = new RecordingPushGitService(new GitRepositoryState(true, namedRoot, "main", [], null));
        var interactions = new RecordingInteractionService { ConfirmResult = false };
        var namedViewModel = CreateViewModel(namedService, interactions, namedRoot, [namedRoot]);
        await namedViewModel.RefreshAsync();
        Assert(namedViewModel.PushCommand.CanExecute(null), "named branch enables push");

        namedService.BlockPushPlan();
        var execution = ((AsyncRelayCommand)namedViewModel.PushCommand).ExecuteAsync();
        await namedService.WaitForPushPlanAsync();
        Assert(namedViewModel.IsBusy, "push preflight sets the busy state");
        Assert(!namedViewModel.PushCommand.CanExecute(null), "busy push command is disabled");
        namedService.ReleasePushPlan();
        await execution;

        var detachedRoot = workspace.CreateDirectory("detached-branch");
        var detachedService = new RecordingPushGitService(
            new GitRepositoryState(true, detachedRoot, "detached at abc1234", [], null, IsDetachedHead: true));
        var detachedViewModel = CreateViewModel(detachedService, interactions, detachedRoot, [detachedRoot]);
        await detachedViewModel.RefreshAsync();
        Assert(!detachedViewModel.PushCommand.CanExecute(null), "detached HEAD disables push");
    }

    private static GitViewModel CreateViewModel(
        IGitService service,
        IUserInteractionService interactions,
        string primaryRoot,
        IReadOnlyList<string> roots) =>
        new(
            service,
            interactions,
            new TestLogger(),
            () => new GitContext(primaryRoot, primaryRoot, roots),
            () => false,
            _ => { });

    private static async Task<string> CreateRepositoryAsync(
        TempWorkspace workspace,
        string name,
        string branch = "main")
    {
        var repository = workspace.CreateDirectory(name);
        await RunGitAsync(repository, "init", "-q", "-b", branch);
        await ConfigureIdentityAsync(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "initial\n");
        await RunGitAsync(repository, "add", "--", "README.md");
        await RunGitAsync(repository, "commit", "-q", "-m", "initial");
        return repository;
    }

    private static async Task<string> CreateBareRemoteAsync(TempWorkspace workspace, string name)
    {
        var remote = workspace.CreateDirectory(name);
        await RunGitAsync(remote, "init", "--bare", "-q");
        return remote;
    }

    private static async Task ConfigureIdentityAsync(string repository)
    {
        await RunGitAsync(repository, "config", "user.name", "SynthiaCode Push Tests");
        await RunGitAsync(repository, "config", "user.email", "push-tests@example.invalid");
        await RunGitAsync(repository, "config", "core.autocrlf", "false");
    }

    private static async Task<string> CommitChangeAsync(string repository, string message)
    {
        await File.AppendAllTextAsync(Path.Combine(repository, "README.md"), $"{message}\n");
        await RunGitAsync(repository, "add", "--", "README.md");
        await RunGitAsync(repository, "commit", "-q", "-m", message);
        return (await RunGitAsync(repository, "rev-parse", "HEAD")).Trim();
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
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
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(await error) ? await output : await error);
        }
        await error;
        return await output;
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingPushGitService(params GitRepositoryState[] states) : IGitService
    {
        private readonly Dictionary<string, GitRepositoryState> statesByRoot = states.ToDictionary(
            state => Path.GetFullPath(state.RootPath!),
            StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> stateRequestCounts = new(StringComparer.OrdinalIgnoreCase);
        private TaskCompletionSource<bool>? pushPlanGate;
        private TaskCompletionSource<bool> pushPlanStarted = NewCompletionSource();

        public List<string> PushRoots { get; } = [];

        public Task<GitRepositoryState> GetRepositoryStateAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(workingDirectory);
            stateRequestCounts[root] = GetStateRequestCount(root) + 1;
            return Task.FromResult(statesByRoot[root]);
        }

        public Task<GitReviewCatalog> GetReviewCatalogAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            var state = statesByRoot[Path.GetFullPath(workingDirectory)];
            return Task.FromResult(new GitReviewCatalog(state.RootPath!, state.Branch ?? string.Empty, [], []));
        }

        public Task<string> GetDiffAsync(
            string repositoryRoot,
            GitChangedFile file,
            bool staged,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task ApplyHunkAsync(
            string repositoryRoot,
            GitDiffHunkPatch patch,
            GitHunkOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnstageAsync(string repositoryRoot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RevertAsync(
            string repositoryRoot,
            IReadOnlyCollection<GitChangedFile> files,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<GitCommitResult> CommitAsync(
            string repositoryRoot,
            string message,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitCommitResult("abc1234", message));

        public async Task<GitPushPlan> GetPushPlanAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default)
        {
            pushPlanStarted.TrySetResult(true);
            if (pushPlanGate is not null)
            {
                await pushPlanGate.Task.WaitAsync(cancellationToken);
            }
            var root = Path.GetFullPath(repositoryRoot);
            var branch = statesByRoot[root].Branch!;
            return new GitPushPlan(root, branch, "origin", $"refs/heads/{branch}", CreatesUpstream: true);
        }

        public Task<GitPushResult> PushAsync(
            string repositoryRoot,
            GitPushPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(repositoryRoot);
            PushRoots.Add(root);
            return Task.FromResult(new GitPushResult(root, plan.Branch, plan.Remote, plan.RemoteBranch, plan.CreatesUpstream));
        }

        public int GetStateRequestCount(string root) =>
            stateRequestCounts.GetValueOrDefault(Path.GetFullPath(root));

        public void BlockPushPlan()
        {
            pushPlanStarted = NewCompletionSource();
            pushPlanGate = NewCompletionSource();
        }

        public Task WaitForPushPlanAsync() => pushPlanStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public void ReleasePushPlan() => pushPlanGate!.TrySetResult(true);

        private static TaskCompletionSource<bool> NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingInteractionService : IUserInteractionService
    {
        public bool ConfirmResult { get; set; } = true;

        public string? LastConfirmation { get; private set; }

        public bool ConfirmDestructiveAction(string title, string message)
        {
            LastConfirmation = message;
            return ConfirmResult;
        }

        public bool ConfirmAction(string title, string message) => ConfirmDestructiveAction(title, message);

        public string? PromptForText(string title, string message, string initialValue) => null;

        public void OpenInEditor(string path) { }

        public void OpenExternalUri(Uri uri) { }

        public void ShowImagePreview(string path) { }

        public GeneratedImageEditSelection? SelectGeneratedImageEdit(string path) => null;

        public CodexReviewTarget? SelectCodeReviewTarget(GitReviewCatalog catalog) => null;

        public ProjectTrustDecision PromptForProjectTrust(string projectPath) => ProjectTrustDecision.Cancel;

        public ProjectFolderEditSelection? EditProjectFolders(RecentProject project) => null;

        public void RevealInExplorer(string path) { }
    }
}
