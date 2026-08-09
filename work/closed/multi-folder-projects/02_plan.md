# Multi-folder local projects implementation plan

- **Planned:** 6 August 2026
- **Parity target:** ChatGPT Codex multi-folder local projects
- **Baseline:** clean commit `1e68742` (`Goal Mode implementation`)
- **Scope rule:** This slice implements multi-folder local projects only. Activity view, notifications, Voice, ephemeral side chats, worktree handoff, push/PR, scheduled tasks, plugins, browser control, and unrelated review features remain separate gaps.

## 1. Official behavior and current gap

The current Codex project documentation defines these semantics:

1. A local project can attach multiple folders.
2. Exactly one folder is primary.
3. New chats, default Git operations, and automatic discovery of `AGENTS.md`, skills, and `config.toml` use the primary folder.
4. Secondary folders remain available for file search, reading, and editing, but do not contribute automatic project configuration discovery.
5. **Edit project** can add folders and make an attached folder primary.
6. When attached folders contain different Git repositories, the review surface can select and inspect each repository.

Sources:

- [Projects and chats](https://learn.chatgpt.com/docs/projects#use-local-projects-for-folders-and-codebases)
- [What's new](https://learn.chatgpt.com/docs/whats-new)
- [Code review](https://learn.chatgpt.com/docs/code-review#review-multiple-repositories)
- [Codex sandbox configuration](https://learn.chatgpt.com/docs/config-file/config-advanced#approval-policies-and-sandbox-modes)
- [Codex app-server](https://learn.chatgpt.com/docs/app-server)

SynthiaCode currently keys a project only by one `RecentProject.Path`. Thread scope, default workspace, settings and skill discovery, attachment references, Git inspection, and `workspaceWrite` sandbox requests all assume that one path. The navigation project menu has no edit action.

## 2. Required user outcome

For a local project, the user can:

1. Open **Edit project** from the project action menu.
2. See every attached folder and which one is primary.
3. Add an existing folder, remove a secondary folder, and make an attached folder primary.
4. Save only a valid, distinct, existing folder set with exactly one primary folder.
5. Restart SynthiaCode without losing the folder set or primary selection; legacy single-folder settings continue to load unchanged.
6. Create and continue local chats with the primary folder as their working directory and automatic Codex configuration/skill-discovery root.
7. Let Codex read and, under `workspaceWrite`, edit every attached folder without granting unrestricted filesystem access.
8. Attach files or subfolders from any attached root as durable workspace references rather than importing them as external copies.
9. Select any detected attached Git repository in the Changes inspector and use the existing diff, stage, unstage, discard, commit, Editor, and Explorer actions against that repository.

Changing the primary folder migrates the project scope and existing non-worktree chats atomically. Worktree chats retain their worktree path but remain associated with the migrated project. Removing a folder migrates any non-worktree chat or queued turn still rooted there to the current primary folder.

## 3. Domain and persistence design

### 3.1 Backward-compatible project shape

- Keep `RecentProject.Path` as the primary folder and project identity used by existing UI and thread-scope code.
- Add a persisted optional collection of additional folder paths. Expose a normalized primary-first folder list as the authoritative project root set.
- Normalize absolute Windows paths, compare case-insensitively, preserve secondary ordering, reject missing paths and duplicates, and keep the primary out of the additional list.
- Deep-copy the folder collection in `SettingsStorageMapper`; legacy records with no collection remain valid single-folder projects.

### 3.2 Atomic project migration

- Add one project update operation that validates the proposed folder set before mutating settings.
- Reject a new primary already used by a different saved project instead of silently merging projects.
- When the primary changes, migrate matching thread and composer-draft `ProjectPath` values, retain worktree paths, and move non-worktree workspace/queued paths to the new primary.
- Before changing roots, stamp legacy workspace attachment references with their actual owning root so relative paths remain stable through migration.
- Save once after a successful update, refresh navigation/Git/configuration state, and leave settings untouched when validation or persistence fails.

## 4. Codex execution and attachment routing

### 4.1 Harness-neutral root context

- Add an optional primary-first workspace-root list to conversation start, resume, fork, and turn-start commands.
- Preserve the list in queued-turn snapshots and dispatch compositions.
- Keep non-Codex harnesses compatible; the Codex adapter is the only provider-specific consumer.

### 4.2 App-server mapping

- For `workspaceWrite` turns, serialize the attached folders as normalized `sandboxPolicy.writableRoots`; do not broaden read-only, unrestricted, config-owned custom profiles, or managed permission semantics.
- Include a bounded generated developer-instruction suffix identifying secondary paths as path data, with the primary remaining `cwd`. This makes the extra roots discoverable to the agent while explicitly preserving primary-only automatic `AGENTS.md`, skills, and `config.toml` discovery.
- Reapply the current folder set on start/resume/fork and every new turn so project edits take effect without rewriting shared `config.toml`.

### 4.3 Multi-root attachments

- Persist the owning workspace root on workspace-reference attachments; legacy references fall back to their chat workspace.
- Resolve, revalidate, open, queue, restore, and submit a reference only when its owning root is still attached to the project.
- Treat a file/folder under any attached root as a workspace reference. Continue importing images and genuinely external paths through the existing managed-copy boundary.

## 5. Native project and Git surfaces

### 5.1 Edit project dialog

- Add an **Edit project** action to each project menu.
- Use a native, owner-centered WPF dialog with a keyboard-accessible folder list, clear Primary labeling, Add folder, Make primary, Remove, Save, and Cancel controls.
- Disable removing the only folder or the current primary until another folder is made primary. Show inline validation for missing/duplicate folders and changes that collide with another project.
- Disable project-folder mutation while a project chat is actively running.

### 5.2 Multi-repository Changes inspector

- Extend Git context with the current project roots.
- Discover distinct repositories from the primary/secondary folders (and retain the active worktree behavior), expose an accessible repository selector when more than one is available, and keep the primary repository first.
- Changing the selector refreshes the existing changed-file and diff state without changing the project primary folder.
- All existing Git mutations and deep links use the selected repository root.

## 6. Verification plan

1. Persistence tests cover legacy loading, deep-copy isolation, normalized primary-first round trips, and invalid/colliding updates.
2. Migration tests cover project/thread/draft/queue identity changes, worktree preservation, removed-root handling, and attachment-root stamping.
3. Protocol tests prove exact `sandboxPolicy.writableRoots`, primary `cwd`, generated secondary-root context, and safe omission outside `workspaceWrite`.
4. Queue and workflow tests prove new/resumed/forked/normal/queued turns receive the current roots and that primary-only settings/skills context remains unchanged.
5. Attachment tests prove import/revalidation/open/submission from every attached root and rejection after detachment.
6. Git tests prove distinct repository discovery, primary ordering, selection, and action routing.
7. Rendered WPF tests prove the Edit project action/dialog contract and repository selector accessibility at narrow widths.
8. Run all focused groups, Debug and Release builds, the complete Debug suite, and `git diff --check`. Compare unrelated failures against clean commit `1e68742`.

## 7. Completion criteria

This slice is complete only when folder editing is durable, primary changes migrate existing state safely, Codex receives every attached root within the selected permission boundary, attachments work across roots, Git repositories are selectable, legacy single-folder projects remain compatible, focused tests pass, builds introduce no warnings, and the parity inventory records the delivered outcome plus still-open gaps.

## 8. Completion record

- **Implemented:** 6 August 2026
- **Focused verification:** 41/41 multi-folder cases pass.
- **Regression verification:** 288/294 Debug cases pass. The same six legacy behavioral failures reproduce at protected baseline `1e68742`; no new failures were introduced.
- **Build quality:** the Debug app build completes with zero warnings and errors, and `git diff --check` is clean.
- **Delivered boundary:** durable primary/secondary roots, scoped-state migration, bounded Codex writable roots and context, multi-root attachments, native project-folder management, and selected-repository Git actions are complete. Structured hunk review, inline review comments, push/PR, and unrelated parity gaps remain separate slices.
