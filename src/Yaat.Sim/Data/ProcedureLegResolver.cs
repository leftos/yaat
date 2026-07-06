using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data;

/// <summary>
/// Converts CIFP procedure legs into typed <see cref="ProcedureLeg"/> sequences that preserve
/// ARINC 424 path terminators (VA/VI/VM/CA, course-tracked CF, FM). A phase can then fly the
/// coded path instead of collapsing every leg to a fix position. Shared by SID, STAR, and
/// approach resolution. Mirrors the fix resolution and RF/AF arc expansion of the flat
/// <c>DepartureClearanceHandler.ResolveLegsToTargets</c>, but keeps the fix-less heading/course
/// legs that the flat resolver silently drops.
/// </summary>
internal static class ProcedureLegResolver
{
    /// <summary>
    /// Resolves an ordered CIFP leg sequence into typed procedure legs. RF/AF arcs are
    /// pre-expanded into <see cref="ProcedureLegType.Arc"/> waypoints; PI legs are skipped
    /// (approach-only, handled by hold-in-lieu); unresolvable fixes are skipped.
    /// </summary>
    public static List<ProcedureLeg> Resolve(IReadOnlyList<CifpLeg> legs)
    {
        var navDb = NavigationDatabase.Instance;
        var result = new List<ProcedureLeg>();
        LatLon? previousFixPos = null;

        foreach (var leg in legs)
        {
            // Procedure-turn legs are approach-only (hold-in-lieu); never flown on a SID/STAR body.
            if (leg.PathTerminator == CifpPathTerminator.PI)
            {
                continue;
            }

            TurnDirection? turn = leg.TurnDirection switch
            {
                'L' => TurnDirection.Left,
                'R' => TurnDirection.Right,
                _ => null,
            };

            // Pure heading/course legs — no terminating fix (VA/CA/FA/VI/CI/VM).
            switch (leg.PathTerminator)
            {
                case CifpPathTerminator.VA:
                    result.Add(
                        new ProcedureLeg
                        {
                            Type = ProcedureLegType.HeadingToAltitude,
                            CourseMagnetic = leg.OutboundCourse,
                            TargetAltitudeFt = leg.Altitude?.Altitude1Ft,
                            Turn = turn,
                        }
                    );
                    continue;
                case CifpPathTerminator.CA:
                case CifpPathTerminator.FA:
                    result.Add(
                        new ProcedureLeg
                        {
                            Type = ProcedureLegType.CourseToAltitude,
                            CourseMagnetic = leg.OutboundCourse,
                            TargetAltitudeFt = leg.Altitude?.Altitude1Ft,
                            Turn = turn,
                        }
                    );
                    continue;
                case CifpPathTerminator.VI:
                    result.Add(
                        new ProcedureLeg
                        {
                            Type = ProcedureLegType.HeadingToIntercept,
                            CourseMagnetic = leg.OutboundCourse,
                            TerminatesOnNextLegIntercept = true,
                            Turn = turn,
                        }
                    );
                    continue;
                case CifpPathTerminator.CI:
                    result.Add(
                        new ProcedureLeg
                        {
                            Type = ProcedureLegType.CourseToIntercept,
                            CourseMagnetic = leg.OutboundCourse,
                            TerminatesOnNextLegIntercept = true,
                            Turn = turn,
                        }
                    );
                    continue;
                case CifpPathTerminator.VM:
                    result.Add(
                        new ProcedureLeg
                        {
                            Type = ProcedureLegType.HeadingToManual,
                            CourseMagnetic = leg.OutboundCourse,
                            Turn = turn,
                        }
                    );
                    continue;
            }

            // Course/heading to a DME distance, along-track distance, or radial (CD/VD/FD/FC/CR/VR).
            // The named fix on these legs (FC/FD) is the leg's origin, not its terminus, so they
            // resolve to a reference point + a distance/radial trigger rather than a destination fix.
            if (
                leg.PathTerminator
                is CifpPathTerminator.CD
                    or CifpPathTerminator.VD
                    or CifpPathTerminator.FD
                    or CifpPathTerminator.FC
                    or CifpPathTerminator.CR
                    or CifpPathTerminator.VR
            )
            {
                var distanceOrRadial = ResolveDistanceOrRadialLeg(leg, navDb, turn);
                if (distanceOrRadial is not null)
                {
                    result.Add(distanceOrRadial);
                }
                continue;
            }

            // Fix-bearing legs (IF/TF/DF/CF/FM/HA/HF/HM/RF/AF/Other).
            if (string.IsNullOrWhiteSpace(leg.FixIdentifier))
            {
                continue;
            }

            var pos = navDb.GetFixPosition(leg.FixIdentifier);
            if (pos is null)
            {
                continue;
            }

            var fixPos = new LatLon(pos.Value.Lat, pos.Value.Lon);

            // Deduplicate an adjacent repeat of the same fix.
            if (result.Count > 0 && string.Equals(result[^1].FixName, leg.FixIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                previousFixPos = fixPos;
                continue;
            }

            // RF leg: expand the radius-to-fix arc into intermediate waypoints.
            if (
                leg.PathTerminator == CifpPathTerminator.RF
                && leg.ArcCenterLat is { } rfLat
                && leg.ArcCenterLon is { } rfLon
                && leg.ArcRadiusNm is { } rfRadius
                && previousFixPos is { } rfPrev
            )
            {
                ExpandArc(result, new LatLon(rfLat, rfLon), rfRadius, rfPrev, fixPos, leg.TurnDirection);
            }

            // AF leg: expand the DME arc about the recommended navaid.
            if (
                leg.PathTerminator == CifpPathTerminator.AF
                && leg.RecommendedNavaidId is not null
                && leg.Rho is { } afRho
                && previousFixPos is { } afPrev
                && navDb.GetFixPosition(leg.RecommendedNavaidId) is { } navaid
            )
            {
                ExpandArc(result, new LatLon(navaid.Lat, navaid.Lon), afRho, afPrev, fixPos, leg.TurnDirection);
            }

            var type = leg.PathTerminator switch
            {
                CifpPathTerminator.IF => ProcedureLegType.InitialFix,
                CifpPathTerminator.DF => ProcedureLegType.DirectToFix,
                CifpPathTerminator.CF => ProcedureLegType.CourseToFix,
                CifpPathTerminator.FM => ProcedureLegType.HeadingToManual,
                _ => ProcedureLegType.TrackToFix,
            };

            bool isFlyOver =
                leg.IsFlyOver
                || leg.FixRole is CifpFixRole.FAF or CifpFixRole.MAP
                || leg.PathTerminator is CifpPathTerminator.HA or CifpPathTerminator.HF or CifpPathTerminator.HM;

            result.Add(
                new ProcedureLeg
                {
                    Type = type,
                    FixName = leg.FixIdentifier,
                    FixPosition = fixPos,
                    CourseMagnetic = leg.OutboundCourse,
                    AltitudeRestriction = leg.Altitude,
                    SpeedRestriction = leg.Speed,
                    Turn = turn,
                    IsFlyOver = isFlyOver,
                }
            );

            previousFixPos = fixPos;
        }

        return result;
    }

    /// <summary>
    /// Returns the maximal LEADING run of coded legs (heading/intercept legs plus course-to-fix)
    /// that a departure procedure phase should fly, or null when the procedure opens with a plain
    /// fix leg. The run stops at the first plain fix (TF/DF/IF/Arc); those interior fixes stay in
    /// the NavigationRoute so <c>FlightPhysics</c> flies them with full turn anticipation rather
    /// than the phase's simpler steering. So a SID that opens with VA/VI (LINDZ) or a CF on the
    /// runway course is flown as charted, while later fix-to-fix legs are unaffected.
    /// </summary>
    public static List<ProcedureLeg>? ExtractActiveDepartureLegs(List<ProcedureLeg> legs)
    {
        int runEnd = 0;
        while (runEnd < legs.Count && IsCodedLeg(legs[runEnd].Type))
        {
            runEnd++;
        }

        return runEnd == 0 ? null : legs.Take(runEnd).ToList();
    }

    private static bool IsCodedLeg(ProcedureLegType type) =>
        type
            is ProcedureLegType.HeadingToAltitude
                or ProcedureLegType.CourseToAltitude
                or ProcedureLegType.HeadingToIntercept
                or ProcedureLegType.CourseToIntercept
                or ProcedureLegType.HeadingToManual
                or ProcedureLegType.CourseToFix
                or ProcedureLegType.CourseToDistance
                or ProcedureLegType.HeadingToDistance
                or ProcedureLegType.CourseToRadial
                or ProcedureLegType.HeadingToRadial;

    /// <summary>
    /// Resolves a CD/VD/FD/FC/CR/VR leg to a distance- or radial-terminated <see cref="ProcedureLeg"/>.
    /// Reference point is the origin fix for FC, the recommended navaid otherwise. Returns null when
    /// the reference position, distance, or radial can't be resolved.
    /// </summary>
    private static ProcedureLeg? ResolveDistanceOrRadialLeg(CifpLeg leg, NavigationDatabase navDb, TurnDirection? turn)
    {
        string? referenceId = leg.PathTerminator == CifpPathTerminator.FC ? leg.FixIdentifier : leg.RecommendedNavaidId;
        if (string.IsNullOrWhiteSpace(referenceId))
        {
            return null;
        }
        var refPos = navDb.GetFixPosition(referenceId);
        if (refPos is null)
        {
            return null;
        }
        var reference = new LatLon(refPos.Value.Lat, refPos.Value.Lon);

        bool isHeading = leg.PathTerminator is CifpPathTerminator.VD or CifpPathTerminator.VR;

        if (leg.PathTerminator is CifpPathTerminator.CR or CifpPathTerminator.VR)
        {
            if (leg.Theta is not { } radial)
            {
                return null;
            }
            return new ProcedureLeg
            {
                Type = isHeading ? ProcedureLegType.HeadingToRadial : ProcedureLegType.CourseToRadial,
                CourseMagnetic = leg.OutboundCourse,
                TerminationReferencePosition = reference,
                TerminationRadialMagnetic = radial,
                AltitudeRestriction = leg.Altitude,
                SpeedRestriction = leg.Speed,
                Turn = turn,
            };
        }

        if (leg.LegDistanceNm is not { } distance)
        {
            return null;
        }
        return new ProcedureLeg
        {
            Type = isHeading ? ProcedureLegType.HeadingToDistance : ProcedureLegType.CourseToDistance,
            CourseMagnetic = leg.OutboundCourse,
            TerminationReferencePosition = reference,
            TerminationDistanceNm = distance,
            AltitudeRestriction = leg.Altitude,
            SpeedRestriction = leg.Speed,
            Turn = turn,
        };
    }

    private static void ExpandArc(
        List<ProcedureLeg> result,
        LatLon center,
        double radiusNm,
        LatLon previousFix,
        LatLon terminatorFix,
        char? turnDirection
    )
    {
        double startBearing = GeoMath.BearingTo(center, previousFix);
        double endBearing = GeoMath.BearingTo(center, terminatorFix);
        bool turnRight = GeoMath.ResolveArcTurnRight(turnDirection, startBearing, endBearing);
        var arcPoints = GeoMath.GenerateArcPoints(center, radiusNm, startBearing, endBearing, turnRight);

        // Insert intermediate points; the terminator fix itself is added by the caller.
        for (int i = 0; i < arcPoints.Count - 1; i++)
        {
            result.Add(
                new ProcedureLeg
                {
                    Type = ProcedureLegType.Arc,
                    FixName = $"ARC{i + 1:D2}",
                    FixPosition = arcPoints[i],
                }
            );
        }
    }
}
