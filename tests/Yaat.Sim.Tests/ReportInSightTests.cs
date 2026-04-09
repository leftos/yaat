using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Covers the live visual acquisition path in the RTIS (report traffic in
/// sight) and RFIS (report field in sight) command handlers. Each failure
/// reason from <see cref="VisualAcquisitionFailure"/> is exercised via a
/// shaped scenario around KOAK so the RPO-facing message can be asserted.
/// </summary>
[Collection("NavDbMutator")]
public class ReportInSightTests
{
    // KOAK: ~37.721, -122.221, elevation 9ft, runway 28R heading ~284°
    private const double AptLat = 37.721;
    private const double AptLon = -122.221;

    public ReportInSightTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // RFIS — report field in sight
    // -------------------------------------------------------------------------

    [Fact]
    public void Rfis_SucceedsInClearWeatherInsideRange()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "KOAK");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.True(ac.HasReportedFieldInSight);
        Assert.Contains("field in sight", ac.PendingNotifications[0]);
    }

    [Fact]
    public void Rfis_Fails_WhenNoDestinationAssigned()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("no arrival airport", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_Fails_WhenDestinationNotInNavDatabase()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "ZZZZ");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("ZZZZ", result.Message);
        Assert.Contains("nav database", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_Fails_InClassA()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 19000, destination: "KOAK");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("Class Alpha", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_Fails_AboveCeiling()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 4000, destination: "KOAK");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC020 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("layer", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_Fails_WhenAirportBehind()
    {
        // Aircraft north of KOAK heading north → airport behind
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("behind", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_Fails_OutOfRange()
    {
        // 1 SM visibility → ~0.87 nm max range; aircraft 3+ nm north of KOAK
        var ac = MakeAircraft(37.76, -122.221, heading: 180, altitude: 3000, destination: "KOAK");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 1SM BR SCT005 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("Negative contact", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_Fails_OccludedByBank()
    {
        // Heading north with airport on the right; right bank → high wing is left, target is on the right (low wing) so NOT occluded.
        // To trigger occlusion, put the airport on the left side and bank right.
        // Aircraft south of KOAK, heading east, airport to the northwest (left side).
        // Right bank 25° → high wing left → airport occluded.
        var ac = MakeAircraft(37.71, -122.30, heading: 90, altitude: 3000, destination: "KOAK");
        ac.BankAngle = 25.0;
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("lost visual", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // RTIS — report traffic in sight
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtis_Fails_WhenNoCallsignSpecified()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "KOAK");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand(null), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("no traffic", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_Fails_WhenTargetNotFound()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "KOAK");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: _ => null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("LEAD", result.Message);
        Assert.Contains("not on this frequency", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_Succeeds_WhenTargetInFront()
    {
        var ownship = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "KOAK", callsign: "OWN1");
        var lead = MakeAircraft(37.73, -122.221, heading: 180, altitude: 3000, destination: "KOAK", callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.True(ownship.HasReportedTrafficInSight);
        Assert.Contains("traffic in sight", ownship.PendingNotifications[0]);
    }

    [Fact]
    public void Rtis_Fails_WhenTargetBehind()
    {
        // Ownship heading north, target to the south → behind
        var ownship = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK", callsign: "OWN1");
        var lead = MakeAircraft(37.70, -122.221, heading: 0, altitude: 3000, destination: "KOAK", callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.False(result.Success);
        Assert.Contains("behind", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_Fails_WhenTargetOutOfRange()
    {
        // C172 detection range is ~2.5 nm; put target ~10 nm away
        var ownship = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "KOAK", callsign: "OWN1");
        var lead = MakeAircraft(37.60, -122.221, heading: 180, altitude: 3000, destination: "KOAK", callsign: "LEAD", aircraftType: "C172");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.False(result.Success);
        Assert.Contains("Negative contact", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_Fails_OnMixedCeiling()
    {
        // Ownship below ceiling, target above → mixed
        var ownship = MakeAircraft(37.75, -122.221, heading: 180, altitude: 2000, destination: "KOAK", callsign: "OWN1");
        var lead = MakeAircraft(37.73, -122.221, heading: 180, altitude: 5000, destination: "KOAK", callsign: "LEAD");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC030 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.False(result.Success);
        Assert.Contains("cloud layer", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Flag fast path — preserves existing TickProcessor behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void Rfis_FastPath_WhenFlagAlreadySet()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK");
        // Heading north so live detection would fail (field behind), but flag is already set
        ac.HasReportedFieldInSight = true;
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("field in sight", ac.PendingNotifications[0]);
    }

    [Fact]
    public void Rtis_FastPath_WhenFlagAlreadySet()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK");
        ac.HasReportedTrafficInSight = true;
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("traffic in sight", ac.PendingNotifications[0]);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(
        double lat,
        double lon,
        double heading,
        double altitude,
        string destination,
        string callsign = "TST100",
        string aircraftType = "B738"
    )
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Latitude = lat,
            Longitude = lon,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 250,
            Destination = destination,
        };
    }
}
