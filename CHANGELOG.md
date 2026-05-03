# Changelog

## Unreleased

### Added
- `AT` conditions also fire on ground entities — taxiways (`AT B`), named spots (`AT $5`), parking (`AT @TERM2`), and two-taxiway intersections (`AT B/C`).
- Airborne aircraft check in on first contact in solo-training mode — IFR with callsign/altitude, VFR with position-from-field plus intent (AIM 4-3-1).

### Fixed
- Students joining a room or activating/deactivating mid-session appear in the instructor's controller list immediately — peers see each other's state changes too.
- CRC lobby clients no longer receive controller, position, or consolidation data from other rooms — data lands the moment they're pulled into a room.
- `CTOC` after a mid-taxi `CTO` revokes the stored clearance and reinstates the destination hold-short; previously cleared crossings (`LV`/`RC`/`CROSS`) are left untouched.
- `CTOC` during line-up reverts the line-up — rolling takeoffs brake on the runway and hold; heavy/super aircraft complete the line-up first, then hold.
- ASDE-X destinations show CONUS airports in FAA form (`SFO`) instead of ICAO (`KSFO`), matching real CRC; fix-rule patterns accept either form.
- `--autoconnect` stops retrying as soon as you open File → Connect, pick File → Disconnect, or close the window — no more silent disconnects.
- Typing a callsign plus flight-data fields and pressing ENTER creates a STARS flight plan (DA or VP, discriminated by destination); duplicates return `DUP NEW ID`.

## v0.1.10-alpha [2026/05/01]

### Added
- **Browser-based YAAT Flight Strips at `<server>/vstrips/`** — no-install web client; collects VATSIM CID/initials/ARTCC on first visit; **Tools → Open Strips in Browser** opens it prefilled.
- `SCAN <bay>` — copy a flight strip into an external facility's bay while keeping your own copy; right-click → "Scan to" for the menu form.
- Pilot SAY transmissions use AIM-compliant spoken phraseology (AIM 4-2-8/9/10/11) — spelled headings, "leaving X for Y" altitudes, phoneticized approach suffixes.
- `ATXI` accepts all destination forms: parking/helipads with or without `@`, taxiway spots with or without `$`, and runway designators (targets the named threshold).
- `ATXI` now lands the helicopter on the destination — single command takes it from lift-off through cruise, descent, and touchdown.
- Helicopter air-taxi cruise altitude raised from 50 to 100 ft AGL for more clearance from ground vehicles and obstacles (FAA 7110.65 §3-11-1.c).
- Standalone cheatsheet (`docs/command-cheatsheet.html`) overhauled with collapsible categories, role profiles, prefix-aware filter, keyboard navigation, and rows for previously missing commands.

### Fixed
- `AT <fix>` deferred reports (`SALT`, `SHDG`, `SPOS`) now transmit; bare `SALT`/`SHDG`/`SPOS` work outside presets too.
- STARS flight-plan creation echoes on the scope readout (callsign, type/equipment, beacon, route) and broadcasts to the instructor terminal; duplicates return `DUP NEW ID`.
- Typing a callsign plus flight-data fields and slewing the STARS scope creates an unsupported data block at the click location with the typed plan.
- Typing a callsign and slewing for an aircraft with an existing flight plan drops a ghost data block and pre-claims the track for your position.
- STARS error `ILL FORMAT` is now just `FORMAT`.
- Helicopter `LAND` decelerates smoothly on approach and holds horizontal position through descent — touches down within ~30 ft of the named spot.
- Aircraft on final hold approach-cruise speed until a kinematically computed decel point, settling at FAS by ~2 NM (≈660 ft AGL on a 3° glideslope).
- VFR departure strips show flight rules in the requested-altitude box: `VFR`, `VFR/010`, `VFR/035` — matching CRC vStrips.
- vStrips `HSM` (half-strip move) parses multi-word bay names like `Local 1` — destination resolves greedily against the bay registry, same as `STRIP`/`HSC`.
- Dragging a flight strip to a new bay/rack lands instantly on release; the server's broadcast reconciles silently in the background.
- Right-click commands on **empty** half-strips (annotate, move, swap, delete) now work.
- YAAT Flight Strips locks down while disconnected — bays clear, a red banner appears, and right-click/drag-drop/shortcuts/printer noop until you reconnect.

