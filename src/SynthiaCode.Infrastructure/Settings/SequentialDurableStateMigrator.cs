using SynthiaCode.Core.Settings;

namespace SynthiaCode.Infrastructure.Settings;

public sealed class SequentialDurableStateMigrator : IDurableStateMigrator
{
    private readonly IReadOnlyDictionary<int, IDurableStateMigration> migrations;

    public SequentialDurableStateMigrator(IEnumerable<IDurableStateMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        this.migrations = migrations.ToDictionary(migration => migration.FromVersion);

        foreach (var migration in this.migrations.Values)
        {
            if (migration.ToVersion != migration.FromVersion + 1)
            {
                throw new ArgumentException(
                    $"Durable-state migration {migration.FromVersion}->{migration.ToVersion} is not sequential.",
                    nameof(migrations));
            }
        }
    }

    public static SequentialDurableStateMigrator CreateDefault() => new(
        [new Release010ToVersion1Migration()]);

    public AppSettings Migrate(AppSettings settings, int fromVersion, int toVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (fromVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion));
        }
        if (toVersion < fromVersion)
        {
            throw new NotSupportedException(
                $"Durable-state downgrade {fromVersion}->{toVersion} is not supported.");
        }

        var migrated = SettingsStorageMapper.Clone(settings);
        for (var version = fromVersion; version < toVersion; version++)
        {
            if (!migrations.TryGetValue(version, out var migration))
            {
                throw new InvalidDataException(
                    $"No durable-state migration is registered for version {version}->{version + 1}.");
            }

            migration.Apply(migrated);
        }

        return migrated;
    }
}

public sealed class Release010ToVersion1Migration : IDurableStateMigration
{
    public int FromVersion => DurableStateSchema.Release010;

    public int ToVersion => DurableStateSchema.Current;

    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettingsHarnessMigration.Apply(settings);
    }
}
