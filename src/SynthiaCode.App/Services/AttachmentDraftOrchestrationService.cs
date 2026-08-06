using System.IO;
using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Harnesses;
using SynthiaCode.Core.Logging;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using SynthiaCode.Infrastructure.Attachments;

namespace SynthiaCode.App.Services;

/// <summary>
/// Owns attachment import decisions and the durable composer-draft representation.
/// UI code supplies a workspace path and applies the returned attachments; this service
/// remains independent from WPF controls and view-model state.
/// </summary>
public sealed class AttachmentDraftOrchestrationService
{
    private readonly IAttachmentStore? attachmentStore;
    private readonly WorkspaceAttachmentResolver workspaceAttachmentResolver;
    private readonly CodexTurnRequestFactory turnRequestFactory;
    private readonly HarnessTurnRequestFactory harnessTurnRequestFactory;
    private readonly IAppLogger logger;

    public AttachmentDraftOrchestrationService(
        IAttachmentStore? attachmentStore,
        WorkspaceAttachmentResolver workspaceAttachmentResolver,
        CodexTurnRequestFactory turnRequestFactory,
        IAppLogger logger)
    {
        this.attachmentStore = attachmentStore;
        this.workspaceAttachmentResolver = workspaceAttachmentResolver;
        this.turnRequestFactory = turnRequestFactory;
        harnessTurnRequestFactory = new HarnessTurnRequestFactory(attachmentStore, workspaceAttachmentResolver);
        this.logger = logger;
    }

    public CodexThreadStartOptions CreateThreadStart(
        CodexResolvedPermissionMode permissions, string? model, string cwd,
        string? developerInstructions, string? baseInstructions) =>
        turnRequestFactory.CreateThreadStart(permissions, model, cwd, developerInstructions, baseInstructions);

    public CodexThreadResumeRequest CreateThreadResume(
        CodexResolvedPermissionMode permissions, string? model, string threadId, string cwd,
        string? developerInstructions, string? baseInstructions) =>
        turnRequestFactory.CreateThreadResume(permissions, model, threadId, cwd, developerInstructions, baseInstructions);

    public CodexThreadForkRequest CreateThreadFork(
        CodexResolvedPermissionMode permissions, string? model, string threadId, string cwd,
        string? developerInstructions, string? baseInstructions) =>
        turnRequestFactory.CreateThreadFork(permissions, model, threadId, cwd, developerInstructions, baseInstructions);

    public CodexTurnStartRequest CreateTurnStart(TurnRequestComposition composition) =>
        turnRequestFactory.CreateTurnStart(composition);

    public IReadOnlyList<CodexUserInput> BuildPromptInputs(
        string prompt, IReadOnlyList<AttachmentReference> attachments, string workspacePath,
        CodexModelOption? selectedModel, IReadOnlyList<CodexSkillInput> skillInputs) =>
        turnRequestFactory.BuildInputs(prompt, attachments, workspacePath, selectedModel, skillInputs);

    public StartConversationCommand CreateHarnessConversationStart(
        ConversationId conversationId,
        CodexResolvedPermissionMode permissions,
        string? model,
        string workspacePath,
        string? developerInstructions,
        string? baseInstructions) =>
        harnessTurnRequestFactory.CreateConversationStart(
            conversationId,
            permissions,
            model,
            workspacePath,
            developerInstructions,
            baseInstructions);

    public ResumeConversationCommand CreateHarnessConversationResume(
        ConversationAddress address,
        CodexResolvedPermissionMode permissions,
        string? model,
        string workspacePath,
        string? developerInstructions,
        string? baseInstructions) =>
        harnessTurnRequestFactory.CreateConversationResume(
            address,
            permissions,
            model,
            workspacePath,
            developerInstructions,
            baseInstructions);

    public ForkConversationCommand CreateHarnessConversationFork(
        ConversationId conversationId,
        ConversationAddress source,
        CodexResolvedPermissionMode permissions,
        string? model,
        string workspacePath,
        string? developerInstructions,
        string? baseInstructions) =>
        harnessTurnRequestFactory.CreateConversationFork(
            conversationId,
            source,
            permissions,
            model,
            workspacePath,
            developerInstructions,
            baseInstructions);

    public StartTurnCommand CreateHarnessTurnStart(HarnessTurnRequestComposition composition) =>
        harnessTurnRequestFactory.CreateTurnStart(composition);

    public IReadOnlyList<HarnessContentPart> BuildHarnessPromptInputs(
        string prompt,
        IReadOnlyList<AttachmentReference> attachments,
        string workspacePath,
        CodexModelOption? selectedModel,
        IReadOnlyList<CodexSkillInput> skillInputs) =>
        harnessTurnRequestFactory.BuildInputs(
            prompt,
            attachments,
            workspacePath,
            selectedModel,
            skillInputs);

    public QueuedTurnOptionsSnapshot CaptureQueuedOptions(
        CodexResolvedPermissionMode permissions, CodexPermissionMode permissionMode,
        string workspacePath, string? model, string? reasoningEffort, CodexServiceTierSelection serviceTier) =>
        turnRequestFactory.CaptureQueuedOptions(
            permissions, permissionMode, workspacePath, model, reasoningEffort, serviceTier);

