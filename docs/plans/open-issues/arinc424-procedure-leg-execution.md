# Fly charted procedure legs (ARINC-424 path terminators) on SIDs, STARs, and IAPs

## Context

**The problem.** A controller (Konso1e, DNVR) reported that ATCTrainer aircraft on the **LINDZ ONE departure out of KASE (Aspen)** "only know how to fly straight out — no way of making them the right turn or the left for the back course." We investigated whether YAAT has the same gap.

**What we found (verified against chart + code + real CIFP).** The LINDZ1 chart (RWY 33) reads: *"Climb heading 343° to 9100, then climbing left turn to 16000 on heading 273° to intercept I-PKN NW course (back course) outbound to LINDZ, then transition."* YAAT parses this **exactly right** — `CifpInspector --sid LINDZ1` shows the RW33 transition as three legs: `VA 343°→≥9100`, `VI 273°`, `CF 303°→LINDZ ≥16000`, then enroute transitions (DBL/RIL/EKR/JNC/RLG, all TF). But YAAT **does not fly it as charted**:

- `DepartureClearanceHandler.ResolveLegsToTargets` (`src/Yaat.Sim/Commands/DepartureClearanceHandler.cs:1024`) flattens procedure legs to a flat `List<NavigationTarget>` of positions and at `:1038` **silently drops any leg with no terminating fix** — so `VA 343°→9100` and `VI 273°` vanish. CF legs keep the fix but **discard the coded course** (303°), so they fly direct-to-fix.
- Net: the aircraft climbs ~runway heading and, at just **400 ft AGL**, turns **direct to LINDZ** — never climbing to 9100, never flying the 273° left turn, never tracking the back course. (It *is* better than ATCTrainer, which can't navigate to LINDZ at all — YAAT sequences LINDZ + the transition fixes. But the signature climbing-left-turn-to-back-course is missing.)

**Same gap in STARs and IAPs?**
- **STARs — same root gap.** STAR resolution calls the *exact same* flattener (`NavigationCommandHandler.TryResolveStarFromCifp` → `DepartureClearanceHandler.ResolveLegsToTargets`, `NavigationCommandHandler.cs:404`). VA/CA/VI/VM dropped, CF course discarded. The worst STAR-specific case is **FM legs** — the "fly published course, expect vectors" leg that *terminates most US STARs* (`CifpModels.cs:57` comment). The anchor fix is flown but the outbound course is dropped, so aircraft reach the last STAR fix and hold arrival heading instead of flying the charted outbound toward the vector area.
- **IAPs — mostly already solved; the reusable foundation.** Approaches use *separate, high-fidelity* machinery (`ApproachCommandHandler.BuildFixesFromLegs` → dedicated phases) that already flies procedure turns (`ProcedureTurnPhase`), hold-in-lieu/course reversals (`HoldingPatternPhase`), RF/AF arcs, final-approach-course intercept (`FinalApproachCourseExtractor` + `InterceptCoursePhase`), step-down altitudes, and missed approach. Only **minor** approach gaps remain: fix-less VA/CA/VI/VM legs dropped (`BuildFixesFromLegs:1457`, rare on approaches), and *intermediate* (pre-FAF) CF legs flown direct-to-fix instead of course-tracked.

**Outcome.** Build a shared ARINC-424 path-terminator execution capability so departing/arriving aircraft fly the charted heading/course legs (full fidelity, including back-course tracking), applied across all three procedure types per the user's scope decision. Primary deliverable: **LINDZ1 flies as charted.**

**Scope decision (user-confirmed):** all three procedure types; full fidelity (CF legs intercept and *track* the published course, not direct-to-fix); general engine (VA/VI/VM/CA across all SIDs, not a LINDZ special case).

---

## Design overview

Three building blocks, then per-procedure execution.

### A. Shared typed-leg model — `ProcedureLeg` (new `src/Yaat.Sim/ProcedureLeg.cs`)
A parallel typed sequence, **not** an overloaded `NavigationTarget` (whose `Position` is `required`/non-null and is dereferenced unconditionally across physics, rendering, and snapshot-diff — VA/VI/VM/CA have no position). Fields: `Type` (`HeadingToAltitude`/`CourseToAltitude`/`HeadingToIntercept`/`CourseToIntercept`/`HeadingToManual`/`CourseToFix`/`TrackToFix`/`DirectToFix`/`InitialFix`/`Arc`), optional `FixName`/`FixPosition`, `CourseMagnetic` (= `CifpLeg.OutboundCourse`), `TargetAltitudeFt`, `TerminatesOnNextLegIntercept`, `TurnDirection?` (= `CifpLeg.TurnDirection`, may be null), `AltitudeRestriction?`, `SpeedRestriction?`, `IsFlyOver`. Course→true per-tick via `new MagneticHeading(CourseMagnetic).ToTrue(ctx.Aircraft.Declination)`.

### B. Shared geometry primitives (extract from existing approach phases)
The capture/track math already exists inside approach phases; extract to shared static helpers so departures, arrivals, and approaches call one implementation:
- **Course intercept** (for VI/CI): `GeoMath.SignedCrossTrackDistanceNm(pos, anchorFix, courseTrue)` (`GeoMath.cs:143`) + turn-radius lead `groundSpeed / (turnRate × 62.832)`, exactly as `InterceptCoursePhase.OnTick` (`InterceptCoursePhase.cs:97,136-177`) with the sign-flip crossing fallback.
- **Course tracking onto a fix** (for CF): the aim-point projection block in `FinalApproachPhase.cs:597-627` (`lateralAnchor`/`leadNm`/`aimAlongTrack`/`ProjectPoint`/`BearingTo`) — fly the *course line through the fix* with cross-track correction, not a great-circle pursuit curve.
- **Wind-correction-angle** (for CA/CF track vs VA/VI heading): the WCA block in `FlightPhysics.UpdateNavigation` (`FlightPhysics.cs:225-232`). VA/VI fly a *heading* (no WCA); CA/CF fly a *track* (apply WCA).

### C. Shared resolver — `ResolveLegsToProcedureLegs(IReadOnlyList<CifpLeg>)`
New sibling to `ResolveLegsToTargets`, callable by SID, STAR, and approach builders. Emits typed `ProcedureLeg`s **without dropping fix-less legs**; preserves RF/AF arc expansion (`ExpandArcWaypoints`, unchanged math) as `Arc` legs; still skips `PI` in SID/STAR context. Also produces the flat fix skeleton (for radar "show route" overlay + `SnapshotDiff` `NavigationRoute` coverage) derived from the same list so the two can't drift. Recommend extracting to a shared `ProcedureLegResolver` since three callers need it.

---

## Work plan (staged, each independently shippable + testable)

### Phase 0 — Shared model + primitives + resolver
- Add `ProcedureLeg.cs` (block A) and its snapshot DTO `ProcedureLegDto` (+ `ToSnapshot`/`FromSnapshot`), reusing `AltitudeRestrictionDto`/`SpeedRestrictionDto`.
- Extract block-B helpers into shared statics (e.g. `ProcedureGeometry` or extend `GeoMath`/`WindInterpolator`) **without changing approach behavior** (the approach phases call the extracted helpers; assert no trajectory change via existing approach tests).
- Add `ResolveLegsToProcedureLegs` (block C).
- Critical files: new `src/Yaat.Sim/ProcedureLeg.cs`; `src/Yaat.Sim/Commands/DepartureClearanceHandler.cs`; `src/Yaat.Sim/Phases/Approach/InterceptCoursePhase.cs`, `Phases/Tower/FinalApproachPhase.cs` (extract, then call shared helper); `src/Yaat.Sim/Simulation/Snapshots/` (new DTO).

### Phase 1 — Departures: `DepartureProcedurePhase` (the LINDZ fix — primary deliverable)
- New phase `src/Yaat.Sim/Phases/Tower/DepartureProcedurePhase.cs` owning lateral nav from the moment `InitialClimbPhase`'s deferred-turn gate fires until the last typed leg completes, then installs the remaining fix-to-fix transition into `ControlTargets.NavigationRoute` and returns `true`. Per-leg tick logic:
  - **VA** (heading→alt): hold `course` true-equiv (no WCA); sequence when `Altitude ≥ TargetAltitudeFt`.
  - **CA** (course→alt): hold *track* (WCA-corrected); sequence on altitude.
  - **VI/CI** (heading→intercept): hold heading; sequence when within turn-radius lead of the *next* leg's course line (forward reference — read leg[i+1]'s course at fly-time, robust to amendments). Reuse block-B intercept.
  - **VM** (heading→manual, terminal RV-SID): hold heading until comms handoff (+5s) or controller command — the existing `_rvSidActive` semantics. Short-term: leave the working RV-SID path in `InitialClimbPhase` untouched; new phase handles the VA/VI/CF-with-continuation family (LINDZ1 is *not* an RV-SID — ends in CF). Long-term: converge both onto this phase.
  - **CF** (course→fix): capture then *track* the course onto the fix (block-B tracking); fly-over the fix if it begins an enroute transition; sequence at `DistanceNm < NavArrivalNm`.
  - **Turn direction:** honor `leg.TurnDirection` where coded (set `PreferredTurnDirection` until established, then null); shortest-turn where blank — do **not** synthesize a direction.
