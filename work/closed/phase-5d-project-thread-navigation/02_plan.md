# Phase 5D Project and Thread Navigation Checklist

**Status:** Complete - 16 July 2026

Phase 5D consolidates the project and thread rails into one project-scoped navigation hierarchy. It preserves the existing settings schema and thread lifecycle semantics.

## Navigation model

- [x] Add app-layer project navigation nodes with selection, expansion, thread count, and running summary.
- [x] Group threads by normalized, case-insensitive Windows project paths.
- [x] Preserve recent-project ordering and active-thread restoration.
- [x] Update existing projects in place so selection never reorders the hierarchy.
- [x] Keep existing `RecentProjects` and `ProjectThreads` compatibility surfaces.

## Interaction design

- [x] Expand the selected project and collapse inactive projects.
- [x] Allow the active project disclosure to be collapsed without changing application context.
- [x] Display each project's threads directly beneath its project row.
- [x] Create current-checkout threads from a project-row `+` action.
- [x] Keep isolated worktree creation in the project's advanced action menu.
- [x] Move resume, fork, archive, unarchive, and worktree removal into the selected thread's action menu.

## Visual and accessibility behavior

- [x] Show project names, thread counts, and running summaries.
- [x] Remove project paths from navigation rows and show only project names.
- [x] Show only the selected thread title above the Task and Changes workspace.
- [x] Show a compact empty state for projects without threads.
- [x] Hide completed and idle thread status pills.
- [x] Retain only running, failed, cancelled, and archived status pills.
- [x] Preserve theme-aware button, selection, focus, and disabled-state resources.
- [x] Give project/thread action menus a high-contrast theme-aware button style.
- [x] Theme context-menu surfaces and menu-item hover/disabled states for dark mode.
- [x] Add automation labels for the combined navigation and thread actions.

## Compatibility and verification

- [x] Keep the settings schema unchanged.
- [x] Preserve project, thread, terminal, Git, worktree, and conversation switching behavior.
- [x] Add grouping, Unicode path, accordion, and compact-status regression tests.
- [x] Pass all 75 behavioral tests.
- [x] Pass the Release build with zero warnings and errors.
- [x] Refresh and launch-verify the self-contained portable build.
