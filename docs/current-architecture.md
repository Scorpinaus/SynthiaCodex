# SynthiaCode: Current Architecture

**Recorded:** 6 August 2026
**Release:** 0.1.0
**Phase:** Modern WPF redesign through Phase 21, with product extensions through generated-image, attachment, prompt-editing, chat-management, queued-dispatch hardening, Goal mode, and multi-folder local projects
**Purpose:** Describe the current architecture, presentation shell, runtime and persistence boundaries, implemented desktop workflows, and release verification baseline.

## System shape

The solution is a Windows-only WPF desktop application with four projects:

| Project | Responsibility | Dependencies |
| --- | --- | --- |
| `SynthiaCode.Core` | App-neutral contracts, settings records, thread state, Codex notification state, Git/worktree/terminal models | None |
| `SynthiaCode.Infrastructure` | Codex CLI/app-server transport, JSON settings, Git, worktrees, ConPTY terminal, auth, logging | Core |
| `SynthiaCode.App` | WPF composition root, window, theme resources, UI services, commands, presentation state | Core and Infrastructure |
| `SynthiaCode.Tests` | xUnit-discovered behavioral and integration-style test suite; the executable entry point remains only as a UTF-8 transport fixture | App, Core, and Infrastructure |

The intended dependency direction is therefore:

```text
Tests ────────────────> App ───────────────> Infrastructure ─────> Core
  └────────────────────┴─────────────────────────────────────────> Core
```

`AppServices.Create()` is the manual composition root. It constructs the concrete infrastructure and application-workflow services, then supplies them to `MainViewModel`. There is no external dependency-injection container.

## Startup and shutdown

1. `App.OnStartup` enforces a single process with a named mutex.
2. `AppServices.Create` creates settings, Codex, auth, Git, worktree, terminal, theme, picker, interaction, and logging services.
3. `MainWindow` is constructed and shown before asynchronous initialization begins.
4. `MainViewModel.InitializeAsync` loads settings, restores shell preferences, applies the theme, restores recent-project metadata, and runs Codex/auth diagnostics.
5. App-server warm-up starts in the background after the shell reaches Ready.
6. Window closing calls `MainViewModel.ShutdownAsync`, which cancels active turns, disposes terminal sessions, saves the active thread, and disposes the app-server client.
7. `App.OnExit` performs a final idempotent disposal and releases the mutex.

This ordering deliberately keeps shell visibility independent of Codex app-server startup.

## Presentation ownership

`MainViewModel` is the shell coordinator. `ProjectThreadViewModel`, `TaskViewModel`, `TerminalViewModel`, `DiagnosticsViewModel`, `AccountViewModel`, `GitViewModel`, `SkillsViewModel`, and `EffectiveCodexSettingsViewModel` own feature presentation state and commands. The shell supplies explicit operation delegates or immutable-on-read context callbacks and receives status/selection callbacks; feature view models do not reference or control one another.

App-server lifecycle is exposed to presentation through `IAppServerSessionCoordinator`. Its implementation owns `CodexAppServerClient`, process transport startup, reconnect serialization, batched notifications, typed app-server operations, health transitions, and disposal. Protocol request/response JSON and delta-payload batching remain in the Codex Core/Infrastructure boundary rather than WPF presentation.

`App.xaml` is now a resource-composition root. Theme colors live in matching light, graphite-dark, and system-color high-contrast dictionaries; foundations, typography, vector icons, buttons, inputs, navigation, and transient controls are split by concern. Feature views consume semantic dynamic resources, so changing theme dictionaries does not recreate the window or any feature view model.

`MainWindow.xaml` uses `WindowChrome` and native `SystemCommands` for dragging, system-menu, minimize, maximize/restore, close, resize, and Windows 11 maximize-button hit testing. Its primary layout is no longer a workspace `TabControl`: the project rail, conversation, lower terminal dock, and changes/settings inspector are composed simultaneously. At compact widths the rail and inspector become mutually exclusive drawers behind a scrim; the approval host remains at the highest application-owned z-order. `Esc` dismisses the active side drawer but never dismisses UI behind a pending approval.

