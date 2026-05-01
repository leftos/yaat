# Yaat.VStrips in a CRC browser tab via Avalonia.Browser

## Context

Real CRC has a browser-tab feature (WebView2-based, `Vatsim.Nas.Crc.Ui.Displays.Browser`) that lets controllers load arbitrary URLs inside a tab — that's where CRC's own web vStrips lives. YAAT today ships a standalone Avalonia desktop binary (`tools/Yaat.VStrips/`) that connects to yaat-server and renders the same `StripItemDto` payloads as the embedded Strips tab in `Yaat.Client`, but it can only open as a separate desktop window. Controllers who already use CRC browser tabs would prefer YAAT's strip UI in that same tab so they don't manage a third floating window.

The original notes file (`docs/plans/open-issues/vstrips-standalone-browser-url.md`) lays out three options (a/b/c) and asks for honest scoping. After exploration:

- **CRC's BrowserDisplay accepts arbitrary `http(s)://` URLs** including `http://localhost:NNNN`. There is **no postMessage/IPC bridge** between CRC and the embedded page — the page is a sandboxed WebView2 instance that must talk to its own backend independently.
- **The standalone is at `tools/Yaat.VStrips/`** (not `src/`, as the notes file states). It references only `Yaat.Client.Core` and reuses `VStripsView` + `VStripsViewModel` directly.
- **No HTML/web assets exist anywhere in the repo.** No wwwroot on yaat-server, no SPA tooling, no static-file middleware. `Yaat.Server/Program.cs` currently maps only SignalR + a few `/api/*` endpoints.
- **`StripItemDto` and `FlightStripsStateDto` are pure POCO** (string/bool/enum/arrays) and serialize cleanly over SignalR JSON. The hub method surface (`FindRoomForMyCid`, `JoinRoom`, `SendCommand`, `StripItemsChanged`/`FlightStripsStateChanged` broadcasts) is already what a web client would use.

**Decisions confirmed with user:**
- Day-1 fidelity: **full parity** (drag/drop, all four inline-edit modes, printer modal).
- Tech approach: **Avalonia.Browser (WASM)** — compile the existing `VStripsView` AXAML to WebAssembly rather than hand-write a TypeScript SPA.
- Hosting: **yaat-server hosts `/vstrips`** — single canonical install; standalone window keeps existing role.

This direction reuses the Avalonia view + view-model code rather than rewriting it in HTML/JS, but introduces Avalonia.Browser as a new platform target with real risks (SignalR client compatibility, drag/drop fidelity, bundle size, custom-Canvas drawing performance). The plan front-loads a spike to retire those unknowns before committing to the full port.

## Approach

A new project `tools/Yaat.VStrips.Web/` (Avalonia.Browser host) renders the same `VStripsView` in a browser. yaat-server's wwwroot serves the WASM bundle at `/vstrips/`. Identity (CID, initials, ARTCC, server) flows in via URL query params on the CRC tab side, since there's no postMessage bridge. The standalone desktop `Yaat.VStrips` and the embedded Strips tab in `Yaat.Client` keep working unchanged.

The biggest risk is that `Yaat.Client.Core` currently targets `net10.0` only and pulls in `Avalonia.Desktop` + `Velopack` (both desktop-only). The plan assumes a small refactor: extract a new `src/Yaat.Client.Strips/` project containing the WASM-portable subset (view, view-models, DTO types, an abstracted preferences/storage interface, and a thin SignalR wrapper). Both `Yaat.Client.Core` and the new web host reference `Yaat.Client.Strips`. No existing call site breaks.

## Plan

### Phase 0 — Spike (DONE — 2026-05-01)

Goal: retire the unknowns before committing to a 4-6 week effort. **Outcome: green for Avalonia.Browser. Proceed to Phase 1, but with a lighter refactor than originally planned (see "Spike findings" below).**

