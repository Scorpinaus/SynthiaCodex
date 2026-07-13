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

### Milestone 6: Power features

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
