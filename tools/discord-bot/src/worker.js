import { VALIDATION_CHANNELS } from "./validation-channels.js";

// Discord interaction types
const PING = 1;
const APPLICATION_COMMAND = 2;
const MESSAGE_COMPONENT = 3;

// Response types
const PONG = 1;
const DEFERRED_CHANNEL_MESSAGE = 5;
const UPDATE_MESSAGE = 7;

// Role IDs
const MEMBER_ROLE_ID = "1479929042429018192";

// R2 public URL for uploaded attachments
const R2_PUBLIC_URL = "https://pub-1f460757f70f46d8b557747a4d0ffe0d.r2.dev";

// Forum channel IDs for tracking GitHub issues as Discord threads
const TRACKING_FORUMS = {
  "track-issue": "1479888529222795355",
  "track-feature-request": "1479890009153605724",
};

// Per-repo GitHub App installation tokens ({ token, expiresAt }), valid ~1 hour. Cached at module
// scope so they survive across invocations within an isolate; refreshed before expiry.
const cachedInstallationTokens = new Map();

// Minimum spacing between mutating GitHub requests, enforced inside githubFetch. Bursts of writes
// (a cron sweep posting comments, /reopen stripping labels) are what trip GitHub's secondary rate
// limit, and once tripped it blocks every write the installation makes for about a minute.
export const GITHUB_WRITE_SPACING_MS = 1200;

// Retry budgets for githubFetch. Work started from a fetch handler runs under ctx.waitUntil(),
// which Cloudflare cancels 30s after the response is sent, so those callers can only ride out
// short waits. The cron handler has a 15-minute wall-clock budget and can sit out a full
// content-creation block instead.
export const INTERACTION_RETRY_BUDGET_MS = 20000;
export const CRON_RETRY_BUDGET_MS = 150000;

// GitHub's guidance for a secondary rate limit that carries no timing hint: "wait for at least one
// minute before retrying", then wait an exponentially increasing amount between further retries.
const SECONDARY_LIMIT_BASE_WAIT_MS = 60000;

// Backstop against a pathological retry loop; the caller's budget is the real bound.
const MAX_RATE_LIMIT_RETRIES = 6;

// KV key prefix for a report whose issue creation GitHub rate-limited; the cron drains these.
const PENDING_ISSUE_PREFIX = "pending-issue:";

// A queued report that still can't be created a day later is not going to succeed on its own.
const PENDING_ISSUE_TTL_SECONDS = 86400;

const QUEUED_ISSUE_NOTICE =
  "GitHub is rate-limiting content creation right now, so this report is queued — the issue will be " +
  "created automatically within ~5 minutes. No need to re-run the command.";

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// Status labels → display text, emoji, and whether they represent a terminal (closed) state
const STATUS_LABELS = {
  "in progress": {
    emoji: "🔧",
    message: "This issue is now **in progress** — someone is actively working on it.",
    terminal: false,
  },
  completed: {
    emoji: "✅",
    message: "This issue has been **completed** and the fix/feature should be available soon.",
    terminal: true,
  },
  wontfix: {
    emoji: "🚫",
    message:
      "This issue has been marked as **won't fix** — it's been reviewed but won't be addressed at this time.",
    terminal: true,
  },
  "not a bug": {
    emoji: "❌",
    message:
      "This has been reviewed and determined to be **not a bug** — the current behavior is working as intended.",
    terminal: true,
  },
  duplicate: {
    emoji: "♻️",
    message:
      "This issue has been closed as a **duplicate** — it's already being tracked in another issue.",
    terminal: true,
  },
};

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (request.method !== "POST") {
      return new Response("Method not allowed", { status: 405 });
    }

    // Manual sync endpoint: POST /sync/58 (requires Authorization: Bearer <GITHUB_WEBHOOK_SECRET>)
    const syncMatch = url.pathname.match(/^\/sync\/(\d+)$/);
    if (syncMatch) {
      return handleManualSync(request, env, parseInt(syncMatch[1], 10));
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
    const commandName = interaction.data.name;

    // /validate is channel-scoped (no user ID check) — handle before the admin gate
    if (commandName === "validate") {
      const channelId = interaction.channel?.id || interaction.channel_id;
      const artcc = VALIDATION_CHANNELS[channelId];
      if (!artcc) {
        return ephemeral("This command can only be used in a scenario validation channel.");
      }

      const cooldownKey = `validate-cooldown:${channelId}`;
      const existing = await env.THREAD_ISSUES.get(cooldownKey);
      if (existing) {
        return ephemeral("Validation was triggered recently. Try again in a few minutes.");
      }

      ctx.waitUntil(
        runValidationTrigger({
          artcc,
          channelId,
          env,
          appId: interaction.application_id,
          interactionToken: interaction.token,
        }),
      );

      return ephemeral(`Validation triggered for ${artcc}. Results will appear here shortly.`);
    }

    const userId = interaction.member?.user?.id || interaction.user?.id;
    if (userId !== env.DISCORD_ALLOWED_USER_ID) {
      return ephemeral("You don't have permission to use this command.");
    }

    // track-issue / track-feature-request: connect an existing GitHub issue to a Discord thread.
    // When run inside an existing forum thread, link THAT thread (recovery path when /create-issue
    // failed); otherwise create a new thread in this command's forum (bug reports / feature requests).
    if (TRACKING_FORUMS[commandName]) {
      const issueNumber = interaction.data.options?.find((o) => o.name === "issue_number")?.value;
      if (!issueNumber) {
        return ephemeral("You must provide an issue number.");
      }

      const trackChannel = interaction.channel;
      const inThread = trackChannel && (trackChannel.type === 11 || trackChannel.type === 12);

      ctx.waitUntil(
        (inThread
          ? processLinkThreadCommand({
              threadId: trackChannel.id,
              issueNumber,
              guildId: interaction.guild_id,
              token: interaction.token,
              appId: interaction.application_id,
              env,
            })
          : processTrackCommand({
              commandName,
              issueNumber,
              guildId: interaction.guild_id,
              token: interaction.token,
              appId: interaction.application_id,
              env,
            })
        ).catch((err) => console.error("Track command processing failed:", err)),
      );

      return jsonResponse({ type: DEFERRED_CHANNEL_MESSAGE, data: { flags: 64 } });
    }

    const channel = interaction.channel;
    if (!channel || (channel.type !== 11 && channel.type !== 12)) {
      return ephemeral("This command must be used inside a forum thread.");
    }

    ctx.waitUntil(
      processCommand({
        threadId: channel.id,
        guildId: interaction.guild_id,
        commandName,
        token: interaction.token,
        appId: interaction.application_id,
        env,
      }).catch((err) => console.error("Command processing failed:", err)),
    );

    const silentCommands = ["recreate-issue", "disconnect", "sync"];
    const deferResponse = { type: DEFERRED_CHANNEL_MESSAGE };
    if (silentCommands.includes(commandName)) {
      deferResponse.data = { flags: 64 };
    }
    return jsonResponse(deferResponse);
  }

  if (interaction.type === MESSAGE_COMPONENT) {
    if (interaction.data.custom_id === "run_validation") {
      const channelId = interaction.channel?.id || interaction.channel_id;
      const artcc = VALIDATION_CHANNELS[channelId];
      if (!artcc) {
        return ephemeral("This button only works in a scenario validation channel.");
      }

      const cooldownKey = `validate-cooldown:${channelId}`;
      const existing = await env.THREAD_ISSUES.get(cooldownKey);
      if (existing) {
        return ephemeral("Validation was triggered recently. Try again in a few minutes.");
      }

      ctx.waitUntil(
        runValidationTrigger({
          artcc,
          channelId,
          env,
          appId: interaction.application_id,
          interactionToken: interaction.token,
        }),
      );

      return jsonResponse({
        type: 4,
        data: {
          content: `Validation triggered for ${artcc}. Results will appear here shortly.`,
          flags: 64,
        },
      });
    }

    if (interaction.data.custom_id === "accept_rules") {
      const userId = interaction.member?.user?.id;
      const guildId = interaction.guild_id;
      if (!userId || !guildId) {
        return ephemeral("Something went wrong. Please try again.");
      }

      ctx.waitUntil(
        grantMemberRole(guildId, userId, env).catch((err) =>
          console.error("Failed to grant Member role:", err),
        ),
      );

      return jsonResponse({
        type: 4,
        data: {
          content:
            "You've accepted the rules. Welcome to the server! You should now have access to all channels.",
          flags: 64,
        },
      });
    }

    return ephemeral("Unknown button.");
  }

  return new Response("Unknown interaction type", { status: 400 });
}

