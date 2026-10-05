using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The autonomous ready-to-taxi call-up fires once, from the stand, and only while the aircraft is
/// still waiting to taxi. A ground clearance that takes it off the stand (FOLLOWG) answers the
/// request, so the pilot does not re-announce "at parking … ready to taxi" every 120 s while taxiing
/// (issue YAAT-308 case 1).
/// </summary>
public class ReadyToTaxiCallupE2ETests
{
    private const string TwoParkedAtOak = """
        {
          "id": "ready-to-taxi-followg",
          "name": "Ready-to-taxi call-up, FOLLOWG",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            },
            {
              "id": "a2",
              "aircraftId": "N738SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "GA16" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ReadyToTaxiCallupE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void FollowG_AtParking_DoesNotRepeatCallupAfter120s()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = new SimulationEngine(new TestAirportGroundData());
        List<string> warnings = engine.LoadScenario(TwoParkedAtOak, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = false;
        scenario.SetAiStaffedPositions([TestAiPositions.OakGround(_zoa)]);
        var said = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => said.Add(entry);

        Tick(engine, 7);
        Assert.Single(ReadyToTaxiLines(said, "N152SP"));

        // FOLLOWG answers the ready-to-taxi request: the pilot is moving on a ground clearance now.
        CommandResult result = engine.SendCommand("N152SP", "FOLLOWG N738SP");
        Assert.True(result.Success, result.Message);

        // Well past two follow-up horizons (120 s each): without the request being closed, the pilot
        // would re-voice the original stand line from the taxiway.
        Tick(engine, 260);

        Assert.Single(ReadyToTaxiLines(said, "N152SP"));
        Assert.False(engine.FindAircraft("N152SP")!.PendingPilotRequest!.IsOpen);
    }

    private static List<TerminalEntry> ReadyToTaxiLines(List<TerminalEntry> said, string callsign) =>
        [
            .. said.Where(e =>
                string.Equals(e.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase)
            ),
        ];

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }
}
