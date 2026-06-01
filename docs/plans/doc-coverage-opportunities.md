# Doc Coverage Opportunities

> **What this is:** a prioritized backlog of subsystem reference docs worth adding under `docs/`,
> so future agents (and humans) stop re-parsing the same big, complex parts of the codebase on
> every task. Produced by a 22-agent gap-analysis workflow over **both repos** (yaat + yaat-server).
>
> **Status:** **All Tier-1 docs (#1–#7) + the `logging.md` quick win (#15) drafted + adversarially verified**
> (✅ in the tables below). Tier 2 (#8–#14) and the rest of Tier 3 (#16–#18) remain candidates. Per-item
> drafting material (complete outline, every footgun, the measured cold-read cost) is in the verbatim
> appendix: [doc-coverage-opportunities.detail.md](doc-coverage-opportunities.detail.md).

## Methodology

1. **Inventory** — mapped the existing `docs/` corpus (41 docs; **26 are true "read-before-touching-X"
   subsystem references**, the rest are plans/roadmaps/validation) and mapped **48 source subsystems**
   across both repos with LOC / complexity / churn signals.
2. **Gap ID** — cross-referenced source against docs. An `architecture.md` file-tree mention does *not*
   count as coverage; only a dedicated subsystem doc does. Partial overlaps (e.g. `phases.md` touches
   approach phases but there's no approach-*geometry* doc) count as gaps.
3. **Deep-dive** — one agent per candidate actually read the code, judged whether a doc is worth it,
   and designed it (outline + key files + footguns + the cold-read cost it amortizes).
4. **Completeness critic** — caught cross-cutting concerns the tidy-directory pass missed.

**Result:** 18 opportunities, **all judged worth documenting** (0 rejected). The critic added 6 the
gap pass missed — all cross-cutting concerns with no tidy home directory (Aircraft Data Model, the
SignalR wire contract, Test Harness, Logging, Track Sharing, Airspace). That's the validation that the
biggest reparse costs live *between* directories, not inside them.

## Prioritized backlog

Tiers are my synthesis over the agents' (mostly uniform) priority scores, ranked by
**cold-read cost × churn × density of silent-failure traps × breadth of reuse**.

### Tier 1 — write first (highest leverage)

| # | Proposed doc | Scope (one line) | Effort | Churn | Cold-read cost |
|---|---|---|---|---|---|
| 1 | ✅ `command-handlers.md` | Inside the dispatcher: the two switch surfaces + per-domain handler effects | M | High | ~12K LOC / 9 files |
| 2 | ✅ `aircraft-data-model.md` | `AircraftState` + 13 satellites + `ControlTargets`; the 3-projection trap | M | High | ~1.4K LOC + 1,314 accessor refs / 17 files |
| 3 | ✅ `training-hub-contract.md` | The `/hubs/training` JSON wire contract (client↔server DTO matching, source-gen) | M | High | ~3K LOC / 9 files, **2 repos** |
| 4 | ✅ `server-rooms-and-hub.md` | Hosted tick loop, `RoomEngine`, room isolation, delta/fingerprint engine | L | High | ~8.4K LOC / 9 files, **2 repos** |
| 5 | ✅ `navigation-database.md` | NavData/CIFP singleton, route expansion, the RV-SID footgun, FRD | M | Med-High | ~2.9K LOC / 6 files |
| 6 | ✅ `flight-physics.md` | Per-tick kinematics, airspeed frames, the validated performance-constant table | L | High | ~3.2K LOC / 5 files |
| 7 | ✅ `test-harness.md` | Fixtures, the singleton-race protocol, the pathfinder oracle/budget infra | M | High | ~1.6K LOC / 10 files + 8 singletons |

### Tier 2 — high value (high churn / aviation-sensitive)

| # | Proposed doc | Scope (one line) | Effort | Churn | Cold-read cost |
|---|---|---|---|---|---|
| 8 | `approach-and-pattern-geometry.md` | Airborne approach/pattern/hold/intercept geometry (the most bug-reported area) | L | High | ~6K LOC / 18–22 files |
| 9 | `conflict-and-visual-detection.md` | Airborne CA / ground proximity / ATPA wake / visual acquisition + thresholds | M | High | ~2.4K LOC / 10–12 files |
| 10 | `scenario-loading-and-generation.md` | The two spawn pipelines + four runtime queues; spawn-state bugs start here | L | Med-High | ~3K LOC / 9 files |
| 11 | `solo-training-evaluation.md` | The scoring rule-to-code map (27 7110.65/AIM refs), same-runway tracker | L | Med | ~6.2K LOC / 10 files (3.5K in one class) |
| 12 | `track-sharing-and-consolidation.md` | STARS/ERAM shared datablock state, pointouts, the consolidation hierarchy | M | Med | ~1.5–1.8K LOC / ~19 files, **2 repos** |
| 13 | `client-mainviewmodel.md` | The client integration seam: threading, scenario bootstrap fan-out, lifecycle | M | High | ~8.3K LOC / 8–11 files |
| 14 | `radar-rendering.md` | SkiaSharp two-thread render pipeline, datablock geometry, context menus | L | High | ~5.5K LOC / 12–15 files |

### Tier 3 — useful, contained (good quick wins)

| # | Proposed doc | Scope (one line) | Effort | Churn | Cold-read cost |
|---|---|---|---|---|---|
| 15 | ✅ `logging.md` | `SimLog`/`AppLog`, the AsyncLocal-vs-static design, test-capture recipe | **S** | High-touch | ~290 LOC / 5 files |
| 16 | `command-input-ux.md` | Client-side parse-once → autocomplete + signature help (pre-send) | M | Med | ~2.9K LOC / 9 files |
| 17 | `weather-and-wind.md` | METAR/winds-aloft parsers, the 3 interpolation axes, declination | M | Med | ~1.8K LOC / 11–14 files, **2 repos** |
| 18 | `airspace-database.md` | Class B/C containment + boundary-crossing; both consumers | M | Low-Med | ~900 LOC / 9 files, **2 repos** |

**Quick win:** #15 `logging.md` is S-effort, touches every file, and turns three scattered CLAUDE.md
fragments into one reference — highest ROI-per-hour on the list.

## Natural clusters & write-order notes

Several of these are halves of the same seam and are best written together (shared "add-a-field"
checklists, shared cross-links) rather than independently:

- **Command trilogy:** existing `command-pipeline.md` (the flow) → **#1 `command-handlers.md`** (the
  effect) → **#16 `command-input-ux.md`** (the client pre-send UX). Write #1 next to the existing
  pipeline doc so the hand-off ("ApplyCommand is a thin routing switch → handlers") is closed.
- **Cross-repo wire seam — #3 + #4 overlap heavily and should be written as a pair.** Both cover
  `TrainingHub.cs`, `TrainingDtos`, the `AircraftChangeTracker` delta gate, the
  `AircraftStateDto`↔`AircraftDto` name-based pairing, and the *"field appears on join but never
  updates live"* trap. Split them by axis — #3 = wire shape (DTO matching + source-gen contexts,
  WASM failure mode), #4 = server internals (hosted loop, room isolation, `RoomEngine` 30-branch
  chain) — and keep **one** canonical add-a-field checklist shared between them, or they'll diverge.
