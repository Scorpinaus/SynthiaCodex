using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;

namespace SynthiaCode.Core.Diagnostics;

public sealed record AppDiagnostics(
    string AppVersion,
    string DotNetVersion,
    string WindowsVersion,
    string SettingsPath,
    CodexInstallation Codex,
    AuthenticationState Authentication);
