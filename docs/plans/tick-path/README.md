# Tick-path unification

**Status:** top priority (steer 2026-09-02; controller AI waits). Steps 1–3 and 3d-0 … 3d-5a shipped; **3d-5b is in progress** (3d-5b-1 shipped, 3d-5b-2 built and awaiting commit; 3d-5b-2 next) — the plan is in [03d-action-router.md](./03d-action-router.md) § 3d-5b.

The design was re-derived clean-room this session; the decisions are ADRs [0001](../../adr/0001-state-equivalence-is-the-tick-contract.md)–[0006](../../adr/0006-decompose-simulationengine-before-adding-to-it.md) and the vocabulary is [`CONTEXT.md`](../../../CONTEXT.md). It replaces the deleted `tick-loop-unification.md`, which scoped to post-physics only and carried three factual claims that did not hold. Ordered steps, each green on its own, incremental to main:

## Steps

| Step | File | Status |
|---|---|---|
| 1. Oracle first (+ 1b: the missing legs and the two blind-spot fixtures) | [01-oracle.md](./01-oracle.md) | shipped 2026-09-02 |
| 2. `SimulationEngine` decomposition | [02-engine-decomposition.md](./02-engine-decomposition.md) | shipped 2026-09-02 |
| 3. Spine over the whole sim-second, run profile, `host` rename (3a, 3b, 3c-0, 3c) | [03-spine.md](./03-spine.md) | shipped 2026-09-04 |
| 3d. The action router (3d-0 … 3d-6) | [03d-action-router.md](./03d-action-router.md) | 3d-0 … 3d-5b-1 shipped 2026-09-05; 3d-5b-2 built (awaiting commit), 3d-5b-2 next; 3d-6 docs after it |
| 4. Relocate tick-reachable ATC logic into `Yaat.Sim` | [04-relocation.md](./04-relocation.md) | not started |
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
  - [ ] **Pointout auto-ack inverts §5-4-7.a.1.a**: "If the receiving controller takes no action, revert to verbal procedures." Non-response must not become approval. Withdraw the pending point-out on timeout and advise the initiator to coordinate verbally — that also clears the flashing indicator the current timer exists to solve. (En route; §5-4-7.a.1.b makes automated approval terminal-only under a facility directive or LOA)
  - [ ] **Fix the false ERAM-coast citation** at `CrcVisibilityTracker.cs:531` — there is no §5-13-8.3; §5-13-8 governs a *controller manually initiating* a coast track and prescribes no automatic reacquisition. The §5-13-7 citations elsewhere are correct and stay (it says a coast track may not be used for separation, which is exactly what they claim)
  - [ ] Coordination expiry warning fires at 120 s of a 180 s window, so the "about to expire" state is on for two-thirds of its life — move to the last 30-60 s (`TickProcessor.cs:1301`)
  - [ ] Product call: raise the 3 s solo auto-accept floor (`TickProcessor.cs:1342`) to at least the 5 s default so a student sees a handoff sit pending — a training-value judgement, no citation. `HandoffUnacceptedRule` derives its horizon from the same constant
  - [ ] Separate issue, pre-existing: `ApproachEvaluator.ComputeSeparation` (`:164`) measures the trailer's establishment position against the leader's *current* position, including aircraft that have already landed and taxied in — not an in-trail separation at a common instant. Consuming scores on every path spreads the number to more surfaces

## History

- [x] Latent bug found during the review, fixed independently 2026-09-02 (`5ee2f4a0`): `_replayTrackApplier` was documented replay-only but `DispatchAiCommand` uses it on the live path, so a recorded `AS` carrying an AI connection id displaced the live AI's identity. `ResolveEffectiveIdentity` now resolves the AI branch before the shared selected-position map; pinned by `ReplayTrackApplierIdentityIsolationTests`
