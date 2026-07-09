# Changelog

## Unreleased

### Fixed
- A VFR aircraft told to go around now re-enters the traffic pattern, levelling 300 ft below pattern altitude and turning crosswind past the departure end, instead of climbing straight out to 2,000 ft AGL. It rejoins on the side it was last assigned — a `MRT`/`MLT`, else the pattern it was already flying, else the runway's L/R side. `GA` with a heading or altitude (`GA 270`) still hands the climb-out to you.
- `MRT` / `MLT` issued to an aircraft that is already going around now turns it back toward the pattern instead of leaving it in the climb.
- A go-around into the pattern now levels at the airport's published pattern altitude for that runway, and honours a pattern altitude set with `MRT 28R 15`, rather than always using the aircraft category's default.

## v0.9.6-beta [2026/07/08]

### Highlights
- Holding short of a runway now works as instructed — `HS 28L` stops the aircraft on the entry side and overrides **Auto-cross runway** or an earlier `CROSS`, and `RES HS` applies all its targets or none.
- An aircraft following traffic landing ahead of it now extends its leg and waits until that traffic is on the ground and clear, instead of cutting in front or breaking off the follow.
- Spawned IFR aircraft squawk a realistic beacon code drawn from the facility's own code banks — no two aircraft share a code, and no aircraft is given one that raises a false alert on a controller's scope.
- `ADD` altitudes now use the same shorthand as every other altitude argument — `ADD VFR S P -360 15 035` spawns at 3,500 ft instead of 35 ft.

### Changed
- `FOLLOW` re-sequences the follower onto the traffic's runway when that traffic is landing a different runway; refused once established on base or final.

### Fixed
- `HS <rwy>` and `RES HS <rwy>` now stop the aircraft at the runway when **Auto-cross runway** is on.
- `HS <rwy>` holds on the entry side of the runway instead of stopping past it.
- `HS <rwy>` overrides an earlier `CROSS` for the same runway, and is rejected once the aircraft has entered it.
- A runway crossing cleared by `CROSS` while the aircraft is still taxiing toward it now transits the runway properly.
- `RES HS` with several targets applies all of them or none.
- `RES HS <taxiway>` stops the aircraft short of the intersection rather than on it.
- `RES CROSS <rwy>` / `RES HS <target>` now name the runway crossing or added hold-short in both the response and the pilot's readback (e.g. `RES CROSS 28L` responds "Resume taxi (cross 28L)" and the pilot reads back "resume taxi, cross runway 28L") instead of replying with just "Resume taxi".
- Aircraft List **Info** column warnings like "No landing clnc" now prepend to the aircraft's status instead of replacing it, so the phase stays visible.
- Parallel commands mixing a transponder verb with a ground or tower verb now all take effect, such as `SQ, SQNORM, PUSH TE T` at parking.
- An aircraft following traffic landing ahead of it extends its downwind, turning base only after that traffic is on the ground and clear.
- `FOLLOW` persists when the traffic is faster; only losing sight of the traffic breaks off the follow.
- `ADD` altitudes now use the same shorthand as every other altitude argument: `ADD VFR S P -360 15 035` spawns at 3500 ft instead of 35 ft. Full feet (`3500`) and AGL (`KOAK+010`) work too, on both the airborne and at-fix variants.
- `ADD` now rejects extra position arguments instead of silently dropping them — `ADD VFR S P @BERKS 0 035` reported a successful spawn while placing the aircraft at 0 ft.
- Spawned IFR aircraft now squawk a beacon code drawn from the facility's configured code banks — the same code filing a flight plan would assign — instead of a random one, and two aircraft can no longer be assigned the same code. Applies to `ADD`, the arrival generator, and scenario-file aircraft.
- Aircraft are never assigned a beacon code that raises a false alert on a controller's scope. The whole 7500-7777 block — the 7500 (hijack), 7600 (radio failure), and 7700 (emergency) series — plus the monitored VFR conspicuity codes (1202, 1203, 1255, 1276, 1277) and the military 5000-5062 block are now withheld; previously only codes ending in `00`, plus 7777, were.

## v0.9.5-beta [2026/07/08]

### Highlights
- Follow traffic now sequences on the upwind and crosswind legs — an aircraft told to follow traffic stays in trail on upwind and crosswind too, not just downwind, extending its leg instead of turning ahead of the traffic it's following.
- Unloading a scenario no longer crashes when the Ground view's tower-cab background image is shown.
- Runway-exit hold-short bars now sit at a consistent distance from the runway — high-speed exit taxiways no longer stop aircraft closer to the runway, and each runway's published hold-short distance is used where available.

### Changed
- A pattern aircraft told to **follow** traffic now sequences behind it on the **upwind and crosswind** legs as well as downwind — it extends its current leg to stay in trail instead of turning ahead of the traffic it was told to follow. It never turns on its own to break the hold: past a 4 nm extension it reports "extending … unable to turn" and keeps flying the leg until you turn or re-sequence it.

### Fixed
- Unloading a scenario no longer crashes the client when the Ground view's satellite (tower-cab) background image is shown.
- Runway-exit hold-short bars now sit at a consistent distance from the runway regardless of the exit taxiway's angle, so high-speed exits no longer stop aircraft closer to the runway.
- Hold-short distances now use each runway's published value from the airport data where available, instead of estimating from runway width for every runway.

## v0.9.4-beta [2026/07/07]

### Highlights
- **Adjust taxi speed** — issue `SPD {n}` to a taxiing aircraft to taxi faster or slower than normal; `SPD 0` restores the default.
- **Separate Departure and Arrival strip sections** — the flight-strip printer now splits departures and arrivals, each with its own Move to Bay and Move All to Bay button.
- **No more UI freezes in busy sessions** — sessions with many aircraft (especially with route overlays shown) no longer stutter with periodic multi-second freezes.
- **Smoother macOS graphics** — YAAT renders on Metal by default on Apple Silicon (lower CPU), with a new **Settings → Display → Graphics** selector to switch renderers.

### Added
- Taxi speed is now adjustable — issue `SPD {n}` to a taxiing aircraft to taxi at `n` knots, slower or faster than normal (`SPD 0` restores normal).
- macOS: a **Settings → Display → Graphics** selector to choose the graphics renderer (Automatic, Metal, OpenGL, or Software); takes effect after restart.
- A **Settings → Display → Scroll / zoom sensitivity** slider (10–100%) slows mouse-wheel and trackpad zoom on the Radar and Ground views, taming a too-fast Mac trackpad.

### Changed
- macOS now renders on Metal by default instead of OpenGL, reducing CPU use on Apple Silicon Macs.
- The flight-strip printer modal now shows separate **Departure** and **Arrival** sections, each with its own **Move to Bay** and **Move All to Bay** button.

### Fixed
- Busy sessions with many aircraft no longer cause periodic multi-second UI freezes, most noticeable with taxi-route or nav-route overlays shown.
- A taxi clearance holding short of a *taxiway* (e.g. `TAXI D C HS E RWY 28R`) now routes through that taxiway instead of detouring around it.
- An `ER`/`EL` exit side is now honored when followed by a bare `EXIT <taxiway>` — `ER` then `EXIT D` exits right at taxiway D.
- Scenarios that pre-assign departure strips to a bay now place them there as each aircraft spawns, rather than in the printer queue.
- Annotating (`AN`) or offsetting (`STRIPO`) a flight strip still in the printer is now rejected with a reminder to move it to a bay first.
- Appending `GIVEWAY <callsign>` to a taxi clearance now works — `TAXI A A1 1R GIVEWAY KLM605` (comma optional) taxis the route and gives way to that traffic.

## v0.9.3-beta [2026/07/07]

### Added
- App freezes are now recorded in `yaat-client.log` with their duration and memory state — attach that log (and, on macOS, any hang report) when reporting a UI lockup.

### Fixed
- An arrival cleared onto a STAR with a single-digit runway (runways 1–9, e.g. `JARR BDEGA 1R`) now flies the published runway-transition legs and shows them on the **Show nav route** overlay and STARS programmed-fix highlighting, instead of silently dropping the transition. Scenario aircraft spawned on such a STAR are fixed the same way. Previously only the zero-padded form (`01R`) resolved the transition.

## v0.9.2-beta [2026/07/06]

### Highlights
- **Show nav route on the radar** — right-click **Display → Show nav route** now draws the exact path an aircraft is flying (DME/RF arcs, holding racetracks, procedure-turn reversals, and departure climb legs included), with each fix labeled with its crossing altitude and speed restriction.
- **No aircraft is ever assigned a reserved squawk** — the emergency codes (7500/7600/7700), 7777, and any code ending in "00" are excluded at spawn, on `RANDSQ`, and when a flight plan is filed, so a departure can't spawn already squawking 7600.
- **Departures track onto the scope at the right time** — a departure is no longer auto-tracked the instant its wheels leave the ground; it becomes owned once it climbs onto the display, and a closed-traffic pattern aircraft is no longer handed to the departure controller.
- **Squawk VFR clears the beacon-mismatch flash** — telling an aircraft to squawk VFR (`SQVFR`/`SQV`) now stops the data block's assigned-code flash instead of pulsing the stale code indefinitely.

### Added
- The radar right-click **Display → Show nav route** (renamed from "Show flight path") now draws the exact lateral path an aircraft is flying — DME and RF arcs included, where it previously drew a straight line across the arc — and labels each fix with any crossing altitude and speed restriction, whether you set it with `CFIX` or it comes published on the SID, STAR, or approach the aircraft is flying.
- **Show nav route** now also draws an aircraft's active procedure geometry — holding-pattern racetracks, procedure-turn reversals, and departure-procedure coded climb legs — each labeled with its climb-to or crossing restriction (including DME-arc altitude windows).

### Fixed
- A DME arc (AF leg) with no charted turn direction no longer risks sweeping the long way around the navaid — the shorter arc is chosen — so aircraft on those procedures fly and display the correct curve.
- Placing a ghost track (unsupported data block) on an aircraft no longer leaves a stray blue dot at the aircraft's real position on CRC STARS.
- Aircraft are never assigned a reserved or non-discrete beacon code — the emergency SPCs 7500/7600/7700, 7777, and any block code ending in "00" — whether at spawn, on `RANDSQ`, or when a flight plan is filed. Previously a departure could spawn already squawking a code like 7600.
- The radar data block's beacon-code mismatch flash now stops once an aircraft is told to squawk VFR (`SQVFR`/`SQV`), instead of pulsing the stale assigned code against the aircraft's `1200` indefinitely. It resumes if a new beacon code is later assigned (e.g. recycled from the Flight Plan Editor). An assigned-but-not-yet-squawked code still flashes, as before.
- A departure is no longer auto-tracked to a radar position the instant its wheels leave the ground — it becomes owned only once it climbs onto the STARS display, past the acquisition floor. A closed-traffic pattern aircraft that never leaves the tower's airspace is no longer handed to the departure controller before it appears on the scope.
- Amending a flight plan or requesting a new beacon code in CRC's Flight Plan Editor for an aircraft whose radar track is owned by another position now shows `ILL TRK` in the STARS preview area instead of silently doing nothing.
- Clearing a field — including the departure or destination airport — in an existing flight plan through CRC's Flight Plan Editor now applies instead of being silently ignored.

## v0.9.1-beta [2026/07/06]

### Highlights
- **Continuous Range Readout groups on Center scopes** — create a CRR group with the `LF` command (anchored to a fix, FRD, lat/long, or a click); each grouped aircraft's data block shows its live range to the group.
- **Conflict Data Blocks for untracked intruders** — a Center scope now shows a `TFC` block for a squawking, untracked Mode-C intruder predicted to lose separation with one of your tracks, and your track's data block flashes.
- **Track edits are sector-protected on Center** — `QZ`, `QQ`, `QU`, and `AM` now reject another sector's track with `NOT YOUR TRACK` unless you own it (override with `/OK`, `/TT`, or `///`).

### Fixed
- A CRC controller on an ERAM (Center) position can now create Continuous Range Readout (CRR) groups with the `LF` command — anchored to a fix, FRD, lat/long, or a clicked location — and each grouped aircraft's full data block shows a Range Data Block with the live distance to the group. The CRR view's color menu and clear/delete round-trips work too. Previously `LF` was rejected and the CRR group topic was never published.
- ERAM (Center) scopes now show a Conflict Data Block (CDB) for an uncorrelated Mode-C intruder — a squawking, untracked target with no flight plan — when it is predicted to lose separation with one of your tracked aircraft: the intruder renders as a `TFC` block with its beacon code and reported altitude, and your tracked aircraft's data block flashes. Previously an uncorrelated intruder never triggered a conflict indication.
- A CRC controller on an ERAM (Center) position can no longer amend the flight data of a track owned by another sector: `QZ` (assigned altitude), `QQ` (interim altitude), `QU` (route direct), and `AM` (flight-plan amendment) now reject `NOT YOUR TRACK` unless you own the track. Override the same-facility check with `/OK` on `QU` or `/TT`/`///` on `QQ`; `QZ` and `AM` have no override, so take the track first (`QT`). An untracked target stays freely editable. Previously any sector could rewrite any track's altitude, route, or flight plan.

## v0.9.0-beta [2026/07/05]

### Highlights
- **ERAM (Center) keyboard commands now work** — a controller on a CRC Center position can hand off and accept tracks, set assigned and interim altitudes, amend routes and flight plans, edit data blocks, and assign beacon codes from the keyboard. Previously every ERAM keyboard entry was rejected.
- **Center scopes get conflict alerts and lifelike tracks** — ERAM data blocks flash on a short-term conflict alert, show target-history trails, and coast or freeze like a real en-route scope when an aircraft drops below radar coverage.
- **"Rewind to this moment" in the terminal** — right-click any terminal line to scrub a replay to the moment that command, chat, or radio call happened; loading a recording now repopulates the full terminal history so every line stays scrubbable.
- **Radar data block flags a beacon-code mismatch** — when an aircraft squawks a code other than the one assigned, the block shows the reported code with the assigned code pulsing beside it, matching CRC STARS.

### Added
- Right-click a terminal line → **Rewind to this moment** scrubs the replay to the scenario-second that command, chat, or SAY happened.
- The terminal header's timestamp button cycles each line's time display between wall-clock, scenario-elapsed, and both.
- Loading a recording or bug bundle repopulates the full terminal history — including chat — so every line stays scrubbable.

### Fixed
- `CM 020` and other commands whose verb appears inside a live callsign (e.g. `CMD2`) now apply to the selected aircraft instead of failing with "matches multiple aircraft".
- `CLANDF` (force landing) now works on an aircraft that is going around, cancelling the go-around and bringing it back down onto its assigned runway.
- `MLT`/`MRT` to an aircraft on the upwind leg no longer cuts it left across the field — it continues upwind and turns for the requested side.
- An aircraft with a VFR plan to only a destination can now be assigned a runway and cleared for takeoff from its parked airport.
- The `ADD` command's `H` engine token now spawns a helicopter instead of failing with "Invalid engine type 'H'". `ADD V S H @H1` drops a light civil helicopter (R22/R44/B06) on helipad or parking spot H1; name a type to override it (e.g. `ADD V S H @H1 H60`). The weight token is cosmetic for helicopters.
- The `ADD` command's weight-token hint now reads `S/S+/L/H` instead of the incorrect `H/J/L/S`.
- A CRC controller working an ERAM (Center) position can now hand off tracks from the keyboard: type the receiving sector ID before an aircraft's FLID to initiate a handoff, a bare FLID to accept an inbound handoff or recall an outbound one, or `/OK` before the FLID to force-take a track owned by another sector. Previously every ERAM keyboard handoff was rejected, even though the underlying handoff logic already existed.
- ERAM implied commands now work on a Center position instead of returning FORMAT: `//` toggles an aircraft's on-frequency indicator, a `1`–`9` repositions its data block, `/0`–`/3` sets its leader-line length, and a bare FLID cycles the data block between a limited and a full data block.
- `QX <FLID>` now drops an ERAM track (only the owning sector, or any sector with `/OK`), and clears the track's pending handoff state cleanly. Dropping previously worked only through a non-standard form that left a stale handoff behind.
- ERAM `QZ` and `QQ` altitude commands were swapped: `QZ` now sets the assigned (flight-plan) altitude — including `QZ VFR` and `QZ OTP` — and `QQ` sets the interim altitude, with `QQ R`/`L`/`P` for reported/local-interim/procedure altitudes and the bare forms to clear them. Previously each set the other's altitude field.
- An ERAM full data block no longer lingers as a ghost after its aircraft is removed — the data block's create and delete now use the same identifier, so CRC can clear it.
- ERAM data blocks now render per sector: a track owned by another sector shows as a limited data block instead of a full data block, and Quick Look (`QL`) promotes a quick-looked sector's tracks (and an inbound handoff) to full data blocks. Previously every sector saw every track as a full data block.
- ERAM (Center) data blocks now flash on a short-term conflict alert, and a controller on a Center position receives the conflict-alert list, when two tracked aircraft are predicted to lose separation within the next four minutes — using the en-route standard (5 miles laterally, or 3 miles at or below FL230, and 1,000 feet vertically) and taking each aircraft's assigned/interim data-block altitude into account. Alerts are scoped to your own ERAM facility. Previously ERAM published no conflict alerts at all, so its data blocks never flashed.
- An ERAM interim, procedure, or local-interim altitude entered with `QQ` now shows at the correct flight level in CRC. The value was stored in feet instead of hundreds of feet, so `QQ 150` rendered as a nonsensical altitude on the data block.
- Removing an aircraft that was in a conflict alert now clears that alert from every CRC controller in the room, not just one of them. In a room with multiple controllers the alert could otherwise linger on the others' STARS/ERAM displays until they reconnected.
- ERAM `QZ` now accepts a block altitude in the `<floor>B<ceiling>` form (hundreds of feet, e.g. `QZ 200B250` for a FL200–FL250 block), shown as `200B250` on the aircraft's data block. The floor must be below the ceiling. Previously only a single assigned altitude could be entered and the block form was rejected as a format error.
- ERAM `QR <altitude> <FLID>` now sets the controller-entered reported altitude (CERA) shown on the data block, instead of toggling the route-line display. `QR` was mis-wired to route display (which is `QU`), so a Center controller could not enter a reported altitude at all.
- ERAM radar targets now report ground speed to CRC — it was previously blank on the ERAM target.
- The ERAM `QN` leader-line command now accepts the numeric keypad direction (`1`–`9`) that CRC sends, in addition to the spelled-out compass point (`N`, `NE`, …). A numeric direction was previously rejected as a format error.
- ERAM point-out acknowledge and clear now work from the keyboard: the receiving sector acknowledges a point-out with `QP A <sector> <FLID>`, and the initiating sector (not the receiver) clears an acknowledged point-out — the clear authorization was backwards.
- A received ERAM point-out now forces the aircraft's data block to a full data block, so its yellow P / white A indicator is visible even on a track you don't control; `QP <FLID>` minimizes it back to a limited data block. A point-out to another ARTCC's sector (e.g. `ZLA15`) is now attributed to that facility instead of your own.
- ERAM commands now accept a typed FLID — an aircraft's callsign, computer ID (CID), or assigned beacon code typed after a verb (e.g. `QQ 150 234`) — instead of only a slewed data block. Previously a typed FLID was rejected, and a FLID resolved by callsign only.
- ERAM radar targets now render the correct symbol from the aircraft's transponder and the facility's ERAM configuration (conflict-alert floor, single-sensor ASR sites) instead of always a correlated beacon: a standby transponder shows a primary target with no Mode C altitude, an identing aircraft shows the ident symbol, and uncorrelated traffic distinguishes a Code-1200 VFR target, an uncorrelated beacon, and a Mode C intruder. A correlated target within single-sensor (ASR) radar coverage shows the reduced-separation symbol. A special-purpose beacon code (hijack 7500, radio-fail 7600, emergency 7700, ADIZ 1276, UAS lost-link 7400, AFIO 7777) now blinks its data-block indicator.
- ERAM (Center) scopes now show target-history trails — the row of past radar-return positions behind each target, styled by the target's symbol and limited by the display's history count. Previously ERAM tracks had no history trail.
- ERAM (Center) tracks now coast and freeze like a real en-route scope. When a tracked aircraft drops below Center radar coverage or disconnects, its flight-plan track keeps showing for 24 seconds as a coast track (**CST** in the data block) that dead-reckons forward along its last heading and speed — the radar target disappears while the track and data block remain — before it is removed; the track resumes normally if the aircraft climbs back into coverage. `QH F <location> <FLID>` freezes a track at a location (**FRZN**), parked and unpaired from its target and exempt from coasting, and starting track on it — the `TRACK` command or CRC's `QT` — unfreezes it. Previously ERAM tracks were always drawn as normal and never coasted or froze.
- A coasting radar track now dead-reckons on both STARS and ERAM: after losing radar coverage it continues straight ahead along its last heading and speed (matching real radar) instead of following the aircraft's true position, and snaps back to the live target when it is reacquired. Its history trail (the past-position dots behind it) is dropped for the duration of the coast, since with no returns the trail would otherwise reveal the aircraft's real path. Previously a STARS coast track tracked the aircraft's actual position while it was below coverage.
- An ERAM (Center) controller who hands a track off now sees a handoff-accepted confirmation on the data block: once the receiving sector accepts, the transferring controller's data block briefly shows `O` followed by the accepting sector (`OUNK` when it is off-facility), or `K` when the track was force-taken with `/OK`, then reverts to a limited data block after 30 seconds. Previously the transferring controller got no on-scope confirmation that the handoff had been accepted.
- The ERAM (Center) `QU` route command now draws an aircraft's actual filed route as a line through its fixes, out to the requested number of minutes (`QU 30 UAL123`) or all the way to the destination with `/M` (marked with an underlined X). `QU <FLID>` toggles a single aircraft's route line, bare `QU` clears the sector's route lines, several aircraft can be requested at once (`QU 30 UAL123/AAL456`), and lines auto-remove 30 seconds after the last one was drawn. Previously `QU` drew only a straight dead-reckoning line off the aircraft's heading.
- `QU <fix> <FLID>` on a Center position now amends an aircraft's route direct to the specified fix (or a left-clicked location): it drops the fixes before the target, inserts a fix-radial-distance for the aircraft's present position, and keeps the rest of the filed route. Previously `QU` could not amend a route at all.
- A user kicked from a training room can no longer rejoin it on their own from the room list — they stay out until an instructor pulls them back in. A kicked user drops into the room's lobby so the instructor can pull them back with the existing Pull button. The room's creator can no longer be kicked, so a host can't be locked out of the room they created. Previously a kicked user could immediately rejoin from the room list.
- A Center controller can now amend a flight plan from the ERAM keyboard with `AM`: `AM <FLID> TYP`/`BCN`/`SPD`/`ALT`/`RMK` (or the field numbers `3`/`4`/`5`/`8`/`11`) edits the aircraft type, assigned beacon code, filed cruise speed, assigned altitude, or remarks, and `AM <FLID> RTE …` splices the route — replace a leg with a dotted fix list, or use `[`/`]` to swap the departure/destination airport. `AM <FLID>` on its own reads the flight plan back. Previously every `AM` was rejected as a format error.
- `VP <aircraft type> <route> <FLID>` now files a VFR flight plan for an existing Center radar track that has none, and `DM <FLID>` (activate flight plan) is accepted rather than rejected — YAAT flight plans are already active, so it is a graceful no-op. Both previously returned a format error.
- `QB` now assigns a beacon code, equipment suffix, or voice type from the ERAM keyboard: `QB <FLID>` auto-assigns a discrete code, `QB <code> <FLID>` assigns a specific code, `QB <suffix> <FLID>` sets the equipment suffix, and `QB /v`/`/r`/`/t <FLID>` sets the pilot's voice type (full voice / receive-only / text-only). Previously the `QB` keyboard command was rejected as a format error.
- A pilot who files receive-only or text-only in the flight-plan remarks (`/r/` or `/t/`) now shows the correct voice type on the ERAM data block, with full voice assumed when no marker is present. The voice type stays in sync with the remarks whenever they are edited.
- The ERAM (Center) `QS` command now edits the data block's fourth-line heading/speed/free-text (HSF) fields instead of the STARS scratchpad: `QS 270 <FLID>` sets an assigned heading (a 3-digit magnetic heading), `QS /250` (or `QS /M82` for a Mach number) sets an assigned speed, `` QS `<text> `` sets free text, and `QS */` / `QS /*` / `QS *` delete the heading / speed / all of them. A bare `QS <FLID>` toggles the fourth-line display. Previously `QS` wrote the STARS scratchpad and the ERAM heading and speed fields were always blank.
- A Center controller can now toggle a separation halo (distance reference indicator) around a target from the ERAM keyboard: `QP J <FLID>` draws the standard 5-mile circle and `QP T <FLID>` the reduced-separation 3-mile circle; repeating the command removes it. Previously `QP J` / `QP T` were misread as a point-out to a sector named "J" or "T".
- The ERAM `LA` / `LB` / `LC` track-range commands now return a readout on a Center position instead of a format error: `LA` gives the distance and magnetic bearing between two points (adding ground speed and flying time when the first is a track, or a true bearing with `T`), `LB` does the same between a fix and a location or track, and `LC <fix>/<time> <track>` gives the ground-speed adjustment needed to cross a fix at a specified Zulu time.
- The ERAM `QQ` interim-altitude command now accepts its `/TT` and `///` logic-check overrides, and a slash-separated FLID list (`QQ 110 UAL123/AAL456/DAL789`) to set the same altitude on several tracks at once. Previously the override tokens were not recognized and a multi-aircraft entry affected only one track.
- An ERAM data block now shows the non-RVSM indicator based on the aircraft's filed equipment suffix — only `/L`, `/Z`, `/W` and the ATC failure suffixes are RVSM — instead of treating every aircraft as RVSM-capable.
- An ERAM Top-Down (Center) ground target no longer depicts a Boeing 757 with the heavy aircraft icon, matching real ERAM (unlike ASDE-X). Other heavy types are unaffected.
- Fix-radial-distance (FRD) positions are now referenced to magnetic north instead of true north, matching real-world VOR-radial convention (7110.65 §4-4-3). Radar-cursor FRD readouts, pilot position reports, and the ERAM route-amendment anchor now show the magnetic radial, and scenario aircraft placed by a fix-radial-distance now spawn at the correct magnetic bearing — roughly 13–14° off from before on the US West Coast.
- Autotrack now recognizes a departure filed under either its FAA or ICAO identifier for every airport — including non-CONUS airports such as Anchorage (ANC/PANC), Honolulu (HNL/PHNL) and San Juan (SJU/TJSJ) — not just the CONUS `K` prefix. Previously a mismatch between the filed identifier and the autotrack-list identifier skipped auto-tracking outside the lower 48.
- The built-in flight plan editor's **BCN** box now shows the aircraft's assigned squawk, and updates live when you recycle the beacon (↻) or file a new plan while the editor is open. Previously it stayed `0000` even after a squawk was generated and shown on the strip.
- A bare `FOLLOWF` or `RTISF` issued while a traffic call is still pending now follows (or acquires) that called traffic instead of rejecting.
- The radar data block now flags a beacon-code mismatch — when an aircraft squawks a code other than the one assigned to it, the block shows the reported code followed by the assigned code pulsing beside it (on both the STARS data block and the EuroScope tag), matching CRC STARS. It catches a VFR aircraft still on 1200 after being assigned a discrete code, or a wrong code dialed in; it stays hidden while the transponder is standby/off and for emergency/special codes (7500/7600/7700).
- A departure filed on a fixed-path RNAV SID whose route lists the departure airport's own on-field navaid right after the SID (e.g. `HUSSH2 OAK SYRAH` at OAK) no longer turns back over the field after takeoff. The departure route now drops that redundant on-field navaid instead of flying to it, and when the next fix names a published enroute transition it flies the full transition (for `HUSSH2 ... SYRAH`, the SYRAH transition via REBAS and TAMMM).

