# Ground & Tower Commands — Completeness & Implementation Review

*Date: 2026-04-29. Replaces the plan that produced this review.*

## 1. Executive summary

YAAT's ground/tower command surface is in noticeably better shape than the prior-session inventory suggested. Most "gaps" identified before this review were either phraseology-only (handled verbally by RPOs, no sim effect) or wrongly attributed — verbs that exist and behave correctly. After fresh verification reads of every cited file:

- **Ground completeness**: two real gaps — contextual `EXPEDITE` for taxi, intersection departure. Both have meaningful sim effect.
- **Tower completeness**: no behavioral gaps. Every controller-issued sim-affecting tower verb is present.
- **Robustness**: a handful of real concerns. Top three: (1) `CLAND` accepts from any phase, including cruise; (2) `EXIT` queues silently when issued before landing; (3) silent command rejection in several ground phases gives the RPO no feedback.
- **Already excellent**: stabilization-driven auto-go-around (`LandingPhase.CheckStabilizationGate`, `LandingPhase.cs:728`), kinematic exit-planning braking (`LandingPhase.cs:475-592`), `GoAround` ground-state gating via `_canGoAround` keyed to `RejectedLandingMinSpeed` (`LandingPhase.cs:604`, `LandingPhase.cs:933-959`), `CTOC` mid-roll abort (`DepartureClearanceHandler.cs:1063-1071`), aircraft-type-aware Vr (`AircraftPerformance.cs:253`).

The central thesis — *the sim should refuse nonsensical clearances rather than silently no-op* — holds, but applies to fewer cases than the plan flagged.

## 2. Ground commands — completeness

> *Registry-level completeness (`CanonicalCommandType` ↔ `CommandRegistry.All` ↔ `CommandScheme.Default()`) is test-enforced. This section covers semantic/behavioral gaps with sim effect.*

**Contextual `EXPEDITE` for taxi.** `EXPEDITE` exists for altitude (climb/descend faster). Real ATC also uses "expedite taxi" / "expedite crossing" to raise taxi speed when conflict timing demands it. No taxi-context behavior exists today. Recommended: extend `EXPEDITE` to detect ground/taxi context, raise the ground speed limit, and clear on the next `HOLD`/`RES`/`HS`.

**Intersection departure.** No canonical "line up at intersection X" verb. `RWY` assigns a runway end; `LUAW` and `CTO` both presuppose threshold (`TakeoffPhase.cs:74-75` reads `ctx.Runway.ThresholdLatitude/Longitude` unconditionally). Real-world intersection departures shorten available runway distance significantly — material for both takeoff performance modelling and SRS / wake-separation timing. Recommended: extend `RWY` (or add `INTDEP <runway> <intersection>`) to set a takeoff origin offset, plumbed through TakeoffPhase's threshold reference.

Excluded per scope rule (verbal-only, no sim state): frequency/handoff verbs, pushback request/approval loop, taxi readback/amend (a fresh `TAXI` *is* the amendment), push-and-hold vs push-and-engine-start. Also excluded: "HS variants parity" — the `HS` prefix on strip commands `HSC`/`HSA`/`HSD` stands for *Half Strip*, not *Hold Short*.

## 3. Ground commands — implementation robustness

**`HOLD POSITION` is context-blind.** `GroundCommandHandler.TryHoldPosition` (`GroundCommandHandler.cs:703`) only sets `aircraft.Ground.IsHeld = true`. The same verb fires in mid-taxi, in `CrossingRunwayPhase`, after pushback, and pre-LUAW; phases interpret it via their own command-acceptance rules. There's no RPO-visible feedback distinguishing "held during taxi" from "held on runway pre-takeoff." This works in practice but is invisible. Recommended: emit a phase-specific message ("holding short of taxiway X" vs "holding position on runway Y") so the RPO knows what state the sim now believes.

**Silent rejection in ground phases.** `PushbackPhase.CanAcceptCommand` (`PushbackPhase.cs:291`), `CrossingRunwayPhase.CanAcceptCommand` (`CrossingRunwayPhase.cs:127`), and `RunwayExitPhase.CanAcceptCommand` (`RunwayExitPhase.cs:513`) reject any command not in their whitelist with a generic "rejected" — no message explaining why. For training fidelity, the RPO needs to know *which* command was rejected and *why* (e.g. "cannot taxi during runway crossing"). Recommended: surface the rejection reason from `CommandAcceptance.Rejected` paths.

