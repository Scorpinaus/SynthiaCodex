# Queued Follow-ups Implementation Plan

- **Prepared:** 19 July 2026
- **Repository baseline inspected:** `8686251`
- **Backlog target:** `feature_parity.md` -> P0 item 1
- **Scope:** Steer/Queue behavior during an active local Codex turn, a per-thread editable next-turn queue, and a persisted default preference
- **Implementation status:** Core feature complete and verified on 19 July 2026; remaining refinements are listed below

## Implementation completion record

The implemented slice delivers the core P0 outcome:

- Queue is the persisted default; `steer` and legacy `interrupt` settings remain compatible.
- The active composer exposes a context-sensitive primary Queue/Steer action and a visible one-shot inverse action. `Ctrl+Enter` uses the default and `Ctrl+Shift+Enter` uses the inverse without changing the preference.
- Every Codex thread owns an isolated, persisted queue. Items can be edited inline, reordered, manually sent or steered, and deleted.
- Queue entries capture their workspace, model, reasoning, service-tier, sandbox, approval, reviewer, and permission-profile request options.
- A successful `turn/completed` drains exactly the owning thread's FIFO head, including when a different thread is selected and running. Failed/cancelled turns leave pending work paused.
- Dispatch uses a per-thread semaphore, rechecks head identity and running state, persists `Starting` before the request, and removes the item only after app-server acknowledgement.
- Interrupted persisted `Starting` entries restore as `NeedsAttention`; restored queues never auto-send merely because the app starts.
- Archive and owned-worktree removal are disabled while any queued items remain.
- Queue bounds are enforced at 50 items, 64 KiB per item, and 256 KiB aggregate text per thread.
- The settings General card exposes the preference, the queue panel sits above the composer, and the new controls have accessible names and responsive-layout coverage.

TDD evidence was preserved in the implementation sequence: missing domain symbols failed first, integration tests failed before routing commands existed, WPF tests failed before the queue surface existed, and the archive guard assertion failed before the guard was added. The completed console suite contains 128 passing tests.

### Deliberate follow-up refinements

These refinements from the maximal design below were not required for the shipped core workflow and remain future hardening work:

- Interactive and queued starts still share orchestration inside `MainViewModel`; a separate `ConversationTurnCoordinator` was not extracted.
- Prompt and active guidance retain compatibility buffers instead of moving to one authoritative text property.
- The explicit inverse button replaces the proposed split-button menu.
- Enqueue-time resolved options are validated for workspace existence at dispatch, but model-catalog and managed-permission policy are not yet fetched and re-resolved immediately before a background start. Archive/worktree guards and captured thread-explicit options prevent selection drift, but policy revalidation remains the main reason parity is recorded as **Near**, not **Full**.
- Queue-specific structured telemetry and a live real-Codex disconnect smoke matrix remain to be added; automated ambiguous-state restoration and failure-pausing coverage is present.

## 1. Outcome

SynthiaCode should let a user keep typing while Codex is working and deliberately choose one of two outcomes:

- **Steer** appends the message to the currently active turn through `turn/steer`.
- **Queue** stores the message for a later turn and starts queued work in FIFO order after the current turn completes successfully.

The queue must belong to the Codex thread rather than the currently selected UI. A user may queue work in thread A, switch to thread B, and still have thread A drain safely when its active turn finishes. Queue items must be editable, reorderable, manually sendable, and deletable. Pending items and the global default behavior must survive app restart.

The implementation is complete when an active composer has an explicit primary Steer/Queue action, the alternate action is discoverable and keyboard-accessible, queue state is visible above the composer, and no race can start two turns for the same thread or silently lose/duplicate a queued prompt.

## 2. Research findings and parity contract

The current official behavior establishes the following target:

