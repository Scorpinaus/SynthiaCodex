# Phase 5 Task Checklist

## Design and lifecycle baseline

- [x] Use Windows ConPTY directly behind app-neutral terminal contracts; do not add a terminal runtime dependency.
- [x] Start PowerShell as the initial and only Phase 5 shell profile.
- [x] Use `ProcessStartInfo`-equivalent structured native arguments and validate the requested working directory before process creation.
- [x] Keep terminal sessions owned by the application and keyed to the Codex thread that created them.
- [x] Ensure graceful exit is attempted before forcefully terminating a terminal process.
- [x] Strip terminal control sequences that the WPF text surface cannot render so output remains readable.

## Test-first pass/fail gates

- [x] Add a failing ConPTY integration test proving PowerShell starts in the requested directory and streams output.
- [x] Add a failing view-model test proving a worktree-backed thread starts its terminal in the worktree path.
- [x] Add a failing view-model test proving terminal input, clear, and kill actions target the selected thread's session.
- [x] Add a failing view-model test proving terminal output remains isolated when switching between threads.
- [x] Add a failing shutdown test proving all terminal sessions are stopped and disposed when the app closes.
- [x] Run the new tests before production implementation and record the expected failures.

## Core and infrastructure

- [x] Add `TerminalStartRequest`, terminal output/exit event models, `ITerminalSession`, and `ITerminalService` in Core.
- [x] Implement `WindowsConPtyTerminalService` and a disposable ConPTY-backed PowerShell session in Infrastructure.
- [x] Support asynchronous UTF-8 output streaming without blocking the WPF dispatcher.
- [x] Support command/input writes, terminal resize, clear-at-UI, and explicit session stop.
- [x] Validate and expose the current working directory and running/exited state.
- [x] Close pseudoconsole, pipe, thread, and process handles deterministically.

## Thread and workspace integration

- [x] Start each terminal in the selected thread's active workspace path, falling back to the selected project when no thread exists.
- [x] Keep at most one active terminal session per Codex thread and reuse it when returning to that thread.
- [x] Keep output buffers independent across project threads.
- [x] Switch the visible terminal output and status when the selected thread changes.
- [x] Stop and dispose every owned terminal session during application shutdown.
- [x] Prevent hidden terminal processes from surviving thread/worktree cleanup or application exit.

## WPF surface

- [x] Add a terminal toggle and terminal panel to the main workspace.
- [x] Show the terminal's active working directory and running/exited status.
- [x] Add Start PowerShell, Clear, and Kill controls with state-aware enablement.
- [x] Add a scrollable, selectable, read-only monospace output surface with normal copy support.
- [x] Add a command/input box with paste support and a Send action.
- [x] Preserve existing task, Git, diagnostics, authentication, thread, and worktree workflows.

## Verification and maintenance

- [x] Run the expanded console assertion suite.
- [x] Build the full solution with zero warnings and errors.
- [x] Refresh the canonical portable build if all checks pass.
- [x] Run `scripts\maintenance-sweep.cmd` after all Phase 5 work is complete.
- [x] Record final verification, publish, and maintenance results below.

## Implementation notes

- The pre-implementation test run failed at compile time because the new terminal contracts, ConPTY implementation, and view-model surface were intentionally absent. This establishes the red baseline before production code.
- The focused ConPTY run exposed that anonymous pipe handles are synchronous and that `STARTF_USESTDHANDLES` is required to keep the hosted PowerShell attached to ConPTY instead of the parent console. Output reading now runs on a dedicated background task.
- The expanded console runner passes all 48 tests, including a real ConPTY PowerShell session that verifies streamed output and the requested working directory.
- `dotnet build NativeCodexAssistant.sln --no-restore` passes with zero warnings and zero errors.
- `scripts\publish-portable.cmd` produced the self-contained `portable\NativeCodexAssistant\NativeCodexAssistant.App.exe` artifact.
- `scripts\maintenance-sweep.cmd` removed eight reproducible build/intermediate directories, recovered 144.74 MiB, and preserved the canonical portable build.
