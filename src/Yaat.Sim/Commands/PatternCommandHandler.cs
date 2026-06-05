using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Commands;

internal static class PatternCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("PatternCommandHandler");

    // Falls back through assigned runway → filed destination → spawn-time airport
    // context. The last fallback covers VFR cold-call aircraft that have neither
    // a flight plan filed nor a runway assignment yet.
    private static string ResolveAirportContext(AircraftState aircraft)
    {
        var assigned = aircraft.Phases?.AssignedRunway?.AirportId;
        if (!string.IsNullOrEmpty(assigned))
        {
            return assigned;
        }

        if (!string.IsNullOrEmpty(aircraft.FlightPlan.Destination))
        {
            return aircraft.FlightPlan.Destination;
        }

        return aircraft.AirportId;
    }

    internal static CommandResult TryEnterPattern(
        AircraftState aircraft,
        PatternDirection? requestedDirection,
        PatternEntryLeg entryLeg,
        string? runwayId,
        double? finalDistanceNm
    )
    {
        // Pattern entry only makes sense airborne — on the ground it would
        // clobber the taxi/takeoff sequence. Closed-traffic departures should
        // come via CTO MLT/MRT (ApplyClosedTraffic), not via ERD/ELD.
        if (aircraft.IsOnGround)
        {
            return new CommandResult(false, "Pattern entry requires the aircraft to be airborne");
        }

        // Capture state before the runway-resolution block mutates it, so the
        // parallel-runway sidestep detection below can compare current vs target.
        var previousAssignedRunway = aircraft.Phases?.AssignedRunway;
        var previousActivePhase = aircraft.Phases?.CurrentPhase;

        // Resolve runway from argument if provided
        if (runwayId is not null)
        {
            var airportId = ResolveAirportContext(aircraft);
            if (string.IsNullOrEmpty(airportId))
            {
                return new CommandResult(false, "No airport context to resolve runway");
            }

            var resolved = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
            if (resolved is null)
            {
                return new CommandResult(false, $"Runway {runwayId} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            aircraft.Phases.AssignedRunway = resolved;
            NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, resolved.Designator);
        }

        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway for pattern entry");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool touchAndGo = aircraft.Phases.TrafficDirection is not null;

        // EF (Enter Final) doesn't carry an L/R in its verb, so the dispatcher passes
        // null. Defer to the runway's natural pattern direction: 28R with 28L present
        // → Right; 28L with 28R present → Left; single runway → FAA default Left
        // (AIM 4-3-3). Without this, EF on a close parallel like OAK 28R would stamp
        // the wrong side and any subsequent COPT/TouchAndGo or GoAround circuit would
        // fly the downwind over the parallel runway. ELD/ERD/ELB/ERB/ELC/ERC pass an
        // explicit L/R that reflects the controller's verb and bypasses the inference.
        PatternDirection direction = requestedDirection ?? GoAroundHelper.InferDefaultPatternDirection(runway) ?? PatternDirection.Left;

        // Parallel-runway sidestep (7110.65 §4-8-7, AIM §5-4-19). When EF targets a
        // runway parallel to the one the aircraft is currently flying FinalApproach on,
        // retarget the active phase chain instead of rebuilding from a far-away
        // PatternEntry. The aircraft is already established on the parallel localizer
        // + glideslope; the sidestep just shifts the centerline/threshold a few
        // hundred feet laterally.
        if (
            entryLeg == PatternEntryLeg.Final
            && previousAssignedRunway is not null
            && previousActivePhase is FinalApproachPhase finalApproach
            && !string.Equals(previousAssignedRunway.Designator, runway.Designator, StringComparison.OrdinalIgnoreCase)
            && IsParallelSidestepCandidate(previousAssignedRunway, runway)
        )
        {
            // AGL gate: below the stabilized-approach floor (FAA AC 120-71) the aircraft
            // doesn't have enough lateral distance left to capture the new centerline
            // before flare. Real ATC would reject ("unable, go around if needed") and
            // we mirror that.
            double aglFt = aircraft.Altitude - runway.ElevationFt;
            if (aglFt < MinSidestepAglFt)
            {
                return new CommandResult(false, "Unable, too low for sidestep");
            }
            return ApplySidestep(aircraft, finalApproach, runway, category);
        }

        // Detect if the aircraft is on the wrong side of the runway
        bool isOnWrongSide = false;
        PatternWaypoints? waypoints = null;

        if (entryLeg is PatternEntryLeg.Downwind or PatternEntryLeg.Base)
        {
            // Crosswind heading points toward the pattern side
            TrueHeading crosswindHdg = direction == PatternDirection.Right ? runway.TrueHeading + 90.0 : runway.TrueHeading - 90.0;

            // Positive = pattern side, negative = wrong side
            double patternSideOffset = GeoMath.AlongTrackDistanceNm(
                aircraft.Position,
                new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                crosswindHdg
            );

            isOnWrongSide = patternSideOffset < 0;

            if (isOnWrongSide)
            {
                var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
                var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                    runway,
                    aircraft.Ground.Layout?.FindRunway(runway.Designator),
                    aircraft.Pattern.SizeOverrideNm,
                    aircraft.Pattern.AltitudeOverrideFt
                );
                waypoints = PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
            }
        }

        // For Final entry: detect "close-in aligned" — the aircraft is already
        // inside the standard glideslope-TPA intercept distance and reasonably
        // aligned with the new FAC. The standard entry point would be behind
        // the aircraft, forcing a teardrop / 360. Instead, anchor the entry at
        // the aircraft's current along-track on the new centerline (mirrors the
        // ERB-no-distance precedent below for Base entry). When this engages,
        // the loop check below is irrelevant — it validates maneuverability for
        // the standard far entry, not the close-in override.
        bool isCloseInFinal = false;
        double closeInAlongTrack = 0.0;
        if (!aircraft.IsOnGround && entryLeg == PatternEntryLeg.Final && finalDistanceNm is null)
        {
            var airportRunwaysCi = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOvCi, altOvCi) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                aircraft.Pattern.SizeOverrideNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            waypoints ??= PatternGeometry.Compute(runway, category, direction, sizeOvCi, altOvCi, airportRunwaysCi);

            TrueHeading reciprocalCi = waypoints.FinalHeading.ToReciprocal();
            double alongTrackOutboundCi = GeoMath.AlongTrackDistanceNm(
                aircraft.Position,
                new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                reciprocalCi
            );
            double standardEntryDistNm =
                CategoryPerformance.PatternAltitudeAgl(category) / GlideSlopeGeometry.FeetPerNm(GlideSlopeGeometry.AngleForCategory(category));
            double angleOffDeg = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);

            // Engage only in the "sweet spot": aircraft is on the approach side,
            // inside the standard intercept, and roughly aligned. Outside that
            // window the standard far-entry / loop-check path is what we want.
            //
            // Inside the window we still need two safety gates:
            //   - alongTrack ≥ MinimumPerpendicularBaseFinalDistanceNm: below this
            //     a stable final segment can't be established from the override.
            //   - altitude must be feasible to descend over the path (mirrors the
            //     ERB altitude check below; uses approach speed because this is a
            //     final-leg path, not a base-leg path).
            //
            // When either safety gate fails we deliberately FALL THROUGH to the
            // existing loop check rather than rejecting from here. The loop check
            // and downstream FinalApproachPhase already handle these geometries
            // (either by rejecting "short final" or by routing the aircraft via
            // a far entry point that the FinalApproachPhase auto-go-around can
            // recover). Rejecting here would leave the aircraft with no phases,
            // since the phase-clear at line 228 happens after our early returns.
            if (
                alongTrackOutboundCi > 0
                && alongTrackOutboundCi < standardEntryDistNm
                && angleOffDeg <= MaxCloseInFinalAngleOffDeg(alongTrackOutboundCi, category)
                && alongTrackOutboundCi >= MinimumPerpendicularBaseFinalDistanceNm(category)
            )
            {
                double crossTrackAbsNmCi = Math.Abs(
                    GeoMath.SignedCrossTrackDistanceNm(
                        aircraft.Position,
                        new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                        waypoints.FinalHeading
                    )
                );
                double totalPathNmCi = crossTrackAbsNmCi + alongTrackOutboundCi;
                double approachSpeedKt = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category);
                double pathMinutesCi = totalPathNmCi / (approachSpeedKt / 60.0);
                double maxDescentFtCi = CategoryPerformance.PatternDescentRate(category) * pathMinutesCi;
                double altitudeToLoseFtCi = aircraft.Altitude - runway.ElevationFt;
                if (altitudeToLoseFtCi <= maxDescentFtCi)
                {
                    isCloseInFinal = true;
                    closeInAlongTrack = alongTrackOutboundCi;
                }
            }
        }

        // For a Final entry with no explicit distance on a DIAGONAL (misaligned) join
        // that isn't the close-in case: "make straight-in". The aircraft descends
        // immediately on the diagonal cut-in toward the runway and joins final as CLOSE
        // to the threshold as it can while still reaching the glideslope by the join — a
        // shortcut, not a fixed base. A low aircraft shortcuts to the minimum final; a
        // higher one (which needs more descent room) joins farther out. Capped at the
        // aircraft's along-track so EF never routes it outbound / farther from the field.
        // Aligned aircraft (within the close-in angle envelope) keep the existing
        // fixed-distance / close-in behavior — they already cut in at a shallow angle. When
        // the aircraft is too high to lose its altitude on the diagonal even at the capped
        // join, a controller-facing warning is raised below (the command still succeeds).
        double? finalEntryDistanceNm = null;
        bool straightInTooHigh = false;
        if (!aircraft.IsOnGround && entryLeg == PatternEntryLeg.Final && finalDistanceNm is null && !isCloseInFinal)
        {
            var airportRunwaysAa = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOvAa, altOvAa) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                aircraft.Pattern.SizeOverrideNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            waypoints ??= PatternGeometry.Compute(runway, category, direction, sizeOvAa, altOvAa, airportRunwaysAa);

            double alongTrackOutboundAa = GeoMath.AlongTrackDistanceNm(
                aircraft.Position,
                new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                waypoints.FinalHeading.ToReciprocal()
            );
            double angleOffDegAa = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);
            if (alongTrackOutboundAa > 0 && angleOffDegAa > MaxCloseInFinalAngleOffDeg(alongTrackOutboundAa, category))
            {
                // Returns null when the capped join is inside the category minimum final.
                // finalEntryDistanceNm then stays null and the loop check below handles the
                // geometry as before — this path only changes a diagonal join the aircraft
                // has room for. (An explicit reject for the inside-minimum case diverged
                // several recordings where an aircraft gets EF close-in/diagonal — e.g. a
                // re-clearance on short final — that the fixed fallback already absorbs.)
                finalEntryDistanceNm = ComputeAltitudeAwareFinalEntryDistanceNm(
                    aircraft,
                    runway,
                    category,
                    waypoints,
                    alongTrackOutboundAa,
                    out straightInTooHigh
                );
            }
        }

        // For Final entry: reject if the maneuver would create a 360° loop. Skipped when
        // the altitude-aware join is in play (finalEntryDistanceNm) — that entry is, by
        // construction, never placed beyond the aircraft's along-track, so the reverse-to-
        // a-far-entry loop this guards against cannot form.
        // Compute the two heading changes: (1) turn from current heading to the
        // bearing toward the entry point, (2) turn from arrival bearing at the
        // entry point to the runway (approach) heading. If BOTH exceed 90°, the
        // total turn approaches 360° — the aircraft must reverse to the entry
        // point and then reverse again to align with final. That's a loop.
        if (!aircraft.IsOnGround && entryLeg == PatternEntryLeg.Final && !isCloseInFinal && finalEntryDistanceNm is null)
        {
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                aircraft.Pattern.SizeOverrideNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            waypoints ??= PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
            var (eLat, eLon) = GetEntryPoint(waypoints, PatternEntryLeg.Final, finalDistanceNm, category);

            double bearingToEntry = GeoMath.BearingTo(aircraft.Position, new LatLon(eLat, eLon));
            double turnToEntry = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearingToEntry));
            double turnAtEntry = new TrueHeading(bearingToEntry).AbsAngleTo(runway.TrueHeading);

            // The aircraft can only execute high-angle turns if it has enough
            // room. Compute the minimum distance needed for the combined turn
            // using standard-rate geometry: arc length = turn_angle × turn_radius.
            // With standard 3°/s turns at the aircraft's current speed, the
            // turn radius is V²/(g·tan(bank)) ≈ V/ω where ω = 3°/s.
            // Simplification: use the distance to the entry point vs the arc
            // distance required. If the straight-line distance is less than
            // the arc needed, the maneuver is infeasible (loop).
            double distToEntry = GeoMath.DistanceNm(aircraft.Position, new LatLon(eLat, eLon));

            // Turn radius model depends on flight rules. IFR keeps the
            // conservative standard-rate envelope (3°/s on IAS); VFR uses a
            // pattern-rate envelope on groundspeed so light/slow VFR traffic
            // can accept the tighter turns it actually flies in the pattern.
            // Precedent for IsVfr branching: FinalApproachPhase.cs:367.
            double turnRateDegs;
            double speedKts;
            if (aircraft.FlightPlan.IsVfr)
            {
                // 12°/s ≈ 25° bank at 90 kt — AIM medium-bank ceiling for
                // traffic-pattern maneuvering (AIM 4-3-2/3 + FAA Airplane
                // Flying Handbook pattern-turn guidance). Groundspeed is
                // wind-corrected, floored at 60 kt so a light single on a
                // strong headwind isn't granted an unrealistically tight
                // radius.
                turnRateDegs = 12.0;
                speedKts = Math.Max(aircraft.GroundSpeed, 60);
            }
            else
            {
                turnRateDegs = 3.0;
                speedKts = Math.Max(aircraft.IndicatedAirspeed, 80);
            }
            double radiusNm = (speedKts / 3600.0) / (turnRateDegs * Math.PI / 180.0);

            // Arc distance for both turns combined
            double totalTurnDeg = turnToEntry + turnAtEntry;
            double arcNm = (totalTurnDeg * Math.PI / 180.0) * radiusNm;

            Log.LogDebug(
                "[EF-LoopCheck] {Callsign}: vfr={IsVfr}, hdg={Hdg:F0}, brg→entry={Brg:F0}, turnToEntry={T1:F0}°, turnAtEntry={T2:F0}°, spd={Spd:F0}kt, rate={Rate:F0}°/s, r={R:F2}nm, dist={Dist:F1}nm, arc={Arc:F1}nm",
                aircraft.Callsign,
                aircraft.FlightPlan.IsVfr,
                aircraft.TrueHeading.Degrees,
                bearingToEntry,
                turnToEntry,
                turnAtEntry,
                speedKts,
                turnRateDegs,
                radiusNm,
                distToEntry,
                arcNm
            );

            // Reject if the required arc exceeds what the aircraft can fly in
            // the available straight-line distance. The aircraft physically
            // cannot complete the turns before reaching the entry point.
            if ((totalTurnDeg > 180) && (arcNm > distToEntry))
            {
                return new CommandResult(false, "Unable, short final");
            }
        }

        // Clear current phases and build new sequence from entry leg
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Clear(ctx);

        // For wrong-side entry: midfield crossing then downwind entry
        PatternEntryLeg effectiveEntryLeg = isOnWrongSide ? PatternEntryLeg.Downwind : entryLeg;
        double? effectiveFinalDistanceNm = isCloseInFinal ? closeInAlongTrack : (isOnWrongSide ? null : finalDistanceNm);

        // If the aircraft is airborne, heading roughly aligned with the runway,
        // near the runway (within 3nm of departure end), and NOT already close to
        // the downwind entry, override to upwind entry. This handles the go-around
        // case where the pilot should fly upwind→crosswind→downwind instead of
        // navigating directly to the downwind abeam point at the wrong heading.
        if (!aircraft.IsOnGround && !isOnWrongSide && effectiveEntryLeg == PatternEntryLeg.Downwind && waypoints is not null)
        {
            double hdgDiff = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);
            double distToDepEnd = GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.EndLatitude, runway.EndLongitude));
            double distToDownwindEntry = GeoMath.DistanceNm(aircraft.Position, new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon));
            if (hdgDiff < 30 && distToDepEnd < 3.0 && distToDownwindEntry > 1.0)
            {
                effectiveEntryLeg = PatternEntryLeg.Upwind;
            }
        }

        // Compute waypoints for the entry point check
        {
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                aircraft.Pattern.SizeOverrideNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            waypoints ??= PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
        }

        // ERB/ELB without a distance argument: derive FinalDistanceNm from the
        // aircraft's current along-track projection onto the extended centerline.
        // This makes BasePhase turn onto final at the aircraft's present position
        // along centerline, yielding a perpendicular base leg whose length equals
        // the current cross-track. "Enter base from present distance" — rather
        // than aiming for the standard pattern base turn point (which would force
        // a diagonal leg). Setting useAircraftPositionAsEntry skips PatternEntryPhase
        // below so BasePhase starts immediately from the aircraft's current position.
        bool useAircraftPositionAsEntry = isCloseInFinal;
        if (!aircraft.IsOnGround && !isOnWrongSide && effectiveEntryLeg == PatternEntryLeg.Base && effectiveFinalDistanceNm is null)
        {
            TrueHeading reciprocal = waypoints.FinalHeading.ToReciprocal();
            double alongTrackOutbound = GeoMath.AlongTrackDistanceNm(
                aircraft.Position,
                new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                reciprocal
            );
            if (alongTrackOutbound < MinimumPerpendicularBaseFinalDistanceNm(category))
            {
                return new CommandResult(false, "Unable, too close for base");
            }

            // Altitude feasibility: can the aircraft descend from its current
            // altitude to runway elevation within the base + final path at
            // category pattern descent rate and base speed? Controllers issuing
            // "enter base" to an aircraft well above TPA should descend it first
            // (AIM 4-3-3: pattern entry at TPA). Rejecting here prompts a DM.
            double crossTrackAbsNm = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    aircraft.Position,
                    new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                    waypoints.FinalHeading
                )
            );
            double totalPathNm = crossTrackAbsNm + alongTrackOutbound;
            double baseSpeedKt = CategoryPerformance.BaseSpeed(category);
            double pathMinutes = totalPathNm / (baseSpeedKt / 60.0);
            double maxDescentFt = CategoryPerformance.PatternDescentRate(category) * pathMinutes;
            double altitudeToLoseFt = aircraft.Altitude - runway.ElevationFt;
            if (altitudeToLoseFt > maxDescentFt)
            {
                return new CommandResult(false, "Unable, too high for base");
            }

            effectiveFinalDistanceNm = alongTrackOutbound;
            useAircraftPositionAsEntry = true;
        }

        var (circuitSizeOv, circuitAltOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            aircraft.Ground.Layout?.FindRunway(runway.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );
        var circuitPhases = PatternBuilder.BuildCircuit(
            runway,
            category,
            direction,
            effectiveEntryLeg,
            touchAndGo,
            effectiveFinalDistanceNm,
            circuitSizeOv,
            circuitAltOv,
            NavigationDatabase.Instance.GetRunways(runway.AirportId)
        );

        var phases = new PhaseList { AssignedRunway = runway };
        NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, runway.Designator);
        phases.LandingClearance = aircraft.Phases.LandingClearance;
        phases.ClearedRunwayId = aircraft.Phases.ClearedRunwayId;
        // Stamp the commanded pattern direction so a subsequent go-around preserves
        // it (GoAroundHelper otherwise defaults VFR to Left, regardless of the
        // ERD/ELB/ERB/ELD intent). Applies to wrong-side entries too — the
        // controller's clearance still names the side the pilot must rejoin on.
        phases.TrafficDirection = direction;

        // If the aircraft is airborne and far from the pattern, insert a PatternEntryPhase
        // to navigate to the entry point with descent/climb to pattern altitude.
        // For downwind entry, add a lead-in waypoint so the aircraft aligns with the
        // downwind heading before reaching the abeam point.
        if (!aircraft.IsOnGround && !isOnWrongSide)
        {
            var (entryLat, entryLon) = useAircraftPositionAsEntry
                ? (aircraft.Position.Lat, aircraft.Position.Lon)
                : GetEntryPoint(waypoints, effectiveEntryLeg, effectiveFinalDistanceNm ?? finalEntryDistanceNm, category);
            double distToEntry = GeoMath.DistanceNm(aircraft.Position, new LatLon(entryLat, entryLon));

            if (distToEntry > 1.0)
            {
                double? leadInLat = null;
                double? leadInLon = null;

                // For downwind entry: choose between a 45° midfield intercept (AIM 4-3-3)
                // and a straight-in join to the extended downwind leg. Score each by the
                // total heading change required along aircraft → lead-in → abeam → downwind,
                // with a penalty on any single turn >120° (discourages U-turns / looping past
                // the field).
                if (effectiveEntryLeg == PatternEntryLeg.Downwind)
                {
                    var leadIn = ChooseDownwindLeadIn(aircraft, runway, direction, category, waypoints, entryLat, entryLon);
                    leadInLat = leadIn.Lat;
                    leadInLon = leadIn.Lon;
                }

                // For final entry, target the glideslope altitude at the entry point
                // (not TPA). FinalApproachPhase handles glideslope tracking from there.
                double entryAltitude = waypoints.PatternAltitude;
                if (effectiveEntryLeg == PatternEntryLeg.Final)
                {
                    double entryDist = GeoMath.DistanceNm(entryLat, entryLon, waypoints.ThresholdLat, waypoints.ThresholdLon);
                    double gsAngle = GlideSlopeGeometry.AngleForCategory(category);
                    entryAltitude = GlideSlopeGeometry.AltitudeAtDistance(entryDist, runway.ElevationFt, gsAngle);
                }

                var kind = ClassifyEntryKind(aircraft, runway, direction, effectiveEntryLeg);
                phases.Add(
                    new PatternEntryPhase
                    {
                        EntryLat = entryLat,
                        EntryLon = entryLon,
                        PatternAltitude = entryAltitude,
                        Kind = kind,
                        LeadInLat = leadInLat,
                        LeadInLon = leadInLon,
                    }
                );
            }
        }

        if (isOnWrongSide)
        {
            phases.Add(new MidfieldCrossingPhase { Waypoints = waypoints });

            // Large/turbine cross at TPA+500 per AIM 4-3-3.1.b; they still need to
            // descend to TPA before joining downwind. TeardropReentryPhase does that
            // via an outbound leg + 45° intercept to abeam. Pistons/helicopters already
            // cross at TPA (see MidfieldCrossingPhase) and can drop straight into downwind.
            if (category is AircraftCategory.Jet or AircraftCategory.Turboprop)
            {
                phases.Add(new TeardropReentryPhase { Waypoints = waypoints });
            }
        }

        foreach (var phase in circuitPhases)
        {
            phases.Add(phase);
        }

        aircraft.Phases = phases;

        // EF capped the straight-in join at the aircraft's along-track (it can't be
        // sent outbound), but the aircraft is too high to descend over the remaining
        // cut-in + final path. Surface a controller-facing warning so the RPO can
        // call it out / pick a different approach; the clearance still stands.
        if (straightInTooHigh)
        {
            aircraft.PendingWarnings.Add($"{aircraft.Callsign} unable to descend for straight-in {runway.Designator} — too high");
        }

        var legDesc =
            entryLeg == PatternEntryLeg.Final
                ? "final"
                : $"{(direction == PatternDirection.Left ? "left" : "right")} {entryLeg.ToString().ToLowerInvariant()}";
        var distStr = finalDistanceNm is not null ? $", {finalDistanceNm:G}nm final" : "";
        var sideStr = isOnWrongSide ? " (crossing midfield)" : "";
        return CommandDispatcher.Ok($"Enter {legDesc}{CommandDispatcher.RunwayLabel(aircraft)}{distStr}{sideStr}");
    }

    internal static CommandResult TryChangePatternDirection(
        AircraftState aircraft,
        PatternDirection newDirection,
        string? runwayId,
        int? altitudeOverride
    )
    {
        // Resolve runway from argument if provided
        if (runwayId is not null)
        {
            var airportId = ResolveAirportContext(aircraft);
            if (string.IsNullOrEmpty(airportId))
            {
                return new CommandResult(false, "No airport context to resolve runway");
            }

            var resolved = NavigationDatabase.Instance.GetRunway(airportId, runwayId);
            if (resolved is null)
            {
                return new CommandResult(false, $"Runway {runwayId} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            aircraft.Phases.AssignedRunway = resolved;
            NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, resolved.Designator);
            // Changing runway clears altitude override — different runway, different TPA
            aircraft.Pattern.AltitudeOverrideFt = null;
        }

        // Set explicit altitude override if provided
        if (altitudeOverride is not null)
        {
            aircraft.Pattern.AltitudeOverrideFt = altitudeOverride;
        }

        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            aircraft.Ground.Layout?.FindRunway(runway.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );
        var waypoints = PatternGeometry.Compute(runway, category, newDirection, sizeOv, altOv, airportRunways);

        // Set traffic direction — aircraft is now in pattern mode. Stamp both the
        // transient PhaseList field (current circuit) and the persistent
        // AircraftPattern field (survives FH/TR/TL phase clearing and ERB/ELB
        // single-approach overrides) so future auto-cycles honor the controller
        // intent without requiring a re-issue.
        aircraft.Phases.TrafficDirection = newDirection;
        aircraft.Pattern.TrafficDirection = newDirection;

        // When the aircraft has an Active standard pattern leg, rebuild the
        // chain so the active phase gets a fresh OnStart with the new
        // waypoints. PatternBuilder.UpdateWaypoints only swaps the
        // Waypoints reference on each phase — Crosswind/Upwind/Downwind/Base
        // cache target lat/lon/heading at OnStart and write
        // Targets.TargetTrueHeading once, so an active phase keeps flying to
        // the old target if we don't rebuild. Skips ground / non-pattern-leg
        // phases (Takeoff / FinalApproach / Landing / TouchAndGo /
        // PatternEntry / MidfieldCrossing) — those keep the existing
        // UpdateWaypoints path so a later still-Pending leg picks up the new
        // waypoints in its own OnStart.
        var currentLeg = GetCurrentPatternLeg(aircraft.Phases.CurrentPhase);
        if (!aircraft.IsOnGround && currentLeg is { } activeLeg)
        {
            bool wrongSide = IsOnWrongSideForPattern(aircraft.Position, runway, newDirection);
            var rebuiltChain = new List<Phase>();
            if (wrongSide)
            {
                // Mirror TryEnterPattern's wrong-side path: cross the field
                // and join downwind on the correct (new) pattern side.
                rebuiltChain.Add(new MidfieldCrossingPhase { Waypoints = waypoints });
                rebuiltChain.AddRange(
                    PatternBuilder.BuildCircuit(
                        runway,
                        category,
                        newDirection,
                        PatternEntryLeg.Downwind,
                        touchAndGo: true,
                        finalDistanceNm: null,
                        sizeOv,
                        altOv,
                        airportRunways
                    )
                );
            }
            else
            {
                // Same side: rebuild from the leg the aircraft is currently
                // flying. The new active-phase instance's OnStart rewrites
                // Targets.TargetTrueHeading from the new waypoints.
                rebuiltChain.AddRange(
                    PatternBuilder.BuildCircuit(
                        runway,
                        category,
                        newDirection,
                        activeLeg,
                        touchAndGo: true,
                        finalDistanceNm: null,
                        sizeOv,
                        altOv,
                        airportRunways
                    )
                );
            }

            ApplyRebuiltPatternChain(aircraft, runway, newDirection, rebuiltChain);

            Log.LogDebug(
                "[ChangePatternDirection] {Callsign}: rebuilt chain from {Leg} for {Dir} {Rwy}, wrongSide={WrongSide}",
                aircraft.Callsign,
                activeLeg,
                newDirection,
                runway.Designator,
                wrongSide
            );

            var dirStrRebuild = newDirection == PatternDirection.Left ? "left" : "right";
            return CommandDispatcher.Ok($"Make {dirStrRebuild} traffic{CommandDispatcher.RunwayLabel(aircraft)}");
        }

        // Update waypoints on existing pattern phases
        bool hasPatternPhases = PatternBuilder.UpdateWaypoints(aircraft.Phases, waypoints);

        // If no pattern phases exist yet (e.g., after takeoff or go-around),
        // append a full pattern circuit after the current phase.
        if (!hasPatternPhases)
        {
            var circuit = PatternBuilder.BuildCircuit(
                runway,
                category,
                newDirection,
                PatternEntryLeg.Upwind,
                true,
                null,
                sizeOv,
                altOv,
                airportRunways
            );
            aircraft.Phases.InsertAfterCurrent(circuit);
        }
        else
        {
            // Replace any pending LandingPhase with TouchAndGoPhase
            // since the aircraft is now in pattern mode.
            // Don't override specific approach instructions (SG/LA).
            for (int i = 0; i < aircraft.Phases.Phases.Count; i++)
            {
                if (aircraft.Phases.Phases[i] is LandingPhase { Status: PhaseStatus.Pending })
                {
                    aircraft.Phases.Phases[i] = new TouchAndGoPhase();
                    break;
                }
            }
        }

        var dirStr = newDirection == PatternDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"Make {dirStr} traffic{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    // Aircraft is on the "wrong side" of the runway for the requested pattern
    // direction when its along-track projection onto the crosswind-heading
    // (the bearing perpendicular to the runway pointing toward the pattern
    // side) is negative — i.e. it is on the opposite side of the threshold
    // axis from where the pattern lives. Mirrors TryEnterPattern's local
    // check (kept as the canonical site that drives MidfieldCrossing
    // insertion).
    private static bool IsOnWrongSideForPattern(LatLon position, RunwayInfo runway, PatternDirection direction)
    {
        TrueHeading crosswindHdg = direction == PatternDirection.Right ? runway.TrueHeading + 90.0 : runway.TrueHeading - 90.0;
        double patternSideOffset = GeoMath.AlongTrackDistanceNm(
            position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            crosswindHdg
        );
        return patternSideOffset < 0;
    }

    // Replace the aircraft's phase list with a freshly-built chain, preserving
    // LandingClearance / ClearedRunwayId metadata. Sets DestinationRunway and
    // starts the first phase. Shared by RebuildPatternFromLeg and the MLT/MRT
    // mid-flight rebuild path.
    private static void ApplyRebuiltPatternChain(AircraftState aircraft, RunwayInfo runway, PatternDirection direction, List<Phase> chain)
    {
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases!.Clear(ctx);

        var phases = new PhaseList
        {
            AssignedRunway = runway,
            LandingClearance = aircraft.Phases.LandingClearance,
            ClearedRunwayId = aircraft.Phases.ClearedRunwayId,
            TrafficDirection = direction,
        };
        foreach (var phase in chain)
        {
            phases.Add(phase);
        }

        aircraft.Phases = phases;
        NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, runway.Designator);
        aircraft.Phases.Start(ctx);
    }

    /// <summary>
    /// Advance to the next phase when the current phase is of type T.
    /// Used for TC (skip upwind to crosswind) and TD (skip crosswind to downwind).
    /// </summary>
    internal static CommandResult TryPatternTurnTo<T>(AircraftState aircraft, string legName)
        where T : Phase
    {
        if (aircraft.Phases?.CurrentPhase is T)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.AdvanceToNext(ctx);
            return CommandDispatcher.Ok($"Turn {legName}");
        }

        return new CommandResult(false, $"Not on the leg before {legName}");
    }

    internal static CommandResult TryPatternTurnBase(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is DownwindPhase)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.AdvanceToNext(ctx);
            return CommandDispatcher.Ok("Turn base");
        }

        return new CommandResult(false, "Not on downwind");
    }

    // Bare EXT on downwind models 7110.65 §3-8-1 "EXTEND DOWNWIND" — the only
    // codified extend-leg phraseology. EXT UPWIND / EXT CROSSWIND have no
    // standard FAA phrase; AIM §4-3-3 ¶3.2 authorizes upwind for sequencing,
    // and in real ops the equivalent instructions are "continue runway heading,
    // I'll call your crosswind" or a vector. The leg-arg form maps the
    // training-tool shorthand onto those underlying maneuvers.
    internal static CommandResult TryExtendPattern(AircraftState aircraft, PatternEntryLeg? requestedLeg)
    {
        if (requestedLeg is not { } leg)
        {
            return TryExtendCurrentLeg(aircraft);
        }

        // Defensive guard — parser already filters Base/Final tokens, but the leg
        // arg is a typed enum that can carry any value.
        if (leg is PatternEntryLeg.Base or PatternEntryLeg.Final)
        {
            return new CommandResult(false, $"Extend not allowed on {leg.ToString().ToLowerInvariant()} leg");
        }

        var currentLeg = GetCurrentPatternLeg(aircraft.Phases?.CurrentPhase);
        if (currentLeg is not { } current)
        {
            // EXT UPWIND from a non-pattern-leg phase (HoldingShort, LineUp, Takeoff,
            // InitialClimb, FinalApproach pre-T/G, TouchAndGo): arm the upcoming Upwind
            // either directly in the queue or, if the next circuit hasn't been appended
            // yet, via the AircraftPattern.ExtendNextUpwind one-shot flag. EXT CROSSWIND
            // / EXT DOWNWIND keep the original rejection — scope decision in plan.
            if (leg == PatternEntryLeg.Upwind && TryArmNextUpwind(aircraft) is { } armed)
            {
                return armed;
            }
            return new CommandResult(false, "Extend applies on upwind, crosswind, or downwind");
        }

        if (leg == current)
        {
            return TryExtendCurrentLeg(aircraft);
        }

        int requestedOrder = LegOrder(leg);
        int currentOrder = LegOrder(current);

        if (requestedOrder > currentOrder)
        {
            return new CommandResult(false, $"EXT {leg.ToString().ToUpperInvariant()} cannot be issued before reaching that leg");
        }

        if (currentOrder - requestedOrder > 1)
        {
            return new CommandResult(
                false,
                $"Cannot roll back to {leg.ToString().ToLowerInvariant()} from {current.ToString().ToLowerInvariant()} — use bare EXT or L360/R360 for spacing"
            );
        }

        return RebuildPatternFromLeg(aircraft, leg);
    }

    private static CommandResult TryExtendCurrentLeg(AircraftState aircraft)
    {
        switch (aircraft.Phases?.CurrentPhase)
        {
            case DownwindPhase dw:
                dw.IsExtended = true;
                return CommandDispatcher.Ok("Extend downwind");
            case UpwindPhase uw:
                uw.IsExtended = true;
                return CommandDispatcher.Ok("Extend upwind");
            case CrosswindPhase cw:
                cw.IsExtended = true;
                return CommandDispatcher.Ok("Extend crosswind");
            case BasePhase:
                return new CommandResult(false, "Extend not allowed on base leg");
            default:
                // Bare EXT from a non-pattern-leg phase: same upcoming-Upwind arm path
                // used by EXT UPWIND. This is the common case where the user wants to
                // extend the next upwind during a touch-and-go ground roll.
                if (TryArmNextUpwind(aircraft) is { } armed)
                {
                    return armed;
                }
                return new CommandResult(false, "Extend applies on upwind, crosswind, or downwind");
        }
    }

    /// <summary>
    /// Pre-arm extension on the upcoming Upwind for an aircraft not currently on a pattern
    /// leg. Two layers:
    ///   1. Pending UpwindPhase already in the queue (initial circuit after CTO MRT, or
    ///      already-appended next circuit) → set IsExtended directly.
    ///   2. No pending UpwindPhase but a touch-and-go cycle is in progress (the auto-cycle
    ///      block in PhaseRunner.Tick will append the next circuit when the current cycle
    ///      terminator completes) → set the one-shot AircraftPattern.ExtendNextUpwind flag.
    /// Returns null when no pre-arm is possible (caller falls back to its rejection text).
    /// </summary>
    private static CommandResult? TryArmNextUpwind(AircraftState aircraft)
    {
        if (TryFindNextPendingPhase<UpwindPhase>(aircraft) is { } pendingUpwind)
        {
            pendingUpwind.IsExtended = true;
            Log.LogDebug("[ExtendPattern] {Callsign}: armed IsExtended on pending UpwindPhase already in queue", aircraft.Callsign);
            return CommandDispatcher.Ok("Extend upwind");
        }

        if (WillAppendNextCircuit(aircraft))
        {
            aircraft.Pattern.ExtendNextUpwind = true;
            Log.LogDebug("[ExtendPattern] {Callsign}: set ExtendNextUpwind flag — next circuit not yet queued", aircraft.Callsign);
            return CommandDispatcher.Ok("Extend upwind (queued for next circuit)");
        }

        return null;
    }

    /// <summary>
    /// True when PhaseRunner's auto-cycle block will append a new circuit after the
    /// current/pending cycle terminator completes. Mirrors the gate at PhaseRunner.Tick
    /// (wasCycleTerminator + IsComplete + TrafficDirection + AssignedRunway).
    /// </summary>
    private static bool WillAppendNextCircuit(AircraftState aircraft)
    {
        var phases = aircraft.Phases;
        if (phases is null)
        {
            return false;
        }
        if (phases.AssignedRunway is null)
        {
            return false;
        }
        if (aircraft.Pattern.TrafficDirection is null && phases.TrafficDirection is null)
        {
            return false;
        }

        // Any pending or active cycle terminator means PhaseRunner will append the next
        // circuit when that terminator completes. GoAroundPhase only auto-cycles when
        // ReenterPattern is true.
        for (int i = phases.CurrentIndex; i < phases.Phases.Count; i++)
        {
            var p = phases.Phases[i];
            if (p is TouchAndGoPhase or StopAndGoPhase or LowApproachPhase)
            {
                return true;
            }
            if (p is GoAroundPhase ga && ga.ReenterPattern)
            {
                return true;
            }
        }

        return false;
    }

    private static PatternEntryLeg? GetCurrentPatternLeg(Phase? phase)
    {
        return phase switch
        {
            UpwindPhase => PatternEntryLeg.Upwind,
            CrosswindPhase => PatternEntryLeg.Crosswind,
            DownwindPhase => PatternEntryLeg.Downwind,
            BasePhase => PatternEntryLeg.Base,
            _ => null,
        };
    }

    private static int LegOrder(PatternEntryLeg leg) =>
        leg switch
        {
            PatternEntryLeg.Upwind => 0,
            PatternEntryLeg.Crosswind => 1,
            PatternEntryLeg.Downwind => 2,
            PatternEntryLeg.Base => 3,
            PatternEntryLeg.Final => 4,
            _ => -1,
        };

    private static PatternDirection? CurrentPatternDirection(AircraftState aircraft)
    {
        return aircraft.Phases?.CurrentPhase switch
        {
            UpwindPhase up => up.Waypoints?.Direction,
            CrosswindPhase cw => cw.Waypoints?.Direction,
            DownwindPhase dw => dw.Waypoints?.Direction,
            BasePhase bp => bp.Waypoints?.Direction,
            _ => null,
        };
    }

    private static CommandResult RebuildPatternFromLeg(AircraftState aircraft, PatternEntryLeg leg)
    {
        if (aircraft.Phases?.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway for pattern rebuild");
        }

        var runway = aircraft.Phases.AssignedRunway;
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);
        var direction = CurrentPatternDirection(aircraft) ?? aircraft.Phases.TrafficDirection ?? PatternDirection.Left;

        var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            aircraft.Ground.Layout?.FindRunway(runway.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );

        bool touchAndGo = aircraft.Phases.TrafficDirection is not null;
        var circuitPhases = PatternBuilder.BuildCircuit(
            runway,
            category,
            direction,
            leg,
            touchAndGo,
            finalDistanceNm: null,
            sizeOv,
            altOv,
            NavigationDatabase.Instance.GetRunways(runway.AirportId)
        );

        // Mark the first phase of the new circuit as extended so the aircraft holds
        // the requested leg's heading until the next turn command is issued.
        if (circuitPhases.Count > 0)
        {
            switch (circuitPhases[0])
            {
                case UpwindPhase up:
                    up.IsExtended = true;
                    break;
                case CrosswindPhase cw:
                    cw.IsExtended = true;
                    break;
                case DownwindPhase dw:
                    dw.IsExtended = true;
                    break;
            }
        }

        ApplyRebuiltPatternChain(aircraft, runway, direction, circuitPhases);

        Log.LogDebug("[ExtendPattern] {Callsign}: rolled back to {Leg}, IsExtended=true on first phase", aircraft.Callsign, leg);
        return CommandDispatcher.Ok($"Extend {leg.ToString().ToLowerInvariant()}");
    }

    internal static CommandResult TryMakeShortApproach(AircraftState aircraft)
    {
        var category = AircraftCategorization.Categorize(aircraft.AircraftType);

        if (aircraft.Phases?.CurrentPhase is DownwindPhase liveDownwind)
        {
            // Compress the base-turn target so the aircraft turns base immediately
            // from its current position. Physics handles the bank — no teleport.
            // Also arm the upcoming Base with the category-floored short final so
            // the descent profile through Base targets the GS intercept altitude.
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            liveDownwind.ApplyShortApproach(ctx);
            if (TryFindNextPendingPhase<BasePhase>(aircraft) is { } pendingBaseLive)
            {
                pendingBaseLive.FinalDistanceNm = CategoryPerformance.MinShortApproachFinalNm(category);
            }
            return CommandDispatcher.Ok("Make short approach");
        }

        if (aircraft.Phases?.CurrentPhase is BasePhase liveBase)
        {
            liveBase.FinalDistanceNm = CategoryPerformance.MinShortApproachFinalNm(category);
            return CommandDispatcher.Ok("Make short approach");
        }

        // Queued modifier: aircraft hasn't reached downwind/base yet (e.g. still on
        // PatternEntry, Upwind, or Crosswind). Arm the next pending pattern leg so
        // the short approach takes effect when the aircraft actually gets there.
        // Both the upcoming Downwind (compress base extension + early descent) AND
        // the following Base (set FinalDistanceNm to the category-floored short final)
        // must be armed together — otherwise the descent profile would target the
        // normal-pattern altitude on Base and the aircraft arrives high to Final.
        bool armed = false;
        if (TryFindNextPendingPhase<DownwindPhase>(aircraft) is { } pendingDownwind)
        {
            pendingDownwind.ShortApproachArmed = true;
            armed = true;
        }
        if (TryFindNextPendingPhase<BasePhase>(aircraft) is { } pendingBase)
        {
            pendingBase.FinalDistanceNm = CategoryPerformance.MinShortApproachFinalNm(category);
            armed = true;
        }
        if (armed)
        {
            return CommandDispatcher.Ok("Make short approach");
        }

        return new CommandResult(false, "Make short approach requires downwind or base leg in the pattern");
    }

    internal static CommandResult TryMakeNormalApproach(AircraftState aircraft)
    {
        if (aircraft.Phases?.CurrentPhase is DownwindPhase liveDownwind)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            liveDownwind.RemoveShortApproach(ctx);
            if (TryFindNextPendingPhase<BasePhase>(aircraft) is { } pendingBaseLive)
            {
                pendingBaseLive.FinalDistanceNm = null;
            }
            return CommandDispatcher.Ok("Make normal approach");
        }

        if (aircraft.Phases?.CurrentPhase is BasePhase liveBase)
        {
            liveBase.FinalDistanceNm = null;
            return CommandDispatcher.Ok("Make normal approach");
        }

        // Queued modifier: clear SA arm on both the upcoming Downwind and the
        // following Base — symmetric to TryMakeShortApproach.
        bool cleared = false;
        if (TryFindNextPendingPhase<DownwindPhase>(aircraft) is { } pendingDownwind)
        {
            pendingDownwind.ShortApproachArmed = false;
            cleared = true;
        }
        if (TryFindNextPendingPhase<BasePhase>(aircraft) is { } pendingBase)
        {
            pendingBase.FinalDistanceNm = null;
            cleared = true;
        }
        if (cleared)
        {
            return CommandDispatcher.Ok("Make normal approach");
        }

        return new CommandResult(false, "Make normal approach requires downwind or base leg in the pattern");
    }

    /// <summary>
    /// Returns the first pending phase of the given type after the current index,
    /// or null if none is pending. Used by SA/MNA to arm or clear short-approach
    /// behavior on an upcoming Downwind/Base before the aircraft reaches it.
    /// </summary>
    private static T? TryFindNextPendingPhase<T>(AircraftState aircraft)
        where T : Phase
    {
        var phases = aircraft.Phases;
        if (phases is null)
        {
            return null;
        }

        for (int i = phases.CurrentIndex + 1; i < phases.Phases.Count; i++)
        {
            var p = phases.Phases[i];
            if (p.Status != PhaseStatus.Pending)
            {
                continue;
            }

            if (p is T match)
            {
                return match;
            }
        }

        return null;
    }

    internal static CommandResult TryCancel270(AircraftState aircraft)
    {
        // NO270 cancels a planned (pending) 270 from P270. An in-progress 270
        // is cancelled by issuing any other command (FH, FPH, TB, etc.) since
        // MakeTurnPhase.CanAcceptCommand returns ClearsPhase for all commands.
        if (aircraft.Phases is not null)
        {
            int nextIdx = aircraft.Phases.CurrentIndex + 1;
            if (
                nextIdx < aircraft.Phases.Phases.Count
                && aircraft.Phases.Phases[nextIdx] is MakeTurnPhase { TargetDegrees: >= 269 and <= 271, Status: PhaseStatus.Pending }
            )
            {
                aircraft.Phases.Phases.RemoveAt(nextIdx);
                return CommandDispatcher.Ok("Cancel planned 270");
            }
        }

        return new CommandResult(false, "No planned 270 to cancel");
    }

    internal static CommandResult TryPlan270(AircraftState aircraft)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "No active pattern phase");
        }

        var current = aircraft.Phases.CurrentPhase;
        if (current is not (DownwindPhase or BasePhase or CrosswindPhase or UpwindPhase))
        {
            return new CommandResult(false, "Plan 270 requires an active pattern leg");
        }

        var patternDir = aircraft.Phases.TrafficDirection;
        if (patternDir is null)
        {
            return new CommandResult(false, "Plan 270 requires an active traffic pattern");
        }

        // Check if there's already a planned 270 in the next phase
        int nextIdx = aircraft.Phases.CurrentIndex + 1;
        if (nextIdx < aircraft.Phases.Phases.Count && aircraft.Phases.Phases[nextIdx] is MakeTurnPhase { TargetDegrees: >= 269 and <= 271 })
        {
            return new CommandResult(false, "270 already planned for next turn");
        }

        var turnDir = patternDir == PatternDirection.Left ? TurnDirection.Left : TurnDirection.Right;
        var turnPhase = new MakeTurnPhase { Direction = turnDir, TargetDegrees = 270 };
        aircraft.Phases.InsertAfterCurrent(turnPhase);

        var dirStr = turnDir == TurnDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"Plan {dirStr} 270 at next turn");
    }

    internal static CommandResult TrySetPatternSize(AircraftState aircraft, double sizeNm)
    {
        if (sizeNm is < 0.25 or > 10.0)
        {
            return new CommandResult(false, "Pattern size must be between 0.25 and 10.0 NM");
        }

        aircraft.Pattern.SizeOverrideNm = sizeNm;

        // Update waypoints on active pattern phases if in a pattern
        if (aircraft.Phases?.AssignedRunway is { } runway)
        {
            var category = AircraftCategorization.Categorize(aircraft.AircraftType);
            var direction = aircraft.Phases.TrafficDirection ?? PatternDirection.Left;
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                aircraft.Ground.Layout?.FindRunway(runway.Designator),
                sizeNm,
                aircraft.Pattern.AltitudeOverrideFt
            );
            var waypoints = PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);
            PatternBuilder.UpdateWaypoints(aircraft.Phases, waypoints);
        }

        return CommandDispatcher.Ok($"Pattern size {sizeNm:G} NM");
    }

    internal static CommandResult TryMakeSTurns(AircraftState aircraft, TurnDirection initialDirection, int count)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "No active phase for S-turns");
        }

        // S-turns are inserted before FinalApproachPhase or during downwind/base
        var sturnPhase = new STurnPhase { InitialDirection = initialDirection, Count = count };
        aircraft.Phases.InsertAfterCurrent(sturnPhase);

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.AdvanceToNext(ctx);

        var dirStr = initialDirection == TurnDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"S-turns, initial {dirStr}, {count} turns");
    }

    /// <summary>
    /// OFL/OFR: dogleg perpendicular to current pattern heading, acquire a parallel
    /// track offset <paramref name="offsetNm"/> NM to the left or right, then hold
    /// parallel. State lives on the active phase; discarded on phase completion.
    /// Allowed on upwind/crosswind/downwind/base. Rejected on final (use MLS/MRS
    /// for final-leg spacing) and when no pattern leg is active.
    /// </summary>
    internal static CommandResult TryOffsetPattern(AircraftState aircraft, TurnDirection direction, double? offsetNm)
    {
        const double DefaultOffsetNm = 0.5;
        const double MinOffsetNm = 0.1;
        const double MaxOffsetNm = 1.5;

        double resolved = offsetNm ?? DefaultOffsetNm;
        if (resolved < MinOffsetNm || resolved > MaxOffsetNm)
        {
            return new CommandResult(false, $"Pattern offset must be between {MinOffsetNm:G} and {MaxOffsetNm:G} NM");
        }

        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "Pattern offset applies on upwind, crosswind, downwind, or base");
        }

        var state = new PatternLateralOffsetState { TargetNm = resolved, Direction = direction };

        switch (aircraft.Phases.CurrentPhase)
        {
            case DownwindPhase dw:
                dw.LateralOffset = state;
                break;
            case UpwindPhase up:
                up.LateralOffset = state;
                break;
            case CrosswindPhase cw:
                cw.LateralOffset = state;
                break;
            case BasePhase bp:
                bp.LateralOffset = state;
                break;
            default:
                return new CommandResult(false, "Pattern offset applies on upwind, crosswind, downwind, or base");
        }

        var dirStr = direction == TurnDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"Offset {dirStr} {resolved:G} NM");
    }

    internal static CommandResult TryMakeTurn(AircraftState aircraft, TurnDirection direction, double degrees)
    {
        var dirStr = direction == TurnDirection.Left ? "left" : "right";

        // Standalone turn: no active phases — create a minimal phase list with
        // just the turn so R360/L360/R270/L270 work on airborne aircraft that
        // haven't been given a pattern entry (e.g. VFR traffic handed off to tower).
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            var standalone = new MakeTurnPhase { Direction = direction, TargetDegrees = degrees };
            aircraft.Phases = new PhaseList();
            aircraft.Phases.Add(standalone);
            var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Start(startCtx);
            return CommandDispatcher.Ok($"Make {dirStr} {degrees:F0}");
        }

        var turnPhase = new MakeTurnPhase { Direction = direction, TargetDegrees = degrees };

        // For 360s on pattern legs: re-insert a fresh copy of the current phase
        // after the turn so the aircraft resumes the same leg instead of
        // incorrectly skipping to the next one.
        bool is360 = Math.Abs(degrees - 360) < 1;
        Phase? resumePhase = is360 ? ClonePatternPhase(aircraft.Phases.CurrentPhase) : null;

        if (resumePhase is not null)
        {
            aircraft.Phases.InsertAfterCurrent(new List<Phase> { turnPhase, resumePhase });
        }
        else
        {
            aircraft.Phases.InsertAfterCurrent(turnPhase);
        }

        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.AdvanceToNext(ctx);

        return CommandDispatcher.Ok($"Make {dirStr} {degrees:F0}");
    }

    /// <summary>
    /// Create a fresh copy of a pattern phase with the same configuration,
    /// so the aircraft can resume the same leg after a 360° turn.
    /// Returns null if the current phase is not a pattern leg.
    /// </summary>
    private static Phase? ClonePatternPhase(Phase? phase)
    {
        return phase switch
        {
            DownwindPhase dw => new DownwindPhase { Waypoints = dw.Waypoints, IsExtended = dw.IsExtended },
            BasePhase bp => new BasePhase { Waypoints = bp.Waypoints, FinalDistanceNm = bp.FinalDistanceNm },
            CrosswindPhase cw => new CrosswindPhase { Waypoints = cw.Waypoints },
            UpwindPhase up => new UpwindPhase { Waypoints = up.Waypoints },
            _ => null,
        };
    }

    internal static CommandResult TrySetupTouchAndGo(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (!CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new TouchAndGoPhase()))
        {
            return new CommandResult(false, "Cleared touch-and-go requires a pending approach (no landing phase to replace)");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedTouchAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
            aircraft.Pattern.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);

        return CommandDispatcher.Ok($"Cleared touch-and-go{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    internal static CommandResult TrySetupStopAndGo(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (!CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new StopAndGoPhase()))
        {
            return new CommandResult(false, "Cleared stop-and-go requires a pending approach (no landing phase to replace)");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedStopAndGo;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
            aircraft.Pattern.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);

        return CommandDispatcher.Ok($"Cleared stop-and-go{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    internal static CommandResult TrySetupLowApproach(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (!CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new LowApproachPhase()))
        {
            return new CommandResult(false, "Cleared low approach requires a pending approach (no landing phase to replace)");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedLowApproach;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
            aircraft.Pattern.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);

        return CommandDispatcher.Ok($"Cleared low approach{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    internal static CommandResult TrySetupClearedForOption(AircraftState aircraft, PatternDirection? trafficPattern)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (!CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new TouchAndGoPhase()))
        {
            return new CommandResult(false, "Cleared for the option requires a pending approach (no landing phase to replace)");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedForOption;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway?.Designator;
        if (trafficPattern is { } dir)
        {
            aircraft.Phases.TrafficDirection = dir;
            aircraft.Pattern.TrafficDirection = dir;
        }
        EnsurePatternMode(aircraft.Phases);

        return CommandDispatcher.Ok($"Cleared for the option{CommandDispatcher.RunwayLabel(aircraft)}{TrafficLabel(trafficPattern)}");
    }

    private static string TrafficLabel(PatternDirection? dir)
    {
        return dir switch
        {
            PatternDirection.Left => ", make left traffic",
            PatternDirection.Right => ", make right traffic",
            _ => "",
        };
    }

    private static bool IsHelicopter(AircraftState aircraft) =>
        AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;

    internal static CommandResult TryHoldPresentPosition(AircraftState aircraft, TurnDirection? orbitDirection)
    {
        if (orbitDirection is null && !IsHelicopter(aircraft))
        {
            return new CommandResult(false, "HPP (hover) requires a helicopter — use HPPL or HPPR for 360s");
        }

        var phase = new VfrHoldPhase { OrbitDirection = orbitDirection };

        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
            aircraft.Phases = new PhaseList { AssignedRunway = aircraft.Phases.AssignedRunway };
        }
        else
        {
            aircraft.Phases = new PhaseList();
        }

        aircraft.Phases.Add(phase);
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var dirStr = orbitDirection switch
        {
            TurnDirection.Left => "left 360s",
            TurnDirection.Right => "right 360s",
            _ => "hover",
        };
        return CommandDispatcher.Ok($"Hold present position, {dirStr}");
    }

    internal static CommandResult TryHoldAtFix(AircraftState aircraft, string fixName, double lat, double lon, TurnDirection? orbitDirection)
    {
        if (orbitDirection is null && !IsHelicopter(aircraft))
        {
            return new CommandResult(false, "HFIX (hover) requires a helicopter — use HFIXL or HFIXR for holding turns");
        }

        var phase = new VfrHoldPhase
        {
            FixName = fixName,
            FixLat = lat,
            FixLon = lon,
            OrbitDirection = orbitDirection,
        };

        RunwayInfo? runway = aircraft.Phases?.AssignedRunway;
        if (aircraft.Phases is not null)
        {
            var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.Clear(ctx);
        }

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        if (runway is not null)
        {
            NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, runway.Designator);
        }

        aircraft.Phases.Add(phase);
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Start(startCtx);

        var dirStr = orbitDirection switch
        {
            TurnDirection.Left => "left orbits",
            TurnDirection.Right => "right orbits",
            _ => "hover",
        };
        return CommandDispatcher.Ok($"Hold at {fixName}, {dirStr}");
    }

    /// <summary>
    /// Choose the lead-in waypoint for a downwind entry. Evaluates two candidates
    /// — a 45° midfield intercept (AIM 4-3-3) and a straight-in join to the
    /// extended downwind leg — and picks whichever requires less total maneuvering
    /// from the aircraft's current position and heading. A penalty on any single
    /// turn exceeding 120° discourages near-U-turn (loop) paths.
    ///
    /// 45° entry heading: downwind ± 45° (+ for right pattern, − for left).
    /// 45° lead-in distance: 50% of runway length, with per-category floors so
    /// turboprops and jets get enough room to stabilize on the entry leg:
    ///   Piston/Helicopter: 0.5 × runway length (no floor — tight patterns)
    ///   Turboprop:         max(0.5 × runway length, 1.5 nm)
    ///   Jet:               max(0.5 × runway length, 2.0 nm)
    /// Extended-downwind lead-in distance: 1.5 nm (legacy default).
    /// </summary>
    private static (double Lat, double Lon) ChooseDownwindLeadIn(
        AircraftState aircraft,
        RunwayInfo runway,
        PatternDirection direction,
        AircraftCategory category,
        PatternWaypoints waypoints,
        double abeamLat,
        double abeamLon
    )
    {
        const double UTurnPenaltyThresholdDeg = 120.0;
        const double ExtendedDownwindLeadInNm = 1.5;

        double downwindDeg = waypoints.DownwindHeading.Degrees;
        double entry45Deg = direction == PatternDirection.Right ? downwindDeg + 45.0 : downwindDeg - 45.0;
        var entry45Hdg = new TrueHeading(entry45Deg);
        TrueHeading reverseEntry45 = entry45Hdg.ToReciprocal();
        TrueHeading reverseDownwind = waypoints.DownwindHeading.ToReciprocal();

        double runwayHalfNm = runway.LengthFt * 0.5 / 6076.12;
        double categoryFloorNm = category switch
        {
            AircraftCategory.Jet => 2.0,
            AircraftCategory.Turboprop => 1.5,
            _ => 0.0,
        };
        double leadIn45Nm = Math.Max(runwayHalfNm, categoryFloorNm);
        var lead45 = GeoMath.ProjectPoint(abeamLat, abeamLon, reverseEntry45, leadIn45Nm);
        var leadXtdDownwind = GeoMath.ProjectPoint(abeamLat, abeamLon, reverseDownwind, ExtendedDownwindLeadInNm);

        double score45 = ScoreLeadInPath(aircraft, lead45.Lat, lead45.Lon, entry45Deg, downwindDeg, UTurnPenaltyThresholdDeg);
        double scoreXtdDownwind = ScoreLeadInPath(
            aircraft,
            leadXtdDownwind.Lat,
            leadXtdDownwind.Lon,
            downwindDeg,
            downwindDeg,
            UTurnPenaltyThresholdDeg
        );

        Log.LogDebug(
            "[PatternEntry.LeadIn] {Callsign}: 45° score={Score45:F1} XDW score={ScoreXDW:F1} → {Chosen}",
            aircraft.Callsign,
            score45,
            scoreXtdDownwind,
            score45 <= scoreXtdDownwind ? "45°" : "XDW"
        );

        return score45 <= scoreXtdDownwind ? (lead45.Lat, lead45.Lon) : (leadXtdDownwind.Lat, leadXtdDownwind.Lon);
    }

    /// <summary>
    /// Sum of absolute heading changes along the path aircraft → lead-in → abeam → downwind,
    /// plus a penalty for any single turn exceeding <paramref name="uTurnThresholdDeg"/>.
    /// The penalty is linear past the threshold so a 160° turn scores far worse than a 119°
    /// one, which is what distinguishes a clean 45° intercept from a loop-around.
    /// </summary>
    private static double ScoreLeadInPath(
        AircraftState aircraft,
        double leadInLat,
        double leadInLon,
        double entryHeadingDeg,
        double downwindHeadingDeg,
        double uTurnThresholdDeg
    )
    {
        double bearingToLeadIn = GeoMath.BearingTo(aircraft.Position, new LatLon(leadInLat, leadInLon));
        double turnInit = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, bearingToLeadIn);
        double turnMid = GeoMath.AbsBearingDifference(bearingToLeadIn, entryHeadingDeg);
        double turnAbeam = GeoMath.AbsBearingDifference(entryHeadingDeg, downwindHeadingDeg);

        double total = turnInit + turnMid + turnAbeam;
        total += Math.Max(0.0, turnInit - uTurnThresholdDeg);
        total += Math.Max(0.0, turnMid - uTurnThresholdDeg);
        total += Math.Max(0.0, turnAbeam - uTurnThresholdDeg);
        return total;
    }

    /// <summary>
    /// Altitude-aware Final entry distance for a diagonal EF join (no explicit distance).
    /// The aircraft descends immediately on the diagonal cut-in toward the runway, so it
    /// joins final as CLOSE to the threshold as it can while still reaching the glideslope
    /// altitude by the join — a "make straight-in" shortcut. The closest stabilized final
    /// (<see cref="MinimumPerpendicularBaseFinalDistanceNm"/>) is the floor; an aircraft
    /// too high to lose its altitude on the diagonal at the pattern descent rate joins
    /// farther out (longer final = more descent room); capped at
    /// <paramref name="alongTrackOutboundNm"/> so EF never routes the aircraft outbound.
    /// Returns <c>null</c> when even the minimum final exceeds the along-track cap (no room
    /// for a stable final from this position) — defer to the fixed-distance fallback + loop
    /// check. <paramref name="tooHighToDescend"/> is set when, even at the capped join, the
    /// diagonal can't absorb the descent at the category rate — a controller-facing warning,
    /// not a rejection.
    /// </summary>
    private static double? ComputeAltitudeAwareFinalEntryDistanceNm(
        AircraftState aircraft,
        RunwayInfo runway,
        AircraftCategory category,
        PatternWaypoints waypoints,
        double alongTrackOutboundNm,
        out bool tooHighToDescend
    )
    {
        tooHighToDescend = false;

        double minFinalNm = MinimumPerpendicularBaseFinalDistanceNm(category);
        if (alongTrackOutboundNm < minFinalNm)
        {
            // The along-track cap is inside the minimum final — no room to establish a
            // stable final from here. Defer to the fixed-distance fallback + loop check.
            return null;
        }

        double feetPerNm = GlideSlopeGeometry.FeetPerNm(GlideSlopeGeometry.AngleForCategory(category));
        double altitudeToLoseFt = aircraft.Altitude - runway.ElevationFt;
        double crossTrackAbsNm = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon), waypoints.FinalHeading)
        );
        double descentRateFpm = CategoryPerformance.PatternDescentRate(category);
        double approachSpeedKt = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category);

        // Walk candidate join distances from the closest stabilized final outward to the
        // along-track cap. Take the FIRST (closest) where the aircraft, descending at the
        // pattern rate over the diagonal cut-in, can reach the glideslope altitude at the
        // join. A low aircraft satisfies this at the minimum final (closest shortcut); a
        // higher one needs a longer final; capped at along-track (never outbound).
        const double stepNm = 0.05;
        for (double joinNm = minFinalNm; joinNm <= alongTrackOutboundNm + 1e-9; joinNm += stepNm)
        {
            double candidateNm = Math.Min(joinNm, alongTrackOutboundNm);
            double alongGapNm = alongTrackOutboundNm - candidateNm;
            double diagonalNm = Math.Sqrt((alongGapNm * alongGapNm) + (crossTrackAbsNm * crossTrackAbsNm));
            double descentNeededOnDiagonalFt = altitudeToLoseFt - (candidateNm * feetPerNm);
            double diagonalMinutes = diagonalNm / (approachSpeedKt / 60.0);
            double descentAvailableFt = descentRateFpm * diagonalMinutes;
            if (descentAvailableFt >= descentNeededOnDiagonalFt)
            {
                return candidateNm;
            }
        }

        // Even at the along-track cap the aircraft can't lose its altitude on the diagonal
        // at the pattern descent rate. Join at the cap and flag a controller-facing warning.
        tooHighToDescend = true;
        return alongTrackOutboundNm;
    }

    /// <summary>
    /// Get the entry point coordinates for a given pattern entry leg.
    /// For base with a custom final distance, computes the point on the
    /// extended centerline offset laterally by pattern size.
    /// For final entry, computes the glideslope-TPA intercept point on
    /// the extended centerline (or uses the custom final distance).
    /// </summary>
    private static (double Lat, double Lon) GetEntryPoint(
        PatternWaypoints wp,
        PatternEntryLeg leg,
        double? finalDistanceNm,
        AircraftCategory category
    )
    {
        if (leg == PatternEntryLeg.Base && finalDistanceNm is not null)
        {
            // Place the entry on the base leg perpendicular to the extended
            // centerline at finalDistanceNm out, at pattern width on the
            // pattern side. BasePhase fires the final turn when cross-track
            // ≤ turn radius, so the entry's along-track is what determines
            // where the aircraft rolls onto centerline. A diagonal offset
            // would carry pattern-length into the along-track and shift the
            // rollout outbound past the requested distance.
            TrueHeading reciprocal = wp.FinalHeading.ToReciprocal();
            var finalPoint = GeoMath.ProjectPoint(wp.ThresholdLat, wp.ThresholdLon, reciprocal, finalDistanceNm.Value);
            double patternWidth = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    new LatLon(wp.BaseTurnLat, wp.BaseTurnLon),
                    new LatLon(wp.ThresholdLat, wp.ThresholdLon),
                    wp.FinalHeading
                )
            );
            TrueHeading perpBearing = wp.BaseHeading.ToReciprocal();
            var entryPoint = GeoMath.ProjectPoint(finalPoint.Lat, finalPoint.Lon, perpBearing, patternWidth);
            return (entryPoint.Lat, entryPoint.Lon);
        }

        if (leg == PatternEntryLeg.Final)
        {
            // Entry point on extended centerline at the glideslope-TPA intercept distance.
            // If finalDistanceNm is specified, use that distance instead.
            double gsAngle = GlideSlopeGeometry.AngleForCategory(category);
            double entryDist = finalDistanceNm ?? (CategoryPerformance.PatternAltitudeAgl(category) / GlideSlopeGeometry.FeetPerNm(gsAngle));
            TrueHeading reciprocal = wp.FinalHeading.ToReciprocal();
            var point = GeoMath.ProjectPoint(wp.ThresholdLat, wp.ThresholdLon, reciprocal, entryDist);
            return (point.Lat, point.Lon);
        }

        return leg switch
        {
            // AIM 4-3-3: enter downwind at midfield (abeam the threshold), not at the departure end.
            // The standard 45° entry joins the downwind leg abeam the runway midpoint.
            PatternEntryLeg.Downwind => (wp.DownwindAbeamLat, wp.DownwindAbeamLon),
            PatternEntryLeg.Crosswind => (wp.CrosswindTurnLat, wp.CrosswindTurnLon),
            PatternEntryLeg.Base => (wp.BaseTurnLat, wp.BaseTurnLon),
            PatternEntryLeg.Upwind => (wp.DepartureEndLat, wp.DepartureEndLon),
            _ => (wp.DownwindAbeamLat, wp.DownwindAbeamLon),
        };
    }

    /// <summary>
    /// Maximum lateral separation (nm) between two parallel runway centerlines for
    /// EF to be treated as a sidestep instead of a fresh pattern-entry build. AIM
    /// §5-4-19.1 anchors the side-step maneuver at runways "no more than 1200 feet"
    /// between centerlines; 0.25 nm (~1520 ft) leaves a small buffer for nominal
    /// mag-variation differences while still excluding non-parallel pairs (e.g.
    /// OAK 28L/30, where the headings alone already disqualify them).
    /// </summary>
    private const double MaxSidestepCenterlineSeparationNm = 0.25;

    /// <summary>
    /// Maximum runway-heading difference (degrees) between two runways for EF to be
    /// treated as a sidestep. True parallels are typically &lt;1°; CIFP / mag-var
    /// rounding can push apparent deltas to a few degrees; 5° is a safe cap that
    /// still excludes non-parallel pairs.
    /// </summary>
    private const double MaxSidestepHeadingDeltaDeg = 5.0;

    /// <summary>
    /// Minimum AGL (ft) at which a sidestep retarget is still safe. Below the
    /// stabilized-approach floor (FAA AC 120-71 / InFO 11009 — 500 ft VMC) the
    /// aircraft is committed to the original runway and doesn't have enough lateral
    /// distance left to capture the parallel centerline before flare.
    /// </summary>
    private const double MinSidestepAglFt = 500.0;

    /// <summary>
    /// Maximum heading delta (degrees) from runway heading for EF to engage the
    /// "close-in aligned final" path that uses the aircraft's current along-track as
    /// the entry point. Anchored to 7110.65 §5-9-2 / TBL 5-9-1:
    ///   - 30° general intercept envelope (≥ 2 NM from approach gate),
    ///   - 20° tightened envelope inside 2 NM,
    ///   - 45° helicopters.
    /// Beyond these limits an aircraft is meaningfully misaligned and should be
    /// vectored before EF; we leave the standard far-entry / loop-check path to
    /// handle it (likely rejecting with "Unable, short final").
    /// </summary>
    private static double MaxCloseInFinalAngleOffDeg(double alongTrackNm, AircraftCategory category)
    {
        if (category == AircraftCategory.Helicopter)
        {
            return 45.0;
        }
        return alongTrackNm < 2.0 ? 20.0 : 30.0;
    }

    /// <summary>
    /// True when <paramref name="target"/> is parallel to <paramref name="current"/>
    /// and their centerlines are within <see cref="MaxSidestepCenterlineSeparationNm"/>.
    /// Both runways must be at the same airport; the heading delta must be within
    /// <see cref="MaxSidestepHeadingDeltaDeg"/>.
    /// </summary>
    private static bool IsParallelSidestepCandidate(RunwayInfo current, RunwayInfo target)
    {
        if (!string.Equals(current.AirportId, target.AirportId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (current.TrueHeading.AbsAngleTo(target.TrueHeading) > MaxSidestepHeadingDeltaDeg)
        {
            return false;
        }

        double crossTrackNm = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                new LatLon(target.ThresholdLatitude, target.ThresholdLongitude),
                new LatLon(current.ThresholdLatitude, current.ThresholdLongitude),
                current.TrueHeading
            )
        );
        return crossTrackNm <= MaxSidestepCenterlineSeparationNm;
    }

    /// <summary>
    /// Apply a parallel-runway sidestep on an active FinalApproachPhase: retarget the
    /// running phase to the new runway, transfer any landing clearance, and clear any
    /// active instrument approach (it doesn't apply to the parallel runway). The
    /// FinalApproachPhase keeps its established/intercept/glideslope state — the
    /// aircraft is still flying a 3° slope, just on the parallel centerline.
    /// </summary>
    private static CommandResult ApplySidestep(
        AircraftState aircraft,
        FinalApproachPhase finalApproach,
        RunwayInfo newRunway,
        AircraftCategory category
    )
    {
        var phases = aircraft.Phases!;
        phases.ActiveApproach = null;
        if (phases.ClearedRunwayId is not null)
        {
            phases.ClearedRunwayId = newRunway.Designator;
        }
        finalApproach.RetargetRunway(newRunway, GlideSlopeGeometry.AngleForCategory(category));
        return CommandDispatcher.Ok($"Sidestep, runway {newRunway.Designator}");
    }

    /// <summary>
    /// Minimum along-track distance (nm) for an ERB/ELB-no-distance turn to base —
    /// below this, the aircraft cannot establish a stable final approach segment
    /// after the base-to-final turn. Tuned per category so the turn radius still
    /// leaves useful final: jets need ~2 nm, pistons ~1 nm, helicopters ~0.5 nm.
    /// </summary>
    private static double MinimumPerpendicularBaseFinalDistanceNm(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 2.0,
            AircraftCategory.Turboprop => 2.0,
            AircraftCategory.Piston => 1.0,
            AircraftCategory.Helicopter => 0.5,
            _ => 2.0,
        };

    /// <summary>
    /// Ensure the aircraft is in pattern mode. If TrafficDirection is not set,
    /// infer direction from existing pattern phases or default to Left.
    /// </summary>
    private static void EnsurePatternMode(PhaseList phases)
    {
        if (phases.TrafficDirection is not null)
        {
            return;
        }

        // Infer from existing pattern phases
        foreach (var phase in phases.Phases)
        {
            if (phase is DownwindPhase { Waypoints: not null } dw)
            {
                phases.TrafficDirection = dw.Waypoints.Direction;
                return;
            }
            if (phase is BasePhase { Waypoints: not null } bp)
            {
                phases.TrafficDirection = bp.Waypoints.Direction;
                return;
            }
        }

        phases.TrafficDirection = PatternDirection.Left;
    }

    internal static CommandResult TryGoAround(GoAroundCommand ga, AircraftState aircraft)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "Go around not applicable");
        }

        if (ga.TrafficPattern is { } patDir)
        {
            // Traffic pattern direction only applies to VFR, visual approach, or already-in-pattern aircraft
            bool canSetPattern =
                aircraft.FlightPlan.IsVfr
                || (aircraft.Phases.TrafficDirection is not null)
                || (aircraft.Phases.ActiveApproach?.ApproachId.StartsWith("VIS", StringComparison.Ordinal) == true);
            if (!canSetPattern)
            {
                return new CommandResult(false, "Traffic pattern direction not applicable for IFR aircraft");
            }

            aircraft.Phases.TrafficDirection = patDir;
            aircraft.Pattern.TrafficDirection = patDir;

            // Set pattern altitude override if provided (e.g., GA MLT 15)
            if (ga.TargetAltitude is not null)
            {
                aircraft.Pattern.AltitudeOverrideFt = ga.TargetAltitude;
            }
        }

        bool isGaPattern = aircraft.Phases.TrafficDirection is not null;
        var gaCtx = CommandDispatcher.BuildMinimalContext(aircraft);
        int? gaTargetAlt = ga.TargetAltitude;
        bool hasAtcOverride = ga.AssignedMagneticHeading is not null || ga.TargetAltitude is not null;

        // Build MAP phases for instrument approaches without ATC override
        var mapPhases = (!isGaPattern && !hasAtcOverride) ? ApproachCommandHandler.BuildMissedApproachPhases(aircraft) : [];

        if (gaTargetAlt is null && mapPhases.Count > 0)
        {
            var mapFixes = aircraft.Phases.ActiveApproach!.MissedApproachFixes;
            gaTargetAlt = ApproachCommandHandler.GetMissedApproachAltitude(mapFixes);
        }
        else if (gaTargetAlt is null && isGaPattern)
        {
            // AIM 4-3-2: hand off to UpwindPhase 300ft below pattern altitude so the
            // crosswind turn becomes available at the same threshold as a VFR departure.
            double fieldElev = gaCtx.Runway?.ElevationFt ?? 0;
            double patAgl = CategoryPerformance.PatternAltitudeAgl(gaCtx.Category);
            gaTargetAlt = (int)(fieldElev + patAgl - 300.0);
        }

        var goAround = new GoAroundPhase
        {
            AssignedMagneticHeading = ga.AssignedMagneticHeading,
            TargetAltitude = gaTargetAlt,
            ReenterPattern = isGaPattern,
            NextLandingFullStop = GoAroundHelper.CaptureLandingFullStopIntent(aircraft.Phases),
        };

        var phases = new List<Phase> { goAround };
        phases.AddRange(mapPhases);

        aircraft.Phases.ReplaceUpcoming(phases);
        aircraft.Phases.AdvanceToNext(gaCtx);

        var gaMsg = "Go around";
        if (ga.TrafficPattern is PatternDirection.Left)
        {
            gaMsg += ", make left traffic";
        }
        else if (ga.TrafficPattern is PatternDirection.Right)
        {
            gaMsg += ", make right traffic";
        }
        if (ga.AssignedMagneticHeading is not null)
        {
            gaMsg += $", fly heading {ga.AssignedMagneticHeading.Value.ToDisplayInt():000}";
        }
        if (ga.TargetAltitude is not null)
        {
            gaMsg += $", climb to {ga.TargetAltitude:N0}";
        }
        return CommandDispatcher.Ok(gaMsg);
    }

    internal static CommandResult TryClearedToLand(ClearedToLandCommand ctl, AircraftState aircraft)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (aircraft.IsOnGround)
        {
            return new CommandResult(false, "Cannot clear to land — aircraft is on the ground");
        }

        if (aircraft.Phases.AssignedRunway is null)
        {
            return new CommandResult(false, "Cannot clear to land — no runway assigned");
        }

        var isHeliCtl = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        Phase landingCtl = isHeliCtl ? new HelicopterLandingPhase() : new LandingPhase();
        if (!CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, landingCtl))
        {
            return new CommandResult(false, "Cannot clear to land — no pending approach (assign an approach or pattern entry first)");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = aircraft.Phases.AssignedRunway.Designator;
        aircraft.Phases.TrafficDirection = null;
        // CLAND signals full-stop intent — drop persistent pattern direction so
        // PhaseRunner.cs:70 takes the post-landing exit branch instead of auto-
        // cycling another circuit from a stale MLT/MRT.
        aircraft.Pattern.TrafficDirection = null;
        if (ctl.NoDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
        }
        return CommandDispatcher.Ok($"Cleared to land{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    internal static CommandResult TryLandAndHoldShort(LandAndHoldShortCommand lahso, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (aircraft.Phases.AssignedRunway is null)
        {
            return new CommandResult(false, "No assigned runway");
        }

        if (groundLayout is null)
        {
            return new CommandResult(false, "No ground layout available for LAHSO intersection calculation");
        }

        var runway = aircraft.Phases.AssignedRunway;

        // Find the landing runway in the ground layout
        var landingGround = groundLayout.FindGroundRunway(runway.Designator);
        if (landingGround is null)
        {
            return new CommandResult(false, $"Landing runway {runway.Designator} not found in ground layout");
        }

        // Find the crossing runway in the ground layout
        var crossingGround = groundLayout.FindGroundRunway(lahso.CrossingRunwayId);
        if (crossingGround is null)
        {
            return new CommandResult(false, $"Crossing runway {lahso.CrossingRunwayId} not found in ground layout");
        }

        // Compute the intersection point
        var intersection = RunwayIntersectionCalculator.FindIntersection(landingGround, crossingGround);
        if (intersection is null)
        {
            return new CommandResult(false, $"Runway {runway.Designator} does not intersect runway {lahso.CrossingRunwayId}");
        }

        // Compute hold-short distance from threshold
        double holdShortDistNm = RunwayIntersectionCalculator.ComputeHoldShortDistanceNm(
            intersection.Value.DistFromStartNm,
            runway.Designator,
            landingGround,
            crossingGround.WidthFt
        );

        if (holdShortDistNm < 0.1)
        {
            return new CommandResult(false, $"Hold-short point too close to threshold for runway {lahso.CrossingRunwayId}");
        }

        // Compute the hold-short lat/lon on the landing runway centerline
        var holdShortPoint = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading, holdShortDistNm);

        if (!CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, new LandingPhase()))
        {
            return new CommandResult(false, "Cannot clear LAHSO — no pending approach (assign an approach or pattern entry first)");
        }

        // Set LAHSO target
        aircraft.Phases.LahsoHoldShort = new LahsoTarget
        {
            Lat = holdShortPoint.Lat,
            Lon = holdShortPoint.Lon,
            DistFromThresholdNm = holdShortDistNm,
            CrossingRunwayId = lahso.CrossingRunwayId,
        };

        // LAHSO includes landing clearance per 7110.65 §3-10-5.b
        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = runway.Designator;
        aircraft.Phases.TrafficDirection = null;
        // LAHSO is always full-stop — drop persistent pattern direction.
        aircraft.Pattern.TrafficDirection = null;

        return CommandDispatcher.Ok($"Cleared to land{CommandDispatcher.RunwayLabel(aircraft)}, hold short runway {lahso.CrossingRunwayId}");
    }

    internal static CommandResult TryCancelLandingClearance(AircraftState aircraft)
    {
        if (aircraft.Phases is null || aircraft.Phases.LandingClearance is null)
        {
            return new CommandResult(false, "No landing clearance to cancel");
        }

        aircraft.Phases.LandingClearance = null;
        aircraft.Phases.ClearedRunwayId = null;
        return CommandDispatcher.Ok($"Landing clearance cancelled{CommandDispatcher.RunwayLabel(aircraft)}");
    }

    /// <summary>
    /// Determines the entry kind for a PatternEntryPhase. Non-downwind legs map
    /// directly; a downwind entry is classified by the angular delta between the
    /// aircraft's current track and the downwind course.
    /// </summary>
    internal static PatternEntryKind ClassifyEntryKind(
        AircraftState aircraft,
        RunwayInfo runway,
        PatternDirection direction,
        PatternEntryLeg entryLeg
    )
    {
        return entryLeg switch
        {
            PatternEntryLeg.Upwind => PatternEntryKind.Upwind,
            PatternEntryLeg.Base => PatternEntryKind.Base,
            PatternEntryLeg.Final => PatternEntryKind.Final,
            _ => PatternEntryPhase.ClassifyDownwindEntry(
                aircraft.Position,
                aircraft.TrueTrack,
                new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
                runway.TrueHeading,
                runway.TrueHeading.ToReciprocal(),
                direction
            ),
        };
    }
}
