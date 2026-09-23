using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// SFO taxilane Yankee runs ~208° true behind the B gates, and it reaches taxiway Alpha only through the
/// AY1 / AY2 / AY3 connectors (Y and A share no junction). An E75L pushed off gate B12 with
/// <c>PUSH Y A1</c> comes to rest on Y facing 208° — AY2 ~96 ft behind it, AY3 ~600 ft ahead — and the
/// follow-up clearance is <c>TAXI Y A A1 1R</c>.
///
/// <para>The Y→A bridge leaves through the connector the aircraft is facing. AY3 is both ahead of the nose
/// and the shorter way to A1 (3,440 ft vs 3,650 ft through AY2), and the mandatory-connector detour
/// (<c>SegmentExpander.TryDetour</c>) ranks its candidate A-entry nodes by the bridge's pavement cost plus a
/// reversal charge against the aircraft's pose rather than by the bridge's raw distance — so the 261 ft hop
/// back through AY2 pays for its about-face and loses to the 1,383 ft bridge through AY3, and the taxi out
/// of the ramp alley starts as one continuous turn.</para>
/// </summary>
public class SfoYankeeConnectorChoiceTests
{
    /// <summary>Rest position of the E75L after <c>PUSH Y A1</c> off gate B12 (measured from the push run).</summary>
    private const double StartLat = 37.61216797;

    /// <summary>Rest longitude of that same push.</summary>
    private const double StartLon = -122.38345984;

    /// <summary>Rest heading of that same push: along Y, toward A1 and AY3.</summary>
    private const double StartHeadingDeg = 208.0;

    /// <summary>A taxi out of a ramp alley turns onto the lane; it does not turn around on it.</summary>
    private const double MaxFirstSegmentTurnDeg = 90.0;

    private readonly ITestOutputHelper _output;

    public SfoYankeeConnectorChoiceTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin shared NavData/sidecar singletons before layout build (CLAUDE.md singleton races).
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiYAA1_FromYankeeFacingA1_BridgesThroughAy3_NotBackThroughAy2()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if ((layout is null) || (TestVnasData.NavigationDb is null))
        {
            _output.WriteLine("SFO layout or navdata not found — skipping");
            return;
        }

        var startPosition = new LatLon(StartLat, StartLon);
        var startHeading = new TrueHeading(StartHeadingDeg);

        // The same start-node resolution GroundCommandHandler.TryTaxi performs for a TAXI command.
        GroundNode? startNode = layout.FindNearestNodeForTaxi(startPosition, startHeading) ?? layout.FindNearestNode(startPosition);
        Assert.NotNull(startNode);

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            startNode!.Id,
            ["Y", "A", "A1"],
            out string? failReason,
            new ExplicitPathOptions
            {
                OccupiedTaxiway = null,
                DestinationRunway = "1R",
                ExplicitHoldShorts = [],
                StartHeadingTrue = StartHeadingDeg,
            },
            AircraftCategory.Jet
        );

        Assert.Null(failReason);
        Assert.NotNull(route);

        string sequence = string.Join(" ", route!.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase));
        double departureBearingDeg = route.Segments[0].Edge.DepartureBearing;
        double firstTurnDeg = startHeading.AbsAngleTo(new TrueHeading(departureBearingDeg));
        _output.WriteLine(
            $"start node #{startNode.Id}: route [{sequence}] in {route.Segments.Count} segments, "
                + $"first segment departs {departureBearingDeg:F0}° ({firstTurnDeg:F0}° off the {StartHeadingDeg:F0}° nose), "
                + $"warnings [{string.Join("; ", route.Warnings)}]"
        );

        Assert.True(
            firstTurnDeg <= MaxFirstSegmentTurnDeg,
            $"the route's first segment departs {departureBearingDeg:F0}°, {firstTurnDeg:F0}° off the {StartHeadingDeg:F0}° nose "
                + $"(limit {MaxFirstSegmentTurnDeg:F0}°) — the clearance leaves through a connector behind the aircraft (taxiways: {sequence})"
        );

        Assert.True(
            Traverses(route, "AY3") && !Traverses(route, "AY2"),
            $"the Y→A bridge used the connector behind the aircraft — expected AY3, ~600 ft ahead of the nose, "
                + $"got taxiways [{sequence}] with the first segment departing {departureBearingDeg:F0}° "
                + $"({firstTurnDeg:F0}° off the {StartHeadingDeg:F0}° nose)"
        );
    }

    /// <summary>
    /// True when any segment is a leg of <paramref name="twy"/> — including a junction arc that names it
    /// (<c>"Y - AY2"</c>), which is how the route enters the connector.
    /// </summary>
    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );
}
