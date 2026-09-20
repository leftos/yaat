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
        RunwayInfo runway = MakeRunway();
        (double lat, double lon) = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            new TrueHeading((runway.TrueHeading.Degrees + 180) % 360),
            distanceNm
        );

        AircraftState aircraft = MakeAircraft(altitude: 3000, ias: 220);
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
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);

        CommandResult result = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            aircraft,
            TestDispatch.Context(Random.Shared, validateDctFixes: false)
        );
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void LvCondition_FiresWhileEarlierAltitudeBlockContinues()
    {
        AircraftState aircraft = MakeAircraft(altitude: 3000);

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
        AircraftState aircraft = MakeAircraft(altitude: 5000);

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
        AircraftState aircraft = MakeAircraftOnFinal(distanceNm: 9.0, activePhase: false);

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
        AircraftState aircraft = MakeAircraftOnFinal(distanceNm: 9.0, activePhase: false);

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

        RunwayInfo runway = aircraft.Phases!.AssignedRunway!;
        (double lat, double lon) = GeoMath.ProjectPoint(
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
        double? standing = aircraft.Targets.TargetSpeed;
        Assert.False(
            (standing == 210) || (standing == 180),
            $"the chain's own speeds are gone once its last block has fired, but {standing:F0} kt is still standing"
        );
        Assert.True(
            (standing is null) || (standing.Value == FlightPhysics.RegulatorySpeedLimit(aircraft)),
            $"the chain's own speeds (210, 180) are gone; only the 91.117 correction may stand, not {standing:F0} kt"
        );
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void SpeedUntilDistance_FiresDuringActivePhase()
    {
        AircraftState aircraft = MakeAircraftOnFinal(distanceNm: 9.0, activePhase: true);

        DispatchOk(aircraft, "SPD 210 UNTIL 10");

        Assert.NotNull(aircraft.Phases?.CurrentPhase);
        Assert.Equal(210, aircraft.Targets.TargetSpeed);

        FlightPhysics.Update(aircraft, 1.0);

        Assert.True(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Null(aircraft.Targets.TargetSpeed);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.NotNull(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// 14 CFR 91.117 holds an aircraft short of an assigned speed without ending the assignment, so the target stays
    /// standing at the cap. The block carrying it is nonetheless finished — the aircraft is flying the fastest speed it
    /// lawfully may where it is, and there is nothing further for that instruction to do — so the chain behind it runs.
    /// </summary>
    [Fact]
    public void SpeedHeldAtTheRegulatoryCap_StillAdvancesTheChain()
    {
        AircraftState aircraft = MakeAircraftUnderTheBravoShelf(ias: 220);

        DispatchOk(aircraft, "SPD 210; H 090");

        Assert.Equal(2, aircraft.Queue.Blocks.Count);
        Assert.True(aircraft.Queue.Blocks[0].IsApplied);
        Assert.False(aircraft.Queue.Blocks[1].IsApplied);
        Assert.Equal(210, aircraft.Targets.TargetSpeed);

        for (int i = 0; i < 12; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
        }

        Assert.Equal(ClassBShelfSpeedLimitKts, aircraft.IndicatedAirspeed, 0.5);
        Assert.True(aircraft.Queue.Blocks[1].IsApplied, "the heading block never fired behind a speed the 200 kt cap capped");
        Assert.Equal(90, aircraft.Targets.AssignedMagneticHeading?.Degrees);
        // The assignment outlives the cap that bent it: only ATC ends a speed adjustment (7110.65 §5-7-4).
        Assert.Equal(210, Assert.IsType<double>(aircraft.Targets.TargetSpeed), 0.5);
    }

    /// <summary>
    /// And it is arriving on the capped speed that finishes the block, not merely being assigned one above the cap: an
    /// aircraft still decelerating toward the cap is mid-instruction, so the chain waits for it.
    /// </summary>
    [Fact]
    public void SpeedAboveTheCap_DoesNotCompleteBeforeReachingTheCap()
    {
        AircraftState aircraft = MakeAircraftUnderTheBravoShelf(ias: 250);

        DispatchOk(aircraft, "SPD 210; H 090");

        FlightPhysics.Update(aircraft, 1.0);
        FlightPhysics.Update(aircraft, 1.0);

        Assert.True(
            aircraft.IndicatedAirspeed > ClassBShelfSpeedLimitKts + 10.0,
            $"premise: two seconds of deceleration leaves the aircraft well above the cap, not at {aircraft.IndicatedAirspeed:F0} kt"
        );
        Assert.False(aircraft.Queue.Blocks[1].IsApplied, "the heading block fired while the speed reduction was still running");
    }

    /// <summary>14 CFR 91.117(c): the cap in the airspace underlying Class B.</summary>
    private const double ClassBShelfSpeedLimitKts = 200.0;

    /// <summary>The reported position in the issue #308 recording: laterally under the SFO Class B shelf, below its floor.</summary>
    private static readonly LatLon UnderSfoShelf = new(37.7387, -122.2474);

    /// <summary>Free-flying under the SFO Bravo shelf, where 91.117(c) allows this aircraft 200 kt and no more.</summary>
    private static AircraftState MakeAircraftUnderTheBravoShelf(double ias)
    {
        AircraftState aircraft = MakeAircraft(altitude: 1500, ias: ias);
        aircraft.Position = UnderSfoShelf;
        aircraft.TrueHeading = new TrueHeading(292.3);
        aircraft.TrueTrack = new TrueHeading(292.3);
        aircraft.Targets.TargetTrueHeading = new TrueHeading(292.3);
        Assert.Equal(ClassBShelfSpeedLimitKts, FlightPhysics.RegulatorySpeedLimit(aircraft));
        return aircraft;
    }
}
