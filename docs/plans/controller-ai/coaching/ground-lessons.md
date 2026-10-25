# Ground lessons from coaching sessions

The rules the trainer taught in coaching sessions, distilled from the session logs in [`sessions/`](sessions/). Each lesson is written as a rule a controller follows, with its source exchange and where it belongs in the controller AI.

**Target** is one of:
- a GroundBrain rule in [`../04-ground-brain.md`](../04-ground-brain.md) (new, changed or confirmed);
- an entry in `src/Yaat.Sim/Data/FacilityOps/KOAK.json`;
- the scan checklist in the `atc-coaching` skill;
- a pause trigger.

| # | Lesson | Source | Target | State |
|---|---|---|---|---|
| L1 | A pause with nothing for the student's position is resumed without a ruling. | [S1 E1](sessions/001-2026-10-25-s1-oak-2-3-4.md#e1--t1-trigger-new_aircraft-nks8659--n33027) | `atc-coaching` loop | Applied |
| L2 | A push faces the aircraft the way its taxi out goes. | S1 E2, E3, E4 | GroundBrain push rule (confirmed) | Open |
| L3 | A push onto a non-movement taxilane is at the pilot's discretion; Ground does not own the ramp. | S1 E2 (YAAT-1240) | GroundBrain push rule; pilot readback | Open |
| L4 | A taxi route starts at the first movement-area taxiway; non-movement taxilanes are not issued (the last one only to pick the ramp exit, rarely worth it). | S1 E5 | GroundBrain taxi-route rule | Open |
| L5 | The full-length connector is left out when it is a numbered variation of the taxiway before it ("30 via U, W", not W1); kept when it is not ("28R via C, B"). | S1 E5 (7110.65) | GroundBrain taxi phraseology | Open |
| L6 | A taxier close behind company on the same route gets "runway N, follow company" instead of a route. | S1 E7 (YAAT-1242) | GroundBrain sequencing rule | Open |
| L7 | Transfer a departure to Tower as soon as it is sequenced: once Ground can no longer change its order to the runway (OAK: on W east of U toward W1), not at the hold-short. | S1 E10, E13, E17 | GroundBrain transfer rule; KOAK.json sequencing point | Open |
| L8 | OAK's standard taxi-in from 30 to the passenger terminal (all gates) is W, V, T: U is kept for southbound departures, B for cargo (FedEx ramp, spots A/B to 30, light cargo to 28L). | S1 E16 | KOAK.json standard routes | Open |
| L9 | Arrival and departure flows get separate lanes; pick the taxi-in that keeps them apart. | S1 E16 | GroundBrain taxi-route rule | Open |
| L10 | On a ramp Ground does not own, a push into a blocked alley is still issued as the push wanted; the pilot works out when it is clear. | S1 E15 | GroundBrain push rule | Open |
| L11 | A taxi call from an aircraft boxed in on the ramp gets "standby" until its way to the movement area is open. | S1 E19 | GroundBrain taxi rule | Open |
| L12 | After any pause of the student's own, read the pause events before resuming. | S1 misses (t=310) | `atc-coaching` loop | Applied |
