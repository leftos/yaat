using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;

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
        double? finalDistanceNm,
        // Resolved ground layout for authored pattern size/altitude. Production dispatch passes
        // ctx.GroundLayout (resolves the authored TPA even when the per-aircraft Ground.Layout is unset,
        // issue #210); the null default falls back to aircraft.Ground.Layout for direct test callers.
        AirportGroundLayout? groundLayout = null
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
        var previousClearedRunwayId = aircraft.Phases?.ClearedRunwayId;

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
                return new CommandResult(false, $"Runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found at {airportId}");
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

        // A landing clearance names a runway (7110.65 §3-10-5), so a pattern entry that reassigns
        // the aircraft to a different runway voids it — the controller must clear it again for the
        // new runway ("CHANGE TO RUNWAY (n), RUNWAY (n) CLEARED TO LAND", §3-10-5.c). Re-entering
        // the pattern for the SAME runway keeps the standing clearance. Mirrors the equivalent
        // guard in TryChangePatternDirection (MRT/MLT). The instrument sidestep branch below is
        // exempt and returns before the rebuild: there the approach clearance itself authorizes
        // the landing on the parallel (§4-8-7, AIM §5-4-19).
        bool voidsLandingClearance =
            runwayId is not null
            && previousClearedRunwayId is not null
            && !string.Equals(previousClearedRunwayId, runway.Designator, StringComparison.OrdinalIgnoreCase);

        var standingClearance = voidsLandingClearance ? null : aircraft.Phases.LandingClearance;

        // The terminal phase of the rebuilt circuit follows the controller's
        // standing landing clearance, not the transient pattern turn-direction.
        // A full-stop CLAND must survive a later pattern-entry command
        // (EF/ERB/ELB/...) — every entry stamps TrafficDirection (below) for
        // go-around geometry, so reading that field as touch-and-go intent turns
        // a cleared-to-land aircraft into a touch-and-go on the second entry.
        // Only an explicit TG/COPT/SG/LA authorizes a non-full-stop terminal.
        // With no landing clearance, fall back to pattern-work state (closed
        // traffic via MRT/MLT/CTO defaults to touch-and-go and re-enters; a plain
        // entry full-stops and auto-goes-around at minimums if never cleared).
        bool touchAndGo = standingClearance switch
        {
            ClearanceType.ClearedToLand => false,
            ClearanceType.ClearedForOption or ClearanceType.ClearedTouchAndGo or ClearanceType.ClearedStopAndGo or ClearanceType.ClearedLowApproach =>
                true,
            _ => aircraft.Phases.TrafficDirection is not null,
        };

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

        // EF to the runway the aircraft is already established on final for, when it is
        // inside the standard entry point (short final), is redundant — it is already
        // in the commanded state (7110.65 §3-8-1). Tearing down the live FinalApproach
        // to rebuild an entry from there places the fixed entry point behind the
        // aircraft and routes it on a bogus outbound re-entry (#228); real pilots just
        // continue (AIM §4-4-1). Continue the approach instead, preserving the live
        // final / glideslope / clearance state. An aircraft still outside the entry
        // point takes the normal (inbound) re-sequence below.
        if (
            entryLeg == PatternEntryLeg.Final
            && previousActivePhase is FinalApproachPhase
            && previousAssignedRunway is not null
            && string.Equals(previousAssignedRunway.Designator, runway.Designator, StringComparison.OrdinalIgnoreCase)
        )
        {
            var thresholdLl = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
            double angleOffFinal = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);
            double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, thresholdLl, runway.TrueHeading));
            double alongTrackOutbound = GeoMath.AlongTrackDistanceNm(aircraft.Position, thresholdLl, runway.TrueHeading.ToReciprocal());
            double standardEntryDistNm =
                CategoryPerformance.PatternAltitudeAgl(category) / GlideSlopeGeometry.FeetPerNm(GlideSlopeGeometry.AngleForCategory(category));
            if (
                angleOffFinal <= EstablishedFinalAngleOffDeg
                && crossTrackNm <= EstablishedFinalCrossTrackNm
                && alongTrackOutbound > 0
                && alongTrackOutbound < standardEntryDistNm
            )
            {
                return CommandDispatcher.Ok($"Continuing final{CommandDispatcher.RunwayLabel(aircraft)}");
            }
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
                    (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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
                (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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
                (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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

        // For Final entry inside the category straight-in floor: an aircraft with an inbound
        // component (a base leg, or a diagonal cut-in) is not flying a straight-in at all — it
        // is flying a pattern. The straight-in machinery has nothing to offer it: the diagonal
        // join above already bailed (its along-track cap is inside the minimum final), and the
        // fixed entry point is farther from the threshold than the aircraft, so navigating to it
        // means turning away from the field (#284). What such an aircraft actually does is
        // continue its base and roll onto the target runway's centerline — a runway change in
        // the pattern (7110.65 §3-10-5.c "CHANGE TO RUNWAY (n)"), not a straight-in and not the
        // instrument "sidestep" of the FinalApproachPhase branch above.
        //
        // Retarget the pattern instead: BasePhase from the aircraft's present position onto the
        // new centerline, reusing the ERB-no-distance machinery below. The straight-in floor is
        // deliberately NOT applied — AIM FIG 4-3-2 note 3 only requires the turn to final be
        // complete 1/4 mile out (MinPatternRetargetFinalNm), which jets and turboprops cannot
        // fly, so their retarget window is empty and they fall through to the reject below.
        //
        // An aircraft already flying FinalApproachPhase for the requested runway is excluded: it
        // is doing what EF asks, and mid base-to-final roll-out its heading is transiently far off
        // the final course, which would otherwise read as a "crossing" aircraft and re-insert a
        // base leg it has already flown. The same-runway continue / short-final guards own it.
        bool alreadyOnFinalForRequestedRunway =
            previousActivePhase is FinalApproachPhase
            && previousAssignedRunway is not null
            && string.Equals(previousAssignedRunway.Designator, runway.Designator, StringComparison.OrdinalIgnoreCase);

        bool isPatternRetarget = false;
        double retargetAlongTrackNm = 0.0;
        if (
            !aircraft.IsOnGround
            && entryLeg == PatternEntryLeg.Final
            && finalDistanceNm is null
            && !isCloseInFinal
            && finalEntryDistanceNm is null
            && !alreadyOnFinalForRequestedRunway
        )
        {
            var gate = EvaluatePatternRetarget(aircraft, runway, category, direction, groundLayout, ref waypoints);
            if (gate.IsRetarget)
            {
                isPatternRetarget = true;
                retargetAlongTrackNm = gate.AlongTrackNm;
                direction = gate.Side;
                waypoints = gate.Waypoints;
            }
        }

        // For Final entry: reject if the maneuver would create a 360° loop. Skipped when
        // the altitude-aware join is in play (finalEntryDistanceNm) — that entry is, by
        // construction, never placed beyond the aircraft's along-track, so the reverse-to-
        // a-far-entry loop this guards against cannot form. Also skipped for a pattern
        // retarget, which never leaves the aircraft's present position.
        // Compute the two heading changes: (1) turn from current heading to the
        // bearing toward the entry point, (2) turn from arrival bearing at the
        // entry point to the runway (approach) heading. If BOTH exceed 90°, the
        // total turn approaches 360° — the aircraft must reverse to the entry
        // point and then reverse again to align with final. That's a loop.
        if (!aircraft.IsOnGround && entryLeg == PatternEntryLeg.Final && !isCloseInFinal && finalEntryDistanceNm is null && !isPatternRetarget)
        {
            var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
            var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
                runway,
                (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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

            // EF must never route the aircraft outbound / farther from the field
            // (COMMANDS.md contract). Only an aircraft ALREADY tracking outbound — the downwind
            // leg, which parallels the runway in the departure direction (AIM 4-3-2.c.4) — may
            // legitimately be sent ahead to an entry point farther from the threshold and then
            // turned onto final; that is just the pattern. (Upwind and crosswind traffic is on
            // the departure side of the threshold, so its along-track-outbound is negative and
            // the `acAlongOutbound > 0` term below excludes it without needing the cone.)
            // An aircraft with any inbound component (final, base, or a diagonal cut-in) on the
            // approach side would have to turn AWAY from the runway to reach an entry point
            // behind it: the #228 "tour of the airspace" for an aligned aircraft, and the #284
            // outbound run down the final for a base leg. Reject and leave it on its current
            // approach. (A base leg that CAN fly the join was already retargeted above.)
            double acAlongOutbound = GeoMath.AlongTrackDistanceNm(
                aircraft.Position,
                new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                waypoints.FinalHeading.ToReciprocal()
            );
            double entryAlongOutbound = GeoMath.DistanceNm(eLat, eLon, waypoints.ThresholdLat, waypoints.ThresholdLon);
            double angleOffFinal = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);
            double crossTrackAbsNm = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    aircraft.Position,
                    new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon),
                    waypoints.FinalHeading
                )
            );
            bool trackingOutbound = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading.ToReciprocal()) <= OutboundTrackToleranceDeg;
            bool insideCategoryFloor = acAlongOutbound > 0 && acAlongOutbound < MinimumPerpendicularBaseFinalDistanceNm(category);
            bool entryLiesBehindAircraft = entryAlongOutbound > acAlongOutbound + OutboundFinalEntryMarginNm;

            // (1) Aligned, on the final approach course, genuine short final: the fixed entry point
            // sits behind the aircraft and flying there reverses it away from the runway — the #228
            // "tour of the airspace". An aligned aircraft merely OFFSET from the centerline (beyond
            // EstablishedFinalCrossTrackNm) is not on short final; it re-intercepts via the far
            // entry, which is the normal way back onto a final it is parallelling.
            bool onShortFinalInsideEntry =
                angleOffFinal <= MaxCloseInFinalAngleOffDeg(acAlongOutbound, category)
                && insideCategoryFloor
                && crossTrackAbsNm <= EstablishedFinalCrossTrackNm
                && entryLiesBehindAircraft;

            // (2) CROSSING the final approach course (a base leg, or a diagonal cut-in) inside the
            // category floor: it cannot fly a stabilized straight-in, and the retarget above already
            // declined it (too steep for its category, too high to descend, or the centerline is
            // behind it). The fixed entry point is behind it too, so routing there flies it outbound
            // down the final — issue #284. Reject; the controller re-vectors or goes around.
            //
            // Aircraft already tracking outbound (the downwind, AIM 4-3-2.c.4) are excluded: for
            // them the entry point ahead is simply the rest of the pattern, not a reversal.
            bool crossingFinalInsideFloor =
                angleOffFinal > MaxCloseInFinalAngleOffDeg(acAlongOutbound, category)
                && !trackingOutbound
                && insideCategoryFloor
                && entryLiesBehindAircraft;

            // Reject if the required arc exceeds what the aircraft can fly in
            // the available straight-line distance. The aircraft physically
            // cannot complete the turns before reaching the entry point.
            bool cannotCompleteTurns = (totalTurnDeg > 180) && (arcNm > distToEntry);

            if (onShortFinalInsideEntry || crossingFinalInsideFloor || cannotCompleteTurns)
            {
                Log.LogDebug(
                    "[EF-Reject] {Callsign}: shortFinal={ShortFinal} crossingInsideFloor={Crossing} cannotCompleteTurns={CannotTurn} — acAlong={AcAlong:F2}nm entryAlong={EntryAlong:F2}nm crossTrack={Cross:F2}nm angleOffFinal={AngleF:F0}° trackingOutbound={Tracking}",
                    aircraft.Callsign,
                    onShortFinalInsideEntry,
                    crossingFinalInsideFloor,
                    cannotCompleteTurns,
                    acAlongOutbound,
                    entryAlongOutbound,
                    crossTrackAbsNm,
                    angleOffFinal,
                    trackingOutbound
                );
                // A runway argument already retargeted AssignedRunway/DestinationRunway
                // above; restore the prior runway so a rejected EF leaves the aircraft
                // fully on its current approach.
                if (runwayId is not null && previousAssignedRunway is not null)
                {
                    aircraft.Phases.AssignedRunway = previousAssignedRunway;
                    NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, previousAssignedRunway.Designator);
                }
                return new CommandResult(false, "Unable, short final");
            }
        }

        // For wrong-side entry: midfield crossing then downwind entry.
        // For a pattern retarget: a base leg from the aircraft's present position onto the new
        // centerline (the ERB-no-distance shape), never a Final entry to a far waypoint.
        PatternEntryLeg effectiveEntryLeg =
            isOnWrongSide ? PatternEntryLeg.Downwind
            : isPatternRetarget ? PatternEntryLeg.Base
            : entryLeg;
        double? effectiveFinalDistanceNm =
            isPatternRetarget ? retargetAlongTrackNm
            : isCloseInFinal ? closeInAlongTrack
            : isOnWrongSide ? null
            : finalDistanceNm;

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
                (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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
        bool useAircraftPositionAsEntry = isCloseInFinal || isPatternRetarget;
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

        // Only now, past every reject, tear down the running phases. Clearing earlier left a
        // rejected entry (e.g. ERB "too close for base") with a skipped, dead phase chain and an
        // aircraft flying straight ahead with no phase at all — the command must be a no-op when
        // it fails. The same reasoning is why the close-in and retarget gates above fall through
        // rather than rejecting from inside their own blocks.
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft);
        aircraft.Phases.Clear(ctx);

        var (circuitSizeOv, circuitAltOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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
        phases.LandingClearance = standingClearance;
        phases.ClearedRunwayId = voidsLandingClearance ? null : aircraft.Phases.ClearedRunwayId;
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
            else if (circuitPhases.OfType<DownwindPhase>().FirstOrDefault() is { } joinDownwind)
            {
                // The crossing can drop a piston/helicopter inside the computed downwind track; have
                // the downwind re-intercept it so the base/final geometry rolls out on centerline.
                joinDownwind.RejoinTrack = true;
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
            aircraft.PendingWarnings.Add(
                $"{aircraft.Callsign} unable to descend for straight-in {RunwayIdentifier.ToDisplayDesignator(runway.Designator)} — too high"
            );
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
        int? altitudeOverride,
        AirportGroundLayout? groundLayout = null
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
                return new CommandResult(false, $"Runway {RunwayIdentifier.ToDisplayDesignator(runwayId)} not found at {airportId}");
            }

            aircraft.Phases ??= new PhaseList();
            bool runwayChanged = !string.Equals(aircraft.Phases.ClearedRunwayId, resolved.Designator, StringComparison.OrdinalIgnoreCase);
            aircraft.Phases.AssignedRunway = resolved;
            NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, resolved.Designator);
            // Changing runway clears altitude override — different runway, different TPA
            aircraft.Pattern.AltitudeOverrideFt = null;

            // A landing clearance names a runway (7110.65 §3-10-5), so sending the aircraft around a
            // pattern for a different one voids it — the controller has to clear it again. Without this
            // the aircraft keeps the clearance all the way to a touchdown it was never cleared for:
            // FinalApproachPhase.HasLandingClearance reads only the clearance type, never the runway.
            if (runwayChanged && aircraft.Phases.ClearedRunwayId is not null)
            {
                aircraft.Phases.LandingClearance = null;
                aircraft.Phases.ClearedRunwayId = null;
            }
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
            (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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

        // MLT/MRT during a go-around converts the climb-out into a pattern go-around: the aircraft
        // levels 300ft below pattern altitude (AIM 4-3-2) instead of running out to the 2000ft-AGL
        // self-clear. Clearing the upcoming phases leaves the go-around last, which is what arms
        // PhaseRunner's auto-cycle to append the circuit once the climb-out completes — that path
        // carries the pre-go-around landing intent, resolves the authored pattern altitude, and
        // clears the stale landing clearance, none of which a circuit spliced in here would do.
        if (!aircraft.IsOnGround && aircraft.Phases.CurrentPhase is GoAroundPhase activeGoAround)
        {
            var goAroundCtx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout ?? aircraft.Ground.Layout);
            activeGoAround.RetargetForPatternClimbOut(goAroundCtx, (int)(waypoints.PatternAltitude - GoAroundHelper.PatternHandoffMarginFt));
            aircraft.Phases.ReplaceUpcoming([]);

            Log.LogDebug(
                "[ChangePatternDirection] {Callsign}: retargeted active go-around as {Dir} pattern climb-out for {Rwy}",
                aircraft.Callsign,
                newDirection,
                runway.Designator
            );

            var dirStrGoAround = newDirection == PatternDirection.Left ? "left" : "right";
            return CommandDispatcher.Ok($"Make {dirStrGoAround} traffic{CommandDispatcher.RunwayLabel(aircraft)}");
        }

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
                // The crossing can drop the aircraft inside the computed downwind track; re-intercept it.
                if (rebuiltChain.OfType<DownwindPhase>().FirstOrDefault() is { } joinDownwind)
                {
                    joinDownwind.RejoinTrack = true;
                }
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

        // If no pattern phases exist yet (a departure told to stay in closed traffic), append a full
        // circuit after the current phase. A go-around never lands here — it returns above, letting
        // PhaseRunner's auto-cycle build the circuit once the climb-out completes.
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
    // axis from where the pattern lives. The signed offset keeps this
    // side-aware: an aircraft displaced toward the pattern side (positive) is
    // never wrong-side. A WrongSidePatternDeadbandNm deadband treats an
    // aircraft essentially on the extended centerline (e.g. climbing out on
    // the upwind leg, offset ≈ 0) as NOT wrong-side, so a direction change
    // there flies standard closed traffic instead of an immediate midfield
    // crossing. Only a non-pattern-side displacement beyond the deadband (e.g.
    // off the parallel runway) drives MidfieldCrossing insertion.
    private static bool IsOnWrongSideForPattern(LatLon position, RunwayInfo runway, PatternDirection direction)
    {
        TrueHeading crosswindHdg = direction == PatternDirection.Right ? runway.TrueHeading + 90.0 : runway.TrueHeading - 90.0;
        double patternSideOffset = GeoMath.AlongTrackDistanceNm(
            position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            crosswindHdg
        );
        return patternSideOffset < -WrongSidePatternDeadbandNm;
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

        // Early crosswind turn (issue #208): TC issued before the aircraft reached the upwind
        // leg — i.e. during the takeoff roll / initial climb (TakeoffPhase, which only ends at
        // 400 ft AGL). Arm the pending UpwindPhase so it turns crosswind the instant the leg
        // activates (~400 ft AGL, the safe-turn floor) rather than rejecting. Scoped to TC
        // (UpwindPhase); TD keeps its current rejection. No pending Upwind (plain IFR/SID
        // departure) falls through to the rejection below.
        if (
            typeof(T) == typeof(UpwindPhase)
            && aircraft.Phases?.CurrentPhase is TakeoffPhase
            && TryFindNextPendingPhase<UpwindPhase>(aircraft) is { } pendingUpwind
        )
        {
            pendingUpwind.TurnCrosswindArmed = true;
            Log.LogDebug("[PatternTurn] {Callsign}: armed early crosswind turn on pending Upwind (TC during takeoff)", aircraft.Callsign);
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
    internal static CommandResult TryExtendPattern(AircraftState aircraft, PatternEntryLeg? requestedLeg, AirportGroundLayout? groundLayout = null)
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
            // Not on a numbered leg yet (PatternEntry after ERD/ERC/ELD/ELC, HoldingShort,
            // LineUp, Takeoff, InitialClimb, FinalApproach pre-T/G, TouchAndGo). Pre-arm the
            // requested upcoming leg if it's queued so the extension fires automatically when
            // the aircraft reaches it. Upwind arms either the pending UpwindPhase or, if the
            // next circuit hasn't been appended yet, the AircraftPattern.ExtendNextUpwind
            // one-shot flag; Crosswind/Downwind arm the matching pending phase in the queue
            // (e.g. EXT DOWNWIND right after ERD 28R, before the aircraft flies onto downwind).
            if (leg == PatternEntryLeg.Upwind && TryArmNextUpwind(aircraft) is { } armed)
            {
                return armed;
            }
            if (leg is PatternEntryLeg.Crosswind or PatternEntryLeg.Downwind)
            {
                return TryArmFutureLeg(aircraft, leg);
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
            return TryArmFutureLeg(aircraft, leg);
        }

        if (currentOrder - requestedOrder > 1)
        {
            return new CommandResult(
                false,
                $"Cannot roll back to {leg.ToString().ToLowerInvariant()} from {current.ToString().ToLowerInvariant()} — use bare EXT or L360/R360 for spacing"
            );
        }

        return RebuildPatternFromLeg(aircraft, leg, groundLayout);
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
                // Bare EXT from a non-numbered-leg phase. During pattern entry (after
                // ERD/ERC/ELD/ELC) extend the leg the entry is leading into — the first
                // pending Upwind/Crosswind/Downwind in the queue. During a touch-and-go ground
                // roll, extend the next lap's upwind (queued, or via the ExtendNextUpwind
                // one-shot when the next circuit isn't appended yet).
                if (TryArmFirstPendingPatternLeg(aircraft) is { } armedEntry)
                {
                    return armedEntry;
                }
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
    /// Arm IsExtended on the first pending Upwind/Crosswind/Downwind in the queue — the leg a
    /// PatternEntryPhase is leading into. Used by bare EXT from a non-numbered-leg phase so that,
    /// right after an entry clearance, EXT extends the entry leg (ERD/ELD → downwind, ERC/ELC →
    /// crosswind, near-DER upwind override → upwind). Stops at and returns null on a leading
    /// Base/Final (ERB/EF entry — nothing extendable) or when no numbered leg is queued (a
    /// touch-and-go whose next circuit isn't appended yet), so the caller falls back to
    /// TryArmNextUpwind's append path or the rejection.
    /// </summary>
    private static CommandResult? TryArmFirstPendingPatternLeg(AircraftState aircraft)
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

            switch (p)
            {
                case UpwindPhase up:
                    up.IsExtended = true;
                    Log.LogDebug("[ExtendPattern] {Callsign}: armed IsExtended on pending entry UpwindPhase", aircraft.Callsign);
                    return CommandDispatcher.Ok("Extend upwind");
                case CrosswindPhase cw:
                    cw.IsExtended = true;
                    Log.LogDebug("[ExtendPattern] {Callsign}: armed IsExtended on pending entry CrosswindPhase", aircraft.Callsign);
                    return CommandDispatcher.Ok("Extend crosswind");
                case DownwindPhase dw:
                    dw.IsExtended = true;
                    Log.LogDebug("[ExtendPattern] {Callsign}: armed IsExtended on pending entry DownwindPhase", aircraft.Callsign);
                    return CommandDispatcher.Ok("Extend downwind");
                case BasePhase or FinalApproachPhase:
                    return null; // entry leads onto base/final — nothing extendable
            }
        }

        return null;
    }

    /// <summary>
    /// Pre-arm an extension on a leg the aircraft has not reached yet but that is already
    /// queued in the current circuit (e.g. EXT DOWNWIND while on upwind). The pending leg's
    /// IsExtended flag is set directly — the same one OnTick already honors when the aircraft
    /// reaches the leg — so the extension fires automatically without a second command. Only
    /// Crosswind and Downwind can be a future leg here: Upwind is the first leg, and Base/Final
    /// are rejected by both the parser and the caller. MNA cancels a pending pre-arm.
    /// </summary>
    private static CommandResult TryArmFutureLeg(AircraftState aircraft, PatternEntryLeg leg)
    {
        switch (leg)
        {
            case PatternEntryLeg.Crosswind when TryFindNextPendingPhase<CrosswindPhase>(aircraft) is { } pendingCrosswind:
                pendingCrosswind.IsExtended = true;
                Log.LogDebug("[ExtendPattern] {Callsign}: pre-armed IsExtended on pending CrosswindPhase", aircraft.Callsign);
                return CommandDispatcher.Ok("Extend crosswind");
            case PatternEntryLeg.Downwind when TryFindNextPendingPhase<DownwindPhase>(aircraft) is { } pendingDownwind:
                pendingDownwind.IsExtended = true;
                Log.LogDebug("[ExtendPattern] {Callsign}: pre-armed IsExtended on pending DownwindPhase", aircraft.Callsign);
                return CommandDispatcher.Ok("Extend downwind");
            default:
                return new CommandResult(false, $"No upcoming {leg.ToString().ToLowerInvariant()} leg to extend");
        }
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

    /// <summary>
    /// Classifies an aircraft's current position within a traffic pattern for the given runway, from its
    /// active phase: which leg it's flying, which side that leg sits on (null on final — a straight-in has
    /// no left/right base), and its distance to the threshold. Returns null when the aircraft is not flying
    /// a pattern or final-approach phase. Used by VFR pattern-leg traffic advisories to match a call like
    /// "two-mile right base for 28R" to the aircraft it describes.
    /// </summary>
    internal static (PatternEntryLeg Leg, PatternDirection? Side, double DistanceNm)? ClassifyPatternPosition(
        AircraftState aircraft,
        RunwayInfo runway
    )
    {
        var phase = aircraft.Phases?.CurrentPhase;
        PatternEntryLeg? leg = phase switch
        {
            UpwindPhase => PatternEntryLeg.Upwind,
            CrosswindPhase => PatternEntryLeg.Crosswind,
            DownwindPhase => PatternEntryLeg.Downwind,
            BasePhase => PatternEntryLeg.Base,
            FinalApproachPhase => PatternEntryLeg.Final,
            _ => null,
        };
        if (leg is null)
        {
            return null;
        }

        PatternDirection? side = phase switch
        {
            UpwindPhase up => up.Waypoints?.Direction,
            CrosswindPhase cw => cw.Waypoints?.Direction,
            DownwindPhase dw => dw.Waypoints?.Direction,
            BasePhase bp => bp.Waypoints?.Direction,
            _ => null,
        };

        double distanceNm =
            phase is FinalApproachPhase { DistanceToThresholdNm: var finalDist } && finalDist < double.MaxValue
                ? finalDist
                : GeoMath.DistanceNm(aircraft.Position, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude));

        return (leg.Value, side, distanceNm);
    }

    private static CommandResult RebuildPatternFromLeg(AircraftState aircraft, PatternEntryLeg leg, AirportGroundLayout? groundLayout = null)
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
            (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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

        // Queued modifier: clear SA arm on the upcoming Downwind, the following Base's
        // short-approach final distance, and any pre-armed leg extension (EXT CW/DW issued
        // before the aircraft reached that leg) — symmetric to TryMakeShortApproach / EXT.
        bool cleared = false;
        if (TryFindNextPendingPhase<CrosswindPhase>(aircraft) is { IsExtended: true } pendingCrosswind)
        {
            pendingCrosswind.IsExtended = false;
            cleared = true;
        }
        if (TryFindNextPendingPhase<DownwindPhase>(aircraft) is { } pendingDownwind)
        {
            pendingDownwind.ShortApproachArmed = false;
            pendingDownwind.IsExtended = false;
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

        // A 270 for spacing is flown the LONG way round — opposite the pattern's normal turn — so
        // the aircraft turns away from the runway, sweeps ~270°, and rolls out on the same course a
        // normal 90° pattern turn would have reached. Turning the pattern's own way instead ends
        // 180° off (on the next leg's reciprocal), which is the wrong-way bug this fixes.
        var turnDir = patternDir == PatternDirection.Left ? TurnDirection.Right : TurnDirection.Left;
        var turnPhase = new MakeTurnPhase { Direction = turnDir, TargetDegrees = 270 };
        aircraft.Phases.InsertAfterCurrent(turnPhase);

        var dirStr = turnDir == TurnDirection.Left ? "left" : "right";
        return CommandDispatcher.Ok($"Plan {dirStr} 270 at next turn");
    }

    internal static CommandResult TrySetPatternSize(AircraftState aircraft, double sizeNm, AirportGroundLayout? groundLayout = null)
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
                (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
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

        // S-turns are inserted before FinalApproachPhase or during downwind/base.
        var sturnPhase = new STurnPhase { InitialDirection = initialDirection, Count = count };

        // When S-turns are issued while already on final, resume the approach after them so
        // the aircraft re-captures the glideslope instead of advancing straight to Landing
        // far out and touching down short of the runway (same failure as a 360 on final).
        if (aircraft.Phases.CurrentPhase is FinalApproachPhase fa)
        {
            aircraft.Phases.InsertAfterCurrent(new List<Phase> { sturnPhase, fa.CloneForResume() });
        }
        else
        {
            aircraft.Phases.InsertAfterCurrent(sturnPhase);
        }

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
            // A 360 on final must resume the approach (re-capture the glideslope) before
            // landing, otherwise the chain advances straight to LandingPhase far out and the
            // aircraft descends at the category rate to the ground, touching down short of the
            // threshold. CloneForResume realigns with the final course and consumes the
            // go-around roll — mirrors the S-turn-for-spacing resume.
            FinalApproachPhase fa => fa.CloneForResume(),
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
        EnsurePatternMode(aircraft);

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
        EnsurePatternMode(aircraft);

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
        EnsurePatternMode(aircraft);

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
        EnsurePatternMode(aircraft);

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
            TurnDirection.Left => "left 360s",
            TurnDirection.Right => "right 360s",
            _ => "hover",
        };
        return CommandDispatcher.Ok($"Hold at {PhraseologyVerbalizer.FixDisplayTextUpper(fixName)}, {dirStr}");
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
    /// Tolerances for treating an aircraft as "already established on final" for the
    /// requested runway, so a redundant same-runway EF is a graceful no-op (continue
    /// the approach) rather than a phase rebuild. Keyed off the aircraft actually being
    /// in FinalApproachPhase for that runway; these guard against a nonsensically
    /// off-course "final" phase.
    ///
    /// The angle tolerance spans the whole base-to-final roll-out (a pattern base is
    /// perpendicular, so the transient heading reaches ~90° off the final course before the
    /// turn completes). An aircraft rolling onto the centerline it was asked to join is already
    /// complying; the cross-track bound is what actually establishes it is on that final.
    /// </summary>
    private const double EstablishedFinalAngleOffDeg = 90.0;
    private const double EstablishedFinalCrossTrackNm = 0.5;

    /// <summary>
    /// Slack (NM) allowed before an EF Final entry point is considered to lie farther
    /// outbound than the aircraft. Beyond this the reposition would route the aircraft
    /// away from the field, which EF must never do (COMMANDS.md contract) — reject
    /// "Unable, short final" instead and leave the aircraft on its current approach.
    /// </summary>
    private const double OutboundFinalEntryMarginNm = 0.1;

    /// <summary>
    /// Angular tolerance (degrees) between the aircraft's track and the runway's outbound
    /// (reciprocal) axis for it to count as "already tracking outbound". AIM 4-3-2.c defines the
    /// traffic-pattern legs by track: the downwind parallels the runway in the departure direction,
    /// base is perpendicular, final is along the landing direction. Only a downwind aircraft may be
    /// routed to a Final entry point farther from the threshold than its present position — for it
    /// that is simply the rest of the pattern. Anything with an inbound component would have to turn
    /// away from the field. 60° leaves a 30° margin against a textbook 90° base leg, and admits the
    /// 45° downwind entry of AIM 4-3-3. Upwind and crosswind traffic sits on the departure side of
    /// the threshold and is excluded by its negative along-track-outbound, not by this cone.
    /// </summary>
    private const double OutboundTrackToleranceDeg = 60.0;

    /// <summary>
    /// Minimum along-track (NM) at which a pattern retarget can still complete its turn to final.
    /// AIM FIG 4-3-2 note 3 requires only that the turn to final be complete at least 1/4 mile
    /// from the runway. This is light-aircraft pattern geometry.
    /// </summary>
    private const double MinPatternTurnToFinalNm = 0.25;

    /// <summary>
    /// Along-track floor (NM) for an EF pattern retarget. Pistons and helicopters fly the AIM
    /// 1/4-mile turn-to-final; jets and turboprops cannot fly a stabilized final that short, so
    /// they keep the straight-in floor. That makes their retarget window
    /// (<see cref="MinPatternRetargetFinalNm"/>, <see cref="MinimumPerpendicularBaseFinalDistanceNm"/>)
    /// empty by construction: a jet inside its straight-in floor rejects "Unable, short final"
    /// rather than being retargeted onto a final it cannot stabilize on.
    /// </summary>
    private static double MinPatternRetargetFinalNm(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Piston or AircraftCategory.Helicopter => MinPatternTurnToFinalNm,
            _ => MinimumPerpendicularBaseFinalDistanceNm(category),
        };

    /// <summary>
    /// Outcome of the EF pattern-retarget gate. <see cref="IsRetarget"/> false means the aircraft
    /// keeps the straight-in machinery (and, inside the category floor, the never-outbound reject).
    /// </summary>
    /// <param name="IsRetarget">True when EF should degrade to a base entry from the present position.</param>
    /// <param name="AlongTrackNm">Along-track distance from the threshold: the final segment the base rolls out onto.</param>
    /// <param name="Side">Base-leg side, derived from which side of the centerline the aircraft is on.</param>
    /// <param name="Waypoints">Pattern geometry recomputed for <paramref name="Side"/>.</param>
    private readonly record struct PatternRetargetGate(bool IsRetarget, double AlongTrackNm, PatternDirection Side, PatternWaypoints Waypoints);

    /// <summary>
    /// Decides whether an <c>EF</c> should degrade into a base entry (see the call site in
    /// <c>TryEnterPattern</c>). Engages only for an aircraft crossing the final approach course,
    /// not already tracking outbound, inside the category straight-in floor but outside the AIM
    /// FIG 4-3-2 note 3 turn-to-final minimum, still closing on the target centerline, and able to
    /// descend over the remaining base + final. Purely a decision — mutates nothing.
    /// </summary>
    /// <param name="waypoints">
    /// Lazily computed pattern geometry, shared with the caller so it isn't recomputed. Replaced
    /// with geometry for the retargeted side when the gate engages.
    /// </param>
    private static PatternRetargetGate EvaluatePatternRetarget(
        AircraftState aircraft,
        RunwayInfo runway,
        AircraftCategory category,
        PatternDirection direction,
        AirportGroundLayout? groundLayout,
        ref PatternWaypoints? waypoints
    )
    {
        var airportRunways = NavigationDatabase.Instance.GetRunways(runway.AirportId);
        var (sizeOv, altOv) = PatternGeometry.ResolveAuthoredOverrides(
            runway,
            (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runway.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );
        waypoints ??= PatternGeometry.Compute(runway, category, direction, sizeOv, altOv, airportRunways);

        var threshold = new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon);
        double alongTrackNm = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, waypoints.FinalHeading.ToReciprocal());
        double angleOffFinal = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);
        double angleOffOutbound = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading.ToReciprocal());

        // Which side of the target centerline is the aircraft on, and is it still flying toward it?
        // Positive right-offset ⇒ a right base. An aircraft that has already crossed the centerline
        // (or is diverging from it) cannot fly a normal base-to-final onto it — it would need an
        // S-turn back across a final it has already passed (AIM 4-3-3 FIG 4-3-3 note 7).
        double rightOffsetNm = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading + 90.0);
        TrueHeading towardCenterline = rightOffsetNm >= 0 ? runway.TrueHeading - 90.0 : runway.TrueHeading + 90.0;
        bool closingOnCenterline = aircraft.TrueHeading.AbsAngleTo(towardCenterline) < 90.0;

        bool engages =
            (angleOffFinal > MaxCloseInFinalAngleOffDeg(alongTrackNm, category))
            && (angleOffOutbound > OutboundTrackToleranceDeg)
            && (alongTrackNm > MinPatternRetargetFinalNm(category))
            && (alongTrackNm < MinimumPerpendicularBaseFinalDistanceNm(category))
            && closingOnCenterline
            && CanDescendOverBaseAndFinal(aircraft, runway, category, Math.Abs(rightOffsetNm), alongTrackNm);

        if (!engages)
        {
            return new PatternRetargetGate(false, 0.0, direction, waypoints);
        }

        // The base leg's side is a fact about where the aircraft is, not about the runway's default
        // pattern side. EF infers direction from the runway (28L with 28R present → Left), but an
        // aircraft north of the 28L centerline is on a RIGHT base for 28L; building a left base
        // there would be a wrong-side base.
        var side = rightOffsetNm >= 0 ? PatternDirection.Right : PatternDirection.Left;
        var retargetWaypoints = PatternGeometry.Compute(runway, category, side, sizeOv, altOv, airportRunways);

        Log.LogDebug(
            "[EF-Retarget] {Callsign}: alongTrack={Along:F2}nm rightOffset={Cross:F2}nm angleOffFinal={AngleFinal:F0}° angleOffOutbound={AngleOut:F0}° side={Side}",
            aircraft.Callsign,
            alongTrackNm,
            rightOffsetNm,
            angleOffFinal,
            angleOffOutbound,
            side
        );

        return new PatternRetargetGate(true, alongTrackNm, side, retargetWaypoints);
    }

    /// <summary>
    /// Can the aircraft descend from its current altitude to runway elevation over the remaining
    /// base + final path at the category pattern descent rate and base speed? Mirrors the
    /// ERB/ELB-no-distance altitude check — an aircraft well above TPA should be descended first
    /// (AIM 4-3-3: pattern entry at TPA), so rejecting here prompts a DM.
    /// </summary>
    private static bool CanDescendOverBaseAndFinal(
        AircraftState aircraft,
        RunwayInfo runway,
        AircraftCategory category,
        double crossTrackAbsNm,
        double alongTrackNm
    )
    {
        double totalPathNm = crossTrackAbsNm + alongTrackNm;
        double baseSpeedKt = CategoryPerformance.BaseSpeed(category);
        double pathMinutes = totalPathNm / (baseSpeedKt / 60.0);
        double maxDescentFt = CategoryPerformance.PatternDescentRate(category) * pathMinutes;
        return (aircraft.Altitude - runway.ElevationFt) <= maxDescentFt;
    }

    /// <summary>
    /// Cross-track deadband (NM) for the MLT/MRT wrong-side test. An aircraft within this
    /// distance of the runway's extended centerline is treated as essentially ON the
    /// centerline (e.g. climbing out on the upwind leg), NOT on the wrong side — a
    /// direction change there flies standard closed traffic (upwind → crosswind →
    /// downwind) and the crosswind turn naturally carries it to the correct pattern side,
    /// so no midfield crossing is needed. Only a displacement to the non-pattern side
    /// beyond this deadband (e.g. an aircraft off the parallel runway) is a genuine
    /// wrong-side that warrants crossing midfield. ~0.1 NM ≈ 600 ft (runway is 150 ft wide).
    /// </summary>
    private const double WrongSidePatternDeadbandNm = 0.1;

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
        return CommandDispatcher.Ok($"Sidestep, runway {RunwayIdentifier.ToDisplayDesignator(newRunway.Designator)}");
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
    /// Ensure the aircraft is in pattern mode, inferring the side the same way a go-around does
    /// (<see cref="GoAroundHelper.ResolvePatternIntent"/>): the controller's stated intent, then the
    /// pattern legs the aircraft has flown, then the runway's L/R side. Callers are the option
    /// clearances (TG/SG/LA/COPT), which are VFR-only, so the left-traffic fallback here only
    /// covers the IFR case that the dispatcher already rejects.
    /// </summary>
    private static void EnsurePatternMode(AircraftState aircraft)
    {
        aircraft.Phases!.TrafficDirection = GoAroundHelper.ResolvePatternIntent(aircraft) ?? PatternDirection.Left;
    }

    internal static CommandResult TryGoAround(GoAroundCommand ga, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (aircraft.Phases is null || aircraft.Phases.IsComplete)
        {
            return new CommandResult(false, "Go around not applicable");
        }

        // A controller-commanded go-around always overrides a CLANDF forced landing.
        aircraft.Phases.ForceLanding = false;

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

        bool hasAtcOverride = ga.AssignedMagneticHeading is not null || ga.TargetAltitude is not null;

        // Apply the same pattern default the automatic go-around uses (GoAroundHelper.Trigger), so a
        // controller-issued GA behaves identically to one the simulation triggers itself. A VFR
        // aircraft cleared to land has had both direction fields dropped by CLAND; the side it was
        // flying is recovered from its own pattern legs. An assigned heading or altitude means the
        // controller is flying the climb-out themselves, so no pattern is inferred — an aircraft
        // already established in one keeps its direction regardless.
        if (!hasAtcOverride)
        {
            aircraft.Phases.TrafficDirection = GoAroundHelper.ResolvePatternIntent(aircraft);
        }

        bool isGaPattern = aircraft.Phases.TrafficDirection is not null;
        var gaCtx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout ?? aircraft.Ground.Layout);

        // Build MAP phases for instrument approaches without an ATC override — an assigned
        // heading or altitude replaces the published missed approach.
        var missedApproachPhases = (!isGaPattern && !hasAtcOverride) ? ApproachCommandHandler.BuildMissedApproachPhases(aircraft) : [];

        var goAround = new GoAroundPhase
        {
            AssignedMagneticHeading = ga.AssignedMagneticHeading,
            TargetAltitude = ga.TargetAltitude ?? GoAroundHelper.ResolveClimbOutAltitude(gaCtx, isGaPattern, missedApproachPhases),
            ReenterPattern = isGaPattern,
            NextLandingFullStop = GoAroundHelper.CaptureLandingFullStopIntent(aircraft.Phases),
        };

        GoAroundHelper.InstallGoAroundPhases(gaCtx, goAround, missedApproachPhases);

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

    internal static CommandResult TryClearedToLand(ClearedToLandCommand ctl, AircraftState aircraft, AirportGroundLayout? groundLayout)
    {
        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (aircraft.IsOnGround)
        {
            return new CommandResult(false, "Cannot clear to land — aircraft is on the ground");
        }

        bool following = aircraft.Approach.FollowingCallsign is not null;

        // Immediate clearance: the aircraft is already established on an approach or
        // pattern with an assigned runway. Replace the approach ending with a landing.
        if (aircraft.Phases.AssignedRunway is { } assignedRunway)
        {
            if (
                (ctl.RunwayId is not null)
                && !string.Equals(RunwayIdentifier.NormalizeDesignator(ctl.RunwayId), assignedRunway.Designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                // Low approach on runway A, then cleared to land on a different, diverging runway B
                // (7110.65 §3-10-5 "change to runway", issue #292). Fly the low pass, then a sharp
                // turn onto B's final and land. Only when the aircraft is on/set up for a low approach;
                // any other runway mismatch keeps the strict reject below.
                if (IsOnLowApproach(aircraft))
                {
                    return TryRetargetLowApproachToRunway(ctl, aircraft, assignedRunway, groundLayout);
                }

                return new CommandResult(
                    false,
                    $"Cannot clear for runway {RunwayIdentifier.ToDisplayDesignator(ctl.RunwayId)} — {aircraft.Callsign} is established for runway {RunwayIdentifier.ToDisplayDesignator(assignedRunway.Designator)}"
                );
            }

            var isHeliCtl = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
            Phase landingCtl = isHeliCtl ? new HelicopterLandingPhase() : new LandingPhase();
            if (CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, landingCtl))
            {
                aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
                aircraft.Phases.ClearedRunwayId = assignedRunway.Designator;
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

            if (!following)
            {
                return new CommandResult(false, "Cannot clear to land — no pending approach (assign an approach or pattern entry first)");
            }
            // A following aircraft with a runway but no pending approach falls through to
            // the deferred (armed) clearance below.
        }

        // Deferred (armed) clearance: a following aircraft has no runway/approach yet
        // because it is still pursuing its lead. Remember the clearance — VfrFollowPhase
        // applies it when the follower joins the pattern. A named runway is matched
        // against the lead's runway at join; a bare CLAND inherits the lead's runway.
        if (following)
        {
            aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
            aircraft.Phases.ClearedRunwayId = ctl.RunwayId is null ? null : RunwayIdentifier.NormalizeDesignator(ctl.RunwayId);
            if (ctl.NoDelete)
            {
                aircraft.Ground.AutoDeleteExempt = true;
            }
            string rwyClause = ctl.RunwayId is not null ? $" runway {RunwayIdentifier.ToDisplayDesignator(ctl.RunwayId)}" : "";
            return CommandDispatcher.Ok($"Cleared to land{rwyClause}, will land behind {aircraft.Approach.FollowingCallsign}");
        }

        if (ctl.RunwayId is not null)
        {
            return new CommandResult(
                false,
                $"Cannot clear for runway {RunwayIdentifier.ToDisplayDesignator(ctl.RunwayId)} — {aircraft.Callsign} has no approach; use EF {RunwayIdentifier.ToDisplayDesignator(ctl.RunwayId)} or have it follow traffic first"
            );
        }

        return new CommandResult(false, "Cannot clear to land — no runway assigned");
    }

    // --- Low approach on runway A, then cleared to land on a diverging runway B (issue #292) ---
    // 7110.65 §3-10-5 subpara 3 "change to runway"; AIM §4-3-12.1 authorizes the low-approach turn
    // ("Unless otherwise authorized by ATC, the low approach should be made straight ahead...").

    // Divergence band for the low-approach runway change. Below the minimum the runways are
    // near-parallel — that is a sidestep (IsParallelSidestepCandidate/ApplySidestep), not this
    // maneuver. Above the maximum the heading change exceeds a single continuous base-leg turn (>90°
    // puts the new final behind the abeam line) and needs a downwind/base re-entry, so the pilot
    // declines. 90° is the clean textbook base-to-final edge (aviation review).
    private const double MinRetargetDivergenceDeg = 15.0;
    private const double MaxRetargetDivergenceDeg = 90.0;

    // Roll-out distance on the new runway's final that the turn is aimed at. This is a SHORT final by
    // design: for diverging runways that share a corner (KOAK 28R/33) the aircraft on runway A's final
    // is always well left of runway B's final, so B's final is only ever a tight, curved intercept —
    // never a 1 nm straight-in. A small gate lets the aircraft fly the low approach down A and make a
    // genuinely late, sharp turn onto B's short final, rather than peeling off a mile out.
    private const double RetargetFinalGateNm = 0.5;

    private static bool IsOnLowApproach(AircraftState aircraft)
    {
        var phases = aircraft.Phases;
        if (phases is null)
        {
            return false;
        }

        if (phases.LandingClearance == ClearanceType.ClearedLowApproach)
        {
            return true;
        }

        return HasLowApproachPhase(phases);
    }

    private static bool HasLowApproachPhase(PhaseList phases) =>
        phases.CurrentPhase is LowApproachPhase
        || phases.Phases.Any(p => p is LowApproachPhase && p.Status is PhaseStatus.Pending or PhaseStatus.Active);

    /// <summary>
    /// Feasibility of turning a low approach on runway <paramref name="a"/> onto a landing on the
    /// diverging runway <paramref name="b"/> (issue #292). Returns the gate point on B's final (the
    /// roll-out target) when feasible. Reuses the EF turn-radius model so the maneuverability check is
    /// consistent with pattern entry. Evaluated at the command instant; the actual turn follows the
    /// low pass, so the along-track gate check (the aircraft must still be outbound of the gate) is the
    /// load-bearing one — the low pass then carries the aircraft down to that gate.
    /// </summary>
    internal static (bool Feasible, string Reason, double GateLat, double GateLon) EvaluateLowApproachRetargetFeasibility(
        AircraftState aircraft,
        RunwayInfo a,
        RunwayInfo b
    )
    {
        string bLabel = RunwayIdentifier.ToDisplayDesignator(b.Designator);

        // Gate point on B's final approach course, RetargetFinalGateNm out from B's threshold.
        TrueHeading bFinalOutbound = b.TrueHeading.ToReciprocal();
        var gate = GeoMath.ProjectPoint(b.ThresholdLatitude, b.ThresholdLongitude, bFinalOutbound, RetargetFinalGateNm);

        // (1) Divergence band.
        double divergence = a.TrueHeading.AbsAngleTo(b.TrueHeading);
        if (divergence < MinRetargetDivergenceDeg)
        {
            return (false, $"Unable, runway {bLabel} is too close to sidestep from a low approach", gate.Lat, gate.Lon);
        }
        if (divergence > MaxRetargetDivergenceDeg)
        {
            return (false, $"Unable, will re-enter the pattern for runway {bLabel}", gate.Lat, gate.Lon);
        }

        // (2) Intersecting runways: a diverging turn across a physical crossing at low altitude is
        //     unsafe. Non-intersecting pairs (e.g. KOAK 28R/33) pass.
        if (
            SegmentsIntersect(
                new LatLon(a.ThresholdLatitude, a.ThresholdLongitude),
                new LatLon(a.EndLatitude, a.EndLongitude),
                new LatLon(b.ThresholdLatitude, b.ThresholdLongitude),
                new LatLon(b.EndLatitude, b.EndLongitude)
            )
        )
        {
            return (false, $"Unable, runway {bLabel} intersects the low-approach runway", gate.Lat, gate.Lon);
        }

        // (3) Converging the wrong way / already past B's final: the aircraft must still be on the
        //     approach side of the gate so it can turn onto B's final without turning away from the field.
        double alongPastGate = GeoMath.AlongTrackDistanceNm(aircraft.Position, new LatLon(gate.Lat, gate.Lon), bFinalOutbound);
        if (alongPastGate <= 0)
        {
            return (false, $"Unable, past the final for runway {bLabel}", gate.Lat, gate.Lon);
        }

        // (4) Turn feasibility: can the aircraft roll out on B's final at the gate given its turn radius
        //     and the distance available? Use the rate the maneuver actually flies — the appended
        //     PatternEntry/FinalApproach turn at the airframe-default standard rate (3°/s), not the
        //     tighter 12°/s pattern envelope the EF loop-check uses — so the gate only approves turns
        //     the aircraft can really complete (conservative bias, aviation review).
        const double turnRateDegs = 3.0;
        double speedKts = Math.Max(aircraft.GroundSpeed, 60);
        double radiusNm = (speedKts / 3600.0) / (turnRateDegs * Math.PI / 180.0);

        double bearingToGate = GeoMath.BearingTo(aircraft.Position, new LatLon(gate.Lat, gate.Lon));
        double turnToGate = aircraft.TrueHeading.AbsAngleTo(new TrueHeading(bearingToGate));
        double turnAtGate = new TrueHeading(bearingToGate).AbsAngleTo(b.TrueHeading);
        double distToGate = GeoMath.DistanceNm(aircraft.Position, new LatLon(gate.Lat, gate.Lon));
        double arcNm = (turnToGate + turnAtGate) * Math.PI / 180.0 * radiusNm;
        if ((turnToGate + turnAtGate) > 180 && arcNm > distToGate)
        {
            return (false, $"Unable, can't make the turn to runway {bLabel}", gate.Lat, gate.Lon);
        }

        return (true, "", gate.Lat, gate.Lon);
    }

    /// <summary>
    /// Straddle test: do segments p1-p2 and p3-p4 cross? Treats lat/lon as planar, adequate at
    /// runway scale. Ignores collinear/touching edge cases (runways that merely share an endpoint
    /// are not treated as intersecting).
    /// </summary>
    private static bool SegmentsIntersect(LatLon p1, LatLon p2, LatLon p3, LatLon p4)
    {
        static double Cross(LatLon o, LatLon a, LatLon b) => ((a.Lat - o.Lat) * (b.Lon - o.Lon)) - ((a.Lon - o.Lon) * (b.Lat - o.Lat));

        double d1 = Cross(p3, p4, p1);
        double d2 = Cross(p3, p4, p2);
        double d3 = Cross(p1, p2, p3);
        double d4 = Cross(p1, p2, p4);

        return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
    }

    /// <summary>
    /// CLAND &lt;B&gt; issued during a low approach on runway A, where B is a different diverging runway
    /// (issue #292). Flies the low pass, then a sharp turn onto B's final and a landing on B. Reassigns
    /// the runway and re-issues the landing clearance for B (7110.65 §3-10-5 change-to-runway). Rejects
    /// with a pilot "unable" when the geometry is infeasible, leaving the low approach intact.
    /// </summary>
    private static CommandResult TryRetargetLowApproachToRunway(
        ClearedToLandCommand ctl,
        AircraftState aircraft,
        RunwayInfo assignedRunway,
        AirportGroundLayout? groundLayout
    )
    {
        var airportId = ResolveAirportContext(aircraft);
        if (string.IsNullOrEmpty(airportId))
        {
            return new CommandResult(false, "No airport context to resolve runway");
        }

        var runwayB = NavigationDatabase.Instance.GetRunway(airportId, ctl.RunwayId!);
        if (runwayB is null)
        {
            return new CommandResult(false, $"Runway {RunwayIdentifier.ToDisplayDesignator(ctl.RunwayId!)} not found at {airportId}");
        }

        var category = AircraftCategorization.Categorize(aircraft.AircraftType);

        // A low approach then a tight turn onto a diverging runway's short final is a light-aircraft
        // VFR-pattern maneuver flown low and slow. A jet or turboprop can't fly it (aviation review),
        // so restrict it to pistons and helicopters.
        if (category is not (AircraftCategory.Piston or AircraftCategory.Helicopter))
        {
            return new CommandResult(false, "Unable, low-approach runway change is a light-aircraft maneuver");
        }

        var feasibility = EvaluateLowApproachRetargetFeasibility(aircraft, assignedRunway, runwayB);
        if (!feasibility.Feasible)
        {
            return new CommandResult(false, feasibility.Reason);
        }

        var phases = aircraft.Phases!;
        if (!HasLowApproachPhase(phases))
        {
            return new CommandResult(false, "Cannot change landing runway — not on a low approach");
        }

        // Make the low approach on A the current phase. It caches A's threshold/heading on OnStart, so
        // this must run while AssignedRunway is still A. If already current this is a no-op.
        if (phases.CurrentPhase is not LowApproachPhase)
        {
            phases.SkipTo<LowApproachPhase>(CommandDispatcher.BuildMinimalContext(aircraft));
        }
        if (phases.CurrentPhase is not LowApproachPhase lowApproach)
        {
            return new CommandResult(false, "Cannot change landing runway — not on a low approach");
        }

        lowApproach.EnableRetargetToDifferentRunway(feasibility.GateLat, feasibility.GateLon, runwayB.TrueHeading, RetargetFinalGateNm);

        // Build the runway-B tail: pattern entry onto B's final at the gate, then final + landing.
        var directionB = GoAroundHelper.InferDefaultPatternDirection(runwayB) ?? PatternDirection.Left;
        var airportRunwaysB = NavigationDatabase.Instance.GetRunways(runwayB.AirportId);
        var (sizeOvB, altOvB) = PatternGeometry.ResolveAuthoredOverrides(
            runwayB,
            (groundLayout ?? aircraft.Ground.Layout)?.FindRunway(runwayB.Designator),
            aircraft.Pattern.SizeOverrideNm,
            aircraft.Pattern.AltitudeOverrideFt
        );

        double gsAngle = GlideSlopeGeometry.AngleForCategory(category);
        double entryAltB = GlideSlopeGeometry.AltitudeAtDistance(RetargetFinalGateNm, runwayB.ElevationFt, gsAngle);
        var patternEntryB = new PatternEntryPhase
        {
            EntryLat = feasibility.GateLat,
            EntryLon = feasibility.GateLon,
            PatternAltitude = entryAltB,
            Kind = PatternEntryKind.Final,
        };

        var circuitB = PatternBuilder.BuildCircuit(
            runwayB,
            category,
            directionB,
            PatternEntryLeg.Final,
            touchAndGo: false,
            RetargetFinalGateNm,
            sizeOvB,
            altOvB,
            airportRunwaysB
        );

        var bTail = new List<Phase> { patternEntryB };
        bTail.AddRange(circuitB);
        phases.ReplaceUpcoming(bTail);

        // Retarget runway + re-issue the landing clearance for B (7110.65 §3-10-5). AssignedRunway
        // flips to B only now, after the low approach cached A's geometry above.
        phases.AssignedRunway = runwayB;
        NavigationCommandHandler.SyncDestinationRunwayWithActiveStar(aircraft, runwayB.Designator);
        phases.LandingClearance = ClearanceType.ClearedToLand;
        phases.ClearedRunwayId = runwayB.Designator;
        phases.LandingRunwayChangedFromLowApproach = true;
        phases.TrafficDirection = null;
        aircraft.Pattern.TrafficDirection = null;
        if (ctl.NoDelete)
        {
            aircraft.Ground.AutoDeleteExempt = true;
        }

        // 7110.65 §3-10-5 subpara 3: restate the runway number to emphasize the changed runway —
        // "CHANGE TO RUNWAY (n), RUNWAY (n) CLEARED TO LAND".
        string bDisplay = RunwayIdentifier.ToDisplayDesignator(runwayB.Designator);
        return CommandDispatcher.Ok($"Change to runway {bDisplay}, runway {bDisplay}, cleared to land");
    }

    /// <summary>
    /// CLANDF — instructor/RPO forced landing. Grants landing clearance, commits a full-stop
    /// landing, and raises <see cref="PhaseList.ForceLanding"/> so FinalApproachPhase and
    /// LandingPhase suppress every automatic go-around and drive the aircraft to a touchdown
    /// regardless of energy state. RPO-only (rejected in solo training). Canceled by GA, by
    /// cancelling the landing clearance (CLC/CTLC), or by touchdown.
    /// </summary>
    internal static CommandResult TryForceLanding(ForceLandingCommand flc, AircraftState aircraft, DispatchContext ctx)
    {
        if (ctx.SoloTrainingMode)
        {
            return new CommandResult(false, "CLANDF is RPO-only; clear the aircraft to land with CLAND in solo training");
        }

        if (aircraft.Phases is null)
        {
            return new CommandResult(false, "Aircraft has no active phase sequence");
        }

        if (aircraft.IsOnGround)
        {
            return new CommandResult(false, "Cannot force landing — aircraft is on the ground");
        }

        if (aircraft.Phases.AssignedRunway is not { } assignedRunway)
        {
            return new CommandResult(false, "Cannot force landing — no runway assigned");
        }

        // Commit a full-stop LandingPhase ending (replacing any pending TG/SAG/low-approach so
        // CLANDF always lands), mirroring CLAND. When the aircraft is already on final or in the
        // landing phase there is no pending ending to replace — the active phase reads the
        // ForceLanding flag next tick, so a failed replace there is not an error.
        bool alreadyLandingOrFinal = aircraft.Phases.CurrentPhase is FinalApproachPhase or LandingPhase or HelicopterLandingPhase;
        bool isHeli = AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter;
        Phase landing = isHeli ? new HelicopterLandingPhase() : new LandingPhase();

        if (aircraft.Phases.CurrentPhase is GoAroundPhase)
        {
            // CLANDF overrides an in-progress go-around: cancel the climb-out and re-establish the
            // aircraft on final for the assigned runway. A go-around wipes every pending landing
            // phase (GoAroundHelper.InstallGoAroundPhases → ReplaceUpcoming), so there is nothing
            // for ReplaceApproachEnding to swap; install a fresh final + full-stop landing and
            // advance to it. ForceLanding (set below) suppresses the auto-go-around
            // FinalApproachPhase would otherwise re-trigger, driving a touchdown from any energy state.
            var reversalCtx = CommandDispatcher.BuildMinimalContext(aircraft);
            aircraft.Phases.ReplaceUpcoming([new FinalApproachPhase { SkipInterceptCheck = true }, landing]);
            aircraft.Phases.AdvanceToNext(reversalCtx);
            FlightPhysics.NotifyPhaseAdvanced(aircraft);
        }
        else if (!CommandDispatcher.ReplaceApproachEnding(aircraft.Phases, landing) && !alreadyLandingOrFinal)
        {
            return new CommandResult(false, "Cannot force landing — no approach or pattern to land from (assign an approach or pattern entry first)");
        }

        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = assignedRunway.Designator;
        aircraft.Phases.ForceLanding = true;
        aircraft.Phases.TrafficDirection = null;
        aircraft.Pattern.TrafficDirection = null;

        return CommandDispatcher.Ok($"Forcing landing{CommandDispatcher.RunwayLabel(aircraft)}");
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
            return new CommandResult(false, $"Landing runway {RunwayIdentifier.ToDisplayDesignator(runway.Designator)} not found in ground layout");
        }

        // Find the crossing runway in the ground layout
        var crossingGround = groundLayout.FindGroundRunway(lahso.CrossingRunwayId);
        if (crossingGround is null)
        {
            return new CommandResult(
                false,
                $"Crossing runway {RunwayIdentifier.ToDisplayDesignator(lahso.CrossingRunwayId)} not found in ground layout"
            );
        }

        // Compute the intersection point
        var intersection = RunwayIntersectionCalculator.FindIntersection(landingGround, crossingGround);
        if (intersection is null)
        {
            return new CommandResult(
                false,
                $"Runway {RunwayIdentifier.ToDisplayDesignator(runway.Designator)} does not intersect runway {RunwayIdentifier.ToDisplayDesignator(lahso.CrossingRunwayId)}"
            );
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
            return new CommandResult(
                false,
                $"Hold-short point too close to threshold for runway {RunwayIdentifier.ToDisplayDesignator(lahso.CrossingRunwayId)}"
            );
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

        return CommandDispatcher.Ok(
            $"Cleared to land{CommandDispatcher.RunwayLabel(aircraft)}, hold short runway {RunwayIdentifier.ToDisplayDesignator(lahso.CrossingRunwayId)}"
        );
    }

    internal static CommandResult TryCancelLandingClearance(AircraftState aircraft)
    {
        if (aircraft.Phases is null || aircraft.Phases.LandingClearance is null)
        {
            return new CommandResult(false, "No landing clearance to cancel");
        }

        aircraft.Phases.LandingClearance = null;
        aircraft.Phases.ClearedRunwayId = null;
        // Cancelling the (CLANDF-granted) landing clearance also lifts the forced-landing override.
        aircraft.Phases.ForceLanding = false;
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
