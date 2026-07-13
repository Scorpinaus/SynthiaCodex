# Maintenance Sweep

Use the maintenance sweep after builds, tests, or portable publishing to remove reproducible output and keep the repository compact.

Preview the exact targets without deleting anything:

```powershell
.\scripts\maintenance-sweep.cmd -WhatIf
```

Run the normal sweep:

```powershell
.\scripts\maintenance-sweep.cmd
```

The normal sweep removes:

- each project's `bin\` and `obj\` directories
- project and root `TestResults\` directories
- the root `.vs\` Visual Studio cache
- the root `NativeCodexAssistant.log` file
- portable directories matching `portable\NativeCodexAssistant-*\`

The canonical `portable\NativeCodexAssistant\` build is preserved. To produce the smallest source-only folder, explicitly remove it too:

```powershell
.\scripts\maintenance-sweep.cmd -RemovePortable
```

The portable app can be recreated later with:

```powershell
.\scripts\publish-portable.cmd
```

The script validates every target against the repository root before deletion, supports PowerShell `-WhatIf` and `-Confirm`, normalizes read-only generated files, and reports the space recovered.

User and source state is intentionally preserved, including `.git\`, `.vscode\`, `*.user`, `*.suo`, settings under `%LOCALAPPDATA%`, documentation, and all source files.
