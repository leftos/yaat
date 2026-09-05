import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  PAYMENT_PREFIX,
  computeProgress,
  forgetPayment,
  formatChannelName,
  handleKofiWebhook,
  parseKofiBody,
  paymentRecordFrom,
  refreshSupportDisplay,
} from "./support.js";

/** In-memory KV that keeps list() metadata like the real namespace does. */
function fakeKv() {
  const store = new Map();
  return {
    store,
    async get(key, options) {
      const entry = store.get(key);
      if (entry === undefined) return null;
      return options?.type === "json" ? JSON.parse(entry.value) : entry.value;
    },
    async put(key, value, options) {
      store.set(key, { value, metadata: options?.metadata });
    },
    async delete(key) {
      store.delete(key);
    },
    async list(options) {
      const keys = [...store.entries()]
        .filter(([name]) => !options?.prefix || name.startsWith(options.prefix))
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([name, entry]) => ({ name, metadata: entry.metadata }));
      return { keys, list_complete: true, cursor: undefined };
    },
  };
}

const CONFIG = { voiceChannelId: "voice-1", textChannelId: "text-1", embedMessageId: "msg-1" };
const NOW = new Date("2026-09-05T12:00:00Z");

function makeEnv(kv) {
  return {
    THREAD_ISSUES: kv,
    DISCORD_BOT_TOKEN: "bot-token",
    KOFI_VERIFICATION_TOKEN: "secret-token",
    MONTHLY_COST_USD: "24",
    KOFI_PAGE_URL: "https://ko-fi.com/leftos",
  };
}

/** Records every Discord call; `respond` can fail a specific call to simulate a 429. */
function stubDiscord(respond = () => new Response("{}", { status: 200 })) {
  const calls = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url, init) => {
      const call = { method: init?.method || "GET", url: String(url), body: init?.body ? JSON.parse(init.body) : undefined };
      calls.push(call);
      return respond(call);
    }),
  );
  return calls;
}

function kofiPayload(overrides) {
  return {
    verification_token: "secret-token",
    message_id: "6b5a0b2d-0000-0000-0000-000000000001",
    timestamp: "2026-09-03T14:22:31Z",
    type: "Donation",
    is_public: true,
    from_name: "Jo Example",
    message: "Great work!",
    amount: "5.00",
    url: "https://ko-fi.com/Home/CoffeeShop?txid=tx-1",
    email: "jo@example.com",
    currency: "USD",
    is_subscription_payment: false,
    is_first_subscription_payment: false,
    kofi_transaction_id: "tx-1",
    tier_name: null,
    ...overrides,
  };
}

function kofiRequest(payload) {
  return new Request("https://bot.example/kofi", {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ data: JSON.stringify(payload) }).toString(),
  });
}

function makeCtx() {
  const pending = [];
  return { waitUntil: (promise) => pending.push(promise), settle: () => Promise.all(pending) };
}

