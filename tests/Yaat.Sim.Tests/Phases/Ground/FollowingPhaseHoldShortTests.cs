using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// The two decisions <see cref="FollowingPhase"/> makes about a runway hold-short bar it is about to reach:
/// whether to stop there at all, and — when it does — whether the bar is a crossing or the aircraft's own
/// departure bar. Both run on the real SFO layout, at the bars either side of runway 1R on taxiway F1: the
/// exact geometry the 28/28 departure funnel leads a follower through.
/// </summary>
public class FollowingPhaseHoldShortTests(ITestOutputHelper output)
{
    private const string Callsign = "FOL1";
    private const string Leader = "LEAD1";
    private const string AircraftType = "B738";
    private const double FeetPerNm = 6076.12;

    /// <summary>Mirrors <c>FollowingPhase.HoldShortDetectionNm</c> (~120 ft), which is private to the phase.</summary>
    private const double DetectionNm = 0.02;

    /// <summary>Approach distance to the bar — inside the detection window, so the bar counts as immediately ahead.</summary>
    private const double ApproachFt = 60.0;

    /// <summary>
    /// A follower being led off the runway it is standing on does not stop at that runway's far-side bar.
    /// Leaving a runway is not crossing it, and holding short of the pavement under your own wheels strands
    /// the aircraft on an active runway waiting for a clearance the controller has no reason to issue.
    /// </summary>
    [Fact]
    public void Follower_LeavingTheRunwayItIsOn_DoesNotHoldShortOfIt()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        if (FindBarReachableFromTheRunway(layout) is not var (onRunway, bar))
        {
            output.WriteLine("SKIP: SFO layout has no 1R hold-short bar within the detection window of a 1R centerline node");
            return;
        }

        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        AircraftState aircraft = PlaceFollower(ground, onRunway.Position, GeoMath.BearingTo(onRunway.Position, bar.Position), "28L");

        Assert.True(
            RunwayOccupancy.IsOnPavement(aircraft, runway1R),
            "precondition: the aircraft must start on 1R pavement, otherwise this exercises the ordinary crossing path"
        );
        double gapFt = GeoMath.DistanceNm(onRunway.Position, bar.Position) * FeetPerNm;
        output.WriteLine($"on-runway node {onRunway.Id} is {gapFt:F0} ft from 1R bar node {bar.Id}");

        Tick(aircraft, layout);

        Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is HoldingShortPhase);
    }

    /// <summary>
    /// A bar protecting the runway the follower's own clearance ends at is its departure bar, so it holds
    /// there as <see cref="HoldShortReason.DestinationRunway"/> — which is what stops <c>RES</c> releasing it
    /// onto the runway without a takeoff clearance, and what ranks it at the front of the departure line.
    /// </summary>
    [Fact]
    public void Follower_AtTheBarOfItsOwnDestinationRunway_HoldsAsDestinationRunway() =>
        AssertHoldReasonApproachingThe1RBar(destinationRunway: "1R", expected: HoldShortReason.DestinationRunway);

    /// <summary>
    /// A bar protecting any other runway is a crossing, whatever the follower is ultimately departing from.
    /// </summary>
    [Fact]
    public void Follower_AtTheBarOfAnotherRunway_HoldsAsRunwayCrossing() =>
        AssertHoldReasonApproachingThe1RBar(destinationRunway: "28L", expected: HoldShortReason.RunwayCrossing);

    private void AssertHoldReasonApproachingThe1RBar(string destinationRunway, HoldShortReason expected)
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        List<GroundNode> bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "1R", "F1");
        if (bars.Count == 0)
        {
            output.WriteLine("SKIP: SFO layout has no runway 1R hold-short on taxiway F1");
            return;
        }

        // Short of the bar on the taxiway, nose toward the runway: off the pavement, so the "already on it"
        // skip cannot fire and the bar is genuinely one this aircraft would be crossing.
        GroundNode bar = bars[0];
        TrueHeading awayFromRunway = TaxiCoverageRunner.TaxiwayDepartureHeading(bar);
        LatLon position = GeoMath.ProjectPoint(bar.Position, awayFromRunway, ApproachFt / FeetPerNm);
        AircraftState aircraft = PlaceFollower(ground, position, GeoMath.BearingTo(position, bar.Position), destinationRunway);

        if (RunwayOccupancy.IsOnPavement(aircraft, SfoGroundHarness.Runway("1R")))
        {
            output.WriteLine($"SKIP: the approach position {ApproachFt:F0} ft short of 1R bar node {bar.Id} is still on 1R pavement");
            return;
        }

        Tick(aircraft, layout);

        HoldingShortPhase hold = Assert.Single(aircraft.Phases!.Phases.OfType<HoldingShortPhase>());
        output.WriteLine($"held at node {hold.HoldShort.NodeId} target={hold.HoldShort.TargetName} reason={hold.HoldShort.Reason}");
        Assert.Equal(expected, hold.HoldShort.Reason);
    }

    private SfoGround? Build()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: false);
        if (built is null)
        {
            output.WriteLine("SKIP: SFO layout or navdata unavailable");
        }

        return built;
    }

    /// <summary>
    /// A follower rolling toward a bar, carrying a taxi route that ends at <paramref name="destinationRunway"/>.
    /// Not added to the world: these tests drive the phase directly, so nothing else may move it.
    /// </summary>
    private static AircraftState PlaceFollower(SfoGround ground, LatLon position, double headingDegrees, string destinationRunway)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = new TrueHeading(headingDegrees),
            Altitude = 0,
            IndicatedAirspeed = 10,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO", Destination = "KLAX" },
        };
        aircraft.Ground.Layout = ground.Layout;
        aircraft.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = 0,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = destinationRunway,
                },
            ],
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new FollowingPhase(Leader));
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        return aircraft;
    }

    /// <summary>
    /// One phase tick. The context carries no aircraft lookup, so once the bar check has had its say the
    /// phase finds no leader and idles — harmless here, because what is under test is the phase list the bar
    /// check leaves behind.
    /// </summary>
    private static void Tick(AircraftState aircraft, AirportGroundLayout layout)
    {
        FollowingPhase phase = Assert.IsType<FollowingPhase>(aircraft.Phases!.CurrentPhase);
        Assert.True(aircraft.GroundSpeed > 0, "precondition: the bar check only runs on a moving aircraft");
        phase.OnTick(CommandDispatcher.BuildMinimalContext(aircraft, layout));
    }

    /// <summary>
    /// A node on the 1R centerline that sits within the bar-detection window of a 1R hold-short bar — the
    /// runway-side approach to an intersection such as F1, where an aircraft rolling off the runway meets the
    /// far-side bar while still on the pavement.
    /// </summary>
    private static (GroundNode OnRunway, GroundNode Bar)? FindBarReachableFromTheRunway(AirportGroundLayout layout)
    {
        var bars = layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId?.Contains("1R") ?? false)).ToList();
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if (!node.Edges.Any(e => e.IsRunwayCenterline && e.MatchesRunway("1R")))
            {
                continue;
            }

            foreach (GroundNode? bar in bars)
            {
                if (GeoMath.DistanceNm(node.Position, bar.Position) <= DetectionNm)
                {
                    return (node, bar);
                }
            }
        }

        return null;
    }
}
