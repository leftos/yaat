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
            Position = new LatLon(37.60687, -122.38195),
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
        output.WriteLine($"[OnStart] CurrentState={phase.CurrentState} turn={runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading):F2}°");

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
            aircraft.Position.Lat,
            aircraft.Position.Lon,
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
            Position = new LatLon(37.60687, -122.38195),
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

    /// <summary>
    /// Diagnostic (upstream follow-up): replay the recorded scenario through
    /// the current engine and capture UAL859's pose during the taxi →
    /// hold-short transition. The bundle shows UAL859 spawned at (37.606822,
    /// -122.382064) heading 104° true, then TAXI A1 1R fires as a preset.
    /// Within ~5 s the aircraft reaches HoldingShort but at heading 37.69°
    /// (near-parallel to runway) instead of ~117° (perpendicular-to-runway
    /// along A1). This diagnostic captures the per-second taxi trace to
    /// pinpoint where the rotation goes wrong.
    ///
    /// Writes <c>.tmp/issue142-ual859-taxi.csv</c>. Render with:
    /// <code>
    /// dotnet run --project tools/Yaat.LayoutInspector -- \
    ///     tests/Yaat.Sim.Tests/TestData/sfo.geojson \
    ///     --ticks .tmp/issue142-ual859-taxi.csv \
    ///     --html .tmp/issue142-ual859-taxi.html \
    ///     --html-runway 01R
    /// </code>
    /// </summary>
    [Fact]
    public void Diagnostic_TraceUal859TaxiApproachToHoldShort()
    {
        var recording = Helpers.RecordingLoader.Load("TestData/issue142-sfo-rwy01r-shallow-recording.zip");
        if (recording is null)
        {
            output.WriteLine("SKIP: recording not available");
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("TaxiIngressResolver", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("GroundCommandHandler", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("TaxiingPhase", Microsoft.Extensions.Logging.LogLevel.Debug)
            .EnableCategory("GroundNavigator", Microsoft.Extensions.Logging.LogLevel.Debug)
            .InitializeSimLog();

        var groundData = new TestAirportGroundData();
        var engine = new Yaat.Sim.Simulation.SimulationEngine(groundData);
        engine.Replay(recording, 0);

        TickRecorder? recorder = null;

        // Run all the way through: spawn → taxi → hold-short → CTO (t=46) →
        // LineUp (pivot fallback in current code) → Takeoff → airborne.
        // Stop when the aircraft is no longer OnGround (takeoff complete) or
        // at a generous budget.
        const int maxSeconds = 120;
        int lastT = 0;
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("UAL859");
            if (ac is null)
            {
                continue;
            }

            recorder ??= new TickRecorder(ac);
            recorder.Record(t);
            lastT = t;

            bool logThis = t <= 10 || t >= 45 || (t % 5 == 0);
            if (logThis)
            {
                output.WriteLine(
                    $"[t={t}] phase={ac.Phases?.CurrentPhase?.Name ?? "(none)"} "
                        + $"lat={ac.Position.Lat:F6} lon={ac.Position.Lon:F6} hdg={ac.TrueHeading.Degrees:F2}° "
                        + $"ias={ac.IndicatedAirspeed:F2}kt alt={ac.Altitude:F0}ft twy={ac.CurrentTaxiway ?? "-"}"
                );
            }

            if (!ac.IsOnGround && ac.IndicatedAirspeed > 100)
            {
                output.WriteLine($"[t={t}] UAL859 airborne — stopping trace");
                break;
            }
        }

        if (recorder is not null)
        {
            string outPath = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "issue142-ual859-fullchain.csv");
            recorder.WriteCsv(outPath);
            output.WriteLine($"[diag] wrote {recorder.Count} ticks ({lastT}s) to {outPath}");
        }
    }
}
