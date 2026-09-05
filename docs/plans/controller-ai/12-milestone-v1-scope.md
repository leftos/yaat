# v1 slice — radio model, CA2 Tower brain, H1 soak

**Status:** scoped, on hold behind the tick-path work. Phase 1 is superseded — tick-path unification is now
its own top-priority programme, decided in ADRs [0001](../../adr/0001-state-equivalence-is-the-tick-contract.md)-[0006](../../adr/0006-decompose-simulationengine-before-adding-to-it.md);
Phases 2–3 have one ([`11-radio-model.md`](./11-radio-model.md)); Phases 4–7 build against the existing
subdesigns [`05`](./05-tower-brain.md), [`07`](./07-soak-runner.md), [`08`](./08-detectors-and-findings.md).

This is the ordered v1 cut of the [controller-AI plan](./README.md) — the seven pieces of work between
CA1/K1 (shipped) and the first soak run worth trusting. It maps onto the README's milestone series as:
**Phase 1** is new groundwork, **Phases 2–4** are the radio and coordination prerequisites CA2 needs,
**Phase 5** is **CA2**, and **Phases 6–7** are **H1**.

## Done when

```
SoakRunner run --scenario oak_ground_easy.json --seed 42 --sim-hours 4 --positions GC,LC
```

completes a seeded 4-sim-hour KOAK session gate-to-gate with AI Ground and AI Local, exits with a
findings report, and the same seed reproduces byte-for-byte — on the same machine and across both
platforms the product ships on.

## Why this order

Three capabilities in a forced sequence.

**The radio model comes first** because it is a hard technical dependency, established two independent
ways: the single shared `ActiveFrequency` lets one position's response clear a gate held for a pilot on
another's frequency, and `InitialClimbPhase.UpdateRvSidHeadingHold` needs a transfer signal keyed on
`TunedPosition` that an origin-neutral AI-issued `CT` never sets today. See
[`11-radio-model.md`](./11-radio-model.md).

**CA2 comes second** because a second AI position cannot exist until two positions can talk at once.

**H1 comes last** because detector value is proportional to what is running: a detector suite over
CA1 alone — single Ground brain, no concurrency, no coordination — has little to find beyond what the
existing anomaly log already surfaces. Running it after CA2 means the first soak exercises the radio
model and the coordination bus under real load. This one is a value/sequencing choice rather than a
hard technical dependency: H1's *framework code* could in principle start in parallel with CA2.

Nothing here is greenfield — all three extend machinery that already ships, against aviation-reviewed
designs already in this folder. Technical risk is low; execution risk is real and concentrated in the
verification. The soak tick path and the live server's post-physics path have already diverged silently
once in this codebase (pilot-proactive reminders ran dark for ~2.5 months), which is why Phase 1 exists
and why every later phase that adds per-tick behaviour carries its own parity check.

Every phase is a vertical slice — state, delivery and verification together, demonstrable when done.
No phase is a technical layer, and no phase leaves either repo broken: `Yaat.Sim` changes and their
`yaat-server` counterparts land together.

## Phases

- [x] **1 — One tick spine.** Every host runs the same post-physics steps in the same order, so sim, server, replay and soak cannot silently diverge — shipped 2026-09-04 as tick step 3c (see `MAIN.md`); criteria 1–3 and 5 met, criterion 4 (desync triage) exercised by the first three retirements
- [ ] **2 — Per-position frequency state.** Two AI positions each run a pilot exchange at once, with no cross-frequency gate leakage
- [ ] **3 — Party line and transmission collisions.** Everyone tuned hears everything, simultaneous keys garble and recover, the human hears only what they monitor
- [ ] **4 — Coordination bus and position transfer.** Ground and Local coordinate off-frequency, hand aircraft over, and cannot deadlock on a shared resource
- [ ] **5 — AI Local clearances and gate-to-gate (CA2).** AI Local sequences and clears; an aircraft flies a full gate-to-gate loop with no human input
- [ ] **6 — Soak detectors and findings output (H1a).** A soak run over CA1 + CA2 ends with a deduplicated findings report a reviewer can jump into
- [ ] **7 — Trusted harness and cross-platform determinism (H1b).** The demo line runs, and the determinism the whole soak premise rests on is proven on both shipped platforms

