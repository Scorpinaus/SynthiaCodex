# Native Windows Codex Assistant Implementation Plan

## 1. Objective

Build a lightweight, Windows-native desktop assistant that launches and talks to `codex app-server` as its primary execution engine while presenting a focused graphical workflow for local software development.

The first version should feel like a small native developer tool, not a full clone of the first-party Codex desktop app. It should prioritize fast startup, predictable Windows behavior, clear task streaming, safe defaults, and room to grow into richer Codex features later.

## 2. Product Direction

### Primary user

The primary user is a developer working locally on Windows who wants Codex available through a clean desktop surface instead of a terminal-only flow.

### Core promise

Open a project, describe a task, watch Codex work, inspect results, and continue iterating without leaving a native Windows app.

### Design posture

- Native Windows first.
- Lightweight over maximal feature parity.
- Local project safety by default.
- Codex app-server compatibility over custom agent behavior.
- Incremental depth: start with app-server over stdio, then add richer thread, Git, worktree, and automation workflows.

## 3. Key Decisions

### UI framework

Use **WPF on .NET 10** for the first implementation.

Rationale:

- Mature Windows desktop framework.
- Good process, filesystem, Git, terminal, and OS integration.
- Lower complexity than WinUI 3 for an internal/developer tool.
- Avoids Electron's Chromium/Node runtime footprint.
- Allows straightforward single-instance, tray, settings, and native file picker behavior.

WinUI 3 remains a possible later UI refresh if Windows 11 Fluent polish becomes more important than implementation stability.

### .NET version

Target **.NET 10** across the WPF app, Core, Infrastructure, and Tests projects.

Rationale:

- Keeps the application on the current long-term support .NET release.
- Uses the installed .NET 10 SDK and Windows Desktop runtime toolchain.
- Gives every project one consistent target framework: `net10.0` for libraries and `net10.0-windows` for WPF-facing projects.
- Allows self-contained portable builds to run without a separately installed .NET 10 runtime.

Pin the repository to the .NET 10 SDK family through `global.json`, while allowing newer .NET 10 feature bands so servicing updates do not require repository churn.

### Codex integration path

Use `codex app-server --listen stdio://` as the primary integration path from the first usable build.

Why:

- It is the interface intended for rich Codex clients.
- It exposes thread and turn primitives instead of forcing every request into a one-shot process.
- It supports streamed agent events, conversation lifecycle, approvals, and future resume/fork/archive workflows.
- Stdio keeps the integration local and avoids exposing a WebSocket listener.

Keep `codex exec --json` only as a fallback and utility path for diagnostics, simple one-off automation, or compatibility if app-server is unavailable for a specific Codex version.

Tradeoff:

- The MVP has more protocol work up front.
- The app must handle app-server lifecycle, request IDs, notifications, version compatibility, and recovery.

### Safety defaults

Default to bounded local execution:

- `--sandbox workspace-write`
- approval policy configurable, but not bypassed by default
- no `--dangerously-bypass-approvals-and-sandbox`
- no unauthenticated non-loopback WebSocket listener
- no direct storage of API keys in app settings for v1
- app-server transport defaults to stdio

### Distribution

Use a predictable portable folder for local testing and zip sharing, then keep a normal Windows installer path for later releases:

- development/testing: root-level portable folder produced by `scripts/publish-portable.cmd`
- local zip artifact: `portable\NativeCodexAssistant\`
- internal preview: framework-dependent or self-contained installer
- public/internal release: MSIX or signed installer

## 4. Reference Codex Capabilities

The assistant should align with documented Codex surfaces:

- CLI interactive and non-interactive modes
- ChatGPT sign-in for subscription access
- API-key sign-in for usage-based access
- `codex app-server` JSON-RPC over stdio
- thread, turn, and item event streaming
- `codex exec --json` only as fallback/utility
- Codex Windows sandbox behavior
- shared Codex configuration under the user's Codex home
- skills, plugins, MCP, rules, and model configuration where exposed through Codex config

References:

- https://developers.openai.com/codex/cli/reference
- https://developers.openai.com/codex/auth
- https://developers.openai.com/codex/pricing
- https://developers.openai.com/codex/noninteractive
- https://developers.openai.com/codex/app-server
- https://developers.openai.com/codex/app/features
- https://developers.openai.com/codex/app/windows
- https://developers.openai.com/codex/windows
- https://github.com/openai/codex

## 5. High-Level Architecture

```text
Native WPF UI
  |
  |-- Project and task views
  |-- Thread/task timeline
  |-- Diff and file preview panes
  |-- Settings and diagnostics
  |
Application Services
  |
  |-- CodexProcessService
  |-- CodexAppServerClient
  |-- CodexThreadService
  |-- CodexCliUtilityRunner
  |-- AuthService
  |-- ProjectService
  |-- GitService
  |-- TerminalService
  |-- SettingsService
  |-- AutomationService
  |
Local System
  |
  |-- codex.exe
  |-- git.exe
  |-- PowerShell / Windows Terminal / ConPTY
  |-- user projects
  |-- %USERPROFILE%\.codex
