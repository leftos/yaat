using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The traffic lookout a failed RTIS check arms is not open-ended: it ends after two minutes of sim time or at the
/// ownship's touchdown, whichever comes first, and the pilot keys up once with "still negative contact on that
/// traffic". A traffic advisory is about traffic passing close to the flight path now (AIM 4-1-15.a.1), so a sighting
/// minutes later would report traffic the controller never asked about — the case the montage C2 pursuit clip shows,
/// where the lead is told of traffic on its six and keeps it in "looking" until swinging into view on the runway.
/// </summary>
[Collection("NavDbMutator")]
public sealed class TrafficLookoutLifetimeTests
{
    private const double AptLon = -122.221;

    private static readonly PhysicsTickOptions SoloOptions = new(true, false, MagneticDeclination.EvaluationDateUtc);

    public TrafficLookoutLifetimeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Lookout_StaysPendingJustBeforeTheLifetime_ThenEndsWithOneNegativeContact()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AircraftState? Lookup(string cs) => cs == "LEAD" ? lead : null;
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.Single(ownship.PendingObservations);
        ownship.PendingPilotReadbacks.Clear();

        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 119.9, options: PhysicsTickOptions.Default);

        Assert.Single(ownship.PendingObservations);
        Assert.Empty(ownship.PendingPilotReadbacks);

        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 120, options: PhysicsTickOptions.Default);

        Assert.Empty(ownship.PendingObservations);
        string report = Assert.Single(ownship.PendingPilotReadbacks);
        Assert.Contains("still negative contact", report, StringComparison.OrdinalIgnoreCase);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);

        // The lookout is gone: no second report on a later tick.
        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 121, options: PhysicsTickOptions.Default);
        Assert.Single(ownship.PendingPilotReadbacks);
    }

    [Fact]
    public void Lookout_EndingInSoloTraining_QueuesOneReportTransmissionWithoutTheCallsign()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AircraftState? Lookup(string cs) => cs == "LEAD" ? lead : null;
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup, listAircraft: () => [ownship, lead], soloTrainingMode: true);

        // A traffic advisory arms the lookout in solo training (RTIS itself is the RPO form there).
        CommandResult armed = CommandDispatcher.Dispatch(
            new ReportTrafficAdvisoryCommand(new TrafficAdvisoryDetails(Clock: 6, Miles: 3, Direction: "N", AircraftType: "B738", Altitude: null)),
            ownship,
            ctx
        );
        Assert.True(armed.Success, armed.Message);
        Assert.IsType<TrafficAcquisitionObservation>(Assert.Single(ownship.PendingObservations));
        ownship.PendingPilotTransmissions.Clear();

        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 120, options: SoloOptions);

        Assert.Empty(ownship.PendingObservations);
        PilotTransmission report = Assert.Single(ownship.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Report, report.Kind);
        Assert.False(report.IsInitialContact);
        Assert.Equal("still negative contact on that traffic.", report.Text);
        Assert.DoesNotContain("LEAD", report.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookout_EndsAtTouchdown_LongBeforeTheLifetime()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AircraftState? Lookup(string cs) => cs == "LEAD" ? lead : null;
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        TrafficAcquisitionObservation obs = Assert.IsType<TrafficAcquisitionObservation>(Assert.Single(ownship.PendingObservations));
        Assert.True(obs.ArmedAirborne);
        ownship.PendingPilotReadbacks.Clear();

        ownship.IsOnGround = true;
        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 30, options: PhysicsTickOptions.Default);

        Assert.Empty(ownship.PendingObservations);
        string report = Assert.Single(ownship.PendingPilotReadbacks);
        Assert.Contains("still negative contact", report, StringComparison.OrdinalIgnoreCase);
        Assert.False(ownship.Approach.HasReportedTrafficInSight);
    }

    [Fact]
    public void Lookout_ArmedOnTheGround_KeepsLookingUntilTheLifetime()
    {
        // Holding short: the pilot never landed during this lookout, so the touchdown ending does not apply.
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        ownship.IsOnGround = true;
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AircraftState? Lookup(string cs) => cs == "LEAD" ? lead : null;
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        TrafficAcquisitionObservation obs = Assert.IsType<TrafficAcquisitionObservation>(Assert.Single(ownship.PendingObservations));
        Assert.False(obs.ArmedAirborne);
        ownship.PendingPilotReadbacks.Clear();

        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 1, options: PhysicsTickOptions.Default);

        Assert.Single(ownship.PendingObservations);
        Assert.Empty(ownship.PendingPilotReadbacks);

        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 120, options: PhysicsTickOptions.Default);

        Assert.Empty(ownship.PendingObservations);
        Assert.Contains("still negative contact", Assert.Single(ownship.PendingPilotReadbacks), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lookout_TrafficFirstSeenOnTheExpiryTick_ReportsInSight()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AircraftState? Lookup(string cs) => cs == "LEAD" ? lead : null;
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        Assert.Single(ownship.PendingObservations);
        ownship.PendingPilotReadbacks.Clear();

        // The turn puts the traffic ahead on the very tick the two-minute window closes.
        ownship.TrueHeading = new TrueHeading(180);
        ownship.TrueTrack = new TrueHeading(180);
        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 120, options: PhysicsTickOptions.Default);

        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Empty(ownship.PendingObservations);
        Assert.Contains(ownship.PendingPilotReadbacks, line => line.Contains("in sight", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ownship.PendingPilotReadbacks, line => line.Contains("negative contact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Lookout_TargetGoneFromTheWorld_ClearsSilently()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: cs => cs == "LEAD" ? lead : null);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        ownship.PendingPilotReadbacks.Clear();

        PilotObservationUpdater.Update(ownship, cs => null, weather: null, simTimeSeconds: 121, options: PhysicsTickOptions.Default);

        Assert.Empty(ownship.PendingObservations);
        Assert.Empty(ownship.PendingPilotReadbacks);
    }

    [Fact]
    public void NewRtis_AfterTheLookoutEnded_ArmsAFreshLifetime()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AircraftState? Lookup(string cs) => cs == "LEAD" ? lead : null;

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, TestDispatch.Context(Random.Shared, findAircraft: Lookup));
        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 120, options: PhysicsTickOptions.Default);
        Assert.Empty(ownship.PendingObservations);
        ownship.PendingPilotReadbacks.Clear();

        DispatchContext later = TestDispatch.Context(Random.Shared, findAircraft: Lookup, scenarioElapsedSeconds: 130);
        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, later);

        TrafficAcquisitionObservation rearmed = Assert.IsType<TrafficAcquisitionObservation>(Assert.Single(ownship.PendingObservations));
        Assert.Equal(130, rearmed.IssuedAtSeconds);
        ownship.PendingPilotReadbacks.Clear();

        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 249.9, options: PhysicsTickOptions.Default);
        Assert.Single(ownship.PendingObservations);
        Assert.Empty(ownship.PendingPilotReadbacks);

        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 250, options: PhysicsTickOptions.Default);
        Assert.Empty(ownship.PendingObservations);
        Assert.Contains("still negative contact", Assert.Single(ownship.PendingPilotReadbacks), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FieldLookout_IsUnaffected_AtThreeHundredSeconds()
    {
        AircraftState ac = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        DispatchContext ctx = TestDispatch.Context(Random.Shared);

        CommandDispatcher.Dispatch(new ReportFieldInSightCommand(), ac, ctx);
        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
        ac.PendingPilotReadbacks.Clear();

        PilotObservationUpdater.Update(ac, aircraftLookup: null, weather: null, simTimeSeconds: 300, options: PhysicsTickOptions.Default);

        Assert.IsType<FieldAcquisitionObservation>(Assert.Single(ac.PendingObservations));
        Assert.Empty(ac.PendingPilotReadbacks);
    }

    [Fact]
    public void BareRtisf_AfterTheLookoutEnded_DoesNotBorrowTheTarget()
    {
        AircraftState ownship = MakeAircraft(37.75, AptLon, heading: 0, altitude: 3000, callsign: "OWN1");
        AircraftState lead = MakeAircraft(37.70, AptLon, heading: 0, altitude: 3000, callsign: "LEAD");
        AircraftState? Lookup(string cs) => cs == "LEAD" ? lead : null;
        DispatchContext ctx = TestDispatch.Context(Random.Shared, findAircraft: Lookup);

        CommandDispatcher.Dispatch(new ReportTrafficInSightCommand("LEAD"), ownship, ctx);
        PilotObservationUpdater.Update(ownship, Lookup, weather: null, simTimeSeconds: 120, options: PhysicsTickOptions.Default);
        Assert.Empty(ownship.PendingObservations);

        CommandResult result = CommandDispatcher.Dispatch(new ReportTrafficInSightForcedCommand(null), ownship, ctx);

        Assert.True(result.Success);
        Assert.True(ownship.Approach.HasReportedTrafficInSight);
        Assert.Null(ownship.Approach.LastReportedTrafficCallsign);
    }

    private static AircraftState MakeAircraft(double lat, double lon, double heading, double altitude, string callsign)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
        };
    }
}
