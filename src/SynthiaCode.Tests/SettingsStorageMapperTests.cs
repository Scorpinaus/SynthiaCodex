using SynthiaCode.Core.Attachments;
using SynthiaCode.Core.Codex;
using SynthiaCode.Core.Codex.AppServer;
using SynthiaCode.Core.Projects;
using SynthiaCode.Core.Settings;
using Xunit;

public sealed class SettingsStorageMapperTests
{
    [Fact]
    public void Snapshot_preserves_placeholder_and_deeply_isolates_storage_dtos()
    {
        var source = new AppSettings
        {
            RecentProjects = [new RecentProject(ProjectPath, "Persistence tests", CreatedAt)],
            ProjectThreads = [CreatePersistedThread(isTitlePlaceholder: true)],
            ComposerAttachmentDrafts =
            [
                new ComposerAttachmentDraftSnapshot
                {
                    ProjectPath = ProjectPath,
                    ThreadId = "thread-1",
                    Attachments = [CreateAttachment("draft-attachment")]
                }
            ]
        };

        var snapshot = AppSettingsSnapshot.Create(source);
        var saved = Assert.Single(snapshot.ProjectThreads);

        source.ProjectThreads[0].IsTitlePlaceholder = false;
        source.RecentProjects.Clear();
        source.ProjectThreads[0].TimelineItems.Clear();
        source.ProjectThreads[0].RawEvents.Add("later event");
        source.ProjectThreads[0].ConversationTurns[0].Activity.Clear();
        source.ProjectThreads[0].ConversationTurns[0].UserAttachments[0].DisplayName = "changed attachment";
        source.ProjectThreads[0].QueuedFollowUps[0].Options.Model = "changed-model";
        source.ProjectThreads[0].QueuedFollowUps[0].Attachments[0].DisplayName = "changed queue attachment";
        source.ProjectThreads[0].QueuedFollowUps[0].SkillInputs.Clear();
        source.ComposerAttachmentDrafts[0].Attachments.Clear();
        source.ProjectThreads.Add(new PersistedProjectThread { ThreadId = "thread-2" });

        Assert.True(saved.IsTitlePlaceholder);
        Assert.Single(snapshot.RecentProjects);
        Assert.Single(snapshot.ProjectThreads);
        Assert.Single(saved.TimelineItems);
        Assert.Single(saved.RawEvents);
        Assert.Single(saved.ConversationTurns[0].Activity);
        Assert.Equal("attachment", saved.ConversationTurns[0].UserAttachments[0].DisplayName);
        Assert.Equal("model", saved.QueuedFollowUps[0].Options.Model);
        Assert.Equal("queue attachment", saved.QueuedFollowUps[0].Attachments[0].DisplayName);
        Assert.Single(saved.QueuedFollowUps[0].SkillInputs);
        Assert.Single(snapshot.ComposerAttachmentDrafts[0].Attachments);
        Assert.Equal("draft-attachment", snapshot.ComposerAttachmentDrafts[0].Attachments[0].DisplayName);
        Assert.NotSame(source.RecentProjects, snapshot.RecentProjects);
        Assert.NotSame(source.ProjectThreads, snapshot.ProjectThreads);
        Assert.NotSame(source.ProjectThreads[0], saved);
        Assert.NotSame(source.ProjectThreads[0].TimelineItems, saved.TimelineItems);
        Assert.NotSame(source.ProjectThreads[0].ConversationTurns[0], saved.ConversationTurns[0]);
        Assert.NotSame(source.ProjectThreads[0].ConversationTurns[0].Activity, saved.ConversationTurns[0].Activity);
        Assert.NotSame(source.ProjectThreads[0].QueuedFollowUps[0], saved.QueuedFollowUps[0]);
        Assert.NotSame(source.ProjectThreads[0].QueuedFollowUps[0].SkillInputs, saved.QueuedFollowUps[0].SkillInputs);
    }

