# YAAT Discord Bot

Cloudflare Worker that bridges Discord forum threads and GitHub issues.

**Slash commands** (restricted to one user):
- `/create-issue` — creates a GitHub issue labeled `bug` from the forum thread
- `/create-feature-request` — creates a GitHub issue labeled `enhancement`

**Auto-sync:** New thread replies are posted as GitHub issue comments every 15 minutes. Running the slash command again in a linked thread triggers an immediate sync.

**GitHub → Discord:** When an issue is labeled (in progress, completed, won't fix, not a bug, duplicate), closed, or reopened, a status update is posted back to the Discord thread.

## Setup

### 1. Create a Discord Application

1. Go to https://discord.com/developers/applications → **New Application**
2. Copy the **Application ID** and **Public Key** from General Information
3. Go to **Bot** → copy the **Token**
4. Go to **OAuth2** → URL Generator:
   - Scopes: `bot`, `applications.commands`
   - Bot Permissions: `Read Message History`, `Send Messages`
   - Copy the generated URL and open it to invite the bot to your server

### 2. Create a GitHub PAT

1. Go to https://github.com/settings/tokens → **Generate new token (classic)**
2. Scope: `repo`
3. Copy the token

### 3. Get your Discord User ID

1. Enable Developer Mode in Discord (Settings → Advanced)
2. Right-click your name → **Copy User ID**

### 4. Create the KV namespace

```bash
cd tools/discord-bot
npm install
npx wrangler kv namespace create THREAD_ISSUES
```

Copy the `id` from the output into `wrangler.toml` under `[[kv_namespaces]]`.

### 5. Deploy the Worker

```bash
# Set secrets
npx wrangler secret put DISCORD_PUBLIC_KEY
npx wrangler secret put DISCORD_BOT_TOKEN
npx wrangler secret put GITHUB_TOKEN
npx wrangler secret put DISCORD_ALLOWED_USER_ID
npx wrangler secret put GITHUB_WEBHOOK_SECRET    # any random string, e.g. openssl rand -hex 20

# Deploy
npm run deploy
```

### 6. Set the Discord Interactions URL

1. Copy the Worker URL from the deploy output (e.g. `https://yaat-discord-bot.<you>.workers.dev`)
2. Go to your Discord Application → **General Information**
3. Set **Interactions Endpoint URL** to the Worker URL
4. Save — Discord will verify the endpoint

### 7. Register Slash Commands

```bash
# Guild-scoped (instant, good for testing):
DISCORD_APP_ID=<app-id> DISCORD_BOT_TOKEN=<token> npm run register -- --guild <guild-id>

# Global (takes up to 1 hour to propagate):
DISCORD_APP_ID=<app-id> DISCORD_BOT_TOKEN=<token> npm run register
```

### 8. Set up GitHub Webhook

1. Go to https://github.com/leftos/yaat/settings/hooks → **Add webhook**
2. **Payload URL:** `https://yaat-discord-bot.<you>.workers.dev/github`
3. **Content type:** `application/json`
4. **Secret:** the same value you used for `GITHUB_WEBHOOK_SECRET`
5. **Events:** select "Let me select individual events" → check only **Issues**
6. Save

## Status Labels

The following GitHub labels trigger Discord notifications when applied:

| Label | Discord message |
|-------|-----------------|
| `in progress` | 🔧 In Progress |
| `completed` | ✅ Completed |
| `wontfix` | 🚫 Won't Fix |
| `not a bug` | ❌ Not a Bug |
| `duplicate` | ♻️ Duplicate |

Closing an issue also posts a status (✅ Completed or 🚫 Won't Fix depending on close reason). Reopening posts 🔄 Reopened.

Create these labels in GitHub if they don't exist yet.
