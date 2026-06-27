using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the N152SP / N513SJ "turn off runway as soon as airborne" bug.
///
/// Recording: S2-OAK-4 | VFR Transitions/Radar Concepts — N152SP (C172, IFR)
/// at OAK 28R receives <c>CTO MRC 020</c>, which parses to a 90° right turn
/// (runway + 90°). Pre-fix behavior: aircraft turns immediately at Vr at
/// ~100 ft AGL, settling on 022° true within seconds. Same pattern with
/// N513SJ given <c>CTO 360 020</c> — turn starts at ~250 ft AGL.
///
/// Two-part fix:
///   1. IFR aircraft must reject VFR-only CTO modifiers (MRC, ML*, OC,
///      DCT, MLT, MRT, …). Only bare CTO (follow SID), runway heading (RH),
///      or a numeric heading vector is valid for IFR.
///   2. For the IFR cases that remain (bare CTO with RV SID, or
///      FlyHeadingDeparture), defer the heading change in
///      <c>InitialClimbPhase</c> until the aircraft is ≥ 400 ft above field
///      elevation (TERPS criterion — no lateral past-DER requirement for IFR;
///      that is VFR-pattern-only, AIM 4-3-2; see issue #195).
/// </summary>
public class N152spIfrCtoDeferralTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/n152sp-ifr-cto-vfr-modifier-recording.yaat-bug-report-bundle.zip";
    private const double FieldElevation = 9.0;
    private const double IfrTurnAglFloor = 400.0;

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
            .EnableCategory("InitialClimbPhase", LogLevel.Debug)
            .EnableCategory("TakeoffPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    // -------------------------------------------------------------------
    // Part 1: IFR dispatch must reject VFR-only CTO modifiers
    // -------------------------------------------------------------------

    /// <summary>
    /// CTO MRC on an IFR aircraft must be rejected. Pre-fix the command was
    /// accepted and the aircraft began a 90° right turn at Vr; after the fix
    /// the dispatcher returns a rejection naming the IFR restriction.
    /// </summary>
    [Fact]
    public void CtoMrc_RejectsForIfrAircraft()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // t=767 is just before the recorded "CTO MRC 020" action at t=768.
        // The aircraft is in HoldingShortPhase (rebuilt at t=745).
        engine.Replay(recording, 767);

        var n152sp = engine.FindAircraft("N152SP");
        Assert.NotNull(n152sp);
        Assert.False(n152sp.FlightPlan.IsVfr, "Recording fixture invariant: N152SP filed IFR.");

        var result = engine.SendCommand("N152SP", "CTO MRC 020");

        output.WriteLine($"CTO MRC 020 result: success={result.Success} message={result.Message}");

        Assert.False(result.Success, "CTO MRC must be rejected for IFR aircraft.");
        Assert.NotNull(result.Message);
        Assert.Contains("IFR", result.Message);
    }

    /// <summary>
    /// CTO with VFR-only modifiers (ML90, OC, MLT, MRT) must all be rejected
    /// for IFR. Mirrors the rule that IFR departures accept only bare CTO
    /// (follow SID), runway heading (RH), or a numeric heading.
    /// </summary>
    [Theory]
    [InlineData("CTO ML90 020")]
    [InlineData("CTO OC 020")]
    [InlineData("CTO MLT 020")]
    [InlineData("CTO MRT 020")]
    public void CtoVfrModifier_RejectsForIfrAircraft(string command)
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 767);

        var n152sp = engine.FindAircraft("N152SP");
        Assert.NotNull(n152sp);

        var result = engine.SendCommand("N152SP", command);

        output.WriteLine($"{command} result: success={result.Success} message={result.Message}");

        Assert.False(result.Success, $"{command} must be rejected for IFR aircraft.");
        Assert.NotNull(result.Message);
        Assert.Contains("IFR", result.Message);
    }

    /// <summary>
    /// Bare CTO with only an altitude must still be accepted for IFR — the
    /// aircraft follows the filed SID (or runway heading if no SID resolves).
    /// </summary>
    [Fact]
    public void BareCto_AcceptedForIfrAircraft()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 767);
        var n152sp = engine.FindAircraft("N152SP");
        Assert.NotNull(n152sp);

        var result = engine.SendCommand("N152SP", "CTO 020");

        output.WriteLine($"CTO 020 result: success={result.Success} message={result.Message}");

        Assert.True(result.Success, $"CTO with altitude only must be accepted for IFR: {result.Message}");
    }

    /// <summary>
    /// CTO with a numeric heading (FlyHeading) is the one turning departure
    /// IFR aircraft can still receive. Must be accepted; deferral behavior
    /// is exercised separately by <see cref="Ifr_InitialClimb_FlyHeading_DefersTurnUntil400Agl_IgnoringDerPosition"/>.
    /// </summary>
    [Fact]
    public void Cto360_AcceptedForIfrAircraft()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 767);
        var n152sp = engine.FindAircraft("N152SP");
        Assert.NotNull(n152sp);

        var result = engine.SendCommand("N152SP", "CTO 360 020");

        output.WriteLine($"CTO 360 020 result: success={result.Success} message={result.Message}");

        Assert.True(result.Success, $"CTO with numeric heading must be accepted for IFR: {result.Message}");
    }

    /// <summary>
    /// CTO RH (fly runway heading) must be accepted for IFR aircraft — runway
    /// heading is routinely issued to IFR departures (issue #221). The aircraft
    /// holds runway heading and awaits vectors; no SID is loaded.
    /// </summary>
    [Fact]
    public void CtoRunwayHeading_AcceptedForIfrAircraft()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 767);
        var n152sp = engine.FindAircraft("N152SP");
        Assert.NotNull(n152sp);
        Assert.False(n152sp.FlightPlan.IsVfr, "Recording fixture invariant: N152SP filed IFR.");

        var result = engine.SendCommand("N152SP", "CTO RH 020");

        output.WriteLine($"CTO RH 020 result: success={result.Success} message={result.Message}");

        Assert.True(result.Success, $"CTO RH (runway heading) must be accepted for IFR: {result.Message}");
    }

    // -------------------------------------------------------------------
    // Part 2: InitialClimbPhase must defer IFR turn until past DER + 400 AGL
    // -------------------------------------------------------------------

    /// <summary>
    /// IFR FlyHeadingDeparture must NOT have its heading applied at the start
    /// of InitialClimbPhase. Aircraft must continue on runway heading until it
    /// reaches the 400 ft AGL TERPS floor (AIM 5-2-9.e.1 / 7110.65 5-8-3 NOTE) —
    /// with no lateral past-DER requirement (that is a VFR-pattern rule, AIM 4-3-2;
    /// see issue #195).
    /// </summary>
    [Fact]
    public void Ifr_InitialClimb_FlyHeading_DefersTurnUntil400Agl_IgnoringDerPosition()
    {
        const double runwayHdg = 280.0;
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            heading: runwayHdg,
            elevationFt: FieldElevation,
            thresholdLat: 37.7295,
            thresholdLon: -122.2098,
            endLat: 37.7355,
            endLon: -122.2253
        );

        var departure = new FlyHeadingDeparture(new MagneticHeading(360), null);
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N152SP",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR" },
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = FieldElevation + IfrTurnAglFloor, // just at TakeoffPhase completion
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        var climbPhase = new InitialClimbPhase
        {
            Departure = departure,
            AssignedAltitude = 2000,
            IsVfr = false,
        };
        climbPhase.OnStart(ctx);

        // At 400 ft AGL but still near the threshold (not past DER), turn must NOT be applied.
        Assert.True(
            (targets.TargetTrueHeading is null) || (Math.Abs(NormalizeAngleDiff(targets.TargetTrueHeading.Value.Degrees - runwayHdg)) < 1),
            $"At threshold + 400 AGL, target heading must still be runway heading. Got {targets.TargetTrueHeading?.Degrees:F1}"
        );

        // Case 1: below the 400 ft AGL floor (even past the DER) → still deferred. This is the
        // regression guard for the original N152SP "turn at Vr (~100 AGL)" bug; the 400 ft floor
        // alone prevents it.
        aircraft.Altitude = FieldElevation + 200;
        var pastDer = GeoMath.ProjectPoint(new LatLon(runway.EndLatitude, runway.EndLongitude), new TrueHeading(runwayHdg), 0.5);
        aircraft.Position = pastDer;
        climbPhase.OnTick(ctx);
        Assert.True(
            (targets.TargetTrueHeading is null) || (Math.Abs(NormalizeAngleDiff(targets.TargetTrueHeading.Value.Degrees - runwayHdg)) < 1),
            $"Past DER but only 200 AGL: turn must be deferred. Got hdg={targets.TargetTrueHeading?.Degrees:F1}"
        );

        // Case 2: at the 400 ft AGL floor but still at the threshold (NOT past the DER) → IFR turns
        // anyway. Lateral past-DER is a VFR-pattern rule (AIM 4-3-2); IFR turns at 400 ft above field
        // elevation regardless of position over the runway (issue #195).
        aircraft.Altitude = FieldElevation + IfrTurnAglFloor;
        aircraft.Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        climbPhase.OnTick(ctx);
        Assert.NotNull(targets.TargetTrueHeading);
        Assert.True(
            Math.Abs(NormalizeAngleDiff(targets.TargetTrueHeading.Value.Degrees - 360)) < 1,
            $"At 400 ft AGL the IFR turn must apply regardless of DER position. Got {targets.TargetTrueHeading.Value.Degrees:F1}"
        );
    }

    /// <summary>
    /// During the airborne portion of TakeoffPhase (Vr through 400 AGL), an
    /// IFR aircraft with FlyHeadingDeparture must keep runway heading, not
    /// the assigned departure heading. Pre-fix this method applied the turn
    /// at Vr at ~Vr+50 ft AGL.
    /// </summary>
    [Fact]
    public void Ifr_TakeoffPhase_FlyHeading_KeepsRunwayHeadingThroughAirborneClimb()
    {
        const double runwayHdg = 280.0;
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            heading: runwayHdg,
            elevationFt: FieldElevation,
            thresholdLat: 37.7295,
            thresholdLon: -122.2098,
            endLat: 37.7355,
            endLon: -122.2253
        );

        var departure = new FlyHeadingDeparture(new MagneticHeading(360), null);
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "N152SP",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR" },
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = new TrueHeading(runwayHdg),
            Altitude = FieldElevation,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        var phase = new TakeoffPhase();
        phase.SetAssignedDeparture(departure);
        phase.OnStart(ctx);

        // Tick to airborne (ground roll).
        for (int i = 0; i < 300; i++)
        {
            if (phase.OnTick(ctx))
            {
                break;
            }
            if (!aircraft.IsOnGround)
            {
                aircraft.Altitude += 50;
            }
        }

        Assert.False(aircraft.IsOnGround, "Aircraft must reach Vr and become airborne.");

        // Immediately after Vr the heading target must still be the runway
        // heading, not the assigned 360. Pre-fix this was 360 at Vr.
        Assert.NotNull(targets.TargetTrueHeading);
        Assert.True(
            Math.Abs(NormalizeAngleDiff(targets.TargetTrueHeading.Value.Degrees - runwayHdg)) < 1,
            $"At Vr, target heading must remain runway heading. Got {targets.TargetTrueHeading.Value.Degrees:F1}"
        );
    }

    // -------------------------------------------------------------------
    // Part 3: full replay through the recorded bug moment
    // -------------------------------------------------------------------

    /// <summary>
    /// Full replay through the recorded bug moment confirms that the
    /// originally recorded <c>CTO MRC 020</c> action is now rejected: the
    /// aircraft remains on the ground at t=820, where pre-fix it was already
    /// climbing through ~100 ft AGL turning right.
    /// </summary>
    [Fact]
    public void N152sp_FullReplayThroughBugMoment_RejectsRecordedCtoMrc()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past the recorded CTO MRC 020 action (t=768).
        engine.Replay(recording, 820);

        var n152sp = engine.FindAircraft("N152SP");
        Assert.NotNull(n152sp);

        output.WriteLine(
            $"t=820: phase={n152sp.Phases?.CurrentPhase?.Name ?? "(none)"} alt={n152sp.Altitude:F0} hdg={n152sp.TrueHeading.Degrees:F1} onGround={n152sp.IsOnGround}"
        );

        // Pre-fix snapshot at t=820 had alt=114 hdg=318 — already turning.
        // Post-fix the CTO MRC was rejected, so the aircraft must remain on
        // the ground (in some ground phase — TaxiingPhase / HoldingShortPhase /
        // HoldingInPositionPhase / LinedUpAndWaitingPhase). The original
        // recorded chain Taxiing → HoldingShort → HoldingInPosition collapses
        // back to Taxiing once the CTO MRC fails to install LineUp/Takeoff.
        Assert.True(n152sp.IsOnGround, $"N152SP must remain on the ground after CTO MRC rejection (alt={n152sp.Altitude:F0}).");
        Assert.True(
            n152sp.Phases?.CurrentPhase is TaxiingPhase or HoldingShortPhase or HoldingInPositionPhase or LinedUpAndWaitingPhase,
            $"Expected a ground phase but got {n152sp.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}"
        );
    }

    private static double NormalizeAngleDiff(double angle)
    {
        while (angle > 180)
        {
            angle -= 360;
        }
        while (angle < -180)
        {
            angle += 360;
        }
        return angle;
    }
}
