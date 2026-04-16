# YAAT Flight Strips (Standalone)

Drop-in replacement for vStrips that students and RPOs run alongside CRC during
YAAT training sessions. Connects to yaat-server via SignalR, joins an active
training room, and renders the same flight-strip bays, drag/drop, and keyboard
shortcuts as the embedded Strips tab inside the full Yaat.Client trainer — without
the speech pipeline, radar, ground view, or aircraft list overhead.

Built as a stopgap while YAAT awaits vNAS team approval for the official vStrips
website integration.

## Quick start

```bash
dotnet run --project tools/Yaat.VStrips
```

1. **File → Connect** — enter the yaat-server URL (e.g. `http://localhost:5000`).
2. **Room → Join Room** — pick an active training room from the list.
3. The strip bays populate automatically once a scenario is loaded.
4. Drag strips between racks, drop on the trash zone to delete, use keyboard
   shortcuts for fast navigation.

## Architecture

- References `Yaat.Client.Core` only — no `Yaat.Client`, no LM-Kit, no Velopack.
- `StandaloneViewModel` (~200 lines) owns a `ServerConnection`, `VStripsViewModel`,
  and `UserPreferences`. No speech, ground, radar, terminal, or aircraft grid.
- `RoomPickerWindow` lists active rooms via `GetActiveRoomsAsync` and joins on
  selection.
- Self-contained publish is ~109 MB (Avalonia + .NET runtime + SkiaSharp).

## Keyboard shortcuts

| Key | Action |
|-----|--------|
| PageDown / PageUp | Next / previous bay |
| Tab / Esc | Toggle printer panel |
| Del / Backspace | Delete selected strip |
| Ctrl+Alt+1..9 | Jump to bay by ordinal |
| Shift+click | Toggle offset |
| Alt+click | Delete strip |

## Build & publish

```bash
# Debug build
dotnet build tools/Yaat.VStrips

# Self-contained Windows publish
dotnet publish tools/Yaat.VStrips -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o .tmp/vstrips-publish
```

The release workflow (`release.yml`) publishes and Velopack-packs Yaat.VStrips on
Windows, Linux, and macOS alongside Yaat.Client. Pack ID: `YaatVStrips`.