## v0.8.10-beta [2026/07/03]

### Highlights
- **Taxi routes on the ground view** — draw an aircraft's taxi route on the ground view: on hover, for every taxiing aircraft at once, or pinned per aircraft via the right-click **Taxi route** submenu.
- **ASDE-style fix on the surface display** — the ground datablock and a new Aircraft List **Fix** column now show a departure's exit fix (or the destination), matching a real tower surface display.
- **Airport layouts pick up vNAS map corrections** — updated taxiway layouts published to vNAS now reach the simulation mid-session, instead of staying pinned to the first copy downloaded.
- **Fewer crashes** — looking up an unknown callsign in CRC's flight plan editor, removing an aircraft from the list, or an unexpected error no longer closes the client.

### Added
- **Taxi routes on the ground view.** Two new **Settings > Display** options control when an aircraft's taxi route is drawn on the ground view: **"Show taxi route when hovering an aircraft"** (on by default) temporarily draws the route of whichever aircraft the mouse is over, and **"Show all taxiing aircraft's routes"** (off by default) draws every taxiing aircraft's route at once. Right-click an aircraft and use the **Taxi route** submenu to override one aircraft — **Always show**, **Always hide**, or **Follow "Show all"** — independently of the global setting.
- **Warp a ground aircraft to a taxi spot.** `WARPG $9` now teleports a ground aircraft to taxi spot 9, matching the `$spot` form `TAXI`/`PUSH` already accept. `WARPG` previously took only two taxiway names, a node id (`#42`), or a parking (`@B12`).
- **Ground-view command hints on hover.** Hovering a taxi spot or parking node on the ground view now shows the token that references it in a command — `$9` for taxi spot 9, `@F7` for parking F7 — so you can read straight off the map how to `TAXI`/`PUSH`/`WARPG` to it.

### Changed
- The Ground View datablock — and a new **Fix** column in the Aircraft List — now show the ASDE-style fix instead of the destination airport: a departure's exit fix or the destination, chosen per the airport's ASDE-X/SAID facility configuration (falling back to the destination when no fix rule applies). This matches how a real ASDE-X / tower surface display labels each target.

### Fixed
- Looking up a callsign with no flight plan in CRC's flight plan editor — including an unknown or mistyped callsign — no longer crashes CRC.
- Fixed a crash that could occur when an aircraft was removed from the aircraft list.
- An unexpected error on the app's UI thread no longer closes the client — it is logged and the app keeps running.
- Departures lining up for takeoff now follow the taxiway and its painted lead-on line onto the runway centerline, instead of cutting the corner across the taxiway/runway junction (e.g. Oakland RWY 33 from taxiway C).
- Scenario preset strip commands (e.g. auto-checking a departure-strip box shortly after spawn) now apply to the aircraft's strip.
- Track commands scheduled behind a `WAIT` — scratchpad, temporary altitude, and similar — now apply when the wait expires.
- A single-taxiway clearance to a terminal gate (e.g. SFO `TAXI B @F1`) now routes directly to the ramp instead of looping the long way around.
- An aircraft leaving a ramp spot on a clearance via multiple taxiways (e.g. an Oakland GA parking given `TAXI D C B 28R`) now taxis straight out onto the first taxiway instead of doubling back through the ramp and spinning.
- A taxi clearance whose two named taxiways meet only through a connecting taxiway (e.g. SFO `TAXI A B1`, where A and B1 are bridged by Q) now taxis through the connector (`A Q B1`) instead of dropping the named taxiways and routing a different way.
- Airport ground layouts now pick up map corrections published to vNAS instead of reusing the first-downloaded copy indefinitely — the freshness check relied on an HTTP method the vNAS server rejects, so an updated taxiway layout never reached the simulation until the on-disk cache was cleared by hand.
- Other cached vNAS data — video maps and ARTCC facility configs — likewise now refreshes when it changes server-side instead of staying pinned to the first copy for the whole session; their in-memory caches previously never re-checked.
- A VFR aircraft cleared for a pattern-exit departure (`CTO MRC`/`MRD`/`MLC`/`MLD`) with no filed cruise altitude and no assigned altitude now climbs to pattern altitude instead of descending straight back into the ground after liftoff.
- An aircraft joining a traffic pattern after crossing midfield (e.g. `ELD`/`ERD` to a runway on the far side, or a cross-runway closed-traffic departure) now re-intercepts the correct downwind track instead of holding a too-close downwind and overshooting the turn to final onto the parallel runway.

## v0.8.9-beta [2026/07/03]

### Highlights
- **Realistic touchdown points** — aircraft now land at a proper aiming point down the runway (light aircraft near the numbers, turboprops in the touchdown zone) instead of right on the threshold.
- **Smoother, realistic taxi turns** — aircraft slow down for corners and turn at a speed-appropriate rate instead of whipping the nose around or braking hard on the far side.
- **Taxi and pushback routing goes direct** — routes to terminal gates and ramp spots no longer loop the wrong way around, even through complex intersections.
- **Arrivals slow to final approach speed at varied distances** — instead of every arrival reducing at the same ~2 NM point, the arrival stream spreads out like the live network.

### Changed
- Pushing an aircraft onto a ramp spot (`PUSH $7A`) now lines it up straight on the marking the way a tug does — reversing past the spot then pulling forward so the nosewheel sits on the mark, ending nose-out toward the parent taxiway ready to taxi. Previously it stopped centered on the spot at an arbitrary heading, jutting a half-fuselage toward the adjacent taxiway. Add `FACE <dir>` to override the facing.
- **Arriving aircraft now vary how far out they slow to final approach speed.** Instead of every arrival reducing to its final approach speed at the same tight ~2 NM from the runway, each aircraft picks its own distance — most settle around 2–3.5 NM, but some slow to final approach speed as far as 5 NM out, reproducing the live-network spread where virtual pilots reduce early and compress the arrival stream. The distance is fixed per aircraft and reproduces the same way in replays.

