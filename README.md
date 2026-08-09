# SynthiaCode

SynthiaCode is a lightweight Windows-native WPF desktop assistant for working with Codex on local projects.

SynthiaCode is an independent application and is not affiliated with or endorsed by OpenAI.

The app is intended to launch and communicate with `codex app-server` while keeping the Windows desktop workflow small, predictable, and easy to test.

**Current release:** 0.1.0

The current build also includes a Git-aware Changes workspace for selectable attached repositories; Unstaged, Staged, exact Commit, merge-base Branch, and latest-turn diffs; file- and hunk-level staging/unstaging/confirmed discard; commits; dedicated Codex code review; and editor/Explorer shortcuts.

See the [current architecture](docs/current-architecture.md) for the release boundary and the [feature-parity audit](feature_parity.md) for the implemented matrix and prioritized gaps.

## Modern Windows workspace

The frontend uses a semantic WPF design system with an approved neutral graphite dark palette, an intentionally derived light palette, and a Windows system-color high-contrast palette. The existing blue-to-teal mark remains isolated to SynthiaCode branding; interface focus, progress, and primary actions use restrained emerald.

The custom native-aware shell replaces full-page workspace tabs with:

- a persistent or compact-drawer project rail;
- a virtualized conversation workspace and bounded composer;
- a resizable dark terminal dock that can maximize within the workspace;
- a persistent or compact-drawer changes/settings inspector;
- a top-priority, keyboard-contained approval sheet.

At 1440 px and above, the rail and enabled inspector are persistent. From 1100–1439 px the inspector becomes a drawer, and from 800–1099 px only one side drawer can be open. Existing shortcuts, app-server behavior, permission semantics, per-thread terminal ownership, Git actions, drafts, queues, attachments, and persistence remain unchanged.

The directional visual reference is [`assets/design/modern-wpf-redesign-concept-graphite-v2.png`](assets/design/modern-wpf-redesign-concept-graphite-v2.png); production UI is rendered entirely with WPF controls, vector geometries, and theme resources.

## Queued follow-ups

While a Codex turn is running, the composer can either **Queue follow-up** for the next turn or **Steer task** to add guidance to the current turn. Queue is the default and can be changed under Settings -> General -> Follow-up behavior.

Queued messages belong to their Codex thread and appear above the composer. They can be edited, reordered, sent or steered manually, and deleted. A successful turn completion starts the first queued message as a separate turn; failed or cancelled turns leave the queue paused. Pending queues survive restart but never auto-send merely because the app starts.

- `Ctrl+Enter` uses the configured follow-up behavior.
- `Ctrl+Shift+Enter` uses the other behavior once without changing the preference.

## Goal mode

Each Codex chat can own a persistent goal for work that spans many turns. Use **Set goal** above the composer to enter an objective; the objective becomes the first prompt for a new goal and remains visible as its completion criterion.

The goal row shows its current status, token and elapsed-time usage, and any runtime-provided token budget. You can pause or resume active work, edit the objective without starting another prompt, and clear the goal. Goal state is owned by `codex app-server`, isolated per chat, restored after reconnect, and updated from server notifications.

## Multi-folder projects

A saved local project can contain one primary folder and additional attached folders. Open a project's action menu and choose **Edit project folders** to add or remove folders or make another attached folder primary. Existing project chats and drafts migrate with the primary selection; worktree chats retain their worktree directory.

The primary folder remains the Codex working directory and the automatic discovery root for `AGENTS.md`, skills, and `config.toml`. Under workspace-write permissions, every attached folder is passed to Codex as a bounded writable root. Files and subfolders from secondary roots remain durable workspace references, and the Changes inspector lets you select any distinct Git repository discovered across the attached folders.

## Code review

In a Git-backed Codex project chat, choose **Review** beside the composer or submit exactly `/review`. The native picker can review uncommitted changes, changes against a local or remote base branch, a recent commit, or custom instructions. SynthiaCode calls the dedicated app-server `review/start` workflow and renders its lifecycle and prioritized findings as a labeled review turn in the current chat. The latest review is also parsed into typed P0-P3 file/line findings and shown as accessible inline cards on matching rows in the selected repository's Changes diff; valid findings that cannot anchor in the loaded diff remain visible in an explicit fallback section.

