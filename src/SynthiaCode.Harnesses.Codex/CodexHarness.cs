using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Harnesses.Codex;

public sealed class CodexHarness(
    ICodexDiscoveryService discovery,
    ICodexHarnessBackend backend) : IAgentHarness
{
    public const string ExecutablePathSetting = "executablePath";

    private static readonly HarnessCapabilities SupportedCapabilities = new(
        HarnessCapability.CreateConversation |
        HarnessCapability.ResumeConversation |
        HarnessCapability.ReadConversation |
        HarnessCapability.RenameConversation |
        HarnessCapability.ArchiveConversation |
        HarnessCapability.ForkConversation |
        HarnessCapability.RollbackConversation |
        HarnessCapability.StartTurn |
        HarnessCapability.CancelTurn |
        HarnessCapability.SteerTurn |
        HarnessCapability.Streaming |
        HarnessCapability.ImageInput |
        HarnessCapability.WorkspaceReferences |
        HarnessCapability.ModelCatalog |
        HarnessCapability.ModelOptions |
        HarnessCapability.Approvals |
        HarnessCapability.PermissionProfiles |
        HarnessCapability.Skills |
        HarnessCapability.Account |
        HarnessCapability.RateLimits |
        HarnessCapability.Configuration |
        HarnessCapability.ContextUsage |
        HarnessCapability.GeneratedImages |
        HarnessCapability.AgentCollaboration);

    private CodexInstallation? installation;

    public HarnessDescriptor Descriptor { get; } = new(
        HarnessId.Codex,
        "Codex",
        "OpenAI Codex app-server",
        SupportedCapabilities);

    public async Task<HarnessAvailability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        installation = await discovery.DetectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return installation.IsFound
            ? new HarnessAvailability(
                HarnessAvailabilityState.Available,
                "Codex is available.",
                installation.ExecutablePath)
            : new HarnessAvailability(
                HarnessAvailabilityState.Unavailable,
                "Codex is unavailable.",
                installation.Detail);
    }

    public async Task<IHarnessSession> ConnectAsync(
        HarnessConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var preferredPath = options.Settings is not null &&
            options.Settings.TryGetValue(ExecutablePathSetting, out var configuredPath)
                ? configuredPath
                : null;
        installation = await discovery.DetectAsync(preferredPath, cancellationToken).ConfigureAwait(false);
        if (!installation.IsFound)
        {
            throw new InvalidOperationException(installation.Detail);
        }

        await backend.EnsureConnectedAsync(installation, cancellationToken).ConfigureAwait(false);
        return new CodexHarnessSession(Descriptor, backend);
    }
}

