using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the CMD6 CTOPP bug: bare CTOPP made an EC35 helicopter accelerate
/// forward to 60 kt along its parked heading (~280°/west) and drift westbound, instead
/// of lifting vertically into a hover and holding position.
///
/// Recording: S2-OAK-5 | Advanced Concepts — CMD6 is an EC35 parked at the OAK HELI spot
/// (heading ~280°). The controller types bare CTOPP at t≈854.5s.
///
/// After the fix: bare CTOPP (and CTOPP +AGL) lifts the helicopter straight up to a hover
/// (default 100 ft AGL) with zero forward speed and holds present position; the directional
/// forms (heading / OC / DCT) still depart, but only after the vertical climb.
/// </summary>
public class CtoppHoverHoldTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/cmd6-ctopp-hover-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "CMD6";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// The headline regression: after bare CTOPP, the helicopter lifts straight up and holds
    /// position — it must not accelerate forward and drift like a fixed-wing departure. Before
    /// the fix it accelerated toward 60 kt along its parked heading (~280°/west). Replay to the
    /// parked ground state, issue CTOPP, then advance physics and assert it stays over its spot.
    /// </summary>
    [Fact]
    public void BareCtopp_HelicopterHoldsPosition_NoWestboundDrift()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 850);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.True(aircraft.IsOnGround);
        var spot = aircraft.Position;

        var result = engine.SendCommand(Callsign, "CTOPP");
        Assert.True(result.Success, result.Message);

        double maxDriftFt = 0;
        double maxIas = 0;
        double finalIas = 0;
        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            double driftFt = GeoMath.DistanceNm(aircraft.Position, spot) * 6076.12;
            maxDriftFt = Math.Max(maxDriftFt, driftFt);
            maxIas = Math.Max(maxIas, aircraft.IndicatedAirspeed);
            finalIas = aircraft.IndicatedAirspeed;

            if (t % 5 == 0)
            {
                output.WriteLine(
                    $"t={850 + t} phase={aircraft.Phases?.CurrentPhase?.Name} ias={aircraft.IndicatedAirspeed:F1} alt={aircraft.Altitude:F0} drift={driftFt:F0}ft"
                );
            }
        }

        // Helicopter holds position: settles to a stationary hover (no sustained forward flight),
        // never approaching the 60 kt forward-departure speed, and barely moves off its spot.
        Assert.True(finalIas < 2, $"helicopter should settle to a hover (~0 kt) but ended at {finalIas:F1} kt");
        Assert.True(maxIas < 5, $"helicopter must not accelerate toward departure speed; peaked at {maxIas:F1} kt");
        Assert.True(maxDriftFt < 60, $"helicopter should hold position but drifted {maxDriftFt:F0} ft from its spot");
    }

    /// <summary>
    /// After lifting off, the helicopter should be hovering (VfrHoldPhase) at roughly the
    /// default hover altitude (100 ft AGL), having climbed straight up from the ground.
    /// </summary>
    [Fact]
    public void BareCtopp_ReachesHoverAndHolds()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 850);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.True(aircraft.IsOnGround);
        double fieldElevation = aircraft.Altitude;

        var result = engine.SendCommand(Callsign, "CTOPP");
        Assert.True(result.Success, result.Message);

        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
        }

        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        double agl = aircraft.Altitude - fieldElevation;
        output.WriteLine($"final phase={aircraft.Phases?.CurrentPhase?.Name} agl={agl:F0} ias={aircraft.IndicatedAirspeed:F1}");

        Assert.IsType<VfrHoldPhase>(aircraft.Phases!.CurrentPhase);
        Assert.True(agl is > 10 and < 60, $"helicopter should hover near 25 ft AGL but was {agl:F0} ft AGL");
        Assert.False(aircraft.IsOnGround);
    }

    /// <summary>
    /// Hold mode chain: bare CTOPP builds [HelicopterTakeoffPhase, VfrHoldPhase] and the
    /// liftoff commands zero forward speed (pure vertical climb).
    /// </summary>
    [Fact]
    public void BareCtopp_BuildsHoverChain_ZeroForwardSpeed()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Land at t=854 — CMD6 is parked on the ground, just before the recorded CTOPP fires.
        engine.Replay(recording, 850);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.True(aircraft.IsOnGround);

        var result = engine.SendCommand(Callsign, "CTOPP");
        Assert.True(result.Success, result.Message);

        var takeoff = Assert.IsType<HelicopterTakeoffPhase>(aircraft.Phases!.CurrentPhase);
        Assert.Equal(25, takeoff.CompletionAgl);
        Assert.Contains(aircraft.Phases.Phases, p => p is VfrHoldPhase);
        Assert.Equal(0, aircraft.Targets.TargetSpeed);
    }

    /// <summary>
    /// CTOPP +002 holds at 200 ft AGL instead of the default 100.
    /// </summary>
    [Fact]
    public void CtoppPlusAgl_SetsHoverAltitude()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 850);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        var result = engine.SendCommand(Callsign, "CTOPP +002");
        Assert.True(result.Success, result.Message);

        var takeoff = Assert.IsType<HelicopterTakeoffPhase>(aircraft.Phases!.CurrentPhase);
        Assert.Equal(200, takeoff.CompletionAgl);
        Assert.Contains(aircraft.Phases.Phases, p => p is VfrHoldPhase);
    }

    /// <summary>
    /// Depart mode regression (the CMD6 "CTOPP DCT VPCOL 020" bug): a directional CTOPP builds
    /// [HelicopterTakeoffPhase, InitialClimbPhase] and must hold its ground position with zero
    /// forward speed for the FULL vertical-liftoff climb (~400 ft AGL), not just the first few
    /// seconds. Before the fix the auto-speed schedule spun the helicopter up to ~100 kt during
    /// the ~20 s liftoff, drifting it ~1.77 nm along its parked heading before InitialClimbPhase
    /// was ever meant to start forward flight.
    /// </summary>
    [Fact]
    public void DirectionalCtopp_HoldsPositionThroughEntireVerticalLiftoff()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 850);
        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.True(aircraft.IsOnGround);
        double fieldElevation = aircraft.Altitude;
        var spot = aircraft.Position;

        var result = engine.SendCommand(Callsign, "CTOPP 090");
        Assert.True(result.Success, result.Message);

        Assert.IsType<HelicopterTakeoffPhase>(aircraft.Phases!.CurrentPhase);
        Assert.Contains(aircraft.Phases.Phases, p => p is InitialClimbPhase);
        Assert.Equal(0, aircraft.Targets.TargetSpeed);

        // Tick through the ENTIRE vertical-liftoff phase (to ~400 ft AGL at 1200 fpm ≈ 20 s).
        // For every tick the helicopter is still in HelicopterTakeoffPhase it must hold ~0 forward
        // speed and stay over its spot; only once it hands off to InitialClimbPhase may it move.
        double maxLiftoffDriftFt = 0;
        double maxLiftoffIas = 0;
        bool reachedInitialClimb = false;
        for (int t = 1; t <= 40; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            if (aircraft.Phases?.CurrentPhase is HelicopterTakeoffPhase)
            {
                double driftFt = GeoMath.DistanceNm(aircraft.Position, spot) * 6076.12;
                maxLiftoffDriftFt = Math.Max(maxLiftoffDriftFt, driftFt);
                maxLiftoffIas = Math.Max(maxLiftoffIas, aircraft.IndicatedAirspeed);
            }
            else if (aircraft.Phases?.CurrentPhase is InitialClimbPhase)
            {
                reachedInitialClimb = true;
            }

            if (t % 5 == 0)
            {
                output.WriteLine(
                    $"t={850 + t} phase={aircraft.Phases?.CurrentPhase?.Name} ias={aircraft.IndicatedAirspeed:F1} agl={aircraft.Altitude - fieldElevation:F0} maxLiftoffDrift={maxLiftoffDriftFt:F0}ft"
                );
            }

            if (reachedInitialClimb)
            {
                break;
            }
        }

        Assert.True(reachedInitialClimb, "helicopter never completed the vertical liftoff into InitialClimbPhase");
        Assert.True(maxLiftoffIas < 2, $"helicopter must hold zero forward speed during the vertical liftoff but peaked at {maxLiftoffIas:F1} kt");
        Assert.True(maxLiftoffDriftFt < 200, $"helicopter must hold position during the vertical liftoff but drifted {maxLiftoffDriftFt:F0} ft");
    }

    /// <summary>
    /// A mid-liftoff HelicopterTakeoffPhase with a non-default completion AGL survives a
    /// snapshot round-trip.
    /// </summary>
    [Fact]
    public void HelicopterTakeoffPhase_CompletionAgl_SurvivesSnapshotRoundTrip()
    {
        var phase = new HelicopterTakeoffPhase { CompletionAgl = 200 };
        var dto = (Yaat.Sim.Simulation.Snapshots.HelicopterTakeoffPhaseDto)phase.ToSnapshot();
        var restored = HelicopterTakeoffPhase.FromSnapshot(dto);
        Assert.Equal(200, restored.CompletionAgl);
    }
}
