# Changelog

## 0.1.1-alpha

### Added
- ERAM WIP: `QN` / `QF` / `QL` / `QT` / `QZ` / `QQ` / `QS` / `RD` / `QU` / `QP` verbs, sector configuration, dwell lock, and pointouts. Per-track annotations (interim/procedure/controller altitudes, VCI, leader direction/length) serialize into snapshots.
- STARS visibility hysteresis so tracks no longer flicker on the edge of coverage, with preparation for future RW ground vehicles being excluded from the display.
- Real NEXRAD weather overlay via WMS (5-minute background refresh) when using "Load Live Weather".
- CRC session/info protocol: client name, version, controller info, and transmit/receive frequency lists are captured from `StartSession` and available to peer info requests.
- Airport elevation lookup falls back between ICAO and FAA identifiers (e.g. `KSFO` ↔ `SFO`).
- Build identification and in-app docs: the title bar, the new Help → About YAAT dialog, and the first line of `yaat-client.log` all show the version and whether the build is a Velopack-installed release or a dev build. The Help menu also links Getting Started, User Guide, Commands Reference, and Changelog on GitHub.
- Airport-authored runway data drives pattern altitude, pattern size, default exit side, and forbidden exits. OAK 28L flies its published 600 ft AGL pattern instead of the per-category default; SFO 28R picks south-side exits and avoids L/P (which cross toward 1L/19R).

### Changed
- RTIS soft-fail now surfaces the specific visual-acquisition reason (distance, cloud layer, hemisphere, bank) to the RPO while keeping the pilot readback diagnostic-free. Traffic-in-sight readbacks are promoted from gray notifications to orange warnings.
- README, INSTALL, GETTING_STARTED, and CONTRIBUTING lead with the prebuilt installer path instead of a from-source build.
- Wire format and recording bundles now use a single `Position {Lat, Lon}` field in place of separate `Latitude` / `Longitude` doubles. Existing v4 bundles can be migrated with `tools/upgrade_bundles_latlon.py`.
- `EXT` now extends the current pattern leg — upwind, crosswind, or downwind. Base is rejected (use `MNA` to widen instead).

### Fixed
- Taxi pathfinder fixes focused on eliminating spurrious U-turns
- Pattern aircraft going around now turn crosswind 300 ft below pattern altitude (per AIM 4-3-3), matching the threshold for normal VFR departures, instead of holding runway heading until they reach pattern altitude.
- Follow speed clamp no longer compounds each tick, so followers stay locked to the leader's speed instead of drifting slower.
- Follow state is shown in the Info column during pattern legs (previously went blank once the leader entered a pattern).

## 0.1.0-alpha

- Initial release
