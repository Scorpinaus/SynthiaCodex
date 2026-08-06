using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using SynthiaCode.App;
using Xunit;

public sealed class ReleaseMetadataTests
{
    [Fact]
    public void App_assembly_exposes_the_current_release_identity()
    {
        var expectedReleaseVersion = ReadProjectVersion();
        var expectedAssemblyVersion = ToAssemblyVersion(expectedReleaseVersion);
        var assembly = typeof(AppServices).Assembly;
        var name = assembly.GetName();
        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.Equal(expectedAssemblyVersion, name.Version);
        Assert.Equal(expectedAssemblyVersion.ToString(), fileVersion.FileVersion);
        Assert.StartsWith(expectedReleaseVersion, informationalVersion, StringComparison.Ordinal);
        Assert.Equal("SynthiaCode", fileVersion.ProductName);
        Assert.Equal("SynthiaCode contributors", fileVersion.CompanyName);
        Assert.Equal("Copyright © 2026 SynthiaCode contributors", fileVersion.LegalCopyright);
    }

    [Fact]
    public void Architecture_snapshot_describes_the_current_release_boundary()
    {
        var expectedReleaseVersion = ReadProjectVersion();
        var repositoryRoot = FindRepositoryRoot();
        var architecture = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "current-architecture.md"));

        Assert.Contains("# SynthiaCode: Current Architecture", architecture, StringComparison.Ordinal);
        Assert.Contains("**Recorded:** 2 August 2026", architecture, StringComparison.Ordinal);
        Assert.Contains($"**Release:** {expectedReleaseVersion}", architecture, StringComparison.Ordinal);
        Assert.Contains("**Phase:** Modern WPF redesign through Phase 21", architecture, StringComparison.Ordinal);
        Assert.Contains("xUnit-discovered behavioral and integration-style test suite", architecture, StringComparison.Ordinal);
        Assert.Contains("262 passing tests", architecture, StringComparison.Ordinal);
        Assert.DoesNotContain("Console-based behavioral and integration-style assertion runner", architecture, StringComparison.Ordinal);
        Assert.DoesNotContain("Phase 6A completes", architecture, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_publishes_the_current_release_and_architecture_links()
    {
        var expectedReleaseVersion = ReadProjectVersion();
        var repositoryRoot = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));

        Assert.Contains($"**Current release:** {expectedReleaseVersion}", readme, StringComparison.Ordinal);
        Assert.Contains("[current architecture](docs/current-architecture.md)", readme, StringComparison.Ordinal);
        Assert.Contains("[feature-parity audit](feature_parity.md)", readme, StringComparison.Ordinal);
    }

    private static string ReadProjectVersion()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "SynthiaCode.App", "SynthiaCode.App.csproj");
        var versions = XDocument.Load(projectPath)
            .Descendants("Version")
            .Select(element => element.Value.Trim())
            .Where(version => version.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return versions.Length == 1
            ? versions[0]
            : throw new InvalidDataException($"Expected one app Version in '{projectPath}', but found {versions.Length}.");
    }

    private static Version ToAssemblyVersion(string releaseVersion)
    {
        var suffixIndex = releaseVersion.IndexOfAny(new[] { '-', '+' });
        var versionCore = suffixIndex >= 0 ? releaseVersion[..suffixIndex] : releaseVersion;

        if (!Version.TryParse(versionCore, out var parsed) || parsed.Build < 0)
        {
            throw new InvalidDataException($"Release version '{releaseVersion}' must contain major, minor, and patch components.");
        }

        return new Version(parsed.Major, parsed.Minor, parsed.Build, 0);
    }

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

        throw new DirectoryNotFoundException("Could not locate the SynthiaCode repository root.");
    }
}
