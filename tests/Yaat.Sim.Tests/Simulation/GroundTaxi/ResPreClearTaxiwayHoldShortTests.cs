using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Bare <c>RES</c> to an aircraft taxiing toward a taxiway hold short it has not reached pre-clears that bar, the way bare
/// <c>CROSS</c> does: the aircraft taxis through the junction without holding short and without stopping.
///
/// <para>The rig: at KOAK a B738 taxis from GA16 via F, C, B to runway 28R, cleared across runway 33 and told to hold
/// short of taxiway E, which crosses C past the 33 crossing. RES is given while the aircraft rolls toward E: once well
/// before the bar, and once inside its taxi-rate stopping distance of the bar's painted stop, where the taxi phase is
/// already braking for it and has to be re-aimed past it.</para>
/// </summary>
public class ResPreClearTaxiwayHoldShortTests(ITestOutputHelper output)
{
    private const string Callsign = "N738SP";
    private const string Taxi = "TAXI F C B CROSS 33 RWY 28R HS E";
    private const string TaxiwayTarget = "E";
    private const int ApproachBudgetSeconds = 400;
    private const int PassBudgetSeconds = 300;

    /// <summary>The slowest (kt) the aircraft given RES as it starts off may roll after RES: it is given at 5 kt.</summary>
    private const double FarFloorKts = 4.0;

    /// <summary>
    /// The slowest (kt) the aircraft given RES while braking may roll on through the junction. RES lands at about 18 kt; a
    /// phase that kept braking for the bar would carry it down toward a stop.
    /// </summary>
    private const double BrakingFloorKts = 8.0;

    /// <summary>The least speed (kt) at which the aircraft counts as rolling toward the bar rather than starting off.</summary>
    private const double RollingKts = 5.0;

    /// <summary>
    /// How far (ft) beyond its taxi-rate stopping distance the painted stop may still be for the aircraft to count as braking
    /// for it: the taxi phase starts its taxi-rate stop a few feet early, so it brakes along a profile just outside it.
    /// </summary>
    private const double BrakingMarginFt = 10.0;

