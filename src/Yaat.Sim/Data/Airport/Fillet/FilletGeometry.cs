namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>Shared fillet corner math and cubic-bezier construction.</summary>
public static class FilletGeometry
{
    public static double ComputeTurnAngle(double bearingA, double bearingB)
    {
        double diff = GeoMath.AbsBearingDifference(bearingA, bearingB);
        return 180.0 - diff;
    }

    public static double InitialBearing(GroundNode intersection, GroundNode otherNode, GroundEdge edge)
    {
        if (edge.IntermediatePoints.Count > 0)
        {
            if (edge.Nodes[0].Id == intersection.Id)
            {
                var pt = edge.IntermediatePoints[0];
                return GeoMath.BearingTo(intersection.Position, new LatLon(pt.Lat, pt.Lon));
            }

            var ptLast = edge.IntermediatePoints[^1];
            return GeoMath.BearingTo(intersection.Position, new LatLon(ptLast.Lat, ptLast.Lon));
        }

        return GeoMath.BearingTo(intersection.Position, otherNode.Position);
    }

    public static double SelectMaxRadius(GroundEdge edgeA, GroundEdge edgeB, double turnAngleDeg)
    {
        bool hasRunway = edgeA.IsRunwayCenterline || edgeB.IsRunwayCenterline;
        bool hasRamp = edgeA.IsRamp || edgeB.IsRamp;

        if (hasRamp)
        {
            return FilletConstants.RampRadiusFt;
        }

        if (hasRunway && (turnAngleDeg <= 45.0))
        {
            return FilletConstants.HighSpeedExitRadiusFt;
        }

        if (hasRunway)
        {
            return FilletConstants.RunwayExitRadiusFt;
        }

        return FilletConstants.DefaultRadiusFt;
    }

    /// <summary>Symmetric tangent distance (feet) for a corner pair.</summary>
    public static double ComputeIdealTangentFt(
        double turnAngleDeg,
        double requestedRadiusFt,
        double availableAFt,
        double availableBFt,
        bool capA,
        bool capB,
        double intersectionCapAFt,
        double intersectionCapBFt
    )
    {
        double halfAngleRad = (turnAngleDeg / 2.0) * (Math.PI / 180.0);
        double tanHalf = Math.Tan(halfAngleRad);

        double maxTangentAFt = capA ? availableAFt / 2.0 : availableAFt;
        double maxTangentBFt = capB ? availableBFt / 2.0 : availableBFt;
        maxTangentAFt = Math.Min(maxTangentAFt, intersectionCapAFt);
        maxTangentBFt = Math.Min(maxTangentBFt, intersectionCapBFt);

        double maxFitRadiusFt = Math.Min(maxTangentAFt, maxTangentBFt) / tanHalf;
        double radiusFt = Math.Min(maxFitRadiusFt, requestedRadiusFt);
        double tangentDistFt = radiusFt * tanHalf;

        if (tangentDistFt > FilletConstants.MaxTangentDistFt)
        {
            tangentDistFt = FilletConstants.MaxTangentDistFt;
        }

        return tangentDistFt;
    }

    /// <summary>
    /// Conservative fillet radius (ft) for plan-time gating from tangent lengths and arm bearings.
    /// Uses min(ta,tb)/tan(turn/2); asymmetric cubics built from endpoint positions alone can
    /// under-estimate radius when ta ≠ tb.
    /// </summary>
    public static double EffectiveMinRadiusFt(
        double tangentAFt,
        double tangentBFt,
        double bearingAToJunctionDeg,
        double bearingBToJunctionDeg,
        LatLon posA,
        LatLon posB
    )
    {
        _ = (posA, posB);
        double bearingAFromTangent = (bearingAToJunctionDeg + 180.0) % 360.0;
        double bearingBFromTangent = (bearingBToJunctionDeg + 180.0) % 360.0;
        double effectiveTurnDeg = 180.0 - GeoMath.AbsBearingDifference(bearingAFromTangent, bearingBFromTangent);
        double halfAngleRad = (effectiveTurnDeg / 2.0) * (Math.PI / 180.0);
        double tanHalf = Math.Tan(halfAngleRad);
        if (tanHalf < 1e-9)
        {
            return double.MaxValue;
        }

        return Math.Min(tangentAFt, tangentBFt) / tanHalf;
    }

    public static BezierBuildResult BuildBezier(
        LatLon tanA,
        LatLon tanB,
        double bearingAToJunctionDeg,
        double bearingBToJunctionDeg,
        double requestedRadiusFt
    )
    {
        double bearingAFromTangent = (bearingAToJunctionDeg + 180.0) % 360.0;
        double bearingBFromTangent = (bearingBToJunctionDeg + 180.0) % 360.0;
        double effectiveTurnDeg = 180.0 - GeoMath.AbsBearingDifference(bearingAFromTangent, bearingBFromTangent);
        double sweepRad = effectiveTurnDeg * (Math.PI / 180.0);
        double kappa = (4.0 / 3.0) * Math.Tan(sweepRad / 4.0);

        double radiusNm = requestedRadiusFt / GeoMath.FeetPerNm;
        double depthA = kappa * radiusNm;
        double depthB = kappa * radiusNm;

        // Control points pull the curve toward the junction (into the corner), so they project
        // along the from-tangent bearing — not back out along the arm toward the remote node.
        var (p1Lat, p1Lon) = GeoMath.ProjectPointRaw(tanA.Lat, tanA.Lon, bearingAFromTangent, depthA);
        var (p2Lat, p2Lon) = GeoMath.ProjectPointRaw(tanB.Lat, tanB.Lon, bearingBFromTangent, depthB);

        var bezier = new CubicBezier(tanA.Lat, tanA.Lon, p1Lat, p1Lon, p2Lat, p2Lon, tanB.Lat, tanB.Lon);
        return new BezierBuildResult(
            P1Lat: p1Lat,
            P1Lon: p1Lon,
            P2Lat: p2Lat,
            P2Lon: p2Lon,
            MinRadiusFt: bezier.MinRadiusOfCurvatureFt(tanA.Lat, 10),
            ArcLengthNm: bezier.ArcLengthNm(20),
            EffectiveTurnDeg: effectiveTurnDeg,
            BearingAFromTangentDeg: bearingAFromTangent,
            BearingBFromTangentDeg: bearingBFromTangent
        );
    }

    public sealed record BezierBuildResult(
        double P1Lat,
        double P1Lon,
        double P2Lat,
        double P2Lon,
        double MinRadiusFt,
        double ArcLengthNm,
        double EffectiveTurnDeg,
        double BearingAFromTangentDeg,
        double BearingBFromTangentDeg
    );
}
