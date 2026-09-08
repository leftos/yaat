# Tick-path unification

**Status:** top priority (steer 2026-09-02; controller AI waits). Steps 1–3 and 3d shipped (ADRs 0001–0007); **step 4, relocation, is underway** — [04-relocation.md](./04-relocation.md). Its first slice shipped 2026-09-06 ([04a](./04a-attendance-and-track-automation.md)): attendance is a recorded input, and delayed handoffs, auto-accept, point-out auto-ack and the autotrack passes are Sim steps — the `ProcessAutoAccept` family the step-5 residual was made of is retired and every oracle baseline is empty. The session clock shipped 2026-09-07 (one pinned `SessionStartUtc`; TDLS timestamps, strip PDT/ETA text and the METAR observation clock read it). Strips and TDLS crossed into the snapshot 2026-09-07 (engine-owned; the recorded TDLS verbs and the spawn hooks re-apply on every run kind; the `actions`/`s2oak4` test and replay baselines now bank the strip/TDLS host bodies). Shrinking `IActionHost` is underway (plan 04c, deleted — its record is the `IActionHost` item in [04-relocation.md](./04-relocation.md)): the TDLS bodies (commit A) and the strip bodies (commit B) crossed 2026-09-07 — the `Strip`/`Tdls`/`TdlsOps` arms are Sim arms, the strip auto-print, deferred strip dispatch and TDLS steps are Sim steps, `IHostSteps`'s step-4 debt is `LiveTrafficSync`, `CoordinationTimers`, `TowerLists`, and **all twelve oracle baselines are empty again**; the remaining `IActionHost` slots are coordination, ASDE-X/SAID, CRR groups, bookmarks and the clock; also open on step 4: a reconstruct-vs-live-log pin on the 2026-09-06 S2-OAK-4 bundle (go-around timing drift; possibly already fixed by 3d-4b).

The design was re-derived clean-room this session; the decisions are ADRs [0001](../../adr/0001-state-equivalence-is-the-tick-contract.md)–[0006](../../adr/0006-decompose-simulationengine-before-adding-to-it.md) and the vocabulary is [`CONTEXT.md`](../../../CONTEXT.md). It replaces the deleted `tick-loop-unification.md`, which scoped to post-physics only and carried three factual claims that did not hold. Ordered steps, each green on its own, incremental to main:

## Steps

| Step | File | Status |
|---|---|---|
| 1. Oracle first (+ 1b: the missing legs and the two blind-spot fixtures) | [01-oracle.md](./01-oracle.md) | shipped 2026-09-02 |
| 2. `SimulationEngine` decomposition | [02-engine-decomposition.md](./02-engine-decomposition.md) | shipped 2026-09-02 |
| 3. Spine over the whole sim-second, run profile, `host` rename (3a, 3b, 3c-0, 3c) | [03-spine.md](./03-spine.md) | shipped 2026-09-04 |
| 3d. The action router (3d-0 … 3d-6) | [03d-action-router.md](./03d-action-router.md) | shipped 2026-09-06 (ADR 0007) |
| 4. Relocate tick-reachable ATC logic into `Yaat.Sim` | [04-relocation.md](./04-relocation.md) | **underway** — first slice (attendance + track automation, [04a](./04a-attendance-and-track-automation.md)) shipped 2026-09-06 |
| 5. Retire the accepted divergences; hash + step trace | [05-retirements.md](./05-retirements.md) | three retirements done 2026-09-04; the rest open |

## Rules every step follows

