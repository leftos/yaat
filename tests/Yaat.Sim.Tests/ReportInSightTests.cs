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
        Assert.True(ac.Approach.HasReportedFieldInSight);
        // Acquisition is a key event — announced via PendingWarnings (orange).
        Assert.Contains("field in sight", ac.PendingWarnings[0]);
        Assert.Empty(ac.PendingNotifications);
    }

    [Fact]
    public void Rfis_Fails_WhenNoDestinationAssigned()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Contains("no arrival airport", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ac.PendingObservations);
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
        Assert.Empty(ac.PendingObservations);
    }

    [Fact]
    public void Rfis_SoftFails_InClassA()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 19000, destination: "KOAK");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        Assert.Contains("Class Alpha", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pilot readback stays diagnostic-free — controller already knows why at FL180+.
        Assert.DoesNotContain("Class", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("on top", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
    }

    [Fact]
    public void Rfis_SoftFails_AboveCeiling()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 4000, destination: "KOAK");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC020 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        // RPO diagnostic names the binding layer (OVC020).
        Assert.Contains("OVC020", result.Message);
        // Pilot readback stays diagnostic-free — no METAR codes.
        Assert.DoesNotContain("OVC", ac.PendingNotifications[0]);
        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
    }

    [Fact]
    public void Rfis_SoftFails_AboveHighOvc_WithLowerScattered_NamesHighLayer()
    {
        // Multi-layer regression: SCT020 (not a ceiling) over OVC150. Aircraft at
        // 16,000 MSL is below FL180 but above the OVC at 15,000 AGL → fail with
        // binding = OVC150. The scattered layer is present in Layers but does not
        // mislead the check.
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 16000, destination: "KOAK");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM SCT020 OVC150 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("OVC150", result.Message);
        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
    }

    [Fact]
    public void Rfis_SoftFails_WhenAirportBehind()
    {
        // Aircraft north of KOAK heading north → airport behind
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        Assert.Contains("behind ownship", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pilot readback uses the real-world "field's behind us" idiom (no
        // sim-internal "outside forward hemisphere" diagnostic).
        Assert.Contains("field's behind us", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("looking", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hemisphere", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
    }

    [Fact]
    public void Rfis_SoftFails_OutOfRange()
    {
        // 1 SM visibility → ~0.87 nm max range; aircraft 3+ nm north of KOAK
        var ac = MakeAircraft(37.76, -122.221, heading: 180, altitude: 3000, destination: "KOAK");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 1SM BR SCT005 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        // RPO diagnostic includes distance + max range + visibility qualifier.
        Assert.Contains("nm", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pilot readback is plain.
        Assert.Contains("Negative contact", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("looking", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
    }

    [Fact]
    public void Rfis_SoftFails_OccludedByBank()
    {
        // Aircraft south of KOAK, heading east, airport to the northwest (left side).
        // Right bank 25° → high wing left → airport occluded.
        var ac = MakeAircraft(37.71, -122.30, heading: 90, altitude: 3000, destination: "KOAK");
        ac.BankAngle = 25.0;
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.False(ac.Approach.HasReportedFieldInSight);
        // RPO diagnostic mentions the bank/occlusion.
        Assert.Contains("bank", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pilot readback uses the "in the turn" idiom, no high-wing diagnostics.
        Assert.Contains("in the turn", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
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
        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        // Traffic acquisition is a key event — announced via PendingWarnings
        // (WRN/Orange) so it catches the RPO's eye, not PendingNotifications (RSP/gray).
        Assert.Contains("traffic in sight", ownship.PendingWarnings[0]);
        Assert.Empty(ownship.PendingNotifications);
    }

    // When the pilot can't acquire traffic on the first check, RTIS now soft-fails:
    // command returns success, pilot notification names the reason, and a
    // TrafficAcquisitionObservation is queued for per-tick re-check. See
    // RtisSoftFailLookingTests for the full soft-fail contract; these cases pin the
    // per-reason pilot phraseology.

    [Fact]
    public void Rtis_TargetBehind_PilotReportsNegativeContactAndLooking()
    {
        // Reviewed with aviation-sim-expert: pilot readback for "traffic behind us"
        // is the plain "negative contact, {cs}, looking" — real crews don't
        // verbally report hemisphere geometry.
        var ownship = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK", callsign: "OWN1");
        var lead = MakeAircraft(37.70, -122.221, heading: 0, altitude: 3000, destination: "KOAK", callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        var notification = ownship.PendingNotifications[0];
        Assert.Contains("Negative contact", notification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEAD", notification);
        Assert.Contains("looking", notification, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_TargetOutOfRange_PilotReportsNegativeContactAndLooking()
    {
        var ownship = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "KOAK", callsign: "OWN1");
        var lead = MakeAircraft(37.60, -122.221, heading: 180, altitude: 3000, destination: "KOAK", callsign: "LEAD", aircraftType: "C172");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        var notification = ownship.PendingNotifications[0];
        Assert.Contains("Negative contact", notification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("looking", notification, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_OnMixedCeiling_PilotReportsCloudsBetweenAndLooking()
    {
        // Reviewed with aviation-sim-expert: pilot paraphrases the METAR code as
        // "clouds between us" rather than reading the raw OVC030 tag.
        var ownship = MakeAircraft(37.75, -122.221, heading: 180, altitude: 2000, destination: "KOAK", callsign: "OWN1");
        var lead = MakeAircraft(37.73, -122.221, heading: 180, altitude: 5000, destination: "KOAK", callsign: "LEAD");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC030 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        var notification = ownship.PendingNotifications[0];
        Assert.Contains("clouds between us", notification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("looking", notification, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Flag fast path — preserves existing TickProcessor behavior
    // -------------------------------------------------------------------------

    [Fact]
    public void Rfis_FastPath_WhenFlagAlreadySet()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK");
        // Heading north so live detection would fail (field behind), but flag is already set
        ac.Approach.HasReportedFieldInSight = true;
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("field in sight", ac.PendingWarnings[0]);
        Assert.Empty(ac.PendingNotifications);
    }

    [Fact]
    public void Rtis_FastPath_WhenFlagAlreadySet()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000, destination: "KOAK");
        ac.Approach.HasReportedTrafficInSight = true;
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("traffic in sight", ac.PendingWarnings[0]);
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
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = destination },
        };
    }
}
