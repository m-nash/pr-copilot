#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Installs pr-copilot MCP server for GitHub Copilot CLI.

.DESCRIPTION
    Downloads the latest release of pr-copilot, extracts it, and runs --setup
    to register the MCP server and install the skill file.

.PARAMETER Version
    Specific version to install (e.g., "0.1.0"). Defaults to "latest".

.EXAMPLE
    ./install.ps1
    ./install.ps1 -Version 0.1.0
#>
param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"
$Owner = "m-nash"
$Repo = "pr-copilot"
if ($IsWindows) {
    $InstallDir = Join-Path $env:USERPROFILE ".copilot" "mcp-servers" "pr-copilot"
} else {
    $InstallDir = Join-Path $env:HOME ".copilot" "mcp-servers" "pr-copilot"
}

# Detect platform RID
if ($IsWindows) {
    $rid = "win-x64"
    $exeName = "PrCopilot.exe"
    $oldPattern = "PrCopilot.old*.exe"
} elseif ($IsMacOS) {
    $arch = uname -m
    $rid = if ($arch -eq "arm64") { "osx-arm64" } else { "osx-x64" }
    $exeName = "PrCopilot"
    $oldPattern = "PrCopilot.old*"
} else {
    Write-Error "Unsupported platform. pr-copilot currently supports Windows and macOS."
    exit 1
}

Write-Host "🔍 Fetching release info..." -ForegroundColor Cyan

if ($Version -eq "latest") {
    $releaseUrl = "https://api.github.com/repos/$Owner/$Repo/releases/latest"
} else {
    $releaseUrl = "https://api.github.com/repos/$Owner/$Repo/releases/tags/v$Version"
}

try {
    $headers = @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "pr-copilot-installer"
    }
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers
} catch {
    Write-Error "Failed to fetch release from $releaseUrl — $_"
    exit 1
}

$asset = $release.assets | Where-Object { $_.name -like "*$rid*" -and $_.name -like "*.zip" } | Select-Object -First 1
if (-not $asset) {
    Write-Error "No $rid asset found in release $($release.tag_name)"
    exit 1
}

Write-Host "📦 Downloading $($asset.name) ($($release.tag_name))..." -ForegroundColor Cyan
$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) "pr-copilot-$($release.tag_name).zip"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

Write-Host "📂 Installing to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Handle locked exe (running MCP server)
$exePath = Join-Path $InstallDir $exeName
if (Test-Path $exePath) {
    $ext = if ($IsWindows) { ".exe" } else { "" }
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
        Rename-Item $exePath $bakTarget -Force
        Write-Host "  Renamed $exeName → $(Split-Path $bakTarget -Leaf)" -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not rename existing exe. Close any running instances and retry."
        exit 1
    }
}

Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# Write version sidecar for viewer update detection
$installedVersion = ($release.tag_name) -replace '^v', ''
Set-Content -Path (Join-Path $InstallDir "version.txt") -Value $installedVersion -NoNewline

# Make binary executable on Unix
if (-not $IsWindows) {
    chmod +x $exePath
}

Write-Host "⚙️  Running setup..." -ForegroundColor Cyan
& $exePath --setup

# Best-effort cleanup of old versions (some may be locked by other sessions)
Get-ChildItem $InstallDir -Filter $oldPattern | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "✅ pr-copilot installed successfully!" -ForegroundColor Green
Write-Host "   Restart your Copilot CLI session to use it." -ForegroundColor DarkGray
