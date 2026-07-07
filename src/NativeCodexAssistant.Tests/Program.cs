using NativeCodexAssistant.Core.Auth;
using NativeCodexAssistant.Core.Codex;
using NativeCodexAssistant.Core.Logging;
using NativeCodexAssistant.Core.Settings;
using NativeCodexAssistant.Infrastructure.Auth;
using NativeCodexAssistant.Infrastructure.Logging;
using NativeCodexAssistant.Infrastructure.Projects;
using NativeCodexAssistant.Infrastructure.Settings;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("recent projects are deduped and capped", TestRecentProjectsAsync),
    ("settings round trip to json", TestSettingsRoundTripAsync),
    ("auth detection reports file cache without reading token", TestAuthDetectionAsync)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}");
        Console.Error.WriteLine(ex);
    }
}

if (failures > 0)
{
    return 1;
}

Console.WriteLine($"All {tests.Count} tests passed.");
return 0;

static Task TestRecentProjectsAsync()
{
    using var temp = TempWorkspace.Create();
    var settings = new AppSettings();
    var service = new RecentProjectService();

    for (var i = 0; i < 12; i++)
    {
        var path = temp.CreateDirectory($"Project{i}");
        service.AddRecentProject(settings, path);
    }

    AssertEqual(10, settings.RecentProjects.Count, "recent project cap");

    var duplicate = settings.RecentProjects[4].Path;
    service.AddRecentProject(settings, duplicate);

    AssertEqual(10, settings.RecentProjects.Count, "dedupe preserves cap");
    AssertEqual(duplicate, settings.RecentProjects[0].Path, "duplicate moves to top");
    AssertEqual(1, settings.RecentProjects.Count(project => string.Equals(project.Path, duplicate, StringComparison.OrdinalIgnoreCase)), "duplicate count");

    return Task.CompletedTask;
}

static async Task TestSettingsRoundTripAsync()
{
    using var temp = TempWorkspace.Create();
    var logger = new FileAppLogger(temp.Root);
    var store = new JsonSettingsStore(temp.Root, logger);
    var settings = new AppSettings
    {
        Theme = "Dark",
        PreferredCodexPath = @"C:\Tools\codex.exe"
    };
    settings.RecentProjects.Add(new(temp.CreateDirectory("Repo"), "Repo", DateTimeOffset.UtcNow));

    await store.SaveAsync(settings);
    var loaded = await store.LoadAsync();

    AssertEqual("Dark", loaded.Theme, "theme");
    AssertEqual(settings.PreferredCodexPath, loaded.PreferredCodexPath, "preferred codex path");
    AssertEqual(1, loaded.RecentProjects.Count, "recent project count");
    AssertTrue(File.Exists(store.SettingsPath), "settings file exists");
}

static async Task TestAuthDetectionAsync()
{
    using var temp = TempWorkspace.Create();
    var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    Environment.SetEnvironmentVariable("CODEX_HOME", temp.Root);

    try
    {
        var logger = new TestLogger();
        var service = new CodexAuthService(logger);
        var installation = new CodexInstallation(true, @"C:\Tools\codex.exe", "codex test", "Codex test", "Test installation");

        var missing = await service.GetAuthenticationStateAsync(installation);
        AssertEqual(AuthReadiness.Unknown, missing.Readiness, "missing auth readiness");

        File.WriteAllText(Path.Combine(temp.Root, "auth.json"), "{\"access_token\":\"do-not-read\"}");
        var detected = await service.GetAuthenticationStateAsync(installation);

        AssertEqual(AuthReadiness.LikelySignedIn, detected.Readiness, "detected auth readiness");
        AssertTrue(!detected.Detail.Contains("do-not-read", StringComparison.Ordinal), "token is not surfaced");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
    }
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{label}: expected true.");
    }
}

internal sealed class TempWorkspace : IDisposable
{
    private TempWorkspace(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public static TempWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "NativeCodexAssistant.Tests", Guid.NewGuid().ToString("N"));
        return new TempWorkspace(root);
    }

    public string CreateDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class TestLogger : IAppLogger
{
    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? properties = null,
        Exception? exception = null)
    {
    }
}