- [x] Create `tools/Yaat.VStrips.Web/` with Avalonia.Browser AppBuilder targeting `net10.0-browser`. References Avalonia 11.3.13 + Avalonia.Browser. Velopack and Avalonia.Desktop are NOT excluded — they tree-shake out of the WASM link.
- [x] Verify `Microsoft.AspNetCore.SignalR.Client` 10.0.3 builds and connects under `net10.0-browser`. **Compiles cleanly, no JS-interop fallback needed.** Runtime connect not yet tested but the API surface is intact under WASM.
- [x] Add the project to `yaat.slnx`; install the `wasm-tools` workload (auto-resolves via `dotnet workload restore`).
- [x] Render the real `VStripsView` against a stub `VStripsViewModel` seeded with two `StripItemDto` (UAL238 + AAL2839). Custom barcode drawing, route-fit truncation, monospace font, AXAML resources (`StripCellBrush`, `MonoFont`, etc.) all render via Skia software canvas in headless chromium.
- [ ] Validate drag/drop with two fake racks — **deferred**. Headless chromium can't reliably emulate Avalonia's pointer-event drag model; needs interactive validation in real CRC WebView2 or full chromium. Move to Phase 2 verification.
- [x] Measure bundle size (release publish):
  - **Raw wwwroot: 35 MB**
  - **Brotli-compressed total (what the browser downloads): 6.2 MB**
  - Raw .wasm files: 17.5 MB
  - Brotli .wasm: 6.0 MB (dominated by `dotnet.native.wasm` at 2.4 MB)
  - Page load + initial render in headless chromium: ~228ms load, ~1s to first paint of strip view
- [x] Smoke harness (`tools/Yaat.VStrips.Web/test/smoke.mjs`) — boots `dotnet run`, drives chromium via Playwright, captures screenshot + network log. Tree-kill cleanup (`taskkill /F /T /PID`) prevents orphan WasmAppHost / chromium processes. Runs end-to-end in ~62s.

### Spike findings (informs Phase 1+)

1. **No `Yaat.Client.Strips` extraction is needed.** The web project can reference `Yaat.Client.Core` directly. The Avalonia.Desktop + Win32 automation interop transitive types produce harmless `WASM0001/WASM0061/WASM0062` warnings at link time but don't fail the build, and the linker tree-shakes them out of the final bundle. **This drops Phase 1 from "extract a multi-targeted strips project" to "abstract a thin transport interface so the web host can inject a JS-shim ServerConnection if needed".**
2. **WASM render path:** Avalonia.Browser tries WebGPU (mode 3) → WebGL2 (mode 2) → software canvas in order. Headless chromium's headless-shell only supports software canvas; full chromium and CRC's WebView2 (Edge) get hardware acceleration.
3. **Bundle size at 6 MB Brotli is well within "acceptable for a CRC tab"** — typical SPA bundles are 1–3 MB so we're 2–3x bigger but still under 10 MB cold-start. AOT compilation could shrink further if needed; the spike used `RunAOTCompilation=false`.
4. **SignalR.Client compiles, but runtime WebSocket transport in WASM still needs validation.** Defer to Phase 2 (when we wire the real transport against a running yaat-server).
5. **Resource declarations (font, brushes) live in `Yaat.Client/App.axaml`.** The web host's `App.axaml` must mirror them or `FlightStripControl` panels render blank.

### Phase 1 — Disconnect lockdown + Open-in-Browser (DONE — 2026-05-01)

Side asks raised during the spike, both shipped together:

- [x] `VStripsViewModel.IsConnected` ObservableProperty + `OnConnectionLost()` clears strip lookup, every rack's `Strips` collection, and the printer queue while leaving the bay shells and facility name intact. Bay layout still visible; live data gone.
- [x] `ServerConnection` gained a new `Connected` event (fires once on first successful handshake; `Reconnected` only fires after a transport drop, which left initial connect in a no-op gap). VStripsViewModel subscribes to `Connected`, `Closed`, `Reconnecting`, `Reconnected` and flips `IsConnected` accordingly.
- [x] `VStripsView.axaml` shows a red "DISCONNECTED — read-only until reconnect" banner pinned to the top of the workspace whenever `!IsConnected`. Banner is hit-test-invisible so it doesn't intercept input.
- [x] Code-behind input gates: `OnStripPointerPressed` and `OnKeyDown` early-return when `!vm.IsConnected` — drag/drop, right-click context menus, and keyboard shortcuts all noop when disconnected.
- [x] Printer modal (`<Border IsVisible="Printer.IsOpen">`) gets `IsEnabled="{Binding IsConnected}"` — buttons still visible but greyed and unclickable. Printer toggle button itself is `IsEnabled` gated too so a disconnected user can't open the modal.
- [x] `tools/Yaat.VStrips/StandaloneViewModel` tracks `ConnectedServerUrl` (set in `AttemptConnectAsync`, cleared on disconnect/close) and exposes a new `OpenInBrowserAsync` `RelayCommand` that builds `{server}/vstrips/?cid=...&initials=...&artcc=...&room=...` and shells out via `Process.Start(UseShellExecute=true)`.
- [x] `tools/Yaat.VStrips/MainWindow.axaml` adds **Tools → Open in Browser** bound to the command, gated on `IsConnected`.
- [x] Regression coverage: `Disconnect_ClearsRackStripsAndPrinterButKeepsBayLayout` and `Disconnect_BannerVisibleWhenOfflineAndHidesOnReconnect` (`tests/Yaat.Client.UI.Tests/Views/VStripsViewInteractionTests.cs`). All 19 strip view-interaction tests pass.

