using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A pilot told of traffic it cannot see keeps looking for it (<see cref="TrafficAcquisitionObservation"/>), but not for
/// the rest of the flight: a traffic advisory is about traffic that may pass close to the pilot's flight path now (AIM
/// 4-1-15.a.1), and "traffic in sight" reports previously issued traffic (P/CG). The KOAK montage C2 clip flown in solo:
/// N52417 is told at t=1 of the Cessna at its 6 o'clock, which stays behind it all the way down final, and the lookout
/// must not end in an in-sight report from the runway minutes later when the follower on final swings into view.
/// </summary>
[Collection("NavDbMutator")]
public sealed class StaleTrafficLookoutE2ETests
{
    private const string Lead = "N52417";
    private const string Follower = "N738SP";

    private static readonly (int Second, string Callsign, string Command)[] Script =
    [
        (1, Lead, "CAINH"),
        (1, Follower, "CAINH"),
        (1, Follower, "RTIS 12 2 W C172"),
        (1, Lead, "RTIS 6 2 W C172"),
        (2, Lead, "DCT VPCBT; ERB 28R"),
        (3, Lead, "SPD 70"),
        (4, Follower, "DCT VPCBT"),
        (100, Follower, "FOLLOW N52417"),
        (390, Lead, "CLAND 28R"),
        (420, Follower, "CLAND 28R"),
    ];

    private readonly ITestOutputHelper _output;

    public StaleTrafficLookoutE2ETests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void C2Pursuit_LeadToldOfTrafficBehindIt_DoesNotReportItInSightAfterLanding()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "MontageC2Pursuit.json"));
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(json, rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(engine.Scenario);
        scenario.SoloTrainingMode = true;

        // Both pilots already worked by the student, so the Class C entry gate lets the lead fly its base and land
        // rather than hold outside the airspace.
        foreach (string callsign in new[] { Lead, Follower })
        {
            AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(callsign));
            aircraft.HasMadeInitialContact = true;
            aircraft.HasControllerAcknowledgedInitialContact = true;
        }

        var inSightSeconds = new List<double>();
        var negativeContactLines = new List<(double Second, string Message)>();
        engine.TerminalEntryEmitted += entry =>
        {
            if (
                (entry.Kind == "SayReadback")
                && (entry.Callsign == Lead)
                && entry.Message.StartsWith("traffic in sight", StringComparison.OrdinalIgnoreCase)
            )
            {
                inSightSeconds.Add(scenario.ElapsedSeconds);
            }

            if (
                (entry.Kind == "SayPilot")
                && (entry.Callsign == Lead)
                && entry.Message.Contains("negative contact", StringComparison.OrdinalIgnoreCase)
            )
            {
                negativeContactLines.Add((scenario.ElapsedSeconds, entry.Message));
            }
        };

        bool lookoutArmed = false;
        double? touchdownSecond = null;
        for (int second = 1; (second <= 900) && ((touchdownSecond is null) || (scenario.ElapsedSeconds < touchdownSecond + 120)); second++)
        {
            foreach ((int at, string callsign, string command) in Script.Where(s => s.Second == second))
            {
                Assert.True(engine.SendCommand(callsign, command).Success, $"t={second}: {callsign} {command} was refused");
            }

            engine.TickOneSecond();
            AircraftState lead = Assert.IsType<AircraftState>(engine.FindAircraft(Lead));
            lookoutArmed |= lead.PendingObservations.OfType<TrafficAcquisitionObservation>().Any();
            touchdownSecond ??= lead.IsOnGround ? scenario.ElapsedSeconds : null;
        }

        _output.WriteLine(
            $"lead touched down at t={touchdownSecond}; traffic in sight at t=[{string.Join(", ", inSightSeconds)}]; "
                + $"negative contact at t=[{string.Join(", ", negativeContactLines.Select(line => line.Second))}]"
        );
        Assert.True(lookoutArmed, "the traffic call at t=1 never left the lead looking for the traffic behind it");
        double touchdown = Assert.IsType<double>(touchdownSecond);
        Assert.DoesNotContain(inSightSeconds, at => at >= touchdown);

        // The lookout ends once, on the frequency, in the pilot's own words: the traffic's callsign is never said.
        (double endedAt, string message) = Assert.Single(negativeContactLines);
        Assert.True(endedAt < touchdown, $"the lookout ended at t={endedAt}, after touchdown at t={touchdown}");
        Assert.DoesNotContain(Follower, message, StringComparison.OrdinalIgnoreCase);
    }
}
