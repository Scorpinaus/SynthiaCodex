using System.Diagnostics;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Codex.AppServer;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Infrastructure.Codex;

namespace NativeCodexAssistant.App.Services;

public sealed class AppServerSessionCoordinator : IAppServerSessionCoordinator
{
    private readonly ICodexProcessService processService;
    private readonly IAppLogger logger;
    private readonly CodexAppServerClientMetadata metadata;
    private readonly AppServerNotificationBatcher notificationBatcher;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object requestGate = new();
    private readonly Dictionary<CodexRequestId, CodexAppServerClient> requestClients = [];
    private CodexAppServerClient? client;
    private AppServerSessionState state;
    private long recoveryStartedTimestamp;
    private bool disposed;

    public AppServerSessionCoordinator(
        ICodexProcessService processService,
        IAppLogger logger,
        CodexAppServerClientMetadata metadata)
    {
        this.processService = processService;
        this.logger = logger;
        this.metadata = metadata;
        notificationBatcher = new AppServerNotificationBatcher(notification =>
            NotificationReceived?.Invoke(this, notification));
    }

    public event EventHandler<AppServerNotification>? NotificationReceived;

    public event EventHandler<CodexServerRequest>? ServerRequestReceived;

    public event EventHandler<AppServerConnectionFailedEventArgs>? ConnectionFailed;

    public event EventHandler<AppServerSessionStateChangedEventArgs>? StateChanged;

    public AppServerSessionState State => state;

    public AppServerNotificationBatchMetrics NotificationMetrics => notificationBatcher.Metrics;

    public async Task EnsureConnectedAsync(CodexInstallation installation, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (client?.IsHealthy == true)
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (client?.IsHealthy == true)
            {
                return;
            }

            var isRecovery = client is not null || state == AppServerSessionState.Reconnecting;
            await DisposeClientAsync().ConfigureAwait(false);
            SetState(isRecovery ? AppServerSessionState.Reconnecting : AppServerSessionState.Connecting);

            CodexAppServerClient? candidate = null;
            try
            {
                var transport = await processService
                    .StartAppServerTransportAsync(installation, cancellationToken)
                    .ConfigureAwait(false);
                candidate = new CodexAppServerClient(transport, metadata);
                candidate.NotificationReceived += OnNotificationReceived;
                candidate.ServerRequestReceived += OnServerRequestReceived;
                candidate.ConnectionFailed += OnConnectionFailed;
                await candidate.InitializeAsync(CodexInitializeOptions.Default, cancellationToken).ConfigureAwait(false);
                client = candidate;
                SetState(AppServerSessionState.Connected);

                if (isRecovery && recoveryStartedTimestamp != 0)
                {
                    logger.Log(
                        AppLogLevel.Information,
                        "app_server_recovered",
                        "The Codex app-server connection recovered.",
                        new Dictionary<string, string?>
                        {
                            ["elapsedMilliseconds"] = ((long)Stopwatch.GetElapsedTime(recoveryStartedTimestamp).TotalMilliseconds).ToString()
                        });
                    recoveryStartedTimestamp = 0;
                }
            }
            catch
            {
                if (candidate is not null)
                {
                    candidate.NotificationReceived -= OnNotificationReceived;
                    candidate.ServerRequestReceived -= OnServerRequestReceived;
                    candidate.ConnectionFailed -= OnConnectionFailed;
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }

                SetState(AppServerSessionState.Unavailable);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public Task<CodexThreadStartResult> StartThreadAsync(CodexThreadStartOptions options, CancellationToken cancellationToken = default) =>
        GetConnectedClient().StartThreadAsync(options, cancellationToken);

    public Task<CodexThreadResumeResult> ResumeThreadAsync(CodexThreadResumeRequest request, CancellationToken cancellationToken = default) =>
        GetConnectedClient().ResumeThreadAsync(request, cancellationToken);

    public Task<CodexThreadReadResult> ReadThreadAsync(CodexThreadReadRequest request, CancellationToken cancellationToken = default) =>
        GetConnectedClient().ReadThreadAsync(request, cancellationToken);

    public Task<CodexThreadForkResult> ForkThreadAsync(CodexThreadForkRequest request, CancellationToken cancellationToken = default) =>
        GetConnectedClient().ForkThreadAsync(request, cancellationToken);

    public Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        GetConnectedClient().ArchiveThreadAsync(threadId, cancellationToken);

    public Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken = default) =>
        GetConnectedClient().UnarchiveThreadAsync(threadId, cancellationToken);

    public Task<CodexTurnSteerResult> SteerTurnAsync(CodexTurnSteerRequest request, CancellationToken cancellationToken = default) =>
        GetConnectedClient().SteerTurnAsync(request, cancellationToken);

    public Task<CodexTurnStartResult> StartTurnAsync(CodexTurnStartRequest request, CancellationToken cancellationToken = default) =>
        GetConnectedClient().StartTurnAsync(request, cancellationToken);

    public Task CancelTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default) =>
        GetConnectedClient().CancelTurnAsync(threadId, turnId, cancellationToken);