`MainViewModel.UpdateViewportWidth` projects exclusive compact (800–1099 px), medium (1100–1439 px), and wide (1440 px and above) shell states. The rail is persistent in medium/wide states, the inspector is persistent only when enabled in wide state, and the terminal remains a bounded lower row unless maximized within the center workspace. Existing persisted open/closed preferences remain the source of truth.

Feature controls bind directly to their existing feature view models. Timeline, raw-event, thread, diagnostic, recent-project, and changed-file lists use recycling virtualization and content scrolling. The transcript keeps pixel scrolling for variable-height turns and uses a dedicated 72 px near-latest coordinator that pauses following after deliberate upward navigation and resets follow state when chat identity changes; terminal output remains coalesced through the existing 50 ms presentation path.

`ProjectThreadViewModel` also owns the unified project/thread navigation projection. `ProjectNavigationItemViewModel` groups presentation threads by normalized primary project path, tracks project disclosure and running summaries, and preserves the existing active-project `Threads` collection as a compatibility surface. `RecentProject.Path` remains the primary-folder identity while an optional persisted secondary-folder collection supplies a normalized primary-first root set; legacy single-folder records remain valid.

`ProjectThreadView` renders project-name disclosure rows with their project-scoped threads and empty state; filesystem paths are retained for routing but omitted from the navigation UI. A project-row `+` creates a current-checkout thread immediately; isolated worktree creation and Edit project folders are retained in the project's advanced menu. The native folder editor adds/removes secondary roots and changes the primary only while that project's chats are idle. Only the selected project is expanded automatically. Selecting an existing project refreshes its recent timestamp in place rather than reordering the hierarchy. Completed and idle thread pills are intentionally suppressed; running, failed, cancelled, and archived states remain visible. Selected-thread lifecycle operations are exposed through high-contrast contextual action buttons and fully theme-aware context-menu surfaces. The workspace heading above Task and Changes contains only the selected thread title.

Projectless General chats use the same thread lifecycle, transcript, search, pin/archive, rename, and persistence paths without inventing a filesystem CWD. Sidebar chat management and cross-chat search operate on the persisted thread projection, while find-in-chat remains scoped to the selected transcript. User prompts can be copied directly, edited with rollback of later turns, or used as stable fork points; all three actions preserve the original submitted text in turn history.

`UserAccountView` is anchored in an `Auto` row below the independently scrolling project list. Its upward-opening flyout presents the ChatGPT email-derived identity, plan, remaining rate-limit windows, reset times, optional credits, Settings, and authentication actions. `AccountViewModel` reads typed `account/read` and `account/rateLimits/read` results, consumes account notifications before thread routing, keeps account data in memory only, and treats refresh failures as nonfatal. The app-server does not currently expose an authoritative display name or avatar, so the UI uses the email local part and generated initials.

`MainViewModel.cs` remains the shell coordinator. It owns UI validation, project/thread selection, cross-feature event routing, shell layout, theme/status projection, app-server warm-up, notification marshaling, and shutdown ordering.

Application workflows no longer use `MainViewModel` as a hidden service layer:

- `ThreadLifecycleUseCaseService` owns durable create, resume/fallback, fork, archive, rename, delete, pin, and worktree transitions;
- `TurnExecutionUseCaseService` owns start, edit/rollback, steer, cancel, automatic-title, and conversation state transitions;
- `CodeReviewUseCaseService` owns pending, bound, failed, and inline-thread validation for dedicated review turns;
- `FollowUpQueueUseCaseService` owns queue mutation, durable queue snapshots, per-thread serialized dispatch, recovery, removal, and disposal;
- `ThreadStatePersistenceUseCaseService` owns bounded transcript and active-thread snapshots;
- `ConversationWorkflowController` is limited to runtime conversation identity, notification routing, and detached snapshots.

`MainViewModel` composes resolved request inputs and projects returned snapshots into feature view models. Concrete app-server lifecycle, terminal lifetime, diagnostics/auth operations, Git operations, and the five application use-case boundaries are owned outside presentation.

## Runtime flows

### Codex task