    private const string Scenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6RS",
          "name": "RES pre-clears a taxiway hold short",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH02N738SPG8F0L1W5T7R9RS",
              "aircraftId": "N738SP",
              "aircraftType": "B738",
              "transponderMode": "Standby",
              "startingConditions": { "type": "Parking", "parking": "GA16" },
              "onAltitudeProfile": false,
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KLVK",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 0,
                "route": "",
                "remarks": "/V/",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ],
          "atc": [
            {
              "id": "01G9CQW3XEE3RW84EBYKEBCPQD",
              "artccId": "ZOA",
              "facilityId": "OAK",
              "positionId": "01GEAMB98RKCPP9HCNPW5AVDA5",
              "autoConnect": true,
              "autoTrackAirportIds": []
            }
          ],
          "flightStripConfigurations": []
        }
        """;

    /// <summary>RES given as soon as the aircraft is rolling toward E, long before it brakes for the bar.</summary>
    [Fact]
    public void Res_PreClearsTaxiwayHoldShort_AircraftTaxisThroughWithoutStopping() =>
        Run(static approach => approach.SpeedKts >= RollingKts, FarFloorKts);

    /// <summary>
    /// RES given once the aircraft is braking for E's painted stop and has covered half its braking distance from taxi speed:
    /// the cleared bar re-aims the segment in progress, and the aircraft stops braking and re-accelerates through the junction.
    /// </summary>
    [Fact]
    public void Res_PreClearsTaxiwayHoldShort_InsideBrakingDistance_AircraftTaxisThroughWithoutStopping() =>
        Run(
            static approach =>
                (approach.SpeedKts >= RollingKts)
                && (approach.ToStopFt > 0.0)
                && (approach.ToStopFt <= approach.TaxiStopFt + BrakingMarginFt)
                && (approach.ToStopFt < 0.5 * approach.PeakTaxiStopFt),
            BrakingFloorKts
        );

    /// <summary>
    /// Where the aircraft is against E's painted stop when RES may be given: its speed, the distance to the stop, its taxi-rate
    /// stopping distance now, and the largest one it has had on the way (from its taxi speed).
    /// </summary>
    private sealed record Approach(double SpeedKts, double ToStopFt, double TaxiStopFt, double PeakTaxiStopFt);

    private void Run(Func<Approach, bool> when, double floorKts)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(Scenario, 1, MagneticDeclination.EvaluationDateUtc);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        CommandResult taxi = engine.SendCommand(Callsign, Taxi);
        Assert.True(taxi.Success, $"{Taxi} was rejected: {taxi.Message}");
        engine.TickOneSecond();

        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, hs => hs.TargetName == TaxiwayTarget);
        Assert.False(bar.IsCleared, $"{Taxi} left the hold short of {TaxiwayTarget} cleared");
        int barSegment = route.Segments.FindIndex(s => s.ToNodeId == bar.NodeId);
        Assert.True(barSegment >= 0, $"the hold short of {TaxiwayTarget} is on no segment of the route");

        double taxiDecelFtPerS2 = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet) * GeoMath.FeetPerNm / 3600.0;
        bool resGiven = false;
        double peakTaxiStopFt = 0.0;
        for (int second = 0; (second < ApproachBudgetSeconds) && !resGiven; second++)
        {
            if ((aircraft.Phases?.CurrentPhase is TaxiingPhase) && (route.CurrentSegmentIndex <= barSegment))
            {
                double speedFtPerS = aircraft.GroundSpeed * GeoMath.FeetPerNm / 3600.0;
                double taxiStopFt = speedFtPerS * speedFtPerS / (2.0 * taxiDecelFtPerS2);
                peakTaxiStopFt = Math.Max(peakTaxiStopFt, taxiStopFt);
                var approach = new Approach(
                    aircraft.GroundSpeed,
                    TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, bar),
                    taxiStopFt,
                    peakTaxiStopFt
                );
                if (when(approach))
                {
                    CommandResult res = engine.SendCommand(Callsign, "RES");
                    output.WriteLine($"RES at {approach}: success={res.Success} msg={res.Message}");
                    Assert.True(res.Success, $"RES was rejected: {res.Message}");
                    Assert.True(bar.IsCleared, $"RES left the hold short of {TaxiwayTarget} armed");
                    resGiven = true;
                }
            }

            StepSecond(engine, static () => { });
        }

        Assert.True(resGiven, $"the B738 never met the RES condition approaching taxiway {TaxiwayTarget}");

        double slowestKts = double.MaxValue;
        bool heldAtBar = false;
        void AfterSubTick()
        {
            slowestKts = Math.Min(slowestKts, aircraft.GroundSpeed);
            heldAtBar |= aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: TaxiwayTarget };
        }

        for (int second = 0; (second < PassBudgetSeconds) && (route.CurrentSegmentIndex <= barSegment); second++)
        {
            StepSecond(engine, AfterSubTick);
        }

        double pastJunctionKts = aircraft.GroundSpeed;
        output.WriteLine(
            $"after RES: slowest {slowestKts:F2} kt, {pastJunctionKts:F2} kt past the junction, segment {route.CurrentSegmentIndex} (bar on {barSegment})"
        );
        Assert.False(heldAtBar, $"the B738 held short of {TaxiwayTarget} after RES pre-cleared it");
        Assert.True(route.CurrentSegmentIndex > barSegment, $"the B738 never taxied past the {TaxiwayTarget} junction");
        Assert.True(
            slowestKts >= floorKts,
            $"the B738 slowed to {slowestKts:F2} kt at the pre-cleared {TaxiwayTarget} junction, below {floorKts:F1}"
        );
        Assert.True(pastJunctionKts > slowestKts, $"the B738 never re-accelerated after RES: {pastJunctionKts:F2} kt past the junction");
    }

    private static void StepSecond(SimulationEngine engine, Action afterSubTick)
    {
        engine.BeginSecond();
        engine.OpenSecond(engine.BareHost);
        engine.RunPrePhysics(engine.BareHost);
        for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
        {
            engine.RunPhysicsSubTick(1.0 / SimulationEngine.PhysicsSubTickRate, sub);
            afterSubTick();
        }

        engine.RunPostPhysics(engine.BareHost);
        engine.RunEndOfSecond(engine.BareHost);
    }
}
