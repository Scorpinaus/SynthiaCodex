using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Infrastructure.Settings;

public sealed class CoalescingSettingsStore : ISettingsStore
{
    private readonly object syncRoot = new();
    private readonly ISettingsStore inner;
    private readonly IAppLogger logger;
    private readonly TimeSpan coalescingWindow;
    private AppSettings? pendingSnapshot;
    private List<TaskCompletionSource> pendingCompletions = [];
    private bool workerRunning;

    public CoalescingSettingsStore(
        ISettingsStore inner,
        IAppLogger logger,
        TimeSpan? coalescingWindow = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.coalescingWindow = coalescingWindow ?? TimeSpan.FromMilliseconds(75);
        if (this.coalescingWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(coalescingWindow));
        }
    }

    public string SettingsPath => inner.SettingsPath;

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        inner.LoadAsync(cancellationToken);

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = AppSettingsSnapshot.Create(settings);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startWorker = false;

        lock (syncRoot)
        {
            pendingSnapshot = snapshot;
            pendingCompletions.Add(completion);
            if (!workerRunning)
            {
                workerRunning = true;
                startWorker = true;
            }
        }

        if (startWorker)
        {
            _ = RunWorkerAsync();
        }

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await completion.Task.ConfigureAwait(false);
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            if (coalescingWindow > TimeSpan.Zero)
            {
                await Task.Delay(coalescingWindow).ConfigureAwait(false);
            }

            AppSettings snapshot;
            List<TaskCompletionSource> completions;
            lock (syncRoot)
            {
                snapshot = pendingSnapshot ?? throw new InvalidOperationException("A settings snapshot was not queued.");
                pendingSnapshot = null;
                completions = pendingCompletions;
                pendingCompletions = [];
            }

            try
            {
                await inner.SaveAsync(snapshot).ConfigureAwait(false);
                foreach (var completion in completions)
                {
                    completion.TrySetResult();
                }

                logger.Log(
                    AppLogLevel.Information,
                    "settings_save_batch_completed",
                    "Settings save requests were coalesced into one physical write.",
                    new Dictionary<string, string?>
                    {
                        ["requestCount"] = completions.Count.ToString(),
                        ["coalescedCount"] = Math.Max(0, completions.Count - 1).ToString()
                    });
            }
            catch (Exception ex)
            {
                foreach (var completion in completions)
                {
                    completion.TrySetException(ex);
                }
            }

            lock (syncRoot)
            {
                if (pendingSnapshot is null)
                {
                    workerRunning = false;
                    return;
                }
            }
        }
    }
}
