# Phase 4 Task Checklist

## Design and safety baseline

- [x] Use the sibling layout `<parent>/<repo-name>.worktrees/<task-id>` so assistant worktrees are outside the primary checkout.
- [x] Derive filesystem-safe, Git-safe task and branch names and resolve collisions without overwriting existing paths or branches.
- [x] Record assistant ownership in repository Git metadata rather than placing tracking files in a worktree.
- [x] Treat missing, malformed, or nonmatching ownership records as user-created worktrees and refuse cleanup.
- [x] Use `ProcessStartInfo.ArgumentList` for all Git arguments and validate resolved paths.

## Test-first pass/fail gates

- [x] Add a failing integration test proving worktree creation uses the sibling layout, creates an isolated branch, and leaves the main checkout unchanged.
- [x] Add a failing integration test proving only assistant-owned worktrees are listed.
- [x] Add a failing safety test proving cleanup refuses a user-created/unregistered worktree.
- [x] Add a failing cleanup test proving a clean assistant-created worktree can be removed and its ownership record is cleared.
- [x] Add a failing persistence test proving thread mode and workspace path survive settings serialization.
- [x] Add a failing view-model test proving a worktree task sends the worktree path as the Codex turn `cwd`.
- [x] Run the new tests before production implementation and record the expected failures.

## Core and infrastructure

- [x] Add app-neutral worktree models and an `IWorktreeService` boundary.
- [x] Implement creation, listing, thread association, and guarded removal through `git.exe`.
- [x] Keep an assistant-owned JSON registry under the repository's common Git directory.
- [x] Ignore stale registry records when listing active worktrees and never adopt unregistered Git worktrees.
- [x] Refuse forced removal; dirty worktrees must be cleaned or committed by the user first.
- [x] Preserve worktree branches during cleanup so removal does not silently discard branch history.

## Thread and task integration

- [x] Persist a thread's main project path separately from its active workspace path.
- [x] Support creating a thread in either the current checkout or a new assistant worktree.
- [x] Start and resume Codex turns with the selected thread's active workspace path.
- [x] Keep Git status/diff actions scoped to the active thread workspace.
- [x] Clearly expose the selected thread's checkout mode, branch, and active path.
- [x] Add explicit confirmed cleanup for a selected assistant-created worktree.
- [x] Disable cleanup while the selected thread is running and retain the thread record after cleanup with a clear unavailable-workspace state.

## WPF surface

- [x] Add a checkout-mode selector for new threads.
- [x] Label each thread as current-checkout or worktree-backed.
- [x] Display the active workspace path prominently on the task surface.
- [x] Add a guarded **Remove worktree** action with exact-path confirmation.
- [x] Preserve existing thread, Git, diagnostics, authentication, and prompt workflows.

## Verification and maintenance

- [x] Run the expanded console assertion suite.
- [x] Build the full solution with zero warnings and errors.
- [x] Refresh the portable build if all checks pass.
- [x] Run `scripts\maintenance-sweep.cmd` after Phase 4 is complete.
- [x] Record final verification and maintenance results below.

## Implementation notes

- The pre-implementation test run failed at compile time because the new worktree contracts and implementation were intentionally absent, establishing the red test baseline.
- The expanded console runner passes all 44 tests. The opt-in live Codex smoke test remains skipped unless `SYNTHIACODE_RUN_LIVE_CODEX_SMOKE=1` is set.
- `dotnet build SynthiaCode.sln --no-restore` passes with zero warnings and zero errors.
- The first portable publish attempt failed because the sandboxed process could not authenticate to NuGet over TLS. The approved network-enabled retry restored successfully and produced `portable\SynthiaCode\SynthiaCode.App.exe`.
- `scripts\maintenance-sweep.cmd` removed eight reproducible build/intermediate directories, recovered 144.42 MiB, and preserved the canonical portable build.
