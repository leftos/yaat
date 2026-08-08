// Registers slash commands with Discord.
// Usage: DISCORD_APP_ID=... DISCORD_BOT_TOKEN=... node src/register.js [--guild GUILD_ID]

const commands = [
  {
    name: "create-issue",
    description: "Create a GitHub bug report from this forum thread",
    type: 1,
  },
  {
    name: "create-feature-request",
    description: "Create a GitHub feature request from this forum thread",
    type: 1,
  },
  {
    name: "track-issue",
    description: "Link this thread to a GitHub bug issue (or create a thread if run outside one)",
    type: 1,
    options: [
      {
        name: "issue_number",
        description: "The GitHub issue number to track",
        type: 4, // INTEGER
        required: true,
      },
    ],
  },
  {
    name: "track-feature-request",
    description: "Link this thread to a GitHub feature request (or create a thread if run outside one)",
    type: 1,
    options: [
      {
        name: "issue_number",
        description: "The GitHub issue number to track",
        type: 4, // INTEGER
        required: true,
      },
    ],
  },
  {
    name: "recreate-issue",
    description: "Re-fetch thread, reupload attachments, and replace the linked GitHub issue body",
    type: 1,
  },
  {
    name: "resolve",
    description: "Mark this thread as resolved (adds checkmark to title and reaction)",
    type: 1,
  },
  {
    name: "unresolve",
    description: "Unmark this thread as resolved (removes checkmark from title and reaction)",
    type: 1,
  },
  {
    name: "reopen",
    description: "Reopen the linked GitHub issue and unmark this thread as resolved",
    type: 1,
  },
  {
    name: "disconnect",
    description: "Unlink this thread from its GitHub issue (stops syncing new comments)",
    type: 1,
  },
  {
    name: "sync",
    description: "Force-sync new thread messages to the linked GitHub issue now",
    type: 1,
  },
  {
    name: "validate",
    description: "Re-run scenario validation for this ARTCC channel",
    type: 1,
  },
];

const appId = process.env.DISCORD_APP_ID;
const botToken = process.env.DISCORD_BOT_TOKEN;

if (!appId || !botToken) {
  console.error("Set DISCORD_APP_ID and DISCORD_BOT_TOKEN environment variables");
  process.exit(1);
}

// Use guild-scoped commands for faster propagation during dev
const guildArg = process.argv.indexOf("--guild");
const guildId = guildArg !== -1 ? process.argv[guildArg + 1] : null;

const API = "https://discord.com/api/v10";
const headers = {
  Authorization: `Bot ${botToken}`,
  "Content-Type": "application/json",
};

async function putCommands(url, body, label) {
  const res = await fetch(url, { method: "PUT", headers, body: JSON.stringify(body) });
  if (!res.ok) {
    console.error(`${label} failed (${res.status}):`, await res.text());
    process.exit(1);
  }
  return res.json();
}

const url = guildId
  ? `${API}/applications/${appId}/guilds/${guildId}/commands`
  : `${API}/applications/${appId}/commands`;

console.log(`Registering ${commands.length} commands ${guildId ? `to guild ${guildId}` : "globally"}...`);

const result = await putCommands(url, commands, "Registration");
console.log(`Registered ${result.length} commands:`);
for (const cmd of result) {
  console.log(`  /${cmd.name} (${cmd.id})`);
}

// Commands registered in the other scope would show every command twice in the picker,
// so clear whichever scope we did not just write.
if (guildId) {
  await putCommands(`${API}/applications/${appId}/commands`, [], "Clearing global commands");
  console.log("Cleared global commands (guild-scoped registration is the single source).");
} else {
  const guildsRes = await fetch(`${API}/users/@me/guilds`, { headers });
  if (!guildsRes.ok) {
    console.error(`Listing bot guilds failed (${guildsRes.status}):`, await guildsRes.text());
    process.exit(1);
  }
  for (const guild of await guildsRes.json()) {
    await putCommands(`${API}/applications/${appId}/guilds/${guild.id}/commands`, [], `Clearing guild ${guild.id} commands`);
    console.log(`Cleared guild-scoped commands in ${guild.name} (${guild.id}).`);
  }
}
