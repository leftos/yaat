# Ground-view push route (`PUSHM`) + two adjacent ground fixes

## Context

An RPO driving SFO ground wants to say "push to spot 5A" by pointing at the map, and to
choreograph a **multi-leg tug move** (push out of the gate, then pull forward down the alley to a
spot) the same way. Today the ground view can only express a *single* push target, and the
underlying `PUSH` grammar takes exactly one destination.

The attached bundle (`S1-SFO-2 | Ground Control 28/01`, 1833 s, SFO, ZOA) shows the operator
hitting both walls in the last five minutes of the session:

- `SKW3396` t=1174 bare `PUSH` → now in `HoldingAfterPushback`, so the **"Push to …" menu item
  disappears** (it is gated on `CurrentPhase == "At Parking"`). The operator falls back to
  `TAXIAUTO @SPT5A`, `TAXI T5A`, `TAXI $5A`, then `HOLD`.
- `DAL721` t=1719/1729 `WARPG #978` → `PUSH $5A` → `WARPG @D3` → `PUSH $5A` — probing the
  single-target push by hand.

Three pieces of work come out of it, all confirmed against the code:

1. **`PUSHM` — a multi-target tug move**, plus a "Push route…" draw mode on the ground view that
   previews each leg colored push (reverse) vs pull (forward). *The new feature.*
2. **"Push back, face \<taxiway\>" has been dead since Apr 2026** — the client still sends a bare
   numeric heading the parser rejects. *Separate commit, lands first.*
3. **`TAXI $5A` takes a 983 ft loop** out to the alley mouth and back for a 528 ft move. *Two
   blockers, both fixed this session.*

---

## What already exists — do not rebuild it

| Thing | Where |
|---|---|
| Right-click → context-menu plumbing, right-click-vs-right-drag | `GroundCanvas.HandleRightClick` (`Views/Ground/GroundCanvas.cs:1297`), `Views/Map/RightClickGesture.cs` |
| Node right-click menu, incl. a single **"Push to \<spot\>"** item | `Views/Ground/GroundView.axaml.cs:394`, item at `:430-437` |
| **"Push back to…"** nearest-30 submenu on the aircraft menu | `GroundView.axaml.cs:939` (`BuildPushbackToSpotSubmenu`), added at `:699` |
| Multi-waypoint **draw mode** (start / add / undo / finish, live overlay) | `ViewModels/GroundViewModel.cs:1880-2015`; canvas events `DrawNodeClicked` / `DrawNodeFinished` / `DrawNodeHovered` |
| Targeted pursuit-arc reverse + the **pull-forward second leg** | `Phases/Ground/PushbackPhase.cs` (`TickTargetedPushback` `:331`, `TickPullForward` `:398`) |
| Spot geometry: nose-out heading, half-fuselage setback, staging point | `GroundCommandHandler.ResolveSpotPushbackTargets` (`:1933`), `AirportGroundLayout.TryGetSpotOutboundHeading` (`:2530`) |
| Command send path from any ground menu item | `GroundViewModel.SendRawCommandAsync` → `MainViewModel.SendCommandForViewAsync` → `ServerConnection.SendCommandAsync` |

Read [`docs/ground/pushback.md`](docs/ground/pushback.md) and
[`docs/ground-rendering.md`](docs/ground-rendering.md) before touching either side.

---

## Part 1 — `PUSHM` in Yaat.Sim

### Grammar

```
PUSHM <target> <target> [<target> …] [FACE <cardinal> | TAIL <cardinal> | <cardinal-arrow>]
```

`<target>` is `$spot`, `@parking` (incl. helipad) or `#nodeId` — the same token set `WARPG`
takes. Minimum **two** targets (one target is plain `PUSH`; refuse with a message that says so).
The optional trailing orientation applies to the **final rest pose only**.

Examples: `PUSHM $6A $1` · `PUSHM #1926 $5A` · `PUSHM @D5 #503 $5A FACE E`

