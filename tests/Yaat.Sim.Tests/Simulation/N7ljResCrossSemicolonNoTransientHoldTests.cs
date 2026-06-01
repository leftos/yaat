using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for the sequential semicolon form <c>RES; CROSS 28L</c>.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts — N7LJ (LJ45) preset
/// <c>TAXI D C B W W1 30 HS 28R</c>, crossing 28R/10L then 28L/10R en route to
/// runway 30.
///
/// Bug (S2-OAK-5, 2026-05-30): a controller issued <c>RES; CROSS 28L</c> (the
/// semicolon form). Because <c>;</c> splits into two sequential blocks and the
/// ground queue only advances a sequential block when the aircraft reaches its
/// next *wait* phase, the <c>CROSS 28L</c> block didn't apply until the aircraft
/// had already entered a <see cref="HoldingShortPhase"/> at 28L — whose OnStart
/// fired a spurious "holding short 28L" warning — even though it then crossed
/// without truly stopping. A Resume block should instead be "done" as soon as the
/// aircraft resumes taxiing, so the following block pre-clears 28L before arrival.
///
/// Expected: no <see cref="HoldingShortPhase"/> for 28L is ever installed; the
/// aircraft transitions straight into a <see cref="CrossingRunwayPhase"/> at 28L.
/// </summary>
public class N7ljResCrossSemicolonNoTransientHoldTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/606cf53c33a1.zip";

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
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    private static bool IsHoldFor(HoldingShortPhase hold, string runway) =>
        hold.HoldShort.TargetName?.Contains(runway, StringComparison.Ordinal) == true;

    [Fact]
    public void ResSemicolonCross28L_DoesNotInstallTransientHoldShortAt28L()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // t=1300s — N7LJ is holding short of 28R/10L on B.
        engine.Replay(recording, 1300);

        var ac = engine.FindAircraft("N7LJ");
        Assert.NotNull(ac);
        var holdPhase = ac.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(holdPhase);
        Assert.Equal("28R/10L", holdPhase.HoldShort.TargetName);

        var result = engine.SendCommand("N7LJ", "RES; CROSS 28L");
        Assert.True(result.Success, $"RES; CROSS 28L should succeed, got: {result.Message}");

        // Tick through both crossings. At no point should a HoldingShortPhase for
        // 28L become current, and no such phase should land in the persistent list.
        bool everHeldShortOf28L = false;
        bool crossed28L = false;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("N7LJ");
            Assert.NotNull(ac);

            if (ac.Phases?.CurrentPhase is HoldingShortPhase h && IsHoldFor(h, "28L"))
            {
                everHeldShortOf28L = true;
            }
            if (ac.Phases?.CurrentPhase is CrossingRunwayPhase c && (c.RunwayId?.Contains("28L", StringComparison.Ordinal) == true))
            {
                crossed28L = true;
            }
        }

        var holds28LInList = ac!.Phases!.Phases.OfType<HoldingShortPhase>().Where(h => IsHoldFor(h, "28L")).ToList();

        Assert.False(everHeldShortOf28L, "N7LJ entered a HoldingShortPhase for 28L despite RES; CROSS 28L pre-clearing it");
        Assert.Empty(holds28LInList);
        Assert.True(crossed28L, "N7LJ never crossed 28L within 120s — adjust the replay window");
    }
}
