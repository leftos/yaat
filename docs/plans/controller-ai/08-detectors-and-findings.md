# 08 — Detectors, Findings, and Triage

Part of [Controller AI + Soak Harness](README.md). The anomaly-detection framework (Yaat.Sim), the
findings report, and the triage tooling. Runner mechanics in [07](07-soak-runner.md).

## Detector framework (`src/Yaat.Sim/Soak/`)

Detectors observe only Yaat.Sim types, so they live in Yaat.Sim where they are TDD-able in
`tests/Yaat.Sim.Tests` and shared by the soak runner and the live-attach monitor.

```csharp
enum FindingTier { HardFailure, Progress, Safety }
enum FindingSeverity { Critical, Error, Warning, Advisory }

interface ISoakDetector
{
    string Id { get; }                       // stable, e.g. "physics-sanity"
    FindingTier Tier { get; }
    void OnEpisodeStart(SoakEpisodeInfo info);
    void OnTick(SoakTickContext ctx);        // once per sim-second, after post-physics
    void OnEpisodeEnd(FindingAggregator sink);
}
```

`SoakTickContext` carries: `SimulationWorld World`, `SimScenarioState Scenario`,
`double ElapsedSeconds`, plus this tick's taps — drained warnings/notifications (from
`CollectingTrainingBroadcast`; on the RoomEngine path `TickProcessor.BroadcastWarnings` drains
`PendingWarnings`, so the collector is the tap), new/cleared `ActiveConflict` +
`EramActiveConflict`, the `AiCommandOutcome` list, captured Warning+ SimLog records
(`CapturingSimLogProvider` — `SimLog.Initialize` sets the process-wide factory), the
`GeneratorSpawnLog` tail, and the removed-aircraft delta.

### Condition model (mandatory dedup)

Detectors never emit raw findings per tick. They open a **condition** keyed by
`(detectorId, subjectKey)` (subject = callsign, conflict-pair id, message-template hash…), update it
while it persists, and close it when it resolves. One condition = **one finding** carrying
`firstSimTime`, `lastSimTime`, `durationSeconds`, and peak stats. `FindingAggregator` enforces:

- per-detector-per-episode cap (default 100, then a single "suppressed N further" finding);
- per-aircraft-per-detector cap (default 3);
- a load-time grace window (t < 10 s) suppressing spawn-transient noise.

## Detector list

### Tier A — HardFailure (Critical/Error)

| Detector | Signal |
|---|---|
| `tick-exception` | catch around `AdvanceOneSecond`; exception type + stack in finding data; ends the episode |
| `simlog-error` | captured `LogLevel.Error` (Critical) / `Warning` (Error, allowlist-filtered) records, keyed by (category, message template) |
| `physics-sanity` | per aircraft: NaN/Inf in lat/lon/alt/IAS/GS/heading; alt < −1,000 ft or > 70,000 ft; GS > 700 kt; **teleport**: inter-tick displacement > max(3×GS×1 s, 0.5 NM), suppressed on the spawn tick and for live-traffic shadows |
| `completion-accounting` | incremental + end-of-episode audit: every aircraft that left the world must have a `CompletedAircraftRecord` (drained each tick — the FIFO caps at 500); a silent vanish (the DEL-without-completion-stamp gap) is a finding |

### Tier B — Progress (Warning)

| Detector | Signal |
|---|---|
| `stuck-aircraft` | per-aircraft rolling displacement accumulator (own state — `PositionHistory` caps at 10 entries, insufficient). Ground: < 50 ft net over 180 s while in a *movement* phase (allowlist of legitimate stops: HoldingShort, CrossingRunway-wait, AtParking, HoldingAfterPushback, LUAW, …). Air: holds allowed; otherwise flag only "orphaned flight" (level, unchanging, receding past the last route fix for 20 min). One condition-with-duration per stuck episode |
| `ground-deadlock` | mutual `Ground.SpeedLimit == 0` (written per sub-tick by `GroundConflictDetector`) across ≥ 2 aircraft sustained > 120 s |
| `handoff-limbo` | handoff pending to an AI-covered or virtual position for > (AutoAcceptDelay + 60 s) |
| `ai-rejection` | any `AiCommandOutcome.Success == false` — the AI is phase-aware by design, so a rejection is an AI bug or a dispatcher bug, both wanted. Dedup by (position, verb, reason template) |
| `queue-stall` | departure/hold-short queue not advancing while runway ops continue; generator corridor divergence (rearmost spawn distance in `GeneratorSpawnLog` growing without bound) |
| `scenario-completion` | **the new completion signal**: zero non-shadow aircraft AND delayed/deferred spawn queues empty AND every generator inactive or past MaxTime, sustained 60 sim-s → episode "completed". A *finite* scenario not completing within k× its nominal duration (default k = 3) is a finding; generator soaks (maxTime null) simply run out the budget |

