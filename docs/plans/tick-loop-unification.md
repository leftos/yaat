# Tick-loop unification — one spine, many hosts

**Status:** designed and scheduled — lands as **Phase 1** of the controller-AI v1 slice, before the
per-frequency radio model. See [`controller-ai/12-milestone-v1-scope.md`](./controller-ai/12-milestone-v1-scope.md).
**Motivation:** requirement `DET-03` in that scope doc; flagged as the highest-leverage execution
risk by the milestone research.

## The problem

Four paths advance a sim-second. They resolve to **two** independent post-physics lists.

| Path | Entry point | Post-physics list | Used by |
|---|---|---|---|
| A | `SimulationEngine.TickOneSecond()` | `TickPostPhysics` | Yaat.Sim tests |
| B | `SimulationEngine.ReplayOneSecond` / `ReplayRangeCore` / `ReplayOneSubTick` | `TickPostPhysics` | replay, bug bundles, recording reconstruction |
| C | `RoomEngine.AdvanceOneSecond()` | `TickProcessor.ProcessPostPhysics` | `RecordingManager` reconstruction |
| D | `RoomEngine.AdvanceLiveSecond()` (= C + weather, position history, METAR) | `TickProcessor.ProcessPostPhysics` | live server hosted loop, headless soak host |

[`docs/tick-loop.md`](../tick-loop.md) already documents that the two lists exist and prescribes the
seam pattern (a public `SimulationEngine.Tick*` method both hosts call). That convention is real and
it works — six such seams exist today. What it does not do is *enforce* anything. The divergence is
on three axes, and the existing guard only samples the first.

### Axis 1 — membership

`TickPostPhysics` runs six engine steps. `ProcessPostPhysics` runs thirty entries: the same six, four
more engine steps the engine path never calls, and ~20 host-only steps.

Engine path never calls `TickAutoDelete` or `TickSoloTrainingEvaluation`. For solo evaluation that is
plainly right — no controller to notify. For **auto-delete it is a state mutation**: it is the only
post-physics step that removes aircraft, so the live world and a replayed world diverge in population
unless the recorded `DEL` stream happens to cover it.

### Axis 2 — ordering (not documented anywhere)

Four ordering disagreements exist, not the one the axis was originally opened for:

1. **`TickPilotProactive`** runs **2nd** on the engine path and **14th** on the server path — after
   transponders, both autotrack passes, coordination timers, tower lists, visual detection, conflict
   alerts, ERAM alerts, ASDE-X alerts and solo-training evaluation.
2. **`PilotSpeech` / `Readbacks` are swapped.** The engine drains `Readbacks` (step 10) before
   `PilotSpeech` (step 11); the server drains `PilotSpeech` (bucket 17) before `Readbacks` (bucket 18).
3. **`StripDispatches`** runs 2nd on the engine (right after `Warnings`) and 27th-of-32 on the server
   (immediately before `AutoDelete`) — a 25-bucket gap, not a minor reorder.
4. **`AutoDelete` / `SoloTrainingEvaluation` membership** — absent from the engine path entirely,
   present on the server at buckets 28 and 13. (This is axis 1, listed here because the fix has to
   pick a position for them.)

Shared code executes at a different point in the sequence depending on host. Verified:
`PilotProactive` reads only aircraft and scenario state, so **this is latent risk, not a live bug**.
It becomes a bug the moment a proactive rule reads anything visual detection or conflict alerting
writes — and CA2 adds AI positions that touch exactly that surface.

### Axis 3 — arguments

`TickPostPhysics` calls `TickConflictAlerts([])`; the server passes the room's real internal airports.
Deliberate and documented, but it means conflict classification differs by path, so a replay can
reach a conflict verdict the live run did not.

### Why the current defense is not enough

Six parity tests exist (`PilotProactiveServerParityTests`, `VisualDetectionServerParityTests`,
`SoloTrainingServerParityTests`, `TransponderIdentServerParityTests`, …) — one written each time
someone noticed a seam. That is a **sampling** strategy: each guards one feature, and a seventh
feature is guarded only if a person remembers to write a seventh test. Nothing detects a *new* step
added to one list and not the other, which is precisely how pilot-proactive request reminders ran
dark on the live server for ~2.5 months.

