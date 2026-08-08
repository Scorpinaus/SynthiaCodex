using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Harnesses.Codex;
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

public interface ICodexSkillsSessionFeature : ICodexSkillsFeature
{
    event EventHandler<AppServerSessionStateChangedEventArgs>? StateChanged;
}

public interface IProjectTrustSession : ICodexProjectTrustFeature
{
    Task EnsureProjectTrustSessionConnectedAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default);
}

public interface IAppServerSessionCoordinator :
    IAsyncDisposable,
    ICodexHarnessBackend,
    ICodexAccountFeature,
    ICodexExecutionPolicyFeature,
    ICodexSkillsSessionFeature,
    ICodexConfigurationFeature,
    IProjectTrustSession,
    ICodexGoalFeature,
    ICodexReviewFeature,
    ICodexApprovalFeature
{
    event EventHandler<CodexServerRequest>? ServerRequestReceived;

    event EventHandler<AppServerConnectionFailedEventArgs>? ConnectionFailed;

    AppServerSessionState State { get; }

    AppServerNotificationBatchMetrics NotificationMetrics { get; }

    void FlushNotifications();
}
