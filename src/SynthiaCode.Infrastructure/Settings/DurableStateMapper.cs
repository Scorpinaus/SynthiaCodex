using SynthiaCode.Core.Settings;

namespace SynthiaCode.Infrastructure.Settings;

public static class DurableStateMapper
{
    public static DurablePreferencesDocument ToPreferences(AppSettings settings, long generation)
    {
        var source = SettingsStorageMapper.Clone(settings);
        return new DurablePreferencesDocument
        {
            Generation = generation,
            Theme = source.Theme,
            PreferredCodexPath = source.PreferredCodexPath,
            LastModelOverride = source.LastModelOverride,
            LastReasoningEffortOverride = source.LastReasoningEffortOverride,
            LastServiceTierOverride = source.LastServiceTierOverride,
            FollowUpBehavior = source.FollowUpBehavior,
            CustomDeveloperInstructionsEnabled = source.CustomDeveloperInstructionsEnabled,
            CustomDeveloperInstructions = source.CustomDeveloperInstructions,
            CustomBaseInstructionsEnabled = source.CustomBaseInstructionsEnabled,
            CustomBaseInstructions = source.CustomBaseInstructions,
            SandboxModeOverride = source.SandboxModeOverride,
            ApprovalPolicyOverride = source.ApprovalPolicyOverride,
            PermissionMode = source.PermissionMode,
            CustomPermissionProfileId = source.CustomPermissionProfileId,
            ExecutionPolicySchemaVersion = source.ExecutionPolicySchemaVersion,
            AttachmentSchemaVersion = source.AttachmentSchemaVersion,
            HarnessSchemaVersion = source.HarnessSchemaVersion,
            DefaultHarnessId = source.DefaultHarnessId,
            IsProjectRailOpen = source.IsProjectRailOpen,
            IsDetailsPaneOpen = source.IsDetailsPaneOpen,
            LastSelectedProjectPath = source.LastSelectedProjectPath
        };
    }

    public static ProjectThreadCatalogDocument ToCatalog(AppSettings settings, long generation)
    {
        var source = SettingsStorageMapper.Clone(settings);
        return new ProjectThreadCatalogDocument
        {
            Generation = generation,
            Projects = source.RecentProjects,
            Threads = [.. source.ProjectThreads.Select(thread => new ProjectThreadCatalogEntry
            {
                ScopeKind = thread.ScopeKind,
                ProjectPath = thread.ProjectPath,
                ThreadId = thread.ThreadId,
                ConversationId = thread.ConversationId,
                HarnessId = thread.HarnessId,
                RemoteConversationId = thread.RemoteConversationId,
                Title = thread.Title,
                IsTitlePlaceholder = thread.IsTitlePlaceholder,
                Preview = thread.Preview,
                IsArchived = thread.IsArchived,
                IsPinned = thread.IsPinned,
                IsActive = thread.IsActive,
                IsRunning = thread.IsRunning,
                TurnStatus = thread.TurnStatus,
                Mode = thread.Mode,
                WorkspacePath = thread.WorkspacePath,
                WorktreeBranch = thread.WorktreeBranch,
                AppliedDeveloperInstructions = thread.AppliedDeveloperInstructions,
                AppliedBaseInstructions = thread.AppliedBaseInstructions,
                CreatedAt = thread.CreatedAt,
                UpdatedAt = thread.UpdatedAt
            })]
        };
    }

    public static IReadOnlyList<ConversationDocument> ToConversations(AppSettings settings, long generation)
    {
        var source = SettingsStorageMapper.Clone(settings);
        return [.. source.ProjectThreads.Select(thread => new ConversationDocument
        {
            Generation = generation,
            ThreadId = thread.ThreadId,
            FinalResponse = thread.FinalResponse,
            TimelineItems = thread.TimelineItems,
            RawEvents = thread.RawEvents,
            ConversationTurns = thread.ConversationTurns,
            ContextTokensUsed = thread.ContextTokensUsed,
            ContextWindowTokens = thread.ContextWindowTokens,
            ContextCompactionCount = thread.ContextCompactionCount
        })];
    }

    public static QueueStateDocument ToQueueState(AppSettings settings, long generation)
    {
        var source = SettingsStorageMapper.Clone(settings);
        return new QueueStateDocument
        {
            Generation = generation,
            Threads = [.. source.ProjectThreads
                .Where(thread => thread.QueuedFollowUps.Count > 0)
                .Select(thread => new ThreadQueueState
                {
                    ThreadId = thread.ThreadId,
                    QueuedFollowUps = thread.QueuedFollowUps
                })]
        };
    }

