using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #276: preset "ER ; EXIT D" ignores the ER side.
///
/// Recording: S1-SFO-P (2) | San Francisco GC PV — LXJ574 (CL30) spawns OnFinal
/// 28R at t=249 with two separate preset commands, ER then EXIT D. Taxiway D
/// crosses 28R with a hold-short on BOTH sides (node 831 right / 832 left). The
/// bug: EXIT D (taxiway-only) blind-overwrites ER's Side=Right with null, so the
/// aircraft falls back to the inferred side (Left) and exits at the LEFT D (832).
/// After the fix it should exit at the RIGHT D (831).
/// </summary>
public class Issue276ErThenExitTaxiwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue276-er-exit-left-recording.zip";

    // The two D hold-shorts on 28R (from Yaat.LayoutInspector --node 831 / 832).
    private static readonly LatLon RightD = new(37.624501, -122.381472); // node 831
    private static readonly LatLon LeftD = new(37.623292, -122.382279); // node 832

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

    [Fact]
    public void LXJ574_ExitsRightAtD_NotLeft()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Full replay from t=0: LXJ574 spawns at t=249 and its presets dispatch
        // through the (fixed) command path. Hybrid replay would restore an already
        // clobbered RequestedExit and not exercise the fix, so replay from scratch.
        // Replay to just before touchdown/exit resolution, then tick to the exit.
        engine.Replay(recording, 460);

        var ac = engine.FindAircraft("LXJ574");
        Assert.NotNull(ac);
        output.WriteLine(
            $"t=460: alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
        );

        LatLon lastPos = ac.Position;
        for (int t = 1; t <= 200; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("LXJ574");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft deleted (using last position)");
                break;
            }

            lastPos = ac.Position;

            if (t % 15 == 0)
            {
                output.WriteLine(
                    $"t+{t}: gs={ac.GroundSpeed:F0} hdg={ac.TrueHeading.Degrees:F0}"
                        + $" twy={ac.Ground.CurrentTaxiway ?? "(none)"} phase={ac.Phases?.CurrentPhase?.GetType().Name}"
                );
            }

            // Exited onto D and rolled to a stop at the hold-short line.
            if ((ac.Ground.CurrentTaxiway is not null) && (ac.GroundSpeed < 3))
            {
                break;
            }
        }

        double distRight = GeoMath.DistanceNm(lastPos, RightD);
        double distLeft = GeoMath.DistanceNm(lastPos, LeftD);
        output.WriteLine($"final pos=({lastPos.Lat:F6},{lastPos.Lon:F6}) distToRightD={distRight:F4}nm distToLeftD={distLeft:F4}nm");

        Assert.True(
            distRight < distLeft,
            $"LXJ574 should exit at the RIGHT D (node 831) per ER, but ended nearer the LEFT D: "
                + $"distToRightD={distRight:F4}nm distToLeftD={distLeft:F4}nm"
        );
    }

    /// <summary>
    /// Safety guarantee for the merge fix: when the merged {Side, Taxiway} names a
    /// taxiway that exists only on the OTHER side, the aircraft must still take that
    /// taxiway (the name is a hard filter, the side a soft preference) — it must
    /// never fail to exit. C3 at SFO is right-only, so requesting {Left, C3} must
    /// resolve to the RIGHT C3 rather than returning nothing.
    /// </summary>
    [Fact]
    public void NamedTaxiwayWithUnsupportedSide_FallsBackToOtherSide()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        var heading = new TrueHeading(282); // 28R true heading

        // C3's runway branch node is at ~(37.628669,-122.393256); locate by position
        // so the test is robust to node-id churn.
        var centerlineNode = layout.FindNearestCenterlineNode(37.628669, -122.393256, heading, "28R");
        Assert.NotNull(centerlineNode);

        var result = layout.FindAdjacentHoldShort(centerlineNode, "28R", heading, new ExitPreference { Side = ExitSide.Left, Taxiway = "C3" });

        Assert.NotNull(result);
        Assert.Equal("C3", result.Value.Taxiway);
        Assert.Equal(ExitSide.Right, result.Value.Side);
    }
}