```

## 6. Proposed Repository Structure

```text
NativeCodexAssistant/
  src/
    NativeCodexAssistant.App/
      App.xaml
      MainWindow.xaml
      Views/
      ViewModels/
      Themes/
      Resources/
    NativeCodexAssistant.Core/
      Codex/
      Projects/
      Git/
      Terminal/
      Automations/
      Auth/
      Settings/
      Security/
    NativeCodexAssistant.Infrastructure/
      Processes/
      Persistence/
      Windows/
      Logging/
    NativeCodexAssistant.Tests/
  docs/
    architecture.md
    codex-app-server-notes.md
  implementation_plan.md
```

This file can move into `docs/` once a project scaffold exists.

## 7. Phase 0: Project Foundation

### Goals

Create the WPF solution and core service boundaries without committing to advanced Codex protocol work yet.

### Tasks

- Create WPF app project.
- Create core library for app-neutral logic.
- Create test project.
- Add basic dependency injection.
- Add structured logging.
- Add app settings storage.
- Add global exception handling.
- Add single-instance app behavior.
- Add theme resources for light/dark mode.
- Add shell layout:
  - left project rail
  - central task/thread surface
  - right details pane
  - bottom status bar

### Deliverables

- App opens on Windows.
- App can select a project folder.
- App persists recent projects.
- App detects whether `codex.exe` is available.
- App detects whether Codex appears to have usable authentication.
- App can show a diagnostics panel with Codex path and version.

### Acceptance criteria

- App starts in under a few seconds on a normal Windows machine.
- Missing Codex installation produces a clear setup state.
- Missing or expired authentication produces a clear sign-in state.
- Project picker works with normal Windows paths.
- Settings are persisted locally.

## 8. Phase 1: App-Server MVP

### Goals

Run Codex tasks from the native UI through `codex app-server --listen stdio://`.

The first app-server build can support one active thread at a time, but it should use the real thread/turn lifecycle from day one so later parallelism and resume features do not require a rewrite.

### Codex command shape

```powershell
codex app-server --listen stdio://
```

The app communicates with the process through newline-delimited JSON messages over stdio.

### Minimum protocol flow

1. Start the app-server process.
2. Send `initialize` with native assistant client metadata.
3. Send the `initialized` notification.
4. Start a thread with the selected model/profile defaults.
5. Start a turn with user input, project `cwd`, and sandbox settings.
6. Stream notifications into the task timeline.
7. Mark the turn complete when `turn/completed` arrives.
8. Support cancellation through the app-server interrupt/cancel flow.

### Request defaults

Default turn settings:

- selected project path as `cwd`
- workspace-write sandbox
- configured model/profile only when the user overrides defaults
- web search setting inherited from Codex config unless explicitly set
- no full-access mode from the primary task composer

### Services

#### CodexProcessService

Responsibilities:

- locate `codex.exe`
- run `codex --version`
- run `codex doctor` on demand
- start processes with redirected stdout/stderr
- start and stop app-server safely
- normalize process exit state

#### CodexAppServerClient

Responsibilities:

- manage app-server process lifetime
- write JSON messages to stdin
- read JSON messages from stdout
- allocate request IDs
- correlate responses with pending requests
- route notifications to thread state
- initialize the connection
- start threads
- start turns
- interrupt or cancel active turns
- surface protocol errors clearly

#### CodexThreadService

Responsibilities:

- own UI-facing thread state
- map app-server events into timeline items
- track active turn status
- capture final assistant messages
- capture command, file, and tool events
- expose cancellation and retry actions

#### CodexCliUtilityRunner

Responsibilities:

- run non-agent utility commands such as `codex --version`, `codex doctor`, schema generation, and optional fallback `codex exec --json`
- keep utility process handling separate from the app-server client

### MVP UI

Views:

- Project picker
- Prompt composer
- Running task timeline
- Final response panel
- Raw event inspector for debugging
- Basic settings panel

Timeline event types:

- thread started
- turn started
- command execution started/completed
- file change detected
- agent message
- agent message delta
- tool progress
- error
- turn completed

### Acceptance criteria

- User can choose a Git project.
- User can submit a prompt.
- App starts `codex app-server` over stdio.
- App initializes the app-server connection successfully.
- App starts a thread and turn for the selected project.
- If authentication is missing or expired, the app shows sign-in actions instead of dropping the user into a protocol failure.
- App streams app-server notifications without freezing.
- App shows final response.
- App shows protocol, process, and turn failures clearly.
- User can cancel a running turn.
- App-server process shutdown does not leave orphaned child processes.

## 9. Phase 2: Project and Git Experience

### Goals

Make the app useful for real local development by adding project state, diffs, and basic Git operations.

### GitService

Responsibilities:

- detect Git repository root
- read current branch
- read working tree status
- compute file diffs
- compute staged diffs
- stage/unstage files
- revert selected files or hunks only after explicit confirmation
- create commits

Implementation options:

- Start with `git.exe` process calls for correctness and familiarity.
- Consider LibGit2Sharp later if process invocation becomes limiting.

### UI

Views:

- Changed files list
- Diff viewer
- Stage/unstage controls
- Commit message editor
- Branch/status indicator
- Open in editor action
- Reveal in Explorer action

### Safety

- Destructive operations require confirmation.
- Revert operations must show exact target files.
- No automatic commit/push in MVP.

### Acceptance criteria

