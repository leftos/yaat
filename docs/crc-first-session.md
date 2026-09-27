# CRC first session: from a fresh install to amending a flight plan

A student's-eye walkthrough of CRC 2.18.2 against a local yaat-server, written while setting up an OAK ground profile and amending a flight plan for #463 (2026-09-26). It records what the CRC manual ([`crc/overview.md`](crc/overview.md)) leaves out or gets wrong for this setup. For the local server, run yaat-server from source (`:5130`) and point CRC at it with a `YAAT Local` entry in `%LOCALAPPDATA%\CRC\DevEnvironments.json` (`Setup-CrcEnvironment.ps1 -Servers @(@{Name="YAAT Local";Url="http://localhost:5130"})`; the script needs `GeneralSettings.json` to exist, so run CRC once first). The last section covers driving CRC from an agent through [the client-driver MCP](client-driver-mcp.md); its open frictions are tracked in [plans/client-driver-mcp-friction.md](plans/client-driver-mcp-friction.md).

## Profile
- A fresh CRC opens on **CRC Profiles** (empty list). The manual (docs/crc/overview.md "Profile Setup") says an ARTCC has to be installed first: **Manage Installed ARTCCs...** lists every ARTCC with Install/Uninstall. Here ZOA was already installed (its row shows Uninstall), because CRC downloads data on its first launch.
- **New Profile...** opens *Create New CRC Profile*: ARTCC (preselected ZOA) → Facility → Display Type → Profile Name → **Create Profile**. The Display Type and Name fields stay disabled until the step before them has a value.
- Facility list for ZOA: ZOA, NCT, O90, then the ATCTs alphabetically. "OAK - Oakland Intl ATCT" is the 24th entry.
- The Display Types OAK offers are **STARS** and **Tower Cab**. ASDE-X is not a primary type there, even though the manual's table lists "ASDE-X (if available)" for an ATCT; it gets added as a window/tab later. For a ground position, pick Tower Cab.
- A profile's facility and primary display type can't be changed after creation (manual).
- Create Profile launches the profile at once. The display window is titled `CRC : 1 : Cab : OAK`.

- Save the profile (hamburger → **Save Profile**) before closing CRC. It's stored under `%LOCALAPPDATA%\CRC\Profiles` and appears in the CRC Profiles list for the next run, so it doesn't have to be recreated.

## Before connecting
- A new profile opens with the Messages, Controllers and Voice Switch windows floating over the Tower Cab view. The top bar reads `DISCONNECTED`, the clock, the wind and the altimeter.
- The hamburger (top-left of the display window) holds Connect... / Activate Session / Save Profile / Profile Settings / **General Settings...** / Display Settings / New Window / New Tab / View / Help / Switch Profile / Exit.
- General Settings needs the **User ID** (VATSIM CID), **Password** and **Real Name** before Connect works (manual, "Connecting to VATSIM"). It also holds the audio devices and PTT; neither is needed for a YAAT session.

## Connecting as OAK GC1 (ground)
- Connect... (hamburger, or `Ctrl+F12`) opens *Connect to VATSIM*: Position (OAK: CD 121.100, GC1 121.750, LC1 127.200, ATIS, GC2 121.900, LC2 118.300), Role (Observer/Controller/Student/Instructor), Rating (Observer, Student1–3, Controller1–3, Instructor1–3, Supervisor, Administrator), and Environment (Live, Sweatbox 1/2, Test, then the DevEnvironments.json entries: YAAT Local, YAAT1).
- For a ground student: GC1, Student, Student1, YAAT Local.
- **The first attempt fails with:** "In order to connect using the selected position, you must have a STARS display configured for that position." A Tower Cab profile can't sign in as GC1 until it has a STARS display for that position. Fix: hamburger → **New Window...** (`Ctrl+N`) → Display Mode STARS, Facility OAK, Initial Area "Oakland Tower", Initial Position GC1. The window comes up as `CRC : 2 : STARS : OAK-3O` showing "NOT SIGNED IN, Position: OAK-3O GC1". Connect again from there.
- The display modes offered in New Window for OAK are STARS, SAID (Saab), Tower Cab and Browser; there is no ASDE-X.
- Once connected, the Messages title reads "Connected as OAK_GND". **Activate the session** with `Ctrl+Shift+F12` (hamburger → Activate Session); the title then gains "Session is ACTIVE". The STARS FLIGHT PLAN list fills with the room's departures (callsign + beacon).
- Against a Development yaat-server, CRC binds to the only room automatically (`DevCrcAutoBind`). The CID doesn't have to match the YAAT client's.

## Flight Plan Editor
- `Ctrl+F` opens an empty FPE. Type the callsign in **AID** and press Enter to retrieve the plan; the title becomes the callsign and the button changes from Create to **Amend**.
- Fields: AID, CID, BCN (recycle button assigns a new code), **TYP**, **EQ** (the FAA suffix, a separate box), DEP, DEST, SPD, ALT (hundreds of feet; `VFR`, `OTP`, `VFR/055`), RTE, RMK.
- After Amend, YAAT's terminal echoes `[CRC] OAK_GND (<name>) FP: (<callsign> <type>/<suffix> <beacon> <dep> <dest> <alt>)`, which is a quick check of what the server stored.

## Saving the profile
- Save Profile (hamburger, or `Ctrl+S`) asks **Confirm: Save profile "OAK_GND"?**. Nothing is written until you answer Yes. Each extra save attempt stacks another confirm.
- The saved profile keeps every window's bounds, the extra STARS window included, plus the last environment, position, role and rating. Next time only Connect + Activate are needed.
- Layout used here (5120×1392 screen, YAAT in the middle): Cab window 0,0 1580×880; Messages below it (0,880 1000×460), then Controllers (1000,880) and Voice Switch (1290,880); STARS on the right (3520,0 1600×1000) with the FPE under it (3520,1010).

## Driving CRC through UI Automation (client-driver MCP)
- CRC needs `set_input_mode real`.
- In WPF combo boxes the items' UIA name is the view-model type (`Vatsim.Nas.Crc.Ui.ViewModels.FacilityViewModel`); the readable label is the item's child `Text`. The popup closes between tool calls, so a click on an item fails with "offscreen". Instead, `focus` the combo box and `send_keys {DOWN n}`, then check the result with `screenshot`.
- `set_text` on CRC edits goes through ValuePattern (real mode) and works.
