#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds and installs pr-copilot locally for development/testing.

.DESCRIPTION
    Publishes a Release build, then renames the running PrCopilot binary to
    a backup name (which gets cleaned up on next startup) and copies
    the new build in place. Just restart your Copilot CLI session afterward.

.PARAMETER AutoUpdate
    Enable automatic update checks on MCP server startup.
#>
param(
    [switch]$AutoUpdate
)
$ErrorActionPreference = "Stop"

$RepoRoot = $PSScriptRoot
if ($IsWindows) {
    $InstallDir = Join-Path $env:USERPROFILE ".copilot" "mcp-servers" "pr-copilot"
} else {
    $InstallDir = Join-Path $env:HOME ".copilot" "mcp-servers" "pr-copilot"
}
$StagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "pr-copilot-staging"

# Detect platform RID
if ($IsWindows) {
    $rid = "win-x64"
    $exeName = "PrCopilot.exe"
    $ext = ".exe"
} elseif ($IsMacOS) {
    $arch = uname -m
    $rid = if ($arch -eq "arm64") { "osx-arm64" } else { "osx-x64" }
    $exeName = "PrCopilot"
    $ext = ""
} else {
    Write-Error "Unsupported platform. pr-copilot currently supports Windows and macOS."
    exit 1
}

$ExePath = Join-Path $InstallDir $exeName

# Compute dev version from latest git tag
git -C $RepoRoot fetch --tags --quiet 2>$null
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
$csprojPath = Join-Path $RepoRoot "PrCopilot" "src" "PrCopilot" "PrCopilot.csproj"
dotnet publish $csprojPath `
    -c Release -r $rid --self-contained `
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

# Rename running binary with next available .old.N suffix
if (Test-Path $ExePath) {
    $n = 0
    do {
        $bakTarget = if ($n -eq 0) {
            Join-Path $InstallDir "PrCopilot.old$ext"
        } else {
            Join-Path $InstallDir "PrCopilot.old.$n$ext"
        }
        $n++
    } while (Test-Path $bakTarget)
    try {
        Rename-Item $ExePath $bakTarget -Force
        Write-Host "  Renamed $exeName → $(Split-Path $bakTarget -Leaf)" -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not rename existing binary. Close any running instances and retry."
        exit 1
    }
}

Copy-Item -Path (Join-Path $StagingDir "*") -Destination $InstallDir -Force -Recurse
Remove-Item $StagingDir -Recurse -Force -ErrorAction SilentlyContinue

# Write version sidecar for viewer update detection
Set-Content -Path (Join-Path $InstallDir "version.txt") -Value $devVersion -NoNewline

# Make binary executable on Unix
if (-not $IsWindows) {
    chmod +x $ExePath
}

Write-Host ""
Write-Host "⚙️  Running setup..." -ForegroundColor Cyan
$setupArgs = @("--setup")
if ($AutoUpdate) { $setupArgs += "--auto-update" }
& $ExePath @setupArgs

Write-Host ""
Write-Host "✅ Installed! Restart your Copilot CLI session to pick up the new build." -ForegroundColor Green
