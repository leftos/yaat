using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// E2E tests for the RFIS soft-fail / "keep looking" behavior.
///
/// When the pilot cannot visually acquire the assigned destination on the
/// first check, RFIS returns <see cref="CommandResult.Success"/> = true with
/// a diagnostic-free pilot readback ("Negative contact KOAK, looking"),
/// records a <c>FieldAcquisitionObservation</c> on the aircraft, and re-runs
/// the acquisition check each tick via <c>PilotObservationUpdater</c>. The
/// RPO-facing diagnostic (cloud layer, distance, hemisphere, bank) lives in
/// <c>CommandResult.Message</c> only — the pilot readback stays clean. When
/// the field becomes acquirable, <c>HasReportedFieldInSight</c> is set and
/// the pilot reports "field in sight" via PendingWarnings (orange) so the
/// RPO sees the resolution clearly.
/// </summary>
[Collection("NavDbMutator")]
public class RfisSoftFailLookingTests
{
    public RfisSoftFailLookingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // -------------------------------------------------------------------------
    // First-check soft fail: pilot says "looking", observation stored, command
    // returns success so the client clears the input buffer.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rfis_FieldBehind_ReturnsSuccess_PilotSaysLooking_ObservationStored()
    {
        // Aircraft north of KOAK heading north → field behind ownship.
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success, $"Expected soft-fail success but got: {result.Message}");
        Assert.False(ac.Approach.HasReportedFieldInSight);
        Assert.Contains("looking", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);

