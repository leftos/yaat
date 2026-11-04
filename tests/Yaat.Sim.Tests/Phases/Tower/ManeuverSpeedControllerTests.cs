using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Tower;

/// <summary>
/// A speed the controller assigns during a maneuver survives the maneuver. Once the maneuver's
/// <c>ManeuverSpeedController</c> has handed speed over (<c>CancelAutoResume</c>), a later slow-down — a VFR hold
/// reaching its fix among them — and the maneuver's own resume must both leave the assignment alone. A resume whose
/// captured state was explicit but carried no target speed restores the assigned speed rather than null.
/// </summary>
public class ManeuverSpeedControllerTests
{
    private const string FixName = "SUNOL";
    private const double AssignedSpeedKts = 150;

    /// <summary>
    /// A C208 at 5,000 ft: holding speed 120 kt and a real Vmo of 175 KIAS, so 150 kt sits above holding and under the
    /// type's limit.
    /// </summary>
    private static AircraftState Arrival() => MakeAircraft("C208", altitude: 5000, ias: 150);

    private static AircraftState MakeAircraft(string type, double altitude, double ias)
    {
        TestVnasData.EnsureInitialized();
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = type,
            Position = new LatLon(37.72, -122.22),
            TrueHeading = new TrueHeading(90),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", FlightRules = "VFR" },
        };
    }

    private static PhaseContext Start(Phase phase, AircraftState aircraft)
    {
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
        aircraft.Phases.Start(ctx);
        return ctx;
    }

    private static CommandResult AssignSpeed(AircraftState aircraft, double speed) =>
        CommandDispatcher.Dispatch(new SpeedCommand((int)speed), aircraft, TestDispatch.Context(Random.Shared));

    private static void AssertSpeedSurvives(AircraftState aircraft)
    {
        double target = Assert.NotNull(aircraft.Targets.TargetSpeed);
        double assigned = Assert.NotNull(aircraft.Targets.AssignedSpeed);
        Assert.Equal(AssignedSpeedKts, target, 1);
        Assert.Equal(AssignedSpeedKts, assigned, 1);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
    }

    private static (bool TakenOver, double? PriorAssigned) SnapshotSpeedState(PhaseDto dto) =>
        dto switch
        {
            VfrHoldPhaseDto d => (d.SpeedTakenOver, d.PriorAssignedSpeed),
            STurnPhaseDto d => (d.SpeedTakenOver, d.PriorAssignedSpeed),
            MakeTurnPhaseDto d => (d.SpeedTakenOver, d.PriorAssignedSpeed),
            _ => throw new InvalidOperationException($"Unexpected snapshot type {dto.GetType().Name}"),
        };

    private static Phase Restore(PhaseDto dto) =>
        dto switch
        {
            VfrHoldPhaseDto d => VfrHoldPhase.FromSnapshot(d),
            STurnPhaseDto d => STurnPhase.FromSnapshot(d),
            MakeTurnPhaseDto d => MakeTurnPhase.FromSnapshot(d),
            _ => throw new InvalidOperationException($"Unexpected snapshot type {dto.GetType().Name}"),
        };

    [Fact]
    public void VfrHold_SpeedAssignedOnTheWayToTheFix_SurvivesArrivalAndEnd()
    {
        (double fixLat, double fixLon) =
            NavigationDatabase.Instance.GetFixPosition(FixName) ?? throw new InvalidOperationException($"{FixName} missing from navdata");

        AircraftState aircraft = Arrival();
        aircraft.Position = new LatLon(fixLat, fixLon + 0.2);
        var phase = new VfrHoldPhase
        {
            FixName = FixName,
            FixLat = fixLat,
            FixLon = fixLon,
            OrbitDirection = TurnDirection.Right,
        };
        PhaseContext ctx = Start(phase, aircraft);

        CommandResult spd = AssignSpeed(aircraft, AssignedSpeedKts);
        Assert.True(spd.Success, spd.Message);

        // Arrival at the fix runs the hold's slow-down; the controller's speed must survive it.
        aircraft.Position = new LatLon(fixLat, fixLon);
        phase.OnTick(ctx);
        AssertSpeedSurvives(aircraft);

        phase.OnEnd(ctx, PhaseStatus.Completed);
        AssertSpeedSurvives(aircraft);
    }

    [Fact]
    public void MakeTurn_SpeedAssignedDuringTheTurn_SurvivesEnd()
    {
        AircraftState aircraft = Arrival();
        var phase = new MakeTurnPhase { Direction = TurnDirection.Right, TargetDegrees = 360 };
        PhaseContext ctx = Start(phase, aircraft);

        CommandResult spd = AssignSpeed(aircraft, AssignedSpeedKts);
        Assert.True(spd.Success, spd.Message);

        phase.OnEnd(ctx, PhaseStatus.Completed);
        AssertSpeedSurvives(aircraft);
    }

    [Fact]
    public void Resume_ExplicitPriorWithNullTargetSpeed_RestoresTheAssignedSpeed()
    {
        // A B738 at 6,000 ft holds 200 kt: a 220 kt aircraft with no target reduces, so the resume path runs.
        AircraftState aircraft = MakeAircraft("B738", altitude: 6000, ias: 220);
        aircraft.Targets.AssignedSpeed = 220;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.TargetSpeed = null;

        var phase = new MakeTurnPhase { Direction = TurnDirection.Left, TargetDegrees = 360 };
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
        phase.OnStart(ctx);
        phase.OnEnd(ctx, PhaseStatus.Completed);

        double target = Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.Equal(220.0, target, 1);
        Assert.True(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Theory]
    [InlineData("VfrHold")]
    [InlineData("STurn")]
    [InlineData("MakeTurn")]
    public void SnapshotRoundTrip_KeepsSpeedTakenOverAndPriorAssignedSpeed(string kind)
    {
        AircraftState aircraft = Arrival();
        aircraft.Targets.AssignedSpeed = 200;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        aircraft.Targets.TargetSpeed = null;

        Phase phase = kind switch
        {
            "VfrHold" => new VfrHoldPhase { OrbitDirection = TurnDirection.Left },
            "STurn" => new STurnPhase { InitialDirection = TurnDirection.Left },
            _ => new MakeTurnPhase { Direction = TurnDirection.Right, TargetDegrees = 360 },
        };
        PhaseContext ctx = Start(phase, aircraft);

        aircraft.Targets.TargetSpeed = AssignedSpeedKts;
        aircraft.Targets.AssignedSpeed = AssignedSpeedKts;
        aircraft.Targets.HasExplicitSpeedCommand = true;
        phase.OnCommandAccepted(CanonicalCommandType.Speed, ctx);

        PhaseDto dto = phase.ToSnapshot();
        (bool takenOver, double? priorAssigned) = SnapshotSpeedState(dto);
        Assert.True(takenOver);
        Assert.Equal(200.0, Assert.NotNull(priorAssigned), 1);

        Phase restored = Restore(dto);

        // A FromSnapshot that drops one of the fields fails here rather than silently resuming at the end.
        (bool restoredTakenOver, double? restoredPriorAssigned) = SnapshotSpeedState(restored.ToSnapshot());
        Assert.True(restoredTakenOver);
        Assert.Equal(200.0, Assert.NotNull(restoredPriorAssigned), 1);

        restored.OnEnd(ctx, PhaseStatus.Completed);
        AssertSpeedSurvives(aircraft);
    }
}
