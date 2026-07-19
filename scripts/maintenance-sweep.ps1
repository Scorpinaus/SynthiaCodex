[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [switch]$RemovePortable
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/')
$repoPrefix = $repoFullPath + [System.IO.Path]::DirectorySeparatorChar
$solutionPath = Join-Path $repoFullPath "SynthiaCode.sln"
$srcPath = Join-Path $repoFullPath "src"
$portableRoot = Join-Path $repoFullPath "portable"
$canonicalPortablePath = Join-Path $portableRoot "SynthiaCode"

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Repository marker was not found: $solutionPath"
}

function Get-PathSize {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [long]0
    }

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return [long](Get-Item -LiteralPath $Path -Force).Length
    }

    $measurement = Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    return [long]$measurement.Sum
}

function Format-Size {
    param([long]$Bytes)

    if ($Bytes -ge 1GB) {
        return "{0:N2} GiB" -f ($Bytes / 1GB)
    }

    if ($Bytes -ge 1MB) {
        return "{0:N2} MiB" -f ($Bytes / 1MB)
    }

    if ($Bytes -ge 1KB) {
        return "{0:N2} KiB" -f ($Bytes / 1KB)
    }

    return "$Bytes bytes"
}

function Get-SafeFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $fullPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing maintenance target outside the repository: $fullPath"
    }

    if ($fullPath.Equals($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to target the repository root."
    }

    return $fullPath
}

$candidates = New-Object System.Collections.Generic.List[object]

function Add-Candidate {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Category
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $safePath = Get-SafeFullPath -Path $Path
    $candidates.Add([pscustomobject]@{
        Path = $safePath
        Category = $Category
    })
}

Get-ChildItem -LiteralPath $srcPath -Recurse -Filter *.csproj -File | ForEach-Object {
    $projectDirectory = $_.Directory.FullName
    Add-Candidate -Path (Join-Path $projectDirectory "bin") -Category "project build output"
    Add-Candidate -Path (Join-Path $projectDirectory "obj") -Category "project intermediate output"
    Add-Candidate -Path (Join-Path $projectDirectory "TestResults") -Category "test results"
}

Add-Candidate -Path (Join-Path $repoFullPath ".vs") -Category "Visual Studio cache"
Add-Candidate -Path (Join-Path $repoFullPath "TestResults") -Category "test results"
Add-Candidate -Path (Join-Path $repoFullPath "SynthiaCode.log") -Category "root application log"

if (Test-Path -LiteralPath $portableRoot -PathType Container) {
    Get-ChildItem -LiteralPath $portableRoot -Directory -Force -Filter "SynthiaCode-*" | ForEach-Object {
        Add-Candidate -Path $_.FullName -Category "noncanonical portable build"
    }
}

if ($RemovePortable) {
    Add-Candidate -Path $canonicalPortablePath -Category "canonical portable build"
}

$uniqueByPath = @{}
foreach ($candidate in $candidates) {
    $uniqueByPath[$candidate.Path.ToLowerInvariant()] = $candidate
}

$targets = @($uniqueByPath.Values | Sort-Object { $_.Path.Length })
$filteredTargets = New-Object System.Collections.Generic.List[object]
foreach ($target in $targets) {
    $covered = $false
    foreach ($parent in $filteredTargets) {
        $parentPrefix = $parent.Path.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if ($target.Path.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $covered = $true
            break
        }
    }

    if (-not $covered) {
        $filteredTargets.Add($target)
    }
}

$repositorySizeBefore = Get-PathSize -Path $repoFullPath
$potentialBytes = [long]0
foreach ($target in $filteredTargets) {
    $targetSize = Get-PathSize -Path $target.Path
    $potentialBytes += $targetSize
    Write-Host ("Target: {0} ({1}, {2})" -f $target.Path, $target.Category, (Format-Size $targetSize))
}

if ($filteredTargets.Count -eq 0) {
    Write-Host "Maintenance sweep found no removable generated files."
    Write-Host ("Repository size: {0}" -f (Format-Size $repositorySizeBefore))
    return
}

Write-Host ""
Write-Host ("Potential space recovery: {0}" -f (Format-Size $potentialBytes))

$removedCount = 0
foreach ($target in $filteredTargets) {
    if (-not $PSCmdlet.ShouldProcess($target.Path, "Remove $($target.Category)")) {
        continue
    }

    if (Test-Path -LiteralPath $target.Path -PathType Container) {
        Get-ChildItem -LiteralPath $target.Path -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
            $_.Attributes = [System.IO.FileAttributes]::Normal
        }
    } else {
        (Get-Item -LiteralPath $target.Path -Force).Attributes = [System.IO.FileAttributes]::Normal
    }

    Remove-Item -LiteralPath $target.Path -Recurse -Force
    $removedCount++
}

$repositorySizeAfter = Get-PathSize -Path $repoFullPath
$recoveredBytes = [Math]::Max([long]0, $repositorySizeBefore - $repositorySizeAfter)

Write-Host ""
Write-Host ("Removed targets: {0}" -f $removedCount)
Write-Host ("Space recovered: {0}" -f (Format-Size $recoveredBytes))
Write-Host ("Repository size: {0} -> {1}" -f (Format-Size $repositorySizeBefore), (Format-Size $repositorySizeAfter))

if (-not $RemovePortable -and (Test-Path -LiteralPath $canonicalPortablePath)) {
    Write-Host "Canonical portable build preserved. Use -RemovePortable for a source-only folder."
}
