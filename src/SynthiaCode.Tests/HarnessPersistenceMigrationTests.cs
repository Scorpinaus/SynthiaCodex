using System.Text.Json;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Settings;
using Xunit;

[Trait("Category", TestCategories.InfrastructureIntegration)]
[Collection(TestCategories.NativeCollection)]
public sealed class HarnessPersistenceMigrationTests
{
    [Fact]
    public void Legacy_threads_receive_deterministic_codex_identity_once()
    {
        var settings = new AppSettings
        {
            ProjectThreads = [new PersistedProjectThread { ThreadId = "legacy-thread" }]
        };

        Assert.True(AppSettingsHarnessMigration.Apply(settings));
        var thread = Assert.Single(settings.ProjectThreads);
        var firstId = thread.ConversationId;

        Assert.Equal(AppSettingsHarnessMigration.CurrentSchemaVersion, settings.HarnessSchemaVersion);
        Assert.Equal(KnownHarnessIds.Codex, settings.DefaultHarnessId);
        Assert.Equal(KnownHarnessIds.Codex, thread.HarnessId);
        Assert.Equal("legacy-thread", thread.RemoteConversationId);
        Assert.NotEqual(Guid.Empty, firstId);
        Assert.False(AppSettingsHarnessMigration.Apply(settings));
        Assert.Equal(firstId, thread.ConversationId);
        Assert.Equal(
            AppSettingsHarnessMigration.CreateDeterministicConversationId(
                KnownHarnessIds.Codex,
                "legacy-thread"),
            firstId);
    }

    [Fact]
    public async Task Json_store_loads_and_writes_legacy_harness_identity_without_losing_thread_data()
    {
        using var temp = TempWorkspace.Create();
        var store = new JsonSettingsStore(temp.Root, new TestLogger());
        await File.WriteAllTextAsync(store.SettingsPath,
            """
            {
              "theme": "Dark",
              "projectThreads": [
                {
                  "projectPath": "C:\\repo",
                  "threadId": "legacy-thread",
                  "title": "Legacy title",
                  "preview": "Legacy preview",
                  "finalResponse": "Legacy response"
                }
              ]
            }
            """);

        var loaded = await store.LoadAsync();
        var thread = Assert.Single(loaded.ProjectThreads);
        Assert.Equal("Legacy title", thread.Title);
        Assert.Equal("Legacy response", thread.FinalResponse);
        Assert.Equal(KnownHarnessIds.Codex, thread.HarnessId);
        Assert.Equal("legacy-thread", thread.RemoteConversationId);
        Assert.NotEqual(Guid.Empty, thread.ConversationId);

        await store.SaveAsync(loaded);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        var root = document.RootElement;
        var persisted = root.GetProperty("projectThreads")[0];
        Assert.Equal(AppSettingsHarnessMigration.CurrentSchemaVersion, root.GetProperty("harnessSchemaVersion").GetInt32());
        Assert.Equal(KnownHarnessIds.Codex, persisted.GetProperty("harnessId").GetString());
        Assert.Equal("legacy-thread", persisted.GetProperty("remoteConversationId").GetString());
        Assert.True(persisted.TryGetProperty("conversationId", out var conversationId));
        Assert.True(Guid.TryParse(conversationId.GetString(), out var parsed) && parsed != Guid.Empty);
    }

    [Fact]
    public void Storage_mapper_deep_copy_preserves_harness_identity()
    {
        var conversationId = Guid.NewGuid();
        var source = new ProjectThreadState
        {
            ThreadId = conversationId.ToString("D"),
            ConversationId = conversationId,
            HarnessId = KnownHarnessIds.InMemory,
            RemoteConversationId = "memory-conversation-1",
            ProjectPath = Path.Combine(Path.GetTempPath(), "SynthiaCode", "HarnessPersistence"),
            WorkspacePath = Path.Combine(Path.GetTempPath(), "SynthiaCode", "HarnessPersistence")
        };

        var persisted = SettingsStorageMapper.ToPersisted(source);
        var projected = SettingsStorageMapper.ToPresentation(persisted);

        Assert.Equal(conversationId, projected.ConversationId);
        Assert.Equal(KnownHarnessIds.InMemory, projected.HarnessId);
        Assert.Equal("memory-conversation-1", projected.RemoteConversationId);
    }
}