Rejections (each with an actionable message):
- fewer than two targets;
- a token with no sigil (guard against the existing `PUSH TE @B27` class of bug — MAIN.md backlog);
- a name that does not resolve (`$` → `FindSpotNodeByName` only, `@` → helipad ?? parking, `#` → node id);
- **a leg that crosses runway pavement, or passes through or terminates inside a runway
  holding-position boundary.** A free-space pushback bypasses the hold-short machinery entirely
  (`docs/ground/pushback.md:51`), so "crosses pavement" alone would still let a `#node` target park a
  towed aircraft inside a holding position. Cite **AIM 4-3-18.a.5** (*"A clearance must be obtained
  prior to crossing any runway"*) and **AC 00-65A §11.13** (*"At no time should any aircraft be towed
  on, or across, runways or taxiways without advance approval of the control tower"*), plus §3-7-4 /
  §3-7-5 / §3-7-6 for the holding-position extension. **Do not** cite 7110.65 §3-7-2.a.3 (that is the
  controller's *issuing* duty, not the prohibition) and **do not** cite §3-11-1.c (that is helicopter
  AIR-TAXI phraseology and belongs only to the open `ATXI` item);
- **a leg that transits *along* movement-area pavement.** A leg may *reach* the ramp/movement
  boundary — that is exactly what `PUSH <taxiway>` and `PUSH $spot` already do — but may not run down
  a movement-area taxiway to reach the next target. Classify with the existing
  `RampLaneReposition.IsRampTaxilane` / spot-node boundary. *(This replaces the 1,000 ft cap from the
  first draft, which had no basis and would have refused a legitimate SFO alley transit.)*

A movement-area `#node` **target** is allowed — a tug move onto a taxiway is real and routinely
coordinated (AC 00-65A §11.13, §11.6/§11.7), and the RPO issuing the command *is* the controller, the
same posture `TAXI` already takes.

Optional numeric backstop against a mis-click in the draw overlay: refuse past **2,000 ft** per leg,
labelled in both the code comment and the refusal message as a **UI sanity guard, not an aviation
rule** (SFO's six-alley is ~1,800 ft end to end; a 1,000 ft cap would refuse a real transit).

### The push-vs-pull rule — one body, both sides call it

`src/Yaat.Sim/Data/Airport/PushbackLegPlanner.cs`, pure and layout-only — it sits beside
`RampLaneReposition` / `TaxiApproachLeg`, **not** under `Phases/Ground/`, because
`GroundPhaseConvention.EveryGroundMotionPhase_ReferencesIsImmobile` requires every file in that folder
to carry an `IsImmobile` guard and this one has no `OnTick` at all:

```csharp
public static IReadOnlyList<PushbackLeg> Plan(
    AirportGroundLayout layout, LatLon start, double startTrueHeadingDeg,
    IReadOnlyList<PushbackTarget> targets, AircraftCategory category, out string? refusal);

public readonly record struct PushbackLeg(PushbackLegKind Kind, LatLon Target, int? EndTrueHeadingDeg);
public enum PushbackLegKind { Push, Pull }
```

Per leg, from the pose at that leg's start:
`|normalize(bearingToTarget − noseHeading)| > 90°` → **`Push`** (tail-first reverse), else **`Pull`**
(nose-first forward). Leg *N+1* starts from leg *N*'s target and end heading. The discriminator
itself is sound: a towbar tug and a TLTV both connect at the nose gear and do not reposition between
legs — they select forward or reverse gear. (An earlier citation of AC 00-65A Appendix A items 20/22 here was wrong — those items are hook-up checks, not a mid-tow gear change.) **No dead band** — the
binding physical limit is nose-gear steering angle, not heading delta, and a ~90° tug reposition is
routine.

**Leg 0 depends on where the aircraft is standing** (corrected 2026-09-15 after a measurement run; the
first draft said "leg 0 is always a `Push`"). `Plan` therefore takes a required `bool startsAtStand`:

- **`startsAtStand: true`** — leg 0 is forced `Push`, because an aircraft nose-in on a stand cannot be
  towed forward off it. **And the command is refused** when the first target lies within 90° of the
  nose: that asks the tug to reverse away from something ahead. Gate D5 → `$6A` is exactly this —
  `$6A` measures **1.6° off D5's nose** — and the first draft would have planned a 1,210 ft reverse
  away from it, which `PushbackPhase` could only realise by pivoting 180° in place.
- **`startsAtStand: false`** — leg 0 takes the ordinary geometric rule. This is the case the bug report
  hits: SKW3396 did a bare `PUSH`, ended in the alley pointing along it, then wanted spot `5A` ahead of
  it. That leg is a `Pull`.

**The movement-area transit test must read every name on an edge, not the first.**
`RampLaneReposition.FirstNamedTaxiway` returns the first non-RAMP name, so SFO's shared `M1/Y` arc
classifies as `M1` (a ramp lane) and taxiway `Y` — movement area — becomes invisible to the rule. Use
`EdgeNames` and treat the edge as movement area when **any** name is neither `RAMP` nor
`IsRampTaxilane`.

**The runway refusal names whichever runway it found.** At SFO no named node pair under 2,000 ft crosses
28L/10R without also crossing 28R/10L, so a fixed runway in the message would be wrong.

**Test data comes from the documented technique, not invented gates.**
`docs/plans/sfo-ground-technique-tests.md` A4/A5 record the real ZOA ground-video pairing as **D15
`PUSH $6A`** / **D15 `PUSH $6B`** (shipped as `SfoSixAlleyChoreographyTests`), so the multi-leg happy
path is **D15 → `$6A` → `$6B`**. The first draft's "D5 → `$6A` → `$1`" was invented and is refused by
the 2,000 ft guard — `$6A` → `$1` measures **3,918 ft** (spot 1 is at the far south end of the field).

**But the intermediate end headings are chosen, not derived** (aviation review). AC 00-65A **§11.10**:
*"The CH should use a tail walker during towing operations when the tow vehicle operator turns the
aircraft sharply or backs into a parking position. **Backing of aircraft should be avoided as much as
possible.**"* On a real ramp the crew steers the push so the nose ends pointed at the *next* target,
making the following leg a pull. So `Plan` picks each non-terminal leg's end heading to make the next
leg a `Pull` **whenever the required turn is achievable during that leg** (arc rate
`CategoryPerformance.PushbackTurnRate`, 5°/s, over the leg's length at `PushbackSpeed`); only leg 1
out of a stand is forced to be a `Push`, because a nose-in aircraft has no room to turn around. Where
the turn is not achievable in one leg, fall back to the geometric rule and let the leg be a `Push`.

The **final** leg keeps today's terminus behaviour: a `$spot` terminus reuses
`ResolveSpotPushbackTargets` (reverse past the mark, pull forward onto it, nose-out) so the nosewheel
lands on the paint; `@parking` uses the node's `TrueHeading`; `#node` stops on the arrival heading.
An explicit trailing `FACE`/`TAIL` overrides the terminal facing.

The client calls the same `Plan` for its preview colors — the same posture as
`GroundViewModel.ResolveRemainingRoute` re-running `TaxiPathfinder`, not a second client-side rule.

### Execution — queue N `PushbackPhase`s, do not invent a new phase

`GroundCommandHandler.TryPushbackMulti` builds a `PhaseList` of one `PushbackPhase` per leg, then the
existing terminal (`AtParkingPhase` / `HoldingAfterPushbackPhase`). Each leg re-derives its own kind
from the **live** pose at `OnStart` rather than trusting the plan-time guess, so a nudge or a restore
cannot desync it.

`PushbackPhase` changes:
- new `public PushbackLegKind Kind { get; init; }` (default `Push` — pre-existing snapshots restore
  unchanged);
- `OnStart` for a `Pull` leg aligns the **nose toward** the target instead of away from it, leaves
  `Ground.PushbackTrueHeading` unset, and drives at the transit pull speed (step 3d);
- factor the pursuit arc shared by both directions out of `TickTargetedPushback`. **Leave
  `TickPullForward` alone** — it is the terminal nosewheel-onto-the-mark creep with the nose held,
  and generalizing it would regress `PUSH $spot`.
- **BLOCKER — the alignment stage pivots in place and must not.** `PushbackPhase.cs:257-262` sets
  `ctx.Targets.TargetSpeed = 0`, clears `Ground.PushbackTrueHeading`, and calls `TurnNoseToward` — it
  rotates the aircraft about its centroid at zero groundspeed. A tug cannot do that; nose-gear
  steering only curves a *rolling* path. Today the branch is nearly dead (gate pushes start inside the
  20° `AlignmentThresholdDeg`), but under `PUSHM` **every leg transition enters it** and a 90° turn
  would spin the aircraft on the spot for ~18 s. Either make alignment a rolling arc in the previous
  leg's direction of travel, or rely on the §11.10 facing choice above to guarantee tangential leg
  joins so alignment never triggers. Verified in the current source, 2026-09-15.
- `HasLeftTheStand` returns true once `_reachedTarget || _pullingForward` — see step 3g, which gates
  that conflict priority on leg 1 of a dispatch push so a relocation tow does not outrank taxiing
  traffic for the length of an alley.
- **`CanAcceptCommand` returns `ClearsPhase` for Taxi/TaxiAuto/AirTaxi/Land/Delete (`:486`).** With N
  queued legs, verify `Phases.Clear(ctx)` drops the *remaining* legs and not just the running one —
  otherwise a `TAXI` mid-`PUSHM` leaves leg 3 armed behind the taxi. Needs its own test.

Snapshot: add `Kind` to `PushbackPhaseDto` (`Simulation/Snapshots/PhaseSnapshotDto.cs:428`) and bump
`SnapshotSchemaMigrator.CurrentSchemaVersion` 24 → 25 with a migration that defaults it to `Push`.

### Integration points (17 sites — miss one and it fails silently)

`PushbackMultiCommand` must be added everywhere `PushbackCommand` appears:

- `Commands/ParsedCommand.cs` — the record;
- `Commands/CanonicalCommandType.cs` + `Commands/CommandRegistry.cs:773` — `GroundCommands()`, alias
  `["PUSHM"]`, `CommandDimension.Ground`, repeatable target parameter, and `@`/`$`/`#` modifiers
  **without** `LeadingTokenOnly` so `ArgumentSuggester`'s sigil autocomplete fires on every slot
  (`CommandScheme.Default()` derives from the registry, so nothing else is needed there);
- `Commands/GroundCommandParser.ParsePushback*` — a new `ParsePushbackMulti`;
- `Commands/CommandDispatcher.cs:2430` — a second arm beside `PushbackCommand`;
- `Commands/CommandDescriber.cs` — `:103` canonical-type map, `:766` `FormatPushCanonical`
  counterpart, `:1228` natural form, `:1593` `IsGroundCommand` (**this is what gives it
  `CommandDimension.Ground`; a verb missing here falls to `None` and wipes the command queue** —
  MAIN.md backlog);
- `Simulation/RecordedCommandClassifier.cs:363`;
- `Pilot/PilotRequestTracker.cs:126`;
- `CanAcceptCommand` on `AtParkingPhase` and `HoldingAfterPushbackPhase` (per the answer below).

Canonical text must round-trip: `Describe → parse → Describe` is stable, or the replay router arms
re-derive the wrong state.

### Menu availability

The user's call: offer the push items on **`HoldingAfterPushback`** as well as `At Parking` — the
`SKW3396` case. That applies to the existing single "Push to \<spot\>" item
(`GroundView.axaml.cs:430`), the "Push back to…" submenu, and the new "Push route…".

---

## Part 2 — `PUSHM` in the client: the "Push route…" draw mode

Mirror the existing taxi draw mode rather than duplicating its plumbing: add
`DrawRouteKind { Taxi, Push }` to `GroundViewModel`'s draw state so `IsDrawingRoute`, the canvas
cursor, hover suppression and the `DrawNode*` events keep working unchanged.

| Piece | File |
|---|---|
| `StartPushRoute` / `AddPushWaypoint` / `UndoPushWaypoint` / `FinishPushRoute` → `PUSHM …` text | `ViewModels/GroundViewModel.cs` (beside `:1880-2015`) |
| Preview state: `PushRoutePreview` (`IReadOnlyList<PushbackLeg>`, from `PushbackLegPlanner.Plan`) | same |
| Styled property → **`RenderSnapshot`** (the no-StyledProperty rule) | `Views/Ground/GroundCanvas.cs:52`, `:719`, `:811` |
| `DrawPushLegs(canvas, vp, legs, reversePaint, forwardPaint)` — straight lines + a per-leg arrowhead; legs are free-space so `DrawRoute`'s `TaxiRoute` walk does not apply | `Views/Ground/GroundRenderer.cs`, drawn with the other route overlays |
| `"Push route…"` on the node menu (next to `"Draw taxi route…"`) and on the aircraft menu; right-click commits | `Views/Ground/GroundView.axaml.cs:394`, `:684` |

Waypoints are **not** graph-routed — no `FindRouteToNode`, no `BuildDenseNodeRefPath`. The committed
command carries exactly the clicked targets (`$`/`@` when the node is named, `#id` otherwise).

---

## Part 3 — fix the dead "Push back, face \<taxiway\>" items *(own commit, lands first)*

`GroundViewModel.PushbackHeadingAsync` (`:969`) sends `$"PUSH {heading}"`. `GroundCommandParser`
`:135-138` has rejected a numeric heading since `54218809` (2026-04-25) — every one of those items
fails server-side with *"PUSH no longer accepts numeric headings"*. Nothing tests it, which is why it
went unnoticed.

Fix: snap the edge bearing from `GetPushbackDirections` (`:1194`) to the nearest of the eight compass
points and send `PUSH FACE <cardinal>`. Note that `GeoMath.BearingTo` is **true** and
`MagneticHeading` is magnetic — the 45° buckets absorb the declination, but say so in a comment. Add
the missing client test.

---

## Part 4 — `TAXI $5A`: the 983 ft loop *(fix both blockers)*

**What actually happened.** The command resolved correctly — the snapshot at t=1400 shows
`DestinationSpot: "5A"`, `Description: "T5 T5A $5A"`, terminal node 0 = spot 5A. The *route* is the
problem: D2 (977) → T5 → **node 1 = spot 5** → 1247 → 69 (the T5/Alpha junction) → 1245 → node 0
(spot 5A) — **983 ft** for a 528 ft direct move, out to the alley mouth and back up the adjacent
sub-lane. Spot 5A is 71 ft from spot 5. The operator's own `HOLD` at t=1346 froze the aircraft 34 ft
from spot 5 with `CurrentSegmentIndex: 4`, which is what read as "ended up on spot 5".

Two independent blockers stop the free-space cut that would avoid it:

1. **Name form.** `RampLaneReposition.HasTaxilaneNameForm` (`:369`) accepts *letters then digits
   only*; `"T5A"` returns false on the trailing `A`. So `IsRampTaxilane(layout, "T5A")` and
   `AreSiblingLanes("T5", "T5A")` are both false, and SFO's whole `T5A/T5B/T6A/T6B/T7A/T7B` spot
   sub-lane family can never qualify. → accept `letters + digits + optional trailing letters`, with
   a table test over real OAK/SFO names asserting the existing family (`TE`, `M3`, `M4`) still
   classifies identically and that `A1`, `GL`, `W3` stay excluded by the hold-short rule.
2. **Contract — and F-4's stated rule does not fit this case.** Measurement spike, 2026-09-15
   (corrects the 983 ft above to a measured **998 ft**; straight-line 529 ft; spot 5 → 5A is 71 ft):

   `GroundCommandHandler.cs:270` and `:299` gate **both** cut mechanisms on `route is null`. Here the
   route resolves, so neither is reached. But relaxing that gate alone would still not work: a bare
   `TAXI $5A` has an **empty `Path`** — `GroundCommandParser.ParseTaxiTokens` sets `destSpot` and
   `continue`s without adding to `path`, and `ResolveParkingRoute:1492` takes the `Path.Count == 0`
   branch straight to `TaxiPathfinder.FindRoute` — so `TryPlanDestinationCut`'s
   `path.LastOrDefault(t => !t.StartsWith('#'))` (`RampLaneReposition.cs:180`) yields null and it
   returns immediately. **Both cut mechanisms key off a controller-named lane; an auto-routed taxi
   names none.** All four facts verified in source.

   → The fix is a **third entry point**, `TryPlanResolvedRouteCut`, deriving the lane pair from the
   *resolved route's own segments*, called after a **successful** resolution, with the spike's
   measured predicate:

   ```
   remainingGraphFt / cutFt >= 2.0   AND   remainingGraphFt - cutFt >= 100   AND   cutFt <= MaxCrossingFt
   ```

   Measured rows (cut): D2→`$5A` 5.36 / 310 ft · OAK `V T TE @22` unbounded · B20S→M4 3.75 / 1111 ft ·
   mid-M3→M4 11.62 / 1710 ft. (no cut): OAK `V T TE @23` 1.10 / 19 ft · OAK `V T TC @22` ~1.0 / ~0 ft.
   Clean separation with a 3.4× margin — **but only two genuine negatives exist**, both found by the
   spike, so a synthetic boundary case near the 2.0 threshold is required before shipping.

   **Among qualifying candidates the winner is the one with the smallest crossing, not the shortest
   total route** (decided 2026-09-15 after the first implementation). Minimising `prefix + cut` picked
   the T5 *entry* node and sent the aircraft on a 345 ft freehand diagonal across the apron (625 ft
   total); minimising the crossing picks spot 5 — down the painted lane, then 71 ft across (689 ft
   total). A free-space leg is unmodelled pavement with no graph guidance and is where the real hazard
   sits (AC 00-65A §11.9 stations a wing walker at each wingtip for it; §7's accident history is
   jackknifing and wingtip strikes), so the aircraft stays on the graph as long as it can. This also
   makes the calibration self-consistent — every measured row below was taken at its minimum-cut point.

   Reuse `RankTargets` / `CrossesForeignPavement` for crossability; do not invent a new rule. Per the
   user (2026-09-15): *parallel taxilanes within a certain distance are assumed cuttable* — which is
   already what `CrossesForeignPavement` encodes (its comment records that the GeoJSON carries no
   pavement markings, so `family ∪ RAMP` is the proxy). The SFO geojson has **272 Points, 81
   LineStrings and zero polygons**, so buildings and pavement extent are not modelled at all and no
   imagery check is possible from the data.

   **Mirror the trigger in `GroundViewModel.ResolveRemainingRoute`** (`:1554`) so the client overlay
   draws what the aircraft flies (`docs/ground-rendering.md:168`).

   Two cautions from the spike: `GateB20S_TaxiM5` measures **435 ft against the 450 ft
   `MaxCrossingFt` ceiling** — 15 ft of headroom, so any ranking change can regress it; and F-4's own
   "1.03 vs 1.35–1.74" figures are a *bounded local bridge-cost* ratio inside
   `SegmentExpander.TryDetour`, a different axis from these end-to-end route/cut ratios — do not merge
   the two numbers.

   **Still open after this step:** F-4's other half, the connector-detour ranking
   (`SegmentExpander.TryDetour` cost+tail ordering). Untouched here.

Re-judge and update: `RampLaneRepositionTests`, `SfoYankeeConnectorChoiceTests` (F-4's red pin),
`SfoDetourHonorsNamedTaxiwayTests` (must stay green), and the Nightly `PathfinderGrid` sweep.

New pin: `SfoFiveAlleySpotCutTests` — spawn parked at D2, `TAXI $5A`, assert the route is under
~600 ft and ends on node 0, and that it does not contain node 69.

---

## Testing (TDD — failing test first, every item)

Sim (`tests/Yaat.Sim.Tests`), real SFO layout via `SfoGroundHarness` / `TestAirportGroundData`:

- `GroundCommandParserTests` — every accepted `PUSHM` form and every refusal.
- `PushbackLegPlannerTests` — leg kinds for D2 → `$6A` → `$1` and the reverse order; a ±90°
  boundary case; a runway-crossing refusal.
- `SfoPushRouteE2ETests` — `PUSHM $6A $1` from a real gate completes; per-tick assertions that
  `Ground.PushbackTrueHeading` is set on a `Push` leg and unset on a `Pull` leg; final pose is
  nosewheel-on-mark, nose-out; `HOLD` / `RES` mid-sequence resume on the right leg.
- Snapshot round-trip: a mid-sequence `PushbackPhaseDto` restore, and a v24 snapshot migrating to 25.
- `PhaseAcceptanceAuditTests` — a `PushbackMulti` arm for `AtParking` / `HoldingAfterPushback`.
- Canonical round-trip + `RecordedCommandClassifier` coverage.
- Part 4: `SfoFiveAlleySpotCutTests` plus the re-judged ramp-lane tests above.

Client (`tests/Yaat.Client.Tests`): `GroundViewModel` push-route draw tests (add / undo / finish →
exact command text, leg kinds in the preview), and the missing `PushbackHeadingAsync` test.

Gate every run through `tools/gate.sh`:

```bash
tools/gate.sh .tmp/test.log timeout 30 dotnet test -- --filter-class "*PushbackLegPlannerTests"
tools/gate.sh .tmp/build.log dotnet build -p:TreatWarningsAsErrors=true
pwsh tools/test-all.ps1        # cross-repo, before the final commit
```

## Aviation review — done 2026-09-15

Run with the standard local-references preamble. **Headline: 7110.65 and the AIM contain no pushback
or towing procedure or phraseology at all** — both trees were grepped and the silence reported. The
governing FAA document is **AC 00-65A, *Towbar and Towbarless Movement of Aircraft*, Chg 1, 8/11/23**
(not in the local reference set); tow speed bands come from **ISO 20683-1/-2:2016**. Per the review
gate, silence inverts the evidence ranking, so those are the top available authorities here — but
every constant taken from them is labelled below as sourced or as a judgement call.

**Accepted and folded into the plan above:** the rolling-alignment blocker; the §11.10 facing choice;
the movement-area transit rule replacing the invented 1,000 ft cap; the corrected runway citations and
the widening to holding-position boundaries; allowing movement-area `#node` targets; the
`Phases.Clear` test; the phraseology below.

**Accepted as extra scope (user, 2026-09-15):** steps 3d, 3e, 3f, 3g.

**Numbers that are judgement calls, not citations** — label them as such in code and changelog, never
cite a section for them: the **~5 s** inter-leg dwell (AC 00-65A §11.17 says "should not start and stop suddenly"
and states no duration), and the **2,000 ft** UI backstop. The **5 kt** push/pull and **3 kt** terminal creep are also judgement calls: ISO 20683 §3.4 states only a ceiling (≤10 km/h ≈ 5.4 kt), not either value (corrected 2026-09-16). The 8 kt transit pull was dropped: AC 00-65A §11.14, "Towing speed should not exceed that of walking team members."

**Phraseology.** Ramp control issues it when every leg stays in the non-movement area (7110.65 §3-7-2
NOTE 2; PCG *NONMOVEMENT AREAS*); ground control once a leg reaches movement-area pavement (AIM
4-3-18.a.1). The ATC verb is **"proceed"** — never "taxi" (PCG *TAXI*: movement *"under its own
power"*, which a towed aircraft is not) and never "cleared" (§3-7-1.c). **No new readback form**: the
mandatory-readback set for a surface movement is closed — runway assignment, runway-entry clearance,
hold-short/LUAW (AIM 4-3-18.a.9, mirrored at §3-7-2.a.8) — and because runway legs are refused, a
`PUSHM` can never contain one. Extend `CommandDescriber.FormatPushNatural` (`:1795`) to list the legs
in order and keep the trailing facing. The exact wording is real-world ramp convention, **not
published phraseology** — say so in the docs rather than attaching a citation.

**Noted, not acted on.** Widening the menu onto `Holding After Pushback` (your call) lets an RPO chain
relocation tows on a dispatch-ready aircraft; AC 00-65A §11.21 sanctions engines-running tows only for
"pushing aircraft away from terminal gates used by airlines for dispatch". YAAT models no engine start,
so this is not a correctness bug today. A `#node` terminus should default nose-out along the last
leg's travel rather than "arrival heading", for the same §11.10 reason.

**Doc drift found in passing:** `docs/ground/pushback.md:19` says the simple pushback is ≈1.3× aircraft
length; `AircraftCategory.cs:829-830` says ~1× ("B738 (~110 ft) pushes ~110 ft"). Verified. Fix in step 5.

---

## Docs

`COMMANDS.md` (quick-reference row + a detailed `PUSHM` subsection beside the `PUSH` prose at
:760-766) · `docs/command-cheatsheet.json` then `node tools/build-cheatsheet.mjs` (never hand-edit
the HTML) · `docs/ground/pushback.md` (a "Multi-leg tug move" section) · `docs/ground-rendering.md`
(the new overlay row and canvas event) · `docs/architecture.md` · `USER_GUIDE.md` · `CHANGELOG.md`
(one bullet per change, never bundled) · `docs/plans/MAIN.md` — add the item, link a new
`docs/plans/pushback-route.md` subplan, and strike the F-4 backlog line once Part 4 lands. Copy this
plan into the repo with `cp`, do not re-type it.

## Commit sequence

1. `fix:` dead "Push back, face \<taxiway\>" menu items (Part 3) — small, independent.
2. `fix:` ramp-lane name form + prefer-a-plannable-cut contract (Part 4).
3. `feat:` `PUSHM` grammar, planner and phase execution (Part 1).
4. `feat:` ground-view "Push route…" draw mode and overlay (Part 2).
5. `docs:` COMMANDS / cheatsheet / pushback / architecture / USER_GUIDE / CHANGELOG.

Ask before each commit. Each step's gate must be green before the next starts.

## Tasks

- [x] **Step 1** (Part 3) — landed `830ebd42`. — `PUSH FACE <cardinal>` for the "Push back, face \<taxiway\>" items + the missing client test
- [x] **Step 2a** (Part 4) — landed `c5e58780`. — `HasTaxilaneNameForm` accepts `T5A`/`T6B`; classification table test over real OAK/SFO names
- [x] **Step 2b** (Part 4) — landed `9f35a7d0` (objective = smallest crossing; test pinned by a proven-failing assertion). — — prefer-a-plannable-cut contract in `TryTaxi`; mirror the trigger in `GroundViewModel.ResolveRemainingRoute`; re-judge `RampLaneRepositionTests`, `SfoYankeeConnectorChoiceTests`, `SfoDetourHonorsNamedTaxiwayTests`; new `SfoFiveAlleySpotCutTests`
- [x] **Step 3a** (Part 1) — landed `56a6e25d`. — — `PushbackLegPlanner` + `PushbackTarget`/`PushbackLeg`/`PushbackLegKind`, with unit tests
- [x] **Step 3b** (Part 1) — landed `56a6e25d`. — — `PushbackMultiCommand` record, parser, registry, the 17 integration sites, canonical round-trip
- [x] **Step 3c** (Part 1) — landed `263c62d6`. — — `PushbackPhase.Kind`, the shared pursuit arc, `TryPushbackMulti`, `PhaseAcceptanceAuditTests` arm, SFO E2E. The schema bump was missed here; it lands as v25 → v26 after the 2026-09-16 rebase, since main took v25
- [x] **Step 4a** (Part 2) — landed `e6adc14b`. — `GroundViewModel` push-route draw state + `PUSHM` command text, with tests
- [x] **Step 4b** (Part 2) — landed `e6adc14b`; the phase-gate widening had already shipped on main as `AircraftCommandApplicability.CanPushBack` (`6e84a163`), which the rebase adopted. — canvas styled property → `RenderSnapshot`, `GroundRenderer.DrawPushLegs`, "Push route…" menu items, phase gate widened to `Holding After Pushback`
- **Rebased 2026-09-16 onto main `b970b2c2`** (yaat-server fast-forwarded to `3b48f266`). Main's `6e84a163` made a
  stand park an aircraft and a ramp spot hold it, for every writer, so a `PUSHM` ending on a `$spot` now ends in
  `HoldingAfterPushbackPhase` with no parking spot (`3318a75f`; a gate/helipad terminus still parks, a `#node` one
  holds with the parking spot untouched). Snapshot schema is **v26** for `PushbackPhaseDto.Kind` (`2845fe75`),
  since main took v25. The planner's `startsAtStand` now reads false for an aircraft that pushed onto a spot, which
  is the correct answer the old `AtParking`-on-a-spot state got wrong.
- [ ] **Step 3d — tug moves that roll, plan their reversals, and check the path they fly** — see [tug-motion.md](./tug-motion.md) (approved 2026-09-16, dispatches D1–D5). Supersedes the earlier 3d item: the aviation review of the first design (coupled nose + lead-in, facing-decided last leg) found loops toward taxiway A, fixed-rate steering that reinstates the pivot, an unchecked flown path and an impossible widebody turn rate. The 8 kt transit pull is dropped (user, AC 00-65A §11.14)
- [ ] **Step 3e** (aviation review, user 2026-09-15) — **wingtip/tail-swing clearance belongs in `GroundConflictDetector`, not a plan-time sweep**: today it deliberately treats parked and held neighbours as passable (#222), which is right for a 110 ft gate push and backwards for a multi-leg move down a stand row. Teach it the wingtip envelope (half-wingspan either side of the centroid track, plus tail swing on a push leg) against parked footprints. AC 00-65A §11.9. **Input from the 2026-09-16 review:** in a turn the swept area is the ring about the turn centre from max(0, R − half-span) to max(R + half-span, √(R² + tail²), √(R² + nose²)) — a B738 at R = 58 ft sweeps a 117 ft disc, its tail about 30 ft outside the centroid arc; a larger R sweeps more, so 3e should be able to ask the planner for a tighter R in a narrow corridor; during a spot-push dwell the tail sits about fuselage + staging behind the spot (B738 ≈ 227 ft, B77W ≈ 342 ft); and the tug itself leads the nose by ~30–40 ft on a pull (judgement) and swings out beside it on a push
- [ ] **Step 3f** (aviation review, user 2026-09-15) — towbar accel/brake for **every** pushback, not just `PUSHM`: replace the taxi rates (1.0 kt/s up, 5.0 kt/s brake — 5 kt/s is 0.26 g, a shear-pin event through a towbar) with ~0.3 kt/s and ~1 kt/s. AC 00-65A §11.1. This is MAIN.md backlog item (a); expect existing pushback test expectations to move and replay recordings to desync
- [ ] **Step 3g** (aviation review, user 2026-09-15) — gate `PushbackPhase.HasLeftTheStand`'s conflict priority on leg 1 of a dispatch push rather than any pushback that has left the stand, so a relocation tow does not outrank taxiing traffic for the length of an alley. 7110.65 §3-7-1, §3-7-2.a
- [ ] **Step 5** — aviation review (`aviation-sim-expert`), then docs: COMMANDS.md, cheatsheet JSON + regenerate, `docs/ground/pushback.md`, `docs/ground-rendering.md`, `docs/architecture.md`, USER_GUIDE.md, CHANGELOG.md
**Closed by decision (user, 2026-09-15): free-space cuts stay on ramp lanes.** The steer that opened it
was *"parallel taxilanes / **taxiways** within a certain distance should be assumed to be cuttable … T5A
T5 T5B at SFO, **T and A at SFO**, etc."* The T5-family half shipped as steps 2a + 2b. The `T`/`A` half
is **not** being done: those are bare-letter movement-area taxiways, which `IsRampTaxilane` rejects by
design (`letters >= 2 || digits > 0`) and `CrossesForeignPavement` treats as foreign pavement, and the
aviation review's regime split (ramp = ramp control, movement area = ground control, AIM 4-3-18.a.1)
says the two are not interchangeable. Do not re-raise it as a backlog item.

- [ ] **Close-out** — strike the F-4 backlog line in MAIN.md once step 2b lands (its *other* half, the
      `SegmentExpander.TryDetour` connector-detour ranking, stays open); delete this subplan when the
      whole item ships