### Phase 1 — One tick spine

**Goal:** every host that advances a sim-second — Yaat.Sim tests, replay, recording reconstruction,
the live server and the headless soak host — runs the same simulation-affecting post-physics steps, in
the same order, with the same arguments.
**Requirements:** DET-03, subsumed. **Design:** ADRs [0001](../../adr/0001-state-equivalence-is-the-tick-contract.md)-[0006](../../adr/0006-decompose-simulationengine-before-adding-to-it.md) — this phase is no longer owned by the milestone.

1. The set and order of simulation-affecting post-physics steps is defined in exactly one place, and both hosts execute it by iterating that definition rather than each maintaining a list.
2. Adding a new post-physics step to one host and not the other is a compile error, not a silent divergence — demonstrated by adding a step and observing the build fail until both hosts handle it.
3. A characterization test records the executed step order per host and shows the refactor changed nothing that was not deliberately decided: `PilotProactive` moves to the server's position, the drain order becomes server-literal, `AutoDelete` and `SoloTrainingEvaluation` run on the replay path, and `TickControllerAi` becomes the final spine step.
4. Any recording that desyncs is identified by name with its cause understood, and either kept or deleted as a recorded decision — never silenced by relaxing an assertion.
5. `pwsh tools/test-all.ps1` is green across both repos, and the existing per-feature parity tests still pass unchanged.

### Phase 2 — Per-position frequency state

**Goal:** two AI-staffed positions can each hold a pilot exchange at the same time, with neither
position's radio state able to touch the other's.
**Requirements:** RADIO-01, RADIO-02, RADIO-04, RADIO-06, RADIO-08. **Design:** [`11-radio-model.md`](./11-radio-model.md).

1. Two staffed positions each hold a concurrent pilot exchange in one running session, and neither position's readback clears a gate held on the other's frequency.
2. An aircraft's tuned position survives a snapshot → restore → replay round trip, and the replayed run reproduces the same exchange sequence it recorded.
3. A pilot that never answers releases the frequency after a sim-tick-counted timeout — at 1× and at soak's ~420× realtime alike — through one shared code path rather than a per-caller copy.
4. Every behaviour that used to key off the single shared frequency (pilot proactive calls, readback matching, terminal-line scrub) still works with two positions staffed; no surface is left reading a global frequency.
5. The radio logic runs identically on the soak tick path and the live server's post-physics path, proven by a `RoomEngineTestHarness`-style parity test, and `pwsh tools/test-all.ps1` is green across both repos.

### Phase 3 — Party line and transmission collisions

**Goal:** what is actually heard on a frequency is right — everyone tuned hears everything on it, two
simultaneous keys garble and both speakers recover, and the human hears only the frequencies they are
monitoring.
**Requirements:** RADIO-03, RADIO-05, RADIO-09. **Design:** [`11-radio-model.md`](./11-radio-model.md).
**Has UI surface.**

1. A transmission on a frequency is received by every aircraft and controller tuned to it, not only by its addressee — a student can build the picture from other people's calls.
2. Two transmissions keyed on the same tick garble: neither is received cleanly, each speaker re-requests, and both exchanges recover rather than stalling.
3. The client plays only what the human is monitoring — a student hears their own position, an instructor hears a monitor set they select and can change mid-session.
4. The garble, re-request and party-line phraseology passes `aviation-sim-expert` review against the local 7110.65 / AIM references.
5. Collisions are deterministic — the same seed produces the same garbles and the same recoveries on replay — and the soak and live tick paths deliver transmissions identically under the parity test, with both repos green.

