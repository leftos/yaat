using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="RelativeGeometry"/>: the clock position, distance and altitude difference the context menu's For
/// section and traffic list name another aircraft by, and their wording.
/// </summary>
public class RelativeGeometryTests
{
    [Fact]
    public void ClockPosition_AheadIsTwelve()
    {
        Assert.Equal(12, RelativeGeometry.ClockPosition(90, 90));
        Assert.Equal(12, RelativeGeometry.ClockPosition(90, 104));
        Assert.Equal(12, RelativeGeometry.ClockPosition(90, 76));
    }

    [Fact]
    public void ClockPosition_HoursAroundTheDial()
    {
        Assert.Equal(5, RelativeGeometry.ClockPosition(0, 150));
        Assert.Equal(5, RelativeGeometry.ClockPosition(90, 240));
        Assert.Equal(3, RelativeGeometry.ClockPosition(0, 90));
        Assert.Equal(6, RelativeGeometry.ClockPosition(0, 180));
        Assert.Equal(9, RelativeGeometry.ClockPosition(0, 270));
    }

    [Fact]
    public void ClockPosition_WrapsAtNorth()
    {
        Assert.Equal(1, RelativeGeometry.ClockPosition(350, 20));
        Assert.Equal(11, RelativeGeometry.ClockPosition(10, 340));
        Assert.Equal(12, RelativeGeometry.ClockPosition(0, 359));
        Assert.Equal(12, RelativeGeometry.ClockPosition(359, 1));
    }

    [Fact]
    public void AltitudeDelta_BelowAboveAndLevel()
    {
        Assert.Equal(-1500, RelativeGeometry.AltitudeDelta(3000, 1500));
        Assert.Equal(500, RelativeGeometry.AltitudeDelta(1000, 1520));
        Assert.Equal(4000, RelativeGeometry.AltitudeDelta(2000, 5960));
        Assert.Equal(0, RelativeGeometry.AltitudeDelta(5000, 5060));
        Assert.Equal(0, RelativeGeometry.AltitudeDelta(5000, 4901));
        Assert.Equal(-100, RelativeGeometry.AltitudeDelta(5000, 4900));

        Assert.Equal("1,500 ft below", RelativeGeometry.AltitudeDeltaLine(-1500));
        Assert.Equal("500 ft above", RelativeGeometry.AltitudeDeltaLine(500));
        Assert.Equal("same altitude", RelativeGeometry.AltitudeDeltaLine(0));
        Assert.Equal("1,500 below", RelativeGeometry.AltitudeDeltaColumn(-1500));
        Assert.Equal("4,000 above", RelativeGeometry.AltitudeDeltaColumn(4000));
        Assert.Equal("same altitude", RelativeGeometry.AltitudeDeltaColumn(0));
    }

    [Fact]
    public void WholeNm_RoundsHalfAwayFromZero()
    {
        Assert.Equal(4, RelativeGeometry.WholeNm(3.5));
        Assert.Equal(3, RelativeGeometry.WholeNm(3.49));
        Assert.Equal(0, RelativeGeometry.WholeNm(0.4));
    }

    [Fact]
    public void TrafficType_DropsTheEquipmentSuffix()
    {
        Assert.Equal("B77W", RelativeGeometry.TrafficType("B77W/L"));
        Assert.Equal("E75L", RelativeGeometry.TrafficType("E75L/L"));
        Assert.Equal("B763", RelativeGeometry.TrafficType("H/B763/L"));
        Assert.Equal("B738", RelativeGeometry.TrafficType("B738"));
        Assert.Equal("", RelativeGeometry.TrafficType(""));
    }

    [Fact]
    public void IsAhead_WithinNinetyDegreesEitherSide()
    {
        Assert.True(RelativeGeometry.IsAhead(0, 0));
        Assert.True(RelativeGeometry.IsAhead(0, 90));
        Assert.True(RelativeGeometry.IsAhead(10, 280));
        Assert.False(RelativeGeometry.IsAhead(0, 91));
        Assert.False(RelativeGeometry.IsAhead(0, 180));
        Assert.False(RelativeGeometry.IsAhead(350, 250));
    }

