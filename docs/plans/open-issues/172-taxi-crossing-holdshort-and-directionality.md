# Handoff: taxi crossing / hold-short precedence + directionality hints

> **Status:** scoped & root-caused (2026-06-03), ready to implement. Originated from issue #172 (JBU577
> "taxi spin"). Mentor (Maxim, ZOA) consulted; FAA references checked. **Nothing implemented yet.**
> **Recording:** `tests/Yaat.Sim.Tests/TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip`.

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
web-search; read the files):
- **AIM 2-3-3** (`.claude/reference/faa/aim/chap02_sec03.md`) — a commanded hold-short is a hard stop (no part
  crosses the bars); a runway is "not clear" until all parts cross the marking.
- **7110.65 3-10-5 NOTE 1 + .c** (`.claude/reference/faa/7110.65/chap03_sec10.md`) — does not authorize rolling
  past the subsequent hold-short to clear the runway; controller **must protect** the intersection when an
  aircraft must enter it to clear the runway → trigger for the controller warning.
- **AIM 4-3-21.b** (`.claude/reference/faa/aim/chap04_sec03.md`) — `CROSS` with the hold-short omitted is the
  existing way to say "clear the runway and stop just past it" (auto-hold past the bars). No new verb for that.

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

### W1 — Fix the spin: stop at the taxiway hold-short, never backtrack
- **Problem:** the post-crossing navigator reverses ~180° to reach a hold-short the crossing carried it past.
- **Design:** when a binding taxiway hold-short falls within the runway-crossing tail-clearance distance, the
  aircraft must **stop at the hold-short as it arrives** — no ½-length overshoot past the runway, no reverse.
  Two interacting fixes likely needed: (a) the hold-short stop **offset** must not project *behind* a runway
  hold-short the aircraft just cleared (cap the backward offset; see W1b); (b) the navigator/`CrossingRunway
  →Taxiing` handoff must not produce a backward segment to an already-passed stop node — prefer "stop here"
  over "reverse to the node." Reuse the existing advance-on-pass machinery; do not relax precise arrival for
  normal hold-shorts.
- **W1b (offset):** for a taxiway hold-short, the stop position should place the **nose at the taxiway hold
  line**, with the body extending back (over the runway if unavoidable) — not `aircraftLength + 30 ft` behind
  the node when that lands inside a runway. Decide the correct offset semantics with `aviation-sim-expert`.
- **Files:** `GroundNavigator.cs` (TickStraight overshoot/arrival, `:583–673`), `TaxiingPhase.cs`
  (`BuildPreClearedCrossingPhases`, `SetupCurrentSegment`), `HoldShortAnnotator.cs` / `VirtualNode.cs`.
- **Tests:** replay test (JBU577 `TAXI G B HS B`): crosses, holds short of B, **no 180° reversal, no orbit**
  (`ThrowOnOrbit` + `Issue165` stuck-<5 kt signature). Precedent `Issue172Wja1521CurrentTaxiwayTests`,
  `Issue165SkwTaxiSpinTests`.
- **Repro scaffold (untracked):** `tests/Yaat.Sim.Tests/Simulation/Issue172Jbu577TaxiSpinTests.cs` already
  exists with diagnostic methods that replay the bundle and log JBU577's trajectory/conflict/nav state
  (the spin is visible t≈470–495). Turn its diagnostics into the W1 assertion test; it also documents the
  t=444 `TAXI G B HS B` window and the `TickForwardPastDelete` technique.

### W2 — Hold-short-of-taxiway precedence + "runway not clear" state
- **Problem:** when HS-of-taxiway conflicts with clearing the runway, the sim must keep the aircraft short of
  the taxiway (tail over the runway), and represent the runway as occupied/not-clear.
- **Design:** the aircraft holds at the taxiway hold line (W1); a per-aircraft flag marks "tail over runway
  X / unable to clear" so detection (`GroundConflictDetector` / runway-occupancy) and the W3 warning can use
  it. Confirm with `aviation-sim-expert` whether runway-occupancy logic needs to treat this aircraft as on the
  runway.