- App displays files changed by Codex.
- User can inspect diffs after a task.
- User can stage and commit changes from the app.
- App refuses Git actions outside a detected repository unless explicitly supported.

## 10. Phase 3: Advanced Thread Lifecycle

### Goals

Move beyond the single-thread MVP by adding multiple persistent Codex threads, resume/fork/archive, richer in-flight steering, and stronger app-server recovery.

### Protocol depth

Phase 1 proves the basic initialize/thread/turn flow. Phase 3 expands that into the full native client model:

- multiple project threads
- thread resume
- thread fork
- thread archive/unarchive
- active turn steering when supported
- clean turn interruption
- recovery after app-server restart
- notification opt-out for events the UI does not need
- version-aware protocol handling

### Schema strategy

Generate version-matched schemas during development:

```powershell
codex app-server generate-json-schema --out ./schemas
```

Keep generated schemas or protocol bindings tied to the Codex CLI version used for development.

### Service expansion

#### CodexAppServerClient

Additional responsibilities:

- start/resume/fork/archive threads
- steer active turns if supported
- surface approval and command events
- reconnect or restart app-server after failure
- replay enough local state to recover the visible UI
- expose app-server capability/version metadata

#### ThreadStore

Responsibilities:

- map local UI records to Codex thread IDs
- persist project/thread relationships
- store display metadata
- keep enough local state to recover UI after restart
- track archived/pinned state where supported
- store thread mode metadata such as local checkout versus worktree

### UI additions

- Thread list per project
- Resume previous thread
- Fork thread
- Archive/unarchive thread
- Parallel running indicators
- In-flight steering composer
- Turn cancellation status
- App-server health indicator

### Acceptance criteria

- User can create multiple threads for a project.
- User can resume an earlier thread.
- User can run parallel threads without UI blocking.
- User can cancel a turn.
- App handles app-server crashes with recovery messaging.
- App can recover visible thread metadata after restart.

## 11. Phase 4: Worktrees

### Goals

Support isolated parallel work similar in spirit to the first-party app, while keeping implementation explicit and understandable.

### WorktreeService

Responsibilities:

- create Git worktree for a new task
- derive safe worktree folder names
- list active assistant-created worktrees
- remove completed worktrees after confirmation
- map thread to worktree path
- prevent accidental deletion of user-created worktrees

### Proposed layout

```text
<repo>/
  .codex-assistant/
    worktrees/
      <branch-or-task-id>/
```

Alternative:

```text
<parent-folder>/
  <repo-name>.worktrees/
    <task-id>/
```

The second option may be safer because Git worktrees are commonly siblings of the primary repository instead of nested inside it.

### Acceptance criteria

- User can start a task in the current checkout or a new worktree.
- Worktree tasks do not modify the user's main checkout.
- App clearly labels the active path for each thread.
- App can clean up assistant-created worktrees safely.

## 12. Phase 5: Integrated Terminal

### Goals

Add a native terminal panel scoped to the active project or worktree.

### TerminalService

Implementation direction:

- Use Windows ConPTY through a .NET terminal control or wrapper.
- Start with PowerShell.
- Add Command Prompt, Git Bash, and WSL profiles later.

### UI

- Terminal toggle
- Terminal tabs per project/thread
- Copy/paste
- Clear terminal
- Kill terminal session
- Current working directory indicator

### Acceptance criteria

- User can run project commands without leaving the app.
- Terminal starts in the active project/worktree.
- Terminal output is readable and scrollable.
- Closing a thread does not orphan hidden terminal processes.

## 12A. Phase 5A: UI/UX Modernization

**Status:** Complete — 15 July 2026. The completed acceptance checklist and verification record are maintained in `phase_5a_ui_ux.md`.

### Goals

Improve the application's usability, information hierarchy, responsiveness, accessibility, and visual polish before adding new Phase 6 capabilities.

This phase changes how existing Phase 0-5 capabilities are presented, but does not add skills, plugins, MCP, automations, or other power features.

### Product experience

- Make the active project, thread, workspace, and task state immediately understandable.
- Prioritize the task conversation and prompt composer over diagnostics and protocol details.
- Replace the permanently visible diagnostics column with an optional details surface.
- Simplify thread actions so common actions are prominent and secondary lifecycle actions remain discoverable.
- Provide deliberate empty, loading, authentication, failure, cancellation, and recovery states.
- Improve the readability of assistant responses, commands, file changes, Git diffs, and terminal output.

### Responsive shell

- Support wide, medium, and narrow desktop layouts.
- Make the project/thread rail collapsible.
- Make diagnostics and raw events a drawer, panel, or dedicated view instead of permanent workspace chrome.
- Preserve useful workspace space below the current 1100-pixel minimum width where practical.
- Persist user-facing layout preferences that materially improve continuity.

### Accessibility and interaction

- Define a predictable keyboard focus order and shortcuts for primary actions.
- Add accessible names and automation properties where visible labels are insufficient.
- Verify light, dark, and system themes with sufficient contrast.
- Verify high-DPI layouts, text scaling, keyboard-only use, and basic screen-reader behavior.
- Keep destructive Git and worktree actions explicitly confirmed.

### Important boundary

