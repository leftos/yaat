// Spike smoke test — boots `dotnet run` for Yaat.VStrips.Web, waits for the
// dev server URL to appear in its output, then drives chromium at it.
//
// Usage: node tools/Yaat.VStrips.Web/test/smoke.mjs

import { spawn, spawnSync } from 'node:child_process';
import { chromium } from 'playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, '..', '..', '..');
const tmpDir = resolve(repoRoot, '.tmp');

/**
 * Kill the entire process tree rooted at `pid`. The previous version of this
 * test used `proc.kill('SIGTERM')`, which on Windows + `shell: true` only
 * killed the wrapping `cmd.exe`. The grandchildren — `dotnet.exe` and
 * `WasmAppHost.exe` — kept running, holding the random Kestrel port and the
 * build-output DLL locks, so the next run started racing stale state.
 *
 * `taskkill /F /T /PID <pid>` walks the whole tree on Windows.
 * On POSIX, `kill -- -<pgid>` would do the same, but we only run this
 * harness on Windows for now (the wasm-tools workload is set up that way).
 */
function killTree(pid) {
    if (!pid) return;
    if (process.platform === 'win32') {
        spawnSync('taskkill', ['/F', '/T', '/PID', String(pid)], { stdio: 'ignore' });
    } else {
        try { process.kill(-pid, 'SIGTERM'); } catch { /* already gone */ }
    }
}

async function main() {
    await mkdir(tmpDir, { recursive: true });

    console.log('starting dotnet run...');
    const proc = spawn('dotnet', [
        'run',
        '--project',
        'tools/Yaat.VStrips.Web/Yaat.VStrips.Web.csproj',
        '--no-build',
    ], { cwd: repoRoot, shell: true });

    let urlResolved;
    const urlPromise = new Promise((res, rej) => {
        urlResolved = res;
        setTimeout(() => rej(new Error('timeout waiting for App url')), 60_000);
    });

    let buffer = '';
    proc.stdout.on('data', (chunk) => {
        const s = chunk.toString();
        buffer += s;
        process.stdout.write(s);
        const m = s.match(/App url: (http:\/\/127\.0\.0\.1:\d+\/)/);
        if (m) urlResolved(m[1]);
    });
    proc.stderr.on('data', (chunk) => process.stderr.write(chunk));

    // Belt-and-suspenders: if the parent ever dies, take the tree with it
    // instead of leaving WasmAppHost orphans for the next run to fight.
    process.on('exit', () => killTree(proc.pid));
    process.on('SIGINT', () => { killTree(proc.pid); process.exit(130); });

    let url;
    try {
        url = await urlPromise;
    } catch (e) {
        killTree(proc.pid);
        throw e;
    }
    console.log(`detected app url: ${url}`);

    // Use full chromium (not chrome-headless-shell) so WebGL2 + WebGPU work —
    // Avalonia.Browser falls through render modes and only the software
    // canvas works without GL. Test approximates CRC's actual WebView2
    // environment, which is full-chromium-based.
    const fullChromium = process.env.PW_CHROMIUM_PATH
        ?? 'X:/caches/playwright/chromium-1217/chrome-win64/chrome.exe';
    const browser = await chromium.launch({
        headless: true,
        executablePath: fullChromium,
        args: ['--use-gl=angle', '--enable-unsafe-swiftshader'],
    });
    try {
        const ctx = await browser.newContext();
        const page = await ctx.newPage();

        const consoleLogs = [];
        page.on('console', (msg) => {
            consoleLogs.push(`[${msg.type()}] ${msg.text()}`);
        });
        page.on('pageerror', (err) => {
            consoleLogs.push(`[pageerror] ${err.message}`);
        });

        // Track network — we want bundle-size ground truth.
        const transfers = [];
        page.on('response', async (resp) => {
            try {
                const status = resp.status();
                const u = resp.url();
                const headers = await resp.allHeaders();
                const enc = headers['content-encoding'] || '';
                const lenStr = headers['content-length'] || '';
                const len = lenStr ? Number(lenStr) : null;
                transfers.push({ status, url: u, encoding: enc, length: len });
            } catch (e) {
                // ignore
            }
        });

        const t0 = Date.now();
        await page.goto(url, { waitUntil: 'load', timeout: 90_000 });
        const tLoad = Date.now() - t0;
        console.log(`page loaded in ${tLoad}ms`);

        // Wait for Avalonia to render the spike text — the canvas/view appears after WASM boots.
        // We'll wait up to 60s for the spike heading to be visible.
        const spikeText = 'YAAT vStrips Web — spike';
        try {
            await page.waitForFunction(
                (needle) => document.body && document.body.innerText.includes(needle),
                spikeText,
                { timeout: 60_000 },
            );
            console.log(`spike text rendered after ${Date.now() - t0}ms total`);
        } catch (e) {
            console.error('spike text never appeared in DOM');
            // Avalonia.Browser renders to a canvas — the text may not be in the DOM.
            // Fall back to checking whether a canvas exists.
            const hasCanvas = await page.evaluate(() => {
                const out = document.getElementById('out');
                return out ? !!out.querySelector('canvas') : false;
            });
            console.log(`hasCanvas: ${hasCanvas}`);
        }

        const screenshotPath = resolve(tmpDir, 'vstripsweb-spike.png');
        await page.screenshot({ path: screenshotPath, fullPage: true });
        console.log(`screenshot: ${screenshotPath}`);

        const totalBytes = transfers.reduce((sum, t) => sum + (t.length || 0), 0);
        console.log(`\n=== bundle transfer summary ===`);
        console.log(`total responses: ${transfers.length}`);
        console.log(`total bytes (declared content-length): ${(totalBytes / 1024 / 1024).toFixed(2)} MB`);
        const wasmTransfers = transfers.filter(t => t.url.endsWith('.wasm'));
        const wasmBytes = wasmTransfers.reduce((sum, t) => sum + (t.length || 0), 0);
        console.log(`wasm responses: ${wasmTransfers.length}, ${(wasmBytes / 1024 / 1024).toFixed(2)} MB`);

        await writeFile(
            resolve(tmpDir, 'vstripsweb-spike-network.json'),
            JSON.stringify({ tLoadMs: tLoad, transfers }, null, 2),
        );

        console.log(`\n=== console logs (${consoleLogs.length}) ===`);
        for (const line of consoleLogs.slice(-30)) console.log(line);
    } finally {
        await browser.close();
        killTree(proc.pid);
    }
}

main().catch((e) => {
    console.error(e);
    process.exit(1);
});