    [Fact]
    public void Upsert_updates_existing_storage_object_in_place_and_isolates_mutable_collections()
    {
        var other = CreatePersistedThread(isTitlePlaceholder: false);
        other.ThreadId = "thread-2";
        other.Title = "Other thread";
        other.IsActive = false;
        var existing = CreatePersistedThread(isTitlePlaceholder: false);
        var settings = new AppSettings { ProjectThreads = [other, existing] };
        var state = CreatePresentationThread();
        state.IsActive = true;
        var store = new ThreadStore();
        var originalTimelineItems = existing.TimelineItems;
        var originalRawEvents = existing.RawEvents;
        var originalConversationTurns = existing.ConversationTurns;
        var originalQueuedFollowUps = existing.QueuedFollowUps;

        store.Upsert(settings, state);
        var saved = settings.ProjectThreads[1];

        Assert.Same(existing, saved);
        Assert.Same(other, settings.ProjectThreads[0]);
        Assert.Equal(state.ScopeKind, saved.ScopeKind);
        Assert.Equal(state.ProjectPath, saved.ProjectPath);
        Assert.Equal(state.ThreadId, saved.ThreadId);
        Assert.Equal(state.Title, saved.Title);
        Assert.Equal(state.IsTitlePlaceholder, saved.IsTitlePlaceholder);
        Assert.Equal(state.Preview, saved.Preview);
        Assert.Equal(state.IsArchived, saved.IsArchived);
        Assert.Equal(state.IsPinned, saved.IsPinned);
        Assert.Equal(state.IsActive, saved.IsActive);
        Assert.Equal(state.IsRunning, saved.IsRunning);
        Assert.Equal(state.TurnStatus, saved.TurnStatus);
        Assert.Equal(state.Mode, saved.Mode);
        Assert.Equal(state.WorkspacePath, saved.WorkspacePath);
        Assert.Equal(state.WorktreeBranch, saved.WorktreeBranch);
        Assert.Equal("new developer instructions", saved.AppliedDeveloperInstructions);
        Assert.Equal("new base instructions", saved.AppliedBaseInstructions);
        Assert.Equal(state.CreatedAt, saved.CreatedAt);
        Assert.Equal(state.FinalResponse, saved.FinalResponse);
        Assert.Equal(state.ContextTokensUsed, saved.ContextTokensUsed);
        Assert.Equal(state.ContextWindowTokens, saved.ContextWindowTokens);
        Assert.Equal(state.ContextCompactionCount, saved.ContextCompactionCount);
        Assert.Equal(state.UpdatedAt, saved.UpdatedAt);
        Assert.Equal("New title", existing.Title);
        Assert.Equal("thread-1", store.GetActive(settings, ProjectPath)?.ThreadId);
        Assert.NotSame(originalTimelineItems, saved.TimelineItems);
        Assert.NotSame(originalRawEvents, saved.RawEvents);
        Assert.NotSame(originalConversationTurns, saved.ConversationTurns);
        Assert.NotSame(originalQueuedFollowUps, saved.QueuedFollowUps);
        Assert.NotSame(state.TimelineItems, saved.TimelineItems);
        Assert.NotSame(state.RawEvents, saved.RawEvents);
        Assert.NotSame(state.ConversationTurns, saved.ConversationTurns);
        Assert.NotSame(state.QueuedFollowUps, saved.QueuedFollowUps);

        state.TimelineItems.Clear();
        state.RawEvents.Add("mutated after upsert");
        state.ConversationTurns[0].Activity.Clear();
        state.ConversationTurns[0].UserAttachments[0].DisplayName = "mutated after upsert";
        state.QueuedFollowUps[0].Options.PermissionProfileId = "mutated-profile";
        state.QueuedFollowUps[0].SkillInputs.Clear();

        Assert.Single(saved.TimelineItems);
        Assert.Single(saved.RawEvents);
        Assert.Single(saved.ConversationTurns[0].Activity);
        Assert.Equal("attachment", saved.ConversationTurns[0].UserAttachments[0].DisplayName);
        Assert.Equal("profile", saved.QueuedFollowUps[0].Options.PermissionProfileId);
        Assert.Single(saved.QueuedFollowUps[0].SkillInputs);
    }

    [Fact]
    public void Presentation_projection_isolated_from_persisted_thread_storage()
    {
        var settings = new AppSettings { ProjectThreads = [CreatePersistedThread(isTitlePlaceholder: true)] };
        var projected = Assert.Single(new ThreadStore().GetProjectThreads(settings, ProjectPath));

        projected.TimelineItems.Clear();
        projected.RawEvents.Clear();
        projected.ConversationTurns[0].Activity.Clear();
        projected.ConversationTurns[0].GeneratedImagePaths.Clear();
        projected.ConversationTurns[0].UserAttachments[0].DisplayName = "changed projection attachment";
        projected.QueuedFollowUps[0].Attachments[0].DisplayName = "changed projection queue attachment";
        projected.QueuedFollowUps[0].SkillInputs.Clear();

        var persisted = Assert.Single(settings.ProjectThreads);
        Assert.Single(persisted.TimelineItems);
        Assert.Single(persisted.RawEvents);
        Assert.Single(persisted.ConversationTurns[0].Activity);
        Assert.Single(persisted.ConversationTurns[0].GeneratedImagePaths);
        Assert.Equal("attachment", persisted.ConversationTurns[0].UserAttachments[0].DisplayName);
        Assert.Equal("queue attachment", persisted.QueuedFollowUps[0].Attachments[0].DisplayName);
        Assert.Single(persisted.QueuedFollowUps[0].SkillInputs);
    }