**Hardcoded simple-pushback distance.** `PushbackPhase.cs:20` defines `DefaultPushbackDistanceNm = 0.015` (≈90 ft). This applies only to simple pushback (no target taxiway / heading). A 737 (≈110 ft long) pushed back 90 ft has barely cleared its parking spot. Targeted pushback already uses `CategoryPerformance.PushbackOvershootNm` (referenced from `GroundCommandHandler.cs:551`), which IS aircraft-aware. Recommended: extend the simple-mode constant to use the same category lookup.

**`EXIT` queues without airborne/runway check.** `GroundCommandHandler.TryExitCommand` (`GroundCommandHandler.cs:1062`) sets `aircraft.Phases.RequestedExit = preference` with no validation. If a controller issues `EXIT K` during cruise, the value is silently stored and only consumed when the aircraft eventually reaches `LandingPhase` or `RunwayExitPhase`. Recommended: reject `EXIT` unless `LandingPhase` or `RunwayExitPhase` is current or pending in the phase list.

**Verb-existing-but-behavior-questionable that turn out fine.** Three prior-session concerns are invalidated by fresh reads:

- `GIVEWAY` semantics. `GroundCommandHandler.TryGiveWay` (`GroundCommandHandler.cs:836`) holds the aircraft (`IsHeld = true; GiveWayTarget = X`) until the target passes. `CommandDispatcher` routes both standalone `GiveWayCommand` (`CommandDispatcher.cs:1126`) and conditional dispatch (`CommandDispatcher.cs:1458`). Both forms work; the prior-session "only fires inside LV/AT" claim was wrong.
- `RWY` validates runway existence. `GroundCommandHandler.TryAssignRunway` (`GroundCommandHandler.cs:689`) calls `CommandDispatcher.ResolveRunway` and rejects unknown designators ("Unknown runway X").
- `CrossingRunwayPhase.OnEnd` clears speed targets. Line 116 implements `OnEnd`; line 122 zeros `IndicatedAirspeed` and `TargetSpeed` on completion.

## 4. Tower commands — completeness

Under the scope rule, no behavioral completeness gaps surfaced. All controller-issued sim-affecting verbs are present: `LUAW`, `CTO`/`CTOC`, `CLAND`, `LAHSO`, `GA`, `TC`/`TD`/`TB`, `EL`/`ER`/`EXIT`, `MLT`/`MRT`, `SA`/`MNA`, etc. Excluded non-gaps per scope rule: wake-turbulence advisories and "expect runway X" (verbal-only, already excluded by Decisions §4); "tower directs base" (TB exists — controller-vs-pilot initiation is identical in canonical form); "go around, traffic on runway" (GA fires; the reasoning is verbal). `MAKE SHORT APPROACH` is correct as-is — `PatternCommandHandler.TryMakeShortApproach` (`PatternCommandHandler.cs:505`) tightens downwind/base geometry without altering the touchdown target, which is the right behavior. Post-landing "hold position on runway" is what `LAHSO` already models (`PatternCommandHandler.TryLandAndHoldShort`, `PatternCommandHandler.cs:1238`).

## 5. Tower commands — implementation robustness

**LUAW IS gated.** `DepartureClearanceHandler.TryDepartureClearance` (`DepartureClearanceHandler.cs:68`) accepts only from `HoldingShortPhase`, `TaxiingPhase`, `LineUpPhase`, `HoldingInPositionPhase`. Anything else falls through to "Line up and wait requires aircraft to be taxiing or holding short" (`DepartureClearanceHandler.cs:104`). This aligns with 7110.65 §3-9-4 (LUAW positions for imminent departure; presupposes hold-short). The prior-session claim that LUAW could fire mid-air was wrong.

**CTO is correctly permissive.** Per FAA 7110.65 §3-9-10.a Note: *"Turbine-powered aircraft may be considered ready for takeoff when they reach the runway"*. CTO is **not** required to be preceded by LUAW; LUAW is the exception used when traffic prevents an immediate takeoff (§3-9-4.a). YAAT accepts CTO from `HoldingShort` (auto-line-up via `LineUpFromHoldShort`), `Taxiing` (stored for consumption at hold-short), and `LineUp/LinedUpAndWaiting` (immediate satisfy). The actual gap is narrower than the plan claimed: `TryClearedForTakeoff` (`DepartureClearanceHandler.cs:18`) only checks `aircraft.Phases?.AssignedRunway is not null` — it doesn't verify the assigned runway is consistent with the active taxi route's destination. Recommended: add a runway-route-consistency check when CTO is issued from `Taxiing`.

