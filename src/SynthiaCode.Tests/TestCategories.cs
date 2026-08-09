using Xunit;

internal static class TestCategories
{
    public const string Unit = "Unit";
    public const string ProtocolContract = "ProtocolContract";
    public const string InfrastructureIntegration = "InfrastructureIntegration";
    public const string Wpf = "Wpf";

    public const string NativeCollection = "Native and infrastructure tests";
    public const string WpfCollection = "WPF tests";
}

[CollectionDefinition(TestCategories.NativeCollection, DisableParallelization = true)]
public sealed class NativeTestCollection;

[CollectionDefinition(TestCategories.WpfCollection, DisableParallelization = true)]
public sealed class WpfTestCollection;
