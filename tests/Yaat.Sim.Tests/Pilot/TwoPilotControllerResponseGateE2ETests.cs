using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Reproduces the S2-OAK-4 (ZOA) failure: scenario "VFR Transitions/Radar Concepts"
/// spawns N569SX on a 10-mile final to runway 28R and N436MS parked at OAK with a
/// `TAXI B 28R` preset. Solo-mode student is OAK_TWR. After UNPAUSE both pilots
/// eventually try to transmit a check-in: N569SX's airborne on-final report fires
/// almost immediately; N436MS's holding-short ready-for-departure fires once it
/// finishes taxiing to the runway. With the awaiting-controller-response gate, the
/// second pilot should be held back for the full <c>AwaitedTimeoutSeconds</c> window
/// of silence past the first pilot's transmission, not just gated on airtime.
/// </summary>
[Collection("NavDbMutator")]
public sealed class TwoPilotControllerResponseGateE2ETests
{
    private const string ScenarioFile = "01HG3N8Q5PPR7QXZK33ZPC4D5M.json";
    private const string ArtccId = "ZOA";
    private const string FirstCallsign = "N569SX";
    private const string SecondCallsign = "N436MS";

    private static readonly string ScenariosRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "Scenarios")
    );

    private readonly ITestOutputHelper _output;

    public TwoPilotControllerResponseGateE2ETests(ITestOutputHelper output)
    {
        _output = output;
        SimLogBuilder.CreateForTest(output).EnableCategory("FrequencyState", LogLevel.Information).InitializeSimLog();
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void S2Oak4_TwoSimultaneousProactives_HoldSecondPilotForFullSilenceWindow()
    {
        if (TestVnasData.NavigationDb is null)
        {
            _output.WriteLine("TestVnasData unavailable — skipping");
            return;
        }

        var scenarioPath = Path.Combine(ScenariosRoot, ArtccId, ScenarioFile);
        if (!File.Exists(scenarioPath))
        {
            _output.WriteLine($"S2-OAK-4 not cached at {scenarioPath} — skipping. Download via tools/validate-all-scenarios.py.");
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            _output.WriteLine("OAK ground layout unavailable — skipping");
            return;
        }

        var scenarioJson = File.ReadAllText(scenarioPath);
        var engine = new SimulationEngine(groundData);
        var warnings = engine.LoadScenario(scenarioJson, rngSeed: 42);
        foreach (var w in warnings)
        {
            _output.WriteLine($"[load-warn] {w}");
        }
        Assert.NotNull(engine.Scenario);

        // Solo training mode is what gates the queue. Without it, transmissions are
        // discarded each tick and the gate is irrelevant.
        engine.Scenario.SoloTrainingMode = true;
        // Match the 16:25 production run: VFR Transitions/Radar Concepts is a tower
        // scenario — the student takes OAK_TWR. (ScenarioLoader resolves student
        // position from studentPositionId; we just need solo mode on for queueing.)

        // Track the controller-response gate transitions across the run. Each transition
        // tells us a new pilot just spoke — that's how we observe the queue draining.
        var gateTimeline = new List<(double Time, string? AwaitingFrom)>();
        string? lastGate = null;

        // Run for 5 minutes of sim time. N569SX's airborne check-in fires on the first
        // tick it's airborne. N436MS taxis B → 28R; on a small piston that takes ~60-90s.
        // Then HoldingShortPhase queues its ready-for-departure proactive.
        const int totalSeconds = 5 * 60;
        for (int t = 1; t <= totalSeconds; t++)
        {
            engine.TickOneSecond();
            var current = engine.World.ActiveFrequency.AwaitingControllerResponseTo;
            if (current != lastGate)
            {
                gateTimeline.Add((engine.Scenario.ElapsedSeconds, current));
                _output.WriteLine($"t={engine.Scenario.ElapsedSeconds:F1} gate-transition: {lastGate ?? "<none>"} → {current ?? "<none>"}");
                lastGate = current;
            }
        }

        // The gate timeline should contain at least:
        //   - One transition to N569SX (its on-final check-in fires first)
        //   - One transition to N436MS (its holding-short ready-for-departure fires later)
        var firstGate = gateTimeline.FirstOrDefault(e => e.AwaitingFrom == FirstCallsign);
        var secondGate = gateTimeline.FirstOrDefault(e => e.AwaitingFrom == SecondCallsign);

        Assert.NotEqual(default, firstGate);
        Assert.NotEqual(default, secondGate);
        Assert.True(firstGate.Time < secondGate.Time, $"{FirstCallsign} should set the gate before {SecondCallsign}");

        // Core assertion: by the time N436MS dequeues, N569SX's gate must have been
        // active for at least AwaitedTimeoutSeconds (8s) — meaning the second pilot
        // got held back through the timeout, not let through immediately on airtime.
        // The gap between gate transitions must be >= 8s. If the controller-response
        // gate weren't doing its job, the gap would be just the airtime (~3-5s).
        double gapSeconds = secondGate.Time - firstGate.Time;
        _output.WriteLine($"Gap between {FirstCallsign} and {SecondCallsign} gate set: {gapSeconds:F1}s (must be ≥ airtime + ~8s)");
        Assert.True(
            gapSeconds >= 8.0,
            $"{SecondCallsign} dequeued only {gapSeconds:F1}s after {FirstCallsign} — controller-response gate failed to hold for the full silence window"
        );
    }
}
