# Issue #172 (split) — JBU577 taxi spin

> **Status:** deferred from #172 (2026-06-03). Not yet reproduced in a test. (The SKW3359 A→B detour once thought to share its root was confirmed operator error — the controller should have issued `E` — and closed; it is not a bug.)
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
entry alignment. A spin/orbit is incorrect behavior regardless of route quality, so this stands as a
genuine bug on its own (the SKW3359 A→B detour previously suspected to share its root was confirmed
operator error and closed). Reproduce the spin from the recording before changing code.

## TDD target

E2E recording-replay test (precedent: `Issue165SkwTaxiSpinTests.cs`): replay JBU577's taxi and
assert no orbit — not stuck < 5 kt for > 20 s (excluding hold-shorts) and no 180° U-turn in the
resolved route.