### Tier C — Safety (Advisory; never fails a run — may be the AI's own fault)

| Detector | Signal |
|---|---|
| `conflict-alert` / `eram-conflict` | new pairs from the collected conflict-alert changes; per-pair condition with min-separation stats + duration |
| `runway-occupancy` | `RunwaySafetyAdvisor` command-time advisories (from collected warnings) + landing/takeoff roll over an occupied runway |
| `solo-evaluator` | run `SoloTrainingEvaluator.Evaluate` per tick regardless of solo mode; map `SoloTrainingEvent` severity → Advisory findings — free 7110.65 grading of the AI's control |
| `warning-stream` | drained free-text warnings pass through as telemetry; a configurable regex escalation list promotes known-bad patterns |

Attribution of Tier-C findings (AI misjudgment vs sim bug) is not automatable; mitigation is the
tiering itself plus the per-command **decision log** ([03](03-decision-framework.md)) the triage
flow correlates against.

## Findings report

Per run directory:

```
<out>/<runId>/
  run.json            runner options, repo SHAs (yaat + yaat-server), AI version, start/end, machine
  episodes/<ep>/
    findings.jsonl    one JSON object per finding
    report.json       episode summary (seed, source, ticks, ×realtime, per-tier counts, completion)
    recording.zip     v4 archive (deleted for clean episodes unless --keep-clean-recordings)
  summary.md          human roll-up
  matrix.json         written by `report`: per (scenario × seed) grid with tier counts + repro lines
```

Finding schema (versioned, `"v": 1`):

```json
{
  "id": "ep03-000017", "detector": "stuck-aircraft", "tier": "Progress", "severity": "Warning",
  "scenario": "oak_ground_easy.json", "generatorSpec": null, "seed": 12345,
  "simTimeFirst": 4812.0, "simTimeLast": 5190.0, "durationSeconds": 378,
  "aircraft": ["N152SP"], "message": "No taxi progress for 378s in TaxiingPhase at node B4",
  "data": { "phase": "TaxiingPhase", "netFt": 12.4 },
  "recording": { "path": "episodes/ep03/recording.zip", "snapshotAt": 4812 },
  "repro": "python tools/bug_bundle.py history episodes/ep03/recording.zip --callsign N152SP"
}
```

Every finding also becomes a **timeline bookmark** injected into its archive at finish
(`RecordingArchive.WriteBookmarks`), so opening the recording in the client shows jump-to ticks.

## Triage tooling

`tools/bug_bundle.py soak-triage <runDir>` (house rule: bundle tooling = bug_bundle subcommands):
lists findings; per finding prints/executes the canned `info` / `history --callsign` /
`snapshot --at` sequence — the same flow the bug-bundle skill uses today.

`Yaat.SoakRunner verify --finding <id>` (H5): restore the nearest snapshot →
`ReplayRangeWithVerification` to the finding time with detectors attached in replay mode → assert
the finding recurs with zero drift. This is the determinism tripwire for the whole feature.

## False-positive management

The stuck-phase allowlist is the main source — learn from `TaxiBudgetEvaluator`'s "generous budgets,
tighten don't hand-tune" doctrine. All thresholds live in one config type with per-scenario
overrides; H3 includes an explicit false-positive burn-down pass over real scenarios. The
`simlog-error` detector starts Error-only plus a deliberate warning allowlist (Warning-level is
chatty at some call sites).

## File map (yaat half)

```
src/Yaat.Sim/Soak/
  FindingTier.cs  FindingSeverity.cs  SoakFinding.cs  SoakCondition.cs  ISoakDetector.cs
  SoakTickContext.cs  SoakEpisodeInfo.cs  SoakEpisodeResult.cs  FindingAggregator.cs
  CapturingSimLogProvider.cs
  Detectors/  (one class per detector above)
tests/Yaat.Sim.Tests/Soak/   one test class per detector (TDD; real data via TestVnasData)
tools/bug_bundle.py          soak-triage subcommand
```
