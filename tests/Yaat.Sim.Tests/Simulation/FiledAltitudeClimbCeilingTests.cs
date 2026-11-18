using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A departure with no assigned altitude and no SID top altitude climbs to the altitude as filed when the aircraft was
/// created (<c>AircraftState.FiledCruiseAltitudeFt</c>); a later <c>CRUISE</c>/<c>QZ</c>/STARS <c>++</c> amendment of
/// <see cref="AircraftFlightPlan.Altitude"/> stays data only and never changes the climb. An aircraft created with no plan
/// takes its altitude from the filing that creates the plan. Driven through the engine: a scenario spawn at KOAK on W,
/// taxied to the runway 30 bar, cleared with a bare <c>CTO</c> on HUSSH2, which the CIFP gives no top altitude and the
/// (absent) ARTCC config no initial altitude.
/// </summary>
public class FiledAltitudeClimbCeilingTests(ITestOutputHelper output)
{
    private const string Callsign = "UAL1402";
    private const string Route = "HUSSH2 SYRAH";

    /// <summary>An RPO filing at 9,000 that carries a route, so it rebuilds a pending initial climb.</summary>
    private const string FpWithRoute = "FP B738/L 090 OAK HUSSH2 SYRAH LAX";

    /// <summary>
    /// The IFR no-altitude fallback (<see cref="InitialClimbPhase"/>, step 6 of its target resolution): 1,500 ft above the
    /// field elevation the phase is given. The KOAK value is pinned from a run of today's code.
    /// </summary>
    private const double FallbackTargetFt = 1509;

    /// <summary>How long the climb may take from liftoff to pass 17,500 ft (or 8,900 ft) before the test gives up.</summary>
    private const int ClimbBudgetSeconds = 600;

    /// <summary>On KOAK taxiway W, north-west of the W/W1/W2 junction, facing it.</summary>
    private static readonly LatLon SpawnPose = new(37.70536, -122.21632);

    private const double SpawnHeadingTrue = 131.0;

    /// <summary>
    /// Filed 35,000 at spawn, then <c>CRUISE 170</c> while holding short, then a bare <c>CTO</c>: the aircraft climbs
    /// through 17,500 on its way to the filed 35,000, and the plan's altitude still reads the amended 17,000.
    /// </summary>
    [Fact]
    public void CruiseAmendedBeforeTakeoff_ClimbsToTheAltitudeFiledAtSpawn()
    {
        if (HoldingShort(FiledIfr(35000)) is not { } engine)
        {
            return;
        }

        Send(engine, "CRUISE 170");
        ClearForTakeoffOnHussh2(engine);

        Assert.Equal(35000, AwaitInitialClimb(engine).CruiseAltitude);
        FlyUntil(engine, ac => !ac.IsOnGround, 180);
        double max = FlyUntil(engine, ac => ac.Altitude > 17500, ClimbBudgetSeconds);
        Assert.True(max > 17500, $"levelled at the amended 17,000 instead of the filed 35,000: max {max:F0} ft");
        Assert.Equal(17000, Aircraft(engine).FlightPlan.Altitude.CruiseFeet);
    }

    /// <summary>(a) The amendment arrives after the departure phases are built, airborne below 1,500 AGL: the ceiling stays.</summary>
    [Fact]
    public void CruiseAmendedAfterTheDeparturePhaseIsBuilt_DoesNotLowerTheCeiling()
    {
        if (HoldingShort(FiledIfr(35000)) is not { } engine)
        {
            return;
        }

        ClearForTakeoffOnHussh2(engine);
        FlyUntil(engine, ac => !ac.IsOnGround, 180);
        Assert.True(Aircraft(engine).Altitude < 1500, $"already at {Aircraft(engine).Altitude:F0} ft when the amendment was due");
        Send(engine, "CRUISE 170");

        double max = FlyUntil(engine, ac => ac.Altitude > 17500, ClimbBudgetSeconds);
        Assert.True(max > 17500, $"levelled at the amended 17,000 instead of the filed 35,000: max {max:F0} ft");
        Assert.Equal(17000, Aircraft(engine).FlightPlan.Altitude.CruiseFeet);
    }