    private static PersistedProjectThread CreatePersistedThread(bool isTitlePlaceholder) => new()
    {
        ScopeKind = ThreadScopeKind.Project,
        ProjectPath = ProjectPath,
        ThreadId = "thread-1",
        Title = "Original title",
        IsTitlePlaceholder = isTitlePlaceholder,
        Preview = "Original preview",
        IsArchived = false,
        IsPinned = false,
        IsActive = true,
        IsRunning = false,
        TurnStatus = "Idle",
        Mode = "local",
        WorkspacePath = ProjectPath,
        WorktreeBranch = "original-branch",
        AppliedDeveloperInstructions = "original developer instructions",
        AppliedBaseInstructions = "original base instructions",
        CreatedAt = CreatedAt,
        FinalResponse = "Original response",
        TimelineItems = [CreateTimelineItem()],
        RawEvents = ["original event"],
        ConversationTurns = [CreateTurn()],
        QueuedFollowUps = [CreateQueuedFollowUp()],
        ContextTokensUsed = 100,
        ContextWindowTokens = 200,
        ContextCompactionCount = 3,
        UpdatedAt = UpdatedAt
    };

    private static ProjectThreadState CreatePresentationThread() => new()
    {
        ScopeKind = ThreadScopeKind.Project,
        ProjectPath = ProjectPath,
        ThreadId = "thread-1",
        Title = "New title",
        IsTitlePlaceholder = true,
        Preview = "New preview",
        IsArchived = true,
        IsPinned = true,
        IsActive = false,
        IsRunning = true,
        TurnStatus = "Failed",
        Mode = "worktree",
        WorkspacePath = ProjectPath,
        WorktreeBranch = "new-branch",
        AppliedDeveloperInstructions = "new developer instructions",
        AppliedBaseInstructions = "new base instructions",
        CreatedAt = CreatedAt.AddMinutes(1),
        FinalResponse = "New response",
        TimelineItems = [CreateTimelineItem()],
        RawEvents = ["new event"],
        ConversationTurns = [CreateTurn()],
        QueuedFollowUps = [CreateQueuedFollowUp()],
        ContextTokensUsed = 101,
        ContextWindowTokens = 201,
        ContextCompactionCount = 4,
        UpdatedAt = UpdatedAt.AddMinutes(1)
    };

    private static CodexConversationTurnSnapshot CreateTurn() => new()
    {
        TurnId = "turn-1",
        UserPrompt = "Prompt",
        AssistantResponse = "Response",
        Status = CodexTurnStatus.Completed,
        StartedAt = CreatedAt,
        CompletedAt = UpdatedAt,
        IsSuperseded = true,
        Activity = [CreateTimelineItem()],
        UserAttachments = [CreateAttachment("attachment")],
        GeneratedImagePaths = ["C:\\images\\generated.png"]
    };

    private static QueuedFollowUpSnapshot CreateQueuedFollowUp() => new()
    {
        Id = "queued-1",
        Text = "Queued prompt",
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        State = QueuedFollowUpState.NeedsAttention,
        LastError = "Needs review",
        Options = new QueuedTurnOptionsSnapshot
        {
            WorkspacePath = ProjectPath,
            Model = "model",
            ReasoningEffort = CodexReasoningEffort.High,
            ServiceTier = CodexServiceTierSelection.Fast,
            Sandbox = CodexSandbox.WorkspaceWrite,
            ApprovalPolicy = CodexApprovalPolicy.OnRequest,
            PermissionProfileId = "profile"
        },
        Attachments = [CreateAttachment("queue attachment")],
        SkillInputs = [new CodexSkillInput("skill", "C:\\skills\\SKILL.md")]
    };

    private static AttachmentReference CreateAttachment(string displayName) => new()
    {
        Id = displayName,
        Kind = AttachmentKind.File,
        SourceKind = AttachmentSourceKind.ManagedCopy,
        StorageKey = "storage-key",
        DisplayName = displayName,
        MediaType = "text/plain",
        ByteLength = 42,
        ContentSha256 = "hash"
    };

    private static CodexTimelineItem CreateTimelineItem() => new(
        CodexTimelineItemKind.Error,
        "Title",
        "Detail",
        "method",
        UpdatedAt)
    {
        ItemId = "item-1",
        ActivityKey = "activity-1"
    };

    private static readonly string ProjectPath = Path.Combine(Path.GetTempPath(), "SynthiaCode", "PersistenceTests");
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 7, 26, 10, 1, 0, TimeSpan.Zero);
}
