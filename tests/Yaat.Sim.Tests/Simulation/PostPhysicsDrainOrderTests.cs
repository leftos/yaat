using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Pins the order <see cref="SimulationEngine.TickPostPhysics"/> drains the world's per-aircraft buffers in.
///
/// <para>
/// Step 3c-0 of the tick-path work moved two of these to match the live server (ADR 0002: ordering
/// disagreements defer to live) — pilot speech ahead of readbacks, and the strip dispatches last. Both moves
/// were invisible to every gate the repo had. The tick oracle compares <c>StateSnapshotDto</c>, and neither
/// terminal entries nor room-owned strip state is in it; the full cross-repo suite passed byte-identically
/// before and after each move. So nothing would have caught either being swapped back, which is what this
/// test exists for: the spine step 3c introduces is asserted to reorder nothing, and that assertion needs
/// something that can see this order at all.
/// </para>
///
/// <para>
/// What it does <em>not</em> pin: where <c>TickPilotProactive</c> sits relative to the detectors. That move
/// has no observable today — a proactive rule would have to read what visual detection or conflict alerting
/// writes, and none does yet — so asserting it needs 3c's step trace, not a behavioural test. It is pinned
/// only to the extent that a proactive call still lands in the same tick's drains.
/// </para>
///
/// <para>
/// Approach scores are drained here too, between the transmissions and the strip dispatches, but this path
/// discards the result rather than emitting anything, so their position is unobservable. The test asserts
/// only that the buffer is emptied.
/// </para>
/// </summary>
public class PostPhysicsDrainOrderTests
{
    private const string Callsign = "SWA123";

    /// <summary>
    /// Each seeded buffer carries a marker distinct enough to identify its source in the emission stream.
    /// Kind alone is not enough: pilot transmissions and readbacks both emit under <c>SayReadback</c>.
    /// </summary>
    private const string WarningMarker = "marker-warning";
    private const string NotificationMarker = "marker-notification";
    private const string SpeechMarker = "marker-speech";
    private const string ReadbackMarker = "marker-readback";
    private const string StripMarker = "marker-strip";

    [Fact]
    public void TickPostPhysics_DrainsBuffersInTheLiveServersOrder()
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = new LatLon(37.72, -122.22),
            Altitude = 3000,
            IndicatedAirspeed = 210,
            Transponder = new AircraftTransponder
            {
                Code = 1200,
                AssignedCode = 1200,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" },
            Track = new AircraftTrack(),
        };

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.AddAircraft(aircraft);

        aircraft.PendingWarnings.Add(WarningMarker);
        aircraft.PendingNotifications.Add(NotificationMarker);
        aircraft.PendingPilotSpeech.Add(SpeechMarker);
        aircraft.PendingPilotReadbacks.Add(ReadbackMarker);
        aircraft.PendingStripDispatches.Add(new StripAnnotateCommand("1", StripMarker));

        // One ordered stream across both surfaces the drains emit on. PilotSpeechEmitted is deliberately not
        // subscribed: pilot speech already arrives as a terminal entry, and taking both would record it twice.
        var emissions = new List<string>();
        engine.WarningEmitted += (_, warning) => emissions.Add(warning);
        engine.TerminalEntryEmitted += entry => emissions.Add(entry.Message);
        engine.StripDispatchRequested += (_, command) => emissions.Add(((StripAnnotateCommand)command).Text!);

        engine.TickPostPhysics();

        // Filtered to the seeded markers: the detectors and the proactive pass emit their own lines, and this
        // test is about the relative order of the drains, not about what else a tick says.
        var seeded = new[] { WarningMarker, NotificationMarker, SpeechMarker, ReadbackMarker, StripMarker };
        Assert.Equal([WarningMarker, NotificationMarker, SpeechMarker, ReadbackMarker, StripMarker], emissions.Where(seeded.Contains).ToList());
    }

    [Fact]
    public void TickPostPhysics_DrainsApproachScoresEvenThoughItDiscardsThem()
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = new LatLon(37.72, -122.22),
            Altitude = 3000,
            IndicatedAirspeed = 210,
            Transponder = new AircraftTransponder
            {
                Code = 1200,
                AssignedCode = 1200,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" },
            Track = new AircraftTrack(),
        };

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.AddAircraft(aircraft);
        aircraft.PendingApproachScores.Add(
            new ApproachScore
            {
                Callsign = Callsign,
                AircraftType = "B738",
                ApproachId = "I28R",
                RunwayId = "28R",
                AirportCode = "KSFO",
            }
        );

        engine.TickPostPhysics();

        Assert.Empty(aircraft.PendingApproachScores);
    }
}