- **Files:** `AircraftState.cs` / `AircraftGroundOps.cs` (new flag), `GroundConflictDetector.cs` (occupancy),
  the hold path in `TaxiingPhase.ArriveAtNode`.
- **Tests:** assert the flag/state is set when JBU577 holds short of B; assert it is *not* set for a normal
  taxiway hold-short with room.

### W3 — Warnings, both at issuance and at runtime
- **Issuance:** when `TAXI … HS <twy>` is issued and `<twy>`'s hold-short falls within a fuselage of a runway
  the route crosses/exits, append a route warning ("unable to fully clear RWY X — tail over the hold-short
  bars") → shows in the TAXI echo (existing `route.Warnings` channel).
- **Runtime:** when the aircraft actually holds in that state, surface a controller-facing warning — a new
  per-aircraft datablock flag (model on `NoLandingClearanceWarningActive`) and/or a terminal note from the
  hold phase. Per **7110.65 3-10-5.c**.
- **Files:** `RouteMaterialiser.cs` / `GroundCommandHandler.cs` (issuance), `AircraftState.cs` +
  `RadarDatablockLayout.cs` + the hold phase (runtime).
- **Tests:** assert both warnings fire for JBU577; neither fires for a normal hold-short.

### W4 — Verify "clear the runway & hold just past it" (capability exists)
- **Design:** confirm `TAXI <twy> CROSS <rwy>` (no trailing HS) crosses and `HoldingInPosition`s ½-length past
  the far bars — for the OAK case the aircraft stops just past 28R **before** the J/C fork. Add a regression
  test; no new code expected (works per the existing-code map). See W6 for the `C`-as-direction-hint nuance.
- **Tests:** E2E at OAK: `TAXI J CROSS 28R` (after W6) and/or a synthetic route ending in a crossing → auto-hold
  just past the runway.

### W5 — "Pull forward" command (NEW) — PROPOSAL
- **Problem:** an aircraft holding short of a taxiway with its tail over a runway (W2) needs a way to be moved
  forward until it's clear of the runway, then hold — the controller resolves the encroachment.
- **Proposed behavior:** advance the aircraft forward along its current taxiway/route just enough that its
  **tail clears the runway behind it** (i.e. to the ½-length-past-the-far-bars runway-clear point), then
  `HoldingInPosition`. It supersedes the taxiway hold-short that was binding (the aircraft now enters the
  taxiway minimally). Idempotent if already clear.
- **Proposed command (NAME/PHRASEOLOGY TBD with mentor):** a new `CanonicalCommandType` — working name `FWD`
  (or "pull up / continue forward, clear of the runway"). Functionally it re-targets the navigator to the
  runway-clear point and resumes. Real ATC has no standard verb for this; pick a YAAT verb + a plain-language
  RPO phrasing. Must be in `CommandScheme.Default()` + `CommandRegistry.All` (completeness tests enforce this),
  documented in `COMMANDS.md` + `docs/command-cheatsheet.json` + the HTML cheatsheet, and reviewed by
  `aviation-sim-expert`.
- **Files:** `ParsedCommand.cs` / `CanonicalCommandType`, `CommandScheme`, `CommandRegistry`, a handler
  (likely `GroundCommandHandler`), `CommandDispatcher` routing.
- **Tests:** after JBU577 holds short of B (tail over runway), issue the command → it pulls forward to clear
  RWY 01L/19R and holds, warning clears.

### W6 — Directionality via the crossed runway: `TAXI J CROSS 28R` (NEW) — PROPOSAL
- **Problem:** `TAXI J C CROSS 28R` mis-treats `C` as a route taxiway when the controller only means it as a
  *direction hint* (which way along J / which fork) — the aircraft walks J all the way to the J/C junction.
- **Proposed design:** allow `TAXI <twy> CROSS <rwy>` where the **named runway's crossing point on `<twy>` is
  the directional anchor** (analogous to `--pf-dest-rwy` / destination-runway anchoring the pathfinder already
  does). The aircraft walks `<twy>` *toward* the 28R crossing and stops just past it (W4) — no intermediate
  taxiway needed. The crossed runway disambiguates direction the way a named next-taxiway would.
- **Files:** `CommandSchemeParser` (recognize `<twy> CROSS <rwy>` shape), `GroundCommandHandler.TryTaxi` /
  the pathfinder anchoring (`SegmentExpander` / runway hold-short anchor logic already exists for destination
  runways — `ResolveRunwayHoldShortAnchorOnTaxiway`).
- **Tests:** OAK `TAXI J CROSS 28R` routes along J toward the 28R crossing (correct fork) and holds just past;
  contrast with the old `TAXI J C CROSS 28R`.

### W7 — Per-taxiway turn-direction hints `>` / `<` (NEW) — PROPOSAL
- **Problem:** controllers say "right on A, taxi A B C" — and disambiguate turns mid-route too. YAAT has no
  way to express which way to turn **onto a given taxiway** at its junction; that ambiguity is behind several
  #172 mis-turns (both the start direction AND mid-route junction picks).
- **Proposed syntax:** a `>` (right) or `<` (left) prefix on **any** taxiway token, applied per taxiway at the
  junction where the route turns onto it — not just the first. `TAXI >A B C` = "right onto A";
  `TAXI <A B <C D` = "left onto A, then B (as resolved), left onto C, then D". Hints are optional per token; an
  un-prefixed taxiway keeps the pathfinder's own choice.
- **Proposed design:** the taxi command carries a per-token turn hint (Left/Right/None) alongside each taxiway
  name — the path is no longer a bare `List<string>`, and the **canonical form must round-trip the hints**.
  The pathfinder's junction selection (`SegmentExpander.RouteNamedToNamed` / `FindJunctionCandidates` + the
  directional-anchor scoring) prefers, at each transition onto a hinted taxiway, the junction/onward-direction
  whose turn matches the hint; for the first taxiway the start-anchoring uses it to choose which way to enter.
  This is the controller's explicit override for the junction-selection ambiguity family (SKW3359/SIA31).
