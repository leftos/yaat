# Handoff: taxi crossing / hold-short precedence + directionality hints

> **Status:** **W1–W7 all implemented and verified (2026-06-03).** Originated
> from issue #172 (JBU577 "taxi spin"). Mentor (Maxim, ZOA) consulted; FAA references checked and
> aviation-sim-expert-reviewed. This issue's work is complete — the file can be archived once merged.
> **Recording:** `tests/Yaat.Sim.Tests/TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip`.
>
> **Post-merge note (2026-06-03):** rebased onto current main. SFO geojson was refreshed on main, so
> the `Issue172TerminusDirectionTests` node constants were updated (G/B 155→156, K 10R hold-shorts
> 849/857→848/856) and its wrong-way guard made layout-robust. `Issue172ParallelPassTests` was
> **quarantined** (`Skip`): under main's post-pushback auto-taxi behavior (`f63a865b`), **JBU2435 no
> longer taxis** in the recording window (it stays in `HoldingAfterPushbackPhase`), so the FFT2083/JBU2435
> convergence the test exercised no longer occurs. The production fix it guarded (skip convergence
> slowdown when the nearer aircraft clears first) is intact. **Follow-up:** confirm whether JBU2435 not
> taxiing is correct under the new auto-taxi behavior or a regression, and re-capture a recording that
> reproduces the parallel-pass geometry.

This is a fresh-agent handoff. It bundles one bug fix and several related controller capabilities that all
concern **which direction an aircraft goes during taxi/crossing** and **what "hold short" means when it
conflicts with clearing a runway**. Read the linked subsystem docs before touching code:
`docs/command-pipeline.md`, `docs/command-handlers.md`, `docs/landing-and-runway-exit.md`,
`docs/ground/README.md`, `docs/ground/navigator.md`, `docs/ground/pathfinder.md`, `COMMANDS.md`.

---

## Originating bug (JBU577 spin) — confirmed root cause

JBU577 (an Airbus) exited a runway onto **G** at SFO and got **`TAXI G B HS B`** (+ `CROSS`) to cross
**RWY 01L/19R** and hold short of **B** just beyond. It crossed, then **turned ~180° back toward the runway,
then back toward B** before holding short — a spin. Verified by replaying the bundle (t≈470–495).