- **Data + serialization:** **#2 `aircraft-data-model.md`** is the field-level companion to the
  existing `snapshots-and-replay.md` (which owns the DTO tree / migrator). #2 links to it for snapshot
  mechanics rather than redrawing the tree.
- **Physics + tick:** **#6 `flight-physics.md`** (the integration math) sits beneath existing
  `tick-loop.md` (the step order). Cross-link, don't restate ordering.
- **Arrival:** **#8 `approach-and-pattern-geometry.md`** (airborne half) is the upstream sibling of
  existing `landing-and-runway-exit.md` (ground half); `FinalApproachPhase` is the seam.
- **Training:** **#11 `solo-training-evaluation.md`** (scoring) is orthogonal to existing
  `solo-training-pilot-speech.md` (TTS output) — cross-link only.

## Incidental findings — resolved

The deep-dives turned up stale docs and latent code discrepancies. All were verified against the code
and addressed (nothing committed yet):

1. ✅ **`stars-consolidation.md` repo claim** — `ConsolidationEngine`/`ConsolidationState` actually live in
   `src/Yaat.Sim/Simulation/`. Got a correction note, then the file was removed in the completed/archived-plan
   cleanup; the live `architecture.md`/code references remain correct.
2. ✅ **`EramPointoutState` "runtime-only"** — the code deliberately round-trips it via `AircraftEramStateDto`
   (matching the project's serialize-display-state rule), so the *label* was the bug. Fixed the XML comment,
   `snapshots-and-replay.md`, and the `architecture.md` entry.
3. ✅ **`architecture.md` declination line** — corrected to NOAA WMM via the Geo library.
4. ✅ **`architecture.md` wind-interpolator line** — corrected to ISA compressible-flow equations. The archived
   `weather.md` got a "superseded" note, then was removed in the plan cleanup.
5. ⏳ **`AtpaProcessor.IsExcludedByTcp` always returns `false`** — confirmed working-as-coded (conservative).
   Tracked for a scoped fix in [open-issues/atpa-tcp-exclusion.md](open-issues/atpa-tcp-exclusion.md)
   (needs `TrackOwner` ULID plumbing + aviation review).
6. ✅ **yaat-server `CLAUDE.md` Hub API drift** — corrected `CreateRoom`/`JoinRoom` (+`kind`) and `SendCommand`
   (+`initials`); added a "source of truth is `TrainingHub.cs`" note so it stops drifting.
7. ⏳ **`CompletionReason.Dropped` is never assigned** *(surfaced by the aircraft-data-model verifier)* — the
   `AircraftCompletion.cs` enum documents it as set by "DEL command / scenario unload," but deletes call
   `RemoveAircraft` without stamping a reason, so the value is never produced. Either a stale comment or a
   missing-feature bug — not yet resolved.

## Per-opportunity summaries

Each links to its full block (outline + key files + all footguns + cold-read cost) in
[the detail appendix](doc-coverage-opportunities.detail.md).

**#1 `command-handlers.md` — Command Handlers: dispatcher arms and per-domain effects.**
Picks up exactly where `command-pipeline.md` stops ("ApplyCommand is a thin routing switch → handlers").
Net-new: the `ApplyCommand` vs `TryApplyTowerCommand` split (and why some verbs live in both), the
handler read/write contract (write `ControlTargets` or install a `PhaseList`, never move the aircraft),
and a per-domain effect cheat-sheet. Anchor: `src/Yaat.Sim/Commands/CommandDispatcher.cs`. Top traps:
two switch surfaces (a phase-interactive verb added to only one hits the `__NO_DISPATCHER_ARM__`
fallback on re-fire); dry-run runs the first block on a clone so handlers must be clone-safe; never call
`Queue.Clear()` (clearing is dimension-aware).

**#2 `aircraft-data-model.md` — AircraftState, satellites, ControlTargets, SimulationWorld.**
The field-level + mutator-map + three-projections reference no existing doc carries. Top trap: a field
added to `AircraftSnapshotDto` is **not** automatically on the live `AircraftDto` (`ServerConnection.cs`)
nor the CRC `DtoConverter` output — it can round-trip in a bug bundle yet never reach a running client.
`GetSnapshot()` returns a shallow copy of *live* instances. `Targets` is get-only (restored in place).

**#3 `training-hub-contract.md` — Training Hub wire contract (client↔server SignalR).**
The YAAT-native JSON twin of the CRC `crc-display-state.md`. Owns the hub-method catalog, the
`On<T>` broadcast catalog, and the rule that wire shape is **property-name based** (so `AircraftStateDto`
≠ `AircraftDto` in name/order is fine). Top traps: forget to register a type in a source-gen context →
desktop works, **WASM dies at runtime** with no compile error; a new field appears on join but never
updates until added to `TrainingDtoFingerprint`; session-settings duplicated across 4 DTOs.

**#4 `server-rooms-and-hub.md` — Server rooms, tick orchestration & the hub.**
The server seam `tick-loop.md` stops at. Top trap: physics advances `SimRate` sim-seconds per
wall-clock tick, but `DetectChanges` + `BroadcastUpdates` run **once per wall-clock tick** after the
all-rooms loop — not per sim-second. Callsigns are per-room (no global lookup). `IsBroadcastSuppressed`
gates replay-engine isolation. *(Overlaps #3 — write as a pair.)*

**#5 `navigation-database.md` — NavData/route expansion (fixes, procedures, RV-SIDs, FRD).**
Home for the recurring RV-SID footgun (4+ dedicated test files today, explained only by one CLAUDE.md
line). Top traps: `RouteExpander.Expand` defaults `includeAllTransitionsOnMismatch=true` (the **wrong**
value for nav — fabricates a turn-back through every synthetic `[OAK,X]` transition); mutable static
singleton races parallel tests; procedure-version drift (CNDEL5→CNDEL6); FAA↔ICAO K-prefix fallback
duplicated across ~6 lookups.

**#6 `flight-physics.md` — Kinematics, airspeed frames & performance constants.**
Promotes the airspeed-frame model out of an archived plan into a live reference; supersedes CLAUDE.md's
3-category constant summary with the full validated table (incl. the **4th Helicopter category**). Top
traps: IAS is authoritative, GroundSpeed has no setter (derived on read); `TargetSpeed`/`TargetAltitude`
self-null on arrival (by design); the ~7-layer speed precedence cascade; constants come from
`AircraftPerformance` (profile-then-category), not `CategoryPerformance` directly.

**#7 `test-harness.md` — Fixtures & the singleton-race protocol.**
Consolidates conventions that live only in CLAUDE.md prose + auto-memory. Top traps: the static-singleton
race (`Expected 98 / Actual 96.5` flake → fix is `EnsureInitialized()` in the *constructor*); the
`xunit.runner.json` must-be-Content-copied gotcha; `SimLog` swallows everything by default; the 30s
timeout discipline; the `OracleAutoRouter`/`TaxiBudget*` pathfinder-sweep infra has no doc home today.

**#8 `approach-and-pattern-geometry.md` — Airborne approach & pattern geometry.**
The airborne sibling of `landing-and-runway-exit.md` for the most bug-reported area (intercept
overshoots, downwind extensions, follow runaways, wrong-side entries). Top traps: pattern legs complete
on along-track/cross-track, **not** waypoint arrival; `SignedCrossTrackDistanceNm` positive = RIGHT
(flip it and the aircraft goes to the wrong side); `AirborneFollowHelper.GetAdjustedSpeed` must be fed
the phase baseline, never the previous tick's `TargetSpeed`; all pattern phases set `ManagesSpeed`.

**#9 `conflict-and-visual-detection.md` — CA / ground / ATPA / visual acquisition.**
Four *unrelated* mechanisms that all read as "conflict detection." Consolidates the tuning constants
(the part with zero prose) into one threshold table for aviation review. Top traps: parallel aircraft
are treated as **not** diverging (classic false-positive); approach-corridor suppression is purely
geometric (ignores phase/approach); `IsExcludedByTcp` always returns false; altitude units differ per
detector (hundreds-of-feet vs raw vs AGL).

**#10 `scenario-loading-and-generation.md` — Scenario loading & aircraft generation.**
Maps the immediate/delayed/deferred spawn split + four runtime queues across two disjoint pipelines.
Top material: `CreateBaseState` + approach inheritance, the `RouteExpander` mismatch footgun, the
airline-fleet→type coupling, the CFIX preset case, presets running through the **live dispatcher**, and
the rewind-reload twin path. Includes the add-a-spawn-type checklist.

**#11 `solo-training-evaluation.md` — Solo training evaluation & scoring.**
Aviation-review-sensitive; no map today from "7110.65 §X-Y-Z" to the private helper that implements it.
Top traps: **proof-based clearing** (findings clear because the controller issued RTIS/SAFAL/CWT, not
because spacing recovered); the stable-Id `observedThisTick` lifecycle; the FNV debrief cache invariant
(hash every field the row reads or ship a stale debrief); two callers live in yaat-server.

**#12 `track-sharing-and-consolidation.md` — STARS/ERAM sharing, pointouts, consolidation.**
The multiplayer scope-sharing layer above the TRACK/DROP ownership state machine; corrects the stale
`stars-consolidation.md` plan. Top traps: the engine moved into Yaat.Sim (plan says otherwise); three
inconsistent snapshot policies in one subsystem; `Owner == null` means "root/owns-self," not "unowned";
the two-pass manual-override re-attribution.

**#13 `client-mainviewmodel.md` — Client MainViewModel & app orchestration.**
The integration seam almost every client feature touches. Top traps: **every** `ServerConnection` event
handler must `Dispatcher.UIThread.Post` (omit it → intermittent cross-thread crashes tests won't catch);
three scenario-activation paths must all funnel through `ApplyScenarioBootstrap`; the
`_isApplyingSessionSettings` echo-suppression guard; fragile positional tab-index arithmetic.

**#14 `radar-rendering.md` — Radar display & rendering (SkiaSharp).**
The mechanics layer beneath `architecture.md`'s file list. Top traps: the UI-thread/render-thread
snapshot split (reading a `StyledProperty` in `RenderFromSnapshot` is a race); datablock geometry is
**computed twice** (draw vs hit-test) with no parity test; `DefaultPixelsPerDeg=5000` duplicated in 3
sites; promotes the smart-default-menu convention out of auto-memory.

**#15 `logging.md` — SimLog & AppLog.** *(quick win, S effort)*
Top traps: `SimLog` defaults to `NullLoggerFactory` (Sim logs silently swallowed in tests — use
`SimLogBuilder`); never call `SimLog.Initialize` from a test (poisons the process-wide static for
parallel tests — use `InitializeForTest`); `AppLog.CreateLogger` does **not** defer, so a static field
captured before `AppLog.Initialize` permanently gets `NullLogger`; `FileMode.Create` wipes the prior log
each launch.

**#16 `command-input-ux.md` — Autocomplete, signature help, parse-once.**
The client-side keystroke→dropdown journey *before* send (meets the server path at exactly one point:
`CallsignArgumentResolver.TryRewrite`). Top material: the parse-once contract (16-field result consumed
by both pipelines), the metadata-driven suggesters (most command changes need **zero** suggester code),
and the no-global-fix-picker enforcement point. Note: `ShownRouteBuilder.cs` is misfiled in `Services/`
and is radar-overlay, not input UX.

**#17 `weather-and-wind.md` — METAR/winds-aloft, interpolation, declination.**
Top material: the **three interpolation axes** (altitude / spatial-IDW / time) with different
conventions, and the direction-convention cheat-sheet (FROM vs TOWARD, true vs magnetic per source).
Corrects the two stale `architecture.md` lines (#3, #4 above). Links to `snapshots-and-replay.md` for
the declination-cache replay footgun.

**#18 `airspace-database.md` — Class B/C containment & boundary crossing.**
Only Class B/C are parsed (D silently dropped). Top material: the two consumers (VFR boundary-respect
hold **and** the SoloTrainingEvaluator separation-minima path — the latter mentioned in no current doc),
the synthetic 20 NM Class-C outer-area ring (not in the GeoJSON), the entry-gate switch duplicated in 3
places, and the 6 MB checked-in Brotli fixture.

## Next steps

**Done:** all of Tier 1 (#1–#7) plus the #15 `logging.md` quick win — drafted and adversarially verified;
the incidental findings are resolved (see above).

**Remaining, by leverage:** Tier 2 (#8–#14) and the rest of Tier 3 (#16–#18). Each is self-contained
drafting work using its appendix block.
