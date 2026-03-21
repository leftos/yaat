using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #133: Aircraft taxiing via D C B to RWY28R at OAK
/// get stuck on the last taxi segment and never reach the hold-short node.
///
/// Root cause: the braking curve decelerates the aircraft to speed=0 right at the
/// arrival threshold boundary (0.015nm). Due to floating-point precision,
/// dist=0.0150nm fails the &lt;= 0.015 check, and the aircraft is stuck forever
/// with zero speed just outside the arrival threshold.
///
/// Recording: S2-OAK-4 VFR Transitions Radar Concepts — OAK tower scenario.
/// N172SP taxis via D C B to 28R. LUAW at t=449 stores departure clearance but
/// the aircraft never reaches the hold-short node, so the clearance is never applied.
/// </summary>
public class Issue133Rwy28rTakeoffTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue133-rwy28r-takeoff-recording.json";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        NavigationDatabase.SetInstance(navDb);
        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// N172SP must reach the 28R hold-short within a reasonable time after starting
    /// its taxi via D C B 28R. Before the fix, the aircraft gets stuck on the last
    /// segment with speed=0 at exactly the arrival threshold, never triggering arrival.
    /// </summary>
    [Fact]
    public void N172SP_ReachesHoldShort_AfterTaxiDCB28R()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=440 — the taxi preset (TAXI D C B 28R) starts at t=0.
        // 440 seconds is plenty of time for the aircraft to complete the taxi.
        engine.Replay(recording, 440);

        var n172sp = engine.FindAircraft("N172SP");
        Assert.NotNull(n172sp);

        // The aircraft should have reached the hold-short by now and be in HoldingShortPhase,
        // not still in TaxiingPhase stuck on the last segment.
        Assert.True(
            n172sp.Phases?.CurrentPhase is HoldingShortPhase or HoldingInPositionPhase,
            $"Expected HoldingShortPhase or HoldingInPositionPhase but got {n172sp.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}"
        );
    }

    /// <summary>
    /// After LUAW is issued, the aircraft should line up on 28R within 60 seconds.
    /// Before the fix, LUAW stores the departure clearance but the aircraft never
    /// reaches the hold-short to consume it, so LUAW has no visible effect.
    /// </summary>
    [Fact]
    public void N172SP_LinesUpOnRunway_AfterLuaw()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=440 and issue LUAW
        engine.Replay(recording, 440);

        var n172sp = engine.FindAircraft("N172SP");
        Assert.NotNull(n172sp);

        var result = engine.SendCommand("N172SP", "LUAW");
        Assert.True(result.Success, $"LUAW failed: {result.Message}");

        // Tick forward up to 60 seconds — the aircraft should transition through
        // LineUpPhase to LinedUpAndWaitingPhase
        bool reachedLineUp = false;
        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            n172sp = engine.FindAircraft("N172SP");
            Assert.NotNull(n172sp);

            if (n172sp.Phases?.CurrentPhase is LinedUpAndWaitingPhase or LineUpPhase)
            {
                reachedLineUp = true;
                output.WriteLine($"Reached {n172sp.Phases.CurrentPhase.GetType().Name} at t+{t}");
                break;
            }
        }

        Assert.True(reachedLineUp, "Aircraft never entered LineUpPhase or LinedUpAndWaitingPhase after LUAW");
    }
}
