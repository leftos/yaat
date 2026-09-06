# Command Chaining — the completion & advancement contract

How a chained compound (`CM 050; FH 220`) actually progresses: what "the current command is done"
means per command category, which of the three advancement regimes applies, and what happens when a
block fails after it was accepted. Read this before changing `FlightPhysics.UpdateCommandQueue`,
`CommandBlock` completion, or any fire-time failure handling. Companion docs:
[command-pipeline.md](command-pipeline.md) (input → queue), [command-handlers.md](command-handlers.md)
(dispatcher internals), [phases.md](phases.md) (phase/queue interaction).

## The three advancement regimes

`FlightPhysics.UpdateCommandQueue` (tick step 9) picks one of three behaviors from
`aircraft.Phases?.CurrentPhase`:

- **A — free-flying (no phase).** The current applied block's per-command completion predicates run
  (`UpdateBlockCompletion`), and `CommandBlock.ReadyToAdvance` gates `CurrentBlockIndex++`. A block
  containing a lateral command (Heading/Navigation) advances on the lateral alone — altitude/speed
  siblings run concurrently and never block the next instruction; a block with no lateral command
  requires all its commands complete. While the current block runs, `ApplyReadyConditionalBlocks`
  scans the contiguous *triggered* run behind it so `AT`/`LV`/`ATFN`/… can fire early.
- **B — active phase.** The phase owns `ControlTargets`; completion tracking does not run. Only
  *triggered* blocks fire (per-tick scan plus the event paths `NotifyFixSequenced`,
  `NotifyGroundEntityReached`, and the `NotifyPhaseAdvanced` head-block hook). Untriggered
  `;`-blocks wait for the phase to end.
