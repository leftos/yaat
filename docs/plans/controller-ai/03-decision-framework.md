# 03 — Decision-Policy Framework

Part of [Controller AI + Soak Harness](README.md). Covers how per-position rules are organized,
determinism, pacing, and rejection/anomaly handling. The concrete rule sets live in
[04](04-ground-brain.md) and [05](05-tower-brain.md).

## Organization: priority-ordered guarded rules + per-aircraft intent memo

Each brain = an ordered rule list evaluated over its jurisdiction set, plus an `AiAircraftMemo` per
(position, callsign):

```csharp
interface IDecisionRule
{
    int Priority { get; }                  // fixed; safety first
    bool Applies(AircraftState ac, AiAircraftMemo memo, AiTickContext ctx);
    void Act(AircraftState ac, AiAircraftMemo memo, AiTickContext ctx);  // emits via Sink, updates memo
}
```

- Rules are ordered by fixed priority: **safety rules first** (go-around, cancel takeoff clearance)
  — these run every tick unconditionally, even for aircraft marked stuck. Routine rules (answer
  requests, clearances, handoffs, CTs) run below, subject to pacing.
- The memo carries a small intent state machine per role (Ground:
  `AwaitingTaxiRequest → TaxiIssued → AtHoldShort → HandedToLocal → …`), last command + when, retry
  count, in-flight flag. States are chosen so each is re-derivable from world state
  ([01](01-architecture.md) — the `Reset()` contract).

Why this hybrid over the pure alternatives: pure guarded rules re-derive intent every tick (fragile
pacing, re-issue storms); pure FSMs duplicate the world model the phases already are. The memo FSM
tracks only *what I last said and what I'm waiting for*; the guards read the world.

## Determinism

- **Stable iteration:** the snapshot is sorted `StringComparer.Ordinal` by callsign before rule
  evaluation; brains tick in the fixed order of [01](01-architecture.md) (Ground → Local → Approach
  → Center, ordinal PositionId within role).
- **Dedicated RNG stream:** `AiControllerService.AiRng : SerializableRandom`, seeded from
  `ControllerAiConfig.Seed` and re-seeded on `Reset()` (decision 2026-09-01: owned by the service, not
  snapshotted — consistent with [01](01-architecture.md)'s accepted "not bit-identical across a rewind"
  limitation and avoids a snapshot schema change) — AI variability never perturbs pilot/physics RNG
  draws and vice versa.
- **Stateless jitter where possible:** per-aircraft think-time jitter uses the FNV-1a callsign-hash
  pattern (the `ReleaseAutoCtoJitterSeconds` precedent) — replay-safe, no RNG state.
- **Determinism regression test (CA0 acceptance, permanent):** same scenario + seed twice →
  byte-identical action logs.

## Pacing (a frequency is serial)

`AiPacing` per position:

- Minimum inter-transmission gap (default ~5 s, jittered ±2 s).
- Per-decision think time: 2–8 s from event observation to transmission.
- At most **one transmission per position per tick**.

Result: no every-tick spam, realistic cadence, bounded per-tick work.

## Rejection and anomaly handling

`AiCommandOutcome` failures become `AiAnomaly` records in an `AiAnomalyLog` on scenario state:

| Kind | Meaning |
|---|---|
| `CommandRejected` | dispatcher/phase rejected an AI command the brain believed valid |
| `StuckAircraft` | immobile in jurisdiction beyond threshold |
| `UnansweredPilotRequest` | open pilot request past the follow-up horizon while AI-staffed |
| `HandoffUnaccepted` | HO pending past the patience watchdog |
| `ConflictAlertInAiJurisdiction` | CA pair involving AI-controlled traffic |
| `GoAroundIssued` | informational — frequent GAs indicate a sequencing bug |
| `CoordinationTimeout` | bus request unanswered |
| `CoordinationCompleteMissing` | crossing completed on the ground but no §3-1-3.c completion report reached Local |

- **Bounded retry:** a rule may retry a rejected command at most **2** times with pacing backoff;
  then the aircraft is marked stuck (safety rules still apply) and left alone. An unexpected
  rejection is *the product* for the soak harness — retrying forever would mask bugs.
