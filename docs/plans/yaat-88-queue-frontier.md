# YAAT-88: per-dispatch queue frontier (design)

Line numbers in this design are against the uncommitted X:/dev/yaat.wt/yaat-2806/yaat tree (the YAAT-2806 work), not against main.

I agree with keying on `TransmissionId` and with "a chain's first unfinished block is its frontier". I disagree on three points. "Unfinished" needs a stored per-block flag. More sites need changing than the three the issue names. And `CurrentBlockIndex` readers are not the main risk. I'd split the work into two briefs: a pure refactor first, then the behaviour change. Line numbers below are against the uncommitted `X:\dev\yaat.wt\yaat-2806\yaat` tree.

I ran no tests (read-only); every red/green claim is traced from the code or the YAAT-2806 measurements, unless marked as asserted by you.

## Where I disagree with your position

1. **"First unfinished block" needs a persisted flag.** It has to mean "the queue has walked past it" (`ReadyToAdvance` seen at the frontier). It can't mean "not applied" or "not all complete":
   - Regimes B and C apply blocks without moving the cursor (`AdvanceQueueWhileIdle` never advances it).
   - A lateral block is passed while its altitude still runs.
   - Deriving "passed" from the latched completion flags moves some advances a tick earlier, which changes single-chain behaviour.

   So it needs a per-block `IsPassed` flag, snapshotted.
2. **More sites than three.** The issue names three. Beyond them, these hold or reach across dispatches today:
   - `NotifyPhaseAdvanced` returns at the first triggered head of any dispatch (`FlightPhysics.cs:6362-6365`).
   - The Notify fix and ground loops' `pendingWaitAhead` is queue-wide (`:6172`, `:6238`).
   - `ClearConflictingBlocks` marks superseded commands only on the single cursor block (`CommandDispatcher.cs:4747-4757`).
   - `ConditionalList` shows only the cursor block as `[Active]`.
   - The overlay's `HoldQueuedAt` copies the global WAIT hold-back.
   - `DiscardChainRemainder` keys on the wrong identity.
3. **Only regime A has a frontier step.** Regimes B and C scan every unapplied block, keeping a blocked/holding state per chain. Regime A needs two passes so a WAIT is counted once.
4. **`CurrentBlockIndex` readers are not the main risk.** There are six production readers. The client and yaat-server never read the cursor; the client gets text, and `ScopeQueueBadge.PendingLines` already filters every `[Active]` line. The real risks are:
   - cursor index bookkeeping spread over seven mutation sites;
   - the supersede semantics in `ClearConflictingBlocks`;
   - the per-tick allocation baseline (`TickPerformanceRegressionTests.TickAllocations_MeanBytesPerSimSecond_StayWithinTenPercentOfBaseline`);
   - follow-on blocks still carrying id 0;
   - the first-block-failure path that wipes the whole queue.

## 1. Dispatch identity

