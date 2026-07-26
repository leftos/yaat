# Bug-Finding Expedition — 2026-07

Proactive hunt for latent defects (not issue triage — only #150 was open at the time).
**Report-first: no code changes are made from this document without explicit approval.**

## Baseline

Established before any hunting, so findings are not confounded by pre-existing failures.

| Run | Result |
|---|---|
| `pwsh tools/test-all.ps1` (both repos) | 10,468 passed / 0 failed / 3 skipped |
| Gated sweeps `Category=Nightly\|PathfinderGrid` | 1,223 passed / 0 failed (31 s, Release) |

Both green, so every finding below is uncovered ground rather than a known-red test.

**CI-coverage correction worth recording:** per-PR CI (`.github/workflows/ci.yml:55`) filters only
`Category!=Nightly`, so **`PathfinderGrid` actually runs on every push** — it is continuously
covered. `tools/test-all.ps1:85` excludes *both* categories, so the local default run is weaker
than CI, which is the opposite of the usual assumption. Only the `Nightly` taxi grids are truly
gated (to `.github/workflows/nightly-taxi-grid.yml`).

## Method

Parallel read-only auditors, each required to read the relevant `docs/*.md` subsystem doc *before*
any source (documented-intentional behavior is the spec, not a bug), and each gated on mandatory
self-refutation: re-read the context, try to disprove the finding, grep `tests/` for existing
coverage, drop anything indefensible. Style/naming/refactor suggestions were banned — only defects
tied to a concrete wrong output. Findings marked **[verified]** were additionally re-checked by me
against source; the rest carry the auditor's evidence and are marked with their confidence.

---

## Summary

19 product defects plus 4 test-suite integrity findings, across 7 audited areas. Five are high
severity; none are currently caught by the suite.

**Fixed so far** (TDD — failing test first, then the fix): findings **1**–**5**, **7**–**11**, **14**, **16**, **19**, and **20**.
Everything else is reported only, awaiting triage.

| # | Finding | Area | Sev |
|---|---|---|---|
| 1 | ✅ **FIXED** — Rejected `DCT` wipes the command queue **and** all deferred dispatches | commands | high |
| 2 | ✅ **FIXED** — Post-touchdown `GA` gate is unreachable — aircraft levitates at 30 kt | phases | high |
| 3 | ✅ **FIXED** — Straight taxi legs held at cornering speed (250 ft gate inert) | ground | high |
| 4 | ✅ **FIXED** — Snapshot restore mid-runway-exit silently completes the phase | ground | high |
| 5 | ✅ **FIXED** — All test layouts use 150 ft default runway width; production uses navdata | tests | high |
| 6 | Every pop-out toggle leaks a canvas, renderer, and 10 Hz timer | client | med-high |
| 7 | ✅ **FIXED** — `WAIT`/`BEHIND` deferral re-inflates its own prefix on restore | commands | med-high |
| 8 | ✅ **FIXED** — `CLRWY` permanently rejected after restore — aircraft strands on runway | ground | med |
| 9 | ✅ **FIXED** — `MidfieldCrossing`/`TeardropReentry` destroy the pattern circuit on `SPD`/`CM` | phases | med |
| 10 | ✅ **FIXED** — `InterceptCoursePhase` ignores `LateralInterceptOnly` — decelerates ~70 kt | phases | med |
| 11 | ✅ **FIXED** — `ProcedureTurnPhase` leaks a permanent 200 kt `SpeedCeiling` | phases | med |
| 12 | Ghost fields absent from `StarsTrackFingerprint` — re-ghost never reaches CRC | crc | med |
| 13 | `BrightnessLookup` shared by reference into the render snapshot, mutated live | client | med |
| 14 | ✅ **FIXED** — Split conditional block loses `IsApplied`/`TriggerMet` — zombie queue | commands | med |
| 15 | `EL`/`ER`/`EXIT` during `RunwayExitPhase` acknowledged but ignored | ground | med |
| 16 | ✅ **FIXED** — `DesiredDecelRate` leaks out of `LandingPhase` on `GA` from rollout | phases | low |
| 17 | Documented `ONH` alias is dead as a condition prefix | commands | low |
| 18 | `DrainAllPilotSpeech` never called by the engine (replay-only) | sim | low |
| 19 | ✅ **FIXED** — Restored conflict sets frozen for the rest of a hybrid replay | sim | low |
| 20 | ✅ **FIXED** — `SMF.geojson` is uppercase — its test has never run on CI | tests | med |
| 21 | Replay assertions that cannot fail on the bug they were written for | tests | med |
| 22 | Orphaned recordings — regression coverage that no longer runs | tests | med |
| 23 | Runway-only stub layouts defeat the null-layout guard | tests | med |

Two themes account for most of the list. **Snapshot-restore fidelity** is the biggest: findings 4,
7, 8, 14, and 19 are all "state that exists live is not reconstructed on restore," and they cluster
because restore paths rebuild objects rather than re-resolving them. **Gate-ordering and
teardown** is the second: 2, 9, 10, 11, 15, and 16 are all a guard that never runs or a field never
cleared on one of several exit paths.

---

## Findings, ranked

### 1. A rejected `DCT` wipes the command queue and every pending deferral **[verified]** — ✅ FIXED

> **Fix applied.** DCT-fix validation is now **enabled** in `DryRunValidate`, so the rejection happens
> on the throwaway clone before the real path clears anything.
>
> The obvious version of this fix is wrong, and the guard test proves it:
> `ApproachClearance.Procedure` is serialized by neither `ToSnapshot` nor `FromSnapshot`, so the clone's
> `GetProgrammedFixes()` sees a strictly *smaller* set than the real aircraft whenever an approach is
> already active — simply flipping the flag would start rejecting direct-to clearances onto the active
> approach's own fixes. The fix therefore also re-attaches the real procedure to the clone first
> (`Procedure` became settable; it is immutable nav data, so sharing the reference is safe).
>
> Tests: `RejectedDctPreservesStateTests` — one reproducing the wipe, one guarding against the naive
> fix (`DctToFixOnAlreadyActiveApproach_IsAccepted`). Both have explicit precondition assertions so
> they cannot pass vacuously.


- **File**: `src/Yaat.Sim/Commands/CommandDispatcher.cs:1041` (dry-run override), `:249-277` (real path);
  live gate at `src/Yaat.Sim/Commands/FlightCommandHandler.cs:511-521`
- **Severity**: **high** — a *rejected* command destroys unrelated pending controller work; loss is
  unrecoverable and only partially reported
- **Confidence**: high

This directly violates the contract in `docs/command-pipeline.md` §5.2: *"the user gets the error and
**state is unchanged**."*

`DryRunValidate` builds its clone context with `ValidateDctFixes = false`, so a `DCT`/`ADCT`/`TLDCT`/
`TRDCT` to an off-route fix **passes** validation. The real dispatch then destroys state before
applying:

```csharp
preserved = ClearConflictingBlocks(aircraft, incomingDims, ctx, ctx.PreserveConditionals, out var dropped);
EmitQueueClearWarning(aircraft, dropped, compound);
if (!ctx.PreserveConditionals) { aircraft.DeferredDispatches.Clear(); }
...
var applyResult = ApplyBlock(firstNewBlock, aircraft);
if (!applyResult.Success) { aircraft.Queue.Blocks.Clear(); aircraft.Queue.CurrentBlockIndex = 0; return applyResult; }
```

`ApplyBlock` runs with the live `ValidateDctFixes = true` and fails, wiping the queue including
`preserved`.

**Failure scenario.** Room has `ValidateDctFixes` on. `UAL123` is filed `SUNOL MODESTO OXNARD` with
`WAIT 120 RWY 18L TAXI N B` + `AT 6000 CM 120` pending. Controller types `UAL123 DCT RANDOM`. The
status bar shows *"Fix RANDOM not programmed — use DCTF to override"*, the route is unchanged — **and
both pending conditionals are gone**. The queued `AT 6000 CM 120` is reported via the "queue cleared
(lost: …)" warning; the deferred `WAIT 120 …` taxi is dropped **with no message at all**.

**The codebase already knows this bug class.** The comment above the dry-run block describes fixing
exactly this shape for pattern modifiers — *"passes dry-run against the intact clone queue but fails
on the real aircraft after ClearConflictingBlocks wipes the queued entry — a silent-wipe-then-fail."*
Setting `ValidateDctFixes = false` re-opens it for DCT.

**Why tests miss it.** `ProgrammedFixesTests.cs:199-211` builds a bare aircraft with an empty `Queue`
and no `DeferredDispatches`, asserting only `result.Success == false` and the message text.

### 2. The post-touchdown go-around gate is unreachable — `GA` on a slow rollout makes the aircraft levitate **[verified]** — ✅ FIXED

> **Fix applied.** `TryApplyTowerCommandCore`'s `GoAroundCommand` case now consults
> `currentPhase.CanAcceptCommand(GoAround)` and honours an explicit rejection before installing the phase.
>
> Deliberately scoped to GA rather than making all `Rejected` verdicts authoritative: many phases use a
> catch-all `_ => Rejected(...)` that today covers tower commands which legitimately work *because* of the
> bypass (e.g. `CTO` from `TaxiingPhase`). Honouring every rejection would break those. This one change
> reaches all three dead GA gates — `LandingPhase:1265`, `HelicopterTakeoffPhase:128`,
> `RunwayHoldingPhase:88`.
>
> Tests: `GoAroundEnergyGateDispatchTests` — one asserting rejection *through the dispatcher* plus
> `IsOnGround` staying true (the levitation symptom), one asserting GA above the gate still works.


- **File**: `src/Yaat.Sim/Commands/CommandDispatcher.cs:1456` vs `:1507`, `:1734`;
  gate at `src/Yaat.Sim/Phases/Tower/LandingPhase.cs:1263`
- **Severity**: **high** — produces a physically impossible state; the phase's safety rejection never
  reaches the controller
- **Confidence**: high

`DispatchWithPhase` calls `TryApplyTowerCommand` **before** `currentPhase.CanAcceptCommand`:

```csharp
// Try tower/ground-specific handling first (phase-interactive commands)
var towerResult = TryApplyTowerCommand(firstCmd, aircraft, currentPhase, ctx);   // :1456
...
var acceptance = currentPhase.CanAcceptCommand(cmdType);                          // :1507 — not reached
```

`GoAroundCommand` **is** handled in that tower switch (`:1734` → `PatternCommandHandler.TryGoAround`),
which carries no energy or on-ground guard. So `LandingPhase`'s `_canGoAround` rejection is dead code
for the only command it gates.

**Failure scenario.** A B738 has touched down, `LandingPhase.CurrentState == Rollout`, IAS 30 kt
(`RejectedLandingMinSpeed(Jet)` = 60). Controller sends `GA`. Expected: *"aircraft is below the
go-around speed gate after touchdown."* Actual: `TryGoAround` installs `GoAroundPhase`, whose
`OnStart` sets `IsOnGround = false` and `DesiredVerticalRate = 3000 fpm` — the aircraft climbs away
from the runway at 30 KIAS.

**Why tests miss it.** `TowerPhaseTests.Landing_PostTouchdown_BelowMinSpeed_RejectsGoAround:1122`
calls `phase.CanAcceptCommand(...)` **directly**. No dispatcher-level test issues `GA` to a rolling
aircraft, so the gate passes in isolation while being bypassed in production.

### 3. Any straight taxi leg between two corners is held at cornering speed — the 250 ft gate is inert **[verified]** — ✅ FIXED

> **Fix applied.** `FindBracketingCornerSpeed` now tests `runFt > ShortConnectorMaxLenFt` at the head of
> its walk loop, before either bracket lookup returns a corner speed. The trailing post-accumulation
> check became redundant and was removed.
>
> Test: `GroundNavigatorTests.LongStraightBetweenSharpCorners_IsNotTreatedAsShortConnector` — same 90°
> bracketing corners as the existing short-connector test, but a 1500 ft straight between them.
> Notably it went red by *throwing the pure-pursuit orbit guard*, not by failing its speed assertion:
> crawling a 1500 ft leg at ~3 kt destabilised the navigator into circling a node. The original 200 ft
> test still passes, so the legitimate connector slowdown is intact.


- **File**: `src/Yaat.Sim/Phases/Ground/GroundNavigator.cs:1128`, `:1173-1177`, `:1194-1198`
- **Severity**: **high** — live-sim behavior, fires on ordinary filleted geometry, not an edge case
- **Confidence**: high

`DetectShortConnector` seeds `runFt` with the current segment's length, then calls
`FindBracketingCornerSpeed` in each direction. Both of that function's *terminating* branches return
**before** `runFt` is ever compared to `ShortConnectorMaxLenFt`:

```csharp
var neighbor = route.Segments[next];
if (neighbor.Edge.Edge is GroundArc arc)
{
    return arc.MaxSafeSpeedKts(ctx.Category);   // returns before any runFt check
}
...
if (turn > ConnectorCornerThresholdDeg) { return ...; }   // same — no length check
```

The length gate lives only in the straight-continuation branch, which is reached only when walking
*outward* past additional segments. So a run consisting of one long straight bracketed by two arcs
is never length-rejected.

**Failure scenario.** A jet on `[arc] → 1500 ft straight → [arc]` (ordinary 75 ft fillet corners):
`runFt = 1500`, both lookups return the arc's `MaxSafeSpeedKts` immediately, `_onShortConnector`
becomes true and `_connectorFlowSpeedKts ≈ 9.3 kt` (yaw-rate cap). The entire 1500 ft leg is then
held at ~9.3 kt instead of accelerating to the ~15 kt jet taxi ceiling.

This directly contradicts the constant's own documentation: *"Above this a genuine straight segment
exists and the normal accelerate-then-brake profile is correct"* and *"the length window alone never
forces a slowdown."*

**Why tests miss it.** The only coverage, `GroundNavigatorTests.ShortConnector_HoldsSteadyLowSpeed_NoSurge`,
uses a **200 ft** connector — inside the window, so the missing gate is invisible. Changing that
literal to 2000 ft leaves the assertion passing. There is no negative test asserting a long straight
between two sharp corners *does* accelerate.

### 4. Restoring a snapshot mid-runway-exit silently completes the phase and skips all cleanup **[verified]** — ✅ FIXED

> **Fix applied.** `OnTick`'s `FollowingExitPath` branch rebuilds the route via `StartExitNavigation`
> when it comes back null, mirroring the sibling navigator-owning phases that defer their route build
> to the first tick after restore. If the rebuild fails (layout gone, or a stored edge no longer
> exists) it falls back to the centerline search — the same recovery the build-time failure path takes
> — rather than declaring the exit complete.
>
> `RunwayExitPhase.ExitState` became public so the test can construct the restore DTO.
>
> Test: `RunwayExitRestoreTests.RestoredMidExit_ContinuesFollowingTheExitPath_InsteadOfReportingComplete`,
> which derives real OAK hold-short/branch nodes from the layout at runtime rather than hardcoding ids
> (fillet node ids are geometry-coupled and shift when the fixture is regenerated).


- **File**: `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs:530-535` (the silent return), `:756-800` (`FromSnapshot`)
- **Severity**: **high** — restored/rewound sessions diverge from live; also the phase's only unlogged branch
- **Confidence**: high

`FromSnapshot` restores `_state` (to `FollowingExitPath`), `_exitPath`, and `_navigator` — but
**never rebuilds `_exitRoute`**. `TickFollowingExitPath` treats that as completion:

```csharp
private bool TickFollowingExitPath(PhaseContext ctx)
{
    if (_exitRoute is null || _navigator is null)
    {
        return true;                                   // "completed", silently
    }
```

**Failure scenario.** An aircraft lands OAK 28L, commits to exit G, and a snapshot is taken while it
follows the exit route. On restore the first `OnTick` returns `true`, so the phase list advances
**without** `CompleteExit`: no `HoldingAfterExitPhase` is inserted, `Ground.IsExpeditingExit` is
never cleared, `MarkHoldShortNodeOccupied` never runs (so another arrival can plan the same exit),
and the issue-#175 auto-pull-up `TryStartParallelCrossing` never fires. Nothing is logged.

`ToSnapshot` even persists what's needed to rebuild — `ExitWaypointIndex = _exitRoute?.CurrentSegmentIndex ?? 0`
(`:748`) — and `FromSnapshot` never reads it. Both sibling navigator-owning phases do the opposite:
`CrossingRunwayPhase.FromSnapshot` (`:317-334`) and `ClearRunwayPhase.FromSnapshot` (`:141-148`)
deliberately leave `_initialized = false` so `OnTick` rebuilds the route slice. `RunwayExitPhase` is
the diverged one.

**Why tests miss it.** `SfoRunwayExitTests.cs:70` restores at `t=380`, *before* the `EL T` at 382 —
so `_state` is `RollingOnCenterline` and the route is rebuilt fresh. No test restores while
`_state == FollowingExitPath`.

### 5. Every test ground layout is built with a 150 ft default runway width; production uses navdata **[verified]** — ✅ FIXED

> **Fix applied.** `TestAirportGroundData.GetLayout` now passes the real airport code, matching both
> production call sites, so runway widths come from navdata. It calls `TestVnasData.EnsureInitialized()`
> unconditionally first — the layout is cached per process, so leaving the width dependent on whether
> another test class happened to load nav data first would bake one arbitrary graph in for the whole
> run. Falls back to a null code when nav data is genuinely unavailable, preserving the silent-skip
> convention on a fresh/offline checkout.
>
> **Blast radius was 3 tests, not the ~290 feared** — and all three were stale hardcoded SFO node ids
> (`871`, `155`, `152`, `850`, `860`), not behavioural regressions. Widening SFO's runway rectangle
> from 150 ft to its real 200 ft reseats hold-shorts and renumbers the nodes
> `RunwayCrossingDetector` creates. Fixed by resolving nodes by name via the new
> `TestLayoutNodes` helper and `FindIntersectionNode`, exactly as the project's own footgun note
> prescribes. **Every semantic assertion was left identical** — route stops at the G/B intersection,
> never walks B, reaches the 10R hold-short on K, dedupes hold-shorts — and all still pass, which is
> what establishes that routing behaviour did not change.
>
> Note `docs/test-harness.md` was updated in the same change: it had asserted the harness "builds the
> production graph" and advised passing `runwayAirportCode: null`, which is what let this survive.


- **File**: `tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs:53` vs
  `src/Yaat.Sim/Data/Airport/AirportLayoutDownloader.cs:84` and
  `yaat-server/src/Yaat.Server/Data/AirportGroundDataService.cs:73`
- **Severity**: **high** — systemic test-fidelity gap; ~389 call sites across ~290 test files
- **Confidence**: high

The harness passes `null` for `runwayAirportCode`; both production call sites pass the real code:

```csharp
layout = GeoJsonParser.Parse(shortId, File.ReadAllText(path), null, _filletMode);   // tests
return GeoJsonParser.Parse(faaCode.ToLowerInvariant(), geoJson, faaCode);           // production
```

With `null`, `RunwayCrossingDetector` (`:47-48`) skips the navdata lookup and falls back to
`DefaultRunwayWidthFt = 150.0`. Consequences:

- the on-runway rectangle uses a 75 ft half-width regardless of the real runway, changing which
  nodes classify as on-runway and therefore **where hold-short nodes are seated**;
- `GroundRunway.WidthFt` is 150 for every runway in every test;
- for airports whose GeoJSON carries no `holdShortDistance`, standoff falls back to
  `HoldShortDistanceForWidth(widthFt)`. **`sfo.geojson` has no `holdShortDistance`** (oak.geojson
  does), so SFO hold-shorts sit at **250 ft in tests vs 280 ft in production** for its 200 ft runways.

**The harness doc is wrong about this**, which is how it survived: `docs/test-harness.md` says the
parameterless ctor "builds the production graph" and advises using the pre-initialized layout "only
when you actually need runway crossings" — but it passes `null`, so it never does crossings.
Separately, `AirportE2ETests.cs:271` *does* pass the code, so two different OAK graphs coexist in one
suite.

This does not necessarily mean any shipped behavior is wrong — it means the ground suite, which is
where this repo's bug history concentrates, validates against geometry production never uses.

### 6. Every Radar/Ground pop-out toggle permanently leaks a canvas, renderer, and 10 Hz timer **[verified]** — ✅ FIXED

> **Fix applied.** The repaint timer is now a field, started in `OnAttachedToVisualTree` and stopped in
> `OnDetachedFromVisualTree`. A stopped `DispatcherTimer` is not rooted by the dispatcher, so the
> abandoned canvas becomes collectible and its finalizer reclaims the renderer's native SkiaSharp
> objects — the leak was permanent only because the running timer kept it alive.
>
> **The constructor's `Start()` was redundant, which is how this hid.** The
> `DispatcherTimer(interval, priority, handler)` overload starts the timer itself, so simply moving the
> local into a field left it running before attach — the first version of the fix failed its own test on
> exactly that. It now uses `new DispatcherTimer(priority) { Interval = … }` plus a `Tick` subscription,
> which does not auto-start.
>
> `MapCanvasTimerLifecycleTests` pins attach → running, detach → stopped, for both canvases.
>
> **Still open from this finding:** `RadarCanvas.Dispose()` and `GroundCanvas.Dispose()` remain dead code —
> nothing in `src/` calls either. That is now a promptness question (native handles wait for finalization)
> rather than a leak, so it was left alone.

- **File**: `src/Yaat.Client/Views/Map/MapCanvasBase.cs:31-32`, `:161-168`;
  `src/Yaat.Client/Views/MainWindow.axaml.cs:1722-1737`
- **Severity**: medium-high — compounding, permanent, reachable through ordinary UI use
- **Confidence**: high

`MapCanvasBase` starts a `DispatcherTimer` held only in a constructor **local**, so nothing can stop
it, and its `Tick` delegate roots the canvas for the process lifetime. `OnInvalidateTick` repaints
unconditionally, with no attached-to-visual-tree check.

`OpenRadarViewWindow` does `new RadarViewWindow(...)` on **every** toggle; the close path only closes
the window and nulls the field. Each cycle abandons a live canvas still firing `InvalidateVisual()`
10×/sec at `DispatcherPriority.Render`, competing with real rendering.

**The leak is permanent, not eventually-reclaimed.** Both canvases implement `IDisposable`, but
**nothing in `src/` ever calls `Dispose()`** — `RadarCanvas.Dispose()` (`:1971`) is dead code.
Finalizers would normally still reclaim the native SkiaSharp handles, but the running timer roots the
canvas, so collection never happens. Each leaked canvas pins its renderer's full `SKPaint`/`SKFont`
set (~30 native objects in `GroundRenderer` alone).

**Why tests miss it.** `MainWindowLifecycleTests.GroundAndRadarPopOut_EachCreatesItsOwnWindow`
asserts a window is *created*, never that the prior canvas is torn down. Nothing toggles twice.

### 7. A pending `WAIT n <cmd>` restarts its full countdown after any snapshot restore; `BEHIND` can be dropped entirely — ✅ FIXED

> **Fix applied.** Extracted `CommandDispatcher.StripDeferralGateBlocks` and pointed all three sites at it —
> `TryDeferLeadingWait`, `TryDeferGiveWay`, and `DeferredDispatch.FromSnapshot`. Restore re-parses the
> stored text with the gate still attached, so it now strips that gate exactly the way the dispatch path
> does. One implementation is the point: the bug existed because the producers and the restorer had
> separate logic.
>
> Reaction delays are deliberately exempt — they store the whole command as their payload and carry no
> gate, so stripping would eat a real command. `RestoredReactionDelay_KeepsItsWholePayload` pins that,
> and it stayed green throughout while the two gated tests were red, which is what proved the fix had to
> be conditional rather than blanket.
>
> Mutation-verified: disabling the strip turns both gated tests red and leaves the reaction-delay test green.


- **File**: `src/Yaat.Sim/CommandQueue.cs:394` (`DeferredDispatch.FromSnapshot`); live path
  `src/Yaat.Sim/Commands/CommandDispatcher.cs:1313`, `:1334-1338`
- **Severity**: medium-high — deterministic divergence between a live session and its rewind/replay
  of the same timeline; affects bug-bundle re-sim and bookmark rewind, which triage depends on
- **Confidence**: high

`TryDeferLeadingWait`/`TryDeferGiveWay` store `SourceText = compound.SourceText` — the *full* text
**including** the leading `WAIT n` / `GIVEWAY <cs>` — while `Payload` is the stripped remainder.
`FromSnapshot` rebuilds `Payload` by re-parsing `SourceText`, so the restored payload carries the
prefix again and re-defers when it fires.

**Failure scenario.** `SWA100 WAIT 120 RWY 18L TAXI N B`. At t+60 the instructor rewinds to a
bookmark at t+30. The restored deferral has `RemainingSeconds = 90` but
`Payload = [[WAIT 120],[RWY 18L TAXI N B]]`. At t+120 it fires, `TryDeferLeadingWait` finds the
`WaitCommand` again and creates a fresh 120 s deferral — the taxi clearance is issued at ~t+240
instead of t+150. For `BEHIND KLM605 TAXI S T`, the restored payload re-enters `TryDeferGiveWay`,
which hard-rejects if the target has since been deleted (*"BEHIND target … not found"*), discarding
the taxi clearance entirely.

**Why tests miss it.** `DispatchContactSourceTests.cs:167-176` constructs exactly this shape
(`payload = ParseCompound("FH 270")`, `SourceText = "WAIT 5; FH 270"`), round-trips it, and asserts
**only** `restored.IsScenarioScripted` — never comparing `restored.Payload` to the original. No test
ticks a restored deferral to expiry.

### 8. `CLRWY` is permanently rejected after a snapshot restore, stranding an aircraft on the runway — ✅ FIXED

> **Fix applied.** `AircraftState.FromSnapshot` now re-links every restored `HoldingShortPhase` to the taxi
> route's own `HoldShortPoint` via `GetHoldShortAt`, restoring the shared-instance relationship the live path
> has. That fixes both halves: the phase regains `TailOverRunwayNodeId` (so `CLRWY` is available again), and
> its writes — clearing the hold-short — reach the route the rest of the sim reads.
>
> Chosen over simply persisting the missing fields on the DTO, which would have fixed the rejection but left
> the phase holding a detached copy whose writes go nowhere. Re-linking addresses the root cause this whole
> cluster shares: restore paths *rebuilding* objects instead of *re-resolving* them.


- **File**: `src/Yaat.Sim/Phases/Ground/HoldingShortPhase.cs:209-223`, `:177`;
  `src/Yaat.Sim/Commands/GroundCommandHandler.cs:2147`; DTO `PhaseSnapshotDto.cs:298-316`
- **Severity**: medium — user-visible and self-contradictory
- **Confidence**: high (auditor-reported, strong evidence; not independently re-verified by me)

`HoldingShortPhaseDto` carries only `HoldShortNodeId`/`RunwayId`/`Reason`, and `FromSnapshot`
constructs a **new** `HoldShortPoint` rather than resolving the one on `Ground.AssignedTaxiRoute`.
`TailOverRunwayNodeId` (plus `IsCleared`/`ClearedByAutoCross`/lat/lon) is lost, and later writes to
`_holdShort` no longer reach the route.

**Failure scenario.** An aircraft holds short with its tail over the bars (issue #172 W2 state). After
a rewind/bookmark/replay restore, `CanAcceptCommand(ClearRunway)` sees `TailOverRunwayNodeId == null`
and returns *"CLRWY only applies when holding short of a taxiway with the tail over a runway"* — while
`SimulationEngine.BuildOccupiedHoldShortNodes` still marks the runway hold-short occupied from the
route copy. The aircraft blocks the runway with no command able to move it.

The divergence is already known at the *other* consumer, which works around it —
`SimulationEngine.cs:2697-2700`: *"Read from the route — it survives snapshot restore, unlike the
phase's reconstructed HoldShort copy."* The two command-path consumers were never updated to match.
`TaxiRoute` does persist `TailOverRunwayNodeId` (`:235`, `:294`), so the data is available, just not
consulted.

### 9. `MidfieldCrossingPhase` / `TeardropReentryPhase` destroy the whole pattern circuit on a speed or altitude command — ✅ FIXED

> **Fix applied.** Both now apply the `IsAdditiveAirborneAdjustment` guard, matching every pattern leg
> around them. Verified the fix is not hollow: both set their entry speed in `OnStart` only (as
> `DownwindPhase` does), so a controller adjustment issued mid-phase actually stands rather than being
> re-asserted away each tick.
>
> **Scope narrowed after aviation review**: speed only, not speed *and* altitude. These are entry
> maneuvers, not legs, and `PatternEntryPhase` deliberately splits the axes — altitude clears the entry
> and warns the RPO, because a climb/descend during an entry usually means the aircraft is no longer
> being sequenced into that pattern. Matching it keeps all three entry phases consistent. It also avoids
> an accept-then-discard bug on the teardrop, whose waypoint route carries `At` altitude restrictions
> that `ApplyFixConstraints` rewrites on every sequencing — an accepted `CM`/`DM` would not have been
> flown.
>
> The coverage gap was that `PhaseAcceptanceAuditTests` enumerated pattern phases by hand and omitted
> both, so the new `PatternInterLegPhases` member data closes it permanently.


- **File**: `src/Yaat.Sim/Phases/Pattern/MidfieldCrossingPhase.cs:106`,
  `src/Yaat.Sim/Phases/Pattern/TeardropReentryPhase.cs:118`
- **Severity**: medium — **Confidence**: high

Both phases return a bare `CommandAcceptance.ClearsPhase`, omitting the `IsAdditiveAirborneAdjustment`
leading guard that `phases.md` documents as mandatory for lateral-guidance phases. They are the two
phases sitting *between* pattern legs, and the only pattern phases missing it (`FinalApproachPhase`
and `PatternEntryPhase` are the documented exceptions).

**Failure scenario.** `CTO MRT 28R` from runway 33. While on `MidfieldCrossingPhase` heading for
28R's downwind, the controller issues `SPD 120` (or `DM 1500` for TPA). Speed/altitude are not tower
commands, so `CanAcceptCommand` returns `ClearsPhase`; `CommandDispatcher.cs:220-224` runs
`Phases.Clear` and sets `Phases = null` — the whole `Downwind → Base → FinalApproach → Landing`
circuit is gone. The same command one leg earlier (`UpwindPhase`) or later (`DownwindPhase`) is
additive.

**Why tests miss it.** `PhaseAcceptanceAuditTests` enumerates pattern phases **by hand**
(Base/Crosswind/Downwind/Upwind/PatternEntry) and omits both. The ~20 other tests referencing
`MidfieldCrossingPhase` assert phase-list *construction*, never command acceptance.

### 10. `InterceptCoursePhase` ignores `LateralInterceptOnly` — a JFAC/JLOC join self-decelerates ~70 kt — ✅ FIXED

> **Fix applied.** The speed-anticipation block now also gates on the clearance not being lateral-only.
>
> Confirmed against the real recording rather than reasoned about: at t=1150 UAL4525 held **187 kt**
> before the fix (1.3×FAS for a B738) and **210 kt** after — exactly the STAR crossing restriction its
> sibling test says should be maintained — with cross-track still 0.00 nm, so the lateral join is
> unaffected. That 210 kt figure is what makes the fix self-evidently right. The E2E now asserts speed,
> which is the gap that let this through.


- **File**: `src/Yaat.Sim/Phases/Approach/InterceptCoursePhase.cs:124`
- **Severity**: medium — **Confidence**: high

The speed-anticipation block gates only on `!HasExplicitSpeedCommand`, not on
`ActiveApproach.LateralInterceptOnly`, so it applies approach deceleration to a lateral-only join.
`DispatchJfac` documents the opposite: *"it does NOT cancel a previously assigned speed … the
aircraft holds them through the intercept until CAPP."*

**Failure scenario.** `FH 220, JLOC I08R` on a B738 at 250 kt with no assigned speed. At cross-track
< 2.0 nm the phase writes `TargetSpeed = 1.3 × Vref ≈ 182 kt`. `FinalApproachPhase.OnStart` returns
early on `LateralInterceptOnly` (`:397`) and `OnTick` skips the decel block, so the aircraft holds
~182 kt level on the localizer awaiting `CAPP`, indefinitely.

**Why tests miss it.** The one JLOC-without-CAPP E2E
(`Issue184VectorsStarAppSpeedsTests.Ual4525_JfacWithoutCapp_HoldsAltitude…`) asserts only altitude
and cross-track. No test asserts speed on a JFAC/JLOC join.

### 11. `ProcedureTurnPhase` leaves a permanent 200 KIAS `SpeedCeiling` on every exit path — ✅ FIXED

> **Fix applied.** Added `OnEnd` releasing the cap — but only when the ceiling still equals the 200 KIAS
> the phase imposes. `ClampPtSpeed` never overwrites a ceiling already tighter than that, so such a
> ceiling belongs to the controller and has to survive the phase; releasing unconditionally would have
> silently dropped a real speed restriction. A guard test covers that case. (A controller ceiling of
> exactly 200 is indistinguishable from the phase's own and is released with it.)
>
> **`LowApproachPhase` was deliberately left alone.** The same fix was written for it and then reverted:
> aviation review showed its retarget ceiling is load-bearing, not a leftover. On that path the aircraft
> is *landing* on the new runway, and `PatternEntryPhase.OnStart` unconditionally commands
> `DownwindSpeed` with no `Kind` branch — so releasing the cap would command a jet from ~140 kt to
> 200 kt while turning onto a half-mile final. The real defect there is `PatternEntryPhase` ignoring
> `Kind == Final`; logged as follow-up rather than fixed here.


- **File**: `src/Yaat.Sim/Phases/Approach/ProcedureTurnPhase.cs:293-299`
- **Severity**: medium — silent, with no UI representation (`SpeedCeiling` is not an `Assigned*` field)
- **Confidence**: high

`ClampPtSpeed` writes `SpeedCeiling = 200` every tick, but the phase has no `OnEnd`, and neither
`PhaseList.Clear` nor the dispatcher's phase-clear block (`CommandDispatcher.cs:220-226`, which does
clear `TurnRateOverride`/`HasExplicitTurnRate`/`PreferredTurnDirection`) clears it.

**Failure scenario.** A CRJ on the KCCR VOR/DME-A procedure turn is pulled off with `FH 270 ; CM 8000`.
The phase clears but `SpeedCeiling = 200` persists, so `FlightPhysics.UpdateSpeed`'s layer-6 clamp
caps the jet at 200 KIAS for the rest of the session — climbing to 8000 at 200 instead of 250/280 —
until an `RNS`/`DSR`/`SPD` happens to clear it.

Every *other* `SpeedCeiling` writer in the phase set (`GoAroundPhase:102`, `TouchAndGoPhase:94`,
`StopAndGoPhase:92`, `LowApproachPhase:119`) nulls it in `OnStart`. Sibling instance:
`LowApproachPhase.cs:163` in retarget mode has the same leak.

### 12. Ghost track fields are absent from `StarsTrackFingerprint`, so re-ghosting never reaches CRC — ✅ FIXED

> **Fix applied.** `GhostIsUnsupported`, `GhostLat` and `GhostLon` added to `StarsTrackFingerprint` and
> captured in `CaptureStarsTrack`. Two tests in `ChangeDetectionTests` pin it — a re-placement to a new
> spot, and the `IsUnsupported` toggle that zeroes `GroundTrack`/`GroundSpeed` and drops the history
> trail — closing the ghost→CRC channel the suite had never covered. Both were RED first.
>
> Same class as `TrainingDtoFingerprint` (#305): any field a DTO converter reads must be fingerprinted,
> or the change is computed and silently not broadcast.

- **File**: `yaat-server/src/Yaat.Server/Simulation/AircraftChangeTracker.cs:42-90`, `:648-692`
- **Severity**: medium — narrow trigger, but the RPO gets a success message while the student's
  STARS scope silently keeps the old position, with no self-healing path
- **Confidence**: high on mechanism, medium on real-world frequency

`DtoConverter.ToStarsTrack` derives `Location`, `IsUnsupported`, `GroundTrack`, `GroundSpeed`, and
`History` from `ac.Ghost.*`, but `CaptureStarsTrack` captures **none** of them, so a ghost
re-placement produces an identical fingerprint and no `StarsTrack` change flag.

**Failure scenario.** An RPO places `GHOST 28R` on a departure holding short — this broadcasts fine,
because the overlay branch also writes `Track.Owner` (fingerprinted) and `CrcVisibilityTracker`
reports `StarsNewlyVisible`. The RPO then re-issues `GHOST 28L`. `Track.Owner` is reassigned to the
*same* identity, the aircraft is stopped so `Position`/`GroundSpeed` are unchanged, and it is already
STARS-visible so `newlyVisible` is false. No fingerprinted field moves → no `ReceiveStarsTracks` →
CRC renders the ghost at the first location indefinitely, while the RPO terminal shows success.

**Why tests miss it.** `ChangeDetectionTests.cs:387` asserts ghost mutations flip the **TrainingDto**
flag (they do — `Ghost.IsUnsupported`/`IsOverlay` *are* in `TrainingDtoFingerprint`). There is no
equivalent assertion for the **StarsTrack** flag, so the suite covers the ghost→YAAT-client channel
and omits ghost→CRC entirely. All 6 ghost test files exercise a *fresh* target, never a second call.

### 13. `RadarViewModel.BrightnessLookup` is shared by reference into the render snapshot and mutated in place — ✅ FIXED

> **Fix applied.** `CreateRenderSnapshot` now copies it, matching its three sibling fields and the rule
> `docs/radar-rendering.md` already states: *"all mutable interaction state that the renderer needs is
> defensively copied into the snapshot."* The copy runs on the UI thread (`MapCanvasBase.Render` builds
> the snapshot before handing it to the custom draw operation), so it cannot race the rebuild either.
>
> Copying at snapshot time rather than at `SetBrightnessLookup` is deliberate: the canvas is handed the
> view-model's instance once on load, so a copy there would freeze the lookup and never pick up a video
> map load. **Verified by inspection, not by test** — the race needs a real compositor render thread, and
> `Yaat.Client.UI.Tests` is headless with `parallelizeTestCollections: false`, so the two threads never
> coexist under test. `Render(DrawingContext)` is the only entry point and needs a live drawing context.

- **File**: `src/Yaat.Client/Views/Radar/RadarCanvas.cs:935`;
  `src/Yaat.Client/ViewModels/RadarViewModel.cs:293`, `:615-619`
- **Severity**: medium — unsynchronized read/write of a non-thread-safe `Dictionary` on Avalonia's
  render thread
- **Confidence**: high

`CreateRenderSnapshot` defensively copies `_dataBlockOffsets`, `_minifiedCallsigns`, and
`_highlightedCallsigns` — but passes `_brightnessLookup`, the view-model's live long-lived instance,
**by reference**:

```csharp
_brightnessLookup,                                   // by reference
new Dictionary<string, SKPoint>(_dataBlockOffsets),  // copied
new HashSet<string>(_minifiedCallsigns),             // copied
```

**Failure scenario.** The radar renders at 10 Hz; the render thread is inside
`VideoMapRenderer.Render` calling `brightnessLookup.GetValueOrDefault(...)` per video map. The UI
thread concurrently runs `ApplyVideoMapsDto` on a scenario load, doing `BrightnessLookup.Clear()`
then N inserts (each a potential resize). The render thread holds the *previous* snapshot, whose map
list is still non-empty, so it keeps looking up IDs in a dictionary being rebuilt underneath it — a
torn read at best, `InvalidOperationException` from `FindEntry` at worst, which kills the render loop.

Every other by-reference field in the snapshot was checked and is safe: `ShownPaths`/`ShownShapes`,
`Fixes`, `PinnedMarkers`, `ProgrammedFixNames` are all **assigned fresh collections**, never mutated
in place. `BrightnessLookup` is the only one that is both shared and mutated.

**Why tests miss it.** A timing race with no deterministic trigger, and `Yaat.Client.UI.Tests` runs
headless with `parallelizeTestCollections: false` — there is no compositor render thread, so the two
threads never coexist under test.

### 14. A partially-superseded conditional block is rebuilt without its trigger state — zombie queue or double-fire — ✅ FIXED

> **Fix applied.** `SplitBlockNonConflicting` now copies `IsApplied`, `TriggerMet`, `TriggerCrossingObserved`,
> `TriggerMissed`, and `TriggerClosestApproach` alongside the wait counters and track guard it already carried.


- **File**: `src/Yaat.Sim/Commands/CommandDispatcher.cs:2540-2548` (`SplitBlockNonConflicting`)
- **Severity**: medium — **Confidence**: medium (zombie outcome deterministic; re-fire is timing-dependent)

`CreateBlock` produces a fresh `CommandBlock` with all runtime flags at default, and the split path
copies back only `WaitRemainingSeconds`, `WaitRemainingDistanceNm`, and `TrackApplied`. `IsApplied`,
`TriggerMet`, `TriggerCrossingObserved`, `TriggerMissed`, and `TriggerClosestApproach` are equally
per-block runtime state — `CommandBlock.ToSnapshot`/`FromSnapshot` persists all of them, and
`FlightPhysics.ApplyReadyConditionalBlocks:1297-1300` documents `TriggerMet` as a **latch** precisely
because `IsTriggerMet` flips back to false once the aircraft passes the fix.

**Failure scenario.** `AAL1 CM 10000; LV 5000 FH 270, SPD 210`. The lookahead fires block 1 climbing
through 5,000 ft (`IsApplied`/`TriggerMet` true) while `CurrentBlockIndex` stays on `CM 10000`. The
controller then issues `AAL1 SPD 250`. The block is partially split, keeping `FH 270`; the rebuilt
block re-enters with `IsApplied = false`, `TriggerMet = false` and a `ReachAltitude(5000)` trigger the
aircraft is already 2,000 ft above. It never completes, pinning the queue forever — or, if still
inside the 10 ft snap window, re-applies `FH 270`, overriding whatever heading was issued in between.

**Why tests miss it.** `SplitBlockTrackCommandTests` and `SplitBlockLabelTests` both split a block
that has **never fired**, so the lost state is invisible.

### 15. `EL`/`ER`/`EXIT <twy>` during an active `RunwayExitPhase` is acknowledged but ignored — ✅ FIXED

> **Fix applied**, taking the finding's second option: refuse rather than echo. `TryExitCommand` now
> returns *"Unable — already exiting at J"* once the phase has handed a route to the navigator
> (`CommittedExitTaxiway`), and is unchanged while the aircraft is still tracking the centerline.
> Reproduced first: committed to J, `EXIT B` returned `Success=True, "Exit at B"`.
>
> Refusing was chosen over `LandingPhase`-style re-resolution because tearing down a committed route
> mid-turn hands the pure-pursuit navigator an off-centerline aircraft with no route — the failure mode
> that surfaces as an orbit exception. A pilot already established in the turn would say unable. Honoring
> a change that arrives while the route is committed but the aircraft has not yet reached the turn-off
> would be a genuine improvement on this, and is not attempted here.

- **File**: `src/Yaat.Sim/Phases/Ground/RunwayExitPhase.cs:171-182`; `GroundCommandHandler.cs:2223`
- **Severity**: medium — the controller gets a success echo for an instruction never followed; nothing logged
- **Confidence**: medium (code path certain; whether refusing a late change is *intended* is arguable)

The mid-phase preference re-check is gated on `_holdShortNode is null` and is unreachable once
`_state == FollowingExitPath`, because `OnTick` returns at `:171-174` first. `TryExitCommand` only
writes `Phases.RequestedExit`; nothing clears `_holdShortNode`/`_exitRoute`. So `EXIT T` returns
`Ok("Exit at T")` and the aircraft still turns off at D.

`LandingPhase` implements the same re-check correctly and without a commit gate
(`Phases/Tower/LandingPhase.cs:636-643`), dropping the committed candidate so the next tick
re-resolves. The two rollout phases have diverged.

**Either way it is a defect:** if refusing late changes is intended, `CanAcceptCommand` should
*reject* rather than return a success echo.

### 16. `DesiredDecelRate` leaks out of `LandingPhase` when a rollout is broken off with `GA` — ✅ FIXED

> **Fix applied.** `GoAroundPhase.OnStart` now clears `DesiredDecelRate` alongside the speed and altitude
> floors/ceilings it already cleared, satisfying the contract `docs/flight-physics.md` states explicitly.
>
> Aviation review added an important correction: the leak runs **both** ways, and the slow direction is
> worse. `TickRollout` also *lowers* the rate, to 0.5 kt/s, when the planned exit is far enough off that
> normal braking would reach coast speed too early. A go-around taken at that moment carries 0.5 kt/s
> onto the circuit, where the aircraft physically cannot bleed from downwind speed to Vref — an unstable
> approach every circuit, not merely a brisk one.


- **File**: `src/Yaat.Sim/Phases/Tower/LandingPhase.cs:772`; `src/Yaat.Sim/Phases/Tower/GoAroundPhase.cs:101-104`
- **Severity**: low — wrong deceleration rate on the subsequent approach — **Confidence**: medium

`TickRollout` writes a ground braking rate into `ControlTargets.DesiredDecelRate` every tick, and only
`TickHandoff` (`:829`) clears it. `GoAroundPhase.OnStart` explicitly clears `SpeedFloor`,
`SpeedCeiling`, `AltitudeFloor`, and `AltitudeCeiling` — but not `DesiredDecelRate`.

`docs/flight-physics.md` states the contract explicitly: *"`DesiredDecelRate` … must be cleared on
phase transition or firm braking leaks into the next phase."*

**Failure scenario.** A jet lands with `EXP` (`ExpediteExitDecelRate` = 7.5 kt/s), then the controller
issues `GA` at 70 kt (above the 60 kt gate). On the re-flown circuit every deceleration —
downwind→base, base→final, the FAS bleed — runs at 7.5 kt/s instead of the airborne 3.5 kt/s.

**Why tests miss it.** The only clearing path (`TickHandoff`) is what every landing test exercises;
no test issues `GA` from `Rollout` and then inspects `Targets.DesiredDecelRate` — and per finding 2,
that command path is untested at the dispatcher level entirely.

### 17. The documented `ONH` alias is rejected by the client canonicalizer, so it is dead as a condition prefix

- **File**: `src/Yaat.Sim/Commands/CommandSchemeParser.cs:45`, `:319` vs
  `src/Yaat.Sim/Commands/CommandParser.cs:207`
- **Severity**: low-medium — a documented alias is unusable; the user gets a misleading error and
  nothing is sent — **Confidence**: high

`CommandParser.ParseBlock` accepts both `ONHO ` and `ONH ` as a condition prefix, but the client
canonicalizer — which `MainViewModel.SendCommandAsync` runs *before* sending — knows only `ONHO ` in
both its `isCompound` sniff and its `ParseBlockToCanonical` switch.

**Failure scenario.** `UAL123 FH 270; ONH CM 120` → client parse fails, status bar shows *"ONH does
not accept arguments — expected: ONHO"*, nothing is sent. `COMMANDS.md:528` lists `ONH` as an alias
of `ONHO`, and `:250` documents `ONHO <cmd>` as a precondition form.

**Why tests miss it.** No test in either repo passes `ONH ` as a condition prefix.
`CommandSchemeCompletenessTests` only checks that every `CanonicalCommandType` has *some* alias, not
that each alias works in every grammatical position it is documented for.

### 18. `DrainAllPilotSpeech` is the only `SimulationWorld` drain the engine never calls **[verified]** — ✅ FIXED

> **Fix applied.** `TickPostPhysics` now drains it alongside the other five, emitting a
> `PilotSpeech`-kind terminal entry exactly as `TickProcessor.BroadcastPilotSpeech` does — which is
> also what `AircraftState.cs:154` already documented ("Drained per tick into the terminal as
> `PilotSpeech`-kind entries").
>
> The engine clears `_terminalEntries` at the end of every tick path ("client doesn't use them yet"),
> so a terminal emit alone is unobservable in-engine. A `PilotSpeechEmitted` event was added mirroring
> the existing `WarningEmitted` / `StripDispatchRequested` outlets, which exist for exactly this — so
> non-server consumers can observe a drained buffer.
>
> **`RpoPilotSpeechReplayTests` was written around the bug** — its own doc said "in the embedded engine
> there's no draining" and it asserted on the leaked buffer. Rewired to the event, it now observes
> **32** transmissions across the session instead of the handful that happened to survive to the end.
> Its sibling "setting off" test was asserting `PendingPilotSpeech.Count == 0`, which the fix would have
> made vacuously true; it now counts emissions.

- **File**: `src/Yaat.Sim/Simulation/SimulationEngine.cs:1346-1389`; `src/Yaat.Sim/SimulationWorld.cs:366`, `:373-380`
- **Severity**: low — **replay/standalone path only, not production**
- **Confidence**: high

`TickPostPhysics` calls five of the six `DrainAll*` methods plus `DrainReadyPilotTransmissions`,
omitting exactly `DrainAllPilotSpeech`. The live server calls all seven, so this is **replay-dark,
not live-dark** — the inverse of this codebase's classic two-brains trap.

The engine itself produces into that buffer along paths that run in replay
(`SimulationEngine.cs:951/989/1012` inside `TickVisualDetection`, `HoldingShortPhase.cs:60`,
`DownwindPhase.cs:230`, `AirborneFollowHelper.cs:615`, `ContactCommandHandler.cs:119`), and only that
drain clears it. So under `TickOneSecond`/`ReplayOneSecond`, `PendingPilotSpeech` grows for the whole
session, and any `Assert.Contains(x, ac.PendingPilotSpeech)` passes for a message from *any earlier
tick*.

### 19. Restored conflict sets are frozen for the rest of a hybrid replay **[verified]** — ✅ FIXED

> **Fix applied** (approach chosen by the user). `TickPostPhysics` now calls `TickConflictAlerts([])` and
> `TickEramConflictAlerts()`, discarding the returned diffs, so the standalone/replay path re-evaluates
> conflicts instead of carrying whatever the snapshot restored.
>
> This narrows the "return-value seam is server-only" rule to what it was actually about — broadcast
> entanglement. Discarding the diff broadcasts nothing, and the server never calls `TickPostPhysics`, so there
> is no double-run. The risk was that replay-driven tests would start observing real conflicts; the full suite
> passed unchanged.


- **File**: `src/Yaat.Sim/Simulation/SimulationEngine.cs:438`, `:531-558`, `:1048`, `:1110`
- **Severity**: low today, but a **latent test-integrity hazard**
- **Confidence**: high

`RestoreFromSnapshot` repopulates `ConflictAlerts.Conflicts`/`EramConflicts.Conflicts`, but
`TickConflictAlerts`/`TickEramConflictAlerts` are server-only-invoked and never run under replay. So
in the `Replay → RestoreFromSnapshot → ReplayOneSecond` pattern a restored conflict is never
re-evaluated and **never clears**. A test asserting `Assert.Contains(id, engine.ConflictAlerts.Conflicts)`
passes on the restored ghost; a test asserting "no CA fired" passes **vacuously** against a
permanently-empty set and would survive a regression making the detector fire every tick.

Scope limit (checked): the server's own rewind path is unaffected (`RecordingManager` drives
`Engine.AdvanceOneSecond()`), and `TickOneSecond` has zero production call sites.

### Smaller items

- **`TrueHeading`/`MagneticHeading` normalization launders NaN/Infinity into NaN**
  (`TrueHeading.cs:10`): `((x % 360) + 360) % 360` yields `NaN` for both `NaN` and `±Inf`, so a bad
  heading is accepted as a "normalized" value and propagates silently. Not known to be triggered
  today — but it is why any finite-state guard must assert *finiteness*, not range.
- **`SimulationEngine.TickOneSecond()` silently no-ops when `Scenario is null`** (`:1430-1434`). A
  harness that forgets to set `Scenario` ticks zero times and fails with a misleading
  "did not arrive in budget" instead of a wiring error.
- **`GeoMath.PointInRing` has zero test coverage** — it is the point-in-polygon behind
  `AirspaceVolume` containment (Class B/C boundary holds) and `MvaSector` classification.
- **`ExpandMultiCommandHeuristic` is unreachable dead code** (`CommandSchemeParser.cs:990`): the
  `tokens.Length >= 2` guard at `:885` always short-circuits, so the heuristic can only run when
  `Length < 2 && Length >= 4`. No wrong output, but it is dead weight.
- **Doc drift**: `docs/command-pipeline.md` §1 claims *"The client does **not** canonicalize before
  sending"* — stale, and materially misleading: `MainViewModel.cs:2074` sends
  `compound.CanonicalString`. This staleness refuted three otherwise-plausible findings (the
  `CommaBeforeCondition` client/server split, `THEN`/`AND` bypassing `TrySplitSpecialCompound`, and
  `ONHS` missing from the comma-promotion regex) — all unreachable *because* the client canonicalizes.
  Also: `docs/training-hub-contract.md`'s "13 fields" list is stale (code is correct);
  `docs/test-harness.md` mis-describes `TestAirportGroundData` (finding 5); `CommandSchemeCompletenessTests:77`
  lists a stale `H` alias collision; `docs/plans/` has no `MAIN.md` index.

---

## Follow-ups surfaced by aviation review (not fixed here)

Raised while reviewing the phases cluster; each is a real defect but outside the scope of the finding
that surfaced it.

### F1. `PatternEntryPhase.OnStart` commands pattern speed regardless of entry kind — ✅ FIXED

`PatternEntryPhase.OnStart` wrote `TargetSpeed = DownwindSpeed(...)` with no `Kind` branch. For a
`PatternEntryKind.Final` entry — which the `#292` low-approach runway retarget creates at 0.5 nm from
the threshold — that commands the aircraft *up* to pattern speed while turning onto a half-mile final.
Today the `LowApproachPhase` retarget ceiling masks it, which is why finding 11's fix was deliberately
not applied to that phase.

> **Fix applied.** `OnStart` now commands the speed of the leg being joined: `Final` →
> `ApproachSpeed`, `Base` → `BaseSpeed`, everything else → `DownwindSpeed`. `Base` is included because
> it is the same defect — a base entry hands straight to `BasePhase`, which immediately commands
> `BaseSpeed`. Both are alignment with what the successor phase already does, not new behavior.
>
> **Measured magnitude, correcting the estimate above:** for a B738 with its loaded profile the entry
> commanded **161 kt** where final needs **144** and base **152.5** — a 17 kt overspeed onto short
> final, not the ~60 kt the original note projected. The ~200 kt figure came from the category default
> (`CategoryPerformance.DownwindSpeed(Jet)`), which only applies to types with no profile. The defect
> is real at 17 kt; the note overstated it for profiled types.
>
> RED confirmed before the fix (both `Final` and `Base` returned 161), and a third test pins that
> downwind-family kinds still command pattern speed.
>
> **Second correction: the `#292` path cannot produce the jet scenario at all.**
> `Cland33_JetOnLowApproach_RejectedAsLightAircraftManeuver` shows the retarget rejects jets outright —
> the tight ~1 nm final on a diverging runway is unflyable for one. So the low-approach retarget only
> ever builds a `Final` entry for light aircraft. F1 remains a genuine defect for any `Final` entry;
> the worked example was wrong on two counts.
>
> **Finding 11 is now also complete for `LowApproachPhase`.** With the entry commanding approach speed,
> the retarget's per-tick `SpeedCeiling` was both redundant and leaking — a test confirmed the cap
> (77 kt for the recording's BE36) survived into `Pattern Entry` and beyond, with nothing releasing it.
> The ceiling was removed rather than released in `OnEnd`, since `TargetSpeed` alone holds the low pass
> down. This is the change the aviation review held back in the phases cluster; its stated precondition
> is now met.

### Aviation review of F1–F3 (second round)

The review **caught a regression I introduced in F1** and corrected two claims I had made.

- **F1 was over-scoped and had to be narrowed.** `OnStart` selected on `Kind` alone with no distance
  term, but the *default* Final entry point is the glideslope/TPA intercept —
  `PatternAltitudeAgl / FeetPerNm(3°)`, about **4.7 nm** for a jet — and `EF FINAL <dist>` can place it
  further. So the fix commanded Vref for the whole straight-in, and nothing walked it back:
  `ManagesSpeed` suppresses the auto speed schedule, and `FinalApproachPhase.OnStart` only ever *lowers*
  speed. For an unprofiled jet that is 200 → 140, held from wherever the entry begins — squarely outside
  the 170/210 kt floors of §5-7-3.c.1.b, and it defeats the staged 1.3·Vref → Vref profile. The `Final`
  arm is now gated on the entry point being within **2 nm** of the threshold (`CloseInFinalEntryNm`),
  which is what the #292 retarget builds at 0.5 nm. `Base` was confirmed fine and kept — it is
  geometrically bounded to `wp.BaseTurnLat/Lon`. The original test was 3 nm from its entry point and so
  pinned the over-scoped behavior; it now uses a close-in entry and has a distant-entry counterpart.
- **F2's commit message cited a precedent that only half exists.** `FinalApproachPhase` respects
  `HasExplicitSpeedCommand` in `OnTick` only — its `OnStart` overrides unconditionally. That is not an
  oversight to clean up: it is the safety net that commands Vref outright on a close-in base-to-final
  rollout, and it is what makes an uncancelled pattern speed safe. Now pinned by
  `FinalApproachOnStart_OverridesAnExplicitSpeed_CloseIn` so a later consistency pass cannot delete it.
- **F3 shipped as-is**, with 45° confirmed as the smallest tolerance covering every legal intercept
  (§5-9-2 TBL 5-9-1: 30°, 20° close-in, 45° for helicopters). Two defects were fixed on top: `IsOnFinal`
  was direction-only, so an aircraft on **upwind** — same heading, flying away — counted as on final; it
  now also requires the aircraft to be flying toward the threshold. And my edit had split
  `IsInboundToLand`'s doc comment from its declaration, leaving the constant with two `<summary>` blocks.
- **My "follow adjustments only ever slow the aircraft" was wrong.** The clamp is against the *leg
  baseline*, not the assigned speed, and the helper returns a value on every tick while following — so a
  FOLLOW silently overwrites a controller's assigned speed down to the leg baseline. Leaving spacing
  unguarded is still correct; the gap is that it happens invisibly. Logged as F5.

### F5. A FOLLOW silently overrides a controller-assigned speed

`AirborneFollowHelper` clamps to `Math.Min(adjusted, legBaseline)`, so once a FOLLOW is active an
assigned speed is reduced to at most the leg baseline every tick — a C172 assigned 180 kt with a FOLLOW
drops to ≤90 kt with no controller action and no pilot transmission. Guarding it would break spacing;
the fix is an advisory per AIM §4-4-12.h ("pilots are expected to advise ATC of the speed that will be
used") — e.g. *"unable one eighty, slowing for traffic"* through `PilotResponder`.

### F6. No 91.117(b)/(c) 200-kt cap anywhere

The sim enforces only 91.117(a) (250 below 10,000). Now that an assigned speed survives a whole circuit,
`SPD 250` at TPA is reachable. Fine in a Class C/D surface area (AIM §4-4-12.k lets ATC approve it), but
not beneath a Class B shelf or in a VFR corridor, where 200 kt binds and the pilot is expected to refuse
(AIM §4-4-12.i). There is an airspace database (`src/Yaat.Sim/Data/Airspace/`) to key this off.

### F7. `LandingPhase` and `FinalApproachPhase` disagree about Vref

`LandingPhase.BuildPlan` uses `CategoryPerformance.ApproachSpeed(cat)` (Jet 140) while
`FinalApproachPhase` uses per-type `AircraftPerformance.ApproachSpeed` (B738 144). The 1.3·Vref
unstabilized-go-around gate is therefore computed on a different Vref than the profile feeding it —
about 5 kt tighter. Pre-existing, spotted while tracing the pattern-speed chain.

### F2. `DownwindPhase.OnStart` reverts a controller speed assignment one leg later

`DownwindPhase.OnStart` unconditionally rewrites `TargetSpeed` and `TargetAltitude` with no
`HasExplicitSpeedCommand` guard, so a speed adjustment issued during an entry maneuver is discarded on
reaching downwind. Per 7110.65 §5-7-4 it is the controller who terminates a speed adjustment
("RESUME NORMAL SPEED"); the aircraft does not revert on its own at the next leg.

### F3. `SPD` is rejected inside 5 nm for any aircraft inbound to land

`FlightCommandHandler.ApplySpeed` rejects `SPD` whenever `IsInboundToLand` and inside 5 nm — which
includes a VFR pattern aircraft already cleared to land, making a tower speed instruction to pattern
traffic impossible. The cited §5-7-1.b.4 is a Chapter 5 (Radar) rule; §3-8-1 (tower pattern spacing)
does not reference it and its NOTE 2 treats speed as a normal in-pattern spacing tool.

### F4. `DecelRate` is aggressive for clean flight

3.5 kt/s (~0.18 g) is realistic only while configuring — flaps and gear extending on downwind/base.
Clean and level at idle a transport jet decelerates nearer 0.5–1 kt/s. Harmless today because the value
is used almost exclusively in approach/pattern contexts, but it would be roughly 4× too fast if ever
applied to a clean en-route reduction such as `SPD 250` from 290 at altitude.

---

## Test-suite integrity findings (replay corpus)

A separate class from the product defects above: these do not break the app, they **break the
suite's ability to detect breakage**. Audited across 115 `.zip` archives + 40 legacy fixtures against
188 consuming test files.

### 20. `SMF.geojson` is uppercase — its test has never run on CI **[verified]** — ✅ FIXED

> **Fix applied.** Rather than rename the file (which fixes today's instance and leaves the next
> oddly-cased fixture to reintroduce it), `TestAirportGroundData.GetGeoJsonPath` now falls back to a
> case-insensitive directory match. `SMF.geojson` is deliberately left uppercase so the repo keeps a
> live example exercising that path on CI. Guarded by `TestAirportGroundDataCaseTests`, which asserts
> **every** committed `*.geojson` resolves through the harness. Verified by forcing the fast path off
> so all lookups routed through the fallback — the whole suite still passed.


- **File**: `tests/Yaat.Sim.Tests/TestData/SMF.geojson`;
  `tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs:74`; `RunwayEntryPointTests.cs:132`
- **Severity**: medium — a one-character bug that silently disables a test
- **Confidence**: high

`SMF.geojson` is the **only** uppercase geojson tracked in the repo; `GetGeoJsonPath` does
`shortId.ToLowerInvariant()` and looks for `smf.geojson`. Windows resolves this case-insensitively,
so it passes locally. CI runs `ubuntu-latest` (`ci.yml:14`) where the filesystem is case-sensitive →
`File.Exists` is false → `GetLayout` returns null → the test hits its silent-skip bail and reports
green.

So `TwoEntrancesOnTheSameSide_OnlyTheNearerIsFullLength` — which pins the "same side is always two
entrances" half of the `RunwayEntryPoint` full-length rule — **has never executed in CI**. Fix is a
`git mv`.

### 21. Replay assertions that cannot fail on the bug they were written for — ✅ FIXED

> **Fix applied.** Every conditional assertion below either became unconditional or gained a latch,
> and every silent post-replay `return` became a hard assert. Two of the rewrites were driven by
> measurement rather than by the original diagnosis:
>
> - **`FollowRunawayIasTests`** — the ceiling assertion fired on only **8 of 205 ticks**, because
>   physics snaps `TargetSpeed` to null once the leg speed is reached (`DownwindPhase.cs:260`). A
>   "TargetSpeed was set often enough" latch would therefore fail forever. The assertion now also runs
>   on `IndicatedAirspeed`, which is present every tick and is the observable the bug actually produced
>   (167 KIAS on short final), plus an `Assert.Equal(205, ticksObserved)` latch.
> - **`Issue172Wja1521CurrentTaxiwayTests`** — the unasserted `movingSeconds` was 93/120 not because of
>   a stall but because WJA1521 **arrives at spot 2 at t=92** and parks. A plain moving/total ratio
>   would have scored arrival as a stall. Stationary ticks are now counted only while the aircraft is
>   in `TaxiingPhase` (`stalledSeconds <= 5`), plus an explicit assertion that it arrives.
>
> Both new assertions were mutation-verified: lowering the ceiling to 70 kt fails at t=266, and
> widening the movement epsilon reports 92/92 stalled. `OakCross28RHoldShortTests` also stopped pinning
> geometry-coupled node id `186` — the exit-side bar is now resolved as the 28R/B hold short farthest
> from the aircraft.

- **Severity**: medium — these read as coverage but are not

- **`N44444SpawnCollisionTests`**: `Assert.True(matches.Count <= 1, …)` is satisfied by `0`, so it is
  green when the aircraft never spawns, and nothing asserts the survivor is the spawned aircraft
  rather than the ghost. The class doc concedes it: *"When the ghost path doesn't fire, this test
  passes vacuously even without the fix."*
- **`TowerCabActualAircraftTypeTests`**: the bundle's only null-`AircraftType` amendment targets an
  FP-only entity that never enters the sim (per the method's own doc). If `AmendFlightPlan` regressed
  to writing through to `AircraftState.AircraftType`, no live aircraft is touched and
  `blankActualType` stays 0. It is a "replay doesn't crash" smoke check.
- **`FollowRunawayIasTests.N346G_TargetSpeedStaysWithinFollowCeiling`**: the ceiling assertion only
  executes when `TargetSpeed` is non-null inside t=266–470. There is no post-loop
  `Assert.True(sawSomething)` latch (its sibling test has one), so replay drift moving N346G out of
  that window yields **zero assertions executed** and a green report — with the 578 kt compounding bug
  present.
- **Silent post-replay bails**: several tests `return` *after* `engine.Replay(...)` rather than at the
  documented top-of-test skip point, so fixture drift turns them green instead of red —
  `N70csCrossStopsOnRunwayTests`, `OakCross28RHoldShortTests` (which also pins geometry-coupled node
  id `186`), both `OakHsDirectionalHintTests`, both hold-short tests in `SfoHoldShortTaxiwayTests`,
  and `IssueAmxTaxiOvershootTests:59`.
- **`Issue172Wja1521CurrentTaxiwayTests:97-118`**: computes and logs `movingSeconds` but never asserts
  it — the anti-spin half of the documented check is missing; only `traveledFt > 200.0` fires.

### 22. Orphaned recordings — regression coverage that no longer runs — ✅ FIXED

> **Fix applied** (deletions approved by the maintainer). 24 fixture files totalling **4.4 MB** that no
> `.cs` file references were removed — the 20 legacy `.br` recordings and 3 legacy `.json` from the
> abandoned format migration, plus `issue-sfo-28r-el-t-recording.zip` and
> `issue142-sfo-rwy01r-shallow-recording.zip`. The 12 assertion-free `Diagnostic_*` facts that still
> replayed a full bundle per CI run were deleted too. (`oak-taxi-recording.br` is *referenced* and was
> kept; the `*.br` set is not uniformly dead.)
>
> **Two corrections to the finding below, both found by measuring rather than re-reading:**
>
> - **`conflict-stop-after-behind` was abandoned for a different reason than its doc states.** Snapshot
>   node ids are *not* the obstacle: replaying the session from t=0 rebuilds every taxi route from the
>   recorded commands, and that replay runs fine. The real obstacle is that ten minutes of ground
>   movement **diverges** — at t=595 the re-simulated N569SX is still taxiing 432 ft away instead of
>   parked 98 ft off N152SP's nose, and N152SP is already rolling at 20 kt with no speed limit. A
>   replay-based test therefore passes without touching the conflict detector. I wrote that test,
>   measured it, and **reverted it** rather than land vacuous coverage; the accurate reason is now
>   recorded in the test class so the next person doesn't repeat the experiment. The bundle is kept as
>   provenance for the hand-built coordinates.
> - **`Issue10OnHoldShortDeleteTests` does issue `ONHS DEL`** — twice, with a `sawHoldingAfterExit`
>   latch and `Assert.Fail` fallbacks. Only its `Diagnostic_*` fact was assertion-free. The claim below
>   is wrong.
>
> The assertion-free count was 15, not 16; 3 of those (`Skw3078FixComparisonCapture`) are already
> `[Fact(Skip=…)]` and cost nothing, so 12 were deleted.
>
> **New finding, surfaced by the deletion: a yaat-server test writes into the yaat repo's source
> tree.** After deleting the 20 `.br` fixtures, 17 of them **reappeared** — freshly written, with
> different bytes, at 1–2 s intervals during the test run.
> `Yaat.Server.Tests/MigrateTestRecordingsTest.MigrateAllRecordings` is self-described one-shot
> tooling ("Run manually when needed") but is a plain `[Fact]`, so it runs on every `dotnet test`. It
> resolves the **sibling yaat repo's** `TestData` directory, enumerates `*-recording.json`, re-simulates
> each into a v2 recording, and `File.WriteAllBytes`es a `.br` next to it. It looked dormant only
> because `if (File.Exists(brPath)) continue` skipped everything while the outputs existed — deleting
> them re-armed it. That is also where the dead `.br` corpus came from in the first place.
>
> The test file was deleted (yaat-server). `MigrateToV2` itself is **not** dead — `TrainingHub.cs:1178`
> calls it — so only the test went. All 18 `*-recording.json` sources are genuinely referenced by
> tests; the `.br` copies were the duplicates.

- **`conflict-stop-after-behind-recording…zip` (1.4 MB) — de-facto orphan, most serious.** `rg` finds
  the filename so it looks consumed, but `LoadRecording()` and `BuildEngine()` are **never called**;
  the single `[Fact]` hand-builds two `AircraftState`s at hardcoded lat/lons. The doc explains why:
  *"We can't replay that snapshot directly because `TaxiRoute.FromSnapshot` drops the route when node
  IDs don't survive layout regeneration."* Ground deadlock is the most-represented bug class in this
  corpus, and the synthetic test pins one frozen instant rather than the real 590 s session. The
  reason it was abandoned is itself the unfixed geometry-coupled-node-id footgun.
- **`issue-sfo-28r-el-t-recording.zip` (2.3 MB) — never had an asserting test.** Its consumer was
  deleted in the 2026-05-10 `Diagnostic_*` sweep, and it had been a pure `output.WriteLine` dump with
  zero asserts. The captured sequence (blows past T, cuts through grass, snaps, 180s, oscillates) is
  not exercised by the partial coverage elsewhere.
- **`issue142-sfo-rwy01r-shallow-recording.zip` (353 KB) — dead weight; bug still covered** by a
  hardcoded-pose test. What's absent is the taxi→hold-short→LineUp handoff the recording captured.
- **Legacy**: `issue136-pattern-entry-recording.json`/`.br` never had a test at all. 20 of 21 `.br`
  fixtures are unreferenced dead weight (~1.7 MB) from an abandoned format migration.
- **16 assertion-free `Diagnostic_*` facts** still load and replay a full bundle per CI run while
  asserting nothing (or only `Assert.NotNull`). Notably `Issue10OnHoldShortDeleteTests` never issues
  `ONHS DEL` — the command under test.

### 23. Runway-only stub layouts defeat the null-layout guard — ✅ FIXED

> **Fix applied.** `TestLayoutCoverageTests` now pins both halves of the gap against committed lists:
> the set of runway-only fixtures (`FAT`, `HWD`, `MER`, `RNO`, `SJC` — each parses to an **empty** node
> and edge graph, not merely a taxiway-less one) and the set of airports a recording's manifest declares
> a layout for but whose fixture cannot support ground movement (`ASE`, `HOU`, `SJC`, each with a
> recorded reason). The assertions run in both directions, so adding a new stub, adding a recording that
> lands on one, or filling a gap without updating the list all turn red. Mutation-verified.
>
> **Correction to the finding below:** `issue286-cfix-wait-descent` does **not** declare a FAT ground
> layout — its manifest has `LayoutAirportIds: null`, meaning the recorded session had no ground layout
> in use at all, so replaying it needs no ground graph. The scenario is FAT-based (S3-FAT-3) but the
> stub is not reached. The genuinely declared-but-degraded set is `ASE`, `HOU`, `SJC`.

Five geojsons contain **only runway `LineString`s — zero taxiways, parking, or hold-shorts**:
`mer` (552 B), `sjc` (566 B), `fat` (702 B), `hwd` (1068 B), `rno` (1198 B). Because `GetLayout`
returns **non-null**, the documented null-layout bail never fires and tests cannot detect the
degradation. Recordings whose manifests declare a real ground layout at a stub airport:
`issue216-auto-handoff…` and `multi-cfix-preset…` (SJC primary, 58 refs) and
`issue286-cfix-wait-descent…` (FAT primary, 40 refs).

Separately, **`hou.geojson` is missing entirely** while `issue187-star-dvia-recording`'s manifest
records `LayoutAirportIds: ["iah","hou"]` — so any HOU ground aircraft replays with
`GroundLayout == null`, the exact documented degradation path, and per the footgun it would surface
on an *incidental* aircraft rather than the asserted one. (The ASE equivalent is known and
worked around in that test's doc; HOU is undocumented.)

**Recordings' own bundled maps do not protect them**: `RecordingLoader` returns only
`ToBaseSessionRecording()`, and the engine resolves layouts exclusively through
`TestAirportGroundData`, so the `layouts/`/`airport-geojson/` entries inside each archive are never
used by test replay.

## Verified sound — the post-physics refactor holds

The recent 6-commit post-physics ownership refactor was audited step-by-step against the live server
path. **No live-dark per-tick logic exists**: every step `TickPostPhysics` runs has a server
counterpart; `TickPrePhysics`/`TickPhysics` have a single shared implementation reached identically by
both hosts at the same sub-tick rate (4); and `TickPrePhysicsResult` is fully consumed by the server.
Nothing has drifted back in since the final migration commit `c5047fd7`. `ProcessAsdexAlerts` staying
server-side is a documented decision, not a gap.

One note for future authors: the two hosts run the shared post-physics steps in **different orders**
(engine: PilotProactive → TransponderIdents → VisualDetection; server: TransponderIdents →
VisualDetection → … → PilotProactive). Harmless today — `Pilot/` reads no `.Approach.` or
`.Transponder.` state — but it will silently matter the day someone adds a cross-dependency.

## Cleared — verified NOT bugs

Recorded so future hunts don't re-tread them:

- **`TrainingDtoFingerprint` completeness.** All ~90 `AircraftStateDto` wire fields diffed against
  `CaptureTrainingDto`. The unfingerprinted ones are documented exceptions or derived from
  already-fingerprinted inputs. No hole.
- **Client SignalR UI-thread marshalling.** Every inspected handler wraps its body in
  `Dispatcher.UIThread.Post`. Clean.
- **`AutoPullUpToParallel`** correctly wired across all four DTOs in both repos.
- **No empty catch blocks** in the server `src/`.
- Ground-stack suspects checked and dropped as correct or documented-intentional: fillet bezier
  tangent sign, `RouteMaterialiser` truncation indices, `AutoRouter` pruning, `HoldShortAnnotator`
  pairing, `PushbackPhase` legs, `TaxiRoute.TruncateAt`.
- **Structurally guaranteed, do not build guards for**: heading ∈ 0–360 (`TrueHeading` normalizes on
  construction) and monotonic sim clock (`FastForwardTo` already throws on rewind).
- **`TrainingHub.cs` early returns**: every bare `return;` is the documented `ResolveEngine`→null /
  "Not in a room" sentinel. Value-returning methods correctly return a failure DTO. Nothing silently
  drops a valid user action.
- **`GroundRenderer` image disposal**: follows the documented "drop the reference, don't Dispose
  mid-render" pattern. No path can dispose a bitmap a render pass still holds. (The previous
  airport's `SKImage` leaks on each ground-view switch, but that is the deliberate trade the prior
  bug established.)
- **`ScenarioLifecycleService` reset**: the promising asymmetry (`ExecuteUnloadScenario:410` clears
  `Generators` but not `VfrArrivalGenerators`/`OverflightGenerators`) is **not** a leak —
  `TrainingRoom.ActiveScenario` is derived from `ActiveSim?.Scenario` and `ActiveSim` is set to null,
  so the whole `SimScenarioState` is discarded. The `Generators.Clear()` is redundant defensive code.
- **Airborne suspects dropped**: the `Math.Clamp` min>max crash in
  `AirborneFollowHelper.ComputeAdjustedSpeedWithDesired` (needs `ApproachSpeed > BaseSpeed + 20`,
  unreachable from any profile); empty-`Fixes` dereference in `ApproachNavigationPhase` (all three
  construction sites guard); `InterceptCoursePhase.HandleBustThrough` detached phase list
  (`PhaseList.Clear` makes `AdvanceToNext` a no-op); `STurnPhase.OnEnd` speed resume (gated off).
- **Command suspects dropped**: the `H` alias collision (stale test comment), the
  `CommaBeforeCondition` client/server split, `ONHS` comma-promotion, and `THEN`/`AND` vs
  `TrySplitSpecialCompound` — all unreachable because the client canonicalizes before sending.
- **`RadarCanvas.cs:774-778`** mirrors bound `ShowMvaAltitudeTint` onto a renderer field read during
  draw, bypassing the snapshot — acknowledged with a comment, and a `bool` cannot tear.

---

## Appendix — recommended dynamic bug-finding build-out

Context: `GroundNavigator.ThrowOnOrbit` is the **only** test-escalation guard in the entire
simulation. There is **no `Debug.Assert` anywhere in Yaat.Sim**, and only 5 files reference
`IsNaN`/`IsFinite` at all. No property-testing library is referenced; the repo's own
`SerializableRandom` (Xoshiro256\*\*, serializable state ⇒ reproducible seeds) makes one unnecessary
for most of the below.

**Build these three first:**

1. **Finite-state guard** (~1 h) — `FlightPhysics.ThrowOnNonFiniteState`, set in `ModuleInit`,
   asserting `Position`, `Altitude`, `IndicatedAirspeed`, `VerticalSpeed`, `TrueHeading`, `BankAngle`
   are finite at the end of `FlightPhysics.Update`. Highest leverage per hour in the plan because it
   needs **no sweep**: it retro-fits onto all ~420 existing Sim test files, all nightly grid cases,
   and every replay, at ~zero runtime cost. It copies the shape of the one guard that already works
   (`ThrowOnOrbit`: throw in tests, log-and-recover in the app) and hardens a failure the codebase
   already *tolerates* — `FlightPhysics.cs:67` logs a non-finite position and continues, and the
   downstream `MagneticDeclination` crash is already documented in the E2E guide.
2. **Taxi-grid extension** (~2–3 h) — `TaxiCoverageRunner.Run` across all 16 committed airports (2
   are swept today), both directions (only parking→runway today), plus a jet/piston type axis.
   Almost pure data: `TaxiBudgetDeriver` derives budgets per pair, so nothing needs calibrating, and
   at ~8 ms/case the nightly job stays in minutes. This is where the repo's bug history lives.
   Riders: the zero-groundspeed invariant is already free inside that runner, and
   `FilletComparisonGates.ValidateStructural` over all 16 layouts is a cheap layout-only add.
3. **Snapshot fixed-point** (~2–3 h) — assert `Capture → Restore → Capture` serializes identically,
   run at the end of every taxi-grid case and over all 115 committed recordings. One generic
   assertion replaces ~30 hand-written per-feature round-trip tests and closes the top documented
   integration footgun. It is also the only cheap way to catch a missing `[JsonDerivedType]` before
   it detonates, since that failure currently gets misdiagnosed as a static-singleton race.

**Deliberately not first**: per-phase termination budgets (judgment calls, likely need aviation
review), command-canonical round-trip (needs a `CommandRegistry`-driven valid-command generator), and
anything requiring a new NuGet package.

**Highest-yield property targets** (no new dependency needed): `GeoMath.ProjectPoint`/`BearingTo`/
`DistanceNm` round-trip and the untested `PointInRing`/`BlendBearings`/`DistanceToSegmentFt`;
`WindInterpolator` IAS↔TAS and Mach↔IAS inverse pairs; `CommandSchemeParser` canonical idempotence
plus cross-parser acceptance (only one test crosses the two parsers today);
`RunwayIdentifier` pad/de-pad (72 values, exhaustive); `HoldingEntryCalculator` left/right mirror
symmetry; `MetarComposer` ↔ `MetarParser` tolerance-aware round-trip (15 Compose facts today, zero
round-trips).
