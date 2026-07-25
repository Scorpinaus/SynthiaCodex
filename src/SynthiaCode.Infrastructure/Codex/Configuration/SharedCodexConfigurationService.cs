using System.Security.Cryptography;
using System.Text;
using SynthiaCode.Core.Codex.Configuration;

namespace SynthiaCode.Infrastructure.Codex.Configuration;

public sealed class SharedCodexConfigurationService : ISharedCodexConfigurationService
{
    public const int MaximumFileBytes = 512 * 1024;

    private const string MissingRevision = "missing";
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public SharedCodexConfigurationService(string codexHomePath)
    {
        if (string.IsNullOrWhiteSpace(codexHomePath))
        {
            throw new ArgumentException("A Codex home path is required.", nameof(codexHomePath));
        }

        CodexHomePath = Path.GetFullPath(codexHomePath);
    }

    public string CodexHomePath { get; }

    public async Task<CodexConfigurationSnapshot> LoadAsync(
        string? workspacePath,
        CancellationToken cancellationToken = default)
    {
        var sharedInstructions = await ReadDocumentAsync(
            CodexConfigurationFileKind.SharedInstructions,
            cancellationToken).ConfigureAwait(false);
        var sharedConfiguration = await ReadDocumentAsync(
            CodexConfigurationFileKind.SharedConfiguration,
            cancellationToken).ConfigureAwait(false);
        var provenance = DiscoverProvenance(
            workspacePath,
            sharedInstructions,
            sharedConfiguration);
        return new CodexConfigurationSnapshot(
            sharedInstructions,
            sharedConfiguration,
            provenance);
    }

    public async Task<CodexConfigurationDocument> SaveAsync(
        CodexConfigurationFileKind kind,
        string content,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        var path = GetEditablePath(kind);
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} must be {MaximumFileBytes / 1024} KiB or smaller.");
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadDocumentAsync(kind, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new CodexConfigurationConflictException(path);
            }

            Directory.CreateDirectory(CodexHomePath);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return CreateDocument(kind, path, bytes, exists: true);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<CodexConfigurationDocument> EnsureExistsAsync(
        CodexConfigurationFileKind kind,
        CancellationToken cancellationToken = default)
    {
        var current = await ReadDocumentAsync(kind, cancellationToken).ConfigureAwait(false);
        if (current.Exists)
        {
            return current;
        }

        try
        {
            return await SaveAsync(kind, string.Empty, current.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexConfigurationConflictException)
        {
            current = await ReadDocumentAsync(kind, cancellationToken).ConfigureAwait(false);
            if (current.Exists)
            {
                return current;
            }

            throw;
        }
    }

    private async Task<CodexConfigurationDocument> ReadDocumentAsync(
        CodexConfigurationFileKind kind,
        CancellationToken cancellationToken)
    {
        var path = GetEditablePath(kind);
        if (!File.Exists(path))
        {
            return new CodexConfigurationDocument(kind, path, string.Empty, MissingRevision, Exists: false);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} is larger than the {MaximumFileBytes / 1024} KiB editor limit.");
        }

        using var memory = new MemoryStream((int)stream.Length);
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return CreateDocument(kind, path, memory.ToArray(), exists: true);
    }

    private string GetEditablePath(CodexConfigurationFileKind kind) => kind switch
    {
        CodexConfigurationFileKind.SharedInstructions => Path.Combine(CodexHomePath, "AGENTS.md"),
        CodexConfigurationFileKind.SharedConfiguration => Path.Combine(CodexHomePath, "config.toml"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "Only shared Codex files can be edited from this service.")
    };

    private static CodexConfigurationDocument CreateDocument(
        CodexConfigurationFileKind kind,
        string path,
        byte[] bytes,
        bool exists) =>
        new(
            kind,
            path,
            Utf8.GetString(bytes),
            exists ? Convert.ToHexString(SHA256.HashData(bytes)) : MissingRevision,
            exists);

    private IReadOnlyList<CodexConfigurationSource> DiscoverProvenance(
        string? workspacePath,
        CodexConfigurationDocument sharedInstructions,
        CodexConfigurationDocument sharedConfiguration)
    {
        var sources = new List<CodexConfigurationSource>
        {
            new(
                CodexConfigurationFileKind.SharedInstructions,
                sharedInstructions.Path,
                "Shared CODEX_HOME instructions",
                0,
                sharedInstructions.Exists,
                IsEditable: true),
            new(
                CodexConfigurationFileKind.SharedConfiguration,
                sharedConfiguration.Path,
                "Shared CODEX_HOME configuration",
                1,
                sharedConfiguration.Exists,
                IsEditable: true)
        };
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return sources;
        }

        var workspaceDirectory = ResolveWorkspaceDirectory(workspacePath);
        if (workspaceDirectory is null)
        {
            return sources;
        }

        var repositoryRoot = FindRepositoryRoot(workspaceDirectory) ?? workspaceDirectory;
        var precedence = sources.Count;
        foreach (var directory in EnumerateRootToLeaf(repositoryRoot, workspaceDirectory))
        {
            var relative = Path.GetRelativePath(repositoryRoot, directory);
            var scope = relative == "."
                ? "Workspace root"
                : $"Workspace - {relative}";
            var instructionsPath = Path.Combine(directory, "AGENTS.md");
            if (File.Exists(instructionsPath))
            {
                sources.Add(new CodexConfigurationSource(
                    CodexConfigurationFileKind.WorkspaceInstructions,
                    instructionsPath,
                    scope,
                    precedence++,
                    Exists: true,
                    IsEditable: false));
            }

            var projectConfigurationPath = Path.Combine(directory, ".codex", "config.toml");
            if (File.Exists(projectConfigurationPath))
            {
                sources.Add(new CodexConfigurationSource(
                    CodexConfigurationFileKind.ProjectConfiguration,
                    projectConfigurationPath,
                    scope,
                    precedence++,
                    Exists: true,
                    IsEditable: false));
            }
        }

        return sources;
    }

    private static string? ResolveWorkspaceDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            return File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRootToLeaf(string root, string leaf)
    {
        var directories = new Stack<string>();
        for (var current = new DirectoryInfo(leaf); current is not null; current = current.Parent)
        {
            directories.Push(current.FullName);
            if (PathsEqual(current.FullName, root))
            {
                break;
            }
        }

        return directories;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