### Phase 4 — Coordination bus and position transfer

**Goal:** AI Ground and AI Local coordinate off-frequency and hand aircraft to one another, with an
enumerated precedence for every shared resource and no path to a deadlock.
**Requirements:** RADIO-07, TOWER-03, TOWER-04, TOWER-05, TOWER-07. **Design:** [`05-tower-brain.md`](./05-tower-brain.md).

1. An aircraft handed from AI Ground to AI Local is on the receiving position's frequency afterwards, and RV-SID heading hold releases on that transfer — the AI-issued `CT` behaves like a real handoff, not an origin-neutral no-op.
2. Ground ↔ Local coordination happens over the coordination bus and is never audible on a pilot frequency.
3. Every shared resource — runway crossings, hold-short lines, LUAW slots — resolves through an enumerated precedence table, and a contested resource yields the same winner on every run of the same seed.
4. A coordination request that goes unanswered times out and takes an escape path; the session keeps running instead of locking the two positions against each other.
5. Brains evaluate sequentially in a fixed position order so tie-breaks reproduce, the coordination logic runs on both the soak and live tick paths under the parity test, and `pwsh tools/test-all.ps1` is green across both repos.

### Phase 5 — AI Local clearances and gate-to-gate (CA2)

**Goal:** with AI Ground and AI Local both staffed, an aircraft flies a complete gate-to-gate loop
under AI control.
**Requirements:** TOWER-01, TOWER-02, TOWER-06. **Design:** [`05-tower-brain.md`](./05-tower-brain.md).

1. AI Local issues takeoff clearances sequenced first-come-first-served with wake-turbulence separation drawn from the existing wake and runway-occupancy data, not from a second re-derived separation rule.
2. AI Local issues landing clearances and line-up-and-wait, and an arrival lands and exits the runway under its control.
3. An aircraft completes gate → taxi → takeoff → arrival → landing → taxi → gate with both positions staffed and no human input at any point.
4. The clearance, sequencing and separation behaviour passes `aviation-sim-expert` review against the local 7110.65 / AIM references.
5. The Tower brain runs identically on the soak and live server tick paths under the parity test, and `pwsh tools/test-all.ps1` is green across both repos.

### Phase 6 — Soak detectors and findings output (H1a)

**Goal:** a soak run over the CA1 + CA2 scenario finishes with a findings report a reviewer can act on
— one entry per pathology, and a fast path from the entry to the moment it happened.
**Requirements:** SOAK-01 … SOAK-06. **Design:** [`08-detectors-and-findings.md`](./08-detectors-and-findings.md).

1. A run that hits a tick exception records it as a Tier A finding and keeps going, rather than dying silently or taking the run down with it.
2. Stuck-aircraft detection stays quiet on correct hold-short, parked and pattern states, and fires on genuinely stalled ones.
3. AI command rejections appear with their classified reason, and correct safety refusals do not show up as findings at all.
4. One underlying pathology yields one finding — repeated symptoms attach to it as occurrences instead of arriving as forty peers.
5. A finished run leaves `findings.jsonl`, a summary, a snapshot per finding, and timeline bookmarks that jump the tick animator to the finding's onset tick; detectors register through one interface and run on both tick paths under the parity test, with both repos green.

### Phase 7 — Trusted harness and cross-platform determinism (H1b)

**Goal:** the demo line runs end to end, and the determinism guarantee the entire soak premise rests on
is measured rather than asserted — including across the two platforms the product ships on.
**Requirements:** SOAK-07, SOAK-08, SOAK-09, DET-01, DET-02.

1. The demo line completes a 4-sim-hour KOAK session gate-to-gate with AI Ground and AI Local, and exits with a findings report.
2. Re-running that seed reproduces the run byte-for-byte on the same platform, and CI proves a seeded run reproduces byte-for-byte between Windows x64 and macOS/ARM64.
3. A documented false-positive rate exists, measured on a known-good baseline scenario, before the detector thresholds are trusted.
4. The known taxi-deadlock recording fires the detectors as a positive control, proving they catch a real pathology and not only synthetic ones.
5. Memory and disk stay bounded over a full-length multi-hour run, measured with `dotnet-counters`-class tooling rather than inferred from a short smoke run.