**CLAND is too permissive.** `PatternCommandHandler.TryClearedToLand` (`PatternCommandHandler.cs:1218`) only checks `aircraft.Phases is null`. Per 7110.65 §3-10-5, there is no hard FAA gate; convention (§3-10-4 example, AIM §5-4-7) is that landing clearance arrives on or near final ("expect landing clearance two-mile final"). Silently storing CLAND from cruise is the worst option — the clearance attaches to a runway the aircraft may not yet be vectored to. Recommended: either (a) accept from anywhere and document it explicitly so the RPO knows what to expect, or (b) reject with "not yet on approach to runway X" when issued before final-approach context. Pick one and own it.

**Pattern entry from any phase.** `PatternCommandHandler.TryEnterPattern` (`PatternCommandHandler.cs:15`) clears existing phases and rebuilds for any aircraft with an assigned runway. There is no airborne-state gate (Final has a geometric loop check at `PatternCommandHandler.cs:89` but other entries don't). On a ground aircraft, this would clobber the taxi/takeoff sequence. Recommended: reject pattern entries unless the aircraft is airborne.

**`ReplaceApproachEnding` silent acceptance.** `TG`/`SG`/`LA`/`COPT` all dispatch through `CommandDispatcher.ReplaceApproachEnding` (called from `PatternCommandHandler.cs:799/818/837/856`). If no approach-ending phase is pending in the phase list, the call silently succeeds without inserting anything. Recommended: return a `CommandResult(false, ...)` when the phase list has no terminator to replace, so the RPO sees "nothing to replace."

**GoAround in TakeoffPhase is rejected when airborne.** `TakeoffPhase.CanAcceptCommand` (`TakeoffPhase.cs:204`) returns `Rejected` for `GoAround` once `_airborne == true`. This is intentional — initial climb is committed and "go around" doesn't apply. Worth flagging in user docs: if the controller wants to abort post-rotation, that's not GA — it's "fly heading X / climb maintain Y" via the override path.

**Two GoAround construction sites — currently aligned, watch for drift.** `GoAroundHelper.Trigger` (auto-triggered from stabilization fail or FinalApproachPhase) and `PatternCommandHandler.TryGoAround` (manual `GA` command) both construct a `GoAroundPhase` with `TargetAltitude`, `ReenterPattern`, and `NextLandingFullStop` set the same way. The manual path additionally honors `AssignedMagneticHeading` and `TrafficPattern` overrides. Currently consistent. Action: any new `GoAroundPhase` field must be set in both places, or the construction must be extracted to a shared builder.

## 6. Flight mechanics fidelity

**Vr is type-aware.** `AircraftPerformance.RotationSpeed(aircraftType, category)` (`AircraftPerformance.cs:253`) returns `AircraftProfileDatabase.Get(aircraftType).RotateSpeed` first, falls back to category. Same pattern for initial climb speed/rate. The earlier blanket claim of "single rotation speed per category" was wrong.

**V1 not modeled; V2 not needed.** Per FAA expert (anchored to 14 CFR Part 25 / AFM): V1 ≈ Vr − 5 kts is a defensible category approximation; V2 only matters with engine-out, which YAAT doesn't model. Currently `DepartureClearanceHandler.TryCancelTakeoff` (`DepartureClearanceHandler.cs:1063-1071`) treats any speed as abortable when `aircraft.IsOnGround` — physically this can leave the aircraft past the point of safe stopping. Recommended (P2): add a category-keyed V1 to gate `CTOC` mid-roll behavior — below V1, the abort is "reject and stop"; above V1, the aborts becomes "continue takeoff" with a warning, since stopping safely on remaining runway is no longer guaranteed.

**Climb-out gradient is fixed at 400 ft AGL.** `TakeoffPhase.cs:15` `CompletionAgl = 400.0`. No obstacle clearance, no SID minimum gradient. For most departures this is fine; for climb-restricted SIDs (e.g. SFO's GAP3 gradient) the simulation will declare the climb "complete" before a real aircraft would meet the published constraint. Tag P2.

**Landing flare is closed-form AGL-indexed.** `LandingPhase.TickFlare` (`LandingPhase.cs:381`) computes vsi and target IAS as pure functions of current AGL — no time-dependent state, no float risk. Well-engineered; the design rationale is in the class summary (`LandingPhase.cs:19-25`). No changes needed.

**Pushback dynamics use category-driven turn rate and overshoot.** `CategoryPerformance.PushbackTurnRate` (`PushbackPhase.cs:109`) and `CategoryPerformance.PushbackOvershootNm` (`GroundCommandHandler.cs:551`) — both category-aware. Only the *simple-mode* pushback distance (`PushbackPhase.cs:20`) is fixed; see §3.

## 7. Prioritized fix list

**P0 — correctness/safety.**
- `CLAND` accepts from any phase (`PatternCommandHandler.cs:1218`): reject before final, or document acceptance behavior. Don't silently queue.
- Pattern entries from ground (`PatternCommandHandler.cs:15`): reject when `aircraft.IsOnGround`.
- Silent command rejection in ground phases (`PushbackPhase.cs:291`, `CrossingRunwayPhase.cs:127`, `RunwayExitPhase.cs:513`): surface a reason to the RPO.
- `EXIT` queued without airborne/runway gate (`GroundCommandHandler.cs:1062`): reject when not in/before landing.

**P1 — completeness/behavior.**
- Contextual `EXPEDITE` for taxi.
- `ReplaceApproachEnding` silent no-op when no approach pending: return failure (`PatternCommandHandler.cs:799/818/837/856`).
- CTO runway-route consistency check from `Taxiing` (assigned runway must match taxi route's destination).
- `HOLD POSITION` phase-specific feedback message (`GroundCommandHandler.cs:703`).

**P2 — fidelity polish.**
- Aircraft-category-driven simple-pushback distance (`PushbackPhase.cs:20`).
- Category-keyed V1 to gate `CTOC` mid-roll abort behavior (`DepartureClearanceHandler.cs:1063-1071`).
- Intersection departure as a takeoff origin offset (`TakeoffPhase.cs:74-75`).
- Climb-out gradient enforcement for SID-restricted departures (`TakeoffPhase.cs:15`).

**Closed (prior-session claims invalidated).**
- ~~LUAW position gate~~ — LUAW IS gated (`DepartureClearanceHandler.cs:68-104`).
- ~~CTO must require LinedUpAndWaitingPhase~~ — over-restrictive vs 7110.65 §3-9-10.a Note.
- ~~`GIVEWAY` only fires inside LV/AT~~ — standalone form works (`GroundCommandHandler.cs:836`, `CommandDispatcher.cs:1126`).
- ~~`RWY` does not validate~~ — it does (`GroundCommandHandler.cs:689`).
- ~~`CrossingRunwayPhase` lacks `OnEnd` clear~~ — it has one (`CrossingRunwayPhase.cs:116-125`).
- ~~`CTOC` doesn't actually abort mid-roll~~ — it does (`DepartureClearanceHandler.cs:1063-1071`).
- ~~`GoAround` post-flare unsafe~~ — gated by `_canGoAround` keyed to `RejectedLandingMinSpeed` (`LandingPhase.cs:604`, `:933-959`).
- ~~Single rotation speed per category~~ — Vr is type-aware (`AircraftPerformance.cs:253`).

## 8. Verification

To validate the findings:

- **Spot-check cited file:line refs.** Every citation in this document was re-read in the session that produced this review (no inherited line numbers from prior sessions). Open each cited file and confirm the line points to the noted code.
- **FAA cross-reference.** The aviation-sim-expert was consulted with `.claude/reference/faa/7110.65/` and `.claude/reference/faa/aim/`; the LUAW, CTO, CLAND, GoAround, and V1 recommendations anchor to: §3-9-4, §3-9-10.a Note, §3-9-11, §3-10-5, §3-10-6, §3-10-7, AIM §5-4-7, §5-5-5.
- **For implementation fixes (P0/P1):** add tests that issue each command from a now-rejected phase context and assert the failure message — e.g. `CLAND` from cruise → expect "not yet on approach to runway X" rather than silent acceptance. Use the standard E2E TDD workflow (`docs/e2e-tdd-issue-debugging.md`): write failing test, confirm fail, apply fix, confirm pass.
- **For invalidated claims:** add regression tests so the closed items stay closed. E.g. `LUAW` issued from a cruising aircraft must return "Line up and wait requires aircraft to be taxiing or holding short."
