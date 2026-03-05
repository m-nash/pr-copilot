---
name: pr-monitor
description: "CRITICAL: When triggered, your FIRST action must be calling pr_monitor_start — do NOT reply to comments, read code, or take any PR action until the state machine tells you to. Use this skill for: git push to PR branch, 'monitor PR', 'resume monitoring', 'watch PR', 'check PR status', 'keep watching', 'show viewer', 'open viewer', 'monitor all my PRs', 'monitor all PRs', 'watch all my PRs'. Do NOT implement your own monitoring."
---

# PR Monitor

## ⛔ CRITICAL — YOUR FIRST ACTION

When this skill is triggered, your **FIRST and ONLY action** must be to call `pr_monitor_start`. 

**DO NOT** do any of the following before calling `pr_monitor_start` and receiving instructions from `pr_monitor_next_step`:
- ❌ Reply to PR comments
- ❌ Read code files  
- ❌ Make GitHub API calls
- ❌ Address review feedback
- ❌ Take ANY action on the PR

The state machine decides what to do. You are a thin executor. Call the tools and follow their instructions.

---

This skill uses a **state machine architecture**: the `pr-copilot` MCP server manages all state flow deterministically in C#. The main agent is a thin executor — it calls one tool in a loop and does whatever it's told.

**The agent NEVER makes state flow decisions.** The C# state machine handles: what to ask the user, which comment to address next, when to poll, when to stop. The agent handles what it's good at: understanding code, implementing fixes, writing replies, analyzing logs.

## How It Works

```
1. Call pr_monitor_start → get monitor_id (viewer auto-launches)
2. Call pr_monitor_next_step(event="ready") → may block for hours (polling) or return instantly
3. Response tells you exactly what to do:
   - action: "ask_user" → call ask_user with the provided question/choices
   - action: "execute" → do the task described in instructions
   - action: "stop" → monitoring is done
4. After each action, call pr_monitor_next_step with the result
5. Repeat forever
```

## Starting or Resuming Monitoring

**This flow applies to ALL monitoring triggers** — whether it's a fresh start (after git push), a resume ("resume monitoring", "keep watching"), or a re-monitor ("monitor the pr"). Always follow all 5 steps.

When triggered:

1. **Identify the PR**: Use the current branch to find the open PR number, owner, and repo.
2. **Get session folder**: Use the session state folder path (from your environment context).
3. **Call `pr_monitor_start`** — this creates the log file, fetches all data, and **automatically launches the viewer** if one isn't already running:
   ```
   pr_monitor_start(owner, repo, prNumber, sessionFolder)
   ```
   This returns a `monitor_id`, PR title, URL, CI counts, and other status data. The viewer is launched by the MCP server — the agent does NOT need to launch it.

4. **Enter the loop**: Call `pr_monitor_next_step(monitor_id, event="ready")` — this is where monitoring begins.

**Do NOT skip step 3 on resume.** The MCP server handles viewer launch automatically — just call `pr_monitor_start` and it will launch or reuse the viewer.

## The Agent Loop

After calling `pr_monitor_start`, the agent enters an infinite loop:

```
call pr_monitor_next_step(monitor_id, event, choice, data)
  → if action == "ask_user": call ask_user with the question/choices
       → then call pr_monitor_next_step(monitor_id, event="user_chose", choice=<user's choice>)
  → if action == "execute": do the work described in instructions
       → then call pr_monitor_next_step(monitor_id, event=<completion event>, data=<results>)
  → if action == "stop": monitoring is done, tell the user
  → if action == "merged": PR was merged — display "🟣 PR merged" and stop (do NOT call pr_monitor_next_step again)
```

**Important:** `pr_monitor_next_step` may **block for minutes or hours** when the state machine is in polling mode (zero tokens burned — identical to the old `wait_for_terminal_state`). When a terminal state is detected, it returns with the next instruction. During active flows (addressing comments, investigating failures), it returns instantly.

### Event Types

| Event | When to send | Additional fields |
|-------|-------------|-------------------|
| `ready` | After `pr_monitor_start`, or after resuming | — |
| `user_chose` | User made a choice from ask_user | `choice`: the user's selection |
| `comment_addressed` | Finished addressing a review comment | — |
| `investigation_complete` | Finished analyzing CI failures | `data`: JSON with `findings` and optional `suggested_fix` |
| `push_completed` | Committed and pushed a fix | — |
| `task_complete` | Finished any other execute task | — |

