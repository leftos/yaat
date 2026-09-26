using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A TAXI issued from the ramp a few feet inside the navigator's turning diameter of the node before a hold-short: SFO
/// taxiway S, at its plain node ~237 ft short of the S/ZS junction, with <c>TAXI S ZS HS ZS</c>. The route is a free-space
/// leg to that node, then S to the junction, where the hold-short of ZS binds; its painted bar is ~210 ft back from the
/// junction, a fuselage and a margin short of ZS. The entry-alignment arc is aimed past the lead node at the junction —
/// the aim stops at a bar node — and when it completes, the leg it aimed past is retired inside the navigator, which
/// sets up the junction as its target without the taxi phase's segment set-up running. The target has to become the
/// painted bar then, as it does when the phase sets a segment up itself, or the aircraft taxis through the bar and stops
/// on the junction.
/// </summary>
public sealed class AimedPastOntoHoldShortBarTests(ITestOutputHelper output)
{
    private const string Callsign = "SKW5707";
    private const string AircraftType = "E75L";
    private const string Taxiway = "S";
    private const string HeldShortOf = "ZS";
    private const string TaxiCommand = "TAXI S ZS HS ZS";

    /// <summary>The lead leg must be long enough that the bar, ~210 ft back from the junction, lies on it ahead of the lead node.</summary>
    private const double MinLeadLegFt = 200.0;

    /// <summary>Inside the 2 × 25 ft jet turning diameter the aim walks past, outside the 25 ft radius.</summary>
    private const double StandOffFt = 38.0;

    /// <summary>Behind the lead node and off the taxiway, seen from the junction.</summary>
    private const double StandOffBearingRelDeg = 60.0;

    /// <summary>Facing away from the junction, so the entry is an aimed turn back onto S.</summary>
    private const double HeadingRelDeg = 20.0;

    /// <summary>How close to the painted bar the aircraft must come to rest, feet.</summary>
    private const double BarToleranceFt = 10.0;

    private const int TickBudgetSeconds = 120;

    [Fact]
    public void TaxiAimedPastOntoAHoldShortNode_StopsAtThePaintedBar_NotAtTheNode()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        (GroundNode junction, GroundNode lead) = JunctionAndLeadNode(ground.Layout);
        double awayDeg = GeoMath.BearingTo(junction.Position, lead.Position);
        LatLon pose = GeoMath.ProjectPoint(lead.Position, new TrueHeading(awayDeg + StandOffBearingRelDeg), StandOffFt / GeoMath.FeetPerNm);
        AircraftState aircraft = SpawnHolding(ground, pose, new TrueHeading(awayDeg + HeadingRelDeg));

        double diameterFt = 2.0 * CategoryPerformance.MainGearTurnRadiusFt(AircraftCategorization.Categorize(AircraftType));
        double toLeadFt = FeetBetween(pose, lead.Position);
        Assert.True(toLeadFt < diameterFt, $"lead node {lead.Id} is {toLeadFt:F1} ft away; the aim only walks past a node inside {diameterFt:F0} ft");

        CommandResult result = ground.Engine.SendCommand(Callsign, TaxiCommand);
        Assert.True(result.Success, $"{TaxiCommand} failed: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.True(
            (route.Segments.Count >= 2) && (route.Segments[0].ToNodeId == lead.Id) && (route.Segments[1].ToNodeId == junction.Id),
            $"expected the approach leg → {lead.Id} then S → {junction.Id}; the route does not exercise the aim-past-onto-a-bar case"
        );
        HoldShortPoint holdShort = Assert.IsType<HoldShortPoint>(route.GetHoldShortAt(junction.Id));
        Assert.False(holdShort.IsCleared);
        var bar = new LatLon(Assert.IsType<double>(holdShort.Latitude), Assert.IsType<double>(holdShort.Longitude));
        double barToJunctionFt = FeetBetween(bar, junction.Position);

        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase,
            TickBudgetSeconds,
            second =>
                output.WriteLine(
                    $"t={second, 3} hdg={aircraft.TrueHeading.Degrees, 6:F1} gs={aircraft.GroundSpeed, 5:F2} seg={route.CurrentSegmentIndex} "
                        + $"toBar={FeetBetween(aircraft.Position, bar), 6:F1}ft toJunction={FeetBetween(aircraft.Position, junction.Position), 6:F1}ft"
                )
        );

        double toBarFt = FeetBetween(aircraft.Position, bar);
        double toJunctionFt = FeetBetween(aircraft.Position, junction.Position);
        output.WriteLine(
            $"held {toBarFt:F1} ft from the bar, {toJunctionFt:F1} ft from the junction (the bar is {barToJunctionFt:F1} ft back from it)"
        );
        Assert.True(heldAt > 0, $"{Callsign} never held short of {HeldShortOf} within {TickBudgetSeconds} s");
        Assert.True(
            toBarFt <= BarToleranceFt,
            $"{Callsign} held {toBarFt:F1} ft from the painted bar and {toJunctionFt:F1} ft from the junction it protects; "
                + "the aimed-past hand-over targeted the node, not the bar"
        );
    }

    /// <summary>
    /// The S/ZS junction, and the plain S node a straight leg of at least <see cref="MinLeadLegFt"/> before it: a node on
    /// nothing but S, so it is no bar and the aim walks past it.
    /// </summary>
    private static (GroundNode Junction, GroundNode Lead) JunctionAndLeadNode(AirportGroundLayout layout)
    {
        List<(GroundNode Junction, GroundNode Lead)> candidates =
        [
            .. layout
                .Nodes.Values.Where(n => StraightEdges(n).Any(e => e.MatchesTaxiway(HeldShortOf)))
                .SelectMany(junction =>
                    StraightEdges(junction)
                        .Where(e => e.MatchesTaxiway(Taxiway) && ((e.DistanceNm * GeoMath.FeetPerNm) >= MinLeadLegFt))
                        .Select(e => (Junction: junction, Lead: e.OtherNode(junction)))
                )
                .Where(pair => (pair.Lead.Edges.Count == 2) && pair.Lead.Edges.All(e => e is GroundEdge g && g.MatchesTaxiway(Taxiway))),
        ];
        return Assert.Single(candidates);
    }

    private static IEnumerable<GroundEdge> StraightEdges(GroundNode node) => node.Edges.OfType<GroundEdge>();

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>A stationary aircraft holding at an arbitrary ramp pose.</summary>
    private static AircraftState SpawnHolding(SfoGround ground, LatLon position, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