        var obs = Assert.Single(ac.PendingObservations);
        Assert.IsType<FieldAcquisitionObservation>(obs);
    }

    // -------------------------------------------------------------------------
    // RPO diagnostic hint — CommandResult.Message names the specific failure
    // reason so the RPO can decide whether to relay it to the student. Pilot
    // phraseology in PendingNotifications stays diagnostic-free.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rfis_SoftFail_Behind_CommandMessageHintsHemisphere()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for the field", result.Message);
        Assert.Contains("behind ownship", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hemisphere", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        // Real-world pilot idiom — actionable for the controller (cue to offer a vector).
        Assert.Contains("field's behind us", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_SoftFail_OutOfRange_CommandMessageHintsDistance()
    {
        // 1 SM visibility → ~0.87 nm max range; aircraft 3+ nm north.
        var ac = MakeAircraft(37.76, -122.221, heading: 180, altitude: 3000);
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 1SM BR SCT005 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for the field", result.Message);
        Assert.Contains("nm", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfis_SoftFail_AboveCeiling_CommandMessageNamesLayer()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 4000);
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC020 20/12 A2992"] };
        var ctx = TestDispatch.Context(Random.Shared, weather: weather);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for the field", result.Message);
        Assert.Contains("OVC020", result.Message);
        // Pilot says "on top, looking" — no METAR codes.
        Assert.Contains("on top", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OVC", ac.PendingNotifications[0]);
    }

    [Fact]
    public void Rfis_SoftFail_InClassA_CommandMessageNamesClassA()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 19000);
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        // RPO sees the Class A diagnostic.
        Assert.Contains("Class Alpha", result.Message);
        // Pilot readback collapses to the plain default — at FL180+ the controller
        // already knows why; no fictitious "on top" claim about a deck that may
        // not exist (aviation-sim-expert review).
        Assert.Equal("Negative contact, KOAK, looking", ac.PendingNotifications[0]);
    }

    [Fact]
    public void Rfis_SoftFail_OccludedByBank_CommandMessageHintsBank()
    {
        // Aircraft south of KOAK, heading east, airport northwest (left side).
        // Right bank 25° → high wing left → airport occluded.
        var ac = MakeAircraft(37.71, -122.30, heading: 90, altitude: 3000);
        ac.BankAngle = 25.0;
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.Contains("bank", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in the turn", ac.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Hard failures stay hard: no destination, destination not in nav db.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rfis_NoDestination_StillHardFails_NoObservation()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Empty(ac.PendingObservations);
    }

    [Fact]
    public void Rfis_DestinationNotInNavDb_StillHardFails_NoObservation()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 180, altitude: 3000, destination: "ZZZZ");
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        Assert.False(result.Success);
        Assert.Empty(ac.PendingObservations);
    }

    // -------------------------------------------------------------------------
    // Tick-based acquisition: once the field is acquirable, the observation
    // resolves, the flag flips, and the pilot says "field in sight".
    // -------------------------------------------------------------------------

    [Fact]
    public void LookingObservation_ResolvesToInSight_WhenFieldBecomesAcquirable()
    {
        // Heading north so field is behind. Soft-fail → observation stored.
        // Flip heading; one tick of PilotObservationUpdater should resolve it.
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);
        Assert.Single(ac.PendingObservations);
        ac.PendingNotifications.Clear();

        ac.TrueHeading = new TrueHeading(180);
        ac.TrueTrack = new TrueHeading(180);

        PilotObservationUpdater.Update(ac, aircraftLookup: null, weather: null);

        Assert.True(ac.Approach.HasReportedFieldInSight);
        Assert.Empty(ac.PendingObservations);
        // Resolution routes through PendingWarnings (orange) for visibility.
        Assert.Contains("field in sight", ac.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LookingObservation_StaysPending_WhenFieldStillNotAcquirable()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);
        ac.PendingNotifications.Clear();

        PilotObservationUpdater.Update(ac, aircraftLookup: null, weather: null);

        Assert.False(ac.Approach.HasReportedFieldInSight);
        Assert.Single(ac.PendingObservations);
        // No re-emit of "looking" each tick.
        Assert.Empty(ac.PendingNotifications);
        Assert.Empty(ac.PendingWarnings);
    }

    // -------------------------------------------------------------------------
    // Cancellation: a second RFIS replaces the prior observation; cleared
    // destination drops it silently on the next tick.
    // -------------------------------------------------------------------------

    [Fact]
    public void SecondRfis_ReplacesPriorObservation_StillSingleEntry()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);
        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);

        var obs = Assert.Single(ac.PendingObservations);
        Assert.IsType<FieldAcquisitionObservation>(obs);
    }

    [Fact]
    public void Observation_SilentlyClears_WhenDestinationCleared()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);
        Assert.Single(ac.PendingObservations);
        ac.PendingNotifications.Clear();

        // Destination cleared between ticks (e.g. flight plan amended).
        ac.FlightPlan.Destination = "";

        PilotObservationUpdater.Update(ac, aircraftLookup: null, weather: null);

        Assert.Empty(ac.PendingObservations);
        Assert.Empty(ac.PendingWarnings);
        Assert.False(ac.Approach.HasReportedFieldInSight);
    }

    [Fact]
    public void Observation_SilentlyClears_WhenDestinationLeavesNavDb()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);
        Assert.Single(ac.PendingObservations);

        ac.FlightPlan.Destination = "ZZZZ";
        PilotObservationUpdater.Update(ac, aircraftLookup: null, weather: null);

        Assert.Empty(ac.PendingObservations);
        Assert.False(ac.Approach.HasReportedFieldInSight);
    }

    // -------------------------------------------------------------------------
    // Forced variant is unchanged behavior-wise: no observation, flag set
    // immediately, success readback routed through PendingWarnings (orange).
    // -------------------------------------------------------------------------

    [Fact]
    public void Rfisf_SetsFlagImmediately_NoObservation()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        var result = CommandDispatcher.Dispatch(new ReportFieldInSightForcedCommand(), ac, ctx);

        Assert.True(result.Success);
        Assert.True(ac.Approach.HasReportedFieldInSight);
        Assert.Empty(ac.PendingObservations);
        Assert.Contains("field in sight", ac.PendingWarnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rfisf_ClearsPriorLookingObservation()
    {
        var ac = MakeAircraft(37.75, -122.221, heading: 0, altitude: 3000);
        var ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);
        Assert.Single(ac.PendingObservations);

        CommandDispatcher.Dispatch(new ReportFieldInSightForcedCommand(), ac, ctx);

        Assert.Empty(ac.PendingObservations);
        Assert.True(ac.Approach.HasReportedFieldInSight);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(double lat, double lon, double heading, double altitude, string destination = "KOAK")
    {
        return new AircraftState
        {
            Callsign = "TST100",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = destination },
        };
    }
}
