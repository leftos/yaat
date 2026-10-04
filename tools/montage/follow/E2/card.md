# E2 — Base follower that would roll out ahead turns downwind for spacing

**Rule.** A VFR arrival on a right base told to follow traffic on a straight-in final projects where it would roll out on final against where the lead will be then. When it would roll out level with or ahead of the lead, no widen can put it behind: it breaks off the base, turns out to the downwind heading, tells the tower "turning downwind for spacing behind the traffic, request base turn.", and turns base again once it can roll out behind the lead. It never cuts in front of the traffic it was told to follow.

**Grounding.**

- AIM 4-3-4.d — a pilot "should not take advantage of another aircraft, which is on final approach to land, by cutting in front of, or overtaking that aircraft." (`.claude/reference/faa/aim/chap04_sec03.md`)
- AIM 4-3-5 *Unexpected Maneuvers in the Airport Traffic Pattern* — "The controller can anticipate minor maneuvering such as shallow “S” turns. The controller cannot, however, anticipate a major maneuver …"; "Should a pilot decide to make maneuvering turns to maintain spacing behind a preceding aircraft, the pilot should always advise the controller if at all possible." A turn-out to the downwind heading is far more than the shallow S-turns a controller can anticipate, and AIM 4-3-5 asks the pilot to advise of any maneuvering turns for spacing, so the pilot says so.
- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — establish the sequence by requiring aircraft to adjust their operation to achieve proper spacing (`.claude/reference/faa/7110.65/chap03_sec08.md`).
- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)

**What to watch for.**

1. `N52417` flies a practice ILS 28R straight in and is cleared to land; `N738SP` joins a right base for 28R from the northeast, answers the `RTIS` with "Negative contact, N52417, looking", and calls "traffic in sight." at t=102.
2. `FOLLOW N52417` at t=122 is accepted, with the lead about 4.5 NM out on the final and the follower about 1.6 NM north of it on its base, 3.1 NM out: by path the lead is just ahead, but at its faster base speed the follower would reach the final first.
3. At t=123 the follower says "turning downwind for spacing behind the traffic, request base turn." and turns out to the downwind heading (112°), slowing to 62 kt, while the lead passes in front of it on the final.
4. It turns base again at t≈161, rolls out on 28R's final at t≈240 about 1.45 NM behind the lead, and stays at least 1.18 NM in trail to the lead's touchdown.
5. The lead lands first; the follow ends without a call and the follower lands second.

Seed test: `BaseFollowSpacingTests.RightBaseFollower_RolloutAheadOfLeadOnFinal_BreaksOffTurnsDownwindAndTrails`.