```text
Composer command
  -> TaskViewModel command
  -> MainViewModel validation and request composition
  -> TurnExecutionUseCaseService
  -> IAppServerSessionCoordinator
  -> CodexAppServerClient (Infrastructure)
  -> app-server process transport
  -> JSON-RPC notifications
  -> 50 ms Infrastructure notification batcher for agent-message deltas
  -> captured UI SynchronizationContext
  -> MainViewModel notification projection
  -> ConversationWorkflowController / CodexThreadWorkspace
  -> observable response, activity, and raw-event surfaces
  -> ThreadStatePersistenceUseCaseService / settings.json
```

Protocol request construction, response correlation, parsing, and transport failure handling remain inside Infrastructure. Core owns app-server request/result records and notification-derived thread state.

### Dedicated code review

The composer **Review** action and an exact `/review` submission share one native target picker. `GitService` validates the selected repository and supplies stable branch and recent-commit choices; the picker also exposes uncommitted changes and custom instructions. `CodeReviewUseCaseService` creates the pending turn, sends typed inline `review/start` through the existing app-server coordinator, requires the returned `reviewThreadId` to match the current chat, and binds the returned turn ID.

`enteredReviewMode` and `exitedReviewMode` are routed as protocol-specific lifecycle items so their review scope and final Markdown findings are not lost through the harness-neutral activity projection. `CodexThreadService` labels the turn as a code review, persists its scope and findings, and restores the same state from app-server history. The existing turn-completion and cancellation paths remain authoritative. Detached delivery, structured severity/file-line records, inline diff comments, and per-hunk Git actions remain outside this slice.

### Server-request approvals and permission modes

`CodexAppServerClient` classifies app-server messages as outgoing responses, notifications, or incoming server requests. Incoming request IDs retain their integer or string representation. Command-execution, file-change, and permission requests are parsed to typed Core models; malformed and unsupported requests receive deterministic JSON-RPC errors so the server is never left waiting. The client maintains separate outgoing and incoming registries and permits exactly one successful response for each incoming request.

`AppServerSessionCoordinator` attaches request handlers to the active client generation and rejects responses from a replaced connection. `MainViewModel` marshals requests to the captured UI context and owns `ApprovalQueueViewModel`, which serializes prompts globally. `serverRequest/resolved`, reconnect, and shutdown events invalidate stale prompts. Permission responses are constructed by intersecting the selected top-level permission groups with the immutable original request.

`ExecutionPolicyViewModel` owns the three user-facing permission modes and delegates their exact mapping to `CodexPermissionModeResolver`. Ask for approval and Approve for me share the `:workspace` permission profile and `on-request` policy; their reviewers are `user` and `auto_review`, respectively. Custom omits all boundary/policy/reviewer overrides for the configured default or sends only a selected named profile. `permissionProfile/list` is scoped to the project working directory, paginated, deduplicated in server order, and guarded against stale app-server generations. Method-not-found becomes an explicit legacy capability state, where Ask uses `workspace-write` with the same policy and reviewer.

Settings persist the mode and optional profile ID through `AppSettingsPermissionMigration`. Existing workspace-write/on-request settings migrate to Ask, explicit inheritance migrates to Custom, and nonstandard legacy combinations remain a distinct compatibility state without broadening access. `config/read` and `configRequirements/read` supply effective and managed context; reviewer and profile restrictions are enforced alongside legacy sandbox/policy restrictions. Unknown modes, stale profiles, and disallowed selections fail closed. The single resolved result is passed consistently to thread start, resume, fork, replacement-thread, and every turn-start request, and the client rejects any request that combines a permission profile with a legacy sandbox before writing to transport.

### Shared Codex configuration and provenance

`SharedCodexConfigurationService` owns the two editable files in the isolated runtime home: `AGENTS.md` and `config.toml`. It reads strict UTF-8 up to 512 KiB, fingerprints loaded bytes with SHA-256, rejects stale revisions, writes a same-directory temporary file through to disk, and atomically replaces the destination. Missing shared files are materialized only for an explicit save or external-editor action. File contents never enter structured logs.

