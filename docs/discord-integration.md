# Discord Integration

## GitHub Actions Workflows (`.github/workflows/`)

| Workflow | Repo | Trigger | What it does |
|----------|------|---------|--------------|
| `discord-docs.yml` | leftos/yaat | Push to `main` (INSTALL/README/GETTING_STARTED/USER_GUIDE/COMMANDS/SOLO_TRAINING) + manual | Clears + reposts doc content to dedicated channels via bot token; large reference docs post ToC only |
| `discord-scenario-validation.yml` | **leftos/yaat-server** | Sundays 10:00 UTC cron + `workflow_dispatch` | Validates all ARTCC scenarios via `Yaat.ScenarioValidator`, posts reports to per-ARTCC channels, ensures pinned **Run Validation** buttons |
| `nightly-review-alert.yml` ("Nightly Review Notify") | leftos/yaat | `workflow_run` after **Nightly Review** completes | Posts every Nightly Review outcome to the CI/alerts channel via `DISCORD_CI_WEBHOOK_URL` (green on success, red on failure/timeout, white on cancel), with a TL;DR of what was reviewed and filed. The TL;DR + API-equivalent usage estimate + the reviewer's full markdown report (`report.md`) ride along as the review job's `nightly-review-notify` artifact; a timeout-killed run still posts the bare conclusion. Separate `workflow_run` job so a self-timeout still notifies. |

## Discord Bot (`tools/discord-bot/`)

Cloudflare Worker (JS, no framework) deployed as `yaat-discord-bot`. State in KV namespace `THREAD_ISSUES`.

**Scenario validation** (per-ARTCC channels; no admin gate):
- Pinned **Run Validation** button (`run_validation`) — dispatches `discord-scenario-validation.yml` on leftos/yaat-server for that ARTCC
- `/validate` — same trigger (slash commands may be unavailable in read-only channels)
- Channel IDs: [`tools/discord-bot/validation-channels.json`](tools/discord-bot/validation-channels.json)
- Bot `wrangler.toml` `[vars]` `VALIDATION_REPO = "leftos/yaat-server"` (issues/comments still use `GITHUB_REPO`)
- GitHub secret on **yaat-server**: `DISCORD_BOT_TOKEN`
- GitHub App must be installed on **leftos/yaat-server** with **Actions: Read and write** (same installation as yaat, or a second install — the worker resolves the correct installation per repo). If dispatch returns 404, add yaat-server to the app under GitHub → Settings → Applications → your app → Configure

**Slash commands** (restricted to `DISCORD_ALLOWED_USER_ID`):
- `/create-issue` — creates GitHub issue labeled `bug` from forum thread
- `/create-feature-request` — creates GitHub issue labeled `enhancement`
- `/track-issue` / `/track-feature-request` — create a forum thread tracking an existing GitHub issue (by `issue_number`)
- `/resolve` / `/unresolve` — manually toggle resolved state (checkmark title prefix + reaction)
- `/reopen` — reopens linked GitHub issue, removes terminal labels, unmarks thread as resolved

Creating or tracking an issue prefixes the thread title with its issue number (`[#123] Title`); the title is truncated to Discord's 100-char limit and the prefix coexists with the resolution-emoji prefix (`✅ [#123] Title`).

Re-running a slash command in an already-linked thread triggers an immediate comment sync instead.

**Auto-sync** (cron every 5min): New non-bot thread replies → GitHub issue comments.

**Reading a bug-report thread by hand.** When the bot fails (a GitHub secondary rate limit, say) or a raw `discord.com/channels/<guild>/<threadId>` link needs triage, fetch the thread directly with the bot token — Discord is auth-gated, so a plain web fetch cannot. The token is `DISCORD_BOT_TOKEN` in the yaat repo's `.env`; load it by key name without printing the value, then send `Authorization: Bot <token>`.
- **Always send a `User-Agent` header too** (e.g. `DiscordBot (https://github.com/leftos/yaat, 1.0)`). Without it, guild/channel/message endpoints return `403 {"message":"internal network error","code":40333}`, which reads like a permissions or token problem and is not. `/users/@me` and `/users/@me/guilds` succeed without it, so the token appears to verify while every useful call fails.
- Thread title and type: `GET https://discord.com/api/v10/channels/{threadId}` (forum posts are `type=11`).
- Messages: `GET .../channels/{threadId}/messages?limit=100` — returned **newest-first**; reverse for chronological order.
- Bug bundles are in each message's `attachments[].url`. The CDN URLs are time-signed and expire, so download promptly into `.tmp/`, then use the `bug-bundle` skill / `tools/bug_bundle.py`.
- To file the issue yourself, use `gh issue create` on the **yaat** repo — it authenticates as the user, not the bot, so it does not share the bot's rate-limit budget.