// --- Manual sync handler ---

async function handleManualSync(request, env, issueNumber) {
  const auth = request.headers.get("Authorization");
  if (auth !== `Bearer ${env.GITHUB_WEBHOOK_SECRET}`) {
    return new Response("Unauthorized", { status: 401 });
  }

  const threadId = await findThreadForIssue(env, issueNumber);
  if (!threadId) {
    return jsonResponse({ error: `No linked Discord thread for issue #${issueNumber}` }, 404);
  }

  const mapping = await env.THREAD_ISSUES.get(threadId, { type: "json" });
  if (!mapping) {
    return jsonResponse({ error: "Thread mapping found but data is missing" }, 500);
  }

  const githubToken = await getGitHubToken(env, env.GITHUB_REPO, INTERACTION_RETRY_BUDGET_MS);
  const count = await syncThread(env, threadId, mapping, githubToken, INTERACTION_RETRY_BUDGET_MS);
  return jsonResponse({ issue: issueNumber, synced: count });
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
  } else if (event === "issue_comment") {
    ctx.waitUntil(handleIssueCommentEvent(payload, env));
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
    // Skip if the /reopen slash command already handled this (avoids duplicate message)
    const reopenFlag = await env.THREAD_ISSUES.get(`reopen:${issueNumber}`);
    if (reopenFlag) {
      await env.THREAD_ISSUES.delete(`reopen:${issueNumber}`);
      return;
    }
    statusMessage = `🔄 This issue has been **reopened** and will be looked at again.\n\nSee ${issueLink} for details.`;
  }

  if (statusMessage) {
    await postToDiscordThread(env.DISCORD_BOT_TOKEN, threadId, statusMessage);
  }

  // Determine resolution type
  if (action === "labeled" && label) {
    const status = STATUS_LABELS[label.name.toLowerCase()];
    if (status?.terminal) {
      await markThreadResolved(env.DISCORD_BOT_TOKEN, threadId, status.emoji);
    }
  } else if (action === "closed") {
    const emoji = issue.state_reason === "not_planned" ? "🚫" : "✅";
    await prefixThreadTitle(env.DISCORD_BOT_TOKEN, threadId, emoji);
    await env.THREAD_ISSUES.put(`pending-archive:${threadId}`, "1", { expirationTtl: 1800 });
  } else if (action === "reopened") {
    await unmarkThreadResolved(env.DISCORD_BOT_TOKEN, threadId);
  }
}

async function handleIssueCommentEvent(payload, env) {
  const { action, comment, issue } = payload;
  if (action !== "created") return;

  // Skip comments posted by the GitHub App (via Discord→GitHub sync) to prevent echo loops.
  // The app posts as a [bot] user, so check user type. Also keep the text heuristic as a fallback.
  if (comment.user?.type === "Bot" || comment.body?.includes("via Discord:\n\n")) return;

  const threadId = await findThreadForIssue(env, issue.number);
  if (!threadId) return;

  const author = comment.user?.login || "Unknown";
  const shortBody =
    comment.body?.length > 1800 ? comment.body.slice(0, 1800) + "…" : (comment.body || "");
  const issueLink = `[#${issue.number}](${issue.html_url})`;
  const commentLink = `[comment](${comment.html_url})`;

  const message = `💬 **${author}** commented on ${issueLink} (${commentLink}):\n\n${shortBody}`;
  await postToDiscordThread(env.DISCORD_BOT_TOKEN, threadId, message);

  // If the issue was just closed, the archive was deferred so this comment could be posted first.
  // Title was already updated on close; now that the comment is visible, archive the thread.
  const pendingArchive = await env.THREAD_ISSUES.get(`pending-archive:${threadId}`);
  if (pendingArchive) {
    await env.THREAD_ISSUES.delete(`pending-archive:${threadId}`);
    await discordPatch(`/channels/${threadId}`, env.DISCORD_BOT_TOKEN, { archived: true });
  }
}