Phase 5A should avoid a broad application-layer rewrite. Small extractions needed to make a view testable are allowed, but service decomposition, persistence redesign, protocol restructuring, and performance-focused refactoring belong to Phase 5B.

### Acceptance criteria

- The primary task workflow is visually dominant and diagnostics are available on demand.
- Existing project, thread, worktree, Git, authentication, and terminal workflows remain available.
- The shell remains usable at the agreed narrow-window target and at high DPI.
- Primary workflows are operable with the keyboard.
- Empty, busy, failed, cancelled, and recovered states are clear and actionable.
- All existing behavioral tests continue to pass and focused UI-state tests cover the redesigned shell.

## 12B. Phase 5B: Architecture and Performance Optimization

**Status:** Complete — 15 July 2026. The completed checklist, architecture, measurements, and verification record are maintained in `phase_5b_architecture_optimization.md` and `docs/current-architecture.md`.

### Goals

Reduce application-layer concentration, improve streaming and rendering efficiency, and establish maintainable feature boundaries before Phase 6 expands the product surface.

### Application architecture

- Reduce `MainViewModel` to a shell-level coordinator.
- Extract focused project/thread, task, Git, terminal, and diagnostics view models.
- Split `MainWindow.xaml` into feature-oriented WPF views or user controls.
- Keep app-neutral contracts and state in Core, Windows/process implementations in Infrastructure, and presentation behavior in App.
- Separate persisted thread records from WPF notification and presentation concerns.
- Preserve the existing dependency direction from App and Infrastructure toward Core.

### Runtime optimization

- Establish baselines for startup time, long-stream memory use, dispatcher activity, terminal rendering, and settings writes.
- Virtualize long timeline, diagnostics, raw-event, thread, and changed-file collections where appropriate.
- Batch or throttle high-frequency streamed deltas before dispatching UI updates.
- Avoid recreating the complete terminal text value for every small output chunk.
- Bound in-memory diagnostic and streamed-event buffers as well as persisted history.
- Review settings persistence frequency and serialize atomically without blocking interactive work.
- Keep app-server, terminal, and Git process lifecycles deterministic during recovery and shutdown.

### Protocol and testability

- Keep `CodexAppServerClient` isolated behind an app-facing session/coordinator boundary.
- Retain version-aware protocol parsing and raw-event diagnostics.
- Split the test runner into feature-oriented test files or projects while preserving the lightweight local workflow.
- Add regression tests for extracted coordinators, batched streaming, bounded buffers, and persistence behavior.

### Important boundary

Phase 5B optimizes and reorganizes existing behavior. It must not introduce Phase 6 configuration surfaces or silently change Codex, Git, worktree, terminal, sandbox, approval, or authentication semantics.

### Acceptance criteria

- No single presentation view model owns all application workflows.
- Major WPF surfaces can be developed and tested independently.
- Long timelines, raw events, and terminal sessions remain responsive within the agreed stress-test bounds.
- Settings and thread state remain backward compatible or include an explicit migration path.
- App-server recovery and application shutdown do not leak processes or lose visible thread state.
- The full behavioral suite, new performance regressions, build, and portable publish gates pass.

## 12C. Phase 5C: Multi-turn Conversations

**Status:** Complete - 15 July 2026. The detailed checklist is maintained in `phase_5c_multi_turn_conversations.md`.

### Goals

Make each Codex thread a durable multi-turn conversation while preserving turn-specific prompts, activity, responses, statuses, and timestamps.

### Delivered architecture

- Core owns bounded, app-neutral conversation-turn state and routes app-server notifications by thread and turn.
- Infrastructure exposes typed `thread/read` history loading with `includeTurns: true` and parses canonical user/assistant messages.
- Local settings persist deep-copied conversation snapshots and remain compatible with legacy single-response thread records.
- The task surface renders a virtualized chronological transcript and retains a fixed composer for follow-up turns and active-turn guidance.
- Thread switching, resume, fork, recovery, and shutdown preserve independent conversation histories.

### Acceptance criteria

- Follow-up prompts use the existing app-server thread rather than creating a new thread.
- Responses and activity remain associated with the correct turn during streaming and completion races.
- Restarted and resumed threads hydrate canonical history without duplicating local turns.
- Long histories are bounded to 100 turns and 100 persisted activity records per turn.
- The Release build, behavioral suite, and self-contained portable publish gates pass.

## 12D. Phase 5D: Project and Thread Navigation Consolidation

**Status:** Complete - 16 July 2026. The detailed checklist is maintained in `phase_5d_project_thread_navigation.md`.

### Goals

Replace the separate recent-project and thread lists with one project-scoped hierarchy so a project's threads appear directly when that project is selected.

### Delivered experience

- Project rows own disclosure, thread counts, running summaries, and nested project-specific thread lists.
- Selecting a project expands it, collapses the previous project, restores its active thread, and refreshes its workspace context.
- A project-row `+` action creates a current-checkout thread immediately; isolated worktree creation remains in the project's advanced menu.
- Selected-thread lifecycle actions are available from a compact contextual menu.
- Project rows omit filesystem paths, and the Task/Changes workspace header shows only the selected thread title.
- Selecting an existing project preserves its navigation position, and action-menu buttons use a dedicated high-contrast style.
- Completed and idle status pills are omitted; running, failed, cancelled, and archived states remain visible.
- Existing `RecentProjects` and path-keyed `ProjectThreads` storage remains backward compatible without migration.

