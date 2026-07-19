using System.Diagnostics;
using SynthiaCode.Core.Auth;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Logging;

namespace SynthiaCode.Infrastructure.Auth;

public sealed class CodexAuthService(IAppLogger logger) : IAuthService
{
    public Task<AuthenticationState> GetAuthenticationStateAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!installation.IsFound)
        {
            return Task.FromResult(new AuthenticationState(
                AuthReadiness.Unavailable,
                "Sign-in unavailable",
                "Codex CLI must be installed before sign-in can be checked.",
                GetCodexHome()));
        }

        var codexHome = GetCodexHome();
        var authPath = codexHome is null ? null : Path.Combine(codexHome, "auth.json");

        if (authPath is not null && File.Exists(authPath))
        {
            return Task.FromResult(new AuthenticationState(
                AuthReadiness.LikelySignedIn,
                "Likely signed in",
                "A Codex credential cache exists. Token contents were not read.",
                codexHome));
        }

        if (codexHome is not null && Directory.Exists(codexHome))
        {
            return Task.FromResult(new AuthenticationState(
                AuthReadiness.Unknown,
                "Sign-in unknown",
                "No file credential cache was found. Codex may still use the OS credential store.",
                codexHome));
        }

        return Task.FromResult(new AuthenticationState(
            AuthReadiness.NotSignedIn,
            "Not signed in",
            "No Codex home folder or file credential cache was found.",
            codexHome));
    }

    public Task<bool> StartLoginAsync(
        CodexInstallation installation,
        LoginMethod method,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!installation.IsFound || installation.ExecutablePath is null)
        {
            return Task.FromResult(false);
        }

        var arguments = method switch
        {
            LoginMethod.DeviceCode => "login --device-auth",
            _ => "login"
        };

        return Task.FromResult(StartVisibleCommand(installation.ExecutablePath, arguments, "codex_login_started"));
    }

    public Task<bool> StartLogoutAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!installation.IsFound || installation.ExecutablePath is null)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(StartVisibleCommand(installation.ExecutablePath, "logout", "codex_logout_started"));
    }

    private bool StartVisibleCommand(string executablePath, string arguments, string eventName)
    {
        try
        {
            var quotedCommand = $"\"{executablePath}\" {arguments}";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k {quotedCommand}",
                UseShellExecute = true
            });

            logger.Log(AppLogLevel.Information, eventName, "Started a visible Codex authentication command.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Log(AppLogLevel.Error, "codex_auth_command_failed", "Could not start Codex authentication command.", exception: ex);
            return false;
        }
    }

    private static string? GetCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".codex");
    }
}
