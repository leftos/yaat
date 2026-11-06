using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// E2E tests for the RTIS soft-fail / "keep looking" behavior.
///
/// When the pilot cannot visually acquire the requested traffic on the first
/// check, RTIS now returns <see cref="CommandResult.Success"/> = true with a
/// pilot readback ("negative contact, looking"), records a
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
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success, $"Expected soft-fail success but got: {result.Message}");
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        Assert.Null(ownship.Approach.LastReportedTrafficCallsign);
        Assert.Contains("looking", ownship.PendingPilotReadbacks[0], StringComparison.OrdinalIgnoreCase);

        PilotObservation obs = Assert.Single(ownship.PendingObservations);
        TrafficAcquisitionObservation traffic = Assert.IsType<TrafficAcquisitionObservation>(obs);
        Assert.Equal("LEAD", traffic.TargetCallsign);
    }

    [Fact]
    public void Rtis_TargetOutOfRange_ReturnsSuccess_PilotSaysLooking_ObservationStored()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.60, AptLon, heading: 180, altitude: 3000, callsign: "LEAD", aircraftType: "C172");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        Assert.Single(ownship.PendingObservations);
    }

    // -------------------------------------------------------------------------
    // RPO diagnostic hint — CommandResult.Message names the specific failure
    // reason so the RPO can decide whether to relay it to the student. Pilot
    // phraseology in PendingPilotReadbacks stays diagnostic-free.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtis_SoftFail_Behind_CommandMessageHintsHemisphere()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for traffic", result.Message);
        Assert.Contains("behind ownship", result.Message, StringComparison.OrdinalIgnoreCase);
        // Pilot readback stays diagnostic-free.
        Assert.DoesNotContain("hemisphere", ownship.PendingPilotReadbacks[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rtis_SoftFail_OutOfRange_CommandMessageHintsDistanceAndType()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.60, AptLon, heading: 180, altitude: 3000, callsign: "LEAD", aircraftType: "C172");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for traffic", result.Message);
        Assert.Contains("nm", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C172", result.Message);
    }

    [Fact]
    public void Rtis_SoftFail_MixedCeiling_CommandMessageNamesLayer()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 2000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.73, AptLon, heading: 180, altitude: 5000, callsign: "LEAD");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC030 20/12 A2992"] };
        DispatchContext ctx = TestDispatch.Context(Random.Shared, weather: weather, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.Contains("Looking for traffic", result.Message);
        Assert.Contains("OVC030", result.Message);
    }

    // -------------------------------------------------------------------------
    // Soft-fail wording per reader: a pilot never learns the traffic's callsign
    // from an advisory (7110.65 2-1-21.a.1-5, AIM 4-1-15.c.1), so the spoken
    // answer and the solo terminal leave it out; the RPO terminal keeps it.
    // -------------------------------------------------------------------------

    private const string OwnCallsign = "N123AB";

    [Fact]
    public void Rtis_SoftFail_TargetBehind_SoloAndSpokenOmitTheTrafficCallsign_RpoKeepsIt()
    {
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AssertSoftFailWording(
            () => MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: OwnCallsign),
            lead,
            new TrafficAdvisoryDetails(6, 3, "N", "B738", null),
            weather: null,
            clause: ""
        );
    }

    [Fact]
    public void Rtis_SoftFail_MixedCeiling_SoloAndSpokenOmitTheTrafficCallsign_RpoKeepsIt()
    {
        AircraftState lead = MakeAircraft(37.73, AptLon, heading: 180, altitude: 5000, callsign: "LEAD");
        var weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 10SM OVC030 20/12 A2992"] };
        AssertSoftFailWording(
            () => MakeAircraft(37.75, AptLon, heading: 180, altitude: 2000, callsign: OwnCallsign),
            lead,
            new TrafficAdvisoryDetails(12, 1, "S", "B738", null),
            weather,
            clause: "clouds between us, "
        );
    }

    [Fact]
    public void Rtis_SoftFail_InTheTurn_SoloAndSpokenOmitTheTrafficCallsign_RpoKeepsIt()
    {
        // Heading south in a right bank: the high wing is on the left (east), where the traffic is.
        AircraftState lead = MakeAircraft(37.73, -122.19, heading: 180, altitude: 3000, callsign: "LEAD");
        AssertSoftFailWording(
            () =>
            {
                AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: OwnCallsign);
                ownship.BankAngle = 25.0;
                return ownship;
            },
            lead,
            new TrafficAdvisoryDetails(10, 2, "S", "B738", null),
            weather: null,
            clause: "in the turn, "
        );
    }

    /// <summary>
    /// Dispatches the soft-failing traffic call once in solo training (the structured advisory <paramref name="advisory"/>, the RTIS
    /// form solo training takes) and once in RPO mode (<c>RTIS LEAD</c>), each on a fresh ownship from <paramref name="makeOwnship"/>,
    /// and checks the solo terminal, the spoken text and the RPO terminal.
    /// </summary>
    private static void AssertSoftFailWording(
        Func<AircraftState> makeOwnship,
        AircraftState lead,
        TrafficAdvisoryDetails advisory,
        WeatherProfile? weather,
        string clause
    )
    {
        AircraftState solo = DispatchSoftFailingRtis(
            makeOwnship(),
            lead,
            new ReportTrafficAdvisoryCommand(advisory),
            weather,
            soloTrainingMode: true
        );
        PilotTransmission transmission = Assert.Single(solo.PendingPilotTransmissions);
        Assert.Equal($"negative contact, {clause}looking.", transmission.Text);
        Assert.Equal($"{CallsignParser.IcaoToSpoken(OwnCallsign)}, negative contact, {clause}looking.", transmission.SpeechText);

        AircraftState rpo = DispatchSoftFailingRtis(makeOwnship(), lead, new ReportTrafficInSightCommand("LEAD"), weather, soloTrainingMode: false);
        Assert.Equal($"Negative contact, LEAD, {clause}looking", Assert.Single(rpo.PendingPilotReadbacks));

        AircraftState rpoSpeech = DispatchSoftFailingRtis(
            makeOwnship(),
            lead,
            new ReportTrafficInSightCommand("LEAD"),
            weather,
            soloTrainingMode: false,
            rpoShowPilotSpeech: true
        );
        PilotSpeechText speech = Assert.Single(rpoSpeech.PendingPilotSpeech);
        Assert.DoesNotContain("LEAD", speech.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"Negative contact, LEAD, {clause}looking", speech.TerminalForRpo);
        Assert.Empty(rpoSpeech.PendingPilotReadbacks);
    }

    private static AircraftState DispatchSoftFailingRtis(
        AircraftState ownship,
        AircraftState lead,
        ParsedCommand command,
        WeatherProfile? weather,
        bool soloTrainingMode,
        bool rpoShowPilotSpeech = false
    )
    {
        DispatchContext ctx = TestDispatch.Context(
            Random.Shared,
            weather: weather,
            findAircraft: cs => cs == "LEAD" ? lead : null,
            listAircraft: () => [ownship, lead],
            soloTrainingMode: soloTrainingMode,
            rpoShowPilotSpeech: rpoShowPilotSpeech
        );

        CommandResult result = CommandDispatcher.Dispatch(command, ownship, ctx);

        Assert.True(result.Success, $"Expected soft-fail success but got: {result.Message}");
        Assert.False(ownship.Approach.HasReportedTrafficInSight, $"LEAD was acquired: {result.Message}");
        return ownship;
    }

    // -------------------------------------------------------------------------
    // Hard failures stay hard: no callsign, target not on frequency.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtis_NoCallsign_StillHardFails_NoObservation()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand(null), ownship, ctx);

        Assert.False(result.Success);
        Assert.Empty(ownship.PendingObservations);
    }

    [Fact]
    public void Rtis_TargetNotOnFrequency_StillHardFails_NoObservation()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 180, altitude: 3000, callsign: "OWN1");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: _ => null);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("GHOST"), ownship, ctx);

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
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.Single(ownship.PendingObservations);
        ownship.PendingPilotReadbacks.Clear();

        // Turn around — now target is in forward hemisphere.
        ownship.TrueHeading = new TrueHeading(180);
        ownship.TrueTrack = new TrueHeading(180);

        PilotObservationUpdater.Update(
            ownship,
            cs => cs == "LEAD" ? lead : null,
            weather: null,
            simTimeSeconds: 0,
            options: PhysicsTickOptions.Default
        );

        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Equal("LEAD", ownship.Approach.LastReportedTrafficCallsign);
        Assert.Empty(ownship.PendingObservations);
        // Acquisition readback routes through PendingPilotReadbacks (SAY channel).
        Assert.Contains("in sight", ownship.PendingPilotReadbacks[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LookingObservation_StaysPending_WhenTargetStillNotAcquirable()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        ownship.PendingPilotReadbacks.Clear();

        PilotObservationUpdater.Update(
            ownship,
            cs => cs == "LEAD" ? lead : null,
            weather: null,
            simTimeSeconds: 0,
            options: PhysicsTickOptions.Default
        );

        Assert.False(ownship.Approach.HasReportedTrafficInSight);
        Assert.Single(ownship.PendingObservations);
        // No re-emit of "looking" each tick.
        Assert.Empty(ownship.PendingPilotReadbacks);
    }

    // -------------------------------------------------------------------------
    // Cancellation: new RTIS replaces prior observation; target removal clears it.
    // -------------------------------------------------------------------------

    [Fact]
    public void NewRtis_WithDifferentCallsign_ReplacesPriorObservation()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead1 = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD1");
        AircraftState lead2 = MakeAircraft(37.70, -122.25, heading: 0, altitude: 3000, callsign: "LEAD2");
        AircraftState? Lookup(string cs) =>
            cs switch
            {
                "LEAD1" => lead1,
                "LEAD2" => lead2,
                _ => null,
            };
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD1"), ownship, ctx);
        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD2"), ownship, ctx);

        PilotObservation obs = Assert.Single(ownship.PendingObservations);
        TrafficAcquisitionObservation traffic = Assert.IsType<TrafficAcquisitionObservation>(obs);
        Assert.Equal("LEAD2", traffic.TargetCallsign);
    }

    [Fact]
    public void Observation_SilentlyClears_WhenTargetAircraftGone()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.Single(ownship.PendingObservations);
        ownship.PendingPilotReadbacks.Clear();

        // Target gone from sim — updater's lookup returns null.
        PilotObservationUpdater.Update(ownship, cs => null, weather: null, simTimeSeconds: 0, options: PhysicsTickOptions.Default);

        Assert.Empty(ownship.PendingObservations);
        Assert.Empty(ownship.PendingPilotReadbacks);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
    }

    // -------------------------------------------------------------------------
    // Forced variant is unchanged: no observation, flag set immediately.
    // -------------------------------------------------------------------------

    [Fact]
    public void Rtisf_SetsFlagImmediately_NoObservation()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand("LEAD"), ownship, ctx);

        Assert.True(result.Success);
        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Equal("LEAD", ownship.Approach.LastReportedTrafficCallsign);
        Assert.Empty(ownship.PendingObservations);
    }

    [Fact]
    public void Rtisf_Bare_WhileRtisPending_FoldsInPendingTarget()
    {
        // RTIS soft-fails (target behind) → pending observation, no stored callsign. A
        // bare RTISF must fold in that pending target and supersede the observation, so a
        // subsequent bare FOLLOW/FOLLOWF has a callsign to resolve.
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandResult rtis = CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.True(rtis.Success);
        Assert.Null(ownship.Approach.LastReportedTrafficCallsign);
        Assert.Single(ownship.PendingObservations);

        // Bare RTISF — no explicit callsign.
        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand(null), ownship, ctx);

        Assert.True(result.Success);
        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Equal("LEAD", ownship.Approach.LastReportedTrafficCallsign);
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
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
        };
    }
}
