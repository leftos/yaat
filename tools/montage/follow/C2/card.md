# C2 — Pursuit too close behind a pattern-bound lead S-turns for spacing

**Rule.** A VFR aircraft told to follow traffic that is bound for the pattern, but has no runway yet, pursues it at pattern spacing: 1.0 NM behind a Cessna, measured along the lead's path. Told to follow from closer than that, it does not slow alone and close up nose to tail: it makes a shallow S-turn to the pattern side at its slowest safe speed, tells the tower "S-turning for spacing behind the traffic.", and once the gap is built turns back to fly nose-on to the lead in trail.

**Grounding.**

- AIM 4-3-5 *Unexpected Maneuvers in the Airport Traffic Pattern* — "On occasion it may be necessary for pilots to maneuver their aircraft to maintain spacing with the traffic they have been sequenced to follow. The controller can anticipate minor maneuvering such as shallow “S” turns."; "Should a pilot decide to make maneuvering turns to maintain spacing behind a preceding aircraft, the pilot should always advise the controller if at all possible." (`.claude/reference/faa/aim/chap04_sec03.md`)
- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — "Establish the sequence of arriving and departing aircraft by requiring them to adjust flight or ground operation, as necessary, to achieve proper spacing." (`.claude/reference/faa/7110.65/chap03_sec08.md`)
- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)

**What to watch for.**

1. Both Cessnas come in from the east toward Lake Chabot: `N52417` slows to 70 kt with a right base for 28R queued behind Lake Chabot, and `N738SP`, 1.6 NM behind at 100 kt, overtakes it. The follower calls "traffic in sight." at t=20.
2. At t=100 `FOLLOW N52417` is accepted with the follower 0.81 NM behind the lead.
3. At t=101 the follower says "S-turning for spacing behind the traffic.", slows to 62 kt and turns right, to the north (the pattern side), on a heading of about 300° against the lead's westbound track.
4. The gap reaches 1.1 NM at t=152 and 1.37 NM at t=200, when the follower has turned back. From t=205 until the lead turns base at t=355 the follower flies nose-on to the lead (its heading within 0.3° of the bearing to it), 1.17–1.37 NM in trail at the lead's 70 kt.
5. At t=355 the lead turns its right base at Lake Chabot and the follower joins that base behind it. The lead lands at t=687; the follower says "the traffic's on the ground, breaking off the follow." at t=700 and lands second at t=753.

Seed test: `FollowRunwaylessLeadFromPatternTests.FreePursuit_TooCloseBehindPatternBoundLead_STurnsOutsideThenFollowsNoseOnInTrail`.

Recorded headless with `--sim-hours 0.23` (828 s).
