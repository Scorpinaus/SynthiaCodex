# Projectless Threads Implementation Plan

- **Prepared:** 19 July 2026
- **Repository baseline inspected:** `93fdd41`
- **Baseline verification:** `dotnet test SynthiaCode.sln --no-restore` succeeded and the console behavioral runner passed all 138 tests
- **Scope:** Create, select, run, resume, fork, archive, persist, and restore Codex threads without requiring the user to add or select a project
- **Implementation status:** Completed on 19 July 2026; implementation and regression coverage are recorded in section 16

## 1. Outcome

SynthiaCode should expose a first-class **General** thread scope alongside project-scoped threads. A signed-in user must be able to create a General thread from the navigation rail, or submit a prompt with no project selected and have SynthiaCode create the General thread automatically. The thread must remain fully usable for conversation turns, attachments, queued follow-ups, permission resolution, resume/fork/archive, and the integrated terminal.

A projectless thread still needs a deterministic filesystem working directory. The implementation should therefore create one shared, app-managed General workspace under SynthiaCode's local application-data directory, for example:

```text
%LOCALAPPDATA%\SynthiaCode\workspaces\general
```

This directory is an implementation workspace, not a recent project and not a fake project row. All General threads share it in the same way multiple project threads share a checkout. This choice gives every Codex lifecycle request a stable absolute `cwd`, keeps workspace-write permissions narrow, supports terminal and attachment behavior, and avoids using the user's home directory or the portable application folder.

The feature is complete when all of the following are true:

1. The navigation rail has a visible, keyboard-accessible action for creating a General thread without selecting a project.
2. Submitting the first prompt with neither a project nor a thread selected creates a General thread and starts the turn in the managed General workspace.
3. Project-row `+` and worktree actions retain their current project-specific behavior.
4. General and project threads cannot be mis-grouped, overwrite each other's active selection, or inherit the wrong project path.
5. General threads survive restart and support selection, resume, fork, archive, unarchive, attachments, queued follow-ups, permissions, and terminal sessions.
6. Git and assistant-worktree operations remain unavailable for General threads and explain why instead of asking the user to select a project to use the Task surface.
7. Existing `settings.json` files load unchanged and all existing project-thread behavior continues to pass.

## 2. Research findings

### 2.1 Codex app-server supports a thread without a project

The project requirement is imposed by SynthiaCode, not by the Codex app-server protocol:

