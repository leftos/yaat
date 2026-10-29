using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;

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
}