## Goal

Any per-tick behaviour that affects simulation state runs **identically — same steps, same order,
same arguments** — across all four paths. Without dragging host-only concerns (broadcasts, CRC glue,
strips, TDLS, autotrack) into `Yaat.Sim`, where they do not belong.

## The key distinction: spine steps vs host steps

Not everything in the server's thirty entries is simulation. Classify by one test:

> **Does this step mutate state a snapshot captures, or state a later spine step reads?**

**Spine** (must be identical everywhere): `TickLiveTrafficRunwayUse`, `TickTransponders`,
`TickVisualDetection`, `TickConflictAlerts`, `TickEramConflictAlerts`, `TickSoloTrainingEvaluation`,
`TickPilotProactive`, the world-buffer drains, `TickAutoDelete`, `TickControllerAi`.

**Host** (free to differ): autotrack, coordination timers, tower lists, ASDE-X CRC glue, strips,
TDLS, surface coast, every `Broadcast*`.

The spine is the contract. The host list stays the host's business, including its own internal
ordering constraints (FP-creator autotrack before deferred autotrack).

The `PilotContacts.AnyAnswering` gate — with its `DiscardAllPilotTransmissions` branch — is
**spine-side** by that test: `DiscardAllPilotTransmissions` mutates state a snapshot captures. It is
currently hand-copied into both hosts (`TickProcessor.cs:1887` documents the copy in a comment);
collapsing it is a small visible win of this refactor.

## Design

### 1. The spine is data, not control flow

Sixteen members, in this order. Every member already exists as a public engine method returning what
a host needs — the refactor is orchestration, not new step logic.

```csharp
// Yaat.Sim/Simulation/PostPhysicsStep.cs
public enum PostPhysicsStep
{
    LiveTrafficRunwayUse,      // void
    Transponders,              // void
    VisualDetection,           // void
    ConflictAlerts,            // ConflictAlertChanges     — host.InternalAirports in, host.OnConflictAlerts out
    EramConflictAlerts,        // EramConflictAlertChanges — host.OnEramConflictAlerts out
    SoloTrainingEvaluation,    // IReadOnlyList<SoloTrainingEvent> — host.OnSoloTrainingEvents out
    PilotProactive,            // void — D-01: moved from engine position 2 to here
    Warnings,                  // List<(string Callsign, string Warning)>
    Notifications,             // List<(string Callsign, string Notification)>
    PilotSpeech,               // List<(string Callsign, string PilotSpeech)>
    Readbacks,                 // List<(string Callsign, string Readback)>
    Transmissions,             // AnyAnswering-gated: List<PilotTransmission> | discard
    ApproachScores,            // List<ApproachScore>
    StripDispatches,           // List<(string Callsign, ParsedCommand Command)> — D-05: late, not 2nd
    AutoDelete,                // IReadOnlyList<AircraftState> — host.OnAutoDeleted out
    ControllerAi,              // void, self-guarding — D-08: final step
}
```

`SimulationEngine` holds one ordered array over that enum — the single source of truth for what runs
and in what order. Each buffer drain is its own member (D-04) rather than a composite
`DrainWorldBuffers` step: collapsing six ordered drains into one re-hides ordering inside control
flow, which is axis 2 all over again.

### 2. Hosts supply arguments and consume results through one interface

```csharp
public interface IPostPhysicsHost
{
    /// STARS internal airports for conflict classification. The standalone and replay
    /// hosts have no STARS configuration and return empty — explicitly, not by omission.
    IReadOnlyList<string> InternalAirports { get; }

    void OnConflictAlerts(ConflictAlertChanges changes);
    void OnEramConflictAlerts(EramConflictAlertChanges changes);
    void OnSoloTrainingEvents(IReadOnlyList<SoloTrainingEvent> events);
    void OnAutoDeleted(IReadOnlyList<AircraftState> removed);
    void OnDrained(PostPhysicsStep step, object payload);   // shape per D-07; encoding is the planner's call

    /// Host-only steps attach here, keyed by the spine step they follow.
    void AfterStep(PostPhysicsStep step);
}
```

