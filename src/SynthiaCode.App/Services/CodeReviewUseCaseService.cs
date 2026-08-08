using SynthiaCode.Application.Conversations;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Harnesses.Codex;

namespace SynthiaCode.App.Services;

/// <summary>
/// Owns the conversation-state transition for a first-class Codex review turn.
/// Repository discovery and target selection remain presentation orchestration concerns.
/// </summary>
public sealed class CodeReviewUseCaseService(
    ICodexReviewFeature reviewFeature,
    IConversationWorkspace conversations)
{
    public async Task<CodeReviewExecutionResult> StartAsync(
        CodeReviewExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ThreadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(request));
        }
        ArgumentNullException.ThrowIfNull(request.Target);

        conversations.BeginTurn(
            request.ThreadId,
            request.Target.DisplayLabel,
            operation: ConversationOperationKind.CodeReview);
        try
        {
            var started = await reviewFeature.StartReviewAsync(
                new CodexReviewStartRequest(
                    request.ThreadId,
                    request.Target,
                    CodexReviewDelivery.Inline),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(started.ReviewThreadId, request.ThreadId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Codex returned a detached review thread for an inline review request.");
            }

            var bound = conversations.BindPendingTurn(request.ThreadId, started.TurnId);
            conversations.RegisterTurn(request.ThreadId, started.TurnId);
            if (bound.Status == CodexTurnStatus.Running)
            {
                conversations.RegisterTurnStarted(request.ThreadId, started.TurnId, bound.Status);
            }
            else
            {
                conversations.RegisterTurnFinished(request.ThreadId);
            }

            var result = new CodeReviewExecutionResult(
                request.ThreadId,
                started.TurnId,
                bound.Status,
                conversations.GetSnapshot(request.ThreadId));
            return result;
        }
        catch (Exception ex)
        {
            conversations.FailPendingTurn(request.ThreadId, ex.Message);
            conversations.RegisterTurnFinished(request.ThreadId);
            throw;
        }
    }
}

public sealed record CodeReviewExecutionRequest(
    string ThreadId,
    CodexReviewTarget Target);

public sealed record CodeReviewExecutionResult(
    string ThreadId,
    string TurnId,
    CodexTurnStatus Status,
    ConversationWorkspaceSnapshot Snapshot);
