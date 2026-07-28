using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.Services;

/// <summary>
/// Executes Codex turns and owns the corresponding conversation state transitions.
/// Presentation callers provide fully resolved protocol requests and project returned snapshots.
/// </summary>
public sealed class TurnExecutionUseCaseService
{
    private readonly IAppServerSessionCoordinator appServer;
    private readonly ConversationWorkflowController conversations;
    private readonly ThreadLifecycleUseCaseService threadLifecycle;
    private readonly ThreadStatePersistenceUseCaseService persistence;

    public TurnExecutionUseCaseService(
        IAppServerSessionCoordinator appServer,
        ConversationWorkflowController conversations,
        ThreadLifecycleUseCaseService threadLifecycle,
        ThreadStatePersistenceUseCaseService persistence)
    {
        this.appServer = appServer;
        this.conversations = conversations;
        this.threadLifecycle = threadLifecycle;
        this.persistence = persistence;
    }

    public async Task<TurnExecutionResult> StartAsync(
        TurnExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        UpdatePreview(request.Settings, request.ThreadId, request.Prompt, request.Attachments);
        conversations.BeginTurn(request.ThreadId, request.Prompt, request.Attachments);
        request.PendingStarted?.Invoke(conversations.GetSnapshot(request.ThreadId));

        try
        {
            var started = await appServer.StartTurnAsync(request.StartRequest, cancellationToken).ConfigureAwait(false);
            var bound = conversations.BindPendingTurn(request.ThreadId, started.TurnId);
            conversations.RegisterTurn(request.ThreadId, started.TurnId);
            RegisterStatus(request.ThreadId, started.TurnId, bound.Status);
            request.TurnStarted?.Invoke(new TurnExecutionResult(
                request.ThreadId,
                started.TurnId,
                bound.Status,
                conversations.GetSnapshot(request.ThreadId),
                false,
                null));

            var titleApplied = false;
            string? titleError = null;
            if (!string.IsNullOrWhiteSpace(request.AutomaticTitle))
            {
                try
                {
                    titleApplied = await threadLifecycle.RenameIfPlaceholderAsync(
                        request.Settings, request.ThreadId, request.AutomaticTitle, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    titleError = ex.Message;
                }
            }

            return new TurnExecutionResult(
                request.ThreadId,
                started.TurnId,
                bound.Status,
                conversations.GetSnapshot(request.ThreadId),
                titleApplied,
                titleError);
        }
        catch (Exception ex)
        {
            conversations.FailPendingTurn(request.ThreadId, ex.Message);
            conversations.RegisterTurnFinished(request.ThreadId);
            throw;
        }
    }

    public async Task<TurnEditExecutionResult> EditAsync(
        TurnEditExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var committed = false;
        try
        {
            var rollback = await appServer.RollbackThreadAsync(
                new CodexThreadRollbackRequest(request.ThreadId, request.RollbackCount), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(rollback.ThreadId, request.ThreadId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex returned a different thread after editing the prompt.");
            }

            conversations.SupersedeTurnsFrom(request.ThreadId, request.SourceTurnId);
            conversations.ReconcileHistory(request.ThreadId, rollback.Turns);
            conversations.BeginTurn(request.ThreadId, request.Prompt, request.Attachments);
            committed = true;
            UpdatePreview(request.Settings, request.ThreadId, request.Prompt, request.Attachments);

            var started = await appServer.StartTurnAsync(request.StartRequest, cancellationToken).ConfigureAwait(false);
            var bound = conversations.BindPendingTurn(request.ThreadId, started.TurnId);
            conversations.RegisterTurn(request.ThreadId, started.TurnId);
            RegisterStatus(request.ThreadId, started.TurnId, bound.Status);
            return new TurnEditExecutionResult(
                true,
                request.ThreadId,
                started.TurnId,
                bound.Status,
                conversations.GetSnapshot(request.ThreadId),
                null);
        }
        catch (Exception ex)
        {
            Exception error = ex;
            if (committed)
            {
                conversations.FailPendingTurn(request.ThreadId, ex.Message);
                try
                {
                    await persistence.SaveAsync(
                        request.Settings,
                        request.ThreadId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception persistenceError)
                {
                    error = new AggregateException(
                        "The edited turn failed and its recovery state could not be saved.",
                        ex,
                        persistenceError);
                }
            }
            conversations.RegisterTurnFinished(request.ThreadId);
            return new TurnEditExecutionResult(
                committed,
                request.ThreadId,
                null,
                null,
                conversations.GetSnapshot(request.ThreadId),
                error);
        }
    }

    public async Task<ConversationWorkspaceSnapshot> SteerAsync(
        string threadId,
        CodexTurnSteerRequest request,
        string guidance,
        CancellationToken cancellationToken = default)
    {
        await appServer.SteerTurnAsync(request, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            conversations.AddGuidance(threadId, guidance);
        }
        return conversations.GetSnapshot(threadId);
    }

    public Task CancelAsync(string threadId, string turnId, CancellationToken cancellationToken = default) =>
        appServer.CancelTurnAsync(threadId, turnId, cancellationToken);

    private void RegisterStatus(string threadId, string turnId, CodexTurnStatus status)
    {
        if (status == CodexTurnStatus.Running)
        {
            conversations.RegisterTurnStarted(threadId, turnId, status);
        }
        else
        {
            conversations.RegisterTurnFinished(threadId);
        }
    }

    private static void UpdatePreview(
        AppSettings settings,
        string threadId,
        string prompt,
        IReadOnlyCollection<AttachmentReference> attachments)
    {
        var persisted = settings.ProjectThreads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (persisted is null)
        {
            return;
        }

        persisted.Preview = string.IsNullOrWhiteSpace(prompt)
            ? $"{attachments.Count} attachment{(attachments.Count == 1 ? string.Empty : "s")}"
            : prompt;
    }
}

public sealed record TurnExecutionRequest(
    AppSettings Settings,
    string ThreadId,
    string Prompt,
    IReadOnlyList<AttachmentReference> Attachments,
    CodexTurnStartRequest StartRequest,
    string? AutomaticTitle,
    Action<ConversationWorkspaceSnapshot>? PendingStarted = null,
    Action<TurnExecutionResult>? TurnStarted = null);

public sealed record TurnExecutionResult(
    string ThreadId,
    string TurnId,
    CodexTurnStatus Status,
    ConversationWorkspaceSnapshot Snapshot,
    bool AutomaticTitleApplied,
    string? AutomaticTitleError);

public sealed record TurnEditExecutionRequest(
    AppSettings Settings,
    string ThreadId,
    string SourceTurnId,
    int RollbackCount,
    string Prompt,
    IReadOnlyList<AttachmentReference> Attachments,
    CodexTurnStartRequest StartRequest);

public sealed record TurnEditExecutionResult(
    bool StateCommitted,
    string ThreadId,
    string? TurnId,
    CodexTurnStatus? Status,
    ConversationWorkspaceSnapshot Snapshot,
    Exception? Error);
