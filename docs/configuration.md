# Configuration

pr-copilot supports optional configuration through the `args` array in `~/.copilot/mcp-config.json`. The `--setup` command writes this file automatically, but you can also edit it by hand.

## Config File

```json
{
  "mcpServers": {
    "pr-copilot": {
      "command": "~/.copilot/mcp-servers/pr-copilot/PrCopilot.exe",
      "args": ["--auto-update"],
      "timeout": 3600000
    }
  }
}
```

The `args` array is where configuration lives. Arguments listed here are passed to the MCP server every time it starts.

## Available Options

### `--auto-update`

Checks for a newer release on GitHub in the background each time the MCP server starts. If a newer version is found:

1. The new binary is downloaded and installed without interrupting the current session
2. The TUI viewer (if running) shows an upgrade banner: `⬆ X.Y.Z available — restart to update`
3. The new version takes effect on next MCP server restart

**Enable during install:**
```powershell
./install.ps1 -AutoUpdate
```

**Enable on an existing install:**
```powershell
~/.copilot/mcp-servers/pr-copilot/PrCopilot.exe --setup --auto-update
```

**Enable by hand** — add `"--auto-update"` to the `args` array in `~/.copilot/mcp-config.json`.

**Disable** — remove `"--auto-update"` from the `args` array, or re-run `--setup` without it:
```powershell
~/.copilot/mcp-servers/pr-copilot/PrCopilot.exe --setup
```
