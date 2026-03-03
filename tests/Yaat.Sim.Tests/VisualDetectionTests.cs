using Xunit;

namespace Yaat.Sim.Tests;

public class VisualDetectionTests
{
    // KOAK: 37.721, -122.221, elevation 9ft, Runway 28R heading ~284°
    private const double AptLat = 37.721;
    private const double AptLon = -122.221;
    private const double AptElev = 9.0;

    // -------------------------------------------------------------------------
    // CanSeeAirport — basic cases
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeAirport_InFront_WithinRange_BelowCeiling_True()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.True(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, 5000, 10.0));
    }

    [Fact]
    public void CanSeeAirport_Behind_False()
    {
        // Aircraft heading north, airport to the south → behind
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        Assert.False(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, 5000, 10.0));
    }

    [Fact]
    public void CanSeeAirport_BeyondVisibility_False()
    {
        // 1SM visibility ≈ 0.869nm, airport ~2nm away
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.False(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, 5000, 1.0));
    }

    [Fact]
    public void CanSeeAirport_AboveCeiling_False()
    {
        // Ceiling 2000 AGL + 9ft elevation = 2009 MSL, aircraft at 3000 MSL
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.False(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, 2000, 10.0));
    }

    [Fact]
    public void CanSeeAirport_NoCeiling_StillChecksRangeAndBearing()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 10000);
        Assert.True(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, null, 10.0));
    }

    [Fact]
    public void CanSeeAirport_AboveFL180_False()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 18000);
        Assert.False(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, null, 10.0));
    }

    [Fact]
    public void CanSeeAirport_NoVisibility_UsesMaxRange()
    {
        // Aircraft 5nm away, no visibility data → max range 12nm
        var ac = MakeAircraft(37.80, -122.221, heading: 180, altitude: 3000);
        Assert.True(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, null, null));
    }

    // -------------------------------------------------------------------------
    // CanSeeAirportForRunway — approach side check
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeForRunway_OnApproachSide_True()
    {
        // Runway heading 284° → approach from ~104° (east side)
        // Aircraft to the east of airport, heading west toward airport
        var ac = MakeAircraft(37.721, -122.15, heading: 270, altitude: 3000);
        Assert.True(VisualDetection.CanSeeAirportForRunway(ac, AptLat, AptLon, AptElev, null, 10.0, 284.0));
    }

    [Fact]
    public void CanSeeForRunway_OnDepartureSide_False()
    {
        // Aircraft to the west of airport (departure end for Rwy 28R), looking east at airport
        var ac = MakeAircraft(37.721, -122.30, heading: 90, altitude: 3000);
        Assert.False(VisualDetection.CanSeeAirportForRunway(ac, AptLat, AptLon, AptElev, null, 10.0, 284.0));
    }

    [Fact]
    public void CanSeeForRunway_OnDownwind_True()
    {
        // Aircraft slightly south of airport, on a left downwind for 28R
        // Heading north-ish (350°) so airport is in forward hemisphere
        // bearing from airport to aircraft is roughly south (~180°), approach side reciprocal is 104°
        // 180-104 = 76° < 120° → should pass approach-side check
        var ac = MakeAircraft(37.69, -122.221, heading: 350, altitude: 3000);
        Assert.True(VisualDetection.CanSeeAirportForRunway(ac, AptLat, AptLon, AptElev, null, 10.0, 284.0));
    }

    // -------------------------------------------------------------------------
    // CanSeeTraffic
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeTraffic_InFront_WithinRange_True()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 3000);
        Assert.True(VisualDetection.CanSeeTraffic(own, tgt, 5000, AptElev, 10.0));
    }

    [Fact]
    public void CanSeeTraffic_Behind_False()
    {
        var own = MakeAircraft(37.73, -122.221, heading: 180, altitude: 3000);
        var tgt = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        Assert.False(VisualDetection.CanSeeTraffic(own, tgt, 5000, AptElev, 10.0));
    }

    [Fact]
    public void CanSeeTraffic_OppositeSidesOfCeiling_False()
    {
        // Ceiling at 3000 AGL + 9 = 3009 MSL. Own at 2500 (below), target at 4000 (above)
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 2500);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 4000);
        Assert.False(VisualDetection.CanSeeTraffic(own, tgt, 3000, AptElev, 10.0));
    }

    [Fact]
    public void CanSeeTraffic_BeyondVisibility_False()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 3000);
        // 0.5SM = 0.43nm, targets ~1.3nm apart
        Assert.False(VisualDetection.CanSeeTraffic(own, tgt, null, AptElev, 0.5));
    }

    [Fact]
    public void CanSeeTraffic_AboveFL180_False()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 18000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 18000);
        Assert.False(VisualDetection.CanSeeTraffic(own, tgt, null, AptElev, 10.0));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(double lat, double lon, double heading, double altitude)
    {
        return new AircraftState
        {
            Callsign = "TST100",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            Track = heading,
            Altitude = altitude,
            IndicatedAirspeed = 250,
            GroundSpeed = 250,
        };
    }
}
