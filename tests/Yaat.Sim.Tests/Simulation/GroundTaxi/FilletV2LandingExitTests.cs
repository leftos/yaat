using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Sim-level validation that landing rollout and runway-exit planning behave
/// correctly over V2 fillet geometry. Mirrors the OAK rollout scenarios in
/// <see cref="LandingExitDecelTests"/> but loads the layout via the V2 arc
/// generator (<see cref="FilletMode.V2"/>) instead of Legacy.
///
/// These exercise the geometry-sensitive parts of the exit pipeline — exit-node
/// survival after filleting, exit-angle classification (high-speed vs standard),
/// and exit-aware braking — which is exactly where tighter V2 arcs at runway-exit
/// junctions would surface. Production stays on Legacy; this is part of the gate
/// for flipping the default (docs/plans/filletv2/v2-sim-validation.md).
/// </summary>
public class FilletV2LandingExitTests
{
    private static AirportGroundLayout? LoadOakV2Layout() => new TestAirportGroundData(FilletMode.V2).GetLayout("OAK");

    private static RunwayInfo MakeRunway(string designator, double heading, double thresholdLat, double thresholdLon) =>
        TestRunwayFactory.Make(designator: designator, heading: heading, elevationFt: 9.0, thresholdLat: thresholdLat, thresholdLon: thresholdLon);

    private static AircraftState MakeLandedAircraft(double lat, double lon, double heading, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            Altitude = 9.0,
            IndicatedAirspeed = ias,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, AirportGroundLayout? layout, double dt = 1.0) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = 9.0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

    private static double SimulateRollout(LandingPhase phase, PhaseContext ctx, int maxTicks = 500)
    {
        int ticks = 0;
        while (ticks < maxTicks)
        {
            FlightPhysics.Update(ctx.Aircraft, ctx.DeltaSeconds);
            bool done = phase.OnTick(ctx);
            ticks++;
            if (done)
            {
                break;
            }
        }

        return ctx.Aircraft.IndicatedAirspeed;
    }

    [Fact]
    public void OAK28R_NoPreference_CompletesAtReasonableSpeed_OnV2()
    {
        var layout = LoadOakV2Layout();
        if (layout is null)
        {
            return;
        }

        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 130);
        ac.Phases!.AssignedRunway = rwy;
        var ctx = Ctx(ac, rwy, layout);

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        ac.IsOnGround = true;

        double finalSpeed = SimulateRollout(phase, ctx);

        // With a ground layout the pilot plans for the first reachable exit even
        // without an explicit preference; final speed lands at the exit turn-off
        // speed (15-30 kt for jets, coast 40 kt if no exit resolves). The V2 exit
        // geometry must keep this inside the same envelope as Legacy.
        Assert.InRange(finalSpeed, 0, 41);
    }

    [Fact]
    public void OAK28R_ExitFarAhead_MaintainsCoastSpeed_OnV2()
    {
        var layout = LoadOakV2Layout();
        if (layout is null)
        {
            return;
        }

        var rwy = MakeRunway("28R", 280.0, 37.724806, -122.204721);
        var ac = MakeLandedAircraft(37.724806, -122.204721, 280.0, ias: 60);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "H" };
        ac.Phases.AssignedRunway = rwy;

        var ctx = Ctx(ac, rwy, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        for (int i = 0; i < 5; i++)
        {
            phase.OnTick(ctx);
        }

        Assert.True(ctx.Aircraft.IndicatedAirspeed >= 39.0, $"Speed dropped to {ctx.Aircraft.IndicatedAirspeed:F1}, expected >= 39");
    }

    [Fact]
    public void ComputeExitAngle_OAK30_W5_IsHighSpeed_OnV2()
    {
        var layout = LoadOakV2Layout();
        if (layout is null)
        {
            return;
        }

        var hsNodes = layout.GetRunwayHoldShortNodes("30");
        var w5Node = hsNodes.FirstOrDefault(n => n.Edges.Any(e => e.TaxiwayName == "W5"));

        // Legacy keeps a 30/W5 high-speed-exit hold-short; if V2 fillets it away
        // that is a real regression, so assert presence rather than skip.
        Assert.NotNull(w5Node);

        double? angle = layout.ComputeExitAngle(w5Node, "W5", new TrueHeading(310.0));
        Assert.NotNull(angle);
        Assert.InRange(angle.Value, 0, 45);
    }

    [Fact]
    public void FindExitAhead_OAK28R_H_ReturnsIt_OnV2()
    {
        var layout = LoadOakV2Layout();
        if (layout is null)
        {
            return;
        }

        var result = layout.FindExitAheadOnRunway(37.724806, -122.204721, new TrueHeading(280.0), new ExitPreference { Taxiway = "H" }, "28R");
        Assert.NotNull(result);
    }

    [Fact]
    public void FindNearestExit_OAK28R_ReturnsExitOnCorrectRunway_OnV2()
    {
        var layout = LoadOakV2Layout();
        if (layout is null)
        {
            return;
        }

        var rwy28R = layout.FindGroundRunway("28R");
        Assert.NotNull(rwy28R);
        var coords = rwy28R.Coordinates;
        int midIdx = coords.Count / 2;

        var exitNode = layout.FindNearestExit(coords[midIdx].Lat, coords[midIdx].Lon, new TrueHeading(280.0), "28R");
        Assert.NotNull(exitNode);

        var rwy28L = layout.FindGroundRunway("28L");
        Assert.NotNull(rwy28L);

        double distTo28R = MinDistToRunwayNm(exitNode, rwy28R);
        double distTo28L = MinDistToRunwayNm(exitNode, rwy28L);
        Assert.True(
            distTo28R <= distTo28L,
            $"Exit node {exitNode.Id} at ({exitNode.Position.Lat:F6},{exitNode.Position.Lon:F6}) is closer to 28L ({distTo28L:F4}nm) than 28R ({distTo28R:F4}nm)"
        );
    }

    private static double MinDistToRunwayNm(GroundNode node, GroundRunway runway)
    {
        double minDist = double.MaxValue;
        foreach (var coord in runway.Coordinates)
        {
            double dist = GeoMath.DistanceNm(node.Position, new LatLon(coord.Lat, coord.Lon));
            if (dist < minDist)
            {
                minDist = dist;
            }
        }

        return minDist;
    }
}
