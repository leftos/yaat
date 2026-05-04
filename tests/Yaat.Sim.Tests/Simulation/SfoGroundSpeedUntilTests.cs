namespace Yaat.Sim.Tests.Simulation;

using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

/// <summary>
/// E2E tests for runway speed-limiting bug: aircraft rolling on the runway
/// should not be slowed to a crawl by the ground conflict detector when the
/// potential conflict is stationary and fully past the hold short line.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — WJA1508 lands 28R, SKW3398
/// already exited via T and is holding past the hold short line. WJA1508
/// correctly skips exit T as occupied, but decelerates to ~5 kts due to
/// closing-proximity conflict with the stationary SKW3398.
/// </summary>
public class SfoGroundSpeedUntilTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/5d33df162626.zip";

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
        SimLog.InitializeForTest(loggerFactory);

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// WJA1508 on 28R should not be slowed to a crawl by stationary SKW3398
    /// which has already exited the runway and is holding past the hold short line.
    /// During the "Runway Exit" phase while still on the centerline, the minimum
    /// ground speed should stay well above the 5 kts conflict-trail speed.
    /// </summary>
    [Fact]
    public void WJA1508_NotSlowedByStationarySKW3398PastHoldShort()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=430 — WJA1508 is in Landing phase at ~70 kts, SKW3398 is
        // stationary in "Holding After Exit" ~416 ft away.
        engine.Replay(recording, 430);

        var wja = engine.FindAircraft("WJA1508");
        var skw = engine.FindAircraft("SKW3398");
        Assert.NotNull(wja);
        Assert.NotNull(skw);
        Assert.Equal("Holding After Exit", skw.Phases?.CurrentPhase?.Name);

        // Tick forward through the passing zone. WJA1508 should NOT crawl at 5 kts.
        // The minimum acceptable speed on the runway while passing a stationary
        // off-runway aircraft is the normal rollout coast speed (~15 kts for this
        // category), not the 5 kts conflict-trail speed.
        double minObservedGs = wja.GroundSpeed;
        for (int t = 1; t <= 30; t++)
        {
            engine.ReplayOneSecond();
            wja = engine.FindAircraft("WJA1508");
            Assert.NotNull(wja);

            string phase = wja.Phases?.CurrentPhase?.Name ?? "";
            if (phase == "Landing")
            {
                if (wja.GroundSpeed < minObservedGs)
                {
                    minObservedGs = wja.GroundSpeed;
                }
            }

            if (phase is "Landing" or "Runway Exit")
            {
                output.WriteLine($"t={430 + t} phase={phase} gs={wja.GroundSpeed:F1} spdLimit={wja.Ground.SpeedLimit?.ToString("F1") ?? "null"}");
            }
        }

        // The aircraft should never be limited to the 5 kts slow-taxi speed
        // while on the runway. 10 kts is a generous floor — real rollout coast
        // speeds are 15+ kts.
        Assert.True(
            minObservedGs > 10.0,
            $"WJA1508 ground speed dropped to {minObservedGs:F1} kts on the runway — "
                + "ground conflict detector should not limit runway aircraft for stationary off-runway traffic"
        );
    }

    /// <summary>
    /// WJA1508 lands 28R with no explicit exit preference. It should take a
    /// standard exit (D, L, P, or N) rather than skipping all of them and
    /// falling through to the high-speed exit Q. Before the fix, the default
    /// exit path never planned braking below coast speed, so the aircraft
    /// arrived at standard exits too close for the handoff buffer.
    /// </summary>
    [Fact]
    public void WJA1508_TakesStandardExitWithDefaultSelection()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 420);

        string? exitTaxiway = null;
        for (int t = 1; t <= 200; t++)
        {
            engine.ReplayOneSecond();
            var wja = engine.FindAircraft("WJA1508");
            if (wja is null)
            {
                break;
            }

            string phase = wja.Phases?.CurrentPhase?.Name ?? "";
            if (t % 10 == 0)
            {
                output.WriteLine($"t={420 + t} phase={phase} gs={wja.GroundSpeed:F1}");
            }

            if (phase == "Holding After Exit")
            {
                exitTaxiway = wja.Ground.CurrentTaxiway;
                output.WriteLine($"t={420 + t} WJA1508 exited on taxiway {exitTaxiway}");
                break;
            }
        }

        Assert.NotNull(exitTaxiway);
        Assert.NotEqual("Q", exitTaxiway);
    }

    [Fact]
    public void Diagnostic_LogRunwayRollout()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to a point where both aircraft should exist
        engine.Replay(recording, 300);

        var wja = engine.FindAircraft("WJA1508");
        var skw = engine.FindAircraft("SKW3398");
        output.WriteLine($"t=300: WJA1508={wja is not null} type={wja?.AircraftType}, SKW3398={skw is not null} type={skw?.AircraftType}");

        if (wja is null || skw is null)
        {
            // Try scanning for all aircraft
            output.WriteLine("Scanning for aircraft...");
            foreach (var ac in engine.World.GetSnapshot())
            {
                output.WriteLine($"  {ac.Callsign} phase={ac.Phases?.CurrentPhase?.Name} gs={ac.GroundSpeed:F1} onGround={ac.IsOnGround}");
            }

            // Try replaying further
            for (int t = 1; t <= 600; t++)
            {
                engine.ReplayOneSecond();
                if (t % 30 == 0)
                {
                    var snapshot = engine.World.GetSnapshot();
                    output.WriteLine($"t={300 + t}: aircraft count={snapshot.Count}");
                    foreach (var ac in snapshot)
                    {
                        if (ac.Callsign.Contains("WJA") || ac.Callsign.Contains("SKW"))
                        {
                            output.WriteLine(
                                $"  {ac.Callsign} phase={ac.Phases?.CurrentPhase?.Name} gs={ac.GroundSpeed:F1} alt={ac.Altitude:F0} onGround={ac.IsOnGround}"
                            );
                        }
                    }
                }
            }

            return;
        }

        // Log state over time
        var layout = new TestAirportGroundData().GetLayout("SFO");
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            wja = engine.FindAircraft("WJA1508");
            skw = engine.FindAircraft("SKW3398");
            if (wja is null)
            {
                break;
            }

            if (t % 5 == 0)
            {
                double dist = GeoMath.DistanceNm(wja.Position.Lat, wja.Position.Lon, skw?.Position.Lat ?? 0, skw?.Position.Lon ?? 0) * 6076.12;
                string spdLimit = wja.Ground.SpeedLimit?.ToString("F1") ?? "null";
                string skwPhase = skw?.Phases?.CurrentPhase?.Name ?? "gone";
                string skwGs = skw?.GroundSpeed.ToString("F1") ?? "?";
                output.WriteLine(
                    $"t={300 + t} WJA1508: phase={wja.Phases?.CurrentPhase?.Name} gs={wja.GroundSpeed:F1} "
                        + $"spdLimit={spdLimit} hdg={wja.TrueHeading.Degrees:F0} "
                        + $"| SKW3398: phase={skwPhase} gs={skwGs} "
                        + $"| dist={dist:F0}ft"
                );

                if (layout is not null)
                {
                    NearestNodeHelper.Log(output, $"  WJA:", wja, layout);
                }
            }
        }
    }
}
