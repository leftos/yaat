using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.V2Acceptance;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: under the all-V2 ground stack, N9225L (C172 landing OAK 28R, no exit
/// instruction) must roll out and turn off onto G without the navigator stalling at a
/// 5 kt crawl. Before the Bézier arc-playback fix, the V2 navigator reinterpreted the
/// runway→G corner fillet as a circle of the Bézier's *minimum* radius of curvature
/// (72 ft for endpoints 153 ft apart — geometrically impossible), so closed-form
/// playback ended ~56 ft short of the corner's exit node. The two short follow-on
/// segments then started with a 50–80 ft cross-track, tripping the establish-straight
/// re-acquire gate (5 kt) twice and adding ~9 s vs V1. With true Bézier arc-length
/// playback the corner ends exactly on its node and the exit flows.
/// </summary>
[Collection("V2 Acceptance")]
public class N9225LExitTimingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N9225L";
    private const int TouchdownSecond = 400;

    // The re-acquire gate pins ground speed at exactly ReacquireSpeedKts (5.0). A normal
    // braking pass-through of that speed lasts a tick or two; a crawl holds it for many
    // seconds. Before the fix the exit had two such plateaus (≈6 s and ≈5 s); after it,
    // none. Allow a small pass-through window.
    private const int MaxConsecutiveReacquireFloorSeconds = 2;

    [Fact]
    public void N9225L_ExitsG_WithoutReacquireCrawl_OnV2()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData(FilletMode.Standard);
        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, TouchdownSecond);

        int? stopSecond = null;
        string? exitTaxiway = null;
        int floorRun = 0;
        int maxFloorRun = 0;
        for (int t = TouchdownSecond + 1; t <= TouchdownSecond + 60; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            string? twy = ac.Ground.CurrentTaxiway;
            if ((exitTaxiway is null) && (twy is not null) && !twy.StartsWith("RWY", StringComparison.Ordinal))
            {
                exitTaxiway = twy;
            }

            // Count consecutive seconds pinned at the re-acquire floor (4.5–5.5 kt).
            if ((ac.GroundSpeed is >= 4.5 and <= 5.5) && (exitTaxiway is not null))
            {
                floorRun++;
                maxFloorRun = Math.Max(maxFloorRun, floorRun);
            }
            else
            {
                floorRun = 0;
            }

            if ((ac.GroundSpeed <= 1.0) && (t > TouchdownSecond + 5))
            {
                stopSecond = t;
                break;
            }
        }

        output.WriteLine($"N9225L exit taxiway={exitTaxiway ?? "(none)"} stopped@={stopSecond?.ToString() ?? "n/a"} maxFloorRun={maxFloorRun}s");

        Assert.Equal("G", exitTaxiway);
        Assert.NotNull(stopSecond);
        Assert.True(
            maxFloorRun <= MaxConsecutiveReacquireFloorSeconds,
            $"N9225L crawled at the {5.0:F0} kt re-acquire floor for {maxFloorRun} consecutive seconds during the 28R→G "
                + "exit. That means the navigator handed off a corner/short segment with a large cross-track (arc playback "
                + "ending short of its node, or a loose arrival into a short stub) and the establish-straight gate pinned it."
        );
    }
}
