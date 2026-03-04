# pr-copilot

An MCP server that monitors GitHub pull requests through GitHub Copilot CLI. It provides a state machine-driven monitoring loop with an optional TUI dashboard viewer.

## Features

- **Automated PR monitoring** — polls PR status (CI checks, reviews, comments, merge conflicts)
- **State machine architecture** — deterministic C# logic drives all decisions; the LLM agent is a thin executor
- **TUI dashboard** — live Terminal.Gui viewer with CI status, comments, approvals, progress bars, and deep links
- **Comment handling** — address, explain, or ignore review comments one-by-one or in batch
- **CI failure investigation** — analyze failed check logs, suggest fixes, rerun failed jobs via Azure DevOps
- **Auto-merge** — squash merge when approved + CI green (with admin override option)
- **After-hours awareness** — pauses polling during off-hours, resumes automatically

## Requirements

- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated with repo access
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli)

### Platform support

| Platform | MCP Server | TUI Viewer |
|----------|-----------|------------|
| Windows (x64) | ✅ | ✅ Requires [Windows Terminal](https://aka.ms/terminal) |
| macOS (x64) | ✅ | ✅ Launches in iTerm (if installed) or Terminal.app |
| macOS (Apple Silicon) | ✅ | ✅ Launches in iTerm (if installed) or Terminal.app |

The MCP server and all PR monitoring features work on both platforms. The TUI dashboard viewer opens in a separate terminal window (Windows Terminal on Windows, iTerm/Terminal.app on macOS).

### For building from source

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Optional

- **Playwright MCP server** — enables clicking "Rerun failed jobs" in Azure DevOps UI. Without it, the tool falls back to triggering new builds via empty commits.

## Installation

### Quick install (PowerShell 7+)

```powershell
# Downloads the correct binary for your platform and runs --setup
irm https://raw.githubusercontent.com/m-nash/pr-copilot/main/install.ps1 | iex
```

### From published binary

Download the latest release for your platform from [GitHub Releases](https://github.com/m-nash/pr-copilot/releases), extract it, then run setup:

```powershell
# Windows
PrCopilot.exe --setup

# macOS
chmod +x PrCopilot
./PrCopilot --setup
```

`--setup` will:
1. Extract `SKILL.md` to `~/.copilot/skills/pr-monitor/`
2. Register the MCP server in `~/.copilot/mcp-config.json`

### From source

```bash
git clone https://github.com/m-nash/pr-copilot.git
cd pr-copilot

# Build and install using the dev script (auto-detects platform)
./Install-Debug.ps1
```

Or manually:

```powershell
# Windows
dotnet publish PrCopilot/src/PrCopilot/PrCopilot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ~/.copilot/mcp-servers/pr-copilot

# macOS (Apple Silicon)
dotnet publish PrCopilot/src/PrCopilot/PrCopilot.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ~/.copilot/mcp-servers/pr-copilot

# Then run setup
~/.copilot/mcp-servers/pr-copilot/PrCopilot --setup
```

### Optional: Playwright MCP

For full CI rerun automation (clicking "Rerun failed jobs" in Azure DevOps):

```powershell
copilot -i "mcp add playwright -- npx -y @playwright/mcp@latest --browser msedge"
```

## Usage

In any Copilot CLI session with a PR checked out:

```
> monitor the pr
> monitor this pr https://github.com/owner/repo/pull/123
> monitor pr 456
```

The `pr-monitor` skill activates automatically on:
- `git push` to a branch with an open PR
- "monitor the PR", "watch the PR", "check PR status", etc.
- A direct PR link or PR number (assumes the current repo)

### What happens

1. **pr_monitor_start** — fetches PR info, launches the TUI viewer, begins polling
2. **Polling loop** — checks CI status, reviews, comments every 30–120 seconds
3. **Terminal state detected** — the state machine presents choices to you:
   - 💬 New comment → Address / Explain / Handle myself
   - ❌ CI failure → Investigate / Re-run failed / Handle myself
   - ✅ Approved + CI green → Merge / Wait for more approvals
   - ⚠️ Merge conflict → Handle myself
4. **After your action** — monitoring resumes automatically

### CLI flags

| Flag | Description |
|------|-------------|
| `--setup` | Install SKILL.md and register MCP server |
| `--update` | Self-update to the latest GitHub release |
| `--version` | Print version and exit |
| `--viewer --pr N --log PATH --trigger PATH` | Launch TUI viewer (internal use) |

### Configuration

pr-copilot supports optional configuration through the `args` array in `mcp-config.json`. See [docs/configuration.md](docs/configuration.md) for available options.

## Architecture

```
+---------------+    MCP/JSON-RPC     +--------------------+
|  Copilot CLI  |<------------------->|   PrCopilot.exe    |
|   (Agent)     |                     |   (MCP Server)     |
+---------------+                     +--------------------+
                                      |   State Machine    |
                                      |  (C# transitions)  |
                                      +--------------------+
                                      |  GitHub CLI (gh)   |
                                      |   (API calls)      |
                                      +---------+----------+
                                                | log file
                                      +---------v----------+
                                      |    TUI Viewer      |
                                      |   (Terminal.Gui)   |
                                      +--------------------+
```

The state machine (`MonitorTransitions`) makes all decisions deterministically. The LLM agent only:
- Understands code (to address comments)
- Writes replies (to PR threads)
- Analyzes logs (to investigate CI failures)
- Follows instructions (execute what the state machine says)

## Updating

```powershell
# Self-update to the latest release (no need to kill running instances)
PrCopilot --update      # macOS
PrCopilot.exe --update  # Windows
```

The update renames the running binary, extracts the new version, and cleans up on next startup.

## Development

```bash
# Build
dotnet build PrCopilot/PrCopilot.slnx

# Run tests
dotnet test PrCopilot/PrCopilot.slnx
```

### Local install for testing

```powershell
# Build and install without killing running instances (auto-detects platform)
./Install-Debug.ps1
# Then restart your Copilot CLI session
```

### Manual macOS viewer QA (iTerm)

```bash
chmod +x scripts/manual-viewer-mac-test.sh
./scripts/manual-viewer-mac-test.sh
```

The script launches the viewer and feeds realistic `STATUS|`, `TERMINAL|`, `RESUMING|`, and `STOPPED|` log lines so a human can validate live UI updates, section resizing/color changes, terminal-state freeze behavior, and auto-close on stop.
