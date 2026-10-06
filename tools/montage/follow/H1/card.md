# H1 — Ground follow through a turn and a runway hold short

**Rule.** An aircraft told to follow taxiing traffic falls in behind it and taxis where the traffic taxis, at a taxi gap (YAAT taxis at normal taxi speed until it is within about 180 ft of the lead, matches the lead's speed from there, and stops about 90 ft behind a stopped lead), through every turn the traffic makes.

The follow is not a runway crossing clearance: where the traffic was cleared across a runway, the follower stops at that runway's holding position and waits for its own `CROSS`, then crosses and picks the follow up again.

**Student position.** Oakland Ground (`OAK_GND`, GC1); the scenario's `studentPositionId` is the ground position, so the solo pilots call and answer Ground. Oakland Tower is in the `atc` list.

**Grounding.**

- 7110.65 §3-7-1.a *GROUND TRAFFIC MOVEMENT* — the conditional-instruction ban "does not preclude issuing instructions to follow an aircraft observed to be operating on the movement area in accordance with an ATC clearance/instruction and in such a manner that the instructions to follow are not ambiguous." (`.claude/reference/faa/7110.65/chap03_sec07.md`)
- 7110.65 §3-7-2 *TAXI AND GROUND MOVEMENT OPERATIONS*, phraseology — `FOLLOW (traffic) (restrictions as necessary)`; §3-7-2.c — "Issue a crossing clearance to aircraft for each runway their route crosses."; §3-7-2.d — "When an aircraft/vehicle is instructed to “follow” traffic and requires a runway crossing, issue a runway crossing clearance in addition to the follow instructions and/or hold short instructions, as applicable." (same file)
- AIM 4-3-18.a.5 *Taxiing* — "A clearance must be obtained prior to crossing any runway. ATC will issue an explicit clearance for all runway crossings." (`.claude/reference/faa/aim/chap04_sec03.md`)

**What to watch for.** Recorded headless with `--sim-hours 0.1` (360 s); the times are sim seconds.

1. `N52417` reads back "runway 28R taxi via F C B cross runway 33" at t=3 and pulls out of GA13 onto F. `N738SP`, still parked at GA16, calls "Oakland Ground, at parking GA16, with information Alpha, VFR to Livermore Airport, ready to taxi." at t=8.
2. At t=20 `FOLLOWG N52417` is accepted ("Follow N52417"). The follower starts up, falls in behind the lead on F and follows it down the west side of runway 33, 0.021–0.025 NM (130–150 ft) in trail at 20 kt, then through the left turn from F onto C (t≈117–137, heading 160° to 112°).
3. At t=149 the lead enters runway 33 at C on its crossing clearance; the follower stops short of the runway 33 holding position on C (terminal: "N738SP holding short runway 15/33 at taxiway") while the lead crosses and taxis on.
4. At t=165 `CROSS 33`; the follower reads back "cross runway 33" at t=166, crosses (t=166–194) and is following again from t=194, 0.169 NM behind the lead. It closes up behind the lead when the lead holds short of 28R at B (t=305) and stops 0.019 NM (115 ft) behind it at t=325.

**Capture stop.** No `wait_until` condition tests a position, and the follower's phase is `Following` both before the hold and after the crossing, so the clip stops on sim time, once the follower is across runway 33 and following again:

```json
{"conditions": [{"kind": "sim_seconds", "atLeast": 200}], "timeoutMs": 600000}
```

**Defects the headless take shows (not capture-ready).**

- The follower stops from 20 kt to 0 in one second at t=148–149, 8 ft of travel, 115 ft short of the runway 33 holding position on C (`FollowingPhase.CheckRunwayHoldShort` zeroes the speed when a bar comes within 120 ft).
- `FOLLOWG` gets no pilot readback: the terminal log has no `SayPilot` line from `N738SP` at t=20.
- While following and holding short, `N738SP` repeats its parking call-up ("… at parking GA16, … ready to taxi.") at t=125 and t=245.
- The hold-short warning names no taxiway ("… holding short runway 15/33 at taxiway"); the lead's reads "… at B".

Seed tests: `FollowGroundFromParkingTests.FTH399_AcceptsFollowGroundFromParking`, `FollowGroundCrossChainTests` (both KOAK, S2-OAK-P).
