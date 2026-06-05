using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #187 (ZHU S3-T1-L7): arrivals on a STAR with a
/// <c>WAIT 5 DVIA</c> preset never descend — "Descend via STAR" fires but is a no-op.
///
/// Root causes: <c>JARR TEJAS 27</c> failed (version-less STAR + runway-as-argument), so
/// no STAR became active; then <c>DVIA</c> gated on an active STAR that was never set and
/// silently failed. The fix makes JARR accept those forms and makes DVIA self-activate the
/// STAR from the filed route and overlay its crossing restrictions.
///
/// Recording: S3-T1-L7 (I90_D_APP). UAL8144 (<c>JARR TEJAS 27 ; WAIT 5 DVIA</c>, spawns t=0
/// at 13000) and UCA4348 (bare <c>WAIT 5 DVIA</c>, no JARR, spawns t=360 at 20000) both sat
/// flat at their spawn altitude for the whole 716 s recording.
/// </summary>
public class Issue187StarDviaTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue187-star-dvia-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    private double RunDescentProbe(SimulationEngine engine, string callsign, int seconds)
    {
        var aircraft = engine.FindAircraft(callsign);
        double minAlt = aircraft!.Altitude;
        for (int t = 1; t <= seconds; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(callsign);
            if (aircraft is null)
            {
                break;
            }

            minAlt = Math.Min(minAlt, aircraft.Altitude);
            if (t % 60 == 0)
            {
                var next = aircraft.Targets.NavigationRoute.Count > 0 ? aircraft.Targets.NavigationRoute[0].Name : "(none)";
                output.WriteLine(
                    $"  {callsign} t=+{t, 3} alt={aircraft.Altitude, 7:F0} tgt={aircraft.Targets.TargetAltitude?.ToString("F0") ?? "null", 7} VS={aircraft.VerticalSpeed, 6:F0} next={next}"
                );
            }
        }

        return minAlt;
    }

    [Fact]
    public void Ual8144_JarrThenDvia_Descends()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 60); // past spawn (t=0) and the WAIT 5 DVIA firing (~t=10)

        var aircraft = engine.FindAircraft("UAL8144");
        Assert.NotNull(aircraft);
        output.WriteLine(
            $"UAL8144 @ t=60: alt={aircraft.Altitude:F0} StarVia={aircraft.Procedure.StarViaMode} ActiveStar={aircraft.Procedure.ActiveStarId}"
        );

        Assert.NotNull(aircraft.Procedure.ActiveStarId);
        Assert.True(aircraft.Procedure.StarViaMode, "DVIA should have engaged STAR-via mode");

        double minAlt = RunDescentProbe(engine, "UAL8144", 300);
        Assert.True(minAlt < 11000, $"UAL8144 should descend via the STAR (it sat flat at 13000), but min alt was {minAlt:F0}");
    }

    [Fact]
    public void Uca4348_BareDviaNoJarr_SelfResolvesAndDescends()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 420); // UCA4348 spawns t=360; WAIT 5 DVIA fires ~t=365

        var aircraft = engine.FindAircraft("UCA4348");
        Assert.NotNull(aircraft);
        output.WriteLine(
            $"UCA4348 @ t=420: alt={aircraft.Altitude:F0} StarVia={aircraft.Procedure.StarViaMode} ActiveStar={aircraft.Procedure.ActiveStarId}"
        );

        // No JARR was ever issued — DVIA must have self-activated the filed STAR.
        Assert.NotNull(aircraft.Procedure.ActiveStarId);
        Assert.True(aircraft.Procedure.StarViaMode, "bare DVIA should self-activate the filed STAR");

        double minAlt = RunDescentProbe(engine, "UCA4348", 250);
        Assert.True(minAlt < 18500, $"UCA4348 should descend via the STAR (it sat flat at 20000), but min alt was {minAlt:F0}");
    }

    [Fact]
    public void Ual8144_OnTejas5ToRwy27_FliesForwardWithoutBacktrack()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        var navDb = TestVnasData.NavigationDb!;
        var iah = navDb.GetFixPosition("IAH") ?? navDb.GetFixPosition("KIAH");
        Assert.NotNull(iah);
        var dest = new LatLon(iah.Value.Lat, iah.Value.Lon);

        engine.Replay(recording, 2);
        var aircraft = engine.FindAircraft("UAL8144");
        Assert.NotNull(aircraft);
        output.WriteLine($"UAL8144 route @ spawn: {string.Join(" -> ", aircraft.Targets.NavigationRoute.Select(f => f.Name))}");

        // In the bug, the (mis-ordered NavData) route sent the aircraft NE to HOWLN then ~180° back
        // through TEJAS, gaining ~10 nm away from the field. With JARR resolving the CIFP RW27
        // transition (TEJAS→RIDLR→…→PRAYY) the path is monotonic, so distance-to-field never grows
        // by more than a small margin past its running minimum.
        double minDist = GeoMath.DistanceNm(aircraft!.Position, dest);
        double maxBacktrack = 0;
        for (int t = 1; t <= 420; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("UAL8144");
            if (aircraft is null)
            {
                break;
            }

            double dist = GeoMath.DistanceNm(aircraft.Position, dest);
            minDist = Math.Min(minDist, dist);
            maxBacktrack = Math.Max(maxBacktrack, dist - minDist);
            if (t % 60 == 0)
            {
                output.WriteLine(
                    $"  t=+{t, 3} pos={aircraft.Position.Lat:F3}/{aircraft.Position.Lon:F3} hdg={aircraft.TrueHeading.Degrees:F0} dist={dist:F1} backtrack={maxBacktrack:F1}"
                );
            }
        }

        Assert.True(
            maxBacktrack < 3.0,
            $"UAL8144 should fly the STAR forward without a backtrack loop, but it flew {maxBacktrack:F1} nm away from the field"
        );
    }
}
