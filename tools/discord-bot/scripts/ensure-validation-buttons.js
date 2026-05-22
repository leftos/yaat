// Ensures each scenario-validation channel has a pinned "Run Validation" button.
// Usage: DISCORD_BOT_TOKEN=... node scripts/ensure-validation-buttons.js [--artcc ZOA] [--dry-run]

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const channelsByArtcc = JSON.parse(
  readFileSync(join(__dirname, "../validation-channels.json"), "utf8"),
);

const BUTTON_CUSTOM_ID = "run_validation";
const USER_AGENT = "YAAT-Validation-Buttons/1.0";

function parseArgs(argv) {
  let artccFilter = (process.env.ARTCC_FILTER || "").trim().toUpperCase();
  let dryRun = false;
  for (let i = 2; i < argv.length; i++) {
    if (argv[i] === "--artcc" && argv[i + 1]) {
      artccFilter = argv[++i].toUpperCase();
    } else if (argv[i] === "--dry-run") {
      dryRun = true;
    }
  }
  return { artccFilter, dryRun };
}

function messageHasRunValidationButton(message) {
  for (const row of message.components || []) {
    for (const component of row.components || []) {
      if (component.custom_id === BUTTON_CUSTOM_ID) {
        return true;
      }
    }
  }
  return false;
}

async function discordApi(token, method, path, body) {
  const headers = {
    Authorization: `Bot ${token}`,
    "User-Agent": USER_AGENT,
  };
  let payload;
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    payload = JSON.stringify(body);
  }
  const res = await fetch(`https://discord.com/api/v10${path}`, {
    method,
    headers,
    body: payload,
  });
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`Discord API ${method} ${path} (${res.status}): ${text}`);
  }
  return text ? JSON.parse(text) : null;
}

function buildButtonMessage(artcc) {
  return {
    content:
      `**${artcc} scenario validation** — Results post below after each run. ` +
      `Click **Run Validation** to refresh this channel (also available via \`/validate\`).`,
    components: [
      {
        type: 1,
        components: [
          {
            type: 2,
            style: 1,
            label: "Run Validation",
            custom_id: BUTTON_CUSTOM_ID,
          },
        ],
      },
    ],
  };
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function ensureChannelButton(token, artcc, channelId, dryRun) {
  const pins = await discordApi(token, "GET", `/channels/${channelId}/pins`);
  for (const message of pins) {
    if (messageHasRunValidationButton(message)) {
      console.log(`${artcc}: pinned Run Validation button already present`);
      return;
    }
  }

  if (dryRun) {
    console.log(`${artcc}: would post and pin Run Validation button`);
    return;
  }

  const posted = await discordApi(
    token,
    "POST",
    `/channels/${channelId}/messages`,
    buildButtonMessage(artcc),
  );
  await discordApi(token, "PUT", `/channels/${channelId}/pins/${posted.id}`);
  console.log(`${artcc}: posted and pinned Run Validation button`);
}

async function main() {
  const token = process.env.DISCORD_BOT_TOKEN;
  if (!token) {
    console.error("DISCORD_BOT_TOKEN is required");
    process.exit(1);
  }

  const { artccFilter, dryRun } = parseArgs(process.argv);
  const entries = Object.entries(channelsByArtcc).sort(([a], [b]) => a.localeCompare(b));

  for (const [artcc, channelId] of entries) {
    if (artccFilter && artcc !== artccFilter) {
      continue;
    }
    await ensureChannelButton(token, artcc, channelId, dryRun);
    await sleep(500);
  }

  console.log("Done.");
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