- Wire chain construction in `DepartureClearanceHandler` (`TryResolveSidFromCifp:731`): when resolved typed legs contain heading/course-tracked legs, build `…Takeoff → InitialClimb(gate-only) → DepartureProcedurePhase → (NavigationRoute drains TF transition)`. Thread `ProcedureLegs` through `DepartureRouteResult` (`:12`), `DepartureClearanceInfo` (`PhaseList.cs:45`), and `RefreshStoredDepartureClearance` (`:1150`) for taxi-stored clearances.
- **Keep in `InitialClimbPhase`:** the 400-AGL/DER TERPS gate (`:182-201,241-251`) — single tested source. `DepartureProcedurePhase` never turns below it.
- **Constraints while route is empty:** keep `SidViaMode=true`; the new phase applies the *active leg's* altitude/speed restriction directly each tick (mirroring `ApplyFixConstraints`/`ResolveAltitudeRestriction` incl. 14 CFR 91.117 250kt<10k and `LastProcedureSpeedKts`), since the route-based planner won't fire with an empty route.
- **Command override:** `CanAcceptCommand`/`OnCommandAccepted` mirroring `InitialClimbPhase.cs:301-365` — heading/DCT/speed/altitude `Allowed` (heading/DCT abandons the leg cursor); everything else `ClearsPhase`.
- **Snapshot:** new `DepartureProcedurePhaseDto` with `ProcedureLegs` + `_legIndex`; re-derive intercept/altitude progress each tick (no progress scalar to snapshot → deterministic replay, no migration). Old recordings restore with the legacy flat `DepartureRoute` path (legacy fallback rule).
- Critical files: new `DepartureProcedurePhase.cs`; `DepartureClearanceHandler.cs`; `Phases/PhaseList.cs`; `Simulation/Snapshots/PhaseSnapshotDto.cs`.

