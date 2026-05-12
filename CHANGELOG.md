# Changelog

## Unreleased

### Added
- Aircraft List title shows the pending delayed-spawn count; reads "No pending spawns" once the last delayed aircraft fires.
- Aircraft right-click menus (aircraft list, ground, radar) include a Favorite Commands submenu that runs the active favorites against the clicked aircraft.

### Changed
- IFR `CTO` only accepts bare `CTO` (follow SID) or `CTO` with a numeric heading vector. Pattern and runway-relative modifiers (`MRC`/`MRD`/`MR{N}`, `MLC`/`MLD`/`ML{N}`, `MRH`/`MSO`/`RH`, `OC`, `DCT`/`TLDCT`/`TRDCT`, `MLT`/`MRT`) are now rejected on IFR aircraft with a message explaining the restriction.

### Fixed
- IFR departures no longer turn off the runway at liftoff. The assigned heading vector is held on runway course through `TakeoffPhase` and applied by `InitialClimbPhase` only after the aircraft is past the departure end of runway AND at or above 400 ft above field elevation (TERPS criterion). Matches the VFR deferral already in place per AIM 4-3-2, with a 400 ft floor in place of pattern altitude − 300 ft.
- Minimized pop-out windows preserve their restored position and size across app restarts.
- Aircraft List Info column shows the taxiway intersection at hold-short — e.g. "Holding short 28R @ E" or "Holding short of E on C".
- `CTO MR<n>; DCT FIX` (or `ML<n>`) now takes the short way to the next fix after the departure turn rolls out.

## v0.2.2-alpha [2026/05/09]

### Highlights
- `EXT UPWIND/CROSSWIND/DOWNWIND` (also `EXTEND`) targets a specific pattern leg, with one-leg rollback if issued just after the turn.
- Standalone `yaat-crc-config` tool adds YAAT environments to CRC without installing the full client (~200 KB).
- Audio settings tab routes pilot text-to-speech and the SAY/warning chime to a chosen output device.
- Ground view rotation persists per airport across scenarios; RESET preserves it, Shift+Click RESET includes it.

### Added
- `EXT UPWIND/CROSSWIND/DOWNWIND` (also `EXTEND`) targets a specific pattern leg; rolls back one leg if issued just after the turn started.
- Settings adds an Audio tab pairing microphone selection with a new output-device picker that routes pilot text-to-speech and the SAY/warning chime.
- ARTCC `WakeDirectives` folders let facilities adjust Session Report wake scoring for documented local waivers and wake-advisory directives.
- Standalone `yaat-crc-config` tool adds YAAT environments to CRC without installing the full client — single ~200 KB binary released under `crc-config-v*` tags.
- Favorites pop-out has an Always on Top checkbox in Settings, alongside a title-bar system menu item on Windows and a Window → Always on Top entry in the macOS menu bar; same toggles are available for the existing pop-outs.
- Ground view rotation persists per airport regardless of scenario; RESET preserves rotation, Shift+Click RESET includes it.

### Changed
- Landing aircraft prefer a later same-side runway exit over an earlier opposite-side one, including when the closer same-side option is occupied.

### Fixed
- Student and Instructor connections to YAAT now display with the `(S)` and `(I)` prefix in CRC's controller list, instead of always appearing as plain Controller.
- `WARPG` and `WARP` work in any phase — no longer rejected when the aircraft is holding short, taxiing, or on approach.
- STARS no longer flashes Conflict Alert for two aircraft on parallel finals at an internal airport — the runway approach corridor suppression is now purely geometric and applies to every track inside it, including VFR pattern traffic on visual finals.
- `ELB`/`ERB` from long range now holds altitude on the base leg and descends along the glide path on final.
- Vector commands (FH, TR, CM, SPD) now clear the follow target, so the Aircraft List no longer keeps a stale "following X" prefix.
- `FOLLOW` issued from upwind or crosswind on a same-runway pattern circuit no longer routes the follower back through pattern entry — it just sets the follow target like it does from downwind/base/final.
- Pattern-phase followers now drop the follow target — with the matching pilot transmission — when the lead leaves the area (despawn), lands, or pulls away faster than the follower can catch. Previously the "following X" tag could linger for minutes after the lead was gone.
- `FOLLOW` no longer slows the follower (or extends downwind) when the lead is on the same runway but pattern-flow-behind — e.g. follower is on Downwind while the lead is still on the pattern entry feeder, or follower is further along the same leg. Holds baseline pattern speed until the lead actually catches up.
- Taxi routes through sharp curves at taxiway junctions (e.g. OAK J at K) now slow for the corner instead of spinning past it.
- Pattern direction set by `MLT`/`MRT` now survives heading vectors and single-approach `ERB`/`ELB`/`EF` clearances; subsequent circuits revert to the original side.
- `RFAS`, `S`, `NS`, and `DSR` issued during a pattern leg now adjust speed without cancelling the leg.
- `TAXI` commands without a destination now hold once the aircraft enters the last named taxiway, instead of walking it to its dead-end.
- STARS shows track history dots behind each aircraft on CRC; the trail stays visible across pauses.
- Toggling Auto Cross-Runway mid-session now applies to aircraft already taxiing — turning it on pre-clears their remaining runway crossings, turning it off re-arms only the crossings AutoCross had cleared.

## v0.2.1-alpha [2026/05/09]

### Highlights
- `CWT`, `CTO ... CWT`, and `CLAND ... CWT` issue caution wake turbulence; Session Report flags missing wake-advisory proof.
- Session Report adds runway-separation scoring for same-pavement, intersecting, and converging operations, plus projected/intersecting wake intervals and Class C outer-area service.
- Generated arrival callsigns now match destination and aircraft type using recent airport service data and fleet matching.
- Solo-training initial contact rules support ARTCC-specific handoff timing, position-type pairs, and exact callsign exceptions.

