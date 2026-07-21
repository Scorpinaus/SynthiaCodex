using System.Diagnostics;

namespace SynthiaCode.Infrastructure.Codex;

public sealed class CodexRuntimeEnvironment
{
    public const string HomeEnvironmentVariable = "CODEX_HOME";

    public CodexRuntimeEnvironment(string homePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homePath);

        HomePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(homePath));
        Directory.CreateDirectory(HomePath);
    }

    public string HomePath { get; }

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment[HomeEnvironmentVariable] = HomePath;
    }
}
