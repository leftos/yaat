using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for GitHub issue #213: an aircraft taxiing off OAK runway 28R onto taxiway G
/// must not pure-pursuit-orbit the runway-exit hold-short bar.
///
/// Recording: the issue-207 fixture. N655EX (C210) lands on OAK 28R and is given
/// `TAXI G D J` during rollout. Taxiway G has a 32° dogleg at node 360 — an intermediate
/// shape-point the fillet generator deliberately leaves unfilleted (it assumes shape-points
/// are smooth-curve vertices). The route arrives as two consecutive STRAIGHT segments
/// (1132→360 then 360→361) meeting at the sharp kink. At the low corner speed the
/// turn-rate-limited pure-pursuit can't track the kink and the aircraft circles node 361.
///
/// The fix (in <see cref="Yaat.Sim.Phases.Ground.GroundNavigator"/>) extends the
/// entry-alignment slow-turn to fire for such a sharp UNFILLETED kink (a sharp geometric
/// angle between two consecutive straight segments), so the aircraft rounds the corner via a
/// closed-form arc instead of orbiting it. With the test module's ThrowOnOrbit=true, an orbit
/// throws — so this test simply ticks the aircraft through the exit and asserts it taxis clear.
/// </summary>
public class Issue213OakGExitOrbitTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue207-oak-landing-taxi-holdshort-recording.zip";
    private const string Callsign = "N655EX";

    // Node 361 (the 28R hold-short on G, north of the runway centerline) is at ~37.728174 N.
    // Once the aircraft rounds the kink and taxis north onto taxiway D its latitude climbs
    // well past it; pre-fix it orbits node 361 around ~37.72802 N (south of the node) and never
    // gets there (and throws on the orbit invariant first).
    private const double ClearOfKinkLat = 37.7287;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void TaxiOff28RViaG_RoundsKinkWithoutOrbiting()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 2221);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var result = engine.SendCommand(Callsign, "TAXI G D J");
        Assert.True(result.Success, $"TAXI command failed: {result.Message}");

        // Tick forward through the runway-exit kink. With ThrowOnOrbit=true a pure-pursuit
        // orbit at node 361 throws here — the failure mode this test guards against. Use
        // TickOneSecond (not ReplayOneSecond) so the recorded DEL at t=2264 doesn't remove
        // the aircraft mid-observation.
        double maxLat = aircraft.Position.Lat;
        bool movingAtEnd = false;
        for (int t = 1; t <= 90; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            maxLat = Math.Max(maxLat, aircraft.Position.Lat);
            movingAtEnd = aircraft.GroundSpeed > 0.5;
            if (maxLat >= ClearOfKinkLat)
            {
                break;
            }
        }

        Assert.True(
            maxLat >= ClearOfKinkLat,
            $"{Callsign} did not taxi clear of the 28R→G kink (max lat {maxLat:F6} < {ClearOfKinkLat}); it orbited node 361 instead of rounding it"
        );
        Assert.True(movingAtEnd, $"{Callsign} stalled at the kink instead of taxiing through");
    }
}
