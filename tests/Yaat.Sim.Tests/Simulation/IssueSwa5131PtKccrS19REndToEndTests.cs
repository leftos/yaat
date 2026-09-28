using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E recording replay for SWA5131 on the SOFIA TWO VOR/DME RWY 19R approach at KCCR.
///
/// At t=1601 the controller issued CAPP S19R; SWA5131 is south of CCR (37.88, -122.28)
/// heading ~041° at ~6000 ft. Published procedure requires a course reversal at CCR:
/// outbound on the reciprocal of the CF CCR 190.6° inbound leg, climb to ≥2600, 45°/180° procedure turn
/// (PT heading 055.6° magnetic), back over CCR inbound, then CF RW19R 171.7° to the runway. Every course is
/// referenced to the CCR VOR, whose station declination is E017: inbound 207.6°T, outbound 027.6°T,
/// PT heading 072.6°T, final 188.7°T.
///
/// Guards the aircraft routing to FAWNE and skipping the course reversal because PI legs were dropped, the PT's
/// inbound stage flying a parallel offset east of the course, the PT inbound course being taken from the
/// final approach course and the live declination, and the FAF (CCR) being trimmed out after the PT.
///
/// Recording: tests/Yaat.Sim.Tests/TestData/b143fc615682.zip
/// </summary>
public class IssueSwa5131PtKccrS19REndToEndTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/b143fc615682.zip";

    // CF CCR 190.6°M, PI 055.6°M and CF RW19R 171.7°M, each converted with CCR's station declination (E017).
    private const double ExpectedInboundTrueDeg = 207.6;
    private const double ExpectedOutboundTrueDeg = 27.6;
    private const double ExpectedPtHeadingTrueDeg = 72.6;
    private const double ExpectedFinalTrueDeg = 188.7;

    // The PT hands off within 5° or 0.3 nm of the inbound course, so the aircraft overflies CCR, the FAF.
    private const double CcrOverflightNm = 0.3;

    // Once established inbound after the PT the aircraft descends via the common-route step-downs (AIM 5-4-9):
    // HUKVI at or above 1,500 ft, then CCR (the FAF) at or above 1,100 ft; never held at the COLLI transition's
    // 4,000 ft CCR crossing.
    private const double HukviMinAltitudeFt = 1500;
    private const double TransitionCcrAltitudeFt = 4000;
    private const double CcrFafAltitudeFt = 1100;
    private const double CcrFafAltitudeToleranceFt = 200;

    // Distance past CCR at which the course flown from CCR toward the runway is measured.
    private const double FinalTrackMeasureNm = 2.5;

    // The band an approach phase must hold the inbound course line through the CCR VOR for SustainedOnCourseSeconds.
    private const double InboundCrossTrackToleranceNm = 1.0;

    private const double ConvergedCrossTrackNm = 0.35;

    private const int SustainedOnCourseSeconds = 60;

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
            .EnableCategory("ApproachCommandHandler", LogLevel.Debug)
            .EnableCategory("ProcedureTurnPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Swa5131_PtAtCcr_FlysOutboundClimbsAndInterceptsInbound()
    {
        SessionRecording? recording = LoadRecording();
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Recording or NavData not available, skipping");
            return;
        }

        NavigationDatabase navDb = TestVnasData.NavigationDb!;
        (double Lat, double Lon)? ccrPos = navDb.GetFixPosition("CCR");
        Assert.NotNull(ccrPos);
        var ccr = new LatLon(ccrPos.Value.Lat, ccrPos.Value.Lon);

        // Replay through the CAPP S19R command at t=1601 (and the DCT CCR before it at t=1589).
        // Start ticking from t=1602.
        // The original session had DCT-fix validation OFF, but recordings made before the
        // ValidateDctFixes setting was persisted (see SimControlService.SetValidateDctFixes)
        // do not carry that toggle, so we override it explicitly here.
        engine.ReplayWithScenarioOverride(recording, 1602, scenario => scenario.ValidateDctFixes = false);

        AircraftState? aircraft = engine.FindAircraft("SWA5131");
        Assert.NotNull(aircraft);

        // Confirm CAPP wired the PT phase rather than the implied-PTAC InterceptCoursePhase.
        Assert.NotNull(aircraft.Phases);
        ProcedureTurnPhase pt = Assert.Single(aircraft.Phases.Phases.OfType<ProcedureTurnPhase>());
        Assert.True(
            GeoMath.DistanceNm(new LatLon(pt.FixLat, pt.FixLon), ccr) < 0.05,
            $"PT anchor ({pt.FixLat:F5},{pt.FixLon:F5}) is not the CCR VOR"
        );
        output.WriteLine($"PT: inbound={pt.InboundCourseDeg:F1}T ptOut={pt.PtOutboundCourseDeg:F1}T aircraftDecl={aircraft.Declination:F2}");

        Assert.InRange(pt.InboundCourseDeg, ExpectedInboundTrueDeg - 1.0, ExpectedInboundTrueDeg + 1.0);
        ApproachClearance clearance = Assert.IsType<ApproachClearance>(aircraft.Phases.ActiveApproach);
        Assert.InRange(clearance.FinalApproachCourse.Degrees, ExpectedFinalTrueDeg - 0.1, ExpectedFinalTrueDeg + 0.1);
        Assert.InRange(pt.PtOutboundCourseDeg, ExpectedPtHeadingTrueDeg - 2.0, ExpectedPtHeadingTrueDeg + 2.0);
        Assert.InRange(AbsAngleDiff(pt.PtOutboundCourseDeg, pt.InboundCourseDeg + 180.0), 43.0, 47.0);

        // Drive the simulation, not the recording: the instructor's later DCT CCR (t=1839) and bare CAPP (t=1849)
        // reacted to the old, wrong PT geometry and would cancel the procedure turn under replay.
        (double Lat, double Lon)? hukviPos = navDb.GetFixPosition("HUKVI");
        Assert.NotNull(hukviPos);

        var track = new PtTrack(pt, new LatLon(hukviPos.Value.Lat, hukviPos.Value.Lon), output);
        for (int t = 1603; (t <= 2900) && !track.Done; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft("SWA5131");
            if (aircraft is null)
            {
                break;
            }

            track.Observe(t, aircraft);
        }

        track.AssertFlown();
    }

    private static bool IsApproachPhase(Phase? phase) => phase is ApproachNavigationPhase or InterceptCoursePhase or FinalApproachPhase;

    private static double AbsAngleDiff(double a, double b) => Math.Abs(((((a - b) % 360.0) + 540.0) % 360.0) - 180.0);

    /// <summary>Per-tick observations of the procedure turn and what follows it.</summary>
    private sealed class PtTrack(ProcedureTurnPhase pt, LatLon hukvi, ITestOutputHelper output)
    {
        private readonly LatLon _ptFix = new(pt.FixLat, pt.FixLon);
        private readonly TrueHeading _inboundCourse = new(pt.InboundCourseDeg);
        private readonly ProcedureTurnInbound _inbound = pt.InboundJoin;
        private readonly double _hukviToCcrNm = pt.InboundJoin.DistanceToAnchorNm(hukvi);

        private double? _altAtHukvi;
        private double? _altAtCcr;
        private CifpFixRole? _ccrFixRole;

        private int _tCrossedCcr = -1;
        private bool _wentOutbound;
        private double _minAltDuringPt = double.MaxValue;
        private double? _ptOutboundLastHeading;
        private double? _rolloutHeading;
        private int _tLeftPt = -1;
        private string _phaseAfterPt = "(none)";
        private int _secondsOnCourse;
        private double _minAbsXteAfterPt = double.MaxValue;
        private bool _establishedInbound;
        private double _minDistCcrAfterPt = double.MaxValue;
        private double? _finalTrackDeg;

        public bool Done => _establishedInbound && (_finalTrackDeg is not null);

        public void Observe(int t, AircraftState aircraft)
        {
            double distFromCcrNm = GeoMath.DistanceNm(aircraft.Position, _ptFix);
            double xteNm = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, _ptFix, _inboundCourse);
            double hdg = aircraft.TrueHeading.Degrees;
            Phase? activePhase = aircraft.Phases?.CurrentPhase;
            var ptState = (ProcedureTurnPhase.PtState)((ProcedureTurnPhaseDto)pt.ToSnapshot()).State;

            if ((_tCrossedCcr < 0) && (distFromCcrNm <= 1.5))
            {
                _tCrossedCcr = t;
                output.WriteLine($"t={t}: crossed CCR (dist={distFromCcrNm:F2} nm)");
            }

            if (t % 10 == 0)
            {
                output.WriteLine(
                    $"t={t} pos=({aircraft.Position.Lat:F4},{aircraft.Position.Lon:F4}) hdg={hdg:F0} alt={aircraft.Altitude:F0} "
                        + $"dist={distFromCcrNm:F2} xte={xteNm:F2} ptState={ptState} phase={activePhase?.GetType().Name ?? "(none)"}"
                );
            }

            if (_tCrossedCcr < 0)
            {
                return;
            }

            // Read from the phase's own state, which also catches a turn-back that completes the phase in the same tick.
            if ((ptState == ProcedureTurnPhase.PtState.InterceptInbound) && (_rolloutHeading is null))
            {
                _rolloutHeading = hdg;
                output.WriteLine($"t={t}: turn-back rolled out, intercepting (hdg={hdg:F1})");
            }

            if (activePhase is ProcedureTurnPhase)
            {
                ObserveDuringPt(t, aircraft, hdg, ptState);
                return;
            }

            if (_wentOutbound && (_tLeftPt < 0) && (t > _tCrossedCcr + 60))
            {
                _tLeftPt = t;
                _phaseAfterPt = activePhase?.GetType().Name ?? "(none)";
                output.WriteLine($"t={t}: left ProcedureTurnPhase into {_phaseAfterPt} (xte={xteNm:F2} nm, hdg={hdg:F0})");
            }

            if (_tLeftPt > 0)
            {
                ObserveAfterPt(t, aircraft, activePhase, xteNm, distFromCcrNm);
            }
        }

        private void ObserveDuringPt(int t, AircraftState aircraft, double hdg, ProcedureTurnPhase.PtState ptState)
        {
            _minAltDuringPt = Math.Min(_minAltDuringPt, aircraft.Altitude);
            if (!_wentOutbound && (AbsAngleDiff(hdg, ExpectedOutboundTrueDeg) <= 5.0))
            {
                _wentOutbound = true;
                output.WriteLine($"t={t}: established outbound (hdg={hdg:F1})");
            }

            if (ptState == ProcedureTurnPhase.PtState.PtOutbound)
            {
                _ptOutboundLastHeading = hdg;
            }
        }

        private void ObserveAfterPt(int t, AircraftState aircraft, Phase? activePhase, double xteNm, double distFromCcrNm)
        {
            _minDistCcrAfterPt = Math.Min(_minDistCcrAfterPt, distFromCcrNm);
            if (!_establishedInbound)
            {
                _minAbsXteAfterPt = Math.Min(_minAbsXteAfterPt, Math.Abs(xteNm));
                bool onCourse = (Math.Abs(xteNm) <= InboundCrossTrackToleranceNm) && IsApproachPhase(activePhase);
                _secondsOnCourse = onCourse ? _secondsOnCourse + 1 : 0;
                _establishedInbound = _secondsOnCourse >= SustainedOnCourseSeconds;
            }

            ObserveStepDowns(t, aircraft, activePhase);

            // Past CCR (it came within a mile of it and is now heading away): the course flown from the VOR.
            bool pastCcr = (_minDistCcrAfterPt <= 1.0) && (distFromCcrNm >= FinalTrackMeasureNm);
            if (pastCcr && (_finalTrackDeg is null))
            {
                _finalTrackDeg = GeoMath.BearingTo(_ptFix, aircraft.Position);
                output.WriteLine($"t={t}: {distFromCcrNm:F2} nm past CCR on {_finalTrackDeg:F1}T (closest {_minDistCcrAfterPt:F2} nm)");
            }
        }

        /// <summary>The altitudes crossing HUKVI and CCR along the inbound course, and the role of the CCR fix navigated to.</summary>
        private void ObserveStepDowns(int t, AircraftState aircraft, Phase? activePhase)
        {
            if (activePhase is ApproachNavigationPhase nav && (_ccrFixRole is null))
            {
                _ccrFixRole = nav.Fixes.FirstOrDefault(f => f.Name == pt.FixName)?.Role;
                output.WriteLine($"t={t}: approach navigation after the PT: [{string.Join(", ", nav.Fixes.Select(f => $"{f.Name}/{f.Role}"))}]");
            }

            double toCcrNm = _inbound.DistanceToAnchorNm(aircraft.Position);
            if ((_altAtHukvi is null) && (toCcrNm <= _hukviToCcrNm))
            {
                _altAtHukvi = aircraft.Altitude;
                output.WriteLine($"t={t}: crossed HUKVI at {aircraft.Altitude:F0} ft");
            }

            if ((_altAtCcr is null) && (toCcrNm <= 0.0))
            {
                _altAtCcr = aircraft.Altitude;
                output.WriteLine($"t={t}: crossed CCR at {aircraft.Altitude:F0} ft");
            }
        }

        public void AssertFlown()
        {
            Assert.True(_tCrossedCcr > 0, "SWA5131 never crossed within 1.5 nm of CCR");
            Assert.True(_wentOutbound, $"SWA5131 never went outbound on {ExpectedOutboundTrueDeg}T ± 5");
            Assert.True(_minAltDuringPt >= 2500, $"Aircraft descended below 2500 ft during PT (min={_minAltDuringPt:F0})");
            Assert.NotNull(_ptOutboundLastHeading);
            Assert.InRange(AbsAngleDiff(_ptOutboundLastHeading.Value, ExpectedPtHeadingTrueDeg), 0.0, 2.0);
            // The turn back is a true 180° off the PT heading; only then does the aircraft cut in to the inbound course.
            Assert.NotNull(_rolloutHeading);
            Assert.InRange(AbsAngleDiff(_rolloutHeading.Value, pt.PtOutboundCourseDeg + 180.0), 0.0, 5.0);
            Assert.True(_tLeftPt > 0, "SWA5131 never left ProcedureTurnPhase after going outbound");
            Assert.True(
                _establishedInbound,
                $"SWA5131 never held the inbound course within {InboundCrossTrackToleranceNm} nm for {SustainedOnCourseSeconds}s after the PT "
                    + $"(left PT at t={_tLeftPt} into {_phaseAfterPt}, closest {_minAbsXteAfterPt:F2} nm)"
            );
            Assert.True(
                _minAbsXteAfterPt <= ConvergedCrossTrackNm,
                $"SWA5131 never converged on the inbound course after the PT: closest {_minAbsXteAfterPt:F2} nm, limit {ConvergedCrossTrackNm} nm"
            );
            Assert.NotNull(_altAtHukvi);
            Assert.InRange(_altAtHukvi.Value, HukviMinAltitudeFt, TransitionCcrAltitudeFt - 1.0);
            Assert.NotNull(_altAtCcr);
            Assert.InRange(_altAtCcr.Value, CcrFafAltitudeFt - CcrFafAltitudeToleranceFt, CcrFafAltitudeFt + CcrFafAltitudeToleranceFt);
            Assert.Equal(CifpFixRole.FAF, _ccrFixRole);
            Assert.True(
                _minDistCcrAfterPt <= CcrOverflightNm,
                $"SWA5131 did not overfly CCR inbound after the PT: closest {_minDistCcrAfterPt:F2} nm, expected <= {CcrOverflightNm} nm"
            );
            // Crossing CCR at the FAF altitude, the aircraft reaches the offset-alignment ramp (MAP altitude + 300 ft AGL)
            // right after CCR, so the track flown past CCR is the visual alignment with the runway, not the final
            // approach course; the course FinalApproachPhase steers is asserted on the clearance instead.
            Assert.NotNull(_finalTrackDeg);
        }
    }
}
