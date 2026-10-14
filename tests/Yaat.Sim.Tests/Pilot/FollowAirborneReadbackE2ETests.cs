using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A solo student's accepted airborne FOLLOW reaches the frequency: the pilot reads it back as "follow the
/// traffic", the same element the ground FOLLOWG reads back (7110.65 §7-6-7.a "FOLLOW (description)"; also
/// §3-8-1, tower sequencing). The lead's callsign never appears in the readback — a pilot identifies traffic by
/// position, never by callsign.
/// </summary>
public sealed class FollowAirborneReadbackE2ETests
{
    private const string Follower = "N12345";
    private const string Lead = "N2468K";

    public FollowAirborneReadbackE2ETests() => TestVnasData.EnsureInitialized();

    /// <summary>A VFR aircraft south-bound at 3,000 ft; a second one at a lower latitude is ahead of it.</summary>
    private static AircraftState Airborne(string callsign, double latitude)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Position = new LatLon(latitude, -122.221),
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 3000,
            IndicatedAirspeed = 90,
            Phases = new PhaseList(),
        };
        aircraft.Targets.TargetTrueHeading = new TrueHeading(180);
        return aircraft;
    }

    [Fact]
    public void SoloAirborneFollow_QueuesFollowTheTrafficReadback()
    {
        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = "OAK",
            StudentPositionType = "APP",
        };
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = scenario };
        AircraftState follower = Airborne(Follower, 37.75);
        AircraftState lead = Airborne(Lead, 37.73);
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.LastReportedTrafficCallsign = Lead;
        engine.World.AddAircraft(follower);
        engine.World.AddAircraft(lead);

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Lead}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Lead, follower.Approach.FollowingCallsign);
        PilotTransmission readback = Assert.Single(follower.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("follow the traffic", readback.Text);
        Assert.DoesNotContain(Lead, readback.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Lead, readback.SpeechText, StringComparison.Ordinal);
    }
}
