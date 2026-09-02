# Per-frequency radio model

**Milestone:** the radio prerequisite of the v1 slice — see
[`12-milestone-v1-scope.md`](./12-milestone-v1-scope.md). Ships before CA2, because CA2 is what puts
two AI positions on the air at once.
**Owns:** requirements `RADIO-01` … `RADIO-09`.
**Status:** designed, not built.

Every other controller-AI subsystem had a subdesign file before it was built. The radio model did
not — it existed only as a shipped single-frequency implementation plus a steer paragraph in the
[README](./README.md). This is that file.

## Why it comes first

Two independent reasons, both concrete, neither hypothetical:

1. **The shared gate leaks across positions.** `SimulationWorld.ActiveFrequency` is one object. Its
   awaiting-controller-response gate is global, so an AI Ground answer clears a gate held for a pilot
   waiting on Local's completely separate frequency. This is already on record in the CA0/CA1 v1
   limitations list — it is latent only because exactly one AI position can talk today.
2. **`CT` from an AI position never releases an RV-SID heading hold.**
   `InitialClimbPhase.UpdateRvSidHeadingHold` needs a transfer signal, and the signal it has
   (`HasLeftStudentFrequency`) is student-scoped. An origin-neutral AI-issued `CT` never sets it, so
   the heading hold would never release once Local starts issuing `CT` itself. The fix is a transfer
   signal keyed on the aircraft's tuned position, which is this design.

## The model

### Per-channel half-duplex state machine

Real VHF air-band radio is half-duplex and single-channel: one keyed transmitter per frequency,
everyone else listens or queues. The standard simulation of that — and what `FrequencyState` already
does for a single shared channel — is a **serialized airtime queue** per channel: enqueue candidate
transmissions, gate dequeue on `elapsedSeconds >= nextAvailableAt`, and layer priority gates on top of
FIFO for "this exchange isn't done yet."

`FrequencyState`'s two existing gates (`_awaitingReadbackFrom`, `_awaitingControllerResponseTo`) are
each a narrow instance of one general idea: an **exchange lock** that a request → response → readback
triad holds until it resolves or times out. Generalizing means folding both into a single
`ExchangeState` per `FrequencyState`:

```
ExchangeState { HeldByCallsign, Phase: AwaitingReadback | AwaitingResponse, SinceSeconds }
```

so a controller instruction → pilot readback and a pilot request → controller response → pilot
readback are both instances of the same lock, not two independently-coded gates that can drift.

The existing 8-second timeout ceiling is the right pattern — bounded priority, not a hard lock — and
carries forward unchanged. It is what already prevents a missing readback from silencing an airport.

### One `FrequencyState` per position

```csharp
sealed class FrequencyState { /* existing per-channel queue/gate, internals unchanged */ }

sealed class SimulationWorld
{
    // replaces: public FrequencyState ActiveFrequency { get; } = new();
    public IReadOnlyDictionary<string, FrequencyState> Frequencies => _frequencies; // key: PositionId
    private readonly Dictionary<string, FrequencyState> _frequencies = new(StringComparer.OrdinalIgnoreCase);
}
```

Each `AiPositionConfig.PositionId` gets its own instance. `AircraftState.TunedPosition` selects which
instance an aircraft's `PendingPilotTransmissions` drain into. Cost is one dictionary lookup per drain
and one new persisted field; there is no downside once two AI-staffed positions exist, which CA2
introduces by design.

### Flow

```
AircraftState.TunedPosition (persisted)
        │  selects
        ▼
World.Frequencies[positionId]  ←── PendingPilotTransmissions drained here (existing per-aircraft queue)
        │  TryDequeueReady(elapsedSeconds) — airtime + exchange-lock gates
        ▼
Terminal (SAY line, tagged with frequency) + TTS (per-position speaker id, existing mechanism)
        │
        ▼
Client — filters to the monitored set (student: own position; instructor: selectable set)
```

Direction is **aircraft state → per-position queue → filtered client render**. Nothing flows back into
`AircraftState` from this pipeline except `TunedPosition` transitions themselves, written by `CT`,
self-switch and handoff completion — all existing command/phase code, no new mutation paths.

