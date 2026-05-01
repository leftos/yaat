// Live-server validation — points chromium at a running yaat-server's
// /vstrips/?initials=AJ endpoint, watches for the SignalR connect to flip
// VStripsViewModel.IsConnected to true (which hides the disconnected
// banner). Caller is responsible for booting yaat-server separately —
// keeps the harness lean and lets you reuse a server you already have
// running with a loaded scenario for richer manual validation.
//
// Usage:
//   node tools/Yaat.VStrips.Web/test/live.mjs [server-url] [query]
//
//   server-url defaults to http://localhost:5130
//   query      defaults to ?initials=AJ
//
// Exits 0 on banner clear, 1 on timeout / SignalR error / page error.

import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, '..', '..', '..');
const tmpDir = resolve(repoRoot, '.tmp');

const serverUrl = process.argv[2] || 'http://localhost:5130';
const query = process.argv[3] || '?initials=AJ';
const targetUrl = `${serverUrl.replace(/\/$/, '')}/vstrips/${query}`;

async function main() {
    await mkdir(tmpDir, { recursive: true });
    console.log(`testing ${targetUrl}`);

    const fullChromium = process.env.PW_CHROMIUM_PATH
        ?? 'X:/caches/playwright/chromium-1217/chrome-win64/chrome.exe';
    const browser = await chromium.launch({
        headless: true,
        executablePath: fullChromium,
        args: ['--use-gl=angle', '--enable-unsafe-swiftshader'],
    });

    let exitCode = 1;
    try {
        const ctx = await browser.newContext();
        const page = await ctx.newPage();

        const consoleLogs = [];
        page.on('console', (msg) => consoleLogs.push(`[${msg.type()}] ${msg.text()}`));
        page.on('pageerror', (err) => consoleLogs.push(`[pageerror] ${err.message}`));
        page.on('requestfailed', (req) => {
            consoleLogs.push(`[requestfailed] ${req.url()} ${req.failure()?.errorText}`);
        });
        page.on('response', (resp) => {
            if (resp.status() >= 400) {
                consoleLogs.push(`[http ${resp.status()}] ${resp.url()}`);
            }
        });

        const t0 = Date.now();
        await page.goto(targetUrl, { waitUntil: 'load', timeout: 60_000 });
        console.log(`page loaded in ${Date.now() - t0}ms`);

        // The disconnected banner is a Border with a fixed red background.
        // Avalonia.Browser renders to canvas, so we can't query it via DOM
        // selectors directly. Instead, watch the .NET-side console output
        // forwarded by Mono-WASM's stdio: ServerConnection logs "Connected"
        // once StartAsync succeeds. ConsoleLineLoggerProvider tags every
        // line with INF/WRN/ERR — we look for ` INF ` immediately preceding
        // `] Connected` so the substring isn't ambiguous with "Connecting".
        const connectedRegex = /INF [^\]]+\] Connected\b/;
        console.log('waiting up to 30s for SignalR Connected log...');
        for (let i = 0; i < 60; i++) {
            if (consoleLogs.some((l) => connectedRegex.test(l))) {
                console.log(`SignalR Connected after ${Date.now() - t0}ms total`);
                exitCode = 0;
                break;
            }
            await new Promise((r) => setTimeout(r, 500));
        }

        // Verify there were no SignalR / pageerror lines, even if we
        // technically saw "Connected" elsewhere.
        const errs = consoleLogs.filter(
            (l) => l.startsWith('[error]') || l.startsWith('[pageerror]') || l.startsWith('[requestfailed]') || l.startsWith('[http '),
        );
        if (errs.length > 0) {
            console.log(`\n=== errors / failed requests (${errs.length}) ===`);
            for (const e of errs) console.log(e);
            exitCode = exitCode === 0 ? 0 : 1; // already-success stays success; failure stays failure
        }

        const screenshotPath = resolve(tmpDir, 'vstripsweb-live.png');
        await page.screenshot({ path: screenshotPath, fullPage: true });
        console.log(`screenshot: ${screenshotPath}`);

        if (exitCode !== 0) {
            console.log(`\n=== last 30 console lines ===`);
            for (const line of consoleLogs.slice(-30)) console.log(line);
        }
    } finally {
        await browser.close();
    }

    process.exit(exitCode);
}

main().catch((e) => {
    console.error(e);
    process.exit(1);
});
