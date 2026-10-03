# G6 — Closed-traffic climb follows a runwayless lead, holding the upwind first

**Rule.** A VFR departure in closed traffic told, during its takeoff climb, to follow traffic that has no runway yet accepts the follow but does not turn toward the lead at once. It flies the departure leg first: straight ahead until it is at least 1/2 mile beyond the departure end of the runway and within 300 ft of pattern altitude. Then it turns and pursues the lead, and when the lead joins the pattern it flies the circuit behind it and lands second.

**Grounding.**

- AIM 4-3-2.c.1 — "**Departure.** The flight path that begins after takeoff and continues straight ahead along the extended runway centerline. The departure climb continues until reaching a point at least 1/2 mile beyond the departure end of the runway and within 300 feet of the traffic pattern altitude." (`.claude/reference/faa/aim/chap04_sec03.md`)
- AIM FIG 4-3-2 *Traffic Pattern Operations Single Runway*, keys 4–5 — "Continue straight ahead until beyond departure end of runway."; "If remaining in the traffic pattern, commence turn to crosswind leg beyond the departure end of the runway within 300 feet of pattern altitude." (`.claude/reference/faa/aim/chap04_sec03.md`)
- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)

**What to watch for.**

1. `N738SP` is lined up on 28R. After the `RTIS` it calls "traffic in sight." at t=15, then is cleared for takeoff with right closed traffic. `N52417` is eastbound at 1,500 ft north of the field, direct Lake Chabot with a right base for 28R queued behind it.
2. At t=40 `FOLLOW N52417` is accepted with the follower airborne at 132 ft, 0.39 NM down the runway, and the lead 1.2 NM to its north-east.
3. The follower holds runway heading through the whole departure leg. It finishes its takeoff climb at t=63 (415 ft), passes the departure end at t≈64 (0.90 NM from the threshold) and keeps climbing straight ahead to 709 ft, the 1,009 ft pattern altitude less 300, at t≈87. By then it is 1.41 NM from the threshold and the lead is 2.8 NM away.
4. Only then does it turn, at t=88, right through north onto the lead's eastbound track, level at 1,009 ft by t=120, and pursue the lead 3.5 NM ahead.
5. At t=179 the lead turns its queued right base near Lake Chabot. The follower returns to the pattern ("midfield downwind runway 28R.") and flies the right downwind behind it: base at t=325, final at t=369. The lead lands at t=474 with the follower 1.27 NM behind. The follow ends there without a call, and the follower lands second at t=541.

Seed test: `FollowClimbFollowerTests.PendingPursuit_FromClosedClimb_HoldsUpwindUntilPastDepartureEndAndTpaMinus300`.

Recorded headless with `--sim-hours 0.17` (612 s).