### Fixed
- A taxi clearance to a terminal gate via named taxiways (e.g. SFO `TAXI B K A @F10`) now routes directly instead of looping the long way around.
- Pushing an aircraft back to a spot or gate (`PUSH $5A`, `PUSH @B13`) now reverses straight there instead of routing onto an adjacent taxiway and back.
- Taxiing via a taxiway through a complex intersection no longer sends the aircraft the wrong way. Previously, e.g. `TAXI C D @NEW1` at Oakland could turn an aircraft the wrong way onto C — away from the parking — looping across a runway before doubling back to the spot; it now heads directly to the destination.
- Aircraft taxiing a lane change across a short cross taxiway between two parallel taxiways (e.g. SFO `A F1 B`) now round the turns smoothly along the taxiway fillets and flow through at a steady low speed — instead of pivoting square at the junction, aligning with the connector, then accelerating and braking hard for the second turn.
- Aircraft no longer accelerate through a taxiway corner and brake hard on the far side; a taxi turn is now held to a safe cornering speed for its radius the whole way around.
- Landing aircraft now touch down at a realistic aiming point down the runway instead of right on the threshold — light single-engine aircraft land near the numbers and turboprops land in the touchdown zone, rather than on the threshold marks. A high or rushed approach that reaches the threshold still too high to land now hands off to the flare/go-around logic instead of continuing straight past the runway and climbing away.
- Aircraft no longer whip their nose around at unrealistically high rates when turning at low taxi speed — ground turn rate is now coupled to how fast the aircraft is actually rolling, so a near-stationary aircraft turns slowly (a 120° turn takes several seconds, not ~2) and jets pivot more ponderously than light aircraft. Aircraft also no longer carry full taxi speed through tight ramp/fillet corners.
- An aircraft taxiing to a ramp parking spot now stops with its nose at the spot marking instead of centered on it, so it no longer juts a half-fuselage toward the adjacent taxiway and slows traffic taxiing past (e.g. SFO's tightly-spaced spot 7 ramp off taxiway A).

## v0.8.8-beta [2026/07/02]

### Highlights
- **Find in flight strips and vTDLS** — press Ctrl+F to search every visible field and jump between matches with F3.
- **Import and export favorite command buttons** — share your favorites as `.yaat-favorites.json` files to hand presets to other controllers.
- **One-step forced visual approach and follow** — `CVAF` / `FOLF` clear a visual approach or a visual follow without a prior "field in sight" / "traffic in sight" report (RPO-only).
- **Visual approaches now require the field in sight** — `CVA` is rejected until the pilot reports the airport with `RFIS`; when following traffic, reporting that traffic in sight is enough.

### Added
- **Forced visual-approach and follow clearances.** `CVAF <rwy>` (`VISUALF`) and `FOLLOWF [callsign]` (`FOLF`) clear a visual approach or a visual follow in one step, without first getting the pilot to report the field or traffic in sight — they fold the `RFISF`/`RTISF` in. Both are RPO-only and rejected in solo training, matching `RFISF`/`RTISF`.
- **Find in the flight-strips and vTDLS windows.** Press **Ctrl+F** to open a find bar; matching entries are highlighted and the view scrolls to the current match. **F3** / **Shift+F3** (or **Enter** / **Shift+Enter** in the find box) jump to the next / previous match, and **Esc** closes it. The search matches every visible field — callsign, route, remarks, beacon code, annotations, SID — not just the callsign. In the flight-strips window it searches the bay you're viewing.
- The flight-strips rack view now sticks to the bottom: when you're scrolled to the bottom and a new strip arrives, the view stays pinned to the bottom so the newest strip stays visible instead of being pushed below the fold. Scrolling up to review older strips leaves the view where you put it.
- Import and export favorite command buttons as `.yaat-favorites.json` files to share them — export all or the current tab, append or replace on import.

### Changed
- **A visual approach (`CVA`) now requires the pilot to have the airport in sight first** (7110.65 §7-4-3) — report it with `RFIS` (or force it with `RFISF`/`CVAF`), otherwise the clearance is rejected with "Field not in sight — issue RFIS first". When following traffic (`CVA … FOLLOW`), reporting that traffic in sight is enough (the pilot need not also see the field), and following a **super** is refused. Previously `CVA` was accepted regardless and a missing report only showed up as an advisory in the solo-training report.

### Fixed
- Creating a flight plan to a different airport for a parked aircraft no longer breaks its taxi commands with "Cannot find taxiway … in layout".

## v0.8.7-beta [2026/07/02]

### Added
- Help → About now has an optional **Support the dev on Ko-fi** link, with a note making clear that YAAT is free and donating is entirely optional.

### Fixed
- Requesting a flight strip that already exists prints a duplicate that stacks in the printer, so a lost or late strip can be reprinted.
- Amending a flight plan prints a new revised strip and clears stale printer copies, leaving strips already filed into a bay untouched.

## v0.8.6-beta [2026/07/02]

### Highlights
- **Call for Release (CFR) windows** — release a departure around an assigned Zulu time with the FAA −2/+1 min compliance window, a live countdown badge, and an amber alert if it departs outside it.
- **Per-aircraft command recall** — with an aircraft selected, Up/Down in the command line walk only the commands you sent to that aircraft (plus global ones like `PAUSE`); remembered across restarts.
- **Terminal strip-traffic channel (STRP)** — a new toggle hides flight-strip command echoes and their feedback in one click, with its own customizable color.
- **Re-route a holding or following aircraft** — right-clicking one that's holding in position or following another on the ground now offers Preset/Draw taxi route without resuming its taxi first.

### Added
- **Call for Release (CFR) release-time window.** `CFR <HHMM>` releases the selected departure with the FAA −2/+1 min compliance window around the assigned Zulu time (7110.65 §4-3-4.e.5); bare `CFR` releases immediately, `CFR OFF` clears it, and `CFR CHECK` prints the window status. It never blocks a takeoff — if the departure gets airborne outside the window, or is still holding when it expires, YAAT posts an amber instructor warning (plus an aircraft bubble when warning bubbles are on). While a window is active the Aircraft List shows a live `CFR M:SS` countdown badge in the Info column. The window tracks real-world time, so it's unaffected by pausing or scrubbing the timeline.
- Right-clicking an aircraft that is holding in position or following another aircraft on the ground now offers **Preset taxi route** and **Draw taxi route...**, so you can re-route it from its current position without first resuming its taxi. These options were previously available only for parked, actively-taxiing, and holding-short aircraft.
- Command recall (Up/Down arrow in the command line) now filters to the selected aircraft: pressing Up walks through only the commands you sent to that aircraft, plus global commands like `PAUSE`. With no aircraft selected, recall shows every command as before. Recall is remembered per aircraft across restarts.
- The terminal has a new **STRP** channel toggle (alongside CMD/RSP/…) that hides flight-strip command echoes and their feedback in one click, so routine strip traffic no longer buries requests and commands. Strip lines get their own color (customizable under Settings → terminal colors), and Shift+Click solos the channel like the others.

### Fixed
- Undoing a terminal channel solo (Shift+Click to restore) while scrolled to the newest line no longer jumps the terminal to the top — it stays pinned to the bottom. This also applies to any terminal filter change made while scrolled to the bottom.
- Saving Settings no longer resets a customized **vTDLS** terminal color back to its default. The Settings dialog was not loading the saved vTDLS color, so opening Settings and saving overwrote it with the default.
- Pushing an aircraft back from a gate no longer stalls when another aircraft is parked at the adjacent gate. Ground-conflict avoidance treated the parked neighbor as a hazard and pinned the pushback to a stop, forcing the controller to repeatedly issue `BREAK` to inch it out. A pushback now clears an aircraft parked at an adjacent gate on its own, while still stopping if it would actually back into another aircraft.
- A taxi clearance routed through a taxiway whose name ends in **L**, **C**, or **R** (for example `TAXI W B T TC @10` at Oakland, where `TC` is a ramp connector) is no longer rejected as if that taxiway were a runway ("Taxiway T does not reach runway TC"). A trailing token is now treated as a destination runway only when it is an actual runway number, optionally with an L/C/R suffix (e.g. `28R`, `9L`), so taxiway names like `TC` stay in the taxi path.
- Aircraft now honor charted climb restrictions on SID legs defined by a DME distance, such as the Oakland **COAST9**'s "cross the OAK 4 DME between 1400 and 2000". The aircraft levels off at the window's top altitude until it passes the DME point, then resumes the climb. Previously the sim dropped these legs entirely and the aircraft climbed straight through the restriction.
- When one aircraft is stopped ahead of another on a taxiway and the two are merging onto the same lane, giving the lead aircraft its taxi clearance no longer causes the follower behind it to drive forward through it. The trailing aircraft now holds behind the one it is following while the lead proceeds, instead of the two swapping so the follower was released into the stopped lead — a situation that previously required a manual `BREAK` to recover.
- Right-clicking a departure on the ground map or in the aircraft list and choosing **Cleared for takeoff** (or **Line up and wait**) now clears the aircraft instead of failing with "Unrecognized: CTO 28R". These menu items were appending the runway to a command that takes none; they now send the bare clearance, with the runway shown in the menu label only (the aircraft's assigned runway is used automatically).
- A Delete command queued behind a condition (for example auto-delete on hold-short, `ONHS DEL`) now shows a readable name — "Delete aircraft" — in the command feedback and Pending Commands list, instead of the raw `DeleteCommand {}` text. Other commands that could surface the same way when queued behind a condition were given readable names too.
- After a timeline rewind or recording reload, an aircraft established on final still records its landing in the session's approach report and runway spacing stats.
- Sending `EF` (enter final) to an aircraft already on short final for that runway no longer sends it looping outbound to re-enter — it now continues its approach. A pattern-entry or runway switch that can't be flown from short final is rejected ("unable, short final") and the aircraft stays on its current approach instead of touring the airspace.
- When an aircraft reports traffic in sight (after `RTIS`), the RPO/instructor readback now reads `traffic (N456) in sight` instead of `traffic in sight, N456`, which looked like the reporting aircraft was stating its own callsign was N456. The spoken transmission and the solo-student view are unchanged (they never name the traffic's callsign).

## v0.8.5-beta [2026/07/01]

### Fixed
- Typing a bare command such as `TB` (turn base), `TC`, or `TD` no longer selects an aircraft whose callsign merely contains those letters. Complete commands now take priority over partial (substring) callsign matches — both when the command is sent (it reaches the selected aircraft instead of being swallowed) and in the autocomplete dropdown, where the command is listed above matching callsigns so Enter and Tab pick it without needing Escape first. An exact callsign match still selects the aircraft.

## v0.8.4-beta [2026/06/29]

### Highlights
- **Coordinate-staged IFR departures start on the ground and taxi** — a "ready to taxi" departure placed at map coordinates with a preset `TAXI` route now follows it instead of flying off on unpause; no more recovering each one by hand.
- **Arrival generators no longer all fire at once on startup** — with several randomized-interval generators, arrivals trickle in across the runways instead of appearing as a simultaneous burst.
- **STARS ATPA in-trail cones stay within their own volume** — cones no longer appear between arrivals in separate ATPA volumes (e.g. Oakland 30 vs 28R), and reduced 2.5 NM spacing applies only inside the volume's final-approach distance.
- **Clearer command rejections** — a command that can't apply in the aircraft's current state (e.g. a ground command issued to an airborne aircraft) now reads plainly instead of showing an internal "no dispatcher arm" diagnostic.

### Fixed
- IFR departures staged at map coordinates at field elevation (with a preset `TAXI` command) now start on the ground and taxi via their preset route, instead of flying off in their spawn heading the moment the simulation is unpaused. Scenarios commonly place "ready to taxi" departures this way rather than at a named parking spot; previously each one had to be recovered by hand — setting its airport, warping it onto a taxiway, and re-issuing the taxi.
- When a scenario has several arrival generators with randomized intervals, they no longer all spawn an arrival on the very first tick of the simulation. The first generator still starts on schedule; each additional one now begins at a random point within its first interval, so the initial arrivals trickle in across the runways instead of appearing as a simultaneous burst at startup.
- A command that can't be applied to an aircraft in its current state now gives a plain rejection message instead of an internal "no dispatcher arm" diagnostic. The most common case — a ground command such as taxi issued to an airborne aircraft — now reads "… requires the aircraft to be on the ground".
- STARS ATPA in-trail cones no longer appear between arrivals that belong to different ATPA volumes (for example one aircraft on final to runway 30 and another to 28R at Oakland, which sit in the separate O30 and O28 volumes). Each arrival is now assigned to a single ATPA volume and only compared against other aircraft in that same volume, instead of being pulled into a neighboring volume whose geometry happened to overlap its final.
- STARS ATPA reduced (2.5 NM) final separation now applies only within the volume's configured final-approach distance (typically 10 NM of the threshold); beyond that distance the required spacing reverts to 3.0 NM. It was previously applied across the whole volume.
- STARS ATPA scratchpad "Ineligible" rules now keep the matched track as a spacing reference for the aircraft behind it (with no cone of its own), instead of removing it from the sequence entirely the way an "Exclude" rule does.

## v0.8.3-beta [2026/06/28]

### Fixed
- On a CRC SAAB SAID surface display, suspending a track now marks it suspended on the display instead of hiding the aircraft, the same as ASDE-X. CRC has fixed the crash that rendering a suspended SAID track used to trigger, so the temporary workaround that hid suspended SAID tracks has been removed.

## v0.8.2-beta [2026/06/27]

### Added
- **"Fly runway heading" (`CTO RH`) now works for IFR departures.** Runway heading used to be a VFR-only takeoff modifier, but it's routinely issued to IFR aircraft too (for example departing off parallels). The aircraft holds runway heading after takeoff and awaits vectors instead of turning onto its SID. `CTO MRH` and `CTO MSO` behave the same way.
- **An IFR aircraft on runway heading (or vectored off its SID) can rejoin its filed SID.** Send it direct to a fix on the SID (`DCT <fix>`), then `CVIA` (climb via): climb via now activates the SID filed in the route and restores its published altitude/speed crossing restrictions, even when no SID was active.
- The radar **Cleared for takeoff** menu now has a default clearance plus a **Fly runway heading** item that works for IFR as well as VFR; **Fly on course** appears only for VFR aircraft, where it's valid.

## v0.8.1-beta [2026/06/26]

### Highlights
- **Non-mentor controllers can join as RPOs** — any signed-in VATSIM controller can now connect; an instructor pulls them into a room to work the position.
- **Your ARTCC is set automatically** — it now comes from your VATSIM/VATUSA profile when you sign in (the ARTCC field is gone from the connect dialog and Settings), and updates on its own if you transfer facilities.
- **One Room Members window** — the separate Members and Students dialogs are now a single window that lists everyone in the room and lets you pull RPOs and CRC students from their lobbies.
- **Sessions reconnect through a server restart** — the client keeps retrying for up to 15 minutes during a deploy, so your session resumes automatically instead of giving up after about 40 seconds.

### Added
- **Non-mentor controllers can now join a session as an RPO.** A deployed server used to admit only mentors and instructors; now any signed-in VATSIM controller can connect, and an instructor pulls them into a room. While waiting, an RPO sees a "waiting for room assignment" screen; once pulled in they work the position like any room member but can't create rooms or load/unload scenarios.

### Changed
- The **Members** and **Students** dialogs are now a single **Room Members** window — it lists everyone in the room (with Kick) alongside a CRC lobby and a YAAT lobby you can pull people from, all in one place.
- Your ARTCC is now filled in automatically from your VATSIM/VATUSA profile when you sign in — the ARTCC field is gone from the connect dialog, the web sign-in pages, and Settings. US controllers get their home ARTCC from VATUSA; everyone else falls back to their VATSIM subdivision. If you transfer facilities it updates on its own within about an hour, no re-sign-in needed.

### Fixed
- With datablock deconfliction on, an aircraft's datablock no longer lingers pinned to the edge of the ground (or radar) display after you pan its symbol out of view. The deconfliction pass was clamping the stray block back inside the viewport every frame, leaving a floating label with no aircraft under it; a symbol panned off-screen now drops its datablock along with the icon.
- The desktop client now keeps trying to reconnect through a full server restart instead of giving up after ~40 seconds. A server deployment is down for roughly 7–10 minutes while it rebuilds, but the client used to abandon reconnection almost immediately and leave you to reconnect by hand; it now retries for up to 15 minutes, so your session resumes automatically when the server comes back. The "server restarting" banner also shows a realistic downtime estimate (~10 minutes) instead of a 30-second countdown that implied the server would be right back.
- Suspending a track on a CRC SAAB SAID surface display no longer crashes CRC; the aircraft is hidden and reappears when un-suspended.
- Un-terminating (tagging) a track on a CRC SAAB SAID surface display redisplays it immediately, even when the aircraft is stationary.
- On a CRC ASDE-X display, un-terminating (tagging) or suspending a stationary track now updates immediately instead of waiting for the aircraft to move.

## v0.8.0-beta [2026/06/26]

### Highlights
- **Sign in with VATSIM** — connecting now verifies you through VATSIM in your browser instead of asking for your CID; your name and controller rating come from VATSIM. The vStrips and vTDLS pages sign in the same way.
- **Scenario access follows your verified VATSIM rating** — higher-rating scenarios open automatically based on your rating, and the manual training-access-key field is gone.
- **Deployed servers admit only mentors and instructors** — you can connect with a VATUSA mentor role or a VATSIM Instructor rating (I1/I2/I3+); students connect with CRC as before.
- **Taxiing along a named runway works again** — a clearance like `TAXI 28R G D` back-taxis along the runway, then turns off onto the taxiways.

### Added
- **Sign in with VATSIM.** Connecting to a server now authenticates you through VATSIM Connect in your browser instead of asking you to type your CID — your CID, name, and controller rating come from VATSIM and are verified. Your sign-in is remembered between launches. The vStrips and vTDLS browser pages sign in the same way. After your first sign-in, your ARTCC is pre-filled from your VATSIM subdivision and your operating initials are suggested from your name (both still editable in Settings).

### Changed
- **Scenario access now follows your verified VATSIM rating** instead of a per-ARTCC training key. Scenarios marked Student3 / Controller1 / Instructor1 are available to controllers at or above that rating, and the manual "Training access key" field has been removed.
- **Deployed servers now admit only mentors and instructors.** You can connect if you hold a VATUSA mentor role, or if your VATSIM rating is Instructor (I1/I2/I3) or higher; lower ratings without a mentor role are turned away at connect. Students being trained connect with CRC, which is unaffected.

### Fixed
- Aircraft can taxi **along** a runway named as a segment of a `TAXI` clearance again — e.g. `TAXI 28R G D` taxis along runway 28R's centerline, then turns off onto taxiways G and D (a back-taxi). The recent ground-movement (taxi pathfinder) rewrite regressed this: naming a runway anywhere but the destination was rejected with "Cannot find taxiway 28R in layout." The aircraft taxis straight onto the cleared runway (no hold-short at its entry), while a *different* runway the route crosses still holds short, and the readback uses runway phraseology ("taxi … on runway two eight right …", per 7110.65 §3-7-2.a). If the named runway doesn't actually meet the adjacent taxiway, the clearance is rejected with a clear "does not intersect" message instead of detouring.
- A `TAXI` clearance to a parking (`@`) or spot (`$`) destination that hangs off the far end of the final taxiway no longer sends the aircraft the wrong way down that taxiway and then detours the long way back to reach it — another regression from the ground-movement rewrite. The final-taxiway junction is now steered toward the destination (as it already was for runway destinations), so e.g. OAK `TAXI G D @NEW1` taxis straight up D to NEW1 instead of looping down C, across E, back over runway 28R, and around via H.

## v0.7.27-beta [2026/06/25]

### Highlights
- **Rewinding the timeline no longer leaves stale STARS ghost tracks** on connected CRC clients — scrubbing back in time used to push phantom tracks that lingered until a CRC reconnect.
- **Live winds aloft above 24,000 ft are now applied** — aircraft cruising in the flight levels under live weather were getting the 24,000 ft wind instead of the real higher-altitude wind.
- **Flight-strip annotations survive a flight-plan amendment** — the lower-right boxes (`9`, `8a`, `8b`) are no longer erased when an aircraft's flight plan is amended.

### Fixed
- Rewinding with the timeline bar no longer leaves stale STARS ghost tracks on connected CRC clients. Scrubbing back in time briefly let the periodic CRC broadcast catch the simulation mid-reconstruction and push tracks for aircraft that weren't present at the rewound time; those phantom tracks lingered on STARS until the controller disconnected CRC and reconnected. The display now stays in sync with the rewound state without a reconnect.
- Live winds aloft above FL240 (the 30,000 / 34,000 / 39,000 ft levels) are now decoded instead of silently dropped. The FAA winds-aloft (FD) bulletin omits the temperature sign at those levels — always-negative aloft — so groups like `257840` arrived as 6 characters and were rejected by the decoder; aircraft cruising in the flight levels under live weather were getting wind clamped to the 24,000 ft layer. Scenario weather was unaffected.
- Flight-strip annotations in the lower-right boxes (`9`, `8a`, `8b`) are no longer erased when the aircraft's flight plan is amended. The strip rebuild that runs on each amendment was truncating those three boxes, and the loss propagated to the CRC/vStrips display.

## v0.7.26-beta [2026/06/23]

### Highlights
- **CRC STARS commands now work from the keyboard, not just by slewing the data block** — accepting, recalling, and initiating handoffs, dropping tracks, toggling conflict-alert inhibit, and the `M`/`Y` multifunction entries (scratchpads, temporary/pilot-reported/amended altitudes, beacon assignment, Mode-C inhibit) all now work by typing the aircraft's callsign.
- **STARS Track Reposition (`TRK RPOS`, F2) now works** — park an aircraft's data block away from its target and slew it back onto the target to re-associate.
- **STARS command rejections now use the real STARS error codes** — `NO TRK` and `ILL TRK`, matching CRC, instead of YAAT's non-standard `TRACK NOT FOUND` / `NOT YOUR TRACK`.
- **Solo training: a departure the scenario clears for takeoff now checks in** after takeoff when it comes under your control, instead of climbing away in silence.

### Fixed
- Accepting a STARS handoff from the keyboard now works in CRC. Pressing the handoff key and Enter while a handoff flashes to you — or typing the aircraft's callsign and the handoff key — now accepts the inbound handoff, instead of being rejected with "TRACK NOT FOUND". (Slewing the data block already worked.) The same keyboard entry also recalls a pending outbound handoff you own, and `<HND OFF> <position> <callsign>` initiates a handoff without slewing.
- More CRC STARS commands now work by keyboard (typing the callsign), not only by slewing the data block — they previously returned "TRACK NOT FOUND":
  - Drop track: `<TERM CNTL>` + callsign drops the named track; `<TERM CNTL>ALL` drops every track you own.
  - Conflict alert: `<CA>K` + callsign toggles CA inhibit for the named track.
- CRC STARS multifunction `M` and `Y` track entries are now honored (both keyboard-with-callsign and slew forms): scratchpad 1 and 2 (set and clear), pilot-reported altitude, temporary altitude, amended filed/requested altitude, assigned beacon code, and Mode-C display inhibit. Previously these multifunction entries were silently ignored.
- CRC STARS Track Reposition (`<TRK RPOS>`, F2) now works. Slewing a tracked aircraft's data block and clicking a screen location parks the data block there as an unsupported data block (owned by you), while the radar target stays on scope as a bare unassociated track at its real position; slewing the parked data block back onto its own target re-associates it. Previously the command was silently ignored.
- CRC STARS command rejections now use the real STARS error codes — `NO TRK` when the referenced track isn't found and `ILL TRK` when you don't own it (or the operation is invalid for that track) — instead of YAAT's non-standard `TRACK NOT FOUND` / `NOT YOUR TRACK`.
- Amending an aircraft's beacon code (via the Flight Plan Editor's beacon field, or a STARS entry) now changes only the *assigned* code, not the code the aircraft is actually squawking. A controller assigns a beacon; the pilot keeps squawking their current code until told to squawk the new one, so the data block correctly shows a beacon mismatch until they comply — instead of the transponder silently snapping to the new code.
- In solo training, a departure cleared for takeoff by the scenario (a runway-spawn aircraft with a `CTO` preset, or one whose hold-for-release you lift and the automated tower then clears) now makes its initial check-in call after takeoff when it comes under your control, instead of climbing away in silence. The scripted/automated takeoff clearance was being treated as if you had already been talking to the pilot, which suppressed the check-in. A takeoff clearance you issue yourself while the aircraft is on the ground still does not produce a redundant check-in.

## v0.7.25-beta [2026/06/22]

### Highlights
- **Speed changes on short final no longer cancel the approach** — a speed instruction (`RFAS`, `SPD`, `RNS`, `DSR`, or a Mach assignment) given to an aircraft established within 5 nm of the runway now draws an "unable" and the aircraft stays on the glidepath, instead of tearing down the whole approach.
- **Line-up-and-wait and takeoff clearances no longer occasionally drive off the airport** — fixed an intermittent case where the aircraft taxied straight across the runway instead of pivoting onto the centerline.
- **`EXT` right after a pattern-entry clearance now works** — extending a leg (e.g. `EXT DOWNWIND` right after `ERD`, or a bare `EXT`) before the aircraft reaches the pattern now arms and fires on arrival, instead of being rejected.
- **More realistic pilot radio calls** — heavy/super appended to the callsign, correct check-in altitudes in solo training, "lost sight of" instead of "negative contact", and fixes named when reporting position.

### Fixed
- Extending a pattern leg right after a pattern-entry clearance now works. Issuing `EXT DOWNWIND` (or bare `EXT`) immediately after `ERD` — while the aircraft is still flying toward the pattern — arms the extension so it takes effect when the aircraft reaches the downwind, instead of being rejected with "Extend applies on upwind, crosswind, or downwind". Bare `EXT` extends whichever leg the entry leads onto (downwind after `ERD`/`ELD`, crosswind after `ERC`/`ELC`).
- An aircraft told to line up and wait, or cleared for takeoff, no longer occasionally taxis straight across the runway and off the airport instead of turning onto the centerline. When the line-up maneuver crossed perpendicular to the runway, the spot at which it should pivot onto the centerline could fall between two simulation steps and be skipped, so the aircraft kept going. This was intermittent — it hit roughly 1 in 10 departures with no obvious trigger.
- Aircraft checking in with the student after a handoff (solo training) now report their altitude correctly. The altitude is rounded to the nearest hundred feet and uses flight-level form above 18,000 ft, and the vertical state is spoken properly: an arrival descending via a STAR now checks in "leaving FL253, descending via the [arrival]" and a climbing departure "leaving … climbing …" / "climbing via the [SID]", instead of always saying "level" at an unrounded altitude (e.g. "level 25331"). Check-in transmissions also now include the aircraft's callsign in the displayed text.
- In solo training mode, handoffs to the student's own position are no longer auto-accepted — the student accepts them by hand, as in a real session. Handoffs between the automated (AI) positions still auto-accept, never faster than 3 seconds, so background traffic keeps flowing even when auto-accept is switched off.
- Stacking several `CFIX` crossing restrictions on one aircraft no longer logs spurious "queue cleared (lost: …)" warnings; every restriction was always applied and flown.
- On a routeless (vectored) aircraft, a chain of `CFIX` commands builds the crossing profile in the order issued.
- Pilots flying heavy or super aircraft now identify themselves with "heavy"/"super" after the callsign (e.g. "American Twenty-Two heavy") in their radio calls and readbacks, matching standard phraseology.
- The "with information [letter]" a pilot states on check-in now follows the field's ATIS: the letter comes from a single scenario value (Alpha by default) and the phrase is dropped entirely when the primary field has no ATIS, instead of always being spoken as "Alpha".
- When a pilot loses sight of the field or of traffic they were following, they now say "lost sight of …" instead of "negative contact …" — the latter is the phrase for traffic never acquired, not for losing a visual already established.
- Pilots no longer echo "caution wake turbulence" when reading back a takeoff or landing clearance — it is a controller advisory, not a pilot readback item. The advisory still appears on the controller's issued command.
- A pilot's fix-passage report now uses the fix's friendly name spoken aloud and its display name in text (e.g. "passing Lake Chabot") instead of the raw fix code ("passing VPCBT").
- An arriving pilot's reminder when no approach has been issued is now a brief "request approach assignment" prompt instead of the invented "N miles to land runway X" — the pilot names neither runway nor approach type, which the controller assigns for the airport's current configuration.
- A departing pilot's ready-to-taxi call now states its operation and destination ("IFR to San Francisco, ready to taxi") when a destination is filed.
- A pilot's go-around call no longer speaks the internal reason aloud (e.g. "(no landing clearance)"); the reason stays in the controller-facing text only. The spoken call is the standard "going around."
- A VFR pilot requesting closed traffic now states its altitude rounded to the nearest hundred feet, instead of an exact figure.
- When a pilot reports traffic in sight, or breaks off following traffic (lost sight of it, can't keep up, can't maintain separation, sequencing tight, S-turning for spacing), they no longer speak the other aircraft's callsign — a pilot identifies traffic from the controller's position/type call, not by callsign. The spoken call and the solo student's view now read "traffic in sight", "lost sight of the traffic", "unable to maintain separation, breaking off the follow", and so on; the other aircraft's callsign still appears in the RPO/instructor view as a diagnostic of which traffic was meant.
- The RPO pilot-speech panel no longer shows an internal "[CALLSIGN]" prefix on traffic/field-in-sight and follow break-off transmissions.
- Datablock deconfliction (the **DCNF** auto-declutter) now fully separates tightly-packed labels and keeps them in order. In Snap mode it lengthens a label's leader line when the eight compass directions at the normal distance can't clear the overlap, instead of leaving labels on top of each other; and in both Snap and Free modes the repositioned labels keep the same left-to-right / top-to-bottom order as the aircraft they belong to, instead of crossing over. Applies to both the radar and ground views.
- A speed instruction (`RFAS`, `SPD`, `RNS`, `DSR`, or a Mach assignment) issued to an aircraft already established on final approach within 5 nm of the runway no longer cancels the approach. The pilot now replies "unable" and stays on the glidepath, instead of the whole approach being torn down — which left the aircraft levelling off and forced you to re-clear it to land.
- The warning shown when a command does cancel an instrument approach now reads "approach to RWY XX" instead of mislabelling it "pattern to RWY XX" (a label reserved for VFR closed traffic).

## v0.7.24-beta [2026/06/22]

### Fixed
- Scenario auto-handoffs between automated control positions now work. A preset such as `AT SERFR HO 2B` on an aircraft owned by an autocontroller — for example background traffic being handed from Center to the approach controller — silently failed instead of starting the handoff. The handoff is now initiated and the receiving automated position accepts it, the same way the autotrack-driven handoffs already did.
- Center-to-TRACON handoff targets written with a facility prefix now resolve. A handoff to a neighboring TRACON position can be named by its single-character facility prefix plus TCP code (e.g. `Q2B` for NorCal's Boulder sector), matching how the target appears in ERAM, in addition to the bare TCP code (`2B`).

## v0.7.23-beta [2026/06/22]

### Highlights
- **Ctrl+F8 toggles the radar DCB** — hide the Display Control Bar to give the scope more room; remembered between sessions.
- **Local scenarios flagged in Load Recent** — recent scenarios loaded from a file now show a **(Local)** prefix, distinguishing them from vNAS ARTCC catalog entries.
- **Push-to-talk no longer intermittently drops commands** — releasing PTT just as an aircraft spawned or was removed, or pressing it before startup finished loading, could abort recognition.
- **vNAS scenario edits apply immediately** — reloading a scenario from the ARTCC catalog now picks up edits right away, instead of being stuck on a cached copy for up to 24 hours.

### Added
- Radar view: **Ctrl+F8** toggles the DCB (Display Control Bar), hiding it to give the scope more room. Matches CRC's DCB toggle; the state is remembered between sessions.
- The **Load Recent Scenario** menu now marks entries loaded from a local file with a **(Local)** prefix, so it's clear at a glance which recent scenarios came from a file versus the vNAS ARTCC catalog.

### Fixed
- Push-to-talk voice commands no longer intermittently fail to produce a command. Releasing PTT at the moment an aircraft spawned or was removed — or pressing PTT before navigation data finished loading at startup — could abort speech recognition before it issued anything.
- Edits made to a scenario in vNAS now take effect immediately when you reload the scenario from the ARTCC catalog. The server previously cached each scenario's contents for up to 24 hours, so changes to an existing scenario would not appear until that cache expired or the server was restarted.

## v0.7.22-beta [2026/06/20]

### Highlights
- **Immediate takeoff** — append `IMM` (or `WD`/`ND`) to `CTO` for a brisk taxi onto the runway and a rolling start, to fit a departure in ahead of an arrival; `LUAW WD` is the line-up-and-wait equivalent (still holds at the centerline).
- **Pre-arm a pattern-leg extension** — issue `EXT CROSSWIND` / `EXT DOWNWIND` before the aircraft reaches that leg and it extends automatically on arrival; `MNA` cancels a pending pre-arm.
- **360s and S-turns on final no longer land short** — an aircraft given `L360`/`R360` or `MLS`/`MRS` on final now re-intercepts the approach and touches down on the runway, instead of descending to the ground well before the threshold.

### Added
- `EXT CROSSWIND` / `EXT DOWNWIND` can be issued before the aircraft reaches that leg (e.g. while still on upwind) to pre-arm the extension, which then fires automatically on arrival; `MNA` cancels a pending pre-arm.
- Cleared for **immediate** takeoff — append `IMM` (or the interchangeable `WD`/`ND`) to `CTO`, e.g. `CTO IMM` or `CTO RT270 IMM`. The aircraft taxis briskly onto the runway and begins its takeoff roll without stopping at the centerline, to fit a departure in ahead of an arrival. The same suffix on `LUAW` (`LUAW WD`) gives a "line up and wait, without delay" — a brisk taxi onto the runway that still stops and holds at the centerline. Super and Heavy aircraft still make a standing-start takeoff.

### Fixed
- An aircraft told to fly a 360 (`L360`/`R360`) or make S-turns (`MLS`/`MRS`) on final for spacing no longer abandons the approach and lands far short of the runway. It now re-intercepts the final approach after the maneuver and touches down on the runway — previously it descended straight to the ground from wherever the maneuver ended, touching down well before the threshold and rolling up to "exit" onto a taxiway at the approach end (e.g. Bravo at KOAK runway 28L).

## v0.7.21-beta [2026/06/19]

### Highlights
- **New `REPORT` command** — have an aircraft call out an event when it happens: a pattern leg (`REPORT BASE`/`FINAL`, repeats each circuit), a distance on final (`REPORT 5 FINAL`), or a fix (`REPORT SUNOL`). Cancel with `REPORT OFF` or the radar right-click menu.
- **Speech bubbles can stay on screen until clicked** — an opt-in alternative to the timed auto-dismiss, for both pilot/SAY and WARN bubbles.
- **Recordings and bug reports record their build version** — both the client and server simulation version, so a report can be matched against the build that produced it.
- **`FOLLOW` no longer overflies much-slower traffic** — the follower slows to stay behind, and when it genuinely can't keep spacing it reports "unable to maintain separation" and goes around instead of ending up on top of the aircraft it was told to follow.

### Added
- New `REPORT` command has an aircraft report an event when it happens — `REPORT BASE`/`FINAL` (repeats each circuit), `REPORT 5 FINAL`, or `REPORT SUNOL` — cancellable with `REPORT OFF [leg]` and on the radar right-click menu.
- Recordings and bug report bundles now record the version they were captured with — both your client's version and the server's simulation version — so a report can be matched against the build that produced it.
- Speech bubbles can now stay on screen until clicked to dismiss — an opt-in alternative to the timed auto-dismiss, covering both SAY/pilot and WARN bubbles.

### Fixed
- An aircraft told to taxi while still rolling out from landing (e.g. `TAXI G D J` right after touchdown) no longer calls "holding short" of — and stops at — the runway it just landed on. The taxi clearance now treats rolling off the landing runway as clearing it, not as a runway crossing to hold for. Crossings of any other runway later on the route still require a separate clearance.
- Turning speech bubbles on or off now updates the Ground view immediately, matching the Radar view, instead of only after the Ground view is reopened.
- `TC` (turn crosswind) issued during the initial climb after a closed-traffic or pattern-exit departure now turns the aircraft crosswind early, at ~400 ft AGL.
- A departure lining up from a taxiway that meets the runway at a steep angle (e.g. Bravo onto Oakland's 28R) now turns onto the centerline instead of taxiing to the runway end and doubling back.
- An aircraft taxiing off a runway onto a taxiway that bends sharply near the runway (e.g. OAK runway 28R onto taxiway G) now rounds the bend instead of circling it before continuing.
- During recording playback, the timeline clock and slider now advance continuously while playing, instead of freezing until you pause or unpause. Because the displayed time was stale, the +15/−15 skip buttons (and dragging the timeline) computed their target from the wrong time, so returning to "the same" timestamp could land on a different aircraft position each time — these are now consistent.
- An aircraft that has just landed no longer lingers as a coasting track on the controller's CRC STARS (or ERAM) radar scope. Its terminal-radar track is now dropped the moment it touches down and moves to the surface display, instead of coasting — which, at faster simulation rates, could leave a stale "coast" target sitting on the aircraft for up to a minute while it taxied.
- A point-out to a sector that is combined into another position no longer gets stuck. Previously the controller working the combined position could not accept it, leaving the target flashing in point-out status indefinitely. The point-out is now directed to whoever is working that combined position, so they can acknowledge it.
- An aircraft told to `FOLLOW` much-slower traffic to the same runway (e.g. a faster single told to follow a Cessna 152) no longer overtakes it and ends up directly on top. The follower now slows to stay behind where it can, and when it genuinely cannot keep spacing — its approach speed is simply faster than the traffic's — it reports "unable to maintain separation" and goes around instead of overflying the aircraft it was told to follow. Followers also no longer speed up to chase a lead that is too far ahead (they extend the downwind instead).
- An aircraft flying a VFR traffic pattern (closed traffic / touch-and-goes) now turns crosswind at the departure end of the runway and flies the runway's published pattern altitude on every circuit, instead of stretching a long upwind out toward the parallel runway — most visible at a smaller specified pattern such as KOAK runway 28L.

## v0.7.20-beta [2026/06/17]

### Fixed
- An aircraft taxiing or holding short on the ground no longer appears on the controller's CRC STARS (or ERAM) radar scope. Aircraft on the airport surface now stay on the surface display (ASDE-X / SAAB SAID) until they are airborne, matching real terminal radar.

## v0.7.19-beta [2026/06/17]

### Fixed
- Manually consolidating a position into one TCP while a position below it in the hierarchy is consolidated into a different TCP no longer shows the lower position consolidated under both owners at once on the STARS scope. The nested position now follows only its own consolidation.
- An aircraft sent around (`GA`) and then given a heading (`FH`) during the missed-approach climb now holds the published missed-approach altitude, instead of reverting to the altitude it was last cleared to for the approach — which could leave it leveling off too low or climbing past the missed-approach altitude.
- Saying "expedite" after an altitude over voice (e.g. "climb and maintain five thousand, expedite") no longer drops the word and mangles the altitude into a bogus runway. Speech recognition was mistaking long words that end in a runway-suffix sound ("expedite", "tonight") for a "left"/"right"/"center" runway designator.

## v0.7.18-beta [2026/06/15]

### Highlights
- **Bug reports capture the server side automatically** — a bug report bundle now embeds your session's server log even on a remote server, anonymized to your training room.
- **Deleting an aircraft no longer risks crashing a trainee's CRC** — removing an aircraft that was on a CRC ASDE-X or SAAB SAID surface display no longer leaves an orphaned, clickable target that could crash CRC.
- **Departures keep climbing after a heading** — a heading given just after takeoff (`FH`) no longer cancels the climb, so the aircraft continues to its assigned altitude.
- **Hold-short on the taxi route crosses fully** — an aircraft told to hold short of a runway on the taxiway it is taxiing now crosses completely and stops clear when cleared, instead of stopping on the runway.

### Added
- Bug report bundles now embed your session's server log even when connected to a remote server, scoped to your training room with participants (CIDs, names, initials) anonymized.

### Fixed
- The per-airport wind readout in the Radar and Ground weather overlays now ends in `KT` (e.g. `23005KT`, `36008G18KT`, `00000KT`), matching standard METAR notation and the wind already shown elsewhere in the app.
- Variable-direction wind in the Radar and Ground weather overlays now shows the `VRB` token (e.g. `VRB04KT`) instead of dropping the direction and showing only the speed.
- The flashing red `NoLndgClnc` datablock warning now clears when an aircraft on final without a landing clearance is sent around with `GA` — instead of continuing to flash after the go-around. It also clears whenever the aircraft is otherwise taken off the approach.
- Deleting an aircraft (`DEL`) while it was on a CRC ASDE-X or SAAB SAID surface display no longer leaves a clickable, orphaned surface target that could crash the controller's CRC when clicked. The track still coasts for 45 seconds (or drops at its destination field), but its radar target is removed the moment the aircraft is deleted.
- Fix-name suggestions — the `AT` / `DCT` autocomplete dropdown and the radar right-click **Cross fix** / **Depart fix** / **Direct to** menus — now list the aircraft's current navigation route first, then its filed route, and put the destination airport ahead of the departure airport (an aircraft is unlikely to be turned back toward where it departed).
- Typing a callsign before an `AT <fix>` condition (e.g. `N428KK AT …`) now draws the fix suggestions from that aircraft's route instead of whichever aircraft is selected on the radar, so the fix it's flying direct to actually appears in the dropdown.
- A `DCT` (proceed direct) issued after a relative turn (`RELR` / `RELL` / `RT` / `LT`) now turns the shortest way to the fix. Previously the relative turn's forced direction carried over into the direct-to, so the aircraft kept turning the long way around instead of proceeding direct, and a follow-up `DCT` couldn't recover it without first breaking the turn with a heading command.
- A departing aircraft given a heading (`FH`) just after takeoff keeps climbing to its assigned altitude instead of leveling off a few hundred feet up. The heading instruction no longer cancels the climb clearance.
- An aircraft told to hold short of a runway that lies on the single taxiway it is taxiing (e.g. `TAXI J HS 28R`, where Juliet crosses 28R) now crosses the runway completely and stops just clear on the far side when cleared to `CROSS`, instead of stopping partway across — on the runway — and having to be re-issued a taxi instruction.
- A `TAXI` command with a turn-direction hint (e.g. `TAXI >J` or `TAXI <J`) now echoes the turn in the controller response — "Taxi via right on J" / "left on J" — matching the pilot's spoken readback, instead of dropping it from the response.

## v0.7.17-beta [2026/06/14]

### Highlights
- **Friendly waypoint names in responses and readbacks** — commands, pilot readbacks, and `AT <fix>` conditions now show a named reporting point's plain-language name next to its identifier (e.g. "Cross LAKE CHABOT (VPCBT) at 3,000").
- **Pilots speak a navaid's real facility type** — "Mendocino VORTAC", an NDB, or a TACAN, instead of always saying "VOR".
- **CRC surface displays no longer risk a dropped connection** — opening an ASDE-X or SAAB SAID display (or its Safety Logic alerts) no longer hits a server-side race that could drop the connection.

### Added
- Commands that reference a named visual reporting point or landmark — fixes with a friendly name in the per-ARTCC data, like `VPCBT` ("Lake Chabot") or `OAK30NUM` ("Oakland Runway 30 Numbers") — now show that name alongside the identifier in command responses, pilot readbacks, and `AT <fix>` conditions (e.g. "Cross LAKE CHABOT (VPCBT) at 3,000"). Plain fixes are unchanged.

### Fixed
- Pilot speech and position reports speak a navaid's real facility type — "Mendocino VORTAC", an NDB, a TACAN — instead of always saying "VOR".
- `HFIXL` / `HFIXR` hold-at-fix responses read "left/right 360s", matching `HPPL` / `HPPR` and standard 360 phraseology.
- A CRC client subscribing to the ASDE-X or SAAB SAID surface display — or its Safety Logic alerts — no longer risks a dropped connection from a server-side race between the subscription and the per-tick surface-track coast and alert updates.

## v0.7.16-beta [2026/06/13]

### Highlights
- **Crosswind and downwind departures fly the real pattern** — `CTO MRC` / `MRD` / `MLC` / `MLD` now fly the actual traffic pattern (upwind → crosswind → downwind), so `EXT` extends a leg for spacing instead of being rejected.
- **Radar-vectors SIDs keep working when the FAA cycle drops a procedure** — an aircraft cleared for takeoff on a procedure missing from the current CIFP (like the NIMITZ off Oakland) again flies its published initial heading, recovered from a recently cached cycle.
- **Closed-traffic takeoffs survive a rewind or reconnect** — a `CTO MRT` / `MLT` clearance issued while the aircraft is still taxiing no longer reverts to a straight-out departure.

### Fixed
- A `CTOPP` present-position departure now holds the helicopter over its spot during the vertical liftoff instead of drifting it forward before climbing out.
- **Crosswind and downwind departures (`CTO MRC` / `CTO MRD` / `MLC` / `MLD`) now fly the actual traffic pattern** — upwind, then crosswind, then downwind for a downwind departure — climbing out continuously, so `EXT` / `EXT UPWIND` can extend a leg for spacing instead of being rejected with "Extend applies on upwind, crosswind, or downwind".
- A closed-traffic takeoff clearance (`CTO MRT` / `MLT`) issued while an aircraft is still taxiing now survives a rewind or reconnect instead of reverting to a straight-out departure.
- An aircraft cleared for takeoff (`CTO`) on a radar-vectors SID whose coded data is missing from the current FAA CIFP — whether the procedure is still charted (like the NIMITZ departure at Oakland) or was retired — again flies the procedure's published initial heading (e.g. the charted right turn to 315°) instead of holding runway heading and waiting to be vectored. YAAT now recovers the procedure's coded legs from a recently cached AIRAC cycle and posts a terminal note when it does, so you can verify the heading against current charts. The same recovery applies to STARs and approaches whose CIFP data is missing.

## v0.7.15-beta [2026/06/12]

### Highlights
- **Datablock deconfliction** — the new **DCNF** button on the Radar and Ground views automatically spreads apart overlapping aircraft labels so they stay readable in busy traffic (a runway queue, a gate cluster, a downwind line).
- **Active-position selector** — a dropdown in the command bar shows which controller position you're working as and lets you switch with a click, instead of typing `AS [position]`.
- **Scope markers** — pin a fix, NAVAID, or any radar point with `.ff`/`.marker`/`.markers` or a right-click "Pin marker here"; clear them with `.nomarkers`.
- **ASDE-X Safety Logic alerts** — CRC's ASDE-X surface display now flags runway incursions: closed-runway, occupied-runway, taxi-onto-active-runway, and taxiway landings.

### Added
- Pin fixes, NAVAIDs, or any radar point with `.ff`/`.marker`/`.markers` or a right-click "Pin marker here"; `.nomarkers` clears them.
- **Datablock deconfliction on the Radar and Ground views** — an opt-in mode that automatically moves overlapping aircraft datablocks apart so labels stay readable in busy traffic (a runway queue, a gate cluster, a downwind line). The new **DCNF** button (on the Radar DCB, next to MVA, and on the Ground filter bar) cycles each view independently through three settings: off, **Snap** (each label snaps to one of eight leader directions, like a real scope), and **Free-form** (labels slide freely until separated). Datablocks you have dragged by hand stay put and the rest route around them; right-click a dragged label and choose "Reset datablock position" to hand it back to automatic placement. The choice is remembered per view across sessions.
- **Active-position selector in the terminal input bar** — a dropdown to the left of the command box shows the TCP you are currently operating as (the scenario's primary position by default) and lists the other online controllers' positions. Pick one to switch to it (the same as running `AS [TCP]`). The indicator follows whenever you change position with a standalone `AS [TCP]`, and stays put for a one-shot `AS [TCP] [command]`, so you can always see who you are acting as.

### Fixed
- CRC ASDE-X now raises Safety Logic alerts for closed-runway, occupied-runway, taxi-onto-active-runway, and taxiway-landing incursions.
- ASDE-X and SAID surface tracks coast for 45 seconds when an aircraft disconnects, or drop at their destination field.
- ASDE-X and SAID targets now draw position-history trails.
- The SAAB SAID surface display now shows traffic up to 2,500 ft AGL, including at high-elevation airports.
- Clicking a radar datablock that shows an assigned-to or handoff indicator no longer misses near its right edge, and the selection box no longer flickers with the handoff flash.
- Clicking an airborne aircraft's datablock on the Ground view no longer misses on its altitude line.
- **`TRACK [position]` now claims the track for the named position** — with an aircraft selected, typing `TRACK 3Y` (or any sector's TCP) now tracks it under that position, the way the command's hint advertises, instead of silently tracking it under your own active position. It is a one-shot equivalent of `AS 3Y TRACK` and does not change your persistent active position.

## v0.7.14-beta [2026/06/12]

### Fixed
- Ground "draw taxi route" → "Copy to command input" pastes a readable, editable command pinned to your drawn endpoint, so the aircraft taxis exactly what you drew.

## v0.7.13-beta [2026/06/11]

### Highlights
- Force a landing with `CLANDF` — make an aircraft land even when it would normally go around (too high, too fast, off centerline, or no landing clearance). Also a "Force landing" right-click item; cancel with `GA` or `CLC`.
- The ground "draw taxi route" tool now taxis the exact route you drew, instead of cutting to a parallel taxiway or skipping the turn into a parking stand.
- A runway-exit preference (`EL` / `ER` / `EXIT`) now survives a rewind or reconnect, so the aircraft still exits where you assigned it.
- The MVA datablock tint no longer flags an aircraft established on an approach (which descends below the MVA by design).

### Added
- **Force a landing with `CLANDF`** — a new RPO/instructor command (also a "Force landing" right-click menu item on the radar, ground, and aircraft-list views) that makes an aircraft land even when it would otherwise go around. It clears the aircraft to land and drives it down onto the runway no matter how high, fast, or off-centerline the approach is, suppressing the automatic go-around (unstable approach, too high at the missed-approach point, or no landing clearance). Call it back off with `GA` or by cancelling the landing clearance (`CLC`). Not available in solo training.

### Fixed
- A runway-exit preference (`EL` / `ER` / `EXIT <taxiway>`) now survives a rewind or reconnect instead of being dropped, so the aircraft still exits where you assigned it.
- The MVA datablock tint no longer flags an aircraft established on an approach, which the procedure descends below the MVA by design.
- **The ground "draw taxi route" tool now taxis the exact route you drew** — previously it could send the aircraft down a parallel taxiway (you drew V, it taxied U) or skip the turn into a parking stand and continue straight ahead, because only the waypoints you clicked were committed and the simulator re-routed between them. The tool now commits the full drawn path, so the aircraft follows it faithfully; ending a drawn route inside a parking stand or spot now taxis the aircraft in and parks it.

## v0.7.12-beta [2026/06/11]

### Added
- **Favorite video maps on the Radar view** — right-click any map in the MAP list to mark it a favorite for the whole ARTCC, for the scenario's primary airport, or for just this scenario. Favorited maps show a ★ and sort to the top of the list so the handful you actually use are always within reach, instead of hunting through dozens of maps every session. Favorites only pin and sort — they do not turn maps on by themselves — and are remembered across sessions, so a map favorited for an ARTCC stays starred in every scenario in that ARTCC.
- **Minimum Vectoring Altitude (MVA) awareness on the Radar view** — YAAT now knows the FAA-charted MVA for every facility the FAA publishes (148 TRACONs and centers nationwide) and surfaces it three ways: an airborne IFR aircraft's datablock altitude is drawn red when it is below the MVA for its position and amber when within 100 ft of it (both the STARS datablock and the EuroScope tag); holding Ctrl while moving the cursor shows the MVA floor and sector under the pointer; and right-clicking empty map space lists the MVA at that point. VFR aircraft (MSAW-inhibited by default) and positions outside charted coverage show no indicator. The datablock tint defaults on for Approach/Center scenarios and off for Ground/Tower (configurable per position type in Settings → Display → Overlays), and can be toggled live with the new MVA button on the Radar DCB. (7110.65 §5-6-1.)

## v0.7.11-beta [2026/06/10]

### Highlights
- The `NoLndgClnc` warning flashes earlier — an aircraft on a visual final without a landing clearance flashes the red datablock line from 2 nm out instead of 1 nm.
- The Ground view keeps a departing or arriving aircraft in sight up to the cloud ceiling (or 6,000 ft in clear skies), like the view from the tower, instead of dropping it at a fixed 4,000 ft.
- Flight plans created in CRC's Flight Plan Editor now get a beacon code, and the recycle-beacon button works.
- Chat messages no longer pop up command autocomplete — a line starting with `'`, `/`, or `>` won't trigger a suggestion list.

### Changed
- **The `NoLndgClnc` radar warning flashes earlier** — an aircraft on a visual final without a landing clearance now flashes the red `NoLndgClnc` datablock line from 2 nm out instead of 1 nm, while the AI pilot still calls "short final" at 1 nm as before. Aircraft on an instrument approach are unchanged (already flashing ~3.8 nm out).
- **The Ground view keeps an aircraft in sight up to the cloud ceiling or 6,000 ft** — like looking out the tower window, a departing or arriving aircraft now stays on the Ground view until it climbs through the reported cloud ceiling (or 6,000 ft above the field when the sky is clear), instead of dropping off at a fixed 4,000 ft. It still only shows traffic within 10 nm of the field.

### Fixed
- **Flight plans created in CRC's Flight Plan Editor now get a beacon code** — a discrete VFR or IFR squawk, and the recycle-beacon button works. (7110.65 §5-2-7.1.)
- **Chat messages no longer trigger command autocomplete** — when the command line starts with a chat prefix (`'`, `/`, or `>`), the autocomplete and signature-help popups stay closed, so chat text that happens to read like a command no longer pops a suggestion list.
- **Creating a vStrips strip focuses its first field** — new half-strips, separators, and blank strips drop your cursor into the first editable field so you can type immediately.
- **A departure no longer flickers off the Ground view at rotation** — a pre-tagged (autotrack) departure used to vanish from the Ground view for a couple of seconds the moment it lifted off, reappearing once it climbed through about 100 ft; it now stays in view continuously through liftoff.
- **The Radar view no longer shows a departure before its altitude reads `001`** — an airborne departure now appears on YAAT's Radar view only once its displayed altitude reaches `001` (about 100 ft above the field, adjusted for field elevation), matching when CRC STARS begins the track, instead of popping up the instant the wheels leave the ground.

## v0.7.10-beta [2026/06/09]

### Highlights
- Spawn an arrival already established on a STAR — `ADD I H J TBARR.TBARR4.34R` drops an IFR aircraft onto the arrival at a named waypoint, descending via the procedure, with no need to spawn far out and edit a flight plan.
- Departures and go-arounds can be assigned a speed near the field again — the "no speed inside 5 nm final" rule no longer blocks a climbing departure or go-around.
- VFR aircraft told to FOLLOW now trail behind their traffic instead of pointing straight at the lead and closing, opening the gap with a shallow S-turn when they get too close.
- The Cirrus Vision Jet (SF50) climbs and cruises at realistic speeds — ~170 knots in the climb and ~300 knots in cruise, instead of a generic 250-knot jet climb.

### Added
- **Spawn an aircraft already established on an arrival (STAR)** — the `ADD` command has a new variant, `ADD I {wt} {eng} {waypoint}.{star}[.{runway}] [altitude] [SP{speed}] [LVL] [airport]` (e.g. `ADD I H J TBARR.TBARR4.34R 230`), that drops in an IFR aircraft already on the arrival at the named waypoint. By default it descends via the STAR's published crossings from its current altitude; add `LVL` to hold the altitude until you issue `DVIA`. The altitude is optional (a realistic establishment altitude is computed from the STAR profile if omitted), as are the runway transition, the speed (`SP###`), and the destination airport for multi-airport STARs (defaults to the primary scenario airport). Lets instructors inject arrivals without spawning far out and editing a flight plan. (#197)
- **`SPEEDF` — force a speed inside 5 nm final** — a new speed command that assigns a maintain-speed even when an arrival is inside the 5 nm final-approach gate where a normal `SPD` is refused (e.g. military or compression scenarios). It accepts `+`/`-` floor/ceiling like `SPD` (`SPEEDF 180`, `SPEEDF 170+`) and, unlike the `SPDN`/`SPEEDN` teleport, eases the aircraft to the speed via normal physics. Aliases: `SPDF`, `SLF`.
- **Per-type aircraft performance can be corrected by contributors** — a new overrides file lets a contributor fix how a specific aircraft type climbs, descends, and approaches when the built-in estimate is wrong; the corrected values are authoritative. See `docs/aircraft-performance.md`.

### Changed
- **VFR follow flies a trail instead of chasing the lead** — A VFR aircraft told to `FOLLOW` traffic now settles into a trail behind it and tracks parallel to the lead's path, rather than pointing its nose straight at the other aircraft and continuously closing. It uses lateral course as a spacing tool, not just speed: it eases into position when well behind, and when it gets too close while already at approach speed it makes a shallow S-turn to open the gap. On final more than 5 nm out, a follower that has caught up too close behind its traffic makes one shallow S-turn for spacing and reports it; inside 5 nm it stays committed to the approach as before. (AIM §5-5-12, §4-3-5.)

### Fixed
- **Departures and go-arounds can be assigned a speed near the airport again** — the 7110.65 "no speed inside 5 nm final" rule was being applied to any aircraft within 5 nm of its runway, so a departing aircraft (a Vision Jet climbing out at 160–180 kt, for example) or an aircraft going around had its `SPD` commands rejected and any assigned speed silently dropped a moment later. The restriction now applies only to aircraft actually inbound on an arrival approach; departures, go-arounds, and missed approaches keep their assigned speed. (Reported on Discord; 7110.65 §5-7-1.b.4 / §5-7-3.4.)
- **An arrival slowed near the airport no longer speeds back up at 5 nm** — when an aircraft assigned a speed (or told to reduce to final approach speed) reaches the 5 nm final where the controller can no longer adjust it, it now holds that speed or eases down to its approach speed instead of re-accelerating toward its normal descent speed. (7110.65 §5-7-1.b.4 / §5-7-1.d.)
- **The Cirrus Vision Jet (SF50) climbs and cruises at realistic speeds** — with no performance profile it was using the generic jet default and climbing at 250 kt; it now climbs ~170 KIAS and cruises ~300 KTAS with a 31,000 ft ceiling.
- **Interfacility handoffs from CRC are now recognized as handoffs** — when a controller hands a track to an adjacent terminal facility in CRC STARS using the triangle/delta entry (the `` ` ``/tilde key, e.g. `Δ3` to Fresno, `Δ31H` to Fresno's Chandler sector, `Δ11N` to Travis North), YAAT now initiates the handoff to that facility instead of storing the code as the aircraft's primary scratchpad. The handoff-number → facility/sector mapping is read from the ARTCC configuration's STARS handoff IDs, so it follows each facility's real adaptation. A delta entry that doesn't match a configured handoff code is rejected (`ILL POS`) rather than written to the scratchpad.

## v0.7.9-beta [2026/06/09]

### Added
- **SAAB SAID surface display** support for CRC 2.17 — a CRC client connected to a YAAT training room can now open a SAID display at a SAID-configured airport and see live surface targets and tracks, edit data-block fields (callsign, beacon, category, type, fix, scratchpads), tag / terminate / suspend tracks, and place restricted-area / closed-area / text temporary-data overlays and presets, exactly the way ASDE-X already works. SAID display state survives scenario reload and session checkpoints and is wiped when a scenario unloads. (CRC 2.17 introduced SAID alongside ASDE-X; YAAT now emits the four SAID topics so the new display renders against a training room.)

### Fixed
- On CRC's **ASDE-X** display, surface targets and tracks now show their **ground speed** — the velocity/predictor vector and the data-block speed field were drawing zero-length and `00` because the server left the ASDE-X ground-speed field unset. (A latent gap since ASDE-X support shipped, surfaced while adding SAID.)

## v0.7.8-beta [2026/06/08]

### Highlights
- A point-out you send now shows the recipient's sector as `3E*` on the data block — outstanding outgoing point-outs are visible at a glance.
- A point-out the student accepts on CRC stays yellow until they clear it, instead of turning green the moment it's accepted.
- Accepting a handoff with `ACCEPT` keeps the previous controller's data block white until they slew it, matching STARS.
- `ONHO` instructions now fire when a handoff is accepted by the auto-accept timer or accept-all, not just a manual accept.

### Added
- The instructor **Radar View** now marks a pending point-out the student sent to another position with the recipient's sector and an asterisk — e.g. `3E*` — on the owner/scratchpad data block line, so you can see at a glance which tracks have an outstanding outgoing point-out.

### Fixed
- A point-out the student accepts on CRC now stays **yellow** until they slew it a second time to clear it — on both CRC and the instructor's **Radar View** — instead of turning green the moment it's accepted. Clearing it returns the data block to green.
- When a handoff is accepted with the `ACCEPT` command (rather than by the auto-accept timer), the previous controller's data block now stays a white full data block until they slew it — the way STARS shows a just-accepted handoff — instead of immediately turning into a green partial data block. The auto-accept path already behaved this way; the manual `ACCEPT` (and accept-all) now matches it.
- An `ONHO` (on-handoff) instruction — e.g. `ONHO CM 120` (climb when handed off) or `ONHO DEL` — now fires when the handoff is accepted by the auto-accept timer or accept-all, not only when an RPO accepts it by hand.

## v0.7.7-beta [2026/06/08]

### Highlights
- Call out traffic three simpler ways — off the nose (`RTIS NR 2 C172`), by pattern leg (`RTIS BASE R 2 28R M20P`), or by landmark (`RTIS OVER VPCOL C172`), typed or spoken, alongside the radar clock form.
- Traffic advisories in solo training no longer have to be pin-point — a call a mile or a hundred feet off still counts, the altitude is optional, and a slightly-off call adds a coaching note instead of failing.
- IFR departures turn off the runway at 400 ft AGL instead of holding runway heading until past the departure end — a much earlier turn on long, high-elevation runways like Aspen.
- Saving a bug-report bundle from a solo session is fast again — a 90-second session now saves in well under a second instead of over a minute and a half.

### Added
- **VFR-style traffic advisories** — three simpler ways to point out traffic alongside the radar-style `RTIS <clock> <miles> <direction> <type>`: relative to the aircraft's nose (`RTIS NR 2 C172` → "Traffic, off your nose and to the right, 2 miles, a Cessna"), by pattern leg (`RTIS BASE R 2 28R M20P` → "Traffic, 2-mile right base for runway 28R, a Mooney"), and by landmark or VFR reporting point (`RTIS OVER VPCOL C172` → "Traffic, over Oakland Coliseum, a Cessna"). Each can be typed or spoken to the speech recognizer, resolves the traffic you most likely mean, and feeds the same Session Report scoring as the clock form.

### Changed
- In **solo training**, a structured traffic advisory (`RTIS <clock> <miles> <direction> <type> [altitude]`) no longer has to be a pin-point match to count. It now resolves the traffic you most likely mean within realistic tolerances — a whole mile or a hundred feet off is treated as a correct call, the clock is allowed more slack (and a lot more when the aircraft you're talking to is itself turning, since its clock reference is swinging), and when two aircraft are both near your call the closest-matching one is chosen. The altitude is now optional (`RTIS 3 5 W B737` is accepted) for "altitude unknown" VFR traffic. A call that lands within tolerance but is noticeably off still counts but adds a low-severity "traffic advisory imprecise" coaching note so the Session Report reflects how accurate it was. (FAA 7110.65 §2-1-21; AIM §4-1-15.)

### Fixed
- At **SFO**, taxiing aircraft no longer cut the sharp corner where taxiways **L** and **F** meet at the south end — they take the curved **LF** connector instead, for automatic taxi routes and for explicit `TAXI L F` / `TAXI F L` clearances alike (a far-off aircraft still crosses normally where the connector would be a needless detour). The corner-cutting fillet arc that used to be drawn across that apex is also removed from the **Ground View**, matching the painted layout — there is no taxiway line across the apex, only the connector.
- Replaying, rewinding, or saving a bug-report bundle of a **solo training** session no longer diverges from how the session actually played out: a VFR arrival you vectored or cleared into Class B/C airspace stays in the airspace instead of spuriously orbiting outside it in the reconstructed snapshots. Reconstructing a recording now re-establishes the two-way communication your recorded instructions created (it previously dispatched the instruction but skipped marking comms), and replaying a recording in the client restores the recorded student position — so boundary holds, pilot check-ins, and traffic advisories play back as they happened live.
- IFR departures on a SID or an assigned heading now begin their turn off the runway at **400 ft AGL** instead of holding runway heading until they have physically flown past the departure end of the runway. On long, high-elevation runways (e.g. Aspen RWY 33 on the LINDZ ONE) the old behavior delayed the turn to ~1,000 ft AGL — long enough to matter for opposite-direction operations. The turn now starts at ~400 ft above field elevation, per TERPS (AIM 5-2-9 / 7110.65 5-8-3). VFR pattern departures are unchanged: they still climb past the departure end of the runway before turning crosswind (AIM 4-3-2).
- Saving a bug report bundle from a **solo training** session is fast again — a 90-second solo session that took over a minute and a half to save now saves in well under a second. Generating the recording's embedded replay snapshots re-runs the whole session, and solo mode's per-tick airspace-boundary and separation checks were scanning the entire national Class B/C airspace database (≈500 volumes) for every aircraft on every tick. The same per-tick work also slowed live solo sessions; both are fixed.

## v0.7.6-beta [2026/06/08]

### Fixed
- In **solo training mode**, a VFR arrival worked by approach but never handed off to a tower student (as in the OAK VFR-sequencing scenarios) no longer orbits outside Class B/C airspace indefinitely. Any instruction that works the aircraft — a direct, a vector, an approach clearance, a sequencing call — or a request for the pilot to report something (`SALT` say altitude, `SSPD` say speed, and the other directed `SAY` reports) now counts as establishing two-way communication, so it stops holding and enters the airspace. Previously only a pattern-entry command got them in.
- A direct (`DCT`) issued to an aircraft while it is holding outside Class B/C airspace is now honored once the hold releases — the aircraft proceeds to the fix you named instead of snapping back to its original route.

## v0.7.5-beta [2026/06/07]

### Highlights
- **Expedite a runway exit** — `EXP` makes a landing aircraft take the earliest exit and brake hard to clear the runway as fast as possible.
- **`HOLD` now stops an aircraft lining up after `LUAW`** — it stops where it is and stays lined up instead of rolling onto the centerline.
- **Speed commands no longer cancel a turn, hold, or approach in progress** — adjusting speed during a 360, S-turns, a holding pattern, or an approach intercept keeps the maneuver running.
- **A pattern-entry command after `CLAND` keeps the landing clearance** — `EF`/`ERB` and friends no longer turn a cleared full-stop into a touch-and-go.

### Added
- **Expedite a runway exit** — `EXP` on a landing aircraft (`ER EXP`, `ER W5 EXP`, `EL EXP`, `EXIT A3 EXP`, or a bare `EXP` while rolling out) clears the runway fast: takes the earliest reachable exit, brakes harder (jet ~7.5 kts/s vs the normal firm 5) to make it, keeps speed up at high-speed exits, and brakes firmly to the hold-short stop. Combines with `NODEL`; the controller phrase is "exit … without delay".

### Fixed
- In **solo training mode**, an aircraft holding short of a runway it must cross en route to its departure runway no longer reports "ready for departure" — it waits for a crossing clearance, and that call is reserved for the assigned departure runway (e.g. it stays quiet at 15/33 while taxiing to 28R at KOAK).
- A simulated pilot's spoken hold-short of a crossing runway now names only the nearer runway end — "holding short runway one five" instead of "one five slash three three".
- Selecting an aircraft on the radar no longer recolors its data block text and leader line to white — they keep the color they have when unselected (green for an on-ground track, the RPO assignment tint, or the student-scope color). The white rectangular border still marks the selected aircraft, and its position symbol still brightens.
- STARS ghost / "unsupported" tracks no longer appear on the **Ground View** — a ghost track is a STARS-only concept with no surface return, so it does not belong on the surface display. A real aircraft still taxiing that was tagged with a ghost overlay (so it autotracks once airborne) stays visible on the Ground View while it is on the ground; only after it gets airborne does its ghost drop off the Ground View. Phantom data blocks (a STARS flight plan with no aircraft) never show on the Ground View at all.
- `CTO`/`CTOPP` now reject an unrecognized argument — unknown modifier, bad `DCT`/`TRDCT` fix, or trailing junk — instead of silently clearing a plain runway-heading takeoff.
- `EXIT`, `LAND`, and `EXP` now reject a malformed trailing token — a mistyped `NODEL` (no longer silently auto-deleting the aircraft) or an unparseable expedite altitude.
- Unloading a scenario now wipes all its session state — flight strips, pending/sent PDCs, ASDE-X temporary data, ERAM route lines, and STARS line numbers — on every surface including connected CRC clients, while preserving your ERAM display preferences (velocity-vector length, CRR color, quick-look sets).
- The **focus command input** hotkey (default `` ` ``) now works from any YAAT window while the app is focused — the pop-out Radar, Ground, Aircraft List, Controllers, METAR, Terminal, and Favorites windows, plus the Flight Strips and TDLS windows — instead of only the main window. When the terminal is popped out, the hotkey focuses and brings forward the pop-out terminal's input rather than the hidden main-window input.
- Generated arrivals to the same runway no longer bunch up on final — a faster arrival behind a slower one is slowed to hold separation until you take the track.
- An aircraft told to extend its downwind (`EXT`) no longer triggers the midfield-downwind "uncleared" reminder — the pilot stops voicing it in solo training and the matching orange warning stops in RPO mode. Extending the downwind is itself a sequencing instruction, so the reminder was a false positive. Aircraft already cleared (`CLAND`/`COPT`) were already exempt; once an extended aircraft turns base or starts a fresh, uncleared lap the reminder applies again as before.
- On the radar, an aircraft's data-block leader line now draws thicker than its PTL, so the two stay distinguishable when both extend from the target.
- A pattern-entry command (`EF`, `ERB`, `ELB`, …) issued to an aircraft that is already cleared to land no longer cancels the landing clearance and turns the approach into a touch-and-go. The aircraft stays cleared for a full-stop landing — for example `CLAND` then `EF 28R` then `ERB 28R` now lands rather than touching and climbing away. Use `TG` or `COPT` to clear a touch-and-go or the option.
- `ER`/`EL` with both a taxiway and `NODEL` (e.g. `ER W5 NODEL`) now applies the auto-delete exemption and the correct named exit, instead of reading `W5 NODEL` as the taxiway name.
- Arrival generators set to the **SmallPlus** weight category no longer spawn regional jets (CRJ7/CRJ9/E170/E75L/E145/E135) onto short runways such as Oakland's North Field 28R (5,448 ft). SmallPlus now generates upper-small business jets (Citation Excel/XLS/Sovereign, Learjet 60/45) and commuter turboprops; the regional jets now spawn from **Large** generators on the long runways instead (the CRJ900 is back in the mix, since it's no longer competing for a short runway).
- A randomize-weight **Large** (mainline) arrival generator now spawns mostly airliners — mainline narrow-bodies and regional jets — with only an occasional business jet, instead of about a quarter business jets.
- A speed command (`RFAS`, `RNS`, `DSR`, `SPD`, or a Mach change) no longer cancels an in-progress 360/270 turn, S-turns, holding pattern, procedure turn, approach intercept, or VFR hold — it adjusts speed while the maneuver keeps running.
- `HOLD` (hold position) now stops an aircraft that is taxiing onto the runway after `LUAW`. Previously the aircraft ignored the hold and continued onto the centerline; now it stops where it is and stays lined up. Resume the line-up with `LUAW` or `CTO` (a plain `RES` no longer applies). `HOLD` issued once the takeoff roll has begun is rejected, with a reminder to use `CTOC` to cancel the takeoff clearance.

## v0.7.4-beta [2026/06/06]

### Highlights
- **Single-digit runways now display in FAA form** — `8R` and `9` instead of `08R` and `09` — on the radar, in lists and menus, on the ground map, and in pilot read-backs.
- **Right-click menus now match each aircraft's state** — no takeoff clearance for an airborne arrival, runway exits for one that just landed, and VFR-only items hidden for IFR aircraft.
- **`CROSS` works after a taxi that ended at a runway** — `TAXI B 28R` then `CROSS 28R` takes the aircraft across and holds it in position.
- **Solo training hides the pilot's comply time** — the student hears only the read-back, with no "complying in Ns" acknowledgement.

### Changed
- In **solo training mode**, the command-run delay no longer shows the "Pilot complying in Ns" acknowledgement — the student hears only the pilot's read-back and can't tell exactly how long the aircraft will take to comply. Instructor/RPO sessions still get the explicit acknowledgement.
- Single-digit runways now display in FAA form without a leading zero everywhere — `8R` and `9` instead of `08R` and `09` — in the radar datablock, aircraft-list columns, context menus, ground-map and hold-short labels, pilot read-backs (including the simulated pilot's spoken read-backs in solo training, e.g. "runway eight right" instead of "runway zero eight right"), command results, and the session report.

### Fixed
- In **solo training mode**, the radio `SAY` transcript now shows the simulated pilot's read-backs and reports in a compact controller form (e.g. `runway 8R taxi via B C D`, `fly heading 270`) with the callsign in the transcript's callsign column, instead of echoing the full spelled-out spoken phonetics.
- `CLAND 8R` (or any single-digit runway clearance) is no longer rejected for an aircraft established on that runway, and an armed `CLAND` for a following aircraft now lands it when it joins the single-digit runway's pattern. Previously the FAA-form `8R` failed to match the runway's `08R`-style designator.
- Right-click context menus (aircraft list, ground view, radar view) now show only the commands that fit the aircraft's state — airborne arrivals no longer offer "Line up and wait" or "Cleared for takeoff", departures no longer offer landing clearances, and a landed aircraft is offered runway exits.
- VFR-only tower clearances — touch-and-go, stop-and-go, low approach, cleared for the option, and pattern maneuvers — are now hidden for IFR aircraft.
- An aircraft cleared for takeoff (`CTO`) at a hold short facing nearly opposite the runway, as at KMIA 8R, now lines up and departs instead of freezing.
- `CROSS <rwy>` now works when the runway was the destination of the aircraft's previous taxi (e.g. `TAXI B 28R` then `CROSS 28R`) — both while it is still taxiing toward the runway and once it is holding short of it. The aircraft taxis across to the far side and holds in position. Previously it was rejected with "Cannot cross destination runway 28R; use LUAW or CTO".
- A runway named without its leading zero (e.g. `RWY 8R`, `TAXIAUTO 9`) no longer mis-routes ground taxi. Single-digit runway designators now match the airport's `08R`-style runway edges instead of silently failing, which previously steered auto-taxi to a different hold-short than the same runway written as `08R`.
- Aircraft landing on a single-digit runway (e.g. KMIA runway 9) now exit at a mid-field taxiway on the airport-designated turn-off side instead of rolling nearly the full runway length, even narrowbodies like a 737. The runway's authored turn-off side and no-turn-off taxiways were being dropped for single-digit runways, so the exit search fell back to a heuristic that could commit the aircraft to a single exit near the departure end.
- A manually spawned delayed aircraft (`SPAWN`) is now preserved through rewind and recording replay. Previously the spawn was dropped whenever the session was reconstructed, so the aircraft vanished after a rewind-and-resume and never appeared in saved bug-report bundles.
- A taxi clearance to a departure runway that lies beyond a runway crossing now routes all the way to the runway. For example `RWY 9 TAXI P S HS 12` at KMIA, where taxiway S crosses runway 12 on the way to runway 9, now holds short of 12 with runway 9 still the destination; `CROSS 12` then continues the taxi toward runway 9 instead of stopping just past the runway. `HS <rwy>` is a hold-short marker en route, not the end of the taxi. The route stays on the cleared taxiways and crosses each intervening runway (e.g. `RWY 30 TAXI C B W HS 28R` at KOAK crosses 28R then 28L to reach runway 30).
- A track on which the **student** has just accepted an incoming pointout now turns yellow on your radar to match the student's scope, completing the student-scope color mirror added in v0.7.3 — previously this one case kept your own default color even with student-scope sync on.

## v0.7.3-beta [2026/06/06]

### Highlights
- **Clear an aircraft to land on a specific runway** — `CLAND 28R`, including one still following traffic (cleared up front, then lands behind the traffic).
- **Cross a runway after a completed taxi** — `CROSS <rwy>` now works from a hold-short where the previous taxi route ended.
- **The radar now mirrors the student's STARS scope** — each datablock takes the student's color and (optionally) leader-line direction as intended; previously this shipped inert and every datablock kept your own default.
- **VFR sequencing holds behind an extending lead** — an aircraft told to follow traffic that is then sent to extend its pattern stays behind it instead of turning base early and rolling out ahead.

### Added
- `CROSS <rwy>` now moves aircraft across a runway from a hold-short even when the previous taxi route ended there.
- `CLAND 28R` clears an aircraft to land on a named runway — including one still following traffic, which is cleared up front and lands behind the traffic when it joins the final without a second clearance.
- The session-settings (⚙) flyout adds a **Show Pilot Speech (RPO)** toggle to switch sim-initiated pilot reports between green pilot speech and orange warnings live.
- Saved bug-report bundles now include original airport GeoJSON, and bundle tools can extract it for replay debugging.

### Fixed
- Taxi routes to a runway now consistently stop at the near-side hold-short when matching hold-shorts are reachable on multiple sides.
- A VFR aircraft told to follow traffic that is then sent to extend its pattern now stays behind it, instead of turning base early and rolling out on final ahead of the aircraft it was sequencing behind.
- The radar now mirrors the **student's** STARS scope as intended: each datablock takes the student's color (cyan when the student highlights a track; white/green/yellow by who owns it, including yellow for a pointout the student accepted) and, with the **leader-direction** option enabled (Settings > Display > Student Scope Sync), follows the student's leader line. The feature had shipped inert — every datablock kept the instructor's own default color and placement no matter what the student did on their scope — because the student's per-position scope state was never matched to the aircraft.
- Saving the Settings window no longer alters the running session; its scenario/simulation settings are load-time defaults, and live changes go through the gear flyout.
- The session-settings flyout's Command Run Delay range fields are no longer clipped — the values and spinner buttons now display fully.
- `TAXIAUTO` to MIA's south D-gates now routes through the closer concourse alley instead of taxiway N.

## v0.7.2-beta [2026/06/05]

### Highlights
- **Instructor range rings and cones** — `JRING 3` draws a 3 NM ring and `CONE 5` a 5 NM track-projection cone around a target on your own radar (1–30 NM); also on the radar **Display** menu.
- **Approach cones now appear in CRC STARS** — arrivals established on final get the automatic ATPA approach cone drawn ahead of them, shaded as the gap to the aircraft they're following tightens.
- **Rewind keeps track ownership** — scrubbing the timeline and resuming no longer reverts every aircraft back to you; each keeps the controller it had at the rewind point.

### Added
- The ground view no longer draws fillet corners no aircraft can taxi (sharp >155° hairpins the pathfinder never routes), reducing clutter.
- Instructor TPA J-Rings and Cones now draw on your own radar. `JRING 3` (or the radar **Display → J-ring** menu) draws a 3 NM ring around a target; `CONE 5` (or **Display → Cone**) draws a 5 NM cone projecting along its track — both sized 1–30 NM and labelled with the distance, emulating the STARS `*J` / `*P` proximity tools. They are instructor-only and never appear on the student's CRC. The cone's wedge matches CRC's razor-thin 2° by default and is adjustable under **Settings → Display → Overlays**. (Previously `JRING`/`CONE` drew nothing and discarded the size; the manual cone also leaked onto the student's scope.)

### Fixed
- Rewinding or scrubbing the session timeline and then resuming no longer hands every aircraft back to your position and reverts track ownership to the start of the recording — aircraft keep the ownership they had at the rewind point (e.g. a departure already handed to Center stays with Center instead of flashing a handoff to you). Saved bug-report bundles also now capture correct track ownership in their snapshots.
- Automatic ATPA approach cones ("P-cones") now appear in CRC STARS for arrivals established on final — the variable-length cone STARS draws ahead of a trailing aircraft toward the one it is following, sized to the required wake/radar separation and shaded monitor, caution, or alert as the gap tightens. They previously never appeared at any facility, because the server never told CRC which cone to draw, the approach volumes were oriented down the runway instead of up the final (so no arrival was ever counted as inside one), and parallel-runway volumes were mis-aligned by the airport's magnetic variation.

## v0.7.1-beta [2026/06/04]

### Highlights
- **Descend via the STAR** — `DVIA` now descends arrivals on their STAR's published crossing restrictions, even without a prior `JARR`, instead of firing silently and leaving them level.
- **`JARR` by landing runway** — `JARR TEJAS5 27` selects that runway's STAR transition and sets the destination runway.
- **Final approach lines up with the centerline** — ILS finals no longer track about a degree off to one side of the localizer.
- **Departures hold their SID's initial altitude** — an IFR departure cleared without an altitude climbs to and holds its SID's published initial altitude until you climb it.

### Added
- Ground view aircraft datablocks now show the CWT wake category alongside the type as `cwt/type` (e.g. `E/B738`), matching the radar view.
- `JARR` now accepts the landing runway as an argument — `JARR TEJAS5 27` selects that runway's STAR transition and sets the destination runway; combine it with an entry fix as `JARR TEJAS5 RIDLR 27`.
- IFR departures cleared without an altitude (e.g. a `CTO` with only a heading) now climb to and hold their SID's published initial altitude from the facility's vTDLS configuration (e.g. KIAH 4,000 ft, KHOU 5,000 ft) until you issue a climb; a later `CM` supersedes it.

### Fixed
- Arrivals now descend via the STAR: `DVIA` ("descend via") activates the STAR filed in the aircraft's route and applies its published crossing restrictions even without a prior `JARR`, instead of firing silently and leaving the aircraft level.
- `JARR` accepts a STAR name without its version digit (`TEJAS` resolves to the current `TEJAS5`).
- Final approach courses now line up with the runway centerline. They are converted from the published magnetic course using the airport's charted magnetic variation rather than the live value, so an aircraft no longer tracks about a degree off to one side of the localizer on a long final.
- A deferred or preset command that can't apply when it finally fires now reports a warning in the terminal instead of vanishing silently.
- Right-clicking a pop-out window (Aircraft List, Ground, Radar, Controllers, METAR, Terminal, Favorites) no longer opens an "Always On Top" context menu over the window body. Toggle Always on Top from Settings, the configurable hotkey, or — on Windows — the title-bar system menu (right-click the title bar or click the window icon).
- A `TAXI` clearance with a `CROSS <rwy>` clause is no longer rejected with "can't reach <taxiway> without crossing runway" when the crossed runway lies on an earlier named taxiway and a later taxiway follows (e.g. `TAXI C B CROSS 33`); the route now continues onto the last taxiway instead of stopping at the runway.
- A VFR aircraft told to **FOLLOW** traffic that is on a straight-in final (e.g. an IFR arrival on the ILS) is now sequenced onto that runway's final behind the traffic and descends there to await a landing clearance, instead of trailing it level at pattern altitude and flying over the runway when the traffic lands.
- The Aircraft List, sorted by any column with **Only Active** on, now slots a newly-active aircraft into sorted position instead of dropping it at the bottom.
- A command issued at the same instant the server advances a simulation tick could be applied mid-physics, occasionally producing behaviour that didn't match the deterministic replay — for example an aircraft cleared for takeoff that never started its roll. Command application is now serialized against the tick, so live behaviour matches replay.
- Ground view: with an aircraft selected, right-clicking anywhere now offers the taxi-route and **Warp here** menu by snapping to the nearest node, instead of only when the click lands within a node's hit radius — so you can warp an aircraft onto an open stretch of runway or taxiway that has no node directly under the cursor.
- Rewinding the timeline no longer loses why an aircraft is holding short (destination runway vs. runway crossing), which could break automatic departure release after `REL` and the runway-crossing commands after a rewind.
- Cross-runway closed-traffic departures (e.g. `CTO MRT 28R` from runway 33) line up and depart on the departure runway, then join the other runway's pattern.
- Cancelling a takeoff clearance (`CTOC`) mid-line-up holds the aircraft in position immediately; a fresh `CTO` resumes the line-up and departs.
- Two taxiing aircraft converging onto the same taxiway intersection (a merge) no longer deadlock nose-to-nose with both stopped — the one farther from the intersection now holds while the nearer one taxis through, then follows it in trail.
- `EF` (enter final) now flies an altitude-aware straight-in — a diagonal aircraft descends and cuts in close to the threshold instead of a fixed ~3-mile base, joining a longer final (or warning the controller) only when too high to descend in time.

## v0.7.0-beta [2026/06/04]

### Highlights
- Command run delay — optionally make aircraft pause a configurable random moment before complying with each instruction, simulating pilot readback time (off by default).
- Aircraft now read back taxi clearances aloud — route, hold-shorts, runway crossings, and destination — instead of taxiing silently.
- Generated arrivals now pick airlines in proportion to each airport's real traffic, so dominant carriers appear about as often as they actually arrive.
- More font-size controls (Settings → Display): separate Terminal and Interface sizes, plus Strips and vTDLS page zoom, remembered across restarts.

### Added
- **Command run delay** — optionally make aircraft take a configurable random delay before complying with each instruction (Settings > General or the command-bar flyout), simulating pilot readback/setup time; off by default, with an immediate "complying in Ns" acknowledgement.
- **Tools → Open TDLS in Browser** — opens the server's web vTDLS with your CID, initials, ARTCC, and room prefilled, mirroring Open Strips in Browser.
- An aircraft that lands and vacates between two parallel runways now auto-holds short of the parallel runway, so one `CROSS` takes it across; on by default (toggle in Settings).
- The command cheatsheet (**Help → Command Cheatsheet**) and User Guide now list keyboard shortcuts grouped per view alongside the command reference.
- **More font-size controls** (Settings → Display): separate Terminal and Interface sizes, plus Strips and vTDLS page-zoom percents (50–200%, remembered across restarts).
- The conditional list (`SHOWAT`/`SHOWCOND`) and **Pending Cmds** column now include pending `WAIT`/`BEHIND` commands; delete any conditional by index with `DELAT N` (aliases `DELCOND`/`DC`).
- **Per-airport taxi rules** — facility data can declare one-way taxiway segments and named connectors; auto-taxi avoids wrong-way routing and flags a wrong-way `TAXI` (SFO now recognizes the LF connector).
- Approach clearances and autocomplete accept the runway number without a leading zero — `CAPP I8R` resolves like `I08R` across `CAPP`/`JAPP`/`PTAC`.

### Changed
- Take Control during recording playback now confirms before ending the replay; Cancel or Esc keeps you in playback.
- A plain `SPD` below 60 KIAS to a helicopter is now floored to 60 (7110.65 §5-7-3.5) with a warning; use `SPEEDN` to command lower.
- A relocating helicopter (`ATXI`/`LAND`) now swings onto the destination spot faster as it slows toward the hover.
- The trainer now warns when a taxi clearance can't clear a runway (tail left over the bars), and arrivals avoid planning to use the blocked exit.
- **`CLRWY`** (alias `CLEARRWY`) — pull an aircraft holding with its tail over a runway forward until just clear, then hold; clears the "runway not clear" warning.
- **Per-taxiway turn hints** — prefix a taxiway with `>`/`<` in `TAXI` for a right/left turn onto it (e.g. `TAXI >A B <C D`); best-effort, with the echo noting any unhonored turn.
- Aircraft now read back taxi clearances aloud — route, hold-shorts, runway crossings, destination runway, and turn hints — instead of staying silent.
- Generated arrivals pick their airline in proportion to each airport's actual carrier traffic, so dominant carriers appear about as often as they really arrive and rare ones stay rare.

### Fixed
- Arrival generators now sustain a realistic in-trail stream paced by their configured time interval and the wake-turbulence minimum, instead of emitting a long line of aircraft at once.
- Arrival generators set to **SmallPlus** now spawn the regional/commuter feed (regional jets, commuter turboprops) instead of mainline narrow-bodies; Large is mainline-only and `ADD` gains an `S+` token.
- A generator's **Randomize weight** now varies the class around the configured weight, bounded to nearby classes — a Small/SmallPlus generator stays light and mixes in general-aviation (N-number) traffic, while a Large/Heavy generator never drops below the regional feed — so a short-runway generator no longer spawns a mainline jet; previously it ignored the configured weight and used a fixed 15%-small/85%-large split.
- The arrival-generator editor's **Runway** field is now an editable box with suggestions that always lists the runways already configured, even before the ground layout finishes loading.
- Aircraft from an arrival generator with an **AutoTrack** config now spawn already owned by the configured position with the scratchpad set and hand off to the student after the delay, like scenario-loaded aircraft.
- Arrival generators with no **max time** now run the whole session instead of stopping after an hour, and those with no **in-trail distance** space at the radar/wake minimum instead of a fabricated 5 NM.
- A scenario aircraft handed off with an inherited datablock now carries its interim and cleared altitudes on the ERAM datablock instead of dropping the interim altitude.
- Generated arrivals no longer spawn under defunct or foreign airlines mislabeled onto US airports (e.g. SalamAir at Oakland); the airport-airline crosswalk now resolves carrier codes correctly.
- Wake-turbulence separation on final now uses the FAA per-type CWT mile-based minima (7110.65 Table 5-5-2) instead of a coarse four-class approximation, so in-trail spacing is correct for each pair.
- Rewinding or replaying the timeline now preserves active **TIMER** countdowns and **hold-for-release** state and reproduces departure-release (`REL`) timing exactly.
- Aircraft flying a charted SID with heading legs (e.g. **LINDZ ONE** out of Aspen) now fly the published climb, turn, and course to the fix instead of turning direct at 400 ft AGL.
- Arrivals on a STAR that ends with a "fly the published course, expect vectors" leg now fly that outbound course and hold it awaiting vectors, instead of keeping whatever heading they arrived on.
- Playing a recording past a mid-session weather change or flight-plan amendment no longer hangs or ends playback early; rewinding no longer duplicates those actions.
- Dynamic METARs no longer stop updating after you scrub the timeline, load a recording, or restore a session; time-based weather keeps evolving and re-issuing observations (live-fetched weather is untouched).
- With no weather profile loaded, the vStrips METAR bar and the radar/ground winds-and-altimeters overlay now show the standard default report (calm, 10SM, clear, 29.92) instead of staying blank.
- Amending a flight plan with a non-US destination (e.g. `KSFO → WSSS`) via CRC or the `FP`/`VP` commands no longer silently reverts — international destinations are kept as filed.
- A helicopter told to air-taxi or land at a spot (`ATXI`/`LAND @spot`) now turns toward the spot, flies to it, and sets down — even if given a heading or altitude first.
- An airborne helicopter mid-air-taxi (or on final to a spot) can now be redirected with a normal airborne command (`FH`, turn, `CM`/`DM`, `SPD`, `DCT`); `HPP` hovers it in place.
- Two aircraft whose taxi routes cross at a junction no longer freeze into a mutual deadlock — one holds and the other proceeds; a genuine nose-to-nose head-on still stops both.
- A taxiing aircraft is no longer braked to yield for crossing traffic that will clear the junction well before it arrives; the ground slowdown applies only when both would reach the shared point together.
- Naming the taxiway an aircraft is already on as the first taxiway in a clearance (e.g. `TAXI M4 M2 …` from M4) no longer rejects it as "unreachable."
- Re-routing an aircraft holding at the dead-end of a short connector or spot (e.g. `TAXI B B1 …`) no longer rejects the clearance as "transition infeasible"; the route bridges onto the first cleared taxiway.
- A taxi clearance ending on a taxiway with no destination (e.g. `TAXI G B`) now stops at that intersection instead of turning a random way, and a final-taxiway hold-short (e.g. `TAXI B K HS 10R`) now heads toward the hold-short.
- A taxi clearance that holds short of a taxiway it also taxis along (e.g. `TAXI G B HS B`) no longer echoes the hold-short dozens of times; each taxiway hold-short is listed once.
- An aircraft holding short of a taxiway just past a runway crossing no longer spins ~180° back toward the runway to reach the line — it stops at the hold line as it arrives, tail still over the runway.
- `TAXI <twy> CROSS <rwy>` with no taxiway named past the crossing now uses the crossed runway to set direction, instead of sometimes resolving back across the runway behind it.
- Clicking a sent clearance in the vTDLS **PDC** list now re-opens it read-only — every field disabled, no Send — so a sent PDC can't be edited or resent (Dump still works).
- vTDLS clearances now leave the **PDC** list once the aircraft is tracked on STARS by any controller, instead of lingering until the 2-hour timeout.
- Replaying a recording now shows the controller's commands in the terminal as forward playback reaches them — the same `[Command]` lines you see live — instead of silent maneuvers.
- The aircraft-list **Hdg** column now reads magnetic to match **AHdg**, all UI headings show as 3-digit numbers (`090`, `360`), and the spoken heading readback (`SHDG`) reports magnetic too.
- Loading a scenario from the server catalog now prompts for difficulty (and shows solo-training pacing controls) when offered, instead of silently loading the hardest difficulty with default pacing.
- Arrivals cleared for a single-digit runway (e.g. runway **2** at KMSY) are no longer set up on the opposite end and stuck — the designator is now zero-padded to match the airport's runway data.
- Departures with on-handoff or at-altitude presets (`ONHO`, `AT`) now auto-taxi instead of staying parked, and queued conditional commands no longer cancel each other.
- `JFAC`/`JLOC` is now a lateral-only vector — the aircraft intercepts and tracks the localizer while holding its assigned altitude and speed, descending on the glideslope only once you issue `CAPP`.
- `JFAC`/`JLOC` now joins the localizer from any vector instead of blowing through it — a steep or late intercept is flown as a join that corrects back onto centerline rather than reporting "unable, passing through localizer."
- Issuing `CAPP` to an aircraft already established on a `JFAC`/`JLOC` join now authorizes the descent in place instead of cancelling and re-clearing it, removing the spurious "… cancelled by CAPP" warning.
- Arrivals now hold their crossing-restriction speeds after crossing a "cross at" fix (e.g. "cross GUSHR at 210") instead of accelerating back to 250.
- Aircraft no longer slow to near-zero on final from a speed limit misread on certain RNAV approaches (e.g. IAH RNAV 08R); the approach data now parses correctly and a minimum-speed floor prevents unflyable commands.
- Published "at or above" crossing speeds (e.g. "cross HHART at or above 230") are now flown as a floor instead of being read as a maximum that wrongly capped the aircraft.
- Multiple "cross at" (CFIX) restrictions now stack — a second cross-fix restriction no longer drops earlier ones — and CFIX applies in place instead of rerouting past fixes already ahead.
- An aircraft vectored to an ILS no longer starts down the glideslope before it's established on the localizer — it holds altitude until within ~5° and on centerline, then captures; pattern, visual, and forced (PTAC) intercepts are unaffected.

## v0.6.0-beta [2026/06/01]

### Highlights
- Hold for release — hold a satellite field's IFR departures and release them on your schedule with `HFR`/`REL`, plus a Releases panel and one-click release from the radar menu.
- Timeline bookmarks — mark moments on the timeline (a go-around, a conflict) and scrub back to them later; saved into recordings for debriefs.
- Timers — set countdown reminders with `TIMER`, global or attributed to an aircraft, with a live countdown button on the command bar.
- Controllers and METAR tabs — see the room's online controllers and each airport's METAR without opening CRC; both dockable and poppable into their own windows.

### Added
- **Timers** — set a countdown reminder with `TIMER <mm:ss|seconds> [message]` (alias `TMR`). When it expires it posts a green SAY line — your message, or `timer expired` if you didn't give one. Use it bare as a global instructor reminder (`TIMER 5:00 release the next SWA`), or prefix a callsign to attribute it to an aircraft (`N172SP TIMER 2:00 ready to copy`). Timers count in sim time (they pause with the sim and scale with sim rate). A timers button on the command bar shows the soonest one's live countdown; its flyout lists every running timer with one-click cancel, and you can also cancel with `TIMER CANCEL <id>` / `TIMER CANCEL ALL`.
- **Timeline bookmarks** — mark highlight moments on the timeline and scrub back to them later. Click the **🔖** button to add a bookmark at the current position with an optional name (or press **Ctrl+B** for a quick unnamed one; the key is configurable in Settings). Bookmarks show as gold ticks on the rail (click to seek, right-click to rename/delete), with **◀🔖 / 🔖▶** buttons to step between them and a **Bookmarks ▾** list to jump/rename/delete. They work in both live and playback modes and are saved into the recording and bug report bundle, so they reappear when you load that recording for a debrief.
- **Hold for release** — you can now hold a satellite airport's IFR departures until you release them, modeling the release coordination a TRACON gives towered fields. `HFR <airport>` arms a field (e.g. `HFR SJC`); its IFR departures are then held — ones that would appear airborne/lined-up wait off-scope, and ones taxiing out hold short of the runway instead of taking it. Release the next one with `REL <airport>` (or `CTOA <airport>`), a specific aircraft with `REL <callsign>`, or the whole field's queue auto-spaced with `REL <airport> <minutes>` (e.g. `REL SJC 2`). A **Releases** flyout on the command bar shows what's holding at each field with click-to-release buttons, and a held departure gets a one-click "Release (HFR)" item in its radar right-click menu. VFR departures are never held.
- A **Controllers** tab shows the online controllers in the current room — both live CRC-connected controllers and the scenario's auto-connect ATC positions — without opening CRC. It uses CRC's layout: grouped by facility, with each row showing the handoff/sector ID, position name, and frequency; inactive positions are dimmed and the callsign + controller name appear in a hover tooltip. The list updates as controllers connect/disconnect and as scenarios load/unload.
- A **METAR** tab shows the METAR for each airport in the active weather (scenario default or a loaded override), selectable for copy. With no weather loaded it shows default standard conditions (calm wind, 10SM, clear, 29.92) for each scenario airport.
- Controllers and METAR are dockable tabs that can be popped out into their own windows (**View > Pop Out Controllers / Pop Out METAR**).
- Pop-out windows (Aircraft List, Ground, Radar, Controllers, METAR, Terminal, Favorites) now have a right-click **Always On Top** toggle, in addition to the Settings keybind.
- METARs for loaded weather are now reconstructed from the live simulated conditions and re-issued like real observations: a routine report each hour at :53Z, plus an off-cycle SPECI when the wind, visibility, ceiling, or precipitation changes significantly. Live-fetched weather keeps its real METARs unchanged.
- When the simulator automatically slows one ground aircraft for another (a converging or in-trail conflict), the slowed aircraft's ground datablock now shows a "→{callsign} (auto)" badge so it's clear which traffic it's slowing for, instead of an unexplained slowdown. The right-click menu spells it out as "Yielding to" (converging) or "Following" (in trail). A controller-issued give-way still takes precedence.
- The browser flight-strips app (`/vstrips/`) now shows a collapsible METAR bar at the top with the current training METAR for the facility you're viewing — the airport for a tower, or all of a TRACON's airports — so students working strips in the browser see the weather too, not just instructors and RPOs. It follows the facility switcher and updates as the weather changes; click the chevron to expand from the primary airport to every airport.
- The radar can now mirror what the **student** sees on their STARS scope. By default each datablock takes the student's STARS color — white for tracks the student owns, green for tracks owned by another controller, yellow for a pointout to the student, cyan for a track the student highlighted — and is marked "(LDB)" or "(PDB)" when the student sees only a limited or partial data block. Two opt-in extras (Settings > Display > Student Scope Sync) go further: collapse the datablock to the reduced form the student actually sees, and orient the leader line to the student's leader direction. A datablock you've dragged keeps its position; "Reset to student position" in its right-click Display menu snaps it back. With no student position in the scenario, datablocks render as before.
- You can now pin a freetext **note** to an aircraft — `NOTE Watch wake` (or right-click → Note… on the radar, ground, or aircraft list, or click the note line on a EuroScope tag). It shows as an amber line at the bottom of the aircraft's datablock on both the radar and ground views, follows the aircraft across views, and persists through reconnects and recordings. Notes are instructor-only and never shown on the students' CRC scopes. Max 40 characters; a bare `NOTE` clears it.
- `L360`/`R360` now also accept the ATCTrainer aliases `ML3`/`ML360` and `MR3`/`MR360`.

### Changed
- The Aircraft List highlights the selected row clearly (even after focus leaves the grid), and alternating row shading can be toggled from the Choose Columns dialog.
- Tight delay maneuvers — 360s (`L360`/`R360`), 270s (`L270`/`R270`), and VFR holds (`HPPL`/`HPPR`, `HFIXL`/`HFIXR`) — now fly at a slow holding speed and resume normal speed when finished, instead of orbiting at whatever speed the aircraft was already doing.
- **View > Copy View Settings** is now a comparison dialog instead of a submenu. It shows your current view settings beside the source's, grouped into sections (map position, video maps, range, PTL, brightness, labels, filters, and more) with the differing ones highlighted, and you tick exactly which sections to copy. The source can be another scenario or a saved window profile — so you can also copy window geometry, pop-out/dock states, and the Aircraft List column layout. Map-position rows are flagged when the source scenario is a different airport.
- Creating a half-strip (right-click an empty rack → Add half-strip, or Ctrl+Shift+H) now puts the cursor in its first cell so you can type immediately.

### Fixed
- Track ownership and pointout commands no longer require an `AS <TCP>` prefix when the track belongs to a position other than your own. `HO`, `ACCEPT`, `CANCEL`, `DROP`, `PO`, and the pointout responses `OK`/`PORJ`/`PORT` now act on behalf of the relevant position read from the track itself — the current owner, the handoff target for `ACCEPT`, or the pointout recipient/sender — so an instructor/RPO ghosting several positions can work tracks directly, and the radar right-click Handoff/Accept/Drop buttons now work on any track. `TRACK` (claims an untracked aircraft) and the no-argument `PO` still use your active position.
- A `TAXI` clearance no longer has to name the taxiway the aircraft is already on. Continuing onto the next taxiway — `TAXI E RWY 28R` from an aircraft holding short on C, or `TAXI W` to one that just exited onto W5 — now succeeds instead of failing "unreachable" or warning "not in authorized path" for the occupied taxiway, and the readback no longer shows stray junction labels like "C - E". An aircraft given a `TAXI` just after a runway exit is also cleared to finish crossing that same runway.
- A `DCT` (proceed direct) issued right after a 360° orbit (`L360`/`R360`) now turns the aircraft the short way to the fix, not the long way around.
- ATPA volumes now honor their configured excluded TCP list — aircraft tracked by a position in a volume's excluded-TCP list are no longer paired for in-trail spacing in that volume. Previously the exclusion list was silently ignored and every aircraft in the volume was sequenced.
- An aircraft told to give way now resumes more reliably: it keeps holding while the other aircraft's taxi route still crosses its path ahead (instead of releasing the instant the other is no longer dead ahead), breaks a mutual give-way standstill once the other has been stopped a while and there's room to pass, and auto-resumes after 5 minutes if the named traffic never comes (e.g. a mistyped callsign) rather than waiting forever.
- The browser flight-strips (`/vstrips/`) and TDLS apps no longer swallow the browser's refresh shortcuts — F5 and Ctrl+R (Cmd+R on macOS) now reload the page as expected.
- Fixed-wing aircraft no longer hover on `HPP`/`HFIX`; the hover holds are rejected for them with guidance to `HPPL`/`HPPR` / `HFIXL`/`HFIXR` (helicopters still hover).
- `SPEEDN` is now accepted as an alias for force speed (`SPDN`), alongside `SLN`.
- A mistyped command after a callsign now names the unrecognized verb in the error instead of the callsign (e.g. `N929AW BOGUS` blames `BOGUS`, not `N929AW`).
- Scrubbing the replay timeline no longer leaves stale aircraft on CRC's STARS and Tower Cab views — tracks that aren't active at the scrubbed time are now cleared, and the open-position and consolidation displays are refreshed. Each track's owning controller is also preserved through a scrub in sessions recorded from this version onward.

## v0.5.0-beta [2026/05/31]

### Highlights
- Smoother ground handling — the taxi, turn, and corner logic was rebuilt, so aircraft follow the pavement more realistically and far fewer get stuck spinning in place or wandering off the taxiway.
- Taxiing aircraft's speech bubbles now show on the Radar view when no Ground view is up for their airport, so you don't miss ground prompts — plus new options for longer bubble duration and amber warning bubbles.
- The Aircraft List Info column shows each departing aircraft's lateral clearance and climb target at a glance (e.g. "Departing 28R, hdg 270, ↑ 3,000").
- Right-click another aircraft without changing your selection to issue traffic calls to the aircraft you're working — "report in sight" and "follow" on the radar, "give way to" and "follow" on the ground.

### Added
- `PUSH FACE` / `PUSH TAIL` during an active pushback amends the target facing in place, accepted until the nose begins rotating to the prior target. (#167)
- A taxiing aircraft's speech bubble now appears on the Radar view when no Ground view is showing that airport — including when the Ground view is docked but a different tab is in focus — so prompts from aircraft on the ground aren't missed. (#169)
- Option (Settings > Display > Overlays) to always show ground aircraft speech bubbles on the Radar view, even when a Ground view for that airport is open and in focus. (#169)
- Speech bubbles gained a duration multiplier (Settings > Display > Overlays) to scale how long they stay on screen. (#170)
- Optional amber WARN-message speech bubbles (Settings > Display > Overlays) overlay warning-channel messages on the aircraft, distinct from the green SAY/pilot bubbles. (#170)
- The Aircraft List Info column shows a departing aircraft's lateral clearance and climb target, e.g. "Departing 28R, hdg 270, ↑ 3,000" or "Departing 28R, right traffic, ↑ 1,400". (#171)
- With an aircraft selected, right-clicking a *different* aircraft now offers traffic actions issued to the selected aircraft that reference the one you clicked: in the radar view, "report in sight" (RTIS) and — once it has that traffic in sight — "follow"; in the ground view, "give way to" and "follow". Right-clicking a different aircraft no longer changes the selection, so you can point out and sequence traffic without reselecting.
- `ADD` can spawn a departure lined up on a runway with a filed route: `ADD IFR S P 28R NIMI6.OAK.SAU` files the route `NIMI6 OAK SAU` and sets the departure airport, so a subsequent `CTO` flies the filed SID. The route is a dot-joined token after the runway; a numeric token there is still the on-final distance.
- ARTCCs can mark per-airport taxiways the auto-router avoids in auto-taxi and "taxi to…" routing (e.g. OAK taxiway S), using one only when a destination is otherwise unreachable; explicit `TAXI` commands naming the taxiway are unaffected.

### Changed
- The ground-movement stack — taxi pathfinder, corner-fillet geometry, and ground navigator — was rebuilt from scratch using everything learned since the first release, for more realistic taxiing and far fewer aircraft stuck spinning in place or wandering off the pavement.

### Fixed
- Ghosting an aircraft (STARS AID + slew) that is already tracked by another position no longer steals the track from that position; the ghost is rejected with an ownership error. Ghosting an untracked aircraft, or one you already own, still works.
- ERAM `QT` (start track) and `QT D` (drop track) no longer take or drop a track owned by another sector without authorization; both are rejected unless the `/OK` logic-check override is included, which forcefully steals the track as documented.
- A helicopter given a bare `CTOPP` now lifts straight up into a hover and holds position (25 ft AGL) awaiting further instructions, instead of accelerating forward and drifting off along its parked heading. `CTOPP +0XX` holds at a higher AGL, and the directional forms (`CTOPP <hdg>` / `OC` / `DCT FIX`) now climb vertically before turning to depart.
- Training sessions paused for over an hour are now retired from the server even with users connected, ejecting them back to the room list with a notice.
- An IFR aircraft cleared for takeoff on a radar-vectors SID (e.g. NIMI6 off KOAK) no longer turns direct to the first enroute fix when the SID's published departure heading is missing from the current FAA CIFP cycle — for example during a procedure rename (NIMI5 → NIMI6) when the new id is briefly absent from the cycle's data. It now holds runway heading and awaits vectors, and recovers the published heading from a recently-cached prior CIFP cycle when one is available.
- Holding `F1` in CRC STARS (the beaconator) now shows aircraft callsigns, and CRC ERAM data blocks show the callsign instead of the beacon code.
- Aircraft cleared to cross a runway with `CROSS` now follow the painted taxi line through the crossing instead of cutting diagonally across the runway surface when the taxiway curves through the crossing (e.g. SFO H across 01L/19R).
- Aircraft cleared to cross a runway now cross at normal taxi speed and keep rolling all the way across, instead of slowing to a lower crossing speed or braking toward a stop at the far side before continuing onto the next taxiway.
- A runway crossing cleared before the aircraft reaches the hold line — an early `CROSS`, or an auto-cross taxi — is now flown as a proper crossing that tracks the painted taxi line across, instead of taxiing straight through; an aircraft still clearing the runway it just landed on is no longer treated as a new crossing of that runway.
- Ground taxi-route previews (the right-click route options and the route-drawing preview) now use the aircraft's actual performance category instead of always assuming a jet, so a turboprop, piston, or helicopter preview matches the route the taxi command will actually produce.
- When two aircraft's ground paths cross, the conflict resolver now stops one and lets the other proceed at normal taxi speed instead of slowing both to a crawl; an aircraft clearing the runway keeps priority over a taxiing aircraft crossing its path, and an aircraft giving way holds steadily instead of creeping forward.
- An aircraft still exiting the runway it just landed on, when given a TAXI whose route crosses that same runway, now taxis clear to finish its exit instead of incorrectly holding short of the runway it is leaving.
- Commands that combine a verb with a modifier — `RES CROSS 28L`, `RES HS B`, `CLAND NODEL` — are now accepted when typed in the command box, instead of being rejected with a misleading "callsign is not a recognized command" error; previously the modifier form only worked when split into separate commands (e.g. `RES; CROSS 28L`).
- An aircraft pre-cleared across an upcoming runway with a sequential `RES; CROSS <rwy>` no longer briefly flashes "holding short" of that runway before crossing — the crossing clearance now applies as soon as the aircraft resumes taxiing, rather than waiting until it reaches the hold line.
- Conflict alerts now fire between VFR aircraft at the standard STARS thresholds (3 NM / 1,000 ft), matching CRC, instead of only when their targets were nearly merged.
- The right-click **Command** entry on the radar, ground, and flight-list views now opens a focused popup you can type a command into.
- A VFR pattern aircraft told to FOLLOW traffic that is ahead of it (turning base or on final) now extends its downwind to fall in behind that traffic, instead of turning base at its own normal point and overtaking the aircraft it was told to follow. If it reaches its maximum downwind extension before it has spacing, it turns base and reports that the spacing is tight so you can re-sequence.
- The right-click menu for an aircraft holding short now offers to cross the runway it is actually holding short of, once. Previously the ground-map menu could repeat a nearby runway several times (e.g. four "Cross 28R" entries while holding short of 15/33), and the aircraft-list menu offered the departure runway instead of the one being held.
- A STARS primary or secondary scratchpad now rejects entries longer than the facility limit (3 characters, or 4 if enabled) instead of storing them, so a full callsign can no longer be set as one.
- A VFR aircraft with no filed destination now picks up your field as its implicit destination on pattern entry or landing, so it exits the runway and taxis after landing.

## v0.4.0-beta [2026/05/27]

### Highlights
- New vTDLS tab and browser app for pre-departure clearances. Auto-binds to the scenario's primary airport, switches between TDLS facilities, F12 sends the PDC, and the pilot accepts silently — no voice readback required.
- Speech recognition learned a large batch of FAA phraseology: cleared/expect LOC, LOC BC, VOR, and LDA approaches; named SID/STAR phraseology ("eagul five arrival" → EAGUL5); climb-via-SID; cross-fix altitude restrictions; pattern-entry approvals; runway-exit "turn left/right (taxiway)"; and several §3-7 taxi/ground synonyms.
- Opt-in speech-sample capture and Speech Debug window. Saves push-to-talk recordings with full pipeline traces (mic → Whisper → callsign → rule → LLM → final); multi-select export bundles selected recordings into a `.yaat-speech-sample.zip` to send to the devs.
- Compound commands accept `AND` and `THEN` as plain-English aliases for `,` and `;` — `CM 014 AND FH 090 THEN FH 180` works the same as the punctuation form; preserved inside `SAY` / `SAYF` text.

### Added
- Speech-sample capture and sharing: opt-in capture saves push-to-talk recordings and pipeline traces locally, the new Speech Debug window shows each session as a flowchart (mic → Whisper → callsign → rule → LLM → final) with playback and per-stage detail, and multi-select export bundles ticked recordings into a `.yaat-speech-sample.zip` to send to the devs — please share misrecognitions and false matches so we can keep improving speech recognition.
- Compound commands accept `AND` for `,` and `THEN` for `;` (case-insensitive); text inside `SAY` / `SAYF` is preserved verbatim.
- Bare `CROSS` clears the next uncleared hold-short on the taxi route, whether already holding short or still taxiing. `CROSS <target>` now also accepts taxiway and intersection names.
- `CROSS; HOLD` halts the aircraft right after it clears the far-side runway hold bars, so ground control can pick it up between parallel runways.
- Speech recognition understands cross-fix altitude restrictions ("cross CEPIN at/above/below five thousand", "at and maintain five thousand at two five zero knots" per §5-7) and chains "cross fix … cleared (type) approach". Pilot AI reads them back with the matching modifier.
- Speech recognition understands "climb via SID" and "climb via SID except maintain (altitude)" (FAA 7110.65 §4-3, §4-5, AIM §4-4). Pilot AI reads these back.
- Speech recognition understands "cleared (localizer / localizer back course / V-O-R / L-D-A) (runway) approach" (FAA 7110.65 §4-8), extending the existing ILS/RNAV forms. GLS still pending.
- Speech recognition tolerates adverbial wedges before takeoff/land clearances ("runway 28R shortened…", "full length…", "wind 270 at 15…", "at C5 intersection departure…", "change to runway 28R…") per FAA 7110.65 §3-9-7/10, §3-10. Modifiers are consumed; reduced landing distance is not modeled.
- Speech recognition understands pattern-entry approvals "straight in / left traffic / right traffic approved" (FAA 7110.65 §3-10-1).
- Speech recognition understands §3-7 taxi/ground synonyms: "continue taxiing via …" / "proceed via …" (= "taxi via …"), "across runway …" (= "cross runway …"), "hold for wake turbulence/traffic" (= "hold position"), and "behind (callsign)" (= "follow … on ground").
- Speech recognition understands "depart (fix) heading (degrees)" (FAA 7110.65 §5-6-6) and the "option approved" shorthand for "cleared for the option" (§3-10-11).
- Speech recognition understands "caution wake turbulence (traffic info)" (FAA 7110.65 §2-1-20); trailing traffic description is dropped.
- Speech recognition understands "expect (localizer / localizer back course / V-O-R / L-D-A) (runway) approach" (FAA 7110.65 §4-7-5), extending the existing ILS/RNAV/visual forms.
- Speech recognition understands named SID/STAR phraseology: "descend via the (STAR) arrival [, (transition) transition]", "(STAR) arrival" / "cleared (STAR) arrival", and "climb via the (SID) departure [except maintain (alt)]" (FAA 7110.65 §4-5, §4-7, §5-2, §5-5, AIM §5-4). Spoken names like "eagul five" / "eagle five" fuzzy-match to canonical CIFP IDs (EAGUL5), scoped to the aircraft's filed airports and the loaded ground layout.
- Speech recognition understands "maintain (speed) until (fix)" (FAA 7110.65 §5-7) and "cruise (altitude)" (§4-5, §6-6, AIM §4-4). `CRUISE`, `TAL`, and pilot-reported altitude now accept either shorthand-hundreds or literal-feet input.
- Speech recognition understands runway-exit instructions: "[if able] turn left/right (taxiway)" (FAA 7110.65 §3-10-9) and informal variants like "exit left/right [on (taxiway)] [if/when able]". Captured taxiway names are validated against the scenario's taxiway list.
- vTDLS pre-departure clearance: new **vTDLS** tab next to **Strips** in YAAT Client, plus a `/vtdls/` browser app on yaat-server. Auto-binds to the scenario's primary airport; Facility Menu switches between TDLS facilities; persistent dark mode. Nine-field PDC editor; Send (F12) broadcasts the pilot's ACARS message on a dedicated **TDLS** terminal channel. **View → vTDLS → New vTDLS Tab…** opens additional facilities in tabs or pop-outs for top-down TRACON ops.

### Removed
- The standalone **YAAT Flight Strips** installer (`YaatVStrips-*`) is no longer published — the same view ships inside YAAT Client as the Strips tab, and the browser version at `/vstrips/` stays available.

### Fixed
- A rejected `DCT`/`ADCT`/`FDCT` (e.g. to an unprogrammed fix) during an RV SID initial climb no longer drops the published vectors heading hold.
- Misheard NATO suffix letters in GA tail numbers (Whisper's "gulf" for "golf") no longer truncate the callsign; "november three four six gulf" resolves to N346G.
- Tower and pattern clearances accept runway-prefixed phrasing — "runway 28R cleared for takeoff" matches "cleared for takeoff runway 28R" (CTO, CLAND, LUAW, TG, LAHSO, MLT/MRT, ELD/ERD/ELB/ERB).
- "Make straight-in" and "enter straight-in" tower instructions parse to EF with the runway captured, with or without trailing "approach" (AIM 4-3-3).
- Arrival generators no longer silently drop spawns when a scenario's weight/engine combo has no curated aircraft types (e.g. Large+Piston) — the generator falls back to the nearest engine-matching bucket.
- Pop-out state (including the Terminal) and window positions now survive every app exit, including `Ctrl+C` in the launch script, File > Exit, and the confirm-exit dialog after closing with a scenario loaded.
- TAXI right after `PUSH A FACE …` no longer makes the aircraft spin a loop when it was already pointing the right way.
- `PUSH <taxiway>` now positions the aircraft on the taxiway itself instead of stopping short on the curved fillet from the parking ramp.
- Large jets at OAK JSX1 (and other tight nose-in parking spots) no longer orbit the ramp endpoint forever — the slow-turn alignment now fires for the narrower segments these spots use.

## v0.3.7-beta [2026/05/25]

### Fixed
- `DCT`/`ADCT`/`FDCT`/`AFDCT`/`TRDCT`/`TLDCT` (including queued `AT FIX`/`LV` variants) during an RV SID initial climb now amends the route instead of trapping the aircraft in the climb.
- Planned-restart session save now works when the checkpoint directory is a volume mount point — the swap stays inside a subdirectory so the mount point itself is never renamed.
- A server crash between the checkpoint swap's two renames recovers on the next start instead of deleting the orphaned data and losing the session.

## v0.3.6-beta [2026/05/23]

### Highlights
- Opt-in speech bubbles overlay SAY commands and RPO pilot transmissions on the Radar and Ground views — click a bubble to dismiss it.
- `ONHS DEL` auto-deletes a landing aircraft when it reaches its post-runway hold-short; `NODEL` cancels the queue and exempts the aircraft from scenario auto-delete too.
- Lateral commands (`ADCT`/`DCT`/`H`/`TL`/`TR`) issued during initial climb now amend the heading or route alongside the CTO-assigned altitude instead of cancelling the climb.
- Bare `CTO` on radar-vectors SIDs holds the published vectors heading all the way through the initial climb instead of dropping the hold around 400 ft AGL.

### Added
- Opt-in speech bubbles (Settings → Display → Overlays) overlay SAY commands and RPO pilot transmissions on the Radar and Ground views — click a bubble to dismiss it early.
- `ONHS DEL` queues an auto-delete that fires the instant a landing aircraft reaches its post-runway hold-short, with a trailing `*` on the datablock while armed.
- `NODEL` bare verb cancels a queued `ONHS DEL` and exempts the aircraft from scenario-level auto-delete too.

### Fixed
- `RES, CROSS <rwy>` while holding short of a different runway now pre-clears the upcoming crossing instead of failing with "Not holding short".
- The Strips workspace now outlines the currently-selected bay button in white so it's obvious at a glance which bay drives the rack area.
- Aircraft active in the scenario stay in the Aircraft List when a student drops a ghost track on them in CRC STARS (AID + slew).
- Issuing `ADCT`/`DCT`/`H`/`TL`/`TR` during initial climb no longer cancels the climb — heading and route amendments now apply alongside the CTO-assigned altitude.
- Radar datablock and tag now show the wake-turbulence class (e.g. `I/SR22`) for VFR cold-call aircraft and others without a filed flight plan type.
- Radar-vectors SID departures cleared with a bare `CTO` (no altitude assignment) now hold the published vectors heading all the way through the initial climb instead of dropping the hold around 400 ft AGL and turning direct to the first enroute route fix.
- Chained `EXT` / `EXT UPWIND` after a tower command (e.g. `COPT; EXT UPWIND` on final approach) now arms the upcoming upwind instead of being silently dropped.
- `DCT VPCBT; ERB 28R` and `AT VPCBT ERB 28R` no longer reject at typing time — deferred commands check feasibility when the trigger fires.
- Terminal panel filter box is wider, and clearing it now scrolls back to the bottom instead of jumping to the top.

## v0.3.5-beta [2026/05/23]

### Highlights
- New `OFL` / `OFR` commands dogleg a pattern aircraft 0.1–1.5 NM left or right for in-pattern spacing on upwind, crosswind, downwind, or base.
- Sequential commands like `TAXI E RWY 28R; CTO MRT` now auto-fire the takeoff clearance when the aircraft reaches the hold-short.
- `CM`, `DM`, and `SPD` issued mid-orbit or mid-S-turn now apply alongside the maneuver instead of cancelling it.
- Aircraft taxied to a destination runway via an angled connector (e.g. OAK W1 to RWY 30) now line up properly so the follow-up `CTO` rolls instead of stalling.

### Added
- `OFL` / `OFR` (`OFFSETL` / `OFFSETR`) doglegs a pattern aircraft 0.5 NM left or right (range 0.1–1.5 NM) for in-pattern spacing on upwind, crosswind, downwind, or base.

### Fixed
- Sequential compounds like `TAXI E RWY 28R; CTO MRT` now auto-fire the queued takeoff clearance when the aircraft reaches the hold-short.
- Session Report **Aircraft** tab now refreshes the Operation column (Departure / Arrival / Transit), filed route, and aircraft type the moment a flight plan is amended — previously the row stayed on the pre-amend values until some other aircraft event happened to invalidate the cache.
- A planned-restart prepare that fails partway through (disk full, I/O error, retry after a prior failure) no longer destroys prior session checkpoints or leaves rooms stuck paused. The server now writes checkpoints to a sibling staging directory and only swaps them into the live path after every room saves; on any failure, the live directory is left intact and paused rooms resume automatically.
- `EXT` (and `EXT UPWIND`) now arm the upcoming upwind when issued during a touch-and-go ground roll, pre-T/G final approach, or pre-takeoff.
- Issuing `CM`/`DM`/`SPD` to an aircraft mid-orbit (after `R360`/`L360`/`R270`/`L270`) or mid-S-turn (`ST`) no longer cancels the turn — altitude and speed clearances now apply alongside the lateral maneuver instead of tearing it down.
- `EF` on a parallel runway defaults to the side that keeps the downwind clear of the sibling (28R → right, 28L → left), so subsequent `COPT` or auto-go-around cycles stop overflying the parallel.
- Aircraft taxied to a destination runway via an angled connector (e.g. OAK W1 to RWY 30) no longer end up on the runway centerline during taxi, so the follow-up `CTO` lines up and rolls instead of stalling at the threshold.

## v0.3.4-beta [2026/05/22]

### Fixed
- Radar-vectors SID departures cleared with `CTO` during taxi hold the published vectors heading instead of turning direct to enroute fixes when the assigned runway was not set at clearance time.
- Radar-vectors departures on transitions with climb-before-vectors legs hold runway heading until 400 ft AGL, then apply the published vectors course.
- Amending a flight plan to add a runway-specific SID after an early `CTO` refreshes the stored departure clearance and vectors heading.

### Added
- Training sessions now survive planned server restarts. When the server announces a restart, an amber banner appears at the top of the main, radar, and ground windows with a live countdown ("Server restarting for planned maintenance — session resumes in ~30s"), command input freezes, and the active room id is remembered. After the server comes back, the client auto-rejoins to the same room with the scenario paused at the moment of save (aircraft, route, phase, strips, ASDEX, ERAM, line numbers, and in-flight RD/RDH coordination items intact); the banner briefly flashes green ("Reconnected to your room — session resumed at T+182s") then dismisses. The droplet deploy script (`deploy-to-droplet.ps1`) opts in by default — pass `-SkipSessionSave` for an emergency deploy that drops active rooms.

## v0.3.3-beta [2026/05/22]

### Highlights
- Changing the expected approach runway now updates the STAR transition on the navigation route instead of leaving fixes from the previous runway.
- Re-issuing approach, runway, or routing commands no longer leaves obsolete STAR or approach fixes stacked on the route.
- Assigning a runway to an airborne arrival refreshes the STAR transition; changing destination clears arrival routing without canceling departure taxi.

### Fixed
- `EAPP` for a different runway removes STAR fixes from the prior runway transition instead of leaving the old path in the route.
- Superseding `CAPP`, `RWY`, `DCT`, `APT`, or `JFAC` (and a second deferred `CAPP`) drops stale approach fixes from the navigation route instead of stacking them.
- Airborne `RWY` assigns the arrival runway and refreshes the STAR transition; `APT` to a new airport clears arrival routing without canceling departure taxi.

## v0.3.2-beta [2026/05/21]

### Highlights
- Session Report now has an **Aircraft** tab — one row per aircraft that passed through your airspace, with operation, filed route, status, finding counts, and a one-line coaching note. Rows stay listed after landing, handoff, or drop.
- **Finding and command markers** on the rewind timeline bar — color-coded ticks (red Safety, amber Warning, blue Coach) for findings, plus grey ticks for every dispatched command. Click any marker to rewind. From the Aircraft tab, "Show on Timeline" filters the rail to a single callsign and jumps to its spawn time.
- `EAPP` now assigns the destination runway and extends the active STAR — telling a pilot to "expect ILS 30" sets arrival runway 30 and threads the STAR's runway-transition fixes into the live route, matching real-world phraseology.
- **"Show flight path"** now draws procedure vector tails and expected approaches on the radar — a 5 nm arrow off the last STAR fix for radar-vector procedures (WNDSR2, NIMI5, …), and a separate IAF/transition → FAF → threshold line for any aircraft with `EAPP` set but not yet cleared.

### Added
- Session Report → **Aircraft** tab — one row per aircraft that has entered the session, showing operation (Departure/Arrival/Transit), filed route, spawn/completion times, status (Active / Landed RW / Handed off …), finding counts broken out by severity, and a one-line coaching note. Aircraft stay listed in the tab after landing, handoff, or drop so you can review every aircraft that passed through your airspace.
- **Marker overlay on the rewind timeline bar** — finding markers from the session report render as color-coded ticks (red Safety, amber Warning, blue Coach) above the scrub slider. Click a marker to rewind to that moment; hover for the finding title and timestamp. Refreshes every 5 seconds while a scenario is loaded.
- Session Report Aircraft tab → **Show on Timeline** — selects an aircraft, clicks the button, and the main timeline bar filters markers to that callsign's findings only and rewinds to that aircraft's spawn time. A "Filter: <callsign> ×" pill on the timeline bar clears the filter.
- **Command markers on the timeline rail** — each successfully-dispatched aircraft command shows up as a thin grey tick on the marker rail at the time it was issued. Click to rewind, hover to see the canonical command and recipient. Findings and commands share the rail; commands sit slightly lower so they don't compete with the colored finding ticks. The active filter (when set from the Aircraft tab) also narrows command markers to the selected callsign.
- **`EAPP` now assigns the destination runway and extends the active STAR** — telling a pilot to expect a specific approach (e.g. `EAPP I30`) sets `DestinationRunway` to that approach's runway, matching real-world phraseology where "expect ILS Runway 30 approach" implies arrival runway 30. If a STAR is already loaded, its runway-transition fixes for the new runway (e.g. HOPTA → ALLXX → CRSEN on WNDSR2 RW30) are appended to the live navigation route — so the published radar-vector tail enters the route immediately, without waiting for `CAPP`. A `JARR` issued after `EAPP` (no prior `RWY`) also picks up the runway transition now. Bare `CAPP` still resolves from `ExpectedApproach` and commits the runway as before.
- **Show flight path now displays vectors and expected approaches** — for an aircraft on a STAR that ends in a published radar vector (the trailing `FM`/`VM`/`VA` leg on procedures like KOAK's WNDSR2, OAKES3, EMZOH4, PIRAT3), the radar draws a 5 nm arrow off the last STAR fix on the published heading. Same treatment for radar-vector SIDs and for pure-heading aircraft with no upcoming fix. Additionally, if `ExpectedApproach` is set but the approach hasn't been cleared yet, the expected approach is drawn as a separate, non-connecting line — IAF/transition → FAF → runway threshold, with the FAC extended backward 5 nm when no transition is named so you can see where to vector the aircraft. Both segments share the aircraft's allocated path color.

## v0.3.1-beta [2026/05/21]

### Fixed
- Radar-vectors SIDs (NIMI5/6 out of KOAK) no longer turn the aircraft toward the post-SID route the moment radar identity moves to departure — the heading hold now releases on the actual `CT`/`FCA` handoff, so tower students get the full window between liftoff and frequency change to work comms before the turn starts.

## v0.3.0-beta [2026/05/19]

### Highlights
- Rating-gated training scenarios. ARTCC scenarios marked `Student3`, `Controller1`, or `Instructor1` (OTS, advanced, or instructor material) stay hidden in the picker until you enter a training access key — facility TAs hand out keys per ARTCC.
- Inline "N scenarios hidden — requires training access key" note on the ARTCC tab when scenarios you don't have a key for exist for your facility.
- New `Settings → Identity → Training access key` field. Three hierarchical tiers (S3, C1, I1) — a higher-tier key automatically unlocks everything below it.

### Added
- `Settings → Identity → Training access key` — masked field for the per-ARTCC key your facility TA hands out. One key, no prefix; the server tells the client which tiers it unlocks. Three tiers exist (S3, C1, I1); a higher-tier key hierarchically unlocks all lower-tier scenarios as well.
- Server-side scenario enumeration for the ARTCC tab. The client receives only the scenarios its key unlocks; hidden ones never reach the wire. Local-tab JSON loads are unchanged — no key required, dev workflow preserved.
- Inline "N scenarios hidden" affordance on the ARTCC tab when one or more scenarios were filtered by the gate.

### Changed
- `LoadScenarioWindow` ARTCC tab fetches from the YAAT server instead of vNAS directly. The rating dropdown is gone (server filter supersedes it); the facility dropdown stays.
- ARTCC-tab loads send only the scenario id to the server, not the JSON body. The server resolves the canonical scenario data from its own catalog and applies the rating gate against that — client-side edits to a downloaded scenario file no longer affect the gate decision on this path.

## v0.2.6-alpha [2026/05/18]

### Highlights
- Aircraft on final without a landing clearance now flash a red `NoLndgClnc` line on the radar datablock — opt out under Settings → Display → Radar Display.
- `View → Window Profiles` saves and restores named window arrangements (geometry, dock/pop-out state, DataGrid columns) — switch between GC/LC layouts in one click.
- Solo training adds a pilot go-around probability slider (0–100%) so each AI aircraft entering final may spontaneously go around.
- `RES CROSS rwy...` pre-clears upcoming runway crossings and `RES HS target...` adds or promotes hold-shorts on the rest of the taxi route.

### Added
- Aircraft approaching final without a landing clearance flash a red `NoLndgClnc` on the radar datablock — opt out under Settings → Display → Radar Display.
- `View → Window Profiles` saves and restores named window arrangements (geometry, dock/pop-out state, DataGrid columns) — quick switch between GC/LC layouts.
- Aircraft List grid has a new `Name` column (next to `Type`) showing human-readable aircraft types — `C172` → "Cessna Skyhawk 172", `B738` → "Boeing 737-800".
- Solo training: pilot go-around probability slider. Each AI aircraft entering final approach now rolls once against a per-approach percentage (0–100) and spontaneously goes around if the roll succeeds. Set a global default in `Settings → Scenarios`; override per-scenario from the scenario setup dialog (when it appears) or the live session settings flyout (the per-scenario value persists on either path). Defaults to 0% (existing behavior unchanged).
- `RES [CROSS rwy...] [HS target...]` resumes taxi with optional CROSS (pre-clear crossings) and HS (add/promote hold-shorts) modifiers on the rest of the route.

### Fixed
- Radar datablocks, EuroScope tags, and right-click menus now show the aircraft type when no flight plan has been filed — they fall back to the physical type that the Aircraft List already displays.
- Partial-callsign prefixes that match multiple aircraft now list the candidates (e.g. `"N12" matches multiple aircraft: N1234, N1256`) instead of saying `"N12" is not a recognized command`.
- Aircraft list Info column now names the runway actually being crossed instead of the departure runway while an aircraft taxis across one.
- IFR aircraft on radar-vectors SIDs (e.g. NIMI5, OAK6 at OAK) no longer turn back toward the departure airport after takeoff. The route-expander's "emit all transitions on mismatch" fallback was fabricating a route like `CCR, OAK, PYE, OAK, SAC, OAK, SAU, OAK, SGD, OAK, FESIK, …` for a filed `NIMI5 OAK V6 SAC` because vNAS encodes those SIDs' adapted-route hints as synthetic transitions. Flight-plan callers now suppress that fallback, so the route is just the V6 airway fixes (or, for a `NIMI5 OAK <fix>` route, just the direct fix).
- TERM CTL via CRC STARS now actually releases the track. Previously, dropping a track that you had created via DA/VP (or AID + slew) immediately re-acquired on the next tick because the FP-creator auto-track entitlement was never consumed. Controllers had to retry TERM CTL repeatedly or delete the aircraft to get rid of it. Now a manual DROP consumes the entitlement, so the drop sticks.
- `MLT 28L` (or `MRT 28R`) issued mid-pattern after a touch-and-go on the other runway no longer leaves the aircraft turning toward the old pattern side. The handler now rebuilds the chain from the current leg with the new direction, and inserts a midfield crossing when the aircraft ends up on the wrong physical side of the new runway — matches what `ELD 28L` would have done.
- An aircraft cleared to land that was then given `ERB`/`ELB` mid-rollout no longer re-accelerates down the runway. The post-landing branch in the phase runner was keying on `TrafficDirection`, which `ERB` restores after `CLAND` clears it; routing is now keyed off the actual terminator phase (`LandingPhase` → exit; `TouchAndGoPhase`/`StopAndGo`/`LowApproach` → auto-cycle).
- Touch-and-go aircraft now roll on the runway for a realistic 6-10 s before lifting off again, instead of bouncing back into the air after only a couple of seconds. Stop-and-go aircraft pause at full stop a bit longer too (#8).

## v0.2.5-alpha [2026/05/16]

### Highlights
- Removed the bogus "holding outside the charlie, awaiting two-way" radio call from solo-mode VFR aircraft.
- Pilot readbacks for `ERD`/`ELD`/`ERB`/`ELB`/`MLT`/`MRT` and `EXT` now include the runway and the correct pattern leg.
- VFR cold-call check-ins recognize `KOAK`-style destinations as inbound for landing and say so instead of "request transition".
- Edit Arrival Generators dialog now shows the correct runway in its per-generator dropdown.

### Fixed
- Re-sending the same compound command no longer emits a spurious "queue cleared by … (lost: …)" warning listing the blocks the same dispatch is about to re-enqueue. The warning now only names blocks that were truly dropped — i.e. ones whose canonical form isn't repeated in the new compound.
- `EXT` readback now matches the leg the aircraft is actually on, and includes the runway. Bare `EXT` to a downwind aircraft used to read back "extend upwind" because the parser leaves the leg null and the verbalizer fell to the first-declared rule. Now reads back as "extend downwind runway two eight right".
- Pilot readbacks now include the runway when one was assigned. `ERD 28R` reads back as "enter right downwind runway two eight right …" instead of just "enter right downwind …". Same fix for ELD, ERB, ELB, MLT, MRT and any other pattern-entry command that carries a runway.
- Pilots now re-prompt after 90 seconds (down from 5 minutes) when a controller's only response was a bare `STBY`/`ROGER`. A `ROGER` doesn't fulfill a landing or taxi request — the pilot was sitting silent far too long before re-asking.
- VFR cold-call check-ins now correctly say "inbound for landing" when the flight plan's destination is the primary airport but uses the `K`-prefixed ICAO form (e.g. `KOAK` vs scenario id `OAK`). Previously the K-prefix mismatch routed every K-filed inbound through the "request transition" phrasing.
- Solo-training VFR aircraft no longer narrate "holding outside the charlie, awaiting two-way" when their projected track would enter Class B/C airspace. The aircraft still slows and orbits to respect the boundary, but does so silently — real pilots don't announce their own avoidance manoeuvres on the radio.
- Edit Arrival Generators dialog now displays the correct runway for each generator. The dropdown was being populated by splitting runway names on `/`, but ground-layout runway names use ` - ` (e.g. `28R - 10L`), so the loaded value (`30`, `28R`) never matched a dropdown entry and the field rendered blank.

## v0.2.4-alpha [2026/05/13]

### Highlights
- `TAXIAUTO <runway|@parking>` auto-routes the taxi from the aircraft's current position — runway form heads to the full-length lineup.
- Right-clicking a taxiing aircraft now offers `Break conflict` (15-second override of the ground-conflict slowdown) and, when a runway is already assigned, the full `Cleared for takeoff …` submenu — pre-clear a takeoff while the aircraft is still taxiing.
- Right-click a runway threshold on the ground view to open the same taxi menu as the hold-short node, with a `RWY {end}` hover label confirming which runway is targeted.
- Ground aircraft converging on a taxiway no longer freeze each other at near-zero ground speed; ground datablocks now flag held aircraft as `HOLD` (HOLDPOSITION) or `→CALLSIGN` (GIVEWAY).

### Added
- `TAXIAUTO <runway|@parking>` auto-routes the taxi from current position; the runway form targets the full-length lineup.
- Right-clicking a taxiing aircraft now exposes `Break conflict` (issues `BREAK` to override the ground-conflict speed limit for 15s) and, when a runway is already assigned, the full `Cleared for takeoff …` submenu so a CTO can be pre-issued mid-taxi.
- Hovering a runway threshold marker shows a `RWY {end}` label so the controller can confirm which runway is about to be targeted before clicking.
- Right-clicking a runway threshold marker now offers `Draw taxi route…`, `Custom taxi…`, and `Warp here` alongside the taxi-for-takeoff variants — same actions as right-clicking the corresponding hold-short node.

### Changed
- Ground view runway thresholds and runway hold-shorts show a hand cursor on hover when an aircraft is selected; right-click on a runway threshold opens the same Taxi menu as left-click.
- The taxi-submenu labels that read "Takeoff RWY 28R" now read "For Departure 28R" — they're a taxi destination, not a takeoff clearance.
- `Resume taxi` is only offered when the aircraft has an unfinished taxi route assigned. Aircraft holding after pushback or after exiting a runway without a pending taxi command no longer show the option (RES wouldn't do anything).
- Ground datablocks show a status suffix when an aircraft is held: `HOLD` for a `HOLDPOSITION` and `→{CALLSIGN}` for a `GIVEWAY`. The right-click menu now carries a header line ("Held: position" or "Yielding to: SWA123") so the controller-anticipation relationship — "behind the SWA taxiing left to right, taxi via B C D" — is visible everywhere the operator looks. The ground conflict detector also distinguishes controller-issued GIVEWAY pairs from anonymous stationary obstacles in its DebugSink output ("[Pair] ControllerGiveWay N123→SWA456").

### Fixed
- Two aircraft converging on the same taxiway from different feeders no longer pin each other at near-zero ground speed for an extended period. Ground-conflict detection was rewritten around a single-pass pair classifier: each pair maps to exactly one of `SameEdgeTrailing` / `SameEdgeHeadOn` / `Converging` / `Crossing` (plus the stationary / pushback cases), and only one resolver runs. Bundled S1-OAK practical exam regression test covers SWA863 / NKS743 on taxiway U.
- Same-edge head-on no longer mutually stops both aircraft. A deterministic holder is picked (aircraft with more route remaining, tie-broken by callsign) so one proceeds and one waits, instead of both freezing forever on a single-lane segment.
- A pinned ground aircraft (gs ≈ 0 with no controller hold) now reclassifies as a stationary obstacle, so other traffic can pass laterally beside it with wingspan clearance. Closes the systemic "stuck pair won't unstick" failure mode.
- Controller-held aircraft (`GIVEWAY`, `HOLDPOSITION`, `BEHIND`) are now treated as stationary obstacles by the conflict detector. A `GIVEWAY`-held aircraft on a taxiway no longer blocks passing aircraft via closing-proximity when there's lateral wingspan clearance.
- Same-edge same-direction trailing now uses the real distance from the aircraft to the segment's target node. Previously both aircraft compared the full edge length and the trailer was effectively picked by aircraft list order.
- Aircraft taxiing out of OAK parking no longer orbit the ramp at sharp corners; all OAK parking spots reach a runway hold-short reliably.
- Aircraft no longer snap their heading when given a TAXI command after pushback or at parking when the route's first segment heads away from the aircraft's current direction. The navigator now traces a slow-turn from the current pose to the segment start tangent at nose-wheel turn radius and walking pace, instead of writing the arc tangent into the heading at first tick.
- Window geometry now persists across in-app updates. Velopack's restart bypassed Avalonia's window-closing pipeline, so the per-window save (hooked via `Window.Closing`) never ran; geometry is now flushed for all tracked windows before `ApplyUpdatesAndRestart` hands off.

## v0.2.3-alpha [2026/05/11]

### Highlights
- Aircraft right-click menus now offer a Favorite Commands submenu that runs your active favorites against the clicked aircraft.
- Aircraft List title shows pending delayed-spawn count (or "No pending spawns" once they've all fired).
- IFR `CTO` restricted to bare `CTO` (follow SID) or numeric heading vector — pattern/runway-relative modifiers are now rejected with an explanation.
- IFR departures hold runway heading at liftoff and only start the assigned vector past the departure end and above 400 ft AGL (TERPS).

### Added
- Aircraft List title shows the pending delayed-spawn count; reads "No pending spawns" once the last delayed aircraft fires.
- Aircraft right-click menus (aircraft list, ground, radar) include a Favorite Commands submenu that runs the active favorites against the clicked aircraft.

### Changed
- IFR `CTO` only accepts bare `CTO` (follow SID) or `CTO` with a numeric heading vector. Pattern and runway-relative modifiers (`MRC`/`MRD`/`MR{N}`, `MLC`/`MLD`/`ML{N}`, `MRH`/`MSO`/`RH`, `OC`, `DCT`/`TLDCT`/`TRDCT`, `MLT`/`MRT`) are now rejected on IFR aircraft with a message explaining the restriction.

### Fixed
- IFR departures no longer turn off the runway at liftoff. The assigned heading vector is held on runway course through `TakeoffPhase` and applied by `InitialClimbPhase` only after the aircraft is past the departure end of runway AND at or above 400 ft above field elevation (TERPS criterion). Matches the VFR deferral already in place per AIM 4-3-2, with a 400 ft floor in place of pattern altitude − 300 ft.
- Altitude and speed commands (`CM`/`DM`/`SPD`/`RFAS`/`RSP`/`DSR`/`M`) are additive across the takeoff climb, the go-around, the visual follow, and final approach outside 5 nm — adjusting an altitude or speed no longer cancels the active phase's heading, follow target, or missed-approach climb.
- Minimized pop-out windows preserve their restored position and size across app restarts.
- Aircraft List Info column shows the taxiway intersection at hold-short — e.g. "Holding short 28R @ E" or "Holding short of E on C".
- `CTO MR<n>; DCT FIX` (or `ML<n>`) now takes the short way to the next fix after the departure turn rolls out.
- Jets starting at parking with a preset `TAXI` command no longer spin at the first ramp corner.
- `FOLLOW` clears any prior `EXT` on upwind/crosswind/downwind so the follower stops extending and sequences behind the leader.
- `FOLLOW` no longer auto-cancels with "unable to catch up" when the lead is ahead on a later pattern leg (e.g. lead on final, follower on downwind).
- `FOLLOW` on upwind and crosswind now adjusts the follower's speed for spacing and drops the target when the lead despawns or lands — same behavior as downwind.

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
