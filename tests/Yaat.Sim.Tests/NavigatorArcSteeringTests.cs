using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using static Yaat.Sim.Data.Airport.AirportGroundLayout;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="GroundNavigator.ComputeArcSteering"/>: bearing computation,
/// direction reversal, speed from curvature, speed floor, and lookahead clamping.
/// </summary>
public class NavigatorArcSteeringTests
{
    // Same 90° arc fixture as CubicBezierTests — east-to-south turn
    private const double IntersectionLat = 37.72;
    private const double IntersectionLon = -122.22;
    private const double NodeALat = IntersectionLat;
    private const double NodeALon = IntersectionLon + 0.000261;
    private const double NodeBLat = IntersectionLat - 0.000206;
    private const double NodeBLon = IntersectionLon;
    private const double Kappa = 0.5523;
    private const double P1Lat = NodeALat;
    private const double P1Lon = NodeALon - (Kappa * 0.000261);
    private const double P2Lat = NodeBLat + (Kappa * 0.000206);
    private const double P2Lon = NodeBLon;

    private static readonly GroundNode NodeA = new()
    {
        Id = 1,
        Latitude = NodeALat,
        Longitude = NodeALon,
        Type = GroundNodeType.TaxiwayIntersection,
    };

    private static readonly GroundNode NodeB = new()
    {
        Id = 2,
        Latitude = NodeBLat,
        Longitude = NodeBLon,
        Type = GroundNodeType.TaxiwayIntersection,
    };

    private static GroundArc MakeArc(double minRadiusFt = 75.0)
    {
        var bezier = new CubicBezier(NodeALat, NodeALon, P1Lat, P1Lon, P2Lat, P2Lon, NodeBLat, NodeBLon);
        return new GroundArc
        {
            Nodes = [NodeA, NodeB],
            TaxiwayNames = ["W"],
            P1Lat = P1Lat,
            P1Lon = P1Lon,
            P2Lat = P2Lat,
            P2Lon = P2Lon,
            MinRadiusOfCurvatureFt = minRadiusFt,
            DistanceNm = bezier.ArcLengthNm(20),
        };
    }

    private static PhaseContext MakeContext(double lat, double lon)
    {
        var aircraft = new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            TrueHeading = new TrueHeading(270),
            IsOnGround = true,
            Departure = "KOAK",
        };
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
        };
    }

    // --- Bearing ---

    [Fact]
    public void Bearing_AircraftAtArcStart_SteersTowardLookahead()
    {
        var arc = MakeArc();
        var ctx = MakeContext(NodeALat, NodeALon);

        var (bearing, _) = GroundNavigator.ComputeArcSteering(ctx, arc, fromNodeIsZero: true);

        // Aircraft at Node A (east), traversing toward Node B (south).
        // Lookahead is 15% along the curve — should steer roughly WSW to SW (~210-270°)
        Assert.True(bearing > 180 && bearing < 300, $"Bearing from A toward B should be ~SW, got {bearing:F1}°");
    }

    [Fact]
    public void Bearing_AircraftAtArcEnd_SteersTowardFinalNode()
    {
        var arc = MakeArc();
        // Place aircraft very near Node B
        var ctx = MakeContext(NodeBLat + 0.00001, NodeBLon + 0.00001);

        var (bearing, _) = GroundNavigator.ComputeArcSteering(ctx, arc, fromNodeIsZero: true);

        // Near the end, lookahead is clamped to t=1.0, so bearing should be toward Node B (~south)
        Assert.True(bearing > 140 && bearing < 230, $"Bearing near end should be ~S, got {bearing:F1}°");
    }

    // --- Direction Reversal ---

    [Fact]
    public void Bearing_ReversedTraversal_GivesDifferentDirection()
    {
        var arc = MakeArc();
        var ctx = MakeContext(NodeALat, NodeALon);

        var (bearingFwd, _) = GroundNavigator.ComputeArcSteering(ctx, arc, fromNodeIsZero: true);
        var (bearingRev, _) = GroundNavigator.ComputeArcSteering(ctx, arc, fromNodeIsZero: false);

        // When reversed (B→A), aircraft at Node A is near the end of the reversed curve.
        // The bearings should be meaningfully different.
        double diff = GeoMath.AbsBearingDifference(bearingFwd, bearingRev);
        Assert.True(diff > 30, $"Forward vs reversed bearing should differ significantly, diff = {diff:F1}°");
    }

    // --- Speed ---

    [Fact]
    public void Speed_TightCurve_IsLowerThanGentleCurve()
    {
        var tightArc = MakeArc(minRadiusFt: 50.0);
        var gentleArc = MakeArc(minRadiusFt: 150.0);
        var ctx = MakeContext(NodeALat, NodeALon);

        var (_, tightSpeed) = GroundNavigator.ComputeArcSteering(ctx, tightArc, fromNodeIsZero: true);
        var (_, gentleSpeed) = GroundNavigator.ComputeArcSteering(ctx, gentleArc, fromNodeIsZero: true);

        Assert.True(gentleSpeed > tightSpeed, $"Gentle arc speed {gentleSpeed:F1}kts should > tight arc {tightSpeed:F1}kts");
    }

    [Fact]
    public void Speed_IsPositive()
    {
        var arc = MakeArc();
        var ctx = MakeContext(NodeALat, NodeALon);

        var (_, speed) = GroundNavigator.ComputeArcSteering(ctx, arc, fromNodeIsZero: true);
        Assert.True(speed > 0, $"Speed should be positive, got {speed}");
    }

    [Fact]
    public void Speed_FlooredAtMinRadius()
    {
        // Set MinRadiusOfCurvatureFt very high to force the floor to dominate.
        // If local curvature gives a smaller radius, the floor should kick in.
        var arc = MakeArc(minRadiusFt: 500.0);
        var ctx = MakeContext(NodeALat, NodeALon);

        var (_, speed) = GroundNavigator.ComputeArcSteering(ctx, arc, fromNodeIsZero: true);

        // Speed from 500ft radius with Jet turn rate (20 deg/s):
        // ω = 20 × π/180 = 0.349 rad/s; R = 500/6076.12 = 0.0823nm; V = 0.349 × 0.0823 × 3600 ≈ 103.4 kts
        double expectedMin = 20.0 * Math.PI / 180.0 * (500.0 / GeoMath.FeetPerNm) * 3600.0;
        Assert.True(speed >= expectedMin * 0.95, $"Speed {speed:F1}kts should be ≥ floor speed {expectedMin:F1}kts (with 5% tolerance)");
    }

    // --- MaxSafeSpeedKts formula verification ---

    [Fact]
    public void MaxSafeSpeedKts_MatchesFormula()
    {
        var arc = MakeArc(minRadiusFt: 75.0);
        double turnRate = CategoryPerformance.GroundTurnRate(AircraftCategory.Jet); // 20 deg/s

        double expected = turnRate * Math.PI / 180.0 * (75.0 / GeoMath.FeetPerNm) * 3600.0;
        double actual = arc.MaxSafeSpeedKts(turnRate);

        Assert.Equal(expected, actual, 0.01);
    }
}
