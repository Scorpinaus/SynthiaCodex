using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.App.Services;

/// <summary>
/// Executes harness turns and owns the corresponding conversation state transitions.
/// Presentation callers provide harness-neutral commands and project returned snapshots.
/// </summary>
public sealed class TurnExecutionUseCaseService
{
    private readonly IHarnessOperations harnesses;
    private readonly ConversationWorkflowController conversations;
    private readonly ThreadLifecycleUseCaseService threadLifecycle;
    private readonly ThreadStatePersistenceUseCaseService persistence;

    public TurnExecutionUseCaseService(
        IHarnessOperations harnesses,
        ConversationWorkflowController conversations,
        ThreadLifecycleUseCaseService threadLifecycle,
        ThreadStatePersistenceUseCaseService persistence)
    {
        this.harnesses = harnesses;
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
            var started = await harnesses.StartTurnAsync(
                request.ConnectionOptions,
                request.StartCommand,
                cancellationToken).ConfigureAwait(false);
            var bound = conversations.BindPendingTurn(request.ThreadId, started.RemoteTurnId);
            conversations.RegisterTurn(request.ThreadId, started.RemoteTurnId);
            RegisterStatus(request.ThreadId, started.RemoteTurnId, bound.Status);
            request.TurnStarted?.Invoke(new TurnExecutionResult(
                request.ThreadId,
                started.RemoteTurnId,
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
                        request.Settings,
                        request.ThreadId,
                        request.AutomaticTitle,
                        request.ConnectionOptions,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    titleError = ex.Message;
                }
            }

            return new TurnExecutionResult(
                request.ThreadId,
                started.RemoteTurnId,
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
            var rollback = await harnesses.RollbackConversationAsync(
                request.ConnectionOptions,
                new RollbackConversationCommand(request.Address, request.RollbackCount),
                cancellationToken).ConfigureAwait(false);
            if (rollback.Address.LocalId != request.Address.LocalId ||
                rollback.Address.HarnessId != request.Address.HarnessId)
            {
                throw new InvalidOperationException("The harness returned a different conversation after editing the prompt.");
            }

            conversations.SupersedeTurnsFrom(request.ThreadId, request.SourceTurnId);
            conversations.ReconcileHistory(request.ThreadId, rollback.Turns.Select(ToLegacySnapshot));
            conversations.BeginTurn(request.ThreadId, request.Prompt, request.Attachments);
            committed = true;
            UpdatePreview(request.Settings, request.ThreadId, request.Prompt, request.Attachments);

            var started = await harnesses.StartTurnAsync(
                request.ConnectionOptions,
                request.StartCommand,
                cancellationToken).ConfigureAwait(false);
            var bound = conversations.BindPendingTurn(request.ThreadId, started.RemoteTurnId);
            conversations.RegisterTurn(request.ThreadId, started.RemoteTurnId);
            RegisterStatus(request.ThreadId, started.RemoteTurnId, bound.Status);
            return new TurnEditExecutionResult(
                true,
                request.ThreadId,
                started.RemoteTurnId,
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
        HarnessConnectionOptions connectionOptions,
        SteerTurnCommand command,
        string guidance,
        CancellationToken cancellationToken = default)
    {
        await harnesses.SteerTurnAsync(connectionOptions, command, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            conversations.AddGuidance(threadId, guidance);
        }
        return conversations.GetSnapshot(threadId);
    }

    public Task CancelAsync(
        HarnessConnectionOptions connectionOptions,
        ConversationAddress address,
        string remoteTurnId,
        CancellationToken cancellationToken = default) =>
        harnesses.CancelTurnAsync(connectionOptions, address, remoteTurnId, cancellationToken);

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

    private static CodexConversationTurnSnapshot ToLegacySnapshot(ConversationTurnSnapshot source) => new()
    {
        TurnId = source.RemoteTurnId ?? string.Empty,
        UserPrompt = source.UserPrompt,
        AssistantResponse = source.AssistantResponse,
        Status = source.Status switch
        {
            ConversationTurnStatus.Idle => CodexTurnStatus.Idle,
            ConversationTurnStatus.Running => CodexTurnStatus.Running,
            ConversationTurnStatus.Completed => CodexTurnStatus.Completed,
            ConversationTurnStatus.Failed => CodexTurnStatus.Failed,
            ConversationTurnStatus.Cancelled => CodexTurnStatus.Cancelled,
            _ => CodexTurnStatus.Failed
        },
        StartedAt = source.StartedAt ?? DateTimeOffset.UtcNow,
        CompletedAt = source.CompletedAt,
        IsSuperseded = source.IsSuperseded,
        Activity = [.. source.Activity.Select(item => new CodexTimelineItem(
            item.Kind == ActivityKind.Error ? CodexTimelineItemKind.Error : CodexTimelineItemKind.Raw,
            item.Title,
            item.Detail,
            "harness/activity",
            item.Timestamp)
        {
            ItemId = item.Id,
            ActivityKey = item.Id
        })],
        UserAttachments = [.. source.UserAttachments.Select(attachment => attachment.Clone())],
        GeneratedImagePaths = [.. source.GeneratedImagePaths],
        Diff = source.Diff
    };
}

public sealed record TurnExecutionRequest(
    AppSettings Settings,
    string ThreadId,
    ConversationAddress Address,
    string Prompt,
    IReadOnlyList<AttachmentReference> Attachments,
    HarnessConnectionOptions ConnectionOptions,
    StartTurnCommand StartCommand,
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
    ConversationAddress Address,
    string SourceTurnId,
    int RollbackCount,
    string Prompt,
    IReadOnlyList<AttachmentReference> Attachments,
    HarnessConnectionOptions ConnectionOptions,
    StartTurnCommand StartCommand);

public sealed record TurnEditExecutionResult(
    bool StateCommitted,
    string ThreadId,
    string? TurnId,
    CodexTurnStatus? Status,
    ConversationWorkspaceSnapshot Snapshot,
    Exception? Error);
