# Goal mode implementation plan

- **Planned:** 6 August 2026
- **Delivered:** 6 August 2026
- **Status:** Complete for this bounded parity slice
- **Parity target:** Codex Goal mode for a selected local chat
- **Protocol baseline:** Codex CLI/app-server 0.146.0 and the checked-in v2 schemas
- **Scope rule:** This slice implements Goal mode only. Activity view, notifications, multi-folder projects, Git review/PR, worktree handoff, scheduled tasks, plugins, browser control, and other parity gaps remain separate work.

## 1. Evidence and current gap

The current Codex manual describes Goal mode as stable and available in the desktop app, CLI, and IDE. A goal belongs to one chat, appears in a progress row above the composer, and can be paused, resumed, edited, or cleared. Goal state keeps the existing sandbox and approval policy.

The current app-server contract exposes:

- `thread/goal/set`, returning the persisted goal and emitting `thread/goal/updated`;
- `thread/goal/get`, returning the current goal or `null`;
- `thread/goal/clear`, returning whether a goal was cleared and emitting `thread/goal/cleared`;
- objectives from 1 through 4,000 characters;
- statuses `active`, `paused`, `blocked`, `usageLimited`, `budgetLimited`, and `complete`;
- optional token budgets plus token/time usage accounting.

SynthiaCode already checks in these schemas, but its client, typed notification seam, Codex feature interfaces, selected-thread state, commands, and WPF task surface do not use them. `feature_parity.md` also does not currently list Goal mode.

## 2. User outcome

For a selected Codex chat, the user can:

1. Create a goal with a validated objective.
2. See the objective, status, token usage, elapsed usage time, and optional token budget above the composer.
3. Pause an active goal and resume a paused goal.
4. Edit the objective without fabricating or resetting local state.
5. Clear the goal deliberately.
6. Switch chats without leaking one chat's goal into another.
7. Receive server-pushed goal updates without manually refreshing.

Creating the first goal also starts the objective as a normal prompt when the selected chat is idle, matching the documented behavior that goal text is both the prompt and the completion criterion. Editing an existing goal changes only the persisted objective.

## 3. Architecture

### 3.1 Protocol and provider boundary

- Add immutable typed goal/status/result models in `SynthiaCode.Core`.
- Add `SetThreadGoalAsync`, `GetThreadGoalAsync`, and `ClearThreadGoalAsync` to `CodexAppServerClient` with exact v2 JSON shapes and strict response validation.
- Add goal notification kinds and method constants to the existing typed notification seam.
- Expose the operations through a narrow Codex-only goal feature and the existing app-server session coordinator. Do not add goal semantics to the neutral in-memory harness.

### 3.2 Selected-thread orchestration

- Add a goal action contract consumed by `TaskViewModel` and implemented by a narrow `MainViewModel` adapter.
- On selected-thread changes, clear the previous presentation immediately, then load the server-owned goal for the new Codex thread.
- Capture the requested thread id and discard late results after a selection change.
- Apply `thread/goal/updated` and `thread/goal/cleared` only when their `threadId` matches the selected thread.
- Refresh the selected goal after an app-server reconnect.
- Treat `-32601`/unsupported runtimes as an unavailable feature with an actionable upgrade message; do not fail the rest of the chat.

### 3.3 Presentation state and commands

- `TaskViewModel` owns only transient presentation/edit state; app-server owns durable goal state and usage accounting.
- Expose loading, unavailable, error, editing, and busy states.
- Validate a trimmed objective as non-empty and at most 4,000 characters before a request.
- Disable conflicting goal mutations while another goal request is in flight.
- Expose commands for set, begin edit, save edit, cancel edit, pause/resume, and clear.
- Preserve the current goal if a mutation fails and show a local nonfatal error.

### 3.4 WPF surface

- Add an accessible goal row directly above the queued-follow-up/composer region.
- With no goal, show a compact **Set goal** affordance and an inline editor when invoked.
- With a goal, show status, objective, compact usage, and Edit/Pause-or-Resume/Clear controls.
- Keep long objectives wrapped and the row usable at the existing narrow responsive width.
- Use existing semantic theme resources and button/input styles.

## 4. Verification plan

1. Protocol tests prove exact request methods/fields, nullable `get`, response parsing, status values, clear results, and notification classification.
2. View-model tests prove validation, set/edit distinction, pause/resume/clear behavior, busy/error recovery, and state reset across chats.
3. Main workflow tests prove selected-thread loading, stale result rejection, matching notification routing, and first-goal prompt submission.
4. Rendered WPF tests prove the progress row, accessible names, controls, long-objective wrapping, and narrow-width containment.
5. Run the focused Goal mode group, related notification and responsive groups, a solution build, the complete Debug suite, and `git diff --check`.

## 5. Baseline note

Before implementation, `dotnet test SynthiaCode.sln --no-restore --configuration Debug` built successfully but reported three immediate behavioral failures:

- `view model preserves prompt after auth failed turn`;
- `view model runs follow-up turns on the same thread`;
- `view model queues active follow-up and drains after completion`.

Later exact-case comparison against an untouched `bab45a5` snapshot reproduced those failures plus three other legacy view-model/transport failures seen in the final full-suite run. This baseline comparison distinguishes all six from Goal mode regressions.

## 6. Completion criteria

Goal mode is complete for this slice when all protocol and presentation operations work against app-server 0.146.0, goal state remains thread-isolated across switching and reconnects, focused tests pass, the app builds without new warnings, and the parity audit records Goal mode plus the still-open current gaps.

## 7. Completion record

- The 5-case Goal mode group passes, including exact protocol shapes, validation and commands, stale-result protection, selected-chat orchestration, notification routing, first-goal prompt submission, and rendered WPF coverage.
- The related 15-case notification group and 3-case responsive-layout group pass.
- The Release solution build completes with zero warnings and zero errors.
- The complete Debug suite reports 282 passed and 6 failed. Each failing legacy view-model/transport case reproduces in an untouched `bab45a5` snapshot, so none is a Goal mode regression.
- `feature_parity.md` now records Goal mode as Full for the local-chat outcome and retains multi-folder projects, Activity/notifications, Voice coordination, ephemeral side chats, Git review/PR, and other capabilities as separate gaps.
