using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// E2E tests for the RTIS soft-fail / "keep looking" behavior.
///
/// When the pilot cannot visually acquire the requested traffic on the first
/// check, RTIS now returns <see cref="CommandResult.Success"/> = true with a
/// pilot readback ("unable, looking for traffic"), records a
/// <c>TrafficAcquisitionObservation</c> on the aircraft, and re-runs the
/// acquisition check each tick via <c>PilotObservationUpdater</c>. When the
/// target becomes acquirable, <c>HasReportedTrafficInSight</c> is set and the
/// pilot reports "in sight". New RTIS commands, target removal, and approach
/// resets all clear the pending observation.
/// </summary>
[Collection("NavDbMutator")]
public class RtisSoftFailLookingTests
{
    private const double AptLat = 37.721;
    private const double AptLon = -122.221;

    public RtisSoftFailLookingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // First-check soft fail: pilot says "looking", observation stored, command
    // returns success so the client clears the input buffer.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtis_TargetBehind_ReturnsSuccess_PilotSaysLooking_ObservationStored()
    {
        // Ownship heading north, target south → out of forward hemisphere.
        var ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        var lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success, $"Expected soft-fail success but got: {result.Message}");
        Assert.False(ownship.HasReportedTrafficInSight);
        Assert.Null(ownship.LastReportedTrafficCallsign);
        Assert.Contains("looking", ownship.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);

        var obs = Assert.Single(ownship.PendingObservations);
        var traffic = Assert.IsType<TrafficAcquisitionObservation>(obs);
        Assert.Equal("LEAD", traffic.TargetCallsign);
    }

    [Fact]
    public void Rtis_TargetOutOfRange_ReturnsSuccess_PilotSaysLooking_ObservationStored()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        var lead = MakeAircraft(37.60, AptLon, heading: 180, altitude: 3000, callsign: "LEAD", aircraftType: "C172");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.False(ownship.HasReportedTrafficInSight);
        Assert.Single(ownship.PendingObservations);
    }

    // -------------------------------------------------------------------------
    // RPO diagnostic hint — CommandResult.Message names the specific failure
    // reason so the RPO can decide whether to relay it to the student. Pilot
    // phraseology in PendingNotifications stays diagnostic-free.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtis_SoftFail_Behind_CommandMessageHintsHemisphere()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        var lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for traffic", result.Message);
        Assert.Contains("behind ownship", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pilot readback stays diagnostic-free.
        Assert.DoesNotContain("hemisphere", ownship.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_SoftFail_OutOfRange_CommandMessageHintsDistanceAndType()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        var lead = MakeAircraft(37.60, AptLon, heading: 180, altitude: 3000, callsign: "LEAD", aircraftType: "C172");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for traffic", result.Message);
        Assert.Contains("nm", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C172", result.Message);
    }

    [Fact]
    public void Rtis_SoftFail_MixedCeiling_CommandMessageNamesLayer()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 2000, callsign: "OWN1");
        var lead = MakeAircraft(37.73, AptLon, heading: 180, altitude: 5000, callsign: "LEAD");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC030 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather, findAircraft: cs => cs == "LEAD" ? lead : null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for traffic", result.Message);
        Assert.Contains("OVC030", result.Message);
    }

    // -------------------------------------------------------------------------
    // Hard failures stay hard: no callsign, target not on frequency.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtis_NoCallsign_StillHardFails_NoObservation()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand(null), ownship, ctx);

        Assert.False(result.Success);
        Assert.Empty(ownship.PendingObservations);
    }

    [Fact]
    public void Rtis_TargetNotOnFrequency_StillHardFails_NoObservation()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: _ => null);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("GHOST"), ownship, ctx);

        Assert.False(result.Success);
        Assert.Contains("not on this frequency", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ownship.PendingObservations);
    }

    // -------------------------------------------------------------------------
    // Tick-based acquisition: once the target is acquirable, the observation
    // resolves, the flag flips, and the pilot says "in sight".
    // -------------------------------------------------------------------------

    [Fact]
    public void LookingObservation_ResolvesToInSight_WhenTargetBecomesAcquirable()
    {
        // Start with target behind (out of forward hemisphere). Soft-fail and add
        // observation. Then flip ownship heading so target is in front; one tick of
        // PilotObservationUpdater should resolve it.
        var ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        var lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.Single(ownship.PendingObservations);
        ownship.PendingNotifications.Clear();

        // Turn around — now target is in forward hemisphere.
        ownship.TrueHeading = new TrueHeading(180);
        ownship.TrueTrack = new TrueHeading(180);

        PilotObservationUpdater.Update(ownship, cs => cs == "LEAD" ? lead : null, weather: null);

        Assert.True(ownship.HasReportedTrafficInSight);
        Assert.Equal("LEAD", ownship.LastReportedTrafficCallsign);
        Assert.Empty(ownship.PendingObservations);
        // Acquisition readback routes through PendingWarnings (WRN/Orange) for visibility.
        Assert.Contains("in sight", ownship.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LookingObservation_StaysPending_WhenTargetStillNotAcquirable()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        var lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        ownship.PendingNotifications.Clear();

        PilotObservationUpdater.Update(ownship, cs => cs == "LEAD" ? lead : null, weather: null);

        Assert.False(ownship.HasReportedTrafficInSight);
        Assert.Single(ownship.PendingObservations);
        // No re-emit of "looking" each tick.
        Assert.Empty(ownship.PendingNotifications);
    }

    // -------------------------------------------------------------------------
    // Cancellation: new RTIS replaces prior observation; target removal clears it.
    // -------------------------------------------------------------------------

    [Fact]
    public void NewRtis_WithDifferentCallsign_ReplacesPriorObservation()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        var lead1 = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD1");
        var lead2 = MakeAircraft(37.70, -122.25, heading: 0, altitude: 3000, callsign: "LEAD2");
        AircraftState? Lookup(string cs) =>
            cs switch
            {
                "LEAD1" => lead1,
                "LEAD2" => lead2,
                _ => null,
            };
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD1"), ownship, ctx);
        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD2"), ownship, ctx);

        var obs = Assert.Single(ownship.PendingObservations);
        var traffic = Assert.IsType<TrafficAcquisitionObservation>(obs);
        Assert.Equal("LEAD2", traffic.TargetCallsign);
    }

    [Fact]
    public void Observation_SilentlyClears_WhenTargetAircraftGone()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        var lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        var ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.Single(ownship.PendingObservations);
        ownship.PendingNotifications.Clear();

        // Target gone from sim — updater's lookup returns null.
        PilotObservationUpdater.Update(ownship, cs => null, weather: null);

        Assert.Empty(ownship.PendingObservations);
        Assert.Empty(ownship.PendingNotifications);
        Assert.False(ownship.HasReportedTrafficInSight);
    }

    // -------------------------------------------------------------------------
    // Forced variant is unchanged: no observation, flag set immediately.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtisf_SetsFlagImmediately_NoObservation()
    {
        var ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.True(ownship.HasReportedTrafficInSight);
        Assert.Equal("LEAD", ownship.LastReportedTrafficCallsign);
        Assert.Empty(ownship.PendingObservations);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(double lat, double lon, double heading, double altitude, string callsign, string aircraftType = "B738")
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
            Destination = "KOAK",
        };
    }
}