Diff rows now also expose an accessible **Add comment** action. Pending user comments retain the repository, renamed path, old/new side, line number, and captured diff text; they remain editable and removable, persist per chat beside attachment drafts, and are appended deterministically to the next start, active-turn steer, or queued follow-up. Only the captured comment IDs clear after the submission is acknowledged, so comments added while a request is in flight or in another chat remain intact. Queued cards disclose their captured comment count.

For ordinary tracked text modifications, each `@@` row also exposes **Stage hunk** and **Discard hunk** in the Unstaged view or **Unstage hunk** in the Staged view. Hunk patches are applied through Git standard input; discard requires confirmation and refreshes the selected repository without affecting adjacent hunks. Added, deleted, renamed, copied, conflicted, type-changed, untracked, and binary changes retain the existing whole-file actions.

The Changes comparison selector also loads an exact recent commit, compares the selected base branch's merge base to `HEAD`, or renders the latest app-server turn diff. Commit and Branch retain the selected repository; Last turn follows Codex's **All repos** presentation and survives restart through a bounded latest-turn snapshot. All three historical scopes are read-only and reuse the existing virtualized line renderer.

Confidence display for app-server's plain-text review payload and detached review delivery remain separate parity gaps.

## Execution permissions

SynthiaCode handles Codex app-server approval requests for command execution, file changes, and additional permissions. Requests appear in a global modal queue and can be allowed once, allowed for the current session, declined, or cancelled. Permission requests expose the requested permission groups so the response grants only the selected subset.

Use the permission selector beneath the task composer to choose one of three modes:

- **Ask for approval** uses the workspace permission boundary, `on-request` approvals, and the user reviewer.
- **Approve for me** keeps the same workspace boundary and `on-request` policy, but uses Codex automatic review.
- **Custom** either follows the `config.toml` default without SynthiaCode overrides or selects a named permission profile discovered from Codex.

Named profiles and their rules remain owned by `config.toml`; SynthiaCode does not rewrite them. Managed Codex requirements disable unavailable reviewers or profiles, stale selections fail closed, and older Codex app-server versions fall back to the equivalent `workspace-write` behavior for Ask for approval.

## Shared Codex configuration

Settings can edit the `AGENTS.md` and `config.toml` files in SynthiaCode's isolated shared `CODEX_HOME`. Saves are UTF-8, size-bounded, atomic, and rejected if the file changed externally after it was loaded. Configuration text is never written to application logs.

The same section shows the active source chain for the current workspace in root-to-leaf precedence order. Shared files can be edited in the built-in multiline editors or opened externally; workspace `AGENTS.md` and `.codex/config.toml` sources remain project-owned and use Editor/Explorer deep links.

## Skills and effective settings

Settings discovers Codex skills for the active General, project, or worktree workspace through the existing app-server session. The virtualized list supports search and scope filters, shows metadata, dependencies, paths, enabled state, and partial discovery errors, and provides Editor and Explorer actions. Enable/disable writes use the absolute `SKILL.md` path and refresh from Codex's authoritative effective state.

The task composer has a native enabled-skill picker and `$` completion. Selecting a skill inserts its visible `$name` marker, shows a removable invocation chip, and binds submission to the exact absolute `SKILL.md` path through the app-server's structured `skill` input. Duplicate names remain path- and scope-distinct, while queued follow-ups retain the selected binding through persistence and later dispatch.

External `skills/changed` notifications invalidate the active view without scanning hidden Settings surfaces. The adjacent effective-settings summary is read-only and retains only a small safe allowlist plus origin labels; raw or sensitive Codex configuration never enters presentation state. The existing shared `AGENTS.md` and `config.toml` editors remain available for explicit advanced edits.

## Solution

