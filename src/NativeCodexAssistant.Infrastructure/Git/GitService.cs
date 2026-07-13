using System.Diagnostics;
using System.Text;
using NativeCodexAssistant.Core.Git;
using NativeCodexAssistant.Core.Logging;

namespace NativeCodexAssistant.Infrastructure.Git;

public sealed class GitService(IAppLogger logger) : IGitService
{
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
        var branch = branchResult.ExitCode == 0
            ? branchResult.StandardOutput.Trim()
            : await GetDetachedHeadLabelAsync(root, cancellationToken).ConfigureAwait(false);

        var statusResult = await RunAsync(
            root,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            [0],
            cancellationToken).ConfigureAwait(false);
        var files = ParsePorcelainStatus(statusResult.StandardOutput);

        return new GitRepositoryState(true, root, branch, files, null);
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

    private async Task<string> GetDetachedHeadLabelAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repositoryRoot, ["rev-parse", "--short", "HEAD"], [0, 128], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 ? $"detached at {result.StandardOutput.Trim()}" : "No commits yet";
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
        CancellationToken cancellationToken)
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
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

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
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
