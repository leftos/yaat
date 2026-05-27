using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E replay tests for GitHub issue #161: SKW3404 is pushed off SFO D-gates
/// with <c>PUSH A FACE E</c> (snaps to heading 125° SE) and then given
/// <c>TAXI A F1 B Z S S3 10R</c>. The aircraft visibly "spun around in a
/// circle" before settling on a southbound taxi — even though it was already
/// pointing toward the destination.
///
/// Recording: <c>S1-SFO-4 | FD/CD/GC 19/10</c>. Three actions for SKW3404:
/// <c>PUSH A FACE E</c> at t=0, <c>SQNORM</c> at t=15, <c>TAXI A F1 B Z S S3
/// 10R</c> at t=52. The spin occurs over t=52..t=80, producing ~360° of
/// cumulative heading change before the aircraft heads south.
/// </summary>
public class Issue161PushFaceThenTaxiTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue161-push-face-then-taxi-recording.zip";

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
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay to t=52 (the TAXI command fires). The first leg of the route
    /// is taxiway A heading south — the aircraft must make immediate
    /// southbound progress, not loop northward chasing a wrong-branch start
    /// node. Net southward displacement at t=52+10 cleanly separates the
    /// looping bug (lat trended ~25 ft north at this point) from a correct
    /// taxi (lat trends ~250 ft south).
    /// </summary>
    [Fact]
    public void SKW3404_TaxisSouthwardAfterTaxiCommand()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=52, just after the TAXI command is recorded. The TAXI
        // command at t=52 is applied by Replay; the bug's loop began here.
        engine.Replay(recording, 52);

        var ac = engine.FindAircraft("SKW3404");
        Assert.NotNull(ac);

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);

        double prevHeading = ac.TrueHeading.Degrees;
        double startLat = ac.Position.Lat;
        double minLat = startLat;
        double maxLat = startLat;
        double cumulativeHeadingChange = 0.0;

        output.WriteLine($"t=52: pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F0} ias={ac.IndicatedAirspeed:F1}");
        NearestNodeHelper.Log(output, "t=52", ac, layout, count: 5);

        double? latAt10 = null;
        const int loopSeconds = 15;
        for (int t = 1; t <= loopSeconds; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("SKW3404");
            Assert.NotNull(ac);

            double delta = Math.Abs(GeoMath.SignedBearingDifference(ac.TrueHeading.Degrees, prevHeading));
            cumulativeHeadingChange += delta;
            prevHeading = ac.TrueHeading.Degrees;
            minLat = Math.Min(minLat, ac.Position.Lat);
            maxLat = Math.Max(maxLat, ac.Position.Lat);

            if (t == 10)
            {
                latAt10 = ac.Position.Lat;
            }

            if (t % 3 == 0)
            {
                output.WriteLine(
                    $"t=52+{t}: pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) hdg={ac.TrueHeading.Degrees:F0} "
                        + $"ias={ac.IndicatedAirspeed:F1} cumHdg={cumulativeHeadingChange:F0}"
                );
                NearestNodeHelper.Log(output, $"t=52+{t}", ac, layout, count: 3);
            }
        }

        double northwardExcursionFt = (maxLat - startLat) * GeoMath.FeetPerNm * 60.0;
        double southAt10Ft = latAt10 is { } lat ? (startLat - lat) * GeoMath.FeetPerNm * 60.0 : 0.0;
        output.WriteLine($"northward excursion peak: {northwardExcursionFt:F0}ft, southward progress at t+10: {southAt10Ft:F0}ft");

        // The buggy trajectory drifted ~25 ft north before doubling back; a
        // correct southbound A-taxi never goes north at all from the
        // post-pushback pose.
        Assert.True(
            northwardExcursionFt < 10.0,
            $"SKW3404 drifted {northwardExcursionFt:F0}ft north of its start before recovering — "
                + "after PUSH A FACE E it should taxi straight south on A"
        );

        // 10 seconds in, the aircraft should be well south of its start —
        // ~250 ft with the fix, ~0 ft (or slightly north) with the bug.
        Assert.NotNull(latAt10);
        Assert.True(
            southAt10Ft > 100.0,
            $"SKW3404 had only {southAt10Ft:F0}ft of southward progress after 10s — " + "expected >100ft of southbound taxi on A"
        );
    }
}
