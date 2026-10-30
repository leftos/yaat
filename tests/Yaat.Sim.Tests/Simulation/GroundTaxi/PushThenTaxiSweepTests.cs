using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-41: no aircraft ends up unable to taxi after a pushback. For each stand at KOAK and KSFO and each ramp taxilane
/// the push menu offers from it (<see cref="PushTargetPlanner.ComputeStand"/>, <see cref="PushTargetKind.Taxilane"/>
/// targets), a B738 parked there is given a bare <c>PUSH {lane}</c> and held; then, from a fresh push each time, a
/// controller's <c>TAXI {taxiway}</c> toward every exit taxiway of the lane (<see cref="ExitTaxiways"/>) is accepted
/// (never refused for want of room to turn around; a clearance missing a route taxiway is retried with it) and the
/// aircraft moves off or completes its route. A lane with no exit taxiway fails.
///
/// <para>
/// B738 for every stand: the planner needs a design group, and the existing pushes at these stands use group III types.
/// Taxi-out stands drop out, because the planner offers them no push target. The ramp is empty, so a green case does not
/// prove the push clear of parked neighbours.
/// </para>
/// </summary>
public partial class PushThenTaxiSweepTests(ITestOutputHelper output)
{
    private const string Callsign = "SWP41";
    private const string SweepType = "B738";
    private const int PushBudgetSeconds = 180;
    private const int TaxiWindowSeconds = 60;
    private const double MinMovedFt = 30.0;

    /// <summary>The least (ft) an aircraft must move for a completed route to count (<see cref="CompletedOnItsRoute"/>).</summary>
    private const double MinCompletedMoveFt = 1.0;

    private static readonly string[] Airports = ["OAK", "SFO"];

    /// <summary>The most (ft) an aircraft expected to hold where it stands (<see cref="ExpectedHolds"/>) may move.</summary>
    private const double MaxHeldMoveFt = 1.0;

    /// <summary>
    /// The (airport, stand, lane, taxiway) cases whose <c>TAXI {taxiway}</c> is a no-destination hold with the nose already
    /// inside the taxiway setback: accepted, the aircraft holds where it stands, as
    /// <see cref="BareTaxiFirstStepHeadingTests.BareTaxiB_FromCInsideTheSetback_HoldsWhereItStands"/> rules.
    /// </summary>
    private static readonly HashSet<(string Airport, string Stand, string Lane, string Taxiway)> ExpectedHolds =
    [
        ("SFO", "A13V", "M2", "M1"),
        ("SFO", "B8", "BC", "AY1"),
        ("SFO", "B9", "BC", "AY1"),
        ("SFO", "C10", "BC", "A"),
        ("SFO", "G9", "B5", "B4"),
    ];

    private static readonly Lazy<IReadOnlyList<object[]>> AllCases = new(BuildAllCases);

    /// <summary>Every (airport, stand, lane) the sweep pushes.</summary>
    public static IEnumerable<object[]> SweepCases() => AllCases.Value;

    /// <summary>Counts the sweep's pushes and taxi clearances at each airport, and how long enumerating them took.</summary>
    [Fact]
    [Trait("Category", "Nightly")]
    public void CountCases()
    {
        var clock = Stopwatch.StartNew();
        IReadOnlyList<object[]> cases = AllCases.Value;
        output.WriteLine($"enumerated in {clock.Elapsed.TotalSeconds:F1} s");
        foreach (string airport in Airports)
        {
            List<object[]> atAirport = [.. cases.Where(c => (string)c[0] == airport)];
            if (Layout(airport) is not { } layout)
            {
                continue;
            }

            var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);
            int taxis = atAirport.Sum(c => ExitTaxiways(layout, classification, (string)c[2]).Taxiways.Count);
            int stands = atAirport.Select(c => (string)c[1]).Distinct(StringComparer.Ordinal).Count();
            string lanes = string.Join(
                ", ",
                atAirport
                    .Select(c => (string)c[2])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(lane => $"{lane}->{ExitTaxiways(layout, classification, lane)}")
            );
            output.WriteLine($"{airport}: {stands} stands, {atAirport.Count} pushes, {taxis} taxi clearances; lanes {lanes}");
        }

