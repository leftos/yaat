// Server-cost tracker: Ko-fi payment webhooks → KV ledger → a voice-channel-name ticker and a
// bot-edited embed showing progress towards the monthly hosting cost. Supporter roles are handed
// out by Ko-fi's own Discord integration, so nothing here ever stores a supporter's email.

export const PAYMENT_PREFIX = "support:payment:";
const DISPLAY_KEY = "support:display";

/** Ko-fi event types that count towards the server cost. Shop orders and commissions do not. */
const COUNTED_TYPES = new Set(["Donation", "Subscription"]);

const PROGRESS_BAR_WIDTH = 20;
const MAX_LISTED_SUPPORTERS = 25;
const MAX_NAME_LENGTH = 80;
const KOFI_BLUE = 0x29abe0;
const FUNDED_GREEN = 0x3ba55c;

const MONTH_NAMES = [
  "January",
  "February",
  "March",
  "April",
  "May",
  "June",
  "July",
  "August",
  "September",
  "October",
  "November",
  "December",
];

/** True once the setup script has written the channel and message ids the worker renders into. */
export function isSupportConfigured(config) {
  return Boolean(config?.voiceChannelId && config?.textChannelId && config?.embedMessageId);
}

/** The monthly hosting cost from the worker's vars; a missing or non-positive value is a config bug. */
export function monthlyCostFrom(env) {
  const cost = Number(env.MONTHLY_COST_USD);
  if (!Number.isFinite(cost) || cost <= 0) {
    throw new Error(`MONTHLY_COST_USD must be a positive number, got ${JSON.stringify(env.MONTHLY_COST_USD)}`);
  }
  return cost;
}

/** Constant-time string comparison so a token check does not leak how many leading bytes matched. */
export function timingSafeEqual(a, b) {
  if (a.length !== b.length) return false;
  let result = 0;
  for (let i = 0; i < a.length; i++) {
    result |= a.charCodeAt(i) ^ b.charCodeAt(i);
  }
  return result === 0;
}

/**
 * Ko-fi POSTs `application/x-www-form-urlencoded` with a single `data` field holding the payment
 * JSON. Returns the parsed object, or null when the body is not in that shape.
 */
export function parseKofiBody(bodyText) {
  const data = new URLSearchParams(bodyText).get("data");
  if (!data) return null;
  try {
    const parsed = JSON.parse(data);
    return parsed && typeof parsed === "object" ? parsed : null;
  } catch {
    return null;
  }
}

/** UTC calendar month of an ISO timestamp as `YYYY-MM`, or null if the timestamp is unparseable. */
export function monthKeyOf(isoTimestamp) {
  const ms = Date.parse(isoTimestamp);
  if (!Number.isFinite(ms)) return null;
  const date = new Date(ms);
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}`;
}

function isAnonymousName(name) {
  const trimmed = (name || "").trim();
  return trimmed === "" || trimmed.toLowerCase() === "anonymous";
}

/**
 * Turns a verified Ko-fi payment into the ledger record, or returns `{ error }` when a required
 * field is missing or malformed. Only fields the display needs are kept — never the email.
 */
export function paymentRecordFrom(payload) {
  const id = typeof payload.kofi_transaction_id === "string" ? payload.kofi_transaction_id.trim() : "";
  if (!id) return { error: "missing kofi_transaction_id" };

  const amount = Number(payload.amount);
  if (!Number.isFinite(amount) || amount < 0) return { error: `bad amount ${JSON.stringify(payload.amount)}` };

  const month = monthKeyOf(payload.timestamp);
  if (!month) return { error: `bad timestamp ${JSON.stringify(payload.timestamp)}` };

  const name = String(payload.from_name || "").trim().slice(0, MAX_NAME_LENGTH);
  return {
    record: {
      id,
      name,
      isPublic: payload.is_public === true && !isAnonymousName(name),
      amount: Math.round(amount * 100) / 100,
      currency: String(payload.currency || ""),
      isSubscription: payload.is_subscription_payment === true || payload.type === "Subscription",
      paidAt: new Date(Date.parse(payload.timestamp)).toISOString(),
      month,
    },
  };
}

/** Reads every ledger record from KV metadata — one list call per 1000 payments, no per-key gets. */
export async function listPayments(kv) {
  const records = [];
  let cursor;
  do {
    const page = await kv.list({ prefix: PAYMENT_PREFIX, cursor });
    for (const key of page.keys) {
      if (key.metadata) records.push(key.metadata);
    }
    cursor = page.list_complete ? undefined : page.cursor;
  } while (cursor);
  return records;
}

function roundCents(value) {
  return Math.round(value * 100) / 100;
}

function nextMonthKey(monthKey) {
  const [year, month] = monthKey.split("-").map(Number);
  const date = new Date(Date.UTC(year, month, 1));
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}`;
}

