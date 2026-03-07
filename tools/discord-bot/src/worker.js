// Discord interaction types
const PING = 1;
const APPLICATION_COMMAND = 2;

// Response types
const PONG = 1;
const DEFERRED_CHANNEL_MESSAGE = 5;

// Status labels → display text and emoji
const STATUS_LABELS = {
  "in progress": {
    emoji: "🔧",
    message: "This issue is now **in progress** — someone is actively working on it.",
  },
  completed: {
    emoji: "✅",
    message: "This issue has been **completed** and the fix/feature should be available soon.",
  },
  wontfix: {
    emoji: "🚫",
    message:
      "This issue has been marked as **won't fix** — it's been reviewed but won't be addressed at this time.",
  },
  "not a bug": {
    emoji: "❌",
    message:
      "This has been reviewed and determined to be **not a bug** — the current behavior is working as intended.",
  },
  duplicate: {
    emoji: "♻️",
    message:
      "This issue has been closed as a **duplicate** — it's already being tracked in another issue.",
  },
};

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (request.method !== "POST") {
      return new Response("Method not allowed", { status: 405 });
    }

    // GitHub webhook endpoint
    if (url.pathname === "/github") {
      return handleGitHubWebhook(request, env, ctx);
    }

    // Discord interactions endpoint (default path)
    return handleDiscordInteraction(request, env, ctx);
  },

  // Cron trigger: sync new thread replies → GitHub issue comments
  async scheduled(event, env, ctx) {
    ctx.waitUntil(syncAllThreads(env));
  },
};

// --- Discord interaction handler ---

async function handleDiscordInteraction(request, env, ctx) {
  const body = await request.text();
  const signature = request.headers.get("x-signature-ed25519");
  const timestamp = request.headers.get("x-signature-timestamp");

  const isValid = await verifyDiscordSignature(
    env.DISCORD_PUBLIC_KEY,
    signature,
    timestamp,
    body,
  );
  if (!isValid) {
    return new Response("Invalid signature", { status: 401 });
  }

  const interaction = JSON.parse(body);

  if (interaction.type === PING) {
    return jsonResponse({ type: PONG });
  }

  if (interaction.type === APPLICATION_COMMAND) {
    const userId = interaction.member?.user?.id || interaction.user?.id;
    if (userId !== env.DISCORD_ALLOWED_USER_ID) {
      return ephemeral("You don't have permission to use this command.");
    }

    const channel = interaction.channel;
    if (!channel || (channel.type !== 11 && channel.type !== 12)) {
      return ephemeral("This command must be used inside a forum thread.");
    }

    ctx.waitUntil(
      processCommand({
        threadId: channel.id,
        guildId: interaction.guild_id,
        commandName: interaction.data.name,
        token: interaction.token,
        appId: interaction.application_id,
        env,
      }).catch((err) => console.error("Command processing failed:", err)),
    );

    return jsonResponse({ type: DEFERRED_CHANNEL_MESSAGE });
  }

  return new Response("Unknown interaction type", { status: 400 });
}

// --- GitHub webhook handler ---

async function handleGitHubWebhook(request, env, ctx) {
  const body = await request.text();
  const signature = request.headers.get("x-hub-signature-256");

  const isValid = await verifyGitHubSignature(env.GITHUB_WEBHOOK_SECRET, signature, body);
  if (!isValid) {
    return new Response("Invalid signature", { status: 401 });
  }

  const event = request.headers.get("x-github-event");
  const payload = JSON.parse(body);

  if (event === "issues") {
    ctx.waitUntil(handleIssueEvent(payload, env));
  }

  return new Response("OK", { status: 200 });
}

async function handleIssueEvent(payload, env) {
  const { action, issue, label } = payload;
  const issueNumber = issue.number;

  // Find the Discord thread linked to this issue
  const threadId = await findThreadForIssue(env, issueNumber);
  if (!threadId) return;

  const issueLink = `[#${issueNumber}](${issue.html_url})`;
  let statusMessage = null;

  if (action === "labeled" && label) {
    const status = STATUS_LABELS[label.name.toLowerCase()];
    if (status) {
      statusMessage = `${status.emoji} ${status.message}\n\nSee ${issueLink} for details.`;
    }
  } else if (action === "closed") {
    if (issue.state_reason === "not_planned") {
      statusMessage = `🚫 This issue has been **closed** and won't be addressed at this time. If you think this was a mistake, feel free to comment below.\n\nSee ${issueLink} for details.`;
    } else {
      statusMessage = `✅ This issue has been **resolved**! The fix should be available soon.\n\nSee ${issueLink} for details.`;
    }
  } else if (action === "reopened") {
    statusMessage = `🔄 This issue has been **reopened** and will be looked at again.\n\nSee ${issueLink} for details.`;
  }

  if (statusMessage) {
    await postToDiscordThread(env.DISCORD_BOT_TOKEN, threadId, statusMessage);
  }
}

