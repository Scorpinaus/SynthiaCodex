using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;

namespace SynthiaCode.Harnesses.Codex;

/// <summary>
/// Narrow compatibility surface over the existing Codex app-server coordinator.
/// Protocol types stop at this adapter boundary and are translated before they are
/// exposed through the neutral harness contracts.
/// </summary>
public interface ICodexHarnessBackend : ICodexNotificationFeature
{
    Task EnsureConnectedAsync(
        CodexInstallation installation,
        CancellationToken cancellationToken = default);

    Task<CodexThreadStartResult> StartThreadAsync(
        CodexThreadStartOptions options,
        CancellationToken cancellationToken = default);

    Task<CodexThreadResumeResult> ResumeThreadAsync(
        CodexThreadResumeRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexThreadReadResult> ReadThreadAsync(
        CodexThreadReadRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexThreadForkResult> ForkThreadAsync(
        CodexThreadForkRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexThreadRollbackResult> RollbackThreadAsync(
        CodexThreadRollbackRequest request,
        CancellationToken cancellationToken = default);

    Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken = default);

    Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken = default);

    Task SetThreadNameAsync(
        string threadId,
        string name,
        CancellationToken cancellationToken = default);

    Task<CodexTurnStartResult> StartTurnAsync(
        CodexTurnStartRequest request,
        CancellationToken cancellationToken = default);

    Task<CodexTurnSteerResult> SteerTurnAsync(
        CodexTurnSteerRequest request,
        CancellationToken cancellationToken = default);

    Task CancelTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodexModelOption>> ListModelsAsync(
        CancellationToken cancellationToken = default);
}