async function findThreadForIssue(env, issueNumber) {
  // Check reverse mapping first
  const threadId = await env.THREAD_ISSUES.get(`issue:${issueNumber}`);
  if (threadId) return threadId;

  // Fallback: scan all thread mappings (only needed for issues created before reverse mapping)
  for (const name of await listAllKeys(env.THREAD_ISSUES, {})) {
    if (!/^\d+$/.test(name)) continue;
    const mapping = await env.THREAD_ISSUES.get(name, { type: "json" });
    if (mapping && mapping.issueNumber === issueNumber) {
      // Backfill reverse mapping
      await env.THREAD_ISSUES.put(`issue:${issueNumber}`, name);
      return name;
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

// Known resolution emojis used as title prefixes
const RESOLUTION_EMOJIS = ["✅", "🚫", "❌", "♻️"];

// Discord caps thread/channel names at 100 characters.
const DISCORD_THREAD_NAME_MAX = 100;

// Prefix a thread title with its linked GitHub issue number (e.g. "[#123] Title").
// Idempotent, and truncates the base title so the result stays within Discord's limit.
function withIssueNumberPrefix(name, issueNumber) {
  const prefix = `[#${issueNumber}] `;
  if (name.startsWith(prefix)) return name;
  const room = DISCORD_THREAD_NAME_MAX - prefix.length;
  const base = name.length > room ? name.slice(0, room) : name;
  return `${prefix}${base}`;
}

async function prefixThreadTitle(botToken, threadId, emoji) {
  const thread = await discordApi(`/channels/${threadId}`, botToken);

  // Strip any existing resolution emoji prefix before adding the new one
  let name = thread.name;
  for (const e of RESOLUTION_EMOJIS) {
    if (name.startsWith(`${e} `)) {
      name = name.slice(e.length + 1);
      break;
    }
  }
  const newName = `${emoji} ${name}`;
  if (thread.name !== newName) {
    await discordPatch(`/channels/${threadId}`, botToken, { name: newName });
  }

  // Add reaction matching the resolution type
  const encodedEmoji = encodeURIComponent(emoji);
  await fetch(
    `https://discord.com/api/v10/channels/${threadId}/messages/${threadId}/reactions/${encodedEmoji}/@me`,
    { method: "PUT", headers: { Authorization: `Bot ${botToken}` } },
  );
}

async function markThreadResolved(botToken, threadId, emoji = "✅") {
  await prefixThreadTitle(botToken, threadId, emoji);
  await discordPatch(`/channels/${threadId}`, botToken, { archived: true });
}

async function unmarkThreadResolved(botToken, threadId) {
  // Unarchive first (Discord requires unarchive before other modifications)
  const thread = await discordApi(`/channels/${threadId}`, botToken);

  // Strip any resolution emoji prefix
  let name = thread.name;
  let changed = false;
  for (const e of RESOLUTION_EMOJIS) {
    if (name.startsWith(`${e} `)) {
      name = name.slice(e.length + 1);
      changed = true;
      break;
    }
  }

  const patch = { archived: false };
  if (changed) patch.name = name;
  await discordPatch(`/channels/${threadId}`, botToken, patch);

  // Remove all resolution emoji reactions
  for (const e of RESOLUTION_EMOJIS) {
    const encoded = encodeURIComponent(e);
    await fetch(
      `https://discord.com/api/v10/channels/${threadId}/messages/${threadId}/reactions/${encoded}/@me`,
      { method: "DELETE", headers: { Authorization: `Bot ${botToken}` } },
    );
  }
}

// --- Command processing ---

export async function processCommand({ threadId, guildId, commandName, token, appId, env }) {
  try {
    if (commandName === "resolve") {
      await markThreadResolved(env.DISCORD_BOT_TOKEN, threadId);
      await editOriginalResponse(appId, token, { content: "Thread marked as resolved." });
      return;
    }

    if (commandName === "unresolve") {
      await unmarkThreadResolved(env.DISCORD_BOT_TOKEN, threadId);
      await editOriginalResponse(appId, token, { content: "Thread unmarked as resolved." });
      return;
    }

    const githubToken = await getGitHubToken(env, env.GITHUB_REPO, INTERACTION_RETRY_BUDGET_MS);

    if (commandName === "reopen") {
      const mapping = await env.THREAD_ISSUES.get(threadId, { type: "json" });
      if (!mapping) {
        await editOriginalResponse(appId, token, {
          content: "No linked GitHub issue found. Use `/create-issue` or `/create-feature-request` first.",
        });
        return;
      }

      // Mark that we're reopening this issue so the GitHub webhook doesn't duplicate the message
      await env.THREAD_ISSUES.put(`reopen:${mapping.issueNumber}`, "1", { expirationTtl: 60 });

      // Reopen the GitHub issue
      await updateGitHubIssue(
        githubToken,
        env.GITHUB_REPO,
        mapping.issueNumber,
        { state: "open" },
        INTERACTION_RETRY_BUDGET_MS,
      );

      // Remove terminal labels so the issue appears fresh
      const terminalLabels = Object.entries(STATUS_LABELS)
        .filter(([, v]) => v.terminal)
        .map(([k]) => k);
      for (const label of terminalLabels) {
        await removeGitHubLabel(
          githubToken,
          env.GITHUB_REPO,
          mapping.issueNumber,
          label,
          INTERACTION_RETRY_BUDGET_MS,
        );
      }

      // Unmark the thread as resolved on Discord
      await unmarkThreadResolved(env.DISCORD_BOT_TOKEN, threadId);

      // Sync any new thread messages to the reopened issue
      const count = await syncThread(env, threadId, mapping, githubToken, INTERACTION_RETRY_BUDGET_MS);
      const syncNote = count > 0 ? ` (synced ${count} new message(s))` : "";

      await editOriginalResponse(appId, token, {
        content: `Reopened GitHub issue: ${mapping.issueUrl}${syncNote}`,
      });
      return;
    }

    if (commandName === "sync") {
      const mapping = await env.THREAD_ISSUES.get(threadId, { type: "json" });
      if (!mapping) {
        await editOriginalResponse(appId, token, {
          content: "No linked GitHub issue found. Use `/create-issue` or `/create-feature-request` first.",
        });
        return;
      }

      const count = await syncThread(env, threadId, mapping, githubToken, INTERACTION_RETRY_BUDGET_MS);
      await editOriginalResponse(appId, token, {
        content:
          count > 0
            ? `Synced ${count} new message(s) to ${mapping.issueUrl}`
            : `Already up to date: ${mapping.issueUrl}`,
      });
      return;
    }

    if (commandName === "disconnect") {
      const mapping = await env.THREAD_ISSUES.get(threadId, { type: "json" });
      if (!mapping) {
        await editOriginalResponse(appId, token, {
          content: "No linked GitHub issue found — nothing to disconnect.",
        });
        return;
      }

      await env.THREAD_ISSUES.delete(threadId);
      await env.THREAD_ISSUES.delete(`issue:${mapping.issueNumber}`);

      await editOriginalResponse(appId, token, {
        content: `Disconnected from GitHub issue #${mapping.issueNumber}. New comments will no longer sync.`,
      });
      return;
    }

    const existing = await env.THREAD_ISSUES.get(threadId, { type: "json" });

    if (commandName === "recreate-issue") {
      if (!existing) {
        await editOriginalResponse(appId, token, {
          content: "No linked GitHub issue found. Use `/create-issue` or `/create-feature-request` first.",
        });
        return;
      }

      const threadUrl = `https://discord.com/channels/${guildId}/${threadId}`;
      const messages = await discordApi(
        `/channels/${threadId}/messages?limit=100`,
        env.DISCORD_BOT_TOKEN,
      );
      messages.reverse();

      const urlMap = await reuploadAttachments(messages, env.ATTACHMENTS);
      const conversation = formatConversation(messages, urlMap);
      const body = `> Created from [Discord thread](${threadUrl})\n\n## Conversation\n\n${conversation}`;

      await updateGitHubIssue(
        githubToken,
        env.GITHUB_REPO,
        existing.issueNumber,
        { body },
        INTERACTION_RETRY_BUDGET_MS,
      );

      const lastMessageId = messages.length > 0 ? messages[messages.length - 1].id : "0";
      existing.lastSyncedMessageId = lastMessageId;
      await env.THREAD_ISSUES.put(threadId, JSON.stringify(existing));

      await editOriginalResponse(appId, token, {
        content: `Recreated issue body with permanent attachments: ${existing.issueUrl}`,
      });
      return;
    }

    if (existing) {
      const count = await syncThread(env, threadId, existing, githubToken, INTERACTION_RETRY_BUDGET_MS);
      await editOriginalResponse(appId, token, {
        content:
          count > 0
            ? `Synced ${count} new message(s) to ${existing.issueUrl}`
            : `Already up to date: ${existing.issueUrl}`,
      });
      return;
    }

    // A previous run may have queued this report because GitHub was blocking content creation;
    // creating it again here would end up with two issues for one thread.
    const pendingKey = `${PENDING_ISSUE_PREFIX}${threadId}`;
    if (await env.THREAD_ISSUES.get(pendingKey)) {
      await editOriginalResponse(appId, token, {
        content: `This thread already has an issue queued. ${QUEUED_ISSUE_NOTICE}`,
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

    const urlMap = await reuploadAttachments(messages, env.ATTACHMENTS);
    const conversation = formatConversation(messages, urlMap);
    const body = `> Created from [Discord thread](${threadUrl})\n\n## Conversation\n\n${conversation}`;
    const queued = {
      guildId,
      threadName: thread.name,
      body,
      labels,
      lastSyncedMessageId: messages.length > 0 ? messages[messages.length - 1].id : "0",
    };

    let issue;
    try {
      issue = await createGitHubIssue(
        githubToken,
        env.GITHUB_REPO,
        { title: queued.threadName, body: queued.body, labels: queued.labels },
        INTERACTION_RETRY_BUDGET_MS,
      );
    } catch (err) {
      if (!(err instanceof GitHubApiError) || !err.isRateLimit) throw err;

      // A content-creation block lasts about a minute — longer than the 30s ctx.waitUntil() budget
      // this command runs under. Hand the prepared issue to the cron, which can wait it out.
      console.warn(`Queueing issue creation for thread ${threadId}: ${err.message}`);
      await env.THREAD_ISSUES.put(pendingKey, JSON.stringify(queued), {
        expirationTtl: PENDING_ISSUE_TTL_SECONDS,
      });
      await editOriginalResponse(appId, token, { content: QUEUED_ISSUE_NOTICE });
      return;
    }

    await linkIssueToThread(env, threadId, issue, queued);

    await editOriginalResponse(appId, token, {
      content: `Created GitHub issue: ${issue.html_url}`,
    });
  } catch (err) {
    console.error("Error processing command:", err);
    await editOriginalResponse(appId, token, {
      content: `/${commandName} failed: ${err.message}`,
    });
  }
}

/**
 * Records a newly created issue against its Discord thread. KV mappings are written first — and the
 * pending-issue record cleared with them — so a failure in the cosmetic title patch can never leave
 * the cron thinking the issue still needs creating.
 */
async function linkIssueToThread(env, threadId, issue, { guildId, threadName, lastSyncedMessageId }) {
  const mapping = {
    issueNumber: issue.number,
    issueUrl: issue.html_url,
    guildId,
    lastSyncedMessageId,
  };
  await env.THREAD_ISSUES.put(threadId, JSON.stringify(mapping));
  await env.THREAD_ISSUES.put(`issue:${issue.number}`, threadId);
  await env.THREAD_ISSUES.delete(`${PENDING_ISSUE_PREFIX}${threadId}`);

  // Prefix the Discord thread title with the new issue number for quick reference
  await discordPatch(`/channels/${threadId}`, env.DISCORD_BOT_TOKEN, {
    name: withIssueNumberPrefix(threadName, issue.number),
  });
}

// --- Track command: create Discord thread from existing GitHub issue ---

async function processTrackCommand({ commandName, issueNumber, guildId, token, appId, env }) {
  try {
    // Check if this issue is already linked to a thread
    const existingThreadId = await findThreadForIssue(env, issueNumber);
    if (existingThreadId) {
      const threadUrl = `https://discord.com/channels/${guildId}/${existingThreadId}`;
      await editOriginalResponse(appId, token, {
        content: `Issue #${issueNumber} is already linked to a thread: ${threadUrl}`,
      });
      return;
    }

    const githubToken = await getGitHubToken(env, env.GITHUB_REPO, INTERACTION_RETRY_BUDGET_MS);
    const issue = await fetchGitHubIssue(
      githubToken,
      env.GITHUB_REPO,
      issueNumber,
      INTERACTION_RETRY_BUDGET_MS,
    );

    const forumChannelId = TRACKING_FORUMS[commandName];

    // Resolve GitHub labels → forum tags
    const forumChannel = await discordApi(`/channels/${forumChannelId}`, env.DISCORD_BOT_TOKEN);
    const appliedTags = [];
    if (forumChannel.available_tags?.length) {
      if (issue.labels?.length) {
        const tagMap = new Map(forumChannel.available_tags.map((t) => [t.name.toLowerCase(), t.id]));
        for (const label of issue.labels) {
          const labelName = (typeof label === "string" ? label : label.name).toLowerCase();
          const tagId = tagMap.get(labelName);
          if (tagId) appliedTags.push(tagId);
        }
      }
      // Forum requires at least one tag — fall back to the first available tag
      if (appliedTags.length === 0) {
        appliedTags.push(forumChannel.available_tags[0].id);
      }
    }

    // Truncate issue body for the first message (Discord limit: 2000 chars)
    const issueLink = `[#${issueNumber}](${issue.html_url})`;
    let firstMessageContent = `> Tracking GitHub issue ${issueLink}\n\n${issue.body || "*No description provided.*"}`;
    if (firstMessageContent.length > 2000) {
      firstMessageContent = firstMessageContent.slice(0, 1997) + "…";
    }

    // Create forum thread (title prefixed with the tracked issue number for quick reference)
    const threadPayload = {
      name: withIssueNumberPrefix(issue.title, issue.number),
      message: { content: firstMessageContent },
      applied_tags: appliedTags,
    };

    const thread = await discordPost(
      `/channels/${forumChannelId}/threads`,
      env.DISCORD_BOT_TOKEN,
      threadPayload,
    );

    // Store both KV mappings
    const mapping = {
      issueNumber: issue.number,
      issueUrl: issue.html_url,
      guildId,
      lastSyncedMessageId: thread.id,
    };
    await env.THREAD_ISSUES.put(thread.id, JSON.stringify(mapping));
    await env.THREAD_ISSUES.put(`issue:${issue.number}`, thread.id);

    // Post existing GitHub comments into the thread
    const comments = await fetchGitHubComments(
      githubToken,
      env.GITHUB_REPO,
      issueNumber,
      INTERACTION_RETRY_BUDGET_MS,
    );
    for (const comment of comments) {
      const author = comment.user?.login || "Unknown";
      const commentLink = `[comment](${comment.html_url})`;
      const shortBody =
        comment.body?.length > 1800 ? comment.body.slice(0, 1800) + "…" : (comment.body || "");
      const msg = `💬 **${author}** commented on ${issueLink} (${commentLink}):\n\n${shortBody}`;
      await postToDiscordThread(env.DISCORD_BOT_TOKEN, thread.id, msg);
    }

    // If the issue is already closed, mark the thread accordingly
    if (issue.state === "closed") {
      const emoji = issue.state_reason === "not_planned" ? "🚫" : "✅";
      await markThreadResolved(env.DISCORD_BOT_TOKEN, thread.id, emoji);
    }

    const threadUrl = `https://discord.com/channels/${guildId}/${thread.id}`;
    const commentNote = comments.length > 0 ? ` (synced ${comments.length} comment(s))` : "";
    await editOriginalResponse(appId, token, {
      content: `Created thread for issue #${issueNumber}: ${threadUrl}${commentNote}`,
    });
  } catch (err) {
    console.error("Error processing track command:", err);
    await editOriginalResponse(appId, token, {
      content: `Failed to track issue: ${err.message}`,
    });
  }
}

// --- Link an existing thread to an existing GitHub issue ---

// Recovery path for when /create-issue failed (e.g. a rate limit) but the GitHub issue already
// exists: connect the CURRENT forum thread to it, reaching the same end state as /create-issue —
// KV mappings both ways, the thread title prefixed with the issue number, and the sync cron picking
// up future replies from now on. Existing thread content is not re-pushed (the issue already carries
// it); use /sync or /recreate-issue if the issue body needs the discussion.
async function processLinkThreadCommand({ threadId, issueNumber, guildId, token, appId, env }) {
  try {
    const existing = await env.THREAD_ISSUES.get(threadId, { type: "json" });
    if (existing) {
      await editOriginalResponse(appId, token, {
        content: `This thread is already linked to issue #${existing.issueNumber}: ${existing.issueUrl}`,
      });
      return;
    }

    const otherThreadId = await findThreadForIssue(env, issueNumber);
    if (otherThreadId && otherThreadId !== threadId) {
      const otherUrl = `https://discord.com/channels/${guildId}/${otherThreadId}`;
      await editOriginalResponse(appId, token, {
        content: `Issue #${issueNumber} is already linked to another thread: ${otherUrl}`,
      });
      return;
    }

    const githubToken = await getGitHubToken(env, env.GITHUB_REPO, INTERACTION_RETRY_BUDGET_MS);
    const issue = await fetchGitHubIssue(
      githubToken,
      env.GITHUB_REPO,
      issueNumber,
      INTERACTION_RETRY_BUDGET_MS,
    );

    // Prefix the thread title with the issue number for quick reference.
    const thread = await discordApi(`/channels/${threadId}`, env.DISCORD_BOT_TOKEN);
    await discordPatch(`/channels/${threadId}`, env.DISCORD_BOT_TOKEN, {
      name: withIssueNumberPrefix(thread.name, issue.number),
    });

    // Set the sync cursor to the newest current message so the cron only forwards replies posted
    // after linking (Discord returns messages newest-first, so index 0 is the latest).
    const messages = await discordApi(
      `/channels/${threadId}/messages?limit=100`,
      env.DISCORD_BOT_TOKEN,
    );
    const lastMessageId = messages.length > 0 ? messages[0].id : threadId;

    const mapping = {
      issueNumber: issue.number,
      issueUrl: issue.html_url,
      guildId,
      lastSyncedMessageId: lastMessageId,
    };
    await env.THREAD_ISSUES.put(threadId, JSON.stringify(mapping));
    await env.THREAD_ISSUES.put(`issue:${issue.number}`, threadId);

    // Linking by hand is the recovery path for a rate-limited /create-issue, so drop any queued
    // creation for this thread — draining it later would file a duplicate issue.
    await env.THREAD_ISSUES.delete(`${PENDING_ISSUE_PREFIX}${threadId}`);

    // Reflect a closed issue on the thread.
    if (issue.state === "closed") {
      const emoji = issue.state_reason === "not_planned" ? "🚫" : "✅";
      await markThreadResolved(env.DISCORD_BOT_TOKEN, threadId, emoji);
    }

    await editOriginalResponse(appId, token, {
      content: `Linked this thread to GitHub issue #${issueNumber}: ${issue.html_url}`,
    });
  } catch (err) {
    console.error("Error linking thread to issue:", err);
    await editOriginalResponse(appId, token, {
      content: `Failed to link thread to issue: ${err.message}`,
    });
  }
}

// --- Sync logic ---

/** Lists every matching KV key, following the cursor past the 1000-key page limit. */
async function listAllKeys(kv, options) {
  const names = [];
  let cursor;
  while (true) {
    const page = await kv.list(cursor ? { ...options, cursor } : options);
    for (const key of page.keys) {
      names.push(key.name);
    }
    if (page.list_complete || !page.cursor) {
      return names;
    }
    cursor = page.cursor;
  }
}

export async function syncAllThreads(env) {
  // Sweep deferred archives first — these are time-sensitive and cheap (1 Discord PATCH + 1 KV
  // delete each), and must not wait behind thread syncs for the subrequest budget. Both sweeps list
  // their own prefix rather than filtering one big listing: these prefixes sort after the numeric
  // thread IDs, so they are the first thing a truncated listing would drop.
  const pendingArchives = await listAllKeys(env.THREAD_ISSUES, { prefix: "pending-archive:" });
  for (const name of pendingArchives) {
    const threadId = name.slice("pending-archive:".length);
    try {
      await discordPatch(`/channels/${threadId}`, env.DISCORD_BOT_TOKEN, { archived: true });
      await env.THREAD_ISSUES.delete(name);
    } catch (err) {
      console.error(`Failed to sweep pending archive for ${threadId}:`, err);
    }
  }

  // Then file any reports GitHub rate-limited, before the thread syncs spend the subrequest budget.
  await drainPendingIssues(env);

  // Sync Discord thread replies → GitHub issue comments.
  let synced = 0;
  let threadsSynced = 0;
  let githubToken = null;

  for (const name of await listAllKeys(env.THREAD_ISSUES, {})) {
    // Thread mappings are keyed by the raw Discord snowflake; everything else in this namespace is
    // bookkeeping under a prefix (issue:, pending-archive:, pending-issue:, reopen:, validate-cooldown:).
    if (!/^\d+$/.test(name)) continue;

    try {
      if (!githubToken) githubToken = await getGitHubToken(env, env.GITHUB_REPO, CRON_RETRY_BUDGET_MS);
      const mapping = await env.THREAD_ISSUES.get(name, { type: "json" });
      if (!mapping) continue;
      const count = await syncThread(env, name, mapping, githubToken, CRON_RETRY_BUDGET_MS);
      if (count > 0) {
        synced += count;
        threadsSynced++;
      }
    } catch (err) {
      console.error(`Failed to sync thread ${name}:`, err);
    }
  }

  if (synced > 0) {
    console.log(`Cron sync: posted ${synced} comment(s) across ${threadsSynced} thread(s)`);
  }
}

/**
 * Files the issues /create-issue had to queue because GitHub was blocking content creation. This
 * runs in the cron handler, whose 15-minute wall-clock budget can ride out a full block — unlike
 * the 30s ctx.waitUntil() budget the slash command itself runs under.
 */
async function drainPendingIssues(env) {
  const queuedKeys = await listAllKeys(env.THREAD_ISSUES, { prefix: PENDING_ISSUE_PREFIX });
  if (queuedKeys.length === 0) return;

  const githubToken = await getGitHubToken(env, env.GITHUB_REPO, CRON_RETRY_BUDGET_MS);
  for (const name of queuedKeys) {
    const threadId = name.slice(PENDING_ISSUE_PREFIX.length);
    try {
      const queued = await env.THREAD_ISSUES.get(name, { type: "json" });
      if (!queued) continue;

      // The thread may have been linked by hand since (the /track-issue recovery path).
      if (await env.THREAD_ISSUES.get(threadId)) {
        await env.THREAD_ISSUES.delete(name);
        continue;
      }

      const issue = await createGitHubIssue(
        githubToken,
        env.GITHUB_REPO,
        { title: queued.threadName, body: queued.body, labels: queued.labels },
        CRON_RETRY_BUDGET_MS,
      );
      await linkIssueToThread(env, threadId, issue, queued);
      await postToDiscordThread(
        env.DISCORD_BOT_TOKEN,
        threadId,
        `Created GitHub issue: ${issue.html_url}`,
      );
      console.log(`Filed queued issue #${issue.number} for thread ${threadId}`);
    } catch (err) {
      // Leave the record in place — the next cron run retries it until its TTL expires.
      console.error(`Failed to file queued issue for thread ${threadId}:`, err);
    }
  }
}

async function syncThread(env, threadId, mapping, githubToken, budgetMs) {
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

  // All new messages may be the bot's own (e.g. the status message posted when the issue
  // closed). GitHub rejects a blank comment body with a 422, so skip the comment entirely.
  if (filteredMessages.length > 0) {
    const urlMap = await reuploadAttachments(filteredMessages, env.ATTACHMENTS);

    const digest = formatConversation(filteredMessages, urlMap);
    await createGitHubComment(githubToken, env.GITHUB_REPO, mapping.issueNumber, digest, budgetMs);
  }

  // Always update cursor to latest message (including bot messages)
  mapping.lastSyncedMessageId = messages[messages.length - 1].id;
  await env.THREAD_ISSUES.put(threadId, JSON.stringify(mapping));

  return filteredMessages.length;
}

// --- Attachment re-upload ---

async function reuploadAttachments(messages, r2Bucket) {
  const urlMap = new Map();
  for (const msg of messages) {
    for (const att of msg.attachments || []) {
      try {
        const res = await fetch(att.url);
        if (!res.ok) continue;
        const data = await res.arrayBuffer();
        const key = `${msg.id}-${att.filename}`;
        await r2Bucket.put(key, data, {
          httpMetadata: { contentType: att.content_type || "application/octet-stream" },
        });
        urlMap.set(att.url, `${R2_PUBLIC_URL}/${key}`);
      } catch (err) {
        console.error(`Failed to reupload ${att.filename}:`, err);
      }
    }
  }
  return urlMap;
}

// --- Formatting ---

function formatMessage(msg, urlMap = new Map()) {
  const parts = [];
  if (msg.content) parts.push(msg.content);
  if (msg.attachments?.length) {
    for (const att of msg.attachments) {
      const url = urlMap.get(att.url) || att.url;
      const isImage = att.content_type?.startsWith("image/");
      parts.push(isImage ? `![${att.filename}](${url})` : `[${att.filename}](${url})`);
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

function formatConversation(messages, urlMap = new Map()) {
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
      return `**${author}** (${timestamp}):\n${formatMessage(msg, urlMap)}`;
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

async function discordPost(path, botToken, body) {
  const res = await fetch(`https://discord.com/api/v10${path}`, {
    method: "POST",
    headers: {
      Authorization: `Bot ${botToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`Discord POST ${path} failed (${res.status}): ${text}`);
  }
  return res.json();
}

async function discordPatch(path, botToken, body) {
  const res = await fetch(`https://discord.com/api/v10${path}`, {
    method: "PATCH",
    headers: {
      Authorization: `Bot ${botToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const text = await res.text();
    console.error(`Discord PATCH ${path} failed (${res.status}): ${text}`);
  }
}

/** A failed GitHub REST call, carrying enough detail for callers to react to a rate limit. */
export class GitHubApiError extends Error {
  constructor(message, status, isRateLimit) {
    super(message);
    this.name = "GitHubApiError";
    this.status = status;
    this.isRateLimit = isRateLimit;
  }
}

/** Builds a GitHubApiError from a failed response, consuming its body. */
async function githubApiError(summary, res) {
  const text = await res.text();
  return new GitHubApiError(`${summary} (${res.status}): ${text}`, res.status, isRateLimited(res, text));
}

/** True when a failed response is a primary or secondary rate limit rather than a real error. */
function isRateLimited(res, bodyText) {
  if (res.status === 429) return true;
  if (res.status !== 403) return false;
  if (res.headers.get("retry-after")) return true;
  if (res.headers.get("x-ratelimit-remaining") === "0") return true;
  return /rate limit|abuse detection/i.test(bodyText);
}

// Earliest time the next mutating GitHub request may be sent. Module scope, so it paces every write
// an isolate makes regardless of which handler issues it.
let nextWriteAllowedAt = 0;

/** Holds a mutating request until its pacing slot comes up, reserving the slot before awaiting. */
async function awaitWriteSlot() {
  const now = Date.now();
  const slot = Math.max(now, nextWriteAllowedAt);
  nextWriteAllowedAt = slot + GITHUB_WRITE_SPACING_MS;
  if (slot > now) {
    await sleep(slot - now);
  }
}

/** How long to wait before retrying a 403/429, or null when the response is not a rate limit. */
async function rateLimitRetryDelayMs(res, attempt) {
  const retryAfter = Number(res.headers.get("retry-after"));
  if (Number.isFinite(retryAfter) && retryAfter > 0) {
    return retryAfter * 1000;
  }

  const reset = Number(res.headers.get("x-ratelimit-reset"));
  if (res.headers.get("x-ratelimit-remaining") === "0" && Number.isFinite(reset) && reset > 0) {
    return Math.max(reset * 1000 - Date.now(), 1000);
  }

  const bodyText = await res.clone().text();
  if (!isRateLimited(res, bodyText)) {
    return null;
  }
  return SECONDARY_LIMIT_BASE_WAIT_MS * 2 ** attempt;
}

/**
 * fetch() wrapper for the GitHub REST API. Paces mutating requests so bursts don't trip GitHub's
 * secondary rate limit, and retries a 403/429 that is one — honoring Retry-After and
 * x-ratelimit-reset, else GitHub's documented "wait at least a minute" guidance. Retries stop once
 * the next wait would run past budgetMs (see INTERACTION_RETRY_BUDGET_MS / CRON_RETRY_BUDGET_MS),
 * and a 403 that is NOT a rate limit (e.g. a permissions error) is returned unchanged so the caller
 * surfaces the real error.
 */
export async function githubFetch(url, options, budgetMs) {
  const deadline = Date.now() + budgetMs;
  const isWrite = (options.method || "GET") !== "GET";

  for (let attempt = 0; ; attempt++) {
    if (isWrite) {
      await awaitWriteSlot();
    }

    const res = await fetch(url, options);
    if (res.status !== 403 && res.status !== 429) {
      return res;
    }
    if (attempt >= MAX_RATE_LIMIT_RETRIES) {
      return res;
    }

    const waitMs = await rateLimitRetryDelayMs(res, attempt);
    if (waitMs === null) {
      return res; // not a rate limit — a real error the caller must see
    }
    if (Date.now() + waitMs > deadline) {
      return res; // longer than this handler can wait; the caller decides what to do
    }

    console.warn(`GitHub rate limit (${res.status}) on ${url}; retrying in ${waitMs}ms`);
    await sleep(waitMs);
  }
}

async function fetchGitHubIssue(token, repo, issueNumber, budgetMs) {
  const res = await githubFetch(
    `https://api.github.com/repos/${repo}/issues/${issueNumber}`,
    {
      headers: {
        Authorization: `Bearer ${token}`,
        "User-Agent": "yaat-discord-bot",
        Accept: "application/vnd.github.v3+json",
      },
    },
    budgetMs,
  );
  if (!res.ok) {
    throw await githubApiError(`GitHub issue #${issueNumber} not found`, res);
  }
  return res.json();
}

async function fetchGitHubComments(token, repo, issueNumber, budgetMs) {
  const comments = [];
  let page = 1;
  while (true) {
    const res = await githubFetch(
      `https://api.github.com/repos/${repo}/issues/${issueNumber}/comments?per_page=100&page=${page}`,
      {
        headers: {
          Authorization: `Bearer ${token}`,
          "User-Agent": "yaat-discord-bot",
          Accept: "application/vnd.github.v3+json",
        },
      },
      budgetMs,
    );
    if (!res.ok) break;
    const batch = await res.json();
    if (batch.length === 0) break;
    comments.push(...batch);
    if (batch.length < 100) break;
    page++;
  }
  return comments;
}

export async function createGitHubIssue(token, repo, { title, body, labels }, budgetMs) {
  const res = await githubFetch(
    `https://api.github.com/repos/${repo}/issues`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
        "User-Agent": "yaat-discord-bot",
        Accept: "application/vnd.github.v3+json",
      },
      body: JSON.stringify({ title, body, labels }),
    },
    budgetMs,
  );
  if (!res.ok) {
    throw await githubApiError("GitHub API failed", res);
  }
  return res.json();
}

async function updateGitHubIssue(token, repo, issueNumber, fields, budgetMs) {
  const res = await githubFetch(
    `https://api.github.com/repos/${repo}/issues/${issueNumber}`,
    {
      method: "PATCH",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
        "User-Agent": "yaat-discord-bot",
        Accept: "application/vnd.github.v3+json",
      },
      body: JSON.stringify(fields),
    },
    budgetMs,
  );
  if (!res.ok) {
    throw await githubApiError("GitHub update issue failed", res);
  }
  return res.json();
}

async function createGitHubComment(token, repo, issueNumber, body, budgetMs) {
  const res = await githubFetch(
    `https://api.github.com/repos/${repo}/issues/${issueNumber}/comments`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
        "User-Agent": "yaat-discord-bot",
        Accept: "application/vnd.github.v3+json",
      },
      body: JSON.stringify({ body }),
    },
    budgetMs,
  );
  if (!res.ok) {
    throw await githubApiError("GitHub comment failed", res);
  }
}

async function removeGitHubLabel(token, repo, issueNumber, label, budgetMs) {
  const encoded = encodeURIComponent(label);
  const res = await githubFetch(
    `https://api.github.com/repos/${repo}/issues/${issueNumber}/labels/${encoded}`,
    {
      method: "DELETE",
      headers: {
        Authorization: `Bearer ${token}`,
        "User-Agent": "yaat-discord-bot",
        Accept: "application/vnd.github.v3+json",
      },
    },
    budgetMs,
  );
  // 404 = label wasn't on the issue, that's fine
  if (!res.ok && res.status !== 404) {
    console.error(`Failed to remove label "${label}" from issue ${issueNumber}:`, await res.text());
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

async function grantMemberRole(guildId, userId, env) {
  const res = await fetch(
    `https://discord.com/api/v10/guilds/${guildId}/members/${userId}/roles/${MEMBER_ROLE_ID}`,
    {
      method: "PUT",
      headers: { Authorization: `Bot ${env.DISCORD_BOT_TOKEN}` },
    },
  );
  if (!res.ok) {
    console.error("Failed to grant Member role:", await res.text());
  }
}

// --- Validation workflow trigger ---

async function runValidationTrigger({ artcc, channelId, env, appId, interactionToken }) {
  const cooldownKey = `validate-cooldown:${channelId}`;
  try {
    await triggerValidationWorkflow(artcc, env);
    await env.THREAD_ISSUES.put(cooldownKey, "1", { expirationTtl: 300 });
  } catch (err) {
    console.error("Failed to trigger validation workflow:", err);
    const detail = err instanceof Error ? err.message : String(err);
    await editOriginalResponse(appId, interactionToken, {
      content: `Failed to start validation for ${artcc}: ${detail}`,
      flags: 64,
    });
  }
}

async function triggerValidationWorkflow(artcc, env) {
  const repo = env.VALIDATION_REPO || env.GITHUB_REPO;
  const token = await getGitHubToken(env, repo, INTERACTION_RETRY_BUDGET_MS);
  const res = await githubFetch(
    `https://api.github.com/repos/${repo}/actions/workflows/discord-scenario-validation.yml/dispatches`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
        "User-Agent": "yaat-discord-bot",
        Accept: "application/vnd.github.v3+json",
      },
      body: JSON.stringify({ ref: "main", inputs: { artcc } }),
    },
    INTERACTION_RETRY_BUDGET_MS,
  );
  if (!res.ok) {
    throw await githubApiError("GitHub workflow dispatch failed", res);
  }
}

// --- GitHub App authentication ---

async function createAppJwt(env) {
  const now = Math.floor(Date.now() / 1000);
  const payload = { iat: now - 60, exp: now + 600, iss: env.GITHUB_APP_ID };
  return createJWT(payload, env.GITHUB_APP_PRIVATE_KEY);
}

async function resolveInstallationId(repo, env, jwt, budgetMs) {
  const defaultRepo = env.GITHUB_REPO;
  if (repo === defaultRepo && env.GITHUB_APP_INSTALLATION_ID) {
    return env.GITHUB_APP_INSTALLATION_ID;
  }

  const validationRepo = env.VALIDATION_REPO || "";
  if (repo === validationRepo && env.VALIDATION_APP_INSTALLATION_ID) {
    return env.VALIDATION_APP_INSTALLATION_ID;
  }

  const res = await githubFetch(
    `https://api.github.com/repos/${repo}/installation`,
    {
      headers: {
        Authorization: `Bearer ${jwt}`,
        Accept: "application/vnd.github.v3+json",
        "User-Agent": "yaat-discord-bot",
      },
    },
    budgetMs,
  );
  if (!res.ok) {
    const text = await res.text();
    throw new Error(
      `GitHub App is not installed on ${repo} (${res.status}). ` +
        `Install the app on that repository with Actions: Read and write. ${text}`,
    );
  }

  const data = await res.json();
  return String(data.id);
}

async function getGitHubToken(env, repo, budgetMs) {
  const cached = cachedInstallationTokens.get(repo);
  // Reuse a cached token only while it's comfortably valid (60s safety margin). Installation
  // tokens expire after ~1 hour, and a stale one would 401 every GitHub call.
  if (cached && cached.expiresAt > Date.now() + 60000) {
    return cached.token;
  }

  const jwt = await createAppJwt(env);
  const installationId = await resolveInstallationId(repo, env, jwt, budgetMs);

  // Through githubFetch like every other GitHub call: a rate-limited auth endpoint would otherwise
  // fail the command outright, since nothing works without this token.
  const res = await githubFetch(
    `https://api.github.com/app/installations/${installationId}/access_tokens`,
    {
      method: "POST",
      headers: {
        Authorization: `Bearer ${jwt}`,
        Accept: "application/vnd.github.v3+json",
        "User-Agent": "yaat-discord-bot",
      },
    },
    budgetMs,
  );
  if (!res.ok) {
    throw await githubApiError("Failed to get GitHub App installation token", res);
  }

  const data = await res.json();
  // Installation tokens are valid ~1 hour; cache with the returned expiry so getGitHubToken
  // refreshes before it lapses instead of reusing an expired token for the isolate's lifetime.
  const expiresAt = data.expires_at ? Date.parse(data.expires_at) : Date.now() + 3000000;
  cachedInstallationTokens.set(repo, { token: data.token, expiresAt });
  return data.token;
}

async function createJWT(payload, pemKey) {
  // Handle PEM keys stored with literal \n (Cloudflare secrets) and strip all headers
  const pem = pemKey.replace(/\\n/g, "\n");
  const isPkcs8 = pem.includes("BEGIN PRIVATE KEY") && !pem.includes("BEGIN RSA PRIVATE KEY");
  const pemBody = pem.replace(/-----[A-Z ]+-----/g, "").replace(/\s/g, "");
  const derBytes = Uint8Array.from(atob(pemBody), (c) => c.charCodeAt(0));
  const pkcs8Der = isPkcs8 ? derBytes.buffer : wrapPkcs1InPkcs8(derBytes);

  const key = await crypto.subtle.importKey("pkcs8", pkcs8Der, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);

  const header = base64url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
  const body = base64url(JSON.stringify(payload));
  const signingInput = `${header}.${body}`;

  const signature = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, new TextEncoder().encode(signingInput));
  return `${signingInput}.${base64url(signature)}`;
}

function wrapPkcs1InPkcs8(pkcs1Der) {
  const keyLen = pkcs1Der.byteLength;
  const totalLen = keyLen + 22; // 3 (version) + 15 (AlgorithmIdentifier) + 4 (OCTET STRING header)
  // prettier-ignore
  const header = new Uint8Array([
    0x30, 0x82, (totalLen >> 8) & 0xff, totalLen & 0xff,       // SEQUENCE
    0x02, 0x01, 0x00,                                           // INTEGER version = 0
    0x30, 0x0d,                                                  // SEQUENCE (AlgorithmIdentifier)
    0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, // OID rsaEncryption
    0x05, 0x00,                                                  // NULL
    0x04, 0x82, (keyLen >> 8) & 0xff, keyLen & 0xff,            // OCTET STRING
  ]);
  const pkcs8 = new Uint8Array(header.length + keyLen);
  pkcs8.set(header);
  pkcs8.set(pkcs1Der, header.length);
  return pkcs8.buffer;
}

function base64url(input) {
  const str = typeof input === "string" ? btoa(input) : btoa(String.fromCharCode(...new Uint8Array(input)));
  return str.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
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

function jsonResponse(data, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
