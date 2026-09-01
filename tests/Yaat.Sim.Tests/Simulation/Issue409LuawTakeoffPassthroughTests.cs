using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for issue #409: an aircraft holding short that receives CTO while another
/// aircraft is lined up and waiting on the same runway must stop behind the occupant,
/// not taxi through it.
///
/// Recording: OAK TWR | North Field Day 13 — runway 28R, entry via taxiway B.
///   t=853 N630LT (PA31, holding short 28R): LUAW → lines up, LinedUpAndWaiting by t=880
///   t=877 DAL802 (MD81, holding short 28R same entry): CTO → LineUp
///   t=893 (buggy) DAL802 drives straight through the stationary N630LT
///          (interpolated minimum separation ~1 ft) and departs
///   t=910 N630LT: CTO → departs
///
/// Root cause: GroundConflictDetector classified the actively-moving LineUpPhase
/// aircraft as Stationary (phase-name list), putting the pair into the no-op
/// Stationary/Stationary bucket — no speed limit was ever written. Expected after
/// fix: DAL802 holds behind N630LT until it departs, then continues and departs.
/// </summary>
[Collection("NavDbMutator")]
public class Issue409LuawTakeoffPassthroughTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue409-luaw-takeoff-passthrough-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void ClearedForTakeoff_BehindLuawOccupant_StopsInsteadOfPassingThrough()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just before DAL802's CTO (t=877); N630LT received LUAW at t=853.
        engine.Replay(recording, 875);

        var dal = engine.FindAircraft("DAL802");
        var luaw = engine.FindAircraft("N630LT");
        Assert.NotNull(dal);
        Assert.NotNull(luaw);
        output.WriteLine($"t=875 precondition: DAL802 phase={dal.Phases?.CurrentPhase?.Name} N630LT phase={luaw.Phases?.CurrentPhase?.Name}");

        // Step through the conflict window and past N630LT's departure, tracking the
        // closest approach while both aircraft are on the ground.
        double minSeparationFt = double.MaxValue;
        int minSeparationAt = -1;
        int lastTick = -1;
        for (int t = 876; t <= 1000; t++)
        {
            engine.ReplayOneSecond();
            lastTick = t;
            if (dal.IsOnGround && luaw.IsOnGround)
            {
                double sepFt = GeoMath.DistanceNm(dal.Position, luaw.Position) * 6076.12;
                if (sepFt < minSeparationFt)
                {
                    minSeparationFt = sepFt;
                    minSeparationAt = t;
                }
            }
            else if ((!dal.IsOnGround) && (!luaw.IsOnGround))
            {
                // Both airborne — the conflict window and its resolution are fully observed.
                break;
            }
        }

        output.WriteLine($"min on-ground separation {minSeparationFt:F0} ft at t={minSeparationAt}");
        output.WriteLine($"t={lastTick}: DAL802 phase={dal.Phases?.CurrentPhase?.Name} alt={dal.Altitude:F0} onGround={dal.IsOnGround}");
        output.WriteLine($"t={lastTick}: N630LT phase={luaw.Phases?.CurrentPhase?.Name} alt={luaw.Altitude:F0} onGround={luaw.IsOnGround}");

        // Buggy behavior bottomed out at ~1-40 ft (a pass-through). The conflict
        // detector's proximity stop keeps the pair at least a fuselage apart.
        Assert.True(
            minSeparationFt > 60,
            $"DAL802 passed through the LUAW aircraft N630LT: min on-ground separation {minSeparationFt:F0} ft at t={minSeparationAt}"
        );

        // The hold must resolve, not deadlock: N630LT departs on its t=910 CTO, and
        // DAL802 continues its own takeoff once the runway is clear.
        Assert.False(luaw.IsOnGround, $"N630LT should have departed (phase={luaw.Phases?.CurrentPhase?.Name})");
        Assert.False(dal.IsOnGround, $"DAL802 should have departed after N630LT cleared the runway (phase={dal.Phases?.CurrentPhase?.Name})");
    }
}
