# YAAT Scope — a STARS × ERAM instructor radar

Design canvas (visual mocks, datablock anatomy, feature matrix, technical design): https://claude.ai/artifact/Kr562HhSk9FKQbAnZVeWDb

Grounded in the CRC manuals `docs/crc/stars.md` and `docs/crc/eram.md`. The terms used here (YAAT Scope, profile, zoom band, dwell, pilot lens, scope entry) are defined in [`CONTEXT.md`](../../../CONTEXT.md) § Radar display.

## Goal

An instructor radar that takes the strongest parts of STARS and ERAM without copying either one. The student's CRC display stays exact. This scope is for the instructor, so it can be easier to read.

- **Datablocks** have fixed fields and never time-share. Line 2 uses ERAM's altitude notation (`300C`, `100T253`, `230↓253`, `230XXXX`, `200B250`…) followed by the STARS right side: ground speed in tens, CWT category, `^` for RNAV, and the ATPA in-trail distance.
  - Line 3 is a single status slot, filled in priority order: SPC › handoff › beacon mismatch › CST/FRZN › scratchpads and type.
  - Line 4 holds ERAM's heading/speed/free-text notes, shown only when set.
  - Column 0 holds the VCI (on-frequency mark) and `R` (not your control).
  - There are four levels: Limited, Partial, Full, and Dwell (hover expands the block).
  - Held keys stand in for time-sharing: F1 shows beacon codes, F10 shows type and destination, Alt shows scratchpads.
- **Colors** follow STARS ownership (white yours, green others', amber inbound handoff, yellow point out, cyan highlight, red CA). Brightness is set per group, as in ERAM.
- **Semantic zoom**: zoomed in it behaves like STARS (other controllers' blocks shown full, ATPA cones, rings). At medium range, others' blocks drop to partial and vector lines run 1 minute. Zoomed out it behaves like ERAM (vector lines 4 minutes, route lines, DRI halos). Band edges live in the profile. A manual change pins a value until AUTO is pressed.
- **Input**: four ways to enter a command, all feeding one command pipeline.
  - Type, then click the track (STARS).
  - Select the track, then type (YAAT today).
  - Middle-click executes (ERAM).
  - Click a datablock field to open a menu. Field menus split scope data from pilot commands.
- **Toolbar** combines ERAM menus, STARS spinners, tear-offs (any button can be pulled onto the scope) and named pref sets. The views dock holds the status area (SSA) and the lists.
- **Tools**: measure (range, bearing, time to fix), minimum separation, speed-to-time, rings (J-ring, cone or halo), CRR groups, and ERAM-style route display.

## Decisions

- **Scope entries are shared.** Scope data the instructor enters writes the same `AircraftStarsState` / `AircraftEramState` that CRC reads, so the student sees it.
- **ERAM vs STARS altitudes.** When ERAM and STARS viewers are sent different flight-plan altitudes, the scope shows the profile's value and marks it when the other side differs. The dwell view lists both.
- **Profile at room open.** The student position picks STARS or ERAM; no student, or both kinds, opens Mixed. A manual pick per room type overrides this.
- **Ground.** The Ground view stays as it is. Top-down mode (TDM) is a Ctrl+T toggle on the scope, never a zoom band.
- **Conflict alerts.** The scope mirrors the student's system rule: STARS 3 NM with a 5 s look-ahead, or ERAM's 4-minute probe that honours the datablock altitude.
- **Command line.** It accepts scope entries in the profile's CRC dialect alongside YAAT pilot commands. The preview names which kind it read before you send.
- **Field-menu pilot picks.** A plain click sends; Shift+click puts the command in the command line for editing.
- **Pilot lens.** All pilot-lens overlays are off by default: the queued-commands badge, the intent path, check-in truth and the RPO queue view.
- **Rollout.** YAAT Scope is built as a new view beside today's Radar (reached from the View menu). The old Radar view is deleted once the new one reaches parity; no long-lived dual renderer.
- **EuroScope tag mode** survives as a template on the new engine. Its flyouts become the field menus.
- **Layout cost.** Each block's layout is cached and re-laid out only when a field changes.

## Architecture

- **`TrackPresenter`** (new, UI thread, pure): takes (AircraftModel, profile, zoom band, lens, selection, reveal key) and returns a `TrackPresentation` with the block level, a color token, and fields. Each field carries its slot, text, token and action. It has no Skia or Avalonia dependency, so it is unit-testable.
- **`DatablockTemplate` engine** (new): lays the fields out into lines and returns one `FieldRect` list that both drawing and hit-testing use. It carries the STARS, ERAM, Mixed and EuroScope templates, which ends the draw-vs-hit-test duplication described in `docs/radar-rendering.md`.
- **Render thread**: the render snapshot copies the rects; the render thread reads no StyledProperty. Layers draw back to front, each with its own brightness group: maps A/B, weather, rings, routes/tools, history, vectors, targets, blocks, alerts, pilot lens.
- **Actions**: a field action produces command text sent through `SendCommandAsync`, so a menu can do nothing the command line can't. Toolbar and view actions are dot commands (`.scope …`, `.view …`) and echo in the feedback line.
- **Profiles**: STARS, ERAM and Mixed ship as JSON files under `YaatPaths`; a pref set is the same shape holding one user's values.

## Build order

- [ ] 1. The new YAAT Scope view on the template engine, with the STARS, ERAM, Mixed and EuroScope templates, opened from the View menu beside today's Radar
- [ ] 2. ERAM altitude line, line-3 status slot, line-4 HSF, VCI, dwell
- [ ] 3. Toolbar with menus, tear-offs and pref sets; views dock with the SSA and lists
- [ ] 4. Profiles and zoom bands (profile picked from the student position); one Vector control; one Rings tool; TDM toggle
- [ ] 5. Field menus split scope data from pilot commands (click sends, Shift+click stages); middle-click executes; measure, CRR, speed-to-time; CRC-dialect entries in the command line
- [ ] 6. Pilot lens (off by default): intent path, queued-commands badge, check-in truth, RPO queue view
- [ ] 7. Parity check against today's Radar, then delete the old view, `RadarDatablockLayout`, and the pieces the engine replaced

## Tests that matter

- Altitude notation: every row of ERAM Table 3 (`docs/crc/eram.md` § FDB Line 2) as a `TrackPresenter` test case.
- Line-3 priority order; the STARS FDB criteria (`docs/crc/stars.md` § Full Data Blocks), one test each.
- Geometry: for every drawn field, the hit-test returns the same rect.
- Zoom bands: a manual change stays pinned until AUTO.
- The ERAM/STARS altitude mismatch marker.

## Reviews owed

- `aviation-sim-expert` on the CA mirroring, the ATPA display, the pilot-lens intent path, and the scope-entry vs pilot-command split.
- `csharp-reviewer` on each step.
