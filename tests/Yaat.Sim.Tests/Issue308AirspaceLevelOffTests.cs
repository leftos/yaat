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
        AircraftState ac = ClimbingDeparture();

        AirspaceBoundaryCrossing? crossing = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, lookaheadSeconds: 60);

        Assert.NotNull(crossing);
        Assert.Equal(AirspaceClass.Bravo, crossing.Volume.Class);
        Assert.Equal(2100, crossing.Volume.LowerFtMsl);
        Assert.True(crossing.Volume.ContainsLateral(ac.Position), "the departure track runs beneath the shelf, not toward its ring");
    }

    [Fact]
    public void VerticalPierce_SelectsLevelOffAndCapsBelowTheFloor()
    {
        AircraftState ac = ClimbingDeparture();

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        AirspaceBoundaryHoldPhase phase = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(ac.Phases!.Phases));
        Assert.Equal(AirspaceHoldMode.LevelOff, phase.Mode);
        Assert.Equal(2000, phase.LevelOffCeilingFtMsl);
    }

    [Fact]
    public void LevelOff_CapsTheClimbWithoutTurningOrSlowing()
    {
        AircraftState ac = ClimbingDeparture();
        TrueHeading? originalHeading = ac.Targets.TargetTrueHeading;

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
        AircraftState ac = ClimbingDeparture();
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        PhaseContext ctx = Context(ac);
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
        AircraftState ac = ClimbingDeparture();
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        PhaseContext ctx = Context(ac);
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
        AircraftState ac = ClimbingDeparture();
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        PhaseContext ctx = Context(ac);
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
        AircraftState ac = Airborne(new LatLon(37.7213, -122.4200), trueHeading: 90, altitude: 2000, ias: 600);
        ac.HasMadeInitialContact = true;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        AirspaceBoundaryHoldPhase phase = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(ac.Phases!.Phases));
        Assert.Equal(AirspaceHoldMode.Orbit, phase.Mode);
        Assert.True(phase.ManagesSpeed);
    }

    [Fact]
    public void Orbit_TurnsAwayFromTheBoundaryNotIntoIt()
    {
        // Tracking east at the OAK Class C: the boundary lies ahead and slightly right of the nose,
        // so the avoidance turn must go left. The hardcoded right turn used to swing the nose across it.
        AircraftState ac = Airborne(new LatLon(37.7500, -122.4200), trueHeading: 90, altitude: 2000, ias: 600);
        ac.HasMadeInitialContact = true;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        double bearingToBoundary = GeoMath.BearingTo(ac.Position, PhaseIntersection(ac));
        double relative = GeoMath.SignedBearingDifference(ac.TrueTrack.Degrees, bearingToBoundary);
        TurnDirection expected = relative >= 0 ? TurnDirection.Left : TurnDirection.Right;
        Assert.Equal(expected, phase.OrbitDirection);
    }

    [Fact]
    public void Orbit_EndsWhenTheControllerVectors()
    {
        AircraftState ac = Airborne(new LatLon(37.7213, -122.4200), trueHeading: 90, altitude: 2000, ias: 600);
        ac.HasMadeInitialContact = true;
        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);
        var phase = (AirspaceBoundaryHoldPhase)ac.Phases!.Phases[0];
        PhaseContext ctx = Context(ac);
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
    public void LevelOffCeiling_IsTheHighestRoundHundredBelowTheFloor(int floorFtMsl, double surfaceElevationFt, int expected) =>
        Assert.Equal(expected, AirspaceAvoidance.LevelOffCeilingFt(floorFtMsl, magneticCourseDeg: 0, surfaceElevationFt));

    // Above 3000 AGL the level must conform to 14 CFR 91.159: eastbound is an odd thousand + 500,
    // westbound an even thousand + 500. Under a 6000 ft floor that is 5500 and 4500, not 5900.
    [Theory]
    [InlineData(6000, 90, 5500)]
    [InlineData(6000, 270, 4500)]
    [InlineData(4000, 0, 3500)]
    public void LevelOffCeiling_ConformsToHemisphericRuleAbove3000Agl(int floorFtMsl, double magneticCourse, int expected) =>
        Assert.Equal(expected, AirspaceAvoidance.LevelOffCeilingFt(floorFtMsl, magneticCourse, surfaceElevationFt: 0));

    [Fact]
    public void AssignedAltitudeThroughTheShelf_DrawsAnUnableWithACounterOffer()
    {
        AircraftState ac = ClimbingDeparture();
        // "CM 35" under a 2,100 ft shelf: a clearance the pilot cannot legally fly (AIM 5-5-6.a.3).
        ac.Targets.AssignedAltitude = 3500;

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        PilotTransmission transmission = Assert.Single(ac.PendingPilotTransmissions);
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
        AircraftState ac = ClimbingDeparture();

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(), AirspaceDatabase.Default, LookupAirport);

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    // 14 CFR 91.117(c): 200 kt in the airspace underlying Class B, not the 250 that applies below 10,000.
    [Fact]
    public void SpeedUnderTheBravoShelf_IsCappedAt200()
    {
        Assert.True(AirspaceDatabase.Default.IsUnderClassBShelf(UnderSfoShelf, altitudeFtMsl: 1500));
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1500, ias: 250);
        ac.AircraftType = "C25C";
        ac.Targets.TargetSpeed = 250;

        for (int i = 0; i < 60; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(ac.IndicatedAirspeed <= 200, $"expected the 200 kt underlying-Bravo limit to bite, got {ac.IndicatedAirspeed}");
    }

    [Fact]
    public void SpeedAboveTheBravoShelfFloor_KeepsTheOrdinary250Limit() =>
        // Inside the shelf's altitude band the aircraft is in Class B proper, where 91.117(c) does not apply.
        Assert.False(AirspaceDatabase.Default.IsUnderClassBShelf(UnderSfoShelf, altitudeFtMsl: 2500));

    /// <summary>
    /// 14 CFR 91.117 bends a speed assignment; it does not cancel it. The pilot complies with the cap "without
    /// notification" (7110.65 §5-7-2 NOTE 1) and only ATC terminates a speed adjustment (§5-7-4), so an aircraft
    /// stopped short of its assigned speed by the cap is still carrying that assignment.
    /// </summary>
    [Fact]
    public void TargetAboveTheCap_IsKeptWhenTheCappedSpeedIsReached()
    {
        AircraftState ac = JetBelow10k(ias: 230);
        ac.Targets.TargetSpeed = 280;

        Tick(ac, seconds: 60);

        Assert.Equal(Below10kSpeedLimitKts, ac.IndicatedAirspeed, 0.5);
        Assert.Equal(280, Assert.IsType<double>(ac.Targets.TargetSpeed), 0.5);
    }

    /// <summary>
    /// And it is the assignment that retires the target, not the cap: through 10,000 ft the aircraft takes up the
    /// speed it was given, reaches it, and only then has nothing left to fly to.
    /// </summary>
    [Fact]
    public void TargetAboveTheCap_IsReachedAndNulledOnceTheCapLifts()
    {
        AircraftState ac = JetBelow10k(ias: 230);
        ac.Targets.TargetSpeed = 280;
        Tick(ac, seconds: 60);
        Assert.Equal(Below10kSpeedLimitKts, ac.IndicatedAirspeed, 0.5); // premise: held down by 91.117(a)

        ac.Altitude = 10_500;

        Tick(ac, seconds: 60);

        Assert.Equal(280, ac.IndicatedAirspeed, 0.5);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    /// <summary>An assignment the aircraft may fly in full is still retired the moment it is flying it.</summary>
    [Fact]
    public void TargetAtOrBelowTheCap_StillNullsOnArrival()
    {
        AircraftState ac = JetBelow10k(ias: 230);
        ac.Targets.TargetSpeed = 240;

        Tick(ac, seconds: 60);

        Assert.Equal(240, ac.IndicatedAirspeed, 0.5);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// A <see cref="ControlTargets.SpeedCeiling"/> is not a regulatory cap: it is the last word on what this aircraft
    /// flies here, so arriving on it retires the target exactly as before.
    /// </summary>
    [Fact]
    public void CeilingClampedTarget_StillNullsOnArrival()
    {
        AircraftState ac = JetBelow10k(ias: 230);
        ac.Targets.TargetSpeed = 240;
        ac.Targets.SpeedCeiling = 220;

        Tick(ac, seconds: 60);

        Assert.Equal(220, ac.IndicatedAirspeed, 0.5);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// Both clamps at once, with the ceiling the tighter of the two: the aircraft flies the lower, and the assignment
    /// outlives both. It unwinds in the order the clamps lift — the ceiling first, then the cap — and only arriving on
    /// the assigned speed itself retires it, because nothing but ATC ends a speed adjustment (7110.65 §5-7-4).
    /// </summary>
    [Fact]
    public void TargetAboveTheCap_UnderALowerCeiling_UnwindsCeilingThenCap()
    {
        AircraftState ac = JetBelow10k(ias: 230);
        ac.Targets.TargetSpeed = 280;
        ac.Targets.SpeedCeiling = 220;

        Tick(ac, seconds: 60);

        Assert.Equal(220, ac.IndicatedAirspeed, 0.5);
        Assert.Equal(280, Assert.IsType<double>(ac.Targets.TargetSpeed), 0.5);
        // A chained SPD is finished in this state: the aircraft is flying the fastest speed allowed it here.
        Assert.True(FlightPhysics.IsSpeedAssignmentHeldAtRegulatoryLimit(ac), "the held assignment must read as complete at the clamped speed");

        ac.Targets.SpeedCeiling = null;
        Tick(ac, seconds: 60);

        Assert.Equal(Below10kSpeedLimitKts, ac.IndicatedAirspeed, 0.5);
        Assert.Equal(280, Assert.IsType<double>(ac.Targets.TargetSpeed), 0.5);

        ac.Altitude = 10_500;
        Tick(ac, seconds: 60);

        Assert.Equal(280, ac.IndicatedAirspeed, 0.5);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// Neither is the ground-conflict speed limit, which caps a taxiing aircraft that 91.117 never reaches at all
    /// (the cap is airborne-only). Arriving on it retires the target.
    /// </summary>
    [Fact]
    public void GroundSpeedLimitClampedTarget_StillNullsOnArrival()
    {
        AircraftState ac = Airborne(ClearOfClassB, trueHeading: 90, altitude: 20, ias: 0);
        ac.AircraftType = JetType;
        ac.IsOnGround = true;
        ac.Ground.SpeedLimit = 10;
        ac.Targets.TargetSpeed = 20;

        Tick(ac, seconds: 60);

        Assert.Equal(10, ac.IndicatedAirspeed, 0.5);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// The cap limits what the aircraft flies, not only what it may be told to fly. A pilot complies with 91.117(c)
    /// beneath a Class B shelf without being told to (7110.65 §5-7-2 NOTE 1; AIM 4-4-12.i), so an aircraft that comes
    /// under one faster than 200 kt with nothing left to fly to slows to it of its own accord.
    /// </summary>
    [Fact]
    public void StandingSpeedAboveTheCap_WithNoTarget_SlowsToTheCap()
    {
        AircraftState ac = JetUnderTheShelf(ias: 222);
        Assert.Null(ac.Targets.TargetSpeed); // premise: nothing is flying it to a speed

        Tick(ac, seconds: 30);

        Assert.Equal(ClassBShelfSpeedLimitKts, ac.IndicatedAirspeed, 0.5);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    /// <summary>The correction is to the lower of the cap and an active ceiling: a ceiling under the cap still binds.</summary>
    [Fact]
    public void StandingSpeedAboveTheCap_RespectsALowerSpeedCeiling()
    {
        AircraftState ac = JetUnderTheShelf(ias: 222);
        ac.Targets.SpeedCeiling = 190;

        Tick(ac, seconds: 30);

        Assert.Equal(190, ac.IndicatedAirspeed, 0.5);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// A <see cref="ControlTargets.SpeedFloor"/> above the cap does not hold the aircraft up there. 91.117 outranks an
    /// ATC "or greater", which is why the floor is already clamped to the cap where it mints a target of its own.
    /// </summary>
    [Fact]
    public void StandingSpeedAboveTheCap_IgnoresASpeedFloorAboveTheCap()
    {
        AircraftState ac = JetUnderTheShelf(ias: 222);
        ac.Targets.SpeedFloor = 210;

        Tick(ac, seconds: 30);

        Assert.Equal(ClassBShelfSpeedLimitKts, ac.IndicatedAirspeed, 0.5);
    }

    /// <summary>Inside the snap window there is nothing to correct — only a real overspeed mints a target.</summary>
    [Fact]
    public void StandingSpeedAtOrUnderTheCap_MintsNoTarget()
    {
        AircraftState atTheCap = JetUnderTheShelf(ias: 200);
        AircraftState insideTheSnapWindow = JetUnderTheShelf(ias: 201);

        Tick(atTheCap, seconds: 30);
        Tick(insideTheSnapWindow, seconds: 30);

        Assert.Equal(ClassBShelfSpeedLimitKts, atTheCap.IndicatedAirspeed, 0.5);
        Assert.Null(atTheCap.Targets.TargetSpeed);
        Assert.Equal(201, insideTheSnapWindow.IndicatedAirspeed, 0.5);
        Assert.Null(insideTheSnapWindow.Targets.TargetSpeed);
    }

    /// <summary>14 CFR 91.117 is an airborne limit; what a taxiing aircraft may do is the ground-conflict limit's business.</summary>
    [Fact]
    public void OnGround_NeverMintsARegulatoryTarget()
    {
        AircraftState ac = JetUnderTheShelf(ias: 222);
        ac.IsOnGround = true;
        ac.Altitude = 9;

        Tick(ac, seconds: 30);

        Assert.Null(ac.Targets.TargetSpeed);
        Assert.Equal(222, ac.IndicatedAirspeed, 0.5);
    }

    /// <summary>14 CFR 91.117(a) — the cap below 10,000 ft with no Class B shelf overhead.</summary>
    private const double Below10kSpeedLimitKts = 250.0;

    /// <summary>14 CFR 91.117(c) — the cap in the airspace underlying Class B.</summary>
    private const double ClassBShelfSpeedLimitKts = 200.0;

    /// <summary>A type with no 91.117(d) minimum-safe-speed waiver, so the cap applies to it in full.</summary>
    private const string JetType = "C25C";

    /// <summary>Well east of every Bay Area Class B footprint, so only the 250 kt below-10,000 cap applies.</summary>
    private static readonly LatLon ClearOfClassB = new(37.6258, -120.9544);

    /// <summary>A jet under the SFO Bravo shelf, where 91.117(c) caps it at 200 kt.</summary>
    private static AircraftState JetUnderTheShelf(double ias)
    {
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1500, ias: ias);
        ac.AircraftType = JetType;
        Assert.True(AirspaceDatabase.Default.IsUnderClassBShelf(ac.Position, ac.Altitude), "premise: 91.117(c)'s 200 kt is what applies here");
        return ac;
    }

    /// <summary>A jet out from under every shelf and below 10,000 ft, where 91.117(a)'s 250 kt is the only cap on it.</summary>
    private static AircraftState JetBelow10k(double ias)
    {
        AircraftState ac = Airborne(ClearOfClassB, trueHeading: 90, altitude: 8000, ias: ias);
        ac.AircraftType = JetType;
        Assert.False(
            AirspaceDatabase.Default.IsUnderClassBShelf(ac.Position, ac.Altitude),
            "premise: nothing but 91.117(a) may cap the aircraft here"
        );
        return ac;
    }

    private static void Tick(AircraftState ac, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }
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
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1485, ias: 80);
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
