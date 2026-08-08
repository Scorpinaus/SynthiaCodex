using System.Xml.Linq;
using Xunit;

public sealed class ArchitectureBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedProjectReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["SynthiaCode.Core"] = [],
            ["SynthiaCode.Application"] = ["SynthiaCode.Core"],
            ["SynthiaCode.Infrastructure"] = ["SynthiaCode.Core"],
            ["SynthiaCode.Harnesses.Codex"] = ["SynthiaCode.Application", "SynthiaCode.Core"],
            ["SynthiaCode.Harnesses.InMemory"] = ["SynthiaCode.Application", "SynthiaCode.Core"],
            ["SynthiaCode.App"] =
            [
                "SynthiaCode.Application",
                "SynthiaCode.Core",
                "SynthiaCode.Harnesses.Codex",
                "SynthiaCode.Infrastructure",
            ],
        };

    private static readonly IReadOnlyDictionary<string, string> OwnedNamespaceRoots =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SynthiaCode.Core"] = "SynthiaCode.Core",
            ["SynthiaCode.Application"] = "SynthiaCode.Application",
            ["SynthiaCode.Infrastructure"] = "SynthiaCode.Infrastructure",
            ["SynthiaCode.Harnesses.Codex"] = "SynthiaCode.Harnesses.Codex",
            ["SynthiaCode.Harnesses.InMemory"] = "SynthiaCode.Harnesses.InMemory",
            ["SynthiaCode.App"] = "SynthiaCode.App",
        };

    private static readonly IReadOnlyDictionary<string, string[]> ForbiddenNamespaceDependencies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["SynthiaCode.Core"] =
            [
                "SynthiaCode.App",
                "SynthiaCode.Application",
                "SynthiaCode.Harnesses",
                "SynthiaCode.Infrastructure",
            ],
            ["SynthiaCode.Application"] =
            [
                "SynthiaCode.App",
                "SynthiaCode.Harnesses",
                "SynthiaCode.Infrastructure",
            ],
            ["SynthiaCode.Infrastructure"] =
            [
                "SynthiaCode.App",
                "SynthiaCode.Application",
                "SynthiaCode.Harnesses",
            ],
            ["SynthiaCode.Harnesses.Codex"] =
            [
                "SynthiaCode.App",
                "SynthiaCode.Harnesses.InMemory",
                "SynthiaCode.Infrastructure",
            ],
            ["SynthiaCode.Harnesses.InMemory"] =
            [
                "SynthiaCode.App",
                "SynthiaCode.Harnesses.Codex",
                "SynthiaCode.Infrastructure",
            ],
        };

    [Fact]
    public void Production_project_references_match_the_phase_0_dependency_graph()
    {
        var projectFiles = FindProductionProjectFiles();
        Assert.Equal(
            ExpectedProjectReferences.Keys.Order(StringComparer.Ordinal),
            projectFiles.Keys.Order(StringComparer.Ordinal));

        foreach (var (projectName, expectedReferences) in ExpectedProjectReferences)
        {
            var document = XDocument.Load(projectFiles[projectName]);
            var actualReferences = document
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actualReferences);
        }
    }

    [Fact]
    public void Production_source_namespaces_are_owned_by_their_projects()
    {
        var violations = new List<string>();

        foreach (var (projectName, namespaceRoot) in OwnedNamespaceRoots)
        {
            var projectDirectory = Path.GetDirectoryName(FindProductionProjectFiles()[projectName])!;
            foreach (var sourceFile in EnumerateSourceFiles(projectDirectory))
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(sourceFile))
                {
                    lineNumber++;
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("namespace ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var declaredNamespace = trimmed["namespace ".Length..]
                        .Trim()
                        .TrimEnd('{', ';')
                        .Trim();
                    if (!declaredNamespace.Equals(namespaceRoot, StringComparison.Ordinal)
                        && !declaredNamespace.StartsWith($"{namespaceRoot}.", StringComparison.Ordinal))
                    {
                        violations.Add(FormatViolation(sourceFile, lineNumber, declaredNamespace));
                    }
                }
            }
        }

        AssertNoViolations("Namespace ownership violations", violations);
    }

    [Fact]
    public void Lower_layers_do_not_import_forbidden_upper_layer_namespaces()
    {
        var violations = new List<string>();
        var projectFiles = FindProductionProjectFiles();

        foreach (var (projectName, forbiddenNamespaces) in ForbiddenNamespaceDependencies)
        {
            var projectDirectory = Path.GetDirectoryName(projectFiles[projectName])!;
            foreach (var sourceFile in EnumerateSourceFiles(projectDirectory))
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(sourceFile))
                {
                    lineNumber++;
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("using ", StringComparison.Ordinal)
                        && !trimmed.StartsWith("global using ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var importedNamespace = trimmed.StartsWith("global using ", StringComparison.Ordinal)
                        ? trimmed["global using ".Length..]
                        : trimmed["using ".Length..];
                    var aliasSeparator = importedNamespace.IndexOf('=');
                    if (aliasSeparator >= 0)
                    {
                        importedNamespace = importedNamespace[(aliasSeparator + 1)..];
                    }

                    importedNamespace = importedNamespace.Trim();
                    if (importedNamespace.StartsWith("static ", StringComparison.Ordinal))
                    {
                        importedNamespace = importedNamespace["static ".Length..].TrimStart();
                    }

                    importedNamespace = importedNamespace.TrimEnd(';');
                    var forbiddenNamespace = forbiddenNamespaces.FirstOrDefault(candidate =>
                        importedNamespace.Equals(candidate, StringComparison.Ordinal)
                        || importedNamespace.StartsWith($"{candidate}.", StringComparison.Ordinal));
                    if (forbiddenNamespace is not null)
                    {
                        violations.Add(FormatViolation(sourceFile, lineNumber, forbiddenNamespace));
                    }
                }
            }
        }

        AssertNoViolations("Forbidden namespace dependencies", violations);
    }

    [Fact]
    public void Windows_desktop_dependencies_remain_at_the_app_boundary()
    {
        var projectFiles = FindProductionProjectFiles();
        var wpfProjects = new List<string>();
        var windowsTargetedProjects = new List<string>();

        foreach (var (projectName, projectFile) in projectFiles)
        {
            var document = XDocument.Load(projectFile);
            if (document.Descendants("UseWPF").Any(element =>
                    string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase)))
            {
                wpfProjects.Add(projectName);
            }

            if (document.Descendants("TargetFramework").Any(element =>
                    element.Value.Contains("-windows", StringComparison.OrdinalIgnoreCase)))
            {
                windowsTargetedProjects.Add(projectName);
            }
        }

        Assert.Equal(["SynthiaCode.App"], wpfProjects.Order(StringComparer.Ordinal));
        Assert.Equal(["SynthiaCode.App"], windowsTargetedProjects.Order(StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, string> FindProductionProjectFiles()
    {
        var sourceDirectory = Path.Combine(FindRepositoryRoot(), "src");
        return Directory
            .EnumerateFiles(sourceDirectory, "SynthiaCode.*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains("SynthiaCode.Tests", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path)!, StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string projectDirectory)
    {
        return Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path, projectDirectory));
    }

    private static bool IsBuildArtifact(string path, string projectDirectory)
    {
        var relativePath = Path.GetRelativePath(projectDirectory, path);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatViolation(string sourceFile, int lineNumber, string dependency)
    {
        var relativePath = Path.GetRelativePath(FindRepositoryRoot(), sourceFile);
        return $"{relativePath}:{lineNumber} -> {dependency}";
    }

    private static void AssertNoViolations(string heading, IReadOnlyCollection<string> violations)
    {
        Assert.True(
            violations.Count == 0,
            $"{heading}:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
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
