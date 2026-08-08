using System.Text;
using SynthiaCode.Application.Harnesses;
using SynthiaCode.Core.Harnesses;

namespace SynthiaCode.Harnesses.InMemory;

public sealed class InMemoryHarness : IAgentHarness
{
    private static readonly HarnessCapabilities SupportedCapabilities = new(
        HarnessCapability.CreateConversation |
        HarnessCapability.ResumeConversation |
        HarnessCapability.ReadConversation |
        HarnessCapability.RenameConversation |
        HarnessCapability.ArchiveConversation |
        HarnessCapability.ForkConversation |
        HarnessCapability.RollbackConversation |
        HarnessCapability.StartTurn |
        HarnessCapability.CancelTurn |
        HarnessCapability.SteerTurn |
        HarnessCapability.Streaming |
        HarnessCapability.ImageInput |
        HarnessCapability.WorkspaceReferences |
        HarnessCapability.ModelCatalog);

    public HarnessDescriptor Descriptor { get; } = new(
        HarnessId.InMemory,
        "In-memory",
        "Deterministic local harness used to prove the runtime boundary.",
        SupportedCapabilities);

    public Task<HarnessAvailability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HarnessAvailability(
            HarnessAvailabilityState.Available,
            "The in-memory harness is available."));
    }

    public Task<IHarnessSession> ConnectAsync(
        HarnessConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IHarnessSession>(new InMemoryHarnessSession(Descriptor));
    }
}

