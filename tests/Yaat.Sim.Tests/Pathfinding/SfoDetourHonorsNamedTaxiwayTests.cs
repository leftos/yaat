using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// A mandatory-connector bridge that starts off the first cleared taxiway is how the route gets onto
/// that taxiway, so it must still reach it. SFO gate G3 hangs off the ramp with no junction between
/// Alpha and Quebec, so <c>TAXI A Q B F 28L HS 1L</c> bridges A→Q straight from the stand: a bridge
/// that arrives on Q without ever touching A leaves the cleared taxiway A unreached, and
/// <c>SegmentExpander.ResolveExplicit</c>'s honor-named-taxiway check then refuses the whole clearance
/// ("Cannot taxi via A from the aircraft's position").
///
/// <para>So the landing candidates are filtered by the honor check's own predicate before they are
/// ranked on bridge cost plus tail cost — the ranking may reorder the bridges, never drop the named
/// taxiway.</para>
/// </summary>
public class SfoDetourHonorsNamedTaxiwayTests
{
    /// <summary>Heading of the G3 stand, the nose attitude a parked aircraft taxis out from.</summary>
    private const double StandHeadingDeg = 32.0;

    private readonly ITestOutputHelper _output;

    public SfoDetourHonorsNamedTaxiwayTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin shared NavData/sidecar singletons before layout build (CLAUDE.md singleton races).
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void SfoG3_AQBF28L_HS1L_StillResolves_ThroughA()
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if ((layout is null) || (TestVnasData.NavigationDb is null))
        {
            _output.WriteLine("SFO layout or navdata not found — skipping");
            return;
        }

        var stand = layout.FindParkingByName("G3");
        Assert.True(stand is not null, "the SFO layout has no parking named 'G3'");

        var standHeading = new TrueHeading(StandHeadingDeg);

        // The same start-node resolution GroundCommandHandler.TryTaxi performs for a TAXI command.
        var startNode = layout.FindNearestNodeForTaxi(stand!.Position, standHeading) ?? layout.FindNearestNode(stand.Position);
        Assert.NotNull(startNode);

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode!.Id,
            ["A", "Q", "B", "F"],
            out string? failReason,
            new ExplicitPathOptions
            {
                DestinationRunway = "28L",
                ExplicitHoldShorts = [HoldShortTarget.Parse("1L")],
                StartHeadingTrue = StandHeadingDeg,
            },
            AircraftCategory.Jet
        );

        Assert.True(
            route is not null,
            $"'TAXI A Q B F 28L HS 1L' from G3 (start node #{startNode.Id}) resolved to no route: {failReason ?? "(no reason)"}"
        );
        Assert.Null(failReason);
        SfoGroundHarness.DumpRoute(_output, route!);

        int alphaIdx = FirstLegIndex(route, "A");
        int quebecIdx = FirstLegIndex(route, "Q");
        string sequence = string.Join(" ", route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.True(alphaIdx >= 0, $"the route never traverses the cleared taxiway A (taxiways: {sequence})");
        Assert.True(
            (quebecIdx >= 0) && (alphaIdx < quebecIdx),
            $"the route reaches Q at segment {quebecIdx} without traversing A first (A at segment {alphaIdx}; taxiways: {sequence})"
        );

        SfoGroundHarness.AssertHoldShorts(
            _output,
            route,
            ("1L", HoldShortReason.ExplicitHoldShort, false),
            ("1R", HoldShortReason.RunwayCrossing, false),
            ("28L", HoldShortReason.DestinationRunway, false)
        );
    }

    /// <summary>
    /// Index of the first segment that is a leg of <paramref name="twy"/>, counting a junction arc that
    /// names it (<c>"A - Q"</c>) — the turn onto or off the taxiway — as reaching it; -1 when the route
    /// never does.
    /// </summary>
    private static int FirstLegIndex(TaxiRoute route, string twy) =>
        route.Segments.FindIndex(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );
}
