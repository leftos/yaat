# 07 — Soak Runner

Part of [Controller AI + Soak Harness](README.md). The standalone tool that runs AI-controlled
scenarios headless for many sim-hours and emits findings + artifacts. Detectors and report formats
in [08](08-detectors-and-findings.md); live attach in [09](09-live-attach.md).

## Placement (locked decision)

**The runner lives at `yaat-server/tools/Yaat.SoakRunner` and drives the RoomEngine path** via a new
`HeadlessRoom`. Verified rationale:

1. Only the server records commands — `RoomEngine.SendCommandAsync` → `Record(new
   RecordedCommand(...))`; standalone `SimulationEngine.SendCommand` does **not** append to
   `ActionLog`, so a SimulationEngine-only runner would produce non-replayable artifacts.
2. The full lifecycle machinery is server-side: `TickProcessor.ProcessPostPhysics` runs auto-accept
   (without it every AI handoff strands), pointout auto-ack, autotrack, conflict/ERAM/ASDEX
   processing, auto-delete, warnings fan-out. `RoomEngine.AdvanceOneSecond` is the exact live tick
   path and is already driven headless by `RoomEngineTestHarness.Tick()`.
3. Tool-project precedent exists in that dependency direction (`yaat-server/tools/Yaat.SwimSlice`
   references `$(YaatSimProject)` + `Yaat.Server.csproj`); a yaat-repo tool referencing yaat-server
   would invert the public→private direction.

Rejected: runner in yaat `tools/` on bare `SimulationEngine` (fails on handoffs/recording); lifting
auto-accept etc. into Yaat.Sim (large refactor of deliberately server-owned behavior for zero
product benefit).

Consequence: cross-repo feature — detector framework + bug_bundle tooling in yaat, host + runner +
attach mode in yaat-server — planned and landed together per house rules.

## HeadlessRoom

`src/Yaat.Server/Simulation/Headless/HeadlessRoom.cs` — a production-grade sibling of
`RoomEngineTestHarness` (which stays test-only):

- Real `TickProcessor` + command handlers + `RoomEngineFactory` wiring, one `TrainingRoom`, no
  ASP.NET host, no hubs.
