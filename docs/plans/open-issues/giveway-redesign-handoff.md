# GIVEWAY / BEHIND redesign — handoff

## Why this exists

`GIVEWAY` / `BEHIND` / `GW` is YAAT's ground separation command — "hold until this other aircraft has passed." It works today, but the implementation is split across five layers with no central authority, and it doesn't talk to the new `GroundConflictDetector` at all. That's fine for the simple case but limits what we can do with the command.

This document is the handoff for a fresh look. Read it cold; nothing about the current architecture is sacred.

## What the user can issue today

```
GIVEWAY <callsign>           # immediate hold
GW <callsign>                # alias
BEHIND <callsign>            # alias; usually used as a condition

<command>; BEHIND <callsign> # deferred dispatch — run <command> after <callsign> passes
<command>, BEHIND <callsign> # parallel condition variant
```

Registered in `CommandRegistry.cs:661–668` as `CanonicalCommandType.GiveWay`. The parser (`CommandSchemeParser.cs:41–207`, `658–668`) treats `GIVEWAY`/`BEHIND`/`GW` as both a command and a compound-block condition keyword.

## How it works today (five layers)

| Layer | File | What |
|---|---|---|
| **Command handler** | `GroundCommandHandler.TryGiveWay` (lines 911–926) | Sets `aircraft.Ground.IsHeld = true` and `aircraft.Ground.GiveWayTarget = callsign`. Rejects if not on ground or no taxi route assigned. |
| **State** | `AircraftGroundOps.IsHeld` / `GiveWayTarget` (lines 40–41) | Two fields, snapshotted via `AircraftGroundOpsDto`. |
| **Phase honoring** | `TaxiingPhase` / `AirTaxiPhase` / `CrossingRunwayPhase` / `FollowingPhase` / `PushbackPhase` / `PushbackToSpotPhase` / `RunwayExitPhase` | Each phase short-circuits its `OnTick` when `IsHeld=true`. Distributed implementation. |
| **Auto-resume** | `FlightPhysics.UpdateGiveWayResume` (lines 1178–1207) and `IsGiveWayMet` (1306–1352) | Per-tick: if target is gone / airborne / past us, clear `IsHeld` + `GiveWayTarget`. Geometry-based ("ahead and moving away" or "no longer head-on"). |
| **Deferred dispatch** | `SimulationEngine.IsGiveWayDeferredMet` (1502–1511) + the `DeferredDispatches` loop (1410–1469) | Conditional form: `<cmd>; BEHIND <X>` queues `<cmd>`; engine watches each tick until `IsGiveWayMet` returns true, then dispatches. |
| **Conflict detector** | `GroundConflictDetector` | As of the recent rewrite, `IsHeld=true` classifies the aircraft as `Stationary` so passing traffic with lateral clearance isn't blocked. That's the ONLY interaction. |

## What works well

- The geometry-based release condition in `IsGiveWayMet` is the smart part. It handles both opposite-direction ("target is now behind me") and same-direction ("target moved ahead and is pulling away") cases without the user specifying which.
- Deferred dispatch via `BEHIND` lets controllers queue follow-up actions atomically: `TAXI A B C; BEHIND SWA123`.
- Phase-side `IsHeld` checks are simple and uniform — no special-case state machine.
- After the recent fix, an active `GIVEWAY` doesn't cause secondary blockages for unrelated taxiing traffic.

## What doesn't work as well

### 1. The detector is blind to the relationship

The `GroundConflictDetector` doesn't know aircraft A is holding for aircraft B. It treats them as two unrelated obstacles. Consequences:

- The convergence resolver may "discover" the same pair the controller already sequenced, and might pick a *different* winner than the controller's intent. (Doesn't actively misbehave today because the held aircraft classifies as `Stationary`, but the detector's pair-classification still spends cycles on it.)
- The operator UI has no visibility into "SWA123 → giving way to NKS456" — only the raw `GiveWayTarget` field, which isn't surfaced.
- Detector-induced yielding (`Converging` pair → yielder gets a low SpeedLimit) is anonymous — no audit trail of who's yielding to whom.

### 2. No "give way" emerges automatically

A controller-initiated GIVEWAY is one expression of an idea the detector already computes. Today there's no unification:
- Controller: "SWA123, give way to NKS456" → IsHeld + GiveWayTarget set.
- Detector: "SWA123 converges with NKS456 — slow SWA123 to 5 kts" → SpeedLimit set on SWA123.

These should be the same thing. Right now they're two parallel paths producing the same observable outcome (SWA slows for NKS), but with different state, different UI, different auto-release semantics.

### 3. BEHIND as a condition has a constraint asymmetry

`BEHIND` in the conditional form (`TAXI A B C; BEHIND X`) gates command dispatch. But there's no way to express "ONCE X has passed, then also do Y" without chaining. And no way to say "wait until you can pass X" if X never moves.

### 4. The auto-release geometry is a heuristic

