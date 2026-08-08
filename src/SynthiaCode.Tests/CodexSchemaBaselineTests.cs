using Xunit;

public sealed class CodexSchemaBaselineTests
{
    private const string CurrentCodexCliBaseline = "0.147.0";

    [Fact]
    public void Generated_app_server_schemas_match_the_current_codex_baseline()
    {
        var repositoryRoot = FindRepositoryRoot();
        var schemaDirectory = Path.Combine(repositoryRoot, "schemas");
        var baselineNote = File.ReadAllText(Path.Combine(schemaDirectory, "README.md"));
        var clientRequest = File.ReadAllText(Path.Combine(schemaDirectory, "ClientRequest.json"));

        Assert.Contains($"Codex CLI {CurrentCodexCliBaseline}", baselineNote, StringComparison.Ordinal);
        Assert.Contains($"@openai/codex@{CurrentCodexCliBaseline}", baselineNote, StringComparison.Ordinal);
        Assert.Contains("\"thread/turns/list\"", clientRequest, StringComparison.Ordinal);
        Assert.Contains("\"thread/items/list\"", clientRequest, StringComparison.Ordinal);
        Assert.Contains("\"plugin/search\"", clientRequest, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(schemaDirectory, "v2", "ThreadTurnsListParams.json")));
        Assert.True(File.Exists(Path.Combine(schemaDirectory, "v2", "ThreadItemsListParams.json")));
        Assert.True(File.Exists(Path.Combine(schemaDirectory, "v2", "PluginSearchParams.json")));
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