- Hosts drain the log: the soak harness turns entries into findings
  ([08](08-detectors-and-findings.md)); live rooms emit `[AI]` warning terminal lines.
- **Decision log:** every issued command carries an `AiIntent` tag (position, triggering rule,
  rationale) recorded alongside the outcome — the triage flow uses it to attribute
  separation findings (AI misjudgment vs sim bug).

## House-rule obligations

- Every decision rule is TDD'd (failing test first, real navdata via
  `TestVnasData.EnsureInitialized()`, real airport layouts).
- Every rule set gets `aviation-sim-expert` review before implementation and re-review after.

## CA0 observer thresholds (2026-09-01)

| Rule | Opens when | Closes when |
|---|---|---|
| `StuckAircraftRule` | a movement phase (taxi, pushback, crossing, clear-runway, runway exit, follow, air-taxi, line-up, takeoff) shows < 50 ft net displacement from its last progress anchor for 180 s — 600 s while the ground-conflict detector has it yielding (`Ground.SpeedLimit` set: a departure queue is sequencing, 7110.65 §3-8-1). A controller-ordered stop (`Ground.Hold`, a hold for release) never counts | the aircraft moves ≥ 50 ft, enters a non-movement phase, is ordered to hold, or leaves |
| `UnansweredPilotRequestRule` | a request this role answers (taxi → Ground; takeoff/landing → Local; approach/airspace entry → radar) is still open after the pilot has had to ask again (`LastRequestedAtSeconds > FirstRequestedAtSeconds`; the pilot's own clock — `NormalFollowUpDelaySeconds` 120 s, re-based to 90 s by a STANDBY — so an acknowledged wait never counts). A takeoff request is never overdue while the departure is held for release (AIM 5-2-7) | the request is answered or dropped |
| `HandoffUnacceptedRule` | (radar roles) a handoff to or from the position is pending longer than `AutoAcceptDelay` + 60 s (a house threshold — §2-1-17.a is qualitative) | accepted, recalled, or the aircraft leaves |
| `ConflictAlertInAiJurisdictionRule` | a terminal conflict alert names an aircraft in the position's jurisdiction (one episode per AI position involved) | the engine clears the alert or a controller acknowledges it |
| `CommandRejected` (service) | the dispatcher rejects an AI command | point event |

## CA1 notes (2026-09-01)

- **Pacing is constants, not config** (`AiPacing`): `MinGapSeconds` 5 jittered ± 2 s from `AiRng`, `ThinkMinSeconds`/`ThinkMaxSeconds`
  2–8 s from `FinalApproachSpeedVariety.UnitInterval(callsign, rule)` (stateless), and `IssuedThisTick` — one transmission per position
  per tick. `AiRuleScope.TryIssue` is the single emission path: it starts the aircraft's think-time clock for the rule (`AiAircraftMemo.
  Observe`), and once the think time and the gap have elapsed issues the request and marks it in flight.
- **Memo FSM** (`AiAircraftMemo`): `GroundIntent` {None, TaxiIssued, CrossingRequested, CrossingIssued, HandedToLocal, TaxiInIssued},
  `InFlight` + `IssuedAtSeconds` + `EffectDeadlineSeconds` (reaction delay + 15 s; a command whose outcome never arrives counts as
  rejected), `Rejections` / `NextAttemptAtSeconds` (backoff 10 s × rejections) / `GaveUp` (after `MaxRetries` = 2 retries), the
  think-time observation, and the crossing bookkeeping. `GroundBrain.SettleOutcomes` matches the host's outcomes to the in-flight
  memos before any rule runs. Every intent re-derives from world state after `Reset()`; the accepted cost is timing (a duplicate `CT`).
- **`CoordinationTimeout`** joins the anomaly kinds: Ground asked a staffed Local for a crossing (one terminal line per bar) and the bar is
  still uncleared 120 s later; closes when the crossing is cleared or the aircraft leaves.
- **Stuck watchdog refinement:** the yield state is sticky for the stall (`AiAircraftMemo.YieldedDuringStall`) — the ground-conflict
  detector lifts its limit a moment before the aircraft rolls, and a five-minute queue wait must not read as a 180 s non-yield stall.