public sealed class InMemoryHarnessSession : HarnessSessionBase,
    IConversationCreationFeature,
    IConversationResumeFeature,
    IConversationReadFeature,
    IConversationNamingFeature,
    IConversationArchiveFeature,
    IConversationForkFeature,
    IConversationRollbackFeature,
    ITurnExecutionFeature,
    ITurnCancellationFeature,
    ITurnSteeringFeature,
    IModelCatalogFeature
{
    private readonly object stateGate = new();
    private readonly Dictionary<string, ConversationState> conversations = new(StringComparer.Ordinal);
    private long nextConversationId;
    private long nextTurnId;

    public InMemoryHarnessSession(HarnessDescriptor descriptor) : base(descriptor)
    {
        RegisterFeature<IConversationCreationFeature>(this);
        RegisterFeature<IConversationResumeFeature>(this);
        RegisterFeature<IConversationReadFeature>(this);
        RegisterFeature<IConversationNamingFeature>(this);
        RegisterFeature<IConversationArchiveFeature>(this);
        RegisterFeature<IConversationForkFeature>(this);
        RegisterFeature<IConversationRollbackFeature>(this);
        RegisterFeature<ITurnExecutionFeature>(this);
        RegisterFeature<ITurnCancellationFeature>(this);
        RegisterFeature<ITurnSteeringFeature>(this);
        RegisterFeature<IModelCatalogFeature>(this);
        SetState(HarnessSessionState.Connected);
    }

    public Task<StartConversationResult> StartConversationAsync(
        StartConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var remoteId = $"memory-conversation-{Interlocked.Increment(ref nextConversationId)}";
        var address = new ConversationAddress(command.LocalConversationId, HarnessId.InMemory, remoteId);
        lock (stateGate)
        {
            conversations.Add(remoteId, new ConversationState(address));
        }

        Publish(new ConversationStartedEvent(HarnessId.InMemory, remoteId, DateTimeOffset.UtcNow));
        return Task.FromResult(new StartConversationResult(address));
    }

    public Task<ResumeConversationResult> ResumeConversationAsync(
        ResumeConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetRequired(command.Address);
        return Task.FromResult(new ResumeConversationResult(
            command.Address,
            SnapshotTurns(state)));
    }

    public Task<ReadConversationResult> ReadConversationAsync(
        ReadConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetRequired(command.Address);
        return Task.FromResult(new ReadConversationResult(
            command.Address,
            command.IncludeTurns ? SnapshotTurns(state) : []));
    }

    public Task SetConversationNameAsync(
        ConversationAddress address,
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A conversation name is required.", nameof(name));
        }

        lock (stateGate)
        {
            GetRequiredUnsafe(address).Name = name.Trim();
        }
        return Task.CompletedTask;
    }

    public Task SetConversationArchivedAsync(
        ConversationAddress address,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remoteId = RequireRemoteId(address);
        lock (stateGate)
        {
            GetRequiredUnsafe(address).IsArchived = archived;
        }
        Publish(new ConversationArchivedEvent(
            HarnessId.InMemory,
            remoteId,
            archived,
            DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task<ForkConversationResult> ForkConversationAsync(
        ForkConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remoteId = $"memory-conversation-{Interlocked.Increment(ref nextConversationId)}";
        var address = new ConversationAddress(command.LocalConversationId, HarnessId.InMemory, remoteId);
        lock (stateGate)
        {
            var source = GetRequiredUnsafe(command.Source);
            var lastTurnIndex = source.Turns.Count - 1;
            if (!string.IsNullOrWhiteSpace(command.LastTurnId))
            {
                lastTurnIndex = source.Turns.FindIndex(turn =>
                    string.Equals(turn.RemoteTurnId, command.LastTurnId, StringComparison.Ordinal));
                if (lastTurnIndex < 0)
                {
                    throw new KeyNotFoundException($"In-memory turn '{command.LastTurnId}' was not found.");
                }
                if (source.Turns[lastTurnIndex].Status is ConversationTurnStatus.Idle or ConversationTurnStatus.Running)
                {
                    throw new InvalidOperationException("An in-progress turn cannot be used as a fork boundary.");
                }
            }

            var clone = new ConversationState(address)
            {
                Name = string.IsNullOrWhiteSpace(source.Name) ? null : $"Fork of {source.Name}"
            };
            foreach (var turn in source.Turns.Take(lastTurnIndex + 1))
            {
                clone.Turns.Add(turn.Clone());
            }
            conversations.Add(remoteId, clone);
        }

        Publish(new ConversationStartedEvent(HarnessId.InMemory, remoteId, DateTimeOffset.UtcNow));
        return Task.FromResult(new ForkConversationResult(address));
    }

    public Task<RollbackConversationResult> RollbackConversationAsync(
        RollbackConversationCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.TurnCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "The rollback count cannot be negative.");
        }

        ConversationState state;
        lock (stateGate)
        {
            state = GetRequiredUnsafe(command.Address);
            if (command.TurnCount > state.Turns.Count)
            {
                throw new InvalidOperationException("The rollback count exceeds the available conversation history.");
            }
            if (command.TurnCount > 0)
            {
                state.Turns.RemoveRange(state.Turns.Count - command.TurnCount, command.TurnCount);
            }
        }
        return Task.FromResult(new RollbackConversationResult(command.Address, SnapshotTurns(state)));
    }

    public Task<StartTurnResult> StartTurnAsync(
        StartTurnCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remoteConversationId = RequireRemoteId(command.Address);
        var remoteTurnId = $"memory-turn-{Interlocked.Increment(ref nextTurnId)}";
        var turn = new TurnState(remoteTurnId, command.Prompt, DateTimeOffset.UtcNow);
        lock (stateGate)
        {
            var state = GetRequiredUnsafe(command.Address);
            if (state.IsArchived)
            {
                throw new InvalidOperationException("An archived conversation cannot start a turn.");
            }
            state.Turns.Add(turn);
        }

        Publish(new TurnStartedEvent(
            HarnessId.InMemory,
            remoteConversationId,
            remoteTurnId,
            turn.StartedAt));
        return Task.FromResult(new StartTurnResult(remoteTurnId));
    }

    public Task CancelTurnAsync(
        ConversationAddress address,
        string remoteTurnId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remoteConversationId = RequireRemoteId(address);
        lock (stateGate)
        {
            var turn = GetRequiredTurnUnsafe(address, remoteTurnId);
            if (turn.Status != ConversationTurnStatus.Running)
            {
                throw new InvalidOperationException("Only a running turn can be cancelled.");
            }
            turn.Status = ConversationTurnStatus.Cancelled;
            turn.CompletedAt = DateTimeOffset.UtcNow;
        }
        Publish(new TurnCompletedEvent(
            HarnessId.InMemory,
            remoteConversationId,
            remoteTurnId,
            ConversationTurnStatus.Cancelled,
            null,
            DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task<SteerTurnResult> SteerTurnAsync(
        SteerTurnCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remoteConversationId = RequireRemoteId(command.Address);
        var now = DateTimeOffset.UtcNow;
        ActivityItem activity;
        lock (stateGate)
        {
            var turn = GetRequiredTurnUnsafe(command.Address, command.ExpectedRemoteTurnId);
            if (turn.Status != ConversationTurnStatus.Running)
            {
                throw new InvalidOperationException("Only a running turn can be steered.");
            }
            activity = new ActivityItem(
                $"guidance-{turn.Activity.Count + 1}",
                ActivityKind.Information,
                "Guidance added",
                command.Prompt,
                now,
                true);
            turn.Activity.Add(activity);
        }
        Publish(new ActivityChangedEvent(
            HarnessId.InMemory,
            remoteConversationId,
            command.ExpectedRemoteTurnId,
            activity,
            now));
        return Task.FromResult(new SteerTurnResult(command.ExpectedRemoteTurnId));
    }

    public Task<IReadOnlyList<HarnessModelDescriptor>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<HarnessModelDescriptor> models = [new HarnessModelDescriptor(
            "memory-model",
            "Memory model",
            "Deterministic model exposed by the in-memory harness.",
            true,
            false,
            [HarnessInputModality.Text, HarnessInputModality.Image, HarnessInputModality.WorkspaceReference],
            [])];
        return Task.FromResult(models);
    }

    public void EmitAssistantText(
        ConversationAddress address,
        string remoteTurnId,
        string delta,
        string messageId = "assistant")
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        var remoteConversationId = RequireRemoteId(address);
        lock (stateGate)
        {
            var turn = GetRequiredTurnUnsafe(address, remoteTurnId);
            if (turn.Status != ConversationTurnStatus.Running)
            {
                throw new InvalidOperationException("Only a running turn can receive assistant text.");
            }
            turn.AssistantResponse.Append(delta);
        }
        Publish(new AssistantTextDeltaEvent(
            HarnessId.InMemory,
            remoteConversationId,
            remoteTurnId,
            messageId,
            delta,
            DateTimeOffset.UtcNow));
    }

    public void CompleteTurn(
        ConversationAddress address,
        string remoteTurnId,
        ConversationTurnStatus status = ConversationTurnStatus.Completed,
        string? error = null)
    {
        if (status is ConversationTurnStatus.Idle or ConversationTurnStatus.Running)
        {
            throw new ArgumentException("A terminal turn status is required.", nameof(status));
        }

        var remoteConversationId = RequireRemoteId(address);
        var completedAt = DateTimeOffset.UtcNow;
        lock (stateGate)
        {
            var turn = GetRequiredTurnUnsafe(address, remoteTurnId);
            if (turn.Status != ConversationTurnStatus.Running)
            {
                throw new InvalidOperationException("Only a running turn can be completed.");
            }
            turn.Status = status;
            turn.CompletedAt = completedAt;
        }
        Publish(new TurnCompletedEvent(
            HarnessId.InMemory,
            remoteConversationId,
            remoteTurnId,
            status,
            error,
            completedAt));
    }

    private ConversationState GetRequired(ConversationAddress address)
    {
        lock (stateGate)
        {
            return GetRequiredUnsafe(address);
        }
    }

    private ConversationState GetRequiredUnsafe(ConversationAddress address)
    {
        var remoteId = RequireRemoteId(address);
        return conversations.TryGetValue(remoteId, out var state)
            ? state
            : throw new KeyNotFoundException($"In-memory conversation '{remoteId}' was not found.");
    }

    private TurnState GetRequiredTurnUnsafe(ConversationAddress address, string remoteTurnId) =>
        GetRequiredUnsafe(address).Turns.FirstOrDefault(turn =>
            string.Equals(turn.RemoteTurnId, remoteTurnId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"In-memory turn '{remoteTurnId}' was not found.");

    private static string RequireRemoteId(ConversationAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.HarnessId != HarnessId.InMemory)
        {
            throw new InvalidOperationException(
                $"Conversation '{address.LocalId}' belongs to harness '{address.HarnessId}', not the in-memory harness.");
        }
        return !string.IsNullOrWhiteSpace(address.RemoteId)
            ? address.RemoteId
            : throw new InvalidOperationException(
                $"Conversation '{address.LocalId}' does not have an in-memory remote ID.");
    }

    private IReadOnlyList<ConversationTurnSnapshot> SnapshotTurns(ConversationState state)
    {
        lock (stateGate)
        {
            return state.Turns.Select(turn => turn.Snapshot()).ToArray();
        }
    }

    private sealed class ConversationState(ConversationAddress address)
    {
        public ConversationAddress Address { get; } = address;
        public string? Name { get; set; }
        public bool IsArchived { get; set; }
        public List<TurnState> Turns { get; } = [];
    }

    private sealed class TurnState(
        string remoteTurnId,
        string userPrompt,
        DateTimeOffset startedAt)
    {
        public string RemoteTurnId { get; } = remoteTurnId;
        public string UserPrompt { get; } = userPrompt;
        public StringBuilder AssistantResponse { get; } = new();
        public ConversationTurnStatus Status { get; set; } = ConversationTurnStatus.Running;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset? CompletedAt { get; set; }
        public List<ActivityItem> Activity { get; } = [];

        public ConversationTurnSnapshot Snapshot() => new(
            RemoteTurnId,
            UserPrompt,
            AssistantResponse.ToString(),
            Status,
            StartedAt,
            CompletedAt,
            false,
            Activity.ToArray(),
            [],
            []);

        public TurnState Clone()
        {
            var clone = new TurnState(RemoteTurnId, UserPrompt, StartedAt)
            {
                Status = Status,
                CompletedAt = CompletedAt
            };
            clone.AssistantResponse.Append(AssistantResponse);
            clone.Activity.AddRange(Activity);
            return clone;
        }
    }
}
