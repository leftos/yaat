# Discord Integration

YAAT touches Discord in two unrelated ways: the community server's automation (GitHub Actions workflows, the bot, the
server-cost tracker) and the desktop client's [Rich Presence](#desktop-client-rich-presence), which shows the scenario a
user is running on their own Discord profile.

## GitHub Actions Workflows (`.github/workflows/`)

| Workflow | Repo | Trigger | What it does |
|----------|------|---------|--------------|
| `discord-docs.yml` | leftos/yaat | Push to `main` (INSTALL/README/GETTING_STARTED/USER_GUIDE/COMMANDS/SOLO_TRAINING) + manual | Clears + reposts doc content to dedicated channels via bot token; large reference docs post ToC only |
| `discord-scenario-validation.yml` | **leftos/yaat-server** | Sundays 10:00 UTC cron + `workflow_dispatch` | Validates all ARTCC scenarios via `Yaat.ScenarioValidator`, posts reports to per-ARTCC channels, ensures pinned **Run Validation** buttons |
| `nightly-review-alert.yml` ("Nightly Review Notify") | leftos/yaat | `workflow_run` after **Nightly Review** completes | Posts every Nightly Review outcome to the CI/alerts channel via `DISCORD_CI_WEBHOOK_URL` (green on success, red on failure/timeout, white on cancel), with a TL;DR of what was reviewed and filed. The TL;DR + API-equivalent usage estimate + the reviewer's full markdown report (`report.md`) ride along as the review job's `nightly-review-notify` artifact; a timeout-killed run still posts the bare conclusion. Separate `workflow_run` job so a self-timeout still notifies. An agent-level failure (a revoked `CLAUDE_CODE_OAUTH_TOKEN`, an exhausted quota) does **not** fail the `claude-code-action` step — the agent returns a `result` record with `is_error: true` and the step reports success — so the review job's publish step reads that record, puts its text in the TL;DR and fails the job itself; before 2026-09-17 three such nights posted green. |

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

## Server-cost tracker (Ko-fi)

Shows how close YAAT's hosting bill (`MONTHLY_COST_USD`, $24/mo on DigitalOcean) is to being covered, fed by Ko-fi. Logic in `tools/discord-bot/src/support.js`; ids in `tools/discord-bot/support-config.json` (written by `pnpm run setup-support`).

**Surfaces.** A locked voice channel whose *name* is the ticker (`💰 Server: $15 / $24 · 62%`, visible in the sidebar without opening anything), and a read-only `#server-costs` text channel holding one pinned embed the worker edits in place (progress bar, carried-over amount, this month's public supporters, a Ko-fi link button). Each public payment also gets a one-line thank-you post there. No per-person amounts anywhere.

**Metric — calendar month with carry-forward.** The bar for month *M* is `carry(M) + payments(M)` against the cost, where `carry(M) = max(0, carry(M-1) + payments(M-1) − cost)`: a surplus rolls into the next month (the embed says how many further months it already covers), a shortfall floors at zero rather than becoming debt. Gross amounts, UTC months. One-time tips and every monthly membership charge both count; shop orders and commissions are acknowledged and ignored.

**Ko-fi webhook → `POST /kofi`.** Ko-fi sends `application/x-www-form-urlencoded` with a single `data` field holding the payment JSON, authenticated by a `verification_token` inside that JSON (no HMAC) — the worker compares it to the `KOFI_VERIFICATION_TOKEN` secret in constant time and answers 401 otherwise. Ko-fi wants a 2xx within 15 s and retries up to four times, so the ledger write is synchronous and the Discord updates run in `ctx.waitUntil`; a redelivery is recognised by `kofi_transaction_id` and acknowledged without a second count. Ko-fi has **no cancellation or refund event**, which is fine for a payments-per-month metric; refunds and dashboard test events are removed by hand with `/support-forget`.

**Ledger.** One KV key per payment, `support:payment:{kofi_transaction_id}`, holding `{id, name, isPublic, amount, currency, isSubscription, paidAt, month}` — the record rides in KV *metadata* so a refresh is a single `list()` call, never one `get` per payment. The supporter's email is never stored. `isPublic` is true only when Ko-fi's `is_public` is true *and* the name is not "Anonymous"; everything else is counted as anonymous. `support:display` remembers the last channel name and embed hash actually written, so the 5-minute cron (which also handles the month rollover) only touches Discord when something changed — Discord allows two channel renames per ten minutes — and a rejected write is retried next tick because it was never recorded as done.

**Roles.** `One-time Supporter` and `Monthly Supporter` are created by the setup script but *assigned by Ko-fi's own Discord integration* (ko-fi.com/Discord/Settings → attach each role to the matching reward; the Ko-fi bot's role must sit above both). Ko-fi removes the monthly role when support stops and leaves the one-time role permanently, which is the intended behaviour. Supporters claim their role from the Ko-fi page ("Connect to Discord & Claim"). Discord rewards need Contributor/Standard mode on Ko-fi (5% on one-time tips).

