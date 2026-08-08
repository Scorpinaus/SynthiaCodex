using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Settings;

namespace SynthiaCode.Infrastructure.Settings;

public sealed class JsonPreferencesRepository : IPreferencesRepository
{
    private readonly VersionedJsonFile<DurablePreferencesDocument> file;

    public JsonPreferencesRepository(string appDataDirectory, IAppLogger logger)
    {
        Path = System.IO.Path.Combine(appDataDirectory, "preferences.json");
        file = new VersionedJsonFile<DurablePreferencesDocument>(
            Path,
            logger,
            document => document.SchemaVersion,
            document => document.Generation);
    }

    public string Path { get; }

    public Task<DurablePreferencesDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default) =>
        file.LoadAsync(generation, cancellationToken);

    public Task SaveAsync(DurablePreferencesDocument document, CancellationToken cancellationToken = default) =>
        file.SaveAsync(document, cancellationToken);
}

public sealed class JsonProjectThreadCatalogRepository : IProjectThreadCatalogRepository
{
    private readonly VersionedJsonFile<ProjectThreadCatalogDocument> file;

    public JsonProjectThreadCatalogRepository(string appDataDirectory, IAppLogger logger)
    {
        Path = System.IO.Path.Combine(appDataDirectory, "catalog.json");
        file = new VersionedJsonFile<ProjectThreadCatalogDocument>(
            Path,
            logger,
            document => document.SchemaVersion,
            document => document.Generation);
    }

    public string Path { get; }

    public Task<ProjectThreadCatalogDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default) =>
        file.LoadAsync(generation, cancellationToken);

    public Task SaveAsync(ProjectThreadCatalogDocument document, CancellationToken cancellationToken = default) =>
        file.SaveAsync(document, cancellationToken);
}

public sealed class JsonConversationRepository : IConversationRepository
{
    private readonly string directory;
    private readonly IAppLogger logger;

    public JsonConversationRepository(string appDataDirectory, IAppLogger logger)
    {
        directory = System.IO.Path.Combine(appDataDirectory, "conversations");
        this.logger = logger;
    }

    public string DirectoryPath => directory;

    public async Task<IReadOnlyList<ConversationDocument>> LoadAsync(
        IEnumerable<string> threadIds,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadIds);
        var conversations = new List<ConversationDocument>();
        foreach (var threadId in threadIds)
        {
            var file = CreateFile(threadId);
            var conversation = await file.LoadAsync(generation, cancellationToken).ConfigureAwait(false);
            if (conversation is null)
            {
                continue;
            }

            if (!string.Equals(conversation.ThreadId, threadId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Conversation file identity mismatch for thread '{threadId}'.");
            }

            conversations.Add(conversation);
        }

        return conversations;
    }

    public async Task SaveAsync(
        IEnumerable<ConversationDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        Directory.CreateDirectory(directory);
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.ThreadId))
            {
                throw new InvalidDataException("A conversation document must have a thread ID.");
            }

            await CreateFile(document.ThreadId).SaveAsync(document, cancellationToken).ConfigureAwait(false);
        }
    }

    public string GetPath(string threadId) =>
        System.IO.Path.Combine(directory, CreateFileName(threadId));

    private VersionedJsonFile<ConversationDocument> CreateFile(string threadId) => new(
        GetPath(threadId),
        logger,
        document => document.SchemaVersion,
        document => document.Generation);

    private static string CreateFileName(string threadId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(threadId));
        return $"{Convert.ToHexString(hash).ToLowerInvariant()}.json";
    }
}

public sealed class JsonQueueStateRepository : IQueueStateRepository
{
    private readonly VersionedJsonFile<QueueStateDocument> file;

    public JsonQueueStateRepository(string appDataDirectory, IAppLogger logger)
    {
        Path = System.IO.Path.Combine(appDataDirectory, "queues.json");
        file = new VersionedJsonFile<QueueStateDocument>(
            Path,
            logger,
            document => document.SchemaVersion,
            document => document.Generation);
    }

    public string Path { get; }

