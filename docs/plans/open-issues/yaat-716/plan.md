# YAAT-716: auto-sort a vStrips rack

Linear: YAAT-716 (GitHub #1201). Mock: [separator-lanes.html](separator-lanes.html).

## Behaviour

- A checkable **Auto-sort this rack** item in the rack's right-click menu turns the rack's sort on or off. It is in both the empty-rack menu (`BuildEmptyRackMenu`) and the strip menu (`BuildStripContextMenu`), because a full rack has no empty space to right-click.
- While the sort is on, the rack reads top to bottom as follows:
  - **Groups:** tail registrations come first, then airline callsigns, each group A→Z. An airline callsign is exactly three letters followed by a digit (`^[A-Z]{3}[0-9]`, after trimming and upper-casing). Everything else is a tail: `N1234`, `N172SP`, `C-GABC`, `GABCD`. So `N1234` sorts above `AAL2210`.
  - **Keys:** a full strip sorts by its callsign (`FieldValues[0]`). A half strip sorts by its line 1 (`FieldValues[0]`, the `#1` box).
  - **Empty strips:** blank strips, and half strips whose line 1 is empty, sit at the bottom of their segment in their existing relative order.
  - **Order:** comparison is ordinal and case-insensitive. Ties (a scanned copy shares its callsign) break on the strip id, so the order is deterministic and replays byte-for-byte.
  - **Separators split the rack** (mock lane A). Separators stay where the controller put them, and each run of strips between two separators, or between a separator and a rack end, is sorted on its own.
- **Manual moves snap to the sorted spot.** A drag, push or `STRIP` move into or within a sorted rack is accepted, and the strip lands where the sort puts it. The client's optimistic drop uses the same sorted spot, so nothing flashes. A move that lands a strip in a different segment (across a separator) keeps that segment.
- **Banner:** a sorted rack shows a small pinned "auto-sort on" banner at its top and bottom. The banners stay fixed while the strips scroll, and they are not strips: they take no index and cannot be dragged.
- **Toggling on** sorts the rack at once. **Toggling off** leaves the strips where they are.
- **Who can toggle:** anyone who can edit strips in the room. The flag is shared bay state, like every strip edit, so every Strips tab and `/vstrips/` viewer in the room sees the same sorted rack and banner.
- **Persistence:** the flag is in snapshots (session persistence, recordings, rewind) and carries over a scenario restart the same way separators do. A full strip-state reset clears it.
- **CRC:** real CRC vStrips has no auto-sort (no field in `StripBay`, `FlightStripsConfiguration` or the wire contract). A CRC `MoveStripItem` into a sorted rack reaches the same primitives and snaps. No CRC wire DTO changes.

## Design

**Sim (owns order):**
- `FlightStripState.AutoSortedRacks`: a set of `(BayId, RackKey)`, guarded by `state.Gate`. `Reset` clears it. `ClearSession` keeps it with the rack skeleton.
- `StripRackSort` (new, `src/Yaat.Sim/Simulation/Strips/`): the comparer and segment sort over a rack's id list. It is pure, so the client reuses it for the optimistic drop. Index 0 is the visual bottom, so the stored list is the top-down sorted order reversed within each segment.
- `StripMutations.ResortRackIfAuto(state, bayId, rackKey)`: sorts the rack's segments when the flag is set, then runs `SyncPlacementRecords` and marks the bay changed. It is called at the end of every mutation that can break order:
  - `AppendStripToBay` and `MoveStripToBayRack` (both racks of a move);
  - `RequestDepartureStripForAircraftIntoBay`, whose raw `row.Add` at `StripMutations.cs:550` bypasses both primitives;
  - `UpdateStripFields`, because HSA and CRC edits change a half strip's line 1. It re-sorts the edited strip's rack.
- A new canonical strip command, `RACKSORT <bay>/<rack> ON|OFF`. Its bay and rack qualifier syntax matches the existing strip moves (`StripBayCanonicalQualifierTests`). It needs:
  - a `CanonicalCommandType`, plus entries in `CommandScheme.Default()` and `CommandRegistry.All`;
  - the parser, the describer and `IsStripCommand`;
  - the phase-transparent list, and the router's Strip arm, so it records and replays;
  - `VStripsCanonicalBuilder.BuildRackSort`.
- Snapshot: an `AutoSort` bool on `StripBayRackSnapshotDto`, written and read by `FlightStripSnapshotMapper`. Add a `SnapshotSchemaMigrator` step if the schema version requires one.
- Scenario restart carries the flag over beside the separators (`RecordingManager.RestartScenarioAsync`, yaat-server).

**Server (relays):**
- `StripBroadcaster` adds the sorted rack keys to the YAAT hub's `FlightStripsStateDto`, for example `BayItems[].AutoSortedRacks`. JSON hub only; the MessagePack `CrcDtos.Strips.cs` stays unchanged.

**Client (`Yaat.Client.Strips`, shared by desktop and `/vstrips/`):**
- `StripRackViewModel` gains an observable `IsAutoSorted`, set by `VStripsViewModel.ReconcileFullState`.
- `VStripsViewModel.OptimisticallyMove` places a strip that lands in a sorted rack by `StripRackSort`.
- Both menus get the checkable item, which sends `RACKSORT` through `_sendCommand`.
- The rack view draws the two banners outside the scrolling strip list.

## Steps

1. **Sim: sort rules.** Write `StripRackSort` and its tests first (`tests/Yaat.Sim.Tests/Simulation/Strips/StripRackSortTests.cs`). Cover:
   - tails before airlines, and `N1234` above `AAL2210`;
   - a half strip keyed by line 1;
   - blanks and empty half strips at the bottom of their segment;
   - separators splitting the rack into segments;
   - ties broken by id;
   - the reversed storage order.
2. **Sim: flag, command and hooks.** Add `AutoSortedRacks`, `RACKSORT` end to end, and `ResortRackIfAuto` at every call site above. Tests (Sim.Tests, bare engine) put a strip into a sorted rack through each path and assert the order:
   - `STRIP`, HSM, SEP, BLANK, HSC, SCAN;
   - spawn routing through `RequestDepartureStripForAircraftIntoBay`;
   - HSA re-keying a half strip;
   - toggle on (sorts) and toggle off (no move).
   Also: a recording with `RACKSORT` replays to the same order, and the command-completeness tests pass.
3. **Snapshot and restart.** Add the `AutoSort` snapshot field, the mapper change and any migrator step, with a round-trip test in `StripTdlsEngineSnapshotTests`. Add yaat-server's restart carry-over with a test beside the separator carry-over's.
4. **Server broadcast.** Add the `AutoSortedRacks` field to the YAAT hub strips DTO in both repos (`StripDtos.cs` on the client side), and a `StripBroadcaster` test. `CrcWireContractTests` must stay green, because nothing changes on the CRC DTOs.
5. **Client.**
   - `IsAutoSorted` and reconciliation: `VStripsViewModelTests`.
   - Optimistic sorted drop: `VStripsViewModelTests` and `VStripsDropIndexTests`.
   - The checkable menu item in both menus, with its checked state: `VStripsViewInteractionTests` through `BuildEmptyRackMenu` and `BuildStripContextMenu`.
   - The pinned banners, which stay visible when the rack scrolls: a UI render test, and a GuideCapture scene for the user guide.
6. **Docs.**
   - `COMMANDS.md` (both tables), `docs/command-cheatsheet.json` and its regenerated HTML.
   - The `USER_GUIDE.md` vStrips section, with the new screenshot.
   - `docs/flight-strips.md`: the flag, `ResortRackIfAuto` and its call sites, and `RACKSORT`. Fix the explorer's drift while in the file: `PrependStripToBay` → `AppendStripToBay` (`:74`, `:687`), one word for the rack's list ("row" vs "column"), and mark the yaat-server paths at `:11`.
   - A strips row in `docs/architecture.md`'s Task Index (the explorer's proposed row).
   - Glossary entries for rack, bay and auto-sorted rack.
   - Changelog bullet.
7. **Review.** `csharp-reviewer` over the diff. No aviation review: this changes strip-board ordering only, not aircraft behaviour.

Gate: `pwsh tools/test-all.ps1` (both repos) before landing. This is cross-repo, so the server DTO and the restart carry-over land with the Sim change.