function record(overrides) {
  return {
    id: "tx",
    name: "Jo",
    isPublic: true,
    amount: 5,
    currency: "USD",
    isSubscription: false,
    paidAt: "2026-09-03T14:22:31.000Z",
    month: "2026-09",
    ...overrides,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("parseKofiBody", () => {
  it("reads the JSON inside the form-encoded data field", () => {
    const body = new URLSearchParams({ data: JSON.stringify({ type: "Donation" }) }).toString();
    expect(parseKofiBody(body)).toEqual({ type: "Donation" });
  });

  it("returns null for a missing field or malformed JSON", () => {
    expect(parseKofiBody("")).toBeNull();
    expect(parseKofiBody("other=1")).toBeNull();
    expect(parseKofiBody("data=%7Bnot-json")).toBeNull();
  });
});

describe("paymentRecordFrom", () => {
  it("keeps only what the display needs and never the email", () => {
    const { record: parsed } = paymentRecordFrom(kofiPayload({ amount: "12.5" }));
    expect(parsed).toEqual({
      id: "tx-1",
      name: "Jo Example",
      isPublic: true,
      amount: 12.5,
      currency: "USD",
      isSubscription: false,
      paidAt: "2026-09-03T14:22:31.000Z",
      month: "2026-09",
    });
    expect(JSON.stringify(parsed)).not.toContain("example.com");
  });

  it("treats Anonymous and private payments as not public", () => {
    expect(paymentRecordFrom(kofiPayload({ from_name: "Anonymous" })).record.isPublic).toBe(false);
    expect(paymentRecordFrom(kofiPayload({ is_public: false })).record.isPublic).toBe(false);
    expect(paymentRecordFrom(kofiPayload({ is_public: undefined })).record.isPublic).toBe(false);
  });

  it("marks subscription payments from either signal", () => {
    expect(paymentRecordFrom(kofiPayload({ type: "Subscription" })).record.isSubscription).toBe(true);
    expect(paymentRecordFrom(kofiPayload({ is_subscription_payment: true })).record.isSubscription).toBe(true);
  });

  it("rejects a missing transaction id, a bad amount, and a bad timestamp", () => {
    expect(paymentRecordFrom(kofiPayload({ kofi_transaction_id: "" })).error).toMatch(/kofi_transaction_id/);
    expect(paymentRecordFrom(kofiPayload({ amount: "five" })).error).toMatch(/amount/);
    expect(paymentRecordFrom(kofiPayload({ amount: "-1" })).error).toMatch(/amount/);
    expect(paymentRecordFrom(kofiPayload({ timestamp: "yesterday" })).error).toMatch(/timestamp/);
  });
});

describe("computeProgress", () => {
  it("starts empty", () => {
    const progress = computeProgress([], 24, NOW);
    expect(progress).toMatchObject({ month: "2026-09", carry: 0, raw: 0, covered: 0, surplus: 0, percent: 0, monthsAhead: 0, supporters: [], anonymousCount: 0 });
  });

  it("rolls last month's surplus into this month", () => {
    const progress = computeProgress([record({ id: "a", month: "2026-08", amount: 30 }), record({ id: "b", amount: 10 })], 24, NOW);
    expect(progress).toMatchObject({ carry: 6, raw: 10, covered: 16, percent: 67 });
  });

  it("floors a short month at zero instead of carrying debt", () => {
    const progress = computeProgress([record({ id: "a", month: "2026-07", amount: 10 })], 24, NOW);
    expect(progress.carry).toBe(0);
  });

  it("charges every month between the first payment and now, including empty ones", () => {
    const progress = computeProgress([record({ id: "a", month: "2026-06", amount: 50 })], 24, NOW);
    // June: 50 - 24 = 26 → July: 26 - 24 = 2 → August: 2 - 24 → 0
    expect(progress.carry).toBe(0);
    const closer = computeProgress([record({ id: "a", month: "2026-07", amount: 50 })], 24, NOW);
    expect(closer.carry).toBe(2);
  });

  it("reports how many further months a surplus covers", () => {
    const progress = computeProgress([record({ amount: 60 })], 24, NOW);
    expect(progress).toMatchObject({ covered: 60, surplus: 36, monthsAhead: 1, percent: 250 });
  });

  it("lists public supporters once each and counts the rest as anonymous", () => {
    const progress = computeProgress(
      [
        record({ id: "a", name: "Jo", amount: 3, paidAt: "2026-09-02T00:00:00.000Z" }),
        record({ id: "b", name: "Sam", isSubscription: true, paidAt: "2026-09-01T00:00:00.000Z" }),
        record({ id: "c", name: "Jo", isSubscription: true, paidAt: "2026-09-03T00:00:00.000Z" }),
        record({ id: "d", name: "Anonymous", isPublic: false }),
        record({ id: "e", name: "Hidden", isPublic: false }),
      ],
      24,
      NOW,
    );
    expect(progress.supporters).toEqual([
      { name: "Sam", isSubscription: true },
      { name: "Jo", isSubscription: true },
    ]);
    expect(progress.anonymousCount).toBe(2);
  });

  it("assigns a payment to its UTC month", () => {
    const lastSecondOfAugust = paymentRecordFrom(kofiPayload({ timestamp: "2026-08-31T23:59:59Z" })).record;
    const firstSecondOfSeptember = paymentRecordFrom(kofiPayload({ timestamp: "2026-09-01T00:00:00Z" })).record;
    expect(lastSecondOfAugust.month).toBe("2026-08");
    expect(firstSecondOfSeptember.month).toBe("2026-09");
  });
});

describe("formatChannelName", () => {
  it("renders whole dollars without cents and cents when present", () => {
    expect(formatChannelName(computeProgress([record({ amount: 15.5 })], 24, NOW))).toBe("💰 Server: $15.50 / $24 · 65%");
    expect(formatChannelName(computeProgress([record({ amount: 24 })], 24, NOW))).toBe("💰 Server: $24 / $24 · 100%");
  });
});

describe("handleKofiWebhook", () => {
  let kv;
  let env;

  beforeEach(() => {
    kv = fakeKv();
    env = makeEnv(kv);
  });

  it("rejects a wrong or missing verification token", async () => {
    stubDiscord();
    const wrong = await handleKofiWebhook(kofiRequest(kofiPayload({ verification_token: "nope" })), env, makeCtx(), CONFIG, NOW);
    expect(wrong.status).toBe(401);
    const missing = await handleKofiWebhook(kofiRequest(kofiPayload({ verification_token: undefined })), env, makeCtx(), CONFIG, NOW);
    expect(missing.status).toBe(401);
    expect(kv.store.size).toBe(0);
  });

  it("rejects a body that is not Ko-fi's form shape", async () => {
    const res = await handleKofiWebhook(new Request("https://bot.example/kofi", { method: "POST", body: "{}" }), env, makeCtx(), CONFIG, NOW);
    expect(res.status).toBe(400);
  });

  it("acknowledges but does not count shop orders", async () => {
    const calls = stubDiscord();
    const res = await handleKofiWebhook(kofiRequest(kofiPayload({ type: "Shop Order" })), env, makeCtx(), CONFIG, NOW);
    expect(res.status).toBe(200);
    expect(kv.store.size).toBe(0);
    expect(calls).toEqual([]);
  });

  it("records a donation, thanks the supporter, and renders both surfaces", async () => {
    const calls = stubDiscord();
    const ctx = makeCtx();
    const res = await handleKofiWebhook(kofiRequest(kofiPayload()), env, ctx, CONFIG, NOW);
    expect(res.status).toBe(200);
    await ctx.settle();

    const stored = kv.store.get(`${PAYMENT_PREFIX}tx-1`);
    expect(stored.metadata).toMatchObject({ id: "tx-1", amount: 5, month: "2026-09" });

    expect(calls.map((c) => [c.method, c.url])).toEqual([
      ["POST", "https://discord.com/api/v10/channels/text-1/messages"],
      ["PATCH", "https://discord.com/api/v10/channels/voice-1"],
      ["PATCH", "https://discord.com/api/v10/channels/text-1/messages/msg-1"],
    ]);
    expect(calls[0].body.content).toBe("☕ Thank you, **Jo Example**, for supporting YAAT!");
    expect(calls[1].body.name).toBe("💰 Server: $5 / $24 · 21%");
    const embed = calls[2].body.embeds[0];
    expect(embed.title).toBe("YAAT server costs — September 2026");
    expect(embed.fields[0].value).toBe("☕ Jo Example");
    expect(embed.timestamp).toBe(NOW.toISOString());
    expect(calls[2].body.components[0].components[0].url).toBe("https://ko-fi.com/leftos");
  });

  it("counts a redelivered transaction once", async () => {
    const calls = stubDiscord();
    const first = makeCtx();
    await handleKofiWebhook(kofiRequest(kofiPayload()), env, first, CONFIG, NOW);
    await first.settle();
    const callsAfterFirst = calls.length;

    const second = makeCtx();
    const res = await handleKofiWebhook(kofiRequest(kofiPayload()), env, second, CONFIG, NOW);
    await second.settle();
    expect(res.status).toBe(200);
    expect(kv.store.size).toBe(2); // the payment + the display snapshot
    expect(calls.length).toBe(callsAfterFirst);
  });

  it("does not name a private supporter in the channel", async () => {
    const calls = stubDiscord();
    const ctx = makeCtx();
    await handleKofiWebhook(kofiRequest(kofiPayload({ is_public: false })), env, ctx, CONFIG, NOW);
    await ctx.settle();
    expect(calls.map((c) => c.method)).toEqual(["PATCH", "PATCH"]);
    expect(calls[1].body.embeds[0].fields[0].value).toBe("+1 anonymous");
  });

  it("writes the ledger even before the channels are set up", async () => {
    const calls = stubDiscord();
    const ctx = makeCtx();
    await handleKofiWebhook(kofiRequest(kofiPayload()), env, ctx, { voiceChannelId: "", textChannelId: "", embedMessageId: "" }, NOW);
    await ctx.settle();
    expect(kv.store.has(`${PAYMENT_PREFIX}tx-1`)).toBe(true);
    expect(calls).toEqual([]);
  });
});

describe("refreshSupportDisplay", () => {
  let kv;
  let env;

  beforeEach(() => {
    kv = fakeKv();
    env = makeEnv(kv);
    kv.put(`${PAYMENT_PREFIX}tx-1`, "{}", { metadata: record({ id: "tx-1" }) });
  });

  it("skips Discord entirely when nothing changed since the last render", async () => {
    const calls = stubDiscord();
    await refreshSupportDisplay(env, CONFIG, NOW);
    expect(calls.length).toBe(2);
    await refreshSupportDisplay(env, CONFIG, NOW);
    expect(calls.length).toBe(2);
  });

  it("retries a rename Discord refused on the next refresh", async () => {
    let failRename = true;
    const calls = stubDiscord((call) =>
      failRename && call.url.endsWith("/channels/voice-1")
        ? new Response(JSON.stringify({ retry_after: 300 }), { status: 429 })
        : new Response("{}", { status: 200 }),
    );
    await refreshSupportDisplay(env, CONFIG, NOW);
    failRename = false;
    await refreshSupportDisplay(env, CONFIG, NOW);
    expect(calls.map((c) => [c.method, c.url.split("/v10")[1]])).toEqual([
      ["PATCH", "/channels/voice-1"],
      ["PATCH", "/channels/text-1/messages/msg-1"],
      ["PATCH", "/channels/voice-1"],
    ]);
  });

  it("re-renders when the month rolls over even with no new payment", async () => {
    const calls = stubDiscord();
    await refreshSupportDisplay(env, CONFIG, NOW);
    await refreshSupportDisplay(env, CONFIG, new Date("2026-10-01T00:05:00Z"));
    expect(calls.length).toBe(4);
    expect(calls[2].body.name).toBe("💰 Server: $0 / $24 · 0%");
    expect(calls[3].body.embeds[0].title).toBe("YAAT server costs — October 2026");
  });

  it("fails fast on a missing monthly cost", async () => {
    stubDiscord();
    await expect(refreshSupportDisplay({ ...env, MONTHLY_COST_USD: "" }, CONFIG, NOW)).rejects.toThrow(/MONTHLY_COST_USD/);
  });
});

describe("forgetPayment", () => {
  it("drops the record and re-renders, and reports an unknown id", async () => {
    const kv = fakeKv();
    const env = makeEnv(kv);
    await kv.put(`${PAYMENT_PREFIX}tx-1`, "{}", { metadata: record({ id: "tx-1" }) });
    const calls = stubDiscord();

    expect(await forgetPayment(env, CONFIG, " tx-1 ", NOW)).toBe(true);
    expect(kv.store.has(`${PAYMENT_PREFIX}tx-1`)).toBe(false);
    expect(calls[0].body.name).toBe("💰 Server: $0 / $24 · 0%");

    const before = calls.length;
    expect(await forgetPayment(env, CONFIG, "tx-9", NOW)).toBe(false);
    expect(calls.length).toBe(before);
  });
});
