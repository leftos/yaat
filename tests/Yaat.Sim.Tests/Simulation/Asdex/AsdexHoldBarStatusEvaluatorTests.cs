using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The stateless hold-bar status rule on SFO's real bar set, with aircraft put where the rule's thresholds are: an arrival
/// lights its runway inside one mile of the threshold, a departure lights only the bars ahead once it rolls fast, bars clear as
/// a user passes them and all at once when it slows, climbs out or crosses the threshold high; nothing else lights a bar.
/// </summary>
public class AsdexHoldBarStatusEvaluatorTests
{
    private readonly IReadOnlyList<AsdexHoldBarRunway> _runways;
    private readonly IReadOnlyList<AsdexHoldBar> _bars;
    private readonly double _fieldElevationFt;

    public AsdexHoldBarStatusEvaluatorTests()
    {
        TestVnasData.EnsureInitialized();
        _runways = AsdexHoldBarBuilderTests.SfoRunways();
        _bars = AsdexHoldBarBuilder.Build(_runways, AsdexHoldBarBuilderTests.SfoLayout());
        _fieldElevationFt = NavigationDatabase.Instance.GetAirportElevation("SFO") ?? throw new InvalidOperationException("SFO has no elevation");
    }

    /// <summary>The landing end <paramref name="end"/> of its safety-logic runway, and that runway's id.</summary>
    private (RunwayInfo End, string RunwayId) End(string end)
    {
        AsdexHoldBarRunway runway = AsdexHoldBarBuilderTests.RunwayWithEnd(_runways, end);
        return (runway.Runway.ForApproach(end), runway.Id);
    }

    private IReadOnlySet<string> Active(params AircraftState[] aircraft) => ActiveOn(_runways, _bars, aircraft);

    private IReadOnlySet<string> ActiveOn(IReadOnlyList<AsdexHoldBarRunway> runways, IReadOnlyList<AsdexHoldBar> bars, AircraftState[] aircraft) =>
        AsdexHoldBarStatusEvaluator.ActiveBarIds(
            runways,
            [.. runways.Select(runway => new AsdexRunwayAxis(runway.Runway))],
            bars,
            aircraft,
            _fieldElevationFt
        );

    private HashSet<string> BarsOf(string runwayId) => [.. _bars.Where(bar => bar.ProtectedRunwayId == runwayId).Select(bar => bar.Id)];

    /// <summary>The ids of <paramref name="runwayId"/>'s bars farther than <paramref name="pastFt"/> down the runway from <paramref name="end"/>'s threshold.</summary>
    private HashSet<string> BarsBeyond(RunwayInfo end, string runwayId, double pastFt)
    {
        var along = AsdexBarAlong.From(end);
        return [.. _bars.Where(bar => (bar.ProtectedRunwayId == runwayId) && (along.Of(bar) > pastFt)).Select(bar => bar.Id)];
    }

    private static double RunwayElevationFt(RunwayInfo end) => (end.Elevation1Ft + end.Elevation2Ft) / 2;

    /// <summary>Measures a bar's centre along a runway direction from its threshold.</summary>
    private sealed class AsdexBarAlong(LatLon threshold, TrueHeading heading)
    {
        public static AsdexBarAlong From(RunwayInfo end) => new(new LatLon(end.ThresholdLatitude, end.ThresholdLongitude), end.TrueHeading);

        public double Of(AsdexHoldBar bar)
        {
            var centre = new LatLon((bar.Points[0].Lat + bar.Points[1].Lat) / 2, (bar.Points[0].Lon + bar.Points[1].Lon) / 2);
            return GeoMath.AlongTrackDistanceNm(centre, threshold, heading) * GeoMath.FeetPerNm;
        }
    }

    /// <summary>
    /// An aircraft on <paramref name="end"/>'s centreline, <paramref name="alongFt"/> past its threshold (negative before it),
    /// heading and tracking <paramref name="offsetDeg"/> off the runway direction.
    /// </summary>
    private static AircraftState At(RunwayInfo end, double alongFt, double altitudeFt, bool onGround, double speedKts, double offsetDeg)
    {
        var threshold = new LatLon(end.ThresholdLatitude, end.ThresholdLongitude);
        LatLon position =
            alongFt >= 0
                ? GeoMath.ProjectPoint(threshold, end.TrueHeading, alongFt / GeoMath.FeetPerNm)
                : GeoMath.ProjectPoint(threshold, new TrueHeading(end.TrueHeading.Degrees + 180), -alongFt / GeoMath.FeetPerNm);
        return Aircraft(position, altitudeFt, onGround, speedKts, end.TrueHeading.Degrees + offsetDeg);
    }