For the active workspace, the service locates the nearest Git root and enumerates existing `AGENTS.md` and `.codex/config.toml` files from root to leaf after the two shared sources. `CodexConfigurationViewModel` keeps dirty editor text during automatic Settings refresh, exposes explicit refresh/save conflict recovery, and supplies Editor/Explorer commands for every provenance record. Project-owned sources are provenance-only in the built-in editor and deep-link to the user's external tools.

### Skills and effective Codex settings

Phase 6A reuses the initialized `AppServerSessionCoordinator`; it does not create another process, transport, client, or settings store. Infrastructure owns typed serialization and tolerant parsing for `skills/list`, `skills/config/write`, and a separate allowlisted `config/read` projection. A `-32601` response becomes an unsupported-capability result and leaves the shared app-server connection healthy.

`SkillsViewModel` resolves the same active General, project, or worktree CWD used for task execution. Absolute normalized `SKILL.md` paths are row identity, so duplicate names remain distinct. The source collection retains valid rows when the same CWD reports load errors; search and scope filtering project that source into a recycling-virtualized list. Enable/disable sends only path and desired state, applies the returned `effectiveEnabled`, then forces discovery to reconcile the complete row.

`skills/changed` is an invalidation signal rather than row data. The view model debounces it, refreshes only while Settings is active, and otherwise marks the cache stale. Refresh generations and cancellation prevent an older CWD from replacing a newer context. The shell marks both Skills and effective settings stale when the active context changes and disposes the Skills notification subscription before the shared coordinator shuts down.

`EffectiveCodexSettingsViewModel` presents model, provider, reasoning effort, service tier, profile, sandbox mode, approval policy/reviewer, web search, and workspace network access with available origin labels. Infrastructure discards every other configuration field before constructing the Core result, so raw JSON, MCP headers, environment values, and secrets never enter presentation state or application logs. This read-only overview coexists with the existing explicit shared-file editor, model selectors, and permission resolver.

```text
app-server request (method + id)
  -> CodexAppServerClient typed parser and incoming registry
  -> AppServerSessionCoordinator active-generation check
  -> MainViewModel UI-context dispatch
  -> ApprovalQueueViewModel / ApprovalPromptView
  -> selected decision or permission subset
  -> coordinator generation check
  -> client exact-once JSON response with original id type
```

High-frequency agent-message deltas are grouped by thread, turn, and item before UI dispatch. Any non-delta event first flushes pending text, which preserves ordering for completion, error, tool, and lifecycle notifications. Idle deltas flush on the batching timer so streaming remains visibly progressive.

### Multi-turn conversation state

`CodexThreadWorkspace` owns one `CodexThreadService` per app-server thread and a turn-to-thread routing index. Each service exposes a bounded chronological collection of `CodexConversationTurn` objects. A turn owns its user prompt, assistant response, activity collection, status, and start/completion timestamps; the older singular final-response and timeline properties remain compatibility projections.

Submitting a follow-up calls `turn/start` with the existing thread ID. A pending local turn is created immediately, then bound to the returned app-server turn ID. Binding and notification reduction share a state gate and reconcile an already-observed turn, so notifications that arrive before the request response do not create duplicate turns or lose the response.

Thread selection and resume use typed `thread/read`/resume results with `includeTurns: true`. Canonical app-server user and assistant messages are reconciled with local turn snapshots, while richer local activity remains attached to its matching turn. If the server cannot provide history, the local snapshot remains usable. Legacy records containing only a preview and final response synthesize one visible completed turn.

`TaskView` presents the collection as a recycling-virtualized chronological transcript. Each turn has a distinct outer boundary, a user-message surface, and an assistant-message surface whose activity expander precedes the final answer, so related work and outcome share one card while adjacent turns remain visually independent. Dedicated review turns add a non-color Code review label and scope above the same Markdown answer surface. The app-server stream is retained in bounded raw-event and diagnostic collections, while visible turn activity is an allowlisted projection of commentary, commands, file changes, tools, searches, plans, collaboration, review lifecycle, guidance, and actionable errors. Activity projection preserves complete user-facing detail, lists every reported changed path, and prefers structured web-search queries, page URLs, and find-in-page data over compatibility summaries. Stable item keys consolidate start, progress, and completion into one row; lifecycle, token, output-delta, reasoning, final-answer, and unknown notifications remain diagnostics-only. It follows live output while the viewport is near the bottom, exposes a Jump to latest action after manual scrolling, hides empty activity, collapses historical activity, wraps long detail without horizontal transcript overflow, and keeps the composer fixed. Assistant text uses the native Markdown renderer for common technical prose, nested lists, tables, footnotes, definitions, safe links/images/HTML, syntax-highlighted fenced code, and per-block copying; unsafe browser content remains visible and inert. Generated-image completion events are deduplicated into safe local previews and persist through restore and fork. The first action is labelled Run task; subsequent submissions are labelled Send follow-up. During an active turn, the same composer becomes the guidance input.

