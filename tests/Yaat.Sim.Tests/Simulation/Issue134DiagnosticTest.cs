using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

public class Issue134DiagnosticTest(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue134-v3-commands-only.json";

    private static readonly string[] LandingCallsigns = ["N569SX", "N775JW", "N70CS", "N805FM", "FDX3807"];

    [Fact]
    public void Diagnostic_LandingAircraftTickByTick()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);
        NavigationDatabase.SetInstance(navDb);
        var engine = new SimulationEngine(groundData);

        // Replay to first CLAND minus some buffer
        engine.Replay(recording, 600);

        // Track phase transitions for each aircraft
        var prevPhases = new Dictionary<string, string>();

        for (int t = 1; t <= 900; t++)
        {
            engine.ReplayOneSecond();

            foreach (string cs in LandingCallsigns)
            {
                var ac = engine.FindAircraft(cs);
                if (ac is null)
                {
                    continue;
                }

                string phase = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
                string prevPhase = prevPhases.GetValueOrDefault(cs, "");

                // Log on phase transition or every 5 seconds during ground phases
                bool phaseChanged = phase != prevPhase;
                bool isGroundPhase = phase.Contains("Landing") || phase.Contains("Exit") || phase.Contains("Holding") || phase.Contains("Flare");
                bool periodicLog = isGroundPhase && (t % 5 == 0);

                if (phaseChanged || periodicLog)
                {
                    string rwy = ac.Phases?.AssignedRunway?.Designator ?? "?";
                    string exitPref = ac.Phases?.RequestedExit?.Taxiway ?? ac.Phases?.RequestedExit?.Side?.ToString() ?? "none";

                    output.WriteLine(
                        $"[{cs}] t={600 + t} phase={phase} "
                            + $"pos=({ac.Latitude:F6},{ac.Longitude:F6}) "
                            + $"hdg={ac.TrueHeading.Degrees:F0} "
                            + $"gs={ac.GroundSpeed:F1} ias={ac.IndicatedAirspeed:F1} "
                            + $"alt={ac.Altitude:F0} "
                            + $"onGround={ac.IsOnGround} "
                            + $"rwy={rwy} exitPref={exitPref} "
                            + $"taxiway={ac.CurrentTaxiway ?? "-"}"
                    );
                }

                prevPhases[cs] = phase;
            }
        }
    }
}
