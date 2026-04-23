using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// End-to-end regression test for issue #142: LineUpPhase must not crawl
/// parallel to the runway when the aircraft arrives at the hold-short with
/// a shallow heading relative to the runway. The real bug was UAL859 at
/// SFO 01R arriving at taxiway A1's hold-short with heading 37.69° (22.7°
/// right of the 15° runway heading) and 324 ft left of centerline; the
/// old closed-form LineUpPlanBuilder produced a 1861 ft nose-out segment
/// at 12 kts, leading to ~90 seconds of crawling along the runway with
/// the aircraft visually "taxiing" instead of taking off.
///
/// <para>
/// The fix classifies this geometry as a pivot case (waste-straight &gt;
/// 20% of remaining runway) and drives the aircraft through a
/// perpendicular-to-centerline pivot instead.
/// </para>
///
/// <para>
/// This test is programmatic (not replay-based): the recorded bundle's
/// <c>engine.Replay</c> diverges from the recorded pose during the final
/// taxi, producing a different heading at hold-short than the real run.
/// Placing the aircraft directly at the recorded pose reliably reproduces
/// the bug condition on current code and is stable under refactors of
/// the upstream TaxiingPhase.
/// </para>
/// </summary>
[Collection("NavDbMutator")]
public class Issue142SfoRwy01rShallowLineupTests(ITestOutputHelper output)
{
    /// <summary>
    /// Drive a <see cref="LineUpPhase"/> through <see cref="FlightPhysics"/>
    /// ticks with the UAL859 pose + SFO 01R runway. Assert the phase
    /// completes within a 60-second budget and ends on-centerline aligned.
    /// </summary>
    [Fact]
    public void Ual859SfoRwy01rShallowPose_CompletesLineupWithinBudget()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return;
        }

        var runway = TestVnasData.NavigationDb.GetRunway("KSFO", "01R");
        if (runway is null)
        {
            output.WriteLine("SKIP: KSFO 01R not in navdata");
            return;
        }

        var groundData = new TestAirportGroundData();
        var sfoLayout = groundData.GetLayout("SFO");
        if (sfoLayout is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return;
        }

        // Recorded UAL859 pose at the moment LineUpPhase would activate per
        // the issue #142 bug bundle manifest.
        var aircraft = new AircraftState
        {
            Callsign = "UAL859",
            AircraftType = "B738",
            Latitude = 37.60687,
            Longitude = -122.38195,
            TrueHeading = new TrueHeading(37.69),
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = sfoLayout,
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhase();
        phase.OnStart(ctx);

        // LUAW mode (no next phase) — phase brakes to 0 at stop point.
        Assert.False(phase.RollingMode);
        output.WriteLine(
            $"[OnStart] CurrentState={phase.CurrentState} turn={runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading):F2}°"
        );

        const int budgetTicks = 60 * 4; // 60 s @ 0.25 s/tick
        bool completed = false;
        int completionTick = -1;
        for (int i = 0; i < budgetTicks; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            if (phase.OnTick(ctx))
            {
                completed = true;
                completionTick = i + 1;
                break;
            }
        }

        double signedCrossNm = GeoMath.SignedCrossTrackDistanceNm(
            aircraft.Latitude,
            aircraft.Longitude,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading
        );
        double crossFt = Math.Abs(signedCrossNm) * GeoMath.FeetPerNm;
        double hdgDiff = Math.Abs(runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading));

        output.WriteLine(
            $"[end] completed={completed} tick={completionTick} "
                + $"cross={crossFt:F2}ft hdgDiff={hdgDiff:F2}° ias={aircraft.IndicatedAirspeed:F2}kt "
                + $"state={phase.CurrentState}"
        );

        Assert.True(
            completed,
            $"LineUpPhase did not complete within {budgetTicks * 0.25:F0} s "
                + $"(cross={crossFt:F2}ft hdgDiff={hdgDiff:F2}° ias={aircraft.IndicatedAirspeed:F2}kt state={phase.CurrentState})"
        );
        Assert.True(crossFt < 5.0, $"final cross-track {crossFt:F2}ft exceeds 5 ft tolerance");
        Assert.True(hdgDiff < 2.0, $"final heading-diff {hdgDiff:F2}° exceeds 2° tolerance");
        Assert.True(aircraft.IndicatedAirspeed < 2.0, $"LUAW mode should brake to ~0, got {aircraft.IndicatedAirspeed:F2}kt");
    }

    /// <summary>
    /// Diagnostic: record UAL859's full trajectory through the pivot-fallback
    /// lineup as a per-tick CSV for visual inspection with Yaat.LayoutInspector.
    /// Writes <c>.tmp/issue142-ual859-lineup.csv</c>. Render with:
    /// <code>
    /// dotnet run --project tools/Yaat.LayoutInspector -- \
    ///     tests/Yaat.Sim.Tests/TestData/sfo.geojson \
    ///     --ticks .tmp/issue142-ual859-lineup.csv \
    ///     --html .tmp/issue142-ual859-lineup.html \
    ///     --html-runway 01R
    /// </code>
    /// </summary>
    [Fact]
    public void Diagnostic_RecordTicksForUal859PivotLineup()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return;
        }

        var runway = TestVnasData.NavigationDb.GetRunway("KSFO", "01R");
        if (runway is null)
        {
            output.WriteLine("SKIP: KSFO 01R not in navdata");
            return;
        }

        var groundData = new TestAirportGroundData();
        var sfoLayout = groundData.GetLayout("SFO");
        if (sfoLayout is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return;
        }

        var aircraft = new AircraftState
        {
            Callsign = "UAL859",
            AircraftType = "B738",
            Latitude = 37.60687,
            Longitude = -122.38195,
            TrueHeading = new TrueHeading(37.69),
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = sfoLayout,
            Logger = NullLogger.Instance,
        };

        var phase = new LineUpPhase();
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.AssignedRunway = runway;
        phase.OnStart(ctx);

        var recorder = new TickRecorder(aircraft);
        recorder.Record(0);

        const int budgetTicks = 60 * 4; // 60 s @ 0.25 s/tick
        int secondsElapsed = 0;
        for (int i = 0; i < budgetTicks; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            bool done = phase.OnTick(ctx);
            // Record every tick (4 Hz) — resolution lets LI visualise the
            // SlowTurn arcs cleanly.
            secondsElapsed++;
            recorder.Record(secondsElapsed);
            if (done)
            {
                output.WriteLine($"[diag] phase complete at subTick {i + 1} ({(i + 1) * ctx.DeltaSeconds:F2}s)");
                break;
            }
        }

        string repoRoot = TickRecorder.FindRepoRoot();
        string outPath = Path.Combine(repoRoot, ".tmp", "issue142-ual859-lineup.csv");
        recorder.WriteCsv(outPath);
        output.WriteLine($"[diag] wrote {recorder.Count} ticks to {outPath}");
    }
}