- The checked-in `schemas/v2/ThreadStartParams.json` has no required fields and declares `cwd` as `string | null`.
- The current upstream protocol defines both `ThreadStartParams.cwd` and `TurnStartParams.cwd` as optional values. See the primary protocol source in [OpenAI Codex `v2.rs`](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2.rs).
- The official [Codex app-server guide](https://developers.openai.com/codex/app-server/#start-or-resume-a-thread) treats `thread/start` as the operation that creates a fresh conversation and shows `cwd` as a request option, not a project identity.
- The official [turn-start example](https://developers.openai.com/codex/app-server/#start-a-turn) also passes a filesystem `cwd`; upstream describes it as a turn-scoped working-directory override rather than a required project record.

Protocol optionality does not mean SynthiaCode should omit `cwd`. If it does, app-server behavior depends on the directory from which the app-server process was launched, which can be the installed or portable application directory. That is invisible to the user, unsafe as a workspace-write boundary, and unstable across packaging. SynthiaCode should deliberately provide its managed General workspace on thread start, resume, fork, and every turn start.

### 2.2 Current SynthiaCode constraints

The current implementation has project assumptions at every application layer:

- `MainViewModel.CanManageThreads` requires `SelectedProjectPath`.
- `NewThreadAsync`, `SubmitPromptAsync`, `EnsureActiveThreadAsync`, `CreateThreadState`, resume/fork/archive/unarchive, `GetActiveWorkspacePath`, persisted-state restore, active-state save, and navigation refresh all guard on or derive identity from `SelectedProjectPath`.
- `ProjectThreadState.ProjectPath` and `PersistedProjectThread.ProjectPath` are non-null strings, and `ThreadStore` normalizes them unconditionally with `Path.GetFullPath`.
- `ThreadStore` identifies a thread by `(ProjectPath, ThreadId)` and scopes active/archive operations to a project path.
- `ProjectThreadViewModel` and `ProjectThreadView.xaml` render only recent-project groups. The only visible creation action is the `+` on a project row.
- `CodexThreadStartOptions` cannot carry `cwd`; `CodexAppServerClient.StartThreadAsync` therefore always omits it.
- `CodexTurnStartRequest` requires a non-null `Cwd`, which is good for SynthiaCode's operational safety but currently makes `GetActiveWorkspacePath` a hard project dependency.
- Composer attachment drafts are keyed by `ProjectPath` and cannot represent a pre-thread General draft.
- `CreateTerminalContext` can run only when it receives a project or selected-thread workspace.
- `GitContext` and `GitViewModel` interpret a missing project as an unusable application state, even though Task and Terminal should work for General threads.
- Execution-policy and permission-profile discovery is scoped from `SelectedProjectPath` instead of the selected thread's effective workspace.
- Archive notifications currently call `SetArchived` using whichever project happens to be selected, rather than the routed thread's stored scope.

Removing only the command guard would create partially functional records and would allow the selected project, General workspace, attachment draft, terminal, and archive state to drift apart. The implementation must introduce explicit scope identity and route all workspace-dependent behavior through it.

## 3. Product and architecture decisions

### 3.1 Use an explicit scope, not a sentinel project path

Add a Core `ThreadScopeKind` with two values:

```text
Project
General
```

Add a small immutable `ThreadScopeKey` value that combines the kind with an optional normalized project path. `ThreadScopeKey.General` has no project path. `ThreadScopeKey.ForProject(path)` validates and normalizes its path. Use this key in application and store APIs instead of passing nullable strings whose meaning changes by caller.

Do not represent General with a magic path such as `"General"`, the application directory, the user profile, or the managed workspace path in `ProjectPath`. A sentinel would leak into recent-project grouping and make path normalization, migrations, and user-facing labels ambiguous.

### 3.2 Keep a stable app-managed General workspace

Introduce `IGeneralWorkspaceService` in Core and a filesystem implementation in Infrastructure. Its responsibilities are deliberately small:

- resolve the absolute General root below `SystemPaths.AppDataDirectory`;
- create the directory idempotently before it is returned;
- reject a file occupying the expected directory path;
- verify on every resolution that the resulting path is contained within the configured SynthiaCode app-data root;
- surface an actionable initialization error without falling back to the user profile or install directory.

Use one shared General root, not a new folder per thread. Shared scope matches the existing mental model of several conversations over one workspace, lets pre-thread composer drafts have a workspace, makes General forks deterministic, and avoids orphan-folder cleanup. Do not automatically delete this directory when a thread is archived. Destructive cleanup can be designed separately with explicit confirmation.

### 3.3 Preserve existing JSON names

`AppSettings.ProjectThreads` and the storage DTO name `PersistedProjectThread` are project-oriented, but renaming the serialized collection would add migration risk without improving the user outcome. Keep the JSON property names in this slice and add an explicit scope field:

- `PersistedProjectThread.ScopeKind`, serialized as a readable string and defaulting to `Project` when absent;
- `ProjectThreadState.ScopeKind`, with a computed `ScopeKey` and `IsGeneral` helper;
- `ComposerAttachmentDraftSnapshot.ScopeKind`, also defaulting to `Project`.

Existing settings files omit the new field and must therefore deserialize as project-scoped. A General record uses `ScopeKind = General`, `ProjectPath = ""`, and `WorkspacePath = <managed general root>`. The scope field, not an empty path alone, is authoritative.

### 3.4 Keep `cwd` operationally required inside SynthiaCode

Although upstream allows a missing `cwd`, the SynthiaCode request pipeline should continue to require a resolved absolute workspace for resume, fork, turn start, queued turns, attachment resolution, and terminal start.

Extend only `CodexThreadStartOptions` with nullable `Cwd`, because the wire protocol makes that field optional and existing callers/tests rely on default omission. SynthiaCode's MainViewModel should always populate it for user-created threads:

- General thread: managed General workspace;
- project/current checkout: selected project path;
- requested worktree: selected project path on `thread/start`, followed by the created worktree path on the first `turn/start`; the turn override becomes the subsequent working directory.

Do not make `CodexTurnStartRequest.Cwd` nullable in this feature. A non-null internal turn contract catches routing defects before they reach app-server.

### 3.5 Make General creation distinct from project creation

The creation commands have explicit scope behavior while retaining compatibility with the existing global shortcut:

- `NewGeneralThreadCommand`: activates the General scope and creates a thread in the managed General workspace, regardless of which project was previously selected.
- `NewThreadCommand`: creates in the current scope, so it creates a General thread when no project is selected and preserves existing project behavior after a project is selected.
- `NewThreadForProjectCommand`: retains current-checkout creation for its owning project.
- `NewWorktreeThreadForProjectCommand`: retains isolated-worktree creation and remains project-only.
- First composer submission with no selected project/thread: implicitly uses the same General creation path before starting the turn.

Do not overload the existing parameterless `NewThreadAsync` and infer intent from mutable selection after asynchronous work. Pass an immutable `ThreadCreationContext` containing scope, workspace, and requested workspace mode into the creation path.

## 4. Data model and persistence changes

### 4.1 Add scope primitives

Create `src/SynthiaCode.Core/Settings/ThreadScope.cs` containing:

- `ThreadScopeKind`;
- `ThreadScopeKey` with `General`, `ForProject`, equality, and display helpers;
- central project-path normalization so view models and stores do not each call `Path.GetFullPath` differently.

Required invariants:

| Scope | `ProjectPath` | `WorkspacePath` | Git/worktree eligibility |
| --- | --- | --- | --- |
| Project/current checkout | absolute project path | same project path | Git if detected; worktree creation allowed if repository |
| Project/worktree | absolute owning project path | absolute assistant worktree path | Git and owned-worktree actions allowed |
| General | empty/null storage value | absolute managed General root | Git/worktree actions unavailable in this slice |

`ProjectThreadState` should expose `EffectiveWorkspacePath` only if doing so removes repeated fallback logic. It must never make `ProjectPath` the fallback for a General record.

### 4.2 Generalize `ThreadStore`

Refactor `src/SynthiaCode.Core/Settings/ThreadStore.cs` around scope-aware operations:

```text
GetThreads(settings, scope, includeArchived)
GetActive(settings, scope)
SetActive(settings, scope, threadId)
SetArchived(settings, threadId, archived)
Upsert(settings, state)
```

Keep thin `GetProjectThreads` compatibility wrappers if that avoids an all-at-once test migration, but new application code should use the scope-aware API.

Specific behavior:

- Normalize `ProjectPath` only for `Project` records.
- Match an upsert by globally unique `ThreadId`; if an existing record has a different scope, fail rather than silently move it.
- Scope `IsActive` updates to exactly one `ThreadScopeKey`, so a project and General may each retain their last active thread.
- Archive/unarchive by thread ID after retrieving the record's own scope. Do not use the currently selected project for a routed server notification.
- Project queries must explicitly exclude General records even if a malformed General record contains a path.
- General queries must explicitly require `ScopeKind.General`.
- Validate that a General record has a non-empty absolute `WorkspacePath`. The workspace service, not the store, owns root-containment checks.

### 4.3 Snapshot and backward compatibility

Update `AppSettingsSnapshot.CloneThread`, `CloneDraft`, and `ThreadStore` presentation/storage mapping to copy `ScopeKind`.

Add regression fixtures for all of the following:

1. A literal pre-feature JSON record without `ScopeKind` loads as `Project`.
2. A General thread round-trips with empty `ProjectPath`, its managed `WorkspacePath`, and string `ScopeKind`.
3. Project and General records with different IDs remain separately queryable and active.
4. A General attachment draft with `ThreadId = null` survives a snapshot and is transferred to the first created General thread.
5. Unknown scope strings fail closed or map through an explicit compatibility policy; they must not become General implicitly.

No bulk rewrite of settings is required on load. The new field is written on the next normal save.

## 5. App-server request changes

### 5.1 Core request model

In `src/SynthiaCode.Core/Codex/AppServer/CodexAppServerModels.cs`:

- add `string? Cwd = null` to `CodexThreadStartOptions` without breaking existing named callers;
- prefer placing it at the end of the positional record or converting call sites to named arguments so existing permission arguments cannot shift silently;
- keep resume, fork, and turn requests on their current required `string Cwd` contract.

### 5.2 Infrastructure serialization

In `src/SynthiaCode.Infrastructure/Codex/CodexAppServerClient.cs`:

- validate a supplied thread-start `Cwd` as non-whitespace;
- serialize `params.cwd` only when it is present;
- preserve current default behavior for `CodexThreadStartOptions.Default`;
- do not normalize paths in the protocol client; callers own host-path semantics.

Add protocol assertions proving:

- default thread start omits `cwd`;
- explicit General/project thread start emits the exact path;
- turn start continues to require and emit `cwd`;
- permission-profile versus legacy-sandbox exclusivity remains unchanged.

## 6. MainViewModel orchestration

### 6.1 Inject and initialize the General workspace

Wire `IGeneralWorkspaceService` through `AppServices.Create`, `App.xaml.cs`, and `MainViewModel`. Resolve/create the root during `InitializeAsync` before navigation and draft restoration. Store either the validated path or a captured initialization error.

Failure behavior:

- project flows remain available;
- General creation and implicit General submission are disabled;
- the status message explains that SynthiaCode could not prepare its General workspace and includes the safe path/error detail;
- never fall back to a broader directory.

### 6.2 Split creation contexts

Introduce a private immutable request such as:

```text
ThreadCreationContext
  ScopeKey
  WorkspacePath
  WorkspaceMode
```

Refactor `NewThreadAsync`, `EnsureActiveThreadAsync`, and `CreateThreadState` so they consume this context instead of reading `SelectedProjectPath` after awaits.

Creation sequence:

1. Resolve the context synchronously from the user action.
2. Validate auth, Codex installation, workspace existence, and project/worktree eligibility.
3. Ensure the app-server session.
4. Call `thread/start` with resolved permissions, model override, and the context workspace as `cwd`.
5. Create a worktree only for a project context that requested it.
6. Persist `ScopeKind`, owning `ProjectPath`, and final `WorkspacePath`.
7. Set the new thread active only within its scope.
8. Restore the per-thread conversation/queue workspaces and transfer a matching new-thread attachment draft.
9. Refresh navigation and select the new thread.

If worktree creation fails after `thread/start`, retain current failure semantics but log the orphan Codex thread ID. A later hardening item may archive that upstream thread; this projectless feature must not introduce an automatic destructive cleanup.

### 6.3 Resolve active scope and workspace centrally

Replace direct `SelectedProjectPath` fallbacks with two helpers:

```text
GetActiveScopeKey()
GetActiveWorkspacePath()
```

Resolution order:

1. selected thread's stored scope and workspace;
2. selected project and its checkout path;
3. General scope and managed General root.

The third branch makes a no-project first submission deterministic. A stale selected thread workspace must fail with its existing actionable “workspace unavailable” error; it must not fall through to another scope.

Update these paths to use the helpers or an immutable captured context:

- `SubmitPromptAsync` and `EnsureActiveThreadAsync`;
- resume and fork;
- queued follow-up capture and background dispatch;
- `CreateTerminalContext`;
- attachment add/open/draft capture/restore;
- execution-policy and permission-profile refresh;
- active-state save and notification-triggered archive state;
- shutdown snapshot persistence.

### 6.4 Separate command eligibility

Replace the current all-purpose `CanManageThreads` with narrow predicates:

- `CanCreateAnyThread`: Codex present, authenticated, not shutting down;
- `CanCreateGeneralThread`: above plus General workspace available;
- `CanCreateProjectThread`: above plus selected/target project path;
- `CanUseSelectedThread`: authenticated and selected thread has a valid effective workspace;
- `CanUseProjectWorktree`: selected thread is project-scoped and the existing repository/ownership checks pass.

Archive, unarchive, resume, and fork must no longer require `SelectedProjectPath`. Worktree removal must continue to require a project-scoped owned worktree.

### 6.5 Restore and selection flows

General threads must be visible after startup even though SynthiaCode currently restores full presentation state only after a project is selected.

Refactor `RestorePersistedThreadState` and `RefreshProjectThreads` into scope-aware equivalents. Navigation refresh should always query General summaries plus every recent project's summaries. Selecting a General thread should:

- capture the prior scope's composer draft;
- clear `SelectedProjectPath` without treating it as a browse failure;
- load the General thread collection;
- restore the selected thread's transcript and queued-follow-up workspace;
- refresh terminal, Git empty state, permissions, title, and attachment draft;
- persist active General selection.

Selecting a project or project thread should preserve the current recent-project timestamp and expansion behavior. Avoid binding General selection directly to a setter that performs synchronous partial switching; route it through one explicit selection method so scope, project, thread, draft, terminal, and Git state move atomically on the UI thread.

### 6.6 Fork and lifecycle semantics

- Forking a General thread creates another General thread that shares the managed General workspace and copies conversation history through app-server, matching multiple threads over one checkout.
- Forking a project current-checkout thread retains the project checkout.
- Forking a project worktree thread retains the existing owned-worktree behavior.
- Archive/unarchive works by thread ID and stored scope.
- General archive does not delete the shared General workspace.
- General threads display a workspace label such as `General workspace`, never `Current checkout`.

## 7. Navigation and shell UX

### 7.1 Project/thread navigation

In `ProjectThreadViewModel`:

- add a `GeneralThreads` observable collection or a small `GeneralThreadNavigationViewModel` with thread count and running summary;
- add `NewGeneralThreadCommand` and explicit General-thread selection routing;
- keep `Projects`, `Threads`, and the project-row commands as compatibility surfaces;
- refresh General and project groups in one navigation pass;
- ensure command state changes when auth, Codex discovery, General workspace availability, selection, or shutdown state changes.

In `ProjectThreadView.xaml`:

1. Rename the top-level navigation concept from only `Projects` to `Threads` or `Tasks`.
2. Add a visible `New thread` button bound to `NewGeneralThreadCommand`.
3. Render a fixed `General` disclosure section before recent projects, with count/running summary and an empty state.
4. Keep a separate `Projects` heading and `Add` button for folder selection.
5. Extract the repeated thread row into a shared `DataTemplate` used by General and project groups.
6. Keep Resume/Fork/Archive/Unarchive actions for General rows; hide or disable `Remove worktree` by its existing command predicate.
7. Add automation names that distinguish `New General thread`, `General threads`, and project-specific creation.

The global creation action must not silently create inside the currently selected project. Project creation remains attached to the owning project row.

### 7.2 Main shell labels

`MainWindow.xaml` currently says “Choose a project to start working” whenever `SelectedProjectPath` is null. Replace the path binding with a view-model context label:

- General selected: `General workspace`;
- project selected: existing project path;
- no selection but General available: `Create a thread or choose a project`;
- General unavailable: concise workspace-error state.

Keep the main workspace heading bound to the selected thread title. Update the status-bar shortcut copy so `Ctrl+O` remains “add project” rather than implying that a project is required.

### 7.3 Git and Terminal surfaces

For General:

- Task remains fully enabled.
- Terminal uses the selected thread ID as its session key and the managed General root as its working directory.
- Changes shows `No project attached to this thread` and offers `Choose a project` without implying that the thread is invalid.
- Git mutation/open commands remain disabled because the active scope is not project-scoped, even if the user manually initializes a repository inside the General directory. Enabling General Git is a separate product decision.
- Worktree creation/removal is not shown as available.

Update `GitContext` to carry the scope kind, rather than inferring product eligibility from a nullable path. `TerminalContext` can remain key/path based because it already works for any valid workspace.

## 8. Attachments, queues, and permissions

### 8.1 Attachment drafts

General scope makes pre-thread attachments possible because the managed root exists before a Codex thread does.

Generalize `CaptureAttachmentDraft` and `RestoreAttachmentDraft` to accept `ThreadScopeKey`:

- project draft key: `(Project, normalized project path, thread ID?)`;
- General draft key: `(General, no project path, thread ID?)`.

When the first General thread is created from a no-thread composer, transfer the `(General, null thread ID)` draft to that thread, matching current project behavior. Workspace references inside the General root remain workspace references; external files/folders continue to be imported into managed attachment storage.

### 8.2 Queued follow-ups

Queued entries already snapshot `WorkspacePath`, so their domain model does not need a new scope field. Verify that:

- enqueue captures the General root;
- background dispatch never reads the newly selected project;
- restored General queues validate the General root and start with the captured cwd;
- archive remains blocked while the queue is non-empty;
- selecting a project while a General turn completes cannot reroute the queued start.

### 8.3 Execution permissions

Change policy context from `SelectedProjectPath` to the effective active workspace. Named permission-profile discovery requires a `cwd`, and the General root supplies one safely.

Verify that Ask for approval still resolves to the workspace boundary and that the General root is the only General writable root implied by the selected policy. Do not use the user profile as a fallback. Managed requirements, stale-profile fail-closed behavior, reviewer selection, and legacy sandbox compatibility remain unchanged.

## 9. File-level implementation map

| File | Implemented change |
| --- | --- |
| `src/SynthiaCode.Core/Settings/ThreadScope.cs` | New scope enum/key and normalization invariants |
| `src/SynthiaCode.Core/Settings/AppSettings.cs` | Persist scope on threads and composer drafts; add runtime scope helpers |
| `src/SynthiaCode.Core/Settings/AppSettingsSnapshot.cs` | Deep-copy new scope fields |
| `src/SynthiaCode.Core/Settings/ThreadStore.cs` | Scope-aware query/active/archive/upsert operations and project compatibility wrappers |
| `src/SynthiaCode.Core/Workspaces/IGeneralWorkspaceService.cs` | New testable General-workspace contract |
| `src/SynthiaCode.Infrastructure/Workspaces/GeneralWorkspaceService.cs` | Contained app-data directory creation and validation |
| `src/SynthiaCode.Core/Codex/AppServer/CodexAppServerModels.cs` | Add optional `Cwd` to thread-start options |
| `src/SynthiaCode.Infrastructure/Codex/CodexAppServerClient.cs` | Serialize optional `thread/start.params.cwd` |
| `src/SynthiaCode.App/AppServices.cs` | Construct and expose General workspace service |
| `src/SynthiaCode.App/App.xaml.cs` | Inject service into MainViewModel |
| `src/SynthiaCode.App/ViewModels/MainViewModel.cs` | Scope-aware creation, submission, selection, persistence, workspace, lifecycle, attachment, policy, Git, and terminal routing |
| `src/SynthiaCode.App/ViewModels/ProjectThreadViewModel.cs` | General group, commands, selection, and navigation refresh |
| `src/SynthiaCode.App/ViewModels/GitViewModel.cs` | Scope-aware empty state and command eligibility |
| `src/SynthiaCode.App/Views/ProjectThreadView.xaml` | General section, explicit General creation action, lifecycle menu, and accessibility labels |
| `src/SynthiaCode.App/Views/GitView.xaml` | General “no project attached” empty state |
| `src/SynthiaCode.Tests/ProjectlessThreadTests.cs` | Store, workspace, protocol, creation, first-turn, failure-isolation, and lifecycle regressions |
| `src/SynthiaCode.Tests/Program.cs` | Registers projectless coverage and composes the General workspace in existing view-model tests |
| `src/SynthiaCode.Tests/Phase5DNavigationTests.cs` | General grouping/selection and preserved project-row behavior |
| `src/SynthiaCode.Tests/ResponsiveLayoutTests.cs` | General section layout, wrapping, menu, and accessibility checks |
| `feature_parity.md` | Record completed General-thread parity and remaining chat-management gaps |

If `MainViewModel` grows materially during orchestration changes, extract a small `ThreadContextCoordinator` only after scope tests are green. Do not combine this feature with a broad MVVM rewrite.

## 10. Implementation sequence

### Phase 1: Scope and workspace foundation

1. Add failing Core tests for legacy project default, General store round-trip, active selection per scope, and General archive.
2. Add `ThreadScopeKind`/`ThreadScopeKey`.
3. Add General workspace service tests for idempotent creation, absolute path, containment, and occupied-path failure.
4. Implement and compose the workspace service.

**Exit gate:** Store and workspace tests pass; no UI or app-server behavior changes yet.

### Phase 2: Persistence compatibility

1. Add scope fields to persisted/presentation thread and draft models.
2. Update snapshots and store mapping.
3. Load a literal old settings fixture and round-trip a mixed Project/General settings graph.

**Exit gate:** Existing settings tests plus new compatibility tests pass with stable JSON property names.

### Phase 3: Protocol cwd support

1. Add a failing client serialization test for thread-start cwd.
2. Extend `CodexThreadStartOptions` safely using named arguments.
3. Serialize cwd conditionally.
4. Re-run approval/permission lifecycle serialization tests.

**Exit gate:** Thread-start default remains backward compatible and explicit cwd is exact.

### Phase 4: Scope-aware orchestration

1. Add failing integration tests for General explicit creation and no-project first submission.
2. Split command predicates and immutable creation contexts.
3. Refactor workspace/scope resolution and `CreateThreadState`.
4. Remove project guards from General-valid lifecycle operations.
5. Generalize restore, selection, active save, archive notifications, and shutdown.

**Exit gate:** General create/turn/resume/fork/archive/restart tests pass; project/worktree tests remain green.

### Phase 5: Navigation and feature contexts

1. Add General navigation projection and explicit selection command.
2. Add XAML General section and global creation action using one shared thread row template.
3. Update shell labels, Git empty state, terminal context, draft routing, and permission cwd.
4. Add responsive/accessibility tests.

**Exit gate:** General threads are discoverable and fully usable without selecting a project; project navigation behavior is unchanged.

### Phase 6: Full regression and documentation

1. Run formatting/static checks and the complete suite.
2. Perform the manual matrix below with a fake and, when explicitly enabled, live app-server.
3. Update architecture and README only after observed behavior matches the plan.

## 11. Automated verification matrix

### Core/store

- legacy record without `ScopeKind` is Project;
- General record does not appear in any project query;
- project record does not appear in General query;
- active flags are unique within, not across, scopes;
- upsert refuses a scope change for an existing thread ID;
- archive/unarchive locates by thread ID independent of UI selection;
- General workspace path round-trips and snapshots deeply;
- General pre-thread attachment draft transfers correctly.

### Protocol

- `thread/start` includes General cwd;
- default options omit cwd;
- resume/fork/turn use General cwd;
- queued General turn uses captured cwd;
- permission fields remain mutually exclusive and unchanged.

### MainViewModel

- `NewGeneralThreadCommand.CanExecute` is true when authenticated with no project;
- explicit General creation clears project context but does not remove/reorder recent projects;
- first no-project submission creates one thread then one turn, not two threads;
- General selection restores transcript and draft after switching to a project and back;
- General fork remains General;
- General archive/unarchive works while a different project is selected when the notification arrives;
- missing General root disables only General creation;
- project-row plus still creates current checkout;
- project worktree creation/removal behavior is unchanged;
- terminal starts in General root and remains isolated by thread key;
- Git commands are disabled with the General empty state;
- external and General-workspace attachments serialize correctly;
- background queued General follow-up cannot drift to a selected project;
- policy/profile discovery receives General root.

### WPF/accessibility

- New General thread action is visible at 800 px minimum width;
- General and long project/thread labels wrap without horizontal scrolling;
- General and project rows share selected/action-menu styling;
- automation names identify scope and action;
- account footer remains fixed while navigation scrolls.

## 12. Manual acceptance matrix

| Scenario | Expected result |
| --- | --- |
| Fresh app, no recent projects, click New thread | General thread appears and is selected |
| Fresh app, type prompt and press Ctrl+Enter | General thread is created implicitly and turn runs in managed General root |
| Add external file before first General turn | Managed attachment is retained and sent; no project prompt appears |
| Start terminal in General thread | PowerShell starts in managed General root |
| Open Changes in General thread | “No project attached” state; no Git mutation commands |
| Add/select project, then use project-row `+` | Current-checkout project thread is created |
| While project selected, use global New thread | New thread in the selected project, preserving the existing shortcut behavior |
| While project selected, click New in General section | New General thread; project remains in recent projects |
| Fork General thread | Fork appears in General and uses General root |
| Archive/unarchive General thread | State persists and workspace remains intact |
| Queue follow-up, switch to project, complete General turn | Follow-up starts on General thread with General cwd |
| Restart app | General group and persisted threads remain visible and resumable |
| General workspace path is blocked by a file/ACL | General action disabled with actionable error; project flows still work |

## 13. Final verification commands

Run from the repository root:

```powershell
dotnet test SynthiaCode.sln
dotnet run --project src\SynthiaCode.Tests\SynthiaCode.Tests.csproj
dotnet build SynthiaCode.sln -c Release
git diff --check
```

When a compatible local Codex installation and explicit opt-in are available, also run the existing live smoke test with `SYNTHIACODE_RUN_LIVE_CODEX_SMOKE=1` and verify a General thread's `thread/start`, `turn/start`, resume, and archive lifecycle.

## 14. Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Empty project path is interpreted as a corrupt project | Explicit `ScopeKind`; project queries exclude General before path normalization |
| General thread accidentally uses the previously selected project | Immutable creation context and explicit General activation before async calls |
| App-server inherits install/portable cwd | Always send validated managed General cwd from SynthiaCode |
| Workspace-write becomes the whole user profile | Never use home as fallback; root General under app data and validate containment |
| General archive notification mutates the wrong project record | Archive by routed thread ID and stored scope |
| Existing JSON is rewritten incompatibly | Preserve `ProjectThreads`/`ProjectPath` names; missing scope defaults to Project; literal legacy tests |
| General and project attachment drafts collide | Scope-aware draft keys plus project path only for Project scope |
| Queued follow-up runs in selected project | Continue using immutable queued workspace snapshot; add cross-scope regression |
| Git/worktree commands operate on General root | Carry scope in Git context and require Project scope for these commands |
| General root cannot be created | Fail General closed, explain error, preserve project functionality |
| Shared General files surprise users | Label it clearly as `General workspace`; do not auto-delete; document that General threads share it |

## 15. Explicit non-goals for the first slice

- Renaming the persisted `ProjectThreads` JSON collection.
- Importing or listing every Codex thread that SynthiaCode did not create.
- Allowing the user to choose a custom General workspace path.
- Creating or deleting a separate directory for every General thread.
- Enabling Git Changes or assistant worktrees for General scope.
- Automatically deleting General workspace contents on archive.
- Making turn `cwd` nullable inside SynthiaCode.
- Redesigning the entire MainViewModel or navigation architecture beyond the scope abstraction required here.

These can be evaluated after the core projectless flow is stable and backward-compatible.

## 16. Completion record

The implementation was completed using a test-first workflow. Five initially failing projectless tests established the scope/store, managed-workspace, protocol `cwd`, explicit creation, and implicit first-turn requirements before product code was added. Lifecycle and General-workspace failure-isolation regressions were added during hardening. The final console runner passes all **145 tests**.

### Implemented outcome

| Area | Completed behavior |
| --- | --- |
| Scope and persistence | Explicit `Project`/`General` scope keys; mixed-scope storage, active selection, archive lookup, snapshots, and legacy default-to-Project compatibility |
| Workspace | Idempotent, contained `%LOCALAPPDATA%\SynthiaCode\workspaces\general` creation with fail-closed error handling |
| Protocol | Optional `thread/start.params.cwd`; MainViewModel supplies an absolute workspace to thread start and continues supplying it to resume, fork, turn, queue, permission, attachment, and terminal flows |
| Creation | No-project global New and first prompt create General threads; dedicated General action works from any selected project; project-row checkout/worktree actions are unchanged |
| Lifecycle | General selection, restore, resume, fork, archive, unarchive, transcript state, queued work, attachments, and terminal routing use the General scope/workspace |
| Navigation | Dedicated General group above Projects with selection-safe refresh and lifecycle actions |
| Git/worktrees | General worktree removal is ineligible; Git shows a no-project-attached state while Task and Terminal remain usable |
| Failure isolation | A blocked General workspace disables General/no-scope creation without disabling project-thread creation |

### Differences from the original draft

- The global New command creates in the current scope for backward compatibility. The dedicated General-section New command is the unambiguous way to switch from a selected project to General.
- A separate `GeneralThreadNavigationViewModel` and broad `MainViewModel` rewrite were unnecessary; the existing navigation view model now owns the General collection and commands.
- `SystemPaths`, `MainWindow.xaml`, README, and architecture documentation required no functional change for this slice. The implementation record is maintained here and in `feature_parity.md`.
- The XAML reuses the same thread state/action concepts but keeps separate General/project item templates so the project-only Remove worktree action cannot leak into General rows.

### Verified commands

```powershell
dotnet build src\SynthiaCode.Tests\SynthiaCode.Tests.csproj --no-restore
dotnet run --project src\SynthiaCode.Tests\SynthiaCode.Tests.csproj --no-build
```

Final solution tests, Debug/Release rebuilds, executable-path verification, and `git diff --check` are recorded in the implementation handoff.
