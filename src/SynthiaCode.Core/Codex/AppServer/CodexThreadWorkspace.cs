using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Core.Codex.AppServer;

public sealed class CodexThreadWorkspace
{
    private readonly Dictionary<string, CodexThreadService> threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> turnThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<(HarnessId HarnessId, string RemoteId), string> remoteThreads = [];

    public IReadOnlyCollection<string> ThreadIds => threads.Keys;

    public CodexThreadService Restore(ProjectThreadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RegisterConversation(state.ThreadId, state.GetConversationAddress());
        var service = GetOrCreate(state.ThreadId);
        service.Restore(
            state.ThreadId,
            state.FinalResponse,
            state.TimelineItems,
            state.RawEvents,
            state.Preview,
            state.ConversationTurns,
            state.ContextTokensUsed,
            state.ContextWindowTokens,
            state.ContextCompactionCount);
        return service;
    }

    public CodexThreadService GetOrCreate(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread ID is required.", nameof(threadId));
        }

        if (!threads.TryGetValue(threadId, out var service))
        {
            service = new CodexThreadService();
            service.Restore(threadId, null, null, null);
            threads.Add(threadId, service);
        }

        return service;
    }

    public CodexThreadService GetRequired(string threadId) =>
        threads.TryGetValue(threadId, out var service)
            ? service
            : throw new KeyNotFoundException($"Thread '{threadId}' is not loaded.");

    public bool Remove(string threadId)
    {
        var removed = threads.Remove(threadId);
        foreach (var address in remoteThreads
            .Where(pair => string.Equals(pair.Value, threadId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList())
        {
            remoteThreads.Remove(address);
        }
        foreach (var turnId in turnThreads
            .Where(pair => string.Equals(pair.Value, threadId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList())
        {
            turnThreads.Remove(turnId);
        }

        return removed;
    }

    public void RegisterTurn(string threadId, string turnId)
    {
        turnThreads[turnId] = threadId;
    }

    public void RegisterConversation(string threadId, ConversationAddress address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(address);
        if (!string.IsNullOrWhiteSpace(address.RemoteId))
        {
            remoteThreads[(address.HarnessId, address.RemoteId)] = threadId;
        }
    }

    public string? ApplyEvent(HarnessEvent harnessEvent)
    {
        ArgumentNullException.ThrowIfNull(harnessEvent);
        string? threadId = null;
        if (!string.IsNullOrWhiteSpace(harnessEvent.RemoteConversationId))
        {
            remoteThreads.TryGetValue(
                (harnessEvent.HarnessId, harnessEvent.RemoteConversationId),
                out threadId);
            if (string.IsNullOrWhiteSpace(threadId) && threads.ContainsKey(harnessEvent.RemoteConversationId))
            {
                threadId = harnessEvent.RemoteConversationId;
                remoteThreads[(harnessEvent.HarnessId, harnessEvent.RemoteConversationId)] = threadId;
            }
        }
        if (string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(harnessEvent.RemoteTurnId))
        {
            turnThreads.TryGetValue(harnessEvent.RemoteTurnId, out threadId);
        }
        if (string.IsNullOrWhiteSpace(threadId) && harnessEvent is ConversationStartedEvent started)
        {
            threadId = started.RemoteConversationId!;
            remoteThreads[(started.HarnessId, started.RemoteConversationId!)] = threadId;
        }
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(harnessEvent.RemoteTurnId))
        {
            RegisterTurn(threadId, harnessEvent.RemoteTurnId);
        }
        GetOrCreate(threadId).ApplyEvent(harnessEvent);
        return threadId;
    }

    public string? ApplyNotification(CodexAppServerNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var threadId = notification.ThreadId;
        var turnId = notification.TurnId;
        if (string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(turnId))
        {
            turnThreads.TryGetValue(turnId, out threadId);
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(turnId))
        {
            RegisterTurn(threadId, turnId);
        }

        GetOrCreate(threadId).ApplyNotification(notification);
        return threadId;
    }

}
