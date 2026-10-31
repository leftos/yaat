using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The one runway-user rule the hold bars and every Safety Logic alert kind read, on SFO's real runways: an arrival inside
/// 6,000 ft of the threshold along the extended centreline, or an aircraft over the footprint at 50 kt or more below 200 ft
/// above the runway, aligned by track. Anything else on the footprint, on the ground or below 200 ft, only occupies it.
/// </summary>
public class AsdexRunwayUsersTests
{
    private readonly IReadOnlyList<AsdexHoldBarRunway> _runways;
    private readonly IReadOnlyList<AsdexRunwayAxis> _axes;
    private readonly double _fieldElevationFt;

    public AsdexRunwayUsersTests()
    {
        TestVnasData.EnsureInitialized();
        _runways = AsdexHoldBarBuilderTests.SfoRunways();
        _axes = [.. _runways.Select(runway => new AsdexRunwayAxis(runway.Runway))];
        _fieldElevationFt = NavigationDatabase.Instance.GetAirportElevation("SFO") ?? throw new InvalidOperationException("SFO has no elevation");
    }

    private RunwayInfo End(string end) => AsdexHoldBarBuilderTests.RunwayWithEnd(_runways, end).Runway.ForApproach(end);

    private int IndexOf(string end) => _runways.ToList().IndexOf(AsdexHoldBarBuilderTests.RunwayWithEnd(_runways, end));

    private bool IsUserOf(AircraftState aircraft, string end) =>
        AsdexRunwayUsers.IsUserOf(aircraft, AsdexRunwayUsers.UsesOf(aircraft, _runways, _axes, _fieldElevationFt), _runways, IndexOf(end));

    private bool Occupies(AircraftState aircraft, string end) => AsdexRunwayUsers.Occupies(aircraft, _runways[IndexOf(end)]);

