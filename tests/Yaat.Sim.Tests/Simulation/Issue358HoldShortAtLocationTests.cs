using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #358: <c>HS &lt;target&gt;@&lt;taxiway&gt;</c> — hold short of a
/// taxiway or runway at a specific location taxiway.
///
/// Scenario (from the Discord report, KOAK): an aircraft that exited 28R at E is holding short
/// of C on E. The controller issues <c>TAXI C D J HS C</c> intending "taxi C, D, J and hold
/// short of C where J meets C". Instead the bare <c>HS C</c> binds the first C-adjacent node
/// on the route (the E/C junction the aircraft is already at, so it holds immediately), and
/// the route terminates at the D/J junction without ever traveling J.
///
/// The new syntax <c>HS C@J</c> ("hold short of C, on J") disambiguates which crossing binds
/// and steers the route along J to that crossing. <c>HS 28R@J</c> does the same for a runway
/// target. No recording exists for this issue — the tests script the route directly on the
/// real OAK layout, resolving all node IDs dynamically (node IDs are mint-order ephemeral).
/// </summary>
public class Issue358HoldShortAtLocationTests(ITestOutputHelper output)
{
    private const double FeetPerNm = 6076.12;

    private static AirportGroundLayout? LoadOak()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null) : null;
    }

    /// <summary>
    /// The aircraft's holding position from the report: on E, just short of C — the E-side
    /// neighbor of the E/C junction.
    /// </summary>
    private static GroundNode? FindStartNodeOnEShortOfC(AirportGroundLayout layout)
    {
        var junction = layout.FindIntersectionNode("E", "C");
        if (junction is null)
        {
            return null;
        }

        foreach (var edge in junction.Edges)
        {
            if (edge.MatchesTaxiway("E") && !edge.MatchesTaxiway("C"))
            {
                return edge.OtherNode(junction);
            }
        }

        return null;
    }

    private static AircraftState MakeAircraftAt(GroundNode node, double headingTrue)
    {
        var ac = new AircraftState
        {
            Callsign = "N358HS",
            AircraftType = "C172",
            Position = node.Position,
            TrueHeading = new TrueHeading(headingTrue),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static bool NodeIncidentTo(GroundNode node, string taxiway) => node.Edges.Any(e => e.MatchesTaxiway(taxiway));

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * FeetPerNm;

    private static TaxiCommand ParseTaxi(string input)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, $"'{input}' failed to parse: {parsed.Reason}");
        return Assert.IsType<TaxiCommand>(parsed.Value);
    }

    private void LogRoute(string label, TaxiRoute? route)
    {
        if (route is null)
        {
            output.WriteLine($"[{label}] route=<null>");
            return;
        }

        var taxiways = route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"[{label}] taxiways=[{string.Join(", ", taxiways)}]");
        output.WriteLine($"[{label}] warnings=[{string.Join(" | ", route.Warnings)}]");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"[{label}]   HS node={hs.NodeId} target={hs.TargetName} reason={hs.Reason} cleared={hs.IsCleared}");
        }
    }

    /// <summary>
    /// The core fix: <c>TAXI C D J HS C@J</c> travels J and binds the hold-short to the
    /// C crossing on J — not the E/C junction the aircraft starts at.
    /// </summary>
    [Fact]
    public void TaxiCdj_HsCAtJ_RoutesAlongJ_AndBindsJSideCrossing()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        var start = FindStartNodeOnEShortOfC(layout);
        Assert.NotNull(start);
        var ecJunction = layout.FindIntersectionNode("E", "C");
        Assert.NotNull(ecJunction);
        var jcJunction = layout.FindIntersectionNode("J", "C");
        Assert.NotNull(jcJunction);

        double headingTowardC = GeoMath.BearingTo(start.Position, ecJunction.Position);
        var ac = MakeAircraftAt(start, headingTowardC);

        var taxi = ParseTaxi("TAXI C D J HS C@J");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        var route = ac.Ground.AssignedTaxiRoute;
        LogRoute("C@J", route);
        Assert.True(result.Success, $"TAXI C D J HS C@J failed: {result.Message}");
        Assert.NotNull(route);

        // The route actually travels J.
        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "J", StringComparison.OrdinalIgnoreCase));

        // Exactly one explicit hold-short for C, bound on J at the J/C crossing.
        var hs = Assert.Single(
            route.HoldShortPoints,
            h => (h.Reason == HoldShortReason.ExplicitHoldShort) && (h.TargetName?.StartsWith("C", StringComparison.OrdinalIgnoreCase) == true)
        );
        Assert.True(layout.Nodes.TryGetValue(hs.NodeId, out var boundNode), $"HS node {hs.NodeId} not in layout");

        Assert.True(NodeIncidentTo(boundNode, "J"), $"HS bound to node {boundNode.Id}, which is not on J");
        Assert.True(NodeIncidentTo(boundNode, "C"), $"HS bound to node {boundNode.Id}, which is not adjacent to C");
        Assert.False(
            NodeIncidentTo(boundNode, "E"),
            $"HS bound to node {boundNode.Id} at the E/C junction — the start-position crossing, not the J-side crossing"
        );

        double distToJc = DistanceFt(boundNode.Position, jcJunction.Position);
        Assert.True(distToJc < 300, $"HS node {boundNode.Id} is {distToJc:F0} ft from the J/C junction — expected the crossing on J");

        // The route terminates at the crossing — it does not continue down J past C
        // (nothing beyond the hold-short was cleared).
        var lastSeg = route.Segments[^1];
        Assert.True(layout.Nodes.TryGetValue(lastSeg.ToNodeId, out var endNode), $"route end node {lastSeg.ToNodeId} not in layout");
        double endDistToJc = DistanceFt(endNode.Position, jcJunction.Position);
        Assert.True(endDistToJc < 300, $"route ends {endDistToJc:F0} ft past the J/C junction (node {endNode.Id})");
    }

    /// <summary>
    /// Runway targets take the same qualifier: <c>HS 28R@J</c> binds the 28R hold-short bar
    /// that sits on J (J's far end past the C crossing).
    /// </summary>
    [Fact]
    public void TaxiCdj_HsRunway28RAtJ_BindsBarOnJ()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        var start = FindStartNodeOnEShortOfC(layout);
        Assert.NotNull(start);
        var ecJunction = layout.FindIntersectionNode("E", "C");
        Assert.NotNull(ecJunction);

        double headingTowardC = GeoMath.BearingTo(start.Position, ecJunction.Position);
        var ac = MakeAircraftAt(start, headingTowardC);

        var taxi = ParseTaxi("TAXI C D J HS 28R@J");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        var route = ac.Ground.AssignedTaxiRoute;
        LogRoute("28R@J", route);
        Assert.True(result.Success, $"TAXI C D J HS 28R@J failed: {result.Message}");
        Assert.NotNull(route);

        Assert.Contains(route.Segments, s => string.Equals(s.TaxiwayName, "J", StringComparison.OrdinalIgnoreCase));

        var hs = Assert.Single(
            route.HoldShortPoints,
            h => (h.Reason == HoldShortReason.ExplicitHoldShort) && (h.TargetName?.Contains("28R", StringComparison.OrdinalIgnoreCase) == true)
        );
        Assert.True(layout.Nodes.TryGetValue(hs.NodeId, out var boundNode), $"HS node {hs.NodeId} not in layout");
        Assert.Equal(GroundNodeType.RunwayHoldShort, boundNode.Type);
        Assert.NotNull(boundNode.RunwayId);
        Assert.True(boundNode.RunwayId.Value.Contains("28R"), $"HS bound to bar for {boundNode.RunwayId}, expected 28R");
        Assert.True(NodeIncidentTo(boundNode, "J"), $"HS bound to node {boundNode.Id}, which is not on J");
    }

    /// <summary>
    /// Pins today's bare-target behavior: <c>HS C</c> without a location keeps binding the
    /// first C-adjacent node in route-walk order (here: the E/C junction at the start).
    /// The new syntax is the disambiguator; the bare form must not change.
    /// </summary>
    [Fact]
    public void TaxiCdj_BareHsC_KeepsFirstCrossingBinding()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        var start = FindStartNodeOnEShortOfC(layout);
        Assert.NotNull(start);
        var ecJunction = layout.FindIntersectionNode("E", "C");
        Assert.NotNull(ecJunction);

        double headingTowardC = GeoMath.BearingTo(start.Position, ecJunction.Position);
        var ac = MakeAircraftAt(start, headingTowardC);

        var taxi = ParseTaxi("TAXI C D J HS C");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        var route = ac.Ground.AssignedTaxiRoute;
        LogRoute("bare C", route);
        Assert.True(result.Success, $"TAXI C D J HS C failed: {result.Message}");
        Assert.NotNull(route);

        var hs = Assert.Single(
            route.HoldShortPoints,
            h => (h.Reason == HoldShortReason.ExplicitHoldShort) && string.Equals(h.TargetName, "C", StringComparison.OrdinalIgnoreCase)
        );
        Assert.True(layout.Nodes.TryGetValue(hs.NodeId, out var boundNode), $"HS node {hs.NodeId} not in layout");

        double distToStart = DistanceFt(boundNode.Position, ecJunction.Position);
        Assert.True(distToStart < 300, $"bare HS C bound {distToStart:F0} ft from the first (E/C) crossing — behavior changed");

        // The instructor is told the hold binds right at the start and how to pick a later crossing.
        Assert.Contains(route.Warnings, w => w.Contains("HS C holds at the first C crossing", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Post-hoc located hold-short: a standalone <c>HS C@J</c> issued while the aircraft is
    /// already taxiing a route that travels J binds the C crossing on J, not the first
    /// C-adjacent node behind it on the route.
    /// </summary>
    [Fact]
    public void PostHoc_HsCAtJ_BindsJSideCrossingOnExistingRoute()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        var start = FindStartNodeOnEShortOfC(layout);
        Assert.NotNull(start);
        var ecJunction = layout.FindIntersectionNode("E", "C");
        Assert.NotNull(ecJunction);
        var jcJunction = layout.FindIntersectionNode("J", "C");
        Assert.NotNull(jcJunction);

        double headingTowardC = GeoMath.BearingTo(start.Position, ecJunction.Position);
        var ac = MakeAircraftAt(start, headingTowardC);

        // A route that travels the full length of J (hold short of 28R at its far end),
        // so the J-side C crossing is en route.
        var taxi = ParseTaxi("TAXI C D J HS 28R@J");
        var taxiResult = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        Assert.True(taxiResult.Success, $"setup taxi failed: {taxiResult.Message}");

        var parsed = CommandParser.Parse("HS C@J");
        Assert.True(parsed.IsSuccess, $"'HS C@J' failed to parse: {parsed.Reason}");
        var hsCmd = Assert.IsType<HoldShortCommand>(parsed.Value);

        var result = GroundCommandHandler.TryHoldShort(ac, hsCmd, layout);
        var route = ac.Ground.AssignedTaxiRoute;
        LogRoute("post-hoc C@J", route);
        Assert.True(result.Success, $"HS C@J failed: {result.Message}");
        Assert.NotNull(route);

        var hs = Assert.Single(
            route.HoldShortPoints,
            h => (h.Reason == HoldShortReason.ExplicitHoldShort) && (h.TargetName?.StartsWith("C", StringComparison.OrdinalIgnoreCase) == true)
        );
        Assert.True(layout.Nodes.TryGetValue(hs.NodeId, out var boundNode), $"HS node {hs.NodeId} not in layout");
        Assert.True(NodeIncidentTo(boundNode, "J"), $"HS bound to node {boundNode.Id}, which is not on J");
        double distToJc = DistanceFt(boundNode.Position, jcJunction.Position);
        Assert.True(distToJc < 300, $"HS node {boundNode.Id} is {distToJc:F0} ft from the J/C junction — expected the crossing on J");
    }

    // --- Parsing ---

    [Fact]
    public void Parse_MalformedLocatedTargets_Fail()
    {
        Assert.False(CommandParser.Parse("HS C@").IsSuccess, "'HS C@' (empty location) should fail to parse");
        Assert.False(CommandParser.Parse("HS @J").IsSuccess, "'HS @J' (empty target) should fail to parse");
        Assert.False(CommandParser.Parse("HS C@J@D").IsSuccess, "'HS C@J@D' (two separators) should fail to parse");
        Assert.False(CommandParser.Parse("RES HS C@").IsSuccess, "'RES HS C@' should fail to parse");
        Assert.False(CommandParser.Parse("CROSS 28R HS C@").IsSuccess, "'CROSS 28R HS C@' should fail to parse");
    }

    [Fact]
    public void Parse_LocatedTargets_RoundTripCanonical()
    {
        var hs = CommandParser.Parse("HS C@J");
        Assert.True(hs.IsSuccess, hs.Reason);
        Assert.Equal("HS C@J", CommandDescriber.DescribeCommand(hs.Value!));

        var res = CommandParser.Parse("RES HS C@J");
        Assert.True(res.IsSuccess, res.Reason);
        Assert.Equal("RES HS C@J", CommandDescriber.DescribeCommand(res.Value!));

        var cross = CommandParser.Parse("CROSS 28R HS 33@J");
        Assert.True(cross.IsSuccess, cross.Reason);
        Assert.Equal("CROSS 28R HS 33@J", CommandDescriber.DescribeCommand(cross.Value!));
    }

    // --- Pilot readback ---

    /// <summary>
    /// The pilot readback must verbalize the structured target, never leak the raw
    /// <c>@</c> token into speech. A taxiway target must not be read back as a runway.
    /// </summary>
    [Fact]
    public void Readback_LocatedTaxiwayTarget_SpeaksTaxiwayWithoutAtSign()
    {
        var parsed = CommandParser.Parse("HS C@J");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        string? spoken = PhraseologyVerbalizer.Verbalize(parsed.Value!);
        Assert.NotNull(spoken);
        output.WriteLine($"readback: {spoken}");

        Assert.DoesNotContain("@", spoken);
        Assert.Contains("charlie", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runway", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readback_LocatedRunwayTarget_SpeaksRunway()
    {
        var parsed = CommandParser.Parse("HS 28R@J");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        string? spoken = PhraseologyVerbalizer.Verbalize(parsed.Value!);
        Assert.NotNull(spoken);
        output.WriteLine($"readback: {spoken}");

        Assert.DoesNotContain("@", spoken);
        Assert.Contains("two eight right", spoken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A taxiway HS target inside a TAXI must never be read back as a runway ("hold short of
    /// runway charlie") — the pilot's readback would assert a runway hold that was never issued.
    /// Pins the aviation-review-required forms for all four target shapes.
    /// </summary>
    [Theory]
    [InlineData("TAXI C D J HS C", "taxi via charlie, delta, juliett, hold short of charlie")]
    [InlineData("TAXI C D J HS C@J", "taxi via charlie, delta, juliett, hold short of charlie at juliett")]
    [InlineData("TAXI B C HS 28R", "taxi via bravo, charlie, hold short of runway two eight right")]
    [InlineData("TAXI C D J HS 28R@J", "taxi via charlie, delta, juliett, hold short of runway two eight right at juliett")]
    public void Readback_TaxiWithHoldShort_RunwayWordMatchesTargetType(string command, string expected)
    {
        var parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.Equal(expected, PhraseologyVerbalizer.Verbalize(parsed.Value!));
    }

    /// <summary>
    /// The spoken located form must round-trip through the STT rule mapper — the pilot AI now
    /// SAYS "hold short of charlie at juliet", so a controller echoing it back must not have the
    /// location silently dropped (or, inside a TAXI, swallowed into the path variadic).
    /// </summary>
    [Theory]
    [InlineData("hold short of charlie at juliet", "HS C@J")]
    [InlineData("hold short of runway two eight right at juliet", "HS 28R@J")]
    [InlineData("hold short of charlie at taxiway juliet", "HS C@J")]
    [InlineData("hold short of charlie on juliet", "HS C@J")]
    [InlineData("taxi via charlie delta juliet hold short of charlie at juliet", "TAXI C D J HS C@J")]
    [InlineData("taxi via charlie delta juliet hold short of charlie", "TAXI C D J HS C")]
    public void Stt_LocatedHoldShort_RoundTripsToCanonical(string transcript, string expectedCanonical)
    {
        var ctx = MapContext.Empty with { TaxiwayNames = new HashSet<string>(["B", "C", "D", "J"], StringComparer.OrdinalIgnoreCase) };
        var result = PhraseologyMapper.Map(transcript, ctx);
        Assert.NotNull(result);
        Assert.Equal(expectedCanonical, result.CanonicalCommand);
    }
}
