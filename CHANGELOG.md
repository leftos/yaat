# Changelog

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
