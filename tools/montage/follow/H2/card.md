# H2 — Give way to traffic crossing ahead on the taxiway

**Rule.** A taxi clearance with no hold-short instruction lets an aircraft cross every taxiway on its route, so when two taxi routes cross, the ground controller sequences them. Told to give way (`GIVEWAY`, the controller's "behind the traffic" or "hold for the traffic"), the aircraft stops where it is, lets the named traffic cross ahead of it, and continues on its own clearance once the traffic is past the crossing, with no further call from the controller.

**Student position.** Oakland Ground (`OAK_GND`, GC1); the scenario's `studentPositionId` is the ground position, so the solo pilots call and answer Ground. Oakland Tower is in the `atc` list.

**Grounding.**

- 7110.65 §3-7-1 *GROUND TRAFFIC MOVEMENT* — "Issue by radio or directional light signals specific instructions which approve or disapprove the movement of aircraft, vehicles, equipment, or personnel on the movement area" (`.claude/reference/faa/7110.65/chap03_sec07.md`).
- 7110.65 §3-7-2.a NOTE — "The absence of holding instructions authorizes an aircraft/vehicle to cross all taxiways that intersect the taxi route."; the paragraph's phraseology carries `HOLD POSITION`, `HOLD FOR (reason)` and `BEHIND (traffic)` (same file).
- AIM 7-6-1.d *Giving Way* (`.claude/reference/faa/aim/chap07_sec06.md`) — "If you think another aircraft is too close to you, give way instead of waiting for the other pilot to respect the right‐of‐way to which you may be entitled." (the pilot's side of the same idea; the paragraph is about collision avoidance in general).

**What to watch for.** Recorded headless with `--sim-hours 0.1` (360 s); the times are sim seconds. The crossing is node 439 of the KOAK layout, where K crosses F at the F/K/L junction west of runway 33.

1. `N738SP` leaves GA16 on its clearance ("runway 28R taxi via F C B cross runway 33", t=3) and heads south on F towards the junction. `N52417`, taxiing in on K at spot K0LS, reads back "taxi via K D to parking GA7, cross runway 33" at t=14 and heads north on K towards the same junction.
2. At t=56, with `N52417` about 380 ft short of the crossing at 20 kt and `N738SP` 0.15 NM from it on F, `GIVEWAY N52417` is accepted ("Give way to N52417"). `N738SP` stops on F at t=58, about 630 ft short of the crossing.
3. `N52417` crosses F at t≈68 and turns onto K's northeast leg. `N738SP` holds for 16 s (t=58–73) and starts rolling again at t=74, when the traffic is past the crossing and turning away; the two are never closer than 0.101 NM (614 ft, t=71). `N738SP` reaches the junction at t≈100, 0.137 NM behind `N52417`, which is then crossing runway 33 (t=99–112).

**Capture stop.** No `wait_until` condition tests a position, so the clip stops on sim time, once `N738SP` has passed the junction behind the traffic:

```json
{"conditions": [{"kind": "sim_seconds", "atLeast": 105}], "timeoutMs": 600000}
```

**Defects the headless take shows (not capture-ready).**

- The give-way stop brakes for one second and then snaps to a standstill: 11.1 kt at t=56, 9.1 kt at t=57 (the 2 kt/s piston brake), then 0 at t=58 with 0 ft travelled in that second.
- `GIVEWAY` gets no pilot readback: the terminal log has no `SayPilot` line from `N738SP` after t=56.
- `N52417`, a ground spawn on taxiway K with an inbound flight plan, calls "Oakland Ground, at the ramp, with information Alpha, VFR to Oakland Airport, ready to taxi." at t=8: it is on a taxiway, not a ramp, and Oakland is its destination.

Seed tests: `GiveWayAutoReleaseTests`, `GiveWayInTrailReleaseTests`, `Commands/GiveWayRedesignTests`.
