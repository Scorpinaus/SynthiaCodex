using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Application.Harnesses;

public abstract class HarnessSessionBase : IHarnessSession
{
    private readonly Dictionary<Type, IHarnessFeature> features = [];
    private HarnessSessionState state = HarnessSessionState.Idle;
    private bool disposed;

    protected HarnessSessionBase(HarnessDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public event EventHandler<HarnessEvent>? EventReceived;

    public event EventHandler<HarnessSessionState>? StateChanged;

    public HarnessDescriptor Descriptor { get; }

    public HarnessSessionState State => state;

    public HarnessCapabilities Capabilities => Descriptor.Capabilities;

    public bool TryGetFeature(Type featureType, out IHarnessFeature? feature)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        ThrowIfDisposed();

        if (!typeof(IHarnessFeature).IsAssignableFrom(featureType))
        {
            feature = null;
            return false;
        }

        return features.TryGetValue(featureType, out feature);
    }

    protected void RegisterFeature<TFeature>(TFeature feature)
        where TFeature : class, IHarnessFeature
    {
        ArgumentNullException.ThrowIfNull(feature);
        ThrowIfDisposed();
        if (!features.TryAdd(typeof(TFeature), feature))
        {
            throw new InvalidOperationException($"Feature {typeof(TFeature).Name} is already registered.");
        }
    }

    protected void Publish(HarnessEvent harnessEvent)
    {
        ArgumentNullException.ThrowIfNull(harnessEvent);
        ThrowIfDisposed();
        if (harnessEvent.HarnessId != Descriptor.Id)
        {
            throw new InvalidOperationException(
                $"Event harness '{harnessEvent.HarnessId}' does not match session harness '{Descriptor.Id}'.");
        }

        EventReceived?.Invoke(this, harnessEvent);
    }

    protected void SetState(HarnessSessionState nextState)
    {
        ThrowIfDisposed();
        if (state == nextState)
        {
            return;
        }

        state = nextState;
        StateChanged?.Invoke(this, nextState);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await DisposeAsyncCore().ConfigureAwait(false);
        state = HarnessSessionState.Disposed;
        disposed = true;
        StateChanged?.Invoke(this, state);
        GC.SuppressFinalize(this);
    }

    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
