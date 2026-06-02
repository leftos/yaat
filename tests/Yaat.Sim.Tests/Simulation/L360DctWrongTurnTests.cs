using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N428KK (C172, ~112 KIAS near OAK) was given L360 (left 360° orbit) at
/// t=1288 then "DCT OAK30NUM" at t=1402 in recording "S2-OAK-5 (1) | Practical Exam
/// Preparation/Advanced Concepts" (ZOA). OAK30NUM bears ~187°T from the aircraft — a
/// short ~50° RIGHT turn. Instead the aircraft turned LEFT, swinging through east,
/// until the controller forced recovery with FHN 180 at t=1420.
///
/// Root cause: <see cref="Yaat.Sim.Phases.Tower.MakeTurnPhase"/> drives
/// <c>Targets.PreferredTurnDirection = Left</c> every tick. When DCT cleared the orbit
/// phase, the stale Left bias survived into the direct-to-fix LNAV —
/// <see cref="FlightPhysics"/>.ResolveDirection honors the bias over the shortest path,
/// so the aircraft turned the long way around. The fix clears the bias in
/// <c>MakeTurnPhase.OnEnd</c> (fires on both force-clear and natural completion),
/// mirroring <c>InitialClimbPhase.OnEnd</c> — the same fix used for the sibling
/// R270→DCT bug in <see cref="Mr270DctWrongDirectionTests"/>.
/// </summary>
public class L360DctWrongTurnTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/l360-dct-wrong-turn-recording.zip";
    private const string Callsign = "N428KK";

    // DCT OAK30NUM is applied at t=1402; by 1405 the orbit phase has been cleared and
    // the direct-to-fix route is active. Replaying here lands just after the handoff.
    private const int DctAppliedTime = 1405;
    private const int TurnWindowSeconds = 18;

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
            .EnableCategory("MakeTurnPhase", LogLevel.Debug)
            .EnableCategory("FlightPhysics", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// After DCT OAK30NUM clears the L360 orbit, the aircraft must turn the SHORT way to
    /// the fix (right, toward ~187°T) — not continue the orbit's left turn through east.
    /// Full replay reaches the post-DCT state, then physics-only ticking isolates the
    /// turn (the recorded FHN 180 workaround at t=1420 is deliberately not replayed).
    /// </summary>
    [Fact]
    public void N428KK_TurnsShortWayToOak30numAfterL360()
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

        // Secondary: the orbit's left-turn bias must be cleared once DCT clears the phase.
        // (On the buggy code it is still Left here.)
        Assert.Null(ac.Targets.PreferredTurnDirection);

        double startHeading = ac.TrueHeading.Degrees;
        double startOffNose = Math.Abs(SignedDelta(GeoMath.BearingTo(ac.Position, fix.Position) - startHeading));
        output.WriteLine(
            $"t={DctAppliedTime}: hdg={startHeading:F1} bearingToFix={GeoMath.BearingTo(ac.Position, fix.Position):F1} "
                + $"offNose={startOffNose:F1} preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"}"
        );

        double minHeadingDelta = 0; // most-negative signed delta from start = worst left swing
        double endOffNose = startOffNose;

        for (int t = 1; t <= TurnWindowSeconds; t++)
        {
            engine.TickOneSecond(); // physics only — do NOT replay the FHN 180 workaround at t=1420
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
            minHeadingDelta = Math.Min(minHeadingDelta, signedFromStart);

            output.WriteLine(
                $"  t+{t, 2}: hdg={ac.TrueHeading.Degrees, 7:F1} bearing={bearing, 6:F1} offNose={endOffNose, 6:F1} "
                    + $"dFromStart={signedFromStart, 7:F1} preferred={ac.Targets.PreferredTurnDirection?.ToString() ?? "(none)"}"
            );
        }

        // Primary: the aircraft converged on OAK30NUM (turned toward the fix) instead of
        // diverging by turning left through east.
        Assert.True(
            endOffNose < startOffNose,
            $"Off-nose to OAK30NUM should shrink (turn toward the fix) but grew from {startOffNose:F1} to {endOffNose:F1} — wrong-way turn"
        );
        Assert.True(
            endOffNose < 20.0,
            $"Aircraft should be roughly on course to OAK30NUM after {TurnWindowSeconds}s but off-nose was {endOffNose:F1}"
        );

        // The correct turn is to the RIGHT (the fix bears right of the post-orbit heading);
        // the bug swings LEFT. Allow a small snap margin.
        Assert.True(
            minHeadingDelta > -5.0,
            $"Aircraft turned LEFT (min heading delta {minHeadingDelta:F1} from start {startHeading:F1}) instead of the short right turn to OAK30NUM"
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