```text
SynthiaCode.sln
src\
  SynthiaCode.App\
  SynthiaCode.Application\
  SynthiaCode.Core\
  SynthiaCode.Harnesses.Codex\
  SynthiaCode.Harnesses.InMemory\
  SynthiaCode.Infrastructure\
  SynthiaCode.Presentation\
  SynthiaCode.Tests.Unit\
  SynthiaCode.Tests\
  SynthiaCode.UnicodeEchoFixture\
```

## Build And Test

Install a .NET 10 SDK with Windows Desktop support. The repository's `global.json` accepts current .NET 10 feature bands and servicing updates.

Restore, build, and run the test suite with:

```powershell
dotnet test SynthiaCode.sln
```

`dotnet test` is the authoritative local and CI command. It discovers and reports each behavioral case as a normal xUnit fact. Both test projects are libraries. `SynthiaCode.Tests.Unit` targets `net10.0` without App, Infrastructure, Windows, or WPF references. `SynthiaCode.UnicodeEchoFixture` is the dedicated UTF-8 transport fixture executable.

Use the test category when you need a focused gate:

```powershell
dotnet test src\SynthiaCode.Tests.Unit\SynthiaCode.Tests.Unit.csproj --filter "Category=Unit"
dotnet test src\SynthiaCode.Tests\SynthiaCode.Tests.csproj --filter "Category=ProtocolContract"
dotnet test src\SynthiaCode.Tests\SynthiaCode.Tests.csproj --filter "Category=InfrastructureIntegration"
dotnet test src\SynthiaCode.Tests\SynthiaCode.Tests.csproj --filter "Category=Wpf"
```

Pure unit and fake-only protocol test collections can run in parallel. Infrastructure, native-process, and WPF collections are serialized.

Standard solution builds produce the runnable application at:

```text
src\SynthiaCode.App\bin\Debug\net10.0-windows\SynthiaCode.App.exe
src\SynthiaCode.App\bin\Release\net10.0-windows\SynthiaCode.App.exe
```

## Branding

The approved symbol source is `assets\branding\synthiacode-logo-symbol-v1.png`. SynthiaCode uses derived app-ready resources for the executable, window, header, and About card.

Regenerate the cropped PNG and multi-size Windows icon after changing the approved source:

```powershell
.\scripts\generate-brand-assets.cmd
```

The generated resources are written to `src\SynthiaCode.App\Assets\Branding\`.

## Portable App Folder

Use the portable publish wrapper to produce one predictable runnable folder:

```powershell
.\scripts\publish-portable.cmd
```

The output folder is always:

```text
portable\SynthiaCode\
```

Run the app from:

```text
portable\SynthiaCode\SynthiaCode.App.exe
```

Zip `portable\SynthiaCode\` directly when sharing or testing a build.

The PowerShell script behind the wrapper is:

```powershell
.\scripts\publish-portable.ps1
```

By default it creates a Release, self-contained `win-x64` build. For a framework-dependent build:

```powershell
.\scripts\publish-portable.cmd -FrameworkDependent
```

## Automated GitHub Builds

Every successful push to `main` publishes a self-contained Windows test build as a GitHub Actions artifact. The same package can be generated for any branch with **Actions -> Windows CI -> Run workflow**; test artifacts are retained for 14 days.

Pushing a version tag that exactly matches the app project's version, such as `v0.1.1`, runs the full test and packaging pipeline and publishes a permanent GitHub Release ZIP with a SHA-256 checksum. See [automated Windows builds and releases](docs/automated-releases.md) for the download, versioning, tagging, and verification steps.

## Maintenance Sweep

Preview or remove reproducible build output while preserving the current portable app:

```powershell
.\scripts\maintenance-sweep.cmd -WhatIf
.\scripts\maintenance-sweep.cmd
```

Add `-RemovePortable` for the smallest source-only folder. See [docs/maintenance-sweep.md](docs/maintenance-sweep.md) for the exact targets and safety rules.

## Notes

Generated build output under `portable\` is intentionally ignored by source control. App settings and logs remain under the user's local app data folder rather than inside the portable app folder.
