# Step 2 — `SimulationEngine` decomposition

Part of [tick-path unification](./README.md). Shipped 2026-09-02 (ADR 0006).

- [x] 2. `SimulationEngine` decomposition — shipped 2026-09-02 (`f5bee1d0` split, `6c17062a` extraction). Closes the backlog item below. Ownership map: [tick-loop.md](../../tick-loop.md) § the engine's partial files
  - **Partial split by cluster**, 5,546 lines / 175 members in one class → 12 files, largest `SimulationEngine.Tick.cs` at 1,114 (deliberate — it is the file steps 3 and 4 add to). Pure move, verified two ways: an exact-coverage assertion in the generator (no line assigned twice, none unassigned) and a code-line multiset comparison afterwards (4,883 = 4,883)
  - **`Simulation/Replay/ReplayDriver.cs`** (463 L, internal) now owns the recorded-action cursors; the engine's replay methods are one-line delegators, so none of the ~500 test call sites moved. Three copies of the cursor-seek loop (fast-forward, replay load, snapshot restore) collapsed into one `SeekTo`
  - **What stayed on the engine, each because something outside replay depends on it**: the two replay mode flags (four non-replay members read them; replaced by `RunProfile` in 3b), `_replayTrackApplier` (`DispatchAiCommand` uses it live), `SnapshotIntervalSeconds` (yaat-server's `RecordingManager` reads it in production), `TickTimings`, and `AccumulateTiming` — the last a genuine miscategorisation caught by the compiler, since `TickPhysics` calls it five times and it is therefore on every run kind's path
  - Both commits green on the full cross-repo suite, unchanged at 11,304 + 2,088
