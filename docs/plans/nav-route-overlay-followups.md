# "Show nav route" overlay — deferred follow-ups

The radar **Display → Show nav route** overlay (renamed from "Show flight path") now draws the
exact lateral path an aircraft flies from server-provided positions — DME/RF arcs, custom fixes,
and FRD fixes included — and labels each fix with its crossing altitude/speed restriction (CFIX or
procedure-published), using the ≥/≤ FL-aware convention. An aviation audit (FAA 7110.65 / AIM,
ARINC 424) surfaced three deeper, **pre-existing** fidelity gaps that were consciously deferred.
None of these block the shipped feature; they are enhancements.

## 1. Fix-less leg restrictions are invisible (CD/VD/CA/VA) — largest

`DepartureClearanceHandler.ResolveLegsToTargets` only emits a `NavigationTarget` for a leg that has
a resolvable `FixIdentifier`. Legs terminated by a **condition** rather than a fix — CD/VD
(course/heading→DME distance), CA/VA (course/heading→altitude), CR/VR (→radial) — carry no fix, so
they never enter `NavigationRoute`, and any restriction coded on them is dropped from the overlay.
The canonical case is **KOAK COAST9** leg 1: a CD carrying an altitude *window* ("OAK 4 DME between
1400–2000") — the exact example in `CifpModels.cs`'s own doc comment. The aircraft *does* honor it
(the control law flies these from `ProcedureLegResolver.Resolve`, which already models CD/VD/CA/VA
with restrictions as typed `ProcedureLeg`s), but the overlay omits the constraint.

**Fix sketch:** project the phase system's `ProcedureLegs` (or cross-reference them) to the client
and synthesize a labeled marker at each fix-less leg's computed endpoint. Needs a new wire channel
or an additive synthetic-fix projection. Aviation-validated as the highest-value gap.

## 2. Holds draw only the fix, not the racetrack (HM/HF/HA)

A procedure hold adds only the hold fix (marked `IsFlyOver`); the racetrack (inbound course, turn
direction, leg length/time) is not drawn, so a held aircraft visibly orbits while the overlay shows
a bare fix. All the data exists on the leg (`OutboundCourse`, `TurnDirection`, `LegDistanceNm`).
Moderate rendering work.

## 3. Procedure turns are skipped (PI)

`ResolveLegsToTargets` explicitly skips PI legs, so the 45°/180° barb or teardrop reversal is not
drawn. Approach-only; the aircraft's actual maneuver still shows in track history. Low–moderate.

## Documented simplification (not a bug): published vs. active restriction authority

The overlay shows published SID/STAR restrictions the same as controller-issued CFIX ones (the
user's explicit choice). Per **AIM 5-2-9** / **5-4-1**, published crossings only bind under an
active "climb via" / "descend via" clearance, and **AIM 5-2-9.10** cancels published SID altitude
restrictions once ATC issues an altitude (speed/lateral survive). The exception is ODPs
(**AIM 5-2-9.11**), whose crossings cannot be canceled. A future refinement could visually
distinguish *active* (CFIX, or published-under-a-via-clearance) from *latent published*
restrictions — the overlay can't infer clearance state from CIFP data alone.
