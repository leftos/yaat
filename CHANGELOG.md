# Changelog

## Unreleased

### Added
- Pilot SAY transmissions use AIM-compliant spoken phraseology (AIM 4-2-8/9/10/11). Altitudes read "Leaving five thousand three hundred for eight thousand" instead of "5300 ft, climbing to 8000 ft"; headings spell three digits ("Heading two seven zero, direct MENLO"); Mach drops the leading zero ("Mach point seven eight"); approach IDs expand the type code and phoneticize parallel-approach suffixes ("Expecting the ILS Yankee one niner left approach"). Direct (`SALT`) and deferred (`AT FIX SALT`) reports produce identical text.
- `ATXI` (helicopter air-taxi) now accepts every controller-natural destination form: parking spots and helipads with or without `@` (`ATXI @FDX1`, `ATXI HELI`), taxiway spots with or without `$` (`ATXI $7A`, `ATXI 7A`), and runway designators (`ATXI 28R` — targets the threshold of the named end). Previously, anything with a `@`/`$` sigil was passed through to the spot lookup verbatim and missed, and runways weren't searched at all.
- `ATXI` now lands the helicopter on the destination instead of leaving it in a permanent hover. The command queues `AirTaxi → HelicopterLanding → AtParking` (the same chain `LAND` already uses), so the heli lifts off, cruises, descends, and stops on the spot in one command.
- Helicopter air-taxi cruise altitude raised from 50 to 100 ft AGL — still inside the FAA 7110.65 §3-11-1.c "below 100 ft AGL" envelope, but with more clearance from ground vehicles and obstacles.

### Fixed
- `AT <fix>` deferred reports now actually transmit, e.g. `AT WAITZ SALT` (say altitude when crossing WAITZ), `AT MENLO SHDG`, `AT RHV SAY position report`. Previously the trigger fired but the pilot transmission was silently lost. Direct `SALT`, `SHDG`, and `SPOS` also now work — these three bare SAY variants returned `Command not yet supported` outside of presets.
- STARS flight-plan creation from CRC now echoes the new flight plan on the scope readout area (callsign, type/equipment, beacon code, and route or `NO ROUTE`) and broadcasts the command to the YAAT instructor terminal — previously these gestures were silent on both sides. Duplicate flight-plan creates (typing `<FLT DATA>` or `<VFR PLAN>` for a callsign that already has a flight plan) now return `DUP NEW ID` to the scope instead of silently amending.
- Typing a callsign plus flight-data fields and clicking the STARS scope (e.g. `N10194 C172` + slew) now creates an unsupported data block at the click location with the typed flight plan, the same way ENTER would have. Previously this gesture either rejected with a `CALLSIGN MISMATCH` warning when the typed callsign matched an existing track, or produced a malformed `GHOST` canonical that failed to parse server-side.
- STARS error `ILL FORMAT` is now just `FORMAT`.
- Helicopter `LAND` no longer overshoots the destination by ~150 ft, then drifts forward another ~700 ft during the descent. The `AirTaxi` phase now decelerates smoothly as it approaches the spot (was: cruised at 40 KIAS until within 0.05 nm, then braked too late and coasted past), and the landing descent now holds horizontal position instead of bleeding altitude into forward motion (the heli previously accelerated to ~40 kt while dropping the last 50 ft). Helicopters now touch down within ~30 ft of the named spot.
- Aircraft on final approach no longer slow to final approach speed (FAS) earlier than needed. A slow piston that only needs to bleed a few knots used to start decelerating at a fixed 5 NM from the threshold, wasting most of final at FAS. The decel point is now computed kinematically — aircraft hold approach-cruise speed until just before short final and settle at FAS by ~2 NM (≈660 ft AGL on a 3° glideslope).
- VFR aircraft now show flight rules on departure strips. The requested-altitude box reads `VFR`, `VFR/010` (1,000 ft), or `VFR/035` (3,500 ft) instead of the IFR-style `010`/`035` — matching CRC vStrips.
- vStrips `HSM` (half-strip move) now parses multi-word bay names. Dragging a half-strip into bays like `Local 1` or `Local 2` previously failed silently with *"Unknown strip bay"* — the canonical `HSM N569SX Local 1/1/2` was mis-parsed as bay `N569SX` with key `Local`. HSM now resolves the destination greedily against the bay registry, the same way `STRIP` and `HSC` already did.