    private static double RunwayElevationFt(RunwayInfo end) => (end.Elevation1Ft + end.Elevation2Ft) / 2;

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
        double trackDeg = end.TrueHeading.Degrees + offsetDeg;
        return new AircraftState
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
    }

    private AircraftState Arrival(RunwayInfo end, double beforeThresholdFt, double heightFt) =>
        At(end, -beforeThresholdFt, _fieldElevationFt + heightFt, onGround: false, speedKts: 140, offsetDeg: 0);

    private static AircraftState Rolling(RunwayInfo end, double alongFt, double speedKts) =>
        At(end, alongFt, RunwayElevationFt(end), onGround: true, speedKts: speedKts, offsetDeg: 0);

    private static AircraftState OverRunway(RunwayInfo end, double alongFt, double heightFt, double speedKts) =>
        At(end, alongFt, RunwayElevationFt(end) + heightFt, onGround: false, speedKts: speedKts, offsetDeg: 0);

    /// <summary>1R and 28L cross on the airfield: the crossing lies on both centrelines, between each runway's two ends.</summary>
    [Fact]
    public void Crossing_1RAnd28L_LiesOnBothRunways()
    {
        AsdexRunwayAxis axis1R = _axes[IndexOf("01R")];
        AsdexRunwayAxis axis28L = _axes[IndexOf("28L")];

        Assert.True(axis1R.TryCrossing(axis28L, out LatLon crossing));

        Assert.InRange(axis1R.Along(crossing), 0, axis1R.LengthFt);
        Assert.InRange(axis28L.Along(crossing), 0, axis28L.LengthFt);
        Assert.InRange(axis1R.Lateral(crossing), -1, 1);
        Assert.InRange(axis28L.Lateral(crossing), -1, 1);
    }

    /// <summary>A zero-length runway (both ends at one point) has no direction, so it crosses nothing.</summary>
    [Fact]
    public void Crossing_ZeroLengthRunway_None()
    {
        RunwayInfo real = End("28L");
        var point = new RunwayInfo
        {
            AirportId = real.AirportId,
            Id = real.Id,
            Designator = real.Designator,
            Lat1 = real.Lat1,
            Lon1 = real.Lon1,
            Elevation1Ft = real.Elevation1Ft,
            TrueHeading1 = real.TrueHeading1,
            Lat2 = real.Lat1,
            Lon2 = real.Lon1,
            Elevation2Ft = real.Elevation1Ft,
            TrueHeading2 = real.TrueHeading2,
            WidthFt = real.WidthFt,
        };

        Assert.False(new AsdexRunwayAxis(point).TryCrossing(_axes[IndexOf("01R")], out _));
        Assert.False(_axes[IndexOf("01R")].TryCrossing(new AsdexRunwayAxis(point), out _));
    }

    [Fact]
    public void Crossing_ParallelRunways28LAnd28R_None()
    {
        Assert.False(_axes[IndexOf("28L")].TryCrossing(_axes[IndexOf("28R")], out _));
        Assert.False(_axes[IndexOf("01L")].TryCrossing(_axes[IndexOf("01R")], out _));
    }

    /// <summary>
    /// How far past <paramref name="end"/>'s threshold, along its centreline, the runway crosses the one holding
    /// <paramref name="otherEnd"/>.
    /// </summary>
    private double CrossingFt(string end, string otherEnd)
    {
        Assert.True(_axes[IndexOf(end)].TryCrossing(_axes[IndexOf(otherEnd)], out LatLon crossing));
        RunwayInfo runway = End(end);
        return GeoMath.DistanceNm(runway.ThresholdLatitude, runway.ThresholdLongitude, crossing.Lat, crossing.Lon) * GeoMath.FeetPerNm;
    }

    private bool ConvergesAhead(AircraftState a, string endA, AircraftState b, string endB)
    {
        AsdexRunwayUse useA = AsdexRunwayUsers.UseOn(AsdexRunwayUsers.UsesOf(a, _runways, _axes, _fieldElevationFt), _runways, IndexOf(endA))!.Value;
        AsdexRunwayUse useB = AsdexRunwayUsers.UseOn(AsdexRunwayUsers.UsesOf(b, _runways, _axes, _fieldElevationFt), _runways, IndexOf(endB))!.Value;
        return AsdexRunwayUsers.ConvergesAhead(useA, useB, _axes);
    }

    /// <summary>A departure rolling on 1R before the 28L crossing and an arrival in 28L's box both have the crossing ahead.</summary>
    [Fact]
    public void ConvergesAhead_DepartureOn1RBeforeTheCrossing_AndArrivalTo28L()
    {
        AircraftState departure = Rolling(End("01R"), CrossingFt("01R", "28L") - 2000, 80);
        AircraftState arrival = Arrival(End("28L"), 3000, 300);

        Assert.True(ConvergesAhead(departure, "01R", arrival, "28L"));
        Assert.True(ConvergesAhead(arrival, "28L", departure, "01R"));
    }

    /// <summary>A departure already past the 28L crossing on 1R moves away from it: the pair no longer converges.</summary>
    [Fact]
    public void ConvergesAhead_DeparturePastTheCrossing_DoesNot()
    {
        AircraftState departure = Rolling(End("01R"), CrossingFt("01R", "28L") + 500, 80);
        AircraftState arrival = Arrival(End("28L"), 3000, 300);

        Assert.False(ConvergesAhead(departure, "01R", arrival, "28L"));
    }

    private static RunwayInfo IahRunway(string end) =>
        NavigationDatabase.Instance.GetRunway("IAH", end)
        ?? NavigationDatabase.Instance.GetRunway("KIAH", end)
        ?? throw new InvalidOperationException($"IAH {end} is not in navdata");

    /// <summary>
    /// The axes of the two IAH runways holding <paramref name="endA"/> and <paramref name="endB"/>, and where their
    /// centrelines meet.
    /// </summary>
    private static (AsdexRunwayAxis[] Axes, LatLon Crossing) IahPair(string endA, string endB)
    {
        AsdexRunwayAxis[] axes = [new AsdexRunwayAxis(IahRunway(endA)), new AsdexRunwayAxis(IahRunway(endB))];
        Assert.True(axes[0].TryCrossing(axes[1], out LatLon crossing));
        return (axes, crossing);
    }

    /// <summary>A use of runway <paramref name="index"/> from its midpoint, moving toward <paramref name="crossing"/>.</summary>
    private static AsdexRunwayUse MidRunwayTowardTheCrossing(AsdexRunwayAxis[] axes, int index, LatLon crossing)
    {
        double mid = axes[index].LengthFt / 2;
        return new AsdexRunwayUse(index, mid, axes[index].Along(crossing) > mid ? 1 : -1);
    }

    /// <summary>How far (ft) past the nearer runway end the crossing lies along the axis; 0 when it lies on the runway.</summary>
    private static double BeyondTheEndFt(AsdexRunwayAxis axis, LatLon crossing)
    {
        double along = axis.Along(crossing);
        return Math.Max(0, Math.Max(-along, along - axis.LengthFt));
    }

    /// <summary>
    /// IAH 15L/33R and 8R/26L do not intersect; their centrelines meet about 0.38 NM and 0.33 NM beyond their ends, inside
    /// the 1 NM that makes flight paths intersect (7110.65 §3-9-9.b, §3-10-4.a): two aircraft rolling toward it converge.
    /// </summary>
    [Fact]
    public void ConvergesAhead_NonintersectingRunwaysMeetingWithin1NmBeyondTheEnds_Converge()
    {
        (AsdexRunwayAxis[] axes, LatLon crossing) = IahPair("15L", "8R");
        Assert.InRange(BeyondTheEndFt(axes[0], crossing), 1, GeoMath.FeetPerNm);
        Assert.InRange(BeyondTheEndFt(axes[1], crossing), 1, GeoMath.FeetPerNm);

        Assert.True(
            AsdexRunwayUsers.ConvergesAhead(MidRunwayTowardTheCrossing(axes, 0, crossing), MidRunwayTowardTheCrossing(axes, 1, crossing), axes)
        );
    }

    /// <summary>
    /// IAH 15L/33R and 8L/26R meet about 1.31 NM beyond the 15/33 end but 0.57 NM beyond the 8/26 end: within 1 NM of
    /// either departure end (7110.65 §3-9-9.b), so the pair converges.
    /// </summary>
    [Fact]
    public void ConvergesAhead_CentrelinesMeetingWithin1NmOfOneDepartureEndOnly_Converge()
    {
        (AsdexRunwayAxis[] axes, LatLon crossing) = IahPair("15L", "8L");
        Assert.True(BeyondTheEndFt(axes[0], crossing) > GeoMath.FeetPerNm);
        Assert.InRange(BeyondTheEndFt(axes[1], crossing), 1, GeoMath.FeetPerNm);

        Assert.True(
            AsdexRunwayUsers.ConvergesAhead(MidRunwayTowardTheCrossing(axes, 0, crossing), MidRunwayTowardTheCrossing(axes, 1, crossing), axes)
        );
    }

    /// <summary>OAK 30 meets 28L/10R (2.51 / 2.91 NM) and 28R/10L (3.05 / 3.54 NM) beyond 1 NM of both ends: no convergence.</summary>
    [Theory]
    [InlineData("28L")]
    [InlineData("28R")]
    public void ConvergesAhead_CentrelinesMeetingMoreThan1NmBeyondBothEnds_DoNot(string otherEnd)
    {
        RunwayInfo r30 = NavigationDatabase.Instance.GetRunway("OAK", "30") ?? throw new InvalidOperationException("OAK 30 is not in navdata");
        RunwayInfo other =
            NavigationDatabase.Instance.GetRunway("OAK", otherEnd) ?? throw new InvalidOperationException($"OAK {otherEnd} is not in navdata");
        AsdexRunwayAxis[] axes = [new AsdexRunwayAxis(r30), new AsdexRunwayAxis(other)];
        Assert.True(axes[0].TryCrossing(axes[1], out LatLon crossing));
        Assert.True(BeyondTheEndFt(axes[0], crossing) > GeoMath.FeetPerNm);
        Assert.True(BeyondTheEndFt(axes[1], crossing) > GeoMath.FeetPerNm);

        Assert.False(
            AsdexRunwayUsers.ConvergesAhead(MidRunwayTowardTheCrossing(axes, 0, crossing), MidRunwayTowardTheCrossing(axes, 1, crossing), axes)
        );
    }

    /// <summary>
    /// An arrival to 15L/33R whose landing threshold lies past the crossing (the centrelines meeting in its approach, before
    /// the threshold it lands over) does not converge, though the crossing is ahead of it.
    /// </summary>
    [Fact]
    public void ConvergesAhead_CrossingInFrontOfTheLandingThreshold_DoesNot()
    {
        (AsdexRunwayAxis[] axes, LatLon crossing) = IahPair("15L", "8R");
        double along = axes[0].Along(crossing);
        // Landing away from the crossing's end: 3,000 ft further out on the approach than the crossing, moving toward it.
        int direction = along > axes[0].LengthFt ? -1 : 1;
        var arrival = new AsdexRunwayUse(0, along - (direction * 3000), direction);

        Assert.False(AsdexRunwayUsers.ConvergesAhead(arrival, MidRunwayTowardTheCrossing(axes, 1, crossing), axes));
    }

    [Fact]
    public void LinedUpAt0Kt_IsNotAUser_ButOccupiesTheRunway()
    {
        AircraftState linedUp = Rolling(End("28R"), 100, 0);

        Assert.False(IsUserOf(linedUp, "28R"));
        Assert.True(Occupies(linedUp, "28R"));
    }

    [Fact]
    public void ADeparture_IsAUserAt50Kt_NotAt49()
    {
        Assert.False(IsUserOf(Rolling(End("28R"), 800, 49), "28R"));
        Assert.True(IsUserOf(Rolling(End("28R"), 800, 50), "28R"));
    }

    [Fact]
    public void AnArrival_At5999FtAlongThe28LCentreline_IsAUser_At6100Ft_IsNot()
    {
        Assert.True(IsUserOf(Arrival(End("28L"), 5999, 320), "28L"));
        Assert.False(IsUserOf(Arrival(End("28L"), 6100, 320), "28L"));
    }

    [Fact]
    public void AClimbingDeparture_StopsBeingAUser_Through200FtAboveTheRunway()
    {
        Assert.True(IsUserOf(OverRunway(End("01L"), 5000, 190, 160), "01L"));
        Assert.False(IsUserOf(OverRunway(End("01L"), 5000, 200, 160), "01L"));
    }

    /// <summary>
    /// Alignment is by track: an arrival crabbing into a crosswind, its track on the 28L centreline, is a user whatever its
    /// heading.
    /// </summary>
    [Fact]
    public void ACrabbingArrival_TrackOnTheCentreline_HeadingOff_IsAUser()
    {
        RunwayInfo end = End("28L");
        AircraftState arrival = Arrival(end, 3000, 160);
        arrival.TrueHeading = new TrueHeading(end.TrueHeading.Degrees + 40);

        Assert.True(IsUserOf(arrival, "28L"));
    }

    [Fact]
    public void ABackTaxiAt15Kt_IsAnOccupant_NotAUser()
    {
        AircraftState backTaxi = At(End("28R"), 3000, RunwayElevationFt(End("28R")), onGround: true, speedKts: 15, offsetDeg: 180);

        Assert.False(IsUserOf(backTaxi, "28R"));
        Assert.True(Occupies(backTaxi, "28R"));
    }

    /// <summary>An airborne aircraft over the footprint that is not a user (a hover) occupies the runway below 200 ft above it only.</summary>
    [Fact]
    public void AnAirborneNonUserOverTheFootprint_OccupiesItAt150Ft_NotAt250Ft()
    {
        AircraftState low = OverRunway(End("28L"), 3000, 150, 5);
        AircraftState high = OverRunway(End("28L"), 3000, 250, 5);

        Assert.False(IsUserOf(low, "28L"));
        Assert.True(Occupies(low, "28L"));
        Assert.False(Occupies(high, "28L"));
    }

    /// <summary>
    /// Below 1 kt an aircraft has no track, so its heading stands for it: an arrival hovering 3,000 ft short of 28L with its
    /// nose down the runway is a user whatever its track, and one with its nose across the runway is not, whatever its track.
    /// </summary>
    [Fact]
    public void BelowOneKnot_TheHeadingStandsForTheTrack()
    {
        RunwayInfo end = End("28L");
        AircraftState noseOn = Arrival(end, 3000, 160);
        noseOn.IndicatedAirspeed = 0.5;
        noseOn.TrueTrack = new TrueHeading(end.TrueHeading.Degrees + 90);
        AircraftState noseAcross = Arrival(end, 3000, 160);
        noseAcross.IndicatedAirspeed = 0.5;
        noseAcross.TrueHeading = new TrueHeading(end.TrueHeading.Degrees + 90);

        Assert.True(noseOn.GroundSpeed < 1);
        Assert.True(IsUserOf(noseOn, "28L"));
        Assert.False(IsUserOf(noseAcross, "28L"));
    }

    /// <summary>
    /// Two aircraft head opposite ways along 28R/10L when each is aligned with the runway within 35° and they move toward each
    /// other: reciprocal tracks closing do, a track 36° off the reciprocal does not, two aircraft moving the same way do not,
    /// and reciprocal tracks moving apart do not.
    /// </summary>
    [Fact]
    public void HeadsOpposite_ReciprocalAlignedTracks_Not36DegOff_NotTheSameDirection()
    {
        RunwayInfo end = End("28R");
        AsdexRunwayAxis axis = _axes[IndexOf("28R")];
        AircraftState rolling = Rolling(end, 2000, 80);
        AircraftState reciprocal = At(end, 8000, RunwayElevationFt(end), onGround: true, speedKts: 20, offsetDeg: 180);
        AircraftState offBy36 = At(end, 8000, RunwayElevationFt(end), onGround: true, speedKts: 20, offsetDeg: 180 + 36);
        AircraftState sameWay = At(end, 8000, RunwayElevationFt(end), onGround: true, speedKts: 20, offsetDeg: 0);
        AircraftState behindMovingAway = At(end, 1000, RunwayElevationFt(end), onGround: true, speedKts: 20, offsetDeg: 180);

        Assert.True(AsdexRunwayUsers.HeadsOpposite(rolling, reciprocal, axis));
        Assert.True(AsdexRunwayUsers.HeadsOpposite(reciprocal, rolling, axis));
        Assert.False(AsdexRunwayUsers.HeadsOpposite(rolling, offBy36, axis));
        Assert.False(AsdexRunwayUsers.HeadsOpposite(rolling, sameWay, axis));
        Assert.False(AsdexRunwayUsers.HeadsOpposite(rolling, behindMovingAway, axis));
        Assert.False(AsdexRunwayUsers.HeadsOpposite(behindMovingAway, rolling, axis));
    }

    /// <summary>
    /// A live-traffic shadow rolling at 50 kt or more with no acceleration to read (no history yet) is an arrival when it was
    /// seen landing on the runway, and a departure otherwise.
    /// </summary>
    [Theory]
    [InlineData(true, AsdexRunwayUserRole.Arrival)]
    [InlineData(false, AsdexRunwayUserRole.Departure)]
    public void AGroundShadowWithNoAccelerationToRead_IsAnArrivalOnlyIfSeenLanding(bool landedOnRunway, AsdexRunwayUserRole expected)
    {
        AircraftState shadow = Rolling(End("28R"), 2000, 80);
        shadow.LiveTraffic = new AircraftLiveTraffic { Source = LiveTrafficSource.Asdex, LandedOnRunway = landedOnRunway };

        AsdexRunwayUses uses = AsdexRunwayUsers.UsesOf(shadow, _runways, _axes, _fieldElevationFt);

        Assert.Equal(expected, AsdexRunwayUsers.Role(shadow, AsdexRunwayUsers.ArrivesOn(uses, _runways, IndexOf("28R"))));
    }

    /// <summary>An aircraft inside 28L's arrival box, 3,000 ft before the threshold and 300 ft up, in <paramref name="phase"/>.</summary>
    private AircraftState InsideThe28LBox(Phase phase) => WithPhase(Arrival(End("28L"), 3000, 300), phase);

    private static AircraftState WithPhase(AircraftState aircraft, Phase phase)
    {
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        return aircraft;
    }

    /// <summary>The role of an aircraft on 28L, proved to be inside 28L's arrival box first.</summary>
    private AsdexRunwayUserRole RoleInsideThe28LBox(AircraftState aircraft)
    {
        AsdexRunwayUses uses = AsdexRunwayUsers.UsesOf(aircraft, _runways, _axes, _fieldElevationFt);
        bool arrivesOn28L = AsdexRunwayUsers.ArrivesOn(uses, _runways, IndexOf("28L"));
        Assert.True(arrivesOn28L);
        return AsdexRunwayUsers.Role(aircraft, arrivesOn28L);
    }

    /// <summary>A takeoff phase already airborne off a parallel stays a departure inside the arrival box it is passing through.</summary>
    [Fact]
    public void Role_TakeoffPhaseInsideAParallelsBox_IsDeparture() =>
        Assert.Equal(AsdexRunwayUserRole.Departure, RoleInsideThe28LBox(InsideThe28LBox(new TakeoffPhase())));

    /// <summary>An initial climb off a parallel stays a departure inside the arrival box it is passing through.</summary>
    [Fact]
    public void Role_InitialClimbInsideAParallelsBox_IsDeparture() =>
        Assert.Equal(AsdexRunwayUserRole.Departure, RoleInsideThe28LBox(InsideThe28LBox(new InitialClimbPhase())));

    /// <summary>A helicopter takeoff off a parallel stays a departure inside the arrival box it is passing through.</summary>
    [Fact]
    public void Role_HelicopterTakeoffInsideAParallelsBox_IsDeparture() =>
        Assert.Equal(AsdexRunwayUserRole.Departure, RoleInsideThe28LBox(InsideThe28LBox(new HelicopterTakeoffPhase())));

    /// <summary>A touch-and-go that has re-accelerated is a departure (7110.65 §3-8-2), inside another runway's arrival box too.</summary>
    [Fact]
    public void Role_ReacceleratingTouchAndGoInsideAParallelsBox_IsDeparture() =>
        Assert.Equal(AsdexRunwayUserRole.Departure, RoleInsideThe28LBox(InsideThe28LBox(LiftedOffOptionPhases.TouchAndGo(End("28L")))));

    /// <summary>A go-around climbs, but it is still an arrival to the runway whose box it is in until it is past the threshold.</summary>
    [Fact]
    public void Role_GoAroundInsideARunwaysBox_StaysArrival() =>
        Assert.Equal(AsdexRunwayUserRole.Arrival, RoleInsideThe28LBox(InsideThe28LBox(new GoAroundPhase())));

    /// <summary>A live-traffic shadow has no phase to tell a departure by, so inside a box it is an arrival even while climbing.</summary>
    [Fact]
    public void Role_ShadowClimbingInsideABox_StaysArrival()
    {
        AircraftState shadow = Arrival(End("28L"), 3000, 300);
        shadow.LiveTraffic = new AircraftLiveTraffic { Source = LiveTrafficSource.Asdex };
        shadow.VerticalSpeed = 1500;

        Assert.Equal(AsdexRunwayUserRole.Arrival, RoleInsideThe28LBox(shadow));
    }

    /// <summary>The runway end whose takeoff or landing direction leads along the axis toward <paramref name="crossing"/>.</summary>
    private static string EndTowardTheCrossing(AsdexRunwayAxis axis, LatLon crossing) =>
        axis.Along(crossing) > axis.LengthFt ? axis.Runway.Id.End1 : axis.Runway.Id.End2;

    /// <summary>A 400 ft wide footprint ring along the runway, from <paramref name="end"/>'s threshold.</summary>
    private static List<LatLon> Footprint(RunwayInfo end, double lengthFt)
    {
        var start = new LatLon(end.ThresholdLatitude, end.ThresholdLongitude);
        LatLon stop = GeoMath.ProjectPoint(start, end.TrueHeading, lengthFt / GeoMath.FeetPerNm);
        var right = new TrueHeading(end.TrueHeading.Degrees + 90);
        var left = new TrueHeading(end.TrueHeading.Degrees - 90);
        double halfWidthNm = 200 / GeoMath.FeetPerNm;
        return
        [
            GeoMath.ProjectPoint(start, left, halfWidthNm),
            GeoMath.ProjectPoint(stop, left, halfWidthNm),
            GeoMath.ProjectPoint(stop, right, halfWidthNm),
            GeoMath.ProjectPoint(start, right, halfWidthNm),
        ];
    }

    /// <summary>
    /// IAH 15L/33R and 8R/26L meet beyond their ends, inside the 1 NM reach. A departure climbing out 100 ft up still converges
    /// with an arrival to the other runway while it is over its own, and stops counting once it is airborne past the end, the
    /// crossing still ahead of it: the reach holds only while it is a runway user.
    /// </summary>
    [Theory]
    [InlineData(-1000, true)]
    [InlineData(1000, false)]
    public void ADepartureAirbornePastItsRunwaysEnd_StopsCounting(double pastTheEndFt, bool converges)
    {
        (AsdexRunwayAxis[] axes, LatLon crossing) = IahPair("15L", "8R");
        RunwayInfo departureEnd = axes[0].Runway.ForApproach(EndTowardTheCrossing(axes[0], crossing));
        RunwayInfo arrivalEnd = axes[1].Runway.ForApproach(EndTowardTheCrossing(axes[1], crossing));
        Assert.True(pastTheEndFt < BeyondTheEndFt(axes[0], crossing));
        AircraftState departure = WithPhase(
            At(departureEnd, axes[0].LengthFt + pastTheEndFt, RunwayElevationFt(departureEnd) + 100, onGround: false, speedKts: 160, offsetDeg: 0),
            new InitialClimbPhase()
        );
        AircraftState arrival = WithPhase(
            At(arrivalEnd, -3000, RunwayElevationFt(arrivalEnd) + 300, onGround: false, speedKts: 140, offsetDeg: 0),
            new FinalApproachPhase()
        );
        AsdexHoldBarRunway[] runways =
        [
            new("15L-33R", axes[0].Runway, Footprint(departureEnd, axes[0].LengthFt)),
            new("8R-26L", axes[1].Runway, Footprint(arrivalEnd, axes[1].LengthFt)),
        ];
        var surfaces = new AsdexSafetyLogicRunways(
            [new AsdexRunwaySurface(runways[0], axes[0], isClosed: false), new AsdexRunwaySurface(runways[1], axes[1], isClosed: false)],
            [runways[0].Area, runways[1].Area]
        );

        AsdexDetection detection = AsdexSafetyLogicDetector.Detect(
            surfaces,
            [],
            [departure, arrival],
            RunwayElevationFt(arrivalEnd),
            new AsdexDetectionInputs(false, AsdexDetection.NoOppDirPairs)
        );

        Assert.Equal(converges, detection.Alerts.Any(alert => alert.Kind == AsdexAlertKind.Converging));
    }
}