- **Files:** `CommandSchemeParser` (lexer/grammar — strip `>`/`<` per token; fragile, re-validate every
  `CommandSchemeParserTests` case), the taxi command record + canonical form (per-token hint field),
  `SegmentExpander` junction scoring + `GroundCommandHandler.TryTaxi` start-anchoring, `CommandInputController`
  autocomplete/signature-help, `COMMANDS.md` + cheatsheet (×3) + `USER_GUIDE.md` + `docs/yaat-vs-atctrainer.md`.
- **Tests:** an airport where a junction admits both turns — `>X` takes the right junction/leg, `<X` the left,
  at both the first taxiway and a mid-route taxiway (`… <C …`); verify the canonical form preserves the hints.

---

## Suggested order & risk

1. **W1 + W1b + W2** (the spin fix + precedence + state) — core, highest value, sensitive navigator/crossing
   code. Land with the JBU577 replay test first (TDD).
2. **W3** (warnings) — depends on W2's state flag.
3. **W4** (verify capability) — cheap regression once W6 lands (or before, with a synthetic route).
4. **W6** (`TAXI J CROSS 28R` direction hint) — parser + pathfinder anchoring.
5. **W5** (pull-forward command) — new verb; depends on W2 (the state it resolves).
6. **W7** (`>`/`<` hints) — parser-heavy, fragile test surface; can land independently.

Cross-repo: run `pwsh tools/test-all.ps1` (W1/W2 touch `Yaat.Sim` shared by yaat-server). Update `COMMANDS.md`,
`docs/command-cheatsheet.json` + HTML cheatsheet, `USER_GUIDE.md`, `docs/yaat-vs-atctrainer.md`, and
`docs/architecture.md` for any new command/syntax. `aviation-sim-expert` review for W1b/W2/W5.

## Open decisions for the implementing agent (confirm with the user / mentor)
- W5: the pull-forward command's verb + RPO phraseology.
- W7: confirm the hint tokens (`>`/`<`) — they apply per-taxiway anywhere in the instruction
  (e.g. `TAXI <A B <C D`); decide how the canonical form encodes them and how they render in autocomplete/echo.
- W1b: the precise taxiway-hold-short stop offset (nose-at-bar vs a buffer) when it conflicts with a runway.
- W3: runtime-warning surface (datablock flag vs terminal note vs both).