## v0.1.9-alpha [2026/04/30]

### Added
- "Push all in rack to {bay}" / "Push all to {bay}" right-click options in YAAT Flight Strips. Right-click on a strip in a rack with multiple strips, or on empty rack space, to bulk-move every strip (or separator/blank) in that rack to another bay's rack 0 in one click — order preserved.

### Fixed
- Departure strips now auto-route based on the student's controller position. Tower students get departure strips delivered straight to their first **Ground** bay, simulating the Clearance→Ground hand-off that's invisible in local-only training. Approach students get a strip in their facility's matching bay (e.g. "Friant" for `FAT_F_APP`) when the aircraft begins its takeoff roll, mimicking the tower's "rolling call". Ground, Center, and unknown student positions keep the printer-queue behavior.
- Command rejections now explain *why*. Typing a verb without arguments (e.g. `CM`) shows the expected signature in the status bar (`Expected: CM <altitude>`); commands issued during a phase that doesn't accept them (e.g. `FH 270` during pushback) describe the phase state (`aircraft is being pushed back; only HOLD/RES are accepted until pushback completes`) instead of the generic `Cannot accept X during Y`; and unrecognized verbs report `"is not a recognized command"` instead of falling back to `Unknown command`.

## v0.1.8-alpha [2026/04/27]

### Added
- "Move All to Bay" button in the YAAT Flight Strips printer's pending queue. Sorts pending strips alphabetically by callsign and dispatches each to bay rack 0 in one click. (Unique to YAAT Flight Strips — CRC has no equivalent.)

### Fixed
- "Configure CRC Environments" no longer reports *"CRC is not installed"* when CRC is installed but its registry entry is missing. The menu (in YAAT Client and YAAT Flight Strips) and the standalone `Setup-CrcEnvironment.ps1` script now find CRC by probing its config folder — `%LOCALAPPDATA%\CRC` on Windows, `~/Library/Application Support/CRC` on macOS, `~/.config/CRC` on Linux.
- Inline-edit cells in YAAT Flight Strips half-strips, matching CRC. The white half of a half-strip is now a 3×2 grid of editable cells — click into any cell, type, and Tab cycles 0→5 with wrap. Annotation cells in the 3×3 annotation grid Tab-cycle 1→9 the same way. Right-clicking on these cells now shows the strip context menu instead of the system text-edit flyout.
- Inline-edit separator labels, matching CRC. Single-click a separator's label to focus it and type a new label; click-out or Tab commits. Dragging a separator now moves it to the new rack instead of duplicating it.
- Separator bands now render at the full 69 px strip height, matching CRC's layout. Foreground is black on handwritten/white bands and white on red/green.
- YAAT Flight Strips now auto-joins your room mid-session, not just at connect time, matching how CRC tracks room membership. Connect Flight Strips first, then join a room later from CRC or YAAT Client, and Flight Strips picks it up automatically.
- Bay view in YAAT Flight Strips scrolls vertically when stacked strips exceed the viewport height (previously the overflow was clipped).
- Blank strips in the YAAT Flight Strips printer carousel can now be deleted (Delete button on each strip), matching CRC.
- "Print Blank Strip" in YAAT Flight Strips now jumps the printer carousel to the strip that was just added, so you can immediately edit or place it without scrolling — matching CRC's behavior.
- Adding a separator or blank strip to an empty rack in YAAT Flight Strips — via right-click "Add…" or Ctrl+Shift+S — now stacks the new item at the visual top of the rack instead of the bottom, matching CRC.
- vStrips `HSC` command parses multi-word bay names. `HSC Ground 1/2` now correctly maps to bay "GROUND 1" rack 2, instead of being misread as bay "GROUND" with a stray "1/2" line. Bay names with spaces (e.g. "GROUND 1", "LOCAL EAST") now work consistently.
- Portable downloads now actually run. The v0.1.7-alpha portable release was a bare single-file exe that crashed at startup because the rendering DLLs that ship alongside it weren't included in the download. Releases now ship as `*-Portable.zip` containing the full app — extract into a fresh folder and run the exe. (Linux is unaffected; the AppImage already bundles everything.)

