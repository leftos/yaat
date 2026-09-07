# In-pattern runway switches (MLT/MRT to a parallel), OTG, and `COPT MLT 28L`

## Context

Bundle: `X:\Downloads\S2-OAK-4 _ VFR Transitions_Radar Concepts.yaat-bug-report-bundle.zip` (ZOA, client 0.12.25-beta).
N342T (DA42) went around off 28R at t≈1100, climbed the 28R upwind (right traffic), and at t=1125 (while paused)
received `MLT 28L`. Live it immediately turned left across 28L and joined the 28L left downwind (the
"midfield downwind runway 28L" callout fired at t≈1155, 30 s after the command).

**Root cause (live).** `PatternCommandHandler.TryChangePatternDirection` (`src/Yaat.Sim/Commands/PatternCommandHandler.cs:1017-1085`)
classifies the aircraft with `IsOnWrongSideForPattern` against the *new* runway only. The 28R centreline sits
0.165 nm right of 28L's (CIFP thresholds), beyond `WrongSidePatternDeadbandNm = 0.1`, so the wrong-side branch
inserts `MidfieldCrossingPhase` (target = midpoint of 28L's left downwind, `MidfieldCrossingPhase.cs:47-52`) and a
downwind entry. That manoeuvre is right for a free-flying aircraft entering from the wrong side, and wrong for an
aircraft already flying a pattern leg of the neighbouring runway. Issue #7's fix
(`Issue7MltCrossRunwayWrongSideTests`, same scenario, pinned at −0.165 nm) introduced exactly this behaviour.

**Owner decisions (this session).**
- Free-flight wrong-side entries (`TryEnterPattern`) keep the midfield crossing. An aircraft *already on a pattern
  leg* told to switch runways transitions leg-to-leg instead.
- Upwind: continue the current upwind; turn crosswind beyond **both** departure ends (the farther DER along the
  upwind axis), then the new runway's crosswind → downwind.
- Crosswind, opposite side: keep the existing midfield crossing. Same side: existing same-side rebuild.
- Downwind, opposite side (parallels): **crossover at midfield** — continue the current downwind to abeam midfield,
  turn toward the field, cross perpendicular at TPA (no TPA+500 / teardrop), roll onto the new downwind with
  `RejoinTrack`. Already past midfield → cross now. Already past the abeam point → handle as Base. Non-parallel
  runway pairs keep the existing crossing. Same side: existing rebuild, plus `RejoinTrack` (the parallel downwind
  is laterally offset).
- Base: same side → existing rebuild ("extend base" onto the parallel's final); opposite side → existing crossing.
- The cross-runway takeoff clearance (`CTO 28R MLT 28L`) uses the same parallel model; crossing runways (33 → 28R)
  keep `Upwind(A) → MidfieldCrossing(B)`.
- New `OTG` ("on the go") **condition prefix** like `ONHS`: `OTG MLT 28L` fires once the aircraft is climbing out
  after its next cycle terminator (touch-and-go / stop-and-go / low approach) or go-around on the current runway.
- The option clearances accept the pattern modifiers with a runway: `COPT MLT 28L`, `TG MLT 28L`, `SG …`, `LA …`
  (plus the optional altitude, like `CTO MLT 28R 15`).
- `Issue7MltCrossRunwayWrongSideTests` is rewritten to the new model (its recording stays).
- Replay-fidelity drift found in the bundle is tracked under the tick-path plan (see the last section).

## Design

### A. Shared runway-transition circuit (`PatternBuilder`)

New `PatternBuilder.BuildRunwayTransitionCircuit(flownRunway, patternRunway, entryLeg, category, type, wind,
direction, touchAndGo, sizeOv, altOv, airportRunways, flownAuthored, patternAuthored)` replaces
`BuildCrossRunwayDepartureCircuit` (`PatternBuilder.cs:209-261`); `DepartureClearanceHandler.ApplyClosedTraffic`
(`DepartureClearanceHandler.cs:771-786`) and the new MLT/MRT / auto-cycle paths all call it.

- **Parallel pair** (move `IsParallelSidestepCandidate` + its two constants out of `PatternCommandHandler.cs:2928-2936, 3194-3214`
  into a shared `RunwayGeometry.AreCloseParallels(a, b)` in `src/Yaat.Sim/Phases/`): chain =
  `UpwindPhase(transition waypoints)` → `CrosswindPhase(transition waypoints)` → `DownwindPhase(B, RejoinTrack)` → `BasePhase(B)`
  → `FinalApproachPhase` → terminator. No `MidfieldCrossingPhase`.
- **Crossing pair**: today's chain — `UpwindPhase(A)` → `MidfieldCrossingPhase(B, BiasTurnToPatternSide)` → `DownwindPhase(B, RejoinTrack)` → ….

"Transition waypoints": `PatternGeometry.Compute` (`PatternGeometry.cs:248-345`) gets its body extracted into a core
that takes the departure-end point explicitly; `Compute` passes `runway.End*`, and a new
`PatternGeometry.ComputeTransition(flownRunway, patternRunway, …)` passes whichever of the two DERs projects
farther along the pattern runway's heading (`GeoMath.AlongTrackDistanceNm`). Everything downstream
(`CrosswindTurn`, `DownwindStart`) then follows from that point, so the upwind never turns over either runway
and the crosswind runs to a downwind start abeam the turn. No optional parameters.

### B. `TryChangePatternDirection` on an active leg (`PatternCommandHandler.cs:901-1085`)

- Capture `previousRunway = aircraft.Phases?.PatternRunway ?? aircraft.Phases?.AssignedRunway` **before** line 926 overwrites it;
  `runwaySwitch = previousRunway is not null && designator differs`.
- Per active leg (`GetCurrentPatternLeg`):
  - **Upwind + runwaySwitch** → `BuildRunwayTransitionCircuit(previousRunway, runway, Upwind, …)` via `ApplyRebuiltPatternChain`.
    Same runway keeps today's deadband logic (N500M guard).
  - **Crosswind** → unchanged (same-side rebuild / wrong-side crossing).
  - **Downwind + runwaySwitch + same side** → same-side rebuild with `RejoinTrack = true` on the new downwind.
  - **Downwind + runwaySwitch + opposite side + parallels** → `DownwindPhase(A waypoints, ExitAtMidfield = true)` →
    `MidfieldCrossingPhase(B waypoints, CrossAtPatternAltitude = true, PreferredTurn = previous direction, i.e. toward the field)`
    → `DownwindPhase(B, RejoinTrack)` → Base → Final → terminator. Skip the first downwind when the aircraft's along-track is
    already ≥ midfield; fall through to the Base handling when it is ≥ the abeam point. Non-parallel pairs keep today's crossing.
  - **Base** → unchanged.
- New phase flags, both snapshotted as nullable DTO fields (`RejoinTrack` precedent, `PhaseSnapshotDto.cs:951`):
  `DownwindPhase.ExitAtMidfield` (completes when `aircraftAlongTrack >= _abeamAlongTrack / 2 − AlongTrackToleranceNm`, the
  same midfield the broadcast uses at `DownwindPhase.cs:222`) and `MidfieldCrossingPhase.CrossAtPatternAltitude` (skips the
  +500 ft in `MidfieldCrossingPhase.cs:66-68`; `PreferredTurnDirection` set from a new `InitialTurn` init property instead of the
  bias-to-pattern-side bool — keep `BiasTurnToPatternSide` semantics by mapping it onto the same property).
- Docstring/comment at `PatternCommandHandler.cs:3153-3166` (deadband rationale) rewritten: the deadband now only governs a
  same-runway direction change.

### C. Auto-cycle honours a pending pattern runway (`PhaseRunner.cs:123-165`)

`runway = PatternRunway ?? AssignedRunway` already exists. When `PatternRunway` differs from `AssignedRunway` (the runway the
terminator or go-around was flown on), build `BuildRunwayTransitionCircuit(AssignedRunway, PatternRunway, Upwind, …)` instead of
`BuildNextCircuit`, then set `AssignedRunway = PatternRunway`. The CTO cross-runway path already sets both to the pattern
runway at clearance time (`DepartureClearanceHandler.cs:803-804`), so it is unaffected. Verify the other `PatternRunway`
readers (`PhaseClearSummary.cs:66`, `VfrFollowPhase.cs:295,500`) still make sense when it is armed ahead of a switch.

### D. `OTG` condition prefix

Mirror `ONHS` end to end:
- `CanonicalCommandType.OnTheGo`; `CommandRegistry.All` `Bare(OnTheGo, "On the Go", …, ["OTG"])` next to `:1476`; `CommandScheme.Default()`
  entry (completeness tests enforce both).
- `OnTheGoCondition : BlockCondition` (`ParsedCommand.cs:616`); `CommandParser.ParseBlock` (`:245-262`, including the
  "condition followed by AT/LV/ATFN" split); `CommandDescriber` round-trip ("OTG"); the client-side mirror in
  `CommandSchemeParser.ParseBlockToCanonical` (#335 trap: both parsers must agree).
- `CommandDispatcher` (`:2833`, `:3233`) → `BlockTrigger { Type = BlockTriggerType.AfterCycleTerminator }`.
- `FlightPhysics.cs:1787` evaluation with a latch like `AfterRunwayCrossing` (`CommandQueue.cs:181`, `FlightPhysics.cs:1816`):
  met once a `TouchAndGoPhase` / `StopAndGoPhase` / `LowApproachPhase` / `GoAroundPhase` has completed since the block was queued
  and the current phase is airborne (`UpwindPhase`). Latch field snapshotted on the block DTO.
- When it fires with `MLT 28L` the aircraft is on the fresh upwind for the old runway → section B's Upwind path.

### E. `COPT/TG/SG/LA MLT <rwy> [alt]`

- Records: `TouchAndGoCommand(RunwayId, TrafficPattern)` etc. (`ParsedCommand.cs:416-422`) gain `PatternRunwayId` and
  `PatternAltitude` (required positional fields, all call sites updated — no defaults).
- Parser: extract the `MLT|MRT [rwy] [alt]` token parse shared by `ParseMakeTraffic` (`CommandParser.cs:1648-1685`) and the CTO
  form (`DepartureCommandParser.cs:310-330`) into one helper; `ParseTouchAndGo` / `ParseOptionWithDirection` (`:1607-1640`)
  accept `[landingRwy] [MLT|MRT [patternRwy] [alt]]`. Describer round-trips every field.
- Handlers `TrySetupTouchAndGo/StopAndGo/LowApproach/ClearedForOption` (`PatternCommandHandler.cs:2414-2530`) and
  `TryArmPendingLandingClearance`: resolve the pattern runway at the aircraft's airport (`NavigationDatabase.Instance.GetRunway`),
  stamp `Phases.PatternRunway`, `Pattern.TrafficDirection`, `Pattern.AltitudeOverrideFt`; readback
  "Cleared for the option, Runway 28R, make left traffic Runway 28L" (phraseology per `docs/pilot-phraseology.md`).
  Section C then builds the transition after the option.

## Files

- `src/Yaat.Sim/Phases/PatternBuilder.cs`, `PatternGeometry.cs`, new `RunwayGeometry.cs`
- `src/Yaat.Sim/Phases/Pattern/DownwindPhase.cs`, `MidfieldCrossingPhase.cs`, `PhaseRunner.cs`
- `src/Yaat.Sim/Commands/PatternCommandHandler.cs`, `DepartureClearanceHandler.cs`, `CommandParser.cs`, `DepartureCommandParser.cs`,
  `ParsedCommand.cs`, `CommandDescriber.cs`, `CommandDispatcher.cs`, `CommandRegistry.cs`, `CommandScheme.cs`,
  `CanonicalCommandType.cs`, `CommandSchemeParser.cs`
- `src/Yaat.Sim/CommandQueue.cs`, `FlightPhysics.cs`, `Simulation/Snapshots/PhaseSnapshotDto.cs` (+ block DTO)
- Tests under `tests/Yaat.Sim.Tests/` (below); docs listed in Verification.

## Tests (TDD — each written red first)

Install the bundle: `python tools/bug_bundle.py install "<bundle>" --desc parallel-mlt-from-upwind` →
`TestData/parallel-mlt-from-upwind-recording.yaat-bug-report-bundle.zip` (then `validate`).
The reconstruction has the MLT landing in `GoAroundPhase` (see the drift note), so every test restores a snapshot and drives
the engine with `TickOneSecond` + direct `PatternCommandHandler.TryChangePatternDirection(...)` / `CommandDispatcher.Dispatch`
calls, never the recorded MLT. Pattern from `BugN500mMltUpwindTurnsLeftImmediateTests` (restore → sanity → act → assert).

1. `ParallelRunwayMltFromUpwindTests` — restore t=1120, tick until `UpwindPhase` for 28R/Right; `MLT 28L`; assert no
   `MidfieldCrossingPhase`, heading holds runway heading until along-track ≥ the farther DER (28L's at OAK), then a left
   crosswind and a `DownwindPhase` for 28L south of 28L (cross-track sign) heading east. Mirror: restore t=1140 (Upwind 28L/Left
   in the reconstruction), `MRT 28R`; the turn fires beyond 28L's DER, not 28R's.
2. Rewrite `Issue7MltCrossRunwayWrongSideTests` to the same expectations (its (b)/(c) "not turning right" asserts stay).
3. `ParallelRunwayMltFromDownwindTests` — restore t≈980 (28R right downwind, before midfield): `MLT 28L` → downwind continues to
   midfield, `MidfieldCrossingPhase` at TPA with a right (toward-field) turn, then 28L downwind with `RejoinTrack`; variant at
   t≈1030 (past midfield → crosses now); same-side `MRT 28L` → rebuild + `RejoinTrack`, re-intercepts 28L's downwind track.
4. CTO parallel: a `TakeoffDepartureTests`/`PatternEntryTests`-style case `CTO 28R MLT 28L` at OAK → chain
   Upwind → Crosswind → Downwind(RejoinTrack), no `MidfieldCrossingPhase`, upwind target = farther DER. Existing
   `cross-rwy-cto-mrt` (33 → 28R) test stays green.
5. OTG: parser/describer round-trip (both parsers), `CommandRegistry`/`CommandScheme` completeness; restore t=880 (final 28R,
   COPT recorded at 883), queue `OTG MLT 28L`, tick through the touch-and-go (t≈910-935): the block does not fire before the
   terminator, fires on the fresh upwind, and the chain is the transition. Go-around variant: restore t=1095, `OTG MLT 28L`,
   fires after the go-around climb-out.
6. `COPT MLT 28L` family: parser/describer round-trip for all four verbs (with/without landing runway, altitude); handler
   stamps `PatternRunway`/direction/altitude and the readback; restore t=880, dispatch `COPT MLT 28L`, tick past the option →
   auto-cycle built the transition and `AssignedRunway` became 28L.
7. Regression sentinels from memory: `BugN500mMltUpwindTurnsLeftImmediateTests`, `PatternFlyabilityFloorTests`,
   `Issue412WrongRunwayPatternTests`, `ExtendedDownwindNoClimbTests`, `Bug157leCtoMltStuckTests`, `GoAroundClimbOutTests`,
   `AirborneMrtCompletedChainTests`, `CtoParserTests`, the #335 canonicalizer tests.

Run targeted tests with `timeout 30 dotnet test -- --filter-class "*.<Class>"`; then `pwsh tools/test-all.ps1` (both repos).

## Verification & landing

- Build `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log`; `pwsh tools/test-all.ps1 2>&1 | tee .tmp/test-all.log`.
- **Aviation review (mandatory)**: `aviation-sim-expert` with the local-references preamble on: crosswind turn beyond both
  DERs (AIM 4-3-2), the midfield crossover at TPA for an in-pattern switch (AIM 4-3-3 / AC 90-66B), keeping the free-flight
  wrong-side entry unchanged, the `COPT … MLT 28L` readback wording. Then `csharp-reviewer`, then `architecture-updater`.
- Docs: `docs/approach-and-pattern-geometry.md` (replace the "Wrong-side has a deadband" paragraph at `:407-414` and the
  cross-runway departure section at `:187-200` with a "Runway transitions" section), `docs/command-chaining.md` (OTG trigger),
  `COMMANDS.md` (OTG row, `TG/SG/LA/COPT` forms, MLT/MRT switch behaviour), `docs/command-cheatsheet.json` +
  `node tools/build-cheatsheet.mjs`, `USER_GUIDE.md`, `docs/architecture.md`, `CHANGELOG.md`.
- Execution: orchestrate via `implementer` briefs (A+B+tests 1-3 as one brief; C+E+test 6 as one; D+test 5 as one; docs inline),
  gate each with `git status --short` + diff review; commit after each green step (standing authority from plan approval).

## Replay-fidelity drift (moved to the tick-path plan)

The bundle's reconstruction reaches the go-around 5–10 s later than the live run did, so the recorded `MLT 28L` hit
`GoAroundPhase` in the reconstruction and `UpwindPhase` live. It is a `live-vs-reconstruct` divergence and is tracked as a
reconstruct-vs-live-log pin under [tick-path/04-relocation.md](../tick-path/04-relocation.md), not as a standalone issue.
