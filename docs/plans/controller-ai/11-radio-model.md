# Per-frequency radio model

**Milestone:** the radio prerequisite of the v1 slice — see
[`12-milestone-v1-scope.md`](./12-milestone-v1-scope.md). Ships before CA2, because CA2 is what puts
two AI positions on the air at once.
**Owns:** requirements `RADIO-01` … `RADIO-10`.
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

**Party line is presentation-only in v1** (`RADIO-03`): every aircraft and controller tuned to a
frequency *hears* the traffic on it, but no pilot AI changes its behaviour because of a call addressed
to someone else. Tagging each transmission with its position id at the source is what keeps AI
reception a later filter rather than a rewrite of the send path — "I heard him get cleared to land"
sequencing awareness is a separate behavioural surface with its own aviation review, deliberately out
of the v1 slice.

**Do not reuse CTAF as precedent for this.** AIM §4-1-9.g's non-towered model — one shared advisory
channel where everyone transmits and everyone hears — looks superficially like party line but is the
opposite case: it has no position split and no controller. A towered field's frequencies are
function-scoped by rule (7110.65 §2-4-1: "Use radio frequencies for the special purposes for which
they are intended… do not use ground control frequency for airborne communications"). A non-towered
field reusing the towered party-line path as though it were a single position would model the right
audio for the wrong reason, and would break the moment a position is staffed.

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

**Settled: garble uniformly, for every exchange type.** A "frequency busy" rejection cue has no
procedural basis and is more orderly than real radio, which is the failure this section warns about.
The real term is P/CG **BLOCKED** — "a radio transmission has been distorted or interrupted due to
multiple simultaneous radio transmissions" — and the recovery path is the pilot's own, per AIM
§4-2-2.a/.d and §4-2-3.a.3: listen before transmitting, wait a few seconds, call again. Nothing in
either publication lets a blocked transmission count as received.

Log every blocked attempt distinctly from a successful one, so the soak harness can tell "the radio
model is working as designed" from "a transmission silently vanished" — the second is Tier A.

### A readback that never comes must not hold the frequency forever

Pilot AI can fail to produce the expected readback: a stuck aircraft, a despawn, or a phase transition
that leaves it unable to respond (mid-emergency, mid-go-around). Without a working release this is the
Coffman hold-and-wait condition with a radio channel as the resource.

- Treat "release the frequency" as **a single code path invoked by every exit condition** — successful
  readback, timeout, invalidating phase change, despawn, **and communications transfer** — never one
  path per condition that each clears state independently and drifts. This is `RADIO-06`.
- **A transfer abandons the exchange; it never carries it.** If a pilot asks Ground for something and
  is transferred before Ground answers, Ground owed the answer and dropped it (a controller error
  against §2-1-18 and §5-4-5.h). The receiving position owes nothing until the pilot asks *it*: there
  is no codified notion of a request pending across a frequency change. The exchange lock releases,
  but `PilotPendingRequest` survives, so the pilot re-prompts the receiving position — P/CG *STAND BY*,
  "the caller should reestablish contact if a delay is lengthy." Letting the exchange re-target the
  receiving position instead would re-create this file's own headline bug one level up, at the
  frequency boundary rather than inside one frequency.
- Correct operation should rarely reach this path: §3-10-9.b puts the frequency change **last** in the
  transmission (Local issues taxi instructions, takes the readback, *then* hands off) precisely so a
  transfer cannot land mid-exchange. The abandon path exists for when the AI or the student gets that
  ordering wrong, which is exactly what the soak harness is for.
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

### MONITOR is a transfer, not a second frequency

`CT` is not the only way an aircraft changes frequency. 7110.65 **§2-1-17.e**:

> "In situations where an operational advantage will be gained, and following coordination with the
> receiving controller, you may instruct **aircraft on the ground** to monitor the receiving
> controller's frequency. EXAMPLE — 'Monitor Tower.' 'Monitor Ground.'"

P/CG *MONITOR* defines what it suppresses: "listen on a specific frequency and stand by for
instructions. **Under normal circumstances do not establish communications.**" So MONITOR moves
`TunedPosition` exactly as CONTACT does. The only difference is that the pilot makes no check-in call
on arrival. It is emphatically **not** "guard two frequencies at once" — the aircraft leaves the
transferring position's frequency either way. The CPDLC analogue says the same (AIM §5-3-1, UM120:
"The flight crew is not required to establish voice contact on the frequency").