## v0.1.9-alpha [2026/04/30]

### Added
- "Push all in rack to {bay}" / "Push all to {bay}" right-click options in YAAT Flight Strips bulk-move every item in a rack — order preserved.

### Fixed
- Departure strips auto-route to the student's first **Ground** bay (Tower) or matching facility bay on takeoff roll (Approach); other positions keep the printer queue.
- Command rejections explain why — missing arguments show the expected signature, phase-blocked commands describe the phase state, and unknown verbs say "not a recognized command".

## v0.1.8-alpha [2026/04/27]

### Added
- "Move All to Bay" button in the YAAT Flight Strips printer queue sorts pending strips by callsign and dispatches each to bay rack 0 in one click.

### Fixed
- "Configure CRC Environments" finds CRC by probing its config folder (`%LOCALAPPDATA%\CRC`, `~/Library/Application Support/CRC`, `~/.config/CRC`) instead of relying on the registry entry.
- Inline-edit cells in YAAT Flight Strips half-strips — click any cell to type, Tab cycles 0→5 (white) or 1→9 (annotation grid), matching CRC.
- Inline-edit separator labels (single-click to focus, click-out/Tab commits) and dragging now moves separators instead of duplicating, matching CRC.
- Separator bands render at full 69 px strip height with black-on-white/handwritten and white-on-red/green text, matching CRC.
- YAAT Flight Strips auto-joins your room mid-session (not just at connect time) — join from CRC or YAAT Client and Flight Strips picks it up automatically.
- Bay view in YAAT Flight Strips scrolls vertically when stacked strips exceed the viewport height.
- Blank strips in the YAAT Flight Strips printer carousel can be deleted via the Delete button, matching CRC.
- "Print Blank Strip" jumps the printer carousel to the new strip so you can edit or place it without scrolling, matching CRC.
- Separators and blank strips added to an empty rack (right-click "Add…" or Ctrl+Shift+S) stack at the visual top, matching CRC.
- vStrips `HSC` parses multi-word bay names like "GROUND 1" or "LOCAL EAST" — `HSC Ground 1/2` maps to bay "GROUND 1" rack 2.
- Portable releases now ship as `*-Portable.zip` containing the full app (extract into a folder and run); Linux's AppImage already bundles everything.

## v0.1.7-alpha [2026/04/26]

### Added
- VATSIM CID, initials, and ARTCC fields appear in the Connect dialog itself (both apps), shortcutting around opening Settings.
- "Restore" button in the Connect dialog re-adds the default servers (YAAT1, Local) and resets their URLs; user-added servers are left alone.
- YAAT Flight Strips auto-joins your active training room on connect when your VATSIM CID is already in a room from another client.
- **Tools → Configure CRC Environments…** in YAAT Flight Strips adds YAAT1 and YAAT Local entries to CRC's `DevEnvironments.json`, mirroring YAAT Client.
- The room terminal labels Flight Strips joins/leaves as "(Flight Strips)" so other RPOs can tell which client each participant is using.

### Fixed
- YAAT Flight Strips no longer crashes when you click a bay containing strips.
- Server list and room picker in YAAT Flight Strips's Connect dialog now render correctly.
- Removing or moving a server in YAAT Flight Strips's Connect dialog persists across window close.
- YAAT Flight Strips populates the default server list (YAAT1, Local) on first launch — fresh installs see them in the Connect dialog.

## v0.1.6-alpha [2026/04/26]