### Added
- Arrival generators use recent airport-service data and fleet matching so callsigns fit both destination and aircraft type.
- `CWT`, `CTO ... CWT`, and `CLAND ... CWT` issue caution wake turbulence and feed Session Report proof scoring.
- Session Report adds Class C outer-area service, no-minima advisories, structured RFIS field proof, and transferred-away aircraft filtering.
- Session Report scores projected/intersecting flight-path wake intervals and missing wake-advisory proof.
- Session Report scores reciprocal same-pavement, intersecting, and nonintersecting converging runway operations with FAA-grounded runway-separation evidence.
- Solo-training initial contact rules now support ARTCC-specific handoff timing, position-type pairs, and exact callsign exceptions.

### Fixed
- The terminal-panel settings flyout now populates correctly after scenario load, room join, and scenario-default auto-delete.
- Recording replays preserve generator-spawned aircraft so callsigns and aircraft state stay stable across generator changes.
- Scenario and replay loads no longer race when multiple missing aircraft profiles use sibling fallback.
- Structured RTIS and SAFAL proof resolve targets from the full live and replay aircraft snapshot.

## v0.2.0-alpha [2026/05/08]

### Highlights
- Solo training mode — type natural-language ATC and YAAT plays the pilots, with optional Piper voices, plain-English readbacks, paced arrivals, and typed commands that persist across runs.
- Live Session Report — separation, wake, traffic-advisory, and Class C scoring grounded in 7110.65; opens non-modal so you can keep working the radar.
- `CT [target]` / `FCA` / VFR `CM A/B025` — new pilot dispatch verbs: contact next controller (auto-resolved, or by callsign/freq/TCP), VFR sign-off without handoff, and at-or-above / at-or-below altitude restrictions.
- Favorite commands pop-out — fixed-grid panel with categories, airport scope, colors, sizing, ground overrides, batch blanks, and drag rearranging.

### Added
- `CT [target]` tells the pilot to contact the next controller — accepts callsign, frequency, or TCP, or auto-resolves with no argument. Identifies the target by published radio name (e.g. "NorCal Approach"); the terminal shows the digit frequency ("125.35") while TTS speaks digit-by-digit.
- `FCA` issues the VFR frequency-change-approved dismissal — pilot signs off without a follow-on handoff.
- VFR altitude restrictions support `CM A/B025` and `CM A/B2500` for at-or-above and at-or-below clearances.
- Solo-training VFR pilots hold outside FAA Class B/C airspace until `CLBRV` clearance or `STBY`/`ROGER` contact is established. Terminal renders as "Class C / OAK"; TTS speaks "the charlie / Oakland Airport".
- Solo training mode accepts typed natural-language ATC commands, syncs across rooms and recordings, persists across runs, and labels windows as Solo/RPO mode.
- Solo-training pilot voices use the optional Piper pack with per-aircraft speakers, radio effects, and logged TTS text for review. Custom-fix friendly names (e.g. `OAK30NUM` → "Oakland Runway 30 Numbers") and `FixPronunciations/*.json` entries (e.g. `VPCOL` → "Oakland Colliseum") flow into TTS instead of being spelled letter-by-letter. Pilot transmissions appear as delayed `SAY` radio transcripts (clear-of-runway, going-around, lost-sight-of-traffic, follow-target-landed, unable-to-catch-up, unable-to-exit, holding-short-crossing, no-clearance short-final reminder) while command confirmations stay immediate. Tower students now hear pilot speech that previously appeared only as orange terminal warnings.
- Solo-training readbacks now shorten safe altitude, heading, speed, and direct-to responses as frequency load rises.
- Scenario setup and the command-bar gear flyout pace solo parking call-ups by interval and adjust generated arrivals live.
- Solo-training pilots repeat pending taxi, departure, landing, approach, and airspace-entry requests until handled, waiting longer after `STBY`/`ROGER`.
- Solo-training pilots warn near DA/MDA and go around at published minimums if landing clearance is still missing.
- Solo-training pilots answer rejected live maneuver clearances with an audible `unable` readback and reason when available.
- Solo training adds a live Session Report with FAA-grounded separation scoring, coaching notes, approach outcomes, and runway statistics.
- The Session Report now scores actual same-runway separation losses for departures and arrivals using 7110.65 SRS rules.
- The Session Report now scores same-runway and close-parallel wake losses using 7110.65 CWT interval and mile-based minima.
- The Session Report now scores structured traffic advisories and safety alerts with `RTIS ...` and `SAFAL ...`, while RPO shorthand remains outside solo scoring.
- The Session Report now scores Class C IFR/VFR separation from FAA AIS airspace geometry and 7110.65 §7-8-3 instead of treating raw conflict-alert state as a scoring standard.
- Speech settings separate STT and TTS into collapsible panels, and TTS includes Piper voice-pack install and remove controls.
- Favorite commands now support a fixed-grid pop-out panel with categories, airport scope, colors, sizing, ground overrides, batch blanks, and drag rearranging.
- The user guide now explains compound-command sequencing, conditional triggers, speed `UNTIL`, and queue/phase clearing.
- A standalone Solo Training guide now explains pilot requests, pacing controls, safeguards, solo/RPO command differences, scenario-author notes, the recommended CRC + popped-out YAAT terminal layout, and Discord docs posting.
- Session Report opens as a non-modal window so the controller can keep working the radar while it refreshes.

