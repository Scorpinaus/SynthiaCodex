# Phase 5B Architecture and Performance Optimization Checklist

**Status:** Complete — 15 July 2026

All checklist items and Phase 5B acceptance gates are fulfilled. Phase 6 remains a separate, not-yet-started phase.

The current dependency/runtime architecture, concentration points, and initial measurements are recorded in `docs/current-architecture.md`.

## Baselines

- [x] Measure startup time to shell visibility and readiness.
- [x] Measure memory and UI responsiveness during a representative long Codex stream.
- [x] Measure terminal output throughput and dispatcher update frequency.
- [x] Measure settings-write frequency and serialized settings size.
- [x] Record recovery and shutdown behavior with active turns and terminal sessions.

## Presentation architecture

- [x] Reduce `MainViewModel` to shell-level state and coordination.
- [x] Extract project/thread, task, Git, terminal, and diagnostics view models.
- [x] Split `MainWindow.xaml` into feature-oriented WPF views or user controls.
- [x] Give each feature explicit inputs, commands, lifetime, and test boundaries.
- [x] Keep cross-feature coordination explicit and avoid feature view models directly controlling one another.

## State and persistence

- [x] Separate persisted thread DTOs from WPF observable presentation models.
- [x] Preserve backward compatibility for existing `settings.json` records or implement an explicit migration.
- [x] Review thread snapshot timing and debounce or coalesce redundant saves.
- [x] Keep settings writes atomic and recover safely from interrupted temporary files.
- [x] Bound live and persisted timeline, raw-event, diagnostic, and terminal history.

## Streaming and rendering performance

- [x] Virtualize long timeline, raw-event, thread, diagnostic, and changed-file surfaces where appropriate.
- [x] Batch or throttle high-frequency app-server delta updates before UI dispatch.
- [x] Replace full terminal-buffer string recreation on every output chunk with an incremental or throttled presentation path.
- [x] Verify that background process reads never block the WPF dispatcher.
- [x] Define and test representative stress bounds for long tasks and terminal sessions.

## Service and process boundaries

- [x] Introduce an app-facing app-server session/coordinator boundary around `CodexAppServerClient` if needed.
- [x] Keep protocol parsing/version handling inside the Codex infrastructure boundary.
- [x] Review app-server restart, pending request failure, thread resume, and notification routing ownership.
- [x] Review terminal, Git, worktree, login, and utility process disposal paths.
- [x] Preserve sandbox, approval, authentication, worktree ownership, and destructive-action semantics.

## Tests and verification

- [x] Split the monolithic console test source into feature-oriented files or projects.
- [x] Add regression tests for extracted view models and coordinators.
- [x] Add tests for batching, bounded buffers, persistence compatibility, recovery, and shutdown.
- [x] Compare final measurements with the recorded baselines.
- [x] Run the full behavioral suite and build with zero warnings and errors.
- [x] Refresh the portable build after all architecture and performance gates pass.

## Phase boundary

- Preserve the approved Phase 5A user experience while refactoring internals.
- Do not begin Phase 6 skills, plugins, MCP, or settings work.

## Implementation notes

- The initial audit identified `MainViewModel` (2,473 physical lines, 34 commands) and `MainWindow.xaml` (805 physical lines) as the main presentation concentration points.
- The first bounded optimization caps each live/restored Codex timeline and raw-event collection at 500 entries while retaining the existing persisted limit of 100 entries per thread.
- A stress regression verifies eviction of the oldest streamed records and retention of the newest. The behavioral runner now passes all 55 tests.
- Startup telemetry now records `startup_shell_visible` and `startup_ready`. The first instrumented debug run reached shell visibility in 541 ms and readiness in 759 ms.
- Terminal storage now uses a thread-safe 250,000-character circular buffer. Presentation updates are coalesced into 50 ms batches instead of posting and recreating the full display string for every chunk.
- The representative terminal-buffer run appended 39.06 MiB in 4.39 ms and retained the newest 250,000 characters. A synchronous 100-chunk burst produced one UI presentation update. These are local synthetic baselines, not cross-machine targets.
- Each terminal session now logs received chunks/characters, presentation updates, retained characters, and elapsed duration. Each atomic settings save logs duration and serialized byte size for the remaining persistence baseline work.
- The application composition root now wraps JSON persistence with a 75 ms coalescing store. Each request captures an immutable deep snapshot; a representative 20-request burst produces one physical write containing the latest queued snapshot.
- JSON saves are serialized through a gate, flushed through a write-through temporary file, and promoted atomically. Loading prefers a valid newer temporary file and recovers it when the primary file is missing or corrupt.
- Settings persistence, interrupted-write recovery, snapshot isolation, and burst coalescing are covered by the behavioral runner, which now passes all 57 tests.
- Agent-message deltas now pass through a 50 ms app-facing notification batcher before UI dispatch. Non-delta events flush pending text first, preserving protocol order and final-response integrity.
- The representative long stream processed 25,001 notifications into two UI batches, allocated 20.71 MiB, and completed the synthetic batching path in 23.86 ms. Background Codex and ConPTY reads remain asynchronous with `ConfigureAwait(false)`; only bounded/batched presentation work reaches the captured UI context.
- Recovery and shutdown telemetry now records elapsed duration and active resource counts. The synthetic recovery completed in 22 ms; shutdown with one active turn and one terminal session completed in 12 ms with cancellation, persistence, and disposal verified.
- Long-stream batching, idle-delta flushing, recovery timing, and combined active-turn/terminal shutdown increased the passing behavioral suite to 59 tests.
- `AppServerSessionCoordinator` now exclusively owns app-server transport/client creation, typed operations, reconnect serialization, notification batching, health transitions, and disposal. `MainViewModel` no longer references `CodexAppServerClient`.
- Terminal, diagnostics/authentication, and Git state/commands now have dedicated feature view models with explicit context callbacks and lifetime boundaries. Direct boundary regressions live in `Phase5BBoundaryTests.cs`; the behavioral suite now contains 63 tests.
- Project/thread and task presentation now have dedicated view models as well. `MainWindow.xaml` is a 44-line shell composing five feature controls, and every collection surface uses recycling virtualization where appropriate.
- Persisted threads are plain `PersistedProjectThread` DTOs; `ThreadStore` projects them into observable `ProjectThreadState` instances. A literal legacy JSON regression proves the property-compatible settings shape still loads without migration.
- Diagnostic lines are capped at 500, live timeline/raw events at 500 each, persisted timeline/raw events at 100 each, and terminal text at 250,000 characters per session.
- Process review added cancellation-safe process-tree termination for Codex utility, Git, and worktree commands. App-server and ConPTY sessions retain deterministic async disposal; visible authentication terminals remain explicitly user-owned.
- The post-refactor local comparison recorded startup 541/759 ms (unchanged), 25,001 notifications to 2 UI batches with 20.71 MiB allocated in 40.25 ms, terminal storage 39.06 MiB in 2.24 ms with one UI presentation update per 100-chunk burst, recovery 27 ms, shutdown 2 ms, and settings coalescing 20 logical requests to one physical write.
- The suite is split between the legacy behavioral runner and feature-oriented Phase 5B boundary tests and now contains 67 passing tests.
- The final Release solution build completed with zero warnings and errors, and all 67 Release tests passed.
- The guarded self-contained `win-x64` publish completed successfully. The portable executable reached Ready in a launch check; its SHA-256 is `17612E765DC721437EBE964D4EC1DACFAC94F2556A1B801DA0C5B7D146756783`.
