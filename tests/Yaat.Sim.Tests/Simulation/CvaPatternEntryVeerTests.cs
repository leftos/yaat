using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for CVA pattern entry veer bug: N3212L at OAK was given
/// ERD 28R, CVA 28R, CLAND and veered northeast away from the field
/// instead of landing. Root cause: CVA's >90° path builds a pattern
/// circuit starting from DownwindPhase without a PatternEntryPhase,
/// causing rapid phase cascading when the aircraft is far from the
/// actual downwind track.
///
/// Recording: S2-OAK-1 VFR Takeoff/Landing — N3212L (C150).
/// </summary>
public class CvaPatternEntryVeerTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/cva-pattern-entry-veer-recording.zip";

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

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: replay to just before CVA 28R (t=2394), then tick forward
    /// logging heading, altitude, phase, and nav targets each second to trace
    /// the veer behavior.
    /// </summary>
    [Fact]
    public void Diagnostic_CvaPatternEntry_TickByTick()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to just before CVA 28R at t=2394
        engine.Replay(recording, 2390);

        var ac = engine.FindAircraft("N3212L");
        if (ac is null)
        {
            output.WriteLine("N3212L not found at t=2390");
            return;
        }

        output.WriteLine($"=== N3212L before CVA 28R (t=2390) ===");
        output.WriteLine($"  pos=({ac.Latitude:F4},{ac.Longitude:F4}) hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0}");
        output.WriteLine($"  phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        for (int t = 1; t <= 120; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N3212L");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: (deleted)");
                break;
            }

            string phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
            string tgtHdg = ac.Targets.TargetTrueHeading is { } th ? $"{th.Degrees:F0}" : "nav";

            output.WriteLine(
                $"t+{t, 3}: pos=({ac.Latitude:F4},{ac.Longitude:F4}) "
                    + $"hdg={ac.TrueHeading.Degrees:F0} tgtHdg={tgtHdg} alt={ac.Altitude:F0} gs={ac.GroundSpeed:F0} "
                    + $"phase={phaseName}"
            );
        }
    }

    /// <summary>
    /// After ERD 28R + CVA 28R + CLAND, the aircraft must not veer away from
    /// the field. It should either be heading toward the runway or navigating
    /// toward the pattern, and getting closer to the airport over time.
    /// </summary>
    [Fact]
    public void CvaAfterErd_AircraftDoesNotVeerAway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past ERD (t=2320), CVA (t=2394), CLAND (t=2400)
        engine.Replay(recording, 2405);

        var ac = engine.FindAircraft("N3212L");
        Assert.NotNull(ac);

        // OAK runway 28R threshold is approximately at this position
        double rwyLat = 37.7213;
        double rwyLon = -122.2207;

        double initialDist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, rwyLat, rwyLon);
        output.WriteLine($"t=2405: dist to rwy={initialDist:F2}nm hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0}");
        output.WriteLine($"  phase={ac.Phases?.CurrentPhase?.GetType().Name}");

        // Tick 60 seconds — aircraft should be getting closer or at least
        // not flying further away
        for (int t = 1; t <= 60; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft("N3212L");
            if (ac is null)
            {
                break;
            }
        }

        Assert.NotNull(ac);
        double finalDist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, rwyLat, rwyLon);
        output.WriteLine($"t=2465: dist to rwy={finalDist:F2}nm hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0}");

        // The aircraft should be getting closer to the runway, not further away.
        // Allow small tolerance for pattern entry maneuvers.
        Assert.True(
            finalDist < initialDist + 0.5,
            $"Aircraft should be getting closer to the runway (or holding pattern distance), "
                + $"but went from {initialDist:F2}nm to {finalDist:F2}nm away"
        );

        // The heading should not be pointing away from the airport (northeastish = ~20-60°)
        double bearingToRwy = GeoMath.BearingTo(ac.Latitude, ac.Longitude, rwyLat, rwyLon);
        double headingDiffFromRwy = Math.Abs(GeoMath.SignedBearingDifference(ac.TrueHeading.Degrees, bearingToRwy));
        output.WriteLine($"  bearing to rwy={bearingToRwy:F0} heading diff={headingDiffFromRwy:F0}");

        Assert.True(
            headingDiffFromRwy < 120,
            $"Aircraft heading {ac.TrueHeading.Degrees:F0} is {headingDiffFromRwy:F0}° away from the "
                + $"runway bearing {bearingToRwy:F0} — likely veering away"
        );
    }
}
