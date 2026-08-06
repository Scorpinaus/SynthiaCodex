using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Application.Harnesses;

public interface IHarnessOperations
{
    Task<IHarnessSession> GetSessionAsync(
        HarnessId harnessId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default);

    Task<StartConversationResult> StartConversationAsync(
        HarnessId harnessId,
        HarnessConnectionOptions connectionOptions,
        StartConversationCommand command,
        CancellationToken cancellationToken = default);

    Task<ResumeConversationResult> ResumeConversationAsync(
        HarnessConnectionOptions connectionOptions,
        ResumeConversationCommand command,
        CancellationToken cancellationToken = default);

    Task<ReadConversationResult> ReadConversationAsync(
        HarnessConnectionOptions connectionOptions,
        ReadConversationCommand command,
        CancellationToken cancellationToken = default);

    Task SetConversationNameAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        string name,
        CancellationToken cancellationToken = default);

    Task SetConversationArchivedAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        bool archived,
        CancellationToken cancellationToken = default);

    Task<ForkConversationResult> ForkConversationAsync(
        HarnessConnectionOptions connectionOptions,
        ForkConversationCommand command,
        CancellationToken cancellationToken = default);

    Task<RollbackConversationResult> RollbackConversationAsync(
        HarnessConnectionOptions connectionOptions,
        RollbackConversationCommand command,
        CancellationToken cancellationToken = default);

    Task<StartTurnResult> StartTurnAsync(
        HarnessConnectionOptions connectionOptions,
        StartTurnCommand command,
        CancellationToken cancellationToken = default);

    Task CancelTurnAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        string remoteTurnId,
        CancellationToken cancellationToken = default);

    Task<SteerTurnResult> SteerTurnAsync(
        HarnessConnectionOptions connectionOptions,
        SteerTurnCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HarnessModelDescriptor>> ListModelsAsync(
        HarnessId harnessId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default);
}

public sealed class HarnessOperations(IHarnessRuntimeCoordinator runtime) : IHarnessOperations
{
    public Task<IHarnessSession> GetSessionAsync(
        HarnessId harnessId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default) =>
        runtime.GetOrConnectAsync(harnessId, connectionOptions, cancellationToken);

    public async Task<StartConversationResult> StartConversationAsync(
        HarnessId harnessId,
        HarnessConnectionOptions connectionOptions,
        StartConversationCommand command,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(harnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IConversationCreationFeature>(HarnessCapability.CreateConversation)
            .StartConversationAsync(command, cancellationToken).ConfigureAwait(false);

    public async Task<ResumeConversationResult> ResumeConversationAsync(
        HarnessConnectionOptions connectionOptions,
        ResumeConversationCommand command,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(command.Address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IConversationResumeFeature>(HarnessCapability.ResumeConversation)
            .ResumeConversationAsync(command, cancellationToken).ConfigureAwait(false);

    public async Task<ReadConversationResult> ReadConversationAsync(
        HarnessConnectionOptions connectionOptions,
        ReadConversationCommand command,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(command.Address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IConversationReadFeature>(HarnessCapability.ReadConversation)
            .ReadConversationAsync(command, cancellationToken).ConfigureAwait(false);

    public async Task SetConversationNameAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        string name,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IConversationNamingFeature>(HarnessCapability.RenameConversation)
            .SetConversationNameAsync(address, name, cancellationToken).ConfigureAwait(false);

    public async Task SetConversationArchivedAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        bool archived,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IConversationArchiveFeature>(HarnessCapability.ArchiveConversation)
            .SetConversationArchivedAsync(address, archived, cancellationToken).ConfigureAwait(false);

    public async Task<ForkConversationResult> ForkConversationAsync(
        HarnessConnectionOptions connectionOptions,
        ForkConversationCommand command,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(command.Source.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IConversationForkFeature>(HarnessCapability.ForkConversation)
            .ForkConversationAsync(command, cancellationToken).ConfigureAwait(false);

    public async Task<RollbackConversationResult> RollbackConversationAsync(
        HarnessConnectionOptions connectionOptions,
        RollbackConversationCommand command,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(command.Address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IConversationRollbackFeature>(HarnessCapability.RollbackConversation)
            .RollbackConversationAsync(command, cancellationToken).ConfigureAwait(false);

    public async Task<StartTurnResult> StartTurnAsync(
        HarnessConnectionOptions connectionOptions,
        StartTurnCommand command,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(command.Address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<ITurnExecutionFeature>(HarnessCapability.StartTurn)
            .StartTurnAsync(command, cancellationToken).ConfigureAwait(false);

    public async Task CancelTurnAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        string remoteTurnId,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<ITurnCancellationFeature>(HarnessCapability.CancelTurn)
            .CancelTurnAsync(address, remoteTurnId, cancellationToken).ConfigureAwait(false);

    public async Task<SteerTurnResult> SteerTurnAsync(
        HarnessConnectionOptions connectionOptions,
        SteerTurnCommand command,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(command.Address.HarnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<ITurnSteeringFeature>(HarnessCapability.SteerTurn)
            .SteerTurnAsync(command, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HarnessModelDescriptor>> ListModelsAsync(
        HarnessId harnessId,
        HarnessConnectionOptions connectionOptions,
        CancellationToken cancellationToken = default) =>
        await (await GetSessionAsync(harnessId, connectionOptions, cancellationToken).ConfigureAwait(false))
            .RequireFeature<IModelCatalogFeature>(HarnessCapability.ModelCatalog)
            .ListModelsAsync(cancellationToken).ConfigureAwait(false);
}