## v0.1.7-alpha [2026/04/26]

### Added
- VATSIM CID, initials, and ARTCC fields are now in the Connect dialog itself, alongside the server list. YAAT Flight Strips had no place to set these previously — connecting and joining a room would silently send empty identity to the server. YAAT Client picks them up in Connect too as a shortcut around opening Settings.
- "Restore" button in the Connect dialog re-adds the default servers (YAAT1, Local) and resets their URLs if you've accidentally edited them. User-added servers are left alone.
- YAAT Flight Strips auto-joins your active training room on connect when your VATSIM CID is already in a room (via YAAT Client, CRC, or another vStrips session). The room picker still works for joining other rooms.
- YAAT Flight Strips gained a **Tools → Configure CRC Environments...** menu item, mirroring the same action in YAAT Client. Adds the YAAT1 and YAAT Local entries to CRC's `DevEnvironments.json`.
- The room terminal now distinguishes Flight Strips clients: *"joined the room (Flight Strips)"* / *"left the room (Flight Strips)"* instead of the bare *"joined/left the room"* used for the full trainer. Other RPOs in the room can tell at a glance whether a participant has the full trainer or the limited client.

### Fixed
- YAAT Flight Strips no longer crashes when you click a bay containing strips. The standalone app's resource dictionary was missing the script-style font and the brushes the strip control depends on, so the first attempt to render a strip threw `KeyNotFoundException` for `StaticResource StripScriptFont`.
- The server list in YAAT Flight Strips's Connect dialog (and the room picker) now renders. The standalone app was missing the DataGrid theme include, so both grids rendered as invisible controls — making it look like there were no saved servers and no rooms to pick.
- Removing or moving a server in YAAT Flight Strips's Connect dialog now persists across window close. Previously the change was correctly saved when you clicked **Remove**, but the standalone app's window held a second copy of preferences in memory for window geometry; closing the window re-wrote the file with the deleted server still present.
- YAAT Flight Strips populates the default server list (YAAT1, Local) on first launch instead of starting with an empty list. The defaults previously only kicked in when a preferences file already existed with an empty server list, so a brand-new install showed no servers in the Connect dialog.

## v0.1.6-alpha [2026/04/26]

### Added
- `CXL` and `CLR` aliases for `DELAT` (delete pending queue). Re-issuing a command like `DM 025` deliberately only supersedes the same control surface, so any queued `ERD`, `DCT`, etc. survives — `CXL` is the explicit way to wipe the rest of the pending queue. New "Clearing the Pending Queue" section in COMMANDS.md walks through the workflow.
- Pressing **Enter** with a suggestion highlighted now expands the suggestion before sending the command — equivalent to pressing **Tab** then **Enter**. Toggle off in **Settings > Advanced > Command Input** to restore the previous behavior (Enter sends the typed text as-is). Defaults to on.

