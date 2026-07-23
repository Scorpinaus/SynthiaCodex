# SynthiaCode

SynthiaCode is a lightweight Windows-native WPF desktop assistant for working with Codex on local projects.

SynthiaCode is an independent application and is not affiliated with or endorsed by OpenAI.

The app is intended to launch and communicate with `codex app-server` while keeping the Windows desktop workflow small, predictable, and easy to test.

The current build also includes a Git-aware Changes workspace for repository status, working and staged diffs, staging, unstaging, confirmed discard, commits, and editor/Explorer shortcuts.

See the current [SynthiaCode and ChatGPT desktop feature-parity audit](feature_parity.md) for the implemented matrix and prioritized gaps.

## Queued follow-ups

While a Codex turn is running, the composer can either **Queue follow-up** for the next turn or **Steer task** to add guidance to the current turn. Queue is the default and can be changed under Settings -> General -> Follow-up behavior.

Queued messages belong to their Codex thread and appear above the composer. They can be edited, reordered, sent or steered manually, and deleted. A successful turn completion starts the first queued message as a separate turn; failed or cancelled turns leave the queue paused. Pending queues survive restart but never auto-send merely because the app starts.

- `Ctrl+Enter` uses the configured follow-up behavior.
- `Ctrl+Shift+Enter` uses the other behavior once without changing the preference.

## Execution permissions

SynthiaCode handles Codex app-server approval requests for command execution, file changes, and additional permissions. Requests appear in a global modal queue and can be allowed once, allowed for the current session, declined, or cancelled. Permission requests expose the requested permission groups so the response grants only the selected subset.

Use the permission selector beneath the task composer to choose one of three modes:

- **Ask for approval** uses the workspace permission boundary, `on-request` approvals, and the user reviewer.
- **Approve for me** keeps the same workspace boundary and `on-request` policy, but uses Codex automatic review.
- **Custom** either follows the `config.toml` default without SynthiaCode overrides or selects a named permission profile discovered from Codex.

Named profiles and their rules remain owned by `config.toml`; SynthiaCode does not rewrite them. Managed Codex requirements disable unavailable reviewers or profiles, stale selections fail closed, and older Codex app-server versions fall back to the equivalent `workspace-write` behavior for Ask for approval.

## Solution

```text
SynthiaCode.sln
src\
  SynthiaCode.App\
  SynthiaCode.Core\
  SynthiaCode.Infrastructure\
  SynthiaCode.Tests\
```

## Build And Test

Install a .NET 10 SDK with Windows Desktop support. The repository's `global.json` accepts current .NET 10 feature bands and servicing updates.

Restore, build, and run the current solution tests with:

```powershell
dotnet test SynthiaCode.sln
dotnet run --project src\SynthiaCode.Tests\SynthiaCode.Tests.csproj
```

The test project currently uses a small console-based assertion runner, so running the test project directly verifies the actual assertions.

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

## Maintenance Sweep

Preview or remove reproducible build output while preserving the current portable app:

```powershell
.\scripts\maintenance-sweep.cmd -WhatIf
.\scripts\maintenance-sweep.cmd
```

Add `-RemovePortable` for the smallest source-only folder. See [docs/maintenance-sweep.md](docs/maintenance-sweep.md) for the exact targets and safety rules.

## Notes

Generated build output under `portable\` is intentionally ignored by source control. App settings and logs remain under the user's local app data folder rather than inside the portable app folder.