        Assert.True(cases.Count > 0, "the sweep enumerated no cases");
    }

    /// <summary>
    /// Every <see cref="ExpectedHolds"/> entry is run by the sweep: its push is one of the sweep's cases and its taxiway one of
    /// that lane's exit taxiways, so an entry the sweep no longer reaches fails here rather than passing unseen.
    /// </summary>
    [Fact]
    [Trait("Category", "Nightly")]
    public void ExpectedHolds_AreEachRunByTheSweep()
    {
        IReadOnlyList<object[]> cases = AllCases.Value;
        List<string> notRun = [];
        foreach ((string airport, string stand, string lane, string taxiway) in ExpectedHolds)
        {
            bool pushed = cases.Any(c => ((string)c[0] == airport) && ((string)c[1] == stand) && ((string)c[2] == lane));
            bool taxied =
                pushed
                && (Layout(airport) is { } layout)
                && ExitTaxiways(layout, MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value), lane)
                    .Taxiways.Contains(taxiway);
            if (!taxied)
            {
                notRun.Add($"{airport} {stand} PUSH {lane} TAXI {taxiway} ({(pushed ? "taxiway not an exit of the lane" : "push not swept")})");
            }
        }

        Assert.True(notRun.Count == 0, $"expected holds the sweep never runs:\n{string.Join("\n", notRun)}");
    }

    /// <summary>The full sweep: every stand, every taxilane it pushes onto, every exit taxiway of that lane (<see cref="ExitTaxiways"/>).</summary>
    [Theory]
    [Trait("Category", "Nightly")]
    [MemberData(nameof(SweepCases), DisableDiscoveryEnumeration = true)]
    public void PushOntoLane_ThenTaxiTowardEachExitTaxiway_IsAcceptedAndMoves(string airport, string stand, string lane) =>
        RunCase(airport, stand, lane);

    /// <summary>The known cases of YAAT-708 and YAAT-41, run on every change.</summary>
    [Theory]
    [InlineData("OAK", "29", "TE")]
    [InlineData("OAK", "31", "TE")]
    [InlineData("SFO", "F8", "T7A")]
    [InlineData("SFO", "F9", "T7A")]
    [InlineData("SFO", "F19", "T8")]
    public void KnownPush_ThenTaxiTowardEachExitTaxiway_IsAcceptedAndMoves(string airport, string stand, string lane) =>
        RunCase(airport, stand, lane);

    private void RunCase(string airport, string stand, string lane)
    {
        TestVnasData.EnsureInitialized();
        if ((TestVnasData.NavigationDb is null) || (Layout(airport) is not { } layout))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);
        LaneExits exits = ExitTaxiways(layout, classification, lane);
        output.WriteLine($"{airport} {stand} PUSH {lane}: exit taxiways {exits}");
        Assert.True(exits.Taxiways.Count > 0, $"no exit taxiway found for {airport} lane {lane} (stand {stand})");

        IReadOnlyList<string> taxiways = exits.Taxiways;
        List<string> failures = [];
        foreach (string taxiway in taxiways)
        {
            (SimulationEngine engine, AircraftState aircraft) = Pushed(airport, stand, lane);
            bool expectHold = ExpectedHolds.Contains((airport, stand, lane, taxiway));
            if (TaxiFailure(engine, aircraft, taxiway, expectHold) is { } failure)
            {
                failures.Add(failure);
            }
        }

        output.WriteLine($"case took {clock.Elapsed.TotalSeconds:F2} s for {taxiways.Count} taxi clearances");
        Assert.True(failures.Count == 0, $"taxi failure after {airport} {stand} PUSH {lane}:\n{string.Join("\n", failures)}");
    }

    /// <summary>
    /// Sends <c>TAXI {taxiway}</c> to the held aircraft, which must be assigned a taxi route, and ticks until it has moved
    /// <see cref="MinMovedFt"/> or has completed its route (<see cref="CompletedOnItsRoute"/>): null when it did, otherwise
    /// what went wrong and the pose it was held at. A refusal that names a route taxiway missing from the clearance ("route
    /// to X needs Y, not in clearance") is retried once as <c>TAXI Y X</c>, the clearance it implies, and that retry must be
    /// accepted; any other refusal, a turn-about one above all, fails. With <paramref name="expectHold"/> (one of
    /// <see cref="ExpectedHolds"/>), the aircraft must instead hold where it stands (<see cref="HeldWhereItStoodFailure"/>).
    /// </summary>
    private string? TaxiFailure(SimulationEngine engine, AircraftState aircraft, string taxiway, bool expectHold)
    {
        LatLon heldAt = aircraft.Position;
        string pose = $"held at ({heldAt.Lat:F7},{heldAt.Lon:F7}) heading {aircraft.TrueHeading.Degrees:F1} on {aircraft.Ground.CurrentTaxiway}";
        string command = $"TAXI {taxiway}";
        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"  {command}: {result.Success} — {result.Message}");
        if ((!result.Success) && (MissingRouteTaxiway().Match(result.Message ?? "") is { Success: true } missing))
        {
            command = $"TAXI {missing.Groups["via"].Value} {missing.Groups["to"].Value}";
            result = engine.SendCommand(Callsign, command);
            output.WriteLine($"  retried as {command}: {result.Success} — {result.Message}");
        }

        if (!result.Success)
        {
            return $"  {command} refused, {pose}: {result.Message}";
        }

        if (aircraft.Ground.AssignedTaxiRoute is null)
        {
            return $"  {command} accepted but assigned no taxi route, {pose}";
        }

        if (expectHold)
        {
            return HeldWhereItStoodFailure(engine, aircraft, command, pose);
        }

        int doneAt = SfoGroundHarness.TickUntil(
            engine,
            () => (MovedFt(heldAt, aircraft) >= MinMovedFt) || CompletedOnItsRoute(heldAt, aircraft),
            TaxiWindowSeconds,
            null
        );
        if (doneAt < 0)
        {
            return $"  {command} accepted but moved only {MovedFt(heldAt, aircraft):F1} ft in {TaxiWindowSeconds} s without completing its route "
                + $"(phase {aircraft.Phases?.CurrentPhase?.GetType().Name}), {pose}";
        }

        string how = (MovedFt(heldAt, aircraft) >= MinMovedFt) ? "by movement" : "by route completion";
        output.WriteLine($"  moved {MovedFt(heldAt, aircraft):F1} ft after {doneAt} s, done {how}");
        return null;
    }

    /// <summary>
    /// Ticks <see cref="TaxiWindowSeconds"/> after an accepted <paramref name="command"/> whose nose already stood inside the
    /// taxiway setback: null when the aircraft is then in <see cref="HoldingShortPhase"/> having moved less than
    /// <see cref="MaxHeldMoveFt"/>, otherwise what it did instead.
    /// </summary>
    private static string? HeldWhereItStoodFailure(SimulationEngine engine, AircraftState aircraft, string command, string pose)
    {
        LatLon heldAt = aircraft.Position;
        for (int second = 0; second < TaxiWindowSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = MovedFt(heldAt, aircraft);
        if ((aircraft.Phases?.CurrentPhase is HoldingShortPhase) && (movedFt < MaxHeldMoveFt))
        {
            return null;
        }

        return $"  {command} accepted and expected to hold where it stood, but after {TaxiWindowSeconds} s it moved {movedFt:F1} ft "
            + $"(phase {aircraft.Phases?.CurrentPhase?.GetType().Name}), {pose}";
    }

    /// <summary>
    /// Whether the aircraft's live taxi route is complete with the aircraft moved at least <see cref="MinCompletedMoveFt"/>
    /// from <paramref name="heldAt"/> and its reference point within half its length of the route's final node: a route that
    /// ended where it stood, or short of its end, does not count.
    /// </summary>
    private static bool CompletedOnItsRoute(LatLon heldAt, AircraftState aircraft)
    {
        if (
            (aircraft.Ground.AssignedTaxiRoute is not { IsComplete: true } route)
            || (route.Segments.Count == 0)
            || (aircraft.Ground.Layout is not { } layout)
            || (!layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out GroundNode? finalNode))
        )
        {
            return false;
        }

        double toFinalFt = GeoMath.DistanceNm(aircraft.Position, finalNode.Position) * GeoMath.FeetPerNm;
        return (MovedFt(heldAt, aircraft) >= MinCompletedMoveFt) && (toFinalFt <= (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0));
    }

    /// <summary>A refusal naming a route taxiway the clearance left out: <c>route to {to} needs {via}, not in clearance</c>.</summary>
    [GeneratedRegex(@"route to (?<to>\S+) needs (?<via>[^,]+), not in clearance")]
    private static partial Regex MissingRouteTaxiway();

    private static double MovedFt(LatLon from, AircraftState aircraft) => GeoMath.DistanceNm(from, aircraft.Position) * GeoMath.FeetPerNm;

    /// <summary>
    /// A fresh engine over the airport's committed layout with a <see cref="SweepType"/> parked at <paramref name="stand"/>,
    /// given a bare <c>PUSH {lane}</c> and held after it; fails the case as a push failure when the push is refused or does
    /// not come to rest within <see cref="PushBudgetSeconds"/>.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft) Pushed(string airport, string stand, string lane)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout layout = groundData.GetLayout(airport) ?? throw new InvalidOperationException($"no {airport} layout");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-push-then-taxi-sweep",
                ScenarioName = "PUSH lane then TAXI sweep",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        GroundNode parking =
            layout.FindParkingByName(stand) ?? throw new InvalidOperationException($"{airport} layout has no parking named '{stand}'");
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = SweepType,
            Position = parking.Position,
            TrueHeading = parking.TrueHeading ?? throw new InvalidOperationException($"{airport} stand {stand} has no heading"),
            Altitude = FieldElevationFt(airport),
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.ParkingSpot = stand;
        engine.World.AddAircraft(aircraft);

        CommandResult push = engine.SendCommand(Callsign, $"PUSH {lane}");
        Assert.True(push.Success, $"push failure: {airport} {stand} PUSH {lane} was refused: {push.Message}");
        int heldAfter = SfoGroundHarness.TickUntil(engine, () => aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase, PushBudgetSeconds, null);
        Assert.True(
            heldAfter > 0,
            $"push failure: {airport} {stand} PUSH {lane} did not come to rest within {PushBudgetSeconds} s "
                + $"(phase {aircraft.Phases?.CurrentPhase?.GetType().Name})"
        );
        return (engine, aircraft);
    }

    private static AirportGroundLayout? Layout(string airport) => new TestAirportGroundData().GetLayout(airport);

    /// <summary>The field elevation (ft) of a swept airport.</summary>
    private static double FieldElevationFt(string airport) =>
        airport switch
        {
            "OAK" => 9,
            "SFO" => 13,
            _ => throw new ArgumentOutOfRangeException(nameof(airport), airport, "no field elevation for this airport"),
        };

    private static IReadOnlyList<object[]> BuildAllCases()
    {
        TestVnasData.EnsureInitialized();
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        AirportSidecarCatalog sidecars = PushTargetPlannerTests.Sidecars.Value;
        AirplaneDesignGroup group = AirplaneDesignGroups.GroupForType(SweepType);
        List<object[]> cases = [];
        foreach (string airport in Airports)
        {
            if (Layout(airport) is not { } layout)
            {
                continue;
            }

            IEnumerable<string> stands = layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
                .Select(n => n.Name!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            foreach (string stand in stands)
            {
                if (PushTargetPlanner.ComputeStand(layout, envelopes, sidecars, stand, group) is not { } entry)
                {
                    continue;
                }

                foreach (PrecomputedPushTarget target in entry.Targets.Where(t => t.Kind == PushTargetKind.Taxilane))
                {
                    cases.Add([airport, stand, target.Name]);
                }
            }
        }

        return cases;
    }

    /// <summary>The movement-area taxiways a controller taxis toward from a lane, and which way they were found.</summary>
    internal sealed record LaneExits(IReadOnlyList<string> Taxiways, string FoundBy)
    {
        public override string ToString() => $"[{string.Join(" ", Taxiways)}] ({FoundBy})";
    }

    /// <summary>
    /// The movement-area taxiways (not runways, not ramp taxilanes) a controller taxis toward from <paramref name="lane"/>,
    /// ordinal order: those meeting the lane at any of its nodes, at its ends or where it crosses one; failing that, the
    /// outbound taxiway of a spot on the lane (<see cref="AirportGroundLayout.TryGetSpotOutboundTaxiway"/>); failing that,
    /// the first ones a walk over the ramp from the lane's nodes reaches. Empty when none of the three finds one.
    /// </summary>
    internal static LaneExits ExitTaxiways(AirportGroundLayout layout, MovementAreaClassification classification, string lane)
    {
        List<GroundNode> laneNodes = [.. layout.AllEdges.Where(e => IsLaneEdge(e, lane)).SelectMany(e => e.Nodes).DistinctBy(n => n.Id)];
        SortedSet<string> meeting = new(laneNodes.SelectMany(n => MovementAreaNamesAt(n, classification, lane)), StringComparer.Ordinal);
        if (meeting.Count > 0)
        {
            return new([.. meeting], "meets the lane");
        }

        foreach (GroundNode spot in laneNodes.Where(n => n.Type == GroundNodeType.Spot))
        {
            if (layout.TryGetSpotOutboundTaxiway(spot, out string outbound) && IsTaxiableMovementArea(classification, outbound))
            {
                meeting.Add(outbound);
            }
        }

        if (meeting.Count > 0)
        {
            return new([.. meeting], "spot outbound");
        }

        return new(RampWalk(laneNodes, classification, lane), "ramp walk");
    }

    /// <summary>
    /// The movement-area taxiways at the nearest nodes a breadth-first walk from <paramref name="laneNodes"/> reaches over
    /// non-runway edges; empty when the walk reaches none. Each layer's nodes carry no movement-area edge until the last, so
    /// the walk only ever crosses ramp pavement.
    /// </summary>
    private static IReadOnlyList<string> RampWalk(IReadOnlyList<GroundNode> laneNodes, MovementAreaClassification classification, string lane)
    {
        HashSet<int> visited = [.. laneNodes.Select(n => n.Id)];
        List<GroundNode> layer = [.. laneNodes];
        while (layer.Count > 0)
        {
            List<GroundNode> next = [];
            foreach (GroundNode node in layer)
            {
                foreach (IGroundEdge edge in node.Edges.Where(e => !e.IsRunwayCenterline))
                {
                    GroundNode other = edge.OtherNode(node);
                    if (visited.Add(other.Id))
                    {
                        next.Add(other);
                    }
                }
            }

            SortedSet<string> reached = new(next.SelectMany(n => MovementAreaNamesAt(n, classification, lane)), StringComparer.Ordinal);
            if (reached.Count > 0)
            {
                return [.. reached];
            }

            layer = next;
        }

        return [];
    }

    /// <summary>The movement-area taxiways, other than <paramref name="lane"/>, on the edges at <paramref name="node"/>.</summary>
    private static IEnumerable<string> MovementAreaNamesAt(GroundNode node, MovementAreaClassification classification, string lane) =>
        node
            .Edges.SelectMany(NamesOf)
            .Where(name => !string.Equals(name, lane, StringComparison.OrdinalIgnoreCase) && IsTaxiableMovementArea(classification, name));

    private static bool IsLaneEdge(IGroundEdge edge, string lane) =>
        NamesOf(edge).All(n => string.Equals(n, lane, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> NamesOf(IGroundEdge edge) => edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];

    private static bool IsTaxiableMovementArea(MovementAreaClassification classification, string name) =>
        (name.Length > 0)
        && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
        && !name.Contains(':')
        && classification.IsMovementArea(name);
}
