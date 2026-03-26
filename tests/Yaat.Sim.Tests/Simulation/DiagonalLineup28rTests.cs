using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for diagonal runway lineup bug: aircraft holding short of RWY 28R
/// on taxiway B at OAK would immediately turn toward the on-runway graph node
/// when given CTO, creating a diagonal line across the runway surface instead
/// of first turning perpendicular to the centerline, crossing, then aligning.
///
/// Root cause: LineUpPhase Stage 1 called NavigateToTarget() which steered the
/// aircraft toward the on-runway node. When that node was offset along the
/// runway from the hold-short position, the aircraft cut diagonally.
///
/// Fix: Added Stage 0 — aircraft first turns perpendicular to the runway
/// centerline, then crosses straight ahead to the on-runway node, then
/// corrects to centerline and aligns with the runway heading.
///
/// Recording: S2-OAK-4 VFR Transitions/Radar Concepts — OAK tower scenario.
/// N436MS starts at hold-short of 28R on taxiway B, gets CTO at t=47.
/// </summary>
public class DiagonalLineup28rTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/diagonal-lineup-28r-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// N436MS gets CTO at t=47. Trace its trajectory tick-by-tick through the
    /// LineUpPhase and verify it turns perpendicular to the centerline first,
    /// crosses onto the runway, then turns to align with 292°.
    /// </summary>
    [Fact]
    public void N436MS_LineUp28R_PerpendicularThenAligns()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        // Replay to t=45 — N436MS is holding short of 28R on B, CTO hasn't been sent yet
        engine.Replay(recording, 45);

        var ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);
        Assert.True(
            ac.Phases?.CurrentPhase is HoldingShortPhase,
            $"Expected HoldingShortPhase at t=45 but got {ac.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}"
        );

        double holdShortHeading = ac.TrueHeading.Degrees;
        output.WriteLine($"t=45: holding short — pos=({ac.Latitude:F6}, {ac.Longitude:F6}), hdg={holdShortHeading:F1}");

        // Replay to t=48 — CTO is at t=47, so by t=48 the LineUpPhase should have started
        engine.ReplayOneSecond(); // t=46
        engine.ReplayOneSecond(); // t=47 — CTO applied
        engine.ReplayOneSecond(); // t=48

        ac = engine.FindAircraft("N436MS");
        Assert.NotNull(ac);

        var rwyHeading = 292.0;
        output.WriteLine("");
        output.WriteLine($"Runway heading: {rwyHeading:F1}");
        output.WriteLine($"Hold-short heading: {holdShortHeading:F1}");
        output.WriteLine("");
        output.WriteLine("--- Tick-by-tick trajectory through LineUpPhase ---");
        output.WriteLine($"{"t",4} {"Phase",-22} {"Lat",11} {"Lon",12} {"Hdg",7} {"GS",5} {"CrossTrack",11} {"AlongTrack",11}");

        // The perpendicular heading toward the centerline from the south side of
        // 28R is approximately rwyHeading - 90 = 202°.
        double perpHeading = rwyHeading - 90;
        if (perpHeading < 0)
        {
            perpHeading += 360;
        }

        output.WriteLine($"Expected perpendicular heading: ~{perpHeading:F0}");
        output.WriteLine("");

        bool reachedPerp = false;
        int perpTick = 0;
        int crossingTicks = 0;
        double maxCrossingHeadingDrift = 0;
        bool completed = false;

        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);

            string phaseName = ac.Phases?.CurrentPhase?.Name ?? "(none)";
            double crossTrackFt = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    ac.Latitude,
                    ac.Longitude,
                    37.72152,
                    -122.20065,
                    new TrueHeading(rwyHeading)
                )
            ) * GeoMath.FeetPerNm;
            double alongTrackFt = GeoMath.AlongTrackDistanceNm(
                ac.Latitude,
                ac.Longitude,
                37.72152,
                -122.20065,
                new TrueHeading(rwyHeading)
            ) * GeoMath.FeetPerNm;

            double perpDiff = Math.Abs(NormalizeAngle(ac.TrueHeading.Degrees - perpHeading));

            output.WriteLine(
                $"{t,4} {phaseName,-22} {ac.Latitude,11:F6} {ac.Longitude,12:F6} {ac.TrueHeading.Degrees,7:F1} "
                    + $"{ac.GroundSpeed,5:F1} {crossTrackFt,8:F0}ft {alongTrackFt,8:F0}ft  perpDiff={perpDiff:F1}"
            );

            // Detect when aircraft reaches perpendicular heading (within 5°)
            if (!reachedPerp && (perpDiff < 5))
            {
                reachedPerp = true;
                perpTick = t;
                output.WriteLine($"  >>> Reached perpendicular heading at t={t}");
            }

            // While perpendicular and cross-track still large, track heading stability
            if (reachedPerp && (crossTrackFt > 200) && (perpDiff < 15))
            {
                crossingTicks++;
                maxCrossingHeadingDrift = Math.Max(maxCrossingHeadingDrift, perpDiff);
            }

            if (ac.Phases?.CurrentPhase is LinedUpAndWaitingPhase or TakeoffPhase)
            {
                completed = true;
                output.WriteLine($"  >>> Lineup complete at t={t}");
                break;
            }
        }

        output.WriteLine("");
        output.WriteLine($"Reached perpendicular at tick: {perpTick}");
        output.WriteLine($"Crossing ticks (perp heading, cross-track > 200ft): {crossingTicks}");
        output.WriteLine($"Max heading drift during crossing: {maxCrossingHeadingDrift:F1} deg");

        // Aircraft must turn perpendicular before crossing the runway
        Assert.True(reachedPerp, "Aircraft never turned perpendicular to the centerline");

        // Aircraft must maintain roughly perpendicular heading while crossing
        // (at least 2 ticks of straight crossing before turning to align)
        Assert.True(
            crossingTicks >= 2,
            $"Aircraft only crossed perpendicular for {crossingTicks} ticks — expected straight crossing"
        );

        Assert.True(completed, "Aircraft never completed lineup within 30 seconds");
    }

    /// <summary>
    /// N342T gets CTO at t=688, also from taxiway B hold-short of 28R.
    /// Verify the same perpendicular entry behavior for a second aircraft.
    /// </summary>
    [Fact]
    public void N342T_LineUp28R_PerpendicularThenAligns()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("SKIP: recording or navdata not available");
            return;
        }

        // Replay to t=686 — N342T is holding short, CTO at t=688
        engine.Replay(recording, 686);

        var ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);
        Assert.True(
            ac.Phases?.CurrentPhase is HoldingShortPhase,
            $"Expected HoldingShortPhase at t=686 but got {ac.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}"
        );

        double holdShortHeading = ac.TrueHeading.Degrees;
        output.WriteLine($"t=686: holding short — pos=({ac.Latitude:F6}, {ac.Longitude:F6}), hdg={holdShortHeading:F1}");

        // Replay through CTO at t=688
        engine.ReplayOneSecond(); // t=687
        engine.ReplayOneSecond(); // t=688 — CTO applied
        engine.ReplayOneSecond(); // t=689

        ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);

        double rwyHeading = 292.0;
        double perpHeading = rwyHeading - 90;
        if (perpHeading < 0)
        {
            perpHeading += 360;
        }

        output.WriteLine($"Perpendicular heading: ~{perpHeading:F0}");
        output.WriteLine("");
        output.WriteLine($"{"t",4} {"Phase",-22} {"Hdg",7} {"GS",5} {"CrossTrack",11} {"AlongTrack",11}");

        bool reachedPerp = false;
        int crossingTicks = 0;
        bool completed = false;

        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("N342T");
            Assert.NotNull(ac);

            string phaseName = ac.Phases?.CurrentPhase?.Name ?? "(none)";
            double crossTrackFt = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    ac.Latitude,
                    ac.Longitude,
                    37.72152,
                    -122.20065,
                    new TrueHeading(rwyHeading)
                )
            ) * GeoMath.FeetPerNm;
            double alongTrackFt = GeoMath.AlongTrackDistanceNm(
                ac.Latitude,
                ac.Longitude,
                37.72152,
                -122.20065,
                new TrueHeading(rwyHeading)
            ) * GeoMath.FeetPerNm;

            double perpDiff = Math.Abs(NormalizeAngle(ac.TrueHeading.Degrees - perpHeading));

            output.WriteLine(
                $"{t,4} {phaseName,-22} {ac.TrueHeading.Degrees,7:F1} {ac.GroundSpeed,5:F1} "
                    + $"{crossTrackFt,8:F0}ft {alongTrackFt,8:F0}ft  perpDiff={perpDiff:F1}"
            );

            if (!reachedPerp && (perpDiff < 5))
            {
                reachedPerp = true;
                output.WriteLine($"  >>> Reached perpendicular heading at t={t}");
            }

            if (reachedPerp && (crossTrackFt > 200) && (perpDiff < 15))
            {
                crossingTicks++;
            }

            if (ac.Phases?.CurrentPhase is LinedUpAndWaitingPhase or TakeoffPhase)
            {
                completed = true;
                output.WriteLine($"  >>> Lineup complete at t={t}");
                break;
            }
        }

        output.WriteLine("");
        output.WriteLine($"Crossing ticks (perp heading, cross-track > 200ft): {crossingTicks}");

        Assert.True(reachedPerp, "Aircraft never turned perpendicular to the centerline");
        Assert.True(crossingTicks >= 2, $"Only {crossingTicks} perpendicular crossing ticks");
        Assert.True(completed, "Aircraft never completed lineup within 30 seconds");
    }

    private static double NormalizeAngle(double deg)
    {
        deg = ((deg % 360) + 360) % 360;
        return deg > 180 ? deg - 360 : deg;
    }
}
