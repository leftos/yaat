using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Client.Tests;

/// <summary>
/// An aircraft on a taxi fillet arc that ends at a runway bar, behind the bar's painted stop, gets the simulation's approach
/// leg to the bar (<see cref="TaxiApproachLeg"/>), so the overlay must rebuild the same leg: it starts at the aircraft and
/// ends at the bar, as on the server.
/// </summary>
public class GroundViewModelArcApproachLegOverlayTests(ITestOutputHelper output)
{
    private const string Callsign = "N738SP";

    private const string AircraftType = "B738";

    /// <summary>
    /// How far beyond half the fuselage, along the arc, the aircraft must stand at least: clear of the painted stop, which is
    /// half the fuselage plus the navigator's set-back margin (<c>GroundNavigator.SetBackStopMarginFt</c>).
    /// </summary>
    private const double ClearOfThePaintedStopFt = 10.0;

    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask)
        {
            RoomActiveRunways = () => new Dictionary<string, IReadOnlyList<string>>(),
        };
    }

    /// <summary>
    /// KATL's layout, linked into this project's output beside OAK's and SFO's: it is the one committed layout with a taxi
    /// fillet arc ending at a runway bar that an aircraft can stand on behind the bar's painted stop (OAK and SFO have none).
    /// </summary>
    private static AirportGroundLayout LoadAtlLayout()
    {
        string path = Path.Combine("TestData", "atl.geojson");
        Assert.True(File.Exists(path), $"{path} is missing from the test output");
        return GeoJsonParser.Parse("ATL", File.ReadAllText(path), "ATL", new LayoutInputs([], VideoMap: null, SpotEdges: []), FilletMode.Standard);
    }

    [Fact]
    public void OnAFilletArcEndingAtARunwayBar_OverlayDrawsTheServersLegToTheBar()
    {
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = LoadAtlLayout();

        double halfLengthFt = AircraftLength.ResolveFt(AircraftType) / 2.0;
        ArcPose pose = OnArcPoseBehindAPaintedStop(layout, halfLengthFt + ClearOfThePaintedStopFt);
        LatLon position = pose.Arc.PointAtFt(pose.Bar, pose.AlongFt);
        var heading = new TrueHeading(GeoMath.BearingTo(position, pose.Arc.PointAtFt(pose.Bar, pose.AlongFt - 1.0)));
        GroundNode bar = pose.Bar;
        output.WriteLine(
            $"bar #{bar.Id} ({bar.RunwayId}), arc {pose.Arc.TaxiwayName}, {pose.AlongFt:F0} ft along it, TAXI {pose.Taxiway} {pose.Beyond}"
        );

        TaxiRoute server = ServerRoute(layout, position, heading, [pose.Taxiway, pose.Beyond]);
        Assert.True(
            (server.Segments[0].FromNodeId < 0) && (server.Segments[0].ToNodeId == bar.Id),
            $"the server laid no approach leg to bar #{bar.Id}: its route starts {server.Segments[0].FromNodeId} -> {server.Segments[0].ToNodeId}"
        );

        var ac = new AircraftModel
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            Heading = heading,
            TaxiRoute = server.FormatTaxiwaySequence(),
            HasActiveTaxiRoute = true,
            IsOnGround = true,
        };
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        TaxiRoute? drawn = vm.ResolveRemainingRoute(ac);

        Assert.NotNull(drawn);
        Assert.True(drawn!.Segments[0].FromNodeId < 0, "the overlay must start with the free-space leg from the aircraft");
        Assert.True(DistanceFt(drawn.Segments[0].Edge.FromNode.Position, position) < 1.0, "the overlay's leg does not start at the aircraft");
        Assert.Equal(bar.Id, drawn.Segments[0].ToNodeId);
        Assert.Equal(server.Segments[^1].ToNodeId, drawn.Segments[^1].ToNodeId);

        IReadOnlyList<HoldShortChoice> rows = vm.GetHoldShortMenu(ac).Rows;
        string dump = Dump(rows);
        Assert.True(rows.Count > 0, dump);
        HoldShortChoice first = rows[0];
        Assert.Equal(HoldShortChoice.RunwayBadge, first.Label.Badge);
        Assert.True(bar.RunwayId!.Value.Contains(first.Command["HS ".Length..]), dump);
        TaxiRouteSegment leg = Assert.Single(first.Preview.Segments);
        Assert.True(leg.FromNodeId < 0, $"the bar's preview starts at layout node {leg.FromNodeId}, not the approach leg from the aircraft");
        Assert.Equal(bar.Id, leg.ToNodeId);
        Assert.DoesNotContain(rows, r => r.Command == "HS K");
    }

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>How far from the bar the walk across its runway looks for the next taxiway, feet.</summary>
    private const double BeyondTheRunwaySearchFt = 2000.0;

    /// <summary>
    /// A pose on a taxi fillet arc that ends at a runway bar: the bar, the arc, the distance along the arc from the bar, the
    /// bar's taxiway and a taxiway it meets across the runway, which together make a TAXI from the pose.
    /// </summary>
    private readonly record struct ArcPose(GroundNode Bar, GroundArc Arc, double AlongFt, string Taxiway, string Beyond);

    /// <summary>
    /// Among the taxi fillet arcs (no ramp name) that end at a runway bar from the side away from its runway, on a taxiway that
    /// carries on across the runway, the pose farthest along an arc from its bar that is on that arc and on no straight edge,
    /// at least <paramref name="minAlongFt"/> along it.
    /// </summary>
    private static ArcPose OnArcPoseBehindAPaintedStop(AirportGroundLayout layout, double minAlongFt)
    {
        List<ArcPose> poses = [];
        foreach (GroundNode bar in layout.Nodes.Values.Where(node => (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is not null)))
        {
            foreach (
                GroundArc arc in bar
                    .Edges.OfType<GroundArc>()
                    .Where(arc => !arc.IsRamp && !arc.IsRunwayCenterline && LeadsAwayFromTheRunway(layout, bar, arc.OtherNode(bar)))
            )
            {
                string? taxiway = bar.Edges.OfType<GroundEdge>().Select(edge => edge.TaxiwayName).FirstOrDefault(arc.MatchesTaxiway);
                if (
                    (FarthestOnArcFt(layout, bar, arc, minAlongFt) is { } alongFt)
                    && (taxiway is not null)
                    && (TaxiwayBeyondTheRunway(layout, bar, taxiway) is { } beyond)
                )
                {
                    poses.Add(new ArcPose(bar, arc, alongFt, taxiway, beyond));
                }
            }
        }

        Assert.True(poses.Count > 0, $"no ATL runway bar is reached by a taxi fillet arc from {minAlongFt:F0} ft out");
        return poses.MaxBy(pose => pose.AlongFt);
    }

    /// <summary>
    /// The farthest distance along <paramref name="arc"/> from <paramref name="bar"/>, sampled every foot and at least
    /// <paramref name="minAlongFt"/>, where a position is on the arc and on no straight edge; null when there is none.
    /// </summary>
    private static double? FarthestOnArcFt(AirportGroundLayout layout, GroundNode bar, GroundArc arc, double minAlongFt)
    {
        for (double alongFt = Math.Floor(arc.DistanceNm * GeoMath.FeetPerNm) - 1.0; alongFt >= minAlongFt; alongFt -= 1.0)
        {
            LatLon point = arc.PointAtFt(bar, alongFt);
            if ((layout.FindOccupiedTaxiEdge(point) is null) && ReferenceEquals(layout.FindOccupiedFilletArc(point), arc))
            {
                return alongFt;
            }
        }

        return null;
    }

    /// <summary>
    /// A taxiway other than <paramref name="taxiway"/> met at a node across <paramref name="bar"/>'s runway, walking straight
    /// <paramref name="taxiway"/> edges and runway crossing links from the bar; null when there is none within
    /// <see cref="BeyondTheRunwaySearchFt"/>.
    /// </summary>
    private static string? TaxiwayBeyondTheRunway(AirportGroundLayout layout, GroundNode bar, string taxiway)
    {
        GroundRunway runway = RunwayOf(layout, bar);
        int barSide = Math.Sign(SignedCrossFt(runway, bar.Position));
        var seen = new HashSet<int> { bar.Id };
        var queue = new Queue<GroundNode>([bar]);
        while (queue.TryDequeue(out GroundNode? node))
        {
            foreach (GroundEdge edge in node.Edges.OfType<GroundEdge>().Where(edge => edge.MatchesTaxiway(taxiway) || edge.IsRunwayCrossingLink))
            {
                GroundNode next = edge.OtherNode(node);
                if (!seen.Add(next.Id) || (DistanceFt(bar.Position, next.Position) > BeyondTheRunwaySearchFt))
                {
                    continue;
                }

                if ((Math.Sign(SignedCrossFt(runway, next.Position)) != barSide) && (PlainTaxiwayOtherThan(next, taxiway) is { } other))
                {
                    return other;
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    private static string? PlainTaxiwayOtherThan(GroundNode node, string taxiway) =>
        node
            .Edges.OfType<GroundEdge>()
            .Select(edge => edge.TaxiwayName)
            .FirstOrDefault(name =>
                !name.Equals(taxiway, StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("RAMP", StringComparison.OrdinalIgnoreCase)
            );

    private static GroundRunway RunwayOf(AirportGroundLayout layout, GroundNode bar) => layout.Runways.Single(r => r.Id.Equals(bar.RunwayId));

    /// <summary>The signed distance (ft) from <paramref name="point"/> to <paramref name="runway"/>'s centreline, as an infinite line.</summary>
    private static double SignedCrossFt(GroundRunway runway, LatLon point)
    {
        var axisStart = new LatLon(runway.Coordinates[0].Lat, runway.Coordinates[0].Lon);
        var axisEnd = new LatLon(runway.Coordinates[^1].Lat, runway.Coordinates[^1].Lon);
        var axis = new TrueHeading(GeoMath.BearingTo(axisStart, axisEnd));
        return GeoMath.SignedCrossTrackDistanceNm(point, axisStart, axis) * GeoMath.FeetPerNm;
    }

    private static bool LeadsAwayFromTheRunway(AirportGroundLayout layout, GroundNode bar, GroundNode other)
    {
        GroundRunway runway = RunwayOf(layout, bar);
        return Math.Abs(SignedCrossFt(runway, other.Position)) > Math.Abs(SignedCrossFt(runway, bar.Position));
    }

    /// <summary>The route the simulation assigns for a TAXI along <paramref name="path"/> from <paramref name="position"/>.</summary>
    private static TaxiRoute ServerRoute(AirportGroundLayout layout, LatLon position, TrueHeading heading, string[] path)
    {
        // The dispatch looks the field elevation up in the navigation database; the taxi route never reads it.
        using IDisposable navData = NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting());
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = heading,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "ATL", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(
            new PhaseContext
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                DeltaSeconds = 0,
                GroundLayout = layout,
                Logger = NullLogger.Instance,
            }
        );
        aircraft.Ground.Layout = layout;
        var ctx = new DispatchContext
        {
            GroundLayout = layout,
            Rng = new Random(1),
            Weather = null,
            FindAircraft = null,
            ListAircraft = () => [aircraft],
            ValidateDctFixes = false,
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            SoloTrainingMode = false,
            SoloRpoCommandsAllowed = false,
            RpoShowPilotSpeech = false,
            TerminalEmitter = null,
            ArtccConfig = null,
            AsdexSafetyLogicConfigs = AsdexSafetyLogicSeed.Empty,
            ToweredAirports = ToweredAirports.Empty,
            ActiveRunways = ActiveRunways.Empty,
            ScenarioElapsedSeconds = 0,
            SessionStartUtc = DateTime.UnixEpoch,
            PreserveConditionals = false,
            IsScenarioScripted = false,
            FacilityHint = null,
            StudentPosition = null,
            LayoutReadForAirport = null,
            SpeechContextFor = _ => PilotSpeechContext.Canonical,
        };
        CommandResult result = CommandDispatcher.Dispatch(new TaxiCommand([.. path], [], Expedite: false, DestinationRunway: null), aircraft, ctx);
        Assert.True(result.Success, result.Message);
        return aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"TAXI {string.Join(' ', path)} left no route");
    }

    private static string Dump(IReadOnlyList<HoldShortChoice> rows) =>
        string.Join(" | ", rows.Select(r => $"{r.Label.Badge} {r.Label.Name} · {r.Label.Where} · ~{r.Label.DistanceFt} ft — {r.Command}"));
}