**Key: `CommandBlock.TransmissionId`.** `SourceCommandText` (the doc's ruling) fails for two reasons:
- A JRADO or DEPART follow-on carries its own text (`SourceCommandText = DescribeCommand(cmd)`, `NavigationCommandHandler.cs` near 86 and 239). It has to, because rehydration rebuilds it from that one-command text. Keyed by text, it would leave its chain and re-break YAAT-2806.
- Two transmissions with identical text would merge into one chain.

Every dispatch draws a fresh id at `CommandDispatcher.cs:282`. Presets dispatch one line at a time (`SimulationEngine.Presets.cs:696`), so ArWhy's `CM 7000`, chain and `AT 5000 APT SJC` are three chains. A refused transmission hands its id back (`:287`, `:306`), but none leaves blocks behind: the only failure exit after enqueueing is `:788-793`, which empties the queue.

How each block kind carries the id:

| Block source | Id carried? |
|---|---|
| `CreateBlock` | `ctx.TransmissionId` (`CommandDispatcher.cs:5026`) |
| Split block | Yes, copied (`:5277`); `IsPassed` must be copied too (see brief A) |
| Preserved block from `SupersedeForTransmission` / `ClearConflictingBlocks` | Same objects or their splits, so they keep their own id; they do not take the superseding one |
| Rehydrated block | Restored object kept in place (`CommandQueue.cs:430`); `CopyRebuiltNavigationBlock` copies only the runtime parts (`:5183-5188`) |
| JRADO / DEPART follow-on | Parent's id via `Queue.Applying` (2806 change) |
| **JRADI / JAWY follow-on** | **No.** Still `Blocks.Add`, id 0 (`NavigationCommandHandler.cs:102`, `:1425`; factories at `:111`, `:1450`; rehydrate call at `CommandDispatcher.cs:5168`) |
| Deferred WAIT / reaction-delay payload | Re-dispatched through `DispatchCompound` (`SimulationEngine.DeferredCommands.cs:204`), so it gets a fresh id. Correct: the whole payload is one new chain. A mid-chain `WAIT` is an in-queue wait block of the same chain. |

**Id 0** means a block from a snapshot that predates transmission ids, a hand-built test block, or an unstamped follow-on. Advancement treats all id-0 blocks as one chain, which is today's single cursor exactly. That keeps hand-built test queues and old restored queues behaving as they do now. Unstamped follow-ons would then form their own chain and fire mid-chain, so **JRADI/JAWY must at least be stamped** with `Applying?.TransmissionId`.

Abort (`DiscardChainRemainder`, `CommandQueue.cs:513`) switches to the same id. For id 0 it also requires the same `SourceCommandText`, which keeps today's abort behaviour for old and hand-built blocks.

**Snapshots** already persist `CommandBlockDto.TransmissionId` (`CommandQueueDto.cs:62`) and `LastTransmissionId`, both additive with no bump. The new state needs a decision:
- **Option 1 (recommended):** add `CommandBlockDto.IsPassed` (written only when true). Keep `CommandQueueDto.CurrentBlockIndex`, written as "first block not passed". Restore with `IsPassed = dto.IsPassed || index < dto.CurrentBlockIndex`. Both old and new writers satisfy "every block before it is passed", so the field is not repurposed, and the doc's rule for adding a field (`docs/snapshots-and-replay.md`) allows no schema bump.

  Snapshot 0 has no passed blocks, so the hash yaat-server pins (`X:\dev\yaat-server\tests\Yaat.Server.Tests\Simulation\ScenarioLoadPrepareCommitTests.cs:24`) is unchanged.
- **Option 2:** V56→V57, which moves `CurrentBlockIndex` onto the blocks as `IsPassed` and drops the field (the `LegacyRadial` pattern). This is the cleaner DTO, but it changes the snapshot-0 hash (server pin) and the JSON literals in `SnapshotRoundTripTests.cs:600` and `:1117`.

## 2. Actors and invariants

Terms (glossary entries needed in `docs/CONTEXT.md`):
- **Chain**: the blocks one transmission queued, in queue order.
- **Passed**: regime A found the block `ReadyToAdvance` at its chain's frontier.
- **Frontier**: a chain's first block that is not passed.

| # | Invariant |
|---|---|
| I0 | Within a chain, queue order stays transmission order, with a follow-on directly behind its parent. Every mutation keeps it: `EnqueueBlocks` appends, `InsertFollowUp`, `ClearConflictingBlocks` re-insertion, the split's in-place replacement. |
| I1 | **Each chain advances by exactly today's single-cursor rules, as if its blocks were alone in the queue.** That includes the regime-specific quirks: in regime A an unmet triggered frontier holds later triggered blocks (no lookahead, `:4732-4738`), while a running frontier lets them fire out of order. A queue holding one chain therefore behaves exactly as today. |
| I2 | A chain's progress depends only on its own blocks and the aircraft, never on another chain's unfinished block, whether unapplied (unmet trigger, held for release, phase-rejected, counting WAIT) or applied and still running (the airborne `CM 7000` case). Deliberately shared state remains: the aircraft's phase, completion checks that read the aircraft's global targets (another chain's vector can complete a heading, as today), supersede, the `OnHandoff` latch, `LatchSiblingFollowCrossingTriggers`, and a Failed apply ending the tick's scan. |
| I3 | Fixed visit order: regime A runs frontier steps in frontier order, then one triggered-block scan in queue order. Regimes B and C scan in queue order. Per-chain state is looked up by key and never enumerated. |
| I4 | `IsPassed` is set only by regime A's frontier step and never cleared (tracked `IsComplete` is never reset in Sim; checked with `rg`). `CurrentBlockIndex` becomes read-only: the index of the first unpassed block, never "the current block". `CurrentBlock` goes away. |
| I5 | The phase still gates what it gates today: the regime is chosen once per tick from `Phases.CurrentPhase`. A block that installs a phase during regime A ends regime-A work for that tick. Regime C ends its scan when the aircraft leaves the idle phase. |
| I6 | Each block is visited at most once per `UpdateCommandQueue` call: one `IsTriggerMet`, one `CheckWaitComplete`, one `TrackFrdMiss`. The triggered scan skips each chain's frontier block, so a counting WAIT is never counted twice. Notify paths latch and never count. |
| I7 | Same seed gives byte-identical results: `IsPassed`, `TransmissionId` and `LastTransmissionId` are all snapshotted. |
| I8 | A fire-time failure discards only later unapplied blocks of its own chain. |
| I9 | No heap allocation per scan. Scan state is per call (a stackalloc'd key/state span), never shared, because a block's apply can re-enter and change the queue (`InsertFollowUp`, `FlightCommandHandler.cs:2411` `Blocks.Clear()`). |

## 3. Sites that must obey them

| Site (2806 tree) | Today | Change |
|---|---|---|
| `src/Yaat.Sim/FlightPhysics.cs:4697-4759` regime A | One cursor; `++` at 4710; lookahead 4724; WAIT latch 4757 | Frontier step per chain (I1–I6) |
| `FlightPhysics.cs:4811-4877` `ApplyReadyConditionalBlocks` | `break` at the first untriggered block (4830); `holdApplies` queue-wide (4854) | Blocked/holding per chain; `continue` past other chains |
| `FlightPhysics.cs:4890-4965` `AdvanceQueueWhileIdle`, `:4976-5005` `TryApplyIdleUntriggeredBlock` | `frontierBroken`/`holdApplies` queue-wide; returns `bool`, then `break` | Per chain; the bool becomes an outcome (below) |
| `FlightPhysics.cs:4685` `triggerStart`, `:6352` | Cursor-relative start | Scan from 0, skipping applied blocks (equivalent) |
| `FlightPhysics.cs:4768-4804` `LatchFollowingTriggers` | Separate scan | Subsumed by the "holding" seed; delete |
| `FlightPhysics.cs:6316-6409` `NotifyPhaseAdvanced` | First unapplied block after the cursor; `return` at a triggered head or rejection | Each chain's first unapplied block; `continue` past ineligible chains; still fires at most one block |
| `FlightPhysics.cs:6164-6214`, `:6225-6291` Notify fix / ground | `pendingWaitAhead` queue-wide | Per chain (keep firing regardless of `;` order, as today) |
| `FlightPhysics.cs:5706-5743` | Cursor fix-ups at 5722-5725, 5739-5742 | Delete |
| `FlightPhysics.cs:5450` `CompleteGroundedAltitudeCommands` (2806) | Not cursor-dependent | Keep: same-chain `CM 7000; DCT X; FH 090` still needs it |
| `src/Yaat.Sim/Commands/CommandDispatcher.cs:4711-4799` `ClearConflictingBlocks` | Marks only `CurrentBlock` superseded; partitions from `pendingStart` | Leave passed blocks in place; leave **each chain's applied frontier** in place with conflicting commands marked complete; partition the rest. Marking every chain's frontier is required: if a later chain's `FH 270, CM 100` frontier were partitioned, it would split and re-queue as a no-lateral block that holds its chain until the climb finishes. |
| `CommandDispatcher.cs:4730`, `:4739`, `:792` | Cursor resets | Delete; `fastStart` reports every unapplied block |
| `CommandDispatcher.cs:2116-2139` `RemoveQueuedDeleteBlocks` | Fix-up at 2131-2137 | Delete the fix-up |
| `CommandDispatcher.cs:788-793` | First-block failure `Blocks.Clear()` also wipes other chains' survivors | Open decision (below) |
| `CommandDispatcher.cs:5226-5300` split | Copies runtime state | Also copy `IsPassed`; add it to the property map in `tests/Yaat.Sim.Tests/Commands/SplitBlockPreservationTests.cs` (~:54-75) |
| `src/Yaat.Sim/CommandQueue.cs:466-469`, `:472-476`, `:513`, `:529-532`, `:541`, `:547` | Cursor, index fix-ups, text key | Derived index, delete fix-ups, chain key, DTO rule |
| `src/Yaat.Sim/Commands/ConditionalList.cs:57`, `:74` | Starts at the cursor; one `[Active]` | Skip passed blocks; each chain's applied, unfinished frontier is `[Active]` (number 0) |
| `src/Yaat.Sim/NavRouteOverlayProjector.cs:126-151` `HoldQueuedAt` | Copies the queue-wide WAIT hold-back | Per chain |
| `src/Yaat.Sim/Simulation/SimulationEngine.Tick.cs:218-221` | Odd clamp to `Count-1` | Delete |
| `SimulationEngine.DeferredCommands.cs:300` | Abort | Gets the new key through `DiscardChainRemainder` |
| `src/Yaat.Sim/Commands/NavigationCommandHandler.cs:102`, `:1425` | id 0 | Stamp the id |

No change needed (order-independent): `PatternCommandHandler.cs:2712`/`:2818`, `NavRouteOverlayProjector.cs:384`/`:407`, `PilotProactive.cs:730`, `FlightPhysics.cs:5588`, `SimulationEngine.Tick.cs:141`.

On the server side: `X:\dev\yaat-server\src\Yaat.Server\Simulation\DtoConverter.cs:1851` does not depend on block order, and `:2140` renders `ConditionalList` text. On the client side: `ScopeQueueBadge.cs` and `RpoQueueRows.cs:32` filter every `[Active]` line; `WaitUntilHandler.cs:552` checks for an empty queue.

## 4. Design: two briefs, A then B

**Brief A. A per-block flag replaces the cursor; no behaviour change.**
- Add `public bool IsPassed { get; set; }` to `CommandBlock`.
- `CommandQueue.CurrentBlockIndex` becomes read-only (first `!IsPassed`, or `Count`). Consider renaming it `FirstUnpassedIndex` in memory while keeping the DTO name. `IsComplete => Blocks.TrueForAll(b => b.IsPassed)`.
- `FlightPhysics.cs:4710` `++` becomes `block.IsPassed = true`.
- Delete every index fix-up listed above, and the setter writes in `tests/Yaat.Sim.Tests/SpeedCommandTests.cs:878` and `:896`.
- DTO per Option 1; split copies the flag.

Verification: the whole suite and the recording replays stay green with no change. A single cursor is the same thing as "first unpassed block" while only regime A passes blocks.

**Brief B. Per-chain frontier.**

```csharp
// CommandQueue
internal CommandBlock? FrontierOf(int transmissionId);
internal bool IsFrontier(CommandBlock block);
internal int FrontierKeys(Span<int> keys);   // distinct ids of unpassed blocks, in frontier order
// FlightPhysics
private enum ChainState { Open, Holding, Blocked }
private ref struct ChainScan { ChainState Get(int key); void Set(int key, ChainState s, CommandBlock? skipThrough); }
private enum FrontierStep { Done, Lookahead, Latch, StopTick }
private static FrontierStep StepChainFrontier(AircraftState ac, CommandQueue q, int key, double dt, Func<string, AircraftState?>? lookup);
private static void ScanTriggeredBlocks(AircraftState ac, CommandQueue q, ref ChainScan scan, double dt, Func<string, AircraftState?>? lookup); // was ApplyReadyConditionalBlocks
private enum IdleApply { Applied, ChainWaits, StopScan }   // TryApplyIdleUntriggeredBlock's new return
```

- **Regime A.** `StepChainFrontier` is today's `:4697-4759` body for one chain. Its result seeds the scan state: `Lookahead` opens the chain, `Latch` sets it to holding, `Done` blocks it, and `skipThrough` is the frontier block. Stop when the result is `StopTick` or a phase is now active. Then run one `ScanTriggeredBlocks` pass.
- **Regime B.** One `ScanTriggeredBlocks` pass with every chain open.
- **Regime C.** Per-chain blocked/holding state. `IdleApply.StopScan` covers leaving the idle phase and a Failed apply. `ChainWaits` covers phase rejection, missing parsed commands, a counting WAIT and a block held for release.
- The rest is in the sites table: `NotifyPhaseAdvanced`, Notify `pendingWaitAhead`, `HoldQueuedAt`, `ClearConflictingBlocks`, `ConditionalList`, the `DiscardChainRemainder` key, and JRADI/JAWY id stamping.
- Keep preserved blocks ahead of the new compound (`:708`, `:773`). The ordering is no longer load-bearing, but the order asserts at `QueuedCommandDimensionTests.cs:684` and `:869` stay valid; update their rationale comments.
- **Docs:** `docs/command-chaining.md` lines 16, 29-33 and 196; say "unfinished" rather than "unfired", since the airborne case is an applied predecessor (the 2806 report's doc-drift item). Also the `CONTEXT.md` terms, the `docs/architecture.md` Task Index row the 2806 report asked for, `USER_GUIDE.md`, and a changelog entry.
- **Gates:** brief B changes automatic aircraft behaviour, so it goes through `aviation-review-gate`. It runs `tools/test-all.ps1`; the server needs no code change under Option 1.

## 5. Case table

Today's state is traced from the code unless noted.

| Test (file) | Input | Expected | Today |
|---|---|---|---|
| `QueuedCommandDimensionTests.ChainedTakeoffClearance_LetsTheSurvivingClimbFireAheadOfTheUntriggeredChainMate` (existing) | `AT 1000 CM 5000`, then `CTO; SQ 1234` | climb fires, levels at 4,900-5,100 ft | green, must stay green |
| NEW `ConditionalAfterChainedTakeoff_FiresDuringClimbOut` (same fixture) | `CTO; SQ 1234`, **then** `AT 1000 CM 5000` | climb fires at 1,000 ft; SQ applies after the phases end | red: `:4830` stops at SQ during the departure phases |
| NEW `ChainedSquawk_NotHeldBehindUnmetSurvivor` (`IdlePhaseQueueAdvanceTests` harness) | parked, `PUSH`, then `AT 5000 CM 7000`, then `TAXI B3 HS B; SQ 1234` | squawk 1234 shortly after holding short; AT 5000 still unapplied | red: `frontierBroken` `:4919`/`:4955` |
| NEW `TaxiThenCto_FiresAtHoldShort_WithUnmetClimbAhead` (`TaxiCtoSequentialNotFiringTests` fixture) | `AT 5000 CM 7000`, then `TAXI …; CTO` | CTO fires at the hold-short | red: `:6362-6365` and `frontierBroken` |
| NEW `SeparateClimb_DoesNotHoldLaterChain_Airborne` | 3,000 ft, no phase (`AircraftSouthwestOfSns`); `CM 7000`, then `FH 329; DCT SHOEY` | SHOEY on the route below 5,000 ft | red: cursor parked on CM 7000 (`CommandQueue.cs:218`, `:4830`) |
| NEW `SeparateClimb_DoesNotHoldJradoChain` | `CM 7000`, then `FH 329; JRADO SNS264; DCT SHOEY` | JRADO applied below 4,000 ft; DCT SHOEY never before the intercept | red (debugger measured t=193 at 7,000 ft) |
| NEW `TriggeredPreset_FiresWhileChainWaitsOnIntercept` | `CM 7000`; `JRADO SNS264; DCT SHOEY`; `AT 5000 SPD 210` | SPD 210 assigned at 5,000 ft; SHOEY only after the intercept | red: lookahead breaks at DCT |
| T2 `Issue1401PresetChainAfterLvHeadingTests.PresetChainAfterLvHeading_RunsWithoutWaitingForTheSeparatePresetClimb` | ArWhy recording | `aptAt` not null, `jradoAt <= 230`. Add: `shoeyAt is null` or the intercept came first | red on `aptAt` per your report; my trace has the cursor parked on the unmet intercept (`:4732-4738`) |
| T1 `GroundedAltitudeCommand_InAGroundPhase…`, T3 `Jrado_ChainedNextBlock…`, T4 `Jrado_First…`, T5 `Depart_ChainedDirect…` | as in the tree (I'm assuming this T1-T5 numbering) | stay green (single chain under I1) | green |
| NEW `ConditionalList_TwoRunningChains_TwoActiveEntries` | `CM 7000`; `FH 329; DCT SHOEY` | two `[Active]` lines, then `[1] DCT`; DELAT 1 keeps both active blocks | red: FH listed as numbered and deletable |
| NEW `AtFixWait_InOneChain_DoesNotHoldOtherChainAtSameFix` | `AT ABC WAIT 30 SPD 210`; `AT ABC CM 090` | CM 090 at ABC; speed 30 s later | red: queue-wide `pendingWaitAhead`/`holdApplies` |
| NEW `ChainAbort_SameTextTwoTransmissions` | the same conditional compound sent twice; reuse the abort matrix's failing block | only the failed transmission's remainder is discarded | red: text key `CommandQueue.cs:513` |
| NEW guard `WaitCountdown_TwoChains_CountedOnce` | `CM 7000`; `AT 4000 WAIT 20 FH 090` | FH 090 about 20 s after 4,000 ft | green; catches a double count |
| NEW (A) `PassedFlags_RoundTrip_LegacyCursorRestoresPrefix` (`SnapshotRoundTripTests`) | two chains; old JSON with `CurrentBlockIndex: 2` | flags survive; blocks 0-1 restore as passed | new |
| NEW determinism `RestoreMidChain_ReproducesFrontiers` (`NavigationQueuedBlockRestoreTests`) | snapshot mid-JRADO case | same ticks for SHOEY and SPD after restore | new |

**At-risk test classes:**

- `CompoundConditionalLookaheadTests`
- `IdlePhaseQueueAdvanceTests` (`UntriggeredBlock_BehindUnmetTrigger_DoesNotLeapfrog` is single-chain and must stay green)
- `AtFixLookaheadTests`
- `AtAltitudeConditionTests:202`
- `CommandQueueTests:430`/`:552` (still valid with a derived index)
- `ChainAbortAndCompletionTests`
- `Issue286WaitDescentTests`
- `ConditionWaitDelayTests` (hand-built id-0 blocks form one chain)
- `SplitBlockPreservationTests`
- `QueuedCommandDimensionTests`
- `ConditionalListTests`
- `NavRouteOverlayProjectorTests`
- `TickPerformanceRegressionTests`
- recording replays where two chains coexist

A change there is the intended behaviour change and needs re-baselining, not reverting.

## Open decisions for the owner

1. JRADI and JAWY: stamp the id only (recommended, preserves today's behaviour), or also use `InsertFollowUp` so a `;` waits for the intercept. The second is the YAAT-2806 aviation question.
2. `CommandDispatcher.cs:788-793`: should a failed first block remove only its own transmission's blocks? Today it also wipes other chains' survivors. That's a behaviour change.
3. Snapshot Option 1 (no bump) or Option 2 (V57 plus updating the server's snapshot-0 hash).

**Pre-existing issues to book as adds:**
- The Notify fix/ground paths fire blocks regardless of `;` order inside a chain.
- Regime A treats an unmet triggered frontier and a running frontier differently, and regime B differs again; I1 keeps this as is.
- The `OnHandoff` latch is used up by the first block that checks it (`:4741-4744`, `:4845-4848`).

**Trade-offs.** Option 1 keeps a derived field in the DTO in exchange for no server churn. The two-pass regime A changes the cross-chain apply order within one tick from strict queue order to frontier steps first; it is still deterministic. Keeping legacy blocks as one chain means a pre-transmission-id restored queue keeps the old coupling.