The spine **drains once and hands the payload to the host** (D-07); each host then fans out its own
way — `EmitTerminal` + `Fire*Emitted` standalone, `BroadcastTerminalEntry` /
`DispatchDeferredStripAsync` on the server. Today both hosts drain independently.

### 3. One executor, used by both hosts

`RunPostPhysics(IPostPhysicsHost host)` iterates `Spine`, dispatches each step through a **`switch`
expression with no discard arm**, and calls `host.AfterStep(step)`. `TickPostPhysics()` becomes
`RunPostPhysics(new StandaloneHost(this))`; `ProcessPostPhysics` becomes
`room.ActiveSim!.RunPostPhysics(new RoomPostPhysicsHost(room, this))`, whose `AfterStep` switch
attaches the ~20 server steps at their declared points.

**The switch must be an expression, not a statement (D-02).** A `switch` statement with
`default: throw new UnreachableException(...)` compiles cleanly when a new enum member is added and
fails only at runtime. A switch expression with no discard arm raises CS8509, which this repo's
`-p:TreatWarningsAsErrors=true` turns into a build failure — that is what makes "a compile error, not
a silent divergence" literally true. The arms must share a return type; a per-step delegate or a
small result value both work, and the encoding is the planner's call.

### How each axis is closed

| Axis | Closed by |
|---|---|
| **Membership** | The `switch` expression is exhaustive over the enum. A new `PostPhysicsStep` member fails to compile until handled, and a structural test checks every enum member appears exactly once in `Spine`. A step cannot exist on one path only. |
| **Ordering** | Both hosts iterate the *same array*. Divergence is not merely tested-against; it is unrepresentable. |
| **Arguments** | Come from `IPostPhysicsHost`, so the standalone host's empty airport list is a written-down decision at a named site, not an accident of a literal `[]` at one call site. |

## Decisions taken (2026-09-02)

**D-01 — `PilotProactive` takes the server's literal position**, after `SoloTrainingEvaluation`. That
order is the production-exercised one, and `TickPilotProactive` was verified to read only aircraft and
scenario state, with no dependency on anything it would move behind. The change is therefore expected
to be behaviour-neutral — but Stage 0's characterization test proves that rather than assuming it, and
if any recording does move, that is a finding, not a nuisance. Live server behaviour is unchanged; the
engine/replay path is what moves. *Reversibility: costly — reverting re-shifts the engine path and
forces a second pass over the 128-recording corpus.*

**D-02 — spine completeness is enforced by a `switch` expression with no discard arm**, not a switch
statement with a `default: throw`. See §3 above for why.

**D-03 — the Stage 0 characterization test observes genuinely executed step order** via an ordered
step-trace hook on both hosts: an opt-in nullable ordered collection alongside the existing
`TickTimings`. `TickProcessor`'s existing `Run(bucket, step)` wrapper is already the insertion point
on the server; the engine needs an equivalent wrap. `TickTimings` cannot be reused as-is — it is a
`Dictionary<string, (int Count, double Ms)>` and discards order. *Reversibility: costly — the hook is
public surface on `SimulationEngine` and `TickProcessor`, so removing it later is cross-repo.*

**D-04 — each drain is its own spine step**, not a single composite. See §1.
*Reversibility: costly — the enum and `IPostPhysicsHost` shape are consumed by both hosts.*

**D-05 — drain order is server-literal**, consistent with D-01: `Warnings` → `Notifications` →
`PilotSpeech` → `Readbacks` → `Transmissions` → `ApproachScores`, with `StripDispatches` late,
immediately before `AutoDelete`. One precedence rule holds for the whole phase: the
production-exercised order wins. The engine path's terminal-emission order changes here.
*Reversibility: costly — same recording re-triage cost as D-01.*

**D-06 — all drains sit before `AutoDelete`.** On the server every drain already precedes it, which is
what satisfies the documented "a strip command's callsign still resolves this tick" constraint at
`TickProcessor.cs:1880`.

**D-07 — the spine drains once and hands the payload to the host.** See §2.

