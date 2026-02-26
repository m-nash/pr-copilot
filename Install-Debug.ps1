#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds and installs pr-copilot locally for development/testing.

.DESCRIPTION
    Publishes a Release build, then renames the running PrCopilot.exe to
    PrCopilot.old.exe (which gets cleaned up on next startup) and copies
    the new build in place. Just restart your Copilot CLI session afterward.
#>
$ErrorActionPreference = "Stop"

$RepoRoot = $PSScriptRoot
$InstallDir = Join-Path $env:USERPROFILE ".copilot" "mcp-servers" "pr-copilot"
$StagingDir = Join-Path $env:TEMP "pr-copilot-staging"
$ExePath = Join-Path $InstallDir "PrCopilot.exe"

# Compute dev version from latest git tag
$latestTag = git -C $RepoRoot tag --list 'v*' --sort=-v:refname | Select-Object -First 1
if ($latestTag) {
    $ver = $latestTag -replace '^v', ''
    $parts = $ver -split '\.'
    $nextPatch = [int]$parts[2] + 1
    $prefix = "$($parts[0]).$($parts[1]).$nextPatch"
} else {
    $prefix = "0.1.0"
}
$seconds = [int]([datetime]::Now - [datetime]::Today).TotalSeconds
$devVersion = "$prefix-dev.$([datetime]::Now.ToString('yyyyMMdd')).$seconds"
Write-Host "📋 Version: $devVersion" -ForegroundColor DarkGray

Write-Host "🔨 Building Release..." -ForegroundColor Cyan
dotnet publish "$RepoRoot\PrCopilot\src\PrCopilot\PrCopilot.csproj" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$devVersion `
    -o $StagingDir --nologo -v quiet

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

Write-Host "📂 Installing to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Rename running exe with next available .old.N.exe suffix
if (Test-Path $ExePath) {
    $n = 0
    do {
        $bakTarget = if ($n -eq 0) {
            Join-Path $InstallDir "PrCopilot.old.exe"
        } else {
            Join-Path $InstallDir "PrCopilot.old.$n.exe"
        }
        $n++
    } while (Test-Path $bakTarget)
    try {
        Rename-Item $ExePath $bakTarget -Force
        Write-Host "  Renamed PrCopilot.exe → $(Split-Path $bakTarget -Leaf)" -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not rename existing exe. Close any running instances and retry."
        exit 1
    }
}

Copy-Item -Path "$StagingDir\*" -Destination $InstallDir -Force -Recurse
Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue

# Write version sidecar for viewer update detection
Set-Content -Path (Join-Path $InstallDir "version.txt") -Value $devVersion -NoNewline

Write-Host ""
Write-Host "✅ Installed! Restart your Copilot CLI session to pick up the new build." -ForegroundColor Green
