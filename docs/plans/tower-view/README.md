# Built-in Tower View

A 3D tower cab view inside the YAAT desktop client, built on Tower Cab 3D's code, with no separate Tower Cab 3D install. Linear: YAAT-355. Branch: `feat/tower-view` (yaat); the library work lives in the `towercab-3d` repo and is requested through its inbox.

## What exists

- **Tower Cab 3D** (`leftos/towercab-3d`, `X:/dev/towercab-3d`): a Tauri app whose frontend is Vite/TypeScript on CesiumJS with a Babylon.js overlay. Its frontend already runs in a plain browser in "remote mode" (`src/renderer/utils/remoteMode.ts`), answering host calls through `/api/*` that its Rust side serves (`src-tauri/src/server.rs`, `docs/remote-access-architecture.md`). Every traffic source ends at one seam, `useAircraftTimelineStore.addObservationBatch` (`stores/aircraftTimelineStore.ts`), fed `AircraftObservation`s (`types/aircraft-timeline.ts:18-58`). Tower eye positions come from its `tower-positions` data and tower mods.
- **YAAT** has no web view and nothing 3D. The client already receives every aircraft over `/hubs/training` (`AircraftDto`, `src/Yaat.Client.Core/Services/ServerConnection.cs`), without pitch or bank. Views follow the Ground View instance pattern (`MainViewModel.ViewInstances.cs`, `docs/client-mainviewmodel.md`).
- **YAAT-266** (external Tower Cab 3D joining a room without CRC) is separate: it serves students running the real app; this view serves YAAT users.

## Rulings (owner, 2026-10-05)

- **Renderer:** reuse Tower Cab 3D's frontend, not a new renderer.
- **Libraries:** as much of what YAAT and Tower Cab 3D share as possible is refactored into reusable libraries, consumed by both the Tauri app and YAAT's view, rather than YAAT embedding the whole app behind shims.
- **Host:** an embedded web view inside YAAT (a Tower View tab and pop-out), not a browser window.
- **Platforms:** Windows first (WebView2, which Tower Cab 3D already runs on through Tauri); macOS (WKWebView) and Linux (WebKitGTK) are a later item.
- **Licence:** Tower Cab 3D's frontend is relicensed MIT (the owner is its only contributor besides dependabot). The 39 built-in aircraft models come from `Flightradar24/fr24-3d-models` and stay GPL-2.0 data, credited in Tower Cab 3D's `CREDITS.md`; they ship inside the downloaded bundle, never in YAAT's installer.
- **Terrain and imagery:** the user's own Cesium ion token, kept in YAAT preferences; without one, a non-ion terrain/imagery fallback.
- **Traffic:** the YAAT client feeds the page from its own aircraft stream through the web view's JS bridge, in Tower Cab 3D's observation shape. No server change. Attitude is derived in the library from vertical rate and track, as Tower Cab 3D does for VATSIM.
- **Delivery:** the view's web bundle is downloaded on first open into YAAT's cache (like the speech models), versioned against the client.
- **Scope:** the view, tower mods (tower models and positions), and MSFS models (the user's FSLTL/AIG models through Tower Cab 3D's converter). No VATSIM, vNAS or RealTraffic sources, no insets.

## Shape (proposed, to agree with the towercab-3d session)

Libraries in `towercab-3d` (MIT unless noted):

1. **Core**: the observation model, the aircraft timeline store and its `addObservationBatch` seam, interpolation and motion (attitude derivation, gear and phase detection), type-to-model matching.
2. **Scene**: the Cesium viewer and Babylon overlay, the tower eye and camera, tower-position and tower-mod rendering, terrain and imagery providers (ion with the fallback).
3. **Host interface**: one TypeScript interface for everything the Rust side answers today: settings, mods and tower positions, model files and asset URLs, the MSFS model catalog and conversion, proxied fetches, the observation feed. Three hosts implement it: the Tauri app, the existing remote-browser mode, and YAAT's bridge.
4. **Model converter**: the FSLTL/AIG converter sidecar (`build:converter`) published as a standalone versioned download a host can fetch, not only a Tauri sidecar.

YAAT side (`feat/tower-view`):

- A host page bundle built from the libraries plus the YAAT host adapter, published as the versioned download.
- A client project for the view: the web view control, the JS bridge (observations from `AircraftUpdated`, settings and token, the mods and models folders under `YaatPaths`, folder pickers, converter runs), the bundle downloader and cache, and the Tower View tab and pop-out (`WindowGeometryHelper`).

## Build items

1. **Web view spike (yaat):** pick the Avalonia 12 web view control for Windows (WebView2), host a static page, prove the JS bridge both ways and WebGL2, and measure memory; record the choice and its macOS/Linux story.
2. **Library split (towercab-3d, inbox request):** core, scene and host-interface packages; the Tauri app and remote mode move onto them unchanged in behaviour; MIT relicence of the frontend; FR24 credits.
3. **YAAT host adapter and page bundle (towercab-3d or yaat, decided with item 2):** the bridge-backed host and the published, versioned bundle.
4. **Client view (yaat):** the view project, observation feed, settings (ion token, fallback), bundle download, Tower View tab and pop-out, USER_GUIDE section.
5. **Mods and MSFS models (yaat + towercab-3d):** mods folder, tower positions, converter download and runs through the bridge.
6. **macOS and Linux (yaat):** WKWebView and WebKitGTK, after item 4.

## Open questions

- Which Avalonia 12 web view control (item 1 decides).
- Where the YAAT host adapter and page live: towercab-3d (one more host beside Tauri and remote) or yaat (consuming published packages). Item 2's session proposes.
- How the packages are published (npm registry, GitHub packages, or a git dependency) and versioned against the client.