    /// <summary>
    /// (b) The altitude amended, then the route re-filed, which rebuilds the pending initial climb
    /// (<c>RefreshPendingInitialClimbPhases</c>): the rebuilt climb still goes to the filed 35,000.
    /// </summary>
    [Fact]
    public void CruiseAmendedThenRouteRefiled_StillClimbsToTheAltitudeFiledAtSpawn()
    {
        if (HoldingShort(FiledIfr(35000)) is not { } engine)
        {
            return;
        }

        ClearForTakeoffOnHussh2(engine);
        Send(engine, "CRUISE 170");
        CommandResult refiled = engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, Route: Route));
        Assert.True(refiled.Success, refiled.Message);

        Assert.Equal(35000, AwaitInitialClimb(engine).CruiseAltitude);
        FlyUntil(engine, ac => !ac.IsOnGround, 180);
        double max = FlyUntil(engine, ac => ac.Altitude > 17500, ClimbBudgetSeconds);
        Assert.True(max > 17500, $"levelled at the amended 17,000 instead of the filed 35,000: max {max:F0} ft");
    }

    /// <summary>
    /// (c) Spawned with no flight plan, filed by RPO <c>FP</c> at 9,000, then <c>CRUISE 050</c>: the filing that created the
    /// plan set the ceiling, so the aircraft climbs past 5,000 and levels at 9,000.
    /// </summary>
    [Fact]
    public void SpawnedWithoutAPlan_FiledByFp_ClimbsToTheFiledAltitude_NotALaterAmendment()
    {
        if (HoldingShort(flightplanJson: null) is not { } engine)
        {
            return;
        }

        Assert.False(Aircraft(engine).FlightPlan.HasFlightPlan);
        Send(engine, "FP B738/L 090 OAK LAX");
        Assert.Equal(9000, Aircraft(engine).FlightPlan.Altitude.CruiseFeet);
        Send(engine, "CRUISE 050");
        Send(engine, "CTO");

        Assert.Equal(9000, AwaitInitialClimb(engine).CruiseAltitude);
        FlyUntil(engine, ac => !ac.IsOnGround, 180);
        double max = FlyUntil(engine, ac => ac.Altitude > 8900, ClimbBudgetSeconds);
        Assert.True(max > 8900, $"levelled at the amended 5,000 instead of the filed 9,000: max {max:F0} ft");
        double levelled = FlyUntil(engine, _ => false, 60);
        Assert.InRange(levelled, 8950, 9050);
        Assert.InRange(Aircraft(engine).Altitude, 8950, 9050);
        Assert.Equal(5000, Aircraft(engine).FlightPlan.Altitude.CruiseFeet);
    }

    /// <summary>
    /// (d) Filed IFR with no altitude at spawn and none filed since; a <c>CRUISE 170</c> is data only, so the initial climb
    /// takes the no-altitude fallback (<see cref="InitialClimbPhase"/>: cruise 0, an IFR self-clear at 1,500 ft above the
    /// field), never the amended 17,000.
    /// </summary>
    [Fact]
    public void NoAltitudeAtSpawn_TakesTheNoAltitudeFallback_NotTheAmendedAltitude()
    {
        if (HoldingShort(FiledIfr(0)) is not { } engine)
        {
            return;
        }

        Assert.Null(Aircraft(engine).FlightPlan.Altitude.CruiseFeet);
        Send(engine, "CRUISE 170");
        ClearForTakeoffOnHussh2(engine);

        InitialClimbPhase climb = StartInitialClimb(engine);
        AircraftState ac = Aircraft(engine);
        output.WriteLine($"initial climb target {ac.Targets.TargetAltitude} ft, cruise {climb.CruiseAltitude}");
        Assert.Equal(0, climb.CruiseAltitude);
        Assert.Equal(FallbackTargetFt, ac.Targets.TargetAltitude);
    }

    /// <summary>
    /// A planless aircraft cleared for takeoff, then filed by <c>FP</c> with a route while the takeoff phases wait: the route
    /// rebuilds the pending initial climb (<c>RefreshPendingInitialClimbPhases</c>), and the rebuilt climb takes the filing's
    /// 9,000, not the no-altitude fallback.
    /// </summary>
    [Fact]
    public void SpawnedWithoutAPlan_ClearedThenFiledWithARoute_PendingClimbTakesTheFiledAltitude()
    {
        if (HoldingShort(flightplanJson: null) is not { } engine)
        {
            return;
        }

        Send(engine, "CTO");
        Send(engine, FpWithRoute);

        Assert.Equal(Route, Aircraft(engine).FlightPlan.Route);
        Assert.Equal(9000, Aircraft(engine).FiledCruiseAltitudeFt);
        Assert.Equal(9000, AwaitInitialClimb(engine).CruiseAltitude);
    }

    /// <summary>
    /// A planless aircraft given an altitude (<c>CRUISE 170</c>) and then a route-only amendment has filed no altitude: the
    /// route is a filing, but it carries none, so the pilot's altitude stays unset rather than adopting the data-only 17,000.
    /// The <c>FP</c> filed after it carries 9,000 and sets it.
    /// </summary>
    [Fact]
    public void SpawnedWithoutAPlan_CruiseThenRouteOnlyAmendment_FilesNoAltitude_TheLaterFpDoes()
    {
        if (HoldingShort(flightplanJson: null) is not { } engine)
        {
            return;
        }

        Send(engine, "CRUISE 170");
        CommandResult routed = engine.AmendFlightPlan(Callsign, new FlightPlanAmendment(ClearBeaconCode: false, Route: Route));
        Assert.True(routed.Success, routed.Message);
        Assert.Equal(17000, Aircraft(engine).FlightPlan.Altitude.CruiseFeet);
        Assert.Null(Aircraft(engine).FiledCruiseAltitudeFt);

        Send(engine, FpWithRoute);
        Assert.Equal(9000, Aircraft(engine).FiledCruiseAltitudeFt);
    }

    /// <summary>
    /// A planless aircraft given only an altitude (<c>CRUISE 170</c>) has filed nothing: it is cleared for takeoff on the
    /// no-altitude fallback. The <c>FP</c> filed after it is the first filing and sets the climb to its 9,000.
    /// </summary>
    [Fact]
    public void SpawnedWithoutAPlan_AltitudeOnlyAmendmentFilesNothing_TheLaterFpDoes()
    {
        if (HoldingShort(flightplanJson: null) is not { } engine)
        {
            return;
        }

        Send(engine, "CRUISE 170");
        Assert.Null(Aircraft(engine).FiledCruiseAltitudeFt);
        Send(engine, "CTO");
        Assert.Equal(0, AwaitInitialClimb(engine).CruiseAltitude);

        Send(engine, FpWithRoute);
        Assert.Equal(9000, Aircraft(engine).FiledCruiseAltitudeFt);
        Assert.Equal(9000, AwaitInitialClimb(engine).CruiseAltitude);
    }

    /// <summary>
    /// A planless aircraft cleared for takeoff, then filed by an amendment that carries an aircraft type and an altitude
    /// but no route (an ERAM <c>AM</c> of <c>TYP</c> and <c>ALT</c>): the filing sets the pilot's altitude, and the pending
    /// initial climb takes the filing's 9,000 rather than keeping the no-altitude fallback.
    /// </summary>
    [Fact]
    public void SpawnedWithoutAPlan_ClearedThenFiledWithoutARoute_PendingClimbTakesTheFiledAltitude()
    {
        if (HoldingShort(flightplanJson: null) is not { } engine)
        {
            return;
        }

        Send(engine, "CTO");
        Assert.Equal(0, AwaitInitialClimb(engine).CruiseAltitude);
        var amendment = new FlightPlanAmendment(ClearBeaconCode: false, AircraftType: "B738", Altitude: PlannedAltitude.Ifr(9000));
        CommandResult filed = engine.AmendFlightPlan(Callsign, amendment);
        Assert.True(filed.Success, filed.Message);

        Assert.Equal(9000, Aircraft(engine).FiledCruiseAltitudeFt);
        Assert.Equal(9000, AwaitInitialClimb(engine).CruiseAltitude);
    }

    /// <summary>
    /// A planless aircraft cleared for takeoff, then filed VFR at 6,500 with a route (a <c>VP</c>-style filing): a VFR plan
    /// resolves no departure route, so the route refresh leaves the pending climb alone, and the pending initial climb still
    /// takes the filing's 6,500.
    /// </summary>
    [Fact]
    public void SpawnedWithoutAPlan_ClearedThenFiledVfrWithARoute_PendingClimbTakesTheFiledAltitude()
    {
        if (HoldingShort(flightplanJson: null) is not { } engine)
        {
            return;
        }

        Send(engine, "CTO");
        Assert.Equal(0, AwaitInitialClimb(engine).CruiseAltitude);
        var amendment = new FlightPlanAmendment(
            ClearBeaconCode: false,
            AircraftType: "B738",
            Departure: "KOAK",
            Destination: "KLAX",
            Altitude: PlannedAltitude.Vfr(6500),
            FlightRules: "VFR",
            Route: "SYRAH"
        );
        CommandResult filed = engine.AmendFlightPlan(Callsign, amendment);
        Assert.True(filed.Success, filed.Message);

        Assert.Equal(6500, Aircraft(engine).FiledCruiseAltitudeFt);
        Assert.Equal(6500, AwaitInitialClimb(engine).CruiseAltitude);
    }

    /// <summary>
    /// A planless aircraft cleared for takeoff, then filed VFR at 6,500 without a route: the refresh of the pending initial
    /// climb takes the flight rules from the plan, so the climb is VFR as well as taking the filing's 6,500.
    /// </summary>
    [Fact]
    public void SpawnedWithoutAPlan_ClearedThenFiledVfrWithoutARoute_PendingClimbIsVfr()
    {
        if (HoldingShort(flightplanJson: null) is not { } engine)
        {
            return;
        }

        Send(engine, "CTO");
        var amendment = new FlightPlanAmendment(
            ClearBeaconCode: false,
            AircraftType: "B738",
            Altitude: PlannedAltitude.Vfr(6500),
            FlightRules: "VFR"
        );
        CommandResult filed = engine.AmendFlightPlan(Callsign, amendment);
        Assert.True(filed.Success, filed.Message);

        InitialClimbPhase climb = AwaitInitialClimb(engine);
        Assert.True(climb.IsVfr);
        Assert.Equal(6500, climb.CruiseAltitude);
    }

    /// <summary>
    /// The reverse: an aircraft on a VFR plan with no altitude, cleared for takeoff, then filed IFR at 9,000 without a route:
    /// the refreshed pending initial climb is no longer VFR.
    /// </summary>
    [Fact]
    public void FiledVfrWithoutAnAltitude_ClearedThenFiledIfrWithoutARoute_PendingClimbIsNotVfr()
    {
        if (HoldingShort(flightplanJson: VfrWithoutAnAltitude) is not { } engine)
        {
            return;
        }

        Send(engine, "CTO");
        Assert.Null(Aircraft(engine).FiledCruiseAltitudeFt);
        Assert.True(AwaitInitialClimb(engine).IsVfr);
        var amendment = new FlightPlanAmendment(
            ClearBeaconCode: false,
            AircraftType: "B738",
            Altitude: PlannedAltitude.Ifr(9000),
            FlightRules: "IFR"
        );
        CommandResult filed = engine.AmendFlightPlan(Callsign, amendment);
        Assert.True(filed.Success, filed.Message);

        InitialClimbPhase climb = AwaitInitialClimb(engine);
        Assert.False(climb.IsVfr);
        Assert.Equal(9000, climb.CruiseAltitude);
    }

    /// <summary>A VFR plan that files no altitude, so the aircraft spawns with no pilot's filed altitude.</summary>
    private const string VfrWithoutAnAltitude = """
        {"rules": "VFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 0, "cruiseSpeed": 120,
         "route": "", "remarks": "", "aircraftType": "B738/L"}
        """;

    private static string FiledIfr(int cruiseAltitudeFt) =>
        $$"""
            {"rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": {{cruiseAltitudeFt}}, "cruiseSpeed": 450,
             "route": "{{Route}}", "remarks": "", "aircraftType": "B738/L"}
            """;

    private static string ScenarioJson(string? flightplanJson, int headingMag)
    {
        string flightplan = flightplanJson is null ? "" : $"\"flightplan\": {flightplanJson},";
        return $$"""
            {
              "id": "filed-altitude-climb-ceiling",
              "name": "Filed altitude climb ceiling",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "initializationTriggers": [],
              "aircraftGenerators": [],
              "aircraft": [
                {
                  "id": "filed-altitude-1",
                  "aircraftId": "{{Callsign}}",
                  "aircraftType": "B738",
                  "transponderMode": "C",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": {"lat": {{SpawnPose.Lat}}, "lon": {{SpawnPose.Lon}}},
                    "heading": {{headingMag}}
                  },
                  "onAltitudeProfile": false,
                  {{flightplan}}
                  "spawnDelay": 0,
                  "airportId": "OAK",
                  "difficulty": "Easy"
                }
              ],
              "atc": [],
              "studentPositionId": "",
              "autoDeleteMode": "None",
              "flightStripConfigurations": []
            }
            """;
    }

    /// <summary>The KOAK engine with the aircraft taxied to the runway 30 bar, or null (after printing why) without the data.</summary>
    private SimulationEngine? HoldingShort(string? flightplanJson)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is null))
        {
            output.WriteLine("SKIP: navdata or the OAK ground layout is not available");
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        int headingMag = (int)
            Math.Round(MagneticDeclination.TrueToMagnetic(SpawnHeadingTrue, SpawnPose.Lat, SpawnPose.Lon, MagneticDeclination.EvaluationDateUtc));
        foreach (string warning in engine.LoadScenario(ScenarioJson(flightplanJson, headingMag), 42, MagneticDeclination.EvaluationDateUtc))
        {
            output.WriteLine($"[WARN] {warning}");
        }

        Send(engine, "TAXI W W1 30");
        FlyUntil(engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 180);
        Assert.IsType<HoldingShortPhase>(Aircraft(engine).Phases?.CurrentPhase);
        return engine;
    }

    /// <summary>
    /// A bare <c>CTO</c>, after checking the case rests on HUSSH2 putting no SID altitude in force: no climb via (no top
    /// altitude) and no stored SID initial altitude.
    /// </summary>
    private void ClearForTakeoffOnHussh2(SimulationEngine engine)
    {
        Send(engine, "CTO");
        AircraftState ac = Aircraft(engine);
        Assert.Null(FlightPhysics.ClimbViaAltitude(ac));
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    /// <summary>The initial climb the takeoff clearance built, still pending behind the line-up and the roll.</summary>
    private static InitialClimbPhase AwaitInitialClimb(SimulationEngine engine) =>
        Assert.Single(Assert.IsType<PhaseList>(Aircraft(engine).Phases).Phases.OfType<InitialClimbPhase>());

    /// <summary>
    /// Ticks physics sub-tick by sub-tick until the initial climb is the current phase: on a SID with coded legs it hands
    /// off to the procedure at the TERPS gate within a second of starting, so whole-second ticks can step over it.
    /// </summary>
    private static InitialClimbPhase StartInitialClimb(SimulationEngine engine)
    {
        for (int i = 0; (i < (4 * 180)) && (Aircraft(engine).Phases?.CurrentPhase is not InitialClimbPhase); i++)
        {
            engine.TickPhysics(1.0 / SimulationEngine.PhysicsSubTickRate);
        }

        return Assert.IsType<InitialClimbPhase>(Aircraft(engine).Phases?.CurrentPhase);
    }

    private void Send(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"{command} -> {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
    }

    /// <summary>Ticks whole seconds until <paramref name="until"/> holds or the budget runs out; returns the highest altitude flown.</summary>
    private double FlyUntil(SimulationEngine engine, Func<AircraftState, bool> until, int budgetSeconds)
    {
        double max = Aircraft(engine).Altitude;
        for (int t = 0; (t < budgetSeconds) && !until(Aircraft(engine)); t++)
        {
            engine.TickOneSecond();
            max = Math.Max(max, Aircraft(engine).Altitude);
        }

        AircraftState ac = Aircraft(engine);
        string phase = ac.Phases?.CurrentPhase?.Name ?? "(none)";
        output.WriteLine($"{phase} at {ac.Altitude:F0} ft, max {max:F0} ft, {ac.VerticalSpeed:F0} fpm to {ac.Targets.TargetAltitude}");
        return max;
    }

    private static AircraftState Aircraft(SimulationEngine engine) =>
        engine.FindAircraft(Callsign) ?? throw new InvalidOperationException($"{Callsign} left the world");
}
