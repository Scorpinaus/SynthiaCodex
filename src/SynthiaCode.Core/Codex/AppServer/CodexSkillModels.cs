namespace SynthiaCode.Core.Codex.AppServer;

public enum CodexSkillScope
{
    Unknown,
    User,
    Repository,
    System,
    Admin
}

public sealed record CodexSkillListRequest(
    IReadOnlyList<string> Cwds,
    bool ForceReload = false);

public sealed record CodexSkillListResult(
    IReadOnlyList<CodexSkillContextResult> Contexts,
    bool IsSupported = true);

public sealed record CodexSkillContextResult(
    string Cwd,
    IReadOnlyList<CodexSkillMetadata> Skills,
    IReadOnlyList<CodexSkillLoadError> Errors);

public sealed record CodexSkillMetadata(
    string Name,
    string Description,
    string Path,
    CodexSkillScope Scope,
    bool Enabled,
    string? ShortDescription,
    CodexSkillInterface? Interface,
    CodexSkillDependencies? Dependencies);

public sealed record CodexSkillInterface(
    string? DisplayName,
    string? ShortDescription,
    string? BrandColor,
    string? DefaultPrompt,
    string? IconSmall,
    string? IconLarge);

public sealed record CodexSkillDependencies(
    IReadOnlyList<CodexSkillToolDependency> Tools);

public sealed record CodexSkillToolDependency(
    string Type,
    string Value,
    string? Description,
    string? Command,
    string? Transport,
    string? Url);

public sealed record CodexSkillLoadError(
    string Path,
    string Message);

public sealed record CodexSkillConfigWriteRequest(
    string Path,
    bool Enabled);

public sealed record CodexSkillConfigWriteResult(
    bool EffectiveEnabled,
    bool IsSupported = true);