    private static AircraftState Aircraft(LatLon position, double altitudeFt, bool onGround, double speedKts, double trackDeg) =>
        new()
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = position,
            Altitude = altitudeFt,
            IsOnGround = onGround,
            IndicatedAirspeed = speedKts,
            TrueHeading = new TrueHeading(trackDeg),
            TrueTrack = new TrueHeading(trackDeg),
        };

    private AircraftState Arrival(RunwayInfo end, double beforeThresholdFt, double heightFt) =>
        At(end, -beforeThresholdFt, _fieldElevationFt + heightFt, onGround: false, speedKts: 140, offsetDeg: 0);

    private AircraftState Rolling(RunwayInfo end, double alongFt, double speedKts) =>
        At(end, alongFt, RunwayElevationFt(end), onGround: true, speedKts: speedKts, offsetDeg: 0);

    private static AircraftState OverRunway(RunwayInfo end, double alongFt, double heightFt, double speedKts) =>
        At(end, alongFt, RunwayElevationFt(end) + heightFt, onGround: false, speedKts: speedKts, offsetDeg: 0);

    /// <summary>
    /// SFO's parallels are 750 ft apart, inside each other's lateral band: an arrival lights the runway whose centreline it
    /// is on, all the way from one mile to the threshold, never its neighbour's.
    /// </summary>
    [Theory]
    [InlineData("28R", 5900)]
    [InlineData("28R", 3000)]
    [InlineData("28R", 300)]
    [InlineData("28L", 3000)]
    [InlineData("28L", 300)]
    [InlineData("01L", 3000)]
    [InlineData("01L", 300)]
    [InlineData("01R", 3000)]
    [InlineData("19R", 3000)]
    [InlineData("19R", 300)]
    [InlineData("19L", 3000)]
    public void AnArrival_LightsOnlyTheRunwayItIsLinedUpOn(string landingEnd, double beforeThresholdFt)
    {
        (RunwayInfo end, string runwayId) = End(landingEnd);
        Assert.Equal(BarsOf(runwayId), Active(Arrival(end, beforeThresholdFt, 20 + (0.0524 * beforeThresholdFt))));
    }

    [Fact]
    public void SimultaneousArrivalsOnBothParallels_EachLightOnlyTheirOwnRunway()
    {
        (RunwayInfo left, string leftId) = End("28L");
        (RunwayInfo right, string rightId) = End("28R");
        IReadOnlySet<string> active = Active(Arrival(left, 2000, 125), Arrival(right, 2000, 125));

        Assert.Equal(BarsOf(leftId).Union(BarsOf(rightId)).ToHashSet(), active);
    }

    /// <summary>
    /// An arrival on a centreline just outside the range lights nothing, the staggered parallel whose threshold lies nearer
    /// included: the runway it is lined up on is chosen first, and the range applies to that runway alone.
    /// </summary>
    [Theory]
    [InlineData("19R", 6500)]
    [InlineData("01L", 6200)]
    [InlineData("10R", 6300)]
    public void AnArrivalJustOutsideTheRange_LightsNothing_NotEvenTheStaggeredParallel(string landingEnd, double beforeThresholdFt)
    {
        (RunwayInfo end, _) = End(landingEnd);
        Assert.Empty(Active(Arrival(end, beforeThresholdFt, 20 + (0.0524 * beforeThresholdFt))));
    }

    [Fact]
    public void Arrival28L_At6100Ft_LightsNothing()
    {
        (RunwayInfo end, _) = End("28L");
        Assert.Empty(Active(Arrival(end, 6100, 320)));
    }

    [Fact]
    public void Arrival28L_At5900Ft_LightsEvery28LBar_AndNoneOf28R()
    {
        (RunwayInfo end, string runwayId) = End("28L");
        IReadOnlySet<string> active = Active(Arrival(end, 5900, 310));

        Assert.Equal(BarsOf(runwayId), active);
        Assert.Empty(active.Intersect(BarsOf(End("28R").RunwayId)));
    }

    /// <summary>
    /// Alignment is judged by where the aircraft goes, not where its nose points: an arrival crabbing into a crosswind, its
    /// track on the 28L centreline, lights 28L whatever its heading, past the alignment tolerance included.
    /// </summary>
    [Theory]
    [InlineData(33)]
    [InlineData(40)]
    [InlineData(-40)]
    public void ACrabbingArrival_TrackingThe28LCentreline_LightsEvery28LBar(double headingOffDeg)
    {
        (RunwayInfo end, string runwayId) = End("28L");
        AircraftState arrival = Arrival(end, 3000, 160);
        arrival.TrueHeading = new TrueHeading(end.TrueHeading.Degrees + headingOffDeg);

        Assert.Equal(BarsOf(runwayId), Active(arrival));
    }

    [Fact]
    public void LanderOverThe28LRunway_PastTheFirstBars_ClearsThem_AndKeepsTheRest()
    {
        (RunwayInfo end, string runwayId) = End("28L");
        IReadOnlySet<string> active = Active(At(end, 800, _fieldElevationFt + 60, onGround: false, speedKts: 135, offsetDeg: 0));

        HashSet<string> ahead = BarsBeyond(end, runwayId, 800);
        Assert.Equal(ahead, active);
        Assert.Contains(BarsOf(runwayId).Except(ahead), id => id.Contains("|C|", StringComparison.Ordinal));
        Assert.Contains(BarsOf(runwayId).Except(ahead), id => id.Contains("|F2|", StringComparison.Ordinal));
    }

    /// <summary>
    /// A lander rolling out on 28L clears the bars across 1R and 1L crossing by crossing: before each crossing both of its
    /// bars are Active, once past it neither is, and the bars still ahead stay Active.
    /// </summary>
    [Fact]
    public void LanderRollingOut28L_ClearsTheRunwayCrossingBars_CrossingByCrossing()
    {
        (RunwayInfo end, string runwayId) = End("28L");
        var along = AsdexBarAlong.From(end);
        var runwayIds = _runways.Select(runway => runway.Id).ToHashSet(StringComparer.Ordinal);
        List<IGrouping<string, AsdexHoldBar>> crossings =
        [
            .. _bars
                .Where(bar => (bar.ProtectedRunwayId == runwayId) && runwayIds.Contains(bar.Id.Split('|')[1]))
                .GroupBy(bar => bar.Id.Split('|')[1])
                .OrderBy(group => group.Min(along.Of)),
        ];
        Assert.Equal(2, crossings.Count);
        Assert.All(crossings, crossing => Assert.Equal(2, crossing.Count()));

        foreach (IGrouping<string, AsdexHoldBar> crossing in crossings)
        {
            double before = crossing.Min(along.Of) - 150;
            double after = crossing.Max(along.Of) + 150;
            IReadOnlySet<string> approaching = Active(Rolling(end, before, 90));
            IReadOnlySet<string> past = Active(Rolling(end, after, 80));

            Assert.Equal(BarsBeyond(end, runwayId, before), approaching);
            Assert.All(crossing, bar => Assert.Contains(bar.Id, approaching));
            Assert.Equal(BarsBeyond(end, runwayId, after), past);
            Assert.All(crossing, bar => Assert.DoesNotContain(bar.Id, past));
        }
    }

    [Fact]
    public void LanderRollingOut_Below50Kt_ClearsEveryRemainingBar()
    {
        (RunwayInfo end, string runwayId) = End("28L");
        Assert.Empty(Active(At(end, 4500, _fieldElevationFt, onGround: true, speedKts: 49, offsetDeg: 0)));
        Assert.Equal(BarsBeyond(end, runwayId, 4500), Active(At(end, 4500, _fieldElevationFt, onGround: true, speedKts: 51, offsetDeg: 0)));
    }

    [Fact]
    public void Departure1L_LightsOnlyTheBarsAhead_OnceItRollsThrough50Kt()
    {
        (RunwayInfo end, string runwayId) = End("01L");
        Assert.Empty(Active(At(end, 800, _fieldElevationFt, onGround: true, speedKts: 45, offsetDeg: 0)));

        IReadOnlySet<string> active = Active(At(end, 800, _fieldElevationFt, onGround: true, speedKts: 55, offsetDeg: 0));
        HashSet<string> ahead = BarsBeyond(end, runwayId, 800);
        Assert.Equal(ahead, active);
        Assert.NotEmpty(BarsOf(runwayId).Except(ahead));
    }

    [Fact]
    public void Departure1L_Airborne_KeepsTheBarsAheadBelow200FtAgl_AndClearsThemAbove()
    {
        (RunwayInfo end, string runwayId) = End("01L");

        Assert.Equal(BarsBeyond(end, runwayId, 5000), Active(OverRunway(end, 5000, 190, 160)));
        Assert.Empty(Active(OverRunway(end, 5000, 210, 160)));
    }

    /// <summary>
    /// A rejected takeoff on 28R: nothing lights below 50 kt, the bars ahead light once it accelerates through 50 kt, and
    /// they all clear when it decelerates back below 50 kt still on the runway.
    /// </summary>
    [Fact]
    public void RejectedTakeoff28R_LightsTheBarsAheadPast50Kt_AndClearsThemAllBelow50Kt()
    {
        (RunwayInfo end, string runwayId) = End("28R");

        Assert.Empty(Active(Rolling(end, 600, 45)));
        HashSet<string> ahead = BarsBeyond(end, runwayId, 1500);
        Assert.NotEmpty(ahead);
        Assert.Equal(ahead, Active(Rolling(end, 1500, 85)));
        Assert.Empty(Active(Rolling(end, 2800, 45)));
    }

    /// <summary>
    /// A touch-and-go on 28L: the bars behind clear as it rolls and lifts off, the bars ahead stay Active while it climbs
    /// below 200 ft above the runway, and every remaining bar clears together as it passes 200 ft.
    /// </summary>
    [Fact]
    public void TouchAndGo28L_ClearsProgressively_ThenEverythingAt200FtAgl()
    {
        (RunwayInfo end, string runwayId) = End("28L");

        HashSet<string> rolling = BarsBeyond(end, runwayId, 1500);
        Assert.Equal(rolling, Active(Rolling(end, 1500, 100)));
        HashSet<string> rotating = BarsBeyond(end, runwayId, 4500);
        Assert.Equal(rotating, Active(Rolling(end, 4500, 120)));
        HashSet<string> climbing = BarsBeyond(end, runwayId, 7000);
        Assert.Equal(climbing, Active(OverRunway(end, 7000, 120, 125)));

        Assert.True(rotating.IsProperSubsetOf(rolling));
        Assert.True(climbing.IsProperSubsetOf(rotating));
        Assert.NotEmpty(climbing);
        Assert.Empty(Active(OverRunway(end, 7600, 205, 130)));
    }

    /// <summary>
    /// A low approach along 28L at 100 ft: the whole runway lights before the threshold, then the bars clear one stretch at a
    /// time as it passes them, until none is left past the last bar.
    /// </summary>
    [Fact]
    public void LowApproach28L_At100Ft_ClearsTheWholeRunwayProgressively()
    {
        (RunwayInfo end, string runwayId) = End("28L");
        Assert.Equal(BarsOf(runwayId), Active(Arrival(end, 500, 100)));

        HashSet<string> previous = BarsOf(runwayId);
        double lastBarFt = _bars.Where(bar => bar.ProtectedRunwayId == runwayId).Max(AsdexBarAlong.From(end).Of);
        foreach (double alongFt in new[] { 1500, 4000, 7000, 9500, lastBarFt + 100 })
        {
            HashSet<string> ahead = BarsBeyond(end, runwayId, alongFt);
            Assert.Equal(ahead, Active(OverRunway(end, alongFt, 100, 130)));
            Assert.True(ahead.IsProperSubsetOf(previous), $"nothing cleared by {alongFt:F0} ft");
            previous = ahead;
        }

        Assert.Empty(previous);
    }

    [Fact]
    public void AHelicopterHoveringOver28L_LightsNothing_ButALowApproachAt65KtDoes()
    {
        (RunwayInfo end, string runwayId) = End("28L");

        Assert.Empty(Active(OverRunway(end, 3000, 50, 5)));
        Assert.Equal(BarsBeyond(end, runwayId, 3000), Active(OverRunway(end, 3000, 100, 65)));
    }

    [Fact]
    public void GoAround_CrossingThe28LThresholdAt300FtAgl_ClearsEverything()
    {
        (RunwayInfo end, _) = End("28L");
        Assert.NotEmpty(Active(Arrival(end, 100, 290)));
        Assert.Empty(Active(At(end, 200, _fieldElevationFt + 300, onGround: false, speedKts: 150, offsetDeg: 0)));
    }

    [Fact]
    public void LevelOverflightAt1200Ft_InsideOneMile_LightsEvery28LBar()
    {
        (RunwayInfo end, string runwayId) = End("28L");
        Assert.Equal(BarsOf(runwayId), Active(At(end, -4000, 1200, onGround: false, speedKts: 210, offsetDeg: 0)));
    }

    [Fact]
    public void LinedUpAndWaiting_LightsNothing()
    {
        (RunwayInfo end, _) = End("28L");
        Assert.Empty(Active(At(end, 100, _fieldElevationFt, onGround: true, speedKts: 0, offsetDeg: 0)));
    }

    [Fact]
    public void AircraftCrossingTheRunway_LightsNothing()
    {
        (RunwayInfo end, _) = End("28L");
        Assert.Empty(Active(At(end, 6000, _fieldElevationFt, onGround: true, speedKts: 20, offsetDeg: 90)));
    }

    [Fact]
    public void AnArrivalWithItsAlertsInhibited_StillLightsTheBars()
    {
        (RunwayInfo end, string runwayId) = End("28L");
        AircraftState arrival = Arrival(end, 3000, 160);
        arrival.Stars.AsdexAlertsInhibited = true;

        Assert.Equal(BarsOf(runwayId), Active(arrival));
    }

    /// <summary>
    /// Where two runways' pavements overlap at a shallow angle, an aircraft rolling fast through the overlap aligned with both
    /// is a user of the better-aligned one only: SFO's real 28L, and a runway 20° off it through 28L's midpoint, with the
    /// aircraft 5° off 28L and 15° off the other.
    /// </summary>
    [Fact]
    public void ARollerAlignedWithTwoOverlappingRunways_LightsOnlyTheBetterAlignedOne()
    {
        AsdexHoldBarRunway left = AsdexHoldBarBuilderTests.RunwayWithEnd(_runways, "28L");
        var leftAxis = new AsdexRunwayAxis(left.Runway);
        var centre = leftAxis.ToLatLon(leftAxis.LengthFt / 2, 0);
        double skewDeg = leftAxis.BearingDeg + 20;
        LatLon skewEnd1 = GeoMath.ProjectPoint(centre, new TrueHeading(skewDeg + 180), 4000 / GeoMath.FeetPerNm);
        LatLon skewEnd2 = GeoMath.ProjectPoint(centre, new TrueHeading(skewDeg), 4000 / GeoMath.FeetPerNm);
        var skewRunway = new RunwayInfo
        {
            AirportId = "SFO",
            Id = new RunwayIdentifier("SK1", "SK2"),
            Designator = "SK1",
            Lat1 = skewEnd1.Lat,
            Lon1 = skewEnd1.Lon,
            Elevation1Ft = left.Runway.Elevation1Ft,
            TrueHeading1 = new TrueHeading(skewDeg),
            Lat2 = skewEnd2.Lat,
            Lon2 = skewEnd2.Lon,
            Elevation2Ft = left.Runway.Elevation1Ft,
            TrueHeading2 = new TrueHeading(skewDeg + 180),
            WidthFt = 200,
        };
        var skewAxis = new AsdexRunwayAxis(skewRunway);
        List<LatLon> skewArea =
        [
            skewAxis.ToLatLon(-100, -100),
            skewAxis.ToLatLon(-100, 100),
            skewAxis.ToLatLon(skewAxis.LengthFt + 100, 100),
            skewAxis.ToLatLon(skewAxis.LengthFt + 100, -100),
        ];
        var skew = new AsdexHoldBarRunway("SK1-SK2", skewRunway, skewArea);
        List<AsdexHoldBar> bars =
        [
            new("28L-ahead", left.Id, [centre, centre], (leftAxis.LengthFt / 2) + 2000),
            new("SK-ahead", skew.Id, [centre, centre], (skewAxis.LengthFt / 2) + 2000),
        ];
        AircraftState roller = Aircraft(centre, left.Runway.Elevation1Ft, onGround: true, speedKts: 100, trackDeg: leftAxis.BearingDeg + 5);

        Assert.Equal("28L-ahead", Assert.Single(ActiveOn([left, skew], bars, [roller])));
    }
}
