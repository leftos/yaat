# YAAT Flight Strips — User Guide

This is the full reference for **YAAT Flight Strips**, the standalone flight-strips desktop app for YAAT training. For first-time setup (download, identity, server URL), read [GETTING_STARTED.md](GETTING_STARTED.md) first.

> The strip view itself is the same Avalonia control the embedded **Strips** tab in the full Yaat.Client trainer uses — the documentation in [the main YAAT user guide's Flight Strips section](../../USER_GUIDE.md#flight-strips) applies almost verbatim. This guide focuses on the standalone-specific menus, lifecycle, and quirks, and copies the UI reference here so trainees don't have to bounce between repos.

## Table of Contents

- [What is this?](#what-is-this)
- [Window layout](#window-layout)
- [Connecting](#connecting)
- [Rooms](#rooms)
- [Strip view](#strip-view)
  - [Header bar](#header-bar)
  - [Bays and racks](#bays-and-racks)
  - [Strip types](#strip-types)
  - [Selection and drag-drop](#selection-and-drag-drop)
  - [Right-click menus](#right-click-menus)
  - [Inline editing](#inline-editing)
  - [Printer modal](#printer-modal)
  - [Auto-printing](#auto-printing)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Command surface](#command-surface)
- [CRC integration](#crc-integration)
- [Updates](#updates)
- [Preferences and persistence](#preferences-and-persistence)
- [Limitations](#limitations)
- [Troubleshooting](#troubleshooting)

## What is this?

A standalone desktop app that connects to a [yaat-server](https://github.com/leftos/yaat-server) instance, joins a training room, and renders the room's flight-strip bays. Strip state is owned by the server and broadcast to every client in the room — your moves, deletes, and annotations show up on every other client (including any real CRC + vStrips clients) in real time.

It is intentionally minimal: no aircraft grid, no ground/radar view, no terminal, no scenario controls, no speech pipeline. If you need any of those, run the full **Yaat.Client** trainer instead.

## Window layout

```
┌─ Menu bar ────────────────────────────────────────────────────┐
│ File  Room  Tools                                             │
├─ Strip view (header) ─────────────────────────────────────────┤
│ [OAK] [Ground 1] [Ground 2] [Local] …  − 80% +  🗑  [Printer] │
├─ Strip view (racks) ──────────────────────────────────────────┤
│   ┌───────────┬───────────┬───────────┐                       │
│   │           │           │           │                       │
│   │  rack 1   │  rack 2   │  rack 3   │   (bottom-up FIFO)    │
│   │           │           │           │                       │
│   │  ▣ strip  │  ▣ strip  │  ▣ strip  │                       │
│   └───────────┴───────────┴───────────┘                       │
├─ Status bar (bottom) ─────────────────────────────────────────┤
│ Joined room — KOAK Pattern Work        (ZOA) AB's Room        │
└───────────────────────────────────────────────────────────────┘
```

The header is identical to the embedded Strips tab in the full trainer. The status bar and menus are standalone-only.

## Connecting

**File → Connect…** opens the connect dialog.

| Field | Notes |
|-------|-------|
| Server URL | The yaat-server URL your instructor gave you. Saved-server names are remembered between launches. |
| VATSIM CID | Required. Same value you use for CRC — auto-join keys off this. |
| Initials | Required. Two-letter ID used in command attribution and rundown lists. |
| ARTCC ID | Required. The facility you're training (e.g., `ZOA`). |

**File → Disconnect** drops the SignalR connection. Closing the window also disconnects.

### Auto-connect on launch

Pass `--autoconnect <name-or-url>` to skip the connect dialog:

```bash
YaatVStrips.exe --autoconnect YAAT1
YaatVStrips.exe --autoconnect http://localhost:5000
```

The argument is matched first against your saved servers list (case-insensitive name lookup), then treated as a literal URL. The launcher retries up to 30 times at 2-second intervals so it survives launching alongside the server.

## Rooms

A **room** is an isolated training session on the server. You see strips only for the room you've joined.

### Auto-join

When you connect, the app calls the server's `FindRoomForMyCid` API. If exactly one room has been created for your CID, you join it automatically — no further action needed. The status bar updates to `Joined room — <scenario>`.

The server also pushes a `RoomAvailableForCid` event when an instructor pulls a CRC client matching your CID into a room. The app reacts to this push by auto-joining (unless you're already in a different room).

### Manual join

**Room → Join Room…** opens the room picker:

| Column | Meaning |
|--------|---------|
| Creator | Initials of the YAAT user who created the room |
| ARTCC   | ARTCC of the room creator |
| Scenario | Scenario currently loaded (or empty if none yet) |
| Aircraft | Active aircraft count |

Click **Refresh** to re-list, **Join** (or double-click) to enter, **Cancel** to back out.

**Room → Leave Room** leaves the current room without disconnecting from the server. The bay buttons clear; you stay in the connected state and can join a different room.

### Kicked

If an instructor kicks you, the status bar shows `Kicked: <reason>` and the room state clears. You stay connected and can rejoin a different room.

## Strip view

### Header bar

Across the top of the strip area:

- **Facility button** (leftmost) — current facility name. Click to switch this window to another accessible facility in place. Useful when your position spans an ATCT and its parent TRACON: the facility button cycles between them.
- **Bay buttons** — one per accessible bay. **Own bays** render filled in neutral grey; **external bays** (linked from a sibling facility, e.g. a tower's parent TRACON) render with a thin outlined style. External bays are also marked with **↗** in context menus.
- **Zoom controls** (− / % / +) — scale the racks area without affecting the header. Range 50%–150% in 10% steps; default 80% fits two racks comfortably on a 1080p screen.
- **Trash zone** — drop a strip here to delete it.
- **Printer** toggle — opens the printer modal. Bound to **Tab** as well.

### Bays and racks

Each bay holds 1–N racks (configured per facility, typically 3). Racks render strips **bottom-up FIFO** — newer strips land at the visual bottom and older strips stack upward. Racks have a fixed width; if a bay has more racks than fit on screen, a horizontal scrollbar appears.

Bay topology (rack count, own vs external, separator-locking, separate arrival/departure printers, arrival-strip support) comes from the ARTCC config. There is no client-side override.

### Strip types

| Type | What it is |
|------|------------|
| **Departure strip** | Full-width strip from a filed IFR departure flight plan. 18 field slots including a 3×3 annotation grid (boxes 10–18). Auto-printed on aircraft spawn — see [Auto-printing](#auto-printing). |
| **Arrival strip** | Full-width strip auto-printed when an airborne aircraft is within 20 minutes of destination. Same layout as departures. Only rendered when the position's facility config enables arrival strips. |
| **Half-strip** | Compact freeform note up to 6 lines. Sits on either the left or right of a rack slot — slide between sides to fit two half-strips into one slot. |
| **Separator** | Thin colored divider with optional freeform label. Four styles: handwritten, white, red, green. Locked facilities allow handwritten only. |
| **Blank strip** | Empty placeholder for manual annotation. |

Offset strips translate horizontally so the callsign column stays visible above the next rack — useful when racks overlap during heavy traffic.

### Selection and drag-drop

**Click** a strip to select it. **Esc** deselects.

**Plain arrow keys** move selection between adjacent strips. **Ctrl+arrows** move the selected strip itself.

**Drag** any strip:
- Onto another rack (same bay or a different bay's button in the header) to move it
- Onto the **trash zone** in the header to delete

A drop preview shows where the strip will land. Drops on rack padding or empty space resolve to the rack's tail (visual top).

### Right-click menus

**Right-click on a strip** opens its context menu:

| Item | Notes |
|------|-------|
| **Offset / Un-offset** | Toggle the offset margin |
| **Slide** | Half-strip only — toggle between left/right |
| **Edit lines** | Half-strip only — opens the inline editor with lines joined by ` / ` |
| **Edit label** | Separator only — opens the inline editor for the label |
| **Push to {bay}** | Append to rack 1 of the chosen bay (external bays show ↗) |
| **Push all in rack to {bay}** | Bulk-move every strip in the source rack |
| **Delete** | — |

**Right-click on empty rack space** opens the creation menu:

| Item | Notes |
|------|-------|
| **Add half-strip** | Empty 6-line note |
| **Add separator** | Submenu of styles. If the position is configured `lockSeparators=true`, only **Add handwritten separator** appears |
| **Add blank strip** | — |
| **Push all to {bay}** | Only when the rack already has strips |

### Inline editing

Editing happens in a popup that anchors to the clicked element.

**Annotation boxes (full strips):**
- Click any of the nine annotation cells (rendered as boxes 10–18 in the CRC numbering)
- Type and press Enter to commit, or Esc to cancel
- **Tab / Shift+Tab** moves to the next / previous annotation cell without leaving edit mode
- Typing **`?`** substitutes a checkmark **✓** live (server-side `?` on `AN` commands is normalized the same way)

**Half-strip lines:**
- Right-click → **Edit lines**, or **Enter** with the strip selected
- The popup shows the existing lines joined by ` / ` — split on the same delimiter to commit multiple lines
- Maximum 6 lines

**Separator labels:**
- Right-click → **Edit label**, or **Enter** with the separator selected
- Single line of freeform text

### Printer modal

Open the modal with **Tab**, the **Printer** toggle, or **Esc** when nothing is selected. The racks stay visible behind the modal — drops from the modal onto the bays update immediately without dismissing.

```
┌─ Printer ──────────────────────────────────────────────── ✕ ─┐
│                                                              │
│   [    AAL123    ]  [ Request Strip ]                        │
│                                                              │
│           [ Print Blank Strip ]                              │
│                                                              │
│   ─── Departure Printer ────────────────────────────────     │
│   ❮          [ ▣ AAL123 / KOAK / B738 / … ]           ❯      │
│                          1/3                                 │
│         [ Move to Bay ]  [ Move All to Bay ]  [ Delete ]     │
│                                                              │
│   ─── Arrival Printer ──────────────────────────────────     │
│   ❮          [ ▣ N12345 / KOAK / C172 / … ]           ❯      │
│                          1/1                                 │
│              [ Move to Bay ]  [ Delete ]                     │
└──────────────────────────────────────────────────────────────┘
```

- **Request Strip** — type a callsign, click to ask the server to print that aircraft's strip into the queue
- **Print Blank Strip** — adds a blank to the printer queue
- **Departure carousel** — ❮ ❯ step through queued strips, **N/M** counter shows position
  - **Move to Bay** — opens a bay picker for the visible strip
  - **Move All to Bay** — bulk-moves the entire queue
  - **Delete** — discards the visible strip
- **Arrival carousel** — only present when the facility config enables separate arrival/departure printers. Otherwise arrivals share the departure queue.

### Auto-printing

Strip printing is driven by the server based on student position type:

| Position type | Suffixes | Departure on spawn | Arrival within 20 min |
|---------------|----------|---------------------|------------------------|
| Tower | `_TWR`, `_LOC` | First own bay whose name starts with "Ground", else printer queue | Auto-prints to first matching bay if arrival strips enabled |
| Ground / Clearance | `_GND`, `_DEL` | Departure printer queue | — |
| Approach / Departure | `_APP`, `_DEP` | No spawn print — strip appears in the position's matching bay on takeoff roll | Bay matching position display name |
| Center / unknown | `_CTR`, other | Departure printer queue | — |

Arrival auto-print fires when the aircraft is within 20 minutes of destination (server config: `StripMutations.ArrivalAutoPrintMinutes`).

## Keyboard shortcuts

> Strips need keyboard focus. Click anywhere in the strip area first. The menu bar steals focus on Alt; click back into the strip area to resume.

| Key | Action |
|-----|--------|
| Click strip | Select |
| Esc | Deselect; if nothing selected, toggle printer panel |
| Arrow keys | Move selection between adjacent strips |
| Ctrl+arrows | Move the selected strip in that direction |
| Shift+← / → | Toggle offset on the selected strip |
| Ctrl+Shift+← / → | Slide a half-strip; cycle separator style (handwritten → white → red → green) |
| Enter | Edit half-strip lines / separator label |
| Ctrl+1..9 (with full strip selected) | Edit annotation box 10..18 |
| Tab | Toggle printer panel |
| PageDown / PageUp | Next / previous bay |
| Ctrl+Alt+1..9 | Push selected strip to bay N — or, if nothing selected, switch to bay N |
| Ctrl+Alt+← / → | Cycle this window to the previous / next accessible facility |
| Ctrl+Shift+H | Add a half-strip in the selected strip's rack (or rack 1) |
| Ctrl+Shift+S | Add a handwritten separator (cycle styles afterwards with Ctrl+Shift+→) |
| Delete / Backspace | Delete selected strip |

## Command surface

Every UI action emits a canonical YAAT command. The standalone has no command bar, but if you also run the full Yaat.Client trainer alongside, every strip command issued there shows up here in real time. The full reference is in the main repo's [COMMANDS.md → Strip / Data Operations](../../COMMANDS.md#strip--data-operations).

| Verb | Effect |
|------|--------|
| `STRIP {bay}[/{rack}[/{index}]]` | Push the selected aircraft's full strip to a bay |
| `STRIPD` / `STRIPO` | Delete / toggle offset on the selected aircraft's strip |
| `AN {box} [text]` | Write or clear an annotation box (1–9 = boxes 10–18) |
| `HSC {bay}[/{rack}] line\line\…` | Create a half-strip (up to 6 lines, `\` separates lines) |
| `HSA [bay[/rack]] key\new1\…` | Amend by lookup key (auto-search across bays without bay arg) |
| `HSD [bay] key` | Delete by lookup key |
| `HSM` / `HSO` / `HSS` | Move / toggle offset / slide a half-strip |
| `SEP H\|W\|R\|G bay[/rack[/index]] [label]` | Create separator |
| `SEPE bay/rack/index new-label` / `SEPD bay[/rack] label-or-position` | Edit / delete separator |
| `BLANK [bay[/rack[/index]]]` / `BLANKD bay[/rack]` | Create / delete blank |

## CRC integration

**Tools → Configure CRC Environments…** writes YAAT entries to CRC's `DevEnvironments.json` so CRC can connect to the same servers. After running it, restart CRC and pick **YAAT1** (hosted) or **YAAT Local** (localhost) from CRC's environment selector. CRC then connects with your VATSIM credentials and joins the same room you're in here.

The menu shows three states:
- "CRC is not installed on this computer." — install CRC first
- "CRC already has YAAT server environments configured." — nothing to do; just restart CRC
- A success toast — restart CRC to pick up the new entries

The same configuration can be applied externally via the `Setup-CrcEnvironment.ps1` PowerShell script in the YAAT repo; the menu item is offered here so trainees don't need the repo.

## Updates

The app self-updates via [Velopack](https://velopack.io/) on a per-platform channel:

| Platform | Channel |
|----------|---------|
| Windows  | `vstrips-win` |
| macOS    | `vstrips-osx` |
| Linux    | `vstrips-linux` |

When a new version is detected, a banner appears at the bottom of the window with **Update Now** and **Later** buttons. **Update Now** downloads the delta in the background (a progress bar appears), then applies it and restarts. **Later** dismisses the banner for the current session — it will check again next launch.

This is independent of the main Yaat.Client installer's update channel — the two apps update on their own cadence.

## Preferences and persistence

Per-user state lives under a separate appdata namespace from the full client (`yaat-vstrips`, not `yaat`):

| Platform | Path |
|----------|------|
| Windows  | `%LOCALAPPDATA%\yaat-vstrips\` |
| macOS    | `~/Library/Application Support/yaat-vstrips/` |
| Linux    | `~/.local/share/yaat-vstrips/` |

What's saved:
- `preferences.json` — VATSIM CID / initials / ARTCC, saved server list, last-used server URL
- Window geometry under the `VStripsStandalone` key (default 1000×700)
- `yaat-vstrips.log` — runtime log

Bay layouts, zoom level, selection, and drag state are not persisted — they're driven by the server config and reset on each scenario load.

If you also run the full Yaat.Client trainer, its preferences live under `%LOCALAPPDATA%\yaat\` (or platform equivalent). The two stores are independent — set your CID/initials/ARTCC in both if you use both apps.

## Limitations

- **Single facility per window.** The full Yaat.Client trainer can show multiple facility tabs at once (e.g., tower + parent TRACON). The standalone uses the in-place facility switcher in the header instead. Open a second instance of the app if you really need two facilities side-by-side.
- **No aircraft grid, ground/radar view, terminal, scenario controls, weather UI, or speech pipeline.** Use Yaat.Client for those.
- **No command bar.** Strip mutations come from clicks, drags, and shortcuts; if you need to type a command, do it from Yaat.Client.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| **No bays appear after joining a room** | The instructor hasn't loaded a scenario yet, or the ARTCC config doesn't define flight-strip bays for the position you connected as. Check that your ARTCC ID matches the scenario's facility. |
| **Strips appear but don't update** | Status bar should say `Connected to <url>` and `Joined room — …`. If it says `Reconnecting…` or `Disconnected`, the SignalR connection dropped — wait for auto-reconnect or **File → Disconnect** then reconnect manually. |
| **Auto-join didn't fire** | The server only auto-joins when exactly one room exists for your CID. Use **Room → Join Room…** to pick one manually. |
| **Bay layout looks wrong** | Bay topology comes from the server's ARTCC config. Compare with another vStrips client in the same room — if they disagree, the issue is on the server side. |
| **CRC doesn't see the YAAT server** | **Tools → Configure CRC Environments…** then restart CRC. |
| **App refuses to start on Linux** | Install `libfontconfig1`, `libfreetype6`, and a font like `fonts-dejavu-core`. |
| **Update banner stuck on a download** | Click **Later** to dismiss, then check the log at `%LOCALAPPDATA%\yaat-vstrips\yaat-vstrips.log` for Velopack errors. As a last resort, reinstall from the Releases page. |