### Phase 2 — STARs: FM terminator + course tracking
- New `ArrivalProcedurePhase` (descent-context mirror of `DepartureProcedurePhase`, reusing Phase-0 primitives), engaged by `TryResolveStarFromCifp` when a STAR carries heading/course/FM legs. Otherwise unchanged (plain TF STARs keep flying fix-to-fix).
- **FM-terminating leg (high value):** at the FM anchor fix, fly the published outbound course and hold (await vectors) instead of holding arrival heading.
- **CF intermediate course tracking** on arrivals; **VA/CA** executed (rare on STARs but free via the shared resolver/phase).
- Honor STAR via-mode (DVIA) altitude/speed exactly as today.
- Critical files: new `Phases/.../ArrivalProcedurePhase.cs`; `src/Yaat.Sim/Commands/NavigationCommandHandler.cs` (`TryResolveStarFromCifp:358-404`); `src/Yaat.Sim/Scenarios/ScenarioLoader.cs` (`:1051,1137` call sites).

### Phase 3 — IAPs: close the minor gaps
- In `ApproachCommandHandler.BuildFixesFromLegs` (`:1457`) and `BuildMissedApproachFixes` (`:956`): stop dropping fix-less VA/CA/VI/VM legs — execute them via the Phase-0 primitives (a heading/intercept sub-segment before the first named fix).
- Track *intermediate* (pre-FAF) CF legs' coded course instead of direct-to-fix, reusing block-B tracking. (Final-approach CF/FA already course-correct via `FinalApproachCourseExtractor` — leave that path alone.)
- Critical files: `src/Yaat.Sim/Commands/ApproachCommandHandler.cs`.

---

## Aviation realism review — MANDATORY

