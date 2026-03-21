using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #131: Clear to Land (CL/CLAND) not being remembered.
///
/// Recording: S2-OAK-4 VFR Transitions Radar Concepts — OAK tower scenario.
/// Multiple aircraft receive CLAND but the clearance state is lost before
/// FinalApproachPhase checks it, causing unexpected go-arounds.
/// </summary>
public class Issue131ClearToLandTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue131-clear-to-land-recording.json";

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
    /// Diagnostic: full replay of the recording with SimLog wired to xunit output.
    /// All FinalApproach, GoAround, and command dispatch logs will appear in the
    /// test output. Look for "go-around triggered (no landing clearance)" to find
    /// the aircraft that lost their CL state.
    /// </summary>
    [Fact]
    public void Diagnostic_FullReplay()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, (int)recording.TotalElapsedSeconds);
    }

    /// <summary>
    /// After entering the pattern and receiving CLAND on downwind, the aircraft
    /// must land instead of going around. Replays N775JW to downwind (after the
    /// turn fix removes the 360), issues CLAND, then ticks until the aircraft
    /// either lands or goes around.
    /// </summary>
    [Fact]
    public void PatternEntry_ClandOnDownwind_AircraftLands()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past ERD 28R (t=539) and EF 28R (t=705).
        // By t=610 N775JW is on PatternEntry heading toward downwind.
        engine.Replay(recording, 610);

        var ac = engine.FindAircraft("N775JW");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        output.WriteLine($"t=610: phase={ac.Phases.CurrentPhase?.GetType().Name} alt={ac.Altitude:F0}");

        // Tick until we're on DownwindPhase
        for (int t = 1; t <= 200; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N775JW");
            if (ac is null)
            {
                break;
            }

            if (ac.Phases?.CurrentPhase is Yaat.Sim.Phases.Pattern.DownwindPhase)
            {
                output.WriteLine($"t+{t}: reached downwind, issuing CLAND");
                var result = engine.SendCommand("N775JW", "CLAND");
                output.WriteLine($"  CLAND result: {result.Success} — {result.Message}");
                Assert.True(result.Success);
                break;
            }
        }

        ac = engine.FindAircraft("N775JW");
        Assert.NotNull(ac);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases?.LandingClearance);

        // Now tick until the aircraft lands or goes around (max 300s)
        bool landed = false;
        bool goAround = false;
        for (int t = 1; t <= 300; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N775JW");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted (landed and removed)");
                landed = true;
                break;
            }

            if (ac.IsOnGround && ac.GroundSpeed < 30)
            {
                output.WriteLine($"t+{t}: landed, gs={ac.GroundSpeed:F0}kts");
                landed = true;
                break;
            }

            foreach (var w in ac.PendingWarnings)
            {
                if (w.Contains("going around", StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine($"t+{t}: WARNING: {w}");
                    goAround = true;
                }
            }
        }

        Assert.True(landed, "Aircraft should have landed after CLAND on downwind");
        Assert.False(goAround, "Aircraft should not have gone around after CLAND");
    }

    /// <summary>
    /// Diagnostic: tick-by-tick trace of pattern aircraft from the recording.
    /// Logs position, heading, altitude, phase, and nav targets every second
    /// to see exactly how the aircraft flies the pattern entry and base turn.
    /// Uses ReplayOneSecond() so recording actions are properly applied.
    /// </summary>
    [Theory]
    [InlineData("N427MX", 1354, 120, "after ELB 28R")]
    [InlineData("N655EX", 1542, 200, "after ELB 28R")]
    [InlineData("N929AW", 1947, 200, "after ERD 28R")]
    [InlineData("N775JW", 540, 200, "after ERD 28R")]
    [InlineData("N805FM", 822, 120, "after ERB 28R")]
    public void Diagnostic_PatternEntry_TickByTick(string callsign, int replayTo, int tickCount, string context)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, replayTo);

        var ac = engine.FindAircraft(callsign);
        if (ac is null)
        {
            output.WriteLine($"{callsign} not found at t={replayTo}");
            return;
        }

        output.WriteLine($"=== {callsign} {context} (t={replayTo}) ===");
        output.WriteLine($"  pos=({ac.Latitude:F4},{ac.Longitude:F4}) hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0}");
        output.WriteLine($"  phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        var navRoute = ac.Targets.NavigationRoute;
        for (int i = 0; i < navRoute.Count; i++)
        {
            output.WriteLine($"  nav[{i}]: {navRoute[i].Name} ({navRoute[i].Latitude:F4},{navRoute[i].Longitude:F4})");
        }

        output.WriteLine("");

        for (int t = 1; t <= tickCount; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(callsign);
            if (ac is null)
            {
                output.WriteLine($"t+{t}: (deleted)");
                break;
            }

            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            string navInfo =
                ac.Targets.NavigationRoute.Count > 0 ? string.Join(", ", ac.Targets.NavigationRoute.Select(n => $"{n.Name}")) : "(empty)";
            string tgtHdg = ac.Targets.TargetTrueHeading is { } th ? $"{th.Degrees:F0}" : "nav";
            string clearance = ac.Phases?.LandingClearance?.ToString() ?? "none";

            output.WriteLine(
                $"t+{t, 3}: pos=({ac.Latitude:F4},{ac.Longitude:F4}) "
                    + $"hdg={ac.TrueHeading.Degrees:F0} tgtHdg={tgtHdg} alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} "
                    + $"phase={phaseName} clearance={clearance} nav=[{navInfo}]"
            );

            foreach (var w in ac.PendingWarnings)
            {
                output.WriteLine($"  WARNING: {w}");
            }
        }
    }
}
