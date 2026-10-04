# A1 — Downwind follower behind a straight-in on a 2 NM final

**Rule.** A VFR arrival established on the downwind is told to follow traffic on a straight-in final. The follower holds its downwind leg past its normal base-turn point and turns base only once turning would roll it out at least YAAT's pattern spacing behind the lead (1 NM behind a piston; a trainer convention, not an FAA figure), so it sequences in trail and lands second. It never turns ahead of the traffic it is following.

**Grounding.**

- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — establish the sequence by requiring aircraft to adjust their operation to achieve proper spacing; the phraseology is `FOLLOW (description and location of traffic)` and `EXTEND DOWNWIND` (`.claude/reference/faa/7110.65/chap03_sec08.md`).
- 7110.65 §7-6-7.a *SEQUENCING* — "Inform the pilot of the aircraft to follow when the integrity of the approach sequence is dependent on following a preceding aircraft. Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)
- AIM 4-3-2.d.4 — a tower-radar-equipped local controller "would use the radar to advise a pilot on an extended downwind when to turn base leg" (`.claude/reference/faa/aim/chap04_sec03.md`).
- AIM 4-3-3 FIG 4-3-2 key 3 — complete the turn to final "at least 1/4 mile from the runway"; and AIM 4-3-3.d NOTE 1 — a pilot flying a straight-in "should not disrupt the flow of arriving and departing traffic", while pattern traffic stays alert for straight-ins.

**What to watch for.**

1. `N738SP` answers the `RTIS` with "negative contact, looking", reports the traffic in sight, then is told to `FOLLOW N52417`; the follow is accepted, not refused.
2. The follower stays on the downwind past the point where it would normally turn base, holding pattern altitude (about 1,000 ft) while it extends, as the lead flies its straight-in.
3. The follower turns base behind the lead from pattern altitude, descends on base, rolls out in trail on 28R's final about 1.3 NM behind, and is never closer than that in trail on final.
4. The lead touches down first and the follow ends without a call; the follower flies its own approach and lands second. No go-around, no overtake.

Seed test: `FollowPairTrajectoryTests.Follow_FromDownwind_LeadOnStraightInFinal_SequencesBehind`.