### Acceptance criteria

- Projects and threads are presented as one hierarchy in recent-project order.
- Thread selection restores the correct conversation, terminal, Git, and workspace context.
- Empty projects and running background threads remain discoverable.
- Light/dark theme and keyboard focus states remain readable.
- All behavioral tests, the Release build, and the self-contained portable publish gates pass.

## 12E. Phase 5G: Compact Model, Reasoning, and Fast Controls

**Status:** Complete - 18 July 2026. The detailed implementation and verification record is maintained in `phase_5g_compact_model_controls.md`.

### Goals

Replace the permanent Run settings expander with a compact composer selector for the model, reasoning effort, and Fast mode while making the authenticated app-server catalog the authority for selectable capabilities.

### Recommended interaction

- Keep one compact `Model - Reasoning - Fast` summary in the composer footer beside Run task/Send follow-up.
- Open a transient flyout with Model and Reasoning drill-down pages plus an immediate Fast toggle.
- Use catalog display names and descriptions rather than raw model slugs.
- Filter reasoning choices from the selected model's `supportedReasoningEfforts` and fall back to its `defaultReasoningEffort`.
- Enable Fast only when the selected model advertises a `fast` service tier; keep the unavailable row disabled with an explanation.
- Disable the selector during an active turn so changes cannot be mistaken as affecting the in-flight request.

### Account-aware catalog policy

- Read `account.type` and ChatGPT `planType` for identity context and diagnostics.
- Load `model/list` with `includeHidden: false` after authentication and treat the returned catalog as the effective selectable set.
- Do not maintain a client-side plan-to-model or plan-to-reasoning matrix. Plan labels alone do not capture workspace policy, staged access, model retirement, client surface, or API organization/project permissions.
- Omit a ChatGPT plan label for API-key accounts, whose availability follows their API organization and project.
- Invalidate and reload catalog capabilities after sign-in, sign-out, account updates, app-server reconnection, Codex installation changes, and explicit refresh.
- Never persist plan entitlements or the capability catalog; persist only user selections and revalidate them against the current identity.

### Architecture

- Extend the typed model record with display metadata, default and supported reasoning efforts, service tiers, and informational availability copy.
- Preserve catalog ordering, support pagination, and defensively deduplicate/filter returned models.
- Replace string-only picker state with selected typed model/reasoning records and derived Fast availability.
- Add an internal inherit/standard/fast service-tier selection so `turn/start` can omit an untouched override, send `fast`, or explicitly clear a prior override with null.
- Apply the selected model consistently to thread creation, resume, fork, initial turns, and follow-up turns. Keep reasoning and service tier at turn scope.
- Preserve existing model/reasoning settings keys and add a backward-compatible service-tier preference.
- Keep flyout placement and focus behavior in WPF while keeping filtering, fallback, and entitlement reconciliation testable in the view model.

### Recovery and compatibility

- Reconcile a stale saved model to the catalog default and a stale reasoning effort to the selected model's default.
- Turn Fast off when the selected model does not advertise it.
- Preserve the cached catalog and saved protocol values during a transient refresh failure.
- If `turn/start` rejects a stale capability, preserve the prompt, refresh once, explain the changed selection, and require resubmission rather than silently switching models.
- Keep legacy settings files, thread history, guidance, cancellation, recovery, themes, and keyboard shortcuts compatible.

### Acceptance criteria

- Run settings and the visible Load models button are removed.
- The compact summary exactly represents what the next turn will send.
- Models reflect the effective authenticated catalog rather than a hardcoded plan matrix.
- Reasoning options and Fast availability follow per-model catalog metadata.
- Account changes and reconnects invalidate stale capabilities safely.
- Persisted selections reconcile predictably without persisting entitlements.
- Keyboard, screen-reader, light/dark/System theme, high-DPI, text-scale, and compact-window verification passes.
- Protocol, view-model, persistence, recovery, complete behavioral suite, Release build, and portable publish gates pass.

## 12F. Server-Request Approvals and Execution Policies

**Status:** Complete - 19 July 2026. The research and detailed design record is maintained in `server_request_approvals_implementation_plan.md`.

### Delivered scope

- Bidirectional app-server request classification with integer/string request-ID preservation and exact-once responses.
- Typed command-execution, file-change, and permission approvals; malformed and unsupported requests fail closed.
- A connection-aware coordinator and global modal queue with resolved-notification, reconnect, and shutdown invalidation.
- Once/session/decline/cancel decisions plus selectable permission subsets constrained to the original request.
- Persisted sandbox and approval-policy overrides with workspace-write/on-request defaults and explicit inheritance.
- Effective config and managed-requirement reads, dangerous-setting confirmations, and consistent policy application across thread and turn lifecycle calls.
- Focus-safe WPF approval controls, execution-policy settings, protocol/policy/queue/persistence/presentation tests, and updated user/architecture documentation.

This slice does not make SynthiaCode an editor for `config.toml` and does not expose persistent command or network policy amendments. Those remain advanced follow-up work after their live wire contracts are validated.

