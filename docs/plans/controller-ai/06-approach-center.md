# 06 — Approach and Center Brains (sketches)

Part of [Controller AI + Soak Harness](README.md). Later-milestone scope sketches (CA5/CA6) — flesh
each into a full subplan (with `aviation-sim-expert` review) before implementing, following the
pattern of [04](04-ground-brain.md)/[05](05-tower-brain.md).

## Approach brain v1 (CA5)

Owns arrivals from Center ACCEPT to tower CT, and departures from auto-track to the Center HO:

- **Arrivals:** descend via `CM`/`DM` + `SPD` to a feeder gate; greedy first-by-ETA slot sequencing
  to a **single final**; vector base/final (`FH`/`TL`/`TR`); clear the approach (`CAPP`/`PTAC`);
  enforce in-trail spacing by weight class on final (the existing on-approach CWT table); `CT` tower
  around the FAF.
- **Departures:** after auto-track, climb per SID; `HO` to Center at the boundary/altitude.
- **Releases:** issue `HFR`/`REL` for satellite AI towers via the real machinery; participate in
  configured RD-channel departure coordination.
- **v1 bounds:** single final, single airport; no simultaneous/parallel approaches; no holding.

## Center brain v1 (CA6)

- Accept/initiate handoffs at sector boundaries; climb departures to cruise or exit altitude;
  descend arrivals to meet TRACON crossing restrictions; `HO` to Approach.
- **Conflict resolution v1 = altitude-only** (1,000 ft) driven by the existing conflict-probe
  outputs; vectoring solutions later.
- Departure releases via the HFR rundown.
- **v1 bounds:** single sector.
