using System.Collections.Concurrent;
using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Application.Harnesses;

public interface IHarnessRuntimeCoordinator : IAsyncDisposable
{
    event EventHandler<HarnessEvent>? EventReceived;

    event EventHandler<HarnessSessionState>? SessionStateChanged;

    IHarnessRegistry Registry { get; }

    bool TryGetSession(HarnessId harnessId, out IHarnessSession? session);

    Task<IHarnessSession> GetOrConnectAsync(
        HarnessId harnessId,
        HarnessConnectionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class HarnessRuntimeCoordinator(IHarnessRegistry registry) : IHarnessRuntimeCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<HarnessId, IHarnessSession> sessions = [];
    private bool disposed;

    public IHarnessRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));

    public event EventHandler<HarnessEvent>? EventReceived;

    public event EventHandler<HarnessSessionState>? SessionStateChanged;

    public bool TryGetSession(HarnessId harnessId, out IHarnessSession? session)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return sessions.TryGetValue(harnessId, out session);
    }

    public async Task<IHarnessSession> GetOrConnectAsync(
        HarnessId harnessId,
        HarnessConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.TryGetValue(harnessId, out var existing))
            {
                return existing;
            }

            var harness = Registry.GetRequired(harnessId);
            var availability = await harness.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!availability.IsAvailable)
            {
                throw new InvalidOperationException(
                    $"Harness '{harness.Descriptor.DisplayName}' is unavailable: {availability.Detail ?? availability.Summary}");
            }

            var session = await harness.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            if (session.Descriptor.Id != harnessId)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Harness '{harnessId}' returned a session for '{session.Descriptor.Id}'.");
            }

            if (!sessions.TryAdd(harnessId, session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return sessions[harnessId];
            }
            session.EventReceived += OnSessionEventReceived;
            session.StateChanged += OnSessionStateChanged;
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var session in sessions.Values)
            {
                session.EventReceived -= OnSessionEventReceived;
                session.StateChanged -= OnSessionStateChanged;
                await session.DisposeAsync().ConfigureAwait(false);
            }
            sessions.Clear();
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private void OnSessionEventReceived(object? sender, HarnessEvent harnessEvent) =>
        EventReceived?.Invoke(sender, harnessEvent);

    private void OnSessionStateChanged(object? sender, HarnessSessionState state) =>
        SessionStateChanged?.Invoke(sender, state);
}
