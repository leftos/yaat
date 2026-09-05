// Creates the server-cost tracker's Discord surfaces and records their ids in support-config.json:
// the two supporter roles (assigned by Ko-fi's Discord integration), a locked voice channel whose
// name is the ticker, a read-only text channel, and the pinned embed the worker edits in place.
// Idempotent: anything whose id in support-config.json still resolves is kept.
//
// Usage: DISCORD_BOT_TOKEN=... node scripts/setup-support.js --guild <id> [--category <id>]
//          [--kofi-url https://ko-fi.com/leftos] [--cost 24] [--dry-run]

import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { buildSupportMessage, computeProgress, formatChannelName } from "../src/support.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const CONFIG_PATH = join(__dirname, "../support-config.json");
const USER_AGENT = "YAAT-Support-Setup/1.0";

const ONE_TIME_ROLE = { name: "One-time Supporter", color: 0xf7c948 };
const MONTHLY_ROLE = { name: "Monthly Supporter", color: 0x29abe0 };
const TEXT_CHANNEL_NAME = "server-costs";
const TEXT_CHANNEL_TOPIC = "How close YAAT's hosting bill is to being covered this month. Support via Ko-fi — the button in the pinned message.";

const GUILD_TEXT = 0;
const GUILD_VOICE = 2;
const OVERWRITE_ROLE = 0;
const OVERWRITE_MEMBER = 1;

const VIEW_CHANNEL = 1n << 10n;
const SEND_MESSAGES = 1n << 11n;
const CONNECT = 1n << 20n;
const CREATE_PUBLIC_THREADS = 1n << 35n;
const CREATE_PRIVATE_THREADS = 1n << 36n;
const SEND_MESSAGES_IN_THREADS = 1n << 38n;

function parseArgs(argv) {
  const args = { guildId: "", categoryId: "", kofiUrl: process.env.KOFI_PAGE_URL || "https://ko-fi.com/leftos", cost: 24, dryRun: false };
  for (let i = 2; i < argv.length; i++) {
    const flag = argv[i];
    if (flag === "--guild" && argv[i + 1]) args.guildId = argv[++i];
    else if (flag === "--category" && argv[i + 1]) args.categoryId = argv[++i];
    else if (flag === "--kofi-url" && argv[i + 1]) args.kofiUrl = argv[++i];
    else if (flag === "--cost" && argv[i + 1]) args.cost = Number(argv[++i]);
    else if (flag === "--dry-run") args.dryRun = true;
    else throw new Error(`Unknown or incomplete argument: ${flag}`);
  }
  if (!Number.isFinite(args.cost) || args.cost <= 0) throw new Error("--cost must be a positive number");
  return args;
}

function loadConfig() {
  const empty = { guildId: "", voiceChannelId: "", textChannelId: "", embedMessageId: "", oneTimeRoleId: "", monthlyRoleId: "" };
  if (!existsSync(CONFIG_PATH)) return empty;
  return { ...empty, ...JSON.parse(readFileSync(CONFIG_PATH, "utf8")) };
}

async function discordApi(token, method, path, body) {
  const headers = { Authorization: `Bot ${token}`, "User-Agent": USER_AGENT };
  let payload;
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    payload = JSON.stringify(body);
  }
  const res = await fetch(`https://discord.com/api/v10${path}`, { method, headers, body: payload });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`Discord API ${method} ${path} (${res.status}): ${text}`);
  }
  return text ? JSON.parse(text) : null;
}

async function ensureRole(api, guildId, existingRoles, configuredId, spec, dryRun) {
  const byId = configuredId && existingRoles.find((role) => role.id === configuredId);
  const byName = existingRoles.find((role) => role.name === spec.name);
  const found = byId || byName;
  if (found) {
    console.log(`Role "${spec.name}" already exists (${found.id})`);
    return found.id;
  }
  if (dryRun) {
    console.log(`Would create role "${spec.name}"`);
    return "";
  }
  const created = await api("POST", `/guilds/${guildId}/roles`, {
    name: spec.name,
    color: spec.color,
    hoist: false,
    mentionable: false,
  });
  console.log(`Created role "${spec.name}" (${created.id})`);
  return created.id;
}

async function ensureChannel(api, guildId, existingChannels, configuredId, match, create, dryRun) {
  const byId = configuredId && existingChannels.find((channel) => channel.id === configuredId);
  const found = byId || existingChannels.find(match);
  if (found) {
    console.log(`Channel "${found.name}" already exists (${found.id})`);
    return found.id;
  }
  if (dryRun) {
    console.log(`Would create channel "${create.name}"`);
    return "";
  }
  const created = await api("POST", `/guilds/${guildId}/channels`, create);
  console.log(`Created channel "${created.name}" (${created.id})`);
  return created.id;
}