**D-08 — `TickControllerAi` becomes the final spine step.** It is already self-guarding (returns early
on `IsPlaybackMode`, `_isReplayingRecordedActions`, or no installed AI), so it is safe as an
unconditional spine member, and it depends on `ConflictAlerts` / `EramConflictAlerts` having run,
which the spine order guarantees. `RoomEngine.AdvanceLiveSecond`'s separate call at `RoomEngine.cs:710`
**must be removed** or the brains tick twice. *Reversibility: costly — cross-repo, and a deliberate
live behaviour change.*

**D-09 — the live-path reordering D-08 causes is knowingly accepted.** The AI tick moves ahead of
weather/METAR issuance and playback-action application in `AdvanceLiveSecond`, so on a tick where a
METAR is re-issued the brains see the previous METAR — a one-tick lag on an hourly-scale event. The
playback interaction is moot because `TickControllerAi` already no-ops in playback mode. This is the
one place in the phase where live behaviour deliberately changes; it must appear in the Stage 0
characterization expectations as a deliberate edit, not a surprise.

**D-10 — Phase 1 owns the cleanup.** Delete `SweepPendingAutoDeletes()` outright (replace, don't
deprecate — `TickAutoDelete()` covers the same work and returns the removed `AircraftState`s),
collapse `AiTestHost.Tick` to `engine.TickOneSecond()`, and strip the manual sweeps from the four
issue-regression tests. Two surviving entry points to a spine step is exactly how divergence re-enters
after the refactor.

**D-11 — "kept" means the recording did not truly desync.** If triage finds the divergence was an
over-broad assertion, correct the test and the recording stands. Anything genuinely desynced is
**deleted**, per the standing repo rule. No re-baselining: rewriting a recording's snapshot stream
against the new engine would freeze any bug in the refactor in as ground truth and turn the recording
from an independent check into a tautology. No per-recording documented population deltas either —
that is silencing by relaxed assertion. *Reversibility: costly — deleted fixtures survive only in git
history.*

**D-12 — no count threshold on deletions, but a hard gate on kind.** If even one recording desyncs for
a cause that is *not* the expected auto-delete population change, stop and investigate before deleting
anything. An unexplained cause is a finding about the refactor, not about the corpus. The deliverable
is a named triage table — recording, cause, verdict — produced before any deletion.

**D-13 — `InternalAirports` stays host-supplied**, so the standalone and replay hosts' empty list is a
named decision at one site rather than a literal at a call site. Whether replay *should* classify
conflicts with the room's real airport list is deferred: it needs the list recorded in the snapshot
first, which is its own change and does not block this one.

### Left to the planner

- The encoding that lets the switch expression's arms share a return type (per-step delegate vs. a
  small result value) — D-02 fixes the mechanism, not the shape.
- Where the spine array lives: `private static readonly` on `SimulationEngine`, or its own
  `PostPhysicsStep.cs` under `src/Yaat.Sim/Simulation/`. Both match existing precedent.
- Which of yaat-server's nine `*ParityTests.cs` are pruned in Stage 3. The rule stands — delete the
  ones that only assert "the server calls this engine method", keep the ones asserting real behaviour.
- Whether the server's per-step `Run(bucket, step)` timing buckets survive as-is or move behind the
  executor. They feed a benchmark surface, so preserving them is expected.

## Integration points

**Callers of `TickPostPhysics()`** — all inherit the spine automatically:
`TickOneSecond()` (`SimulationEngine.cs:1689`), `ReplayRangeCore` (`:1879`), `ReplayOneSecond()` (`:2082`).

**Server side:** `RoomEngine.ProcessPostPhysics()` (yaat-server `RoomEngine.cs:618`) →
`_tickProcessor.ProcessPostPhysics(Room)`, reached from `RunSecondPhysics()` (`:727`).
`RoomEngine.AdvanceLiveSecond`'s `Room.ActiveSim?.TickControllerAi()` at `RoomEngine.cs:710` **must be
deleted** under D-08.

**Existing step methods** (all public, all returning what a host needs): `TickLiveTrafficRunwayUse()`
(`:4789`), `TickTransponders()` (`:948`), `TickVisualDetection()` (`:970`), `TickPilotProactive()`
(`:912`), `TickConflictAlerts(IReadOnlyList<string>)` (`:1234`), `TickEramConflictAlerts()` (`:1296`),
`TickAutoDelete()` (`:1392`), `TickSoloTrainingEvaluation()` (`:1512`), `TickControllerAi()` (`:1651`).