The composer footer owns one compact model summary instead of a permanent Run settings expander. Its anchored flyout drills into the authenticated model catalog and filters reasoning efforts from the selected model's advertised capabilities. Fast is a catalog-provided service tier rather than a model alias and is enabled only when the selected model advertises `fast`. `account/read.planType` is presentation context only; the visible `model/list` result is the effective capability source for ChatGPT and API-key sessions. Model, reasoning, and inherit/standard/fast preferences persist independently of account entitlements, are revalidated after catalog refresh, and are disabled while the selected turn is active.

### Multi-folder project roots

`RecentProjectService.UpdateProjectFolders` validates a distinct existing folder set before mutation, keeps the selected primary first, rejects collision with another saved project's primary, and migrates matching project-thread and composer-draft scopes in one settings update. Non-worktree chats move to the new primary; worktree chats retain their worktree directory. Legacy attachment references are stamped with their owning pre-migration root before paths change, and queued-turn root snapshots are refreshed.

Conversation start, resume, fork, and turn commands carry a harness-neutral workspace-root list. The Codex adapter keeps the effective primary/worktree as `cwd`, describes secondary folders as path data while preserving primary-only automatic `AGENTS.md`, skill, and `config.toml` discovery, and serializes the roots as `sandboxPolicy.writableRoots` only for explicit `workspaceWrite` turns. Read-only, unrestricted, config-owned, profile, approval, and managed-policy semantics are not broadened.

`GitViewModel` scans the active worktree plus attached project folders, deduplicates repositories by resolved root, keeps the effective primary first, and projects one selected repository into the existing changed-file/diff/commit state. Every Git mutation and Editor/Explorer deep link uses that selected root.

### Attachments and generated-image editing

`AttachmentDraftOrchestrationService` coordinates picker, clipboard, drag/drop, draft, queue, and turn attachment state. `WorkspaceAttachmentResolver` accepts safe file and folder mentions contained by any attached project root, persists the deepest owning root, and rejects roots, sibling-prefix escapes, wildcards, alternate data streams, detached roots, and reparse escapes. `LocalAttachmentStore` imports external images, files, and deterministic folder snapshots into bounded managed storage without persisting their original external paths. Managed images serialize as `localImage`; managed files and folders serialize as `mention` inputs. Attachment schema v3 persists generic metadata while retaining legacy image compatibility.

Startup rehydrates managed attachment paths and performs reference-aware staging/orphan cleanup. Queued and background sends retain the owning workspace and immutable attachment/options snapshot. The generated-image viewer and editor reuse `LocalImageResourcePolicy`; edits can target the whole image or an exported marked region, with bounded drawing controls and a guide image supplied to image generation.

### Queued follow-ups

`CodexFollowUpQueueWorkspace` owns one `CodexFollowUpQueue` per app-server thread, parallel to `CodexThreadWorkspace`. The selected `TaskViewModel` binds to that thread's observable queue, while completion handling accesses queues by routed thread ID so thread A can dispatch safely while thread B remains selected and running.

During an active turn, the application-wide `FollowUpBehavior` setting chooses Queue or Steer as the primary composer action. Queue is the default; `Ctrl+Shift+Enter` and the adjacent alternate button invert the behavior for one message without changing the setting. Queue insertion is local and clears the composer only after the item is persisted. Steer continues to use `turn/steer` with the captured thread and expected turn IDs and clears text only after acknowledgement.

