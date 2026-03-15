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

const url = guildId
  ? `https://discord.com/api/v10/applications/${appId}/guilds/${guildId}/commands`
  : `https://discord.com/api/v10/applications/${appId}/commands`;

console.log(`Registering ${commands.length} commands ${guildId ? `to guild ${guildId}` : "globally"}...`);

const res = await fetch(url, {
  method: "PUT",
  headers: {
    Authorization: `Bot ${botToken}`,
    "Content-Type": "application/json",
  },
  body: JSON.stringify(commands),
});

if (!res.ok) {
  console.error(`Failed (${res.status}):`, await res.text());
  process.exit(1);
}

const result = await res.json();
console.log(`Registered ${result.length} commands:`);
for (const cmd of result) {
  console.log(`  /${cmd.name} (${cmd.id})`);
}