    public Task<QueueStateDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default) =>
        file.LoadAsync(generation, cancellationToken);

    public Task SaveAsync(QueueStateDocument document, CancellationToken cancellationToken = default) =>
        file.SaveAsync(document, cancellationToken);
}

public sealed class JsonDraftStateRepository : IDraftStateRepository
{
    private readonly VersionedJsonFile<DraftStateDocument> file;

    public JsonDraftStateRepository(string appDataDirectory, IAppLogger logger)
    {
        Path = System.IO.Path.Combine(appDataDirectory, "drafts.json");
        file = new VersionedJsonFile<DraftStateDocument>(
            Path,
            logger,
            document => document.SchemaVersion,
            document => document.Generation);
    }

    public string Path { get; }

    public Task<DraftStateDocument?> LoadAsync(long generation, CancellationToken cancellationToken = default) =>
        file.LoadAsync(generation, cancellationToken);

    public Task SaveAsync(DraftStateDocument document, CancellationToken cancellationToken = default) =>
        file.SaveAsync(document, cancellationToken);
}

public sealed class JsonDurableStateManifestRepository : IDurableStateManifestRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IAppLogger logger;

    public JsonDurableStateManifestRepository(string appDataDirectory, IAppLogger logger)
    {
        Path = System.IO.Path.Combine(appDataDirectory, "storage-manifest.json");
        this.logger = logger;
    }

    public string Path { get; }

    public async Task<DurableStateManifest?> LoadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var candidate in new[] { Path, Path + ".tmp", Path + ".bak" })
        {
            var manifest = await TryLoadAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            if (!string.Equals(candidate, Path, StringComparison.Ordinal))
            {
                File.Copy(candidate, Path, overwrite: true);
            }

            return manifest;
        }

        return null;
    }

    public Task SaveAsync(DurableStateManifest manifest, CancellationToken cancellationToken = default) =>
        AtomicJsonFile.SaveAsync(Path, manifest, SerializerOptions, cancellationToken);

    private async Task<DurableStateManifest?> TryLoadAsync(
        string candidate,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(candidate))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(candidate);
            return await JsonSerializer.DeserializeAsync<DurableStateManifest>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "durable_manifest_load_failed",
                $"Durable-state manifest candidate {System.IO.Path.GetFileName(candidate)} could not be loaded.",
                exception: ex);
            return null;
        }
    }
}

internal sealed class VersionedJsonFile<TDocument>(
    string path,
    IAppLogger logger,
    Func<TDocument, int> getSchemaVersion,
    Func<TDocument, long> getGeneration)
    where TDocument : class
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<TDocument?> LoadAsync(long generation, CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { path, path + ".bak", path + ".tmp" })
        {
            var document = await TryLoadAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (document is null ||
                getSchemaVersion(document) != DurableStateSchema.Current ||
                getGeneration(document) != generation)
            {
                continue;
            }

            if (!string.Equals(candidate, path, StringComparison.Ordinal))
            {
                File.Copy(candidate, path, overwrite: true);
                logger.Log(
                    AppLogLevel.Warning,
                    "durable_document_recovered",
                    $"Recovered {System.IO.Path.GetFileName(path)} for committed generation {generation}.");
            }

            return document;
        }

        return null;
    }

    public Task SaveAsync(TDocument document, CancellationToken cancellationToken) =>
        AtomicJsonFile.SaveAsync(path, document, SerializerOptions, cancellationToken);

    private async Task<TDocument?> TryLoadAsync(string candidate, CancellationToken cancellationToken)
    {
        if (!File.Exists(candidate))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(candidate);
            return await JsonSerializer.DeserializeAsync<TDocument>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log(
                AppLogLevel.Warning,
                "durable_document_load_failed",
                $"Durable-state candidate {System.IO.Path.GetFileName(candidate)} could not be loaded.",
                exception: ex);
            return null;
        }
    }
}

internal static class AtomicJsonFile
{
    public static async Task SaveAsync<TDocument>(
        string path,
        TDocument document,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("A durable-state file requires a parent directory."));

        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16_384,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, document, serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}