async function ensureEmbedMessage(api, textChannelId, configuredId, message, dryRun) {
  if (configuredId) {
    try {
      await api("GET", `/channels/${textChannelId}/messages/${configuredId}`);
      console.log(`Embed message already exists (${configuredId})`);
      return configuredId;
    } catch (err) {
      console.log(`Configured embed message ${configuredId} is gone (${err.message}); posting a new one`);
    }
  }
  if (dryRun) {
    console.log("Would post and pin the progress embed");
    return "";
  }
  const posted = await api("POST", `/channels/${textChannelId}/messages`, message);
  await api("PUT", `/channels/${textChannelId}/pins/${posted.id}`);
  console.log(`Posted and pinned the progress embed (${posted.id})`);
  return posted.id;
}

async function main() {
  const token = process.env.DISCORD_BOT_TOKEN;
  if (!token) {
    console.error("DISCORD_BOT_TOKEN is required");
    process.exit(1);
  }

  const args = parseArgs(process.argv);
  const config = loadConfig();
  const guildId = args.guildId || config.guildId;
  if (!guildId) {
    console.error("--guild <id> is required the first time (support-config.json has no guildId yet)");
    process.exit(1);
  }
  const api = (method, path, body) => discordApi(token, method, path, body);

  const botUser = await api("GET", "/users/@me");
  const roles = await api("GET", `/guilds/${guildId}/roles`);
  const channels = await api("GET", `/guilds/${guildId}/channels`);

  const oneTimeRoleId = await ensureRole(api, guildId, roles, config.oneTimeRoleId, ONE_TIME_ROLE, args.dryRun);
  const monthlyRoleId = await ensureRole(api, guildId, roles, config.monthlyRoleId, MONTHLY_ROLE, args.dryRun);

  const progress = computeProgress([], args.cost, new Date());
  const parent = args.categoryId ? { parent_id: args.categoryId } : {};

  const voiceChannelId = await ensureChannel(
    api,
    guildId,
    channels,
    config.voiceChannelId,
    (channel) => channel.type === GUILD_VOICE && channel.name.startsWith("💰 Server:"),
    {
      name: formatChannelName(progress),
      type: GUILD_VOICE,
      ...parent,
      permission_overwrites: [{ id: guildId, type: OVERWRITE_ROLE, deny: String(CONNECT) }],
    },
    args.dryRun,
  );

  const textChannelId = await ensureChannel(
    api,
    guildId,
    channels,
    config.textChannelId,
    (channel) => channel.type === GUILD_TEXT && channel.name === TEXT_CHANNEL_NAME,
    {
      name: TEXT_CHANNEL_NAME,
      type: GUILD_TEXT,
      topic: TEXT_CHANNEL_TOPIC,
      ...parent,
      permission_overwrites: [
        {
          id: guildId,
          type: OVERWRITE_ROLE,
          deny: String(SEND_MESSAGES | CREATE_PUBLIC_THREADS | CREATE_PRIVATE_THREADS | SEND_MESSAGES_IN_THREADS),
        },
        { id: botUser.id, type: OVERWRITE_MEMBER, allow: String(VIEW_CHANNEL | SEND_MESSAGES) },
      ],
    },
    args.dryRun,
  );

  const embedMessageId = textChannelId
    ? await ensureEmbedMessage(api, textChannelId, config.embedMessageId, buildSupportMessage(progress, args.kofiUrl), args.dryRun)
    : "";

  const next = { guildId, voiceChannelId, textChannelId, embedMessageId, oneTimeRoleId, monthlyRoleId };
  if (args.dryRun) {
    console.log("Dry run — support-config.json not written:", JSON.stringify(next, null, 2));
    return;
  }
  writeFileSync(CONFIG_PATH, JSON.stringify(next, null, 2) + "\n");
  console.log(`Wrote ${CONFIG_PATH}`);
  console.log("\nNext steps:");
  console.log("  1. wrangler secret put KOFI_VERIFICATION_TOKEN  (from ko-fi.com/manage/webhooks)");
  console.log("  2. pnpm run deploy, then set the Ko-fi webhook URL to https://<worker>/kofi");
  console.log("  3. Register the slash commands (pnpm run register ...) so /support-refresh and /support-forget exist");
  console.log("  4. ko-fi.com/Discord/Settings: attach One-time Supporter and Monthly Supporter to the matching rewards,");
  console.log("     and drag the Ko-fi bot's role above both in Server Settings → Roles");
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
