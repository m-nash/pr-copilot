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

**The agent NEVER makes state flow decisions.** The C# state machine handles: what to ask the user (via MCP elicitation — directly to the client, bypassing the LLM), which comment to address next, when to poll, when to stop. The agent handles what it's good at: understanding code, implementing fixes, writing replies, analyzing logs.

## How It Works

```
1. Call pr_monitor_start → get monitor_id (viewer auto-launches)
2. Call pr_monitor_next_step(event="ready") → may block for hours (polling + user interaction)
3. Response tells you exactly what to do:
   - action: "execute" → do the task described in instructions
   - action: "stop" → monitoring is done
   - action: "merged" → PR was merged
4. After executing a task, call pr_monitor_next_step with the completion event
5. Repeat forever
```

**Note:** User interaction (choices, questions) is handled inside the tool call via MCP elicitation. The server presents choices directly to the client — the agent never sees or relays user choices. This means `pr_monitor_next_step` may block for extended periods while polling AND while waiting for user input.

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
call pr_monitor_next_step(monitor_id, event, data)
  → if action == "execute": do the work described in instructions
       → then call pr_monitor_next_step(monitor_id, event=<completion event>, data=<results>)
  → if action == "stop": monitoring is done, tell the user
  → if action == "merged": PR was merged — display "🟣 PR merged" and stop (do NOT call pr_monitor_next_step again)
```

**Important:** `pr_monitor_next_step` may **block for minutes or hours** when the state machine is in polling mode or waiting for user input via elicitation (zero tokens burned). When a terminal state is detected and the user makes a choice, the tool returns with the next instruction. During active flows (addressing comments, investigating failures), it returns instantly.

### Event Types

| Event | When to send | Additional fields |
|-------|-------------|-------------------|
| `ready` | After `pr_monitor_start`, or after resuming | — |
| `comment_addressed` | Finished addressing a review comment (implemented fix or agreed) | `data`: JSON with `reply_text` |
| `comment_replied` | Replied to a comment without code changes (pushback or clarification) | `data`: JSON with `reply_text` |
| `investigation_complete` | Finished analyzing CI failures | `data`: JSON with `findings` and optional `suggested_fix` |
| `push_completed` | Committed and pushed a fix | — |
| `task_complete` | Finished any other execute task | — |
| `user_chose` | Used by freeform interpretation when text maps to a choice | `choice`: mapped value |

## Freeform Text Input

When a terminal state is detected, the user sees both enum choices and a text input field. If the user types freeform text instead of picking a choice, the agent receives:

```json
{
  "action": "execute",
  "task": "interpret_freeform",
  "instructions": "The user typed: '...'. The original question was: '...'. The available choices were: [...]."
}
```

**How to handle `interpret_freeform`:**
1. If the text cleanly maps to ONE of the available choices with no extra instructions, tell the user "I'm interpreting this as [choice text]" and call `pr_monitor_next_step` with `event='user_chose'` and `choice=<mapped_value>`.
2. If the text is a custom instruction (or has extra instructions beyond a choice), execute the user's request directly, then call `pr_monitor_next_step` with `event='task_complete'` so the state machine re-discovers the PR state.

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

3. **Enter the multi-PR loop**: Call `pr_monitor_next_step(monitorId="all", event="ready")` — this blocks until any PR has a terminal state and the user makes a choice via elicitation.

4. **Handle the response**: The response includes a `monitorId` field identifying which PR needs attention:
   ```
   → if action == "execute": do the work using the specific monitorId, then:
     pr_monitor_next_step(monitorId=<specific_monitor_id>, event=<completion_event>, data=<results>)
   → if action == "merged": display merge notification
   → if action == "stop": all PRs done
   ```

5. **Resume multi-PR monitoring**: After handling a terminal state, call:
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

When composing reply text for review comments, always use **collaborative, respectful language**. You are representing the user in a professional code review conversation.

**⚠️ IMPORTANT: Do NOT post replies yourself.** Compose your reply text and pass it via `data='{"reply_text": "your reply"}'` in `pr_monitor_next_step`. The server posts it to the correct review thread automatically. Do NOT use `gh api`, `gh pr comment`, or any other method to post comments directly.

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

## Commit Linking in Replies

When composing a reply after making a code change, **always link the commit** that addressed the feedback. This gives the reviewer a direct link to verify the fix.

**How:** After pushing, run `git rev-parse HEAD` to get the SHA, then reference it as `owner/repo@SHA` in your reply text. GitHub auto-links this to the commit.

**Example reply_text:** "Added the null check — Azure/azure-sdk-for-net@abc1234"

---

## Important Rules

- **Trust the state machine** — do NOT improvise state flow. Do NOT add extra questions, skip steps, or interpret the context. Execute exactly what `pr_monitor_next_step` tells you.
- **Resume by default** — after handling a terminal state, the state machine resumes polling automatically unless the user chose to stop.
- **One monitor per PR** — if you push again to the same PR, call `pr_monitor_stop` then `pr_monitor_start` for a fresh baseline.
- **Session-scoped** — monitoring only lasts for the current Copilot CLI session.
- **CI failure investigation** — when the state machine tells you to investigate, fetch logs, analyze, and report back. Always include your findings. If you have a suggested fix, include it in `data.suggested_fix`.
- **NEVER use `/azp run` or `/azp rerun` comments** to trigger CI reruns. These PR comments are not reliable, may trigger unintended pipelines, and bypass the state machine's deferred rerun logic. Always use the Playwright browser automation (via `rerun_via_browser` task) or the state machine's built-in mechanisms to rerun failed jobs.