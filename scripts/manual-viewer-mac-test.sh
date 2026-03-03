#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_PATH="$ROOT_DIR/PrCopilot/src/PrCopilot/PrCopilot.csproj"

SESSION_DIR="${1:-/tmp/pr-copilot-viewer-manual}"
LOG_FILE="$SESSION_DIR/log.txt"
TRIGGER_FILE="$SESSION_DIR/trigger.txt"
DEBUG_FILE="$SESSION_DIR/debug.txt"
PR_NUMBER="999"

mkdir -p "$SESSION_DIR"
: > "$DEBUG_FILE"
printf '#%s - macOS iTerm UI manual test | https://github.com/m-nash/pr-copilot/pull/%s\n' "$PR_NUMBER" "$PR_NUMBER" > "$LOG_FILE"

append_line() {
    printf '%s\n' "$1" >> "$LOG_FILE"
}

feed_status_updates() {
    sleep 2
    append_line 'STATUS|{"checks":{"passed":0,"failed":0,"pending":2,"queued":0,"cancelled":0,"total":2,"failures":[]},"approvals":[],"stale_approvals":[],"unresolved":[],"waiting_for_reply":[],"next_check_seconds":20,"after_hours":false,"timestamp":"phase-1"}'

    sleep 4
    append_line 'STATUS|{"checks":{"passed":1,"failed":1,"pending":0,"queued":0,"cancelled":0,"total":2,"failures":[{"name":"unit tests","reason":"1 test failed","url":"https://example.com/failure"}]},"approvals":[{"name":"alice"}],"stale_approvals":[],"unresolved":[{"id":"c-1","author":"bob","summary":"please guard null before dereference","url":"https://example.com/comment/1"}],"waiting_for_reply":[{"id":"w-1","author":"carol","summary":"can you clarify this edge case handling?","url":"https://example.com/comment/2"}],"next_check_seconds":30,"after_hours":false,"timestamp":"phase-2"}'

    sleep 5
    append_line 'TERMINAL|{"state":"ci_failure","description":"CI failure detected for manual viewer test"}'

    sleep 5
    append_line 'RESUMING|manual-test'
    append_line 'STATUS|{"checks":{"passed":2,"failed":0,"pending":0,"queued":0,"cancelled":0,"total":2,"failures":[]},"approvals":[{"name":"alice"},{"name":"dave"}],"stale_approvals":[],"unresolved":[],"waiting_for_reply":[],"next_check_seconds":15,"after_hours":false,"timestamp":"phase-3"}'

    sleep 6
    append_line 'STOPPED|manual-test|Manual test complete'
}

feed_status_updates &
FEED_PID=$!

cleanup() {
    kill "$FEED_PID" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

export LANG="${LANG:-en_US.UTF-8}"
export LC_ALL="${LC_ALL:-en_US.UTF-8}"
export PR_COPILOT_TUI_DRIVER="${PR_COPILOT_TUI_DRIVER:-CursesDriver}"

echo "Starting manual viewer test"
echo "- Session: $SESSION_DIR"
echo "- Log: $LOG_FILE"
echo "- Trigger: $TRIGGER_FILE"
echo "- Debug: $DEBUG_FILE"
echo "- Driver: $PR_COPILOT_TUI_DRIVER"
echo

dotnet run --project "$PROJECT_PATH" -- --viewer --pr "$PR_NUMBER" --log "$LOG_FILE" --trigger "$TRIGGER_FILE" --debug "$DEBUG_FILE"

wait "$FEED_PID" || true

echo
echo "Manual test finished. Inspect files in: $SESSION_DIR"