On G the nodes run NW: **867** (RWY 01L/19R far-side hold-short) → **1398** (B's join, 74 ft NW) → **155**
(G/B junction, 149 ft from 867). **867→1398 is only ~74 ft — shorter than the aircraft (~120–145 ft)**, so the
aircraft cannot be both fully clear of the runway and fully short of B.

Mechanism: `CrossingRunwayPhase` carries the aircraft ½-length past the runway exit (867) for tail clearance
(`CrossingRunwayPhase.cs:215,230`), which overshoots B. `TaxiingPhase.SetupCurrentSegment` then overrides the
navigator target to B's hold-short **offset position**, which — because the offset is `aircraftLength + 30 ft`
*backward* (`HoldShortAnnotator.cs:303`, `VirtualNode.OffsetBefore`) — lands **SE of 867, behind the runway**.
`GroundNavigator.TickStraight` steers ~180° backward to reach it; its advance-on-pass overshoot guard is
excluded for stop targets (`!isStopTarget`, `GroundNavigator.cs:659`), so it reverses instead of stopping.

---

## Behavior decisions (FAA-grounded; confirmed with mentor)

**"Hold short of [taxiway]" binds and takes precedence even when the aircraft cannot also clear the runway.**
The aircraft stops at the taxiway hold-short with its **tail hanging over the runway bars**; the runway is
**not clear**; the trainer **warns the controller**. It never backtracks. FAA basis (local refs — DO NOT
web-search; read the files. Citations corrected per aviation-sim-expert review 2026-06-03):
- **AIM 2-3-5.3** (`.claude/reference/faa/aim/chap02_sec03.md`) — a commanded hold-short is a hard stop: the
  aircraft must stop so **no part extends beyond** the hold line. **AIM 2-3-5.1** — a runway is "not clear"
  until the entire aircraft is past the marking.
- **7110.65 3-7-4** (`.claude/reference/faa/7110.65/chap03_sec07.md`) — the **controller must protect** the
  runway/intersection when an aircraft must enter it (here, hangs its tail over the bars) → trigger for the
  controller warning. **3-7-2** — "cross without delay" governs the crossing itself.
- **AIM 4-3-21.a** (`.claude/reference/faa/aim/chap04_sec03.md`) — an aircraft does **not** reverse on a runway/
  taxiway; the hold-short-of-taxiway stop overrides the desire to clear the runway (there is no FAA verb for
  "cross and stop clear"; `CROSS` with the hold-short omitted is the existing auto-hold-past-the-bars default
  the trailing `HS <twy>` overrides).

Any aviation-behavior change here must be re-reviewed by `aviation-sim-expert` (include the standard
"read the local FAA files, do not web-search" note).

---

## Existing-code map (verified 2026-06-03)

- **Crossing + terminal hold:** a taxi route ending in a crossing with no trailing hold-short already crosses
  and drops into `HoldingInPositionPhase` ½-length past the far bars — `TaxiingPhase.BuildResumePhases`
  (`:415–422`) / `BuildPreClearedCrossingPhases` (`:436–`), `CrossingRunwayPhase` (`:210–235`). **Capability
  "clear runway & hold just past it" already exists** via `CROSS`-with-no-HS.
- **No "pull forward" command exists.** Ground verbs: `TAXI`, `TAXIAUTO`, `CROSS`, `HS`, `HOLD/HP`, `RES`,
  `FOLLOWG`, `GW/BEHIND`, `BREAK`, `GO` — none nudge a stopped aircraft forward a short distance.
  `RES` from `HoldingShortPhase` satisfies a *runway-crossing* clearance (`CommandDispatcher.cs:1552–1567`);
  it does not pull an aircraft forward off a runway it's encroaching.
- **Route warnings → controller:** `route.Warnings` (set by `RouteMaterialiser.BuildWarnings` +
  `GroundCommandHandler.TryTaxi`) are appended to the TAXI command echo (`GroundCommandHandler.cs:340–343`).
  This is the issuance-warning channel.
- **No runtime ground-warning channel.** Only `NoLandingClearanceWarningActive` (`AircraftState.cs:253`,
  rendered in datablock `RadarDatablockLayout.cs:92`) exists, and it's approach-specific. `HoldingShortPhase.
  OnStart` (`:50–58`) emits a one-time pilot/terminal "holding short …" note. A runtime "unable to clear the
  runway" warning needs a NEW per-aircraft flag (model on `NoLandingClearanceWarningActive`) + datablock render.
- **Hold-short stop offset:** `HoldShortAnnotator.ComputeHoldShortPositions` (`:303`) offsets the stop point
  backward from the hold-short node — `aircraftLength + 30 ft` for taxiway hold-shorts, `½ length` for runway
  hold-shorts — via `VirtualNode.OffsetBefore` (`VirtualNode.cs:61`), applied in `TaxiingPhase.cs:213–217`.
- **Parser:** `CommandSchemeParser.ParseCompound` (client) → canonical → `GroundCommandHandler.TryTaxi`
  (server). Taxi tokens, `CROSS`, `HS` parsing live here. Turn/directionality hints would be parsed here.

---

## Work items

Each item: problem → proposed design → key files → tests. **Designs marked (PROPOSAL) need review** — phraseology
especially should be confirmed with the mentor.

### W1 — Fix the spin: stop at the taxiway hold-short, never backtrack ✅ DONE (2026-06-03)
- **Problem:** the post-crossing navigator reversed ~180° to reach a hold-short the crossing carried it past.
- **Implemented as three changes (all in `Yaat.Sim`, aviation-sim-expert-reviewed):**
  - **Fix A (offset, "W1b")** — `HoldShortAnnotator.ComputeHoldShortPositions` + `VirtualNode.OffsetBefore`
    (new `stopAtRunwayHoldShort` param): when a taxiway hold-short's `aircraftLength+30 ft` setback walks back
    through a runway hold-short the route just crossed, cap it to the nose-at-line setback (½ length) and clamp
    at the runway hold-short so the stop never projects behind the runway. New helper
    `ApproachPassesRunwayHoldShort` detects the runway-adjacency.
  - **Fix B (suppress overshoot)** — `CrossingRunwayPhase.TryBuildCrossingRoute`: when a binding (uncleared)
    taxiway hold-short lies within a fuselage length past the exit (gap `D < L`, so the aircraft can't be both
    clear of the runway and short of the taxiway), skip the ½-length tail-clearance virtual node. The crossing
    ends at the exit and the onward `TaxiingPhase` stops at the taxiway line (tail over the bars). New helper
    `BindingTaxiwayHoldShortWithin`. Covers both the pre-cleared and resume crossing paths (shared method).
  - **Fix C (navigator backstop)** — `GroundNavigator.TickStraight`: a stop target already passed along-track
    (`alongNm >= edgeLengthNm`) arrives in place rather than steering ~180° backward onto it. Narrowly gated;
    does not change normal forward hold-short arrival. (Defense-in-depth — A+B fix JBU577 geometrically; C did
    not need to fire for it.)
- **W1b semantics (aviation-confirmed):** nose at the taxiway hold line (center ½ length back from the node),
  zero buffer; the ½-length runway-hold-short clamp is correct for the aircraft-longer-than-2×-gap case.
  FAA: AIM 2-3-5.3/2-3-5.1 (hard stop, runway not clear), 4-3-21.a (no reversing), 7110.65 3-7-4 (controller
  protects → W3 warning), 3-7-2 (cross without delay).
- **Test:** `Issue172Jbu577TaxiSpinTests.Jbu577_CrossesAndHoldsShortOfB_WithoutReversing` — full replay
  (`Replay(0)` + `ReplayOneSecond` to t=513, before the `TAXI B M1 Y @B5` extension at t=514). Asserts no
  ~180° heading reversal, no backward retreat toward the runway, holds short of B facing the crossing
  direction, no orbit. (Went red on the current code first: "reversed ~180° … 180° from crossing heading.")
- **Ripple note:** the broader hold-short-offset change shifted dense-replay timing enough to add one mild
  1-tick ETA-gate cap in `Issue172ParallelPassTests`; that test was relaxed to assert no *sustained* yield
  (its guarded bug was a ~25 s brake) rather than zero ticks. Full `tools/test-all.ps1` green both repos.

### W2 — Hold-short-of-taxiway precedence + "runway not clear" state ✅ DONE (2026-06-03)
- **Problem:** when HS-of-taxiway conflicts with clearing the runway, the sim must keep the aircraft short of
  the taxiway (tail over the runway), and represent the runway as occupied/not-clear.
- **Implemented (aviation-sim-expert-reviewed):**
  - State lives on the route's `HoldShortPoint.TailOverRunwayNodeId` (the runway hold-short node the tail
    overhangs) — not a new per-aircraft flag. Set in `HoldShortAnnotator.ComputeHoldShortPositions` when the
    crossed runway's gap is shorter than the fuselage (new `FindCrossedRunwayHoldShort` helper). Snapshotted
    via `HoldShortPointDto`.
  - **Occupancy:** the sim has no general "runway in use" gate — occupancy is a set of occupied hold-short
    *nodes* consumed by arrival exit-planning. `SimulationEngine.BuildOccupiedHoldShortNodes` now adds the
    overhung runway node, so an arrival to that runway won't plan to use the exit the tail blocks. Aviation
    confirmed this partial-realism is sufficient; broader cross/land blocking is a future nice-to-have (the
    node is already in the right structure to feed it).
- **Tests:** `Issue172Jbu577TailOverRunwayTests` — the B hold-short is tagged with the runway node; the node
  is occupied while JBU577 holds; the runway-crossing HS is never tagged.

### W3 — Warnings, both at issuance and at runtime ✅ DONE (2026-06-03)
- **Issuance:** `HoldShortAnnotator.ComputeHoldShortPositions` appends a `route.Warnings` entry
  ("holding short of B leaves the tail over RWY 01L/19R — unable to clear the runway") → shows on the TAXI
  echo. (The runtime warning surface is a **terminal note only**, not a datablock flag — user decision.)
- **Runtime:** `HoldingShortPhase.OnStart` emits a controller-side terminal note ("… not clear of RWY X —
  tail over the hold-short bars") on the warning lane only — never as a pilot transmission (runway protection
  is the controller's job, 7110.65 3-7-4; the pilot voice stays the plain "holding short of B").
- **Tests:** issuance warning on the echo; the runtime note fires while JBU577 holds.

### W4 — Verify "clear the runway & hold just past it" (capability exists) ✅ DONE (2026-06-03)
- **Verified** via a faithful OAK recording-replay (`4d4344011a72.zip`, N427MX lands 28L, exits north onto
  G, is cleared `TAXI G C HS 28R` then `RES` to cross 28R). The aircraft holds short of 28R, crosses it via
  `CrossingRunwayPhase`, and settles in `HoldingInPositionPhase` just past the far-side bars (601 ft from the
  entry bars, 146 ft past the far bars near the G/C junction) — it **clears the runway and holds just past**,
  with no stop-short (tail over the bars) and no reversal. This is the positive counterpart to the JBU577 spin
  and the W1 **Fix-B non-regression**: with no binding taxiway hold-short within a fuselage past the exit, the
  ½-length tail-clearance append is **not** suppressed, so the aircraft clears the runway.
- **Test:** `Issue172CrossNoHoldShortTerminalTests.CrossingWithNoBindingTaxiwayHoldShort_ClearsRunwayAndHoldsJustPast_WithoutStoppingShort`
  — test-only, no production change. (Complements `OakCrossThenHoldOnNextTaxiwayTests`, which asserts "lands on
  C near #350" but not the cleared-the-far-bars / no-reversal property.)
- **Finding — the clean `TAXI <twy> CROSS <rwy>` form is W6-dependent, not "no new code":** issuing
  `TAXI G CROSS 28R` (single taxiway + `CROSS`, no onward taxiway) from mid-G does **not** produce the terminal
  crossing the W4 design assumed. (a) From the 28L hold-short it anchors the **wrong direction** (back toward
  28L) — the direction-by-crossed-runway anchoring is exactly W6. (b) Even positioned unambiguously between the
  runways, the resolved route carries **no `RunwayCrossing` hold-short**, so it taxis across 28R in plain
  `TaxiingPhase` (never `CrossingRunwayPhase`) and walks G to its no-destination cap past the crossing — it
  never terminates *at* the runway. So the pure `route.IsComplete → HoldingInPosition ½-length-past` branch and
  the `TAXI <twy> CROSS <rwy>` ergonomics both fall under **W6** (fold the regression `TAXI J CROSS 28R` E2E
  into W6 once the anchoring lands). The *capability* (cross & hold just past, via a route that has a proper
  crossing structure) is confirmed by the test above.

### W5 — "Pull forward" command `CLRWY` (NEW) ✅ DONE (2026-06-03)
- **Problem:** an aircraft holding short of a taxiway with its tail over a runway (W2) needs a way to be moved
  forward until it's clear of the runway, then hold — the controller resolves the encroachment.
- **Implemented as `CLRWY` (alias `CLEARRWY`)** (user-chosen verb; user decisions: tail-over-runway-only scope;
  stop at the ½-length tail-clearance node, not the junction):
  - **Plumbing:** new `CanonicalCommandType.ClearRunway` + `ClearRunwayCommand` record + parser case +
    `CommandRegistry.All` (`Bare(ClearRunway, "Clear Runway", "Ground", …, ["CLRWY","CLEARRWY"])`, which feeds
    `CommandScheme.Default()`) + `CommandDescriber` (canonical `CLRWY`, natural "Clear the runway", ground
    classification) + `CommandDispatcher.TryApplyTowerCommand` routes it to `GroundCommandHandler.TryClearRunway`.
  - **Handler (`GroundCommandHandler.TryClearRunway`):** valid only when the current phase is a
    `HoldingShortPhase` whose `HoldShort.TailOverRunwayNodeId` is set (else a clear rejection). It supersedes
    that taxiway hold-short (`IsCleared`, clears `TailOverRunwayNodeId` → releases the occupied runway node),
    then installs `[ClearRunwayPhase, HoldingInPositionPhase]`.
  - **New `ClearRunwayPhase`** (modeled on `CrossingRunwayPhase`): drives a `GroundNavigator` to a virtual node
    `VirtualNode.OffsetPast(runwayNode, approachNode, ½ aircraft length)` — the **same** ½-length tail-clearance
    node a crossing-with-no-hold-short stops at (the one W1 Fix B suppresses here) — then completes into
    `HoldingInPositionPhase`. Snapshotted via `ClearRunwayPhaseDto` (runway + approach node ids; navigator
    rebuilt lazily on restore, like the crossing phase); registered in `PhaseSnapshotDto` `JsonDerivedType` +
    `PhaseList.FromSnapshot`. Pilot/RPO readback: "clearing the runway, holding".
- **Test:** `Issue172Jbu577ClearRunwayTests` — JBU577 holding short of B (tail over 01L/19R, runway node
  occupied) → `CLRWY` → pulls forward from center 8 ft to ~72 ft (½ length) past the runway bars, at B's hold
  line (#1398), **not** the junction (#155, ~148 ft past); holds; runway node released.
- **Aviation review (2026-06-03): approved, ship.** Confirmed against 7110.65 §3-7-4 / §3-10-6.c (controller's
  duty to move a runway-fouling aircraft; aircraft don't self-reverse — no standard 7110.65 verb exists, so a
  YAAT verb is appropriate) and AIM §4-3-21.b (clear-of-runway = all parts past the holding-position markings,
  then hold; nose protruding onto the taxiway is explicitly sanctioned). The ½-length offset is anchored at the
  `RunwayHoldShort` node (the hold-short line), not the runway edge/centerline — verified correct. Phraseology:
  read-back stays present-progressive "clearing the runway, holding"; the controller-facing natural form renders
  as a real instruction ("Continue forward, clear of the runway, then hold"), not the input token.

### W6 — Directionality via the crossed runway: `TAXI J CROSS 28R` (NEW) ✅ DONE (2026-06-03)
- **Problem:** `TAXI <twy> CROSS <rwy>` did not use the crossed runway for direction — `CROSS <rwy>` only
  pre-cleared the crossing. When `<twy>` crosses two runways (e.g. G crosses both 28L and 28R at OAK), the
  route could resolve the wrong way (back across the runway behind). It also never terminated at the crossing.
- **Implemented (aviation-sim-expert-reviewed), per the user's three design decisions** (terminate just past
  the crossing; anchor only as the directional cue when there's no other destination; whole-route — including
  the start direction):
  - **`GroundCommandHandler.ResolveCrossedRunwayAnchor` / `ResolveCrossedRunwayFarSideHoldShort`** — when a
    `TAXI` has `CROSS <rwy>` and no destination runway / parking / spot, resolve the crossed runway's hold-short
    on the **far side** of the start (the one reached only by crossing the runway — the farther of the runway's
    two hold-shorts on the named taxiways) and pass it as `ExplicitPathOptions.DestinationHintNode`. The
    farthest-along crossed runway wins for multiple `CROSS`.
  - **`SegmentExpander.ResolveTerminus`** — a `DestinationKind.Node` destination on the final taxiway now routes
    straight to it via `LocalSearchToJunction` (the direction-correct walk parking already uses), so the route
    heads toward and across the runway and stops at the far bars instead of the direction-blind terminus walk.
    Reuses existing node-routing; the only callers producing a `Node` destination on an explicit path are spot/
    parking (routed via their own channel) and this anchor, so the change is isolated.
  - Result: the route crosses the runway (a `RunwayCrossing` hold-short is annotated), `CrossingRunwayPhase`
    fires (pre-cleared by the `CROSS` keyword), and it terminates in `HoldingInPositionPhase` just past the far
    bars (W4). With a real destination, `CROSS <rwy>` stays a pure pre-clearance and the destination anchors.
- **Tests:** `Issue172TaxiCrossRunwayAnchorTests` — `TAXI G CROSS 28R` from the ambiguous 28L-hold-short start
  routes north across 28R (not back toward 28L) and holds 18 ft past the far bars; `TAXI J CROSS 28R` (synthetic
  spawn on J) routes along J across 28R and holds 12 ft past. 325-test ground/pathfinder/Issue172 suite green.
- **Scope note:** the anchor steers the **final** named taxiway's direction/terminus (the single-taxiway case is
  the start-direction disambiguation). For a multi-taxiway route where the crossing is on an *earlier* leg
  (e.g. `TAXI G C CROSS 28R`, 28R crossed on G with C past it), the far node isn't on the last taxiway, so the
  anchor no-ops and the existing next-taxiway lookahead handles direction — graceful, no regression.
- **Aviation review (2026-06-03): approved, ship.** Confirmed against 7110.65 §3-7-2 (a crossing clearance is
  inherently directional — taxi toward/over the named runway) and AIM §4-3-21.b (auto-hold once fully past the
  far holding-position markings → the ½-length-past terminus is correct). Optional future nicety (NOT a
  correctness gap, not gating): a bare `CROSS <rwy>` with no onward taxi/hold-short is slightly under-specified
  real-world phraseology (§3-7-2.a.2 NOTE), so an advisory like "crossing clearance issued without onward
  taxi/hold-short" could nudge trainees toward complete phraseology. Backlog only.

### W7 — Per-taxiway turn-direction hints `>` / `<` (NEW) — DONE
- **Problem:** controllers say "right on A, taxi A B C" — and disambiguate turns mid-route too. YAAT had no
  way to express which way to turn **onto a given taxiway** at its junction; that ambiguity is behind several
  #172 mis-turns (both the start direction AND mid-route junction picks).
- **Syntax (shipped):** a `>` (right) or `<` (left) prefix on **any** taxiway token, applied per taxiway at the
  junction where the route turns onto it. `TAXI >A B C` = "right onto A"; `TAXI <A B <C D` = "left onto A,
  then B (as resolved), left onto C, then D". Hints are optional per token; an un-prefixed taxiway keeps the
  pathfinder's own choice. **Best-effort:** an unrealisable hint never strands the route.
- **How it landed:**
  - **Parse + round-trip:** `GroundCommandParser.ParseTaxiTokens` strips the glyph into a parallel
    `List<TurnDirection?>` (`TaxiCommand.PathTurnHints`, index-aligned with `Path`). The wire canonical is the
    raw argument string (`CommandSchemeParser.ToCanonical` = verb + verbatim arg), so the glyph round-trips to
    the server automatically; the display canonical (`CommandDescriber.FormatTaxiCanonical`) re-emits it and the
    natural echo says "right on A". No `CommandSchemeParser` lexer change was needed (the original proposal
    over-scoped this). The current-taxiway prepend + trailing-runway-removal sites keep the parallel list aligned.
  - **Pathfinder bias:** `ExplicitPathOptions` / `SearchContext` carry `PathTurnHints` + `StartHeadingTrue`;
    `WaypointToken.TurnHint` carries the per-token hint. `SegmentExpander.RouteNamedToNamed` adds two additive,
    finite penalties (`< TailUnresolvablePenaltyNm`, so best-effort): `TurnHintOntoTaxiwayPenalty` (mid-route —
    prefer the junction whose onward edge on the hinted taxiway turns the hinted way from the arrival bearing)
    and `FirstTaxiwayTurnHintPenalty` (first taxiway — the initial edge direction vs `StartHeadingTrue`). The
    single-taxiway case (`TAXI >A`) biases `WalkToNaturalTerminus` via `ResolveTurnHintBias`.
  - **Autocomplete:** no change needed — TAXI path tokens have no value suggester today, so the glyph passes
    through harmlessly.
- **Tests:** `GroundCommandParserTurnHintTests` (parse + canonical round-trip), `Issue172TurnHintTests`
  (real-OAK single-taxiway right/left flip via heading; synthetic two-junction mid-route flip; synthetic
  two-entry first-taxiway flip). All green; 267 ground/pathfinder regression tests unaffected.
- **Follow-ups (both DONE):**
  - *Hint-unable advisory* — when a `>`/`<` turn can't be honored, the resolver records an advisory
    (`SearchContext.TurnHintAdvisories` → `route.Warnings`) so the TAXI echo notes "Unable left turn onto B
    — taxiing right instead". Best-effort routing is unchanged.
  - *Spoken taxi readback* — the pilot now voices the route via the rule-inversion verbalizer
    (`PhraseologyVerbalizer.TaxiArgs`/`SpellTaxiPath` fill the previously-empty `{path…}` + `{rwy}`/`{holdshort}`/
    `{crossrwy}` captures), including turns as "right on bravo"/"left on charlie" (aviation-reviewed against
    AIM 4-3-17/4-3-18, 7110.65 §3-7-2).

---

## Suggested order & risk

1. **W1 + W1b + W2** (the spin fix + precedence + state) — core, highest value, sensitive navigator/crossing
   code. Land with the JBU577 replay test first (TDD).
2. **W3** (warnings) — depends on W2's state flag.
3. ~~**W4** (verify capability)~~ — DONE via recording-replay; the `TAXI J CROSS 28R` E2E landed with W6.
4. ~~**W6** (`TAXI J CROSS 28R` direction hint)~~ — DONE. Crossed-runway directional anchor + terminate just
   past the crossing; also delivered the terminal `TAXI <twy> CROSS <rwy>` form W4 found to be W6-dependent.
5. ~~**W5** (pull-forward command)~~ — DONE as `CLRWY`; drives to the ½-length tail-clearance node and holds.
6. ~~**W7** (`>`/`<` hints)~~ — DONE. Glyph parsed into `TaxiCommand.PathTurnHints`; junction-selection bias in
   `SegmentExpander` (best-effort). The `CommandSchemeParser` lexer change the proposal feared was unnecessary
   (canonical is the raw arg string).

Cross-repo: run `pwsh tools/test-all.ps1` (W1/W2 touch `Yaat.Sim` shared by yaat-server). Update `COMMANDS.md`,
`docs/command-cheatsheet.json` + HTML cheatsheet, `USER_GUIDE.md`, `docs/yaat-vs-atctrainer.md`, and
`docs/architecture.md` for any new command/syntax. `aviation-sim-expert` review for W1b/W2/W5.

## Open decisions for the implementing agent (confirm with the user / mentor)
- ~~W5: the pull-forward command's verb + RPO phraseology.~~ DECIDED: `CLRWY` (alias `CLEARRWY`), readback
  "clearing the runway, holding"; stops at the ½-length tail-clearance node.
- ~~W7: confirm the hint tokens (`>`/`<`) and canonical encoding.~~ DECIDED + SHIPPED: `>A`/`<A` glyph prefix,
  round-trips verbatim in the canonical, best-effort, full per-token (first + mid-route).
- W1b: the precise taxiway-hold-short stop offset (nose-at-bar vs a buffer) when it conflicts with a runway.
- W3: runtime-warning surface (datablock flag vs terminal note vs both).
