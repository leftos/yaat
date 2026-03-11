using Xunit;

namespace Yaat.Sim.Tests;

public class VisualDetectionTests
{
    // KOAK: 37.721, -122.221, elevation 9ft, Runway 28R heading ~284°
    private const double AptLat = 37.721;
    private const double AptLon = -122.221;
    private const double AptElev = 9.0;

    public VisualDetectionTests()
    {
        TestVnasData.EnsureInitialized();
    }

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
    public void CanSeeTraffic_AboveFL180_True()
    {
        // Pilots can see traffic in Class A — only visual separation is prohibited (7110.65 §7-1-1)
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 18000);
        var tgt = MakeAircraft(37.73, -122.221, heading: 180, altitude: 18000);
        Assert.True(VisualDetection.CanSeeTraffic(own, tgt, null, AptElev, 10.0));
    }

    [Fact]
    public void CanSeeAirport_AboveFL180_StillFalse()
    {
        // Visual approaches still prohibited in Class A
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 18000);
        Assert.False(VisualDetection.CanSeeAirport(ac, AptLat, AptLon, AptElev, null, 10.0));
    }

    // -------------------------------------------------------------------------
    // Bank angle occlusion
    // -------------------------------------------------------------------------

    [Fact]
    public void BankOcclusion_RightTurn_TargetLeftAndBelow_Occluded()
    {
        // Right bank +25°, target on left (high-wing side) at same altitude
        Assert.True(VisualDetection.IsOccludedByBank(25.0, 360, 315, 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_RightTurn_TargetLeftAndAbove_NotOccluded()
    {
        // Right bank +25°, target on left but well above (above 1000ft buffer)
        Assert.False(VisualDetection.IsOccludedByBank(25.0, 360, 315, 3000, 4500));
    }

    [Fact]
    public void BankOcclusion_RightTurn_TargetRightAndBelow_NotOccluded()
    {
        // Right bank +25°, target on right (low-wing side)
        Assert.False(VisualDetection.IsOccludedByBank(25.0, 360, 45, 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_RightTurn_TargetAhead_NotOccluded()
    {
        // Right bank +25°, target ahead (within 10° nose cone)
        Assert.False(VisualDetection.IsOccludedByBank(25.0, 360, 5, 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_LeftTurn_TargetRightAndBelow_Occluded()
    {
        // Left bank -25°, target on right (high-wing side) at same altitude
        Assert.True(VisualDetection.IsOccludedByBank(-25.0, 360, 45, 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_ShallowBank_NotOccluded()
    {
        // Bank only 12° → below threshold
        Assert.False(VisualDetection.IsOccludedByBank(12.0, 360, 315, 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_ModerateBank_SameAltitude_Occluded()
    {
        // Bank 20° (moderate), target at same altitude (within 500ft buffer)
        Assert.True(VisualDetection.IsOccludedByBank(20.0, 360, 315, 3000, 3000));
    }

    [Fact]
    public void BankOcclusion_ModerateBank_Target600Above_NotOccluded()
    {
        // Bank 20° (moderate), target 600ft above → above 500ft buffer for moderate bank
        Assert.False(VisualDetection.IsOccludedByBank(20.0, 360, 315, 3000, 3600));
    }

    // -------------------------------------------------------------------------
    // Aircraft size (CWT-based range)
    // -------------------------------------------------------------------------

    [Fact]
    public void CanSeeTraffic_SmallTarget_ShortRange()
    {
        // C172 is CWT I → 2.5nm detection range
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        // Target ~2nm away (within 2.5nm CWT I range)
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 3000);
        tgt.AircraftType = "C172";
        Assert.True(VisualDetection.CanSeeTraffic(own, tgt, null, AptElev, null));

        // Target ~4nm away (beyond 2.5nm CWT I range)
        var tgtFar = MakeAircraft(37.68, -122.221, heading: 180, altitude: 3000);
        tgtFar.AircraftType = "C172";
        Assert.False(VisualDetection.CanSeeTraffic(own, tgtFar, null, AptElev, null));
    }

    [Fact]
    public void CanSeeTraffic_MediumJet_MidRange()
    {
        // B738 is CWT F → 7.0nm detection range
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        // Target ~5nm away (within 7nm CWT F range)
        var tgt = MakeAircraft(37.67, -122.221, heading: 180, altitude: 3000);
        tgt.AircraftType = "B738";
        Assert.True(VisualDetection.CanSeeTraffic(own, tgt, null, AptElev, null));
    }

    [Fact]
    public void CanSeeTraffic_HeavyWidebody_LongRange()
    {
        // B77W is CWT B → 12.0nm detection range
        var own = MakeAircraft(37.85, -122.221, heading: 180, altitude: 5000);
        // Target ~9nm away (within 12nm CWT B range)
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 5000);
        tgt.AircraftType = "B77W";
        Assert.True(VisualDetection.CanSeeTraffic(own, tgt, null, AptElev, null));
    }

    [Fact]
    public void CanSeeTraffic_UnknownType_FallsBackToCategory()
    {
        // Unknown type categorizes as Jet (default) → 7.0nm fallback
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000);
        // Target ~8nm away (beyond 7nm jet fallback range)
        var tgt = MakeAircraft(37.62, -122.221, heading: 180, altitude: 3000);
        tgt.AircraftType = "ZZZZ";
        Assert.False(VisualDetection.CanSeeTraffic(own, tgt, null, AptElev, null));
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
        };
    }
}
