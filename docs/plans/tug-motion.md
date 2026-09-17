# Step 3d (revised) — tug moves that roll, plan their reversals, and check the path they fly

Branch `bug/2026-09-16/5-add-right-click-on-ground-view-for-push`, rebased on main `b970b2c2` (9 commits,
`830ebd42` … `6dffd820`). Parts 1–4 of the original plan have shipped. This plan (approved 2026-09-16)
replaces step 3d in [pushback-route.md](./pushback-route.md), which links here.

## Context

A live trace of `PUSHM $6A $6B` off SFO gate D15 shows three things a tug cannot do:

- **Pivot in place.** The nose turns 108°→148° at 0 kt before leg 1, and 143°→28° (115°, about 23 s) at 0 kt on arrival at 6B.
- **Crab.** Leg 1's tail travels 348° while the nose sits at 148° and then turns to 118°: 20–50° sideways.
- **Pull against the spot.** The planner's §11.10 "avoid backing" preference makes 6A→6B a pull travelling 143° into a spot whose nose-out is 28°. The ground video shows `PUSH $6A`, then `PUSH $6B`.

The user chose (2026-09-16) **coupled nose + lead-in** and **the last leg's push/pull follows its required
facing**. The aviation review of that design blocked it:

1. **Loops.** It plans a loop at the alley mouth, toward taxiway A (the reviewer's estimate from geometry, not a run).
2. **Pivot comes back.** Fixed-rate steering (5°/s) brings back the in-place pivot at low speed.
3. **Unchecked path.** Only the straight from→to line is checked, not the flown path.
4. **Widebodies.** 5°/s is impossible for them at walking pace (B77W: 2.8°/s at 3 kt).

**Accepted from the review:**
- steer by a per-type turn radius;
- a straight push-off from a stand before any turn;
- reversal points instead of loops;
- intermediate spots end nose-out;
- check the flown path, footprint included;
- re-key the #167 amendment window;
- relabel 5 kt and 3 kt as judgement calls under ISO 20683's 5.4 kt ceiling;
- drop the AC 00-65A Appendix A items 20/22 citation.

**User answers (2026-09-16):**
- Pulls run at **5 kt**; the 8 kt transit speed is dropped. AC 00-65A §11.14: "Towing speed should not exceed that of walking team members."
- **One planner drives `PUSH` and `PUSHM`.**
- A `PUSH FACE` change over 135° from a stand is **allowed, flown at the type's tightest radius**.
- `PUSH <taxiway> <facing>` **joins the centreline and stops once lined up**.
- A bare `PUSH <taxiway>` stays **straight back** (user ruling 2026-09-15).

**Outcome:**
- Every pushback and tug move turns only while moving, with no crab.
- Direction changes happen only at explicit stop-and-reverse points.
- A spot ends with the nosewheel on the mark, nose-out.
- A move is refused when its **flown** path touches a runway, a runway holding position, or taxiway pavement it wasn't sent to.
- The ground-view preview draws the path the aircraft will fly.

## Design

### 1. `TugKinematics` — one motion body, shared by the phase and the planner
New `src/Yaat.Sim/Data/Airport/TugKinematics.cs`: pure and deterministic, with no aircraft state. It lives in
`Data/Airport/` because `GroundPhaseConvention` rejects a non-phase class under `Phases/Ground/`.

- **Turn radius, per type.**
  - Routine: `WheelbaseFt`, which is 45° of nose-gear steering.
  - Tight: `WheelbaseFt / tan 67.5°`, about 0.41 × wheelbase (B738 ≈ 21 ft).
  - Wheelbase comes from `FaaAircraftDatabase.Get(type)` (`FaaAircraftRecord.WheelbaseFt`, which nothing reads today).
  - Fallback when the record has no wheelbase, by category: Jet 50 ft, Turboprop 30 ft, Piston 7 ft, Helicopter 10 ft.
  - All four rules above are judgement calls.
- **Steering is by curvature, never by rate.** Each step, heading changes by at most `distanceMoved / R`. A stopped aircraft cannot rotate, and the path does not depend on speed. The body follows the path: on a push, the nose is the reciprocal of the direction of travel; on a pull, the nose is the direction of travel.
- **Move shapes** (a `TugMove` is one continuous push or pull):
  - `Straight(distanceFt)`: along the current direction of travel.
  - `ToPoint(point)`: pursues the point. It ends within 1 ft, or within 3 ft once the point is abeam or behind, so a 2 ft step can't skip it (D3).
  - `ViaLine(linePoint, lineTravelDeg, stopAt?)`: captures the line with the **rollout law**, then follows it. The commanded course is `χL − sign(e)·θ(e)`, with θ = 90° when |e| ≥ R_c and `acos(1 − |e|/R_c)` otherwise, and R_c = 1.15 R (judgement margin). The aircraft turns in at up to 90°, flies straight, then rolls out on an arc: the shortest lateral S-curve.
    - This replaced the vector-field law `χL − 90°·(2/π)·atan(e/R)` in D2b. That law converges logarithmically: D15 → `$6A` planned 1,154 ft for a 521 ft move, and each capture took 200–300 ft on lanes 150–180 ft deep.
    - **Near-line blend (D3c).** The rollout angle is steepest at the line (about 2.4° at 0.05 ft), so a captured line followed to a far stop chattered, and the phase read "turning" all the way. Within 5 ft of the line, θ scales linearly to 0. "Turning", which triggers the wingtip speed cap, now means the step actually turned by more than 25% of its maximum.
    - With `stopAt`, it ends once it has captured the line and its along-line position is within 1 ft of `stopAt` (see the stop rule below).
    - With no `stopAt`, it ends at capture: |e| ≤ 1 ft and |Δχ| ≤ 1°.
  - `TurnTo(facingDeg)`: a constant-radius arc until the nose is on the facing.
  - Every move carries `Kind` (Push/Pull), `Tight` (use the tight radius), `Creep` (3 kt instead of 5 kt) and `DwellBefore` (a reversal).
- **API** (as built in D1; distance steps, not time steps):
  - `SteerTravel(pose, move, progress, radiusFt, stepFt)` returns the new direction of travel. The phase calls it with the distance physics will move this sub-tick.
  - `Advance(pose, move, travel, stepFt)` and `Record(progress, pose, move, stepFt)` move the pose and update the move's progress.
  - `IsComplete(pose, move, progress)`.
  - `Simulate(start, moves, type, stepFt)` returns a `TugSimulation` of per-move `TugMoveTrace`s: samples about every 5 ft, completion, path length, maximum travel deviation, end pose, and for a `ViaLine` the end cross-track and line error. The per-move travel budget is max(3 × straight-line distance, 600 ft); a move that exceeds it is unflyable.
  - **Stop rule (corrected in D2):** a `ViaLine` with `stopAt` completes only when it has captured the line **and** reached `stopAt`. A move that reaches `stopAt` before capturing carries on until it captures, and the planner then judges the overshoot. D1 shipped the rule as "reached `stopAt`, captured or not", which followed its brief rather than this plan.

### 2. `TugMovePlanner` — replaces `PushbackLegPlanner` (same file, renamed)
**Input.** A start pose, whether the aircraft is on a stand, the aircraft type, and an ordered list of goals:
- `Spot(node)`: facing = nose-out from `TryGetSpotOutboundHeading`; stop point S = a half-fuselage behind the mark; staging point T = a further clamp(0.75 × length, 40, 100) ft back. `SpotStopGeometry` moves here from `GroundCommandHandler`.
- `Stand(node)`: facing = the node's heading; S = the node.
- `Node(node, facing?)`.
- `TaxiwayLine(point, facingDeg)`, for `PUSH <taxiway> <facing>`.
- `StraightBackTo(point)`, for a bare `PUSH <taxiway>`.
- `Facing(deg)`, for `PUSH FACE/TAIL`.
- `Clear`, for a bare `PUSH`.

An explicit trailing FACE overrides the last goal's facing.

**Output.** A `TugPlan`: the moves, each with its simulated path, and the simulated end pose. A refusal returns
null plus a message.

**Rules.**
1. **Stand start.** Off a stand, the first move is `Push Straight(½ fuselage)` (judgement: the same half-fuselage length `HasLeftTheStand` uses), and the next move must also be a push. A `Node` goal ahead of the nose off a stand is still refused, as today.
2. **Goal with a facing F** (spots, stands, lines, a node with a facing). The approach line L runs through S along F.
   - The **side** is pull if |bearing(P→S) − F| ≤ 90°, including exactly 90°; otherwise push.
     - **Tie band (D2b):** within 5° of 90°, the planner builds the candidates for both sides and lets the ranking decide; on a full tie the push side goes first.
     - The band exists because 6A and 6B sit exactly abeam, so the test reads 90.44° or 89.56° depending on a 2 ft end error.
     - The final pull of a spot goal is always `Creep`, whichever template produced it.
   - Candidate templates, each simulated in turn:
     - **T1:** `[side-kind ViaLine(L, stop)]`. The stop is S for a pull; for a push it is T for a spot (then a reversal and `Pull Creep Straight` onto S) and S otherwise.
     - **T2:** `[other-kind ViaLine(L, floating)]`, reversal, then T1's move.
     - **T3:** `[other-kind TurnTo(F ± (Δ − 90°))]`, reversal, then T1's move — a three-point turn for body rotations Δ > 90°.
   - A candidate is dropped when it:
     - breaks rule 1;
     - has a same-kind joint whose direction of travel changes by more than 90°;
     - contains a move whose direction of travel wanders more than 120° from where that move started (a loop; `TurnTo` is exempt; judgement);
     - exceeds a travel budget;
     - fails the flown-path check (§3).
   - Among the survivors: fewest reversals, then shortest total path, then template order.
   - **D4 revisions.**
     - Both sides' templates are always built; the side test only orders them.
     - T3 is tried for intermediate facings F ± 90°/60°/30°, whenever Δ > 30°.
     - A candidate dropped only because its final pull overshoots by X ≤ 60 ft gets one retry: a same-kind `Straight` of 1.5X + 10 ft before the reversal. The case that needed it is C9 → `$5B`: its best three-point turn overshot by 6.1 ft.
     - A flown-path refusal is reported only from candidates that passed every shape rule.
     - The "ran past the stop" drop applies to a line move of either kind. Before, a push-side stand candidate that ended 214–320 ft past the stand was kept.
     - **Tight radius only when the controller asked for it (D3e).** A turn the planner invents for itself (a T3 three-point turn, or a line capture) always uses the routine radius. `Tight` is kept for the explicit `PUSH FACE` over 135° that the user ruled on. D4 had briefly chosen a tight 140° push-turn right after D15's push-off, which sweeps the tail toward the neighbouring stands.
   - Worked case, D15 `PUSH $6A`: push-off, then a push capture of the T6A line, then a reversal and a pull north onto 6A. `PUSHM $6A $6B` adds a reversal, a push capture of the T6B line to its staging point, and a reversal with a creep pull onto 6B. This is what the reviewer predicts; the tests confirm it.
3. **`Node` goal with no facing.** Kind comes from the nose (target more than 90° off the nose = push), shape `ToPoint`. A change of kind is a reversal.
4. **`Facing(F)`.** Push-off (from a stand), then `Push TurnTo(F)` (`Tight` when the change exceeds 135°), then a straight push for whatever remains of `SimplePushbackDistanceNm`.
5. **`Clear`.** `Push Straight(SimplePushbackDistanceNm)`: behaviour unchanged.
6. **`StraightBackTo(twy)`** (revised in D4, user 2026-09-16). A bare `PUSH <twy>` resolves in three steps.
   - (a) If the push ray crosses a straight `<twy>` edge, push straight back to it. The nose stays on the stand heading, per the 2026-09-15 ruling.
   - (b) Otherwise, if a straight `<twy>` edge running within 30° of the push direction lies behind the aircraft, S-curve onto that edge's line. The push then carries on along the line to the edge's nearest point, so the aircraft ends **on** the taxiway, with the nose within 30° of the stand heading. SFO B2 `PUSH M4` works this way: M4 runs about 87 ft west of the ray.
   - (c) Otherwise, refuse: "taxiway X is not behind the aircraft".
   - The first version stopped at the exit node's along-track projection, which left #172's WJA1521 off M4.
7. **`TaxiwayLine`.** Push-off (from a stand), then `Push ViaLine(L, floating)`. L is the taxiway edge through the exit node along F, resolved by today's `GetEdgeBearingForTaxiway`. `Tight` applies when the change exceeds 135°. `PushbackOvershootNm` is deleted if nothing else reads it.
8. **Dwell.** `DwellBefore` is set on every move whose kind differs from the previous move's.

### 3. Flown-path check (inside the planner, on every candidate)
The check runs on every sample, about every 5 ft of the simulated path, using the footprint rectangle (length
× wingspan about the reference point, oriented by the nose). Missing dimensions fall back to
`HoldShortAnnotator.CwtFallbackLengthFt` and a category span. It refuses a plan when any of these holds:

- **Runway.** The footprint comes within the runway half-width of a runway centreline: the layout's width when it has one, else 150 ft.
- **Holding position.** A footprint side crosses any edge that touches a `RunwayHoldShort` node, or the goal node is one.
- **Movement area.** The fuselage segment (nose to tail) crosses movement-area pavement, as classified by the existing `PavementClassifier.MovementAreaName`. Three things are exempt:
  - the pavement the goal names (`TaxiwayLine` / `StraightBackTo`'s taxiway, or a goal node's edge names);
  - pavement the move starts on, within its first 25 ft;
  - pavement the move arrives at, within its last 25 ft (the current `MovementAreaEndWindowFt`).

- **Off the map — tried in D2b, removed in D3c.** The check refused a plan when any sample lay more than 100 ft from every ground-graph edge. It refused real pushes: OAK `PUSH D` from NEW7, an OAK push facing D, and SFO `PUSH @A9` from A4. Real aprons have ungraphed stretches wider than that, and the layout has no pavement polygons to check against. Keeping a flown path on the pavement remains unsolved: runway, holding-position and movement-area pavement are checked, open apron is not.

- **Taxiway behind the stand (user, 2026-09-16).** For a move that starts at a stand, the first movement-area edge within 300 ft straight behind it is exempt for the whole plan (the 300 ft is a judgement call). The push clearance covers pushing onto that taxiway and pulling back off it. Without the exemption, B12 → `@B13` (tail onto Y, 186 ft behind the B gates) and `PUSH A` across Y were both refused.

**Stand pushes take no facing (user, 2026-09-16: "PUSH @gate should not accept FACE").** `PUSH @stand FACE …` is refused, as is a PUSHM whose last target is a stand and which carries a final facing, and a mid-push facing change on a stand pushback. Such a facing would leave the aircraft crossways in the stand: `@B13 FACE S` was accepted and ended 91° off the stand's heading.

A refusal names the leg and the pavement, for example "Unable, the move to spot 6A would put the aircraft on
taxiway A". The 2,000 ft UI sanity bound per goal stays as it is. The PUSHM "needs two points" refusal moves
to `TryPushbackMulti`; the client preview already gates on it.

### 4. `PushbackPhase` flies one `TugMove`
- **Shape.** The move is init data. The phase keeps its class and its name, "Pushback" (client menu gates key on that string).
- **Movement.** Each tick, `TugKinematics` supplies the heading.
  - **Push:** `Ground.PushbackTrueHeading` is the direction of travel and `TrueHeading` its reciprocal. It stays set for the whole move, dwell included.
  - **Pull:** `PushbackTrueHeading` is null and `TrueHeading` is the direction of travel.
  - Every reader keeps its "set ⇒ tail-first" assumption.
- **Speed.** `TargetSpeed` = 5 kt, or 3 kt for `Creep`. While the aircraft is turning, it is capped at `speed × R / (R + half-span)`, so the wingtip stays at walking pace (judgement).
- **Dwell.** 5 s at 0 kt, counted from zero groundspeed (judgement; AC 00-65A §11.17 states no duration), then start. No rotation happens during the dwell.
- **Removed:** the alignment stage, `AlignmentThresholdDeg`, `NoseRotationProgressThreshold`, the pull-forward sub-leg (now its own move), and `CategoryPerformance.PushbackTurnRate`. `PushbackSpeed` and `PushbackAlignSpeed` stay, relabelled as judgement calls.
- **`HasLeftTheStand`.** True for any move except the stand push-off, which gets a `StartsAtStand` flag. On that move it keeps today's half-fuselage test.
- **`TryGetPushLegEnd`.** Returns the move's planned end, `PlannedEndLat/Lon` from the planner's simulation.
- **#167 amendment.** Accepted only while the stand push-off is running, which is before any turn starts; otherwise "Unable, pushback turn in progress", which keeps #167's "until the nose has begun rotating".
  - **How (D3 decision):** the push-off carries a `TugAmendment`: the single goal of a one-goal `PUSH` (`Facing`, `Spot` or `TaxiwayLine`; not `Stand`, which takes no facing) and the stand start pose.
  - The amendment re-plans that goal with the new facing **from the stand start pose**, so the new plan's first move is the same push-off. It then replaces every phase after the push-off.
  - `PUSH Y FACE N` → `PUSH FACE S` therefore still works. The #167 tests used `PUSH @B13 FACE N`, which put the aircraft crossways in a stand; they moved to `PUSH Y FACE N` in D4.
  - `PUSHM` carries no amendment.
- **No layout (D3 decision).** `Plan` accepts a null layout for `Clear` and `Facing` goals only, and skips the flown-path check. A bare `PUSH` and `PUSH FACE` keep working at an airport without a ground map, as today.
- **`PUSHM #node` (D3 decision).** Resolved by node type: a parking or helipad node is a `Stand`, a spot node is a `Spot`, anything else is a `Node`. This keeps `3318a75f`'s terminus behaviour for `#` targets.
- **`PUSH <taxiway> <facing-taxiway>` (D3 decision).** When the facing taxiway can't be found, the command is refused instead of silently pushing with no facing. When the taxiway is found but has no edge to align along, the raw bearing to it is used.
- **Magnetic vs true (found in D3 prep).** `TryPushback` fed `PUSH FACE`'s magnetic heading to the phase as a true heading, about 13° off at SFO. `TryPushbackMulti` already converted. D3 converts every facing, test first.
- **Snapshot.**
  - `PushbackPhaseDto` carries the move (shape, kind, flags, line point, line travel, stop point, straight distance, facing, planned end) and the progress (start, distance travelled, captured, dwell elapsed).
  - The pre-rework fields stay as nullable `Legacy*` properties, read only by `FromSnapshot`, to build an equivalent move: target + heading → `ViaLine` to the target; heading only → `TurnTo`; neither → `Straight` for the clearance still owed. A pending pull-forward is dropped, with a Warning log, and the aircraft stops at the staging point.
  - v26 is not on main yet, so its `V25→V26` comment is reworded to cover the new shape; no v27.

### 5. `GroundCommandHandler` — every PUSH form through the planner
- `TryPushback`, `TryPushbackToSpot` and `TryPushbackMulti` resolve their goals, call `TugMovePlanner.Plan`, and hand the plan to one `InstallTugMove`. It installs one `PushbackPhase` per move, then the terminus phase: a stand parks, a spot or node holds (as landed in `3318a75f`).
- Command acceptance, canonical text, readbacks and phase-acceptance rules do not change.
- `TryPushbackToSpot` / `BuildTugLegPhase` / `ResolveSpotPushbackTargets` fold into the goal resolution.

### 6. Client preview
- `GroundViewModel.RefreshPushRoutePreview` passes `_drawAircraft.AircraftType`. `PushRoutePreview` becomes the `TugPlan`, and `PushRouteStart` is deleted: the path carries its own start.
- `GroundRenderer.DrawPushLegs` draws each move's sampled path as a polyline in push/pull colour, with an arrowhead at the move's end and a small dot at each reversal point.

## Dispatches (implementer; one worktree, sequential; commit each when green)

Every brief uses absolute `$WT` paths and `tools/gate.sh`, and names its proving tests. The tests use the real
SFO and OAK layouts through `SfoGroundHarness` / `TestAirportGroundData`.

1. **D1 — `TugKinematics`.** Scope: §1 plus `TugKinematicsTests`.
   - Curvature never exceeds 1/R.
   - Zero distance means zero rotation.
   - `ViaLine` capture from 0°, 45° and 90° off, and from 70 ft abeam the SFO T6B line, converges with under 3 ft of overshoot.
   - A B77W radius exceeds a B738's; a missing wheelbase falls back.
   - `Simulate` reports unflyable when the budget is exceeded.

   Proving: `--filter-class "*TugKinematicsTests"`.
2. **D2 — `TugMovePlanner`.** Scope: §2–§3, the rename, and `PushbackLegPlannerTests` → `TugMovePlannerTests`, re-judged.
   - D15 `$6A` gives Push, Push, Pull.
   - D15 `$6A $6B` gives Push, Push, Pull, Push, Pull.
   - `$6A @D16` ends in a pull onto D16.
   - The fuselage never touches taxiway A on either D15 case.
   - A runway / hold-short / taxiway refusal, each on a real layout.
   - `PUSH Y A1` at SFO ends on Y's centreline lined up.
   - A tight-radius `Facing` change over 135°.
   - Every same-kind joint changes direction by at most 90°.

   The planner compiles alongside the old handler code, which is switched over in D3.
   Proving: `--filter-class "*TugMovePlannerTests"`.
3. **D3 — phase + handler + snapshot.** Scope: §4–§5.
   - New assertions in `SfoPushRouteE2ETests` and `PushbackPullLegTests`, measured per tick:
     - no heading change at 0 kt;
     - on a push, |nose − reciprocal(travel)| ≤ 1°; on a pull, |nose − travel| ≤ 1°;
     - every kind change has ≥ 5 s at 0 kt before it;
     - the final spot pose is within 3 ft and 1°.
   - A dispatcher-level #167 amendment test, which closes the explorer's gap.
   - A legacy-snapshot restore test.
   - Delete-and-rewrite the pivot-asserting `GroundPhaseTests` cases (`*_RotatesBeforePushing`, `*_NoMovementDuringAlignment`, `*_AlreadyAligned_SkipsRotation`, `PushbackTurnRate_*`, the six `FaceUpdate` cases) to the new contract.

   Proving: those classes, then `GroundPhaseTests`.
4. **D4 — suites + recordings.**
   - Re-judge `Issue233SfoPushToSpotTests`, `SfoPushbackTests`, `SfoSimultaneousAlleyPushTests`, `PushToSpotLineupTests`, `SfoSixAlley*`, `SfoYankeePush*`, `OakSimplePushThenTaxiApproachTests`, `Issue161PushFaceThenTaxiStartNodeTests`, `WarpCommandTests` and `AirportE2ETests`. Change an expectation only when the new motion explains it; report each change.
   - Report every replay that desyncs, **without deleting it**: the user decides.
   - Proving: `pwsh tools/test-all.ps1`.
5. **D5 — client.** Scope: §6, with the `GroundViewModelPushRouteTests` updates (the preview equals `Plan` for the same inputs, and the path starts at the aircraft).
   Proving: `tests/Yaat.Client.Tests` plus `GroundMovementMenuTests`.

D1 goes alone, because D2 is built on its API. D2 goes alone because it is the risky one. D3 and D4 may be bundled if D3 comes back short.

### 7. Step 3e pulled forward: parked-neighbour clearance by outline (user, 2026-09-16)
A bare `PUSH TE` from OAK gate 25 now goes straight back. The conflict detector held it until BREAK against the aircraft parked at gate 26: its two-half-span rule wants 142 ft and finds 112 ft along the push. This is the #222 regression.

The fix replaces that rule with outline geometry, for any `PushbackPhase` mover (push or pull) against a parked or held neighbour.
- **Outline:** a cross made of the fuselage segment, the wing segment at the reference point and a tailplane segment. On a pull, the fuselage is extended about 30 ft forward for the tug (judgement).
- **Path:** the outline is swept along the mover's remaining planned path (the current move, simulated from the live pose).
- **Test:** the neighbour is passable if the clearance never drops below the 25 ft `WingtipBufferFt`. If the two already started closer than that, as neighbouring stands do, it is passable if the clearance never drops below its starting value.
- **Otherwise:** the existing graduated closing logic applies.

The reviewer's swept-ring and tug-length notes (see pushback-route.md step 3e) are the reference for this work.

## Remaining steps (picked up 2026-09-16; D1–D4 landed, the switch-over is uncommitted in the tree)

- [x] **Bare `PUSH <twy>` straight-back rule (a)** — landed in the tree 2026-09-16 (`TugPlanBuilder.AcrossAngleDeg`; rule (b) also bounds the nearest point's straight-line distance by `MaxGoalDistanceFt`, since SFO B2 `PUSH H` found an H edge 2,190 ft off to the side "alongside"; proving set 98/98, #222 E2E completes at t=50 s with no hold, recording twin in sync). Original text: one 45° split decides across vs alongside. A `<twy>` edge the push ray crosses at an acute angle ≥ 45° is *across* the push: straight back to it (a). Shallower is *alongside*: rule (b)'s window widens from 30° to the same 45° and the S-curve onto its centreline applies; else refuse (c). No distance window on (a) beyond the existing 2,000 ft search range — a 300 ft bound was tried and refused SFO B12 `PUSH A` (417 ft back, 76°, user-approved), while OAK gate 25 `PUSH TE` (#222) crosses TE at 645 ft and **31.5°**, so the angle alone separates the two real cases. OAK gate 25 `PUSH TE` planned one long straight push down the stand row. Pin it with a planner test (push-off, capture onto TE, ≤ 1 ft off a straight TE edge, nose ≤ 1° off its direction) and the replay-free `Issue222…PushTe_FromGate25_…` E2E. Proving: `--filter-class "*Issue222*" --filter-class "*TugMovePlannerTests" --filter-class "*SfoYankeePush*" --filter-class "*Issue172*" --filter-class "*AirportE2ETests"`. **Measured 2026-09-16:** the E2E is held at t=15 s by the 25 ft wingtip floor (24.1 ft found, 24.5 ft floor, 25 ft ahead), not by the 0 ft collision further along. If it still holds near 24 ft after the planner change, the parked-neighbour buffer goes to `aviation-sim-expert` (the 25 ft `WingtipBufferFt` was sized for two movers abeam) — not tuned here.
- [x] **Path-check start window is per plan, not per goal** — landed in the tree 2026-09-16 (proving set 120/120; `PushmThroughATaxiwayNode_…` and `PushmAlongATaxiway_StillRefused` pin both sides). Original text: `TugPathCheck.IsNearStartOrEnd` measures the "pavement the move starts on" window from the plan origin, so goal 2+ of a `PUSHM` that starts on movement-area pavement is refused. Pin with a `PUSHM #<taxiway node> $spot` planner test. **Fix shape (orchestrator, 2026-09-16):** the 25 ft distance window is replaced by contiguous runs per goal — the edges the fuselage crosses at the goal's start pose stay exempt only for the unbroken run of samples from the start over which the fuselage still crosses them, and likewise the edges crossed at the goal's end pose for the unbroken run back from the end. A 25 ft window never fit a 129 ft fuselage leaving a taxiway it stood across, and "leaving" and "arriving" are runs, not distances. **Found while building it:** SFO graphs taxiway A as a chain of collinear same-name stubs (the one node 51 stands across is 5 ft long), so the exempt set cannot be edge identity. It is the edges crossed at the goal's start (or end) pose, extended through shared nodes along the same movement-area name for at most one fuselage length of chain — the aircraft's own length, not a constant: an aircraft leaving a taxiway clears its centreline within about that much travel along it, so a fuselage still across the taxiway further along is transiting. `PushmAlongATaxiway_StillRefused` guards the over-exemption. `MovementAreaEndWindowFt` stays: `TugPavementClassifier.TransitedMovementAreaName` reads it.
- [x] **Clearance floor that never allows contact** — the detector half landed in the tree 2026-09-16 (proving set 137/137 + 1 skip; full solution 14,061 green with only #224 red; pull stops at 28.5 ft with the tug, push at 30.4 ft, the "starts 15 ft apart" push completes unheld). The command-time refusal landed too (`GroundCommandHandler.OverlapRefusal`: any start clearance under the detector's 0.5 ft slack against a parked or held aircraft refuses `PUSH`/`PUSHM` naming both; `TryPushback`/`TryPushbackMulti` take the dispatch context's aircraft list as a required argument; proving set 207/207). Original text, in `GroundConflictDetector.TugMoveClearsParked` (now `TugMoveFoulsParkedAt`): today `min(25, now) − 0.5` goes negative once the outlines touch, so a mover already touching passes anything. Passable requires `min over samples ≥ min(WingtipBufferFt, clearanceNow) − 0.5 ft` with the floor never below 0.5 ft. **A tug move whose outline already crosses a parked neighbour's at the start is refused by the handler, naming both aircraft** (user 2026-09-16: nothing moves through a modelled collision; the RPO fixes the scenario or uses BREAK). Fixes `TugMoveParkedNeighbourTests.PullIntoAircraftParkedOnItsPath_…` and `GroundConflictDetectorTests.PushingTowardParkedNeighbor_TooClose_StillStops`.
- [x] **A tow stops by the outline sweep, not the nose** (user 2026-09-16) — landed with the floor above (`TugMoveLimit`, `TugMoveStopMarginFt`; reason string `outline stop`). Original text: `TugMoveClearsParked` returns the along-path distance to the first sample under the floor, and the tug move is stopped a margin short of it, replacing the nose-based `GetSeparation` stop (25 ft nose-to-tail, inside a pull's 30 ft tug lead) for tug moves against parked or held neighbours. Pin with a test measuring the final gap on a pull. This also covers the "starts inside 25 ft" case: park a B738 about 15 ft off a real stand's outline, off the push path, and push away. **Found while building it (2026-09-16):** a floor measured at the live pose ratchets — once inside the buffer the floor is always "now − 0.5", so the fouled sample is always a foot ahead and the mover creeps to contact at ~0.5 ft/s; and a stop margin measured at the live speed is zero at rest (physics clamps IAS to the limit instantly), so the stop releases every tick. Fixes: the floor is anchored to the **move's start pose** (`TugMoveProgress.Start`/`StartTravelTrueDeg`, already snapshotted), and the margin is the stopping distance from the move's **commanded** speed (`PushbackSpeed`, ≈ 6 ft at 5 kt), constant for the move. The detector runs every physics sub-tick (0.25 s). **Second finding:** a pull stopped by a limit had its `TargetSpeed` nulled by `FlightPhysics` on reaching 0, so `GroundConflictDetector.Classify` called it `Stationary`, it left conflict resolution, and it inched forward ~0.2 ft/s (a push is immune because `PushbackTrueHeading` keeps it `Pushing`). An active `PushbackPhase` is now never `Stationary`, gated beside the #407/#409 rules.
- [ ] **Re-judge the remaining `GroundConflictDetectorTests` pushback-vs-parked cases** against the outline rule; report old → new per change, never loosen.
- [ ] **#222 recording** (`Pushback_CompletesWithoutBreak_PastParkedNeighbor`): if it fails while the replay-free test passes, prove the desync and delete only that method.
- [x] **#224** — rewritten 2026-09-16 to restore the t=575 snapshot (SWA863 restores 0.5 ft from its recorded pose; `minGap=142 ft, leadTravelled=120 ft`, green). **Verdict — replay premise change, not a regression.** `engine.Replay(recording, t)` re-simulates from t=0 (no snapshot restore), so SWA863's recorded `PUSH TE` from gate 32 at t=478 is flown by the new motion: the alongside S-curve carried it 364 ft down TE past the merge node, nose 45° pointing back up TE, so the pair classified `Crossing` and the callsign tie-break pinned it; at HEAD the old push ends 1 ft from the recorded pose and the test passes. Two decisions (user): the alongside rule stops at capture when the capture point is already on the taxiway's extent (below), and the test is rewritten to **restore the t=575 snapshot** so the recorded poses are its premise whatever the push motion does.
- [x] **Alongside push stops at capture when on the taxiway** — landed 2026-09-16 (`TugPlanBuilder.OnTaxiwayCorridorFt`; gate 32 372 ft, ending 8.7 ft off the near TE piece with the nose along the piece it captured; proving set 121/121). (user 2026-09-16): rule (b) tries the floating capture first and accepts it when the end lies within a 25 ft pavement corridor of any straight `<twy>` edge's extent (`OnTaxiwayCorridorFt`, half an ADG-III taxiway's 50 ft width); otherwise it carries on to the nearest point as before (issue #172's M4 case, where the capture completes 200 ft short of M4). **Measured while building it:** a test on one edge's *line* (3 ft) never fires — TE bends at node 245 beside OAK gate 32, so the S-curve captures the far piece's line and ends 8.7 ft off the near piece, inside its extent; the gate-32 push is 372 ft at capture against 430 ft carrying on to the bend, and the recorded old push was ~325 ft.
- [ ] **Full green**: `dotnet test --project tests/Yaat.Sim.Tests`, then `pwsh tools/test-all.ps1`.
- [ ] **Commit** the switch-over (ask first). Changelog bullets: rolling tug moves with no pivot or slide; the 5 s pause at each reversal; spots ending nose-out via push-past-and-pull-forward; `PUSH FACE` facings now true, not magnetic; `PUSH @gate` refusing a facing; a bare `PUSH <twy>` reaching the taxiway; refusals for paths onto runways, holding positions and un-named taxiways; the parked-neighbour outline rule.
- [x] **D5** — landed 2026-09-16: the preview is the `TugPlan` (`GroundViewModel.RefreshPushRoutePreview` → `TugMovePlanner.Plan`, tokens through the now-public `GroundCommandHandler.ResolveTugGoal`), drawn as per-move polylines with an arrowhead and a reversal dot; `PushRouteStart`, `PushbackLegPlanner`, `PushbackTarget`, `PushbackLeg` and `PushbackLegPlannerTests` deleted. Client 1,578/1,578, `GroundMovementMenuTests` 69/69.
- [x] **Docs** — written 2026-09-16: `docs/ground/pushback.md` rewritten; `COMMANDS.md` (both tables, the `PUSH`/`PUSHM` prose — `PUSHM` had never been documented), `docs/command-cheatsheet.json` + regenerated HTML, `docs/conflict-and-visual-detection.md`, `docs/ground-rendering.md`, `docs/architecture.md`, `USER_GUIDE.md`, `CHANGELOG.md`, `docs/ground/README.md`.
- [x] **Aviation re-review** — done 2026-09-16 on measured traces: nothing blocking; all four original blocks resolved (B738 4.13°/s falls out of wheelbase and span; no rotation at rest; loops killed by the wander rule and the budget; every candidate path-checked, D15 173 ft clear of taxiway A wingtips included). Doc corrections applied (the D15 plan is a T3 turn-then-pull, not a staged push; the 180° tie is by numbering; the wingtip cap has a 25 % threshold; the tug lead is measured from the nose tip). Folded in: deleting `PushbackToSpotPhase` and `PushbackTurnRate` (user). Queued in MAIN.md: planner blind to parked aircraft, span-scaled tug buffer, light-category tow speeds, gentle stop, type-blind tight angle, the 120° wander bound.
- [ ] **Close-out** — as below.

## After the code
- **Aviation re-review** of the *built* result, with measured traces:
  - D15 `PUSH $6A` and `PUSHM $6A $6B`;
  - `PUSH Y A1`;
  - `PUSH FACE` at 90° and 180° from a stand;
  - B738 vs B77W radius and sweep.
- **Docs** (mine):
  - `docs/ground/pushback.md` is rewritten around moves, reversal points and the flown-path check, and its ≈1.3× drift is fixed.
  - `COMMANDS.md` and `USER_GUIDE.md` get the `PUSH <twy> <facing>` floating stop and the `PUSH FACE` tight turn.
  - `docs/ground-rendering.md` gets the path overlay.
  - `docs/architecture.md`.
  - `CHANGELOG.md`, one bullet per behaviour change.
- **Subplan** (`docs/plans/pushback-route.md`):
  - step 3e gains the reviewer's swept-ring geometry, the tug's own length, and the "request a tighter R in a corridor" note;
  - 3f and 3g stay queued behind this.

## Verification
- **Targeted suites** per dispatch, as above, then `pwsh tools/test-all.ps1` (both repos).
- **Build** clean: `tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true`.
- **Live traces:** the D15 E2E with `--show-live-output on` shows no heading change at 0 kt, no crab, a ≥5 s dwell at each reversal, a path clear of taxiway A, and a final pose within tolerance.
- **By eye:** `layout-inspect` animation (`--ticks --html`) of the D15 PUSHM trajectory, before the aviation re-review.
