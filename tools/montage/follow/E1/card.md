# E1 — Base follower slightly too close widens its base

**Rule.** A VFR arrival on a right base told to follow traffic on a straight-in final projects where it would roll out on final against where the lead will be then. When it is behind the lead but would roll out short of YAAT's pattern spacing (1 NM behind a piston; a trainer convention, not an FAA figure), it widens its base: it turns 30° off the base heading, away from the field and out along the final, holds that heading until the projected gap is met, then turns back onto the base heading and rolls out at least the spacing behind the lead. It keeps the base leg throughout. YAAT makes no call for a widen this small: AIM 4-3-5 lets the controller 'anticipate minor maneuvering such as shallow “S” turns', though it also asks pilots to advise of maneuvering turns 'if at all possible'.

**Grounding.**

- AIM 4-3-5 *Unexpected Maneuvers in the Airport Traffic Pattern* — "On occasion it may be necessary for pilots to maneuver their aircraft to maintain spacing with the traffic they have been sequenced to follow. The controller can anticipate minor maneuvering such as shallow “S” turns." (`.claude/reference/faa/aim/chap04_sec03.md`)
- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — establish the sequence by requiring aircraft to adjust their operation to achieve proper spacing; the phraseology is `FOLLOW (description and location of traffic)` (`.claude/reference/faa/7110.65/chap03_sec08.md`).
- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)
- AIM 4-3-4.d — a pilot "should not take advantage of another aircraft, which is on final approach to land, by cutting in front of, or overtaking that aircraft."

**What to watch for.**

1. `N52417` flies a practice ILS 28R straight in and is cleared to land; `N738SP` joins a right base for 28R from the northeast, answers the `RTIS` with "Negative contact, N52417, looking", and calls "traffic in sight." at t=80.
2. `FOLLOW N52417` at t=93 is accepted, with the lead about 4.1 NM out on the final and the follower about 2.2 NM north of it on its base, 3.1 NM out.
3. At once the follower turns 30° away from the field (heading 202° → 172°, t=94–99), holds it for about 10 s, and turns back onto the base heading by t=116. No radio call.
4. It rolls out on 28R's final at t≈207 about 1.17 NM behind the lead (the `.rbl` line is at its closest, 0.94 NM, as the base converges on the final) and stays above 1.07 NM in trail to the lead's touchdown.
5. The lead lands first; the follow ends without a call and the follower lands second. No break-off of the base, no go-around.

Seed test: `BaseFollowSpacingTests.RightBaseFollower_RolloutSlightlyTooClose_WidensAndRollsOutBehind`.