## Requirements

Radio requirements (`RADIO-01` … `RADIO-10`) live in [`11-radio-model.md`](./11-radio-model.md).

### Tower — CA2 brain and coordination

- [ ] **TOWER-01** — An AI Local position issues takeoff clearances sequenced first-come-first-served with wake-turbulence separation, consuming the existing wake and runway-occupancy data rather than re-deriving separation
- [ ] **TOWER-02** — AI Local issues landing clearances and line-up-and-wait
- [ ] **TOWER-03** — AI Ground and AI Local coordinate off-frequency over a coordination bus, not over a pilot frequency
- [ ] **TOWER-04** — Precedence is explicit for every shared resource — runway crossings, hold-short lines, LUAW slots — as an enumerated table, not an implicit tick order
- [ ] **TOWER-05** — A coordination request that goes unanswered times out and takes an escape path rather than deadlocking
- [ ] **TOWER-06** — With Ground and Local both staffed, an aircraft completes a full gate-to-gate loop
- [ ] **TOWER-07** — Brain evaluation is sequential and in a fixed position order, so tie-breaks are reproducible

### Soak — H1 detectors and findings

- [ ] **SOAK-01** — Detectors register against the tick loop through one common interface
- [ ] **SOAK-02** — Tier A hard failures — tick exceptions and their kin — are detected and recorded
- [ ] **SOAK-03** — Stuck-aircraft detection is phase-aware and does not fire on correct hold-short, parked or pattern states
- [ ] **SOAK-04** — AI command rejections are classified by reason and do not fire on correct safety refusals
- [ ] **SOAK-05** — Findings are deduplicated by condition key so one pathology yields one finding
- [ ] **SOAK-06** — A run emits `findings.jsonl`, a summary, per-finding snapshots, and timeline bookmarks that jump to the moment of the finding
- [ ] **SOAK-07** — The false-positive rate is measured against a known-good baseline scenario before the harness is trusted
- [ ] **SOAK-08** — The known taxi-deadlock recording fires as a positive control, proving the detectors catch a real pathology
- [ ] **SOAK-09** — Memory and disk stay bounded over a full-length multi-hour run

### Determinism and parity

- [ ] **DET-01** — The same seed reproduces a run byte-for-byte on the same platform
- [ ] **DET-02** — A seeded run reproduces byte-for-byte between Windows x64 and macOS/ARM64, verified in CI
- [ ] **DET-03** — The soak tick path and the live server's post-physics path run matching logic, verified by a parity test rather than assumed

### Coverage

29 requirements, each mapped to exactly one phase. No orphans, no duplicates.

| Phase | Requirements | Count |
|---|---|---|
| 1 — One tick spine | DET-03 | 1 |
| 2 — Per-position frequency state | RADIO-01, 02, 04, 06, 08 | 5 |
| 3 — Party line and collisions | RADIO-03, 05, 09 | 3 |
| 4 — Coordination bus and transfer | RADIO-07, RADIO-10, TOWER-03, 04, 05, 07 | 6 |
| 5 — AI Local clearances (CA2) | TOWER-01, 02, 06 | 3 |
| 6 — Detectors and findings (H1a) | SOAK-01 … 06 | 6 |
| 7 — Trusted harness (H1b) | SOAK-07, 08, 09, DET-01, 02 | 5 |

**DET-03 note.** Tick-path parity is owned by Phase 1, which unifies the two post-physics paths onto
one ordered spine so they cannot diverge by construction. Phases 2 through 6 still carry a parity check
for the per-tick behaviour they each add — the spine makes divergence unrepresentable, but only for
steps that go through it, and a phase that adds a host-only step still has to show the two hosts agree.

