# Getting Started with YAAT Flight Strips

This guide walks you through your first session with **YAAT Flight Strips**, the standalone flight-strips app that runs alongside CRC during YAAT training.

## What is this?

YAAT Flight Strips is a desktop app that displays **flight progress strips** during a YAAT training session. It mirrors the CRC vStrips experience: you see the same departure and arrival strips, half-strips, separators, and blanks a real vStrips client would, and you can move, annotate, and delete them with drag/drop or keyboard shortcuts.

You run it on your own machine, alongside CRC, while an instructor hosts a YAAT server and runs scenarios. Strip state lives on the server — every action you take is broadcast to everyone in the room, including any real CRC + vStrips clients connected.

**You only need this if your instructor is using YAAT.** Real-world or pure-VATSIM controlling uses CRC's own vStrips.

## Step 1: Install

### Option A: Prebuilt release (recommended)

Download an installer or portable archive from the [YAAT Releases page](https://github.com/leftos/yaat/releases/latest). Look for the `YaatVStrips-*` assets (not `YaatClient-*` — that's the full instructor trainer).

| Platform | Installer | Portable |
|----------|-----------|----------|
| Windows  | `YaatVStrips-<ver>-win-Setup.exe` | `YaatVStrips-<ver>-win-Portable.zip` |
| Linux    | `YaatVStrips-<ver>-linux.AppImage` | (the AppImage is itself portable) |
| macOS    | `YaatVStrips-<ver>-osx-Setup.pkg` | `YaatVStrips-<ver>-osx-Portable.zip` |

The **installer** registers the app with your OS and keeps itself up to date in the background. The **portable** unzips to a folder you can run from a USB stick or a locked-down machine — it doesn't auto-update.

**Windows installer:** double-click the `.exe`. If SmartScreen warns the installer is unsigned, click **More info → Run anyway**. The app appears in the Start menu when the install finishes.

**Linux AppImage:** `chmod +x YaatVStrips-<ver>-linux.AppImage && ./YaatVStrips-<ver>-linux.AppImage`. You may need `libfontconfig1` and `libfreetype6` for text rendering (`sudo apt install libfontconfig1 libfreetype6 fonts-dejavu-core` on Debian/Ubuntu).

**macOS package:** double-click the `.pkg`. Gatekeeper may block the first launch — right-click the installed app → **Open** → **Open** again to allow it.

**Portable archives:** extract into an empty folder, then double-click the executable. Don't extract into Downloads — the app expects all its sibling files in the same folder.

### Option B: Build from source

If you have the YAAT repo and the .NET 10 SDK:

```bash
dotnet run --project tools/Yaat.VStrips
```

## Step 2: Configure your identity

The first time you launch, open **File → Connect…**. The connect dialog asks for:

| Field | What to enter |
|-------|---------------|
| **VATSIM CID** | Your VATSIM ID number — same one you use for CRC |
| **Initials** | Any two letters (e.g., your initials — "JE", "AB") |
| **ARTCC ID** | The facility you're training (e.g., `ZOA`, `ZLA`, `ZNY`) |

These are required and persist between sessions. If you also run the full Yaat.Client trainer, the standalone keeps a separate identity store — set the same values in both for the auto-join behavior in Step 4 to work.

## Step 3: Connect to the server

In the same connect dialog, enter the **server URL** your instructor gave you. Examples:

- `https://yaat1.leftos.dev` for the public hosted server
- `http://192.168.1.10:5000` for a server on your instructor's LAN
- `http://localhost:5000` if you're running yaat-server on your own machine

Click **Connect**. The app remembers servers — pick one from the dropdown next time. The status bar at the bottom shows `Connected to <url>` once the SignalR handshake finishes.

> **Tip:** if you launch the app from a shortcut, you can pass `--autoconnect <name-or-url>` to skip the connect dialog and reconnect to the same server every time. The app retries for ~60 seconds so it survives launching before the server is ready.

## Step 4: Join a room

If your CID matches an instructor's room, the app **auto-joins** as soon as you connect — no further action needed. The status bar updates to `Joined room — <scenario name>` and the strip bays appear.

If auto-join doesn't fire (you used a different CID, the instructor doesn't have your CID, or you connected before the room existed):

1. **Room → Join Room…** opens the room picker.
2. The list shows every active room on the server with the creator's initials, ARTCC, scenario name, and aircraft count.
3. Pick one and click **Join** (or double-click).

The bays populate immediately if a scenario is loaded; otherwise they appear as soon as the instructor loads one.

## Step 5: Configure CRC (optional)

The **Tools → Configure CRC Environments…** menu adds YAAT server entries to CRC's `DevEnvironments.json`. After running it, restart CRC and pick **YAAT1** or **YAAT Local** from CRC's environment selector — that points CRC at the same server you're connected to here. CRC then connects with your VATSIM credentials and joins the same training room.

This is the same setup the instructor's `Setup-CrcEnvironment.ps1` script performs; the menu item is offered here as a convenience for trainees who don't have the YAAT repo. It only modifies CRC's config file — it does not install CRC, change your VATSIM credentials, or alter any other setting.

## Step 6: Use the strips

Once you're in a room with strips, you can:

- **Click a bay button** in the header to switch between bays (Ground, Local, …).
- **Drag a strip** from one rack to another, or onto the trash zone in the header to delete it.
- **Open the printer modal** with **Tab** to request a strip by callsign or print a blank.
- **Click an annotation cell** on a full strip to write in it; type `?` for a checkmark.

A few keyboard shortcuts to learn first:

| Key | Action |
|-----|--------|
| Tab | Toggle the printer panel |
| PageUp / PageDown | Previous / next bay |
| Arrow keys | Move selection between strips |
| Delete | Delete the selected strip |
| Esc | Deselect, then toggle the printer panel |
| Ctrl+1..9 | Edit annotation box on the selected full strip |

The full reference — every shortcut, the right-click menus, the printer modal, separators, blanks, half-strips — is in [USER_GUIDE.md](USER_GUIDE.md).

## Troubleshooting

| Problem | Fix |
|---------|-----|
| **No strips appear after joining a room** | Wait for the instructor to load a scenario. The bay buttons remain empty until then. If the instructor confirms a scenario is loaded and you still see nothing, check that your ARTCC ID in the connect dialog matches the scenario's facility. |
| **`Failed to list rooms` in status bar** | Server URL is wrong, or the server is down. Verify the URL with your instructor and that you can reach it from a browser. |
| **CRC doesn't see the YAAT server** | Run **Tools → Configure CRC Environments…** and restart CRC. If the menu says CRC is not installed, install CRC first; if it says YAAT entries are already configured, just restart CRC. |
| **App won't open / immediately crashes on Linux** | Install the font packages listed in [Step 1](#option-a-prebuilt-release-recommended). |
| **Bay buttons are missing for a facility I expect** | The accessible bay set comes from the ARTCC config the server is using. If a bay is genuinely missing for your position, check with the instructor — it may need to be added to the facility config. |

## Next

- [USER_GUIDE.md](USER_GUIDE.md) — full UI and shortcut reference
- [../../COMMANDS.md#strip--data-operations](../../COMMANDS.md#strip--data-operations) — the underlying canonical commands every UI action emits, useful when you want to script a flow
