using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for the <see cref="VisualAcquisition"/> wrapper's weather sourcing:
/// traffic acquisition must use the air mass at the ownship's position (nearest
/// reporting station), not the ownship's flight-plan destination — an overflight
/// with no destination, or a departure bound hundreds of miles away, still flies
/// in the local weather.
/// </summary>
public class VisualAcquisitionTests
{
    public VisualAcquisitionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // KOAK BKN060: base 6000 AGL + 9 ft elevation ≈ 6009 MSL. Ownship below at
    // 5000, target above at 8000 → the deck lies between them.
    private static WeatherProfile OakBkn060() => new() { Metars = ["KOAK 121853Z 27012KT 10SM BKN060 20/12 A2992"] };

    [Fact]
    public void TryAcquireTraffic_NoDestination_UsesNearestStationWeather()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000, destination: null);
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 8000, destination: null);
        var result = VisualAcquisition.TryAcquireTraffic(own, tgt, OakBkn060());
        Assert.False(result.Acquired, "Overflight near KOAK must be blocked by the local BKN060 deck despite having no destination");
        Assert.Equal(VisualAcquisitionFailure.MixedCeiling, result.Reason);
    }

    [Fact]
    public void TryAcquireTraffic_DistantDestination_UsesNearestStationWeather()
    {
        // Destination LAX is ~300 nm from the aircraft's position near KOAK; the
        // only reporting station is KOAK. The local deck must still block.
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000, destination: "LAX");
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 8000, destination: "LAX");
        var result = VisualAcquisition.TryAcquireTraffic(own, tgt, OakBkn060());
        Assert.False(result.Acquired, "Local KOAK deck must block even though the flight plan ends at LAX");
        Assert.Equal(VisualAcquisitionFailure.MixedCeiling, result.Reason);
    }

    [Fact]
    public void TryAcquireTraffic_NoStationNearby_ClearSkyAssumption()
    {
        // Aircraft over the Pacific, far outside the 50 nm station-association
        // range: no local weather is known, so the acquisition is not blocked.
        var own = MakeAircraft(35.0, -130.0, heading: 180, altitude: 5000, destination: null);
        var tgt = MakeAircraft(34.97, -130.0, heading: 180, altitude: 8000, destination: null);
        var result = VisualAcquisition.TryAcquireTraffic(own, tgt, OakBkn060());
        Assert.True(result.Acquired);
    }

    [Fact]
    public void TryMaintainTrafficContact_NoDestination_UsesNearestStationWeather()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000, destination: null);
        var tgt = MakeAircraft(37.72, -122.221, heading: 180, altitude: 8000, destination: null);
        var result = VisualAcquisition.TryMaintainTrafficContact(own, tgt, OakBkn060());
        Assert.False(result.Acquired);
        Assert.Equal(VisualAcquisitionFailure.MixedCeiling, result.Reason);
    }

    // KOAK 3SM, no clouds: flight-visibility range 3 SM ≈ 2.6 nm. The target sits
    // ~5 nm ahead (0.083° of latitude) at the same altitude — beyond what the pilot
    // can physically see through the haze, so maintained contact breaks on
    // visibility alone (no cloud layer involved).
    private static WeatherProfile Oak3SmClear() => new() { Metars = ["KOAK 121853Z 27012KT 3SM CLR 20/12 A2992"] };

    [Fact]
    public void TryMaintainTrafficContact_VisibilityBelowGap_LosesContact()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000, destination: null);
        var tgt = MakeAircraft(37.667, -122.221, heading: 180, altitude: 5000, destination: null);
        var result = VisualAcquisition.TryMaintainTrafficContact(own, tgt, Oak3SmClear());
        Assert.False(result.Acquired, "A 5 nm gap in 3SM visibility must break maintained contact");
        Assert.Equal(VisualAcquisitionFailure.OutOfRange, result.Reason);
    }

    [Fact]
    public void TryMaintainTrafficContact_GapInToleranceBand_KeepsContact()
    {
        // 3SM: the ACQUISITION visibility cap is 2.61 nm, but maintained contact
        // carries a 1.25× tracking tolerance (≈3.26 nm) so threshold chatter between
        // two continuously-varying quantities cannot irreversibly cancel a follow.
        // 0.05° of latitude = 3.0 nm — beyond acquisition, inside the maintain band.
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000, destination: null);
        var tgt = MakeAircraft(37.70, -122.221, heading: 180, altitude: 5000, destination: null);
        var result = VisualAcquisition.TryMaintainTrafficContact(own, tgt, Oak3SmClear());
        Assert.True(result.Acquired, "A gap inside the 1.25× maintain tolerance must keep contact");
    }

    [Fact]
    public void TryMaintainTrafficContact_GapWithinVisibility_KeepsContact()
    {
        var own = MakeAircraft(37.75, -122.221, heading: 180, altitude: 5000, destination: null);
        var tgt = MakeAircraft(37.717, -122.221, heading: 180, altitude: 5000, destination: null);
        var result = VisualAcquisition.TryMaintainTrafficContact(own, tgt, Oak3SmClear());
        Assert.True(result.Acquired, "A ~2 nm gap in 3SM visibility stays within the flight-visibility range");
    }

    private static AircraftState MakeAircraft(double lat, double lon, double heading, double altitude, string? destination)
    {
        var aircraft = new AircraftState
        {
            Callsign = "TST100",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 250,
        };
        if (destination is not null)
        {
            aircraft.FlightPlan.Destination = destination;
        }
        return aircraft;
    }
}
