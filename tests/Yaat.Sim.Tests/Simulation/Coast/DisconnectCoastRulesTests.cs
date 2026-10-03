using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation.Coast;

namespace Yaat.Sim.Tests.Simulation.Coast;

/// <summary>
/// <see cref="DisconnectCoastRules.IsVisibleOnEram"/> exempts the tracks that ERAM coverage does not govern: a QH-frozen
/// track and a QT-coasted track are present wherever the target is, matching the CRC visibility tracker.
/// </summary>
public class DisconnectCoastRulesTests
{
    public DisconnectCoastRulesTests() => TestVnasData.EnsureInitialized();

    private static AircraftState OnTheGround() =>
        new()
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            Position = new LatLon(37.7213, -122.2208),
            Altitude = 9,
            IsOnGround = true,
        };

    [Fact]
    public void TargetOnTheGround_IsNotVisible() => Assert.False(DisconnectCoastRules.IsVisibleOnEram(OnTheGround(), NavigationDatabase.Instance));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrozenOrQtCoastedTrack_IsVisible_WhereverTheTargetIs(bool coasted)
    {
        AircraftState ac = OnTheGround();
        ac.Eram.IsFrozen = !coasted;
        ac.Eram.IsCoastTrack = coasted;

        Assert.True(DisconnectCoastRules.IsVisibleOnEram(ac, NavigationDatabase.Instance));
    }

    // ── Coast-vs-drop decision (CRC docs asdex.md:799): drop when the flight-plan destination is
    //    this surface facility, coast otherwise. Identifiers normalize across ICAO/FAA forms. ──

    [Theory]
    [InlineData("KOAK", "OAK", true)] // ICAO destination, FAA facility → same airport → drop
    [InlineData("OAK", "OAK", true)] // both FAA → drop
    [InlineData("KSFO", "OAK", false)] // departing OAK for SFO → coast
    [InlineData("", "OAK", false)] // no destination filed → coast (never drop)
    public void IsDestinationFacility_DropsOnlyAtDestinationField(string destination, string facility, bool expectedDrop)
    {
        string normalized = NavigationDatabase.NormalizeAirport(destination);
        Assert.Equal(expectedDrop, DisconnectCoastRules.IsDestinationFacility(normalized, facility));
    }
}
