using NativeCodexAssistant.Core.Codex;

namespace NativeCodexAssistant.Core.Auth;

public interface IAuthService
{
    Task<AuthenticationState> GetAuthenticationStateAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default);

    Task<bool> StartLoginAsync(
        CodexInstallation installation,
        LoginMethod method,
        CancellationToken cancellationToken = default);

    Task<bool> StartLogoutAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default);
}
