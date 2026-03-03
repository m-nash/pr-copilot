# Configuration

pr-copilot uses a JSON config file at `~/.copilot/pr-copilot-config.json` for settings. The `--setup` command creates this file automatically with sensible defaults.

## Config File

```json
{
  "autoUpdate": true
}
```

**Location:** `~/.copilot/pr-copilot-config.json`

The file is created automatically by `--setup` if it doesn't exist. Edit it by hand to change settings.

## Available Options

### `autoUpdate` (default: `true`)

Checks for a newer release on GitHub in the background each time the MCP server starts. If a newer version is found:

1. The new binary is downloaded and installed without interrupting the current session
2. The TUI viewer (if running) shows an upgrade banner: `⬆ X.Y.Z available — restart to update`
3. The new version takes effect on next MCP server restart

Auto-update is **enabled by default** — no extra flags needed during install.

**Disable auto-update:**

Edit `~/.copilot/pr-copilot-config.json`:
```json
{
  "autoUpdate": false
}
```

**Re-enable auto-update:**

Set `autoUpdate` back to `true`, or delete the config file (it will be recreated with defaults on next `--setup`).

## Legacy

The `--auto-update` CLI flag is still supported for backward compatibility but is no longer needed. The config file takes precedence for new installs.
