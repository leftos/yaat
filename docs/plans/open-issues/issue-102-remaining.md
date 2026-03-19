# Issue #102 — Remaining: GS descent timing + intercept distance recording

The InterceptCoursePhase localizer capture fix is done (turn anticipation, speed anticipation, auto-scheduler fix). Two related issues remain from the same testing session.

## 1. FinalApproachPhase starts GS descent too late when aircraft is high

**Observed:** SWA1850 enters FinalApproachPhase at 3474ft / 6.8nm from threshold. Glideslope altitude at 6.8nm ≈ 2170ft — aircraft is ~1300ft above the glideslope. The descent rate scale factor (capped at 1.5×) isn't aggressive enough to catch up, and the aircraft crosses the threshold at ~750ft AGL instead of near 0ft.

**Root cause:** FinalApproachPhase computes descent rate as `standardFpm × scale` where scale = `clamp(1.0 + deviation/1000, 0.5, 1.5)`. At 1300ft high, scale = 1.5× — but that's still not enough to converge before the threshold at 140-180kts. The aircraft also doesn't go around when it reaches the DA/MAP while still excessively above the glideslope.

**Two sub-issues:**
- [ ] Increase the descent rate scaling for large GS deviations, or use a proportional-derivative controller that targets GS convergence by a specific distance
- [ ] Add a go-around trigger when the aircraft is above DA at the MAP (missed approach point). Currently the only auto go-around is for "no landing clearance at 0.5nm" — there's no altitude check. Need to determine DA from CIFP data (or use a default like 200ft AGL for ILS) and trigger go-around if `altitude > DA + margin` at the MAP distance

**Key files:**
- `src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs` — descent rate calc (line ~129-139), go-around logic (line ~152)
- `src/Yaat.Sim/Phases/ApproachClearance.cs` — needs DA field from CIFP data
- `src/Yaat.Sim/Data/Vnas/CifpModels.cs` — may need DA/MDA parsed from approach procedure

## 2. Illegal intercept warning fires at wrong distance

**Observed:** Aircraft legally crosses the localizer at 5.7nm but FinalApproachPhase's `CheckInterceptDistance` doesn't consider the aircraft "established" until xte < 0.1nm AND hdgDiff < 15° — which happens at ~4.2nm after the heading correction completes. If the min intercept distance for the runway is between 4.2nm and 5.7nm, a false illegal intercept warning fires.

**Root cause:** `CheckInterceptDistance` (FinalApproachPhase line ~181) uses its own "established" criteria that are stricter than InterceptCoursePhase's capture criteria. The actual capture point (where InterceptCoursePhase handed off) isn't passed to FinalApproachPhase.

**Fix options:**
- [ ] Pass the capture distance from InterceptCoursePhase to FinalApproachPhase (e.g., via a field on the phase or on ApproachClearance)
- [ ] Or relax FinalApproachPhase's "established" check to match the capture criteria (xte within turn radius, heading within 30°) — but this changes the approach score semantics
- [ ] Or record the intercept distance at InterceptCoursePhase capture time and skip FinalApproachPhase's check entirely when an intercept distance is already recorded

**Key files:**
- `src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs` — `CheckInterceptDistance()` (line ~181)
- `src/Yaat.Sim/Phases/Approach/InterceptCoursePhase.cs` — capture point is known here
- `src/Yaat.Sim/Phases/ApproachClearance.cs` — could carry intercept distance
