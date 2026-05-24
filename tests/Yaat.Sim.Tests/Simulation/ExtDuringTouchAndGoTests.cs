using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// EXT pre-arms the upcoming UpwindPhase when issued during a phase that is not a
/// pattern leg (TouchAndGo, FinalApproach pre-T/G, HoldingShort/LineUp/Takeoff,
/// InitialClimb). Bug report bundle: S2-OAK-3 — N172SP doing closed pattern on
/// OAK 28R. The user issued <c>EXT</c> repeatedly during TouchAndGoPhase (~t=578
/// onward); each attempt rejected with "Extend applies on upwind, crosswind, or
/// downwind" until the aircraft happened to transition into UpwindPhase at t=594.
///
/// Expected after fix:
///   * EXT during TouchAndGo when the next circuit is not yet queued → sets
///     <c>AircraftPattern.ExtendNextUpwind</c>; PhaseRunner applies IsExtended to
///     the first Upwind of the appended circuit when T/G completes.
///   * EXT during FinalApproach pre-T/G → same behavior.
///   * EXT during HoldingShort/LineUp/Takeoff/InitialClimb when the initial
///     circuit's UpwindPhase already exists as a pending phase in the queue →
///     sets IsExtended directly on that pending UpwindPhase (no flag needed).
///   * EXT CROSSWIND from a non-pattern-leg phase still rejects (scope decision).
/// </summary>
public class ExtDuringTouchAndGoTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/c0c9f6aa6cb7.zip";
    private const string Callsign = "N172SP";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("PhaseRunner", LogLevel.Debug)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void ExtDuringTouchAndGo_ArmsNextUpwind()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=575 — 15 s into N172SP's first TouchAndGo (which started at
        // t=560 in the recording). The next Upwind has NOT yet been queued by
        // PhaseRunner (that only happens when TouchAndGo completes).
        engine.Replay(recording, 575);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<TouchAndGoPhase>(aircraft.Phases?.CurrentPhase);

        // Pre-condition: no pending UpwindPhase exists yet, ExtendNextUpwind is false.
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
        Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is UpwindPhase { Status: PhaseStatus.Pending });

        var result = engine.SendCommand(Callsign, "EXT");
        output.WriteLine($"EXT result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"EXT during TouchAndGo should succeed but got: {result.Message}");
        Assert.True(aircraft.Pattern.ExtendNextUpwind, "ExtendNextUpwind flag should be set");

        // Tick forward until the aircraft enters the next UpwindPhase. The current
        // T/G phase completes once the rollout is done; PhaseRunner appends the next
        // circuit and consumes the flag, setting IsExtended on the new Upwind.
        UpwindPhase? newUpwind = null;
        for (int dt = 1; dt <= 120; dt++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is UpwindPhase up)
            {
                newUpwind = up;
                output.WriteLine($"t={575 + dt}: entered UpwindPhase, IsExtended={up.IsExtended}, ExtendNextUpwind={ac.Pattern.ExtendNextUpwind}");
                break;
            }
        }

        Assert.NotNull(newUpwind);
        Assert.True(newUpwind!.IsExtended, "First Upwind of the next circuit should have IsExtended=true (consumed from flag)");
        Assert.False(aircraft.Pattern.ExtendNextUpwind, "Flag should be cleared after consumption");
    }

    [Fact]
    public void ExtDuringFinalApproach_BeforeTouchAndGo_ArmsNextUpwind()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=540 — 40 s into N172SP's first FinalApproach (started t=500).
        // The aircraft is committed to T/G (TouchAndGoPhase is queued next).
        engine.Replay(recording, 540);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Contains(aircraft.Phases!.Phases, p => p is TouchAndGoPhase);

        var result = engine.SendCommand(Callsign, "EXT");
        output.WriteLine($"EXT result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"EXT during pre-T/G FinalApproach should succeed but got: {result.Message}");
        Assert.True(aircraft.Pattern.ExtendNextUpwind);

        // Tick through landing + T/G rollout + climb into the next Upwind.
        UpwindPhase? newUpwind = null;
        for (int dt = 1; dt <= 180; dt++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is UpwindPhase up)
            {
                newUpwind = up;
                break;
            }
        }

        Assert.NotNull(newUpwind);
        Assert.True(newUpwind!.IsExtended);
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
    }

    [Fact]
    public void ExtDuringHoldingShort_ArmsPendingUpwind_Directly()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=200 — just after the CTO MRT at t=195 built the initial
        // pattern chain. The aircraft is still at the runway in LineUp/HoldingShort
        // territory and a pending UpwindPhase already exists in the queue.
        engine.Replay(recording, 200);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var pendingUpwindBefore = aircraft.Phases?.Phases.OfType<UpwindPhase>().FirstOrDefault(p => p.Status == PhaseStatus.Pending);
        Assert.NotNull(pendingUpwindBefore);
        Assert.False(pendingUpwindBefore!.IsExtended);

        var result = engine.SendCommand(Callsign, "EXT");
        output.WriteLine($"EXT result: Success={result.Success}, Message={result.Message}");

        Assert.True(result.Success, $"EXT during HoldingShort/LineUp with pending Upwind should succeed but got: {result.Message}");
        Assert.True(pendingUpwindBefore.IsExtended, "Pending Upwind in queue should have IsExtended=true set directly");

        // Layer 1 path → flag stays false because we mutated the queued phase directly.
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
    }

    [Fact]
    public void ExtCrosswind_DuringTouchAndGo_StillRejects()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=575 (during TouchAndGo, no pending Crosswind in queue yet).
        engine.Replay(recording, 575);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.IsType<TouchAndGoPhase>(aircraft.Phases?.CurrentPhase);

        var result = engine.SendCommand(Callsign, "EXT C");
        output.WriteLine($"EXT C result: Success={result.Success}, Message={result.Message}");

        // Scope guard: EXT CROSSWIND / EXT DOWNWIND keep original rejection from
        // non-pattern-leg phases. Only EXT UPWIND (and bare EXT) get the pre-arm.
        Assert.False(result.Success);
        Assert.Contains("upwind", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(aircraft.Pattern.ExtendNextUpwind);
    }
}
