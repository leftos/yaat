using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// An A319 given a plain <c>PUSH</c> off OAK gate 15 comes to rest in
/// <see cref="HoldingAfterPushbackPhase"/> a little over 100 ft ahead of the nearest graph node.
/// <c>TAXI U W RWY 30</c> then resolves a route whose first segment is the <c>T - RAMP</c> fillet arc
/// out of node 763 — a node BEHIND the aircraft — and nothing bridges the aircraft's own position to
/// that arc's start, so the aircraft is written onto the arc start in one tick: a ~100 ft teleport.
///
/// <para>These tests are the behavioural pins: an aircraft under taxi may only cover the ground its
/// own speed allows, it must physically drive to the node its route starts at, and it must then make
/// route progress.</para>
/// </summary>
public class OakSimplePushThenTaxiApproachTests(ITestOutputHelper output)
{
    private const string Callsign = "DAL2150";
    private const string AircraftType = "A319";
    private const string AirportId = "OAK";
    private const string Command = "TAXI U W RWY 30";

    /// <summary>Resting pose after the plain PUSH, from the recorded session (t=1246, just before the TAXI).</summary>
    private const double PushedLat = 37.710217680439534;
    private const double PushedLon = -122.21728593336832;
    private const double PushedHeadingDeg = 53.0;

    /// <summary>The route's start node — the from-node of the <c>T - RAMP</c> fillet arc, ~105 ft behind the tail.</summary>
    private const int StartNodeId = 763;
    private const double StartNodeLat = 37.70999962916134;
    private const double StartNodeLon = -122.21752272724868;

    private const int WindowSec = 60;

    /// <summary>Knots to feet per second.</summary>
    private const double FtPerSecPerKt = 1.6878;

    /// <summary>One second of sampled taxi state.</summary>
    private sealed record Sample(int Second, LatLon Position, double IndicatedAirspeed, double HeadingDeg, int SegmentIndex);

    private static double StepFt(Sample before, Sample after) => GeoMath.DistanceNm(before.Position, after.Position) * GeoMath.FeetPerNm;

    /// <summary>Ground an aircraft can cover in one second at its own speed, plus a 1 kt / 2 ft slack for sampling.</summary>
    private static double StepLimitFt(Sample before, Sample after) =>
        ((Math.Max(before.IndicatedAirspeed, after.IndicatedAirspeed) + 1.0) * FtPerSecPerKt) + 2.0;

    private List<Sample>? RunTaxi()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout(AirportId);
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        var aircraft = MakeAircraft(layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-simple-push-then-taxi",
            ScenarioName = "OAK simple push then taxi",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = AirportId,
            AutoCrossRunway = true,
        };

        var result = engine.SendCommand(Callsign, Command);
        Assert.True(result.Success, $"'{Command}' failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        var first = route.Segments[0];
        output.WriteLine($"result: {result.Message}");
        output.WriteLine(
            $"route: {route.ToSummary()} ({route.Segments.Count} segments); "
                + $"seg[0] {first.FromNodeId} -> {first.ToNodeId} on {first.TaxiwayName}"
        );

        var samples = new List<Sample> { Capture(0, aircraft) };
        for (int t = 1; t <= WindowSec; t++)
        {
            engine.TickOneSecond();
            samples.Add(Capture(t, aircraft));
        }

        foreach (var s in samples)
        {
            output.WriteLine(
                $"t+{s.Second, 2}: pos=({s.Position.Lat:F7},{s.Position.Lon:F7}) ias={s.IndicatedAirspeed, 5:F1} "
                    + $"hdg={s.HeadingDeg, 5:F0} seg={s.SegmentIndex}"
            );
        }

        return samples;
    }

    private static Sample Capture(int second, AircraftState aircraft) =>
        new(
            second,
            aircraft.Position,
            aircraft.IndicatedAirspeed,
            aircraft.TrueHeading.Degrees,
            aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex ?? -1
        );

    private static AircraftState MakeAircraft(AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = new LatLon(PushedLat, PushedLon),
            TrueHeading = new TrueHeading(PushedHeadingDeg),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = AirportId, Destination = "LAX" },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    /// <summary>Every one-second step must be covered at the aircraft's own ground speed — no snap onto the route.</summary>
    [Fact]
    public void TaxiAfterSimplePush_NeverTeleports()
    {
        var samples = RunTaxi();
        if (samples is null)
        {
            return;
        }

        double worstExcessFt = double.NegativeInfinity;
        double worstStepFt = 0;
        double worstLimitFt = 0;
        int worstSecond = 0;
        double maxStepFt = 0;
        int maxStepSecond = 0;

        for (int i = 1; i < samples.Count; i++)
        {
            double stepFt = StepFt(samples[i - 1], samples[i]);
            double limitFt = StepLimitFt(samples[i - 1], samples[i]);
            if (stepFt > maxStepFt)
            {
                maxStepFt = stepFt;
                maxStepSecond = samples[i].Second;
            }

            if ((stepFt - limitFt) > worstExcessFt)
            {
                worstExcessFt = stepFt - limitFt;
                worstStepFt = stepFt;
                worstLimitFt = limitFt;
                worstSecond = samples[i].Second;
            }
        }

        output.WriteLine($"max 1 s step: {maxStepFt:F1} ft at t+{maxStepSecond}; worst excess {worstExcessFt:F1} ft at t+{worstSecond}");

        Assert.True(
            worstExcessFt <= 0.0,
            $"{Callsign} covered {worstStepFt:F1} ft between t+{worstSecond - 1} and t+{worstSecond}, "
                + $"but its own speed allows only {worstLimitFt:F1} ft — the aircraft was teleported onto its route "
                + $"(largest step of the run: {maxStepFt:F1} ft at t+{maxStepSecond})"
        );
    }

    /// <summary>The aircraft must actually drive to the node its route starts at, not be written onto it.</summary>
    [Fact]
    public void TaxiAfterSimplePush_PhysicallyReachesStartNode()
    {
        var samples = RunTaxi();
        if (samples is null)
        {
            return;
        }

        var startNode = new LatLon(StartNodeLat, StartNodeLon);
        double closestFt = double.MaxValue;
        int closestSecond = -1;
        foreach (var s in samples)
        {
            if (s.SegmentIndex > 1)
            {
                continue;
            }

            double ft = GeoMath.DistanceNm(s.Position, startNode) * GeoMath.FeetPerNm;
            if (ft < closestFt)
            {
                closestFt = ft;
                closestSecond = s.Second;
            }
        }

        output.WriteLine($"closest approach to node {StartNodeId} while on the first segments: {closestFt:F1} ft at t+{closestSecond}");

        Assert.True(
            closestFt <= 6.0,
            $"{Callsign} never came within 6 ft of route start node {StartNodeId} while on its first route segment — "
                + $"closest {closestFt:F1} ft at t+{closestSecond}; the route starts at a node the aircraft never taxis to"
        );
    }

    /// <summary>A minute of taxi must leave the first corner behind.</summary>
    [Fact]
    public void TaxiAfterSimplePush_MakesProgress()
    {
        var samples = RunTaxi();
        if (samples is null)
        {
            return;
        }

        int maxIndex = samples.Max(s => s.SegmentIndex);
        output.WriteLine($"furthest segment index reached in {WindowSec}s: {maxIndex}");

        Assert.True(maxIndex >= 2, $"{Callsign} was still on route segment {maxIndex} after {WindowSec}s of taxi");
    }
}
