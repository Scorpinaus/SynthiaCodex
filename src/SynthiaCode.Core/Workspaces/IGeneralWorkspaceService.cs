namespace SynthiaCode.Core.Workspaces;

public interface IGeneralWorkspaceService
{
    string WorkspacePath { get; }

    string EnsureWorkspace();
}
