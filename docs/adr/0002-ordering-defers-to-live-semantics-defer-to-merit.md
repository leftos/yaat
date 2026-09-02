---
status: accepted
date: 2026-09-02
---

# Ordering disagreements defer to live, semantic disagreements defer to merit

Where the paths disagree, something has to win. There are nine adjudications: four ordering
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
- The engine and replay paths absorb the four ordering moves.
- Recordings that desync as a result are triaged individually — see
  [0004](0004-the-oracle-and-the-corpus.md).
