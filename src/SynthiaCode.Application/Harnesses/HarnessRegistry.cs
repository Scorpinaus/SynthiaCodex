using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Application.Harnesses;

public interface IHarnessRegistry
{
    IReadOnlyList<HarnessDescriptor> Harnesses { get; }

    bool TryGet(HarnessId harnessId, out IAgentHarness? harness);

    IAgentHarness GetRequired(HarnessId harnessId);
}

public sealed class HarnessRegistry : IHarnessRegistry
{
    private readonly IReadOnlyDictionary<HarnessId, IAgentHarness> harnesses;

    public HarnessRegistry(IEnumerable<IAgentHarness> harnesses)
    {
        ArgumentNullException.ThrowIfNull(harnesses);
        var registered = new Dictionary<HarnessId, IAgentHarness>();
        foreach (var harness in harnesses)
        {
            ArgumentNullException.ThrowIfNull(harness);
            if (!registered.TryAdd(harness.Descriptor.Id, harness))
            {
                throw new InvalidOperationException(
                    $"Harness '{harness.Descriptor.Id}' is already registered.");
            }
        }

        this.harnesses = registered;
        Harnesses = registered.Values
            .Select(harness => harness.Descriptor)
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<HarnessDescriptor> Harnesses { get; }

    public bool TryGet(HarnessId harnessId, out IAgentHarness? harness) =>
        harnesses.TryGetValue(harnessId, out harness);

    public IAgentHarness GetRequired(HarnessId harnessId) =>
        TryGet(harnessId, out var harness)
            ? harness!
            : throw new KeyNotFoundException($"Harness '{harnessId}' is not registered.");
}