- **C — idle phase** (`Phase.IsIdleAwaitingCommands`: AtParking, HoldingAfterPushback,
  HoldingAfterExit, HoldingInPosition, HoldingShort, LinedUpAndWaiting). These phases never complete
  on their own, so `AdvanceQueueWhileIdle` additionally applies *untriggered* blocks in strict `;`
  order (issue #407). A block the idle phase rejects stays queued.

## Completion per command category

Classification happens once, at block build time: `CommandDescriber.ClassifyCommand` maps each
parsed command to a `TrackedCommandType`. `ClassifyCommandCompletenessTests` pins every type that
classifies `Immediate` to a curated allowlist — a new command silently falling into the
`_ => Immediate` fallback fails the test.

| TrackedCommandType | Complete when | Notes |
|---|---|---|
| `Heading` | `NavigationRoute` empty and heading within 0.5° of target | `IsHeadingReached` |
| `Altitude` | `Targets.TargetAltitude` nulled (within 10 ft) **or the aircraft is on the ground** | on-ground: `UpdateAltitude` early-returns, so a pre-issued climb counts as accepted-and-pending — the chain advances while the target stays armed for departure (the standard pre-departure "maintain" clearance, 7110.65 §4-3-2.e / AIM §4-4-10.7). Speed deliberately gets **no** such escape: `UpdateSpeed` runs on the ground and genuinely converges, so its target nulls normally — the asymmetry is intentional |
| `Speed` | `Targets.TargetSpeed` nulled (within 2 kt) | |
| `Navigation` | `Targets.NavigationRoute` fully consumed | 0.5 nm arrival / turn anticipation |
| `Immediate` | instantly on apply | one-shot commands, and **every phase-installing command** — see below |
| `Wait` | countdown elapsed (`WaitRemainingSeconds` / `WaitRemainingDistanceNm`) | |

**Phase-installing commands** (pattern entries, approaches, taxi, takeoff/landing, holds, military
routes, …) classify `Immediate`, and that is irrelevant to chaining: the moment their handler
installs a `PhaseList`, regime B/C takes over and anything queued behind them is governed by the
*phase's* lifetime, not by a completion predicate.

**Indefinite-hold installers.** Untriggered blocks chained behind a command that installs an
open-ended phase never fire until further clearance ends it, so dispatch warns at issue time
(`CommandDispatcher.WarnIndefiniteHoldChain`, predicate
`CommandDescriber.InstallsIndefiniteHoldPhase`, pinned by `IndefiniteHoldMarkerTests`). The chain
still queues — "after the hold, do X" is preserved — and *triggered* tails still fire mid-phase.
The warning set is per-**command**, not per-phase, because several of these phases have exits:

- **Warned:** `HOLDP` (HoldingPatternPhase with `MaxCircuits = null` — the self-completing
  `MaxCircuits = 1` HILPT is installed by CAPP/JAPP, not by HOLDP), the HPP*/HFIX* VFR holds
  (VfrHoldPhase never self-completes), and `FOLLOW` (VfrFollowPhase exits only on
  lead-landed/lead-lost — the warning is conservative but correct for continued following).
- **Not warned:** `FOLLOWG` — FollowingPhase self-completes on *both* of its exits (runway
  hold-short reached, lead departed) into an `IsIdleAwaitingCommands` phase where chained blocks
  fire, so `FOLLOWG X; CROSS 28R` is a working idiom (its designed terminus, even); and `CAR` —
  only the refueling *anchor* case (AerialRefuelingAnchorPhase) is open-ended, and anchor-vs-track
  can't be resolved at dispatch classification time.

A real hold ends with further clearance, not by itself (7110.65 §4-6-1.c requires an EFC when
delay is expected; §4-6-2.a requires the clearance beyond the fix) — which is why pre-queuing an
untriggered tail behind a hold deserves a warning: it is not an EFC, and no controller pre-issues
the onward clearance to fire at an unspecified future moment.

## Abort on fire-time failure

`DryRunValidate` runs only the first, unconditioned block before a compound is accepted. Every
later block is validated by its own handler at fire time, and **a fire-time failure aborts the
remainder of that compound's chain** — the follow-on instructions were premised on the failed one,
and the FAA model for a partially-invalidated clearance is re-issuance, not fragment survival
(7110.65 §4-2-5.b "restate all applicable altitude restrictions"; AIM §4-4-10.7 — omission from an
amended clearance cancels). `CommandQueue.DiscardChainRemainder` removes every later,
not-yet-applied block with the same `SourceCommandText` (the per-dispatch grouping key — blocks
queued by *other* dispatches survive), marks the failed block's commands complete so the queue
advances past it, and one warning names both the failure and the discarded commands.

Ownership: `FlightPhysics.ApplyBlock` owns the abort for every apply path (all three regimes, both
Notify event paths, `NotifyPhaseAdvanced`); `SimulationEngine.ProcessTriggeredTrackBlocks` invokes
the same discard when a triggered **track** command fails at `TrackEngine.Dispatch`
(`AT FIX HO 2B; FH 090` must not fly the heading after a failed handoff). Callers of a failed apply
**stop scanning the queue for the rest of the tick** — the discard mutated the block list, and
latched triggers make the re-scan next sub-tick lossless. Per-path regression matrix:
`ChainAbortAndCompletionTests`. Note that a *typed* compound containing a track verb never reaches
`DispatchCompound` whole: the action router (and the live server before it) splits it with
`CompoundPolicy.TrySplitSpecialCompound` into units that dispatch independently, so `DCT OAK; AT OAK
HO ZZ9; SQ` applies the `SQ` at issue time. The path that hands a triggered track verb its chain-mates
is a scenario preset (`DispatchPresetCommands` → `DispatchCompound` directly), which is the path the
track-failure test drives.

Parallel (`,`) semantics: `BuildApplyAction` short-circuits at the first failing sibling — earlier
siblings stay applied, the block as a whole counts failed, and the chain remainder is discarded.
Triggered and sequential blocks get identical abort treatment. Partial application matches how a
partially-unable clearance plays out: the pilot flies what they accepted and says "unable" to the
specific item (AIM §4-4-7.3 accept/refuse; 7110.65 §2-1-18.c UNABLE) — a turn already begun is not
un-flown because a later element of the same transmission was unworkable.

## Non-compoundable commands

A genuinely multi-command compound containing a command from `CompoundPolicy.IsNonCompoundable`
(PAUSE, spawn, flight-plan ops, room-wide/global commands) is rejected up front on both sides —
server (`RoomEngine.SendCommandAsync`) and client (`MainViewModel`, same shared predicate) — with
*"{verb} cannot be part of a chained command"*. `DEL` and `DEST`/`APT` are deliberately excluded:
they chain correctly through the queue (`CROSS 28R; DEL` #311, `AT 5000 APT OAK`). A line the
single-command parser accepts whole (free-text `NOTE …`) is not a chain.

## Historical failure classes (what regressions look like)

1. **Single-path fixes** (#294): `UpdateCommandQueue` has parallel scan paths (current-block,
   lookahead, idle, two Notify events) — a fix applied to one must be checked against the others.
   The abort design counters this by living inside `ApplyBlock` itself.
2. **Client canonicalizer drift** (#335): server-side condition/WAIT/compound-policy changes need
   the client mirror updated in lockstep — bugs otherwise reproduce only on the typed path. Shared
   predicates (`CompoundPolicy`) remove the drift by construction.
3. **Dimension-table drift** (#296/#336): `GetCommandDimension` and `GetQueuedCommandDimension`
   are parallel classification tables; adding a command to only one mis-clears or mis-preserves
   queued chains.
4. **Split-block metadata loss** (#281): `SplitBlockNonConflicting` rebuilds a partially-superseded
   block; every `CommandBlock` field must be re-derived or copied. `SplitBlockPreservationTests`
   pins the full property set by reflection.
5. **Idle-phase stranding** (#407/#311): a new "waiting for a command" ground phase must override
   `IsIdleAwaitingCommands` or untriggered chains behind it strand forever.
6. **One TRACK table** (#199, CASUP): every run kind — the live room included — dispatches a track
   verb through `TrackEngine.Dispatch` via the router's `Track` arm; add a verb there and nowhere
   else (the live/replay divergences of #199 and CASUP came from a second, server-side table).
7. **Restore rehydration**: a queued block's `ApplyAction` is a closure; snapshot restore rebuilds
   it from `SourceCommandText` (`RehydrateRestoredQueueBlocks`) — without it a restored block fires
   as a silent no-op.