Each queue item has a stable ID, text, timestamps, a `Pending`, `Starting`, or `NeedsAttention` state, and a deep-copied turn-options snapshot. Queues enforce 50 items, 64 KiB per item, and 256 KiB aggregate text per thread. `AppSettingsSnapshot` and `ThreadStore` deep-copy the queue and nested options, and every enqueue, edit, reorder, delete, state transition, and acknowledgement is persisted through the coalescing settings store. A persisted `Starting` item restores as `NeedsAttention`; restored pending work is presented but never dispatched solely because startup found an idle thread.

Successful `turn/completed` reduction saves the owning transcript and calls the per-thread drain path. A semaphore serializes dispatch for that thread, then running state, head identity, and item state are checked again. The head is persisted as `Starting` before `turn/start`; it is removed only after the response acknowledges the new turn. A failed/cancelled completion does not drain, and start failures leave the head as `NeedsAttention` without automatic retry. Archive and assistant-owned worktree removal remain disabled until the queue is empty.

```text
active composer -> Queue -> per-thread queue -> immediate settings snapshot
turn/completed (Completed only)
  -> routed thread service save
  -> per-thread dispatch gate
  -> persist head as Starting
  -> turn/start with captured thread/workspace/options
  -> acknowledge and remove, or retain as NeedsAttention
```

Background dispatch validates that the captured workspace still exists and never reads the currently selected thread. Immediately before starting the queued turn it refreshes model availability, effective configuration, and managed permission requirements, then resolves the saved options against that current capability state. A refresh failure leaves the item in `NeedsAttention` rather than sending with stale assumptions.

### Terminal

```text
TerminalViewModel
  -> ITerminalService
  -> WindowsConPtyTerminalService
  -> PowerShell process
  -> OutputReceived events
  -> thread-safe per-thread circular character buffer
  -> one scheduled presentation per 50 ms batch
  -> captured SynchronizationContext
  -> TerminalOutput snapshot property
  -> WPF TextBox
```

Each terminal buffer is capped at 250,000 characters. Phase 5B replaced the original per-chunk dispatcher post and full-string recreation with a circular buffer and a coalesced 50 ms presentation path. Session-end telemetry records received chunks, characters, presentation updates, retained characters, and duration.

### Persistence

The composition root exposes a `CoalescingSettingsStore` around `JsonSettingsStore`. Thread transcript saves run through `ThreadStatePersistenceUseCaseService`; queued-follow-up saves run through `FollowUpQueueUseCaseService`; lifecycle transactions persist through `ThreadLifecycleUseCaseService`. Shell preferences remain narrow `MainViewModel` saves. Requests arriving within 75 ms are collapsed into one physical write containing the latest immutable deep snapshot.

`JsonSettingsStore` serializes writes through a gate, flushes the complete settings graph to a write-through `settings.json.tmp`, and replaces `settings.json` with an overwrite move. Loading promotes a valid newer temporary file when an interrupted save left the primary missing or corrupt.

Persisted and presented thread state are separate. `AppSettings.ProjectThreads` contains storage-only `PersistedProjectThread` DTOs. `ThreadStore` maps those records to observable `ProjectThreadState` objects for presentation and maps changes back on upsert. JSON property names were preserved, and a literal legacy-settings regression verifies backward-compatible loading without migration.

Thread snapshots persist the latest 100 timeline items, 100 raw events, and 100 conversation turns. Each persisted turn retains at most 100 activity items plus attachment metadata, prompt-version state, and generated-image paths. Attachment references in drafts, queues, and turns participate in managed-store cleanup. At baseline, the local `settings.json` was 144,872 bytes. Every physical save emits `settings_saved` duration/size telemetry, while each coordinator batch emits logical request and coalesced-request counts. The synthetic burst baseline is 20 logical requests to one physical write.

## Baseline measurements and constraints

