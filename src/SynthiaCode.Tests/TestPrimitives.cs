using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;

internal interface ITestSignal
{
    event EventHandler? Signaled;
}

internal static class StateProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static Task WaitForAsync(
        Func<bool> condition,
        params object?[] additionalSources) =>
        WaitForAsync(condition, "expected state", additionalSources);

    public static async Task WaitForAsync(
        Func<bool> condition,
        string label,
        params object?[] additionalSources)
    {
        ArgumentNullException.ThrowIfNull(condition);

        if (TryEvaluate(condition))
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscriptions = Subscribe(
            DiscoverSources(condition.Target, additionalSources),
            () =>
            {
                if (TryEvaluate(condition))
                {
                    completion.TrySetResult();
                }
            });

        if (TryEvaluate(condition))
        {
            return;
        }

        try
        {
            await completion.Task.WaitAsync(DefaultTimeout);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Timed out waiting for {label}.", ex);
        }
    }

    private static bool TryEvaluate(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Collection was modified", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static IReadOnlyList<object> DiscoverSources(
        object? closure,
        IReadOnlyCollection<object?> additionalSources)
    {
        var sources = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        AddSource(closure, inspectFields: true);
        foreach (var source in additionalSources)
        {
            AddSource(source, inspectFields: false);
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "The state probe needs an event, message, collection, command, or property-change source.");
        }

        return sources;

        void AddSource(object? candidate, bool inspectFields)
        {
            if (candidate is null || candidate is string || !seen.Add(candidate))
            {
                return;
            }

            if (candidate is ITestSignal or INotifyPropertyChanged or INotifyCollectionChanged or ICommand)
            {
                sources.Add(candidate);
            }

            var type = candidate.GetType();
            if (inspectFields)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    AddSource(field.GetValue(candidate), inspectFields: false);
                }
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    !IsEventSourceType(property.PropertyType))
                {
                    continue;
                }

                AddSource(property.GetValue(candidate), inspectFields: false);
            }
        }
    }

    private static bool IsEventSourceType(Type type) =>
        typeof(ITestSignal).IsAssignableFrom(type) ||
        typeof(INotifyPropertyChanged).IsAssignableFrom(type) ||
        typeof(INotifyCollectionChanged).IsAssignableFrom(type) ||
        typeof(ICommand).IsAssignableFrom(type);

    private static IDisposable Subscribe(
        IEnumerable<object> sources,
        Action observe)
    {
        var subscriptions = new List<Action>();

        foreach (var source in sources)
        {
            if (source is ITestSignal testSignal)
            {
                EventHandler handler = (_, _) => observe();
                testSignal.Signaled += handler;
                subscriptions.Add(() => testSignal.Signaled -= handler);
            }

            if (source is INotifyPropertyChanged propertyChanged)
            {
                PropertyChangedEventHandler handler = (_, _) => observe();
                propertyChanged.PropertyChanged += handler;
                subscriptions.Add(() => propertyChanged.PropertyChanged -= handler);
            }

            if (source is INotifyCollectionChanged collectionChanged)
            {
                NotifyCollectionChangedEventHandler handler = (_, _) => observe();
                collectionChanged.CollectionChanged += handler;
                subscriptions.Add(() => collectionChanged.CollectionChanged -= handler);
            }

            if (source is ICommand command)
            {
                EventHandler handler = (_, _) => observe();
                command.CanExecuteChanged += handler;
                subscriptions.Add(() => command.CanExecuteChanged -= handler);
            }
        }

        return new SubscriptionSet(subscriptions);
    }

    private sealed class SubscriptionSet(IReadOnlyList<Action> unsubscribe) : IDisposable
    {
        public void Dispose()
        {
            foreach (var action in unsubscribe)
            {
                action();
            }
        }
    }
}

internal sealed class MessageProbe<T> : ITestSignal
{
    private readonly object syncRoot = new();
    private readonly List<T> messages = [];
    private readonly List<Waiter> waiters = [];

    public event EventHandler? Signaled;

    public void Publish(T message)
    {
        List<TaskCompletionSource<T>> completions;
        lock (syncRoot)
        {
            messages.Add(message);
            completions = waiters
                .Where(waiter => waiter.Predicate(message))
                .Select(waiter => waiter.Completion)
                .ToList();
            waiters.RemoveAll(waiter => completions.Contains(waiter.Completion));
        }

        foreach (var completion in completions)
        {
            completion.TrySetResult(message);
        }

        Signaled?.Invoke(this, EventArgs.Empty);
    }

    public async Task<T> WaitForAsync(
        Func<T, bool> predicate,
        string label = "expected message")
    {
        ArgumentNullException.ThrowIfNull(predicate);

        Waiter? waiter = null;
        lock (syncRoot)
        {
            foreach (var existing in messages)
            {
                if (predicate(existing))
                {
                    return existing;
                }
            }

            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = new Waiter(predicate, completion);
            waiters.Add(waiter);
        }

        try
        {
            return await waiter.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException ex)
        {
            lock (syncRoot)
            {
                waiters.Remove(waiter);
            }

            throw new InvalidOperationException($"Timed out waiting for {label}.", ex);
        }
    }

    private sealed record Waiter(
        Func<T, bool> Predicate,
        TaskCompletionSource<T> Completion);
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object syncRoot = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset utcNow;
    private long timestamp;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        utcNow = start ?? DateTimeOffset.UnixEpoch;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (syncRoot)
        {
            return utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (syncRoot)
        {
            return timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (syncRoot)
        {
            timers.Add(timer);
            timer.ChangeLocked(dueTime, period, timestamp);
        }

        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (syncRoot)
        {
            utcNow += elapsed;
            timestamp += elapsed.Ticks;
            foreach (var timer in timers.ToArray())
            {
                timer.CollectCallbacksLocked(timestamp, callbacks);
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    private void Change(
        ManualTimer timer,
        TimeSpan dueTime,
        TimeSpan period)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(timer.IsDisposed, timer);
            timer.ChangeLocked(dueTime, period, timestamp);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (syncRoot)
        {
            timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private long dueTimestamp = long.MaxValue;
        private long periodTicks = Timeout.InfiniteTimeSpan.Ticks;

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Change(this, dueTime, period);
            return true;
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ChangeLocked(TimeSpan dueTime, TimeSpan period, long now)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            periodTicks = period.Ticks;
            dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : checked(now + dueTime.Ticks);
        }

        public void CollectCallbacksLocked(
            long now,
            List<(TimerCallback Callback, object? State)> callbacks)
        {
            if (IsDisposed || dueTimestamp == long.MaxValue || dueTimestamp > now)
            {
                return;
            }

            callbacks.Add((callback, state));
            dueTimestamp = periodTicks > 0
                ? checked(dueTimestamp + periodTicks)
                : long.MaxValue;
        }

        private static void ValidateTimeout(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}
