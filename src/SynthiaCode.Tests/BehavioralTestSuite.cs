using Xunit;

[CollectionDefinition(nameof(BehavioralSuiteCollection), DisableParallelization = true)]
public sealed class BehavioralSuiteCollection
{
}

[Collection(nameof(BehavioralSuiteCollection))]
public sealed class BehavioralTestSuite
{
    public static IEnumerable<object[]> Cases()
    {
        var filter = Environment.GetEnvironmentVariable("SYNTHIACODE_TEST_FILTER");
        return LegacyBehavioralTests.All
            .Where(test => string.IsNullOrWhiteSpace(filter) || test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(test => new object[] { new BehavioralTestCase(test.Name, test.Run) });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Runs_behavioral_case(BehavioralTestCase testCase) => Task.Run(testCase.Run);
}

public sealed class BehavioralTestCase(string name, Func<Task> run)
{
    public string Name { get; } = name;

    public Task Run() => run();

    public override string ToString() => Name;
}
