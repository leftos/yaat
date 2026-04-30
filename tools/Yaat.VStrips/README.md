# YAAT Flight Strips (Standalone)

Standalone desktop app that mirrors CRC's vStrips for YAAT training sessions. Connects to a [yaat-server](https://github.com/leftos/yaat-server) instance via SignalR, joins a room, and renders the same flight-strip bays, drag/drop, and keyboard shortcuts as the embedded Strips tab in the full Yaat.Client trainer — without the speech pipeline, radar, ground view, terminal, or aircraft list overhead.

Designed for trainees who want a vStrips-equivalent on their own monitor while they work scopes in CRC, and for instructors who prefer running strips in a separate window from the rest of YAAT.

## Documentation

- **[GETTING_STARTED.md](GETTING_STARTED.md)** — install, connect, join a room, see your first strips.
- **[USER_GUIDE.md](USER_GUIDE.md)** — full reference for the UI, drag/drop, shortcuts, and the printer modal.
- **[../../USER_GUIDE.md#flight-strips](../../USER_GUIDE.md#flight-strips)** — the same UI documented from the main YAAT trainer's perspective.
- **[../../COMMANDS.md#strip--data-operations](../../COMMANDS.md#strip--data-operations)** — the canonical command surface every UI action ultimately emits.

## Download

Pre-built installers and portable archives are published on the [YAAT Releases page](https://github.com/leftos/yaat/releases/latest). Look for the `YaatVStrips-*` assets:

| Platform | Installer | Portable |
|----------|-----------|----------|
| Windows  | `YaatVStrips-<ver>-win-Setup.exe` — auto-updates in the background | `YaatVStrips-<ver>-win-Portable.zip` |
| Linux    | `YaatVStrips-<ver>-linux.AppImage` | (the AppImage is itself portable) |
| macOS    | `YaatVStrips-<ver>-osx-Setup.pkg` | `YaatVStrips-<ver>-osx-Portable.zip` |

Installers register the app with the OS and self-update via [Velopack](https://velopack.io/). Portable archives unzip to a folder and run from there with no install. The vStrips installer is independent of the main YAAT installer — you can run both side-by-side without conflict (separate appdata, separate Velopack channel).

For end-user instructions, send users straight to [GETTING_STARTED.md](GETTING_STARTED.md).

## Run from source

```bash
dotnet run --project tools/Yaat.VStrips
```

By default the app opens to a disconnected state. To skip the connect dialog, pass a saved server name or a URL:

```bash
dotnet run --project tools/Yaat.VStrips -- --autoconnect http://localhost:5000
dotnet run --project tools/Yaat.VStrips -- --autoconnect YAAT1
```

The `--autoconnect` arg matches first against entries in `preferences.json`'s saved-servers list (case-insensitive name lookup), then falls back to treating the value as a literal URL. Connection retries up to 30 times at 2-second intervals so the app survives launching alongside the server.

## Build & publish

```bash
# Debug build
dotnet build tools/Yaat.VStrips

# Self-contained Windows publish
dotnet publish tools/Yaat.VStrips -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o .tmp/vstrips-publish
```

The release workflow (`.github/workflows/release.yml`) publishes and Velopack-packs Yaat.VStrips on Windows, Linux, and macOS in parallel with Yaat.Client. Velopack pack ID: `YaatVStrips`. Per-platform channels: `vstrips-win`, `vstrips-osx`, `vstrips-linux`.

## Architecture

- References `Yaat.Client.Core` only — no `Yaat.Client`, no LM-Kit, no Whisper, no Velopack-for-trainer.
- `Program.cs` calls `YaatPaths.Initialize("yaat-vstrips")` so all per-user state (preferences, logs, geometry) lives in `%LOCALAPPDATA%\yaat-vstrips\` (Windows), `~/Library/Application Support/yaat-vstrips/` (macOS), or `~/.local/share/yaat-vstrips/` (Linux). Independent of the main client's `yaat` namespace.
- `StandaloneViewModel` (~350 lines) owns a `ServerConnection`, a single `VStripsViewModel`, and `UserPreferences`. The strip view itself is the same `VStripsView` Avalonia control the embedded tab uses.
- `RoomPickerWindow` lists active rooms via `GetActiveRoomsAsync` and joins on selection. Auto-joins the room bound to the user's CID on connect, and reacts to the server's `RoomAvailableForCid` push when a sibling CRC client gets pulled in.
- `Tools → Configure CRC Environments…` runs `CrcConfigService` to add YAAT entries to CRC's `DevEnvironments.json` so the user can point CRC at the same server.
- One window per process. There is no multi-facility tabbing — switch facilities in place via the facility button in the strip view's header.

## Limitations vs the embedded tab

- **One facility at a time** per window. The full trainer can open multiple facility tabs simultaneously; the standalone uses the in-place facility switcher.
- No aircraft grid, ground view, radar view, terminal, scenario controls, or weather UI. If you need those, run Yaat.Client instead.
- No speech pipeline. Push-to-talk and the local LLM are bundled with the main client only.