    public async Task RestoreAndCleanupPersistedAttachmentsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (attachmentStore is null) return;
        var references = settings.ProjectThreads
            .SelectMany(thread => thread.ConversationTurns.SelectMany(turn => turn.UserAttachments)
                .Concat(thread.QueuedFollowUps.SelectMany(item => item.Attachments)))
            .Concat(settings.ComposerAttachmentDrafts.SelectMany(draft => draft.Attachments)).ToList();
        foreach (var attachment in references.Where(item => item.SourceKind == AttachmentSourceKind.ManagedCopy))
        {
            try { attachment.ManagedPath = attachmentStore.ResolvePath(attachment); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                attachment.ManagedPath = null;
                logger.Log(AppLogLevel.Warning, "attachment_restore_unavailable", "A persisted managed attachment is unavailable.", new Dictionary<string, string?> { ["storageKey"] = attachment.StorageKey }, ex);
            }
        }
        try { await attachmentStore.CleanupAsync(references.Where(item => item.SourceKind == AttachmentSourceKind.ManagedCopy).Select(item => item.StorageKey)).ConfigureAwait(false); }
        catch (Exception ex) { logger.Log(AppLogLevel.Warning, "attachment_cleanup_failed", "Managed attachment cleanup could not be completed.", exception: ex); }
    }

    public async Task<AttachmentImportResult> ImportImagesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var store = attachmentStore ?? throw new InvalidOperationException("Attachment storage is unavailable.");
        var attachments = new List<AttachmentReference>();
        var failures = new List<string>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                attachments.Add(await store.ImportFileAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new AttachmentImportResult(attachments, failures);
    }

    public async Task<AttachmentImportResult> ImportPathsAsync(
        IEnumerable<string> paths,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var attachments = new List<AttachmentReference>();
        var failures = new List<string>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                attachments.Add(await ImportPathAsync(path, workspacePath, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new AttachmentImportResult(attachments, failures);
    }

    public async Task<AttachmentReference> ImportPastedImageAsync(
        Stream imageStream,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        var store = attachmentStore ?? throw new InvalidOperationException("Attachment storage is unavailable.");
        return await store.ImportStreamAsync(imageStream, displayName, cancellationToken).ConfigureAwait(false);
    }

    public void CaptureDraft(
        AppSettings settings,
        string? projectPath,
        string? threadId,
        IEnumerable<AttachmentReference> attachments)
    {
        var scope = string.IsNullOrWhiteSpace(projectPath)
            ? ThreadScopeKey.General
            : ThreadScopeKey.ForProject(projectPath);
        var draft = settings.ComposerAttachmentDrafts.FirstOrDefault(item =>
            scope.Matches(item.ScopeKind, item.ProjectPath) &&
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal));
        var snapshot = attachments.Select(attachment => attachment.Clone()).ToList();
        if (snapshot.Count == 0)
        {
            if (draft is not null)
            {
                settings.ComposerAttachmentDrafts.Remove(draft);
            }
            return;
        }

        draft ??= new ComposerAttachmentDraftSnapshot
        {
            ScopeKind = scope.Kind,
            ProjectPath = scope.ProjectPath ?? string.Empty,
            ThreadId = threadId
        };
        if (!settings.ComposerAttachmentDrafts.Contains(draft))
        {
            settings.ComposerAttachmentDrafts.Add(draft);
        }
        draft.Attachments = snapshot;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<AttachmentReference> RestoreDraft(
        AppSettings settings,
        string? projectPath,
        string? threadId,
        string workspacePath)
    {
        var scope = string.IsNullOrWhiteSpace(projectPath)
            ? ThreadScopeKey.General
            : ThreadScopeKey.ForProject(projectPath);
        var draft = settings.ComposerAttachmentDrafts.FirstOrDefault(item =>
            scope.Matches(item.ScopeKind, item.ProjectPath) &&
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal));
        return (draft?.Attachments ?? []).Select(attachment => RevalidateWorkspaceReference(workspacePath, attachment)).ToList();
    }

    public string ResolveOpenPath(string workspacePath, AttachmentReference attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return attachment.SourceKind == AttachmentSourceKind.ManagedCopy
            ? attachmentStore?.ResolvePath(attachment) ?? attachment.ManagedPath ?? string.Empty
            : workspaceAttachmentResolver.Revalidate(workspacePath, attachment).ManagedPath ?? string.Empty;
    }

    private async Task<AttachmentReference> ImportPathAsync(string path, string workspacePath, CancellationToken cancellationToken)
    {
        var isWithinWorkspace = workspaceAttachmentResolver.IsWithinWorkspace(workspacePath, path);
        if (Directory.Exists(path))
        {
            return isWithinWorkspace
                ? workspaceAttachmentResolver.Resolve(workspacePath, path, AttachmentKind.Folder)
                : await GetStore().ImportFolderAsync(path, cancellationToken).ConfigureAwait(false);
        }
        if (IsSupportedImagePath(path))
        {
            return await GetStore().ImportFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        return isWithinWorkspace
            ? workspaceAttachmentResolver.Resolve(workspacePath, path, AttachmentKind.File)
            : await GetStore().ImportExternalFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private AttachmentReference RevalidateWorkspaceReference(string workspacePath, AttachmentReference attachment)
    {
        if (attachment.SourceKind != AttachmentSourceKind.WorkspaceReference)
        {
            return attachment;
        }
        try
        {
            return workspaceAttachmentResolver.Revalidate(workspacePath, attachment);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            var unavailable = attachment.Clone();
            unavailable.ManagedPath = null;
            return unavailable;
        }
    }

    private IAttachmentStore GetStore() => attachmentStore ?? throw new InvalidOperationException("Attachment storage is unavailable.");

    private static bool IsSupportedImagePath(string path) =>
        string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".jpeg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase);
}

public sealed record AttachmentImportResult(
    IReadOnlyList<AttachmentReference> Attachments,
    IReadOnlyList<string> Failures)
{
    public string ToStatusMessage(string noun)
    {
        var added = Attachments.Count;
        return Failures.Count == 0
            ? $"Added {added} {noun}{(added == 1 ? string.Empty : "s")}" 
            : added == 0
                ? Failures[0]
                : $"Added {added} {noun}{(added == 1 ? string.Empty : "s")}; {Failures.Count} skipped";
    }
}
