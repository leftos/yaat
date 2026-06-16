# Discord Integration

## GitHub Actions Workflows (`.github/workflows/`)

| Workflow | Repo | Trigger | What it does |
|----------|------|---------|--------------|
| `discord-docs.yml` | leftos/yaat | Push to `main` (INSTALL/README/GETTING_STARTED/USER_GUIDE/COMMANDS/SOLO_TRAINING/comparison docs) + manual | Clears + reposts doc content to dedicated channels via bot token; large reference docs post ToC only |
| `discord-scenario-validation.yml` | **leftos/yaat-server** | Sundays 10:00 UTC cron + `workflow_dispatch` | Validates all ARTCC scenarios via `Yaat.ScenarioValidator`, posts reports to per-ARTCC channels, ensures pinned **Run Validation** buttons |

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

**GitHub → Discord** (webhook on `issues` + `issue_comment` events at `/github`):
- Labels (`in progress`, `completed`, `wontfix`, `not a bug`, `duplicate`) → status message posted to linked thread
- Terminal labels/close → per-type emoji prefix on title, matching reaction, thread archived
- Issue reopened → emoji prefix removed, thread unarchived
- New issue comments → posted to linked Discord thread (skips comments from Discord→GitHub sync to prevent echo loops)

**KV mappings:** `threadId → {issueNumber, issueUrl, guildId, lastSyncedMessageId}` and reverse `issue:{N} → threadId`.

**Secrets** (Cloudflare): `DISCORD_PUBLIC_KEY`, `DISCORD_BOT_TOKEN`, `DISCORD_ALLOWED_USER_ID`, `GITHUB_WEBHOOK_SECRET`, `GITHUB_APP_ID`, `GITHUB_APP_PRIVATE_KEY`, `GITHUB_APP_INSTALLATION_ID`

**Secrets** (GitHub Actions): `DISCORD_BOT_TOKEN` on **yaat** (docs sync webhooks) and **yaat-server** (scenario validation)

**Deploy:** `cd tools/discord-bot && pnpm install && pnpm run deploy`. Register commands: `DISCORD_APP_ID=<id> DISCORD_BOT_TOKEN=<token> pnpm run register -- --guild <guild-id>`.

**Validation buttons (manual bootstrap):** `DISCORD_BOT_TOKEN=<token> pnpm run setup-validation-buttons` (or `--artcc ZOA`). The yaat-server workflow runs the same script after each validation job so pins self-heal.