function previousMonthKey(monthKey) {
  const [year, month] = monthKey.split("-").map(Number);
  const date = new Date(Date.UTC(year, month - 2, 1));
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}`;
}

/**
 * Progress for the month containing `now`. Each earlier month's surplus over the cost carries
 * forward; a shortfall floors at zero, so a bad month never starts the next one in debt.
 */
export function computeProgress(records, cost, now) {
  const currentMonth = monthKeyOf(now.toISOString());
  const totalsByMonth = new Map();
  for (const record of records) {
    totalsByMonth.set(record.month, (totalsByMonth.get(record.month) || 0) + record.amount);
  }

  const earlierMonths = [...totalsByMonth.keys()].filter((month) => month < currentMonth).sort();
  let carry = 0;
  if (earlierMonths.length > 0) {
    for (let month = earlierMonths[0]; month < currentMonth; month = nextMonthKey(month)) {
      carry = Math.max(0, roundCents(carry + (totalsByMonth.get(month) || 0) - cost));
    }
  }

  const thisMonth = records.filter((record) => record.month === currentMonth).sort((a, b) => a.paidAt.localeCompare(b.paidAt));
  const raw = roundCents(thisMonth.reduce((sum, record) => sum + record.amount, 0));
  const covered = roundCents(carry + raw);
  const surplus = Math.max(0, roundCents(covered - cost));

  const supportersByName = new Map();
  let anonymousCount = 0;
  for (const record of thisMonth) {
    if (!record.isPublic) {
      anonymousCount++;
      continue;
    }
    const existing = supportersByName.get(record.name);
    if (existing) {
      existing.isSubscription = existing.isSubscription || record.isSubscription;
    } else {
      supportersByName.set(record.name, { name: record.name, isSubscription: record.isSubscription });
    }
  }

  return {
    month: currentMonth,
    cost,
    carry,
    raw,
    covered,
    surplus,
    percent: Math.round((covered / cost) * 100),
    monthsAhead: Math.floor(surplus / cost),
    supporters: [...supportersByName.values()],
    anonymousCount,
  };
}

export function formatUsd(value) {
  return Number.isInteger(value) ? `$${value}` : `$${value.toFixed(2)}`;
}

function monthLabel(monthKey) {
  const [year, month] = monthKey.split("-").map(Number);
  return `${MONTH_NAMES[month - 1]} ${year}`;
}

/** The voice channel's name doubles as a sidebar ticker; Discord caps channel names at 100 chars. */
export function formatChannelName(progress) {
  const percent = Math.min(progress.percent, 999);
  return `💰 Server: ${formatUsd(progress.covered)} / ${formatUsd(progress.cost)} · ${percent}%`;
}

function progressBar(progress) {
  const filled = Math.max(0, Math.min(PROGRESS_BAR_WIDTH, Math.round((progress.covered / progress.cost) * PROGRESS_BAR_WIDTH)));
  return "█".repeat(filled) + "░".repeat(PROGRESS_BAR_WIDTH - filled);
}

function supportersField(progress) {
  const lines = progress.supporters
    .slice(0, MAX_LISTED_SUPPORTERS)
    .map((supporter) => `${supporter.isSubscription ? "🔁" : "☕"} ${supporter.name}`);
  const overflow = progress.supporters.length - MAX_LISTED_SUPPORTERS;
  if (overflow > 0) lines.push(`+${overflow} more`);
  if (progress.anonymousCount > 0) {
    lines.push(`+${progress.anonymousCount} anonymous`);
  }
  return lines.length > 0 ? lines.join("\n") : "Nobody yet this month — be the first!";
}

/**
 * The embed message body. `timestamp` is left off so the caller can decide whether two renders
 * differ; Discord shows it next to the footer once added.
 */
export function buildSupportMessage(progress, kofiUrl) {
  const funded = progress.covered >= progress.cost;
  const descriptionLines = [
    `\`${progressBar(progress)}\` **${Math.min(progress.percent, 999)}%** — ${formatUsd(progress.covered)} of ${formatUsd(progress.cost)} covered`,
  ];
  if (progress.carry > 0) {
    descriptionLines.push(`Carried over from ${monthLabel(previousMonthKey(progress.month))}: ${formatUsd(progress.carry)}`);
  }
  if (progress.monthsAhead > 0) {
    descriptionLines.push(`Surplus already covers the next ${progress.monthsAhead === 1 ? "month" : `${progress.monthsAhead} months`}.`);
  } else if (funded) {
    descriptionLines.push("This month is covered — anything extra rolls into next month.");
  } else {
    descriptionLines.push("Anything over the goal rolls into next month.");
  }

  return {
    embeds: [
      {
        title: `YAAT server costs — ${monthLabel(progress.month)}`,
        description: descriptionLines.join("\n"),
        color: funded ? FUNDED_GREEN : KOFI_BLUE,
        fields: [{ name: "This month's supporters", value: supportersField(progress) }],
        footer: {
          text: "One-time and monthly Ko-fi support both count. Supporters can claim their Discord role from the Ko-fi page.",
        },
      },
    ],
    components: [
      {
        type: 1,
        components: [{ type: 2, style: 5, label: "Support YAAT on Ko-fi", url: kofiUrl }],
      },
    ],
  };
}

