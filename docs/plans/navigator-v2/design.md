# Ground Navigator V2 — clean-room design

> **Status: REVIEWED — ready to implement Phase 2 (interface + router extraction).** This reframes
> Ground-Graph-V2 Workstream 3 from "audit the shared v1.1 navigator" to "build a clean-room V2
> navigator designed for V2 geometry, behind a router switch, deleted-V1-at-the-flip" — mirroring how
> the [fillet generator](../../ground/fillet-generator.md) and [pathfinder](../../ground/pathfinder.md)
> were done. Reviewed by `architect-reviewer` (structure — APPROVE WITH REQUIRED CHANGES) and
> `aviation-sim-expert` (dynamics/speed model). Binding outcomes folded in below; see **§1a Review outcomes**.
>
> Read [`../../ground/navigator.md`](../../ground/navigator.md) first — it is the authoritative description of
> the **current** navigator (v1.1) and the source of the "kept core vs dropped compensations" split below.

## 1. Why clean-room, not patch

The navigator is the only ground-stack layer that was kept *shared* between V1 and V2 (the
"incremental v1.1" decision in [`../ground-graph-v2.md`](../ground-graph-v2.md)). That decision predates the
full-suite-on-V2 sweep, which showed the Legacy-tuned heuristics actively misfiring on V2 geometry.
The case for making it the third clean V2 layer:

1. **Shared = every V2 fix fights V1.** `GroundNavigator` steers under both fillet modes. Any tuning
   change to make V2 behave risks regressing the V1 production default, so each change costs a full V1
   regression pass. The fillet generator and pathfinder avoided this by being clean V2 behind a switch.
2. **Most of the navigator's complexity is Legacy-geometry compensation V2 makes unnecessary — or
   harmful.** `navigator.md` flags five mechanisms "to re-evaluate on V2"; they are the bulk of the hard
   code *and* the source of the failures: slow-turn **synthesis** (rounds corners the Legacy generator
   left sharp — V2 emits proper arcs), **cluster detection** + **chord-chain aggregate-turn** (V2 has no
   chord chains), the **orbit-stall** backstop (a band-aid for pure-pursuit limit cycles), the
   **reverse-arc tangent-flip** heuristic, and the **strict synthesis tolerance** that froze AMX669.
3. **V1 is deleted at the joint flip anyway** (Ground-Graph-V2 Phase 7). Tuning the shared navigator
   means re-tuning soon-to-be-deleted code under the constraint of not breaking soon-to-be-deleted V1.
4. **V2 geometry is easier to follow and the durable core already exists.** The closed-form arc playback
   (invariant I2 — position and heading are pure functions of one scalar, cannot drift) is ideal for
   V2's clean single arcs. With proper arcs in the graph, a V2 navigator is close to "pure-pursuit on
   straights + closed-form playback on arcs + a correct speed model" and little else.

**Evidence (the failing cluster, all V2-pathfinder + V2-fillet):**

| Test | Symptom | Attributed to |
|---|---|---|
| `IssueAmxTaxiOvershootTests.AMX669_HoldsShortOf1L…` | **froze at taxi seg 0** — synthesis skipped (30.8 ft off a 6 ft tolerance on an r=40 ft arc), pure-pursuit produced no motion | strict synthesis tolerance |
| `OakNorthFieldTaxiSpinTests…(EDG320)` | **wobble to ~399° cumulative rotation** (limit 320) taxiing out of SIG4: a 109° entry-alignment turn out of the spot (legit) + pure-pursuit overshoot accelerating to 20 kt on short RAMP/D segments then braking for a tight 15 ft D-RAMP corner | entry turn (legit) + pure-pursuit overshoot + tight-arc speed handling |
| `OakGaSpawnTurnAroundTests…(N172SP)` | spin guard | same family (to be re-confirmed) |
| `Issue166CrossShortcutsGrassTests.Ual19…` | route reaches a runway crossing late / following on collapsed-junction geometry | mixed: ① pathfinder edge-selection + ② following |
| `S2Oak4RvSidCtoTests.N436MS_CtoDuringTaxi…` | still `TaxiingPhase` at t=10 — navigator follows V2 arcs too slowly | following speed |
| `OakCrossThenHold.AfterRes` | route correct (crosses, holds on pure C) but settles 2 s late — `CrossingRunwayPhase` brakes to ~0 at the crossing exit for a tight 7.2 kt C-G arc then re-accelerates | tight-arc speed handling at a crossing handoff |