1. [ChatGPT's Codex workflow documentation](https://learn.chatgpt.com/docs/prompting) defines **Steer** as adding a message to the current run and **Queue** as saving it for the next run. It says queued messages appear above the composer and can be edited, reordered, sent, or deleted.
2. [ChatGPT desktop settings](https://learn.chatgpt.com/docs/reference/settings#general) exposes a General -> Follow-up behavior preference that chooses whether an active-run submission steers or waits for the next run.
3. [Codex IDE settings](https://learn.chatgpt.com/docs/developer-settings?surface=ide) use `queue` as the default, accept `steer`, treat legacy `interrupt` as `steer`, and use Cmd/Ctrl+Shift+Enter to invert the behavior for one message.
4. [Codex app-server](https://developers.openai.com/codex/app-server) gives the two required wire operations: `turn/start` creates a turn; `turn/steer` appends input to the active turn and requires a matching `expectedTurnId`. Steering does not create `turn/started` and does not accept model, working-directory, sandbox, or output overrides.
5. The checked-in schemas document `activeTurnNotSteerable` for turns such as review or manual compaction. A steer failure therefore must not discard text or silently change the message into a queued turn.

### Product decisions for SynthiaCode

Where the public behavior does not define recovery details, use these explicit rules:

- Default to **Queue**, matching the current Codex IDE default and minimizing accidental mid-run direction changes.
- Auto-dispatch only after a turn reaches `Completed`. Keep the queue paused after `Failed`, `Cancelled`, connection loss, or recovery-needed state so a failure does not cascade into more work.
- Do not auto-dispatch persisted work merely because the app starts and finds an idle thread. Restored queues are visible and require an explicit send; auto-draining resumes only as the consequence of a turn completed in the current connected session.
- Preserve FIFO order. Reordering changes the next item. A blocked or ambiguous head item pauses the queue instead of skipping ahead.
- Remove an item only after app-server acknowledges `turn/start` or `turn/steer`. An ambiguous transport failure is never retried automatically.
- A one-shot inverse affects only the submitted composer message and does not change the saved default.
- Queued messages use the turn options captured when they are enqueued, then revalidate those options against the current model catalog, workspace, and managed permission policy before dispatch. This makes background dispatch independent of whichever thread is selected later without bypassing newer restrictions.

## 3. Current implementation and gaps

### Existing strengths to reuse

- `MainViewModel.SteerTurnAsync` already sends `CodexTurnSteerRequest` with the active thread and expected turn IDs, then projects the guidance into the current conversation.
- `SubmitPromptAsync` already owns the pending-turn/start/bind sequence and reconciles a `turn/completed` notification that can arrive before the `turn/start` response.
- `CodexThreadWorkspace` routes notifications and active-turn state independently for parallel threads.
- `PersistedProjectThread`, `ThreadStore`, `AppSettingsSnapshot`, and the coalescing settings store already provide per-thread JSON persistence and immutable write snapshots.
- `TaskView` already keeps the composer fixed and swaps prompt/guidance surfaces according to `IsTurnRunning`.
- Model, reasoning, service tier, and permission choices already build a complete `CodexTurnStartRequest`.

### Gaps that the implementation must close

- `SubmitPromptAsync` rejects active turns, while the active composer is hard-wired to `SteerTurnAsync`; there is no behavior choice.
- Prompt and guidance use separate properties and buttons, making a single context-sensitive send command difficult.
- Turn submission reads `SelectedThread`, `SelectedProjectPath`, global composer options, and the currently displayed permission resolver. That is unsafe for draining a background thread.
- Queue state has no domain model, no per-thread workspace, no persistence, and no presentation model.
- `turn/completed` only marks a thread idle and saves transcript state; it has no guarded next-turn scheduling step.
- `Ctrl+Enter` is bound directly to `SubmitCommand`, so it cannot route to Queue or Steer while a turn is active and there is no one-shot inverse shortcut.
- Failure paths do not classify a rejected request versus an ambiguous request that may have reached app-server.

## 4. User experience specification

### 4.1 Composer behavior

Replace the idle prompt/active guidance split with one composer text surface and one context-sensitive send command. Compatibility wrapper properties may remain temporarily while tests and bindings migrate, but only one text buffer should be authoritative.

| Thread state | Default | Primary button | Primary result | Alternate result |
| --- | --- | --- | --- | --- |
| No active turn | Either | `Run task` or `Send follow-up` | Start a new turn | Not applicable |
| Active turn | Queue | `Queue follow-up` | Add a pending queue item | Steer this message now |
| Active turn | Steer | `Steer task` | Call `turn/steer` | Queue this message |
| Active non-steerable turn | Steer | `Steer task` | Preserve text and show Queue fallback on rejection | Queue succeeds locally |
| Idle with paused queue | Either | Normal send label | Send composer text as a new turn | Queue panel can send its head item |

The primary action has a small adjacent menu that shows both `Steer current turn` and `Queue for next turn`, marks the saved default, and displays the alternate shortcut. This makes the behavior usable without knowing the shortcut.

Keyboard behavior:

- Keep `Ctrl+Enter` as the normal composer send action.
- Add `Ctrl+Shift+Enter` as the one-shot inverse while a turn is active.
- When idle, `Ctrl+Shift+Enter` behaves like normal send because there is no meaningful alternate.
- Commands must use the composer control's input binding or a command parameter so the window-level shortcut cannot accidentally act on a stale, non-selected thread.

Clear the composer only after Queue insertion or app-server acknowledgement succeeds. On validation, steer, or turn-start failure, retain the exact draft and focus.

### 4.2 Queue panel

Render a compact `Queued follow-ups (N)` panel immediately above the composer and below the transcript. The panel is absent when the selected thread has no items.

Each row shows:

- queue position and a one- or two-line prompt preview;
- pending, starting, or needs-attention state;
- an accessible error/status message when blocked;
- `Edit`, `Move up`, `Move down`, `Send now`/`Steer now`, and `Delete` actions.

Interaction rules:

- Edit is inline with Save and Cancel. Save trims only leading/trailing whitespace, rejects an empty result, preserves the item ID, and moves no item.
- Move actions are disabled at the ends of the list and while that item is starting.
- While the thread is idle, only the head item exposes `Send now`; users reorder another item to the head first.
- While a steerable turn is active, a pending row may expose the explicit `Steer now` action. It removes the item only after `turn/steer` is acknowledged.
- Delete requires no destructive confirmation because it is a local unsent draft, but it must be keyboard reachable and announced through automation properties.
- A starting item is locked. Other pending items remain editable, but dispatch cannot pass the locked head.
- A needs-attention item offers Retry and Delete. Retry is always explicit and only enabled after the thread is known idle.

### 4.3 Settings

Add a **General** card to `DetailsView` above Appearance:

- Label: `Follow-up behavior`
- Options: `Queue for next turn` and `Steer current turn`
- Default: `Queue for next turn`
- Help text: explain that `Ctrl+Shift+Enter` uses the other behavior once.

The preference is application-wide, like the existing model and theme preferences. It changes the next active-run composer action immediately and does not mutate existing queue items.

## 5. Domain and persistence design

### 5.1 Types

Add the following Core types, with UI-free behavior that can be unit tested:

```csharp
public enum FollowUpBehavior
{
    Queue,
    Steer
}

public enum QueuedFollowUpState
{
    Pending,
    Starting,
    NeedsAttention
}

public sealed class QueuedFollowUpSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public QueuedFollowUpState State { get; set; }
    public string? LastError { get; set; }
    public QueuedTurnOptionsSnapshot Options { get; set; } = new();
}
```

`QueuedTurnOptionsSnapshot` records the enqueue-time workspace path, model ID, reasoning effort, service tier, logical permission mode, and custom profile ID. It must not persist a claim that managed policy is still valid. Before dispatch, resolve the logical permission choice again for the captured workspace and fail closed if its profile or reviewer is no longer allowed.

Use stable string IDs generated client-side. Normalize a deserialized `Starting` item to `NeedsAttention` because SynthiaCode cannot know whether a prior process died before or after app-server accepted the request.

### 5.2 Per-thread queue service

Add a `CodexFollowUpQueue` domain service with an `ObservableCollection<QueuedFollowUp>` and methods for:

- restore and snapshot;
- enqueue;
- edit;
- move;
- delete;
- mark starting;
- return to pending after a definite rejection;
- mark needs attention after an ambiguous failure;
- acknowledge and remove.

Add `CodexFollowUpQueueWorkspace`, keyed by thread ID, mirroring `CodexThreadWorkspace`. `TaskViewModel` binds to the selected thread's queue service, while background drain logic accesses the same instance directly by thread ID.

Bound persistence prevents `settings.json` growth. Start with 50 items per thread, 64 KiB UTF-8 per item, and 256 KiB aggregate queued text per thread. Put these values in named constants and surface a validation message rather than evicting older items. Revisit them when attachment input is added; attachments are explicitly out of this slice.

### 5.3 Settings changes

Add:

- `AppSettings.FollowUpBehavior`, serialized as `queue` or `steer`;
- `PersistedProjectThread.QueuedFollowUps`;
- `ProjectThreadState.QueuedFollowUps`.

Update every copy boundary:

- `AppSettingsSnapshot.Create` and its thread clone;
- `ThreadStore.ToPresentation`, `ToPersisted`, and `Upsert`;
- `MainViewModel`'s presentation/persisted synchronization paths.

Backward compatibility requires no destructive migration. Missing or unknown preference values map to Queue; legacy `interrupt` maps to Steer. Missing queue collections become empty. Deep-clone all item and option objects so coalesced saves cannot observe later mutations.

Persist immediately, through the existing coalescing store, after enqueue, edit, reorder, delete, state transition, and acknowledgement. Queue edits are user-authored data and should not wait for the next transcript save.

## 6. Dispatch architecture

### 6.1 Extract a thread-explicit turn starter

Refactor the turn-start portion of `SubmitPromptAsync` into a coordinator that takes an explicit immutable context instead of reading current selection:

```text
StartTurnAsync(
  threadId,
  CodexThreadService,
  workspacePath,
  prompt,
  validated turn options,
  dispatch source)
```

This common path must own:

1. connection and thread-loaded preconditions;
2. local pending-turn creation;
3. `CodexTurnStartRequest` construction;
4. app-server request and pending-turn binding;
5. `runningThreadIds`, `activeTurnIds`, and `CodexThreadWorkspace` registration;
6. persisted preview, activity, transcript, and queue acknowledgement;
7. response/notification reordering already handled by `BindPendingTurn`;
8. consistent logging and error classification.

Interactive send builds its context from the selected thread. Queue drain builds it from the queued snapshot and the persisted thread record. Neither path may call `GetActiveWorkspacePath`, `SelectedThread`, or the selected `TaskWorkspace` after the immutable context has been created.

This refactor is the main prerequisite for correct background queues. Do it before wiring automatic drain.

### 6.2 Per-thread serialization

Add one async dispatch gate per thread, owned by the coordinator or follow-up queue workspace. All interactive starts, queue starts, manual queue sends, and completion-triggered drains for a thread must pass through that gate.

Inside the gate, re-check:

- the app is not shutting down;
- the thread is loaded/resumable and not archived;
- no active turn ID or running state exists for that thread;
- the candidate is still the queue head and still Pending;
- the captured workspace exists;
- model/service-tier choice remains available;
- the permission choice resolves under current policy for that workspace.

The gate prevents duplicate starts from duplicate `turn/completed` notifications, a user clicking Send now while drain is scheduled, or a completion arriving before the previous start response finishes binding.

### 6.3 Completion-driven draining

After `ApplyNotification` fully reduces `turn/completed`, updates running dictionaries, persists the completed turn, and releases the per-thread active state, schedule `TryDrainQueueAsync(routedThreadId)` without blocking notification reduction.

Drain behavior:

1. Ignore non-Completed terminal states.
2. Read the routed thread's queue, not the selected queue.
3. Acquire the thread dispatch gate and repeat all preconditions.
4. Mark and persist the head as Starting before any protocol write.
5. Start exactly one turn.
6. On acknowledgement, remove/persist the item and release the gate. Do not start item two yet; its trigger is the new turn's own completion.
7. On a definite local validation or server rejection, return the item to Pending or NeedsAttention with a specific message and stop.
8. On timeout, connection replacement, EOF, or any failure where delivery is ambiguous, mark NeedsAttention and never retry automatically.

This yields one queued message per Codex turn and preserves the user's chosen order.

### 6.4 Steering

Route active composer Steer and queue-row Steer now through one method that captures `threadId` and `expectedTurnId` before awaiting.

- On acknowledgement, add guidance to that exact thread's `CodexThreadService`, clear/remove the source text, and persist the updated state.
- On `activeTurnNotSteerable` or a stale/no-active-turn error, retain the composer/queue item and show an explicit `Queue instead` path.
- Do not silently fall back to Queue because that changes when the model sees the user's instruction.
- Do not pass queued turn options to `turn/steer`; app-server does not accept them.

## 7. File-by-file implementation plan

### Phase A - Characterization and domain model

1. Add characterization tests around current follow-up start, active steering, notification-before-response reconciliation, parallel thread routing, and settings snapshotting.
2. Add `FollowUpBehavior`, queued item/snapshot types, `QueuedTurnOptionsSnapshot`, `CodexFollowUpQueue`, and `CodexFollowUpQueueWorkspace` under `SynthiaCode.Core`.
3. Define queue bounds, transition invariants, and a delivery-failure classification that distinguishes definite rejection from ambiguous transport failure.
4. Keep these types independent of WPF and app-server transport so all edit/reorder/state behavior is deterministic in unit tests.

Likely files:

- new `src/SynthiaCode.Core/Codex/AppServer/CodexFollowUpQueue.cs`
- new `src/SynthiaCode.Core/Codex/AppServer/CodexFollowUpQueueWorkspace.cs`
- new `src/SynthiaCode.Core/Codex/AppServer/QueuedTurnOptionsSnapshot.cs`

### Phase B - Persistence and restoration

1. Extend `AppSettings`, `PersistedProjectThread`, and `ProjectThreadState`.
2. Deep-copy queue state through `AppSettingsSnapshot` and `ThreadStore`.
3. Restore one queue service for every restored project thread next to `CodexThreadWorkspace.Restore`.
4. Normalize missing, invalid, or stale state during load; never auto-send on restoration.
5. Add a single queue-persistence helper in `MainViewModel` rather than duplicating copy/save logic across commands.

Files:

- `src/SynthiaCode.Core/Settings/AppSettings.cs`
- `src/SynthiaCode.Core/Settings/AppSettingsSnapshot.cs`
- `src/SynthiaCode.Core/Settings/ThreadStore.cs`
- `src/SynthiaCode.App/ViewModels/MainViewModel.cs`
- settings round-trip/coalescing tests in `src/SynthiaCode.Tests`

### Phase C - Thread-explicit dispatch coordinator

1. Introduce a `ConversationTurnCoordinator` or equivalent App service and move the stateful start sequence out of `MainViewModel.SubmitPromptAsync`.
2. Pass explicit thread/service/workspace/options context and callbacks for UI-thread state publication.
3. Add per-thread gates and start-source telemetry (`interactive`, `queued-auto`, `queued-manual`).
4. Adapt ordinary first turns and follow-up turns to the coordinator before enabling queues. Existing tests must stay green at this checkpoint.
5. Add option revalidation for queued dispatch, including per-workspace permission profile resolution and model/service-tier availability.

Likely files:

- new `src/SynthiaCode.App/Services/ConversationTurnCoordinator.cs`
- optional new interface for test doubles
- `src/SynthiaCode.App/ViewModels/MainViewModel.cs`
- `src/SynthiaCode.App/AppServices.cs`

### Phase D - View models, commands, and settings

1. In `TaskViewModel`, replace separate prompt/guidance ownership with one composer buffer and expose computed primary/alternate action labels.
2. Add a `FollowUpQueueViewModel` and `QueuedFollowUpItemViewModel` that wrap the selected domain queue without owning persistence or protocol behavior.
3. Replace direct Submit/Steer button routing with `SendComposerMessageCommand` plus an explicit alternate command.
4. Add queue edit, move, delete, manual send, and retry commands with accurate `CanExecute` updates on thread switch, turn state, queue state, connection state, and shutdown.
5. Load and save `FollowUpBehavior` in `MainViewModel`; bind it in the new General settings card.
6. Rebind the selected queue whenever `SelectThread` changes, while background coordinators retain access to other queue services.

Files:

- `src/SynthiaCode.App/ViewModels/TaskViewModel.cs`
- new `src/SynthiaCode.App/ViewModels/FollowUpQueueViewModel.cs`
- new `src/SynthiaCode.App/ViewModels/QueuedFollowUpItemViewModel.cs`
- `src/SynthiaCode.App/ViewModels/MainViewModel.cs`
- `src/SynthiaCode.App/Views/DetailsView.xaml`

### Phase E - WPF presentation and accessibility

1. Add the queue panel above the composer in `TaskView.xaml`, preserving the transcript's recycling virtualization and fixed-composer layout.
2. Add inline edit and compact row actions with theme-owned normal, hover, pressed, focus, disabled, error, and needs-attention states.
3. Add the primary action menu and alternate-action shortcut hint.
4. Update `TaskView.FocusComposer` for the unified text box and restore focus after successful queue actions or recoverable errors.
5. Replace the window-level direct Submit binding with context-aware normal and alternate send bindings.
6. Add automation names, keyboard traversal, live status announcements, minimum 32px action targets, wrapping, and dark/light contrast checks.

Files:

- `src/SynthiaCode.App/Views/TaskView.xaml`
- `src/SynthiaCode.App/Views/TaskView.xaml.cs`
- `src/SynthiaCode.App/MainWindow.xaml`
- `src/SynthiaCode.App/Themes/DarkTheme.xaml`
- `src/SynthiaCode.App/Themes/LightTheme.xaml`
- `src/SynthiaCode.App/Themes/TransientSurfaces.xaml` only if a popup/menu style is needed

### Phase F - Completion drain, recovery, and observability

1. Schedule a routed, per-thread drain after successful `turn/completed` reduction.
2. Persist Starting before protocol write, acknowledge/remove after response, and convert ambiguous states to NeedsAttention.
3. On app-server crash/reconnect, clear active-turn maps as today but preserve every queue; convert any Starting item to NeedsAttention.
4. On shutdown, do not begin a new queued turn. Let the existing shutdown flow cancel active turns and persist queues.
5. Block archive/worktree removal while a queue item is Starting. Allow archive with Pending items only after warning that the queue will remain paused, or initially disable archive until the queue is empty to keep the first release simple and safe.
6. Log queue length, operation, thread ID, item ID, dispatch source, elapsed time, and failure class. Never log prompt text.

Recommended first-release archive rule: disable archive and worktree removal while any queued items exist, with a status message directing the user to send or delete them. This prevents an invisible queue from targeting an archived or removed workspace and can be relaxed later with a dedicated confirmation flow.

### Phase G - Documentation and parity update

1. Update `docs/current-architecture.md` with the thread-owned queue, dispatch gate, persistence, and recovery flow.
2. Update `README.md` shortcuts and settings behavior.
3. Add a focused phase completion note if the repository continues the `phase_5*` convention.
4. Only after implementation and verification, change the feature-parity row and P0 backlog entry in `feature_parity.md` from Missing to Full or Near, with any remaining limitation stated precisely.

## 8. Test plan

Create `QueuedFollowUpTests.cs` and register it in the existing test harness. Prefer pure domain tests for state transitions and focused WPF tests for presentation.

### Domain and persistence

- Missing/unknown preference defaults to Queue; `steer` and legacy `interrupt` parse as Steer.
- Enqueue trims the prompt, rejects empty/over-limit input, and preserves order.
- Edit preserves ID and position; move up/down and delete produce the expected snapshots.
- Bounds reject new items without evicting old ones.
- Starting -> NeedsAttention normalization occurs on restore.
- Two threads restore isolated queues with identical prompt text but distinct IDs.
- JSON round trip and `AppSettingsSnapshot` deep-copy queue items and nested option snapshots.
- A burst of queue edits still coalesces physical settings writes and persists the newest order.

### Composer routing

- Idle normal and alternate sends both start a turn.
- Active + Queue default adds locally and makes no app-server call.
- Active + Steer default calls `turn/steer` and adds guidance without creating a conversation turn.
- `Ctrl+Shift+Enter` inverts active behavior once and leaves the preference unchanged.
- Successful action clears text; every rejected or ambiguous action preserves it.
- Non-steerable/stale-turn rejection exposes Queue instead and never silently queues.

### Dispatch and concurrency

- A successful completion starts exactly the head item and only one turn.
- Reordered head is the next dispatched item.
- Failed/cancelled/interrupted/recovery-needed turns do not auto-drain.
- Duplicate completion notifications do not duplicate `turn/start`.
- Completion before start response is still reconciled and does not cause a second start.
- Manual Send now racing scheduled drain produces one request.
- Thread A drains correctly while thread B is selected; UI state for B is untouched.
- Two different threads may drain independently.
- Removing the item happens only after acknowledgement.
- Definite server rejection leaves a retryable item; connection loss after send marks NeedsAttention and does not auto-retry.
- Shutdown and archive/worktree guards prevent new queued dispatch.
- Captured workspace/model/permission options are used for background dispatch and fail closed when no longer valid.

### WPF and accessibility

- Queue panel is immediately above the composer and hidden at count zero.
- Long queued prompts wrap without horizontal overflow at minimum window width.
- Transcript scroll/follow-latest behavior remains unchanged as queue height changes.
- Buttons, edit controls, state text, and errors have automation names and keyboard order.
- Move buttons disable correctly at list boundaries.
- Primary label/menu updates immediately with active state and preference.
- `Ctrl+Enter` and `Ctrl+Shift+Enter` route correctly from the composer.
- Dark and light states meet existing contrast and focus-visibility standards.

### Manual smoke matrix

Run the portable WPF app against a real Codex app-server and verify:

1. Queue three messages during a run, edit the second, move the third first, and observe three separate ordered turns.
2. Switch threads before completion and confirm the originating thread drains in the background.
3. Steer once with Queue as default, using the alternate shortcut, and confirm no new turn is created.
4. Trigger or simulate a non-steerable active turn and confirm the message is retained with Queue instead.
5. Cancel and fail a turn with pending messages; verify the queue pauses.
6. Restart with pending and needs-attention items; verify nothing auto-sends and all controls remain usable.
7. Disconnect during a queued start; verify there is no automatic retry after reconnect.

## 9. Acceptance gates

The feature is ready only when all of the following are true:

- Queue is the backward-compatible default and the setting round-trips.
- The primary and alternate active-run actions are visible, keyboard accessible, and accurately labelled.
- Queue items are per thread, persisted, editable, reorderable, manually sendable/steerable, and deletable.
- Successful current-session completions drain one item at a time in user-defined order.
- Failure, cancellation, restart, disconnect, archive, worktree removal, and shutdown cannot silently lose or duplicate queued work.
- Background-thread drain never reads selected-thread workspace or options after dispatch context capture.
- Existing first-turn, follow-up, steering, cancellation, multi-thread, model, permission, persistence, and responsive-layout tests remain green.
- New queue tests, `dotnet build SynthiaCode.sln`, the complete test executable, and `git diff --check` pass.
- Architecture, shortcuts, settings, and feature-parity documentation reflect the shipped behavior.

## 10. Non-goals

Keep this P0 slice bounded. It does not include:

- file/image attachments in queued input;
- queueing slash-command-specific structured parts, skills, or review targets beyond their current text behavior;
- per-item model or permission editing UI after enqueue (the captured summary may be shown read-only);
- queue synchronization across machines or cloud execution;
- permanent queue history after an item becomes a turn;
- automatic retry of ambiguous deliveries;
- subagent queues or controls;
- a general background task manager or OS completion notifications.

These can build on the thread-owned queue and dispatch coordinator later without changing the first-release safety contract.

## 11. Main risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Duplicate queued turns after notification/request races | Per-thread async gate, head identity re-check, Starting persistence, and no automatic retry after ambiguous delivery |
| Wrong thread/workspace/options after the user switches chats | Immutable thread-explicit dispatch context plus enqueue-time option snapshot and dispatch-time revalidation |
| Managed permission change is bypassed by a stored snapshot | Persist logical intent, rerun the fail-closed permission resolver for the captured workspace before every queued `turn/start` |
| Queue disappears or reorders after restart | Immediate coalesced saves and deep-copy coverage at every settings boundary |
| A failure cascades through the remaining queue | Auto-drain only from Completed; stop at the first blocked/needs-attention item |
| Refactor regresses ordinary follow-ups | Move existing submission through the coordinator first and keep characterization tests green before enabling automatic drain |
| Queue panel crowds the transcript | Compact collapsed header, bounded visible height with internal scrolling, responsive WPF layout tests |
| Archived/removed worktree retains invisible executable work | First release disables archive/removal while queue is non-empty and never auto-runs restored queues |

## 12. Recommended implementation order

Implement in this dependency order:

1. Characterization tests.
2. Queue domain types and per-thread workspace.
3. Persistence and restore.
4. Thread-explicit turn coordinator and per-thread gates.
5. Ordinary interactive turn migration to the coordinator.
6. Composer routing, default preference, and alternate action.
7. Queue panel and edit/reorder/delete/manual actions.
8. Completion-driven automatic drain.
9. Recovery, ambiguous-delivery handling, archive/worktree/shutdown guards.
10. Full automated and live WPF verification.
11. Architecture, README, and feature-parity updates.

This order keeps each checkpoint independently testable and addresses the highest-risk concurrency boundary before adding automatic behavior.
