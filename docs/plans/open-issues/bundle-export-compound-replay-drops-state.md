# Bug bundle export drops state for `;`/`,` compound commands

## Context

Bug bundle (`*.yaat-bug-report-bundle.zip`) snapshots are **regenerated at export time** by replaying the recorded actions through `SimulationEngine.ReplayWithSnapshotCallback` (server-side, triggered by `MainViewModel.SaveBugReportBundle` → `_connection.ExportRecordingAsync`). When the recording contains compound commands separated by `;` or `,` (e.g. `DCT VPCBT; ERB 28R`, `EF 28L, CLAND`, `DM 015, DCT OAK; ERD 28R`), the regenerated snapshots can show the aircraft as if neither command applied — `Phases=null`, the navigation route still set to its scenario default, queue entries missing.

This was originally surfaced and partially fixed in commit `1f8d1f66` (Apr 26, 2026, "fix: replay compound cmds + preserve VFR pattern dir on go-around"), which patched `SimulationEngine.ReplayCommand` to fall through to `ParseCompound` when the single-command parse failed. **The S2-OAK-3 (1) | VFR Sequencing bundle (recorded May 5, 2026) still exhibits the bug** for `DCT VPCBT; ERB 28R` issued to N42416 and N314GT, even though a fresh replay of the same recording with current code produces correct phase chains. Either the user's server runs a build older than `1f8d1f66`, or the export path takes a different code branch that wasn't patched.

The practical impact is large: every triage starting from a bundle's snapshots is potentially looking at fictional state for any aircraft that received a compound command. Diagnosis built on those snapshots can reach the wrong root cause (we did, on the EF 28L 360 investigation that seeded this issue).

## Goals

- Bundle export-time snapshots match what a fresh replay of the same recording produces with the same code.
- Compound commands (`;` and `,`) round-trip through the export path without losing aircraft state.
- A regression test against an existing bundle that was previously buggy.

## Investigation tasks

- [ ] Check whether `SaveBugReportBundle` (`src/Yaat.Client/ViewModels/MainViewModel.Timeline.cs:192`) actually goes through the same `ReplayCommand` path that `1f8d1f66` patched, or through a different applier.
- [ ] If a different applier exists in the export path, port the compound-fall-through fix there.
- [ ] If the same applier is used, confirm whether the user's server build includes `1f8d1f66`. If yes, find the case that escapes the fix (DCT;ERB specifically? interaction with deferred-action pause/unpause? scenario init order?).
- [ ] Reproduce by exporting a fresh bundle locally for a recording known to contain `DCT X; ERB Y` and `DCT X; ERD Y`, then diff replay-state vs export-snapshot state for the affected aircraft.
- [ ] Compare against the existing `Issue143OakErdCompoundAndGaDirectionTests` setup (`tests/Yaat.Sim.Tests/Simulation/Issue143OakErdCompoundAndGaDirectionTests.cs`) — that test uses `RecordingLoader.Load` + `engine.Replay`, which is a different entry point than the export path.

## Implementation tasks (after root cause known)

- [ ] Apply the same compound-fall-through behavior wherever the export path's command applier diverges.
- [ ] Add a test that round-trips a recording through the export pipeline and asserts the regenerated snapshots match a fresh replay's state at the same elapsed seconds.
- [ ] If the user's server is simply old: bump the recommended server version in the YAAT release notes / installer prompt and add a manifest-level "exported with code rev …" marker so triage can detect old bundles.

## Diagnostic helpers

- `python tools/bug_bundle.py history --callsign X` shows what the export-time snapshots claim happened.
- The follow-up `S2Oak3ErbEfDiagnosticTests` (or its replacement) shows what a fresh replay produces for the same recording.
- Diff = the bug.

## Side notes

- For **triage**, the new `bug_bundle.py history` view inherits the same wrong state when snapshots were buggy. A future improvement: have the tool optionally re-replay the recording locally and overlay/replace baked snapshots before printing the per-callsign timeline. That's a bigger project — separate plan.
- The S2-OAK-3 investigation's plan file at `~/.claude/plans/x-downloads-s2-oak-3-1-vfr-linked-stallman.md` is now superseded by the EF-sidestep commit. Bundle is installed at `tests/Yaat.Sim.Tests/TestData/s2-oak-3-vfr-seq-erb-eflv-recording.yaat-bug-report-bundle.zip` if needed for repro.

## References

- `src/Yaat.Sim/Simulation/SimulationEngine.cs:1591` — `ReplayCommand`, the patched applier.
- `src/Yaat.Sim/Simulation/SimulationEngine.cs:747` — `ReplayWithSnapshotCallback`, used by export.
- `src/Yaat.Client/ViewModels/MainViewModel.Timeline.cs:192-278` — client-side bundle save flow.
- Commit `1f8d1f66` — partial fix for compound replay.
