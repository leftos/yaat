# Teardrop + 45° re-entry after wrong-side midfield crossing

## Background (from aviation-sim-expert review of the 45°-entry work)

When an aircraft is on the wrong side of the runway for its assigned pattern (e.g. south of KOAK 28R with a right pattern), `PatternCommandHandler.TryEnterPattern` inserts a `MidfieldCrossingPhase` that crosses the field at **pattern altitude + 500 ft**, then drops directly into `DownwindPhase`. This skips the descending teardrop and 45° re-entry that AIM 4-3-3 / AC 90-66B §9.4 prescribe.

Concretely, YAAT currently produces:

```
[MidfieldCrossingPhase]  (crosses to mid-downwind at TPA+500)
    → [DownwindPhase]    (descends from TPA+500 to TPA mid-leg, continues to base turn)
```

AIM/AC 90-66B expects:

```
[MidfieldCrossing at TPA+500]
    → [Continue outbound on pattern side, past the downwind leg]
    → [Descending 180° teardrop turn]
    → [Roll out on 45° entry course at TPA]
    → [Join downwind at abeam at TPA]
    → [DownwindPhase takes over]
```

The mechanical issue with today's behavior: aircraft join downwind abeam the midfield but 500 ft above TPA, so the descent happens during the pattern turn-to-base window instead of before downwind entry. Real pilots don't do this.

## References

- **AIM 4-3-3** (in repo: `.claude/reference/faa/aim/chap04_sec03.md:43-99`) — traffic-pattern entry altitudes. Paragraph 1.b: large/turbine-powered aircraft enter at TPA+500. Also references AC 90-66.
- **AC 90-66 (latest revision)** — Non-Towered Airport Flight Operations. Not in repo. §9.4 describes the midfield crossover teardrop procedure. **Implementer should source this before coding** — current plan is drafted from aviation-sim-expert's citation plus general training-literature knowledge.

## Suspected code

