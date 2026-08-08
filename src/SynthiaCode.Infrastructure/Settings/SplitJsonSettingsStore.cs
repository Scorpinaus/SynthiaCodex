using System.Diagnostics;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Infrastructure.Settings;

public sealed class SplitJsonSettingsStore : ISettingsStore
{
    private const string LegacyRelease = "0.1.0";

    private readonly IPreferencesRepository preferencesRepository;
    private readonly IProjectThreadCatalogRepository catalogRepository;
    private readonly IConversationRepository conversationRepository;
    private readonly IQueueStateRepository queueRepository;
    private readonly IDraftStateRepository draftRepository;
    private readonly IDurableStateManifestRepository manifestRepository;
    private readonly ILegacySettingsRepository legacyRepository;
    private readonly IDurableStateMigrator migrator;
    private readonly IAppLogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SplitJsonSettingsStore(string appDataDirectory, IAppLogger logger)
        : this(
            Path.Combine(appDataDirectory, "preferences.json"),
            new JsonPreferencesRepository(appDataDirectory, logger),
            new JsonProjectThreadCatalogRepository(appDataDirectory, logger),
            new JsonConversationRepository(appDataDirectory, logger),
            new JsonQueueStateRepository(appDataDirectory, logger),
            new JsonDraftStateRepository(appDataDirectory, logger),
            new JsonDurableStateManifestRepository(appDataDirectory, logger),
            new JsonSettingsStore(appDataDirectory, logger),
            SequentialDurableStateMigrator.CreateDefault(),
            logger)
    {
    }

    public SplitJsonSettingsStore(
        string settingsPath,
        IPreferencesRepository preferencesRepository,
        IProjectThreadCatalogRepository catalogRepository,
        IConversationRepository conversationRepository,
        IQueueStateRepository queueRepository,
        IDraftStateRepository draftRepository,
        IDurableStateManifestRepository manifestRepository,
        ILegacySettingsRepository legacyRepository,
        IDurableStateMigrator migrator,
        IAppLogger logger)
    {
        SettingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        this.preferencesRepository = preferencesRepository ?? throw new ArgumentNullException(nameof(preferencesRepository));
        this.catalogRepository = catalogRepository ?? throw new ArgumentNullException(nameof(catalogRepository));
        this.conversationRepository = conversationRepository ?? throw new ArgumentNullException(nameof(conversationRepository));
        this.queueRepository = queueRepository ?? throw new ArgumentNullException(nameof(queueRepository));
        this.draftRepository = draftRepository ?? throw new ArgumentNullException(nameof(draftRepository));
        this.manifestRepository = manifestRepository ?? throw new ArgumentNullException(nameof(manifestRepository));
        this.legacyRepository = legacyRepository ?? throw new ArgumentNullException(nameof(legacyRepository));
        this.migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = await manifestRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                return await ImportLegacyOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            ValidateManifest(manifest);
            var preferences = await preferencesRepository.LoadAsync(manifest.Generation, cancellationToken)
                .ConfigureAwait(false);
            var catalog = await catalogRepository.LoadAsync(manifest.Generation, cancellationToken)
                .ConfigureAwait(false);
            var queueState = await queueRepository.LoadAsync(manifest.Generation, cancellationToken)
                .ConfigureAwait(false);
            var draftState = await draftRepository.LoadAsync(manifest.Generation, cancellationToken)
                .ConfigureAwait(false);

            if (preferences is null || catalog is null || queueState is null || draftState is null)
            {
                throw new InvalidDataException(
                    $"Durable state generation {manifest.Generation} is incomplete.");
            }

            var conversations = await conversationRepository.LoadAsync(
                    (catalog.Threads ?? []).Select(thread => thread.ThreadId),
                    manifest.Generation,
                    cancellationToken)
                .ConfigureAwait(false);

            var settings = DurableStateMapper.FromDocuments(
                preferences,
                catalog,
                conversations,
                queueState,
                draftState);
            AppSettingsHarnessMigration.Apply(settings);
            return settings;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = await manifestRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                ValidateManifest(manifest);
            }

            string? importedFromRelease = manifest?.ImportedFromRelease;
            DateTimeOffset? importedAtUtc = manifest?.ImportedAtUtc;
            if (manifest is null && legacyRepository.Exists)
            {
                await legacyRepository.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
                importedFromRelease = LegacyRelease;
                importedAtUtc = DateTimeOffset.UtcNow;
            }

            await SaveGenerationAsync(
                    settings,
                    (manifest?.Generation ?? 0) + 1,
                    importedFromRelease,
                    importedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AppSettings> ImportLegacyOrCreateDefaultAsync(CancellationToken cancellationToken)
    {
        if (!legacyRepository.Exists && !legacyRepository.BackupExists)
        {
            var settings = new AppSettings();
            AppSettingsHarnessMigration.Apply(settings);
            return settings;
        }

        if (!legacyRepository.BackupExists)
        {
            await legacyRepository.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
        }

        var legacy = await legacyRepository.LoadBackupAsync(cancellationToken).ConfigureAwait(false);
        var migrated = migrator.Migrate(
            legacy,
            DurableStateSchema.Release010,
            DurableStateSchema.Current);
        await SaveGenerationAsync(
                migrated,
                generation: 1,
                importedFromRelease: LegacyRelease,
                importedAtUtc: DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);

        logger.Log(
            AppLogLevel.Information,
            "legacy_settings_imported",
            "Release 0.1.0 settings were imported into split durable storage.");
        return SettingsStorageMapper.Clone(migrated);
    }

    private async Task SaveGenerationAsync(
        AppSettings settings,
        long generation,
        string? importedFromRelease,
        DateTimeOffset? importedAtUtc,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var snapshot = SettingsStorageMapper.Clone(settings);
        AppSettingsHarnessMigration.Apply(snapshot);

        await preferencesRepository.SaveAsync(
            DurableStateMapper.ToPreferences(snapshot, generation),
            cancellationToken).ConfigureAwait(false);
        await catalogRepository.SaveAsync(
            DurableStateMapper.ToCatalog(snapshot, generation),
            cancellationToken).ConfigureAwait(false);
        await conversationRepository.SaveAsync(
            DurableStateMapper.ToConversations(snapshot, generation),
            cancellationToken).ConfigureAwait(false);
        await queueRepository.SaveAsync(
            DurableStateMapper.ToQueueState(snapshot, generation),
            cancellationToken).ConfigureAwait(false);
        await draftRepository.SaveAsync(
            DurableStateMapper.ToDraftState(snapshot, generation),
            cancellationToken).ConfigureAwait(false);

        await manifestRepository.SaveAsync(new DurableStateManifest
        {
            Generation = generation,
            ImportedFromRelease = importedFromRelease,
            ImportedAtUtc = importedAtUtc
        }, cancellationToken).ConfigureAwait(false);

        logger.Log(
            AppLogLevel.Information,
            "durable_state_saved",
            "Split durable state was committed.",
            new Dictionary<string, string?>
            {
                ["generation"] = generation.ToString(),
                ["elapsedMilliseconds"] = timer.ElapsedMilliseconds.ToString(),
                ["threadCount"] = snapshot.ProjectThreads.Count.ToString()
            });
    }

    private static void ValidateManifest(DurableStateManifest manifest)
    {
        if (manifest.SchemaVersion != DurableStateSchema.Current)
        {
            throw new InvalidDataException(
                $"Durable-state schema {manifest.SchemaVersion} is not supported by schema {DurableStateSchema.Current}.");
        }
        if (manifest.Generation < 1)
        {
            throw new InvalidDataException("A durable-state manifest must identify a positive generation.");
        }
    }
}
