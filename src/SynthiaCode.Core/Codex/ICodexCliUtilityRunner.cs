namespace SynthiaCode.Core.Codex;

public sealed record CodexCliUtilityResult(
    string Command,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface ICodexCliUtilityRunner
{
    Task<CodexCliUtilityResult> RunDoctorAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default);
}