- `src/Yaat.Sim/Phases/Pattern/MidfieldCrossingPhase.cs` — the whole file. Currently targets the midpoint of the downwind leg (midway between `DownwindStartLat/Lon` and `DownwindAbeamLat/Lon`) at `PatternAltitude + 500` ft. Completes at 0.5 nm.
- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:256-260` — where `MidfieldCrossingPhase` is inserted before the circuit phases. No `PatternEntryPhase` is inserted in the wrong-side branch today (the circuit starts immediately with `DownwindPhase` at abeam).
- `src/Yaat.Sim/Phases/PatternGeometry.cs:115-186` — `PatternWaypoints.Compute`. Provides the waypoints the new phase will need. May need an added `TeardropAnchorLat/Lon` or the phase can compute it inline.

## Proposed geometry

Let `D_abeam` be the midfield abeam point on the downwind leg, `H_downwind` the downwind heading (runway reciprocal), and `H_crosswind` the crosswind heading (pattern-side perpendicular to the runway — already in `PatternWaypoints.CrosswindHeading`).

1. **Crossover target** (unchanged from today, but re-examined): cross the runway to the pattern side at TPA+500 ft. Target the **midfield abeam point `D_abeam`** rather than the mid-downwind point, so the aircraft reaches the abeam at TPA+500 before continuing outbound. *(Today's code targets the mid-downwind point; this change keeps everything keyed to the same reference used by the rest of the pattern code.)*
2. **Outbound leg**: from the crossover (abeam at TPA+500), continue outbound on `H_upwind` (i.e., opposite of downwind) parallel to the runway, on the pattern side, for `L_outbound`. `L_outbound` is the distance needed to set up a descending teardrop that rolls out on the 45° entry course. For a piston turning at standard rate, typical radius is ~0.3 nm; for a jet ~0.7 nm. Use `L_outbound = 2 × turn_radius` as a first cut.
3. **Teardrop turn**: a descending 225° turn (135° past the outbound reciprocal, so the roll-out is on the 45° entry course rather than straight back on downwind). Direction: **away** from the runway first, **into** the pattern at the bottom of the turn. Descend from TPA+500 to TPA during the turn.
4. **45° entry leg**: inbound on the 45° entry course (`H_downwind + 45°` for right pattern, `H_downwind − 45°` for left — same rule as the 45° lead-in for normal ERD/ELD). Roll out already at TPA. Intercept downwind at `D_abeam`.
5. **Downwind**: normal `DownwindPhase` starting at abeam at TPA.

Geometry uses only existing helpers (`GeoMath.ProjectPoint`, `GeoMath.BearingTo`, standard-rate turn radius from `AircraftPerformance`).

## Implementation approach (recommended: new phase)

Two options, of which Option A is cleaner:

**Option A (recommended)** — new phase `TeardropReentryPhase` inserted between `MidfieldCrossingPhase` and the circuit phases. `MidfieldCrossingPhase` stays responsible only for the lateral crossover (it already does this well); the new phase owns the outbound leg, descending teardrop, and 45° intercept. Matches the single-responsibility pattern of existing phases.

**Option B** — extend `MidfieldCrossingPhase` into a multi-state phase (crossover → outbound → teardrop → 45°). Keeps the wrong-side flow as one phase but violates the existing "one concern per phase" convention.

### Files to modify (Option A)

- `src/Yaat.Sim/Phases/Pattern/TeardropReentryPhase.cs` — **new**. State machine: Outbound → Teardrop → InterceptDownwind → Complete. Writes `ControlTargets.TargetTrueHeading`, `TargetAltitude`, and a short `NavigationRoute` (teardrop anchor + 45° intercept point) per state. Completes when within ~0.3 nm of `D_abeam` and heading within ~15° of downwind.
- `src/Yaat.Sim/Commands/PatternCommandHandler.cs:256-260` — insert `TeardropReentryPhase` after `MidfieldCrossingPhase` and before `BuildCircuit` phases, only on the wrong-side path. Pass the waypoints record through.
- `src/Yaat.Sim/Phases/MidfieldCrossingPhase.cs:27-56` — re-target the crossover to `DownwindAbeamLat/Lon` (not the mid-downwind point). Small change, but needed so the handoff to `TeardropReentryPhase` starts from a known point.
- `src/Yaat.Sim/Phases/PatternGeometry.cs` — no mandatory change. `TeardropReentryPhase` can compute the outbound anchor and 45° intercept point itself from existing waypoint fields. If the math shows up in more than one place later, promote it to `PatternGeometry` as a static helper.

### Phase sequence after the change

```
Wrong-side:
[MidfieldCrossingPhase]     → cross to D_abeam at TPA+500
[TeardropReentryPhase]      → outbound leg + descending teardrop + 45° intercept to TPA
[DownwindPhase]             → from D_abeam at TPA, continue to base turn
[BasePhase]
[FinalApproachPhase]
[LandingPhase]
```

## Open questions (resolve before coding)

1. **Category carve-out.** AIM 4-3-3.1.b only *requires* TPA+500 for large/turbine. Small pistons may legally enter direct at TPA via the normal 45° from the correct side after a simple midfield crossing at TPA. Should pistons skip the teardrop entirely (direct midfield-crossing → 45° at TPA), reserving the teardrop for turboprop/jet? Recommend asking the user — simpler pattern for pistons is both realistic and less computationally involved.
2. **Turn direction at the teardrop.** Pattern direction determines it (right pattern → teardrop turn to the right, away from runway then back), but this should be documented in the phase and verified with a diagram before coding.
3. **Descent rate.** Descend linearly across the outbound+teardrop+45° segment (so aircraft reaches TPA at the 45° rollout), or descend during the teardrop only? Linear is simpler; teardrop-only is more realistic. Linear is the recommended first cut.
4. **Is AC 90-66B §9.4 available to the implementer?** If not, implementer should either stop and request the document, or use the expert's description plus conservative defaults and flag uncertainty in code comments.

## Tests (TDD)

- `WrongSide_Jet_PhaseChain_IncludesTeardrop`: KOAK 28R right pattern, B738 south of field. Assert sequence: `MidfieldCrossingPhase` → `TeardropReentryPhase` → `DownwindPhase`.
- `WrongSide_Turboprop_PhaseChain_IncludesTeardrop`: same with DH8D.
- (If piston carve-out resolved to "skip") `WrongSide_Piston_PhaseChain_NoTeardrop`: C182 same position. Assert sequence: `MidfieldCrossingPhase` → `DownwindPhase` (current behavior).
- `TeardropReentry_EndsAtTpa_AtAbeam`: tick the phase forward (using `PhaseTicker` or equivalent). Assert that on completion the aircraft is within 0.3 nm of `D_abeam` at approximately TPA (within 100 ft) and heading within 15° of downwind.
- `TeardropReentry_RightPattern_TurnsRightAwayFromRunway`: assert outbound anchor is on pattern side; teardrop turn is in the correct direction.
- Mirror for left pattern.

## Acceptance criteria

- Wrong-side large/turbine aircraft issued `ERD`/`ELD` enter downwind at TPA at the abeam point, having descended during a visible teardrop on the pattern side — not at TPA+500 mid-downwind.
- Pattern phase sequence is observable in the simulation (visible to instructor/RPO via phase trace in snapshots).
- No regression in correct-side ERD/ELD behavior (covered by existing tests).
- No regression in the 45°-entry lead-in selection for correct-side aircraft.

## Out of scope

- Changes to ERC/ELC, ERB/ELB, EF.
- Per-aircraft wind-drift correction during the teardrop (the existing `WindInterpolator` / `WindCorrectionAngle` machinery applies automatically via `FlightPhysics.UpdateNavigation`).
- Visualization on the client (the existing phase snapshot will carry the new phase type — client rendering can be added separately if desired).
