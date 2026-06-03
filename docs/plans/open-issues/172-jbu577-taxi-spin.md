# Issue #172 (split) — JBU577 taxi spin

> **Status:** deferred from #172 (2026-06-03). Not yet reproduced in a test; may share a root with the SKW3359 A→B fork (see [172-skw3359-ab-fork.md](172-skw3359-ab-fork.md)).
> **Source:** SFO recording `issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip` (in TestData). **Labels:** bug, ground-cmds.

## Symptom

JBU577 spins after a taxi clearance — turns back toward the runway, then toward B again (per the
original report). In the recording, JBU577 was holding short of B at G, then received
`TAXI B M1 Y @B5`, which resolved to `G B M1 Y AY2 A M2 M3 RAMP @B5` (a long route with several
junctions). The spin is the aircraft failing to settle on a direction at one of those junctions.

## Reproduction (to establish)

- Bundle in TestData. JBU577 history (sim-t):
  ```bash
  python tools/bug_bundle.py history tests/Yaat.Sim.Tests/TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip --callsign JBU577
  ```
  Key command: `TAXI B M1 Y @B5` → echo `G B M1 Y AY2 A M2 M3 RAMP @B5`.
- Replay to just before that command and tick forward, logging position/heading/nearest-node and
  writing a `TickRecorder` CSV; inspect with LayoutInspector `--ticks … --tick-table`.

## Suspected area

Junction-candidate selection / entry-alignment at the G/B area, or `GroundNavigator` pure-pursuit
entry alignment. **Likely shares a root with the SKW3359 A→B fork** (poor junction selection across
non-directly-connected taxiways). Re-test JBU577 after that fix lands — it may already be closed.

## TDD target

E2E recording-replay test (precedent: `Issue165SkwTaxiSpinTests.cs`): replay JBU577's taxi and
assert no orbit — not stuck < 5 kt for > 20 s (excluding hold-shorts) and no 180° U-turn in the
resolved route.
