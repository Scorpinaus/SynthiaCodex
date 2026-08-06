using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Application.Harnesses;

public sealed record HarnessConnectionOptions(
    string? WorkspacePath = null,
    IReadOnlyDictionary<string, string>? Settings = null);

public interface IAgentHarness
{
    HarnessDescriptor Descriptor { get; }

    Task<HarnessAvailability> ProbeAsync(CancellationToken cancellationToken = default);

    Task<IHarnessSession> ConnectAsync(
        HarnessConnectionOptions options,
        CancellationToken cancellationToken = default);
}

public interface IHarnessFeature
{
}

public interface IHarnessSession : IAsyncDisposable
{
    event EventHandler<HarnessEvent>? EventReceived;

    event EventHandler<HarnessSessionState>? StateChanged;

    HarnessDescriptor Descriptor { get; }

    HarnessSessionState State { get; }

    HarnessCapabilities Capabilities { get; }

    bool TryGetFeature(Type featureType, out IHarnessFeature? feature);
}

public static class HarnessSessionExtensions
{
    public static bool TryGetFeature<TFeature>(
        this IHarnessSession session,
        out TFeature? feature)
        where TFeature : class, IHarnessFeature
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.TryGetFeature(typeof(TFeature), out var value) && value is TFeature typed)
        {
            feature = typed;
            return true;
        }

        feature = null;
        return false;
    }

    public static TFeature RequireFeature<TFeature>(
        this IHarnessSession session,
        HarnessCapability capability)
        where TFeature : class, IHarnessFeature
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Capabilities.Supports(capability))
        {
            throw new HarnessCapabilityException(session.Descriptor, capability);
        }

        return session.TryGetFeature<TFeature>(out var feature)
            ? feature!
            : throw new InvalidOperationException(
                $"Harness '{session.Descriptor.Id}' advertises '{capability}' but does not provide {typeof(TFeature).Name}.");
    }
}

public sealed class HarnessCapabilityException(
    HarnessDescriptor harness,
    HarnessCapability capability)
    : InvalidOperationException($"Harness '{harness.DisplayName}' does not support {capability}.")
{
    public HarnessDescriptor Harness { get; } = harness;

    public HarnessCapability Capability { get; } = capability;
}

public interface IConversationCreationFeature : IHarnessFeature
{
    Task<StartConversationResult> StartConversationAsync(
        StartConversationCommand command,
        CancellationToken cancellationToken = default);
}

public interface IConversationResumeFeature : IHarnessFeature
{
    Task<ResumeConversationResult> ResumeConversationAsync(
        ResumeConversationCommand command,
        CancellationToken cancellationToken = default);
}

public interface IConversationReadFeature : IHarnessFeature
{
    Task<ReadConversationResult> ReadConversationAsync(
        ReadConversationCommand command,
        CancellationToken cancellationToken = default);
}

public interface IConversationNamingFeature : IHarnessFeature
{
    Task SetConversationNameAsync(
        ConversationAddress address,
        string name,
        CancellationToken cancellationToken = default);
}

public interface IConversationArchiveFeature : IHarnessFeature
{
    Task SetConversationArchivedAsync(
        ConversationAddress address,
        bool archived,
        CancellationToken cancellationToken = default);
}

public interface IConversationForkFeature : IHarnessFeature
{
    Task<ForkConversationResult> ForkConversationAsync(
        ForkConversationCommand command,
        CancellationToken cancellationToken = default);
}

public interface IConversationRollbackFeature : IHarnessFeature
{
    Task<RollbackConversationResult> RollbackConversationAsync(
        RollbackConversationCommand command,
        CancellationToken cancellationToken = default);
}

public interface ITurnExecutionFeature : IHarnessFeature
{
    Task<StartTurnResult> StartTurnAsync(
        StartTurnCommand command,
        CancellationToken cancellationToken = default);
}

public interface ITurnCancellationFeature : IHarnessFeature
{
    Task CancelTurnAsync(
        ConversationAddress address,
        string remoteTurnId,
        CancellationToken cancellationToken = default);
}

public interface ITurnSteeringFeature : IHarnessFeature
{
    Task<SteerTurnResult> SteerTurnAsync(
        SteerTurnCommand command,
        CancellationToken cancellationToken = default);
}

public interface IModelCatalogFeature : IHarnessFeature
{
    Task<IReadOnlyList<HarnessModelDescriptor>> ListModelsAsync(
        CancellationToken cancellationToken = default);
}

public interface IApprovalFeature : IHarnessFeature
{
    event EventHandler<ApprovalRequest>? ApprovalRequested;

    Task RespondAsync(
        ApprovalResponse response,
        CancellationToken cancellationToken = default);
}
