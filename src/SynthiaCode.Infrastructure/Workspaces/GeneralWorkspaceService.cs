using SynthiaCode.Core.Workspaces;

namespace SynthiaCode.Infrastructure.Workspaces;

public sealed class GeneralWorkspaceService : IGeneralWorkspaceService
{
    private readonly string appDataRoot;

    public GeneralWorkspaceService(string appDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            throw new ArgumentException("Application data directory is required.", nameof(appDataDirectory));
        }

        appDataRoot = Path.GetFullPath(appDataDirectory);
        WorkspacePath = Path.GetFullPath(Path.Combine(appDataRoot, "workspaces", "general"));
        EnsureContained(WorkspacePath);
    }

    public string WorkspacePath { get; }

    public string EnsureWorkspace()
    {
        EnsureContained(WorkspacePath);
        if (File.Exists(WorkspacePath))
        {
            throw new IOException($"The General workspace path is occupied by a file: {WorkspacePath}");
        }

        Directory.CreateDirectory(WorkspacePath);
        return WorkspacePath;
    }

    private void EnsureContained(string path)
    {
        var relative = Path.GetRelativePath(appDataRoot, path);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("The General workspace must remain inside SynthiaCode application data.");
        }
    }
}
