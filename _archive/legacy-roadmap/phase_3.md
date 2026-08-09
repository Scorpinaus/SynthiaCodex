# Phase 3 Task Checklist

## Protocol baseline

- [x] Record the development Codex CLI version (`codex-cli 0.130.0-alpha.5`).
- [x] Generate version-matched app-server schemas into `schemas/`.
- [x] Send initialize capabilities, including notification opt-outs used by the UI.
- [x] Surface installed CLI version metadata and app-server health without assuming unsupported server fields.

## Pre-Phase 3 remediation requested by the user

- [x] Add failing tests for persisted theme application and theme changes.
- [x] Add light, dark, and system theme selection without adding a settings page.
- [x] Add a failing process-level test for the dedicated Codex CLI utility runner.
- [x] Add a constructor-injected `ICodexCliUtilityRunner` and diagnostics-panel doctor action.
- [x] Run the complete local test/build gates.
- [x] Run a live authenticated app-server model turn and require a completed response.

## Thread persistence and local state

- [x] Replace the one-thread-per-project settings behavior with a multi-thread `ThreadStore` abstraction.
- [x] Persist project path, Codex thread ID, title/display metadata, created/updated timestamps, archived and pinned state, mode, timeline, raw events, final response, and activity state.
- [x] Preserve existing `ProjectThreads` settings records through additive defaults.
- [x] Restore the selected project's visible thread list and active thread after app restart.
- [x] Add tests for settings compatibility, multi-thread storage, archive state, active selection, and independent timeline recovery.

## App-server lifecycle client

- [x] Add schema-matched `thread/list` support filtered by project `cwd`.
- [x] Add schema-matched `thread/fork`, `thread/archive`, and `thread/unarchive` support.
- [x] Add schema-matched `turn/steer` with the active turn ID precondition.
- [x] Keep `turn/interrupt` using both thread and turn IDs.
- [x] Add request-shape and response-parsing tests for every lifecycle method.
- [x] Map thread archived/unarchived and process failure events into UI-facing state.

## Multiple and parallel threads

- [x] Create new project threads without replacing existing records.
- [x] Select and resume any prior thread.
- [x] Fork the selected thread into a new persistent record.
- [x] Archive/unarchive threads while preserving local metadata.
- [x] Track active turns per thread so parallel turns do not overwrite each other.
- [x] Route notifications by thread/turn ID to the correct timeline.
- [x] Add tests proving two simultaneous threads update independently and keep the selected idle composer available.

## Steering, cancellation, and health

- [x] Add an in-flight steering composer enabled only for a steerable active turn.
- [x] Show cancellation-requested status until a terminal notification arrives.
- [x] Add an app-server health state (starting, healthy, recovering, unavailable).
- [x] Detect read-loop/process failure and surface recovery messaging.
- [x] Restart and reinitialize app-server on the next safe action after failure.
- [x] Preserve visible metadata and force thread resume after app-server recovery.
- [x] Add deterministic fake-transport tests for crash, pending-request failure, restart, and resumed operation.

## WPF surface

- [x] Add a thread list scoped to the selected project.
- [x] Add New, Resume, Fork, Archive, and Unarchive actions with state-aware enablement.
- [x] Add per-thread running/cancelling indicators and an app-server health indicator.
- [x] Preserve the existing task, Git, diagnostics, and prompt workflows.
- [x] Keep archive actions explicit and retain the no-automatic-commit/push behavior.

## Verification and handoff

- [x] Run the 39-test console assertion suite.
- [x] Run the full solution build with zero warnings/errors.
- [x] Run the live authenticated app-server initialization/turn smoke test using an advertised compatible model (`gpt-5.4`).
- [x] Refresh the portable build.
- [x] Run `scripts\maintenance-sweep.cmd` after all Phase 3 work is complete.
- [x] Record final publish and sweep results below.

## Implementation notes

- The installed CLI advertises `gpt-5.4` and completed the live turn successfully. Its account default currently resolves to `gpt-5.6-sol`, which this CLI reports requires a newer Codex version, so the smoke test selects a compatible model from `model/list`.
- The current CLI does not list a `doctor` subcommand in `codex --help`; the dedicated runner and UI action capture and display its exit code, stdout, and stderr instead of hiding unsupported-command failures.
- The official OpenAI developer-docs MCP server was added to global Codex configuration. It becomes callable after Codex restarts; exact request shapes in this implementation were verified against the generated local schemas for `codex-cli 0.130.0-alpha.5`.
- `scripts\publish-portable.cmd` produced the self-contained `win-x64` artifact in `portable\SynthiaCode\`. NuGet vulnerability-feed checks emitted `NU1900` because the service index was unreachable, but publish completed and the executable was verified by the script.
- `scripts\maintenance-sweep.cmd` removed eight reproducible build/intermediate directories, recovered 144.08 MiB, and preserved the canonical portable build.