### Fixed
- An explicit `HS <runway>` in a TAXI command is now honored when "auto-cross runways" is enabled. Previously the auto-cross loop would clear the implicit hold-short on the entry side of the runway and a duplicate explicit hold-short would be left on the *exit* side of the same crossing, so the aircraft taxied across the runway and stopped on the far side. Aircraft now hold short on the entry side as instructed; auto-cross still clears any other (unspecified) crossings along the route.
- VFR pattern aircraft no longer accelerate after reaching final approach speed. After decelerating to Vref on the final leg, `FlightPhysics`'s auto-speed-schedule was kicking in and reassigning the category default speed (~110 kt for a small piston) — pushing the aircraft from Vref back up through 1.3·Vref and tripping the unstable-approach gate at the threshold. `FinalApproachPhase` now declares `ManagesSpeed = true` like every other pattern phase, so the auto-schedule no longer fires while it's active. Aircraft hold Vref through the flare and touch down normally instead of going around unprompted.
- Compound commands chained with `;` are now actually executed when replayed from a recording or bug bundle. Previously, anything containing `;` or `,` (e.g. `DCT VPCOL; ERD 28R`, `DM 015, DCT OAK; ERD 28R`) was silently dropped during replay because the replay path tried single-command parsing first and bailed out on parse failure without falling through to the compound parser. Affected aircraft kept flying their previous heading and never entered the pattern, which then triggered an "unstable" go-around when they crossed the airport at cruise speed. **This bug also affected bug-report-bundle export, which regenerates snapshots via replay** — bundles exported before this fix may show `Phases: null` for aircraft whose live session actually had pattern phases active. Re-export to capture accurate snapshots.
- Mid-session changes to the **Auto Accept Delay** and **Auto Delete Mode** settings now round-trip through bundle replay. Previously only `AutoClearedToLand` and `AutoCrossRunway` were replayed; the other two settings were recorded into the bundle but silently ignored when the bundle played back, so a session that changed Auto Accept Delay to 0s would still replay with the 5s default. The same gap affected snapshot regeneration at bundle export time, producing snapshots that didn't match the live session.
- VFR aircraft re-entering the pattern after a go-around now use the correct traffic side. For parallel runways with `L`/`R` suffixes (28L/28R, 10L/10R), the side follows the runway (28R → right traffic, 28L → left). For single runways and center parallels (30, 28C), the original ERD/ELB/ERB/ELD direction is preserved, falling back to left only when no direction was ever assigned. Previously every VFR go-around defaulted to left traffic, putting an aircraft cleared `ERD 28R` onto the wrong side after a missed approach.
- Aircraft preserve their landing intent across a go-around. A pattern aircraft cleared `TG` keeps cycling touch-and-goes after a GA; an aircraft attempting a full-stop landing (CLAND or visual approach via `ERB`/`ELB`/`ERD`/`ELD`) keeps trying to land on the next circuit. Previously every VFR aircraft was silently switched into touch-and-go cycling after any go-around, so a visual-approach aircraft that auto-went-around for no clearance, an unstable approach, or being too high would unexpectedly start doing touch-and-goes instead of re-attempting its landing.
- `ERD`/`ERB`/`ELD`/`ELB` now stamp the commanded pattern direction onto the aircraft's phase list so a subsequent go-around can preserve it. The direction was previously thrown away once the pattern phases were built.
- `APT` (change destination) now accepts both 3-letter FAA codes (`APT OAK`) and 4-letter ICAO codes (`APT KOAK`) and resolves the input to the canonical ICAO form before storing it on the flight plan. Unknown airports are now rejected with *"Unknown airport ZZZZ"* instead of silently writing the bad value and breaking downstream commands. `APPS` and `CAPP`/`CVA`/`EAPP`/`JAPP` apply the same validation when the user explicitly supplies an airport argument.
- The "Auto Cleared-to-Land" toggle now propagates to the aircraft list immediately. Toggling it mid-session previously left every existing aircraft (and every aircraft spawned afterward) flagged with the red "no landing clnc" warning when reaching final approach, even though the server was correctly auto-clearing them — only reloading the scenario resynced the client. The toggle now updates every aircraft on the spot, both on the toggling client and on every other RPO in the same room.
- `RFIS` (report field in sight) now succeeds at realistic distances for the destination's size and the aircraft's altitude. The previous 12 nm cap caused pilots to report "looking, negative contact" on a clear day at ranges where they should have seen the field. Acquisition range is now bounded by reported METAR visibility, the aircraft's geometric horizon (so low-altitude aircraft still can't see distant fields), and an airport-conspicuity cap derived from the runway envelope (single-strip GA field ~15 nm; multi-runway hub like KSFO up to 25 nm). KOAK and similar major fields now acquire out to ~20 nm at jet altitudes, matching real-world pilot reports.
- `FOLLOW` no longer spams "unable to maintain separation" warnings. When a follower was too close to its leader at minimum approach speed, the warning previously fired every physics tick (~4 Hz, 130+ times in one reported session) until the controller deleted the aircraft — the warning announced "cancelling follow" but the follow didn't actually cancel. The warning now fires once and the follow ends, matching the lost-sight and runaway-gap cancel paths.
- `L360`/`R360`/`L270`/`R270` now actually execute the commanded turn. The aircraft previously rolled 1° in the commanded direction and then sat on that heading indefinitely — the command appeared accepted but the loop never happened, leaving controllers using a 360 for spacing without the spacing they asked for.
- `SA` (make short approach) now works as a queued modifier. Issuing `SA` solo while still on pattern entry / upwind / crosswind, or chaining it like `ERD 28R; SA`, arms the upcoming downwind and base — previously the command silently dropped (compound) or returned *"requires downwind or base leg"* (solo). `MNA` (make normal approach) clears the arm symmetrically. The pilot's short approach is also more realistic: begins descending at downwind entry rather than waiting for abeam, compresses the along-track portion of the pattern (not the lateral offset, per AIM 4-3-3), targets the glideslope-intercept altitude for the compressed final, and floors the final length at the AIM FIG 4-3-2 1/4-mile minimum (Jet 1.5 nm / Turboprop 1.0 nm / Piston 0.5 nm / Helicopter 0.25 nm) so the approach stays stabilized. SA doesn't persist across touch-and-go loops.
- Fix aircraft remaining on STARS after landing.
- YAAT Flight Strips and YAAT Client can now share a room on the same VATSIM CID. The server's per-CID room index assumed one connection per user — a second client joining (or either client disconnecting) corrupted the mapping, so aircraft and strip updates stopped reaching the original client.

