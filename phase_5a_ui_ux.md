# Phase 5A UI/UX Modernization Checklist

**Status:** Complete — 15 July 2026

All checklist items and Phase 5A acceptance gates are fulfilled. Further UI/UX changes should be treated as follow-up polish or separately scoped enhancements rather than blockers for this phase.

## Baseline and design direction

- [x] Capture screenshots of the current wide, minimum-width, light, and dark layouts.
- [x] Document the primary project-to-task-to-review user journey.
- [x] Define wide, medium, and narrow layout targets.
- [x] Define the information hierarchy for project, thread, workspace, task state, and diagnostics.
- [x] Inventory empty, loading, busy, authentication, failure, cancellation, and recovery states.

## Shell and navigation

- [x] Make the project/thread rail collapsible.
- [x] Move diagnostics and raw events into an on-demand details surface.
- [x] Keep Task, Changes, and Terminal navigation clear and keyboard accessible.
- [x] Simplify thread lifecycle actions and distinguish primary from secondary actions.
- [x] Keep the active project and workspace visible without duplicating low-value labels.
- [x] Persist the layout preferences selected for continuity.

## Task experience

- [x] Make the conversation/activity surface the visual focus.
- [x] Improve assistant-response readability, including Markdown if adopted.
- [x] Present command, tool, file-change, error, and cancellation events with clear visual hierarchy.
- [x] Keep the prompt composer available when parallel threads are running.
- [x] Make model, reasoning, steering, run, and cancel controls understandable without crowding the composer.
- [x] Provide clear retry actions while preserving failed prompts.

## Git, terminal, and diagnostics experience

- [x] Improve changed-file scanning and working/staged state distinction.
- [x] Improve diff readability and destructive-action confirmation copy.
- [x] Improve terminal status, working-directory, input, and session-control presentation.
- [x] Separate normal health/status information from developer protocol diagnostics.
- [x] Keep raw events available without rendering them as permanent primary content.

## Accessibility and visual quality

- [x] Verify keyboard focus order and add shortcuts for primary workflows.
- [x] Add accessible names and automation properties where needed.
- [x] Verify light, dark, and system themes for contrast and state visibility.
- [x] Verify high DPI, text scaling, narrow windows, long paths, and long content.
- [x] Correct visible encoding and copy defects, including the worktree separator label.

## Verification

- [x] Add focused UI-state and view-model tests for the redesigned interactions.
- [x] Run all existing behavioral tests.
- [x] Build the solution with zero warnings and errors.
- [x] Perform keyboard-only, high-DPI, narrow-window, and theme smoke tests.
- [x] Refresh the portable build after the UI/UX acceptance gates pass.

## Phase boundary

- Do not begin Phase 6 skills, plugins, MCP, or settings work.
- Defer broad view-model, service, protocol, persistence, and performance refactoring to Phase 5B.

## Implementation notes

- The primary journey is now: choose a project, choose or create a thread and workspace, run and review the task in Response/Activity, inspect Changes or Terminal as needed, and open Details only for account/runtime diagnostics.
- Wide layouts can show navigation and details together. Below 1000 device-independent pixels, opening one side surface closes the other; the application minimum width is now 800 pixels. The 820-by-700 compact layout was visually verified.
- Project navigation and diagnostics visibility persist in `AppSettings`. Compact-mode conflict resolution is covered by tests.
- The response is the default task output, Activity contains structured streamed events, and Run settings is collapsed by default. Markdown rendering was deliberately not introduced because it would add a new rendering dependency and belongs in a separately scoped enhancement.
- New shortcuts are Ctrl+O for project selection, Ctrl+Enter to run, Ctrl+B for navigation, Ctrl+Shift+D for details, Ctrl+Shift+T for Terminal, and F5 for diagnostics refresh.
- Accessibility names were added to primary inputs and streamed surfaces. Header/status rows can grow for text scaling, long paths trim or wrap, and virtualized lists are used for long thread, activity, changed-file, and raw-event surfaces.
- Visual QA covered light, dark, system, wide, and compact states. It exposed stock WPF dark-theme foreground problems in text, tabs, and combo boxes; palette-controlled styles/templates now keep those states readable.
- The expanded assertion runner passes all 52 tests. The final solution build completes with zero warnings and zero errors.
- The first portable publish attempt was blocked by sandboxed NuGet network access. The approved retry restored successfully and refreshed `portable\SynthiaCode\SynthiaCode.App.exe`.
- Dark-mode follow-up QA replaced the stock WPF button chrome with palette-controlled normal, hover, pressed, keyboard-focus, primary, and disabled states. Combo boxes now use the same dark surface instead of a bright Windows frame.
- The Changes view now replaces its disabled file/diff/commit workspace with a focused empty state when the selected folder is not a Git repository.
- Startup now shows the shell immediately, completes Codex/auth diagnostics, and then warms the Codex app-server in the background. The connection badge uses neutral, warning, success, and error colors for idle, connecting/reconnecting, connected, and unavailable/sign-in states. A failed warm-up remains retryable on the next Codex action.
- The two primary workflow actions now have distinct semantic palettes: New thread uses teal and Run task uses blue. Each theme supplies its own background/foreground pair, including dark text on the brighter dark-mode fills, while shared hover, pressed, focus, and disabled behavior remains consistent.
- Mid-turn guidance now reuses the primary composer location instead of adding a second text field. Idle mode shows the task prompt and Run task; the selected running turn swaps the same surface to an instructional guidance composer with Guide task and Cancel actions. Guidance clears when sent, when the turn stops, or when the user changes threads.
