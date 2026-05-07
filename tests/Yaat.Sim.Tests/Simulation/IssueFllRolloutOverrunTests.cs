using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for FLL 10R rollout overrun. NKS461 (A320) lands 10R, decelerates to
/// coast speed (40 kts) cleanly, then hands off to RunwayExitPhase past the last
/// hold-short on 10R and continues coasting at 40 kts off the east end of the
/// runway. The bug: LandingPhase's exit selector marks every nearby 90° exit
/// "unable" because the comfort-braking check fails for them, then never picks
/// the available 45° high-speed exit (J9) that *would* be comfortably reachable.
/// By the time the aircraft is at coast speed, every exit is already behind it.
///
/// Bundle: S1: S1L3 (KFLL East). NKS461 touchdown at t=205, handoff at t=260,
/// recording cut at t=328 with the aircraft still rolling.
/// </summary>
public class IssueFllRolloutOverrunTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/94c6ed9ab1d4.zip";
    private const string Callsign = "NKS461";

    // 10R ends per the bundle's AssignedRunway DTO at t=205:
    //   Threshold (10R end): (26.06588663888889, -80.1583488888889), heading 90.378°
    //   Far end (28L end):   (26.065742166666666, -80.133983)
    //   Length: 8000 ft / 1.317 nm
    private const double ThresholdLat = 26.06588663888889;
    private const double ThresholdLon = -80.1583488888889;
    private const double RunwayHeadingDeg = 90.37819158741917;
    private const double RunwayLengthNm = 1.317;

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
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("LandingPhase", LogLevel.Debug)
            .EnableCategory("RunwayExitPhase", LogLevel.Debug)
            .EnableCategory("AirportGroundLayout", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// NKS461 must NOT pass the runway end while still in Landing or RunwayExit
    /// phase. A clean rollout exits onto a taxiway before the threshold; the bug
    /// has the aircraft sit at 40 kts past the runway end with no taxi route.
    /// </summary>
    [Fact]
    public void Nks461_DoesNotOverrunRunwayEnd()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 200);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);

        bool sawLanding = false;
        double maxAlongTrackOnRunway = 0;
        string? overrunPhase = null;
        double overrunAlongTrackNm = 0;
        bool exitedRunway = false;
        int? exitTime = null;
        string? finalPhase = null;
        double finalIas = 0;

        for (int t = 201; t <= (int)recording.TotalElapsedSeconds; t++)
        {
            engine.ReplayOneSecond();
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            string phaseName = ac.Phases?.CurrentPhase?.Name ?? "(none)";
            finalPhase = phaseName;
            finalIas = ac.IndicatedAirspeed;
            if (phaseName == "Landing")
            {
                sawLanding = true;
            }

            double alongTrackNm = GeoMath.AlongTrackDistanceNm(
                ac.Position,
                new LatLon(ThresholdLat, ThresholdLon),
                new TrueHeading(RunwayHeadingDeg)
            );

            if (phaseName is "Landing" or "Runway Exit")
            {
                if (alongTrackNm > maxAlongTrackOnRunway)
                {
                    maxAlongTrackOnRunway = alongTrackNm;
                }

                if ((alongTrackNm > RunwayLengthNm) && (overrunPhase is null))
                {
                    overrunPhase = phaseName;
                    overrunAlongTrackNm = alongTrackNm;
                }
            }
            else if (sawLanding && !exitedRunway)
            {
                exitedRunway = true;
                exitTime = t;
                output.WriteLine(
                    $"t={t}: NKS461 transitioned past RunwayExit, phase={phaseName}, "
                        + $"alongTrack={alongTrackNm * 6076.12:F0}ft "
                        + $"(runway={RunwayLengthNm * 6076.12:F0}ft), ias={ac.IndicatedAirspeed:F1}kts"
                );
            }
        }

        output.WriteLine(
            $"max along-track while in Landing/RunwayExit: {maxAlongTrackOnRunway * 6076.12:F0}ft "
                + $"of {RunwayLengthNm * 6076.12:F0}ft runway, finalPhase={finalPhase}, finalIAS={finalIas:F1}"
        );
        if (overrunPhase is not null)
        {
            output.WriteLine($"overrun: phase={overrunPhase}, alongTrack={overrunAlongTrackNm * 6076.12:F0}ft");
        }

        Assert.True(sawLanding, "NKS461 never entered Landing phase — replay setup is broken");
        Assert.True(
            overrunPhase is null,
            $"NKS461 passed the runway end while still in {overrunPhase} phase at "
                + $"{overrunAlongTrackNm * 6076.12:F0}ft (runway is {RunwayLengthNm * 6076.12:F0}ft). "
                + "Either LandingPhase must commit to a reachable exit during rollout, "
                + "or RunwayExitPhase must brake to a stop when no exit is found."
        );
        Assert.True(
            exitedRunway,
            $"NKS461 never transitioned out of Landing/RunwayExit phase — final phase={finalPhase}, "
                + $"last seen at {maxAlongTrackOnRunway * 6076.12:F0}ft along the runway."
        );
    }

    /// <summary>
    /// Diagnostic: log NKS461 phase, IAS, along-track, candidate/resolved exit,
    /// and nearest ground nodes per second from t=200 through end of recording.
    /// </summary>
    [Fact]
    public void Diagnostic_LogRolloutAndExitBehavior()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("FLL");

        engine.Replay(recording, 200);

        for (int t = 201; t <= (int)recording.TotalElapsedSeconds; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                output.WriteLine($"t={t}: aircraft deleted");
                break;
            }

            string phase = ac.Phases?.CurrentPhase?.Name ?? "(none)";
            string reqExit = ac.Phases?.RequestedExit is { } req ? $"{req.Taxiway ?? "any"}/{req.Side?.ToString() ?? "any"}" : "none";
            string resolved = ac.Phases?.ResolvedExit is { } re ? $"{re.TaxiwayName} HS#{re.HoldShortNode.Id}" : "none";
            int routeSegs = ac.Ground?.AssignedTaxiRoute?.Segments?.Count ?? 0;

            double alongTrackNm = GeoMath.AlongTrackDistanceNm(
                ac.Position,
                new LatLon(ThresholdLat, ThresholdLon),
                new TrueHeading(RunwayHeadingDeg)
            );

            output.WriteLine(
                $"t={t} phase={phase} ias={ac.IndicatedAirspeed:F1} hdg={ac.TrueHeading.Degrees:F1} "
                    + $"alongTrack={alongTrackNm * 6076.12:F0}ft "
                    + $"pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) "
                    + $"reqExit=[{reqExit}] resolved=[{resolved}] taxiSegs={routeSegs}"
            );

            if (layout is not null && ac.IsOnGround)
            {
                NearestNodeHelper.Log(output, $"  t={t}", ac, layout);
            }
        }
    }
}