    public static DraftStateDocument ToDraftState(AppSettings settings, long generation)
    {
        var source = SettingsStorageMapper.Clone(settings);
        return new DraftStateDocument
        {
            Generation = generation,
            Drafts = source.ComposerAttachmentDrafts
        };
    }

    public static AppSettings FromDocuments(
        DurablePreferencesDocument preferences,
        ProjectThreadCatalogDocument catalog,
        IReadOnlyList<ConversationDocument> conversations,
        QueueStateDocument queueState,
        DraftStateDocument draftState)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(queueState);
        ArgumentNullException.ThrowIfNull(draftState);

        var conversationsByThread = conversations.ToDictionary(
            conversation => conversation.ThreadId,
            StringComparer.Ordinal);
        var queuesByThread = (queueState.Threads ?? []).ToDictionary(
            queue => queue.ThreadId,
            StringComparer.Ordinal);

        var settings = new AppSettings
        {
            Theme = preferences.Theme,
            PreferredCodexPath = preferences.PreferredCodexPath,
            LastModelOverride = preferences.LastModelOverride,
            LastReasoningEffortOverride = preferences.LastReasoningEffortOverride,
            LastServiceTierOverride = preferences.LastServiceTierOverride,
            FollowUpBehavior = preferences.FollowUpBehavior,
            CustomDeveloperInstructionsEnabled = preferences.CustomDeveloperInstructionsEnabled,
            CustomDeveloperInstructions = preferences.CustomDeveloperInstructions,
            CustomBaseInstructionsEnabled = preferences.CustomBaseInstructionsEnabled,
            CustomBaseInstructions = preferences.CustomBaseInstructions,
            SandboxModeOverride = preferences.SandboxModeOverride,
            ApprovalPolicyOverride = preferences.ApprovalPolicyOverride,
            PermissionMode = preferences.PermissionMode,
            CustomPermissionProfileId = preferences.CustomPermissionProfileId,
            ExecutionPolicySchemaVersion = preferences.ExecutionPolicySchemaVersion,
            AttachmentSchemaVersion = preferences.AttachmentSchemaVersion,
            HarnessSchemaVersion = preferences.HarnessSchemaVersion,
            DefaultHarnessId = preferences.DefaultHarnessId,
            IsProjectRailOpen = preferences.IsProjectRailOpen,
            IsDetailsPaneOpen = preferences.IsDetailsPaneOpen,
            LastSelectedProjectPath = preferences.LastSelectedProjectPath,
            RecentProjects = catalog.Projects ?? [],
            ComposerAttachmentDrafts = draftState.Drafts ?? []
        };

        foreach (var entry in catalog.Threads ?? [])
        {
            if (!conversationsByThread.TryGetValue(entry.ThreadId, out var conversation))
            {
                throw new InvalidDataException($"Conversation state is missing for thread '{entry.ThreadId}'.");
            }

            queuesByThread.TryGetValue(entry.ThreadId, out var queue);
            settings.ProjectThreads.Add(new PersistedProjectThread
            {
                ScopeKind = entry.ScopeKind,
                ProjectPath = entry.ProjectPath,
                ThreadId = entry.ThreadId,
                ConversationId = entry.ConversationId,
                HarnessId = entry.HarnessId,
                RemoteConversationId = entry.RemoteConversationId,
                Title = entry.Title,
                IsTitlePlaceholder = entry.IsTitlePlaceholder,
                Preview = entry.Preview,
                IsArchived = entry.IsArchived,
                IsPinned = entry.IsPinned,
                IsActive = entry.IsActive,
                IsRunning = entry.IsRunning,
                TurnStatus = entry.TurnStatus,
                Mode = entry.Mode,
                WorkspacePath = entry.WorkspacePath,
                WorktreeBranch = entry.WorktreeBranch,
                AppliedDeveloperInstructions = entry.AppliedDeveloperInstructions,
                AppliedBaseInstructions = entry.AppliedBaseInstructions,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt,
                FinalResponse = conversation.FinalResponse,
                TimelineItems = conversation.TimelineItems ?? [],
                RawEvents = conversation.RawEvents ?? [],
                ConversationTurns = conversation.ConversationTurns ?? [],
                QueuedFollowUps = queue?.QueuedFollowUps ?? [],
                ContextTokensUsed = conversation.ContextTokensUsed,
                ContextWindowTokens = conversation.ContextWindowTokens,
                ContextCompactionCount = conversation.ContextCompactionCount
            });
        }

        return SettingsStorageMapper.Clone(settings);
    }
}