**GitHub → Discord** (webhook on `issues` + `issue_comment` events at `/github`):
- Labels (`in progress`, `completed`, `wontfix`, `not a bug`, `duplicate`) → status message posted to linked thread
- Terminal labels/close → per-type emoji prefix on title, matching reaction, thread archived
- Issue reopened → emoji prefix removed, thread unarchived
- New issue comments → posted to linked Discord thread (skips comments from Discord→GitHub sync to prevent echo loops)
- **Agent-written comments are attributed to the agent.** A comment an agent posts through `gh` goes out under the maintainer's GitHub account, so the default `💬 **leftos** commented on …` header reads in Discord as the maintainer speaking personally. `mirrorCommentMessage` (`src/worker.js`) looks for the marker the agent puts in the comment body (`🤖 Posted by Claude Code on behalf of …`, required by the global CLAUDE.md) and switches the header to `🤖 **Claude Code** (for **leftos**) commented on …`. The body carries the marker through on its own; the header is the part that would otherwise misattribute. Both mirror paths — the `issue_comment` webhook and the backfill in `/track-issue` — go through that one helper so they cannot drift apart.

**KV mappings:** `threadId → {issueNumber, issueUrl, guildId, lastSyncedMessageId}` and reverse `issue:{N} → threadId`. Bookkeeping keys use prefixes (`issue:`, `pending-archive:`, `pending-issue:`, `reopen:`, `validate-cooldown:`); the sync loop treats only bare numeric snowflake keys as threads. All listings go through `listAllKeys`, which follows the cursor past KV's 1000-key page limit, and the cron sweeps list their own prefix — those keys sort after the numeric thread IDs, so they would be the first to fall off a truncated listing.

**GitHub rate limits:** every GitHub REST call goes through `githubFetch`, which paces mutating requests `GITHUB_WRITE_SPACING_MS` apart and retries a rate-limited 403/429 (honoring `Retry-After` / `x-ratelimit-reset`, else GitHub's "wait at least a minute" guidance). Retries stop once the next wait would overrun the caller's budget — `INTERACTION_RETRY_BUDGET_MS` for slash commands and webhooks, because Cloudflare cancels `ctx.waitUntil()` work 30s after the response, vs `CRON_RETRY_BUDGET_MS` for the cron, which has a 15-minute wall-clock budget. A 403 that isn't a rate limit (e.g. a permissions error) is never retried.

Since a content-creation block outlasts what a slash command can wait out, `/create-issue` and `/create-feature-request` fall back to a queue: the prepared issue is stored as `pending-issue:{threadId}` (24h TTL) and the command replies that it will be filed automatically. The cron drains those records before its thread syncs, then links the thread and posts the issue link into it. Re-running the command while a report is queued reports the queued state instead of filing a duplicate, and `/track-issue` inside the thread clears the record.

**Secrets** (Cloudflare): `DISCORD_PUBLIC_KEY`, `DISCORD_BOT_TOKEN`, `DISCORD_ALLOWED_USER_ID`, `GITHUB_WEBHOOK_SECRET`, `GITHUB_APP_ID`, `GITHUB_APP_PRIVATE_KEY`, `GITHUB_APP_INSTALLATION_ID`

**Secrets** (GitHub Actions): `DISCORD_BOT_TOKEN` on **yaat** (docs sync webhooks) and **yaat-server** (scenario validation); `DISCORD_CI_WEBHOOK_URL` on **yaat** (Nightly Review Notify — a plain channel-webhook URL, not the bot)

**Tests:** `cd tools/discord-bot && pnpm test` (vitest; also run by CI). Covers `githubFetch` retry/pacing and the queued-issue path.

**Deploy:** `cd tools/discord-bot && pnpm install && pnpm run deploy`. Register commands: `DISCORD_APP_ID=<id> DISCORD_BOT_TOKEN=<token> pnpm run register -- --guild <guild-id>`. Registering one scope clears the other (guild vs global), since commands present in both scopes show up twice in Discord's picker.

**Validation buttons (manual bootstrap):** `DISCORD_BOT_TOKEN=<token> pnpm run setup-validation-buttons` (or `--artcc ZOA`). The yaat-server workflow runs the same script after each validation job so pins self-heal.
