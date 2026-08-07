# Hunk-level Git operations implementation plan

- **Planned:** 7 August 2026
- **Selected gap:** P0 interactive Git review — operate on one diff hunk without mutating the rest of the file.
- **Scope rule:** This slice adds stage, unstage, and discard for a selected hunk in the existing Working/Staged review views. Commit/Branch/Last turn scopes, push/PR, and worktree lifecycle remain separate gaps.

## Research and current gap

The [official OpenAI code-review documentation](https://learn.chatgpt.com/docs/code-review) describes the review pane as the place to inspect line-specific changes and decide what to stage or revert. It also distinguishes working-tree and staged views. SynthiaCode already has repository selection, working/staged unified diffs, file-level stage/unstage/discard, destructive confirmation, refresh, and inline findings/comments, but no hunk action.

The minimal extension is to reuse the current unified diff and Git mutation pipeline:

1. Parse each hunk into an immutable patch containing the file header plus exactly one `@@` section.
2. Route a typed operation through `IGitService`:
   - Working view + Stage -> `git apply --cached`.
   - Working view + Discard -> `git apply --reverse` after confirmation.
   - Staged view + Unstage -> `git apply --cached --reverse`.
3. Send the patch over standard input, not a temporary file or shell command.
4. Refresh repository status and preserve the selected file when it still exists.

## Safety boundary

Hunk actions are enabled only for ordinary tracked text modifications whose relevant Git status is `M`. Untracked, added, deleted, copied, renamed, conflicted, type-changed, and binary changes retain the existing whole-file actions. This avoids presenting a partial operation when Git metadata has file-level semantics.

Discard remains destructive and requires a confirmation naming the file and hunk header. A failed or stale patch leaves the repository untouched, surfaces Git's error, and refreshes nothing implicitly.

## Test-first contract

Before production implementation, add focused coverage for:

- extracting multiple independent, newline-terminated patches from one unified diff;
- staging only one of two working-tree hunks in a real temporary repository;
- unstaging only one of two staged hunks;
- discarding only one working-tree hunk while preserving the other;
- view-model action labels, view-specific routing, eligibility, confirmation, refresh, and failure behavior;
- accessible hunk controls in the rendered WPF review pane.

## Planned file changes

| Area | Minimal change |
| --- | --- |
| Core Git model/parser | Add typed hunk operation/patch values and bounded patch extraction. |
| Git service contract | Add one hunk mutation method. |
| Infrastructure Git service | Apply the patch via `git.exe` standard input using cached/reverse flags. |
| Git view model | Project hunk metadata and expose parameterized stage, unstage, and discard commands. |
| Git view | Render the action on eligible `@@` rows with an accessible name and destructive styling for discard. |
| Tests/docs | Protect parser, real Git, view-model, and WPF behavior; update parity and architecture only after verification. |

## Completion gate

The gap is complete when each operation changes only the selected hunk, ineligible diff families expose no hunk action, discard cannot run without confirmation, focused tests pass, the full suite introduces no new failures, Debug/Release builds introduce no warnings, and `git diff --check` is clean.

## Delivered verification

- The pre-implementation build failed at the intended missing hunk types and Git-service contract.
- All four hunk behavioral cases and all 47 Git-filtered tests pass.
- The complete Debug and Release suites each finish at 314/320, matching the six protected legacy app-server failures. A known fork timeout on the first Release run passed 37/37 in isolation; the complete rerun matched baseline.
- Non-incremental Debug and Release solution builds complete with zero warnings and errors.