Scope: §2-1-17.e is written for aircraft **on the ground**. The one airborne monitor instruction in
the book is §5-9-4.d.1 ("Monitor local control frequency, reporting to the tower when over the
approach fix"), which again moves the aircraft to Tower with a deferred first call.

**Command surface.** MONITOR becomes a new canonical command
(`CanonicalCommandType.Monitor`), aliased `MON` / `MONITOR`, deliberately mirroring CONTACT's
`CON` / `CONTACT`. The symmetry is the point: two transfer verbs that differ only in whether the pilot
checks in should read as a pair at the prompt.

It is **not** a flag on the contact command. The two produce different pilot behaviour and a student
hears the difference directly — an unprompted check-in call is exactly what P/CG *MONITOR* says must
not happen — so a flag would hide a behavioural fork behind an argument.

`CON` is freed from `Consolidate` to make this pairing work. Two things justify the reassignment:
neither `atctrainer-commands.json` nor `vice-commands.json` maps `CON` (or any alias) to
consolidation, so no upstream naming is broken; and instructors overwhelmingly consolidate through
CRC rather than YAAT, making it the lower-traffic claim on a three-letter prefix. `Consolidate` keeps
its full name and takes `CONS` if a short form is still wanted. This is a user-facing alias removal,
so it needs a `CHANGELOG.md` entry under `### Changed`, not just a doc note.

Contact keeps `CT` and `CONT` alongside the new `CON` / `CONTACT`: they cost nothing, and every
existing scenario preset, `COMMANDS.md` row and instructor's muscle memory already uses `CT`.

Adding the command and rebalancing the aliases means entries in `CanonicalCommandType`,
`CommandScheme.Default()`, `CommandRegistry.All`, `COMMANDS.md` (both the quick-reference table and the
detailed section) and `docs/command-cheatsheet.json` (then `node tools/build-cheatsheet.mjs`) — the
completeness tests enforce the first three, and the cheatsheet sync check enforces the last.

Two same-type positions (LC1/LC2, GC1/GC2) are out of scope here and tracked separately: the pilot is
never expected to infer which one to call. Resolution is always explicit — via ATIS (§2-9-2 NOTE: "The
ATIS may be used to specify the desired frequency") or an explicit instruction naming the frequency
(§2-1-17.b.2) — and the location name is omitted for a transfer inside one facility (§2-1-17.b.1), so
the LC1/LC2 distinction never surfaces as a pilot-side decision at all.

### Non-goal: relay

There is exactly one legitimate real-world case where another position's answer reaches a pilot
without the pilot ever tuning that position: **relay**. §2-1-17.g ("Whenever possible, *relay*
necessary control instructions until the pilot is able to change frequency", for single-piloted
helicopters) and the §5-9-4.d NOTE on single-frequency approaches ("it will be necessary to *relay*
tower clearances or instructions to preclude changing frequencies prior to landing").

Relay does not contradict `RADIO-02`: the content originates elsewhere, but it is **transmitted by the
position the pilot is currently working, on that position's frequency**, so the speaker and the
awaited party are still the same. The gate is discharged by the position that holds it.

**Not built in v1.** Recorded here so `RADIO-02` is not later misread as claiming that all
cross-position content transfer is invalid, and so nobody "fixes" correct behaviour into existence.

### Implementation ordering

Settled in design review; not negotiable at implementation time without revisiting this file.

1. **The tick spine lands first.** Per [`12-milestone-v1-scope.md`](./12-milestone-v1-scope.md) and
   `MAIN.md`, the v1 slice is one tick spine → per-frequency radio → CA2 → H1. With the spine in place
   the both-tick-paths parity test is a check rather than a construction task; without it, the harness
   gets built by hand now and rebuilt afterwards.
2. **Then the `ExchangeState` fold, alone.** Collapse `_awaitingReadbackFrom` and
   `_awaitingControllerResponseTo` into one lock while the frequency is *still global*. This is a
   behaviour-preserving refactor and must be validated against the existing recordings as such.
3. **Then the per-position keying.** `World.Frequencies[positionId]` plus `AircraftState.TunedPosition`.

Steps 2 and 3 are separate commits, never one. Both change how transmissions are ordered, so shipping
them together leaves any replay divergence with two candidate causes and no cheap way to bisect. This
is the project's standing "split a mechanical refactor from the feature that motivates it" rule, and
this change is the case it was written for.

## Requirements

- [ ] **RADIO-01** — Each aircraft records the controller position it is tuned to, and that value survives snapshot, restore and replay
- [ ] **RADIO-02** — Each staffed position has its own frequency state; one position's exchange never clears a gate held on another's frequency
- [ ] **RADIO-03** — A transmission is heard by every aircraft and controller tuned to that frequency (party line), not only by its addressee
- [ ] **RADIO-04** — An exchange owns its frequency until it completes or times out; other transmissions queue behind it rather than interleaving
- [ ] **RADIO-05** — Two transmissions keyed simultaneously on one frequency garble — neither is received cleanly, and each speaker must re-request
- [ ] **RADIO-06** — A readback that never arrives releases the frequency through a sim-tick-timed timeout, never wall-clock, through one shared code path; a communications transfer releases it through that same path, and the pilot re-prompts the receiving position
- [ ] **RADIO-07** — An AI-issued `CT` or `MON` moves the aircraft to the receiving position's frequency, and RV-SID heading hold releases correctly on that transfer; `MON` additionally suppresses the pilot's check-in call
- [ ] **RADIO-08** — Every existing `ActiveFrequency` call site is migrated; no code path reads a single global frequency
- [ ] **RADIO-09** — The client plays only the frequencies the human is monitoring — own position for a student, a selectable monitor set for an instructor that **defaults to every staffed frequency** and persists in `UserPreferences`
- [ ] **RADIO-10** — `MON` exists as a canonical command with a `MONITOR` alias, is ground-only, and moves the aircraft to the receiving position's frequency without triggering a check-in call

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
  the tick spine (ADR [0001](../../adr/0001-state-equivalence-is-the-tick-contract.md)) has landed, it gives this
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
