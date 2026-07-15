using System.Text;
using System.Text.Json.Nodes;
using NativeCodexAssistant.Core.Codex.AppServer;

namespace NativeCodexAssistant.Infrastructure.Codex;

public sealed class AppServerNotificationBatcher : IDisposable
{
    private const string AgentDeltaMethod = "item/agentMessage/delta";

    private readonly object syncRoot = new();
    private readonly Action<AppServerNotification> emit;
    private readonly TimeSpan interval;
    private readonly Timer timer;
    private PendingDelta? pending;
    private bool disposed;
    private long receivedCount;
    private long emittedCount;
    private long receivedDeltaCount;

    public AppServerNotificationBatcher(Action<AppServerNotification> emit, TimeSpan? interval = null)
    {
        this.emit = emit ?? throw new ArgumentNullException(nameof(emit));
        this.interval = interval ?? TimeSpan.FromMilliseconds(50);
        if (this.interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        timer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public AppServerNotificationBatchMetrics Metrics
    {
        get
        {
            lock (syncRoot)
            {
                return new AppServerNotificationBatchMetrics(receivedCount, emittedCount, receivedDeltaCount);
            }
        }
    }

    public void Enqueue(AppServerNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            receivedCount++;

            var delta = ReadString(notification.Params, "delta") ?? ReadString(notification.Params, "text");
            if (notification.Method == AgentDeltaMethod && !string.IsNullOrEmpty(delta))
            {
                receivedDeltaCount++;
                var key = CreateGroupKey(notification.Params);
                if (pending is not null && string.Equals(pending.Key, key, StringComparison.Ordinal))
                {
                    pending.Text.Append(delta);
                    return;
                }

                FlushLocked();
                pending = new PendingDelta(key, notification, new StringBuilder(delta));
                timer.Change(interval, Timeout.InfiniteTimeSpan);
                return;
            }

            FlushLocked();
            EmitLocked(notification);
        }
    }

    public void Flush()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            FlushLocked();
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            FlushLocked();
            disposed = true;
            timer.Dispose();
        }
    }

    private void FlushLocked()
    {
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (pending is null)
        {
            return;
        }

        var parameters = (JsonObject)pending.Notification.Params.DeepClone();
        parameters["delta"] = pending.Text.ToString();
        EmitLocked(new AppServerNotification(AgentDeltaMethod, parameters));
        pending = null;
    }

    private void EmitLocked(AppServerNotification notification)
    {
        emittedCount++;
        emit(notification);
    }

    private static string CreateGroupKey(JsonObject parameters) => string.Join(
        '\u001f',
        ReadString(parameters, "threadId") ?? string.Empty,
        ReadString(parameters, "turnId") ?? string.Empty,
        ReadString(parameters, "itemId") ?? string.Empty);

    private static string? ReadString(JsonObject parameters, string propertyName) =>
        parameters[propertyName] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed record PendingDelta(
        string Key,
        AppServerNotification Notification,
        StringBuilder Text);
}

public sealed record AppServerNotificationBatchMetrics(
    long ReceivedCount,
    long EmittedCount,
    long ReceivedDeltaCount);