### Fixed
- Command history stores entries uppercase and collapses case-only duplicates.
- Speed `UNTIL` accepts aliases and fires staged speed changes while earlier speed or phase-controlled work continues.
- Generated piston arrivals (Cherokee, Skyhawk) decelerate and touch down at piston speeds (~70 kt) instead of jet speeds and overshooting the runway.
- Generated airline callsigns match operator fleet — regional carriers no longer pair with widebodies, single-fleet carriers no longer get Cherokees.

## v0.1.17-alpha [2026/05/06]

### Highlights
- Up-arrow command recall now persists per scenario across restarts (last 50 entries) — switching scenarios swaps the recall list with it.
- Aircraft list right-click on a delayed spawn is now trimmed to **Spawn now**, **Change spawn delay** (preset offsets 15s–10m or custom like `2m15s`), and **Delete** — no control commands or flight plan editor until the aircraft actually spawns.
- VFR `ADD` cold-call spawns now behave like real cold calls: blank CID and BCN in the Flight Data editor, parking spawns sit on transponder **Standby** until taxi.
- ASDE-X now shows **Standby** aircraft as a primary-only cyan diamond (no callsign, no datablock) instead of a fully-correlated white icon.

### Added
- Up-arrow command recall now persists **per scenario** across client restarts (last 50 entries, newest first). Switching scenarios swaps the recall list with it; commands typed without an active scenario stay in memory only.

### Changed
- Aircraft list right-click on a delayed (deferred) spawn now shows only **Spawn now**, **Change spawn delay** (preset offsets 15s / 30s / 1m / 2m / 5m / 10m, plus a custom input that accepts `90`, `2m15s`, `1h30m`, etc.), and **Delete** — control commands, track/squawk/ask-pilot/coordination submenus, and the flight plan editor are hidden until the aircraft actually spawns.

### Fixed
- VFR `ADD` cold-call spawns no longer pre-fill **CID** or **BCN** in the CRC Flight Data editor — both stay blank until the controller files via `DA` / `VP`. Previously the editor showed the auto-assigned CID and `BCN 0000`.
- VFR `ADD` cold-call spawns at parking are now on transponder **Standby** until the pilot powers up for taxi. Airborne and on-runway VFR ADD spawns continue to squawk Mode C / 1200 (real-world airborne /1200 traffic is altitude-reporting).
- ASDE-X now shows **Standby** aircraft as a primary-only cyan diamond (no callsign, no datablock) instead of a fully-correlated white aircraft icon. Real ASDE-X can't correlate a target to a track without a Mode A/C reply.
- YAAT Client ground view shows **SqStby** (no strikethrough) under Standby aircraft instead of a struck-through `ModeC` label. Same indication, easier to read.
- `DA` / `VP` typed without an equipment suffix (e.g. `DA SR22 050`) now defaults the suffix to `/A` per FAA convention. Typing `SR22/G` still overrides to `/G` as before.
- Generated VFR N-numbers now match FAA format: 5 alphanumeric characters with at most two trailing letters (e.g. `N12345`, `N1234A`, `N123AB`).

## v0.1.16-alpha [2026/05/05]

### Highlights
- **Scenario → Edit Arrival Generators...** edits the live arrival-generator list during a session — add/remove/tune entries, **Apply** pushes to sim immediately, **Save As** writes a new scenario JSON.
- YAAT's flight-plan editor now mirrors CRC: beacon recycle button, Create/Amend label, equipment defaults to `A`, `RMK/` round-trip, input masks.
- `DA` / `VP` / beacon-reroll now only set the *assigned* beacon — the aircraft keeps squawking the previous code (e.g. 1200) until you issue `SQ`, matching real CRC behavior.
- `TAXI` issued to an aircraft already holding short of a runway now implicitly clears the first crossing of that same runway — no separate `CROSS` needed.

### Added
- **Scenario → Edit Arrival Generators...** edits the live arrival-generator list — add/remove/tune entries, **Apply** pushes to sim immediately, **Save As** writes a new scenario JSON.

### Changed
- YAAT's flight-plan editor mirrors CRC: BCN recycle button, Create/Amend label, EQ defaults to `A`, RMK/ round-trip, input masks.

