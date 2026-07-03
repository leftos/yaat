using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// E2E for GitHub issue #240: a Cessna 150 spawned at OAK parking <c>GA9</c> and
/// cleared <c>TAXI D C B 28R</c> must not spin in place leaving the ramp.
///
/// <para>Root cause: <see cref="Yaat.Sim.Data.Airport.Pathfinding.SegmentExpander"/>'s
/// parking→taxiway bridge scored candidates by bias-proximity alone, discarding the
/// bridge path's own cost. GA9 connects only to node 1213, so reaching the southernmost
/// taxiway-D tie-in (nearest the D/C bias) meant threading the J400 ramp cross-connector
/// — a 19 ft / 3 kt arc that doubles back on itself (241.8° WSW → 159.7° SSE → 257.6° WSW).
/// The navigator faithfully follows the zigzag, so the aircraft appears to spin. A clean
/// straight bridge that continues WSW onto D exists and is both shorter and far smoother.</para>
///
/// <para>The fix scores bridge candidates as f = g + h (bridge path cost + bias proximity),
/// so the clean straight bridge wins. This test is synthetic (spawn + live SendCommand) so
/// it exercises the current pathfinder directly; it needs no recorded bundle.</para>
/// </summary>
public class Issue240Ga9TaxiSpinTests(ITestOutputHelper output)
{
    private const string Callsign = "N346G";
    private const string AircraftType = "C150";
    private const string AirportId = "OAK";
    private const string SpotName = "GA9";
    private const string Command = "TAXI D C B 28R";

    // Cumulative absolute heading change along the resolved route's first stretch off
    // the ramp. Pre-fix the bridge zigzags ~227° in the first ~350 ft; a clean ramp exit
    // (turn onto D via one fillet) stays under ~40°. 90° is a wide-margin discriminator.
    private const double RampExitWindowFt = 400.0;
    private const double MaxRampExitTurnDeg = 90.0;

    // Behavioural backstop over a short tick window: confirm the aircraft actually taxis
    // off the ramp rather than milling near the spot, and never stalls outside a legitimate
    // stop. The route-cleanliness check above is the timing-independent spin discriminator;
    // this loop also runs the navigator's orbit guard (ThrowOnOrbit) for free.
    private const int BehaviourWindowSec = 60;
    private const double MinForwardProgressFt = 400.0;
    private const int MaxZeroProgressSec = 30;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("SegmentExpander", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Ga9TaxiDCB_DoesNotZigzagOffTheRamp()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout(AirportId);
        Assert.NotNull(layout);

        var ga9 = layout.FindParkingByName(SpotName);
        Assert.NotNull(ga9);

        var aircraft = SpawnAtParking(ga9, layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-issue240-ga9-taxi-spin",
            ScenarioName = "Issue 240 GA9 taxi spin",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = AirportId,
            AutoCrossRunway = true,
        };

        var spawnPos = aircraft.Position;
        var result = engine.SendCommand(Callsign, Command);
        Assert.True(result.Success, $"'{Command}' failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"route: {route.ToSummary()}  ({route.Segments.Count} segments)");

        // Primary, timing-independent assertion: the resolved route must not double back
        // through the ramp cross-connector. Over the first 400 ft this is ~273° pre-fix and ~77° post-fix.
        double exitTurn = RouteExitTurnDeg(route, layout, RampExitWindowFt);
        output.WriteLine($"ramp-exit cumulative turn over first {RampExitWindowFt:F0} ft: {exitTurn:F1}°");
        Assert.True(
            exitTurn < MaxRampExitTurnDeg,
            $"resolved route zigzags off the GA9 ramp: {exitTurn:F0}° of cumulative turn in the first "
                + $"{RampExitWindowFt:F0} ft (max {MaxRampExitTurnDeg:F0}°). Route: {route.ToSummary()}"
        );

        // Behavioural backstop: tick the aircraft out of the ramp and confirm it taxis
        // smoothly rather than spinning. The orbit guard (ThrowOnOrbit) also runs here for free.
        var evaluator = new TaxiBudgetEvaluator();
        for (int t = 1; t <= BehaviourWindowSec; t++)
        {
            engine.TickOneSecond();
            evaluator.Observe(aircraft);
        }

        double netProgressFt = GeoMath.DistanceNm(spawnPos, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"after {BehaviourWindowSec}s: {evaluator.DiagnosticSummary()}, net {netProgressFt:F0} ft from spawn");

        Assert.True(
            netProgressFt >= MinForwardProgressFt,
            $"{Callsign} did not taxi off the ramp: only {netProgressFt:F0} ft from spawn in {BehaviourWindowSec}s "
                + $"(min {MinForwardProgressFt:F0} ft). {evaluator.DiagnosticSummary()}"
        );
        Assert.True(
            evaluator.MaxConsecutiveZeroProgressSec <= MaxZeroProgressSec,
            $"{Callsign} sat unmoving for {evaluator.MaxConsecutiveZeroProgressSec}s outside a legitimate stop "
                + $"(max {MaxZeroProgressSec}s). {evaluator.DiagnosticSummary()}"
        );
    }

    /// <summary>
    /// Cumulative absolute chord-bearing change along the route's leading segments, until
    /// <paramref name="windowFt"/> of distance is covered. Chord (from-node → to-node)
    /// bearings are node-id-independent and capture a doubling-back route directly.
    /// </summary>
    private static double RouteExitTurnDeg(TaxiRoute route, AirportGroundLayout layout, double windowFt)
    {
        double cumFt = 0.0;
        double turn = 0.0;
        double? prevBearing = null;
        foreach (var seg in route.Segments)
        {
            if (!layout.Nodes.TryGetValue(seg.FromNodeId, out var from) || !layout.Nodes.TryGetValue(seg.ToNodeId, out var to))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(from.Position, to.Position);
            if (prevBearing is { } prev)
            {
                turn += Math.Abs(GeoMath.SignedBearingDifference(prev, bearing));
            }

            prevBearing = bearing;
            cumFt += seg.Edge.DistanceNm * GeoMath.FeetPerNm;
            if (cumFt >= windowFt)
            {
                break;
            }
        }

        return turn;
    }

    private static AircraftState SpawnAtParking(GroundNode spot, AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = spot.Position,
            TrueHeading = spot.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "VFR",
                CruiseAltitude = 1500,
            },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