- `CollectingTrainingBroadcast` (production twin of the tests' `SpyTrainingBroadcast`): buffers
  warnings/notifications/terminal/conflict broadcasts per tick for detector consumption. Null CRC
  broadcast/hub contexts.
- **Seeded load:** `LoadScenarioAsync(json, rngSeed)` reusing the `ReloadForRewindAsync` internals
  (the one existing seed-forcing path; the normal load path draws `Random.Shared.Next()`).
- Initializes `VnasDataService`/`ArtccConfigService`/`AirportGroundDataService` with the normal
  `%LOCALAPPDATA%` caches (warm the cache once sequentially before parallel fan-out).

## CLI surface

```
Yaat.SoakRunner run
  --scenario <path>          repeatable; ATCTrainer scenario JSON
  --generators <spec.json>   generator soak: base scenario + IFR/VFR/overflight generator configs
  --seed 42 | --seeds 100-149
  --sim-hours 4              per-episode budget (episode may end early on scenario completion)
  --positions GC,LC,APP,CTR  AI positions to enable
  --out <dir>                default yaat-server/.tmp/soak/<runId>
  --snapshot-interval 60     sim-seconds between streamed snapshots
  --fail-on hard|stuck|none  default stuck (exit-code threshold; the Safety tier never fails)
  --parallel N               child processes, one episode each
  --keep-clean-recordings    default off: clean episodes delete their archive, keep the report line
  --max-disk-gb 20           hard stop on artifact growth
  --disable-detector X / --enable-detector Y / --list-detectors
  --sim-rate-limit R         optional wall-clock throttle for debugging (default unthrottled)
Yaat.SoakRunner report --run <dir>                    aggregate seed-matrix summary
Yaat.SoakRunner verify --finding <id> --run <dir>     determinism check (H5)
```

Exit codes: `0` clean or below `--fail-on`; `2` findings at/above threshold; `64` usage error; `70`
runner-internal failure (data init, disk). A tick exception is a HardFailure finding → exit 2.

## Episode model

Endless soak = a loop of **bounded episodes** (default 4 sim-hours) with incrementing seeds, each a
fresh room + fresh recording. This bounds memory (`ActionLog` stays a few MB per episode at ~2 AI
commands/s), makes every artifact a self-contained v4 archive replayable from t=0, and sidesteps
rolling-window recording entirely.

### Run loop (per episode)

```
SimLog.Initialize(capturing factory)                  // console Info+, Warning+ ring buffer tap
host = HeadlessRoom.Create(...)
engine = host.CreateRoom(); host.LoadScenarioSeeded(json, seed)
ai = AiControllerService for the enabled positions    // core plan's factory
monitor = RoomSoakMonitor.Attach(engine, detectors, aggregator)
recording = RecordingSink.Open(out/<episode>.partial.zip)
loop until budget or completion or fatal:
    foreach req in ai.CollectRequests(elapsed):       // brains emit; host dispatches (locked contract, 01)
        result = await engine.SendCommandAsync("AI:" + req.PositionId, req.Callsign, req.Canonical, "AI")
        outcomes.Add(new AiCommandOutcome(req, result))
    try { engine.AdvanceOneSecond(); }
    catch (Exception ex) { monitor.ReportTickException(ex); break; }   // state poisoned → end episode
    monitor.OnTick(BuildContext(collector.DrainTick(), outcomes, logTap.Drain()))
    if (elapsed % snapshotInterval == 0 || monitor.OpenedHardOrProgressFindingThisTick)
        recording.WriteSnapshot(engine.ActiveSim.CaptureSnapshot(actionCount))
monitor.OnEpisodeEnd()                                // completion audit
recording.Finish(actionLog, findingsAsBookmarks)      // rename .partial → final
FindingsWriter.Write(...)
```

Notes:

- `AdvanceOneSecond` has no internal try/catch (only `RoomTickLoopService` catches in
  production) — the runner's catch **is** the Tier-A tick-exception detector. After a tick exception
  the episode ends (world state untrustworthy) but artifacts finalize in a `finally`: the recording
  replays deterministically up to the crash, which *is* the repro.
- **Snapshots stream live** via `RecordingArchiveWriter.WriteSnapshot` (O(world-state) memory) —
  never the export path (`RecordingManager.GenerateSnapshotsViaReplay` regenerates snapshots by
  replaying, doubling compute for a multi-sim-hour episode). Cadence 60 s (≈240 snapshots per
  4-sim-hour episode) plus an extra snapshot the moment any Hard/Progress finding opens, so
  `bug_bundle.py snapshot --at <t>` lands exactly on it.
- Wall-clock decoupling: live traffic disabled; unthrottled tight loop by default.

## Multi-seed orchestration

Sequential in-process loop by default; `--parallel N` spawns child processes (one episode each) via
self-invocation — process isolation keeps a tick-loop crash or memory leak in one episode from
contaminating the matrix, and sidesteps static-singleton races (`NavigationDatabase` etc. are
per-process). Children share the warmed `%LOCALAPPDATA%` cache.

`ProgressReporter` prints a per-episode status line + a `TickTimings` per-bucket dump (the opt-in
profiler already in `TickProcessor`) — free throughput instrumentation.

## Artifacts and disk

- Archive written to `.partial.zip`, finalized (actions + final snapshot + findings bookmarks via
  `RecordingArchive.WriteBookmarks`) in `finally`, renamed on success. A hard process crash loses
  only the archive — seed + scenario + AI version reproduce it by rerun.
- Clean episodes delete `recording.zip` by default (`--keep-clean-recordings` overrides); the report
  line survives. `--max-disk-gb` aborts the matrix with exit 70.
- No start-trim needed: `bug_bundle.py trim` is tail-only (`--max-seconds`/`--max-snapshots`,
  preserves all actions), and v4 archives always replay from t=0 with snapshots making seeks cheap.
  The runner emits a canned tail-trim command in `report.json` rather than auto-trimming in v1.
- Estimate ~10–50 MB per 4-sim-hour episode with findings.

## Throughput plan

Estimate 50–200× real-time at 20–40 aircraft (5–20 ms/tick with zero clients — broadcasts no-op) →
a 50-seed × 4-sim-hour matrix in roughly 1–4 wall-hours at `--parallel 8`. **H0 validates this
first**; if it measures < 20×, profile via `TickTimings` and consider a headless flag that skips
strip/TDLS sub-processes.

## File map (runner half)

```
yaat-server/src/Yaat.Server/Simulation/Headless/
  HeadlessRoom.cs  CollectingTrainingBroadcast.cs  NullCrcBroadcast.cs  NullHubContext.cs
yaat-server/src/Yaat.Server/Soak/
  RoomSoakMonitor.cs         attaches detectors + aggregator to one RoomEngine (shared with live attach)
  SoakFindingBroadcastSink.cs
yaat-server/tools/Yaat.SoakRunner/
  Yaat.SoakRunner.csproj  Program.cs  SoakOptions.cs  EpisodePlan.cs  TrafficSource.cs
  EpisodeRunner.cs  RecordingSink.cs  FindingsWriter.cs  SeedMatrixRunner.cs  ProgressReporter.cs
yaat-server/tests/Yaat.Server.Tests/Soak/   HeadlessRoom + RoomSoakMonitor integration tests
```
