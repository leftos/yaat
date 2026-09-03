---
status: accepted
date: 2026-09-02
---

# Ordering disagreements defer to live, semantic disagreements defer to merit

Where the paths disagree, something has to win. There are eight adjudications: three ordering
disagreements, two of membership, one of arguments, and two semantic.

The rule splits by kind. For an **ordering** disagreement — two independent drains, neither more
correct than the other — the live server's current order wins, because the tiebreak is arbitrary and
the production-exercised order is the one with evidence behind it. For a **semantic** disagreement —
where one side is a bug — the correct behaviour wins on merit, live behaviour may change, and the
change is a named decision carrying an aviation review where it touches simulated behaviour.

"The production-exercised order wins" applied uniformly, which was the previous design's rule, is an
expedient rather than a principle. Applied to the semantic cases it would enshrine the ASDE-X
wall-clock coast deadline and the discarded approach scores as contract, for no better reason than
that they happen to be the live side.

## The eight

The spine (see [0001](0001-state-equivalence-is-the-tick-contract.md)) is one ordered list, and that
list is the adjudication record — so the adjudications are enumerated here rather than counted. Engine
means `SimulationEngine.TickPostPhysics`, thirteen steps; live means `TickProcessor.ProcessPostPhysics`,
thirty-two. Positions are given as ordinals rather than line numbers, because resolving each of these
moves the lines.

| Kind | Item | Engine | Live |
|---|---|---|---|
| Ordering | `TickPilotProactive` | 2nd of 13 | 14th of 32 |
| Ordering | `PilotSpeech` / `Readbacks` drained in the opposite order | readbacks, then speech | speech, then readbacks |
| Ordering | `DrainAllStripDispatches` | 8th, right after the warning drain | 27th, immediately before `AutoDelete` |
| Membership | `TickAutoDelete` — the only step that removes aircraft | never called | `Post.AutoDelete` |
| Membership | `TickSoloTrainingEvaluation` | never called | `Post.SoloTrainingEvaluation` |
| Arguments | `TickConflictAlerts` internal airports | `[]` — no approach-corridor suppression, so it over-alerts | the room's real set, from the server's STARS config |
| Semantic | ASDE-X / SAID coast deadline | — | `DateTime.UtcNow` rather than sim-time |
| Semantic | `DrainAllApproachScores` | drained and discarded | consumed |

Every other step the two lists share — `LiveTrafficRunwayUse`, `Transponders`, `VisualDetection`,
`ConflictAlerts`, `EramConflictAlerts`, `Warnings`, `Notifications`, `Transmissions`, `ApproachScores`
— holds the same order relative to the others on both paths. The live indices are larger only because
around twenty host-only steps are interspersed, which is membership rather than disagreement.

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
- Recordings that desync as a result are triaged individually — see
  [0004](0004-the-oracle-and-the-corpus.md).
