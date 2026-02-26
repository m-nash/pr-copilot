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
$BakPath = Join-Path $InstallDir "PrCopilot.old.exe"

Write-Host "🔨 Building Release..." -ForegroundColor Cyan
dotnet publish "$RepoRoot\PrCopilot\src\PrCopilot\PrCopilot.csproj" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $StagingDir --nologo -v quiet

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit 1
}

Write-Host "📂 Installing to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Rename running exe so we can overwrite
if (Test-Path $ExePath) {
    if (Test-Path $BakPath) { Remove-Item $BakPath -Force -ErrorAction SilentlyContinue }
    try {
        Rename-Item $ExePath $BakPath -Force
        Write-Host "  Renamed PrCopilot.exe → PrCopilot.old.exe" -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not rename existing exe. Close any running instances and retry."
        exit 1
    }
}

Copy-Item -Path "$StagingDir\*" -Destination $InstallDir -Force -Recurse
Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "✅ Installed! Restart your Copilot CLI session to pick up the new build." -ForegroundColor Green