### Choice Mapping

**IMPORTANT:** Every `ask_user` response includes a `choice_map` dictionary that maps each choice display text to its exact `choice` value. **Always use the `choice_map` from the response** — do NOT invent or abbreviate choice values.

Example response:
```json
{
  "action": "ask_user",
  "question": "...",
  "choices": ["Merge the PR", "Wait for another approver"],
  "choice_map": {"Merge the PR": "merge", "Wait for another approver": "wait_for_approver"}
}
```
When the user selects "Wait for another approver", look up `choice_map["Wait for another approver"]` → use `"wait_for_approver"` as the `choice` parameter.

Fallback table (only if `choice_map` is missing):

| User selection | choice value |
|----------------|-------------|
| "Address all comments" | `address_all` |
| "Address a specific comment" | `address_specific` |
| "Address this comment" | `address` |
| "Explain and suggest what to do" | `explain` |
| "Investigate the failures" | `investigate` |
| "Show me the failed job logs" | `show_logs` |
| "Re-run failed jobs" | `rerun` |
| "Re-run cancelled jobs" | `rerun_failed` |
| "Apply the suggested fix" / "Apply the recommendation" | `apply_fix` |
| "Ignore and resume monitoring" | `ignore` |
| "Resume monitoring" | `resume` |
| "Merge the PR" | `merge` |
| "Force merge (--admin)" | `merge_admin` |
| "Wait for another approver" | `wait_for_approver` |
| "I'll handle it myself" / "I'll handle..." | `handle_myself` |
| "Address next comment" | `continue` |
| "Skip this comment" | `skip` |
| "Done — resume monitoring" | `done` |
| "I'll handle the rest myself" | `done` |
| "Resolve this thread" | `resolve` |
| "Follow up with more context" | `follow_up` |
| "Reassess my response" | `re_suggest` |
| "Go back to monitoring" | `go_back` |
| Numbered comment selection (e.g., "1. author...") | The full text of the choice |

## Monitor All My PRs

When the user says "monitor all my PRs", "watch all my PRs", or similar:

1. **Get session folder**: Use the session state folder path (from your environment context).
2. **Call `pr_monitor_start_all`**:
   ```
   pr_monitor_start_all(sessionFolder)
   ```
   This auto-detects the GitHub username from `gh` CLI auth. If detection fails, it returns an error — ask the user for their GitHub username and retry:
   ```
   pr_monitor_start_all(sessionFolder, githubUser="their-username")
   ```
   Returns a list of initialized PRs with their `monitor_id`s, titles, and URLs. Each PR gets its own viewer window.

3. **Enter the multi-PR loop**: Call `pr_monitor_next_step(monitorId="all", event="ready")` — this blocks until any PR has a terminal state.

4. **Handle terminal states**: The response includes a `monitorId` field identifying which PR needs attention. Handle it using the specific monitorId:
   ```
   → if action == "ask_user": call ask_user, then:
     pr_monitor_next_step(monitorId=<specific_monitor_id>, event="user_chose", choice=<choice>)
   → if action == "execute": do the work, then:
     pr_monitor_next_step(monitorId=<specific_monitor_id>, event=<completion_event>, data=<results>)
   → if action == "merged": display merge notification
   → if action == "stop": all PRs done
   ```

5. **Resume multi-PR monitoring**: After handling a terminal state, the specific PR's response will include `monitorId="all"` with a message to resume. Call:
   ```
   pr_monitor_next_step(monitorId="all", event="ready")
   ```
   This re-enters the combined poll loop watching all remaining PRs.

6. **Stop all**: To stop monitoring all PRs: `pr_monitor_stop(monitorId="all")`

**Important:** In multi-PR mode, merged PRs are automatically removed from monitoring. When all PRs are merged/closed, the poll loop exits with a "stop" action.

## Standalone Commands

These commands work when monitoring is already active OR when the user just wants to see the viewer:

