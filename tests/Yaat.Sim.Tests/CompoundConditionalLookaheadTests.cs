using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class CompoundConditionalLookaheadTests
{
    private static AircraftState MakeAircraft(double altitude = 3000, double ias = 250) =>
        new()
        {
            Callsign = "TST01",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            TrueTrack = new TrueHeading(090),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
        };

    private static RunwayInfo MakeRunway() => TestRunwayFactory.Make(designator: "28R", thresholdLat: 37.7, thresholdLon: -122.2, heading: 280);

    private static AircraftState MakeAircraftOnFinal(double distanceNm, bool activePhase)
    {
        var runway = MakeRunway();
        var (lat, lon) = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            new TrueHeading((runway.TrueHeading.Degrees + 180) % 360),
            distanceNm
        );

        var aircraft = MakeAircraft(altitude: 3000, ias: 220);
        aircraft.Position = new LatLon(lat, lon);
        aircraft.TrueHeading = runway.TrueHeading;
        aircraft.TrueTrack = runway.TrueHeading;
        aircraft.FlightPlan = new AircraftFlightPlan { Destination = "OAK" };
        aircraft.Phases = new PhaseList { AssignedRunway = runway };

        if (activePhase)
        {
            var phase = new VfrHoldPhase { OrbitDirection = TurnDirection.Left };
            aircraft.Phases.Add(phase);
            phase.Status = PhaseStatus.Active;
        }

        return aircraft;
    }

    private static void DispatchOk(AircraftState aircraft, string command)
    {
        var parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared, validateDctFixes: false));
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void LvCondition_FiresWhileEarlierAltitudeBlockContinues()
    {
        var aircraft = MakeAircraft(altitude: 3000);

        DispatchOk(aircraft, "CM 100; LV 050 FH 270");

        Assert.Equal(2, aircraft.Queue.Blocks.Count);
        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
        Assert.False(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Equal(10000, aircraft.Targets.TargetAltitude);

        aircraft.Altitude = 5000;
        FlightPhysics.Update(aircraft, 0.0);

        Assert.True(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Equal(270, aircraft.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Equal(10000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ConditionalLookahead_DoesNotJumpPastOrdinaryQueuedBlock()
    {
        var aircraft = MakeAircraft(altitude: 5000);

        DispatchOk(aircraft, "FH 270; FH 180; LV 050 FH 090");

        Assert.Equal(3, aircraft.Queue.Blocks.Count);
        Assert.True(aircraft.Queue.Blocks[0].IsApplied);

        FlightPhysics.Update(aircraft, 0.0);

        Assert.False(aircraft.Queue.Blocks[1].IsApplied);
        Assert.False(aircraft.Queue.Blocks[2].IsApplied);
        Assert.Equal(270, aircraft.Targets.AssignedMagneticHeading?.Degrees);
    }

    [Fact]
    public void SpeedUntilDistance_FiresResumeWhileSpeedBlockContinues()
    {
        var aircraft = MakeAircraftOnFinal(distanceNm: 9.0, activePhase: false);

        DispatchOk(aircraft, "SPD 210 UNTIL 10");

        Assert.Equal(2, aircraft.Queue.Blocks.Count);
        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
        Assert.False(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Equal(210, aircraft.Targets.TargetSpeed);

        FlightPhysics.Update(aircraft, 1.0);

        Assert.True(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Null(aircraft.Targets.TargetSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void ChainedSpeedUntilDistance_FiresIntermediateAndFinalBlocks()
    {
        var aircraft = MakeAircraftOnFinal(distanceNm: 9.0, activePhase: false);

        DispatchOk(aircraft, "SPD 210 UNTIL 10; SPD 180 UNTIL 5");

        Assert.Equal(3, aircraft.Queue.Blocks.Count);
        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
        Assert.False(aircraft.Queue.Blocks[1].IsApplied);
        Assert.False(aircraft.Queue.Blocks[2].IsApplied);
        Assert.Equal(210, aircraft.Targets.TargetSpeed);

        FlightPhysics.Update(aircraft, 1.0);
        FlightPhysics.Update(aircraft, 1.0);

        Assert.True(aircraft.Queue.Blocks[1].IsApplied);
        Assert.False(aircraft.Queue.Blocks[2].IsApplied);
        Assert.Equal(180, aircraft.Targets.TargetSpeed);

        var runway = aircraft.Phases!.AssignedRunway!;
        var (lat, lon) = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            new TrueHeading((runway.TrueHeading.Degrees + 180) % 360),
            4.9
        );
        aircraft.Position = new LatLon(lat, lon);

        FlightPhysics.Update(aircraft, 1.0);
        FlightPhysics.Update(aircraft, 1.0);
        FlightPhysics.Update(aircraft, 1.0);

        Assert.True(aircraft.Queue.Blocks[2].IsApplied);
        Assert.Null(aircraft.Targets.TargetSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void SpeedUntilDistance_FiresDuringActivePhase()
    {
        var aircraft = MakeAircraftOnFinal(distanceNm: 9.0, activePhase: true);

        DispatchOk(aircraft, "SPD 210 UNTIL 10");

        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.Equal(210, aircraft.Targets.TargetSpeed);

        FlightPhysics.Update(aircraft, 1.0);

        Assert.True(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Null(aircraft.Targets.TargetSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.NotNull(aircraft.Phases?.CurrentPhase);
    }
}
