# YAAT Flight Strips — User Guide

## What is this?

YAAT Flight Strips is a standalone desktop app that displays flight-strip bays
during YAAT training sessions. It mirrors the vStrips website experience: you see
the same departure and arrival strips, half-strips, separators, and blanks that a
real vStrips client would display, and you can create, move, annotate, and delete
them with drag/drop or keyboard shortcuts.

Students and RPOs run this app alongside CRC while training. Instructors can also
use the embedded Strips tab inside the full YAAT Client instead of (or in addition
to) this standalone app.

## Getting started

### 1. Connect to the server

1. Launch the app.
2. Go to **File → Connect**.
3. Enter the yaat-server URL your instructor gave you (e.g. `http://192.168.1.10:5000`).
4. Click **Connect**.

The status bar at the bottom shows "Connected to ..." when the connection succeeds.

### 2. Join a training room

1. Go to **Room → Join Room**.
2. The room list shows all active rooms. Pick the one your instructor created.
3. Click **Join**.

The strip bays appear once the instructor loads a scenario. If a scenario is
already loaded when you join, the bays populate immediately.

### 3. Using flight strips

#### Bay navigation

- **Bay buttons** in the header switch between bays (Ground, Local, etc.).
- **PageDown / PageUp** cycles through bays.
- **Ctrl+Alt+1..9** jumps directly to a bay by position.

#### Working with strips

- **Drag a strip** from one rack and drop it onto another rack in the same bay
  or onto a different bay button in the header.
- **Drop onto the trash zone** (red bin icon in the header) to delete a strip.
- **Shift+click** a strip to toggle its offset (indentation).
- **Alt+click** a strip to delete it.
- **Del / Backspace** deletes the currently selected strip.

#### Printer panel

- Click the **Printer** toggle (or press **Tab**) to open/close the printer panel
  on the right side.
- New departure strips appear in the printer when aircraft spawn in the scenario.
- Arrival strips auto-print when aircraft are within 20 minutes of the
  destination airport.

#### Strip types

| Type | Description |
|------|-------------|
| **Departure strip** | Full-width strip with callsign, equipment, beacon, altitude, route. Printed automatically on aircraft spawn. |
| **Arrival strip** | Similar layout to departure; auto-printed when ETA < 20 minutes. |
| **Half-strip** | Compact multi-line note. Created via `HSC` in the terminal or by the instructor. |
| **Separator** | Thin colored divider (handwritten / white / red / green) with optional label. |
| **Blank strip** | Empty placeholder for manual annotation. |

#### Annotation boxes

Full strips (departure and arrival) have a 3×3 annotation grid on the right side
(boxes 10–18 in vStrips numbering). These are edited via the `AN` command in the
YAAT terminal — the standalone app displays them but doesn't yet have inline
editing. Inline edit support is planned for a follow-up release.

## Disconnecting

- **File → Disconnect** drops the server connection.
- **Room → Leave Room** leaves the current room but keeps the connection.
- Closing the window disconnects automatically.

## Preferences

The standalone shares the same preferences file as the full YAAT Client
(`%LOCALAPPDATA%/yaat/preferences.json`). Window position/size is saved
separately under the "VStripsStandalone" geometry key.

## Troubleshooting

| Problem | Solution |
|---------|----------|
| No bays appear | The instructor hasn't loaded a scenario yet, or the ARTCC config doesn't define flight-strip bays for the student position. |
| Strips don't move | Check that you're connected and in a room (status bar). The server must be running. |
| Can't find room | Ask the instructor for the room ID, or refresh the room list. |
