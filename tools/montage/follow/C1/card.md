# C1 — Base follower pursues a free-flight lead and joins its base

**Rule.** A VFR arrival on a right base told to follow traffic that has no runway yet, only a pattern entry queued behind a reporting point, pursues it instead of flying its own base: it keeps pattern spacing behind the lead along the lead's own path. When the lead turns base, the follower joins the lead's base line behind it, rolls out on the final in trail and lands second. It never cuts inside the lead's base onto the final ahead of it.

**Grounding.**

- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — "Establish the sequence of arriving and departing aircraft by requiring them to adjust flight or ground operation, as necessary, to achieve proper spacing." (`.claude/reference/faa/7110.65/chap03_sec08.md`)
- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)
- AIM 4-3-4.d — a pilot "should not take advantage of another aircraft, which is on final approach to land, by cutting in front of, or overtaking that aircraft." (`.claude/reference/faa/aim/chap04_sec03.md`)
- AIM 4-3-5 *Unexpected Maneuvers in the Airport Traffic Pattern* — "Should a pilot decide to make maneuvering turns to maintain spacing behind a preceding aircraft, the pilot should always advise the controller if at all possible." (`.claude/reference/faa/aim/chap04_sec03.md`)

**What to watch for.**

1. `N52417` flies direct Lake Chabot with a right base for 28R queued behind it; `N738SP` joins a wide right base for 28R north-east of Lake Chabot. After the two `RTIS` calls at t=30 the lead calls "traffic in sight." at t=52 and the follower at t=55.
2. At t=90 `FOLLOW N52417` is accepted with the lead 1.6 NM south of the follower and still runwayless. The follower is short of the 1.0 NM pattern spacing measured along the lead's path, so it says "S-turning for spacing behind the traffic." at t=91 and turns out to the north-west at 62 kt.
3. At t=199 the lead's queued right base begins at Lake Chabot, and the follower joins the lead's base behind it. It is on the base from t=250, never more than 0.09 NM off the lead's base line, and never crosses south of the 28R centerline before its own final.
4. Both are on final from t=359, the follower 1.43 NM behind; the trail never closes below 1.20 NM.
5. The lead lands at t=525; the follower says "the traffic's on the ground, breaking off the follow." at t=537 and lands second at t=591.

Seed test: `FollowRunwaylessLeadFromPatternTests.FollowFromBase_RecordedCase_JoinsLeadBaseAndLandsInTrail`.

Recorded headless with `--sim-hours 0.17` (612 s).
