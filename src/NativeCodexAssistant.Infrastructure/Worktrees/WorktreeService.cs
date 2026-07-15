using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Worktrees;

namespace NativeCodexAssistant.Infrastructure.Worktrees;

public sealed class WorktreeService(IAppLogger logger) : IWorktreeService
{
    private const string RegistryDirectoryName = "codex-assistant";
    private const string RegistryFileName = "worktrees.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public async Task<AssistantWorktree> CreateAsync(
        WorktreeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task<AssistantWorktree> CreateCoreAsync(
        WorktreeCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repositoryRoot = RequireRepositoryRoot(request.RepositoryRoot);
        var baseTaskId = MakeSafeName(request.TaskName);
        var container = GetWorktreeContainer(repositoryRoot);
        Directory.CreateDirectory(container);

        var taskId = baseTaskId;
        var suffix = 2;
        string worktreePath;
        string branch;
        while (true)
        {
            worktreePath = Path.GetFullPath(Path.Combine(container, taskId));
            branch = $"codex/{taskId}";
            if (!Directory.Exists(worktreePath) && !await BranchExistsAsync(repositoryRoot, branch, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            taskId = $"{baseTaskId}-{suffix++}";
        }

        var startPoint = string.IsNullOrWhiteSpace(request.StartPoint) ? "HEAD" : request.StartPoint.Trim();
        await RunGitAsync(
            repositoryRoot,
            ["worktree", "add", "-b", branch, worktreePath, startPoint],
            cancellationToken).ConfigureAwait(false);

        var created = new AssistantWorktree(
            repositoryRoot,
            worktreePath,
            branch,
            taskId,
            request.ThreadId,
            DateTimeOffset.UtcNow);
        try
        {
            var registry = await LoadRegistryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            registry.Worktrees.RemoveAll(item => PathsEqual(item.Path, worktreePath));
            registry.Worktrees.Add(created);
            await SaveRegistryAsync(repositoryRoot, registry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await RunGitAsync(repositoryRoot, ["worktree", "remove", worktreePath], cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original registry failure; Git reports the leftover worktree separately.
            }

            throw;
        }

        logger.Log(
            AppLogLevel.Information,
            "assistant_worktree_created",
            "An assistant-owned Git worktree was created.",
            new Dictionary<string, string?> { ["path"] = worktreePath, ["branch"] = branch, ["threadId"] = request.ThreadId });
        return created;
    }

    public async Task<IReadOnlyList<AssistantWorktree>> ListAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        repositoryRoot = RequireRepositoryRoot(repositoryRoot);
        var registry = await LoadRegistryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var activePaths = await GetActiveWorktreePathsAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return registry.Worktrees
            .Where(item => PathsEqual(item.RepositoryRoot, repositoryRoot))
            .Where(item => activePaths.Contains(NormalizePath(item.Path)))
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public async Task RemoveAsync(
        string repositoryRoot,
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RemoveCoreAsync(repositoryRoot, worktreePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task RemoveCoreAsync(
        string repositoryRoot,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        repositoryRoot = RequireRepositoryRoot(repositoryRoot);
        var resolvedPath = Path.GetFullPath(worktreePath);
        if (PathsEqual(repositoryRoot, resolvedPath))
        {
            throw new InvalidOperationException("The primary checkout cannot be removed as a worktree.");
        }

        var registry = await LoadRegistryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        var owned = registry.Worktrees.FirstOrDefault(item =>
            PathsEqual(item.RepositoryRoot, repositoryRoot) && PathsEqual(item.Path, resolvedPath));
        if (owned is null)
        {
            throw new InvalidOperationException("Cleanup is allowed only for an assistant-created worktree recorded by this repository.");
        }

        var expectedContainer = GetWorktreeContainer(repositoryRoot);
        if (!IsDirectChild(expectedContainer, resolvedPath))
        {
            throw new InvalidOperationException("The recorded worktree path is outside the assistant worktree container.");
        }

        await RunGitAsync(repositoryRoot, ["worktree", "remove", resolvedPath], cancellationToken).ConfigureAwait(false);
        registry.Worktrees.Remove(owned);
        await SaveRegistryAsync(repositoryRoot, registry, cancellationToken).ConfigureAwait(false);
        logger.Log(
            AppLogLevel.Information,
            "assistant_worktree_removed",
            "An assistant-owned Git worktree was removed.",
            new Dictionary<string, string?> { ["path"] = resolvedPath, ["branch"] = owned.Branch, ["threadId"] = owned.ThreadId });
    }

    internal static string MakeSafeName(string value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "task" : value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(source.Length);
        var pendingSeparator = false;
        foreach (var character in source)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }

            if (builder.Length >= 48)
            {
                break;
            }
        }

        var result = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "task" : result;
    }

    private async Task<bool> BranchExistsAsync(string repositoryRoot, string branch, CancellationToken cancellationToken)
    {
        var result = await RunGitResultAsync(
            repositoryRoot,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"],
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<HashSet<string>> GetActiveWorktreePathsAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryRoot, ["worktree", "list", "--porcelain"], cancellationToken).ConfigureAwait(false);
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => NormalizePath(line[9..]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<WorktreeRegistry> LoadRegistryAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var path = await GetRegistryPathAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path))
        {
            return new WorktreeRegistry();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<WorktreeRegistry>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new WorktreeRegistry();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The assistant worktree registry is malformed; cleanup is disabled for safety.", ex);
        }
    }

    private async Task SaveRegistryAsync(string repositoryRoot, WorktreeRegistry registry, CancellationToken cancellationToken)
    {
        var path = await GetRegistryPathAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, registry, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private async Task<string> GetRegistryPathAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repositoryRoot, ["rev-parse", "--git-common-dir"], cancellationToken).ConfigureAwait(false);
        var gitDirectory = result.StandardOutput.Trim();
        if (!Path.IsPathRooted(gitDirectory))
        {
            gitDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, gitDirectory));
        }

        return Path.Combine(gitDirectory, RegistryDirectoryName, RegistryFileName);
    }

    private async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitResultAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"Git exited with code {result.ExitCode}."
                : detail);
        }

        return result;
    }

    private async Task<GitResult> RunGitResultAsync(
        string workingDirectory,
        IReadOnlyCollection<string> arguments,
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

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return new GitResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static string RequireRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new InvalidOperationException("A Git repository root is required.");
        }

        var resolved = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(resolved) || !Directory.Exists(Path.Combine(resolved, ".git")) && !File.Exists(Path.Combine(resolved, ".git")))
        {
            throw new InvalidOperationException("Worktree actions require a detected Git repository.");
        }

        return resolved;
    }

    private static string GetWorktreeContainer(string repositoryRoot)
    {
        var parent = Directory.GetParent(repositoryRoot)?.FullName
            ?? throw new InvalidOperationException("The repository has no parent directory for sibling worktrees.");
        return Path.GetFullPath(Path.Combine(parent, $"{new DirectoryInfo(repositoryRoot).Name}.worktrees"));
    }

    private static bool IsDirectChild(string container, string path) =>
        string.Equals(Directory.GetParent(path)?.FullName, Path.GetFullPath(container), StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class WorktreeRegistry
    {
        public List<AssistantWorktree> Worktrees { get; set; } = [];
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}