Client-side filtering is presentation-only: the server broadcasts every frequency somebody is
monitoring and the client decides what to render and voice, mirroring how the client already never
runs physics and only renders what it is sent. The transmission DTO gains a frequency/position-id tag.

## Snapshot and determinism

| Item | Persisted? | Why |
|---|---|---|
| `AircraftState.TunedPosition` | **Yes** — add to `AircraftSnapshotDto`, default null/empty, no migration transform | Control-flow state: it drives which position's `CT`/handoff releases the RV-SID heading hold and who the pilot's next proactive call addresses. A field that defaults cleanly, with old data correct under that default, needs no migration step per the project's snapshot rule — an old recording with no concept of multiple frequencies is correctly read as "everyone tuned to the one position that existed." Precedent: `HasLeftStudentFrequency`, added v5→v6 as a bare `bool` with no transform. `TunedPosition` generalizes that flag. |
| `World.Frequencies` / `FrequencyState` contents (queue, gate, activity meter) | **No** — transient, exactly like today's `ActiveFrequency` | Queue ordering and airtime-gate state are re-derived every tick from `PendingPilotTransmissions`, itself transient. Replay never runs the pilot-speech queue as a source of truth — it re-drives recorded commands, and pilot responses regenerate deterministically from the same inputs. |

The governing rule, which is the line the codebase already draws between `Ground.LayoutAirportId`
(persisted identity) and `Ground.Layout` (re-resolved, `[JsonIgnore]`): **anything that changes what a
human or the AI would decide next must be persisted; anything that only changes how quickly or in what
order an already-decided thing is announced stays transient.**

Determinism hazards specific to this subsystem: `Frequencies` is a keyed collection, so any iteration
over it that feeds a simulation decision must sort by an explicit key (position id) — never rely on
`Dictionary` enumeration order. Exchange timeouts must be sim-tick-counted, never wall-clock: soak runs
at ~420× realtime, so a wall-clock timeout fires at the wrong simulated moment *and* breaks replay.

## Failure modes this design has to answer

### A collision must be modelled, not silently dropped

If two stations transmit on one frequency simultaneously, neither is cleanly received — SKYbrary's
"frequency blocking," and in the complete-overlap case *neither party realizes it happened*. The
natural implementation shortcut is to let one transmission win (last-write-wins, or FCFS with the
second silently discarded), which is **more orderly than real radio** and masks exactly the congestion
and stepped-on-call workload the sim exists to train against.

Decide explicitly, per exchange type, between:
- **(a) reject with an audible "frequency busy" cue** the issuing position can act on — most realistic
  for routine calls; or
- **(b) garble both and require a "say again" recovery path** — more realistic for genuine congestion
  training, higher complexity.

`RADIO-05` commits to (b) for simultaneous keying. Whichever an exchange type uses, log every
rejected/blocked attempt distinctly from a successful one, so the soak harness can tell "the radio
model is working as designed" from "a transmission silently vanished" — the second is Tier A.

### A readback that never comes must not hold the frequency forever

Pilot AI can fail to produce the expected readback: a stuck aircraft, a despawn, or a phase transition
that leaves it unable to respond (mid-emergency, mid-go-around). Without a working release this is the
Coffman hold-and-wait condition with a radio channel as the resource.

- Treat "release the frequency" as **a single code path invoked by every exit condition** — successful
  readback, timeout, invalidating phase change, despawn — never one path per condition that each
  clears state independently and drifts. This is `RADIO-06`.
- Force the never-responds case in a test (kill the response mid-exchange, or advance a phase
  transition mid-exchange) and assert the frequency is free within one timeout window. TDD applied
  proactively to a designed-in edge case, not reactively to a filed bug.
- Watch for a compound/chained command whose readback semantics do not map 1:1 onto one exchange.

### One position's state must not clear another's gate

Once Ground and Local both instruct pilots — often the same aircraft in sequence, since a departure is
Ground's and then Local's — a `FrequencyState` keyed wrongly (per-aircraft, or one mutable object both
brains touch without ownership discipline) lets Ground completing *its* exchange reset state Local was
still relying on, or lets a Ground→Local handoff race an in-flight exchange and drop it. This is
`RADIO-02` and the reason the keying scheme is per-position rather than per-aircraft. The handoff
boundary needs its own integration test: concurrent Ground and Local exchanges around a transfer, with
exactly one active exchange per aircraft at all times.

