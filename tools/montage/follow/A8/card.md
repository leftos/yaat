# A8 — 28L downwind follower told to follow a 28R straight-in

**Rule.** At KOAK the close parallels fly opposite patterns: 28L left traffic on the south side, 28R right traffic on the north side. A VFR arrival on 28L's left downwind told to follow traffic on a straight-in for 28R re-sequences onto the lead's runway: it takes 28R's established right circuit, crossing the field at 28R's pattern altitude to reach the north side, and lands 28R behind the lead. It never flies a left base onto 28R, which would cross 28L's final.

**Grounding.**

- AIM 4-3-3 FIG 4-3-3 *Traffic Pattern Operations Parallel Runways*, key 7 — "Do not overshoot final or continue on a track which will penetrate the final approach of the parallel runway." (`.claude/reference/faa/aim/chap04_sec03.md`)
- 7110.65 §3-8-1 *SEQUENCE/SPACING APPLICATION* — establish the sequence by requiring aircraft to adjust their operation to achieve proper spacing (`.claude/reference/faa/7110.65/chap03_sec08.md`). Its phraseology for traffic on another runway is a traffic advisory, so following onto another runway is a trainer affordance ([geometry doc](../../../../docs/approach-and-pattern-geometry.md), *Visual following*).
- 7110.65 §7-6-7.a *SEQUENCING* — "Ensure visual contact is established with the aircraft to follow and provide instruction to follow that aircraft." (`.claude/reference/faa/7110.65/chap07_sec06.md`)

**What to watch for.**

1. `N52417` joins the 28R final straight in from the east and is cleared to land 28R; `N738SP` comes in from the west along the shoreline and joins 28L's left downwind, south of the field, at 600 ft.
2. After the `RTIS` the follower answers "Negative contact, N52417, looking", calls "traffic in sight." at t=137, and at t=142 is told `FOLLOW N52417` with the lead about 2 NM out on 28R's final: accepted, then `CLAND 28R`.
3. The follower turns north across the field instead of turning base (t=142–196), climbing from 600 ft to 28R's pattern altitude (about 1,000 ft) as it crosses, and turns onto 28R's right downwind on the north side.
4. It holds that downwind while the lead finishes its final (the `.rbl` line reads about 0.72 NM abeam at t≈242), turns a right base at t=258 and rolls out on 28R's final about 1.3 NM behind the lead. No left base, nothing across 28L's final.
5. The lead lands first; the follower says "the traffic's on the ground, breaking off the follow." at t=281 and lands on 28R second.

Seed test: `FollowPairTrajectoryTests.Follow_CrossRunway_FromDownwind_ResequencesOntoLeadRunway_NoAboutFace`.
