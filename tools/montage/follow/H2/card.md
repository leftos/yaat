# H2 — Give way to traffic crossing ahead on the taxiway

**Rule.** A taxi clearance with no hold-short instruction lets an aircraft cross every taxiway on its route, so when two taxi routes cross, the ground controller sequences them.

Told to give way (`GIVEWAY`, the controller's "behind the traffic" or "hold for the traffic"), the aircraft taxis on to the crossing and stops with its wingtips clear of the traffic's taxiway, lets the named traffic cross ahead of it, and continues on its own clearance once the traffic is past the crossing, with no further call from the controller.

**Student position.** Oakland Ground (`OAK_GND`, GC1); the scenario's `studentPositionId` is the ground position, so the solo pilots call and answer Ground. Oakland Tower is in the `atc` list.

**Grounding.**

- 7110.65 §3-7-1 *GROUND TRAFFIC MOVEMENT* — "Issue by radio or directional light signals specific instructions which approve or disapprove the movement of aircraft, vehicles, equipment, or personnel on the movement area" (`.claude/reference/faa/7110.65/chap03_sec07.md`).
- 7110.65 §3-7-2.a NOTE — "The absence of holding instructions authorizes an aircraft/vehicle to cross all taxiways that intersect the taxi route."; the paragraph's phraseology carries `HOLD POSITION`, `HOLD FOR (reason)` and `BEHIND (traffic)` (same file).
- AIM 7-6-1.d *Giving Way* (`.claude/reference/faa/aim/chap07_sec06.md`) — "If you think another aircraft is too close to you, give way instead of waiting for the other pilot to respect the right‐of‐way to which you may be entitled." (the pilot's side of the same idea; the paragraph is about collision avoidance in general).

**What to watch for.** Recorded headless with `--sim-hours 0.1` (360 s); the times are sim seconds. The crossing is node 439 of the KOAK layout, where K crosses F at the F/K/L junction west of runway 33.

1. `N738SP` leaves GA16 on its clearance ("runway 28R taxi via F C B cross runway 33", t=3) and heads south on F towards the junction. `N52417`, a KOAK departure on K at spot K0LS, calls ready to taxi ("VFR to San Carlos Airport", t=8), reads back "taxi via K D to parking GA7, cross runway 33" at t=37 and heads north on K towards the same junction.
2. At t=56, with the two 0.25 NM apart, `GIVEWAY N52417` is accepted ("Give way to N52417"). `N738SP` keeps taxiing at 20 kt, brakes at the 2 kt/s piston taxi rate from t=63 and stops on F at t=74, its centre about 62 ft off K's centreline (the two C172s' half-spans plus 25 ft is 61 ft), 66 ft from the crossing.
3. `N52417` crosses F at t≈98, passing 0.011 NM (67 ft) from `N738SP` without being stopped, and turns onto K's northeast leg. `N738SP` holds for 30 s (t=74–103) and starts rolling again at t=104, reaching the junction at t≈112 behind the traffic.

**Capture stop.** No `wait_until` condition tests a position, so the clip stops on sim time, once `N738SP` has passed the junction behind the traffic:

```json
{"conditions": [{"kind": "sim_seconds", "atLeast": 120}], "timeoutMs": 600000}
```

**Defects the headless take shows (not capture-ready).**

- `GIVEWAY` gets no pilot readback: the terminal log has no `SayPilot` line from `N738SP` after t=56.
- `N52417`, a ground spawn on taxiway K, calls "Oakland Ground, at the ramp, with information Alpha, VFR to San Carlos Airport, ready to taxi." at t=8: it is on a taxiway, not a ramp.

Seed tests: `GiveWayStopBrakingTests` (this clip's geometry and timing), `GiveWayAutoReleaseTests`, `GiveWayInTrailReleaseTests`, `Commands/GiveWayRedesignTests`.
