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
/// segment through the bar node, square to the taxiway axis there, half the taxiway width each side of the node. With the main
/// gear on the stub the bar marks, the painted line extended governs, never less than the runway half-width plus the lateral
/// slack; with it off the stub, the runway's standoff S; at a bar whose foot falls beyond a runway end, a part over that bar's
/// paint takes the larger of the two.
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

    /// <summary>KOAK 28R's bar on E at C/E, 249.1 ft from the runway's centreline.</summary>
    private static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar) Oak28REBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28R"));
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "28R", "E", GroundNodeLookup.Junction(layout, "C", "E"));
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
    public void AngledBar_PartBesideThePaint_GearOffTheStub_IsJudgedAgainstS()
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

    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    [InlineData(-10.0)]
    public void AngledBar_PartBesideThePaint_GearOnTheStub_IsJudgedAgainstThePaintedLineExtended(double pastFt)
    {
        // AIM 2-3-5.a.1: with the main gear on the stub the bar marks, every part is judged against the painted line extended.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28LB();
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        LatLon gear = Project(bar.Position, axisDeg, 40.0);
        LatLon point = RunwaySideBesideThePaint(runway, bar, pastFt);

        Assert.True(FloorTermFt(runway, point) < pastFt - 0.5); // premise: the safety floor does not govern here
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gear), pastFt - 0.5, pastFt + 0.5);
    }

    [Fact]
    public void GearOnTheStub_PartOverTheRunwayHalfWidthPlusSlack_FoulsByTheFloor()
    {
        // Along the painted line extended toward the runway, 10 ft short of it, out to 10 ft inside the runway half-width plus
        // the lateral slack: the line would call the part clear; the floor does not.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28LB();
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        LatLon gear = Project(bar.Position, axisDeg, 40.0);
        LatLon start = Project(bar.Position, axisDeg, 10.0);
        double runwaySideDeg =
            RunwayHoldBars.CentrelineFt(runway, Project(start, axisDeg + 90.0, 100.0))
            < RunwayHoldBars.CentrelineFt(runway, Project(start, axisDeg - 90.0, 100.0))
                ? axisDeg + 90.0
                : axisDeg - 90.0;
        double clPerFt =
            (RunwayHoldBars.CentrelineFt(runway, start) - RunwayHoldBars.CentrelineFt(runway, Project(start, runwaySideDeg, 100.0))) / 100.0;
        double floorFt = (runway.WidthFt / 2.0) + RunwayOccupancy.LateralSlackFt;
        LatLon point = Project(start, runwaySideDeg, (RunwayHoldBars.CentrelineFt(runway, start) - (floorFt - 10.0)) / clPerFt);

        double floorTermFt = FloorTermFt(runway, point);
        Assert.InRange(floorTermFt, 9.0, 11.0);
        double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gear);
        Assert.InRange(depthFt, floorTermFt - 0.5, floorTermFt + 0.5);
        Assert.True(depthFt > RunwayOccupancy.HoldLineFoulToleranceFt, $"depth {depthFt:F1} ft");
    }

    [Fact]
    public void GearAtTheStubCut_OneFootInside_IsJudgedOnThePaint_OneFootOutside_OnS()
    {
        // 28L's bar on B: walking back along B past the node where the B - R and B - B1 fillets leave (1742.8 ft back), the first
        // straight edge of another taxiway meets B 1829.5 ft back along B's axis at the bar; less the joining taxiway's half-width
        // (37.5 ft), the cut is 1792.0 ft.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28LB();
        double cutFt = RunwayHoldBars.StubCutFt(layout, runway, bar);
        Assert.InRange(cutFt, 1791.5, 1792.5);
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        LatLon point = RunwaySideBesideThePaint(runway, bar, 10.0);
        double paintFt = Math.Max(10.0, FloorTermFt(runway, point));
        double sFt = StandoffFt(layout, runway, bar) - RunwayHoldBars.CentrelineFt(runway, point);
        Assert.True(Math.Abs(paintFt - sFt) > 2.0, $"premise: paint {paintFt:F1} ft and S {sFt:F1} ft differ");

        // Distances along the axis are read as the marking reads them, from the bar's bearing and distance; a point projected
        // 1700 ft out reads about 1 ft farther, so the gear is placed by that reading.
        LatLon GearBackFt(double backFt)
        {
            double readFt = BackAlongAxisFt(bar, axisDeg, Project(bar.Position, axisDeg, backFt));
            return Project(bar.Position, axisDeg, backFt * backFt / readFt);
        }

        LatLon gearInside = GearBackFt(cutFt - 1.0);
        LatLon gearOutside = GearBackFt(cutFt + 1.0);
        Assert.InRange(BackAlongAxisFt(bar, axisDeg, gearInside), cutFt - 1.05, cutFt - 0.95);
        Assert.InRange(BackAlongAxisFt(bar, axisDeg, gearOutside), cutFt + 0.95, cutFt + 1.05);
        double insideFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gearInside);
        double outsideFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gearOutside);

        Assert.InRange(insideFt, paintFt - 0.5, paintFt + 0.5);
        Assert.InRange(outsideFt, sFt - 0.5, sFt + 0.5);
        Assert.InRange(insideFt - outsideFt, (paintFt - sFt) - 1.0, (paintFt - sFt) + 1.0);
    }

    [Fact]
    public void GearOnTheRunwayAtTheStubMouth_IsJudgedOnS()
    {
        // OAK 28R's bar on E at C/E (249.1 ft out, the taxiway axis 52.3° to the runway): the stub runs only from the bar to the
        // runway edge, so a gear on the runway at its mouth is off the stub and the part is judged on S, not on the paint.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28REBar();
        double sinTheta = Math.Sin(TaxiwayToRunwayDeg(runway, bar) * Math.PI / 180.0);
        double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
        double halfWidthFt = runway.WidthFt / 2.0;
        LatLon gear = Project(bar.Position, RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar) + 180.0, ((barFt - halfWidthFt) + 10.0) / sinTheta);
        Assert.InRange(RunwayHoldBars.CentrelineFt(runway, gear), halfWidthFt - 12.0, halfWidthFt - 4.0); // premise: on the runway

        LatLon point = RunwaySideBesideThePaint(runway, bar, 10.0);
        double paintFt = Math.Max(10.0, FloorTermFt(runway, point));
        double sFt = RunwayHoldBars.BesideLineFt(layout, runway, bar) - RunwayHoldBars.CentrelineFt(runway, point);
        Assert.True(Math.Abs(paintFt - sFt) > 2.0, $"premise: paint {paintFt:F1} ft and S {sFt:F1} ft differ");
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gear), sFt - 0.5, sFt + 0.5);
    }

    [Fact]
    public void GearOnTheAxisBeyondTheFarRunwayEdge_IsJudgedOnS()
    {
        // The same bar, the gear 10 ft beyond the runway's far edge on the same axis: across the centreline it is off the stub,
        // judged on S. Without the side check it read as on the stub and foul by the painted line extended.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak28REBar();
        double sinTheta = Math.Sin(TaxiwayToRunwayDeg(runway, bar) * Math.PI / 180.0);
        double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
        double halfWidthFt = runway.WidthFt / 2.0;
        LatLon gear = Project(bar.Position, RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar) + 180.0, ((barFt + halfWidthFt) + 10.0) / sinTheta);
        Assert.True(
            RunwayHoldBars.IsRightOfCentreline(runway, gear) != RunwayHoldBars.IsRightOfCentreline(runway, bar.Position),
            "premise: the gear is across the centreline"
        );
        Assert.InRange(RunwayHoldBars.CentrelineFt(runway, gear), halfWidthFt + 4.0, halfWidthFt + 12.0);

        LatLon point = RunwaySideBesideThePaint(runway, bar, 10.0);
        double paintFt = Math.Max(10.0, FloorTermFt(runway, point));
        double sFt = RunwayHoldBars.BesideLineFt(layout, runway, bar) - RunwayHoldBars.CentrelineFt(runway, point);
        Assert.True(Math.Abs(paintFt - sFt) > 2.0, $"premise: paint {paintFt:F1} ft and S {sFt:F1} ft differ");
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gear), sFt - 0.5, sFt + 0.5);
    }

    [Theory]
    [InlineData("B772")]
    [InlineData("A388")]
    public void AngledBar_WidebodyNoseAtTheBar_EveryPartWithinFiveFeet(string aircraftType)
    {
        // SFO 28L's bar on Q, a high-speed-exit-style bar on a 200 ft runway: a widebody stopped square to the marking with its
        // nose at the bar. Its runway-side wingtip lies far beside the paint and nearer the centreline than S; with the gear on
        // the stub it is judged against the painted line extended.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Assert.NotNull(RunwayBarFixture.RunwayBar("SFO", "28L", "Q"));
        Assert.InRange(TaxiwayToRunwayDeg(runway, bar), 25.0, 35.0);
        Assert.InRange(runway.WidthFt, 199.0, 201.0); // premise: a 200 ft runway
        AircraftState holder = RunwayOccupancyTests.SquareToTheMarking(runway, bar, aircraftType, facingRunway: true, 0.0, alongFt: 0);
        var body = AircraftBody.Of(holder.AircraftType);

        foreach (AircraftBodyPart part in AircraftBody.AllParts)
        {
            LatLon point = body.PointOf(part, holder.Position, holder.TrueHeading.Degrees);
            double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, holder.Position);
            Assert.True(depthFt <= RunwayOccupancy.HoldLineFoulToleranceFt, $"{part} is {depthFt:F1} ft past the marking of bar #{bar.Id}");
        }
    }

    [Fact]
    public void ParallelTrafficThroughAShortStub_IsJudgedOnS()
    {
        // OAK 28R's bar on E at C/E (taxiway axis 52.3° to the runway), measured on the layout: C/E lies 345.5 ft from 28R's
        // centreline, the bar 249.1 ft, and S at E is 249.1 ft (the standoff, capped at the bar). A B77W (half-span 106.3 ft)
        // with its gear on C at C/E, tracking along C parallel to the runway, has its runway-side wingtip 239.2 ft from the
        // centreline: the gear is off E's stub (past its cut), so the wingtip is judged on S, 9.9 ft inside it, and fouls.
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28R"));
        GroundNode ce = GroundNodeLookup.Junction(layout, "C", "E");
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "28R", "E", ce);
        double standoffFt = StandoffFt(layout, runway, bar);
        double cFt = RunwayHoldBars.CentrelineFt(runway, ce.Position);
        Assert.InRange(cFt, 345.0, 346.0);
        Assert.InRange(standoffFt, 248.6, 249.6);

        var body = AircraftBody.Of("B77W");
        double headingDeg = runway.TrueHeading.Degrees;
        LatLon wingtip = new[] { AircraftBodyPart.LeftWingtip, AircraftBodyPart.RightWingtip }
            .Select(part => body.PointOf(part, ce.Position, headingDeg))
            .MinBy(point => RunwayHoldBars.CentrelineFt(runway, point))!;
        double wingtipFt = RunwayHoldBars.CentrelineFt(runway, wingtip);
        Assert.InRange(wingtipFt, 238.7, 239.7);

        double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, wingtip, ce.Position);
        Assert.InRange(depthFt, standoffFt - wingtipFt - 0.5, standoffFt - wingtipFt + 0.5);
        Assert.True(depthFt > RunwayOccupancy.HoldLineFoulToleranceFt, $"wingtip {wingtipFt:F1} ft from the centreline, S {standoffFt:F1} ft");
    }

    [Fact]
    public void StubCut_IsSetByAStraightJoiningTaxiway_NotByAFilletArcsTangentNode()
    {
        // OAK 15/33's bar on C near J: 30.5 ft back along C, the "C - J" fillet arc leaves its tangent node (#1126); the stub runs
        // on to the node where J's straight edge meets C, and the cut is that junction's distance back along C's axis less J's
        // half-width proxy.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak33BarOnCAtJ();
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        GroundNode? atTheJunction = layout
            .Nodes.Values.Where(n =>
                n.Edges.OfType<GroundEdge>().Any(e => e.TaxiwayName == "C") && n.Edges.OfType<GroundEdge>().Any(e => e.TaxiwayName == "J")
            )
            .MinBy(n => GeoMath.DistanceNm(bar.Position, n.Position));
        Assert.NotNull(atTheJunction);
        GroundNode junction = atTheJunction;
        double expectedFt = BackAlongAxisFt(bar, axisDeg, junction.Position) - (HoldShortStandoff.TaxiwayWidthProxyFt(runway.WidthFt) / 2.0);

        double cutFt = RunwayHoldBars.StubCutFt(layout, runway, bar);
        Assert.InRange(cutFt, 158.5, 159.5); // measured at bar #353
        Assert.True(cutFt > 100.0, $"cut {cutFt:F2} ft, the C/J junction #{junction.Id} gives {expectedFt:F2} ft");
        Assert.InRange(cutFt, expectedFt - 0.5, expectedFt + 0.5);
    }

    [Fact]
    public void GearOnTheStubShortOfTheParallel_IsJudgedOnThePaint()
    {
        // OAK 28R's bar on E at C/E: a B77W with its gear on E's stub, 4.9 ft inside the stub's cut and heading along E toward
        // the runway, so its runway-side wingtip is judged against the painted line extended.
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28R"));
        GroundNode ce = GroundNodeLookup.Junction(layout, "C", "E");
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "28R", "E", ce);
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        double backFt = RunwayHoldBars.StubCutFt(layout, runway, bar) - 4.9;
        LatLon gear = Project(bar.Position, axisDeg, backFt);

        var body = AircraftBody.Of("B77W");
        LatLon wingtip = new[] { AircraftBodyPart.LeftWingtip, AircraftBodyPart.RightWingtip }
            .Select(part => body.PointOf(part, gear, axisDeg + 180.0))
            .MinBy(point => RunwayHoldBars.CentrelineFt(runway, point))!;
        double pastFt = -BackAlongAxisFt(bar, axisDeg, wingtip);
        double paintFt = Math.Max(pastFt, FloorTermFt(runway, wingtip));
        double sFt = StandoffFt(layout, runway, bar) - RunwayHoldBars.CentrelineFt(runway, wingtip);
        Assert.True(Math.Abs(paintFt - sFt) > 2.0, $"premise: paint {paintFt:F1} ft and S {sFt:F1} ft differ");

        double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, wingtip, gear);
        Assert.InRange(depthFt, paintFt - 0.5, paintFt + 0.5);
    }

    [Fact]
    public void BarBeyondTheRunwayEnd_GearOffTheStub_PartBesideThePaint_IsJudgedOnS()
    {
        // OAK 15/33's bar on C near J lies beyond the runway's end, 472.9 ft from the centreline segment, while 15/33's standoff
        // is 164.9 ft. A wingtip of an aircraft whose gear is on J (28R's bar on J at C/J) reaches beside C's paint: off C's
        // stub and beside the paint, it is judged on S, not on the bar's own distance.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak33BarOnCAtJ();
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        Assert.True(GeoMath.FootOfPerpendicular(bar.Position, end1, end2).Clamped); // premise: beyond the runway's end
        GroundRunway ground = layout.Runways.First(r => r.Id.Overlaps(runway.Id));
        Assert.InRange(Assert.NotNull(ground.HoldShortStandoffFt ?? ground.HoldShortDistanceFt), 164.4, 165.4);
        double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
        Assert.InRange(barFt, 472.4, 473.4);

        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        LatLon gearOnJ = Oak28RBarOnJAtC(layout).Position;
        LatLon wingtip = Project(bar.Position, axisDeg + 90.0, (HoldShortStandoff.TaxiwayWidthProxyFt(runway.WidthFt) / 2.0) + 5.0);

        double sLineFt = RunwayHoldBars.BesideLineFt(layout, runway, bar);
        Assert.InRange(sLineFt, 164.4, 165.4);
        double expectedFt = sLineFt - RunwayHoldBars.CentrelineFt(runway, wingtip);
        Assert.InRange(expectedFt, -320.5, -318.5);
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, wingtip, gearOnJ), expectedFt - 0.5, expectedFt + 0.5);
    }

    [Fact]
    public void BarBeyondTheRunwayEnd_GearOffTheStub_PartOverThePaint_IsJudgedOnThePaint()
    {
        // The same bar: the gear 40 ft back along C's axis and 25 ft off it — outside the paint's 17.5 ft half-width, so off
        // the stub — and a part on C's axis 10 ft past the bar's line. Over the paint, the painted line extended governs.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak33BarOnCAtJ();
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        double halfWidthFt = HoldShortStandoff.TaxiwayWidthProxyFt(runway.WidthFt) / 2.0;
        Assert.True(25.0 > halfWidthFt, $"premise: the gear lies off the stub, {halfWidthFt:F1} ft half-width");
        LatLon gear = Project(Project(bar.Position, axisDeg, 40.0), axisDeg + 90.0, 25.0);
        LatLon point = Project(bar.Position, axisDeg + 180.0, 10.0);

        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gear), 9.5, 10.5);
    }

    [Fact]
    public void BarBeyondTheRunwayEnd_GearOffTheStub_PartBehindTheLine_IsJudgedOnS()
    {
        // OAK 15/33's other off-end bar, #355: 242.3 ft from the centreline segment (200.5 ft off the centreline side, 136 ft
        // beyond its end), S 164.9 ft. Along its axis the segment distance is 242.3 at the bar, 231.7 at its nearest (100 ft
        // past) and 675.1 ft 600 ft back; a part 600 ft back reads past -600.4 ft while S reads -510.2, so the S term governs.
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "33"));
        GroundNode bar = RunwayHoldBars.RunwayBars(layout, runway).Single(b => b.Id == 355);
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        Assert.True(GeoMath.FootOfPerpendicular(bar.Position, end1, end2).Clamped); // premise: beyond the runway's end
        double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
        Assert.InRange(barFt, 241.8, 242.8); // 200.5 ft off the centreline side, 136 ft beyond its end
        double sLineFt = RunwayHoldBars.BesideLineFt(layout, runway, bar);
        Assert.InRange(sLineFt, 164.4, 165.4);

        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        LatLon point = Project(bar.Position, axisDeg, 600.0);
        LatLon gear = Project(Project(bar.Position, axisDeg, 40.0), axisDeg + 90.0, 25.0);

        double pastFt = -BackAlongAxisFt(bar, axisDeg, point);
        double sFt = sLineFt - RunwayHoldBars.CentrelineFt(runway, point);
        Assert.InRange(pastFt, -601.0, -599.0);
        Assert.InRange(sFt, -511.0, -509.0);
        Assert.True(pastFt < sFt, $"premise: behind the line at {pastFt:F1} ft, the S term at {sFt:F1} ft");
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, point, gear), sFt - 0.5, sFt + 0.5);
    }

    [Fact]
    public void BarBeyondTheRunwayEnd_ExitingTailPastThePaint_Fouls()
    {
        // A B738 leaving 15/33 on C, facing away from the runway on #353's stub: its gear is on the stub and its tail, half a
        // fuselage length behind the gear, stands 10 ft past the paint, which fouls.
        (AirportGroundLayout layout, RunwayInfo runway, GroundNode bar) = Oak33BarOnCAtJ();
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        var body = AircraftBody.Of("B738");
        double backFt = (body.LengthFt / 2.0) - 10.0;
        Assert.InRange(backFt, 0.0, RunwayHoldBars.StubCutFt(layout, runway, bar));
        LatLon gear = Project(bar.Position, axisDeg, backFt);
        LatLon tail = body.PointOf(AircraftBodyPart.Tail, gear, axisDeg);
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, tail, gear), 9.5, 10.5);

        // The runway-side wingtip is beside the paint, where the two judgements differ: on the stub it reads the painted line
        // extended, off it the S line.
        LatLon wingtip = new[] { AircraftBodyPart.LeftWingtip, AircraftBodyPart.RightWingtip }
            .Select(part => body.PointOf(part, gear, axisDeg))
            .MinBy(point => RunwayHoldBars.CentrelineFt(runway, point))!;
        double pastFt = -BackAlongAxisFt(bar, axisDeg, wingtip);
        double onStubFt = Math.Max(pastFt, FloorTermFt(runway, wingtip));
        double offStubFt = RunwayHoldBars.BesideLineFt(layout, runway, bar) - RunwayHoldBars.CentrelineFt(runway, wingtip);
        Assert.True(Math.Abs(onStubFt - offStubFt) > 2.0, $"premise: on the stub {onStubFt:F1} ft and off it {offStubFt:F1} ft differ");
        Assert.InRange(RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, wingtip, gear), onStubFt - 0.5, onStubFt + 0.5);
    }

    [Fact]
    public void TrafficOnCAbeamThe33End_IsClearOf15_33()
    {
        // C runs abeam the 33 end of 15/33. A B738 whose gear is at the C/J junction, heading 292, is judged for each of its
        // parts against 15/33's nearest same-side bar, as FollowingPhase does through IsInsideBarLine: every part is short of it.
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "33"));
        LatLon gear = GroundNodeLookup.Junction(layout, "C", "J").Position;
        var body = AircraftBody.Of("B738");

        foreach (AircraftBodyPart part in AircraftBody.AllParts)
        {
            LatLon point = body.PointOf(part, gear, 292.0);
            GroundNode nearest = Assert.IsType<GroundNode>(RunwayHoldBars.NearestSameSideBar(layout, runway, point));
            double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway, nearest, point, gear);
            Assert.True(depthFt < 0.0, $"{part} is {depthFt:F1} ft past the marking of bar #{nearest.Id}");
        }
    }

    private static (AirportGroundLayout Layout, RunwayInfo Runway, GroundNode Bar) Oak33BarOnCAtJ()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(new TestAirportGroundData().GetLayout("OAK"));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "33"));
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "33", "C", GroundNodeLookup.Junction(layout, "C", "J"));
        Assert.Contains(bar, RunwayHoldBars.RunwayBars(layout, runway));
        return (layout, runway, bar);
    }

    /// <summary>
    /// A point <paramref name="pastFt"/> past <paramref name="bar"/>'s marking line along the taxiway axis and W/2 + 10 ft
    /// along the line from the axis, on the side nearer the runway: beside the paint.
    /// </summary>
    private static LatLon RunwaySideBesideThePaint(RunwayInfo runway, GroundNode bar, double pastFt)
    {
        double axisDeg = RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar);
        double sideFt = (HoldShortStandoff.TaxiwayWidthProxyFt(runway.WidthFt) / 2.0) + 10.0;
        LatLon onAxis = Project(bar.Position, axisDeg + 180.0, pastFt);
        LatLon sideA = Project(onAxis, axisDeg + 90.0, sideFt);
        LatLon sideB = Project(onAxis, axisDeg - 90.0, sideFt);
        return RunwayHoldBars.CentrelineFt(runway, sideA) < RunwayHoldBars.CentrelineFt(runway, sideB) ? sideA : sideB;
    }

    /// <summary>
    /// How far back (ft) <paramref name="point"/> lies from <paramref name="bar"/> along the taxiway axis <paramref name="axisDeg"/>.
    /// </summary>
    private static double BackAlongAxisFt(GroundNode bar, double axisDeg, LatLon point)
    {
        double distanceFt = GeoMath.DistanceNm(bar.Position, point) * GeoMath.FeetPerNm;
        return distanceFt * Math.Cos(GeoMath.SignedBearingDifference(axisDeg, GeoMath.BearingTo(bar.Position, point)) * Math.PI / 180.0);
    }

    /// <summary>The safety floor's term: the runway half-width plus the lateral slack, less the point's centreline distance.</summary>
    private static double FloorTermFt(RunwayInfo runway, LatLon point) =>
        (runway.WidthFt / 2.0) + RunwayOccupancy.LateralSlackFt - RunwayHoldBars.CentrelineFt(runway, point);

    /// <summary>KOAK 28R's bar on J on the C/J side of the runway.</summary>
    private static GroundNode Oak28RBarOnJAtC(AirportGroundLayout layout) =>
        GroundNodeLookup.HoldShortBar(layout, "28R", "J", GroundNodeLookup.Junction(layout, "C", "J"));
}
