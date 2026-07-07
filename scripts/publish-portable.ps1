[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64", "win-arm64", "win-x86")]
    [string]$Runtime = "win-x64",

    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\NativeCodexAssistant.App\NativeCodexAssistant.App.csproj"
$portableRoot = Join-Path $repoRoot "portable\NativeCodexAssistant"

$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
$portableFullPath = [System.IO.Path]::GetFullPath($portableRoot)
$expectedPrefix = $repoFullPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

if (-not $portableFullPath.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean portable output outside the repository: $portableFullPath"
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "App project was not found: $projectPath"
}

if (Test-Path -LiteralPath $portableFullPath) {
    Remove-Item -LiteralPath $portableFullPath -Recurse -Force
}

New-Item -ItemType Directory -Path $portableFullPath -Force | Out-Null

$publishArgs = @(
    "publish",
    $projectPath,
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--output", $portableFullPath,
    "-p:PublishSingleFile=false"
)

if ($FrameworkDependent) {
    $publishArgs += @("--self-contained", "false")
} else {
    $publishArgs += @("--self-contained", "true")
}

Write-Host "Publishing Native Codex Assistant..."
Write-Host "Project: $projectPath"
Write-Host "Output:  $portableFullPath"
Write-Host "Runtime: $Runtime"
Write-Host "Mode:    $(if ($FrameworkDependent) { "framework-dependent" } else { "self-contained" })"

dotnet @publishArgs

$exePath = Join-Path $portableFullPath "NativeCodexAssistant.App.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Publish completed, but the runnable app was not found: $exePath"
}

Write-Host ""
Write-Host "Portable folder is ready:"
Write-Host "  $portableFullPath"
Write-Host ""
Write-Host "Run:"
Write-Host "  $exePath"
Write-Host ""
Write-Host "Zip this folder:"
Write-Host "  $portableFullPath"