### Fixed
- Cold-call aircraft (squawking 1200 with no filed flight plan) now show every editable field blank in the CRC Flight Data editor. Previously the editor synthesized `EQ=A`, `SPD=<current ground speed>`, `ALT=VFR/<current altitude>`, `RMK=/v/` so a cold target read like a filed VFR plan.
- DA / VP / beacon-reroll now only set the *assigned* beacon code; the pilot keeps squawking the previous code (e.g. 1200) until the controller issues `SQ` (or equivalent) and the pilot complies. FP-creator autotrack fires once the squawk match happens — matching real CRC behavior.
- DA / VP no longer fill the altitude, speed, or remarks boxes from the aircraft's current state when the controller didn't type those fields. The altitude box now follows CRC's grammar: `VFR` (VFR rules, no altitude), `VFR/045` (VFR rules + altitude), `045` (IFR + altitude), blank (IFR, no altitude).
- VFR `ADD` spawns now appear as cold calls — blank flight plan, squawking 1200 — matching scenario-driven cold calls; controllers file the FP via `DA` / `VP` after the aircraft is in the system.
- A `TAXI` command issued to an aircraft already holding short of a runway now implicitly clears the first crossing of that same runway — the controller no longer needs a separate `CROSS` to send the aircraft through. The taxi response acknowledges the implicit cross (e.g. *Taxi via B RWY 28L (cross 28R/10L)*); subsequent crossings on the new route still require explicit clearance.
- Strip and separator commands sent from the strips UI / CRC vStrips are now addressed by id — duplicate first-line text on half-strips no longer fails `HSD`/`HSA`/`HSO`/`HSS`/`HSM` with a multi-match error, and scanned full-strip copies (`STRIP_<callsign>_<short>`) can be deleted, offset, annotated, and pushed independently of their originator. Half-strip and separator ids are also shorter (`HSTRIP_aece26a3` / `SEP_d47c4d97` instead of 32-char GUIDs) so action logs and terminal output stay readable.
- Auto-accept no longer takes handoffs to a TCP that a CRC student is active on — the student receives the handoff and can accept it themselves.
- Aircraft taxiing out of OAK GA-ramp parking spots (GA3, GA7, etc.) no longer make a wide ~270° turn onto their first taxiway. The pathfinder previously picked the geometrically-shortest fillet arc to enter the named taxiway, even when that arc was authored for the opposite direction of travel and would land the aircraft heading 180° from the next walk step. It now prefers bridge endpoints whose first arc is traversed in its natural-forward bezier direction (or is a straight edge), so the aircraft turns the short way out of parking.
- Aircraft list **Type** column shows the physical aircraft type — instructor amendments that blank the filed flight plan no longer leave the column empty.
- `SALT` after a `CTO` with a bundled climb-to altitude (e.g. `CTO ... 014`) reports *"Leaving X for 1,400"* — the assigned altitude is mirrored onto the aircraft the moment the takeoff clearance is issued, and `CTOC` retracts it.
- Conditional `AT FIX` / `LV altitude` commands no longer cancel the active phase at dispatch — the wrapped instruction waits for the trigger to fire.
- Aircraft on a long final now slow progressively instead of holding intercept speed until ~5 NM and then bleeding to Vref in one shot. A configuration gate at ~5–7 NM commands `1.3 × Vref` first; the existing FAS gate at ~2 NM then commands Vref. Heavies spawned `OnFinal` at 12 NM (e.g. B763 at 224 KIAS) now settle at the configuration band by ~5 NM, matching real flap-extension and stabilized-approach pacing.
- Flight plans created or amended from CRC clients now record the correct FAA equipment suffix (e.g. `C182/A`) instead of garbage like `C182/L-DOV/C`.
- Flight-plan altitudes typed in CRC now accept `VFR`, `OTP`, `VFR/065`, `OTP/120`; flight rules switch from the prefix and CRC data blocks render correctly.

## v0.1.15-alpha [2026/05/05]

### Highlights
- One-click radar right-click for **Cleared visual approach**, **Join STAR**, **Join airway**, and pattern entries — uses the aircraft's assigned runway / filed route, with **(other)…** pickers for overrides.
- Aircraft list right-click is now phase-aware — taxi/cross/hold items on the ground, a **Tower** submenu (LUAW/CTO/CLAND/COPT/T&G/S&G/LA/GA) airborne, plus always-visible **Track**, **Squawk**, **Ask pilot to say**, and **Coordination** submenus.
- Radar right-click gains a **Pattern** submenu (TC/TD/TB, EXT, MSA/MNA, 360s, 270s, CA), a **Display** submenu (leader direction, J-ring, cone, blank target), and **Join radial inbound/outbound**.
- Pilot transmissions (RTIS/RFIS readbacks, SAY altitudes/headings/speeds) now land on the **SAY** channel and read in plain English — *"Have N9225L in sight"*, *"Heading 270, direct MENLO"*, *"Leaving 5,000 for FL240"* — without the redundant ownship callsign.

### Added
- Radar right-click → **Approach** → **Cleared visual approach {rwy}** is now one-click when the aircraft has an assigned runway or an active/expected approach (its runway is parsed from the CIFP procedure); a sibling **Cleared visual approach (other)...** picker lists every runway end at the destination. Falls back to free-text only when no runway data is loaded.
- Radar right-click → **Procedures** → **Join STAR {NAME}** is now one-click when a STAR is detected in the filed route; sibling **Join STAR (other)...** picker lists every STAR at the destination.
- Radar right-click → **Tower** / **Pattern** → pattern-entry items (Enter left/right downwind, left/right base, straight-in final) default to the aircraft's assigned runway in one click, with **(other)...** picker for runway override.
- Radar right-click → **Squawk** → **Squawk random** assigns a random discrete code (`RANDSQ`) without typing.
- Radar right-click → **Ask pilot to say...** submenu issues SAY-class commands: Altitude (`SALT`), Heading (`SHDG`), Speed (`SSPD`), Mach (`SMACH`), Position (`SPOS`), Expected approach (`SEAPP`), and a Custom… text entry that emits `SAY <text>`.
- Ground right-click on an aircraft *At Parking* → **Push back to...** submenu lists the closest 30 named parking/spot/helipad nodes by distance and issues `PUSH @<parking>` or `PUSH $<spot>` in one click.
- Aircraft list right-click is now phase-aware: ground phases show **Push back / Hold position / Resume taxi / Cross / LUAW / Cancel takeoff / Final-approach landing items / Exit left/right** as appropriate; airborne phases show a **Tower** submenu (LUAW, CTO, CLAND, COPT, T&G, S&G, LA, GA — runway in label when assigned). The list also gains always-visible **Track**, **Squawk**, **Ask pilot to say...**, and **Coordination** submenus mirroring the radar menu's one-click items.
- Radar right-click → **Display** submenu gains per-aircraft display controls: **Leader direction** (1–9 numpad), **J-ring** (Clear / 1 / 2 / 3 / 5 / 10 nm), **Cone** (Clear / 1 / 2 / 3 / 5 / 10 nm), **Blank target** (`BLANK`), **Unblank target** (`BLANKD`).
- Radar right-click → **Pattern** submenu gains in-pattern maneuver controls: **Turn crosswind / downwind / base** (`TC`/`TD`/`TB`), **Extend pattern leg** (`EXT`), **Make short approach** (`MSA`) / **normal approach** (`MNA`), **Make left/right 360** (`L360`/`R360`), **Make left/right 270** (`L270`/`R270`), **Plan 270 at next turn** (`P270`), **Cancel 270** (`NO270`), **Circle airport** (`CA`).
- Radar right-click → **Procedures** gains **Join radial outbound...** and **Join radial inbound...** items; pick a fix from the filtered list, then enter the bearing in a follow-up dialog (sends `JRADO FIX bearing` / `JRADI FIX bearing`).
- Radar right-click → **Procedures** → **Join airway {ID}** is now one-click when an airway is detected in the filed route; multiple filed airways become a submenu picker. A sibling **Join airway (other)...** free-text input handles airways not on the route. We never enumerate all CIFP airways — the global list is too large to be useful.
- Radar right-click → **Procedures** → **Cross fix** / **Depart fix** are now scoped to fixes on the filed route plus the active DCT queue, instead of enumerating every fix in CIFP. Sibling **(other)...** entries take a free-text fix name when the target isn't on the route.
- Ground right-click on a *Taxiing* aircraft gains **Follow...** and **Give way to...** submenus listing the closest 12 other ground aircraft by distance (sends `FOLLOWG <callsign>` / `GW <callsign>`).
- **Settings → Colors** gains a **Terminal Channels** section with a color picker per kind (Command, Response, System, SAY, Pilot Speech, Warning, Error, Chat); **Reset All Colors** restores them along with the existing ground/radar palette.