### Added
- `CXL` and `CLR` aliases for `DELAT` (clear pending queue) — explicit way to wipe queued commands when re-issuing one only supersedes the same control surface.
- **Enter** with a suggestion highlighted expands it before sending — equivalent to **Tab**+**Enter**. Toggle in **Settings → Advanced → Command Input**.

### Fixed
- Explicit `HS <runway>` in a TAXI command holds short on the entry side even when "auto-cross runways" is enabled; other unspecified crossings still auto-clear.
- VFR pattern aircraft hold Vref through the flare and touch down normally instead of accelerating off final and tripping the unstable-approach gate.
- Compound commands (`;`/`,`) execute correctly during replay — bundles exported before this fix may show `Phases: null` for affected aircraft and should be re-exported.
- Mid-session changes to **Auto Accept Delay** and **Auto Delete Mode** round-trip through bundle replay (joining the existing replay of `AutoClearedToLand`/`AutoCrossRunway`).
- VFR aircraft re-entering the pattern after a go-around use the correct traffic side — runway suffix on parallels, original ERD/ELB/ERB/ELD direction otherwise.
- Aircraft preserve landing intent across go-arounds — `TG` keeps cycling touch-and-goes; CLAND or visual approaches keep trying for a full stop.
- `ERD`/`ERB`/`ELD`/`ELB` stamp the commanded pattern direction onto the aircraft's phase list so go-arounds preserve it.
- `APT` (and `APPS`/`CAPP`/`CVA`/`EAPP`/`JAPP`) accept both FAA (`OAK`) and ICAO (`KOAK`) codes, store canonical ICAO, and reject unknown airports with a clear error.
- The "Auto Cleared-to-Land" toggle propagates to every aircraft in the room immediately, on the toggling client and all other RPOs.
- `RFIS` acquisition range uses METAR visibility, geometric horizon, and airport conspicuity (~15 nm GA, up to 25 nm for hubs like KSFO) instead of a 12 nm cap.
- `FOLLOW` "unable to maintain separation" fires once and actually cancels the follow, matching lost-sight and runaway-gap cancel paths.
- `L360`/`R360`/`L270`/`R270` actually execute the commanded turn instead of rolling 1° and stopping.
- `SA` (make short approach) works as a queued modifier on pattern entry/upwind/crosswind or chained, with realistic compressed-pattern flight per AIM 4-3-3; `MNA` clears the arm.
- Aircraft drop from STARS after landing.
- YAAT Flight Strips and YAAT Client can share a room on the same VATSIM CID without one connection invalidating the other's updates.

### Changed
- Visual-approach `FOLLOW` spacing behind a leading jet bumped from 2 nm to 3 nm (FAA 7110.65 §5-5-4); turboprop and piston unchanged.

## v0.1.5-alpha [2026/04/26]

### Added
- Searchable in-app command cheatsheet under **Help → Command Cheatsheet** — every ATC command grouped by category, filter narrows by verb/alias/description/category.
- Standalone cheatsheet (`docs/command-cheatsheet.html`) adapts to screen too — fluid columns, sticky filter bar, dark-mode aware; printing still produces the compact 4-column layout.

### Fixed
- Aircraft on a STAR hold the last published speed restriction past the terminating fix until ATC issues a new speed (per AIM 5-4-1).
- Aircraft on a cleared approach descend continuously along the published 3° glideslope instead of stepping down only at At/AtOrBelow fixes.
- Aircraft cleared for an approach below the glideslope hold their assigned altitude until the glideslope descends to them (AIM 5-4-14).
- Approaches whose final course differs from the runway heading (LDA/SOIA, magnetic-variation offsets) align with the centerline before flare without spurious go-around warnings.

### Changed
- "Going around" warnings name the specific reason — e.g. *"unstable: bank 22°"*, *"unstable: descent 1450 fpm"* — instead of the bare token *"unstabilized"*.
- `WARP` only requires the fix; heading/altitude/speed are optional and matched left-to-right with slot-skipping (so `WARP SJC 5000` changes only altitude).

