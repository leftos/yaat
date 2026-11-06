using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1447: touchdown ends the ATC speed assignment in force. A pilot who lands is no longer flying the speed ATC gave
/// it (7110.65 §5-7-1.b.4; AIM 4-4-12.g), and a touch-and-go is a landing followed by a departure from the touchdown on
/// (§5-7-3.d), so the climb-out is flown on the type's own pattern speeds, not the arrival's assignment. A go-around
/// before touchdown keeps its assignment. Every touchdown is started the way the pattern starts it — the touchdown phase
/// replaces the landing, so its <c>OnStart</c> is the wheels touching. Real Oakland 28L from the shipped navdata.
/// </summary>
public sealed class TouchdownSpeedCancelTests
{
    private readonly ITestOutputHelper _output;

    public TouchdownSpeedCancelTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo Oak28L()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28L");
        Assert.NotNull(runway);
        return runway;
    }

    /// <summary>An airborne aircraft with no phases, so an SPD/SPEEDF/SMAX dispatch is not gated by the 5nm-final rule.</summary>
    private static AircraftState Airborne(string type, double ias, double altitude)
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = type,
            Position = new LatLon(37.7213, -122.2207),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            VerticalSpeed = 0,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(280);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    /// <summary>Puts the aircraft just over the threshold, sinking, as the pattern hands it to the touchdown phase.</summary>
    private static void PlaceOnFinal(AircraftState ac, RunwayInfo runway, double ias, double aglFt)
    {
        ac.Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        ac.TrueHeading = runway.TrueHeading;
        ac.TrueTrack = runway.TrueHeading;
        ac.Targets.TargetTrueHeading = runway.TrueHeading;
        ac.IndicatedAirspeed = ias;
        ac.Altitude = runway.ElevationFt + aglFt;
        ac.VerticalSpeed = -700;
        ac.IsOnGround = false;
        ac.Phases = new PhaseList { AssignedRunway = runway };
    }

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo runway) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
        };

    /// <summary>The pattern's upwind leg — where a touch-and-go's climb-out goes — built by the same builder the sim uses.</summary>
    private static UpwindPhase UpwindLeg(RunwayInfo runway, AircraftCategory category, string type)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = runway,
                Category = category,
                AircraftType = type,
                WindSpeedKt = 0,
                Direction = PatternDirection.Left,
                EntryLeg = PatternEntryLeg.Upwind,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = [runway],
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        return Assert.IsType<UpwindPhase>(circuit[0]);
    }

    /// <summary>Ticks a touchdown phase until it lifts off, collecting every speed target it writes.</summary>
    private static void RollAndLiftOff(AircraftState ac, RunwayInfo runway, TouchAndGoPhase touchAndGo, List<double?> targets)
    {
        PhaseContext ctx = Ctx(ac, runway);
        for (int tick = 0; (tick < 400) && !touchAndGo.HasLiftedOff; tick++)
        {
            touchAndGo.OnTick(ctx);
            targets.Add(ac.Targets.TargetSpeed);
        }

        Assert.True(touchAndGo.HasLiftedOff, "premise: the touch-and-go lifts off");
    }

    /// <summary>
    /// A C172 given 120 kt on the arrival touches, rolls and goes: its <c>OnStart</c> is the touchdown, so the assignment
    /// is gone before the rollout, and the climb-out on the pattern's upwind leg flies the leg's speed rather than 120.
    /// </summary>
    [Fact]
    public void TouchAndGo_EndsAnAssignedSpeedAtTouchdown()
    {
        RunwayInfo runway = Oak28L();
        AircraftState ac = Airborne("C172", ias: 65, altitude: 3_000);
        Assert.True(Send(ac, "SPEEDF 120").Success);
        Assert.Equal(120, ac.Targets.AssignedSpeed);

        PlaceOnFinal(ac, runway, ias: 65, aglFt: 3);
        var touchAndGo = new TouchAndGoPhase { Status = PhaseStatus.Active };
        touchAndGo.OnStart(Ctx(ac, runway));

        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);

        var targets = new List<double?>();
        RollAndLiftOff(ac, runway, touchAndGo, targets);
        _output.WriteLine($"liftoff at {ac.IndicatedAirspeed:F0} KIAS");

        double legKts = AircraftPerformance.DownwindSpeed("C172", AircraftCategory.Piston);
        PhaseContext ctx = Ctx(ac, runway);
        UpwindPhase upwind = UpwindLeg(runway, AircraftCategory.Piston, "C172");
        upwind.Status = PhaseStatus.Active;
        ac.Phases!.Phases.Add(upwind);
        upwind.OnStart(ctx);
        Assert.Equal(legKts, Assert.IsType<double>(ac.Targets.TargetSpeed), 0.5);

        for (int second = 0; second < 120; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                FlightPhysics.Update(ac, 0.25);
            }

            upwind.OnTick(ctx);
            targets.Add(ac.Targets.TargetSpeed);
        }

        _output.WriteLine($"upwind leg {legKts:F0} kt, settled at {ac.IndicatedAirspeed:F0} KIAS");
        Assert.DoesNotContain(120.0, targets);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, legKts - 5, legKts + 5);
    }

    /// <summary>
    /// A B738 under SMAX touches and goes: the touchdown ends the maximum-forward-speed hold with the rest of the
    /// assignment, and the upwind leg flies the pattern speed rather than being driven back toward the redline.
    /// </summary>
    [Fact]
    public void TouchAndGo_EndsSpeedMaxAtTouchdown()
    {
        RunwayInfo runway = Oak28L();
        AircraftState ac = Airborne("B738", ias: 200, altitude: 3_000);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(ac.Targets.SpeedMaxHeld, "premise: SMAX is held before the approach");
        double smaxKts = Assert.IsType<double>(ac.Targets.AssignedSpeed);

        PlaceOnFinal(ac, runway, ias: 150, aglFt: 3);
        var touchAndGo = new TouchAndGoPhase { Status = PhaseStatus.Active };
        touchAndGo.OnStart(Ctx(ac, runway));

        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Null(ac.Targets.TargetMach);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);

        var targets = new List<double?>();
        RollAndLiftOff(ac, runway, touchAndGo, targets);
        _output.WriteLine($"liftoff at {ac.IndicatedAirspeed:F0} KIAS, SMAX held {ac.Targets.SpeedMaxHeld}");

        double legKts = AircraftPerformance.DownwindSpeed("B738", AircraftCategory.Jet);
        PhaseContext ctx = Ctx(ac, runway);
        UpwindPhase upwind = UpwindLeg(runway, AircraftCategory.Jet, "B738");
        upwind.Status = PhaseStatus.Active;
        ac.Phases!.Phases.Add(upwind);
        upwind.OnStart(ctx);
        Assert.Equal(legKts, Assert.IsType<double>(ac.Targets.TargetSpeed), 0.5);

        for (int second = 0; second < 180; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                FlightPhysics.Update(ac, 0.25);
            }

            upwind.OnTick(ctx);
            targets.Add(ac.Targets.TargetSpeed);
        }

        _output.WriteLine($"upwind leg {legKts:F0} kt, settled at {ac.IndicatedAirspeed:F0} KIAS; SMAX would drive {smaxKts:F0}");
        Assert.False(ac.Targets.SpeedMaxHeld, "the climb-out re-armed SMAX");
        Assert.DoesNotContain(smaxKts, targets);
        Assert.InRange(ac.IndicatedAirspeed, legKts - 5, legKts + 5);
    }

    /// <summary>A stop-and-go's own <c>OnStart</c> is its touchdown, and it ends the assignment the same way.</summary>
    [Fact]
    public void StopAndGo_EndsAnAssignedSpeedAtTouchdown()
    {
        RunwayInfo runway = Oak28L();
        AircraftState ac = Airborne("C172", ias: 65, altitude: 3_000);
        Assert.True(Send(ac, "SPEEDF 120").Success);
        Assert.Equal(120, ac.Targets.AssignedSpeed);

        PlaceOnFinal(ac, runway, ias: 65, aglFt: 3);
        var stopAndGo = new StopAndGoPhase { Status = PhaseStatus.Active };
        stopAndGo.OnStart(Ctx(ac, runway));

        Assert.True(ac.IsOnGround);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    /// <summary>A helicopter landing ends the assignment at the tick its wheels touch.</summary>
    [Fact]
    public void HelicopterLanding_EndsAnAssignedSpeedAtTouchdown()
    {
        RunwayInfo runway = Oak28L();
        AircraftState ac = Airborne("R22", ias: 60, altitude: 3_000);
        Assert.True(Send(ac, "SPEEDF 90").Success);
        Assert.Equal(90, ac.Targets.AssignedSpeed);

        PlaceOnFinal(ac, runway, ias: 60, aglFt: 0);
        var landing = new HelicopterLandingPhase { Status = PhaseStatus.Active };
        PhaseContext ctx = Ctx(ac, runway);
        landing.OnStart(ctx);
        landing.OnTick(ctx);

        Assert.True(ac.IsOnGround, "premise: the descent reaches touchdown");
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
    }

    /// <summary>
    /// A go-around before touchdown keeps the assignment: only a touchdown ends it (§5-7-1.b.4 applies to a landing, not
    /// to a missed approach).
    /// </summary>
    [Fact]
    public void GoAround_BeforeTouchdown_KeepsTheAssignment()
    {
        RunwayInfo runway = Oak28L();
        AircraftState ac = Airborne("B738", ias: 160, altitude: 3_000);
        Assert.True(Send(ac, "SPEEDF 180").Success);
        Assert.Equal(180, ac.Targets.AssignedSpeed);

        PlaceOnFinal(ac, runway, ias: 160, aglFt: 150);
        new GoAroundPhase { Status = PhaseStatus.Active }.OnStart(Ctx(ac, runway));

        Assert.Equal(180, Assert.IsType<double>(ac.Targets.AssignedSpeed), 0.5);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    /// <summary>
    /// A touchdown tick keeps braking on the flare's own target instead of coasting through the tick the assignment ends
    /// on.
    /// </summary>
    [Fact]
    public void Landing_TouchdownTick_EndsTheAssignmentAndBrakes()
    {
        RunwayInfo runway = Oak28L();
        AircraftState ac = Airborne("B738", ias: 150, altitude: 3_000);
        Assert.True(Send(ac, "SPEEDF 180").Success);

        PlaceOnFinal(ac, runway, ias: 150, aglFt: 1);
        var landing = new LandingPhase { Status = PhaseStatus.Active };
        PhaseContext ctx = Ctx(ac, runway);
        landing.OnStart(ctx);
        for (int tick = 0; (tick < 50) && !ac.IsOnGround; tick++)
        {
            landing.OnTick(ctx);
        }

        Assert.True(ac.IsOnGround, "premise: the approach reaches touchdown");
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.NotNull(ac.Targets.TargetSpeed);
    }
}
