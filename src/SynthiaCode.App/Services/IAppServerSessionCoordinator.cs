using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Infrastructure.Codex;

namespace SynthiaCode.App.Services;

public enum AppServerSessionState
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    Unavailable,
    Disposed
}

public sealed class AppServerSessionStateChangedEventArgs(
    AppServerSessionState state,
    AppServerSessionState previousState) : EventArgs
{
    public AppServerSessionState State { get; } = state;

    public AppServerSessionState PreviousState { get; } = previousState;
}

public interface IAppServerSessionCoordinator : IAsyncDisposable
{
    event EventHandler<AppServerNotification>? NotificationReceived;

    event EventHandler<CodexServerRequest>? ServerRequestReceived;

    event EventHandler<AppServerConnectionFailedEventArgs>? ConnectionFailed;

    event EventHandler<AppServerSessionStateChangedEventArgs>? StateChanged;

    AppServerSessionState State { get; }

    AppServerNotificationBatchMetrics NotificationMetrics { get; }

    Task EnsureConnectedAsync(CodexInstallation installation, CancellationToken cancellationToken = default);

    Task<CodexThreadStartResult> StartThreadAsync(CodexThreadStartOptions options, CancellationToken cancellationToken = default);

    Task<CodexThreadResumeResult> ResumeThreadAsync(CodexThreadResumeRequest request, CancellationToken cancellationToken = default);

    Task<CodexThreadReadResult> ReadThreadAsync(CodexThreadReadRequest request, CancellationToken cancellationToken = default);

    Task<CodexThreadForkResult> ForkThreadAsync(CodexThreadForkRequest request, CancellationToken cancellationToken = default);

    Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken = default);

    Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken = default);

    Task<CodexTurnSteerResult> SteerTurnAsync(CodexTurnSteerRequest request, CancellationToken cancellationToken = default);

    Task<CodexTurnStartResult> StartTurnAsync(CodexTurnStartRequest request, CancellationToken cancellationToken = default);

    Task CancelTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodexModelOption>> ListModelsAsync(CancellationToken cancellationToken = default);

    Task<CodexAccountReadResult> ReadAccountAsync(bool refreshToken = false, CancellationToken cancellationToken = default);

    Task<CodexAccountRateLimitsResult> ReadAccountRateLimitsAsync(CancellationToken cancellationToken = default);

    Task<CodexExecutionPolicyConfig> ReadExecutionPolicyConfigAsync(
        string? cwd = null,
        CancellationToken cancellationToken = default);

    Task<CodexExecutionPolicyRequirements> ReadExecutionPolicyRequirementsAsync(
        CancellationToken cancellationToken = default);

    Task<CodexPermissionProfileListResult> ListPermissionProfilesAsync(
        string cwd,
        CancellationToken cancellationToken = default);

    Task RespondToServerRequestAsync(
        CodexServerRequest request,
        CodexServerRequestResponse response,
        CancellationToken cancellationToken = default);

    void FlushNotifications();
}
