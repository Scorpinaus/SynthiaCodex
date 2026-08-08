using System.Diagnostics;
using System.Text;
using SynthiaCode.Core.Git;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Git;

public sealed class GitService(IAppLogger logger) : IGitService
{
    private const int MaximumHunkPatchBytes = 8 * 1024 * 1024;

    public async Task<GitRepositoryState> GetRepositoryStateAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return GitRepositoryState.NotRepository("The selected project folder does not exist.");
        }

        var rootResult = await RunAsync(workingDirectory, ["rev-parse", "--show-toplevel"], [0, 128], cancellationToken)
            .ConfigureAwait(false);
        if (rootResult.ExitCode != 0 || string.IsNullOrWhiteSpace(rootResult.StandardOutput))
        {
            return GitRepositoryState.NotRepository("The selected project is not inside a Git repository.");
        }

        var root = Path.GetFullPath(rootResult.StandardOutput.Trim());
        var branchResult = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], [0, 1, 128], cancellationToken)
            .ConfigureAwait(false);
        var isDetachedHead = branchResult.ExitCode != 0;
        var branch = !isDetachedHead
            ? branchResult.StandardOutput.Trim()
            : await GetDetachedHeadLabelAsync(root, cancellationToken).ConfigureAwait(false);

        var statusResult = await RunAsync(
            root,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            [0],
            cancellationToken).ConfigureAwait(false);
        var files = ParsePorcelainStatus(statusResult.StandardOutput);

        return new GitRepositoryState(true, root, branch, files, null, isDetachedHead);
    }

    public async Task<GitBranchCatalog> GetBranchCatalogAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var state = await GetRepositoryStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!state.IsRepository || string.IsNullOrWhiteSpace(state.RootPath))
        {
            throw new InvalidOperationException(state.ErrorMessage ?? "Branch selection requires a Git repository.");
        }

        var root = state.RootPath;
        var branchesResult = await RunAsync(
            root,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes"],
            [0],
            cancellationToken).ConfigureAwait(false);
        var branches = ParseBranches(branchesResult.StandardOutput);
        var currentBranch = branches.FirstOrDefault(branch =>
            string.Equals(branch, state.Branch, StringComparison.Ordinal));
        var headResult = await RunAsync(
            root,
            ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"],
            [0, 1, 128],
            cancellationToken).ConfigureAwait(false);
        return new GitBranchCatalog(root, currentBranch, branches, headResult.ExitCode == 0);
    }

    public async Task<GitReviewCatalog> GetReviewCatalogAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var state = await GetRepositoryStateAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!state.IsRepository || string.IsNullOrWhiteSpace(state.RootPath))
        {
            throw new InvalidOperationException(state.ErrorMessage ?? "Code review requires a Git repository.");
        }

        var root = state.RootPath;
        var branchesResult = await RunAsync(
            root,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes"],
            [0],
            cancellationToken).ConfigureAwait(false);
        var branches = ParseReviewBranches(branchesResult.StandardOutput, state.Branch);

        var commitsResult = await RunAsync(
            root,
            ["log", "-n", "50", "--format=%H%x1f%h%x1f%s%x1e"],
            [0, 128],
            cancellationToken).ConfigureAwait(false);
        var commits = commitsResult.ExitCode == 0
            ? ParseReviewCommits(commitsResult.StandardOutput)
            : [];

        return new GitReviewCatalog(root, state.Branch ?? string.Empty, branches, commits);
    }

    public async Task<string> GetDiffAsync(
        string repositoryRoot,
        GitChangedFile file,
        bool staged,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryRoot(repositoryRoot);

        GitCommandResult result;
        if (file.IsUntracked)
        {
            var absolutePath = ResolveRepositoryPath(repositoryRoot, file.Path);
            result = await RunAsync(
                repositoryRoot,
                ["diff", "--no-index", "--", "NUL", absolutePath],
                [0, 1],
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var arguments = staged
                ? new[] { "diff", "--cached", "--", file.Path }
                : new[] { "diff", "--", file.Path };
            result = await RunAsync(repositoryRoot, arguments, [0], cancellationToken).ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? "No diff is available for this file in the selected view."
            : result.StandardOutput;
    }

    public async Task<IReadOnlyList<GitDiffDocument>> GetComparisonDiffAsync(
        string repositoryRoot,
        GitComparisonTarget target,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryRoot(repositoryRoot);
        ArgumentNullException.ThrowIfNull(target);
        var revision = await ResolveCommitAsync(repositoryRoot, target.Revision, cancellationToken)
            .ConfigureAwait(false);

        GitCommandResult result;
        string statusSummary;
        switch (target.Scope)
        {
            case GitDiffScope.Commit:
                result = await RunAsync(
                    repositoryRoot,
                    ["show", "--format=", "--root", "--find-renames", "--no-ext-diff", "--no-color", revision, "--"],
                    [0],
                    cancellationToken).ConfigureAwait(false);
                statusSummary = "Commit";
                break;
            case GitDiffScope.Branch:
                var mergeBase = await RunAsync(
                    repositoryRoot,
                    ["merge-base", "HEAD", revision],
                    [0],
                    cancellationToken).ConfigureAwait(false);
                result = await RunAsync(
                    repositoryRoot,
                    ["diff", "--find-renames", "--no-ext-diff", "--no-color", mergeBase.StandardOutput.Trim(), "HEAD", "--"],
                    [0],
                    cancellationToken).ConfigureAwait(false);
                statusSummary = "Branch";
                break;
            default:
                throw new ArgumentException("Only commit and branch comparisons are supported by Git.", nameof(target));
        }

        return GitUnifiedDiffDocumentParser.Parse(result.StandardOutput, statusSummary);
    }

    public async Task ApplyHunkAsync(
        string repositoryRoot,
        GitDiffHunkPatch patch,
        GitHunkOperation operation,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryRoot(repositoryRoot);
        ArgumentNullException.ThrowIfNull(patch);
        if (Encoding.UTF8.GetByteCount(patch.Patch) > MaximumHunkPatchBytes)
        {
            throw new InvalidOperationException("The selected hunk is too large to apply safely.");
        }

        var parsed = GitUnifiedDiffParser.ParseHunks(patch.Patch);
        if (parsed.Count != 1 || parsed[0] != patch)
        {
            throw new InvalidOperationException("The selected hunk patch is invalid or contains additional changes.");
        }

        var arguments = operation switch
        {
            GitHunkOperation.Stage => new[] { "apply", "--cached", "--whitespace=nowarn", "-" },
            GitHunkOperation.Unstage => new[] { "apply", "--cached", "--reverse", "--whitespace=nowarn", "-" },
            GitHunkOperation.Discard => new[] { "apply", "--reverse", "--whitespace=nowarn", "-" },
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported hunk operation.")
        };
        await RunAsync(repositoryRoot, arguments, [0], cancellationToken, patch.Patch).ConfigureAwait(false);
    }

    public Task StageAsync(
        string repositoryRoot,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default) =>
        RunPathCommandAsync(repositoryRoot, ["add", "--"], paths, cancellationToken);

    public async Task UnstageAsync(
        string repositoryRoot,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (await HasHeadAsync(repositoryRoot, cancellationToken).ConfigureAwait(false))
        {
            await RunPathCommandAsync(repositoryRoot, ["reset", "-q", "HEAD", "--"], paths, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await RunPathCommandAsync(repositoryRoot, ["rm", "--cached", "-r", "--"], paths, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RevertAsync(
        string repositoryRoot,
        IReadOnlyCollection<GitChangedFile> files,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryRoot(repositoryRoot);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Select at least one changed file.");
        }

        var hasHead = await HasHeadAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var trackedPaths = files
            .Where(file => !file.IsUntracked)
            .SelectMany(file => new[] { file.Path, file.OriginalPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (trackedPaths.Length > 0 && hasHead)
        {
            await RunPathCommandAsync(
                repositoryRoot,
                ["restore", "--source=HEAD", "--staged", "--worktree", "--"],
                trackedPaths,
                cancellationToken).ConfigureAwait(false);
        }

        else if (trackedPaths.Length > 0)
        {
            await RunPathCommandAsync(
                repositoryRoot,
                ["rm", "--cached", "-r", "--"],
                trackedPaths,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in files.Where(file => file.IsUntracked || !hasHead))
        {
            var fullPath = ResolveRepositoryPath(repositoryRoot, file.Path);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }

    public async Task<GitCommitResult> CommitAsync(
        string repositoryRoot,
        string message,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryRoot(repositoryRoot);
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new InvalidOperationException("Enter a commit message.");
        }

        var commit = await RunAsync(repositoryRoot, ["commit", "-m", message.Trim()], [0], cancellationToken)
            .ConfigureAwait(false);
        var id = await RunAsync(repositoryRoot, ["rev-parse", "--short", "HEAD"], [0], cancellationToken)
            .ConfigureAwait(false);
        var summary = commit.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Commit created";
        return new GitCommitResult(id.StandardOutput.Trim(), summary);
    }

    public async Task<GitPushPlan> GetPushPlanAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        EnsureRepositoryRoot(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var branchResult = await RunAsync(
            root,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            [0, 1, 128],
            cancellationToken).ConfigureAwait(false);
        var branch = branchResult.StandardOutput.Trim();
        if (branchResult.ExitCode != 0 || string.IsNullOrWhiteSpace(branch))
        {
            throw new InvalidOperationException("Push requires a named branch; detached HEAD cannot be pushed.");
        }
        if (!await HasHeadAsync(root, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Branch '{branch}' has no commits to push.");
        }

        var localRef = $"refs/heads/{branch}";
        var upstreamResult = await RunAsync(
            root,
            ["for-each-ref", "--format=%(upstream:remotename)%00%(upstream:remoteref)", localRef],
            [0],
            cancellationToken).ConfigureAwait(false);
        var upstream = upstreamResult.StandardOutput.Trim('\r', '\n').Split('\0');
        if (upstream.Length >= 2 &&
            !string.IsNullOrWhiteSpace(upstream[0]) &&
            !string.IsNullOrWhiteSpace(upstream[1]))
        {
            var remoteRef = upstream[1].Trim();
            if (!remoteRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The upstream configured for branch '{branch}' is not a remote branch. Configure a branch upstream before pushing.");
            }
            return new GitPushPlan(root, branch, upstream[0].Trim(), remoteRef, CreatesUpstream: false);
        }

        var remoteResult = await RunAsync(root, ["remote"], [0], cancellationToken).ConfigureAwait(false);
        var remotes = remoteResult.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (remotes.Length == 0)
        {
            throw new InvalidOperationException(
                $"No Git remotes are configured. Add a remote before pushing branch '{branch}'.");
        }
        if (remotes.Length > 1)
        {
            throw new InvalidOperationException(
                $"Branch '{branch}' has no upstream and multiple remotes are configured ({string.Join(", ", remotes)}). " +
                "Configure an upstream before pushing.");
        }

        return new GitPushPlan(root, branch, remotes[0], localRef, CreatesUpstream: true);
    }

    public async Task<GitPushResult> PushAsync(
        string repositoryRoot,
        GitPushPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureRepositoryRoot(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (!string.Equals(root, Path.GetFullPath(plan.RepositoryRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The confirmed push target does not match the selected repository.");
        }

        var currentPlan = await GetPushPlanAsync(root, cancellationToken).ConfigureAwait(false);
        if (!PushPlansMatch(plan, currentPlan))
        {
            throw new InvalidOperationException(
                "The branch or remote changed after confirmation. Refresh Git status and confirm the push again.");
        }

        var arguments = plan.CreatesUpstream
            ? new[] { "push", "--set-upstream", "--", plan.Remote, plan.Branch }
            : new[] { "push", "--", plan.Remote, $"HEAD:{plan.RemoteRef}" };
        try
        {
            await RunAsync(root, arguments, [0], cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(CreatePushFailureMessage(plan, exception.Message));
        }

        return new GitPushResult(root, plan.Branch, plan.Remote, plan.RemoteBranch, plan.CreatesUpstream);
    }

    internal static IReadOnlyList<GitChangedFile> ParsePorcelainStatus(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var fields = output.Split('\0');
        var files = new List<GitChangedFile>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length < 4)
            {
                continue;
            }

            var indexStatus = field[0];
            var workTreeStatus = field[1];
            var path = field[3..];
            string? originalPath = null;
            if (indexStatus is 'R' or 'C' || workTreeStatus is 'R' or 'C')
            {
                if (index + 1 < fields.Length && !string.IsNullOrEmpty(fields[index + 1]))
                {
                    originalPath = fields[++index];
                }
            }

            files.Add(new GitChangedFile(path, originalPath, indexStatus, workTreeStatus));
        }

        return files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<string> ParseBranches(string output) =>
        output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(branch => !branch.EndsWith("/HEAD", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(branch => branch, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static IReadOnlyList<string> ParseReviewBranches(string output, string? currentBranch) =>
        ParseBranches(output)
            .Where(branch => !string.Equals(branch, currentBranch, StringComparison.Ordinal))
            .ToArray();

    internal static IReadOnlyList<GitReviewCommit> ParseReviewCommits(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return output
            .Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(record => record.Trim('\r', '\n').Split('\x1f', 3))
            .Where(fields => fields.Length == 3 &&
                !string.IsNullOrWhiteSpace(fields[0]) &&
                !string.IsNullOrWhiteSpace(fields[1]))
            .Select(fields => new GitReviewCommit(fields[0], fields[1], fields[2]))
            .ToArray();
    }

    private async Task<string> GetDetachedHeadLabelAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repositoryRoot, ["rev-parse", "--short", "HEAD"], [0, 128], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 ? $"detached at {result.StandardOutput.Trim()}" : "No commits yet";
    }

    private static bool PushPlansMatch(GitPushPlan confirmed, GitPushPlan current) =>
        string.Equals(confirmed.RepositoryRoot, current.RepositoryRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(confirmed.Branch, current.Branch, StringComparison.Ordinal) &&
        string.Equals(confirmed.Remote, current.Remote, StringComparison.Ordinal) &&
        string.Equals(confirmed.RemoteRef, current.RemoteRef, StringComparison.Ordinal) &&
        confirmed.CreatesUpstream == current.CreatesUpstream;

    private static string CreatePushFailureMessage(GitPushPlan plan, string detail)
    {
        if (detail.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("[rejected]", StringComparison.OrdinalIgnoreCase))
        {
            return $"Push was rejected because {plan.Remote}/{plan.RemoteBranch} contains changes that are not in {plan.Branch}. " +
                "Integrate the remote changes before trying again.";
        }
        if (detail.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("could not read username", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("access denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"Git could not authenticate to remote '{plan.Remote}'. Check the credentials configured for Git and try again.";
        }
        if (detail.Contains("repository not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return $"Remote '{plan.Remote}' was not found or access was denied. Check the remote configuration and permissions.";
        }
        if (detail.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("unable to access", StringComparison.OrdinalIgnoreCase))
        {
            return $"Git could not reach remote '{plan.Remote}'. Check the network and remote configuration, then try again.";
        }
        if (detail.Contains("not installed or could not be started", StringComparison.OrdinalIgnoreCase))
        {
            return "Git is not installed or could not be started.";
        }

        return $"Git could not push branch '{plan.Branch}' to '{plan.Remote}/{plan.RemoteBranch}'. " +
            "Use the integrated terminal for additional Git diagnostics.";
    }

    private async Task<bool> HasHeadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        EnsureRepositoryRoot(repositoryRoot);
        var result = await RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "HEAD"],
            [0, 128],
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<string> ResolveCommitAsync(
        string repositoryRoot,
        string revision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new InvalidOperationException("Select a Git revision to compare.");
        }
        var result = await RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "--end-of-options", $"{revision.Trim()}^{{commit}}"],
            [0],
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    private async Task RunPathCommandAsync(
        string repositoryRoot,
        IReadOnlyCollection<string> baseArguments,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        EnsureRepositoryRoot(repositoryRoot);
        if (paths.Count == 0)
        {
            throw new InvalidOperationException("Select at least one changed file.");
        }

        var arguments = baseArguments.Concat(paths).ToArray();
        await RunAsync(repositoryRoot, arguments, [0], cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyCollection<string> arguments,
        IReadOnlyCollection<int> allowedExitCodes,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (standardInput is not null)
        {
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        using var process = new Process { StartInfo = startInfo };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Git is not installed or could not be started.", ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            throw;
        }
        var result = new GitCommandResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));

        logger.Log(
            result.ExitCode == 0 ? AppLogLevel.Debug : AppLogLevel.Warning,
            "git_command_completed",
            "A Git command completed.",
            new Dictionary<string, string?>
            {
                ["command"] = arguments.FirstOrDefault(),
                ["exitCode"] = result.ExitCode.ToString()
            });

        if (!allowedExitCodes.Contains(result.ExitCode))
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"Git exited with code {result.ExitCode}."
                : detail);
        }

        return result;
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void EnsureRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(Path.Combine(repositoryRoot, ".git")) && !File.Exists(Path.Combine(repositoryRoot, ".git")))
        {
            throw new InvalidOperationException("Git actions require a detected repository.");
        }
    }

    private static string ResolveRepositoryPath(string repositoryRoot, string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected file is outside the repository.");
        }

        return fullPath;
    }

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
