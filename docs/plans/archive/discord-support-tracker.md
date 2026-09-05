# Discord server-cost tracker (Ko-fi) — shipped 2026-09-05

Operating reference promoted to `docs/discord-integration.md` § Server-cost tracker; this file is the decision record.

Show progress towards YAAT's running server cost ($24/mo on DigitalOcean) in the Discord server, fed by Ko-fi, and give supporters roles.

## Decisions (interview 2026-09-05)

- **Platform: Ko-fi only.** The repo already links Ko-fi (README, Help → About). Ko-fi's webhook fires once per payment (one-time tips and every monthly membership charge); it has no cancellation or refund event. Buy Me a Coffee was considered and dropped.
- **Roles: Ko-fi's native Discord integration**, not the bot. Ko-fi assigns a role to one-time supporters and to monthly supporters/tier members, and removes the monthly role itself when support stops (one-time roles are permanent, which is what we want). The bot never sees supporter emails. Requires Contributor/Standard mode on Ko-fi (5% on one-time tips).
- **Metric: calendar-month payments with carry-forward.** The bar for month *M* is `carry(M) + payments(M)` vs the monthly cost. `carry(M) = max(0, carry(M-1) + payments(M-1) − cost)`, so a surplus rolls into the next month and a shortfall floors at zero (no debt). Gross amounts.
- **Surfaces: both.** A locked voice channel whose *name* is the ticker (sidebar-visible), plus a text channel with one bot-edited embed (progress bar, carried-over line, this month's supporters by public name, no per-person amounts, a Ko-fi link button). A short thank-you post per public payment.
- **Data: webhook → KV ledger.** `support:payment:{kofi_transaction_id}`; the record rides in KV `metadata` so a refresh is one `list()` call. Idempotent on transaction id (Ko-fi retries).
- **Setup: a script creates the roles + channels + seed embed** and writes their ids to `tools/discord-bot/support-config.json` (committed, like `validation-channels.json`).
- **Admin commands** (owner-only, behind the existing `DISCORD_ALLOWED_USER_ID` gate): `/support-refresh` (force re-render), `/support-forget <transaction_id>` (drop a test event or refunded payment, then re-render). Ko-fi has no refund webhook, so this is the only correction path.

## Ko-fi webhook facts (help.ko-fi.com, api-evangelist/ko-fi asyncapi)

- `POST`, `application/x-www-form-urlencoded`, single field `data` holding a JSON string.
- Verify by comparing `verification_token` (from ko-fi.com/manage/webhooks) with a constant-time compare. No HMAC.
- Fields: `message_id`, `timestamp` (ISO), `type` (`Donation` | `Subscription` | `Shop Order` | `Commission`), `is_public`, `from_name` (may be `Anonymous`), `amount` (decimal string), `currency`, `is_subscription_payment`, `is_first_subscription_payment`, `kofi_transaction_id`, `tier_name`.
- Count `Donation` and `Subscription` only; log and ignore the rest.
- Dashboard test payments carry a real token and a fixed sample transaction id; drop them with `/support-forget`.

## Tasks

- [x] Plan written; MAIN.md pointer added
- [x] `src/support.js`: parse + verify Ko-fi payload, ledger record, `computeProgress` (carry-forward, floor 0), channel-name + embed builders, `refreshSupportDisplay` (rename only on change, 429-tolerant), thank-you post, `forgetPayment`
- [x] `worker.js`: route `POST /kofi`, admin commands `support-refresh` / `support-forget`, cron calls `refreshSupportDisplay`
- [x] `register.js`: register the two commands
- [x] `scripts/setup-support.js` + `pnpm run setup-support`: idempotently create roles (`One-time Supporter`, `Monthly Supporter`), locked voice channel, read-only text channel, seed embed; write `support-config.json`
- [x] `wrangler.toml`: `MONTHLY_COST_USD`, `KOFI_PAGE_URL`; document the `KOFI_VERIFICATION_TOKEN` secret
- [x] Tests (`src/support.test.js`): token check, ledger idempotency, ignored types, carry-forward math (surplus rolls, shortfall floors, month boundary UTC), anonymous/public names, rename skipped when unchanged, forget
- [x] Docs: `docs/discord-integration.md` section, bot `README.md` setup steps (webhook URL, Ko-fi Discord settings, role order), `docs/architecture.md` entry for `tools/discord-bot/`, CHANGELOG bullet
- [x] Owner steps (done 2026-09-05, live at https://yaat-discord-bot.leftos.workers.dev/kofi): `pnpm run deploy`, `wrangler secret put KOFI_VERIFICATION_TOKEN`, `pnpm run setup-support`, register commands, set the webhook URL on Ko-fi, attach the two roles under ko-fi.com/Discord/Settings and drag **Ko-fi bot** above them
- [x] Archive this plan