async function findThreadForIssue(env, issueNumber) {
  // Check reverse mapping first
  const threadId = await env.THREAD_ISSUES.get(`issue:${issueNumber}`);
  if (threadId) return threadId;

  // Fallback: scan all thread mappings (only needed for issues created before reverse mapping)
  const keys = await env.THREAD_ISSUES.list();
  for (const key of keys.keys) {
    if (key.name.startsWith("issue:")) continue;
    const mapping = await env.THREAD_ISSUES.get(key.name, { type: "json" });
    if (mapping && mapping.issueNumber === issueNumber) {
      // Backfill reverse mapping
      await env.THREAD_ISSUES.put(`issue:${issueNumber}`, key.name);
      return key.name;
    }
  }
  return null;
}

async function postToDiscordThread(botToken, threadId, content) {
  const res = await fetch(`https://discord.com/api/v10/channels/${threadId}/messages`, {
    method: "POST",
    headers: {
      Authorization: `Bot ${botToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ content, flags: 4 }),
  });
  if (!res.ok) {
    console.error("Failed to post to Discord thread:", await res.text());
  }
}

// --- Command processing ---

async function processCommand({ threadId, guildId, commandName, token, appId, env }) {
  try {
    const existing = await env.THREAD_ISSUES.get(threadId, { type: "json" });

    if (existing) {
      const count = await syncThread(env, threadId, existing);
      await editOriginalResponse(appId, token, {
        content:
          count > 0
            ? `Synced ${count} new message(s) to ${existing.issueUrl}`
            : `Already up to date: ${existing.issueUrl}`,
      });
      return;
    }

    const thread = await discordApi(`/channels/${threadId}`, env.DISCORD_BOT_TOKEN);
    const threadUrl = `https://discord.com/channels/${guildId}/${threadId}`;

    // Resolve forum tags to label names
    const labels = [commandName === "create-issue" ? "bug" : "enhancement"];
    if (thread.applied_tags?.length && thread.parent_id) {
      const parent = await discordApi(`/channels/${thread.parent_id}`, env.DISCORD_BOT_TOKEN);
      if (parent.available_tags) {
        const tagMap = new Map(parent.available_tags.map((t) => [t.id, t.name]));
        for (const tagId of thread.applied_tags) {
          const tagName = tagMap.get(tagId);
          if (tagName) labels.push(tagName.toLowerCase());
        }
      }
    }

    const messages = await discordApi(
      `/channels/${threadId}/messages?limit=100`,
      env.DISCORD_BOT_TOKEN,
    );
    messages.reverse();

    const conversation = formatConversation(messages);
    const body = `> Created from [Discord thread](${threadUrl})\n\n## Conversation\n\n${conversation}`;

    const issue = await createGitHubIssue(env.GITHUB_TOKEN, env.GITHUB_REPO, {
      title: thread.name,
      body,
      labels,
    });

    const lastMessageId = messages.length > 0 ? messages[messages.length - 1].id : "0";
    const mapping = {
      issueNumber: issue.number,
      issueUrl: issue.html_url,
      guildId,
      lastSyncedMessageId: lastMessageId,
    };

    // Store both forward (thread→issue) and reverse (issue→thread) mappings
    await env.THREAD_ISSUES.put(threadId, JSON.stringify(mapping));
    await env.THREAD_ISSUES.put(`issue:${issue.number}`, threadId);

    await editOriginalResponse(appId, token, {
      content: `Created GitHub issue: ${issue.html_url}`,
    });
  } catch (err) {
    console.error("Error processing command:", err);
    await editOriginalResponse(appId, token, {
      content: `Failed to create issue: ${err.message}`,
    });
  }
}

// --- Sync logic ---

async function syncAllThreads(env) {
  const keys = await env.THREAD_ISSUES.list();
  let synced = 0;

  for (const key of keys.keys) {
    if (key.name.startsWith("issue:")) continue;
    try {
      const mapping = await env.THREAD_ISSUES.get(key.name, { type: "json" });
      if (!mapping) continue;
      const count = await syncThread(env, key.name, mapping);
      synced += count;
    } catch (err) {
      console.error(`Failed to sync thread ${key.name}:`, err);
    }
  }

  if (synced > 0) {
    console.log(`Cron sync: posted ${synced} comment(s) across ${keys.keys.length} thread(s)`);
  }
}

async function syncThread(env, threadId, mapping) {
  const messages = await discordApi(
    `/channels/${threadId}/messages?after=${mapping.lastSyncedMessageId}&limit=100`,
    env.DISCORD_BOT_TOKEN,
  );

  if (messages.length === 0) return 0;

  messages.reverse();

  // Skip messages posted by the bot itself (status updates) to avoid echo loops
  const botMessages = new Set();
  const filteredMessages = messages.filter((msg) => {
    if (msg.author.bot) {
      botMessages.add(msg.id);
      return false;
    }
    return true;
  });

  for (const msg of filteredMessages) {
    const author = msg.author.global_name || msg.author.username;
    const commentBody = `**${author}** via Discord:\n\n${formatMessage(msg)}`;

    await createGitHubComment(env.GITHUB_TOKEN, env.GITHUB_REPO, mapping.issueNumber, commentBody);
  }

  // Always update cursor to latest message (including bot messages)
  mapping.lastSyncedMessageId = messages[messages.length - 1].id;
  await env.THREAD_ISSUES.put(threadId, JSON.stringify(mapping));

  return filteredMessages.length;
}

// --- Formatting ---

function formatMessage(msg) {
  const parts = [];
  if (msg.content) parts.push(msg.content);
  if (msg.attachments?.length) {
    for (const att of msg.attachments) {
      const isImage = att.content_type?.startsWith("image/");
      parts.push(isImage ? `![${att.filename}](${att.url})` : `[${att.filename}](${att.url})`);
    }
  }
  if (msg.embeds?.length) {
    for (const embed of msg.embeds) {
      if (embed.title || embed.description) {
        parts.push(`> ${embed.title || ""}: ${embed.description || ""}`);
      }
    }
  }
  return parts.length > 0 ? parts.join("\n") : "*[empty message]*";
}

function formatConversation(messages) {
  return messages
    .filter((msg) => !msg.author.bot)
    .map((msg) => {
      const author = msg.author.global_name || msg.author.username;
      const timestamp = new Date(msg.timestamp).toLocaleString("en-US", {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        hour12: false,
      });
      return `**${author}** (${timestamp}):\n${formatMessage(msg)}`;
    })
    .join("\n\n---\n\n");
}

// --- API helpers ---

async function discordApi(path, botToken) {
  const res = await fetch(`https://discord.com/api/v10${path}`, {
    headers: { Authorization: `Bot ${botToken}` },
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`Discord API ${path} failed (${res.status}): ${text}`);
  }
  return res.json();
}

async function createGitHubIssue(token, repo, { title, body, labels }) {
  const res = await fetch(`https://api.github.com/repos/${repo}/issues`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
      "User-Agent": "yaat-discord-bot",
      Accept: "application/vnd.github.v3+json",
    },
    body: JSON.stringify({ title, body, labels }),
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`GitHub API failed (${res.status}): ${text}`);
  }
  return res.json();
}