## Failure modes to design against

Radio-specific failure modes (collision handling, readback deadlock, cross-position gate clearing) are
in [`11-radio-model.md`](./11-radio-model.md). These are the cross-cutting ones.

### Opposing-flow deadlock generalizes to every Ground↔Tower resource boundary

The calibration example from CA1 — arrivals taxiing in against departures taxiing out on the same
taxiway, reactive give-way physics locking both flows head-on within ~20 minutes — is not a one-off
Ground bug. It is **two agents each waiting for the other to vacate a shared physical resource, with no
global ordering and no timeout**: the full Coffman set (mutual exclusion, hold-and-wait, no preemption,
circular wait). Nobody designs the cycle; it emerges from two individually-reasonable local rules with
no dependency graph tracking who waits on whom. With Tower coordinating over runway crossings,
hold-short lines and LUAW slots, the same class reappears at every new boundary — Tower withholding a
landing clearance until Ground confirms a crossing aircraft has vacated, while Ground withholds the
crossing clearance waiting on Tower's traffic-advisory state.

*Avoid by:* imposing a **global ordering** on contested resources so give-way breaks ties consistently
instead of symmetrically (the highest-leverage single fix per the AGV/traffic-simulation literature);
giving every wait-for-another-agent state a **hard timeout with an escape action**; building the
coordination bus so it can express a **wait-for graph** (who is blocked on whom, for what) rather than
opaque busy/idle flags — that is what makes this class detectable by the soak harness instead of only
reproducible by luck; and treating the runway/taxiway boundary as a contested resource in the design,
not as "Ground's problem" or "Tower's problem."

*Warning signs:* multiple aircraft with zero net displacement for >60–120 s while still in a
non-terminal phase; symmetric yield chains in logs (A yields to B, B yields to A, or a longer cycle);
a Tower decision that never fires because it is conditioned on a Ground-owned state that only advances
once Tower's own decision fires.

*Phase:* 4 (bus design), verified in 6–7 by a dedicated deadlock/cycle detector.

### Livelock is a distinct failure mode and needs its own detector

Reactive give-way can also produce **livelock**: both aircraft actively yield to each other — stopping,
restarting, re-evaluating, stopping again — burning time without net progress. Position and velocity
are not static, so a "zero displacement for N seconds" stuck detector will not catch it and the run
looks busy in logs. Symmetric priority rules ("yield to whichever is closer to the conflict point")
oscillate when the relative distances flip as each aircraft partially advances then retreats.

*Avoid by:* tracking a rolling window of **net progress** (distance-to-goal delta over N seconds), not
instantaneous velocity; breaking ties on a value that cannot flip mid-encounter (aircraft ID, entry
timestamp into the conflict zone); adding a **commit state** — once an aircraft holds right-of-way at a
conflict point it keeps it until clear, even if the recomputed comparison would flip. That last one is
the anticipation/lookahead fix the traffic-simulation literature converged on.

*Phase:* give-way hardening in 4–5; a distinct livelock detector in 6.

### Priority inversion — Tower's time-critical decision behind Ground's routine work

If both brains funnel decisions through one dispatch path without priority awareness, a burst of
routine Ground taxi instructions can delay a separation-critical Tower decision that arrived a moment
later and matters far more. "Brains issue canonical commands only; phases execute" is the right
architectural mitigation — brains hold no locks — but the *evaluation order within a tick* still needs
an explicit scheme once two brains compete for the same per-tick budget, or it defaults to something
incidental (declaration order, dictionary iteration order) that is both non-deterministic and not
priority-aware.

*Avoid by:* giving the coordination bus explicit priority tiers (separation-affecting Tower decisions >
routine Ground taxi > housekeeping), decided every tick rather than FIFO; keeping the single-thread tick
constraint as the enforcement mechanism — a priority queue on one thread is trivial, and reaching for
parallelism to "solve" ordering reintroduces the determinism hazards below; bounding how long a
lower-priority decision can hold up a higher-priority one.

