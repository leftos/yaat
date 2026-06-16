using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N85439 (C172 VFR) was cleared "CTO MR270 014" (climb/maintain 1400) and climbed
/// normally at 1240 fpm. While still in <see cref="Yaat.Sim.Phases.Tower.TakeoffPhase"/> at
/// ~400 ft AGL, the controller issued "FH 280" (~t=1277), which cleared the entire departure
/// phase chain (Takeoff → InitialClimb) before InitialClimb could raise the climb target to the
/// assigned 1400. TakeoffPhase had set TargetAltitude to fieldElev+400 (~409 ft MSL) as its
/// handoff target; with no phase left to manage altitude and the command queue carrying no
/// altitude block, the aircraft leveled at ~409 ft, TargetAltitude snapped to null, and it never
/// climbed to its assigned 1400 — for the rest of the flight AssignedAltitude=1400 but VS=0.
///
/// A lateral instruction does not cancel an altitude clearance: after FH the aircraft must keep
/// climbing to its assigned 1400. This test replays past the phase clear and ticks physics-only
/// (the controller's later CM commands at t≥1518 are not replayed), asserting the aircraft
/// resumes its climb toward the assigned altitude instead of stalling at ~400 ft AGL.
/// </summary>
public class N85439ClimbStallAfterFhTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/relr270-dct-wrong-turn-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N85439";

    // FH 280 clears the phase chain ~t=1277; by 1305 the aircraft has leveled at ~409 ft on the
    // buggy code. Replaying here lands after the clear but before the controller's CM commands.
    private const int PhaseClearedTime = 1305;
    private const int ClimbWindowSeconds = 200;
    private const double AssignedAltitude = 1400.0;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

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
            .EnableCategory("TakeoffPhase", LogLevel.Debug)
            .EnableCategory("InitialClimbPhase", LogLevel.Debug)
            .EnableCategory("FlightPhysics", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void N85439_ResumesClimbToAssignedAltitudeAfterFh()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, PhaseClearedTime);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        double startAlt = ac.Altitude;
        Assert.Equal(AssignedAltitude, ac.Targets.AssignedAltitude);
        output.WriteLine(
            $"t={PhaseClearedTime}: alt={startAlt:F0} vs={ac.VerticalSpeed:F0} "
                + $"tgtAlt={ac.Targets.TargetAltitude?.ToString("F0") ?? "(null)"} "
                + $"asgAlt={ac.Targets.AssignedAltitude?.ToString("F0") ?? "(null)"}"
        );

        double maxAlt = startAlt;
        for (int t = 1; t <= ClimbWindowSeconds; t++)
        {
            engine.TickOneSecond(); // physics only — do NOT replay the later CM commands at t>=1518
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            maxAlt = Math.Max(maxAlt, ac.Altitude);

            if (t % 20 == 0)
            {
                output.WriteLine(
                    $"  t+{t, 3}: alt={ac.Altitude, 6:F0} vs={ac.VerticalSpeed, 6:F0} "
                        + $"tgtAlt={ac.Targets.TargetAltitude?.ToString("F0") ?? "(null)"}"
                );
            }
        }

        // The aircraft must resume climbing to its assigned 1400 — not stall at ~400 ft AGL
        // (~409 ft MSL) where the cleared TakeoffPhase left it.
        Assert.True(
            maxAlt >= AssignedAltitude - 100,
            $"{Callsign} should climb to its assigned {AssignedAltitude:F0} ft after FH, but only reached {maxAlt:F0} ft "
                + $"(started {startAlt:F0}) — climb stalled when the departure phase was cleared"
        );
    }
}
