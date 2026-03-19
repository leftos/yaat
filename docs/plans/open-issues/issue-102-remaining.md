# Issue #102 — Remaining: GS descent timing + intercept distance recording

The InterceptCoursePhase localizer capture fix is done (turn anticipation, speed anticipation, auto-scheduler fix). Two related issues remain from the same testing session.

## 1. FinalApproachPhase starts GS descent too late when aircraft is high

**Observed:** SWA1850 enters FinalApproachPhase at 3474ft / 6.8nm from threshold. Glideslope altitude at 6.8nm ≈ 2170ft — aircraft is ~1300ft above the glideslope. The descent rate scale factor (capped at 1.5×) isn't aggressive enough to catch up, and the aircraft crosses the threshold at ~750ft AGL instead of near 0ft.

**Root cause:** FinalApproachPhase computes descent rate as `standardFpm × scale` where scale = `clamp(1.0 + deviation/1000, 0.5, 1.5)`. At 1300ft high, scale = 1.5× — but that's still not enough to converge before the threshold at 140-180kts. The aircraft also doesn't go around when it reaches the DA/MAP while still excessively above the glideslope.

**Two sub-issues:**
- [x] Increase the descent rate scaling for large GS deviations — replaced linear scaling with geometry-based convergence that targets a convergence point ahead of the aircraft
- [x] Add a go-around trigger when the aircraft is too high at the MAP — uses MapAltitudeFt from CIFP MAHP leg (DA for precision, MDA for non-precision), falls back to threshold + 200ft for visual approaches

**Key files:**
- `src/Yaat.Sim/Phases/Tower/FinalApproachPhase.cs` — geometry-based descent rate, go-around trigger
- `src/Yaat.Sim/Phases/ApproachClearance.cs` — MapAltitudeFt field
- `src/Yaat.Sim/Commands/ApproachCommandHandler.cs` — ExtractMapAltitude helper, set at all clearance sites

## 2. Illegal intercept warning fires at wrong distance

**Observed:** Aircraft legally crosses the localizer at 5.7nm but FinalApproachPhase's `CheckInterceptDistance` doesn't consider the aircraft "established" until xte < 0.1nm AND hdgDiff < 15° — which happens at ~4.2nm after the heading correction completes. If the min intercept distance for the runway is between 4.2nm and 5.7nm, a false illegal intercept warning fires.

**Root cause:** `CheckInterceptDistance` (FinalApproachPhase) uses its own "established" criteria that are stricter than InterceptCoursePhase's capture criteria. The actual capture point (where InterceptCoursePhase handed off) isn't passed to FinalApproachPhase.

**Fix:**
- [x] Record capture distance from InterceptCoursePhase to ApproachClearance.InterceptCaptureDistanceNm
- [x] Move illegal intercept warning to InterceptCoursePhase.Capture() — fires at actual intercept, not at later establishment
- [x] FinalApproachPhase.CheckInterceptDistance() uses capture distance for score legality but no longer fires warnings
