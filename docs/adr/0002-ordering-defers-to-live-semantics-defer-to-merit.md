---
status: accepted
date: 2026-09-02
---

# Ordering disagreements defer to live, semantic disagreements defer to merit

Where the paths disagree, something has to win. There are ten adjudications: three ordering
disagreements, three of membership, one of arguments, and three semantic.

The rule splits by kind. For an **ordering** disagreement — two independent drains, neither more
correct than the other — the live server's current order wins, because the tiebreak is arbitrary and
the production-exercised order is the one with evidence behind it. For a **semantic** disagreement —
where one side is a bug — the correct behaviour wins on merit, live behaviour may change, and the
change is a named decision carrying an aviation review where it touches simulated behaviour.

"The production-exercised order wins" applied uniformly, which was the previous design's rule, is an
expedient rather than a principle. Applied to the semantic cases it would enshrine the ASDE-X
wall-clock coast deadline and the discarded approach scores as contract, for no better reason than
that they happen to be the live side.

## The ten

The spine (see [0001](0001-state-equivalence-is-the-tick-contract.md)) is one ordered list, and that
list is the adjudication record — so the adjudications are enumerated here rather than counted. The
scope is the whole sim-second, as 0001 says: the two post-physics lists, and the end-of-second work
after them. Engine means `SimulationEngine.TickPostPhysics`, thirteen steps, plus `TickOneSecond`'s
(empty) tail; live means `TickProcessor.ProcessPostPhysics`, thirty-two, plus `AdvanceLiveSecond`'s
tail. Positions are given as ordinals rather than line numbers, because resolving each of these moves
the lines. The last column records when the spine (tick step 3c) or a step-5 retirement resolved it.

| Kind | Item | Engine | Live | Resolved |
|---|---|---|---|---|
| Ordering | `TickPilotProactive` | 2nd of 13 | 14th of 32 | 3c-0, 2026-09-03 |
| Ordering | `PilotSpeech` / `Readbacks` drained in the opposite order | readbacks, then speech | speech, then readbacks | 3c-0, 2026-09-03 |
| Ordering | `DrainAllStripDispatches` | 8th, right after the warning drain | 27th, immediately before `AutoDelete` | 3c-0, 2026-09-03 |
| Membership | `TickAutoDelete` — the only step that removes aircraft | never called | `Post.AutoDelete` | sim step on every path, 2026-09-04 |
| Membership | `TickSoloTrainingEvaluation` | never called | `Post.SoloTrainingEvaluation` | sim step on every path, 2026-09-04 |
| Membership | position-history sampling (end of second) | never | every 5 s in `AdvanceLiveSecond` | sim step on every path, 2026-09-04 |
| Arguments | `TickConflictAlerts` internal airports | `[]` — no approach-corridor suppression, so it over-alerts | the room's real set, from the server's STARS config | 3c-0, 2026-09-03 |
| Semantic | ASDE-X / SAID coast deadline | — | `DateTime.UtcNow` rather than sim-time | open (step 4, with coast as sim core) |
| Semantic | `DrainAllApproachScores` | drained and discarded | consumed | open (the host consumes; the evaluator moves in step 4) |
| Semantic | weather-timeline advance (end of second) | never | gated on `HasMeaningfulChange` (1° / 0.5 kt); replay and reconstruction ungated | ungated on every path, 2026-09-04 |

Every other step the two lists share — `LiveTrafficRunwayUse`, `Transponders`, `VisualDetection`,
`ConflictAlerts`, `EramConflictAlerts`, `Warnings`, `Notifications`, `Transmissions`, `ApproachScores`
— holds the same order relative to the others on both paths. The live indices are larger only because
around twenty host-only steps are interspersed, which is membership rather than disagreement.

**This said eight until 2026-09-04**, because it was scoped to the two post-physics lists even though
0001 had already widened the contract to the whole sim-second. The oracle's end-of-second entries —
position history on every fixture, weather on the weather fixture — were adjudications this record did
not carry, so it was silently short by two while claiming to be the record. Widened when the spine
landed and the first retirements made the gap visible.

**This said nine, and four ordering, until 2026-09-03.** The count came from the design note this ADR
distilled (`git show 6f3a1007^:docs/plans/tick-loop-unification.md`), whose ordering axis listed four
items with the fourth marked in its own text as belonging to the membership axis — "*`AutoDelete` /
`SoloTrainingEvaluation` membership … (This is axis 1, listed here because the fix has to pick a
position for them.)*". Distilling the list into a tally counted those two in both buckets. Three
independent re-derivations from current source agree on three: a pairwise inversion count over the
thirteen shared steps decomposes into exactly three relocations, and the longest increasing
subsequence of the same permutation has length ten, so exactly three elements are out of place.
`Transponders` moving from 3rd to 2nd is not a fourth — it is the slot `PilotProactive` vacates.

## Consequences

- ASDE-X and SAID coast move from `DateTime.UtcNow` to sim-time, matching ERAM coast, which is
  already sim-timed in the same file. This is a deliberate live behaviour change. A coast interval is
  a surveillance quantity — it means "this many scans without a return" — so wall-clock decouples it
  from the picture it describes: at the soak runner's ~420× a 45-second coast lingers for over five
  simulated hours, and a paused room expires one having simulated nothing. A coasted track sitting on
  a runway is something a tower controller reads and acts on (7110.65 §3-6-3.c, which binds tower
  controllers for both IFR and VFR), so this is a fidelity fix, not only a determinism one.
- **The interval stays 45 seconds and stays wall-clock on the wire.** `AsdexTrackDto.CoastTimeout` is
  a `DateTime?` pinned by `CrcWireContractTests`, and CRC counts down against that absolute timestamp
  locally — so the value is held in sim-seconds and projected at the boundary, exactly as
  `CoordinationItem.ExpireTime` already is via `DtoConverter.SimTimeToWallClock`. Emitting
  `UtcNow + 45` while expiring on sim-time would desync CRC's visible countdown from the actual
  delete at any rate other than 1×.
- The engine and replay paths absorb the three ordering moves.
- **The wind physics sees applies every second, ungated.** The live gate (`HasMeaningfulChange`, 1° /
  0.5 kt) was added in yaat-server `80e7418f` to rate-limit a weather broadcast; that broadcast has
  since moved to the reported-METAR issuer, which has its own SPECI criteria, so by 2026-09 the gate
  only quantised the wind that reached physics — a sub-threshold ramp that live froze, replay applied.
  A continuous field applied continuously is the faithful one and the one every run kind can share;
  the reported METAR is unaffected. This is a deliberate live behaviour change: under a slow ramp the
  live aircraft's track now drifts smoothly instead of in steps.
- Recordings that desync as a result are triaged individually — see
  [0004](0004-the-oracle-and-the-corpus.md).
