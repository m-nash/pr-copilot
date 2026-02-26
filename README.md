# pr-copilot

An MCP server that monitors GitHub pull requests through GitHub Copilot CLI. It provides a state machine-driven monitoring loop with a TUI dashboard viewer.

## Features

- **Automated PR monitoring** — polls PR status (CI checks, reviews, comments, merge conflicts)
- **State machine architecture** — deterministic C# logic drives all decisions; the LLM agent is a thin executor
- **TUI dashboard** — live Terminal.Gui viewer with CI status, comments, approvals, progress bars, and deep links
- **Comment handling** — address, explain, or ignore review comments one-by-one or in batch
- **CI failure investigation** — analyze failed check logs, suggest fixes, rerun failed jobs via Azure DevOps
- **Auto-merge** — squash merge when approved + CI green (with admin override option)
- **After-hours awareness** — pauses polling during off-hours, resumes automatically

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or the published self-contained binary)
- [GitHub CLI (`gh`)](https://cli.github.com/) — authenticated with repo access
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli)
- Windows (Terminal.Gui viewer uses Windows-specific APIs)

### Optional

- **Playwright MCP server** — enables clicking "Rerun failed jobs" in Azure DevOps UI. Without it, the tool falls back to triggering new builds via empty commits.

## Installation

### From published binary

```powershell
# Download the latest release
# Then run setup:
PrCopilot.exe --setup
```

`--setup` will:
1. Extract `SKILL.md` to `~/.copilot/skills/pr-monitor/`
2. Register the MCP server in `~/.copilot/mcp-config.json`

### From source

```powershell
git clone https://github.com/m-nash/pr-copilot.git
cd pr-copilot
dotnet build

# Publish self-contained binary
dotnet publish src/PrCopilot/PrCopilot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ~/.copilot/mcp-servers/pr-copilot

# Run setup from published location
~/.copilot/mcp-servers/pr-copilot/PrCopilot.exe --setup
```

### Optional: Playwright MCP

For full CI rerun automation (clicking "Rerun failed jobs" in Azure DevOps):

```powershell
copilot mcp add playwright -- npx -y @playwright/mcp@latest --browser msedge
```

## Usage

In any Copilot CLI session with a PR checked out:

```
> monitor the pr
```

The `pr-monitor` skill activates automatically on:
- `git push` to a branch with an open PR
- "monitor the PR", "watch the PR", "check PR status", etc.

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
| `--version` | Print version and exit |
| `--viewer --pr N --log PATH --trigger PATH` | Launch TUI viewer (internal use) |

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

## Development

```powershell
# Build
dotnet build

# Run tests
dotnet test

# Publish
dotnet publish src/PrCopilot/PrCopilot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ~/.copilot/mcp-servers/pr-copilot
```