Every phase touches pilot AI / departure-arrival-approach procedure execution → **must be reviewed by `aviation-sim-expert`** (per CLAUDE.md). Include in the invocation: *"The FAA 7110.65 and AIM are available as local markdown at `.claude/reference/faa/7110.65/` and `.claude/reference/faa/aim/`. Read them via Read/Grep/Glob. Do NOT use web search for 7110.65 or AIM."* Specific questions to resolve:
1. **Blank turn-direction byte → shortest-turn policy.** LINDZ1's VA/VI legs code no turn direction; correctness relies on shortest-turn (343°→273° = left). Is shortest-turn always safe when blank, or do some SIDs require a charted turn the data omits?
2. **VI→CF intercept-angle limit.** Approaches gate at 30°; departures may tolerate more. Bound, or always-capture?
3. **Altitude-gate semantics at high field elevation** (KASE ≈ 7820 ft; VA ≥9100 ≈ 1280 AGL) — climb through without leveling.
4. **Climb-gradient feasibility** (LINDZ1: 465 ft/NM to 10,600) — confirm whether to model/refuse the gradient or just fly the path (likely out of scope; flag explicitly).
5. **91.117** 250kt<10k during the VA climb through 9100 — already enforced; confirm it applies.

---

## Verification

**E2E TDD (real CIFP, no synthetic data — `TestVnasData.EnsureInitialized()`, silent-skip if absent):**
- **(A) Resolution unit test** (`tests/Yaat.Sim.Tests/.../KaseLindz1HeadingLegsTests.cs`): resolve LINDZ1 RW33 directly; assert the typed legs preserve `VA 343°/≥9100`, `VI 273°`, `CF 303°/LINDZ/≥16000` (fails today — they're dropped). Pins the data plumbing.
- **(B) Full E2E trajectory test** using the user's route **`KASE→KSFO : LINDZ1 LINDZ JNC BEVRR KATTS INYOE DYAMD5`**: spawn at KASE RW33, `CTO`, drive `Takeoff→InitialClimb→DepartureProcedurePhase`. Write a per-tick diagnostic `[Fact]` first (log alt, heading, target heading, leg index/type, signed cross-track to the 303° LINDZ line, distance-to-LINDZ), read it, then assert:
  - climbs straight on ~343° to **≥9100 before any turn toward 273°** (catches the early direct-to-LINDZ turn);
  - sustained window flying ~**273°** at alt ≥9100;
  - **intercepts and tracks the 303° back course** inbound (|cross-track| → <0.5nm, track within ~10° of 303°, distance-to-LINDZ monotonically decreasing, and at CF-leg start bearing-to-LINDZ ≠ heading — i.e. NOT direct);
  - after overflying LINDZ, `NavigationRoute` contains SLOLM/PACES/JNC and `DepartureProcedurePhase` is no longer current.
- STAR/approach phases get analogous resolution + trajectory tests (an FM-terminating STAR for Phase 2; a VA/CA-transition approach for Phase 3).

**Tooling:** `dotnet run --project tools/Yaat.CifpInspector -- --airport KASE --sid LINDZ1` for data truth; the per-tick `[Fact]` is the trajectory inspector (airborne — LayoutInspector not needed).

**Build/test gates (per CLAUDE.md):** `dotnet build -p:TreatWarningsAsErrors=true`; targeted tests; then `pwsh tools/test-all.ps1` (cross-repo — `Yaat.Sim` signature changes break yaat-server otherwise). Tee to `.tmp/`. Update `COMMANDS.md` (if any command behavior changes), `USER_GUIDE.md`, `docs/yaat-vs-atctrainer.md` (this closes a notable ATCTrainer parity gap), and `docs/architecture.md` (new phases/model).

---

## Key decisions & risks
- **Dedicated phases over extending `InitialClimbPhase`** — its self-clear/RV-SID/gate completion model is altitude/heading-scalar; a multi-leg cursor would entangle it. Matches the approach side's separate-phase pattern.
- **CF tracking = course-line tracking** (block-B aim-point), giving the real back-course track. (A simpler "synthetic on-course entry point + reuse `UpdateNavigation`" approximation was considered; rejected for the full-fidelity scope.)
- **Risks:** (1) blank turn-direction byte (aviation Q1) is the top correctness risk for generalizing; (2) magnetic-vs-true + heading-vs-track distinction (apply WCA to CA/CF, not VA/VI) — getting it wrong silently drifts the path; cross-track sign convention is load-bearing; (3) cleanly splitting "typed-leg SID → new phase" from the working RV-SID path in `DepartureClearanceHandler` without regression, and applying per-leg constraints while `NavigationRoute` is empty.
- **Parity framing:** matching ATCTrainer/charted behavior is a **Fixed** (parity gap), not Added, for CHANGELOG.
