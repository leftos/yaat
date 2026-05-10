using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the OAK GA-spawn turn-around bug surfaced from the S2-OAK-4
/// VFR Transitions/Radar Concepts bundle.
///
/// N346G (parking GA3, spawn heading 290°, preset `TAXI C B 28R`) and N172SP
/// (parking GA7, spawn heading 135°, preset `TAXI D C B 28R`) make a wide
/// ~270° clockwise turn instead of the natural short-way turn when leaving
/// parking onto their first taxiway. Snapshot heading samples (every 5 s):
///
///   N346G: 290 -> 209 -> 266 ->  60 -> 126 -> 111 -> 113
///   N172SP: 135 -> 244 -> 260 ->  48 -> 183 -> 164 -> 164
///
/// Both rotations sweep CW the long way through nearly a full revolution.
/// Two suspects surfaced during plan investigation:
///   (a) The recorded `AssignedTaxiRoute` for N346G starts at node 619 (GA1),
///       not 621 (GA3) where the aircraft sits — `GroundCommandHandler.TryTaxi`
///       calls `groundLayout.FindNearestNode(aircraft.Position)` and may resolve
///       to a sibling parking spot.
///   (b) The fillet pass leaves duplicate collinear parking connectors at GA3
///       (#1222 / #1224 both bearing 209.1°) and GA7 (#1396 / #1397 both 243.1°),
///       not collapsed by the dedup step that runs after `phase-d-shorten`.
///
/// This file currently holds the diagnostic test that drives the investigation.
/// Once the diagnostic identifies the exact failure mode, an assertion test
/// is added that fails on `main` and passes after the fix.
/// </summary>
public class OakGaSpawnTurnAroundTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-ga-spawn-turnaround-recording.yaat-bug-report-bundle.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Assertion: neither N346G nor N172SP should accumulate more than 320° of
    /// total heading rotation from spawn through t=30 s, AND signed rotation
    /// must stay within 200° (no near-full-revolution spiral). A natural
    /// taxi-out from these parking spots requires ~150-200° of *signed* turn
    /// (short-way connector + short-way arc onto the taxiway, plus minor
    /// pursuit-control oscillation that bumps the absolute total). The bug
    /// produces ~270-400° of *signed* CW rotation because the pathfinder
    /// picks an arc that lands the aircraft heading opposite the next
    /// segment's direction, forcing a 180° reversal at the arc endpoint.
    ///
    /// This test fails on `main` (the buggy code) and passes once the
    /// pathfinder prefers bridge endpoints whose first target-taxiway arc
    /// is traversed in its natural-forward bezier direction.
    /// </summary>
    [Theory]
    [InlineData("N346G", 320.0, 200.0)]
    [InlineData("N172SP", 320.0, 200.0)]
    public void TaxiOut_DoesNotSpinNearlyFullCircle(string callsign, double maxCumulativeAbsDeg, double maxAbsSignedDeg)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 0);
        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);

        double prevHdg = ac.TrueHeading.Degrees;
        double cumulativeAbs = 0;
        double cumulativeSigned = 0;

        int total = (int)Math.Min(30, recording.TotalElapsedSeconds);
        for (int t = 1; t <= total; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);

            double curHdg = ac.TrueHeading.Degrees;
            double delta = (((curHdg - prevHdg) + 540.0) % 360.0) - 180.0;
            cumulativeAbs += Math.Abs(delta);
            cumulativeSigned += delta;
            prevHdg = curHdg;
        }

        output.WriteLine(
            $"{callsign}: cumulativeAbs={cumulativeAbs:F0}deg cumulativeSigned={cumulativeSigned:F0}deg "
                + $"(max abs {maxCumulativeAbsDeg:F0}, max signed {maxAbsSignedDeg:F0})"
        );
        Assert.True(
            cumulativeAbs <= maxCumulativeAbsDeg,
            $"{callsign} accumulated {cumulativeAbs:F0}deg of rotation in 30s — expected <= {maxCumulativeAbsDeg:F0}deg. "
                + "The 270deg taxi-out spiral compounds CW rotation past a full half-revolution; natural "
                + "pursuit-control taxi out of parking should stay well under this."
        );
        Assert.True(
            Math.Abs(cumulativeSigned) <= maxAbsSignedDeg,
            $"{callsign} accumulated {cumulativeSigned:F0}deg of *signed* rotation in 30s — expected |signed| <= {maxAbsSignedDeg:F0}deg. "
                + "A signed rotation past +/-200deg is the spiral symptom: the aircraft kept turning the same way "
                + "past where a short-way correction would have arrived."
        );
    }
}