`IsGiveWayMet` is well-thought-out for two-aircraft cases (opposite vs same direction), but:
- It doesn't consider whether the held aircraft's route still intersects the target's route. A target that's "past" the held aircraft but still ahead on a shared upcoming taxiway is logically still a blocker.
- It releases on `target.IsOnGround == false` (airborne) — fine for runway hold cases, but for taxi cases the target might fly past the held aircraft's path without the conflict being resolved.
- The 60°/120° heading-diff thresholds work for clean cases but probably misfire on curving taxiways.

### 5. Phase-side IsHeld checks are duplicated

Each ground phase has its own `if (IsHeld) return;` branch. Adding a new phase requires remembering this. Easy to forget, hard to test.

## Design directions worth exploring

### A. Make the detector the authority

Treat GIVEWAY as a controller-supplied input to the detector, not a parallel system. The detector's pair classifier already produces a `Converging` decision with a yielder; let GIVEWAY override or pin that decision.

```
ClassifyPair(A, B):
  if A.GiveWayTarget == B.Callsign:
    → ControllerGiveWay(yielder=A)
  elif B.GiveWayTarget == A.Callsign:
    → ControllerGiveWay(yielder=B)
  else:
    → existing classification
```

Pros: unified state, single source of truth, observable in DebugSink.
Cons: phases still need to enforce `IsHeld`; controller-initiated holds need to survive across pair re-evaluation.

### B. Surface yielding relationships in the UI

If the detector writes `GiveWayTarget` on the convergence yielder, the operator sees in the aircraft list / context menu: "yielding to SWA123 (auto)" vs "yielding to SWA123 (you said GIVEWAY)". Differentiated badge so the operator knows what's instructor-controlled vs system-detected.

Requires:
- DTO field for "yield reason" (Controller / Auto-converging / Auto-trailing).
- Detector writes the field alongside SpeedLimit; controller commands write the same field.
- UI consumes the field.

### C. Move IsHeld enforcement into one place

A `GroundHoldEvaluator` that all phases call instead of each phase implementing `if (IsHeld) return;`. Or a base class method. Or a phase mixin. Whatever the codebase prefers — but stop repeating the check across 7+ phases.

### D. Improve auto-release

The geometry heuristic in `IsGiveWayMet` could consider:
- Whether the target has cleared the held aircraft's *route*, not just the current relative bearing.
- A timeout (e.g., 5 minutes) before forcing release with a warning, so a typo'd target callsign doesn't pin an aircraft forever.
- The target's own ground state — if the target has been stationary for >30s, the held aircraft can probably proceed if it has lateral clearance.

### E. Extend the conditional grammar

`BEHIND X` is a single-condition gate. Useful extensions:
- `BEHIND X+Y` — wait for two aircraft, useful for sequencing.
- `AFTER X CROSSES Y` — distance/landmark based.
- `WHEN X LANDS` — phase-transition based.

Out of scope for the detector rewrite but worth flagging if redesigning the command surface.

## Non-goals

- **Don't replace the geometry-based release condition.** It works for the common cases and refining it is a separate, smaller task.
- **Don't rewrite the deferred-dispatch system.** `BEHIND`-as-condition lives in `SimulationEngine`'s deferred queue and that's the right place for it; only the geometric release check (`IsGiveWayMet`) could be improved.
- **Don't add a new command syntax** unless the controller workflow demands it. The existing `GIVEWAY <callsign>` form is fine; the work is behind the API.
- **Don't merge GIVEWAY with HOLDPOSITION semantically.** HOLDPOSITION is unconditional ("stop and don't move until I say"). GIVEWAY is conditional ("hold for this specific reason"). The fact that they both set `IsHeld=true` is implementation, not intent.

## Suggested reading order

1. `src/Yaat.Sim/Commands/GroundCommandHandler.cs:911-926` — `TryGiveWay`.
2. `src/Yaat.Sim/AircraftGroundOps.cs:40-41` — state fields.
3. `src/Yaat.Sim/FlightPhysics.cs:1178-1207` and `1306-1352` — `UpdateGiveWayResume` + `IsGiveWayMet`.
4. `src/Yaat.Sim/Simulation/SimulationEngine.cs:1410-1469` and `1502-1511` — deferred dispatch.
5. `src/Yaat.Sim/GroundConflictDetector.cs` (whole file) — the new pair classifier this work should integrate with.
6. `src/Yaat.Sim/Phases/Ground/*Phase.cs` — search for `IsHeld` in each.

## Starting questions for the new agent

- What's the right authoritative model? Is "GIVEWAY is a controller-supplied input to the detector" the right abstraction, or should the detector be unaware of controller intent entirely?
- Should the conflict detector be able to *induce* a GIVEWAY (writing `GiveWayTarget` on the auto-yielder) so the operator sees who's yielding to whom?
- Where should the per-phase `IsHeld` checks live? A shared evaluator, base-class method, or stay where they are?
- Is there a UI surface change needed (datablock badge, context-menu entry, aircraft-list column)?
- Are there scenario examples in `docs/atctrainer-scenario-examples/` that exercise GIVEWAY heavily and could become regression tests?

Pick a subset, propose, then implement.
