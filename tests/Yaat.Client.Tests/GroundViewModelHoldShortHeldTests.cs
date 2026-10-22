using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>
/// The Hold short of… ladder of an aircraft the sim holds short: an OAK departure parked at SIG1 and given <c>TAXIAUTO
/// 28L</c>, with runway crossings left to the controller, stops at the bar of the first runway it crosses, or, given a
/// taxiway stop the ladder offers first, at that taxiway's bar. The sim parks a held aircraft back from its bar (half its
/// length at a runway, its length and more at a taxiway), and the held bar is no stop however far back that is: the line
/// for the aircraft names it. The bars after it are stops, and 28L, where the route ends, is the ladder's destination row.
/// Each stop's command is sent to the sim, which arms the bar the stop shows and keeps the aircraft held where it is. The
/// aircraft is copied into a client <see cref="AircraftModel"/> as the server broadcasts it.
///
/// The last case is SFO's tail-over-runway pose (issue #172 W2, <c>TAXI G B HS B</c>), staged on the layout's own nodes
/// rather than through the sim: a taxiway sitting less than a fuselage past the runway has the hold clamped onto the
/// runway's far-side bar, one route node short of the taxiway's own bar, the tail still over the runway. Every bar that
/// pose can be built from on SFO is staged.
/// </summary>
public class GroundViewModelHoldShortHeldTests
{
    private const string Callsign = "N152SP";
    private const int StageBudgetSeconds = 600;
    private const int StaysHeldSeconds = 10;

    private const string ParkedAtOakSig1 = """
        {
          "id": "test-hold-short-held",
          "name": "Hold short held",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": {
                "rules": "VFR", "departure": "KOAK", "destination": "KOAK", "cruiseAltitude": 1500, "cruiseSpeed": 100,
                "route": "", "remarks": "", "aircraftType": "C172"
              }
            }
          ]
        }
        """;

    private readonly ITestOutputHelper _output;

    public GroundViewModelHoldShortHeldTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("C172")]
    [InlineData("B738")]
    public void HoldingShort_HeldBarIsNotAStop_LaterBarsAre(string aircraftType)
    {
        Staged staged = Taxied(aircraftType);
        TickUntilHeld(staged, "the first runway crossing");
        var hold = (HoldingShortPhase)staged.Aircraft.Phases!.CurrentPhase!;
        Assert.NotEqual("28L", RunwayIdentifier.Parse(hold.HoldShort.TargetName!).End1);

        HoldShortMenu menu = AssertHeldBarIsNoStop(staged);

        Assert.Equal("Runway 28L", menu.Destination?.Name);
        AssertEveryStopArmsItsBar(staged, menu);
    }

    [Fact]
    public void HoldingShortOfATaxiway_HeldBarIsNotAStop_LaterBarsAre()
    {
        Staged staged = Taxied("C172");
        HoldShortChoice taxiwayStop = FirstTaxiwayStopBeforeARunway(staged);
        CommandResult hs = staged.Engine.SendCommand(Callsign, taxiwayStop.Command);
        Assert.True(hs.Success, hs.Message);
        TickUntilHeld(staged, taxiwayStop.Command);
        Assert.True(
            HoldShortMenuHelper.IsNonRunwayBar(staged.Aircraft.Phases!.CurrentPhase!.Name),
            $"held at a runway after {taxiwayStop.Command}: {staged.Aircraft.Phases.CurrentPhase.Name}"
        );

        HoldShortMenu menu = AssertHeldBarIsNoStop(staged);

        Assert.Equal("Runway 28L", menu.Destination?.Name);
        AssertEveryStopArmsItsBar(staged, menu);
    }

    /// <summary>
    /// A taxiway hold the sim clamps onto a runway's far-side bar (issue #172 W2's tail-over pose: SFO JBU577
    /// <c>TAXI G B HS B</c>, B less than a fuselage past 01L/19R): the aircraft's centre on the runway's hold-short with
    /// its route running on to the node where the held target binds, one route node later. The bar it stands on is behind
    /// the walk, not ahead of it, so it must not hold the walk back at that node: no stop binds there, and no row names
    /// the held taxiway. Every SFO pose the state can be built from is staged, the JBU577 one included.
    /// </summary>
    [Fact]
    public void HoldingShortOfATaxiwayOnARunwaysFarSideBar_TheHeldNodeIsNotAStop()
    {
        AirportGroundLayout layout = LoadSfo();
        int staged = 0;
        int withAStopPastTheHeldNode = 0;
        foreach (TailOverPose pose in TailOverPoses(layout))
        {
            AircraftModel ac = OnTheFarSideBar(pose);
            GroundViewModel vm = ViewModel(layout);
            TaxiRoute? route = vm.ResolveRemainingRoute(ac);
            if (route?.Segments[0].FromNodeId != pose.Bar.Id)
            {
                _output.WriteLine($"skipped {pose.Bar.RunwayId} on {pose.OnTaxiway}: the route does not start at the bar");
                continue;
            }

            Assert.Equal(pose.Held.Id, route.Segments[0].ToNodeId);
            HoldShortMenu menu = vm.GetHoldShortMenu(ac);
            _output.WriteLine($"{pose.Bar.RunwayId} on {pose.OnTaxiway}, held short of {pose.HeldTaxiway} at {pose.Held.Id}: rows: {Dump(menu)}");
            staged++;
            Assert.DoesNotContain(menu.Rows, r => r.BarPoint == pose.Held.Position);
            Assert.DoesNotContain(menu.Rows, r => string.Equals(CommandTarget(r.Command), pose.HeldTaxiway, StringComparison.OrdinalIgnoreCase));
            if (menu.Rows.Any(r => r.Preview.Segments.Count > 1))
            {
                withAStopPastTheHeldNode++;
            }
        }

        Assert.True(staged > 0, "no SFO runway hold-short bar staged as a tail-over pose");
        Assert.True(withAStopPastTheHeldNode > 0, $"none of the {staged} staged poses has a stop past the held node");
    }

    // --- Asserts ----------------------------------------------------------------------------

    /// <summary>The held aircraft's ladder: at least one stop, none at the held bar's node or naming its target.</summary>
    private HoldShortMenu AssertHeldBarIsNoStop(Staged staged)
    {
        HoldShortPoint heldBar = ((HoldingShortPhase)staged.Aircraft.Phases!.CurrentPhase!).HoldShort;
        AircraftModel ac = AsBroadcast(staged.Aircraft);
        Assert.StartsWith(HoldShortMenuHelper.HoldingShortPrefix, ac.CurrentPhase);

        HoldShortMenu menu = ViewModel(staged.Layout).GetHoldShortMenu(ac);

        string dump = Dump(menu);
        _output.WriteLine($"{staged.Aircraft.AircraftType} held at {heldBar.TargetName} (node {heldBar.NodeId}); me: {menu.MeLine}; rows: {dump}");
        Assert.True(menu.Rows.Count > 0, $"no stop after the held bar: {dump}");
        Assert.DoesNotContain(menu.Rows, r => staged.Layout.Nodes[heldBar.NodeId].Position == r.BarPoint);
        HashSet<string> heldNames = HeldTargetNames(heldBar.TargetName!);
        Assert.DoesNotContain(menu.Rows, r => heldNames.Contains(CommandTarget(r.Command)));
        Assert.StartsWith("now, holding short of ", menu.MeLine);
        return menu;
    }

    /// <summary>
    /// Each stop's command, sent to the sim in turn: accepted, it arms an explicit hold-short at the node of the stop's bar,
    /// and the aircraft stays held, stopped, at the bar it is at.
    /// </summary>
    private static void AssertEveryStopArmsItsBar(Staged staged, HoldShortMenu menu)
    {
        HoldShortPoint heldBar = ((HoldingShortPhase)staged.Aircraft.Phases!.CurrentPhase!).HoldShort;
        foreach (HoldShortChoice stop in menu.Rows)
        {
            CommandResult result = staged.Engine.SendCommand(Callsign, stop.Command);
            Assert.True(result.Success, $"{stop.Command} refused: {result.Message}");
            int barNodeId = staged.Layout.Nodes.Values.Single(n => n.Position == stop.BarPoint).Id;
            Assert.Contains(
                staged.Aircraft.Ground.AssignedTaxiRoute!.HoldShortPoints,
                h => (h.NodeId == barNodeId) && (h.Reason == HoldShortReason.ExplicitHoldShort) && !h.IsCleared
            );
        }

        for (int second = 0; second < StaysHeldSeconds; second++)
        {
            staged.Engine.TickOneSecond();
        }

        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(staged.Aircraft.Phases?.CurrentPhase);
        Assert.Equal(heldBar.NodeId, hold.HoldShort.NodeId);
        Assert.True(staged.Aircraft.GroundSpeed < 0.5, $"moving at {staged.Aircraft.GroundSpeed:F1} kt while held at {heldBar.TargetName}");
    }

    private static string Dump(HoldShortMenu menu) =>
        string.Join(
            " | ",
            menu.Rows.Select(r => $"{r.Label.Badge} {r.Label.Name} ~{r.Label.DistanceFt} at route node {r.Preview.Segments.Count} — {r.Command}")
        );

    /// <summary>The names an <c>HS</c> of the held bar would use: both ends of a runway, else the taxiway.</summary>
    private static HashSet<string> HeldTargetNames(string targetName)
    {
        RunwayIdentifier? runway = targetName.Contains('/') ? RunwayIdentifier.Parse(targetName) : null;
        return (runway is { } id)
            ? new HashSet<string>([id.End1, id.End2], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([targetName], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The target of <c>HS X</c> or <c>HS X@Y</c>: <c>X</c>.</summary>
    private static string CommandTarget(string command) => command["HS ".Length..].Split('@')[0];

    // --- Fixtures ---------------------------------------------------------------------------

    private sealed record Staged(AirportGroundLayout Layout, SimulationEngine Engine, AircraftState Aircraft);

    private static AirportGroundLayout LoadOak()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        Assert.True(File.Exists(path), $"{path} is missing from the test output");
        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), "OAK", [], FilletMode.Standard);
    }

    /// <summary>The SFO layout, the airport the tail-over-runway poses are staged on.</summary>
    private static AirportGroundLayout LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        Assert.True(File.Exists(path), $"{path} is missing from the test output");
        return GeoJsonParser.Parse("SFO", File.ReadAllText(path), "SFO", [], FilletMode.Standard);
    }

    /// <summary>
    /// A tail-over pose: the runway hold-short bar the aircraft stands on, the taxiway it sits on, the node one step along
    /// it, and the taxiway a hold the sim clamps there is short of.
    /// </summary>
    private sealed record TailOverPose(GroundNode Bar, string OnTaxiway, GroundNode Held, string HeldTaxiway);

    /// <summary>
    /// Every SFO pose a taxiway hold is clamped onto a runway's far-side bar at: a runway hold-short bar, the taxiway it
    /// sits on, the node one step along it (<see cref="TailOverPose.Held"/>), and a taxiway that node meets. The bar itself
    /// must meet none of it, so the hold binds at the next node rather than at the bar.
    /// </summary>
    private static IEnumerable<TailOverPose> TailOverPoses(AirportGroundLayout layout) =>
        layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort)
            .SelectMany(bar => bar.Edges.OfType<GroundEdge>().Select(e => (Bar: bar, Edge: e, Held: e.OtherNode(bar))))
            .SelectMany(p =>
                Taxiways(p.Held)
                    .Where(taxiway => !IsOn(p.Edge, taxiway) && !MeetsTaxiway(p.Bar, taxiway))
                    .Select(taxiway => new TailOverPose(p.Bar, p.Edge.TaxiwayName, p.Held, taxiway))
            );

    /// <summary>The pose's aircraft: on the bar facing the held node, cleared along the bar's taxiway and onto the held one.</summary>
    private static AircraftModel OnTheFarSideBar(TailOverPose pose) =>
        new()
        {
            Callsign = "JBU577",
            AircraftType = "B738",
            Position = pose.Bar.Position,
            Heading = new TrueHeading(GeoMath.BearingTo(pose.Bar.Position, pose.Held.Position)),
            IsOnGround = true,
            CurrentPhase = $"{HoldShortMenuHelper.HoldingShortPrefix}{pose.HeldTaxiway}",
            CurrentTaxiway = pose.OnTaxiway,
            TaxiRoute = $"{pose.OnTaxiway} {pose.HeldTaxiway}",
            HasActiveTaxiRoute = true,
        };

    /// <summary>Whether <paramref name="edge"/> is <paramref name="taxiway"/>'s own, not a junction arc's.</summary>
    private static bool IsOn(IGroundEdge edge, string taxiway) => string.Equals(edge.TaxiwayName, taxiway, StringComparison.OrdinalIgnoreCase);

    private static bool MeetsTaxiway(GroundNode node, string taxiway) => node.Edges.Any(e => e.MatchesTaxiway(taxiway));

    /// <summary>The taxiways a hold short of <paramref name="node"/> could name: its edges' own names, a junction arc's
    /// both, without a runway centerline or the ramp.</summary>
    private static IEnumerable<string> Taxiways(GroundNode node) =>
        node
            .Edges.Where(e => !e.IsRunwayCenterline)
            .SelectMany(e => (e is GroundArc arc) ? arc.TaxiwayNames : [e.TaxiwayName])
            .Where(name => (name.Length > 0) && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && (name != "RAMP"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>An OAK <paramref name="aircraftType"/> parked at SIG1, given <c>TAXIAUTO 28L</c> and ticked until it taxies.</summary>
    private static Staged Taxied(string aircraftType)
    {
        AirportGroundLayout layout = LoadOak();
        var engine = new SimulationEngine(new OneLayout(layout), PushPlanStore.CreateShipped());
        string scenario = ParkedAtOakSig1.Replace("\"C172\"", $"\"{aircraftType}\"", StringComparison.Ordinal);
        List<string> warnings = engine.LoadScenario(scenario, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        engine.Scenario!.AutoCrossRunwayMode = AutoCrossRunwayMode.Off;
        AircraftState aircraft = engine.FindAircraft(Callsign) ?? throw new InvalidOperationException($"{Callsign} did not spawn at SIG1");
        CommandResult taxi = engine.SendCommand(Callsign, "TAXIAUTO 28L");
        Assert.True(taxi.Success, taxi.Message);

        for (int second = 0; (second < StageBudgetSeconds) && (aircraft.Phases?.CurrentPhase is not TaxiingPhase); second++)
        {
            engine.TickOneSecond();
        }

        Assert.True(aircraft.Phases?.CurrentPhase is TaxiingPhase, $"never taxied: {aircraft.Phases?.CurrentPhase?.Name}");
        return new Staged(layout, engine, aircraft);
    }

    private static void TickUntilHeld(Staged staged, string what)
    {
        AircraftState aircraft = staged.Aircraft;
        for (int second = 0; (second < StageBudgetSeconds) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase); second++)
        {
            staged.Engine.TickOneSecond();
        }

        Assert.True(aircraft.Phases?.CurrentPhase is HoldingShortPhase, $"never held short of {what}: {aircraft.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>The taxiing aircraft's first taxiway stop with a runway stop after it, so a bar is left past the hold.</summary>
    private static HoldShortChoice FirstTaxiwayStopBeforeARunway(Staged staged)
    {
        HoldShortMenu menu = ViewModel(staged.Layout).GetHoldShortMenu(AsBroadcast(staged.Aircraft));
        int firstRunway = menu.Rows.ToList().FindIndex(r => r.Label.Badge == HoldShortChoice.RunwayBadge);
        HoldShortChoice? stop = menu.Rows.Take(Math.Max(firstRunway, 0)).FirstOrDefault(r => r.Label.Badge == HoldShortChoice.TaxiwayBadge);
        return stop ?? throw new Xunit.Sdk.XunitException($"no taxiway stop before a runway stop while taxiing: {Dump(menu)}");
    }

    /// <summary>The aircraft as the server broadcasts it: position, heading, phase, taxiway, route and runway.</summary>
    private static AircraftModel AsBroadcast(AircraftState aircraft) =>
        new()
        {
            Callsign = aircraft.Callsign,
            AircraftType = aircraft.AircraftType,
            Position = aircraft.Position,
            Heading = aircraft.TrueHeading,
            IsOnGround = true,
            CurrentPhase = aircraft.Phases?.CurrentPhase?.Name ?? "",
            CurrentTaxiway = aircraft.Ground.CurrentTaxiway ?? "",
            TaxiRoute = aircraft.ActiveOrStoredTaxiRoute!.FormatTaxiwaySequence(),
            AssignedRunway = aircraft.Phases?.AssignedRunway?.Designator ?? "",
            HasActiveTaxiRoute = true,
        };

    private static GroundViewModel ViewModel(AirportGroundLayout layout)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> none = new Dictionary<string, IReadOnlyList<string>>();
        var vm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask) { RoomActiveRunways = () => none };
        vm.SetDomainLayoutForTesting(layout);
        return vm;
    }

    private sealed class OneLayout(AirportGroundLayout layout) : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) =>
            string.Equals(airportId.TrimStart('K'), layout.AirportId, StringComparison.OrdinalIgnoreCase) ? layout : null;

#pragma warning disable IDE0060 // IAirportGroundData member: this one-layout stub has no source GeoJSON for any airport.
        public string? GetSourceGeoJson(string airportId) => null;
#pragma warning restore IDE0060
    }
}