**Admin commands** (owner-only, behind `DISCORD_ALLOWED_USER_ID`): `/support-refresh` re-renders both surfaces from the ledger; `/support-forget <transaction_id>` drops one payment (test event, refund) and re-renders.

**Setup.** `DISCORD_BOT_TOKEN=<token> pnpm run setup-support -- --guild <id> [--category <id>] [--kofi-url URL] [--cost 24] [--dry-run]` — idempotently creates the roles, the two channels (voice: `@everyone` denied Connect; text: `@everyone` denied Send Messages/threads, the bot allowed), posts + pins the seed embed, writes `support-config.json` (commit it), and prints the remaining manual steps. The bot needs **Manage Channels** and **Manage Roles** for this. Then `wrangler secret put KOFI_VERIFICATION_TOKEN`, deploy, set the webhook URL to `https://<worker>/kofi` on ko-fi.com/manage/webhooks, and re-register the slash commands.

## Desktop client Rich Presence

While a scenario is active, the desktop client publishes it as the signed-in Discord user's activity (GitHub issue
#439). Nothing here involves the bot, the server or a network call: the client talks to the Discord desktop app on the
same machine over Discord's local RPC channel.

**What is sent.** One activity under YAAT's own Discord application, id `1551088521731772439`
(`MainWindow.DiscordApplicationId`), with no image assets:

| Discord field | Value |
|---|---|
| `details` (line 1) | `ActiveScenarioName`, or the scenario id until a name is known |
| `state` (line 2) | `ARTCC · airport` from `UserPreferences.ArtccId` and `ActiveScenarioPrimaryAirportId` — only the half that is known; the field is left off the wire when neither is |
| `timestamps.start` | Unix seconds the scenario started, which Discord renders as a running elapsed timer. Stamped as "now minus `ElapsedSeconds`", so a joiner's timer matches the room's and a loaded recording starts at its tape position. It is a wall-clock stamp: pause, sim rate, rewind and skip do not move it |

Nothing is published when no scenario is active, and the room, its members and other people's names never go on the
wire. Both text fields are clamped to 128 characters (`DiscordRpcJson.Clamp`, ellipsis-terminated, never splitting a
surrogate pair) because Discord rejects a longer one outright and the rejection only comes back in the `SET_ACTIVITY`
response, which the client does not inspect.