    public Task<IReadOnlyList<CodexModelOption>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        GetConnectedClient().ListModelsAsync(cancellationToken);

    public Task<CodexAccountReadResult> ReadAccountAsync(
        bool refreshToken = false,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().ReadAccountAsync(refreshToken, cancellationToken);

    public Task<CodexAccountRateLimitsResult> ReadAccountRateLimitsAsync(
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().ReadAccountRateLimitsAsync(cancellationToken);

    public Task<CodexExecutionPolicyConfig> ReadExecutionPolicyConfigAsync(
        string? cwd = null,
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().ReadExecutionPolicyConfigAsync(cwd, cancellationToken);

    public Task<CodexExecutionPolicyRequirements> ReadExecutionPolicyRequirementsAsync(
        CancellationToken cancellationToken = default) =>
        GetConnectedClient().ReadExecutionPolicyRequirementsAsync(cancellationToken);

    public async Task RespondToServerRequestAsync(
        CodexServerRequest request,
        CodexServerRequestResponse response,
        CancellationToken cancellationToken = default)
    {
        CodexAppServerClient requestClient;
        lock (requestGate)
        {
            if (!requestClients.TryGetValue(request.RequestId, out requestClient!) ||
                !ReferenceEquals(requestClient, client))
            {
                throw new InvalidOperationException("The approval request belongs to a stale app-server connection.");
            }
        }

        await requestClient.RespondToServerRequestAsync(request, response, cancellationToken).ConfigureAwait(false);
        lock (requestGate)
        {
            requestClients.Remove(request.RequestId);
        }
    }

    public void FlushNotifications() => notificationBatcher.Flush();

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            notificationBatcher.Flush();
            await DisposeClientAsync().ConfigureAwait(false);
            notificationBatcher.Dispose();
            SetState(AppServerSessionState.Disposed);
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
        }
    }

    private CodexAppServerClient GetConnectedClient() =>
        client?.IsHealthy == true
            ? client
            : throw new InvalidOperationException("The Codex app-server session is not connected.");

    private void OnNotificationReceived(object? sender, AppServerNotification notification) =>
        notificationBatcher.Enqueue(notification);

    private void OnServerRequestReceived(object? sender, CodexServerRequest request)
    {
        if (sender is not CodexAppServerClient requestClient)
        {
            return;
        }

        lock (requestGate)
        {
            requestClients[request.RequestId] = requestClient;
        }

        try
        {
            ServerRequestReceived?.Invoke(this, request);
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Error,
                "approval_request_dispatch_failed",
                "An approval request could not be routed to the application.",
                exception: ex);
            _ = RespondWithDispatchFailureAsync(request);
        }
    }

    private void OnConnectionFailed(object? sender, AppServerConnectionFailedEventArgs args)
    {
        notificationBatcher.Flush();
        recoveryStartedTimestamp = Stopwatch.GetTimestamp();
        SetState(AppServerSessionState.Reconnecting);
        ConnectionFailed?.Invoke(this, args);
    }

    private async Task DisposeClientAsync()
    {
        if (client is null)
        {
            return;
        }

        lock (requestGate)
        {
            requestClients.Clear();
        }

        client.NotificationReceived -= OnNotificationReceived;
        client.ServerRequestReceived -= OnServerRequestReceived;
        client.ConnectionFailed -= OnConnectionFailed;
        await client.DisposeAsync().ConfigureAwait(false);
        client = null;
    }

    private async Task RespondWithDispatchFailureAsync(CodexServerRequest request)
    {
        try
        {
            var response = request.Payload switch
            {
                CodexCommandApprovalRequest => CodexServerRequestResponse.Command(CodexApprovalDecision.Decline),
                CodexFileChangeApprovalRequest => CodexServerRequestResponse.FileChange(CodexApprovalDecision.Decline),
                CodexPermissionApprovalRequest => CodexServerRequestResponse.Permissions(new System.Text.Json.Nodes.JsonObject()),
                _ => null
            };
            if (response is not null && client?.IsHealthy == true)
            {
                await client.RespondToServerRequestAsync(request, response).ConfigureAwait(false);
                lock (requestGate)
                {
                    requestClients.Remove(request.RequestId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "approval_request_fail_closed_failed",
                "The application could not decline an unroutable approval request.",
                exception: ex);
        }
    }

    private void SetState(AppServerSessionState value)
    {
        if (state == value)
        {
            return;
        }

        var previous = state;
        state = value;
        StateChanged?.Invoke(this, new AppServerSessionStateChangedEventArgs(value, previous));
    }
}