- **Show PR viewer** (e.g., "show viewer", "show status window", "open the log", "show pr viewer") → **Do NOT call `pr_monitor_start` or `pr_monitor_next_step`.** Just launch the viewer process if not already running:
  ```powershell
  $existingViewer = Get-CimInstance Win32_Process -Filter "Name = 'PrCopilot.exe'" | Where-Object { $_.CommandLine -match '--viewer --pr [PR_NUMBER]' }
  ```
  If not running:
  ```powershell
  wt.exe -w 0 new-tab -- "<INSTALL_DIR>\PrCopilot.exe" --viewer --pr [PR_NUMBER] --log "<SESSION_FOLDER>\pr-monitor-[PR_NUMBER].log" --trigger "<SESSION_FOLDER>\pr-monitor-[PR_NUMBER].trigger" --debug "<SESSION_FOLDER>\pr-monitor-[PR_NUMBER].debug.log"
  ```
- **Stop monitoring** (e.g., "stop monitoring") → call `pr_monitor_stop(monitor_id)`
- **Force check** (e.g., "check now") → write an empty trigger file to `<SESSION_FOLDER>\pr-monitor-[PR_NUMBER].trigger` to wake the poll loop

## Commit/Push Rules

**⚠️ ALWAYS present code changes to the user for review before committing.** No exceptions — even when addressing review comments or applying fixes. Use `ask_user` to show a summary of what changed and get explicit approval before running `git commit` or `git push`.

When any flow in this skill involves committing and pushing code changes (e.g., addressing review comments, resolving merge conflicts):

**⚠️ You MUST check for and honor the user's custom instructions about git commit, git push, staging, and pre-commit workflows.** These are typically defined in the user's `custom_instruction` block or session configuration. Common user rules include:
- "Do not run `git add`" — the user stages manually
- "Do not commit without asking" — summarize changes and wait for approval before `git commit`
- "Do not push without asking" — wait for explicit approval before `git push`
- Pre-commit checks or linting that must pass before committing

**Before every `git commit` or `git push` in this skill:**
1. Review the user's custom instructions for any rules about committing/pushing
2. If the user requires review before commit → summarize the changes and use `ask_user` to get approval
3. If the user requires manual staging → do NOT run `git add`
4. If there are pre-commit hooks or checks → let them run and handle failures
5. Only proceed with commit/push after all user-defined requirements are met

**Do NOT assume you have blanket permission to commit and push** just because the user chose "Address this comment" or "Address all comments." Those choices authorize making code changes — the commit/push step must still follow the user's custom instructions.

---

## Comment Reply Tone

When replying to review comments on the PR (via `gh api .../replies`), always use **collaborative, respectful language**. You are representing the user in a professional code review conversation.

**Rules:**
- **Never use absolute or dismissive language** like "Won't fix", "Not applicable", "This is wrong", "No", or "Rejected"
- **Always explain the reasoning** behind a decision — don't just state the outcome
- **Use softer, collaborative phrasing** that invites further discussion rather than shutting it down

**❌ WRONG tone:**
- "Won't fix — this is by design."
- "Not needed."
- "This doesn't apply here."
- "No, that's incorrect."

**✅ RIGHT tone:**
- "I don't think we need to change this because [reason] — but happy to discuss further if you see it differently."
- "Good catch — I considered this but went with the current approach because [reason]. Let me know if you'd still prefer the change."
- "Thanks for the suggestion! I think we can skip this for now since [reason], but open to revisiting if needed."
- "I looked into this and I believe the current behavior is correct because [reason]. What do you think?"

---

## Important Rules

- **Never act autonomously on terminal states** — ALWAYS present `ask_user` choices first. The user decides. No exceptions.
- **Resume by default** — after handling a terminal state, the state machine resumes polling automatically unless the user chose to stop.
- **One monitor per PR** — if you push again to the same PR, call `pr_monitor_stop` then `pr_monitor_start` for a fresh baseline.
- **Session-scoped** — monitoring only lasts for the current Copilot CLI session.
- **Trust the state machine** — do NOT improvise state flow. Do NOT add extra questions, skip steps, or interpret the context. Execute exactly what `pr_monitor_next_step` tells you.
- **CI failure investigation** — when the state machine tells you to investigate, fetch logs, analyze, and report back. Always include your findings. If you have a suggested fix, include it in `data.suggested_fix`.
- **NEVER use `/azp run` or `/azp rerun` comments** to trigger CI reruns. These PR comments are not reliable, may trigger unintended pipelines, and bypass the state machine's deferred rerun logic. Always use the Playwright browser automation (via `rerun_via_browser` task) or the state machine's built-in mechanisms to rerun failed jobs.