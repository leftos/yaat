using Xunit;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Issue #308: a VFR departure off KOAK 28R climbs under the SFO Class B shelf (2,100 ft MSL floor).
/// The boundary-respect tick used to install an orbiting hold that never capped the climb, so the
/// aircraft circled straight through the shelf floor and — because the hold's exit test only asked
/// whether the altitude sat inside the volume's band — could never leave the hold again.
///
/// A vertical pierce from directly underneath a shelf is answered by levelling off, not by turning:
/// no turn helps when the aircraft is already laterally inside the footprint (AIM 3-2-3.d.2.c).
/// </summary>
public sealed class Issue308AirspaceLevelOffTests
{
    // N436MS at t=160 in the reported recording: climbing through 1485 ft on the 28R departure track,
    // laterally under the SFO Bravo shelf, VFR cruise 3500 filed.
    private static readonly LatLon UnderSfoShelf = new(37.7387, -122.2474);

    [Fact]
    public void ReportedDeparture_IsUnderTheSfoBravoShelf()
    {
        var ac = ClimbingDeparture();

        var crossing = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, lookaheadSeconds: 60);

        Assert.NotNull(crossing);
        Assert.Equal(AirspaceClass.Bravo, crossing.Volume.Class);
        Assert.Equal(2100, crossing.Volume.LowerFtMsl);
        Assert.True(crossing.Volume.ContainsLateral(ac.Position), "the departure track runs beneath the shelf, not toward its ring");
    }

    [Fact]
    public void VerticalPierce_SelectsLevelOffAndCapsBelowTheFloor()
    {
        var ac = ClimbingDeparture();

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        var phase = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(ac.Phases!.Phases));
        Assert.Equal(AirspaceHoldMode.LevelOff, phase.Mode);
        Assert.Equal(2000, phase.LevelOffCeilingFtMsl);
    }

    [Fact]
    public void LevelOff_CapsTheClimbWithoutTurningOrSlowing()
    {
        var ac = ClimbingDeparture();
        var originalHeading = ac.Targets.TargetTrueHeading;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        phase.OnStart(Context(ac));

        Assert.Equal(2000, ac.Targets.AltitudeCeiling);
        Assert.Equal(originalHeading?.Degrees, ac.Targets.TargetTrueHeading?.Degrees);
        Assert.Null(ac.Targets.PreferredTurnDirection);
        // The route survives — the aircraft stays on course beneath the shelf.
        Assert.Single(ac.Targets.NavigationRoute);
        // No holding-speed cap: a VFR aircraft levelling under a shelf keeps cruise speed.
        Assert.False(phase.ManagesSpeed);
    }

    [Fact]
    public void LevelOff_HoldsWhileStillUnderTheShelf()
    {
        var ac = ClimbingDeparture();
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        var ctx = Context(ac);
        phase.OnStart(ctx);

        // Level at the cap: the self-imposed ceiling must not read as "no longer projected to enter"
        // and end the hold, or the climb resumes and the pair oscillates every tick.
        ac.Altitude = 2000;
        ac.VerticalSpeed = 0;

        Assert.False(phase.OnTick(ctx));
        Assert.Equal(2000, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void LevelOff_EndsOnBravoClearanceAndRestoresTheCruiseClimb()
    {
        var ac = ClimbingDeparture();
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        var ctx = Context(ac);
        phase.OnStart(ctx);

        // Physics wipes TargetAltitude when it captures the capped goal (FlightPhysics.UpdateAltitude).
        ac.Altitude = 2000;
        ac.Targets.TargetAltitude = null;

        ac.IsClearedIntoBravo = true;
        Assert.True(phase.OnTick(ctx));
        phase.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Null(ac.Targets.AltitudeCeiling);
        Assert.Equal(3500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void LevelOff_EndsWhenTheControllerAssignsAnAltitude()
    {
        var ac = ClimbingDeparture();
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        var ctx = Context(ac);
        phase.OnStart(ctx);

        // "CM 35" nulls AltitudeCeiling — the controller has taken responsibility for the shelf.
        ac.Targets.AltitudeCeiling = null;
        ac.Targets.TargetAltitude = 3500;
        ac.Targets.AssignedAltitude = 3500;

        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void LateralCrossing_StillOrbits()
    {
        // Well west of the OAK Class C at shelf altitude, tracking east toward its ring.
        var ac = Airborne(new LatLon(37.7213, -122.4200), trueHeading: 90, altitude: 2000, ias: 600);
        ac.HasMadeInitialContact = true;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        var phase = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(ac.Phases!.Phases));
        Assert.Equal(AirspaceHoldMode.Orbit, phase.Mode);
        Assert.True(phase.ManagesSpeed);
    }

    [Fact]
    public void Orbit_TurnsAwayFromTheBoundaryNotIntoIt()
    {
        // Tracking east at the OAK Class C: the boundary lies ahead and slightly right of the nose,
        // so the avoidance turn must go left. The hardcoded right turn used to swing the nose across it.
        var ac = Airborne(new LatLon(37.7500, -122.4200), trueHeading: 90, altitude: 2000, ias: 600);
        ac.HasMadeInitialContact = true;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        double bearingToBoundary = GeoMath.BearingTo(ac.Position, PhaseIntersection(ac));
        double relative = GeoMath.SignedBearingDifference(ac.TrueTrack.Degrees, bearingToBoundary);
        var expected = relative >= 0 ? TurnDirection.Left : TurnDirection.Right;
        Assert.Equal(expected, phase.OrbitDirection);
    }

    [Fact]
    public void Orbit_EndsWhenTheControllerVectors()
    {
        var ac = Airborne(new LatLon(37.7213, -122.4200), trueHeading: 90, altitude: 2000, ias: 600);
        ac.HasMadeInitialContact = true;
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        var ctx = Context(ac);
        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));

        // "FH 270" — the controller is now responsible for keeping the aircraft clear, so the pilot's
        // self-restriction stands down instead of orbiting through the vector forever.
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(270);

        Assert.True(phase.OnTick(ctx));
    }

    // The charted floor is inclusive and Mode C is quantized to 100 ft, so the level is the highest round
    // hundred strictly below the floor — never floor-minus-one.
    // Each case sits inside 3000 AGL, where 91.159 does not bind.
    [Theory]
    [InlineData(2100, 0, 2000)]
    [InlineData(1500, 0, 1400)]
    [InlineData(4000, 1000, 3900)]
    public void LevelOffCeiling_IsTheHighestRoundHundredBelowTheFloor(int floorFtMsl, double surfaceElevationFt, int expected)
    {
        Assert.Equal(expected, AirspaceAvoidance.LevelOffCeilingFt(floorFtMsl, magneticCourseDeg: 0, surfaceElevationFt));
    }

    // Above 3000 AGL the level must conform to 14 CFR 91.159: eastbound is an odd thousand + 500,
    // westbound an even thousand + 500. Under a 6000 ft floor that is 5500 and 4500, not 5900.
    [Theory]
    [InlineData(6000, 90, 5500)]
    [InlineData(6000, 270, 4500)]
    [InlineData(4000, 0, 3500)]
    public void LevelOffCeiling_ConformsToHemisphericRuleAbove3000Agl(int floorFtMsl, double magneticCourse, int expected)
    {
        Assert.Equal(expected, AirspaceAvoidance.LevelOffCeilingFt(floorFtMsl, magneticCourse, surfaceElevationFt: 0));
    }

    [Fact]
    public void AssignedAltitudeThroughTheShelf_DrawsAnUnableWithACounterOffer()
    {
        var ac = ClimbingDeparture();
        // "CM 35" under a 2,100 ft shelf: a clearance the pilot cannot legally fly (AIM 5-5-6.a.3).
        ac.Targets.AssignedAltitude = 3500;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        var transmission = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains("unable 3500", transmission.Text);
        Assert.Contains("bravo", transmission.Text);
        Assert.Contains("we can do 2000", transmission.Text);
        Assert.Contains("november four three six mike sierra", transmission.SpeechText);
        // The pilot does not comply: the cap stays on until the controller changes the assignment.
        Assert.Equal(2000, ((AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0]).LevelOffCeilingFtMsl);
    }

    [Fact]
    public void PilotChosenAltitudeThroughTheShelf_StaysSilent()
    {
        // No ATC assignment — the VFR aircraft is climbing to its own filed cruise, so there is no
        // clearance to refuse and nothing to say (issue #154).
        var ac = ClimbingDeparture();

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    // 14 CFR 91.117(c): 200 kt in the airspace underlying Class B, not the 250 that applies below 10,000.
    [Fact]
    public void SpeedUnderTheBravoShelf_IsCappedAt200()
    {
        Assert.True(AirspaceDatabase.Default.IsUnderClassBShelf(UnderSfoShelf, altitudeFtMsl: 1500));
        var ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1500, ias: 250);
        ac.AircraftType = "C25C";
        ac.Targets.TargetSpeed = 250;

        for (int i = 0; i < 60; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(ac.IndicatedAirspeed <= 200, $"expected the 200 kt underlying-Bravo limit to bite, got {ac.IndicatedAirspeed}");
    }

    [Fact]
    public void SpeedAboveTheBravoShelfFloor_KeepsTheOrdinary250Limit()
    {
        // Inside the shelf's altitude band the aircraft is in Class B proper, where 91.117(c) does not apply.
        Assert.False(AirspaceDatabase.Default.IsUnderClassBShelf(UnderSfoShelf, altitudeFtMsl: 2500));
    }

    [Fact]
    public void AwayFrom_TurnsTheNoseAwayFromTheBoundary()
    {
        var position = new LatLon(37.0, -122.0);
        var trackingNorth = new TrueHeading(0);

        Assert.Equal(TurnDirection.Left, AirspaceAvoidance.AwayFrom(trackingNorth, position, new LatLon(37.0, -121.9)));
        Assert.Equal(TurnDirection.Right, AirspaceAvoidance.AwayFrom(trackingNorth, position, new LatLon(37.0, -122.1)));
    }

    [Fact]
    public void LevelOffCeiling_IsRefusedUnderASurfaceArea()
    {
        // A Class B/C surface area has no flyable airspace beneath it (14 CFR 91.119).
        Assert.Null(AirspaceAvoidance.LevelOffCeilingFt(volumeFloorFtMsl: 0, magneticCourseDeg: 90, surfaceElevationFt: 0));
        Assert.Null(AirspaceAvoidance.LevelOffCeilingFt(volumeFloorFtMsl: 600, magneticCourseDeg: 90, surfaceElevationFt: 0));
    }

    private static LatLon PhaseIntersection(AircraftState ac) =>
        AirspaceDatabase.Default.FindFirstProjectedEntry(ac, lookaheadSeconds: 60)!.Intersection;

    private static AircraftState ClimbingDeparture()
    {
        var ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1485, ias: 80);
        ac.VerticalSpeed = 926;
        ac.Targets.TargetAltitude = 3500;
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = "MOD", Position = new LatLon(37.6258, -120.9544) });
        ac.HasMadeInitialContact = true;
        ac.HasControllerAcknowledgedInitialContact = true;
        return ac;
    }

    private static AircraftState Airborne(LatLon position, double trueHeading, double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C182",
            Position = position,
            TrueHeading = new TrueHeading(trueHeading),
            TrueTrack = new TrueHeading(trueHeading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                HasFlightPlan = true,
                Departure = "KOAK",
                Destination = "KMOD",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(trueHeading);
        return ac;
    }

    private static SimScenarioState SoloScenario() =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = "OAK",
            StudentPositionType = "TWR",
        };

    private static PhaseContext Context(AircraftState ac) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "TWR",
        };

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => new LatLon(37.6213, -122.3790),
            _ => null,
        };
}
