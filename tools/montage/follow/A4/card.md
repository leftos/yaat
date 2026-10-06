# A4 — Downwind follower behind a lead that extends, then turns base

**Rule.** A VFR arrival on the downwind told to follow traffic ahead of it on the same downwind sequences on that traffic, not on its own fixed base-turn point.

While the lead extends its downwind, the follower holds its own downwind behind it; once the lead turns base, the follower keeps extending until turning base would roll it out at least YAAT's pattern spacing (1 NM behind a piston; a trainer convention, not an FAA figure) behind the lead, then turns base and lands second. It never turns inside the traffic it was told to follow.

**Grounding.**

- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — establish the sequence by requiring aircraft to adjust their operation to achieve proper spacing; the phraseology lists `FOLLOW (description and location of traffic)` beside `EXTEND DOWNWIND` (`.claude/reference/faa/7110.65/chap03_sec08.md`).
- 7110.65 §7-6-7.a *SEQUENCING* — "Inform the pilot of the aircraft to follow when the integrity of the approach sequence is dependent on following a preceding aircraft. Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)
- AIM 4-3-5 *Unexpected Maneuvers in the Airport Traffic Pattern* — "On occasion it may be necessary for pilots to maneuver their aircraft to maintain spacing with the traffic they have been sequenced to follow. The controller can anticipate minor maneuvering …" but not a maneuver that interrupts the sequence (`.claude/reference/faa/aim/chap04_sec03.md`).
- AIM 4-3-4.d — a pilot "should not take advantage of another aircraft, which is on final approach to land, by cutting in front of, or overtaking that aircraft."

**What to watch for.**

1. Both Cessnas join the right downwind for 28R from the northwest in trail, about 1.2 NM apart. `N738SP` calls the traffic in sight after the `RTIS` and is told to `FOLLOW N52417` while both are still on the entry.
2. The lead is told to `EXT`; from t≈200 both are on the downwind, the lead extending past its normal base-turn point and the follower holding 1.2 NM behind it at pattern altitude (about 1,000 ft), slowed to approach speed.
3. The lead is told `TB` at t=250. The follower does **not** turn with it: it flies on for another ~70 s, the `.rbl` line closing to about 0.75 NM as the lead's base swings across in front of it, and turns base at t≈322 once the lead is established on final.
4. The follower rolls out on 28R's final about 1.3 NM behind the lead, the lead lands first, and the follower lands second; the follow ends without a call.

Seed test: `FollowPatternSequencingAuditTests.Follower_SequencesBehind_WhenLeadExtendsThenTurnsBase`.
