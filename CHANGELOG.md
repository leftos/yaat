# Changelog

## Unreleased

### Added
- `CXL` and `CLR` aliases for `DELAT` (delete pending queue). Re-issuing a command like `DM 025` deliberately only supersedes the same control surface, so any queued `ERD`, `DCT`, etc. survives — `CXL` is the explicit way to wipe the rest of the pending queue. New "Clearing the Pending Queue" section in COMMANDS.md walks through the workflow.

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
