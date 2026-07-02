using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #228: "Trying to switch runways on short final really
/// breaks routing logic."
///
/// Recording: S2-OAK-5 (2) "Practical Exam Preparation/Advanced Concepts" (ZOA).
/// N629PU is a VFR piston pattern trainer. It departs runway 33, is switched to a
/// 28L pattern (<c>ELD 28L</c>, works), touch-and-goes, and while established on
/// <see cref="FinalApproachPhase"/> for 28L — on short final (~135 ft, ~0.4 nm,
/// aligned) — is given <c>EF 28L</c> (enter final, same runway) at t=2315.
///
/// Observed bug: <see cref="Yaat.Sim.Commands.PatternCommandHandler.TryEnterPattern"/>
/// fell through to the fixed glideslope-TPA entry point (~3 nm behind the aircraft),
/// inserted a <see cref="PatternEntryPhase"/>, and flew the aircraft outbound while
/// climbing to pattern altitude (~887 ft) — "a tour of the whole airspace" — never
/// rejoining. This violates the documented <c>EF</c> contract ("never routes the
/// aircraft outbound / farther from the field").
///
/// Expected: <c>EF</c> for the runway the aircraft is already established on final
/// for is a graceful no-op — it continues the approach (no PatternEntry reposition,
/// no outbound climb) and completes the touch-and-go.
///
/// Replay strategy: hybrid. The fix is localized to the <c>EF</c> command handler
/// (fires at t=2315), so restoring the recorded pre-EF snapshot at t=2310 and
/// replaying the recorded <c>EF 28L</c> forward with current code faithfully
/// reproduces the moment without ticking from t=0.
/// </summary>
public class Issue228EnterFinalShortFinalTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue228-ef-short-final-recording.zip";
    private const string Callsign = "N629PU";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("PatternCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void EnterFinal_SameRunwayOnShortFinal_ContinuesApproachNoOutboundReentry()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(2310);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int startTime = (int)snapshot.ElapsedSeconds;

            // Sanity: pre-EF the aircraft is established on FinalApproach for 28L.
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<FinalApproachPhase>(pre.Phases?.CurrentPhase);
            Assert.Equal("28L", pre.Phases?.AssignedRunway?.Designator);

            // Apply the recorded EF 28L (t=2315) with current code, advancing a few seconds.
            engine.ReplayRange(startTime, 2320, recording.Actions);

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            // Direct effect of the fix: no PatternEntry reposition is inserted; the
            // aircraft stays on its 28L final rather than being torn down and re-routed.
            Assert.DoesNotContain(ac.Phases!.Phases, static p => p is PatternEntryPhase);
            Assert.Equal("28L", ac.Phases?.AssignedRunway?.Designator);

            // Behavioral: it must descend to the runway (touch-and-go), not climb to
            // pattern altitude and balloon outbound. Track min altitude and min
            // distance to the 28L threshold over the descent window.
            var runway = NavigationDatabase.Instance.GetRunway("OAK", "28L");
            Assert.NotNull(runway);
            var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);

            double minAlt = double.MaxValue;
            double minDist = double.MaxValue;
            double maxAlt = double.MinValue;
            double maxDist = double.MinValue;
            for (int t = 2320; t <= 2360; t++)
            {
                engine.ReplayRange(t, t + 1, recording.Actions);
                var cur = engine.FindAircraft(Callsign);
                if (cur is null)
                {
                    break;
                }

                double dist = GeoMath.DistanceNm(cur.Position, threshold);
                minAlt = Math.Min(minAlt, cur.Altitude);
                maxAlt = Math.Max(maxAlt, cur.Altitude);
                minDist = Math.Min(minDist, dist);
                maxDist = Math.Max(maxDist, dist);
                output.WriteLine(
                    $"t={t} alt={cur.Altitude:F0} dist={dist:F2}nm hdg={cur.TrueHeading.Degrees:F0} phase={cur.Phases?.CurrentPhase?.GetType().Name}"
                );
            }

            output.WriteLine($"minAlt={minAlt:F0} maxAlt={maxAlt:F0} minDist={minDist:F2} maxDist={maxDist:F2}");

            Assert.True(
                minAlt <= 60,
                $"Aircraft never descended to the runway (min alt {minAlt:F0} ft) — it flew the bogus outbound re-entry instead of continuing final."
            );
            Assert.True(
                minDist <= 0.3,
                $"Aircraft never reached the 28L threshold (min dist {minDist:F2} nm) — it toured outbound instead of continuing final."
            );
        }
    }

    /// <summary>
    /// Backstop for the never-route-outbound guarantee: an aligned aircraft on short
    /// final (inside the category minimum final, so the close-in-aligned path can't
    /// engage) that is NOT currently in FinalApproachPhase — so the same-runway
    /// continue no-op doesn't apply — must be rejected with "Unable, short final"
    /// rather than routed to the fixed ~3 nm entry point behind it (the #228
    /// mechanism). The aircraft keeps its current approach: phases untouched, runway
    /// unchanged.
    /// </summary>
    [Fact]
    public void EnterFinal_AlignedShortFinal_NotOnFinalPhase_RejectsRatherThanRoutingOutbound()
    {
        TestVnasData.EnsureInitialized();
        var rwy28L = TestVnasData.NavigationDb?.GetRunway("KOAK", "28L");
        if (rwy28L is null)
        {
            return;
        }

        // 0.4 nm from the threshold, aligned with the 28L final course — short final,
        // inside the piston 1.0 nm minimum. No active phase, so the continue no-op
        // (which keys off FinalApproachPhase) does not apply.
        var (lat, lon) = GeoMath.ProjectPoint(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude, rwy28L.TrueHeading.ToReciprocal(), 0.4);

        var ac = new AircraftState
        {
            Callsign = "N2VFR",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Position = new LatLon(lat, lon),
            TrueHeading = rwy28L.TrueHeading,
            Altitude = rwy28L.ElevationFt + 150,
            IndicatedAirspeed = 62,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = rwy28L },
        };

        var result = PatternCommandHandler.TryEnterPattern(ac, PatternDirection.Left, PatternEntryLeg.Final, "28L", null);

        output.WriteLine($"TryEnterPattern(28L) -> Success={result.Success} Message='{result.Message}'");

        Assert.False(result.Success);
        Assert.Contains("short final", result.Message!, StringComparison.OrdinalIgnoreCase);
        // Never routed outbound: no PatternEntry inserted, runway assignment preserved.
        Assert.DoesNotContain(ac.Phases!.Phases, static p => p is PatternEntryPhase);
        Assert.Equal("28L", ac.Phases.AssignedRunway?.Designator);
    }
}
