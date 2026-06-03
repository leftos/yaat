# Issue #176 — Flight-plan amendment "reverts" in the client editor

> **Status:** initial investigation (seed plan; not yet implemented).
> **Labels:** bug, crc-general. **Source:** Discord thread, filed 2026-06-03.
> **Bundle:** `S1-SFO-4 | FD/CD/GC 19/10` (same recording as #172). Aircraft: **SIA31**.
> **Symptom pinned by user:** the YAAT flight-plan **editor** rejected / reverted the amendment.

## Symptom

The controller could not amend SIA31's flight plan. In the recording the amend was issued **three
times in the same second** (t≈1158), correcting the departure SID: `GAPP` → `GUNNR3` → `GAPP7` (the
filed route had a malformed `GNNRR3`; the PDC cleared `GAPP7`). Each attempt was recorded and the
client logged `amended flight plan for SIA31` — i.e. the server applied every attempt — yet the user
kept retrying, which reads as the editor not "taking" the change.

## Root cause

**Server wire path ruled out:** `RoomEngine.AmendFlightPlan` stores the route, bumps the revision,
records the action, and refreshes the strip (`src/Yaat.Server/Simulation/RoomEngine.cs:1103`). The
`AircraftChangeTracker.CaptureFlightPlan` fingerprint includes `Route`
(`AircraftChangeTracker.cs:257–261,428–445`), so a route change re-broadcasts the CRC `FlightPlans`
topic (`CrcBroadcastService` `Receiveln`). The amendment does reach the aircraft and the CRC FDB.

**Leading hypothesis (client editor, needs-repro):**
`FlightPlanEditorManager.Open` reloads the editor via `_openEditor.LoadAircraft(ac)` when the editor
is already open (`src/Yaat.Client/Views/FlightPlanEditorManager.cs:19`), and `LoadAircraft`
overwrites every field and resets `_origRte` (`FlightPlanEditorWindow.axaml.cs:67,79`). A
snapshot-driven refresh or a re-open mid-edit **clobbers the user's typed SID**, which presents as
"reverted." Also inspect:

- `OnFieldChanged` dirty-gating (`FlightPlanEditorWindow.axaml.cs:127`).
- The `RteBox` input mask `RoutePattern` (`[^A-Z0-9./+ ]+`, line ~190) — `GAPP7` is clean, but
  verify nothing strips the SID.
- Whether `AircraftModel.Route` shown after amend is the **raw filed** route or an **expanded** one
  (a long expanded route would look like the SID "didn't take").

## Key files

- `src/Yaat.Client/Views/FlightPlanEditorManager.cs:15` — `Open` (re-`LoadAircraft` when open).
- `src/Yaat.Client/Views/FlightPlanEditorWindow.axaml.cs:35,67,127,144` — ctor, `LoadAircraft`,
  `OnFieldChanged`, `OnAmendClick`.
- `src/Yaat.Client/Views/FlightPlanEditorAmendmentBuilder.cs` — builds the `FlightPlanAmendment`.
- `src/Yaat.Server/Simulation/RoomEngine.cs:1103` — `AmendFlightPlan` (server apply, for the
  round-trip test).
- `src/Yaat.Server/Hubs/CrcClientState.FlightPlan.cs:209` — CRC amend path with the silent
  `NOT YOUR TRACK` ownership guard (rule out as a secondary cause if amend ever comes via CRC).

## Approach

Reproduce first. Drive an amend through the client editor path
(`FlightPlanEditorAmendmentBuilder.Build` → `Connection.AmendFlightPlanAsync` →
`RoomEngine.AmendFlightPlan`) and assert the editor, strip, and datablock reflect the new SID and do
**not** revert. Fix the refresh-clobbers-edit behavior — e.g. do not `LoadAircraft` over a dirty
editor, and re-seed fields from server-confirmed values only after a successful amend.

## Verification

- Failing-then-passing repro test for the editor round-trip.
- Manual: amend a departure SID at SFO with the editor staying open; the new SID sticks across the
  next snapshot update.

## Open questions

- Confirm whether the route the editor displays post-amend is **raw vs expanded** — that decides
  whether this is purely an editor-refresh bug or also a display-form bug.
- Confirm the amend in this recording originated from the YAAT client editor (training hub) vs CRC;
  if CRC was involved, evaluate the `NOT YOUR TRACK` guard as a contributing factor.