### Changed
- Radar **Tower** submenu and Ground **Final Approach** items (Cleared to land, Cleared for the option, Touch and go, Stop and go, Low approach, Go around, Line up and wait) show the aircraft's assigned runway in the label (e.g. *Cleared to land 28R*) — no behavior change, the item still issues the bare command.
- Radar **Speed** submenu values are now type-aware: the assignable speeds run from the aircraft's `ApproachSpeed` to its altitude-resolved `ClimbSpeed` in 10-kt steps (replacing the static 150–350 kt list). C172 sees ~60–100 kt; transport jets see ~140–280 kt at altitude. Falls back to the legacy 150–350 list when filed type is unknown.
- RTIS / RFIS pilot transmissions land on the **SAY** channel instead of WRN/RSP and drop the redundant ownship callsign: success reads as *"Have N9225L in sight"* / *"Have the field in sight"* (was *"N436MS, traffic in sight, N9225L"* / *"N436MS has the field in sight"*); the soft-fail readback (*"Negative contact, …, looking"*) is now also on the SAY channel.
- **`SPOS`** anchors the position readback to a fix the controller is already working with on the aircraft (departure / destination airport, filed route, active DCT queue) and reads it back with the published friendly name — e.g. *"10 miles southwest of OAK - Oakland VOR"*. When no working fix is within 50 nm, it falls back to the nearest sizeable airport (max runway ≥ 6,500 ft within 100 nm). Previously it could anchor on any obscure RNAV waypoint within range.
- **SAY readbacks** (SHDG, SALT, SSPD, SMACH, SEAPP) now emit plain numeric values — *"Heading 270, direct MENLO"*, *"Leaving 5,000 for FL240"*, *"250 knots"*, *"Mach 0.78"*, *"Expecting the ILS Z 19R approach"* — instead of baked-in digit-by-digit AIM phraseology. Spoken-form rendering (digit-by-digit speech, "thousand"/"hundred" forms, phonetic suffix letters) is now the RPO / TTS layer's job.
- **`vstripsweb`** is now opt-in in `start.ps1` / `start.sh`; previously it launched by default.

### Fixed
- `EF` to a parallel runway while on final now side-steps onto the new centerline instead of flying a 360 to rejoin the approach.
- `EF` issued to an aircraft already inside the standard intercept distance now flies a close-in entry instead of overshooting and re-intercepting.
- Aircraft joining downwind from the pattern side at a misaligned angle now read `midfield to {dir} downwind` instead of `crosswind` / `45`.
- Aircraft instructed `SA` (Make Short Approach) now lands cleanly on tight finals instead of going around: the base-leg descent now targets the glideslope-intercept altitude at the *rollout* point (one turn-radius further along the final than the projected anchor), and Landing floats level over the runway while the wings level out from the tight base→final turn before the stabilization gate engages.
- Parallel (`,`) tower-command compounds (e.g. `LUAW,CTO`) now surface every component's outcome on the terminal — previously only the first command's result appeared.
- Transparent compound commands (e.g. `RFIS,SQ 5000`) no longer print empty terminal lines for components that produce no message.
- Lowercase callsign prefixes in the command input (e.g. typing `346g spos`) now match aircraft just like uppercase; previously the parser rejected the whole string as a bad command.

## v0.1.14-alpha [2026/05/04]

### Highlights
- Right-click an aircraft on the ground → **Preset taxi route** issues SOP taxi commands in one click; ships with FLL and OAK starters.
- EuroScope tag owner cell: left-click takes control, right-click opens the RPO assignment menu (give to / accept handoff).
- Right-click → **Tower** submenu lets you pre-clear "Line up and wait" or "Cleared for takeoff" while the aircraft is still taxiing.
- Landing jets no longer coast off the runway end at high-density airports — rollout commits to a reachable exit, or brakes to a stop on the runway as a last resort.