| Measure | Baseline |
| --- | --- |
| `MainViewModel.cs` | 2,473 physical lines, 34 commands, 11 settings-save call sites |
| `MainWindow.xaml` | 805 physical lines |
| Console test source | 2,527 physical lines before test-file extraction |
| Behavioral suite | 59 tests passing after bounded-history, terminal/persistence batching, long-stream, recovery, and shutdown regressions |
| Local serialized settings | 144,872 bytes at audit time |
| Persisted activity/raw history | Last 100 entries per thread |
| Live activity/raw history | Capped at 500 entries per selected/restored thread as the first Phase 5B optimization |
| Terminal history | 250,000 characters per terminal session |
| Startup shell visibility | 541 ms in the first instrumented local debug run |
| Startup readiness | 759 ms in the first instrumented local debug run |
| Synthetic terminal storage throughput | 39.06 MiB appended in 4.39 ms; newest 250,000 characters retained |
| Synthetic terminal presentation | 100 synchronous chunks coalesced into 1 UI update |
| Synthetic settings-write burst | 20 logical save requests coalesced into 1 physical write |
| Synthetic Codex long stream | 25,001 notifications to 2 ordered UI batches; 20.71 MiB allocated in 23.86 ms |
| Synthetic app-server recovery | 22 ms from connection failure to initialized replacement client |
| Synthetic active-resource shutdown | 12 ms with 1 active turn and 1 terminal session |

## Final comparison

| Measure | Final local result | Comparison |
| --- | --- | --- |
| `MainViewModel.cs` | 3,502 physical lines | Thread lifecycle, turn execution, queue dispatch, and thread-state persistence execute behind explicit application use-case services; the remaining size is shell/UI projection and cross-feature coordination. |
| `MainWindow.xaml` | 444 physical lines | Custom chrome, adaptive three-zone shell, compact drawers, lower terminal dock, inspector, status, and approval hosting. |
| Behavioral suite | 262 passing tests | Includes 259 existing behavioral cases plus three focused release/architecture metadata regressions, all discovered individually by xUnit. |
| Startup shell/readiness | 541 ms / 759 ms | unchanged |
| Codex long stream | 25,001 notifications, 2 UI batches, 20.71 MiB, 40.25 ms | same batching/allocation bound; synthetic CPU time varies locally |
| Terminal storage/presentation | 39.06 MiB in 2.24 ms; 250,000 retained; 100 chunks to 1 UI update | faster storage run; same presentation bound |
| Settings burst | 20 logical requests to 1 physical write | unchanged target |
| Recovery | 27 ms | 5 ms slower locally, still well below interactive latency |
| Active-resource shutdown | 2 ms | 10 ms faster locally |

Historical phase counts remain in the feature-parity ledger. Release 0.1.0 uses the current xUnit-discovered suite plus warning-free Debug and Release rebuilds as its authoritative gate.

A no-build behavioral-runner invocation took approximately 12 seconds during the initial audit; this is a coarse runner-duration observation, not a product performance metric.

## Ownership and lifecycle audit

- App-server transport/client startup, pending-request failure, restart serialization, notification batching, and disposal belong to `AppServerSessionCoordinator`; thread reduction/routing belongs to the Codex Core boundary; WPF receives semantic state changes.
- Terminal sessions belong to `TerminalViewModel`; shutdown disposes all sessions and logs bounded-buffer metrics.
- Git and worktree commands use argument lists, retain repository/worktree ownership guards, and now terminate process trees on cancellation.
- Codex utility commands terminate their process tree on cancellation. Visible login/logout consoles are intentionally user-owned after launch.
- Sandbox remains bounded by the selected mode. Explicit `workspace-write` turns add only the current attached project roots; no approval, authentication, destructive-action confirmation, worktree ownership, or archive semantics changed.
- Final-response text remains intentionally complete rather than bounded. Timeline, raw events, diagnostics, and terminal history—the repeatable record streams—are bounded.

## Phase boundary

Release 0.1.0 includes native active-context skill discovery/enablement and exact-path composer invocation, generated-image display/edit flows, managed and multi-root workspace attachments, projectless and multi-folder project chats, Goal mode, prompt editing/forking, chat management/search, queued follow-up hardening, multi-repository Git selection, dedicated inline code review, and the Phase 21 Markdown surface. Arbitrary skill roots, native MCP administration, plugins/connectors, automations, structured findings and hunk review, detached review, push/PR workflows, and full worktree handoff/snapshot lifecycle remain outside the current boundary.
