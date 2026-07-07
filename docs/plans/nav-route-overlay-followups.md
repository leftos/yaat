# "Show nav route" overlay — active-procedure geometry (shipped) + remaining limitations

The radar **Display → Show nav route** overlay draws the exact lateral path an aircraft flies from
server-provided positions (DME/RF arcs, custom fixes, FRD fixes) with per-fix crossing restrictions.
On top of the flat route, the server now projects the **active procedure phase's geometry**
(`NavRouteOverlayProjector` → `AircraftStateDto.NavRouteShapes` → client render) so the overlay also
shows paths the flat `NavRouteFixDto` route can't express:

- **Holding patterns** (`HoldingPatternPhase`) → a racetrack (inbound/outbound legs + the two 180°
  turns, sized from the holding-speed turn radius and leg length, on the correct maneuvering side).
- **Procedure turns** (`ProcedureTurnPhase`) → outbound leg, 45° barb, 180° reversal, and the return
  leg intercepting the inbound course, labeled `PT` with the minimum altitude.
- **Departure procedures** (`DepartureProcedurePhase`) → the open-ended coded legs (CA/VA/CD/VD/CR/VR)
  as chained dashed vectors, each labeled with its climb-to (at-or-above) or crossing restriction —
  including the KOAK COAST9 CD altitude window that was previously invisible.

## Remaining limitations (future work)

1. **Coded-leg vectors are approximate.** Open-ended legs (climb-to-altitude, fly-to-DME) draw a
   fixed nominal-length vector along the leg course, chained anchor-to-anchor — the direction and
   restriction are right, the exact endpoint (a dynamic altitude/distance condition) is not.
2. **Only the three phase types above are projected.** A procedure hold that lives only as a fix in
   `NavigationRoute` (not an active `HoldingPatternPhase`), and other hold-like phases
   (`AirspaceBoundaryHoldPhase`, `VfrHoldPhase`), draw just the fix, not a racetrack. Add cases to
   `NavRouteOverlayProjector.BuildShapes` if those become worth drawing.
3. **Published vs. active restriction authority.** The overlay shows published SID/STAR restrictions
   the same as controller-issued CFIX ones. Per **AIM 5-2-9** / **5-4-1**, published crossings only
   bind under an active "climb via" / "descend via" clearance, and **AIM 5-2-9.10** cancels published
   SID *altitude* restrictions once ATC issues an altitude (speed/lateral survive); the exception is
   ODPs (**AIM 5-2-9.11**), whose crossings can't be canceled. A future refinement could visually
   distinguish *active* from *latent published* restrictions — the overlay can't infer clearance
   state from CIFP data alone.
