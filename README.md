# Native Codex Assistant

Native Codex Assistant is a lightweight Windows-native WPF desktop assistant for working with Codex on local projects.

The app is intended to launch and communicate with `codex app-server` while keeping the Windows desktop workflow small, predictable, and easy to test.

The current build also includes a Git-aware Changes workspace for repository status, working and staged diffs, staging, unstaging, confirmed discard, commits, and editor/Explorer shortcuts.

## Solution

```text
NativeCodexAssistant.sln
src\
  NativeCodexAssistant.App\
  NativeCodexAssistant.Core\
  NativeCodexAssistant.Infrastructure\
  NativeCodexAssistant.Tests\
```

## Build And Test

Install a .NET 10 SDK with Windows Desktop support. The repository's `global.json` accepts current .NET 10 feature bands and servicing updates.

Restore, build, and run the current solution tests with:

```powershell
dotnet test NativeCodexAssistant.sln
dotnet run --project src\NativeCodexAssistant.Tests\NativeCodexAssistant.Tests.csproj
```

The test project currently uses a small console-based assertion runner, so running the test project directly verifies the actual assertions.

## Portable App Folder

Use the portable publish wrapper to produce one predictable runnable folder:

```powershell
.\scripts\publish-portable.cmd
```

The output folder is always:

```text
portable\NativeCodexAssistant\
```

Run the app from:

```text
portable\NativeCodexAssistant\NativeCodexAssistant.App.exe
```

Zip `portable\NativeCodexAssistant\` directly when sharing or testing a build.

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
# SynthiaCodex
