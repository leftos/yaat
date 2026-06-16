using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N85439 (C172, ~96 KIAS near OAK) was given the compound command
/// "RELR 270; DCT OAK30NUM" at t=1332 in recording "S2-OAK-5 (1) | Practical Exam
/// Preparation/Advanced Concepts" (ZOA). RELR 270 from heading ~280°M turns the long way
/// (270° right) to ~190°M, which is correct. When the turn completed (~t=1425) the queued
/// DCT OAK30NUM applied: OAK30NUM bears ~163°T from the aircraft's ~211°T heading — a short
/// ~48° LEFT turn. Instead the aircraft kept turning RIGHT, circling. Re-issuing DCT
/// OAK30NUM and DCT VPMID had no effect; the controller had to break the turn with FH 180
/// before a re-issued direct would take.
///
/// Root cause: <see cref="Yaat.Sim.Commands.FlightCommandHandler"/>.ApplyRightTurn sets
/// <c>Targets.PreferredTurnDirection = Right</c>. ApplyDirectTo installs the direct-to-fix
/// route but never clears that bias, so <see cref="FlightPhysics"/>.ResolveDirection honors
/// the stale Right preference over the shortest path and the aircraft turns the long way
/// around. This is the command-queue sibling of the phase-exit variants fixed in
/// <see cref="Mr270DctWrongDirectionTests"/> and <see cref="L360DctWrongTurnTests"/>; the fix
/// clears the bias in the DCT handlers, mirroring every heading handler (FH, FPH, FORCE).
/// </summary>
public class Relr270DctWrongTurnTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/relr270-dct-wrong-turn-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N85439";

    // DCT OAK30NUM is applied ~t=1425 once the RELR 270 turn completes; by 1426 the
    // direct-to-fix route is active. The controller's FH 180 / re-issued DCT workarounds
    // start at t=1437, so replaying to 1426 lands in the pure-bug window.
    private const int DctAppliedTime = 1426;
    private const int TurnWindowSeconds = 20;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("FlightPhysics", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After RELR 270's turn completes and DCT OAK30NUM applies, the aircraft must turn the
    /// SHORT way to the fix (left, toward ~163°T) — not continue the relative turn's right
    /// bias the long way around. Full replay reaches the post-DCT state, then physics-only
    /// ticking isolates the turn (the recorded FH 180 workaround at t=1448 is not replayed).
    /// </summary>
    [Fact]
    public void N85439_TurnsShortWayToOak30numAfterRelr270()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, DctAppliedTime);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        var fix = ac.Targets.NavigationRoute.FirstOrDefault(n => n.Name == "OAK30NUM");
        Assert.NotNull(fix); // DCT should have installed the direct-to-fix route

        // Secondary: the relative turn's right-turn bias must be cleared once DCT takes over
        // lateral guidance. (On the buggy code it is still Right here.)
        Assert.Null(ac.Targets.PreferredTurnDirection);

        double startHeading = ac.TrueHeading.Degrees;
        double startOffNose = Math.Abs(SignedDelta(GeoMath.BearingTo(ac.Position, fix.Position) - startHeading));
        output.WriteLine(
            $"t={DctAppliedTime}: hdg={startHeading:F1} bearingToFix={GeoMath.BearingTo(ac.Position, fix.Position):F1} "
                + $"offNose={startOffNose:F1} preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"}"
        );

        double maxHeadingDelta = 0; // most-positive signed delta from start = worst right swing
        double endOffNose = startOffNose;

        for (int t = 1; t <= TurnWindowSeconds; t++)
        {
            engine.TickOneSecond(); // physics only — do NOT replay the FH 180 workaround at t=1448
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var liveFix = ac.Targets.NavigationRoute.FirstOrDefault(n => n.Name == "OAK30NUM");
            if (liveFix is null)
            {
                break; // fix sequenced — stop measuring
            }

            double bearing = GeoMath.BearingTo(ac.Position, liveFix.Position);
            endOffNose = Math.Abs(SignedDelta(bearing - ac.TrueHeading.Degrees));
            double signedFromStart = SignedDelta(ac.TrueHeading.Degrees - startHeading);
            maxHeadingDelta = Math.Max(maxHeadingDelta, signedFromStart);

            output.WriteLine(
                $"  t+{t, 2}: hdg={ac.TrueHeading.Degrees, 7:F1} bearing={bearing, 6:F1} offNose={endOffNose, 6:F1} "
                    + $"dFromStart={signedFromStart, 7:F1} preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"}"
            );
        }

        // Primary: the aircraft converged on OAK30NUM (turned toward the fix) instead of
        // diverging by turning right the long way around.
        Assert.True(
            endOffNose < startOffNose,
            $"Off-nose to OAK30NUM should shrink (turn toward the fix) but grew from {startOffNose:F1} to {endOffNose:F1} — wrong-way turn"
        );
        Assert.True(
            endOffNose < 20.0,
            $"Aircraft should be roughly on course to OAK30NUM after {TurnWindowSeconds}s but off-nose was {endOffNose:F1}"
        );

        // The correct turn is to the LEFT (the fix bears left of the post-turn heading); the
        // bug swings RIGHT the long way around. Allow a small snap margin.
        Assert.True(
            maxHeadingDelta < 5.0,
            $"Aircraft turned RIGHT (max heading delta {maxHeadingDelta:F1} from start {startHeading:F1}) instead of the short left turn to OAK30NUM"
        );
    }

    private static double SignedDelta(double deg)
    {
        double d = deg % 360.0;
        if (d > 180.0)
        {
            d -= 360.0;
        }

        if (d < -180.0)
        {
            d += 360.0;
        }

        return d;
    }
}
