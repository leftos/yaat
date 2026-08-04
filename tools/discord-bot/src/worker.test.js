import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";

// worker.js keeps the write-pacing cursor at module scope, so every test re-imports a fresh
// copy instead of reaching into the module to reset it.
let worker;

/** Records the mocked clock time of every fetch call so pacing can be asserted. */
let callTimes;

const SECONDARY_LIMIT_BODY = JSON.stringify({
  message:
    "You have exceeded a secondary rate limit and have been temporarily blocked from content creation.",
  status: "403",
});

const PERMISSION_DENIED_BODY = JSON.stringify({
  message: "Resource not accessible by integration",
  status: "403",
});

function queueResponses(responses) {
  const fetchMock = vi.fn(() => {
    callTimes.push(Date.now());
    const next = responses.shift();
    if (!next) throw new Error("fetch called more times than the test queued responses for");
    return Promise.resolve(next());
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const ISSUE_URL = "https://github.com/leftos/yaat/issues/7";
const rateLimited = (headers) => () => new Response(SECONDARY_LIMIT_BODY, { status: 403, headers });
const created = () =>
  new Response(JSON.stringify({ number: 7, html_url: ISSUE_URL }), { status: 201 });
const json = (value) => new Response(JSON.stringify(value), { status: 200 });

// Captured before vi.useFakeTimers() replaces it, so settle() can yield a genuine event-loop turn
// while the fake clock is installed.
const realSetTimeout = globalThis.setTimeout;

/**
 * Drives the fake clock until `promise` settles. Timers are only run as the code under test
 * schedules them — the clock is never advanced past a pending wait — so tests can still assert the
 * exact interval between two requests.
 *
 * Each turn ends with a real setTimeout(0) yield, not a bare microtask flush: awaiting only
 * already-resolved promises chains microtasks back-to-back and starves the event loop, so work that
 * completes off the fake clock (WebCrypto signing on the libuv threadpool) could never deliver its
 * result and the turn cap tripped spuriously on slow CI runners.
 */
async function settle(promise) {
  let done = false;
  const tracked = promise.finally(() => {
    done = true;
  });
  for (let turn = 0; !done; turn++) {
    if (turn > 1000) throw new Error("promise never settled while draining timers");
    await vi.runAllTimersAsync();
    if (done) break;
    await new Promise((resolve) => realSetTimeout(resolve, 0));
  }
  return tracked;
}

beforeEach(async () => {
  vi.resetModules();
  vi.useFakeTimers();
  callTimes = [];
  worker = await import("./worker.js");
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("githubFetch rate-limit retries", () => {
  it("retries after retry-after when the wait fits the budget", async () => {
    const fetchMock = queueResponses([rateLimited({ "retry-after": "5" }), created]);

    const res = await settle(
      worker.githubFetch("https://api.github.com/repos/o/r/issues", { method: "POST" }, 20000),
    );

    expect(res.status).toBe(201);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(callTimes[1] - callTimes[0]).toBeGreaterThanOrEqual(5000);
  });

  it("gives up without retrying when retry-after exceeds the budget", async () => {
    const fetchMock = queueResponses([rateLimited({ "retry-after": "60" })]);

    const res = await settle(
      worker.githubFetch("https://api.github.com/repos/o/r/issues", { method: "POST" }, 20000),
    );

    expect(res.status).toBe(403);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("retries a 60s block when given the cron budget", async () => {
    const fetchMock = queueResponses([rateLimited({ "retry-after": "60" }), created]);

    const res = await settle(
      worker.githubFetch("https://api.github.com/repos/o/r/issues", { method: "POST" }, 150000),
    );

    expect(res.status).toBe(201);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(callTimes[1] - callTimes[0]).toBeGreaterThanOrEqual(60000);
  });

  it("waits at least a minute for a secondary limit that carries no timing hint", async () => {
    // GitHub's documented guidance when neither retry-after nor x-ratelimit-reset is present.
    const fetchMock = queueResponses([rateLimited({}), created]);

    const res = await settle(
      worker.githubFetch("https://api.github.com/repos/o/r/issues", { method: "POST" }, 150000),
    );

    expect(res.status).toBe(201);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(callTimes[1] - callTimes[0]).toBeGreaterThanOrEqual(60000);
  });

  it("honors x-ratelimit-reset when the primary quota is exhausted", async () => {
    const reset = Math.floor(Date.now() / 1000) + 3;
    const fetchMock = queueResponses([
      rateLimited({ "x-ratelimit-remaining": "0", "x-ratelimit-reset": String(reset) }),
      created,
    ]);

    const res = await settle(
      worker.githubFetch("https://api.github.com/repos/o/r/issues", { method: "POST" }, 20000),
    );

    expect(res.status).toBe(201);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(callTimes[1]).toBeGreaterThanOrEqual(reset * 1000);
  });

  it("retries a 429 the same way as a 403", async () => {
    const fetchMock = queueResponses([
      () => new Response(SECONDARY_LIMIT_BODY, { status: 429, headers: { "retry-after": "2" } }),
      created,
    ]);

    const res = await settle(
      worker.githubFetch("https://api.github.com/repos/o/r/issues", { method: "POST" }, 20000),
    );

    expect(res.status).toBe(201);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("returns a permissions 403 unchanged instead of retrying it", async () => {
    const fetchMock = queueResponses([() => new Response(PERMISSION_DENIED_BODY, { status: 403 })]);

    const res = await settle(
      worker.githubFetch("https://api.github.com/repos/o/r/issues", { method: "POST" }, 150000),
    );

    expect(res.status).toBe(403);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe("githubFetch write pacing", () => {
  it("spaces consecutive mutating requests", async () => {
    queueResponses([created, created]);

    const url = "https://api.github.com/repos/o/r/issues";
    await settle(
      Promise.all([
        worker.githubFetch(url, { method: "POST" }, 20000),
        worker.githubFetch(url, { method: "POST" }, 20000),
      ]),
    );

    expect(callTimes[1] - callTimes[0]).toBeGreaterThanOrEqual(worker.GITHUB_WRITE_SPACING_MS);
  });

  it("does not pace reads", async () => {
    queueResponses([created, created]);

    const url = "https://api.github.com/repos/o/r/issues/1";
    await settle(
      Promise.all([worker.githubFetch(url, {}, 20000), worker.githubFetch(url, {}, 20000)]),
    );

    expect(callTimes[1] - callTimes[0]).toBe(0);
  });
});

// --- Queued issue creation ---

/** In-memory stand-in for the THREAD_ISSUES KV namespace, paginating list() like the real one. */
function fakeKv(pageSize = 1000) {
  const store = new Map();
  return {
    store,
    async get(key, options) {
      const raw = store.get(key);
      if (raw === undefined) return null;
      return options?.type === "json" ? JSON.parse(raw) : raw;
    },
    async put(key, value) {
      store.set(key, value);
    },
    async delete(key) {
      store.delete(key);
    },
    async list(options) {
      const names = [...store.keys()]
        .filter((name) => !options?.prefix || name.startsWith(options.prefix))
        .sort();
      const start = options?.cursor ? Number(options.cursor) : 0;
      const page = names.slice(start, start + pageSize);
      const end = start + page.length;
      const listComplete = end >= names.length;
      return {
        keys: page.map((name) => ({ name })),
        list_complete: listComplete,
        cursor: listComplete ? undefined : String(end),
      };
    },
  };
}

/** A PKCS#8 PEM so getGitHubToken can actually sign its app JWT. */
async function generatePrivateKeyPem() {
  const pair = await crypto.subtle.generateKey(
    { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
    true,
    ["sign", "verify"],
  );
  const der = new Uint8Array(await crypto.subtle.exportKey("pkcs8", pair.privateKey));
  const base64 = btoa(String.fromCharCode(...der));
  return `-----BEGIN PRIVATE KEY-----\n${base64}\n-----END PRIVATE KEY-----`;
}

function makeEnv(kv, privateKeyPem) {
  return {
    THREAD_ISSUES: kv,
    ATTACHMENTS: { put: async () => {} },
    GITHUB_REPO: "leftos/yaat",
    DISCORD_BOT_TOKEN: "bot-token",
    GITHUB_APP_ID: "1",
    GITHUB_APP_PRIVATE_KEY: privateKeyPem,
    GITHUB_APP_INSTALLATION_ID: "42",
  };
}

/** Routes Discord and GitHub calls to canned responses and records every request. */
function stubWorld({ thread, messages, issueResponses, tokenResponses = [] }) {
  const calls = [];
  const pendingIssues = [...issueResponses];
  const pendingTokens = [...tokenResponses];

  vi.stubGlobal(
    "fetch",
    vi.fn(async (url, options = {}) => {
      const method = options.method || "GET";
      calls.push({ url, method, body: options.body ? JSON.parse(options.body) : null });

      if (url.includes("/app/installations/")) {
        const canned = pendingTokens.shift();
        if (canned) return canned();
        return json({ token: "gh-token", expires_at: new Date(Date.now() + 3600000).toISOString() });
      }
      if (url.endsWith("/issues") && method === "POST") {
        const next = pendingIssues.shift();
        if (!next) throw new Error(`unexpected issue creation: ${url}`);
        return next();
      }
      if (url.includes("/messages?")) {
        return json(messages);
      }
      if (/\/channels\/\d+$/.test(url) && method === "GET") {
        return json(thread);
      }
      return json({});
    }),
  );

  return calls;
}

const THREAD_ID = "555";
const QUEUED_RECORD = {
  guildId: "g1",
  threadName: "Aircraft stuck on taxiway",
  body: "> Created from Discord\n\nreport text",
  labels: ["bug"],
  lastSyncedMessageId: "900",
};

function commandArgs(env) {
  return {
    threadId: THREAD_ID,
    guildId: "g1",
    commandName: "create-issue",
    token: "interaction-token",
    appId: "app-id",
    env,
  };
}

describe("queued issue creation", () => {
  let privateKeyPem;
  let kv;
  let env;

  beforeAll(async () => {
    privateKeyPem = await generatePrivateKeyPem();
  });

  beforeEach(() => {
    kv = fakeKv();
    env = makeEnv(kv, privateKeyPem);
  });

  it("queues the report instead of losing it when GitHub blocks content creation", async () => {
    const calls = stubWorld({
      thread: { name: "Aircraft stuck on taxiway" },
      messages: [{ id: "900", author: { username: "reporter" }, content: "it got stuck", timestamp: "2026-07-25T00:00:00Z" }],
      issueResponses: [rateLimited({ "retry-after": "60" })],
    });

    await settle(worker.processCommand(commandArgs(env)));

    const queued = JSON.parse(kv.store.get(`pending-issue:${THREAD_ID}`));
    expect(queued.threadName).toBe("Aircraft stuck on taxiway");
    expect(queued.labels).toContain("bug");
    expect(queued.lastSyncedMessageId).toBe("900");
    expect(kv.store.has(THREAD_ID)).toBe(false);

    const reply = calls.findLast((c) => c.url.includes("/messages/@original"));
    expect(reply.body.content).toMatch(/queued/i);
  });

  it("does not create a second issue while one is queued for the thread", async () => {
    kv.store.set(`pending-issue:${THREAD_ID}`, JSON.stringify(QUEUED_RECORD));
    const calls = stubWorld({ thread: { name: "t" }, messages: [], issueResponses: [] });

    await settle(worker.processCommand(commandArgs(env)));

    expect(calls.some((c) => c.method === "POST" && c.url.endsWith("/issues"))).toBe(false);
    const reply = calls.findLast((c) => c.url.includes("/messages/@original"));
    expect(reply.body.content).toMatch(/already has an issue queued/i);
  });

  it("files the queued issue from the cron and links the thread to it", async () => {
    kv.store.set(`pending-issue:${THREAD_ID}`, JSON.stringify(QUEUED_RECORD));
    const calls = stubWorld({ thread: { name: "t" }, messages: [], issueResponses: [created] });

    await settle(worker.syncAllThreads(env));

    expect(kv.store.has(`pending-issue:${THREAD_ID}`)).toBe(false);
    expect(JSON.parse(kv.store.get(THREAD_ID))).toMatchObject({
      issueNumber: 7,
      issueUrl: ISSUE_URL,
      guildId: "g1",
      lastSyncedMessageId: "900",
    });
    expect(kv.store.get("issue:7")).toBe(THREAD_ID);

    const titlePatch = calls.find((c) => c.method === "PATCH" && c.url.endsWith(`/channels/${THREAD_ID}`));
    expect(titlePatch.body.name).toBe("[#7] Aircraft stuck on taxiway");

    const threadPost = calls.find(
      (c) => c.method === "POST" && c.url.endsWith(`/channels/${THREAD_ID}/messages`),
    );
    expect(threadPost.body.content).toContain(ISSUE_URL);
  });

  it("keeps the record for the next cron run when GitHub is still blocking", async () => {
    kv.store.set(`pending-issue:${THREAD_ID}`, JSON.stringify(QUEUED_RECORD));
    stubWorld({
      thread: { name: "t" },
      messages: [],
      issueResponses: [rateLimited({ "retry-after": "3600" })],
    });

    await settle(worker.syncAllThreads(env));

    expect(kv.store.has(`pending-issue:${THREAD_ID}`)).toBe(true);
    expect(kv.store.has(THREAD_ID)).toBe(false);
  });

  it("drains queued reports that fall on a later KV listing page", async () => {
    kv = fakeKv(1);
    env = makeEnv(kv, privateKeyPem);
    kv.store.set("pending-issue:111", JSON.stringify(QUEUED_RECORD));
    kv.store.set("pending-issue:222", JSON.stringify(QUEUED_RECORD));
    stubWorld({ thread: { name: "t" }, messages: [], issueResponses: [created, created] });

    await settle(worker.syncAllThreads(env));

    expect(kv.store.has("pending-issue:111")).toBe(false);
    expect(kv.store.has("pending-issue:222")).toBe(false);
    expect(kv.store.has("111")).toBe(true);
    expect(kv.store.has("222")).toBe(true);
  });

  it("retries a rate-limited installation-token request instead of failing the command", async () => {
    kv.store.set(`pending-issue:${THREAD_ID}`, JSON.stringify(QUEUED_RECORD));
    stubWorld({
      thread: { name: "t" },
      messages: [],
      issueResponses: [created],
      tokenResponses: [() => new Response(SECONDARY_LIMIT_BODY, { status: 429, headers: { "retry-after": "2" } })],
    });

    await settle(worker.syncAllThreads(env));

    expect(kv.store.has(`pending-issue:${THREAD_ID}`)).toBe(false);
    expect(JSON.parse(kv.store.get(THREAD_ID)).issueNumber).toBe(7);
  });

  it("drops the record without filing when the thread was linked by hand meanwhile", async () => {
    kv.store.set(`pending-issue:${THREAD_ID}`, JSON.stringify(QUEUED_RECORD));
    kv.store.set(THREAD_ID, JSON.stringify({ issueNumber: 3, issueUrl: "u", guildId: "g1", lastSyncedMessageId: "900" }));
    const calls = stubWorld({ thread: { name: "t" }, messages: [], issueResponses: [] });

    await settle(worker.syncAllThreads(env));

    expect(kv.store.has(`pending-issue:${THREAD_ID}`)).toBe(false);
    expect(calls.some((c) => c.method === "POST" && c.url.endsWith("/issues"))).toBe(false);
  });
});

describe("createGitHubIssue", () => {
  it("throws a rate-limit-flagged error when GitHub blocks content creation", async () => {
    queueResponses([rateLimited({ "retry-after": "60" })]);

    const err = await settle(
      worker
        .createGitHubIssue("t", "o/r", { title: "T", body: "B", labels: ["bug"] }, 20000)
        .catch((e) => e),
    );

    expect(err).toBeInstanceOf(worker.GitHubApiError);
    expect(err.status).toBe(403);
    expect(err.isRateLimit).toBe(true);
  });

  it("throws a non-rate-limit error for an ordinary failure", async () => {
    queueResponses([() => new Response(JSON.stringify({ message: "Validation Failed" }), { status: 422 })]);

    const err = await settle(
      worker
        .createGitHubIssue("t", "o/r", { title: "T", body: "B", labels: [] }, 20000)
        .catch((e) => e),
    );

    expect(err).toBeInstanceOf(worker.GitHubApiError);
    expect(err.status).toBe(422);
    expect(err.isRateLimit).toBe(false);
  });

  it("returns the created issue on success", async () => {
    queueResponses([created]);

    const issue = await settle(
      worker.createGitHubIssue("t", "o/r", { title: "T", body: "B", labels: [] }, 20000),
    );

    expect(issue.number).toBe(7);
  });
});