### Added
- Ground View shows a small amber dot at every runway threshold when an aircraft is selected — left-click the dot to open the same "Taxi to runway" submenu produced by right-clicking a hold-short node, without hunting for the right node first.
- Ground View right-click menu now offers **Resume taxi** for aircraft in the "Holding In Position" phase (previously available only for "Holding After Pushback" / "Holding After Exit"; for HIP you had to type `RES`).
- Right-click an aircraft on the ground → **Preset taxi route** submenu issues per-airport SOP taxi commands in one click; ships with FLL's `T-T3-B → 10R` and OAK starters.
- Datablock now shows a struck-through `ModeC` line when an aircraft is squawking standby — radar full datablock, EuroScope tag, and ground datablock — to flag aircraft whose transponder isn't replying to Mode C interrogations.
- Right-click the command input → **Save as favorite…** to capture the current command text without re-typing; the existing add dialog opens with the command pre-filled.
- Speed flyout (EuroScope tag click + right-click menu) gains a "FAS - N kt" entry at the bottom (where N is the aircraft-specific final approach speed); selection dispatches `RFAS`.
- EuroScope tag owner cell: **left-click** takes RPO control (assigns the aircraft to you); **right-click** opens the RPO assignment menu (Take control / Give up control / Give control to *(submenu of room members)* / Unassign).
- EuroScope tag Destination field is now a click target — clicking enters draw-route mode (same flow as right-click "Draw route").
- **Settings > Display > Font Sizes** group: separate font-size knobs for radar datablock, radar tag flyouts, ground datablock, and ground labels (taxi/runway/node), alongside the existing aircraft-list font size.
- **Settings > Misc > Windows > Always on Top** gains a "Flight Strips" checkbox that pins all popped-out flight-strip windows above other windows.
- Condition autocomplete dropdown now lists `ATFN` and `ONHO` alongside `AT`/`LV`/`GIVEWAY`/`BEHIND`.
- Right-click → **Tower** submenu now offers **Line up and wait** and **Cleared for takeoff** while the aircraft is taxiing (not just at the hold-short), so trainers can pre-clear departures during busy traffic. The Cleared for Takeoff item opens a submenu with one-click variants (fly runway heading, on course, make left/right traffic, crosswind/downwind turns, 270s, 360 overhead) plus a Custom… input for arg forms like `LT 270` or `DCT BERKS`.

### Changed
- EuroScope heading-mode drag snaps to the nearest 5° — the live arc, the cursor label, and the dispatched `FH` value all use the same snapped heading.
- EuroScope tag owner cell renders just the assigned RPO's initials (no braces, no `*` shorthand). Aircraft with no RPO assignment show `--`.
- EuroScope altitude/speed flyouts open scrolled to the current value with a smaller visible window (~±5,000 ft / ±60 kt); the rest of the range is reachable by mouse-wheel/keyboard scrolling.
- EuroScope altitude flyout extended to FL400.

### Fixed
- Landing aircraft that would otherwise have coasted off the runway end (e.g. jets on FLL 10R) now commit to a comfortably reachable forward exit during rollout, and brake to a stop on the runway as a last resort if no exit is found.
- Right-click "Command" textbox now accepts typed input on radar, ground, and data-grid right-click menus — typing letters had been silently swallowed.
- Ground View auto-fits the airport on first scenario load with a fresh profile — no longer shows an empty view until you click RESET.
- Ground View saved per-scenario pan/zoom now actually persists across reconnects; previously the auto-fit on layout load silently snapped you back to the centroid every time.
- CRC controller list and STARS consolidation now scope to the room you joined; controllers from other rooms no longer appear in your list or in consolidation views.
- Conditional compounds (`AT`, `LV`, `ATFN`, `ONHO`, `GW`) show autocomplete and signature help when typed after a callsign (e.g. `SWA123 AT SUNOL FH `).
- Ground taxi at multi-corner fillets (e.g. FLL T/T4/C junction) no longer routes the aircraft through an embedded U-turn when the fillet's tangent chain has the existing "shorten" edge anchored at one end and the corner arc landing at the other. The fillet pass adds direct shorten edges from each opposite-end arc anchor, so the walker exits any corner and reaches the next segment without doubling back.
- Ground taxi at V-shaped taxiway connectors (e.g. FLL T4) no longer enters the wrong leg when the destination is on the other side of the apex. The pathfinder now looks ahead through the remaining named taxiways from each candidate transition node and picks the one that produces the shortest legitimate route to the destination, eliminating hairpin U-turns at the next taxiway.

## v0.1.13-alpha [2026/05/04]

### Highlights
- Terminal flags when a command cancels a phase or drops queued work — e.g. "N435C pattern to RWY 28R cancelled by CM 025" or "queue cleared by FH 270 (lost: DCT VPCOL)" instead of a silent loss.
- Modifier verbs (RFIS/RTIS, SQ, ID, SAY, APPS, EAPP, EXP, NORM, DELAT) no longer wipe queued instructions; RNS/DSR scope to speed only, FPH to lateral only.
- RES releases taxi hold-shorts including auto-added runway crossings — destination runway still needs CTO or LUAW.
- STARS DA/VP reject invalid or overlong callsigns — typos like "*T <fix>" no longer create stray flight plans.

### Added
- Orange terminal warning when a command silently cancels in-flight phase work — e.g. issuing `CM 025` to an aircraft on a pattern entry now surfaces "N435C pattern to RWY 28R cancelled by CM 025" so the RPO knows what was lost and can reissue. Recognises pattern and approach chains; falls back to the active phase name otherwise.
- Orange terminal warning when a command silently drops queued instructions — e.g. "N435C queue cleared by FH 270 (lost: DCT VPCOL)". Fires for both intentional clears (DEL/CAPP/CTO) and unintentional ones, giving visibility into queue churn.

