using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the SFO intersection departure that took ~82 s from clearance to takeoff roll.
///
/// Recording: S1-SFO Ground Control — N346G (BE36, piston) holding short of 28R at taxiway E,
/// cleared for takeoff at t=228.
///
/// Two defects combined. The graph lineup route dead-ended: at a runway-CROSSING taxiway the walk
/// toward the runway stepped through a junction fillet arc onto a 28R centerline node and stranded
/// itself, so <see cref="LineUpGraphRoute.TryPlan"/> returned null and <see cref="LineUpPhase"/>
/// fell back to the synthetic pivot — a 282 ft nose-out flown at the piston arc speed of 4.4 kt.
/// And the taxi-in braked to a near-stop at the runway bar although the takeoff clearance was
/// already stored, so the line-up re-accelerated from 2 kt at the piston taxi rate.
///
/// Two later changes cut the remainder. The synthetic line-up now cruises its nose-out straight at
/// <see cref="CategoryPerformance.TaxiSpeed"/> and brakes onto the arc speed by the arc entry, so the
/// arc's turn-rate limit no longer governs the straights either side of it (this recording routes
/// through the graph path and does not exercise that fallback, but every pose the graph declines
/// does). And the piston/helicopter taxi acceleration rate went to the jet's 1.0 kt/s — the constant
/// does breakaway work, and a light single out-accelerates a loaded transport at breakaway.
///
/// The recording contains the operator's own correction (a <c>DEL</c> at t=277), so this test
/// replays only to t=229 — one second past the CTO — and then advances with
/// <see cref="SimulationEngine.TickOneSecond"/>, which applies no further recorded actions
/// (docs/e2e-tdd-issue-debugging.md §5c).
/// </summary>
public class SfoCtoIntersectionDepartureE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-cto-intersection-departure-recording.yaat-bug-report-bundle.zip";

    private const string Callsign = "N346G";

    /// <summary>One second past the recorded <c>CTO</c> at t=228.</summary>
    private const int CtoReplaySeconds = 229;

    /// <summary>
    /// Seconds from the clearance within which the aircraft must be rolling. Measured: 38 s (taxi to the bar
    /// 23 s, line-up 15 s), so this keeps ~15% headroom over the profile
    /// <see cref="Diagnostic_LogCtoToTakeoffProfile"/> prints.
    /// </summary>
    private const int TakeoffDeadlineSeconds = 44;

    /// <summary>Node 835 — the 28R hold-short bar on taxiway E that N346G was stopped at.</summary>
    private const double HoldShortLat = 37.622192;
    private const double HoldShortLon = -122.375736;

    /// <summary>Consecutive seconds below <see cref="MovingIasKts"/> that count as parked.</summary>
    private const int StationaryTickLimit = 5;

    private const double MovingIasKts = 0.5;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>Distance (ft) of a position from the 28R centerline.</summary>
    private static double CrossFt(double lat, double lon, RunwayInfo runway) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(lat, lon, runway.ThresholdLatitude, runway.ThresholdLongitude, runway.TrueHeading))
        * GeoMath.FeetPerNm;

    /// <summary>
    /// The takeoff roll must begin within <see cref="TakeoffDeadlineSeconds"/> of the clearance, and once the
    /// aircraft is past the 28R holding position it must never sit still: a stop on the runway side of the bar
    /// is a runway occupancy the clearance already ruled out.
    /// </summary>
    [Fact]
    public void N346G_ClearedForTakeoffAtTaxiwayE_StartsRollingWithoutStopping()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        RunwayInfo? runway = TestVnasData.NavigationDb!.GetRunway("KSFO", "28R");
        if (runway is null)
        {
            output.WriteLine("SKIP: KSFO 28R not in navdata");
            return;
        }

        engine.Replay(recording, CtoReplaySeconds);

        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        output.WriteLine(
            $"[t={CtoReplaySeconds}] phase={aircraft.Phases?.CurrentPhase?.Name ?? "(none)"} ias={aircraft.IndicatedAirspeed:F2}kt "
                + $"pos=({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) cross={CrossFt(aircraft.Position.Lat, aircraft.Position.Lon, runway):F1}ft"
        );

        double barCrossFt = CrossFt(HoldShortLat, HoldShortLon, runway);

        int stationaryRun = 0;
        for (int t = 1; t <= TakeoffDeadlineSeconds; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            double crossFt = CrossFt(aircraft.Position.Lat, aircraft.Position.Lon, runway);
            bool pastBar = crossFt < barCrossFt - 5.0;

            if (pastBar && aircraft.IndicatedAirspeed < MovingIasKts)
            {
                stationaryRun++;
                Assert.True(
                    stationaryRun < StationaryTickLimit,
                    $"{Callsign} sat still for {stationaryRun}s at t=+{t}s past the 28R holding position "
                        + $"(cross={crossFt:F1}ft vs bar {barCrossFt:F1}ft, phase={aircraft.Phases?.CurrentPhase?.Name ?? "(none)"}, "
                        + $"ias={aircraft.IndicatedAirspeed:F2}kt) — it is cleared for takeoff, it must not park on the runway side of the bar"
                );
            }
            else
            {
                stationaryRun = 0;
            }

            if (aircraft.Phases?.CurrentPhase is TakeoffPhase)
            {
                output.WriteLine($"{Callsign} reached TakeoffPhase {t}s after the clearance (ias={aircraft.IndicatedAirspeed:F2}kt)");
                return;
            }
        }

        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.Fail(
            $"{Callsign} was not rolling {TakeoffDeadlineSeconds}s after the takeoff clearance: "
                + $"phase={aircraft.Phases?.CurrentPhase?.Name ?? "(none)"} ias={aircraft.IndicatedAirspeed:F2}kt "
                + $"pos=({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) "
                + $"cross={CrossFt(aircraft.Position.Lat, aircraft.Position.Lon, runway):F1}ft from the 28R centerline"
        );
    }

    /// <summary>
    /// Per-second profile from the clearance onward: phase, speed, commanded speed, position and the three
    /// nearest ground-graph nodes. Run this to re-measure the clearance-to-roll time whenever the lineup or
    /// the taxi speed plan changes.
    /// </summary>
    [Fact]
    public void Diagnostic_LogCtoToTakeoffProfile()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        RunwayInfo? runway = TestVnasData.NavigationDb!.GetRunway("KSFO", "28R");
        if (layout is null || runway is null)
        {
            output.WriteLine("SKIP: SFO layout or KSFO 28R not available");
            return;
        }

        engine.Replay(recording, CtoReplaySeconds);

        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        string previousPhase = aircraft.Phases?.CurrentPhase?.Name ?? "(none)";
        int takeoffAt = -1;

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                output.WriteLine($"t=+{t}s: {Callsign} is gone");
                break;
            }

            string phase = aircraft.Phases?.CurrentPhase?.Name ?? "(none)";
            output.WriteLine(
                $"t=+{t}s phase={phase} ias={aircraft.IndicatedAirspeed:F2}kt tgt={aircraft.Targets.TargetSpeed:F2}kt "
                    + $"pos=({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) hdg={aircraft.TrueHeading.Degrees:F1}° "
                    + $"cross={CrossFt(aircraft.Position.Lat, aircraft.Position.Lon, runway):F1}ft"
            );
            NearestNodeHelper.Log(output, $"t=+{t}s:", aircraft, layout);

            if (phase != previousPhase)
            {
                output.WriteLine($"  >>> phase {previousPhase} -> {phase} at t=+{t}s");
                previousPhase = phase;
            }

            if ((takeoffAt < 0) && (aircraft.Phases?.CurrentPhase is TakeoffPhase))
            {
                takeoffAt = t;
            }
        }

        output.WriteLine(takeoffAt > 0 ? $"=== TakeoffPhase entered {takeoffAt}s after the clearance ===" : "=== never reached TakeoffPhase ===");
    }
}
