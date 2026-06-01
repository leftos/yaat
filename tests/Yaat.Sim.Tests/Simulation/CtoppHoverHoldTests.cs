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
        Assert.True(maxIas < 20, $"helicopter must not accelerate toward departure speed; peaked at {maxIas:F1} kt");
        Assert.True(maxDriftFt < 200, $"helicopter should hold position but drifted {maxDriftFt:F0} ft from its spot");
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
    /// Depart mode regression: a directional CTOPP still builds [HelicopterTakeoffPhase,
    /// InitialClimbPhase] and lifts vertically first (zero forward speed) — it must not drift
    /// laterally while still on/near the ground.
    /// </summary>
    [Fact]
    public void DirectionalCtopp_DepartsButLiftsVerticallyFirst()
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

        // While in the vertical-liftoff phase the helicopter should climb but not drift.
        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
        }

        double driftFt = GeoMath.DistanceNm(aircraft.Position, spot) * 6076.12;
        double agl = aircraft.Altitude - fieldElevation;
        output.WriteLine($"depart liftoff: agl={agl:F0} drift={driftFt:F0}ft phase={aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(agl > 20, $"helicopter should be climbing vertically but was only {agl:F0} ft AGL");
        Assert.True(driftFt < 150, $"helicopter should lift vertically first but drifted {driftFt:F0} ft");
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
