using System.Reflection;
using System.Text;
using System.Text.Json;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Settings;
using Xunit;

[Trait("Category", TestCategories.InfrastructureIntegration)]
[Collection(TestCategories.NativeCollection)]
public sealed class Phase2DurableStateTests
{
    [Fact]
    public async Task Split_store_round_trips_preferences_catalog_conversation_queue_and_drafts()
    {
        using var temp = TempWorkspace.Create();
        var store = new SplitJsonSettingsStore(temp.Root, new TestLogger());
        var settings = CreateSettings("Dark", "Original response");

        await store.SaveAsync(settings);
        var reloaded = await store.LoadAsync();

        Assert.Equal(Path.Combine(temp.Root, "preferences.json"), store.SettingsPath);
        Assert.False(File.Exists(Path.Combine(temp.Root, "settings.json")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "preferences.json")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "catalog.json")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "queues.json")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "drafts.json")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "storage-manifest.json")));
        var conversationPath = Assert.Single(Directory.GetFiles(Path.Combine(temp.Root, "conversations"), "*.json"));

        Assert.Equal("Dark", reloaded.Theme);
        Assert.Equal("Phase 2", Assert.Single(reloaded.RecentProjects).Name);
        var thread = Assert.Single(reloaded.ProjectThreads);
        Assert.Equal("Original response", thread.FinalResponse);
        Assert.Equal("Queued prompt", Assert.Single(thread.QueuedFollowUps).Text);
        Assert.Equal("Prompt", Assert.Single(thread.ConversationTurns).UserPrompt);
        Assert.Equal("draft.txt", Assert.Single(Assert.Single(reloaded.ComposerAttachmentDrafts).Attachments).DisplayName);

        using var preferences = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Root, "catalog.json")));
        using var conversation = JsonDocument.Parse(await File.ReadAllTextAsync(conversationPath));
        using var queues = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Root, "queues.json")));
        Assert.False(preferences.RootElement.TryGetProperty("projectThreads", out _));
        Assert.False(catalog.RootElement.GetProperty("threads")[0].TryGetProperty("finalResponse", out _));
        Assert.False(conversation.RootElement.TryGetProperty("queuedFollowUps", out _));
        Assert.Equal(DurableStateSchema.Current, conversation.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Queued prompt", queues.RootElement.GetProperty("threads")[0]
            .GetProperty("queuedFollowUps")[0]
            .GetProperty("text")
            .GetString());
    }

    [Fact]
    public async Task Release_010_import_is_backed_up_exactly_and_runs_only_once()
    {
        using var temp = TempWorkspace.Create();
        var legacyPath = Path.Combine(temp.Root, "settings.json");
        var interruptedLegacyPath = legacyPath + ".tmp";
        var legacyJson = """
            {
              "Theme": "Legacy dark",
              "ProjectThreads": [
                {
                  "ProjectPath": "C:\\legacy",
                  "ThreadId": "legacy-remote-id",
                  "Title": "Imported thread",
                  "FinalResponse": "Imported response"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(
            interruptedLegacyPath,
            legacyJson,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var originalBytes = await File.ReadAllBytesAsync(interruptedLegacyPath);
        var store = new SplitJsonSettingsStore(temp.Root, new TestLogger());

        var imported = await store.LoadAsync();

        var backupPath = Path.Combine(temp.Root, "settings.release-0.1.0.backup.json");
        Assert.True(File.Exists(legacyPath));
        Assert.False(File.Exists(interruptedLegacyPath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath));
        Assert.Equal("Legacy dark", imported.Theme);
        var importedThread = Assert.Single(imported.ProjectThreads);
        Assert.Equal(KnownHarnessIds.Codex, importedThread.HarnessId);
        Assert.Equal("legacy-remote-id", importedThread.RemoteConversationId);
        Assert.NotEqual(Guid.Empty, importedThread.ConversationId);

        using (var manifest = JsonDocument.Parse(
                   await File.ReadAllTextAsync(Path.Combine(temp.Root, "storage-manifest.json"))))
        {
            Assert.Equal("0.1.0", manifest.RootElement.GetProperty("importedFromRelease").GetString());
            Assert.Equal(DurableStateSchema.Current, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        await File.WriteAllTextAsync(legacyPath, "{\"Theme\":\"Must not reimport\"}");
        var loadedAgain = await new SplitJsonSettingsStore(temp.Root, new TestLogger()).LoadAsync();
        Assert.Equal("Legacy dark", loadedAgain.Theme);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupPath));
    }

    [Fact]
    public async Task Committed_manifest_generation_recovers_documents_after_an_interrupted_save()
    {
        using var temp = TempWorkspace.Create();
        var logger = new TestLogger();
        var store = new SplitJsonSettingsStore(temp.Root, logger);
        await store.SaveAsync(CreateSettings("Generation one", "Stable response"));
        await store.SaveAsync(CreateSettings("Generation two", "Uncommitted response"));

        var manifestPath = Path.Combine(temp.Root, "storage-manifest.json");
        File.Copy(manifestPath + ".bak", manifestPath, overwrite: true);
        var recovered = await new SplitJsonSettingsStore(temp.Root, logger).LoadAsync();

        Assert.Equal("Generation one", recovered.Theme);
        Assert.Equal("Stable response", Assert.Single(recovered.ProjectThreads).FinalResponse);
        Assert.Contains(logger.Entries, entry => entry.EventName == "durable_document_recovered");
    }

    [Fact]
    public void Durable_migrations_are_explicit_and_strictly_sequential()
    {
        var migrated = SequentialDurableStateMigrator.CreateDefault().Migrate(
            new AppSettings
            {
                HarnessSchemaVersion = 0,
                ProjectThreads = [new PersistedProjectThread { ThreadId = "legacy-thread" }]
            },
            DurableStateSchema.Release010,
            DurableStateSchema.Current);

        Assert.Equal(AppSettingsHarnessMigration.CurrentSchemaVersion, migrated.HarnessSchemaVersion);
        Assert.NotEqual(Guid.Empty, Assert.Single(migrated.ProjectThreads).ConversationId);
        Assert.Throws<InvalidDataException>(() =>
            new SequentialDurableStateMigrator([]).Migrate(new AppSettings(), 0, 1));
        Assert.Throws<ArgumentException>(() =>
            new SequentialDurableStateMigrator([new SkippingMigration()]));
        Assert.Throws<NotSupportedException>(() =>
            SequentialDurableStateMigrator.CreateDefault().Migrate(new AppSettings(), 1, 0));
    }

    [Fact]
    public void Repository_boundaries_are_in_core_while_the_phase_does_not_change_the_in_memory_model()
    {
        var coreAssembly = typeof(AppSettings).Assembly;
        var infrastructureAssembly = typeof(SplitJsonSettingsStore).Assembly;
        Assert.Same(coreAssembly, typeof(IPreferencesRepository).Assembly);
        Assert.Same(coreAssembly, typeof(IProjectThreadCatalogRepository).Assembly);
        Assert.Same(coreAssembly, typeof(IConversationRepository).Assembly);
        Assert.Same(coreAssembly, typeof(IQueueStateRepository).Assembly);
        Assert.Same(coreAssembly, typeof(IDraftStateRepository).Assembly);
        Assert.Same(infrastructureAssembly, typeof(JsonPreferencesRepository).Assembly);

        var properties = typeof(AppSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[]
        {
            "ApprovalPolicyOverride",
            "AttachmentSchemaVersion",
            "ComposerAttachmentDrafts",
            "CustomBaseInstructions",
            "CustomBaseInstructionsEnabled",
            "CustomDeveloperInstructions",
            "CustomDeveloperInstructionsEnabled",
            "CustomPermissionProfileId",
            "DefaultHarnessId",
            "ExecutionPolicySchemaVersion",
            "FollowUpBehavior",
            "HarnessSchemaVersion",
            "IsDetailsPaneOpen",
            "IsProjectRailOpen",
            "LastModelOverride",
            "LastReasoningEffortOverride",
            "LastSelectedProjectPath",
            "LastServiceTierOverride",
            "PermissionMode",
            "PreferredCodexPath",
            "ProjectThreads",
            "RecentProjects",
            "SandboxModeOverride",
            "Theme"
        }, properties);
    }

    private static AppSettings CreateSettings(string theme, string finalResponse)
    {
        var timestamp = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        return new AppSettings
        {
            Theme = theme,
            LastModelOverride = "gpt-phase-2",
            RecentProjects = [new RecentProject("C:\\phase-2", "Phase 2", timestamp)],
            ProjectThreads =
            [
                new PersistedProjectThread
                {
                    ScopeKind = ThreadScopeKind.Project,
                    ProjectPath = "C:\\phase-2",
                    ThreadId = "phase-2-thread",
                    ConversationId = Guid.Parse("daf09553-8e9f-498c-b958-c912022ea201"),
                    HarnessId = KnownHarnessIds.Codex,
                    RemoteConversationId = "remote-phase-2",
                    Title = "Durable state",
                    Preview = "Separate persistence",
                    IsPinned = true,
                    IsActive = true,
                    TurnStatus = "Idle",
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp.AddMinutes(1),
                    FinalResponse = finalResponse,
                    RawEvents = ["event"],
                    ConversationTurns =
                    [
                        new CodexConversationTurnSnapshot
                        {
                            TurnId = "turn-1",
                            UserPrompt = "Prompt",
                            AssistantResponse = finalResponse,
                            Status = CodexTurnStatus.Completed,
                            StartedAt = timestamp,
                            CompletedAt = timestamp.AddMinutes(1)
                        }
                    ],
                    QueuedFollowUps =
                    [
                        new QueuedFollowUpSnapshot
                        {
                            Id = "queue-1",
                            Text = "Queued prompt",
                            CreatedAt = timestamp,
                            UpdatedAt = timestamp,
                            Options = new QueuedTurnOptionsSnapshot { WorkspacePath = "C:\\phase-2" }
                        }
                    ],
                    ContextTokensUsed = 120,
                    ContextWindowTokens = 1_000,
                    ContextCompactionCount = 2
                }
            ],
            ComposerAttachmentDrafts =
            [
                new ComposerAttachmentDraftSnapshot
                {
                    ProjectPath = "C:\\phase-2",
                    ThreadId = "phase-2-thread",
                    Attachments =
                    [
                        new AttachmentReference
                        {
                            Id = "draft-1",
                            Kind = AttachmentKind.File,
                            StorageKey = "draft-key",
                            DisplayName = "draft.txt",
                            MediaType = "text/plain"
                        }
                    ],
                    UpdatedAt = timestamp
                }
            ]
        };
    }

    private sealed class SkippingMigration : IDurableStateMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 2;
        public void Apply(AppSettings settings)
        {
        }
    }
}
