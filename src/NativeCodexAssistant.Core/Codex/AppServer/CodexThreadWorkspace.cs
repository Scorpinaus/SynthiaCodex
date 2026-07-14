using System.Text.Json.Nodes;
using NativeCodexAssistant.Core.Settings;

namespace NativeCodexAssistant.Core.Codex.AppServer;

public sealed class CodexThreadWorkspace
{
    private readonly Dictionary<string, CodexThreadService> threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> turnThreads = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ThreadIds => threads.Keys;

    public CodexThreadService Restore(ProjectThreadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var service = GetOrCreate(state.ThreadId);
        service.Restore(state.ThreadId, state.FinalResponse, state.TimelineItems, state.RawEvents);
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

    public void RegisterTurn(string threadId, string turnId)
    {
        turnThreads[turnId] = threadId;
    }

    public string? ApplyNotification(AppServerNotification notification)
    {
        var threadId = ReadString(notification.Params, "threadId")
            ?? ReadString(notification.Params, "thread.id")
            ?? ReadString(notification.Params, "turn.threadId");
        var turnId = ReadString(notification.Params, "turnId") ?? ReadString(notification.Params, "turn.id");
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

    private static string? ReadString(JsonObject obj, string path)
    {
        JsonNode? current = obj;
        foreach (var segment in path.Split('.'))
        {
            current = current is JsonObject currentObject ? currentObject[segment] : null;
        }

        return current is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
