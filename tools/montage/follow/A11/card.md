# A11 — FOLLOW with no traffic in sight, then FOLLOWF

**Rule.** A pilot can follow only traffic it has in sight, so `FOLLOW` is refused until the aircraft has reported the traffic in sight after a traffic advisory (`RTIS`). `FOLLOWF` is `FOLLOW` with the traffic-in-sight report folded in, for the instructor (RPO) who knows the pilot has the traffic: it is accepted without an `RTIS` and is otherwise exactly `FOLLOW`.

**Grounding.**

- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)
- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — the phraseology is `FOLLOW (description and location of traffic)` (`.claude/reference/faa/7110.65/chap03_sec08.md`).
- `COMMANDS.md`, `FOLLOWF` — "RPO-only; folds `RTISF` in, no prior `RTIS` needed, otherwise exactly `FOLLOW` (same refusals)".

**What to watch for.**

1. Both Cessnas join the right downwind for 28R from the northwest in trail, about 1.2 NM apart. `N738SP` is never given a traffic advisory.
2. With the lead on base and the follower on the downwind, `FOLLOW N52417` at t=195 is refused: "Traffic not in sight — issue RTIS first".
3. `FOLLOWF N52417` at t=200 is accepted: "Follow N52417".
4. In the run without `--solo`, the follower turns base at t=245 and at once turns back out ("turning downwind for spacing behind N52417, request base turn." at t=246), turns base again at t≈256, and lands second; the lead lands first and the follower reports "N52417 is on the ground, breaking off the follow." at t=287.

**Open.** `FOLLOWF` is refused in solo training ("FOLLOWF is RPO-only; use RTIS/RTISF in solo training"), so this clip cannot be recorded with `--solo` as the other clips are; how it is recorded is not decided.

Seed test: `FollowRunwaylessLeadFromPatternTests.FollowForce_NoTrafficInSight_IsAccepted`.
