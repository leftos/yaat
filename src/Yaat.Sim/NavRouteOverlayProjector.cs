using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim;

/// <summary>
/// Projects an aircraft's active procedure phase into drawable <see cref="NavRouteShapeDto"/> shapes
/// for the radar "Show nav route" overlay. Covers the geometry the flat <see cref="NavRouteFixDto"/>
/// route can't express: a holding pattern's racetrack, a procedure turn's course reversal, and the
/// open-ended coded legs (CA/VA/CD/VD/CR/VR) of a SID. Pure geometry — no side effects.
/// </summary>
public static class NavRouteOverlayProjector
{
    /// <summary>Standard-rate turn: 3°/s.</summary>
    private const double StandardTurnRateDegPerSec = 3.0;

    /// <summary>Fallback holding TAS when performance data is unavailable (kt).</summary>
    private const double FallbackHoldingTasKts = 210.0;

    private const double MinTurnRadiusNm = 0.5;
    private const double MaxTurnRadiusNm = 4.0;

    /// <summary>Nominal length of an open-ended coded leg's drawn vector (nm) — enough to read the
    /// climb-out direction. The real leg ends on a condition (altitude/distance/radial), not a point.</summary>
    private const double CodedLegNominalNm = 4.0;

    public static List<NavRouteShapeDto> BuildShapes(AircraftState aircraft)
    {
        return aircraft.Phases?.CurrentPhase switch
        {
            HoldingPatternPhase hold => [BuildHoldRacetrack(aircraft, hold)],
            ProcedureTurnPhase pt => [BuildProcedureTurn(aircraft, pt)],
            DepartureProcedurePhase departure => BuildCodedLegVectors(aircraft, departure),
            _ => [],
        };
    }

    /// <summary>
    /// Builds a procedure-turn course reversal (AIM 5-4-9): outbound leg on the final-approach-course
    /// reciprocal, the published 45° barb, a 180° turn, then the return leg intercepting the inbound
    /// course back to the fix. Labeled "PT" with the minimum altitude. All headings are true, matching
    /// how <see cref="ProcedureTurnPhase"/> flies it.
    /// </summary>
    private static NavRouteShapeDto BuildProcedureTurn(AircraftState aircraft, ProcedureTurnPhase pt)
    {
        var fix = new LatLon(pt.FixLat, pt.FixLon);
        double outboundNm = Math.Clamp(pt.MaxOutboundDistanceNm * 0.4, 2.0, 6.0);
        double barbNm = Math.Clamp(pt.MaxOutboundDistanceNm * 0.3, 2.0, 4.0);
        double radiusNm = TurnRadiusNm(aircraft);
        bool turnRight = pt.OneEightyTurnDirection == TurnDirection.Right;

        LatLon outbound = GeoMath.ProjectPoint(fix, new TrueHeading(pt.InboundCourseDeg + 180.0), outboundNm);
        LatLon barbEnd = GeoMath.ProjectPoint(outbound, new TrueHeading(pt.PtOutboundCourseDeg), barbNm);

        double centerBearing = pt.PtOutboundCourseDeg + (turnRight ? 90.0 : -90.0);
        LatLon turnCenter = GeoMath.ProjectPoint(barbEnd, new TrueHeading(centerBearing), radiusNm);
        LatLon barbExit = GeoMath.ProjectPoint(barbEnd, new TrueHeading(centerBearing), 2.0 * radiusNm);

        var points = new List<LatLon> { fix, outbound, barbEnd };
        // 180° turn: barbEnd → barbExit (excludes barbEnd, includes barbExit).
        points.AddRange(GeoMath.GenerateArcPoints(turnCenter, radiusNm, centerBearing + 180.0, centerBearing, turnRight));
        // Return leg on the 45°-reciprocal heading intercepts the inbound course, then flies to the fix.
        if (TryInterceptInbound(fix, pt.InboundCourseDeg, barbExit, pt.PtOutboundCourseDeg + 180.0) is { } intercept)
        {
            points.Add(intercept);
        }
        points.Add(fix);

        var labels = new List<string> { "PT" };
        if (pt.MinAltitudeFt > 0)
        {
            labels.AddRange(
                CrossingRestrictionLabel.BuildLines(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, pt.MinAltitudeFt), null)
            );
        }