### Fixed
- `RFIS`/`RTIS` no longer wipes a queued pattern entry (or any other pending command block) when the aircraft has no active phase — extends the v0.1.12-alpha fix to the queue path. Same protection now applies to `SQ`, `ID`, `SAY`, and the rest of the transparent-command set.
- `APPS`, `EAPP`, `EXP`, `NORM`, and `DELAT` no longer wipe queued instructions either — they're status/modifier verbs that should leave pending blocks intact.
- `RNS` (resume normal speed) and `DSR` (delete speed restrictions) now drop only queued speed blocks; queued lateral or altitude instructions survive.
- `FPH` (fly present heading) now drops only queued lateral blocks; queued altitude or speed instructions survive.
- CRC STARS shows aircraft inbound from high-elevation departure airports (e.g. KBLU at 5,284 ft).
- `ADD` spawning an IFR aircraft on final auto-fills the flight-plan destination with the scenario's primary airport.
- `RES` releases any taxi hold-short, including auto-added runway crossings — destination runway holds still require `CTO` or `LUAW`.
- STARS `DA`/`FP` reject callsigns with invalid characters or longer than 7 chars — `*T <fix>` and similar typos no longer create stray flight plans.
- Aircraft list no longer flickers when a scenario aircraft spawns with the same callsign as a manually-typed STARS flight plan (`VP`/`DA`).
- CRC STARS `DA` typed with a delta-prefix primary scratchpad (e.g. `` `OAK ``) now sets the field on the new flight plan.
- CRC STARS "DB" duplicate-beacon indicator no longer appears for VFR (1200) or special-purpose (7500/7600/7700) squawks; only shared discrete codes are flagged.

## v0.1.12-alpha [2026/05/03]

### Highlights
- **EuroScope-style interactive tags** — opt-in radar tag mode where every field is clickable. Click altitude/speed for a flyout picker, drag from `AHDG` to a point on the map for a live elastic-vector heading, click scratchpad/handoff for a text-entry popup. Enable in **Settings > Display > Radar Display**. See [USER_GUIDE.md](USER_GUIDE.md#euroscope-style-interactive-tags) and [docs/euroscope/pseudopilot.md](docs/euroscope/pseudopilot.md) for the full reference.
- Sim-initiated pilot transmissions (check-in, hold-short, going-around, midfield/short-final) can render as green pilot speech in the RPO terminal, with an optional audible chime.
- Terminal window has a Filter text box plus Shift+Click solo on category toggles.
- **Help → Command Cheatsheet** opens an HTML cheatsheet in your default browser — filterable, searchable, bundled next to the EXE for offline use.

### Added
- Radar tags get a 4-line EuroScope-style layout when EuroScope mode is on: callsign, type/destination, current+assigned alt/spd/hdg, and runway+scratchpads.
- Altitude flyout dispatches `CM`/`DM` based on whether the picked FL is above or below the aircraft's current altitude.
- Speed flyout dispatches `SPD` or `RNS` (Resume Normal Speed).
- Heading mode supports drag-to-confirm (press `AHDG`, drag past 4 px, release on the target point) and click-to-confirm (release without dragging, then left-click the map). Right-click or Esc cancels.
- Heading-mode preview draws a standard-rate turn arc plus straight-line vector and a `275M  3.2nm  0:48` label (heading magnetic / distance / ETA).
- Runway flyout lists every runway end at the relevant airport (departure if on ground, destination if airborne) sorted numerically; dispatches `RWY <designator>`.
- Scratchpad and handoff fields open text-entry popups with focused TextBox (Enter submits, Esc cancels) plus EuroScope-convention preset chips and an "Accept handoff" action when applicable.
- Squawk field opens a quick-action menu (VFR / Standby / Normal / Ident / Random Squawk).
- ASDE-X data block fields from CRC (`Y`/`H` scratchpads, callsign, beacon, category, type, fix) edit only the ASDE-X display, not STARS or scenario state.
- `ASDX*` commands let the RPO terminal and scenarios drive ASDE-X display state — scratchpads, suspend/terminate, alert inhibit, DB-field overrides.
- Optional Settings toggle renders sim-initiated pilot transmissions (RTIS/RFIS, hold-short, clear-of-runway, going-around, midfield/short-final reminders) as green pilot speech in RPO mode, with optional audible chime.
- Terminal window has a Filter text box that narrows entries by callsign, initials, or message substring.
- Shift+Click a Terminal category toggle to solo it; Shift+Click again restores the previous selection.

### Changed
- **Help → Command Cheatsheet** opens an HTML cheatsheet in your default browser — filterable, searchable, bundled next to the EXE for offline use.

### Fixed
- View → Strips → New Strips Tab... opens a facility picker so external bays (e.g. NCT from an OAK position) can open in their own tab.
- Status bar errors clear on the next successful command, so a stale rejection like "Unable, no arrival airport assigned" no longer lingers.
- `ERB <runway> <distance>` rolls the aircraft onto final at the specified distance from threshold.
- CRC Tower Cab keeps showing the aircraft type after an instructor blanks or changes it in the flight plan editor — Tower Cab is the out-the-window view, so it tracks the actual physical type. STARS, ASDE-X, the FP editor and flight strips still follow the filed FP.
- ASDE-X suspend/terminate/inhibit-alerts/tag commands from CRC now take effect on the display.
- The CTO inline signature hint matches the typed arguments — `CTO 020 150` shows the heading overload, not `CTO RH [altitude?]`.
- Scenario VFR aircraft with no `flightplan` field spawn squawking 1200 with no destination, so `DA`/`VP` file them like a real cold call.
- `ADD VFR` spawns squawk 1200 with no assigned beacon — file with `DA`/`VP` to assign a discrete code.
- Command input autocomplete and signature help follow the cursor; Tab-accepting a suggestion mid-edit replaces the active token and preserves the suffix.
- `VP`/`FP`/`DA` accept destination-only routes (`VP C172 5500 MOD` files KMOD), split equipment suffix from the type, and canonicalize FAA codes to ICAO.
- CRC's FP form popup matches typed STARS — same equipment-suffix split and FAA→ICAO canonicalization.
- Instructor terminal shows the raw STARS keystrokes a student typed for `DA`/`VP` and surfaces failed `DA`/`VP` attempts.
- STARS `DA`/`VP` flight plans for callsigns with no spawned aircraft no longer appear as "No altitude asgn" rows in the Aircraft List.
- `RFIS`/`RTIS` no longer pulls an aircraft off final approach — the in-sight flag updates without clearing the active phase.
- `FOLLOW` on a leader who's already on short final now sequences the follower behind in the leader's pattern instead of pointing it directly at the leader's position over the runway.

## v0.1.11-alpha [2026/05/03]

### Highlights
- Airborne aircraft check in on first contact in solo mode — IFR with callsign and altitude, VFR with position and intent.
- `CAPP` flies the published procedure turn at PT-anchor fixes (e.g. CCR for KCCR S19R).
- Type a callsign plus flight-data fields into STARS to create a DA or VP flight plan — auto-acquires the track once it squawks the assigned code.
- Cruise IAS is realistic at every altitude — a Mooney leveling at 1,400 ft holds ~175 KIAS instead of accelerating to 240.

### Added
- `AT` conditions also fire on ground entities — taxiways (`AT B`), named spots (`AT $5`), parking (`AT @TERM2`), and two-taxiway intersections (`AT B/C`).
- Airborne aircraft check in on first contact in solo-training mode — IFR with callsign/altitude, VFR with position-from-field plus intent (AIM 4-3-1).
- `CTOPP` takes departure modifiers like `CTO`: heading, `LT`/`RT`, `OC`, and `DCT`/`TLDCT`/`TRDCT FIX`, all with optional climb altitude.
- `CAPP` flies the published procedure turn at PT-anchor fixes (e.g. CCR for KCCR S19R) — outbound radial, 45°/180° course reversal, intercept inbound.

### Fixed
- `CAPPSI` rejects when the intercept angle exceeds 90° and a course reversal is published; request vectors or use `CAPP` for the full procedure.
- DCT-fix validation toggle persists in recordings — replays use the session's setting instead of the scenario default.
- `CAPP` for an approach with a hold-in-lieu of procedure turn executes the published hold at the IAF instead of flying past to a downstream feeder fix.
- `CAPP` no longer routes an aircraft already at a common-leg IAF backwards to a transition IAF (e.g. an aircraft at FAWNE getting `CAPP S19R` no longer turns ~64° right and flies 9 nm east toward REJOY).
- `CVA` joins downwind on the side the aircraft is already on, no longer routing it across the field through the departure corridor.
- Aircraft on visual approach short final no longer report "lost sight of the field" as the airport reference passes behind the nose.
- `CVA` is IFR only and uses a wider pattern at 2000 ft AGL with no parallel-runway deconfliction; VFR pattern entry uses `ELD`/`ERD`/`SI`.
- Offset approaches with a published FAC differing from runway heading (e.g. KSFO RNAV (RNP) Y RWY 10L) land on centerline instead of ~150 ft off.
- Aircraft cruise IAS is now realistic at every altitude — Mooney M20P leveling at 1,400 ft holds ~175 KIAS instead of accelerating to 240, CL60 spawning at FL280 starts at ~270 KIAS instead of 460.
- VFR departures filed below 1,500 AGL (short low hops) now exit the initial climb phase cleanly when they reach their filed cruise altitude, instead of being stuck in initial climb indefinitely.
- Students joining a room or activating/deactivating mid-session appear in the instructor's controller list immediately — peers see each other's state changes too.
- CRC lobby clients no longer receive controller, position, or consolidation data from other rooms — data lands the moment they're pulled into a room.
- `CTOC` after a mid-taxi `CTO` revokes the stored clearance and reinstates the destination hold-short; previously cleared crossings (`LV`/`RC`/`CROSS`) are left untouched.
- `CTOC` during line-up reverts the line-up — rolling takeoffs brake on the runway and hold; heavy/super aircraft complete the line-up first, then hold.
- ASDE-X destinations show CONUS airports in FAA form (`SFO`) instead of ICAO (`KSFO`), matching real CRC; fix-rule patterns accept either form.
- `--autoconnect` stops retrying as soon as you open File → Connect, pick File → Disconnect, or close the window — no more silent disconnects.
- Typing a callsign plus flight-data fields and pressing ENTER creates a STARS flight plan (DA or VP, discriminated by destination); duplicates return `DUP NEW ID`.
- Creating a STARS flight plan via VP or DA auto-acquires the aircraft's track to your scope once it's squawking the assigned code.
- `.AUTOTRACK <airport>` from CRC now grants you departure ownership even when the scenario default assigned the airport elsewhere; `.AUTOTRACK -<airport>` removes it from autotracking entirely.
- Departures spawning later in the scenario (delayed queue, generators) auto-print into the Ground bay or printer queue, matching the initial-spawn behavior.

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
