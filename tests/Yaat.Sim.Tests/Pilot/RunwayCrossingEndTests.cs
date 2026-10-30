using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The end a pilot names a runway by (<see cref="RunwayCrossingEnd.Spoken"/>), on SFO's real runway 1L/19R: an active runway by
/// its active end wherever the aircraft is on it; with both ends active, the nearer of them; with neither (or no list for the
/// airport), the end nearest the aircraft.
/// </summary>
public class RunwayCrossingEndTests
{
    private const string Runway = "01L/19R";

    private static readonly AirportGroundLayout Sfo = new() { AirportId = "SFO" };

    /// <summary>An aircraft standing on the 19R threshold, so the nearest end of 1L/19R is 19R.</summary>
    private static AircraftState AtThe19RThreshold() => AtThreshold("19R");

    /// <summary>An aircraft standing on the threshold of SFO's runway end <paramref name="end"/>.</summary>
    private static AircraftState AtThreshold(string end)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo threshold =
            NavigationDatabase.Instance.GetRunway("SFO", end) ?? throw new InvalidOperationException($"SFO {end} missing from navdata");
        return new AircraftState
        {
            Callsign = "UAL1",
            AircraftType = "B738",
            Position = new LatLon(threshold.ThresholdLatitude, threshold.ThresholdLongitude),
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            Phases = new PhaseList(),
        };
    }

    private static ActiveRunways Listing(string airport, params string[] ends) =>
        ActiveRunways.Empty.With(airport, [.. ends.Select(end => new ActiveRunway(end, ActiveRunwayUse.Both))]);

    [Fact]
    public void OneEndActive_NamesTheActiveEnd_EvenNearestTheOther()
    {
        string end = RunwayCrossingEnd.Spoken(AtThe19RThreshold(), Runway, Sfo, Listing("SFO", "01L", "01R"));

        Assert.Equal("1L", end);
    }

    [Fact]
    public void BothEndsActive_NamesTheNearerActiveEnd()
    {
        string end = RunwayCrossingEnd.Spoken(AtThe19RThreshold(), Runway, Sfo, Listing("SFO", "01L", "19R"));

        Assert.Equal("19R", end);
    }

    [Fact]
    public void BothEndsActive_AtTheOtherThreshold_NamesThatNearerActiveEnd()
    {
        string end = RunwayCrossingEnd.Spoken(AtThreshold("01L"), Runway, Sfo, Listing("SFO", "01L", "19R"));

        Assert.Equal("1L", end);
    }

    /// <summary>A list keyed by the ICAO id with an unpadded designator still names the active end.</summary>
    [Fact]
    public void ActiveEndListedByIcaoIdAndUnpaddedDesignator_NamesTheActiveEnd()
    {
        string end = RunwayCrossingEnd.Spoken(AtThe19RThreshold(), Runway, Sfo, Listing("KSFO", "1L"));

        Assert.Equal("1L", end);
    }

    [Fact]
    public void NoActiveRunways_NamesTheNearestEnd()
    {
        string end = RunwayCrossingEnd.Spoken(AtThe19RThreshold(), Runway, Sfo, ActiveRunways.Empty);

        Assert.Equal("19R", end);
    }

    [Fact]
    public void ActiveRunwaysOnlyAtAnotherAirport_NamesTheNearestEnd()
    {
        string end = RunwayCrossingEnd.Spoken(AtThe19RThreshold(), Runway, Sfo, Listing("OAK", "28R"));

        Assert.Equal("19R", end);
    }
}