async function createGitHubComment(token, repo, issueNumber, body) {
  const res = await fetch(`https://api.github.com/repos/${repo}/issues/${issueNumber}/comments`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
      "User-Agent": "yaat-discord-bot",
      Accept: "application/vnd.github.v3+json",
    },
    body: JSON.stringify({ body }),
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`GitHub comment failed (${res.status}): ${text}`);
  }
}

async function editOriginalResponse(appId, token, data) {
  const res = await fetch(
    `https://discord.com/api/v10/webhooks/${appId}/${token}/messages/@original`,
    {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(data),
    },
  );
  if (!res.ok) {
    console.error("Failed to edit response:", await res.text());
  }
}

// --- Crypto ---

async function verifyDiscordSignature(publicKey, signature, timestamp, body) {
  const key = await crypto.subtle.importKey(
    "raw",
    hexToUint8Array(publicKey),
    { name: "Ed25519", namedCurve: "Ed25519" },
    false,
    ["verify"],
  );

  const message = new TextEncoder().encode(timestamp + body);
  return crypto.subtle.verify("Ed25519", key, hexToUint8Array(signature), message);
}

async function verifyGitHubSignature(secret, signature, body) {
  if (!signature || !secret) return false;

  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );

  const sig = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(body));
  const expected = "sha256=" + arrayToHex(new Uint8Array(sig));

  return timingSafeEqual(expected, signature);
}

function timingSafeEqual(a, b) {
  if (a.length !== b.length) return false;
  let result = 0;
  for (let i = 0; i < a.length; i++) {
    result |= a.charCodeAt(i) ^ b.charCodeAt(i);
  }
  return result === 0;
}

function hexToUint8Array(hex) {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < hex.length; i += 2) {
    bytes[i / 2] = parseInt(hex.substring(i, i + 2), 16);
  }
  return bytes;
}

function arrayToHex(arr) {
  return Array.from(arr)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

function ephemeral(content) {
  return jsonResponse({ type: 4, data: { content, flags: 64 } });
}

function jsonResponse(data) {
  return new Response(JSON.stringify(data), {
    headers: { "Content-Type": "application/json" },
  });
}
