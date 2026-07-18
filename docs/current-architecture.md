# Native Codex Assistant: Current Architecture through Phase 5D

**Recorded:** 15 July 2026  
**Phase:** 5D - Project and Thread Navigation Consolidation
**Purpose:** Describe the current post-Phase-5D architecture while retaining the Phase 5B performance baseline.

## System shape

The solution is a Windows-only WPF desktop application with four projects:

| Project | Responsibility | Dependencies |
| --- | --- | --- |
| `NativeCodexAssistant.Core` | App-neutral contracts, settings records, thread state, Codex notification state, Git/worktree/terminal models | None |
| `NativeCodexAssistant.Infrastructure` | Codex CLI/app-server transport, JSON settings, Git, worktrees, ConPTY terminal, auth, logging | Core |
| `NativeCodexAssistant.App` | WPF composition root, window, theme resources, UI services, commands, presentation state | Core and Infrastructure |
| `NativeCodexAssistant.Tests` | Console-based behavioral and integration-style assertion runner | App, Core, and Infrastructure |

The intended dependency direction is therefore:

```text
Tests ────────────────> App ───────────────> Infrastructure ─────> Core
  └────────────────────┴─────────────────────────────────────────> Core
```

`AppServices.Create()` is the manual composition root. It constructs the concrete infrastructure services and passes 15 dependencies into `MainViewModel`. There is no external dependency-injection container.

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

`MainViewModel` is the shell coordinator. `ProjectThreadViewModel`, `TaskViewModel`, `TerminalViewModel`, `DiagnosticsViewModel`, and `GitViewModel` own feature presentation state and commands. The shell supplies explicit operation delegates or immutable-on-read context callbacks and receives status/selection callbacks; feature view models do not reference or control one another.

App-server lifecycle is exposed to presentation through `IAppServerSessionCoordinator`. Its implementation owns `CodexAppServerClient`, process transport startup, reconnect serialization, batched notifications, typed app-server operations, health transitions, and disposal. Protocol request/response JSON and delta-payload batching remain in the Codex Core/Infrastructure boundary rather than WPF presentation.

`MainWindow.xaml` is a 44-line shell that composes `ProjectThreadView`, `TaskView`, `TerminalView`, `GitView`, and `DetailsView`. Feature controls bind directly to their feature view models. Timeline, raw-event, thread, diagnostic, recent-project, and changed-file lists use recycling virtualization and content scrolling.

`ProjectThreadViewModel` also owns the unified project/thread navigation projection. `ProjectNavigationItemViewModel` groups presentation threads by normalized project path, tracks project disclosure and running summaries, and preserves the existing active-project `Threads` collection as a compatibility surface. The persisted `RecentProjects` and `ProjectThreads` collections remain unchanged.

`ProjectThreadView` renders project-name disclosure rows with their project-scoped threads and empty state; filesystem paths are retained for routing but omitted from the navigation UI. A project-row `+` creates a current-checkout thread immediately; isolated worktree creation is retained in the project's advanced menu. Only the selected project is expanded automatically. Selecting an existing project refreshes its recent timestamp in place rather than reordering the hierarchy. Completed and idle thread pills are intentionally suppressed; running, failed, cancelled, and archived states remain visible. Selected-thread lifecycle operations are exposed through high-contrast contextual action buttons and fully theme-aware context-menu surfaces. The workspace heading above Task and Changes contains only the selected thread title.

`MainViewModel.cs` is 1,589 physical lines (down 36% from 2,473) and now owns shell coordination:

- project selection and recent projects;
- thread creation, selection, resume, fork, archive, and worktree routing;
- task start, cancellation, steering, model selection, and notification handling;
- feature context and cross-feature event routing;
- shell layout, theme selection, details visibility, and status text;
- settings persistence and thread snapshots;
- app-server warm-up, semantic notification routing, and shutdown orchestration.

Concrete app-server lifecycle, terminal lifetime, diagnostics/auth operations, and Git operations are owned by their extracted coordinator or feature view models.

## Runtime flows

### Codex task

```text
Composer command
  -> TaskViewModel command
  -> MainViewModel shell coordination
  -> IAppServerSessionCoordinator
  -> CodexAppServerClient (Infrastructure)
  -> app-server process transport
  -> JSON-RPC notifications
  -> 50 ms Infrastructure notification batcher for agent-message deltas
  -> captured UI SynchronizationContext
  -> MainViewModel routing
  -> CodexThreadWorkspace / CodexThreadService
  -> observable response, activity, and raw-event surfaces
  -> thread snapshot / settings.json
```

Protocol request construction, response correlation, parsing, and transport failure handling remain inside Infrastructure. Core owns app-server request/result records and notification-derived thread state.

High-frequency agent-message deltas are grouped by thread, turn, and item before UI dispatch. Any non-delta event first flushes pending text, which preserves ordering for completion, error, tool, and lifecycle notifications. Idle deltas flush on the batching timer so streaming remains visibly progressive.

### Multi-turn conversation state

`CodexThreadWorkspace` owns one `CodexThreadService` per app-server thread and a turn-to-thread routing index. Each service exposes a bounded chronological collection of `CodexConversationTurn` objects. A turn owns its user prompt, assistant response, activity collection, status, and start/completion timestamps; the older singular final-response and timeline properties remain compatibility projections.