    [Fact]
    public void FeetText_FiftyUnderAThousandThenHundreds()
    {
        Assert.Equal("450 ft", RelativeGeometry.FeetText(440));
        Assert.Equal("300 ft", RelativeGeometry.FeetText(310));
        Assert.Equal("1,000 ft", RelativeGeometry.FeetText(990));
        Assert.Equal("2,100 ft", RelativeGeometry.FeetText(2120));
        Assert.Equal("3,400 ft", RelativeGeometry.FeetText(3350));
    }

    [Fact]
    public void GroundStateColumn_StateAndPlaceStrings()
    {
        Assert.Equal(
            "pushing back · gate 24",
            RelativeGeometry.GroundStateColumn(Ground("SWA1", "Pushback", parkingSpot: "24", taxiway: ""), ahead: true)
        );
        Assert.Equal(
            "taxiing on W · ahead",
            RelativeGeometry.GroundStateColumn(Ground("SWA1", "Taxiing", parkingSpot: "", taxiway: "W"), ahead: true)
        );
        Assert.Equal(
            "at parking · gate 1",
            RelativeGeometry.GroundStateColumn(Ground("SWA1", "At Parking", parkingSpot: "1", taxiway: ""), ahead: false)
        );
        Assert.Equal(
            "holding short of 30 at W3 · behind",
            RelativeGeometry.GroundStateColumn(Ground("SWA1", "Holding Short 30/12", parkingSpot: "", taxiway: "W3"), ahead: false)
        );
        Assert.Equal(
            "holding short of 30 · behind",
            RelativeGeometry.GroundStateColumn(Ground("SWA1", "Holding Short RWY 30", parkingSpot: "", taxiway: ""), ahead: false)
        );
        Assert.Equal(
            "holding in position · ahead",
            RelativeGeometry.GroundStateColumn(Ground("SWA1", "Holding In Position", parkingSpot: "", taxiway: ""), ahead: true)
        );
        Assert.Equal(
            "following SWA200 on B · ahead",
            RelativeGeometry.GroundStateColumn(Ground("SWA1", "Following SWA200", parkingSpot: "", taxiway: "B"), ahead: true)
        );
    }

    [Fact]
    public void DescribeGround_FormatsTheMockLine()
    {
        AircraftModel selected = Ground("SWA602", "Taxiing", parkingSpot: "", taxiway: "W");
        AircraftModel clicked = Ground("SWA601", "Holding Short 30/12", parkingSpot: "", taxiway: "W3");
        clicked.Position = new LatLon(selected.Position.Lat + (300 / GeoMath.FeetPerNm / 60), selected.Position.Lon);

        Assert.Equal(
            "SWA601 is holding short of 30 at W3, 300 ft ahead on SWA602's route",
            RelativeGeometry.DescribeGround(selected, clicked, onFromRoute: true)
        );
        Assert.Equal("SWA602 is taxiing on W, 300 ft behind", RelativeGeometry.DescribeGround(clicked, selected, onFromRoute: false));
    }

    private static AircraftModel Ground(string callsign, string phase, string parkingSpot, string taxiway) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            IsOnGround = true,
            CurrentPhase = phase,
            ParkingSpot = parkingSpot,
            CurrentTaxiway = taxiway,
            Position = new LatLon(37.72, -122.22),
            Heading = new TrueHeading(0),
        };

    [Fact]
    public void Describe_FormatsTheMockLine()
    {
        Assert.Equal("AAL601 is at its 2 o'clock, 4 nm, 1,500 ft below", RelativeGeometry.Describe("AAL601", 2, 4, -1500));
        Assert.Equal("N1 is at its 12 o'clock, 1 nm, same altitude", RelativeGeometry.Describe("N1", 12, 1, 0));
    }
}