## 13. Phase 6: Skills, Plugins, MCP, and Settings

### Goals

Expose Codex configuration in a friendly native surface without taking ownership of all Codex internals.

### Approach

Read and respect the user's Codex configuration rather than inventing a parallel config system.

Possible settings UI:

- Codex executable path
- default model/profile
- sandbox default
- approval default
- web search default
- MCP server list
- plugin list
- skill discovery links
- `CODEX_HOME` display

### Important boundary

The app should not directly edit complex Codex config until the file format and user expectations are fully understood. Start with read-only visibility and safe shortcuts to documented CLI commands.

### Acceptance criteria

- App shows relevant Codex environment/config state.
- User can open config files or docs.
- App can run diagnostic commands.
- App does not silently overwrite existing Codex configuration.

## 14. Phase 7: Automations

### Goals

Add lightweight local automations for repeated tasks.

### Automation examples

- Daily repository summary.
- Run Codex review after local test failures.
- Weekly dependency risk scan prompt.
- Scheduled changelog draft.
- Poll a log file and summarize new errors.

### AutomationService

Responsibilities:

- store automation definitions
- schedule local wakeups
- run tasks through app-server threads by default
- use `codex exec --json` only for explicit fallback automation jobs
- capture results
- notify user
- avoid overlapping duplicate runs

### Storage model

Automation record fields:

- ID
- name
- project path
- prompt
- schedule
- execution mode
- sandbox setting
- last run status
- last run timestamp
- enabled flag

### Acceptance criteria

- User can create, disable, and delete a local automation.
- Automation runs in the expected project path.
- Failures are visible and recoverable.
- App does not run hidden automation tasks without clear user opt-in.

## 15. Phase 8: Browser and Artifact Preview

### Goals

Add selected first-party-app-like convenience features while staying native and lightweight.

### Browser preview

Use WebView2 for:

- local dev server previews
- static HTML previews
- public unauthenticated pages

Do not attempt full browser automation in the first pass.

### Artifact previews

Support common outputs:

- Markdown
- images
- PDF
- text logs
- JSON
- CSV

Office previews can be added later through installed Office handlers, WebView2, or export-to-PDF flows.

### Acceptance criteria

- User can open a local URL inside the app.
- User can preview generated files.
- Preview components do not block the core assistant workflow.

## 16. Phase 9: Voice and Convenience Features

### Goals

Add optional input and workflow polish after the core assistant is reliable.

Potential features:

- push-to-talk prompt dictation
- prompt templates
- saved task presets
- project-specific quick actions
- tray icon
- global hotkey
- toast notifications
- jump list recent projects

### Acceptance criteria

- Convenience features are optional.
- They do not slow startup.
- They do not complicate the core task path.

## 17. Authentication and Subscription Access

### Goals

Let users sign in with Codex using the same authentication model they already use in the official CLI, app, or IDE extension.

The native assistant should support:

- existing cached Codex login state
- ChatGPT sign-in for users who want to use their existing subscription or workspace entitlement
- API-key sign-in for users who prefer usage-based Platform billing
- device-code sign-in for environments where the browser callback flow is blocked
- enterprise access-token flows only as an advanced/trusted workflow

### AuthService

Responsibilities:

- detect whether Codex is installed
- detect whether Codex appears signed in without reading or copying token values
- expose sign-in state to the shell UI
- run `codex login` for the standard browser login flow
- run `codex login --device-auth` for device-code login
- run `codex logout` after explicit user confirmation
- surface login diagnostics and `codex-login.log` location when available
- explain whether the current mode is ChatGPT subscription access or API-key usage-based access when Codex exposes enough information to do so
- respect `CODEX_HOME` when the user has configured a custom Codex home

### Recommended sign-in UX

Initial unauthenticated state:

- Primary action: **Sign in with ChatGPT**
- Secondary action: **Use API key**
- Advanced action: **Device code sign-in**

The app should explain the practical difference:

- ChatGPT sign-in uses the user's ChatGPT subscription/workspace entitlement where available.
- API-key sign-in uses OpenAI Platform usage-based billing and may not include ChatGPT workspace or cloud features.

The app should not promise that every subscription includes every Codex capability. Entitlements, workspace policy, and feature availability are still controlled by Codex/OpenAI.

### Credential handling

The assistant should not become a credential store.

Rules:

- Do not store ChatGPT access tokens in app settings.
- Do not copy or parse `%USERPROFILE%\.codex\auth.json`.
- Do not display token contents.
- Do not store API keys after login.
- Prefer Codex CLI-managed credential storage, including OS keyring support when configured.
- Treat `auth.json` as sensitive if it exists.

For API-key sign-in, prefer letting the Codex CLI own the prompt. If the app later provides a transient secure input field, it should pass the value only through a documented Codex login mechanism and clear it from memory as soon as possible after the process exits.

### App-server behavior

Before starting a user turn:

1. Check Codex availability.
2. Check likely auth readiness through diagnostics or a lightweight Codex command.
3. If auth is missing or expired, show the sign-in state instead of starting app-server work.
4. After successful sign-in, restart or reinitialize app-server if needed.

If app-server reports an authentication or entitlement error during initialization or turn execution, the UI should:

