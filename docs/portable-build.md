# Portable Build

Use the portable publish script to produce one predictable folder that contains the runnable app and the files produced by `dotnet publish`.

```powershell
.\scripts\publish-portable.ps1
```

If PowerShell script execution is disabled on the machine, use the command wrapper instead:

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

By default, the script creates a Release, self-contained `win-x64` build so the folder can run without requiring a matching .NET runtime installation. To create a framework-dependent build instead:

```powershell
.\scripts\publish-portable.ps1 -FrameworkDependent
```