Note two of these (EDG320 tight-arc speed, AfterRes crossing decel) are partly a **speed-model**
question — the arc speed cap `GroundArc.MaxSafeSpeedKts` uses a *kinematic* `v = turnRate·radius`
formula, which under-caps tight arcs and (worse) over-permits large ones. This is `aviation-sim-expert`
work *regardless* of rewrite-vs-patch. The clean-room build does not remove that question; it isolates
it. **(Correction, per aviation review:** an earlier framing of this cluster blamed a "3°/s ground turn
rate → ~0.5 kt arc cap." That is wrong — `GroundTurnRate` is Jet 20 / TP 25 / Piston 35 °/s
(`AircraftCategory.cs:564`), already gear-calibrated; the navigator feeds the real rate to the cap
(`GroundNavigator.cs:1578`). The "0.5 kt" was a diagnostic artifact of passing 3.0 to `MaxSafeSpeedKts`
by hand. The real 15 ft piston-arc cap is ~5.4 kt — still too constraining on tight radii, but the fix
is the *formula*, not the turn rate. See §4.4.)

## 1a. Review outcomes (binding)

Both reviews are folded into the sections below; this is the index of binding deltas from the original draft.

**`architect-reviewer` — APPROVE WITH REQUIRED CHANGES.** Strategy sound; the §4.1 interface needed three fixes:
- **B1 — `ExtraSegmentsToAdvance` is a Legacy-only concept** (serves cluster synth, dropped on V2). It must stay in the interface for V1's lifetime but as a documented tombstone: `int ExtraSegmentsToAdvance => 0;` on V2, deleted at the joint flip. (§4.1)
- **B2 — `TargetLat/Lon` mutable setters are the *defining* seam, not a flat property.** The phase mutates the target to the painted hold-short bar *after* `SetupSegment` (`TaxiingPhase.cs:208`), and the navigator's tight-vs-loose arrival threshold silently depends on that mutation. Replace the bare setters with an explicit `OverrideTargetPosition(lat, lon)` (or pass the hold-short point into `SetupSegment`). (§4.1)
- **B3 — five construction sites, not two.** `new GroundNavigator()` / `GroundNavigator.FromSnapshot` live at `TaxiingPhase.cs:26`+`:177`, `RunwayExitPhase.cs:492`+`:665`, `CrossingRunwayPhase.cs:237` (which *discards* the navigator DTO and rebuilds). All must route through the factory; the Phase-2 acceptance criterion is a **grep gate** (zero `new GroundNavigator` / `GroundNavigator.FromSnapshot` outside the router), not just "suite green." (§4.1, §7)
- **S2 — router is a static *factory*** (`Create()` + `UseV2`), not an instance holder: the navigator is stateful per-aircraft (unlike the stateless pathfinder), so the router can't hold the instance. (§4.1)
- **S3 — keep one shared `GroundNavigatorDto`, additive fields, NO internal version tag** (the global `SnapshotSchemaMigrator` owns versioning; the round-trip is already lossy-by-rebuild). V2 populates the target subset; synth-era fields stay at default and are deleted at the flip. (§4.5)
- **S4 — two "keep/drop" entanglements:** kept corner-speed currently gets its angle from the dropped `EffectiveTurnAngleAt` → must rewire to read the single V2 arc's `TurnAngleDeg`; and the kept arc-anticipation arrival threshold shares a code line with the dropped synth branch → extract carefully. (§4.3)
- **S5 — Q1 (no-freeze) is a *global* tick-loop invariant**, not synth-only: kept entry-alignment also has a decline-to-pure-pursuit path that must never leave the aircraft stationary. (§3 Q1, §4.3)

