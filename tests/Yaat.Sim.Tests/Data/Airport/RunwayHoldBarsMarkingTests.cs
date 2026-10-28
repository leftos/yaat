using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// <see cref="RunwayHoldBars.DepthPastMarkingFt"/> judges a point against the holding position marking painted at a bar: a
/// segment through the bar node, square to the taxiway axis there, half the taxiway width each side of the node. In front of the
/// paint the depth is measured past that line along the taxiway axis; beside it, against the runway's standoff S.
/// </summary>
public class RunwayHoldBarsMarkingTests
{
    private static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar) Oak28LB()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28L"));
        GroundNode bar = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B")[0];
        return (layout, runway, bar);
    }

    private static LatLon Project(LatLon from, double bearingDeg, double distanceFt) =>
        GeoMath.ProjectPoint(from, new TrueHeading(distanceFt >= 0 ? bearingDeg : bearingDeg + 180.0), Math.Abs(distanceFt) / GeoMath.FeetPerNm);

    /// <summary>The bearing from <paramref name="point"/>'s foot on the runway centreline out to it: straight away from the runway.</summary>
    private static double AwayFromCentrelineDeg(RunwayInfo runway, LatLon point)
    {
        LatLon foot = GeoMath.FootOfPerpendicular(point, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2)).Foot;
        return GeoMath.BearingTo(foot, point);
    }

    /// <summary>The angle (deg, 0-90) between the taxiway axis at the bar and the runway.</summary>
    private static double TaxiwayToRunwayDeg(RunwayInfo runway, GroundNode bar)
    {
        double diff = GeoMath.AbsBearingDifference(RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar), runway.TrueHeading.Degrees);
        return Math.Min(diff, 180.0 - diff);
    }

    private static double StandoffFt(AirportGroundLayout layout, RunwayInfo runway, GroundNode bar)
    {
        GroundRunway ground = layout.Runways.First(r => r.Id.Overlaps(runway.Id));
        double standoffFt = Assert.NotNull(ground.HoldShortStandoffFt ?? ground.HoldShortDistanceFt);
        return Math.Min(standoffFt, RunwayHoldBars.CentrelineFt(runway, bar.Position));
    }

    [Fact]
    public void AngledBar_PartInFrontOfThePaint_PastTheLine_IsPast()
    {
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28LB();
        double thetaDeg = TaxiwayToRunwayDeg(runway, bar);
        Assert.InRange(thetaDeg, 10.0, 80.0); // premise: 28L's B bar is angled

        double towardDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar) + 180.0;
        LatLon alongTaxiway = Project(bar.Position, towardDeg, 30.0);
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, alongTaxiway, alongTaxiway), 29.0, 31.0);

        LatLon towardCentreline = Project(bar.Position, AwayFromCentrelineDeg(runway, bar.Position), -30.0);
        double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, towardCentreline, towardCentreline);
        Assert.InRange(depthFt, (30.0 * Math.Sin(thetaDeg * Math.PI / 180.0)) - 1.0, (30.0 * Math.Sin(thetaDeg * Math.PI / 180.0)) + 1.0);
        Assert.True(depthFt > RunwayOccupancy.HoldLineFoulToleranceFt, $"depth {depthFt:F1} ft at θ {thetaDeg:F1}°");
    }

    [Fact]
    public void AngledBar_PartBesideThePaint_IsJudgedAgainstS()
    {
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28LB();
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        double halfWidthFt = HoldShortStandoff.TaxiwayWidthProxyFt(runway.WidthFt) / 2.0;
        double standoffFt = StandoffFt(layout, runway, bar);

        // Along the marking line, to the end of it nearer the runway, then out along the taxiway axis to cross-track S + 3: the
        // offset along the line stays W/2 + 10, beside the paint.
        LatLon lineEndA = Project(bar.Position, axisDeg + 90.0, halfWidthFt + 10.0);
        LatLon lineEndB = Project(bar.Position, axisDeg - 90.0, halfWidthFt + 10.0);
        LatLon beside = RunwayHoldBars.CentrelineFt(runway, lineEndA) < RunwayHoldBars.CentrelineFt(runway, lineEndB) ? lineEndA : lineEndB;
        double sinTheta = Math.Sin(TaxiwayToRunwayDeg(runway, bar) * Math.PI / 180.0);
        LatLon point = Project(beside, axisDeg, ((standoffFt + 3.0) - RunwayHoldBars.CentrelineFt(runway, beside)) / sinTheta);

        Assert.InRange(RunwayHoldBars.CentrelineFt(runway, point), standoffFt + 2.5, standoffFt + 3.5);
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, point), -3.5, -2.5);
    }

    [Fact]
    public void AngledBar_CorrectHold_WingtipBeside_IsClear()
    {
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28LB();
        AircraftState holder = RunwayOccupancyTests.SquareToTheMarking(runway, bar, "B738", facingRunway: true, 0.0, alongFt: 0);
        var body = AircraftBody.Of(holder.AircraftType);

        foreach (AircraftBodyPart part in AircraftBody.AllParts)
        {
            LatLon point = body.PointOf(part, holder.Position, holder.TrueHeading.Degrees);
            GroundNode nearest = Assert.IsType<GroundNode>(RunwayHoldBars.NearestSameSideBar(layout, runway, point));
            double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, nearest, point, holder.Position);
            Assert.True(depthFt <= RunwayOccupancy.HoldLineFoulToleranceFt, $"{part} is {depthFt:F1} ft past the marking of bar #{nearest.Id}");
        }

        Assert.False(RunwayOccupancy.AnyPartPastTheHoldLine(holder, runway, layout));
    }

    [Fact]
    public void PerpendicularBar_SameAsBefore()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        (RunwayInfo runway, GroundNode bar, double offSquareDeg) = layout
            .Runways.Select(r => NavigationDatabase.Instance.GetRunway("OAK", r.Id.End1))
            .OfType<RunwayInfo>()
            .SelectMany(r => RunwayHoldBars.RunwayBars(layout, r).Select(b => (r, b, Math.Abs(90.0 - TaxiwayToRunwayDeg(r, b)))))
            .MinBy(candidate => candidate.Item3);
        Assert.InRange(offSquareDeg, 0.0, 5.0);

        double towardDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar) + 180.0;
        foreach (double pastFt in new[] { -20.0, 0.0, 10.0, 30.0 })
        {
            foreach (double offsetFt in new[] { -5.0, 0.0, 5.0 })
            {
                LatLon point = Project(Project(bar.Position, towardDeg, pastFt), towardDeg + 90.0, offsetFt);
                double oldDepthFt = RunwayHoldBars.CentrelineFt(runway, bar.Position) - RunwayHoldBars.CentrelineFt(runway, point);
                Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, point), oldDepthFt - 0.5, oldDepthFt + 0.5);
            }
        }
    }

    [Fact]
    public void PartOverAStubsPaint_AircraftOnTheParallel_IsJudgedBesideThePaint()
    {
        // 28R's bar on J at C/J (θ ≈ 38.7°): J meets the parallel C about 86 ft out, at C's first node past its 15/33 bar
        // toward C/J. Measured on the layout, a B738
        // stopped on C there has its right wingtip 24.1 ft along J's marking line and 12.1 ft past it along J's axis.
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28R"));
        GroundNode bar = Oak28RBarOnJAtC(layout);
        Assert.Contains(bar, RunwayHoldBars.RunwayBars(layout, runway));
        GroundNode cj = GroundNodeLookup.Junction(layout, "C", "J");
        GroundNode cBar33 = GroundNodeLookup.HoldShortBar(layout, "33", "C", cj);
        GroundNode onTheParallel = GroundNodeLookup.OnTaxiway(layout, "C", cBar33, 1.0, cj);

        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        LatLon past = Project(bar.Position, axisDeg + 180.0, 12.1);
        LatLon sideA = Project(past, axisDeg + 90.0, 24.1);
        LatLon sideB = Project(past, axisDeg - 90.0, 24.1);
        LatLon wingtip = RunwayHoldBars.CentrelineFt(runway, sideA) > RunwayHoldBars.CentrelineFt(runway, sideB) ? sideA : sideB;
        LatLon gearOnTheStub = Project(bar.Position, axisDeg, 40.0);

        Assert.True(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, wingtip, onTheParallel.Position) <= 0.0);
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, wingtip, gearOnTheStub), 11.1, 13.1);
    }

    [Fact]
    public void Following_NoseOverStubPaintWithGearBeside_NotInsideHoldLine()
    {
        // 28R's bar on J at C/J (θ ≈ 38.7°): a B738 crossing J's stub along its marking line toward the runway's end of it, the
        // nose in front of the paint's outer part and 10 ft past the line, the main gear beside the paint. Through the paint
        // that rises away from the runway, the nose stands farther from the centreline than the bar itself.
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28R"));
        GroundNode bar = Oak28RBarOnJAtC(layout);
        Assert.Contains(bar, RunwayHoldBars.RunwayBars(layout, runway));

        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        double halfWidthFt = HoldShortStandoff.TaxiwayWidthProxyFt(runway.WidthFt) / 2.0;
        bool plusIsOutward =
            RunwayHoldBars.CentrelineFt(runway, Project(bar.Position, axisDeg + 90.0, halfWidthFt))
            > RunwayHoldBars.CentrelineFt(runway, Project(bar.Position, axisDeg - 90.0, halfWidthFt));
        double outwardDeg = plusIsOutward ? axisDeg + 90.0 : axisDeg - 90.0;
        LatLon nose = Project(Project(bar.Position, outwardDeg, 0.8 * halfWidthFt), axisDeg + 180.0, 10.0);
        var heading = new TrueHeading(outwardDeg + 180.0);
        var follower = new AircraftState
        {
            Callsign = "FOLLOW1",
            AircraftType = "B738",
            Position = Project(nose, outwardDeg, AircraftLength.ResolveFt("B738") / 2.0),
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = runway.ElevationFt,
            IsOnGround = true,
            AirportId = "OAK",
        };

        Assert.Same(bar, RunwayHoldBars.NearestSameSideBar(layout, runway, nose));
        Assert.True(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, nose, nose) > 0.0); // premise: alone, the nose is past the paint
        Assert.True(RunwayHoldBars.CentrelineFt(runway, nose) > RunwayHoldBars.CentrelineFt(runway, bar.Position));
        Assert.Null(FollowingPhase.RunwayInsideHoldLine(follower, layout));
    }

    [Fact]
    public void Depth_DoesNotDependOnHowTheTaxiwayIsDrawn()
    {
        (_, RunwayInfo runway, GroundNode bar) = Oak28LB();
        IGroundEdge[] edges = [.. RunwayHoldBars.BarTaxiwayEdges(bar)];
        Assert.Equal(2, edges.Length);

        double bothDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar, edges);
        foreach (IGroundEdge edge in edges)
        {
            double oneDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar, [edge]);
            Assert.InRange(GeoMath.AbsBearingDifference(bothDeg, oneDeg), 0.0, 0.5);
        }
    }

    /// <summary>KOAK 28R's bar on J on the C/J side of the runway.</summary>
    private static GroundNode Oak28RBarOnJAtC(AirportGroundLayout layout) =>
        GroundNodeLookup.HoldShortBar(layout, "28R", "J", GroundNodeLookup.Junction(layout, "C", "J"));
}