*Phase:* 4.

### Determinism loss

Four sources, each individually an idiomatic C# choice that stops being safe the moment the code path
is reachable from simulation state rather than pure I/O:

- **Unordered collections.** Iterating a `Dictionary`/`HashSet` (per-position `FrequencyState`, the set
  of aircraft contending for a hold-short line) and using enumeration order as a tie-breaker. Sort by
  an explicit deterministic key before consuming order.
- **Parallel brain evaluation.** `Parallel.ForEach`/`Task.WhenAll` over aircraft or over the two brains
  for wall-clock speed at ~420× realtime. If either brain's outcome depends on the other's within a
  tick, the result depends on scheduler timing. Keep brain evaluation single-threaded per room; get
  soak throughput from parallel *rooms or processes*, never from parallelism inside one room's tick.
- **Hidden wall-clock and RNG.** `DateTime.Now`, `Guid.NewGuid()`, unseeded `new Random()` anywhere in
  brain logic, exchange-timeout math or tie-breaking. Route any new ID/counter generation through
  per-room deterministic state seeded and advanced in tick order — never a `static` field, which
  becomes a cross-room race the moment the soak runner hosts multiple rooms in one process.
- **Cross-platform float drift.** IEEE 754 basic operations are deterministic per-platform, but
  transcendental functions — `sin`/`cos`/`atan2`, exactly what bearing and geometry math uses — are not
  guaranteed bit-identical across x86 and ARM. "Same seed reproduces byte-for-byte" is implicitly a
  same-architecture guarantee until DET-02 verifies otherwise, and YAAT ships a notarized Apple Silicon
  build alongside Windows x64.

*Warning signs:* a finding that reproduces on one run of a seed but not a re-run of the same seed on
the same machine (state-level); a finding that reproduces on Windows but not macOS for an identical
seed (float drift).

*Phase:* 2 and 4 introduce the risk surface; 7 proves it holds.

### The soak/live tick split exempts new features from production

Wiring new radio, coordination or detector logic into only one host. The two live in different repos,
so a `git grep` in this one cannot reveal the gap. Add the logic in one of the two
[`docs/tick-loop.md`](../../tick-loop.md) safe shapes and write a `RoomEngineTestHarness` parity test
*before* considering the feature done. Phase 1 removes this class structurally for anything that goes
through the spine.

*Phase:* all of them, as an explicit criterion rather than an assumption.

### Naive detectors flood false positives

Emitting a finding every tick a condition is true is the textbook anti-pattern — it drowns the
true-positive signal and makes findings un-triageable. The condition-keyed model in
[`08`](./08-detectors-and-findings.md) — a condition keyed by `(detectorId, subjectKey)`, updated while
it persists, closed into exactly one finding when it resolves — is the industry-standard shape
(Alertmanager-style fingerprint grouping), not a bespoke invention. It requires every detector to define
a **stable** `subjectKey`: key on a mutable display string instead of a callsign and findings silently
fragment or over-merge.

A reviewer must not have to notice that forty "stuck aircraft" findings are one taxiway deadlock.
Group and correlate by resource or aircraft-pair, surface the root-cause finding first with repeated
symptoms as supporting occurrences. Bookmark the finding's **onset** tick, not its detection tick — the
detection tick lags onset by the debounce window, and the whole point of the bookmark is a fast path to
the 30 seconds worth watching in the tick animator.

*Phase:* 6, calibrated in 7.

### Long-run resource growth

`findings.jsonl` with no rotation or dedup, per-finding snapshot serialization on the hot tick path, and
unbounded accumulators are all invisible in a short smoke run and fatal in a multi-hour one. Serialize
and flush snapshots **off** the tick-critical path — the "nothing reachable from `TickPhysics` may block
on I/O" constraint already applies. Note the failure lands first during exactly the sustained-pathology
scenario the harness exists to find.

