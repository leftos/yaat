# BEHIND grammar extensions

## Why this exists

`BEHIND <callsign>` is YAAT's only conditional-block gate for ground sequencing. Real controllers sequence on a richer vocabulary:

- "Behind the SWA and the NKS landing, taxi to the ramp." — *multiple* aircraft.
- "After UAL crosses M, taxi to the ramp." — *landmark* condition.
- "When LH123 lands, switch to ground." — *phase-transition* condition.

YAAT currently expresses only the first one if you chain commands: `TAXI A B C; BEHIND SWA; ... ; BEHIND NKS` — but the chaining is awkward and the parser doesn't actually support it cleanly (the deferred-dispatch loop fires one payload per condition firing).

## What's proposed

Three additive condition keywords. Each gates a deferred-dispatch block. The existing `BEHIND <callsign>` stays unchanged.

### 1. `BEHIND <X>+<Y>` — multi-callsign

```
TAXI B C D; BEHIND SWA123+NKS456
```

Waits for **both** aircraft to satisfy the existing `IsGiveWayMet` geometry before dispatching the payload.

Parser: add `+` as an in-condition separator. Lexer accepts `SWA123+NKS456` as a single token of type "callsign list".

`DeferredDispatch.GiveWayTarget: string` becomes `GiveWayTargets: List<string>` (with snapshot DTO migration — `string GiveWayTarget` stays as a fallback). `SimulationEngine.IsGiveWayDeferredMet` becomes "ALL targets pass" instead of "the target passes".

### 2. `AFTER <X> CROSSES <Fix>` — landmark

```
TAXI A B C; AFTER SWA123 CROSSES M
```

Waits until aircraft `SWA123` has passed within N feet (configurable; default 100 ft) of the named taxi point `M` (taxiway, hold-short fix, or runway threshold). Resolves via `AirportGroundLayout.FindSpotByName` — same path as `TAXI` already uses.

Encoded as a new condition kind in `ParsedCommand` (the parser already returns `GiveWayCondition`; add `AfterCrossesCondition(callsign, landmarkName)`). New deferred-dispatch field `CrossesTarget: (string Callsign, string Landmark)?` in `DeferredDispatch`. New evaluator in `SimulationEngine.ProcessDeferredDispatches` that compares positions tick-over-tick.

### 3. `WHEN <X> LANDS` — phase transition

```
HO LH123 SOC; WHEN LH123 LANDS
```

Or pair with a SAY command:
```
SAY LH123 SWITCH GND; WHEN LH123 LANDS
```

Waits until the named aircraft enters `LandingPhase` (any landing kind) and reaches `HoldingAfterExitPhase` or `AtParkingPhase` (i.e. completes the landing roll). Encoded as `WhenLandsCondition(callsign)`.

Evaluator polls `target.Phases.CurrentPhase` each tick. Released when the phase chain matches.

### Risks

- **Parser scope creep**: adding three condition keywords means changing `CommandSchemeParser` in three places, plus `ConditionPrefixes`, plus three new tests for each. The grammar surface is fragile — every test in `CommandSchemeParserTests` needs to be re-validated.
- **Backwards compat**: all three forms must coexist with the existing `BEHIND <callsign>` syntax. The parser must not greedily consume a `+` that was meant as something else.
- **Aviation realism review**: aviation-sim-expert should sanity-check that these forms match real-world ATC phraseology (FAA 7110.65 covers some; AIM clarifies pilot expectations). E.g., is "WHEN LH123 LANDS" the controller's actual phrasing or should it be "AFTER LH123 LANDS"?

### Out of scope (for this plan)

- Negative conditions like "UNLESS X" — no real ATC analog, skip.
- Time-based conditions (`AFTER 30 SECONDS`) — `WAIT` already does this.

## Implementation checklist

### Shared

- [ ] Add `BehindCondition.cs` / `AfterCrossesCondition.cs` / `WhenLandsCondition.cs` types (or extend a `DispatchCondition` discriminated union if one fits).
- [ ] Migrate `DeferredDispatch` from a single `GiveWayTarget` field to a `Condition: DispatchCondition` polymorphic field (or keep flat fields with origin discriminator — pick during impl).
- [ ] Update `SimulationEngine.ProcessDeferredDispatches` to dispatch to the right evaluator per condition kind.
- [ ] Aviation-sim-expert review of phraseology + canonical forms.

### `BEHIND X+Y`

- [ ] Parser: lexer accepts `+` inside a condition argument; tokenises `SWA123+NKS456` as a list.
- [ ] `IsGiveWayDeferredMet(aircraft, callsigns: IReadOnlyList<string>)` returns true only when ALL are met.
- [ ] Tests: `BehindGroundTaxiTests.MultiTarget_WaitsForBoth`, `_FiresWhenLastPasses`.

### `AFTER X CROSSES Y`

- [ ] Parser: `AFTER <callsign> CROSSES <landmark>` (three-token compound). Add to `ConditionPrefixes`.
- [ ] Landmark resolution via `AirportGroundLayout.FindSpotByName` (returns position or null).
- [ ] Evaluator: distance from target aircraft's position to landmark position falls below threshold AND target moves past (uses similar geometry to `IsGiveWayMet`).
- [ ] Tests: scenario with SWA approaching M → landmark crossing fires deferred dispatch.

### `WHEN X LANDS`

- [ ] Parser: `WHEN <callsign> LANDS` (three-token). Add to `ConditionPrefixes`.
- [ ] Evaluator: polls `target.Phases.CurrentPhase` for `LandingPhase` complete (transitions to `RunwayExitPhase` or `HoldingAfterExitPhase`).
- [ ] Tests: scenario with LH123 on final → fires deferred after touchdown + rollout.

### UI / docs

- [ ] `USER_GUIDE.md`: document each new condition with examples.
- [ ] `COMMANDS.md`: extend the Conditional Blocks section.
- [ ] `docs/yaat-vs-atctrainer.md` if these features diverge from ATCTrainer.
- [ ] CHANGELOG.md `### Added` for each form (could be three separate commits or one bundled).
- [ ] Delete this plan after merging all three.

## Verification

1. `BEHIND X+Y`: scenario where two aircraft must both clear before a third proceeds; visual confirmation in `Yaat.Client` that the deferred-dispatch panel (if any) shows both pending targets.
2. `AFTER X CROSSES Y`: aircraft taxiing on a long route; deferred command fires only after the named aircraft passes the named landmark, not before.
3. `WHEN X LANDS`: HO command queued until target lands; confirm via terminal/log that the dispatch fires at the touchdown + rollout transition.
4. Parser regression: every existing `BEHIND <single>` test in `BehindGroundTaxiTests` + `WaitCommandDispatchTests` continues to pass unchanged.
