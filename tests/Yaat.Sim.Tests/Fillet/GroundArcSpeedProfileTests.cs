using Xunit;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>
/// A fillet's cornering speed follows its local curvature. The fillet generator can emit a distorted cubic —
/// one long gentle sweep with a single tight stretch near one end (SFO junction J133's B bend: 107 ft, 56°,
/// but a 22 ft minimum radius) — and pricing the whole curve at its tightest point holds an aircraft at
/// walking pace for the full length. The profile keeps the crawl where the curve is actually tight.
/// </summary>
public class GroundArcSpeedProfileTests
{
    private static GroundNode Node(int id, LatLon position) =>
        new()
        {
            Id = id,
            Position = position,
            Type = GroundNodeType.TaxiwayIntersection,
        };

    /// <summary>
    /// A ~150 ft curve that leaves the from-node almost straight (long first control arm) and turns hard
    /// only in its last ~25 ft (short second control arm): gentle, then tight.
    /// </summary>
    private static GroundArc DistortedArc()
    {
        var p0 = new LatLon(37.700, -122.200);
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0, new TrueHeading(90.0), 120.0 / GeoMath.FeetPerNm);
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(p0, new TrueHeading(80.0), 150.0 / GeoMath.FeetPerNm);
        var p3 = new LatLon(p3Lat, p3Lon);
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(p3, new TrueHeading(200.0), 12.0 / GeoMath.FeetPerNm);
        var from = Node(1, p0);
        var to = Node(2, p3);
        var curve = new CubicBezier(p0.Lat, p0.Lon, p1Lat, p1Lon, p2Lat, p2Lon, p3.Lat, p3.Lon);
        return new GroundArc
        {
            Nodes = [from, to],
            TaxiwayNames = ["B"],
            DistanceNm = curve.ArcLengthNm(64),
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = curve.MinRadiusOfCurvatureFt(p0.Lat, 64),
            TurnAngleDeg = 60.0,
        };
    }

    [Fact]
    public void DistortedArc_IsSlowOnlyWhereItIsTight()
    {
        var arc = DistortedArc();
        var profile = arc.SpeedProfile(AircraftCategory.Jet);
        double floorKts = arc.MaxSafeSpeedKts(AircraftCategory.Jet);

        Assert.True(profile.Count >= 8, "profile should sample the curve at several points");
        Assert.Equal(0.0, profile[0].LengthFt, 3);
        Assert.InRange(profile[^1].LengthFt, arc.DistanceNm * GeoMath.FeetPerNm * 0.98, arc.DistanceNm * GeoMath.FeetPerNm * 1.02);
        Assert.True(profile.Min(s => s.SpeedKts) >= floorKts - 1e-6, "no sample may be slower than the whole-arc cap");

        var gentleHalf = profile.Where(s => s.LengthFt < arc.DistanceNm * GeoMath.FeetPerNm * 0.5).ToList();
        Assert.True(
            gentleHalf.All(s => s.SpeedKts >= 2.0 * floorKts),
            $"the gentle first half should run well above the {floorKts:F1} kt tight-stretch cap"
        );
        Assert.True(profile.Any(s => s.SpeedKts <= floorKts + 0.5), "the tight stretch should be capped near the whole-arc minimum");
    }

    [Fact]
    public void DistortedArc_TraversalIsFasterThanCrawlingItAtTheMinimum()
    {
        var arc = DistortedArc();
        double crawlSeconds = arc.DistanceNm / (arc.MaxSafeSpeedKts(AircraftCategory.Jet) / 3600.0);

        double profiledSeconds = arc.TraversalSeconds(AircraftCategory.Jet);

        Assert.True(
            profiledSeconds < 0.6 * crawlSeconds,
            $"profiled traversal {profiledSeconds:F1} s should be well under the flat-cap crawl {crawlSeconds:F1} s"
        );
    }

    [Fact]
    public void UniformArc_ProfileMatchesTheWholeArcCap()
    {
        // A near-circular quarter fillet: every sample sits at the same radius, so the profile is flat.
        var p0 = new LatLon(37.700, -122.200);
        const double radiusFt = 75.0;
        var (p3Lat, p3Lon) = GeoMath.ProjectPoint(p0, new TrueHeading(45.0), radiusFt * Math.Sqrt(2.0) / GeoMath.FeetPerNm);
        double kappa = 0.5523 * radiusFt;
        var (p1Lat, p1Lon) = GeoMath.ProjectPoint(p0, new TrueHeading(90.0), kappa / GeoMath.FeetPerNm);
        var (p2Lat, p2Lon) = GeoMath.ProjectPoint(new LatLon(p3Lat, p3Lon), new TrueHeading(180.0), kappa / GeoMath.FeetPerNm);
        var curve = new CubicBezier(p0.Lat, p0.Lon, p1Lat, p1Lon, p2Lat, p2Lon, p3Lat, p3Lon);
        var arc = new GroundArc
        {
            Nodes = [Node(1, p0), Node(2, new LatLon(p3Lat, p3Lon))],
            TaxiwayNames = ["W", "U"],
            DistanceNm = curve.ArcLengthNm(64),
            P1Lat = p1Lat,
            P1Lon = p1Lon,
            P2Lat = p2Lat,
            P2Lon = p2Lon,
            MinRadiusOfCurvatureFt = curve.MinRadiusOfCurvatureFt(p0.Lat, 64),
            TurnAngleDeg = 90.0,
        };

        var profile = arc.SpeedProfile(AircraftCategory.Jet);
        double cap = arc.MaxSafeSpeedKts(AircraftCategory.Jet);

        Assert.All(profile, s => Assert.InRange(s.SpeedKts, cap - 0.01, cap * 1.1));
    }
}
