using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N44444 (C172, OAK pattern, ~96 KIAS) was given L360 in recording
/// "S2-OAK-4 | VFR Transitions/Radar Concepts" at t=1616 and stayed on its
/// existing heading instead of executing a left 360 loop.
///
/// Root cause: <see cref="MakeTurnPhase"/> only set TargetTrueHeading once in
/// OnStart (start ± 1°). Once <see cref="FlightPhysics"/> snapped to that
/// goal, PreferredTurnDirection was cleared and the turn stalled at ~1°
/// accumulated. A snapshot at t=1700 (84 s after dispatch) confirmed
/// CumulativeTurn=1.0° and the aircraft parked at start − 1°.
/// </summary>
public class IssueL360StallsTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue-l360-stalls-recording.yaat-bug-report-bundle.zip";
    private const int L360DispatchTime = 1616;
    private const string Callsign = "N44444";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("MakeTurnPhase", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay to one second before the recorded L360, dispatch L360 ourselves,
    /// then tick 130 s and assert the aircraft actually executes the loop.
    /// Multiple checkpoints catch different failure modes:
    ///   - +30 s: cumulative ≥ 60° (broken code stalls at ~1°).
    ///   - +60 s: heading rolled at least 120° to the LEFT of start (catches
    ///     wrong-direction regressions).
    ///   - +130 s: phase complete and heading back near start (loop closed).
    /// Also asserts CumulativeTurn never regresses.
    /// </summary>
    [Fact]
    public void N44444_L360_CompletesFullLoop()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, L360DispatchTime - 1);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        double startHeading = ac.TrueHeading.Degrees;
        output.WriteLine($"t={L360DispatchTime - 1}: {Callsign} hdg={startHeading:F2} alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F0}");

        var dispatch = engine.SendCommand(Callsign, "L360");
        Assert.True(dispatch.Success, $"L360 dispatch should succeed: {dispatch.Message}");
        output.WriteLine($"L360 dispatch: {dispatch.Message}");

        ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        var phase0 = FindMakeTurnPhase(ac);
        Assert.NotNull(phase0);
        Assert.Equal(TurnDirection.Left, phase0.Direction);
        Assert.Equal(360.0, phase0.TargetDegrees);

        double turnAt30 = -1;
        double turnAt60 = -1;
        double headingAt60 = double.NaN;
        bool phaseCompleted = false;
        double finalHeading = double.NaN;
        double finalCumulative = 0;
        double previousCumulative = 0;
        bool monotonic = true;

        for (int t = 1; t <= 130; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var p = FindMakeTurnPhase(ac);
            double cumulative = p is null ? previousCumulative : ((MakeTurnPhaseDto)p.ToSnapshot()).CumulativeTurn;

            if (p is not null && (cumulative + 1e-3 < previousCumulative))
            {
                monotonic = false;
                output.WriteLine($"  t+{t, 3}: cumulative regressed from {previousCumulative:F2} to {cumulative:F2}");
            }
            previousCumulative = cumulative;

            if ((t % 5 == 0) || (t == 130))
            {
                output.WriteLine(
                    $"  t+{t, 3}: hdg={ac.TrueHeading.Degrees, 7:F2} cumulative={cumulative, 6:F1} phase={(p is null ? "(none)" : "MakeTurn")}"
                );
            }

            if (t == 30)
            {
                turnAt30 = cumulative;
            }

            if (t == 60)
            {
                turnAt60 = cumulative;
                headingAt60 = ac.TrueHeading.Degrees;
            }

            if (p is null && !phaseCompleted)
            {
                phaseCompleted = true;
                finalHeading = ac.TrueHeading.Degrees;
                finalCumulative = cumulative;
                output.WriteLine($"  t+{t, 3}: phase complete (cumulative≈{cumulative:F1})");
            }
        }

        if (!phaseCompleted)
        {
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            finalHeading = ac.TrueHeading.Degrees;
            finalCumulative = previousCumulative;
        }

        Assert.True(turnAt30 >= 60, $"At t+30 cumulative turn should be ≥ 60° (got {turnAt30:F1}°) — turn stalled");

        double leftRolled = WrapPositive(startHeading - headingAt60);
        Assert.True(
            leftRolled is >= 120 and <= 300,
            $"At t+60 heading should be 120–300° to the LEFT of start (got {leftRolled:F1}° left of {startHeading:F2}, cumulative={turnAt60:F1})"
        );

        Assert.True(phaseCompleted, $"Phase should complete within 130 s (final cumulative={finalCumulative:F1})");

        double headingDelta = AbsAngleDelta(finalHeading, startHeading);
        Assert.True(
            headingDelta <= 15,
            $"After completion heading should be within 15° of start ({startHeading:F2}); got {finalHeading:F2} (Δ {headingDelta:F1}°)"
        );

        Assert.True(monotonic, "Cumulative turn should never regress");
    }

    private static MakeTurnPhase? FindMakeTurnPhase(AircraftState ac)
    {
        if (ac.Phases is null)
        {
            return null;
        }

        foreach (var p in ac.Phases.Phases)
        {
            if (p is MakeTurnPhase mt && mt.Status == PhaseStatus.Active)
            {
                return mt;
            }
        }

        return null;
    }

    private static double WrapPositive(double deg)
    {
        double v = deg % 360.0;
        if (v < 0)
        {
            v += 360.0;
        }
        return v;
    }

    private static double AbsAngleDelta(double a, double b)
    {
        double d = WrapPositive(a - b);
        return d > 180 ? 360 - d : d;
    }
}
