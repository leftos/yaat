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
- **Dedicated RNG stream:** `SimScenarioState.AiRng : SerializableRandom`, seeded from
  `ControllerAiConfig.Seed`, snapshotted like the existing pilot-delay stream — AI variability never
  perturbs pilot/physics RNG draws and vice versa.
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