## v0.1.4-alpha [2026/04/25]

### Fixed
- Auto-update downloads the correct app — Client and Flight Strips now publish on separate channels. **v0.1.3 victims must manually re-install Yaat.Client from the v0.1.4 installer.**
- YAAT Flight Strips uses its own settings folder (`%LOCALAPPDATA%/yaat-vstrips/`) — no more overwriting YAAT Client's preferences or logs.

### Added
- YAAT Flight Strips offers in-app auto-updates with the same "Update Now" / "Later" banner as YAAT Client.

## v0.1.3-alpha [2026/04/25]

### Changed
- Pushback uses cardinal directions: `PUSH FACE E` / `PUSH TAIL W` (or shorthand `>E` / `<W`), with all eight compass points; taxiway+cardinal aligns with the closest direction.

### Fixed
- The inline command hint advances to the next variant when you press space past the last argument (e.g. `ELB 28L ` adds `[distance]`).
- `BEHIND` / `GIVEWAY` conflict detection now fires correctly.
- Partial target callsigns in `BEHIND` / `GIVEWAY` (e.g. `BEHIND 152SP`) resolve to the matching aircraft, matching `FOLLOW` / `RTIS`.
- Aircraft taxiing past a parked aircraft on an adjacent taxiway use actual wingspan for clearance instead of a blanket 100 ft / 90° forward cone.
- A longer aircraft trailing a shorter one (A350 behind E175) stops with its nose clear of the leader's tail, using both lengths for separation.
- Aircraft no longer spin in place at 4-way taxi intersections like SFO's E/F south of 28L — pathfinder routes through the proper centerline.
- Aircraft taxiing to parking stay on the controller's named taxiway instead of slipping onto unnamed parallels; numbered taxiways and ramps stay usable.

## v0.1.2-alpha [2026/04/25]

### Fixed
- "Update Now" no longer crashes — auto-updates download and apply correctly.

## v0.1.1-alpha [2026/04/24]

### Added
- ERAM WIP: `QN`/`QF`/`QL`/`QT`/`QZ`/`QQ`/`QS`/`RD`/`QU`/`QP` verbs, sector config, dwell lock, pointouts; per-track annotations (interim/procedure/controller altitudes, VCI, leader) persist with the session.
- STARS tracks no longer flicker at the edge of coverage.
- Real NEXRAD weather overlay (5-minute background refresh) via "Load Live Weather".
- Connecting to CRC exposes your client name, version, controller info, and transmit/receive frequency lists to peers requesting info.
- Airport elevation lookup falls back between ICAO and FAA identifiers (e.g. `KSFO` ↔ `SFO`).
- Build identification in title bar, **Help → About YAAT** dialog, and `yaat-client.log` first line; Help menu links Getting Started/User Guide/Commands/Changelog on GitHub.
- Airport-authored runway data drives pattern altitude/size, default exit side, and forbidden exits — OAK 28L flies 600 ft AGL; SFO 28R prefers south-side exits.

### Changed
- RTIS/RFIS keep retrying until acquisition with a "negative contact, looking" readback; reasons (distance, cloud, bank, Class A) show as orange warnings to the RPO.
- README, INSTALL, GETTING_STARTED, and CONTRIBUTING lead with the prebuilt installer instead of a from-source build.
- Aircraft positions in client/server messages, bug bundles, and recordings use a single combined `Position` field.
- `EXT` extends the current pattern leg (upwind, crosswind, or downwind); base is rejected (use `MNA` to widen instead).

### Fixed
- Aircraft no longer make spurious U-turns while taxiing.
- Pattern go-arounds turn crosswind 300 ft below pattern altitude (AIM 4-3-3), matching the threshold for normal VFR departures.
- Followers no longer accelerate unreasonably to catch up to their leader.
- Follow state shown in the Info column during pattern legs.

## v0.1.0-alpha [2026/04/23]

- Initial release