### Changed
- Aircraft cleared for a visual approach with `FOLLOW` now sit 3 nm behind a leading jet, up from 2 nm — closer to the FAA 7110.65 §5-5-4 IFR radar separation minimum that real controllers actually use. Turboprop and piston spacing unchanged.

## v0.1.5-alpha [2026/04/26]

### Added
- A searchable in-app command cheatsheet under **Help → Command Cheatsheet**. Lists every ATC command grouped by category (Heading, Altitude/Speed, Approach, Tower, Pattern, Ground, …) with verb, aliases, and short description. The filter box narrows by verb, alias, description, or category name; categories with no matches collapse out of the way.
- The standalone cheatsheet at `docs/command-cheatsheet.html` now adapts to screen as well as print — fluid columns on a desktop monitor, sticky filter bar with the same category-aware search, dark-mode aware. Printing to letter landscape still produces the original compact 4-column layout.

### Fixed
- Aircraft on a STAR (e.g. SFO ALWYS3) no longer accelerate past the last published speed restriction once the procedure's terminating fix is sequenced. Per AIM 5-4-1, the published speed is held until ATC issues a new speed or another restriction takes over. Any explicit speed command (SPD/RVSPD/RFAS/DRS, etc.) clears the held value.
- Aircraft on a cleared approach now descend continuously toward the glideslope instead of holding their assigned altitude until reaching a fix with an At/AtOrBelow restriction (typically the FAF). The descent target now follows the published 3° glideslope extended back through the approach, bounded above by the assigned altitude and below by published AtOrAbove constraints.
- Aircraft cleared for the approach below the glideslope no longer climb up to capture it. They now hold their assigned altitude until the glideslope descends to meet them from above, matching AIM 5-4-14.
- Aircraft on approaches whose published final approach course differs from the runway heading (offset approaches like LDA and SOIA, plus small magnetic-variation differences on aligned approaches) no longer trigger a spurious "going around" warning at the threshold. The aircraft now visually transitions from the published course to the runway centerline over the last segment of the approach, finishing the alignment before flare. The alignment window depends on the offset magnitude — small differences resolve in the last 200 → 150 ft AGL; published offset approaches anchor on the FAA Stabilized Approach Point at 500 → 300 ft AGL.