*Phase:* 7, measured with `dotnet-counters`-class tooling over a full-length run.

### Performance traps

- **O(n²) coordination-bus conflict checks** (every aircraft against every other aircraft's held
  resource each tick). Index contested resources — hold-short lines, runway segments, frequencies — so
  a lookup is by resource key, not a full pair scan. Bites well before the target traffic density.

## "Looks done but isn't" checklist

- [ ] **Radio:** the never-responds/timeout edge case is a designed-in, *tested* path — a dedicated test forces a stuck pilot response and asserts the frequency releases within one timeout window.
- [ ] **Radio:** every pre-existing `ActiveFrequency` call site is migrated — verified by grepping for zero remaining references, not by "the primary send path was updated."
- [ ] **CA2:** a live-server parity test exists, not just soak-path coverage.
- [ ] **CA2:** the precedence table is exhaustive against every resource type Ground and Local can both touch, not only the ones the initial test scenarios exercise.
- [ ] **H1:** a documented false-positive rate from a no-pathology calibration run exists before thresholds are trusted — "it fires on the obviously-broken scenario" is not calibration.
- [ ] **H1:** the known give-way deadlock recording is wired as a positive control before the suite is trusted on novel scenarios.
- [ ] **H1:** memory and disk are verified bounded over a full representative-length run, not inferred from a short one.
- [ ] **All:** a same-seed-replayed-twice determinism check is an automated Tier A assertion, not implied by "the architecture is deterministic."

## Design work before planning

- **Phase 4** — the resource-precedence matrix is implicit today. Enumerate which position has priority
  over which shared resource before the plan finalizes. This is the one phase research flagged as
  needing deeper design work during planning.
- **Phase 7** — confirm an ARM64/macOS CI runner is available for DET-02. If none exists, that is a
  planning-time decision for the maintainer, not a silent deferral.

## Recurring checks

- **Tick-path parity.** Owned by Phase 1; every later phase carries the check for the behaviour it adds.
  A late audit would be too late — this has already caused a silent ~2.5-month failure here.
- **Aviation review.** Mandatory for any change to pilot AI, ATC logic, phraseology or phase
  transitions. Phase 3 (garble and party-line phraseology) and Phase 5 (clearances, sequencing,
  separation) carry it explicitly.
- **Cross-repo landing.** `Yaat.Sim` and `yaat-server` changes land together; every phase ends at
  `pwsh tools/test-all.ps1` green across both repos.

## Deferred past v1

**Radio:** stuck-mic and blocked-transmission simulation beyond simultaneous-key garbling; per-position
TTS voices so each AI controller sounds distinct.
**Tower:** constrained-position-shift sequencing (k=1–3), gated on soak evidence of a real throughput or
realism gap against the FCFS baseline.
**Soak:** crash-fingerprint dedup and minimal-repro trimming, once finding volume makes it necessary; an
offline LLM summarizer over already-captured findings — undecided whether that violates the no-LLM rule,
since it drives no simulation decisions.

## Out of scope

| | Reason |
|---|---|
| LLM driving controller decisions or soak exploration | Breaks deterministic replay, which the entire soak premise rests on |
| MILP or reinforcement-learning runway sequencers | Reintroduce non-determinism or heavyweight solvers; FCFS is the research baseline anyway |
| Rule engine (NRules, RulesEngine) or behaviour-tree library | Solve "rules that change without recompiling" — the opposite of fixed, aviation-reviewed, TDD'd rule sets |
| OpenTelemetry SDK for findings output | No first-party local-file exporter exists; a hand-written JSONL writer is the sanctioned answer |
| Controller AI on published builds or public servers | Internal-only by design — dev and local builds |
| CA3, CA5, CA6, CA7, H2–H5, K2, K3 | Later milestones; this slice ends at the first useful soak |
