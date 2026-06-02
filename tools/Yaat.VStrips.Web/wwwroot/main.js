import { dotnet } from './_framework/dotnet.js';

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

// Keep the browser's reload shortcuts working even though the Avalonia WASM
// canvas captures keyboard input. Capture phase + programmatic reload bypass
// the canvas's preventDefault, so F5 / Ctrl+R / Cmd+R refresh the page.
window.addEventListener('keydown', (e) => {
    const isReload = e.key === 'F5' || ((e.ctrlKey || e.metaKey) && (e.key === 'r' || e.key === 'R'));
    if (isReload) {
        e.stopImmediatePropagation();
        location.reload();
    }
}, { capture: true });

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();

await dotnetRuntime.runMain(config.mainAssemblyName, [window.location.search, window.location.origin]);
