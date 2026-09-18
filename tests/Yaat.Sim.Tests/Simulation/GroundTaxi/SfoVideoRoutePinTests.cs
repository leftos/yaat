using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Pins the taxi routes an SFO ground controller issues in the reference session: for each start
/// point and clearance, the resolved <see cref="TaxiRoute"/> must carry exactly the hold-shorts the
/// clearance implies — the explicit <c>HS</c> bars, the runway crossings on the way, and the
/// destination-runway bar — and must not drop any part of the clearance ("not applied" warning).
///
/// <para>Routes are asserted, not flown: this is the resolution contract, so a regression in the
/// segment expander or the hold-short binder shows up as a named hold-short that moved, vanished,
/// or arrived pre-cleared.</para>
/// </summary>
public class SfoVideoRoutePinTests
{
    private const string Callsign = "PIN1";
    private const string AircraftType = "B738";

    private readonly ITestOutputHelper _output;

    public SfoVideoRoutePinTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("spot", "2", "TAXI A A1 1R", "1R:Destination")]
    [InlineData("spot", "2", "TAXI A L F 28L HS A1", "A1:Explicit;28L:Destination")]
    [InlineData("spot", "2", "TAXI A F1 B 1L", "1L:Destination")]
    [InlineData("spot", "2", "TAXI A A1 1R F1 F RWY 28L HS F1", "F1:Explicit;28L:Destination")]
    [InlineData("spot", "2", "TAXI A F1 F RWY 28L CROSS 1L HS 1R", "1L:Crossing:cleared;1R:Explicit;28L:Destination")]
    [InlineData("parking", "G3", "TAXI A Q B F 28L HS 1L", "1L:Explicit;1R:Crossing;28L:Destination")]
    [InlineData("spot", "2", "TAXI A E B Z S S1 10L", "10L:Destination")]
    [InlineData("spot", "2", "TAXI A E B Z S S3 10R", "10R:Destination")]
    [InlineData("spot", "2", "TAXI A E C 19L HS 19R", "28L:Crossing;28R:Crossing;19R:Explicit;19L:Destination")]
    [InlineData("spot", "2", "TAXI A E CROSS 28L 28R RWY 19R", "28L:Crossing:cleared;28R:Crossing:cleared;19R:Destination")]
    [InlineData("spot", "2", "TAXI A B1 Z Z1 10R", "10R:Destination")]
    [InlineData("parking", "SIG1", "TAXI <C 28R HS E", "E:Explicit;1L:Crossing;1R:Crossing;28R:Destination")]
    [InlineData("parking", "SIG1", "TAXI C C3 10L", "10L:Destination")]
    [InlineData("parking", "B12", "TAXI Y A A1 1R", "1R:Destination")]
    [InlineData("junction", "B/H", "TAXI B H F C CROSS 1L HS 1R RWY 28R", "1L:Crossing:cleared;1R:Explicit;28L:Crossing;28R:Destination")]
    [InlineData("holdshort", "28L/Q>B", "TAXI Q B HS F1", "F1:Explicit")]
    public void Route_Resolves_WithExpectedHoldShorts(string startKind, string startName, string command, string expected)
    {
        (TaxiRoute Route, AirportGroundLayout Layout)? resolved = Resolve(startKind, startName, command);
        if (resolved is null)
        {
            return;
        }

        SfoGroundHarness.AssertHoldShorts(_output, resolved.Value.Route, ParseExpected(expected));
        Assert.DoesNotContain(resolved.Value.Route.Warnings, w => w.Contains("not applied", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A departure cleared down 1R to F1 holds short of F1 at the bar on the runway itself: the bound
    /// node carries a 1R centerline edge, and the route reaches it along 1R — the centerline segment
    /// precedes the first F1 segment.
    /// </summary>
    [Fact]
    public void Route_HsF1_BindsANodeOnThe1RCentreline()
    {
        (TaxiRoute Route, AirportGroundLayout Layout)? resolved = Resolve("spot", "2", "TAXI A A1 1R F1 F RWY 28L HS F1");
        if (resolved is null)
        {
            return;
        }

        TaxiRoute route = resolved.Value.Route;
        AirportGroundLayout layout = resolved.Value.Layout;
        SfoGroundHarness.DumpRoute(_output, route);

        HoldShortPoint hold = Assert.Single(
            route.HoldShortPoints,
            h => (h.Reason == HoldShortReason.ExplicitHoldShort) && SfoGroundHarness.HoldShortMatches(h, "F1")
        );
        Assert.True(layout.Nodes.TryGetValue(hold.NodeId, out GroundNode? node), $"hold-short node {hold.NodeId} is not in the layout");
        Assert.True(
            node!.Edges.Any(e => e.IsRunwayCenterline && e.MatchesRunway("1R")),
            $"F1 hold node {hold.NodeId} has no 1R runway-centerline edge (edges: {string.Join(", ", node.Edges.Select(e => e.TaxiwayName))})"
        );

        int centrelineIdx = route.Segments.FindIndex(s => s.Edge.Edge.MatchesRunway("1R"));
        int f1Idx = route.Segments.FindIndex(s => string.Equals(s.TaxiwayName, "F1", StringComparison.OrdinalIgnoreCase));
        string sequence = string.Join(" ", route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.True(centrelineIdx >= 0, $"route has no 1R centerline segment (taxiways: {sequence})");
        Assert.True(f1Idx >= 0, $"route has no F1 segment (taxiways: {sequence})");
        Assert.True(
            centrelineIdx < f1Idx,
            $"1R centerline segment is at {centrelineIdx}, after the first F1 segment at {f1Idx} (taxiways: {sequence})"
        );
    }

    /// <summary>
    /// A B12 departure cleared <c>Y A A1</c> leaves the stand on Y — the first named taxiway of the
    /// route is Y, not a shortcut straight onto A — and reaches A through an AY connector.
    /// The stand lead-out (<c>RAMP</c>) and the junction arcs (<c>"Y - AY2"</c>) are transitions, not
    /// legs of a taxiway, so they are skipped.
    /// </summary>
    [Fact]
    public void Route_FromB12_StartsOnTaxiwayY()
    {
        (TaxiRoute Route, AirportGroundLayout Layout)? resolved = Resolve("parking", "B12", "TAXI Y A A1 1R");
        if (resolved is null)
        {
            return;
        }

        TaxiRoute route = resolved.Value.Route;
        SfoGroundHarness.DumpRoute(_output, route);

        var legs = route.Segments.Select(s => s.TaxiwayName).Where(IsNamedTaxiway).ToList();
        string sequence = string.Join(" ", legs.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.True(
            legs.Count > 0,
            $"route has no named-taxiway segments (taxiways: {string.Join(" ", route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))})"
        );
        Assert.Equal("Y", legs[0]);

        int ayIdx = legs.FindIndex(t => t.StartsWith("AY", StringComparison.OrdinalIgnoreCase));
        int aIdx = legs.FindIndex(t => string.Equals(t, "A", StringComparison.OrdinalIgnoreCase));
        Assert.True(ayIdx >= 0, $"route reaches A without an AY connector (taxiways: {sequence})");
        Assert.True(aIdx >= 0, $"route has no A segment (taxiways: {sequence})");
        Assert.True(ayIdx < aIdx, $"AY connector is at {ayIdx}, after the first A segment at {aIdx} (taxiways: {sequence})");
    }

    /// <summary>
    /// True for a segment that is a leg of one named taxiway — not the ramp lead-out and not a
    /// junction arc, whose name joins the two taxiways it transitions between (<c>"Y - AY2"</c>).
    /// </summary>
    private static bool IsNamedTaxiway(string taxiwayName) =>
        !string.Equals(taxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase) && !taxiwayName.Contains(" - ", StringComparison.Ordinal);

    private (TaxiRoute Route, AirportGroundLayout Layout)? Resolve(string startKind, string startName, string command)
    {
        SfoGround? built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return null;
        }

        SfoGround ground = built.Value;
        AircraftState aircraft = Spawn(ground, startKind, startName);

        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        Assert.True(result.Success, $"'{command}' from {startKind} {startName} failed: {result.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        return (route, ground.Layout);
    }

    private static AircraftState Spawn(SfoGround ground, string startKind, string startName)
    {
        switch (startKind)
        {
            case "spot":
                return SfoGroundHarness.SpawnAtSpot(ground, Callsign, AircraftType, startName);
            case "parking":
                return SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, startName);
            case "holdshort":
            {
                (string? barSpec, string? towardTaxiway) = SplitToward(startName);
                (string? runway, string? taxiway) = SplitPair(startKind, barSpec);
                return SfoGroundHarness.SpawnAtHoldShort(ground, Callsign, AircraftType, (runway, taxiway, towardTaxiway));
            }
            case "junction":
            {
                (string? taxiA, string? taxiB) = SplitPair(startKind, startName);
                return SfoGroundHarness.SpawnAtJunction(ground, Callsign, AircraftType, taxiA, taxiB);
            }
            default:
                throw new InvalidOperationException($"unknown start kind '{startKind}'");
        }
    }

    private static (string First, string Second) SplitPair(string startKind, string startName)
    {
        string[] parts = startName.Split('/');
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"'{startKind}' start needs a 'first/second' name, got '{startName}'");
        }

        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Splits the hold-short start form <c>"&lt;rwy&gt;/&lt;twy&gt;&gt;&lt;towardTwy&gt;"</c> — e.g.
    /// <c>"28L/Q&gt;B"</c> — into the bar spec and the taxiway that picks the side of the runway.
    /// </summary>
    private static (string Bar, string TowardTaxiway) SplitToward(string startName)
    {
        string[] parts = startName.Split('>');
        if (parts.Length != 2)
        {
            throw new InvalidOperationException($"'holdshort' start needs a 'rwy/twy>towardTwy' name, got '{startName}'");
        }

        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Parses the compact expectation form — <c>"1L:Crossing:cleared;1R:Explicit;28L:Destination"</c> —
    /// into the tuples <see cref="SfoGroundHarness.AssertHoldShorts"/> takes, so the InlineData rows
    /// stay readable.
    /// </summary>
    private static (string Target, HoldShortReason Reason, bool Cleared)[] ParseExpected(string expected)
    {
        if (expected.Length == 0)
        {
            return [];
        }

        return expected.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ParseOne).ToArray();
    }

    private static (string Target, HoldShortReason Reason, bool Cleared) ParseOne(string token)
    {
        string[] parts = token.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            throw new InvalidOperationException($"expectation token '{token}' must be 'target:reason' or 'target:reason:cleared'");
        }

        HoldShortReason reason = parts[1] switch
        {
            "Explicit" => HoldShortReason.ExplicitHoldShort,
            "Destination" => HoldShortReason.DestinationRunway,
            "Crossing" => HoldShortReason.RunwayCrossing,
            _ => throw new InvalidOperationException($"unknown hold-short reason '{parts[1]}' in '{token}'"),
        };

        bool cleared = (parts.Length == 3) && string.Equals(parts[2], "cleared", StringComparison.Ordinal);
        if ((parts.Length == 3) && !cleared)
        {
            throw new InvalidOperationException($"third field of '{token}' must be 'cleared'");
        }

        return (parts[0], reason, cleared);
    }
}