**Hand-rolled copies of the tick sequence that D-10 collapses:**
`tests/Yaat.Sim.Tests/ControllerAi/AiTestHost.cs:108-111` (the full three-call copy, whose doc comment
describes it as "the host loop as the server runs it" — a third copy of the ordering that nothing keeps
in sync with either host), `Simulation/CrossDestinationRunwayTests.cs:166`,
`Simulation/Issue10OnHoldShortDeleteTests.cs:86,134`, `Simulation/Issue311CrossThenDeleteTests.cs:168,306`.
Direct-assertion call sites in `AutoDeleteTickTests.cs` and `SoloTrainingEvaluationTickTests.cs` test the
step in isolation and are not loop copies — leave them.

**Test vehicle for host parity:** `RoomEngineTestHarness` (yaat-server
`tests/Yaat.Server.Tests/Harness/RoomEngineTestHarness.cs`).

**Corpus:** 128 recording fixtures under `tests/Yaat.Sim.Tests/TestData/*.zip`.

## Migration

Staged; every stage ships green and is independently revertable.

**Stage 0 — characterization first.** Before touching anything, add a test that records the actual
executed step order on both paths for a fixed scenario and asserts today's behaviour (D-03). The
refactor is then provably behaviour-preserving, and any intended change shows up as a deliberate edit
to a recorded expectation. This is the repo's standard TDD posture applied to a refactor.

**Stage 1 — introduce the spine, engine side only.** Add the enum, `Spine`, `IPostPhysicsHost`,
`RunPostPhysics`, and a `StandaloneHost`. Rewrite `TickPostPhysics` to delegate. No server change, no
behaviour change. Yaat.Sim tests and the replay corpus must be untouched-green.

**Stage 2 — server adopts the spine.** `ProcessPostPhysics` becomes a `RoomPostPhysicsHost` whose
`AfterStep` carries the host-only steps. **This is the risky stage** — it is where the
`PilotProactive` ordering (D-01), the drain order (D-05) and the controller-AI move (D-08) resolve —
and it is cross-repo, so both halves land together.

**Stage 3 — retire what is now structural.** The per-feature parity tests that only assert "the
server calls this engine method" are superseded and should be deleted rather than left to rot; the
ones asserting real behaviour stay. Per the repo's replace-don't-deprecate rule, no shim.

## Risks

- **The replay corpus.** ~128 recordings under `tests/Yaat.Sim.Tests/TestData`. Any ordering change
  can desync them. The project rule is to ship a correctness fix and delete desynced recordings
  rather than water down the fix — but that trade should be made knowingly, per recording (D-11, D-12).
- **Cross-repo.** Stage 2 spans both repos and must land together; `pwsh tools/test-all.ps1` is the
  gate, not a bare `dotnet test`.
- **Scope.** This is the most load-bearing code in the product. Stage 0 exists precisely so the
  refactor is not also a behaviour change.

## Verification

- Stage 0's characterization test, extended to assert spine order per host
- A structural test: every `PostPhysicsStep` appears exactly once in `Spine`; both hosts execute the
  full spine in array order
- `pwsh tools/test-all.ps1` cross-repo, and the replay E2E corpus
- The existing per-feature parity tests stay green through Stages 0–2, then are pruned in Stage 3
- [`docs/tick-loop.md`](../tick-loop.md) must be updated by this work — it currently documents the
  seam convention this design replaces with structural enforcement

## Deferred

- **A second "post-tick" spine** covering after-the-second host steps (weather issuance, METAR,
  playback actions). Considered and set aside — D-08 puts the controller AI under the post-physics
  spine instead, and a second spine would grow this work's scope. Revisit if the weather/METAR
  ordering turns out to matter to a later brain.
- **Recording `InternalAirports` in the snapshot** so replay could classify conflicts against the
  room's real airport list instead of an empty one (D-13).

## Relationship to the controller-AI milestone

This subsumes `DET-03`. If it lands before H1, the soak harness inherits the guarantee structurally
and H1's parity criterion becomes a check rather than a construction task. If it lands after, every
phase carries its own parity test.
