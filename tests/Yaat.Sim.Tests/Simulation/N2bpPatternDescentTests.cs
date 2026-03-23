using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for pattern entry descent bug: N2BP (SR22) was given EF 28L
/// (Enter Final) at ~2000ft, 4nm from the runway. PatternEntryPhase climbed
/// to TPA (1509ft) and navigated to the runway threshold, arriving too high
/// at 0.5nm → "too high at MAP" go-around.
///
/// Root cause: PatternEntryPhase always targeted TPA and navigated to the
/// threshold for final entry. Fix: for final entry, navigate to the glideslope-
/// TPA intercept point on extended centerline, targeting the GS altitude.
///
/// Recording: S2-OAK-1 (2) VFR Takeoff/Landing — N2BP is an SR22 spawned at
/// 4500ft. Commands EF 28R at t=633, ERD 28R at t=749, EF 28L at t=851.
/// </summary>
public class N2bpPatternDescentTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n2bp-pattern-descent-recording.zip";

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

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After EF 28L at t=851, N2BP should descend on final approach and land
    /// (or at least not go around due to being too high at MAP).
    /// Previously, PatternEntryPhase climbed to TPA and navigated to the
    /// threshold, triggering "too high at MAP" go-around.
    /// </summary>
    [Fact]
    public void EnterFinal_DoesNotGoAroundDueToAltitude()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Replay to just after the EF 28L command at t=851
        engine.Replay(recording, 852);

        var aircraft = engine.FindAircraft("N2BP");
        Assert.NotNull(aircraft);
        output.WriteLine($"N2BP at t=852: alt={aircraft.Altitude:F0}, phase={aircraft.Phases?.CurrentPhase?.Name}");

        // Tick forward until landing, go-around, or timeout
        bool landed = false;
        bool goAroundTooHigh = false;
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft("N2BP");
            if (aircraft is null)
            {
                break;
            }

            var phase = aircraft.Phases?.CurrentPhase;
            if (phase?.Name == "Landing")
            {
                landed = true;
                output.WriteLine($"  Landed at t={t}, alt={aircraft.Altitude:F0}");
                break;
            }

            if (phase?.Name == "GoAround")
            {
                output.WriteLine($"  Go-around at t={t}, alt={aircraft.Altitude:F0}");
                goAroundTooHigh = true;
                break;
            }

            if (t % 30 == 0)
            {
                output.WriteLine($"  t={t, 4} alt={aircraft.Altitude, 7:F0} VS={aircraft.VerticalSpeed, 6:F0} phase={phase?.Name ?? "(none)"}");
            }
        }

        Assert.True(
            landed,
            goAroundTooHigh
                ? "Aircraft went around (was too high) — PatternEntryPhase altitude targeting for final entry is broken"
                : "Aircraft never landed or went around within 600 seconds"
        );
    }
}