        return new NavRouteShapeDto(NavRouteShapeKind.ProcedureTurn, points.Select(p => new[] { p.Lat, p.Lon }).ToList(), labels, fix.Lat, fix.Lon);
    }

    /// <summary>
    /// Intersects the return leg (through <paramref name="from"/> on <paramref name="returnBearingDeg"/>)
    /// with the inbound course line (through <paramref name="fix"/> on <paramref name="inboundCourseDeg"/>),
    /// in a local flat-earth frame around the fix. Returns null when the lines are near-parallel or the
    /// intercept is implausibly far.
    /// </summary>
    private static LatLon? TryInterceptInbound(LatLon fix, double inboundCourseDeg, LatLon from, double returnBearingDeg)
    {
        const double DegToRad = Math.PI / 180.0;
        double cosLat = Math.Cos(fix.Lat * DegToRad);
        if (Math.Abs(cosLat) < 1e-9)
        {
            return null;
        }

        double qe = (from.Lon - fix.Lon) * 60.0 * cosLat;
        double qn = (from.Lat - fix.Lat) * 60.0;
        double ae = Math.Sin(inboundCourseDeg * DegToRad);
        double an = Math.Cos(inboundCourseDeg * DegToRad);
        double be = Math.Sin(returnBearingDeg * DegToRad);
        double bn = Math.Cos(returnBearingDeg * DegToRad);

        double det = (-ae * bn) + (be * an);
        if (Math.Abs(det) < 1e-6)
        {
            return null;
        }

        double t = ((-qe * bn) + (be * qn)) / det;
        double pe = t * ae;
        double pn = t * an;
        if (Math.Sqrt((pe * pe) + (pn * pn)) > 50.0)
        {
            return null;
        }

        return new LatLon(fix.Lat + (pn / 60.0), fix.Lon + (pe / (60.0 * cosLat)));
    }

    /// <summary>
    /// Projects the remaining coded departure legs (from the active leg on) as chained vectors, each
    /// labeled with its crossing restriction. Fix-terminated legs end at their fix; open-ended legs
    /// (CA/VA/CD/VD/CR/VR) draw a nominal-length vector along the leg course, since their real endpoint
    /// is a condition, not a point. A CA/VA "climb to" altitude is shown as an at-or-above floor.
    /// </summary>
    private static List<NavRouteShapeDto> BuildCodedLegVectors(AircraftState aircraft, DepartureProcedurePhase departure)
    {
        var shapes = new List<NavRouteShapeDto>();
        LatLon anchor = departure.LegEntryPosition ?? aircraft.Position;
        int start = Math.Clamp(departure.ActiveLegIndex, 0, departure.Legs.Count);

        for (int i = start; i < departure.Legs.Count; i++)
        {
            var leg = departure.Legs[i];

            LatLon end;
            if (leg.FixPosition is { } fixPos)
            {
                end = fixPos;
            }
            else if (leg.CourseMagnetic is { } course)
            {
                end = GeoMath.ProjectPoint(anchor, new MagneticHeading(course).ToTrue(aircraft.Declination), CodedLegNominalNm);
            }
            else
            {
                // No fix and no course to place the leg — stop rather than chain from a stale anchor.
                break;
            }

            var labels = BuildCodedLegLabels(leg);
            shapes.Add(
                new NavRouteShapeDto(
                    NavRouteShapeKind.CodedLegVector,
                    [
                        [anchor.Lat, anchor.Lon],
                        [end.Lat, end.Lon],
                    ],
                    labels.Count > 0 ? labels : null,
                    labels.Count > 0 ? end.Lat : null,
                    labels.Count > 0 ? end.Lon : null
                )
            );

            anchor = end;
        }

        return shapes;
    }

    private static List<string> BuildCodedLegLabels(ProcedureLeg leg)
    {
        var altitude = leg.AltitudeRestriction;
        if (altitude is null && leg.TargetAltitudeFt is { } target)
        {
            // CA/VA/FA "climb to X" — a floor the aircraft climbs to reach.
            altitude = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, (int)Math.Round(target));
        }
        return [.. CrossingRestrictionLabel.BuildLines(altitude, leg.SpeedRestriction)];
    }

    /// <summary>
    /// Builds a holding-pattern racetrack: inbound leg (into the fix), the two 180° turns, and the
    /// parallel outbound leg, on the maneuvering side set by the turn direction. Densified so the
    /// turns render as curves. Matches how <see cref="HoldingPatternPhase"/> flies the pattern
    /// (inbound course is treated as a true heading).
    /// </summary>
    private static NavRouteShapeDto BuildHoldRacetrack(AircraftState aircraft, HoldingPatternPhase hold)
    {
        double radiusNm = TurnRadiusNm(aircraft);
        double legNm = HoldStraightLegNm(aircraft, hold, radiusNm);
        var points = HoldRacetrackPoints(
            new LatLon(hold.FixLat, hold.FixLon),
            hold.InboundCourse,
            hold.Direction == TurnDirection.Right,
            legNm,
            radiusNm
        );
        return new NavRouteShapeDto(NavRouteShapeKind.HoldRacetrack, points.Select(p => new[] { p.Lat, p.Lon }).ToList(), null, null, null);
    }

    /// <summary>
    /// Builds the closed racetrack polyline (starting and ending at the far end of the inbound leg)
    /// for a hold at <paramref name="fix"/> with the given true <paramref name="inboundCourse"/>, turn
    /// side, straight-leg length, and turn radius. The two 180° turns are densified into arc points.
    /// </summary>
    internal static List<LatLon> HoldRacetrackPoints(LatLon fix, int inboundCourse, bool isRight, double legNm, double radiusNm)
    {
        double sideDeg = isRight ? 90.0 : -90.0;

        // Inbound leg runs from A (far end) into the fix along the inbound course; the outbound leg is
        // parallel, offset by the turn diameter on the maneuvering side.
        var outboundBearing = new TrueHeading(inboundCourse + 180.0);
        var offsetBearing = new TrueHeading(inboundCourse + sideDeg);

        LatLon a = GeoMath.ProjectPoint(fix, outboundBearing, legNm);
        LatLon outboundFar = GeoMath.ProjectPoint(a, offsetBearing, 2.0 * radiusNm);
        LatLon turn1Center = GeoMath.ProjectPoint(fix, offsetBearing, radiusNm);
        LatLon turn2Center = GeoMath.ProjectPoint(a, offsetBearing, radiusNm);

        double nearBearing = inboundCourse + sideDeg; // center → outbound-near / abeam point
        double farBearing = inboundCourse + sideDeg + 180.0; // center → inbound-leg point (fix / A)

        var points = new List<LatLon> { a, fix };
        // Turn 1 at the fix: fix → outbound-near (excludes fix, includes outbound-near).
        points.AddRange(GeoMath.GenerateArcPoints(turn1Center, radiusNm, farBearing, nearBearing, isRight));
        points.Add(outboundFar);
        // Turn 2 at the far end: outbound-far → A (excludes outbound-far, includes A, closing the loop).
        points.AddRange(GeoMath.GenerateArcPoints(turn2Center, radiusNm, nearBearing, farBearing, isRight));
        return points;
    }

    private static double TurnRadiusNm(AircraftState aircraft)
    {
        double tas = HoldingTasKts(aircraft);
        double radius = (tas / 3600.0) / (StandardTurnRateDegPerSec * Math.PI / 180.0);
        return Math.Clamp(radius, MinTurnRadiusNm, MaxTurnRadiusNm);
    }

    private static double HoldStraightLegNm(AircraftState aircraft, HoldingPatternPhase hold, double radiusNm)
    {
        double nm = hold.IsMinuteBased ? HoldingTasKts(aircraft) * (hold.LegLength / 60.0) : hold.LegLength;
        // Keep the straight leg at least a turn-diameter long so the racetrack never collapses.
        return Math.Max(nm, 2.0 * radiusNm);
    }

    private static double HoldingTasKts(AircraftState aircraft)
    {
        double ias = AircraftPerformance.HoldingSpeed(aircraft.AircraftType, aircraft.Altitude);
        double tas = WindInterpolator.IasToTas(ias, aircraft.Altitude);
        return tas > 0 ? tas : FallbackHoldingTasKts;
    }
}