**User control.** `UserPreferences.DiscordRichPresenceEnabled` (JSON key `discordRichPresenceEnabled`, default `true`),
the **Discord** checkbox on Settings → Identity. It takes effect on Settings save via `MainViewModel.RefreshRichPresence`.
The publish points in the scenario lifecycle are described in
[client-mainviewmodel.md](client-mainviewmodel.md#scenario-activation--three-paths-one-router).

**Protocol.** Discord's RPC over local IPC (<https://discord.com/developers/docs/topics/rpc>), hand-rolled — no package
dependency:

- **Endpoint**: `discord-ipc-0` … `discord-ipc-9`, tried in order. A Windows named pipe (200 ms connect bound per
  endpoint); elsewhere a Unix domain socket in the first of `XDG_RUNTIME_DIR`, `TMPDIR`, `TMP`, `TEMP` that is set,
  else `/tmp`.
- **Frame**, both directions: `[int32 opcode LE][int32 length LE][UTF-8 JSON]`. Opcodes: 0 handshake, 1 frame, 2 close,
  3 ping, 4 pong. A declared length that is negative or above 64 KiB is refused on read.
- **Conversation**: the client sends opcode 0 `{"v":1,"client_id":"…"}`, waits for the opcode-1 frame whose `evt` is
  `READY`, then sends opcode 1 `{"cmd":"SET_ACTIVITY","args":{"pid":…,"activity":{…}},"nonce":"…"}`. `pid` is the
  client's own process id (Discord keys the activity on it). A ping is answered with a pong carrying the same payload.

**Files** (`src/Yaat.Client/Services/Discord/`):

| File | Role |
|---|---|
| `DiscordIpcFrame.cs` | Frame read/write, opcode constants, the payload cap |
| `DiscordActivity.cs` | The `DiscordActivity` record (`Details`, `State`, `StartUnixSeconds`) and `DiscordRpcJson`, the `Utf8JsonWriter` builders for the handshake and `SET_ACTIVITY` payloads |
| `IDiscordIpcConnector.cs` / `DiscordIpcConnector.cs` | Finds and opens the endpoint; returns null when Discord is not running |
| `IRichPresencePublisher.cs` | What `MainViewModel` sees: `Publish(activity)` / `Clear()`, both fire-and-forget |
| `DiscordRichPresenceService.cs` | The worker that owns the connection |

**Worker behaviour.** `DiscordRichPresenceService` runs one background task. `Publish`/`Clear` only record the wanted
activity and wake it; the latest value wins, so a change that lands mid-connect replaces the earlier one instead of
queueing behind it. The worker connects only while an activity is wanted and keeps the connection open to answer
pings and pick up changes.

**Fail-silent, with back-off.** Discord not running is the ordinary case, so every failure is cheap and none reaches
the user. A sweep that finds no endpoint, a handshake Discord does not acknowledge within 10 s, a close frame and a
dropped connection all end the session; the worker retries after 5 s, doubling to a 60 s ceiling, and resets to 5 s
after a session that ended cleanly. A change to the wanted activity cuts a back-off short, so starting a scenario never
waits out a minute inherited from an earlier failure. Logging goes to `AppLog` at Debug (per-endpoint misses at Trace);
"Discord is not running" is logged once per absence — again only after a connection has succeeded in between.
`Dispose` cancels the worker and waits at most 2 s, because it runs on the UI thread from `MainWindow.OnClosing`; a
worker wedged in a native pipe call is abandoned with a warning rather than holding the window open.

**Kept out of tests.** `MainWindow` constructs the service only when `App.DiscordRichPresenceAvailable` is true, and
only `Program.Main` sets it — so a headless host (`Yaat.Client.UI.Tests`, `Yaat.GuideCapture`) never opens a pipe to
the developer's own Discord and publishes a status from a test run. `MainViewModel.RichPresence` stays null there;
`MainViewModelRichPresenceTests` assigns a recording fake. The service has an internal constructor taking an
`IDiscordIpcConnector` and a `TimeProvider`: `DiscordRichPresenceServiceTests` scripts Discord's side over an
in-memory duplex stream and drives the handshake timeout and back-off with a manual clock.

### Footguns

- **Clearing is closing the connection.** Discord drops an application's activity as soon as its IPC connection goes
  away, and that is the only mechanism `Clear()` uses. A `SET_ACTIVITY` frame with a null activity is not relied on —
  do not "optimise" the close into one to keep the pipe warm.
- **The availability flag and the preference are different switches.** `App.DiscordRichPresenceAvailable` decides
  whether the service exists at all (host-level, never persisted); `DiscordRichPresenceEnabled` decides whether the
  existing service is given anything to show.
- **`ApplyRecordingResult` is a publish point too.** A recording load replaces the scenario without going through
  `ApplyScenarioBootstrap`; without its own `StartRichPresence` call Discord would keep showing the scenario the
  recording replaced.