### Phase 2 — Web host project + transport abstraction

### Phase 2 — Web host wires up the real transport (DONE — 2026-05-01)

- [x] Live SignalR connect from WASM. `MainView` parses `window.location.search` for cid/initials/artcc/server/room; when identity is present, calls `ServerConnection.ConnectAsync` with the resolved server URL (defaults to `window.location.origin`, threaded in from `main.js`'s argv since `HubConnectionBuilder.WithUrl` rejects relative URLs). Verified end-to-end against `http://localhost:5130`: SignalR negotiate completes in ~300 ms, `Connected` fires ~1 s after page load (`tools/Yaat.VStrips.Web/test/live.mjs`).
- [x] Identity bootstrap from URL query string. Sticks the WASM client in "no room" if `?cid=` is omitted; auto-joins via `FindRoomForMyCidAsync` when CID is present, mirroring `StandaloneViewModel.TryAutoJoinForCidAsync`. Listens to `RoomAvailableForCid` to pick up a sibling CRC's room as soon as it becomes available.
- [x] Storage: passing `preferences: null` to `VStripsViewModel` is sufficient for the spike — the VM already handles the null path. A `localStorage`-backed `UserPreferences` is deferred until users actually need persisted zoom / last-facility state.
- [x] Browser-friendly logging: `AppLog.InitializeForBrowser()` wires `ConsoleLineLoggerProvider` (writes via `Console.WriteLine`, which Mono-WASM forwards to DevTools). All `ServerConnection` and `MainView` logs surface in the browser console.
- [x] Suppress favicon noise via `<link rel="icon" href="data:,">` in `index.html`.
- [ ] Validate drag/drop in real chromium and CRC WebView2 (Avalonia's pointer-driven drag model differs from DOM drag-and-drop; headless can't reliably emulate). Defer to interactive test.
- [ ] Validate inline edits (annotation, half-strip cell, separator label) in WASM. The shared `InlineTextEditPopup` needs Avalonia TextBox focus + IME + Tab/Enter/Escape working under browser-WASM. Defer to interactive test.

### Phase 3 — Server hosting (DONE — 2026-05-01)

- [x] `app.UseStaticFiles()` + `app.MapFallbackToFile("vstrips/{**path:nonfile}", "vstrips/index.html")` in yaat-server's `Program.cs`. Same-origin hosting keeps the SignalR WebSocket handshake CORS-free in CRC's WebView2 tab.
- [x] Register WASM-runtime MIME types explicitly: `.wasm` (application/wasm), `.dat` / `.blat` / `.dll` / `.pdb` / `.br` / `.gz` (application/octet-stream). Without this, Mono fails initialization on the first ICU `.dat` fetch.
- [x] MSBuild `CopyToServerWwwroot` `AfterTargets="Publish"` mirrors `tools/Yaat.VStrips.Web/bin/.../publish/wwwroot/` into `../../yaat-server/src/Yaat.Server/wwwroot/vstrips/`. Self-skips when the sibling checkout isn't present (CI-friendly). Override via `-p:YaatServer=...`.
- [x] yaat-server gitignores `src/Yaat.Server/wwwroot/vstrips/` — it's a build artifact, not source.
- [ ] Brotli/gzip auto-negotiation of the precompressed siblings. `UseStaticFiles` doesn't do this on its own; would need either `MapStaticAssets` (NET 9+) or custom middleware. Bundle currently transfers as raw 17 MB instead of the 6 MB Brotli total. Optimization deferred.
- [ ] Update `docs/architecture.md` to reflect the new project + the wwwroot pipeline.

### Phase 4 — Distribution and docs

- [ ] Optional: extend `CrcConfigService` (used by the Tools menu in standalone) to also seed a `BrowserDisplaySettings.InitialUrl` entry in CRC's settings, pointing at `http://localhost:5130/vstrips/?cid=...`. The standalone already has **Tools → Open in Browser** which opens the same URL in the user's default browser; the CRC seed is a separate convenience.
- [ ] `tools/Yaat.VStrips/USER_GUIDE.md` — add a section "Run as a CRC browser tab" describing how to paste the URL into CRC's BrowserDisplay settings.
- [ ] Top-level `USER_GUIDE.md` flight-strips section — link to the web variant.
- [ ] `docs/flight-strips.md` — add a "Web client" subsection covering the new transport implementation and identity flow.
- [ ] CI: extend `.github/workflows/release.yml` to publish the WASM bundle alongside the existing desktop assets. No Velopack channel needed (it's served, not installed).

## Anchors (files to read or modify)

- `tools/Yaat.VStrips/Yaat.VStrips.csproj` — pattern for a csproj that references only `Yaat.Client.Core`.
- `tools/Yaat.VStrips/StandaloneViewModel.cs:59-105` — connection lifecycle, `RoomAvailableForCid` auto-join, `ScenarioLoaded`/`ScenarioUnloaded` reaction. The web `App` should mirror this.
- `src/Yaat.Client.Core/Views/VStrips/` — the strip view files that will move to `Yaat.Client.Strips`.
- `src/Yaat.Client.Core/ViewModels/VStripsViewModel.cs` — the view-model that will move with them.
- `src/Yaat.Client.Core/Services/ServerConnection.cs` — desktop `IStripsTransport` implementation will live here (or at least call into it).
- `src/Yaat.Client.Core/Services/StripDtos.cs` — `StripItemDto`, `FlightStripsStateDto`, `FlightStripsConfigDto` (move to `Yaat.Client.Strips`).
- `..\yaat-server\src\Yaat.Server\Program.cs` — endpoint mappings; static files go here.
- `..\yaat-server\src\Yaat.Server\Hubs\TrainingHub.cs` — hub surface unchanged; document which methods the web client uses.
- `..\yaat-server\docs\crc-decompiled\Vatsim.Nas.Crc.Ui.Displays.Browser\BrowserDisplay.cs` and `BrowserDisplaySettings.cs` — confirms WebView2, arbitrary URL, no IPC bridge.
- `docs/flight-strips.md` — update with "Web client" section.
- `docs/architecture.md` — add the new project tree.

## Verification

Spike (Phase 0):
- Run `dotnet publish tools/Yaat.VStrips.Web -c Release` and `dotnet run --project ..\yaat-server\src\Yaat.Server`. Open CRC, configure a BrowserDisplay tab pointing at `http://localhost:5000/vstrips/`. The stub view should render with the test strip visible.
- Manually drag the test strip between two racks and confirm it lands without phantom artifacts. This is the gate test.

Full (Phase 1-4):
- Run yaat-server (`YAAT1` profile). Run the web client in Chrome at `http://localhost:5000/vstrips/?cid=12345&initials=AJ&artcc=ZOA&server=http%3A//localhost%3A5000`. Verify auto-join works for an active room.
- Compare side-by-side with the desktop standalone (`dotnet run --project tools/Yaat.VStrips`). Drag, drop, annotate, half-strip cycle, separator edit, printer toggle — all four inline-edit modes — should behave identically.
- Embedded Strips tab in `Yaat.Client` still renders strips post-refactor (no regression in the existing UI).
- `pwsh tools/test-all.ps1` green (cross-repo, catches `Yaat.Sim` signature breakage).
- Load the URL inside CRC's actual BrowserDisplay tab (not Chrome). Verify rendering, input, drag/drop, and that performance with ~50 active strips is acceptable.
- `dotnet build -p:TreatWarningsAsErrors=true` clean across both repos.

## Out of scope

- Any redesign of strip rendering, layout, or commands. Web client must match desktop pixel-for-pixel.
- Authoring an HTML/JS/TypeScript fallback. The spike either succeeds with Avalonia.Browser or we revisit tech choice.
- Multi-room / multi-facility tabbing in the web client. One window = one facility, same as the standalone.
- Speech pipeline. The web client never gets push-to-talk.
- Replacing the desktop standalone. Both ship; the standalone is still useful for users who don't want CRC running.
- Auth tokens / JWT. Identity flow uses CID + initials in the URL just like the standalone — no new auth surface.
