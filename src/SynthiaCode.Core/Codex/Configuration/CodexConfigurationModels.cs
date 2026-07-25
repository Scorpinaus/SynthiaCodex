namespace SynthiaCode.Core.Codex.Configuration;

public enum CodexConfigurationFileKind
{
    SharedInstructions,
    SharedConfiguration,
    WorkspaceInstructions,
    ProjectConfiguration
}

public sealed record CodexConfigurationDocument(
    CodexConfigurationFileKind Kind,
    string Path,
    string Content,
    string Revision,
    bool Exists);

public sealed record CodexConfigurationSource(
    CodexConfigurationFileKind Kind,
    string Path,
    string Scope,
    int Precedence,
    bool Exists,
    bool IsEditable)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string Availability => Exists ? "Active" : "Not created";
}

public sealed record CodexConfigurationSnapshot(
    CodexConfigurationDocument SharedInstructions,
    CodexConfigurationDocument SharedConfiguration,
    IReadOnlyList<CodexConfigurationSource> Provenance);

public interface ISharedCodexConfigurationService
{
    string CodexHomePath { get; }

    Task<CodexConfigurationSnapshot> LoadAsync(
        string? workspacePath,
        CancellationToken cancellationToken = default);

    Task<CodexConfigurationDocument> SaveAsync(
        CodexConfigurationFileKind kind,
        string content,
        string expectedRevision,
        CancellationToken cancellationToken = default);

    Task<CodexConfigurationDocument> EnsureExistsAsync(
        CodexConfigurationFileKind kind,
        CancellationToken cancellationToken = default);
}

public sealed class CodexConfigurationConflictException(string path)
    : IOException($"The Codex configuration file changed outside SynthiaCode: {path}")
{
    public string Path { get; } = path;
}