async function discordRequest(botToken, method, path, body) {
  const headers = { Authorization: `Bot ${botToken}` };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(`https://discord.com/api/v10${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    console.error(`Discord ${method} ${path} failed (${res.status}): ${await res.text()}`);
  }
  return res.ok;
}

async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

/**
 * Re-renders the ticker and the embed from the ledger. Discord allows two channel renames per ten
 * minutes, so both surfaces are only written when their content changed since the last successful
 * write; a failed write is retried by the next cron tick because it is never recorded as done.
 */
export async function refreshSupportDisplay(env, config, now) {
  if (!isSupportConfigured(config)) return;

  const progress = computeProgress(await listPayments(env.THREAD_ISSUES), monthlyCostFrom(env), now);
  const channelName = formatChannelName(progress);
  const message = buildSupportMessage(progress, env.KOFI_PAGE_URL);
  const messageHash = await sha256Hex(JSON.stringify(message));

  const previous = (await env.THREAD_ISSUES.get(DISPLAY_KEY, { type: "json" })) || {};
  const next = { ...previous };

  if (previous.channelName !== channelName) {
    const renamed = await discordRequest(env.DISCORD_BOT_TOKEN, "PATCH", `/channels/${config.voiceChannelId}`, { name: channelName });
    if (renamed) next.channelName = channelName;
  }

  if (previous.messageHash !== messageHash) {
    message.embeds[0].timestamp = now.toISOString();
    const edited = await discordRequest(
      env.DISCORD_BOT_TOKEN,
      "PATCH",
      `/channels/${config.textChannelId}/messages/${config.embedMessageId}`,
      message,
    );
    if (edited) next.messageHash = messageHash;
  }

  if (next.channelName !== previous.channelName || next.messageHash !== previous.messageHash) {
    await env.THREAD_ISSUES.put(DISPLAY_KEY, JSON.stringify(next));
  }
}

function thankYouText(record) {
  return record.isSubscription
    ? `🔁 Thank you, **${record.name}**, for your monthly support of YAAT!`
    : `☕ Thank you, **${record.name}**, for supporting YAAT!`;
}

async function afterNewPayment(env, config, record, now) {
  if (isSupportConfigured(config) && record.isPublic) {
    await discordRequest(env.DISCORD_BOT_TOKEN, "POST", `/channels/${config.textChannelId}/messages`, {
      content: thankYouText(record),
    });
  }
  await refreshSupportDisplay(env, config, now);
}

/**
 * `POST /kofi`. Ko-fi wants a 2xx within 15 seconds and retries otherwise, so the ledger write is
 * synchronous and the Discord updates run after the response. Duplicate deliveries are recognised by
 * transaction id and acknowledged without a second count.
 */
export async function handleKofiWebhook(request, env, ctx, config, now) {
  const payload = parseKofiBody(await request.text());
  if (!payload) {
    return new Response("Expected form field 'data' with JSON", { status: 400 });
  }

  const secret = env.KOFI_VERIFICATION_TOKEN || "";
  const token = typeof payload.verification_token === "string" ? payload.verification_token : "";
  if (!secret || !timingSafeEqual(token, secret)) {
    console.error(
      `Ko-fi verification token mismatch: received ${token.length} chars, expected ${secret.length} (event type ${payload.type})`,
    );
    return new Response("Invalid verification token", { status: 401 });
  }

  if (!COUNTED_TYPES.has(payload.type)) {
    console.log(`Ko-fi ${payload.type} event ignored (only Donation and Subscription count)`);
    return new Response("Ignored", { status: 200 });
  }

  const { record, error } = paymentRecordFrom(payload);
  if (error) {
    console.error(`Ko-fi payload rejected: ${error}`);
    return new Response(error, { status: 400 });
  }

  const key = PAYMENT_PREFIX + record.id;
  if ((await env.THREAD_ISSUES.get(key)) !== null) {
    return new Response("Already recorded", { status: 200 });
  }
  await env.THREAD_ISSUES.put(key, JSON.stringify(record), { metadata: record });
  console.log(`Ko-fi ${payload.type} recorded: ${record.id} ${record.amount} ${record.currency} (${record.month})`);

  ctx.waitUntil(afterNewPayment(env, config, record, now).catch((err) => console.error("Support display update failed:", err)));
  return new Response("OK", { status: 200 });
}

/**
 * Drops one payment from the ledger (a dashboard test event, or a refund — Ko-fi sends no refund
 * webhook) and re-renders. Returns whether the record existed.
 */
export async function forgetPayment(env, config, transactionId, now) {
  const key = PAYMENT_PREFIX + transactionId.trim();
  const existed = (await env.THREAD_ISSUES.get(key)) !== null;
  if (existed) {
    await env.THREAD_ISSUES.delete(key);
    await refreshSupportDisplay(env, config, now);
  }
  return existed;
}