- stop the current turn cleanly
- show a human-readable auth error
- offer sign-in/logout/retry actions
- preserve the user's prompt so it can be retried

### Acceptance criteria

- A user with existing Codex CLI login can use the assistant without signing in again.
- A user without login can start ChatGPT sign-in from the assistant.
- A user can choose API-key sign-in as an advanced path.
- A user can use device-code sign-in if browser callback login fails.
- The app never stores raw tokens or long-lived API keys in its own settings.
- Authentication failures are visible before or during app-server use and are recoverable.

## 18. Data Storage

Use local app data for assistant-owned metadata.

Suggested path:

```text
%LOCALAPPDATA%\NativeCodexAssistant\
```

Suggested storage:

- JSON settings for simple preferences
- SQLite for projects, threads, automations, and task history once data becomes relational

Do not store:

- raw API keys
- copied Codex auth tokens
- unnecessary transcript data already managed by Codex
- secrets from environment variables

## 19. Security Model

### Principles

- Treat Codex as a local command executor with real filesystem impact.
- Keep the user's existing Codex authentication model.
- Prefer sandboxed execution.
- Make privileged or destructive actions explicit.
- Keep remote transports local-only unless deliberately configured.

### Controls

- Default to workspace-write sandbox.
- Show the active workspace path before each run.
- Show whether task runs in main checkout or worktree.
- Confirm destructive Git actions.
- Confirm full-access mode.
- Avoid command-line secrets.
- Redact known secret-looking values from logs where possible.
- Store logs locally only.

### App-server WebSocket warning

Prefer stdio for local native integration. If WebSocket transport is ever used, bind to localhost and configure authentication before exposing anything beyond loopback.

## 20. Error Handling and Diagnostics

Diagnostics panel should include:

- app version
- Windows version
- .NET 10 runtime version and compatibility
- Codex executable path
- Codex version
- `CODEX_HOME`
- likely sign-in state
- selected sign-in method when safely knowable
- Git path/version
- current project path
- sandbox mode
- last process command excluding sensitive values
- last process exit code
- last stderr excerpt

Common recoveries:

- Codex missing: show install guidance.
- Not logged in: offer to run/open `codex login`.
- Login failed: show the login diagnostic log location and offer device-code sign-in.
- Not a Git repo: explain Codex project expectations, show the active `cwd`, and offer supported alternatives.
- Sandbox denial: explain active sandbox and project path.
- App-server crash: restart process and preserve UI state.

## 21. Testing Strategy

### Unit tests

- app-server message parser
- request ID correlation
- Codex notification mapping
- Git status parser
- settings serialization
- auth state transitions
- task state machine
- automation schedule calculations

### Integration tests

- run a fake app-server process that emits responses and notifications
- test cancellation
- test app-server request/response correlation with a fake server
- test Git operations in temporary repositories

### Manual verification

- clean Windows machine
- machine with Codex installed and logged in
- machine without Codex installed
- machine with expired or missing Codex auth
- ChatGPT sign-in flow
- device-code sign-in flow
- normal Git repo
- non-Git folder
- path with spaces
- path with Unicode characters
- long-running Codex task
- canceled Codex task
- failed Codex task
- worktree task
- sandbox-denied command

### UI checks

- light and dark theme
- high DPI
- narrow window
- keyboard-only operation
- screen reader basics
- long output streaming
- large diffs

## 22. Packaging and Release

### Development

- local debug build
- unsigned executable
- simple app settings reset command

### Portable folder

The development and manual-test artifact should always be produced into one predictable root-level folder:

```text
portable\NativeCodexAssistant\
```

The folder is generated with:

```powershell
.\scripts\publish-portable.cmd
```

The command wrapper runs `scripts\publish-portable.ps1` with a process-local PowerShell execution-policy bypass so the workflow works on machines where direct `.ps1` execution is disabled. The script publishes `src\NativeCodexAssistant.App\NativeCodexAssistant.App.csproj` in Release mode, defaults to a self-contained `win-x64` build, and verifies that this executable exists:

```text
portable\NativeCodexAssistant\NativeCodexAssistant.App.exe
```