**`aviation-sim-expert`.** The dynamics findings:
- **Do NOT disturb** `GroundTurnRate` (20/25/35/30 — gear-calibrated), `CornerSpeedForAngle`, `TaxiCornerSpeed`, `TaxiTightCornerSpeed`, accel/decel, `SlowTurnSpeedKts`, `NoseWheelTurnRadiusFt` — all aviation-sound.
- **DO replace the arc speed cap formula.** `GroundArc.MaxSafeSpeedKts(turnRate)`'s kinematic `v = r·ω` is the wrong model (it's "can the heading integrator keep up", not "is it safe"). Use a **lateral-acceleration** cap `v_safe = min(√(a_lat·r), CornerSpeedForAngle(sweep))`, `a_lat ≈ 0.13 g (1.27 m/s²)`, floored at `SlowTurnSpeedKts`. This is the model the codebase already uses for runway exits (`AircraftCategory.cs:767-786`). Yields ~4.7 kt @15 ft, ~7.6 kt @40 ft, ~9.1 kt @56 ft — realistic, and degrades as √r instead of collapsing/over-permitting linearly. **Highest-impact fix; resolves EDG320 tight-arc, AfterRes crossing decel, and the "tight arc too slow" family.** Separable, reviewed unit (touches `AirportGroundLayout`, `RouteCostFunction`, `TaxiPathfinder`, `GroundNavigator` call sites). (§4.4)
- **Runway-crossing momentum (Q4): do NOT brake to ~0 at the crossing exit.** 7110.65 §3-7-2 frames a crossing as cross-and-continue / "without delay" (runway-incursion mitigation). The crossing→taxiing handoff must **preserve current ground speed** (start the next segment's speed profile from actual gs, not a stop). Fixing the arc-cap formula largely dissolves AfterRes. Add a test assertion: min gs through the crossing ≥ ~5 kt. (§4.4, §5)
- Feed `CornerSpeedForAngle` from the single V2 arc's `TurnAngleDeg` (aligns with architect S4). Verify piston 2 kt/s decel has look-ahead to slow 20→8 kt on cramped GA ramps. Expect I7 (no-pivot floor) to go quiet once arcs aren't near-zero — keep it, verify. Keep the 45° entry-alignment gate so normal fillet corners use the arc path, not the 3 kt slow-turn path.

## 2. Scope & non-goals

**In scope:** a `GroundNavigatorV2` that physically steers an aircraft along an already-resolved
`TaxiRoute` over V2 geometry, per tick — the same role `GroundNavigator` plays today, behind a
`GroundNavigatorRouter` switch, default V1 until the joint flip.

**Out of scope (unchanged, owned elsewhere):**
- Route building → pathfinder ([`../../ground/pathfinder.md`](../../ground/pathfinder.md)).
- Arc geometry → fillet generator ([`../../ground/fillet-generator.md`](../../ground/fillet-generator.md)).
- Hold-short insertion, runway crossing, departure-clearance, parking, phase handoff → `TaxiingPhase`
  and siblings. The navigator returns `ArrivedAtNode`; the phase decides what happens.
- Conflict speed-limiting → `GroundConflictDetector` (the navigator only *honors* `Ground.SpeedLimit`).
- The landing/exit turn-off tuning → [`../../landing-and-runway-exit.md`](../../landing-and-runway-exit.md).

**Explicit non-goal:** behavioral parity with V1 on Legacy geometry. V2 only ever runs on V2 fillets +
V2 routes. The acceptance bar is the E2E taxi suite **under V2+V2**, not a V1 diff.

## 3. Requirements

Derived from the current contract (`navigator.md`) and the failing cluster. Each is a test target.

**Functional**
- **R1 — Follow straights.** Track the segment line via pure-pursuit (converge onto the line, not cut
  to the node), physics advances position.
- **R2 — Follow arcs.** Closed-form circular playback over `PathPrimitiveArc` (invariant I2): write
  position+heading together from one scalar; no waypoint feedback loop.
- **R3 — Slow turns.** Closed-form playback over `PathPrimitiveSlowTurn` at a capped speed for
  programmatic tight turns and entry alignment.
- **R4 — Entry alignment.** When a segment starts with heading far off its first tangent (wrong-way
  parking start, post-pushback), roll forward through a synthesised slow-turn rather than snapping
  heading or pivoting in place. *(EDG320's 109° turn out of SIG4 is this — a real need, not Legacy.)*
- **R5 — Stop where required.** Reach exactly (not "within N ft") at uncleared hold-shorts, the route
  end, and parking; honor the hold-short painted-bar offset the phase sets.
- **R6 — Speed profile.** Never overspeed into a future turn/arc/stop: corner-speed limits +
  backward-propagated kinematic braking, per category. Honor `Ground.SpeedLimit` from the conflict
  detector.
- **R7 — Arrival contract.** Return `NavigatorResult.ArrivedAtNode` at the to-node so the owning phase
  advances `CurrentSegmentIndex`; `Navigating` otherwise.
- **R8 — Snapshot round-trip.** Survive save/replay. May be lossy (rebuild the primitive from
  `route.CurrentSegmentIndex` on resume — V1 already does this).
- **R9 — Drop-in for three phases.** Usable by `TaxiingPhase`, `RunwayExitPhase`, `CrossingRunwayPhase`
  through the shared interface, with the V1 navigator still selectable via the router.

**Quality (the cluster fixes)**
- **Q1 — No freeze.** A skipped/declined special-case can never leave the aircraft stationary with
  route remaining (AMX669). Pure-pursuit forward motion is always the floor.
- **Q2 — No spin / minimal over-rotation.** Cumulative heading rotation over a taxi-out stays within
  the realistic envelope (EDG320/N172SP spin guards: ≤ 320° abs, ≤ 200° signed over 30 s).
- **Q3 — Smooth tight-arc speed.** Tight V2 corner arcs are traversed at a sane, aviation-validated
  speed without brake-to-zero-then-reaccelerate oscillation (AfterRes crossing, EDG320 RAMP/D corner).
- **Q4 — Forward progress.** No orbit/limit-cycle around a node; the orbit-stall backstop should be
  *unnecessary* on clean V2 arcs (prove it, don't port it).

## 4. Architecture

### 4.1 The switch (mirror the **live** `TaxiPathfinderRouter`)

Extract the phase-facing surface into `IGroundNavigator`; `GroundNavigator` (V1) implements it
unchanged; new `GroundNavigatorV2` implements it. `GroundNavigatorRouter` is a **static factory** (the
navigator is stateful per-aircraft, so the router builds instances, it does not hold one — this is the
difference from the stateless `TaxiPathfinderRouter`): `static IGroundNavigator Create()` + `static bool
UseV2`. Mirror `TaxiPathfinderRouter` (live); **not** the vestigial `FilletArcGeneratorRouter`.

All **five** construction sites route through the factory — both fresh `new GroundNavigator()` and
`GroundNavigator.FromSnapshot`: `TaxiingPhase.cs:26`+`:177`, `RunwayExitPhase.cs:492`+`:665`,
`CrossingRunwayPhase.cs:237` (today it discards the navigator DTO and rebuilds). Phase-2 acceptance is a
**grep gate**: zero `new GroundNavigator` / `GroundNavigator.FromSnapshot` outside the router (a plain
"suite green on V1" check passes even if a site is missed, because V1 is the default).

`IGroundNavigator` (surface the phases touch, with the review fixes):
```
double MaxSpeedKts { get; set; }
int TargetNodeId { get; }                 void SetTargetNodeId(int nodeId);
double PrevDistToTarget { get; }
NavTickDiag? LastTickDiag { get; }
void SetupSegment(TaxiRoute route, PhaseContext ctx, Func<int,bool> isHoldShortCleared);
void OverrideTargetPosition(double lat, double lon);   // B2: explicit hold-short-bar offset seam,
                                                       //     replaces mutable TargetLat/Lon setters
NavigatorResult Tick(PhaseContext ctx, bool isLastSegment, Func<int,bool> isHoldShortCleared);
int ExtraSegmentsToAdvance => 0;          // B1: Legacy/cluster-synth tombstone; V2 returns 0; dies at flip
GroundNavigatorDto ToSnapshot();          // S3: shared additive DTO; V2 fills the target subset, rebuilds on resume
```
`TargetLat`/`TargetLon` stay readable for snapshot/diagnostics but are **set** only via
`OverrideTargetPosition` (B2 — the painted-bar offset is a two-way contract the navigator's arrival
threshold depends on, not a free setter). `FromSnapshot` goes through the factory like construction (B3).
`SetupPrimitive` is **not** in the interface (no production callers; the two `PathPrimitiveBuilderTests`
callers bind the concrete V1 type and die with V1).

### 4.2 Primitive model — reuse as-is

`PathPrimitive` (Straight / Arc / SlowTurn) and `PathPrimitiveBuilder.FromSegment` already target a
"GroundNavigatorV2" (the doc comments name it). V2 reuses them unchanged — the Bezier→true-circle
recovery is geometry, not Legacy tuning.

### 4.3 Kept core vs dropped compensations

| Mechanism | V1 (v1.1) | V2 | Rationale |
|---|---|---|---|
| Closed-form arc playback (I2) | yes | **keep** | Ideal for clean V2 arcs; the reason arcs can't drift |
| I7 no-pivot-in-place | yes | **keep** | Physical correctness, geometry-independent |
| Pure-pursuit on straights + pre-turn blend | yes | **keep** | Line convergence; geometry-independent |
| Backward-propagated braking + corner speeds | yes | **keep** | Don't-overspeed-into-turns; geometry-independent |
| Entry alignment (slow-turn from misaligned start) | yes | **keep** (R4) | Real need (parking-out U-turns) |
| Speed-limit honoring | yes | **keep** | Conflict-detector contract |
| Slow-turn **synthesis** planner | yes | **drop** | Rounds corners Legacy left sharp; V2 emits proper arcs. *Froze AMX669.* |
| Cluster detection (`TryDetectCluster`) | yes | **drop** | V2 has no chord chains |
| Chord-chain aggregate-turn (`EffectiveTurnAngleAt`) | yes | **drop** | Same — read the single corner directly |
| Orbit-stall backstop (`TicksNearTarget`) | yes | **drop, prove unneeded** (Q4) | Band-aid for un-filleted-node limit cycles |
| Reverse-arc tangent-flip heuristic | yes | **drop / re-derive** | `DirectionalEdge` bearings already handle direction on V2 |
| Strict synthesis tolerance | yes | **gone with synthesis** | Replaced by R4 entry-alignment + Q1 floor |

The V2 tick loop becomes: *dispatch on primitive → straight = pure-pursuit + speed; arc/slow-turn =
closed-form advance + speed; arrival check → `ArrivedAtNode`.* No synthesis trigger, no cluster
retarget, no orbit counter.

**Two extraction hazards (architect S4) — the drops are not clean deletions:**
- Kept corner-speed currently gets its angle from the dropped `EffectiveTurnAngleAt`. Rewire it to read
  the single V2 arc's `TurnAngleDeg` (§4.4c) — a substitution in *kept* code, not a deletion.
- The kept arc-anticipation arrival threshold (`_nextSegmentIsArc`) shares a code line with the dropped
  synth branch (`GroundNavigator.cs:1174-1176`). Extract the kept tight-threshold without dropping it.

**S5 — the freeze is not synth-only.** Kept entry-alignment also *declines and falls through to
pure-pursuit* when geometry doesn't fit (its own "segment long enough" gate). Q1 ("pure-pursuit forward
motion is always the floor") must therefore be a **global** tick-loop invariant, enforced for every
decline path, not just where synthesis used to live.

> **Implemented (2026-05-30) — resolves S5 without a backstop.** The OAK/FLL ramp-cluster coverage pairs
> orbited because of exactly this decline path: a bend *tighter than the nose-wheel radius* (ramp/apron
> corners the fillet generator cannot widen — they stay sharp vertices between 4–60 ft straights) made
> entry-alignment decline (its `segmentLongEnough` gate) and fall through to pure-pursuit, which cannot
> converge on a corner node whose orbit radius `v/ω` exceeds the short segment, even at the `SlowTurnSpeedKts`
> floor → infinite circle. Fix, in two coupled parts (aviation-reviewed):
> 1. **Un-gate the rounding.** Entry-alignment now fires for *any* corner past `EntryAlignmentThresholdDeg`,
>    regardless of segment length — a sub-radius bend *must* be rounded at the nose-wheel radius; there is no
>    "segment long enough" to track it on. This removes the decline path entirely (S5's freeze can't occur),
>    so a global no-freeze floor is unnecessary — the corner is *rounded*, not *rescued*.
> 2. **Turn-rate corner-speed cap (`CornerSpeed`, refines §4.4c).** Per-corner required speed is capped at
>    `v ≤ ω·(½·min(into,out))/θ` (floored at `SlowTurnSpeedKts`) so the speed planner slows the aircraft into
>    the bend *before* arrival — a pilot easing off for a tight ramp turn — instead of overshooting the
>    corner node and stranding the target behind it.
>
> Net: the "V2 emits proper arcs, synthesis unnecessary" rationale holds for *taxiway* corners but not for
> sub-nose-wheel *ramp* corners; those are handled by geometric corner-rounding (the kept entry-alignment,
> broadened), not the dropped Legacy chord-chain synthesis. Validated by the all-V2 coverage gate
> (`V2TaxiCoverageAcceptanceTests`, 31/31). Remaining cluster items: EDG320 mild parking-out wiggle
> (338°/320°, not an orbit) and `OakCrossThenHold.AfterRes` (Q4 crossing→hold handoff).

### 4.4 Speed / turn model — **aviation-reviewed, separable from the rewrite**

The cluster's tight-arc symptoms (EDG320, AfterRes) are a *speed-model* defect, not a turn-rate one.
`aviation-sim-expert` sign-off (above) gives a concrete, separable fix that can land **before or in
parallel with** the navigator rewrite (it touches the shared arc model, which both V1 and V2 read):

**(a) Replace the arc speed cap — the highest-impact fix.** `GroundArc.MaxSafeSpeedKts(turnRate)`
(`AirportGroundLayout.cs:338`) uses a *kinematic* `v = turnRate·radius`: it answers "can the heading
integrator keep up?", not "is it safe?", so it under-caps tight arcs and over-permits large ones (a 100 ft
piston arc → ~36 kt). Replace with a **lateral-acceleration** cap, the model the codebase already uses for
runway exits (`AircraftCategory.cs:767-786`):

```
v_safe = min( sqrt(a_lat · r) , CornerSpeedForAngle(category, arc.TurnAngleDeg) )   floored at SlowTurnSpeedKts
a_lat ≈ 0.13 g ≈ 1.27 m/s²   (taxi comfort / tire-scrub limit; ICAO Annex 14 rapid-exit basis)
```

Yields ~4.7 kt @15 ft, ~7.6 kt @40 ft, ~9.1 kt @56 ft — realistic, degrading as √r. `CornerSpeedForAngle`
(unchanged, aviation-sound) stays the angle-based ceiling. This is a **replace, not deprecate**: the
`MaxSafeSpeedKts(turnRate)` signature changes to a category/lateral-accel parameter, so update the four
call sites (`AirportGroundLayout.cs`, `RouteCostFunction.cs:93`, `TaxiPathfinder.cs:827`+`:1069`,
`GroundNavigator.cs:1578`). **Do NOT touch** `GroundTurnRate`, `CornerSpeedForAngle`, `TaxiCornerSpeed`,
`TaxiTightCornerSpeed`, accel/decel, `SlowTurnSpeedKts`, `NoseWheelTurnRadiusFt` — all validated.

**(b) Crossing→taxiing preserves momentum (Q4).** A cleared runway crossing is "cross and continue /
without delay" (7110.65 §3-7-2) — braking to ~0 at the far side is a runway-incursion anti-pattern. The
`CrossingRunwayPhase`→`TaxiingPhase` handoff must start the next segment's speed profile from the
aircraft's **actual current ground speed**, not re-plan from a stop; do not let the handoff reset
`PrevDistToTarget`/speed state into a spurious stop. Fix (a) largely dissolves AfterRes on its own (the
crossing-exit arc no longer caps near zero); (b) guarantees it. Test target: min gs through the crossing
≥ ~5 kt (§5).

**(c) Corner-speed input on V2 (ties to §4.3 S4).** Feed `CornerSpeedForAngle` the single V2 arc's
`TurnAngleDeg` (`AirportGroundLayout.cs`), replacing the dropped chord-chain `EffectiveTurnAngleAt`
aggregation. Verify the piston 2 kt/s decel has enough look-ahead to slow 20→8 kt on cramped GA ramps
(a test target, not a constant change). Expect I7 (no-pivot floor) to fire far less once arcs aren't
near-zero — keep it (physically correct), verify it goes quiet.

A clean V2 navigator consumes whatever model is signed off; the model change is its own reviewed unit.

### 4.5 Snapshot

V1's round-trip is already intentionally lossy (rebuild primitive from `CurrentSegmentIndex`). V2 keeps
that. Per architect S3: **one shared `GroundNavigatorDto`, additive fields, no internal version tag** —
the global `SnapshotSchemaMigrator` owns versioning. V2 populates the target subset
(`TargetNodeId`/`TargetLat`/`TargetLon`/`MaxSpeedKts`) and a full reconstruct in `SetupSegment` on resume;
the synthesis-era fields (`CurrentNodeRequiredSpeed`, `NextSegmentBearing`, `TicksNearTarget`) stay at
default and are deleted from the DTO at the joint flip (normal additive-then-subtractive evolution).

## 5. Validation harness (the acceptance gate)

The clean-room build is "done" when the **E2E recording-replay taxi suite is green under V2+V2** (the
temp-flip ship config). Anchor tests, run with `TaxiPathfinderRouter.Current = V2` +
`TestAirportGroundData(FilletMode.V2)` + `GroundNavigatorRouter` = V2:
- Freeze/forward-progress: `IssueAmxTaxiOvershoot` (AMX669), `OakNorthFieldTaxiSpin`/`OakGaSpawnTurnAround`
  forward-progress variants.
- Spin guards (Q2): `OakNorthFieldTaxiSpin`/`OakGaSpawnTurnAround` `…DoesNotSpinNearlyFullCircle`.
- Crossing handoff (Q3): `OakCrossThenHold.AfterRes` (+ new assertion: min gs through the crossing ≥ ~5 kt — §4.4b).
- Arc-cap formula (§4.4a): a guard that the lateral-accel cap yields realistic per-radius speeds and that no V2 arc commands below `SlowTurnSpeedKts`.
- Following speed (R6): `S2Oak4RvSidCto.N436MS`.
- Coverage smoke: `FilletV2TaxiCoverageTests` (31 OAK/SFO/FLL pairs) + `FilletV2LandingExitTests`.
- Full Phase-6 re-sweep: `Category!=Nightly` under V2+V2 → 0 failures (the joint-flip gate).

`TickRecorder` + `LayoutInspector --ticks/--tick-table` are the per-tick diagnostics; each fix gets a
fail-first repro per the TDD rule.

## 6. Risks & mitigations

- **Physics coupling (highest).** Writes position/heading directly; interacts with `FlightPhysics`,
  `GroundConflictDetector`, phases, snapshots. *Mitigation:* the strong E2E harness above; build behind
  the switch so nothing ships until V2+V2 is green; keep the kept-core invariants (I2, I7) verbatim.
- **Dropping a compensation that was load-bearing on V2 too.** *Mitigation:* drop incrementally, each
  drop guarded by the relevant spin/orbit test; if a V2 scenario genuinely needs one, re-introduce it
  re-tuned for V2 radii (and document why).
- **Speed-model realism.** *Mitigation:* `aviation-sim-expert` sign-off on `CategoryPerformance`
  before relying on it; treat the turn-rate/maxSafe change as its own reviewed unit.
- **Snapshot/replay regressions.** *Mitigation:* round-trip tests in the bug-bundle replay suite.

## 7. Rollout (reshapes Workstream 3)

1. **This design** → reviewed (`architect-reviewer` + `aviation-sim-expert`) → revised. ✅ done.
2. **Lateral-accel arc-cap (§4.4a)** ✅ done — replaced the kinematic `v=r·ω` cap with
   `min(√(a_lat·r), CornerSpeedForAngle(category, TurnAngleDeg))` floored at `SlowTurnSpeedKts`; signature
   `(double turnRate)`→`(AircraftCategory)`; all call sites updated; formula unit tests rewritten; V1 suite green
   (6781/0). Under V2+V2 the crossing keeps momentum (AfterRes no longer brakes to ~0) and the floor clears the
   AMX669 freeze. **Crossing-momentum guard (§4.4b)** — the min-gs-through-crossing assertion folds into the
   Phase-4 V2 navigator work (where AfterRes goes green under V2+V2), avoiding a V1-default false-fail.
3. `IGroundNavigator` + `GroundNavigatorRouter` (static factory) extraction; V1 implements it; **all five**
   construction sites route through the factory. Pure refactor, no behavior change; V1 stays default.
   **Acceptance: the grep gate** (zero `new GroundNavigator` / `GroundNavigator.FromSnapshot` outside the
   router) + full suite green on V1 — the suite alone won't catch a missed site (B3/N1).
4. `GroundNavigatorV2` skeleton: straight pure-pursuit + closed-form arc/slow-turn + speed profile +
   entry alignment. No synthesis/cluster/orbit machinery. Q1 no-freeze floor is a **global** tick-loop
   invariant (covers the kept entry-alignment decline path too — S5). Rewire corner-speed to the single
   arc's `TurnAngleDeg` (S4).
5. Iterate against the V2+V2 harness; fail-first repro per anchor test; aviation review on any further
   speed/turn change.
6. Joint flip (Ground-Graph-V2 Phase 7): flip fillet + pathfinder + navigator to V2 together; delete
   V1 `GroundNavigator` and its Legacy compensations; retire the routers + the `ExtraSegmentsToAdvance`
   tombstone + the dead DTO fields.

## 8. Open questions — resolved by review

- **Seam (architect):** `IGroundNavigator` is the right seam *with the §4.1 fixes* (explicit
  `OverrideTargetPosition`, `ExtraSegmentsToAdvance` tombstone). Not a thinner abstraction.
- **Router (architect):** Static **factory** (`Create()` + `UseV2`), not injected, not an instance holder
  — the navigator is stateful per-aircraft. Mirror the live `TaxiPathfinderRouter`.
- **Snapshot (architect):** One shared `GroundNavigatorDto`, additive, no internal version tag (§4.5).
- **Turn rate / arc cap (aviation):** `GroundTurnRate` is realistic and untouched; the defect is the
  *kinematic* arc-cap formula — replace with a lateral-accel cap (§4.4a).
- **Crossing momentum (aviation):** Preserve ground speed across the handoff; don't brake to ~0 (§4.4b).
- **Orbit-stall backstop (both):** Drop on V2 and prove unneeded (Q4), guarded by the spin tests; keep
  the Q1 no-freeze floor as the global safety invariant instead.

## Related

- [`../../ground/navigator.md`](../../ground/navigator.md) — current v1.1 navigator (kept-core source).
- [`../ground-graph-v2.md`](../ground-graph-v2.md) — three-layer transition + joint flip gate (Workstream 3).
- [`../filletv2/v2-sim-validation.md`](../filletv2/v2-sim-validation.md) § "Navigator review" — root-cause ② origin.
- [`../pathfinderv2/default-flip-triage.md`](../pathfinderv2/default-flip-triage.md) — the sibling clean-V2 layer's triage + the navigator-WS3 cluster status.
