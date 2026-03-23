# Discord Integration

## GitHub Actions Workflows (`.github/workflows/`)

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| `discord-push.yml` | Push to `main` | Posts commit list embed to `DISCORD_WEBHOOK_URL` |
| `discord-nightly.yml` | Cron 02:00 PT (+ manual) | Claude Haiku summarizes yaat + yaat-server commits since last digest; posts to `DISCORD_NIGHTLY_WEBHOOK_URL` |
| `discord-docs.yml` | Push to `main` (INSTALL/README/USER_GUIDE) + manual | Clears + reposts doc content to dedicated channels via bot token; USER_GUIDE posts ToC only |
| `discord-scenario-validation.yml` | Weekly cron Sunday 02:00 PT (+ manual) | Runs ScenarioValidator for all 20 ARTCCs; posts per-ARTCC reports to dedicated channels via bot token; content-diffs to skip unchanged channels |

## Discord Bot (`tools/discord-bot/`)

Cloudflare Worker (JS, no framework) deployed as `yaat-discord-bot`. State in KV namespace `THREAD_ISSUES`.

**Slash commands** (restricted to `DISCORD_ALLOWED_USER_ID`):
- `/create-issue` — creates GitHub issue labeled `bug` from forum thread
- `/create-feature-request` — creates GitHub issue labeled `enhancement`
- `/resolve` / `/unresolve` — manually toggle resolved state (checkmark title prefix + reaction)
- `/reopen` — reopens linked GitHub issue, removes terminal labels, unmarks thread as resolved

Re-running a slash command in an already-linked thread triggers an immediate comment sync instead.

**Auto-sync** (cron every 5min): New non-bot thread replies → GitHub issue comments.

**GitHub → Discord** (webhook on `issues` + `issue_comment` events at `/github`):
- Labels (`in progress`, `completed`, `wontfix`, `not a bug`, `duplicate`) → status message posted to linked thread
- Terminal labels/close → per-type emoji prefix on title, matching reaction, thread archived
- Issue reopened → emoji prefix removed, thread unarchived
- New issue comments → posted to linked Discord thread (skips comments from Discord→GitHub sync to prevent echo loops)

**KV mappings:** `threadId → {issueNumber, issueUrl, guildId, lastSyncedMessageId}` and reverse `issue:{N} → threadId`.

**Secrets** (Cloudflare): `DISCORD_PUBLIC_KEY`, `DISCORD_BOT_TOKEN`, `GITHUB_TOKEN`, `DISCORD_ALLOWED_USER_ID`, `GITHUB_WEBHOOK_SECRET`

**Deploy:** `cd tools/discord-bot && pnpm install && pnpm run deploy`. Register commands: `DISCORD_APP_ID=<id> DISCORD_BOT_TOKEN=<token> pnpm run register -- --guild <guild-id>`.