public sealed class CodexHarnessSession : HarnessSessionBase,
    IConversationCreationFeature,
    IConversationResumeFeature,
    IConversationReadFeature,
    IConversationNamingFeature,
    IConversationArchiveFeature,
    IConversationForkFeature,
    IConversationRollbackFeature,
    ITurnExecutionFeature,
    ITurnCancellationFeature,
    ITurnSteeringFeature,
    IModelCatalogFeature
{
    private readonly ICodexHarnessBackend backend;

    public CodexHarnessSession(
        HarnessDescriptor descriptor,
        ICodexHarnessBackend backend) : base(descriptor)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        RegisterFeature<IConversationCreationFeature>(this);
        RegisterFeature<IConversationResumeFeature>(this);
        RegisterFeature<IConversationReadFeature>(this);
        RegisterFeature<IConversationNamingFeature>(this);
        RegisterFeature<IConversationArchiveFeature>(this);
        RegisterFeature<IConversationForkFeature>(this);
        RegisterFeature<IConversationRollbackFeature>(this);
        RegisterFeature<ITurnExecutionFeature>(this);
        RegisterFeature<ITurnCancellationFeature>(this);
        RegisterFeature<ITurnSteeringFeature>(this);
        RegisterFeature<IModelCatalogFeature>(this);
        backend.NotificationReceived += OnNotificationReceived;
        SetState(HarnessSessionState.Connected);
    }

    public async Task<StartConversationResult> StartConversationAsync(
        StartConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.StartThreadAsync(command.ToCodex(), cancellationToken).ConfigureAwait(false);
        return new StartConversationResult(new ConversationAddress(
            command.LocalConversationId,
            HarnessId.Codex,
            result.ThreadId));
    }

    public async Task<ResumeConversationResult> ResumeConversationAsync(
        ResumeConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.ResumeThreadAsync(command.ToCodex(), cancellationToken).ConfigureAwait(false);
        return new ResumeConversationResult(
            command.Address with { RemoteId = result.ThreadId },
            (result.Turns ?? []).Select(CodexHarnessMappings.ToHarness).ToArray());
    }

    public async Task<ReadConversationResult> ReadConversationAsync(
        ReadConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.ReadThreadAsync(
            new CodexThreadReadRequest(command.Address.RequireRemoteId(), command.IncludeTurns),
            cancellationToken).ConfigureAwait(false);
        return new ReadConversationResult(
            command.Address with { RemoteId = result.ThreadId },
            result.Turns.Select(CodexHarnessMappings.ToHarness).ToArray());
    }

    public Task SetConversationNameAsync(
        ConversationAddress address,
        string name,
        CancellationToken cancellationToken = default) =>
        backend.SetThreadNameAsync(address.RequireRemoteId(), name, cancellationToken);

    public Task SetConversationArchivedAsync(
        ConversationAddress address,
        bool archived,
        CancellationToken cancellationToken = default) =>
        archived
            ? backend.ArchiveThreadAsync(address.RequireRemoteId(), cancellationToken)
            : backend.UnarchiveThreadAsync(address.RequireRemoteId(), cancellationToken);

    public async Task<ForkConversationResult> ForkConversationAsync(
        ForkConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.ForkThreadAsync(command.ToCodex(), cancellationToken).ConfigureAwait(false);
        return new ForkConversationResult(new ConversationAddress(
            command.LocalConversationId,
            HarnessId.Codex,
            result.ThreadId));
    }

    public async Task<RollbackConversationResult> RollbackConversationAsync(
        RollbackConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.RollbackThreadAsync(
            new CodexThreadRollbackRequest(command.Address.RequireRemoteId(), command.TurnCount),
            cancellationToken).ConfigureAwait(false);
        return new RollbackConversationResult(
            command.Address with { RemoteId = result.ThreadId },
            result.Turns.Select(CodexHarnessMappings.ToHarness).ToArray());
    }

    public async Task<StartTurnResult> StartTurnAsync(
        StartTurnCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.StartTurnAsync(command.ToCodex(), cancellationToken).ConfigureAwait(false);
        return new StartTurnResult(result.TurnId);
    }

    public Task CancelTurnAsync(
        ConversationAddress address,
        string remoteTurnId,
        CancellationToken cancellationToken = default) =>
        backend.CancelTurnAsync(address.RequireRemoteId(), remoteTurnId, cancellationToken);

    public async Task<SteerTurnResult> SteerTurnAsync(
        SteerTurnCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await backend.SteerTurnAsync(command.ToCodex(), cancellationToken).ConfigureAwait(false);
        return new SteerTurnResult(result.TurnId);
    }

    public async Task<IReadOnlyList<HarnessModelDescriptor>> ListModelsAsync(
        CancellationToken cancellationToken = default) =>
        (await backend.ListModelsAsync(cancellationToken).ConfigureAwait(false))
            .Select(CodexHarnessMappings.ToHarness)
            .ToArray();

    protected override ValueTask DisposeAsyncCore()
    {
        backend.NotificationReceived -= OnNotificationReceived;
        return ValueTask.CompletedTask;
    }

    private void OnNotificationReceived(object? sender, CodexAppServerNotification notification)
    {
        foreach (var harnessEvent in CodexHarnessEventTranslator.Translate(notification))
        {
            Publish(harnessEvent);
        }
    }
}