- **Predict, then re-baseline.** Every sub-commit names the baseline entries it will retire *before* running `YAAT_ORACLE_REBASELINE=1`; an unpredicted `Removed` or `Added` stops the work until it is attributed. **The trap this sequencing exists for:** a live-side regression makes a divergence *disappear*, which the oracle reports under `Removed` — and `TickOracleBaseline.Describe` prints "divergence path(s) GONE — if that was the intent, re-baseline to bank it". A regression presents as a congratulation with a suggested fix. 3c commits must be baseline-neutral in `Added` **and** `Removed`; re-baselining is not an available response there
- **Corpus triage** (ADR 0004): an over-broad assertion → fix the test; a genuine desync → delete the recording; an unexpected cause → stop.
- **Green cross-repo before every commit** (`pwsh tools/test-all.ps1`), TDD red-first, and the per-step log records predicted-vs-got so a later reader can check the attribution.
- **Prerequisite: met 2026-09-02** by step 1b above — the oracle now drives the replay leg, the reconstruct leg, and weather. The guard-count discrepancy it inherited (ADR 0005 "eight", 3b "five sites, two reads") was settled with 3b: five sites, eight flag reads, recorded in ADR 0005

## Known live behaviour changes to land as named decisions

ASDE-X/SAID coast moves off wall-clock to sim-time (ERAM coast already is); the three post-physics ordering moves; `DrainAllApproachScores` consumed on every path

## Aviation-review findings (2026-09-02, over ADRs 0002/0003; citations verified against the local 7110.65)

  - [ ] **Auto-accept must be suppressed while a track shows CST.** §5-4-5.5 (transferring controller) and §5-4-6.f.3 (receiving controller), both IFR and VFR, require *verbal* coordination when CST/FAIL/NONE/IF/NT/TRK is displayed. `ProcessAutoAccept` has no coast check today, so a coasting track is silently auto-accepted. Only possible once ADR 0003 brings coast state across the boundary in a form the auto-accept path can read — land the two together
  - [x] **Pointout auto-ack inverts §5-4-7.a.1.a** — fixed 2026-09-07 (step-4 follow-up 3: `TickPointoutTimeout` withdraws after `PointoutNoActionSeconds` = 30 s and advises the initiator; withdraw everywhere, no a.1.(b) carve-out — user decision; the display divergence from STARS, which never times a point-out out, is deliberate). Original finding: "If the receiving controller takes no action, revert to verbal procedures." Non-response must not become approval. Withdraw the pending point-out on timeout and advise the initiator to coordinate verbally — **priority up since step 4 sub-commit B (2026-09-06)**: the auto-ack body now runs on every rewind and bundle reconstruction, so the inversion is reproduced in every debrief artifact, not only live — that also clears the flashing indicator the current timer exists to solve. (§5-4-7.a.1.(a) is EN ROUTE and is where "revert to verbal procedures" lives; §5-4-7.a.1.(b) extends automated point-out approval to TERMINAL only where a facility directive/LOA establishes the procedure — and even then the approval is the receiving controller's own system response after the §5-4-7.b.1 association, so an unattended position's silence could never qualify)
  - [ ] **Fix the false ERAM-coast citation** at `CrcVisibilityTracker.cs:531` — there is no §5-13-8.3; §5-13-8 governs a *controller manually initiating* a coast track and prescribes no automatic reacquisition. The §5-13-7 citations elsewhere are correct and stay (it says a coast track may not be used for separation, which is exactly what they claim)
  - [ ] Coordination expiry warning fires at 120 s of a 180 s window, so the "about to expire" state is on for two-thirds of its life — move to the last 30-60 s (`TickProcessor.cs:1301`)
  - [ ] Product call: raise the 3 s solo auto-accept floor (`TickProcessor.cs:1342`) to at least the 5 s default so a student sees a handoff sit pending — a training-value judgement, no citation. `HandoffUnacceptedRule` derives its horizon from the same constant
  - [ ] Separate issue, pre-existing: `ApproachEvaluator.ComputeSeparation` (`:164`) measures the trailer's establishment position against the leader's *current* position, including aircraft that have already landed and taxied in — not an in-trail separation at a common instant. Consuming scores on every path spreads the number to more surfaces

## History

- [x] Latent bug found during the review, fixed independently 2026-09-02 (`5ee2f4a0`): `_replayTrackApplier` was documented replay-only but `DispatchAiCommand` uses it on the live path, so a recorded `AS` carrying an AI connection id displaced the live AI's identity. `ResolveEffectiveIdentity` now resolves the AI branch before the shared selected-position map; pinned by `ReplayTrackApplierIdentityIsolationTests`
