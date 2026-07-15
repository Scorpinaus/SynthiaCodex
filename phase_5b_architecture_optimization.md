# Phase 5B Architecture and Performance Optimization Checklist

## Baselines

- [ ] Measure startup time to shell visibility and readiness.
- [ ] Measure memory and UI responsiveness during a representative long Codex stream.
- [ ] Measure terminal output throughput and dispatcher update frequency.
- [ ] Measure settings-write frequency and serialized settings size.
- [ ] Record recovery and shutdown behavior with active turns and terminal sessions.

## Presentation architecture

- [ ] Reduce `MainViewModel` to shell-level state and coordination.
- [ ] Extract project/thread, task, Git, terminal, and diagnostics view models.
- [ ] Split `MainWindow.xaml` into feature-oriented WPF views or user controls.
- [ ] Give each feature explicit inputs, commands, lifetime, and test boundaries.
- [ ] Keep cross-feature coordination explicit and avoid feature view models directly controlling one another.

## State and persistence

- [ ] Separate persisted thread DTOs from WPF observable presentation models.
- [ ] Preserve backward compatibility for existing `settings.json` records or implement an explicit migration.
- [ ] Review thread snapshot timing and debounce or coalesce redundant saves.
- [ ] Keep settings writes atomic and recover safely from interrupted temporary files.
- [ ] Bound live and persisted timeline, raw-event, diagnostic, and terminal history.

## Streaming and rendering performance

- [ ] Virtualize long timeline, raw-event, thread, diagnostic, and changed-file surfaces where appropriate.
- [ ] Batch or throttle high-frequency app-server delta updates before UI dispatch.
- [ ] Replace full terminal-buffer string recreation on every output chunk with an incremental or throttled presentation path.
- [ ] Verify that background process reads never block the WPF dispatcher.
- [ ] Define and test representative stress bounds for long tasks and terminal sessions.

## Service and process boundaries

- [ ] Introduce an app-facing app-server session/coordinator boundary around `CodexAppServerClient` if needed.
- [ ] Keep protocol parsing/version handling inside the Codex infrastructure boundary.
- [ ] Review app-server restart, pending request failure, thread resume, and notification routing ownership.
- [ ] Review terminal, Git, worktree, login, and utility process disposal paths.
- [ ] Preserve sandbox, approval, authentication, worktree ownership, and destructive-action semantics.

## Tests and verification

- [ ] Split the monolithic console test source into feature-oriented files or projects.
- [ ] Add regression tests for extracted view models and coordinators.
- [ ] Add tests for batching, bounded buffers, persistence compatibility, recovery, and shutdown.
- [ ] Compare final measurements with the recorded baselines.
- [ ] Run the full behavioral suite and build with zero warnings and errors.
- [ ] Refresh the portable build after all architecture and performance gates pass.

## Phase boundary

- Preserve the approved Phase 5A user experience while refactoring internals.
- Do not begin Phase 6 skills, plugins, MCP, or settings work.