Testing and zip sharing should use `portable\NativeCodexAssistant\` directly instead of digging through `src\...\bin\Release\...` publish folders. The generated `portable\` folder is ignored by source control.

### Maintenance sweep

Use the guarded maintenance wrapper to remove reproducible build output after development or publishing:

```powershell
.\scripts\maintenance-sweep.cmd
```

The default sweep removes project `bin\`/`obj\` output, test results, the root Visual Studio cache and app log, and noncanonical portable copies. It preserves `portable\NativeCodexAssistant\` so the latest runnable build remains available. `-WhatIf` previews exact targets, while `-RemovePortable` explicitly opts into a source-only folder. Every deletion target must resolve beneath the repository root.

### Internal alpha

- signed installer if possible
- versioned release artifact
- update instructions
- diagnostic export

### Beta

- MSIX or signed installer
- automatic update strategy
- release notes
- crash/error reporting only with explicit consent

### Installer checks

- detect Codex CLI
- verify login can be initiated but do not require login during install
- detect Git
- if framework-dependent, detect and require the .NET 10 Windows Desktop Runtime
- avoid modifying Codex config without user action

## 23. Milestone Plan

### Milestone 1: Native shell

Estimated scope:

- WPF app scaffold
- project picker
- settings
- Codex detection
- auth detection and sign-in entry points
- diagnostics

Exit criteria:

- app launches reliably
- recent projects persist
- Codex path/version visible
- existing Codex login is reused or sign-in state is shown

### Milestone 2: First Codex task

Estimated scope:

- app-server stdio process
- initialize/initialized handshake
- single thread start
- turn start with selected project `cwd`
- streaming event timeline
- final response
- cancellation
- error display

Exit criteria:

- user can run a real Codex turn from the app through app-server

### Milestone 3: Git-aware assistant

Estimated scope:

- Git status
- changed files
- diff viewer
- stage/commit

Exit criteria:

- user can inspect and commit Codex changes

### Milestone 4: Advanced thread lifecycle

Estimated scope:

- thread resume
- thread fork/archive
- multiple visible threads
- parallel running indicators
- turn streaming
- app-server recovery

Exit criteria:

- user can run, resume, fork, and manage persistent threads

### Milestone 5: Worktree and terminal

Estimated scope:

- assistant-created worktrees
- terminal panel
- project/worktree-aware execution

Exit criteria:

- user can run parallel isolated tasks

### Milestone 6: UI/UX modernization

Estimated scope:

- responsive and collapsible application shell
- task-first information hierarchy
- simplified thread and workspace actions
- improved task, Git, terminal, and diagnostics presentation
- keyboard, accessibility, theme, DPI, and UI-state verification

Exit criteria:

- existing Phase 0-5 workflows are clear, responsive, accessible, and visually cohesive

### Milestone 7: Architecture and performance optimization

Estimated scope:

- feature-oriented view models and WPF views
- smaller shell coordinator
- separated persistence and presentation models
- virtualized and batched streaming surfaces
- startup, memory, terminal, persistence, recovery, and shutdown baselines
- feature-oriented regression tests

Exit criteria:

- the application is maintainable and remains responsive under representative long-running workloads

### Milestone 8: Power features

Estimated scope:

- automations
- WebView2 preview
- artifact previews
- settings visibility for MCP/plugins/skills

Exit criteria:

- app covers the most useful official-app-adjacent workflows while remaining native and focused

## 24. Major Risks and Mitigations

### App-server protocol changes

Risk:

`codex app-server` is the primary integration surface and may change across Codex versions.

Mitigation:

- keep `codex exec --json` as a fallback utility, not the main architecture
- version-check Codex CLI
- generate schemas for supported versions
- isolate protocol code behind `CodexAppServerClient`

### Scope creep toward full app clone

Risk:

Trying to match every first-party app feature could erase the lightweight advantage.

Mitigation:

- keep MVP narrow
- require a user workflow justification for each feature
- keep app-server primitives small at first: initialize, thread start, turn start, stream, cancel

### Command execution safety

Risk:

The app runs a tool that can modify local files.

Mitigation:

- safe sandbox defaults
- visible workspace path
- Git diff review
- explicit destructive confirmations
- no full-access shortcut in primary UI

### Authentication ownership

Risk:

The assistant could accidentally become responsible for credentials or imply subscription capabilities it cannot guarantee.

Mitigation:

- delegate sign-in to `codex login`
- reuse Codex-managed credential storage
- do not parse or store tokens
- clearly distinguish ChatGPT subscription access from API-key billing
- show entitlement errors as Codex/OpenAI account or workspace state, not app-local failures

### Terminal complexity

Risk:

Embedded terminals are easy to make flaky.

Mitigation:

- defer terminal until after Codex/Git flows work
- use proven ConPTY control/library
- keep terminal sessions scoped and disposable

### Windows path edge cases

Risk:

Spaces, long paths, WSL paths, and permission boundaries can break process calls.

Mitigation:

- use `ProcessStartInfo.ArgumentList`
- test paths with spaces and Unicode
- avoid manual command-line string concatenation
- make WSL support explicit rather than implicit

## 25. Open Questions

- Should the first app name be product-like or plain, such as `Native Codex Assistant`?
- Should v1 require an existing Codex CLI installation, or bundle a pinned Codex runtime?
- Should the first release be framework-dependent or self-contained?
- Should ChatGPT sign-in be the only primary login button, with API key under advanced settings?
- Should `codex exec --json` fallback be hidden behind an advanced/diagnostics setting?
- Which editor should the app open by default: VS Code, Visual Studio, or system file association?
- Should automation run only while the app is open, or install a background scheduled task later?
- How much Codex config should the app edit versus merely display?

## 26. Recommended First Build Slice

The first implementation slice should be:

1. Create WPF solution.
2. Add project picker and recent projects.
3. Detect `codex.exe`.
4. Add auth status detection and ChatGPT sign-in entry point.
5. Add prompt composer.
6. Start `codex app-server --listen stdio://`.
7. Initialize the app-server connection.
8. Start a thread and turn in the selected project.
9. Parse and render app-server notifications.
10. Show final response.
11. Add cancellation.
12. Add basic Git status and changed-file list.
13. Produce the predictable portable test artifact with `scripts\publish-portable.cmd`.

That gives a real, useful native assistant on the same protocol spine we will use for threads, terminal, worktree, and automation surfaces later.
