namespace NativeCodexAssistant.Core.Codex;

public sealed record CodexInstallation(
    bool IsFound,
    string? ExecutablePath,
    string? Version,
    string Summary,
    string Detail)
{
    public static CodexInstallation Missing(string detail) =>
        new(false, null, null, "Codex CLI not found", detail);
}