Submitting a follow-up calls `turn/start` with the existing thread ID. A pending local turn is created immediately, then bound to the returned app-server turn ID. Binding and notification reduction share a state gate and reconcile an already-observed turn, so notifications that arrive before the request response do not create duplicate turns or lose the response.

Thread selection and resume use typed `thread/read`/resume results with `includeTurns: true`. Canonical app-server user and assistant messages are reconciled with local turn snapshots, while richer local activity remains attached to its matching turn. If the server cannot provide history, the local snapshot remains usable. Legacy records containing only a preview and final response synthesize one visible completed turn.

`TaskView` presents the collection as a recycling-virtualized chronological transcript. Each turn has a distinct outer boundary and separate user, activity, and assistant surfaces, so adjacent turns remain visually independent while scrolling. The app-server stream is retained in bounded raw-event and diagnostic collections, while visible turn activity is an allowlisted projection of commentary, commands, file changes, tools, searches, plans, collaboration, guidance, and actionable errors. Stable item keys consolidate start, progress, and completion into one row; lifecycle, token, output-delta, reasoning, final-answer, and unknown notifications remain diagnostics-only. It follows live output while the viewport is near the bottom, exposes a Jump to latest action after manual scrolling, hides empty activity, collapses historical activity, and keeps the composer fixed. The first action is labelled Run task; subsequent submissions are labelled Send follow-up. During an active turn, the same composer becomes the guidance input.

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

The composition root exposes a `CoalescingSettingsStore` around `JsonSettingsStore`. The current application-level code still has 11 direct `SaveAsync` call sites in `MainViewModel`, but requests arriving within 75 ms are collapsed into a single physical write containing the latest immutable deep snapshot.

`JsonSettingsStore` serializes writes through a gate, flushes the complete settings graph to a write-through `settings.json.tmp`, and replaces `settings.json` with an overwrite move. Loading promotes a valid newer temporary file when an interrupted save left the primary missing or corrupt.

Persisted and presented thread state are separate. `AppSettings.ProjectThreads` contains storage-only `PersistedProjectThread` DTOs. `ThreadStore` maps those records to observable `ProjectThreadState` objects for presentation and maps changes back on upsert. JSON property names were preserved, and a literal legacy-settings regression verifies backward-compatible loading without migration.

Thread snapshots persist the latest 100 timeline items, 100 raw events, and 100 conversation turns. Each persisted turn retains at most 100 activity items. At baseline, the local `settings.json` was 144,872 bytes. Every physical save emits `settings_saved` duration/size telemetry, while each coordinator batch emits logical request and coalesced-request counts. The synthetic burst baseline is 20 logical requests to one physical write.

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
| `MainViewModel.cs` | 1,589 physical lines | 36% smaller |
| `MainWindow.xaml` | 44 physical lines plus five feature controls | 95% smaller shell; feature layout independently owned |
| Behavioral suite | 82 passing tests | 23 coordinator, lifecycle, history, persistence, multi-turn, selective-activity, presentation-state, and navigation regressions added after the 59-test baseline |
| Startup shell/readiness | 541 ms / 759 ms | unchanged |
| Codex long stream | 25,001 notifications, 2 UI batches, 20.71 MiB, 40.25 ms | same batching/allocation bound; synthetic CPU time varies locally |
| Terminal storage/presentation | 39.06 MiB in 2.24 ms; 250,000 retained; 100 chunks to 1 UI update | faster storage run; same presentation bound |
| Settings burst | 20 logical requests to 1 physical write | unchanged target |
| Recovery | 27 ms | 5 ms slower locally, still well below interactive latency |
| Active-resource shutdown | 2 ms | 10 ms faster locally |

The Phase 5D Release solution build completed with zero warnings and errors and all 75 tests passed. The canonical self-contained `win-x64` portable folder was refreshed and launch-verified. SHA-256: executable `CA7497DDF1FAAC53C5AE968B8317974EAD5DC14109982E9CA56624DD1FB509A9`; application DLL `9111F7437E00AE840FC574977105CAF97E82115B4FC621F08A31B84BF2297F49`.

A no-build behavioral-runner invocation took approximately 12 seconds during the initial audit; this is a coarse runner-duration observation, not a product performance metric.

## Ownership and lifecycle audit

- App-server transport/client startup, pending-request failure, restart serialization, notification batching, and disposal belong to `AppServerSessionCoordinator`; thread reduction/routing belongs to the Codex Core boundary; WPF receives semantic state changes.
- Terminal sessions belong to `TerminalViewModel`; shutdown disposes all sessions and logs bounded-buffer metrics.
- Git and worktree commands use argument lists, retain repository/worktree ownership guards, and now terminate process trees on cancellation.
- Codex utility commands terminate their process tree on cancellation. Visible login/logout consoles are intentionally user-owned after launch.
- Sandbox remains `workspace-write`; no approval, authentication, destructive-action confirmation, worktree ownership, or archive semantics changed.
- Final-response text remains intentionally complete rather than bounded. Timeline, raw events, diagnostics, and terminal history—the repeatable record streams—are bounded.

## Phase boundary

Phase 5D consolidates project/thread navigation over the completed Phase 5A-5C experience. Skills, plugins, MCP, automations, and other Phase 6 work remain out of scope.
