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
/// E2E + unit tests for GitHub issue #177: helicopter air-taxi / relocation defects.
///
/// Recording: S1-OAK-6 (1) | Misc - No SID/Heli (ZOA, OAK). Helicopters N101H (R22)
/// and N436MS (EC35). The controller launches each via CTOPP, flies one with FH/CM,
/// then issues LAND/ATXI to relocate to helipad RON1/RON2.
///
/// Three defects this exercises:
///   A. AirTaxiPhase rejected airborne commands (FH/CM/DM/turns/DCT) mid-ATXI, so an
///      airborne helicopter couldn't be pulled out of the relocation behavior.
///   B. The relocation never steered to the target: AirTaxiPhase wrote ctx.Aircraft.TrueHeading
///      directly while a stale ControlTargets.TargetTrueHeading (left by the prior FH /
///      HelicopterTakeoff) made FlightPhysics.UpdateHeading snap the heading right back. The
///      heli flew a frozen heading straight past the pad.
///   C. With no assigned runway, the air-taxi target altitude fell back to the current
///      airborne altitude (currentAlt + 100), so the heli held altitude and never descended
///      onto the pad.
///
/// N101H after LAND @RON1: RON1 = (37.71107, -122.22149). Pre-fix it froze on
/// TrueHeadingDeg = 192.82 and overflew the pad, only stopping when the controller issued
/// HOLD at t=194. This test ticks the air-taxi forward WITHOUT re-applying that HOLD
/// (TickOneSecond, not ReplayOneSecond) so the relocation behavior is isolated.
/// </summary>
public class Issue177HelicopterAirTaxiTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue177-oak-heli-recording.zip";
    private const string Callsign = "N101H";

    private static SessionRecording? LoadRecording() => File.Exists(RecordingPath) ? RecordingLoader.Load(RecordingPath) : null;

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

    private static LatLon? Ron1Position()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var node = layout?.FindSpotByName("RON1");
        return node is null ? null : node.Position;
    }

    /// <summary>
    /// Diagnostic — logs the air-taxi trajectory after LAND @RON1 so the frozen-heading /
    /// no-descent behavior is visible. Not an assertion; safe to keep for future regressions.
    /// </summary>
    [Fact]
    public void Diagnostic_LogAirTaxiProfile()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        var ron1 = Ron1Position();
        if (recording is null || engine is null || ron1 is null)
        {
            return;
        }

        engine.Replay(recording, 25);

        for (int t = 25; t <= 160; t += 5)
        {
            var ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            double dist = GeoMath.DistanceNm(ac.Position, ron1.Value);
            output.WriteLine(
                $"t={t} dist={dist:F3}nm hdg={ac.TrueHeading.Degrees:F1} "
                    + $"tgtTrueHdg={ac.Targets.TargetTrueHeading?.Degrees.ToString("F1") ?? "null"} "
                    + $"alt={ac.Altitude:F0} tgtAlt={ac.Targets.TargetAltitude?.ToString("F0") ?? "null"} "
                    + $"ias={ac.IndicatedAirspeed:F0} phase={ac.Phases?.CurrentPhase?.Name ?? "(none)"}"
            );

            for (int s = 0; s < 5; s++)
            {
                engine.TickOneSecond();
            }
        }
    }

    /// <summary>
    /// E2E: after LAND @RON1, the air-taxi must actually steer to RON1 and descend onto it.
    /// Pre-fix the heli froze on its prior heading and held altitude; it never closed on the pad.
    /// </summary>
    [Fact]
    public void N101H_AirTaxi_ClosesOnRon1AndDescends()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        var ron1 = Ron1Position();
        if (recording is null || engine is null || ron1 is null)
        {
            return;
        }

        engine.Replay(recording, 25);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.IsType<AirTaxiPhase>(ac.Phases?.CurrentPhase);

        double fieldElev = TestVnasData.NavigationDb!.GetAirportElevation("OAK") ?? 0;
        double startDist = GeoMath.DistanceNm(ac.Position, ron1.Value);
        double minDist = startDist;
        bool reachedLandingOrParking = false;

        // Tick the air-taxi forward without re-applying the recorded t=194 HOLD.
        for (int t = 0; t < 240; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                break;
            }

            minDist = Math.Min(minDist, GeoMath.DistanceNm(ac.Position, ron1.Value));
            if (ac.Phases?.CurrentPhase is HelicopterLandingPhase or AtParkingPhase)
            {
                reachedLandingOrParking = true;
            }
        }

        output.WriteLine($"startDist={startDist:F3}nm minDist={minDist:F3}nm fieldElev={fieldElev:F0} finalAlt={ac?.Altitude:F0}");

        Assert.True(minDist < 0.10, $"Heli should reach RON1 (within 0.10nm) but min distance was {minDist:F3}nm (start {startDist:F3}nm)");
        Assert.True(reachedLandingOrParking, "Heli should advance to HelicopterLanding/AtParking (it arrived and landed)");
        Assert.NotNull(ac);
        Assert.True(ac.Altitude < fieldElev + 130, $"Heli should descend toward field elevation {fieldElev:F0} but was at {ac.Altitude:F0}");
    }

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.TurnLeft)]
    [InlineData(CanonicalCommandType.TurnRight)]
    [InlineData(CanonicalCommandType.FlyPresentHeading)]
    [InlineData(CanonicalCommandType.ClimbMaintain)]
    [InlineData(CanonicalCommandType.DescendMaintain)]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.DirectTo)]
    public void AirTaxiPhase_AirborneCommands_ClearPhase(CanonicalCommandType cmd)
    {
        var phase = new AirTaxiPhase(37.71107, -122.22149, "RON1");
        Assert.True(phase.CanAcceptCommand(cmd).ClearsThePhase, $"{cmd} should pull the heli out of the air-taxi");
    }

    /// <summary>
    /// HPP (hover present position) is the helicopter-appropriate hold for an air-taxiing heli:
    /// it stops the relocation and hovers in place (VfrHold). To continue, the controller
    /// re-issues ATXI/LAND. The ground HOLD verb does not apply to an airborne helicopter.
    /// </summary>
    [Fact]
    public void N101H_AirTaxi_HppHoversInPlace()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 25);
        Assert.IsType<AirTaxiPhase>(engine.FindAircraft(Callsign)?.Phases?.CurrentPhase);

        var result = engine.SendCommand(Callsign, "HPP");
        Assert.True(result.Success, $"HPP should hover the air-taxiing heli: {result.Message}");
        Assert.IsType<VfrHoldPhase>(engine.FindAircraft(Callsign)?.Phases?.CurrentPhase);
    }

    [Fact]
    public void N101H_AirTaxi_HoldRejectedAirborne()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 25);
        Assert.IsType<AirTaxiPhase>(engine.FindAircraft(Callsign)?.Phases?.CurrentPhase);

        var result = engine.SendCommand(Callsign, "HOLD");
        Assert.False(result.Success, "Ground HOLD should not apply to an airborne air-taxiing heli");
        Assert.Contains("on the ground", result.Message!);
        Assert.IsType<AirTaxiPhase>(engine.FindAircraft(Callsign)?.Phases?.CurrentPhase);
    }

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.ClimbMaintain)]
    [InlineData(CanonicalCommandType.DescendMaintain)]
    [InlineData(CanonicalCommandType.Speed)]
    public void HelicopterLandingPhase_AirborneCommands_ClearPhase(CanonicalCommandType cmd)
    {
        var phase = new HelicopterLandingPhase();
        Assert.True(phase.CanAcceptCommand(cmd).ClearsThePhase, $"{cmd} should pull the heli out of the descent before touchdown");
    }

    [Theory]
    [InlineData(CanonicalCommandType.GoAround)]
    [InlineData(CanonicalCommandType.ExitLeft)]
    [InlineData(CanonicalCommandType.ExitRight)]
    public void HelicopterLandingPhase_GoAroundAndExit_Allowed(CanonicalCommandType cmd)
    {
        var phase = new HelicopterLandingPhase();
        Assert.True(phase.CanAcceptCommand(cmd).IsAllowed, $"{cmd} should be handled in-phase during the descent");
    }

    // Air-taxi steering turn rate scales with groundspeed: the hover pedal-turn rate (30 deg/s)
    // at 0 kt down to the standard airborne rate (5 deg/s) at the 40 kt cruise speed and above.
    [Theory]
    [InlineData(0.0, 30.0)]
    [InlineData(40.0, 5.0)]
    [InlineData(80.0, 5.0)]
    [InlineData(20.0, 17.5)]
    public void AirTaxi_SteerTurnRate_ScalesWithGroundspeed(double groundSpeedKts, double expected)
    {
        Assert.Equal(expected, AirTaxiPhase.SteerTurnRate(AircraftCategory.Helicopter, groundSpeedKts), 3);
    }

    [Fact]
    public void AirTaxi_SteerTurnRate_FasterNearHover()
    {
        double hover = AirTaxiPhase.SteerTurnRate(AircraftCategory.Helicopter, 0);
        double cruise = AirTaxiPhase.SteerTurnRate(AircraftCategory.Helicopter, 40);
        Assert.True(hover > cruise, $"heli should pivot faster near hover ({hover:F1}) than at cruise ({cruise:F1})");
    }
}
