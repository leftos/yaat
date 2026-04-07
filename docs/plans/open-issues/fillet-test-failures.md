# Fillet Arc Test Failures — Investigation Tracker

9 remaining failures after navigator arc-awareness fixes (tangent bearings, arc speed back-propagation, turn anticipation suppression).

## 1. RunwayExitSpeedTests.N569SX_ExitsRunwayWithinReasonableTime

**Error:** Exit took 79s — expected ≤20s. Aircraft was creeping too slowly.
**Airport/Runway:** OAK 28R, C172 (piston)
**Symptom:** Aircraft enters RunwayExitPhase at 25kts, sits at constant speed for ~72s before finally turning.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 2. OakAllExitsTests.OAK30_B738_ExitsSmoothly(W4)

**Error:** Exit took 96s — should complete in under 90s.
**Airport/Runway:** OAK 30, B738 (jet)
**Symptom:** `EXIT W4: actual=W7 (relaxed from W4)` — aircraft misses W4 and exits at W7 instead.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 3. OakAllExitsTests.OAK28R_C172_ExitsSmoothly(E)

**Error:** Exit took 98s — should complete in under 90s.
**Airport/Runway:** OAK 28R, C172 (piston)
**Symptom:** `EXIT E: actual=C1 (relaxed from E)` — aircraft misses E and exits at C1.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 4. ExitKOvershootTests.DAL2581_ExitsAtK_NoHeadingReversal

**Error:** Expected exit at K, actual exit at Q.
**Airport/Runway:** SFO 28R, B738 (jet)
**Symptom:** Aircraft overshoots K and exits at Q instead.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 5. ExitRightTaxiwaySelectionTests.WJA1508_ExitsAtTaxiwayD_NotE

**Error:** Expected exit at D, actual exit at C3.
**Airport/Runway:** SFO 28L, B738 (jet)
**Symptom:** Aircraft exits at wrong taxiway.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 6. OakFullLifecycleTests.LandExitTaxiCrossDepart

**Error:** Assert.Equal() Failure — expected taxiway "H", got something else.
**Airport/Runway:** OAK 28R
**Symptom:** Full lifecycle test; first taxiway after exit doesn't match expected.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 7. OakGroundE2ETests.OAK_FullGroundSequence_NoOverlapAndSIG1Reached

**Error:** N569SX stopped 827ft from SIG1 — should be within 30ft.
**Airport/Runway:** OAK
**Symptom:** Aircraft doesn't reach parking destination.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 8. SfoLineupDiagonalTests.N346G_LineUp28R_TickByTickTrace

**Error:** Aircraft spent 12.0s at a stuck diagonal heading.
**Airport/Runway:** SFO 28R, C172 (piston)
**Symptom:** After reaching on-runway node, aircraft can't turn to runway heading.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)

---

## 9. CvaPatternEntryVeerTests.CvaAfterErd_AircraftDoesNotVeerAway

**Error:** Aircraft heading 112° is 123° away from runway bearing 235°.
**Airport/Runway:** CVA (approach, not ground)
**Symptom:** Aircraft veers away from runway after visual approach pattern entry.

**Investigation:**
- (none yet)

**Action:**
- [ ] (none yet)
