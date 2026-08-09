using System.Xml.Linq;
using System.Text.RegularExpressions;
using Xunit;

[Trait("Category", TestCategories.Unit)]
public sealed class Phase5TestArchitectureTests
{
    [Fact]
    public void Legacy_behavior_cases_have_normal_fact_discovery()
    {
        var root = FindRepositoryRoot();
        var testDirectory = Path.Combine(root, "src", "SynthiaCode.Tests");
        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(testDirectory, "*.cs")
                .Where(path => !path.EndsWith(
                    "FollowUpQueueUseCaseServiceTests.cs",
                    StringComparison.Ordinal))
                .Where(path => !path.EndsWith(
                    "Phase5TestArchitectureTests.cs",
                    StringComparison.Ordinal))
                .Select(File.ReadAllText));

        Assert.Equal(307, Count(source, "[Fact(DisplayName"));
        Assert.DoesNotContain("MemberData", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IReadOnlyList<(string Name, Func<Task> Run)> All",
            source,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            testDirectory,
            "BehavioralTestSuite.cs")));
    }

    [Fact]
    public void Unicode_echo_is_a_dedicated_fixture_executable()
    {
        var root = FindRepositoryRoot();
        var testsProject = XDocument.Load(Path.Combine(
            root,
            "src",
            "SynthiaCode.Tests",
            "SynthiaCode.Tests.csproj"));
        var fixtureProject = XDocument.Load(Path.Combine(
            root,
            "src",
            "SynthiaCode.UnicodeEchoFixture",
            "SynthiaCode.UnicodeEchoFixture.csproj"));

        Assert.DoesNotContain(
            testsProject.Descendants("OutputType"),
            element => element.Value == "Exe");
        Assert.Empty(testsProject.Descendants("StartupObject"));
        Assert.Contains(
            testsProject.Descendants("ProjectReference"),
            element => element.Attribute("Include")?.Value.EndsWith(
                "SynthiaCode.UnicodeEchoFixture.csproj",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            fixtureProject.Descendants("OutputType"),
            element => element.Value == "Exe");
    }

    [Fact]
    public void Test_categories_define_parallel_and_serial_boundaries()
    {
        var root = FindRepositoryRoot();
        var categories = File.ReadAllText(Path.Combine(
            root,
            "src",
            "SynthiaCode.Tests",
            "TestCategories.cs"));
        var runner = File.ReadAllText(Path.Combine(
            root,
            "src",
            "SynthiaCode.Tests",
            "xunit.runner.json"));

        Assert.Contains("Unit", categories, StringComparison.Ordinal);
        Assert.Contains("ProtocolContract", categories, StringComparison.Ordinal);
        Assert.Contains("InfrastructureIntegration", categories, StringComparison.Ordinal);
        Assert.Contains("Wpf", categories, StringComparison.Ordinal);
        Assert.Equal(2, Count(categories, "DisableParallelization = true"));
        Assert.Contains(
            "\"parallelizeTestCollections\": true",
            runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Timing_tests_use_a_manual_clock_and_message_probe()
    {
        var root = FindRepositoryRoot();
        var primitives = File.ReadAllText(Path.Combine(
            root,
            "src",
            "SynthiaCode.Tests",
            "TestPrimitives.cs"));
        var settingsStore = File.ReadAllText(Path.Combine(
            root,
            "src",
            "SynthiaCode.Infrastructure",
            "Settings",
            "CoalescingSettingsStore.cs"));
        var batcher = File.ReadAllText(Path.Combine(
            root,
            "src",
            "SynthiaCode.Infrastructure",
            "Codex",
            "AppServerNotificationBatcher.cs"));

        Assert.Contains("class ManualTimeProvider", primitives, StringComparison.Ordinal);
        Assert.Contains("class MessageProbe", primitives, StringComparison.Ordinal);
        Assert.Contains("TimeProvider? timeProvider", settingsStore, StringComparison.Ordinal);
        Assert.Contains("TimeProvider? timeProvider", batcher, StringComparison.Ordinal);
    }

    [Fact]
    public void Pure_tests_use_the_non_Windows_test_project()
    {
        var root = FindRepositoryRoot();
        var unitProjectPath = Path.Combine(
            root,
            "src",
            "SynthiaCode.Tests.Unit",
            "SynthiaCode.Tests.Unit.csproj");
        var windowsProjectPath = Path.Combine(
            root,
            "src",
            "SynthiaCode.Tests",
            "SynthiaCode.Tests.csproj");
        var unitProject = XDocument.Load(unitProjectPath);
        var windowsProject = XDocument.Load(windowsProjectPath);

        Assert.Equal("net10.0", unitProject.Descendants("TargetFramework").Single().Value);
        Assert.Empty(unitProject.Descendants("UseWPF"));
        Assert.DoesNotContain(
            unitProject.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")?.Value.EndsWith(
                "SynthiaCode.App.csproj",
                StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            unitProject.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")?.Value.Contains(
                "SynthiaCode.Infrastructure",
                StringComparison.Ordinal) == true);

        var removedFromWindows = windowsProject
            .Descendants("Compile")
            .Select(element => element.Attribute("Remove")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testDirectory = Path.Combine(root, "src", "SynthiaCode.Tests");
        var pureSources = Directory
            .EnumerateFiles(testDirectory, "*.cs")
            .Where(path => File.ReadAllText(path).Contains(
                "TestCategories.Unit",
                StringComparison.Ordinal));

        Assert.All(
            pureSources,
            path => Assert.Contains(Path.GetFileName(path), removedFromWindows));
    }

    [Fact]
    public void Test_sources_do_not_define_state_polling_helpers()
    {
        var root = FindRepositoryRoot();
        var testDirectory = Path.Combine(root, "src", "SynthiaCode.Tests");
        var forbiddenNames = new[]
        {
            "Wait" + "UntilAsync",
            "Poll" + "UntilAsync",
            "Wait" + "ForConditionAsync",
            "Spin" + "Until"
        };
        var delayedLoop = new Regex(
            @"while\s*\([^)]*\)\s*\{(?:(?!\n\s*\}).)*Task\s*\.\s*Delay",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        foreach (var path in Directory.EnumerateFiles(testDirectory, "*.cs"))
        {
            var source = File.ReadAllText(path);
            foreach (var forbiddenName in forbiddenNames)
            {
                Assert.DoesNotContain(forbiddenName, source, StringComparison.Ordinal);
            }

            Assert.DoesNotMatch(delayedLoop, source);
        }
    }

    [Fact]
    public void Test_classes_do_not_mix_parallel_and_serial_ownership()
    {
        var root = FindRepositoryRoot();
        var testDirectory = Path.Combine(root, "src", "SynthiaCode.Tests");
        var legacyCases = File.ReadAllText(Path.Combine(
            testDirectory,
            "LegacyRuntimeTests.cs"));

        Assert.DoesNotContain("[Fact", legacyCases, StringComparison.Ordinal);
        Assert.DoesNotContain("[Trait", legacyCases, StringComparison.Ordinal);
        Assert.DoesNotContain("[Collection", legacyCases, StringComparison.Ordinal);
        Assert.Equal(20, Count(File.ReadAllText(Path.Combine(
            testDirectory,
            "LegacyProtocolContractTests.cs")), "[Fact"));
        Assert.Equal(21, Count(File.ReadAllText(Path.Combine(
            testDirectory,
            "LegacyInfrastructureIntegrationTests.cs")), "[Fact"));
        Assert.Equal(34, Count(File.ReadAllText(Path.Combine(
            testDirectory,
            "LegacyWpfTests.cs")), "[Fact"));

        foreach (var path in Directory
                     .EnumerateFiles(testDirectory, "*Tests.cs")
                     .Where(path => !path.EndsWith(
                         "Phase5TestArchitectureTests.cs",
                         StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(path);
            var ownsWpfCollection = source.Contains(
                "Collection(TestCategories.WpfCollection)",
                StringComparison.Ordinal);
            var ownsNativeCollection = source.Contains(
                "Collection(TestCategories.NativeCollection)",
                StringComparison.Ordinal);
            var isWpf = source.Contains(
                "Trait(\"Category\", TestCategories.Wpf)",
                StringComparison.Ordinal);

            Assert.False(
                ownsWpfCollection && ownsNativeCollection,
                $"{Path.GetFileName(path)} owns both serial collections.");
            Assert.Equal(isWpf, ownsWpfCollection);
        }
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SynthiaCode.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SynthiaCode repository root.");
    }
}
