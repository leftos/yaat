# C3 — C172 pursuing a B738 extends past the jet's base turn

**Rule.** A Cessna pursuing a jet keeps the jet's spacing, not a Cessna's: 3 NM pattern spacing behind a jet, and behind a B738 the on-approach wake minimum, which is larger. When the jet turns base before the Cessna has that spacing, the Cessna does not turn base with it. It carries on past the jet's base turn point at its slowest safe speed while the jet draws ahead, and turns base once the gap along the jet's path is enough. It then follows the jet onto the same runway in trail.

**Runway.** Both land on KOAK runway 30, the airliner runway. The KOAK layout (`tests/Yaat.Sim.Tests/TestData/oak.geojson`) gives runway 30 10,507 ft along its line, with a 114 ft displaced threshold on the 30 end, against the B738's `landingDistance` of 5,249 ft in `src/Yaat.Sim/Data/AircraftProfiles.json`. Runway 28R is only 5,445 ft in the same layout, so a B738 is never landed there.

**Grounding.**

- 7110.65 §5-5-4.h, TBL 5-5-2 *Wake Turbulence Separation for On Approach* — "separate an aircraft on approach behind another aircraft to the same runway by ensuring the separation minima in TBL 5-5-2 will exist at the time the preceding aircraft is over the landing threshold." The table gives 4 NM for a category I follower behind an E or F lead. (`.claude/reference/faa/7110.65/chap05_sec05.md`)
- AIM 4-3-5 *Unexpected Maneuvers in the Airport Traffic Pattern* — "On occasion it may be necessary for pilots to maneuver their aircraft to maintain spacing with the traffic they have been sequenced to follow."; "Should a pilot decide to make maneuvering turns to maintain spacing behind a preceding aircraft, the pilot should always advise the controller if at all possible." (`.claude/reference/faa/aim/chap04_sec03.md`)
- AIM 4-3-4.d — a pilot "should not take advantage of another aircraft, which is on final approach to land, by cutting in front of, or overtaking that aircraft." (`.claude/reference/faa/aim/chap04_sec03.md`)
- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)

**What to watch for.**

1. `SWA2471`, a B738 at 2,500 ft and 200 kt, overtakes `N738SP`, a C172 at 1,500 ft and 100 kt, on the same inbound to Lake Chabot. The Cessna answers its `RTIS` with "Negative contact, SWA2471, looking" and calls "traffic in sight." at t=102, once the jet has passed.
2. At t=112 `FOLLOW SWA2471` is accepted with the jet 0.3 NM ahead; the Cessna says "S-turning for spacing behind the traffic." at t=113. At t=114 the jet is told `ERB 30` and turns a right base for runway 30, 0.35 NM ahead of the Cessna.
3. The Cessna does not turn with the jet. It slows to 62 kt by t=135 and holds it, flying 45° left of the jet's westbound leg. It passes the jet's base turn point at t≈131 and carries on 0.72 NM past it, while the gap along the jet's path grows from 0.85 NM at t=130 to 3.08 NM at t=170.
4. It turns base at t=189, 4.15 NM behind the jet along the jet's path (2.19 NM in a straight line). That is the 4 NM wake minimum, not the 3 NM pattern spacing. It flies the jet's long right base and turns final for 30 at t=416.
5. The jet lands on 30 at t=336 and turns off at W5. The Cessna says "the traffic's on the ground, breaking off the follow." at t=344 and lands on 30 second, at t=660.

Seed test: `FollowRunwaylessLeadFromPatternTests.FreePursuit_ExtendingPastTheLeadsBaseTurn_KeepsTheLegAtTheFloorAndTurnsBaseOnceSpaced`.

Recorded headless with `--sim-hours 0.2` (720 s).