### Anti-pattern: a frequency that belongs to code instead of to a position id

Threading a `FrequencyState` reference — or worse, a bespoke enum — through brain and phase code to
mean "the frequency this call is on," derived ad hoc per call site. The existing single
`ActiveFrequency` singleton already shows the failure mode: call sites accumulate implicit assumptions
about "the one frequency," and generalizing later means auditing every call site instead of changing
one dictionary key. It also breaks replay safety — a frequency selected by code-path identity rather
than by persisted aircraft state cannot be reconstructed consistently between a live run and its
replay.

Always resolve as `AircraftState.TunedPosition → World.Frequencies[positionId]`: one lookup, one
source of truth, matching how `Track.Owner` already resolves radar jurisdiction.

## Requirements

- [ ] **RADIO-01** — Each aircraft records the controller position it is tuned to, and that value survives snapshot, restore and replay
- [ ] **RADIO-02** — Each staffed position has its own frequency state; one position's exchange never clears a gate held on another's frequency
- [ ] **RADIO-03** — A transmission is heard by every aircraft and controller tuned to that frequency (party line), not only by its addressee
- [ ] **RADIO-04** — An exchange owns its frequency until it completes or times out; other transmissions queue behind it rather than interleaving
- [ ] **RADIO-05** — Two transmissions keyed simultaneously on one frequency garble — neither is received cleanly, and each speaker must re-request
- [ ] **RADIO-06** — A readback that never arrives releases the frequency through a sim-tick-timed timeout, never wall-clock, through one shared code path
- [ ] **RADIO-07** — An AI-issued `CT` moves the aircraft to the receiving position's frequency, and RV-SID heading hold releases correctly on that transfer
- [ ] **RADIO-08** — Every existing `ActiveFrequency` call site is migrated; no code path reads a single global frequency
- [ ] **RADIO-09** — The client plays only the frequencies the human is monitoring — own position for a student, a selectable monitor set for an instructor

## Before it is called done

- **Grep every pre-existing `ActiveFrequency` reference before starting** and enumerate which call
  sites must move — pilot-proactive call logic, readback matching, terminal-line scrub. Migrating the
  primary send path and leaving a secondary consumer on the old field is the classic version of this
  refactor going wrong; treat anything left un-migrated as a named gap, not something discovered later.
- **Every new broadcast-visible radio field needs its own explicit clear-on-resolve path**, mirroring
  the additive-with-explicit-`Delete*` CRC contract. Absence of an update never means "clear."
- **Expose exchange ownership as a readable timeline** (owner, since-tick, reason) alongside the raw
  state. Diagnosing a readback deadlock or a cross-position gate-clearing bug from raw `FrequencyState`
  fields otherwise costs as much as diagnosing the underlying bug.
- **Parity test on both tick paths.** Radio logic must run identically on the soak path and the live
  server's post-physics path, proven by a `RoomEngineTestHarness`-style test. If
  [`tick-loop-unification.md`](../tick-loop-unification.md) has landed, the spine gives this
  structurally and the test is a check rather than a construction task.
- **Aviation review** of garble, re-request and party-line phraseology against the local 7110.65 / AIM
  references.

## Source references

- `src/Yaat.Sim/Pilot/FrequencyState.cs`, `SimulationWorld.cs` — the current single-frequency model
- [`docs/solo-training-pilot-speech.md`](../../solo-training-pilot-speech.md) — the radio/airtime
  pipeline and its documented v1 limitations
- [`docs/snapshots-and-replay.md`](../../snapshots-and-replay.md) — the snapshot/migration rules the
  determinism table applies
- [`02-positions-and-handoffs.md`](./02-positions-and-handoffs.md) — the transfer semantics `RADIO-07`
  has to satisfy
- SKYbrary, [Frequency Blocking](https://skybrary.aero/articles/frequency-blocking) — partial vs.
  complete overlap, undetected simultaneous transmissions