### Changed
- The "going around" warning issued when an approach becomes destabilized now names the specific reason — e.g. *"unstable: bank 22°"*, *"unstable: descent 1450 fpm"*, or *"unstable: 540 ft off centerline, IAS 195 > 182 kt (1.3·Vref)"* — instead of the bare token *"unstabilized"*.
- `WARP` no longer requires all four arguments. The fix is still required, but heading, altitude, and speed are now optional — when omitted, the aircraft keeps its current value for that parameter (matching the radar context-menu Warp, which already pre-fills current values). Trailing tokens are matched left-to-right against `heading → altitude → speed`, so `WARP SJC` keeps everything but position, `WARP SJC 270` only changes heading, and `WARP SJC 5000` only changes altitude (since 5000 can't be a heading).

## v0.1.4-alpha [2026/04/25]

### Fixed
- **Auto-update downloaded the wrong app.** In v0.1.3-alpha, clicking "Update Now" in YAAT Client could download and install YAAT Flight Strips on top of it (and vice versa), because both apps published their Velopack release index files (`RELEASES`, `releases.win.json`, `assets.win.json`) under the same filenames to the shared GitHub release — whichever job uploaded last won. Each app now publishes on its own Velopack channel (`win`/`linux`/`osx` for Client, `vstrips-win`/`vstrips-linux`/`vstrips-osx` for Flight Strips) and the auto-updater picks the matching channel for the running app. **If your Yaat.Client install was overwritten by Flight Strips during a v0.1.3-alpha update, you'll need to manually re-install Yaat.Client from the v0.1.4-alpha installer — there's no in-app way to recover.**
- YAAT Flight Strips now uses its own settings folder (`%LOCALAPPDATA%/yaat-vstrips/`) instead of sharing `%LOCALAPPDATA%/yaat/` with YAAT Client. The two apps no longer overwrite each other's preferences or log files.

### Added
- YAAT Flight Strips now offers in-app auto-updates with the same "Update Now" / "Later" banner as YAAT Client.

## v0.1.3-alpha [2026/04/25]

### Changed
- Pushback orientation now uses cardinal directions instead of numeric headings: `PUSH FACE E` / `PUSH TAIL W` (or shorthand `>E` / `<W`), with the eight compass points accepted (N, NE, E, SE, S, SW, W, NW). When combined with a taxiway (`PUSH TE FACE E`) the aircraft aligns with whichever of the taxiway's two directions matches the cardinal closest. Voice control accepts "pushback face east", "pushback tail west", and "pushback onto tango facing east".

### Fixed
- The inline command hint now shows the next variant when you press space past the last argument — e.g. `ELB 28L ` advances from `ELB [runway]` to `ELB [runway] [distance]`.
- Fixed `BEHIND` / `GIVEWAY` bug that prevented conflict detection.
- Partial target callsigns in `BEHIND` / `GIVEWAY` (e.g. `BEHIND 152SP`) now resolve to the matching aircraft, matching how partial callsigns already work at the start of a command and in `FOLLOW` / `RTIS`.
- Aircraft no longer stop when taxiing past a parked aircraft on an adjacent taxiway. The ground-conflict check now uses the parked aircraft's actual wingspan to determine wingtip clearance instead of treating anything within ~100 ft and a 90° forward cone as a hard stop.
- A longer aircraft trailing a shorter one (e.g. an A350 behind an E175 on a hold-short queue) now stops with its nose clear of the leader's tail. The previous stop threshold only used the leader's length, which underestimated separation whenever the trailer was bigger.
- Aircraft no longer spin in place at certain 4-way taxi intersections. At crossings like SFO's E/F intersection south of 28L, taxi routes to a parking spot could include a detour through a sub-2 kt corner arc that the aircraft physically couldn't follow, producing a slow on-the-spot pirouette. The pathfinder now sees a cleaned-up graph (parallel bypass edges from adjacent fillets removed) and routes through the proper centerline.
- Aircraft taxiing to a parking spot now stay on the controller's named taxiway as long as possible instead of slipping onto a parallel taxiway to save a few feet. For example, `TAXI E A @B10` now stays on A all the way to the numbered ramp connectors (M1, M3, RAMP) instead of routing through parallel taxiway Y because it was marginally shorter. The bias only affects unnamed letter-only taxiways — numbered taxiways and ramps are still used freely as needed to reach the spot.

## v0.1.2-alpha [2026/04/25]

### Fixed
- The in-app updater no longer crashes when you click "Update Now" — auto-updates now download and apply correctly.

## v0.1.1-alpha [2026/04/24]

### Added
- ERAM WIP: `QN` / `QF` / `QL` / `QT` / `QZ` / `QQ` / `QS` / `RD` / `QU` / `QP` verbs, sector configuration, dwell lock, and pointouts. Per-track annotations (interim/procedure/controller altitudes, VCI, leader direction/length) persist with the session.
- STARS tracks no longer flicker on the edge of coverage.
- Real NEXRAD weather overlay (5-minute background refresh) when using "Load Live Weather".
- When connecting to CRC, your client name, version, controller info, and transmit/receive frequency lists are now visible to peers requesting info.
- Airport elevation lookup falls back between ICAO and FAA identifiers (e.g. `KSFO` ↔ `SFO`).
- Build identification and in-app docs: the title bar, the new Help → About YAAT dialog, and the first line of `yaat-client.log` all show the version and whether the build is an installed release or a dev build. The Help menu also links Getting Started, User Guide, Commands Reference, and Changelog on GitHub.
- Airport-authored runway data drives pattern altitude, pattern size, default exit side, and forbidden exits. OAK 28L flies its published 600 ft AGL pattern instead of the per-category default; SFO 28R picks south-side exits and avoids L/P (which cross toward 1L/19R).

### Changed
- RTIS and RFIS keep trying when the pilot can't acquire the target/field on the first check: the command succeeds, the pilot reports a clean readback (e.g. "Negative contact KOAK, field's behind us, looking"), and re-checks each tick until acquisition. The reason (distance, cloud layer, hemisphere, bank, Class A) is shown to the RPO in the command response. Acquisition readbacks are now orange warnings instead of gray notifications.
- README, INSTALL, GETTING_STARTED, and CONTRIBUTING lead with the prebuilt installer instead of a from-source build.
- Aircraft positions sent between client and server, and stored in bug bundles and recordings, use a single combined `Position` field.
- `EXT` now extends the current pattern leg — upwind, crosswind, or downwind. Base is rejected (use `MNA` to widen instead).

### Fixed
- Aircraft no longer make spurious U-turns while taxiing.
- Pattern aircraft going around now turn crosswind 300 ft below pattern altitude (per AIM 4-3-3), matching the threshold for normal VFR departures, instead of holding runway heading until they reach pattern altitude.
- Followers no longer accelerate unreasonable amounts to catch up to their leader while following.
- Follow state is shown in the Info column during pattern legs (previously went blank once the leader entered a pattern).

## v0.1.0-alpha [2026/04/23]

- Initial release
