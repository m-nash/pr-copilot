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
$InstallDir = Join-Path $env:USERPROFILE ".copilot" "mcp-servers" "pr-copilot"

Write-Host "🔍 Fetching release info..." -ForegroundColor Cyan

if ($Version -eq "latest") {
    $releaseUrl = "https://api.github.com/repos/$Owner/$Repo/releases/latest"
} else {
    $releaseUrl = "https://api.github.com/repos/$Owner/$Repo/releases/tags/v$Version"
}

try {
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers @{ Accept = "application/vnd.github+json" }
} catch {
    Write-Error "Failed to fetch release. Check that the version exists: $releaseUrl"
    exit 1
}

$asset = $release.assets | Where-Object { $_.name -like "*win-x64*" -and $_.name -like "*.zip" } | Select-Object -First 1
if (-not $asset) {
    Write-Error "No win-x64 asset found in release $($release.tag_name)"
    exit 1
}

Write-Host "📦 Downloading $($asset.name) ($($release.tag_name))..." -ForegroundColor Cyan
$zipPath = Join-Path $env:TEMP "pr-copilot-$($release.tag_name).zip"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

Write-Host "📂 Installing to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Handle locked exe (running MCP server)
$exePath = Join-Path $InstallDir "PrCopilot.exe"
$bakPath = Join-Path $InstallDir "PrCopilot.old.exe"
if (Test-Path $exePath) {
    if (Test-Path $bakPath) { Remove-Item $bakPath -Force -ErrorAction SilentlyContinue }
    try {
        Rename-Item $exePath $bakPath -Force
        Write-Host "  Renamed existing PrCopilot.exe → PrCopilot.old.exe" -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not rename existing exe. Close any running instances and retry."
        exit 1
    }
}

Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

Write-Host "⚙️  Running setup..." -ForegroundColor Cyan
& $exePath --setup

# Clean up backup
if (Test-Path $bakPath) { Remove-Item $bakPath -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "✅ pr-copilot installed successfully!" -ForegroundColor Green
Write-Host "   Restart your Copilot CLI session to use it." -ForegroundColor DarkGray
