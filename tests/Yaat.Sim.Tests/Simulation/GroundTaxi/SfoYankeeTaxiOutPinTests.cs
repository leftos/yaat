using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The end-to-end pin for the connector detour out of SFO gate B12: a departure pushed onto taxilane Yankee
/// facing A1 (nose on 208°, along Y) leaves along Y and reaches Alpha through the AY connector ahead of it
/// — AY3, ~600 ft down the lane, rather than AY2 ~96 ft behind the nose — as one continuous turn. The
/// mandatory-connector detour ranks its bridge candidates by pavement cost plus a reversal charge against
/// the aircraft's pose, not by the bridge's raw length, so the connector behind the nose pays for the
/// about-face it would take.
///
/// <para>This class drives the whole chain — push, clearance, resolved route, first seconds of the taxi —
/// so it reds on any part of it. The unit-level pin on the pathfinder's connector choice alone is
/// <c>Pathfinding/SfoYankeeConnectorChoiceTests</c>; this one is the check that the choice reaches the
/// aircraft. The pushes that behave on their own — a bare <c>PUSH Y</c> and the <c>PUSH A</c> control —
/// live in <see cref="SfoYankeePushTests"/>.</para>
/// </summary>
public class SfoYankeeTaxiOutPinTests
{
    private const double MinDistanceFromAlphaFt = 150.0;
    private const double OnTaxiwayToleranceFt = 40.0;
    private const double AlignmentToleranceDeg = 20.0;
    private const double MaxTurnDeg = 135.0;
    private const double AboutFaceDeg = 160.0;
    private const int PushBudgetSeconds = 120;

    /// <summary>
    /// How long the taxi-out is watched for. The AY connector ahead of the push (AY3) is ~700 ft down the
    /// lane, and a jet leaving a standstill at ramp taxi speed needs the better part of a minute to cover
    /// it — the window has to outlast that, or "still on Y" reads as a failure to leave rather than as the
    /// length of the lane.
    /// </summary>
    private const int TaxiObservationSeconds = 90;

    private readonly ITestOutputHelper _output;

    public SfoYankeeTaxiOutPinTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// <c>PUSH Y A1</c> off B12 comes to rest on Yankee (nearer Y than A, never inside 150 ft of Alpha while
    /// reversing) with the nose already along the direction of Y that leads to the AY connector, so the
    /// follow-up <c>TAXI Y A A1 1R</c> — which resolves Y → AY connector → A with the single
    /// destination-runway bar — is flown as one continuous turn rather than an about-face.
    /// </summary>
    [Fact]
    public void E75L_PushYTowardA1_EndsOnY_TaxiesOutWithoutReversal()
    {
        var built = SfoGroundHarness.Build(_output, autoCross: false);
        if (built is null)
        {
            return;
        }

        var ground = built.Value;
        var ac = SfoGroundHarness.SpawnParked(ground, "YKE1", "E75L", "B12");
        var push = Push(ground, ac, "PUSH Y A1");

        Assert.True(
            push.MinDistanceToAlphaFt >= MinDistanceFromAlphaFt,
            $"the pushback came within {push.MinDistanceToAlphaFt:F0}ft of taxiway A (floor {MinDistanceFromAlphaFt:F0}ft) — "
                + "the tail reversed across Yankee toward Alpha instead of stopping on the lane"
        );

        var restPosition = ac.Position;
        double restHeadingDeg = ac.TrueHeading.Degrees;
        AssertRestingOnYankee(ground, ac, push, "PUSH Y A1");

        var route = SendTaxi(ground, ac, "TAXI Y A A1 1R");
        var connectorEntry = ConnectorEntry(route, ground.Layout);
        double laneBearingDeg = YankeeBearingToward(ground.Layout, restPosition, connectorEntry);
        double alignErrDeg = new TrueHeading(laneBearingDeg).AbsAngleTo(new TrueHeading(restHeadingDeg));
        _output.WriteLine(
            $"push alignment: nose {restHeadingDeg:F0}°, Y toward the connector {laneBearingDeg:F0}°, off by {alignErrDeg:F0}°; "
                + $"rest is {GeoMath.DistanceNm(restPosition, connectorEntry) * GeoMath.FeetPerNm:F0}ft from the connector entry node"
        );
        Assert.True(
            alignErrDeg <= AlignmentToleranceDeg,
            $"'PUSH Y A1' left the nose on {restHeadingDeg:F0}° — expected ~{laneBearingDeg:F0}°, the direction of Y that leads "
                + $"toward the AY connector the taxi uses (off by {alignErrDeg:F0}°)"
        );

        AssertLeavesYankeeViaConnector(route);
        SfoGroundHarness.AssertHoldShorts(_output, route, ("1R", HoldShortReason.DestinationRunway, false));

        var taxi = ObserveTaxiStart(ground, ac, "Y");
        AssertContinuousTurn(taxi.MaxAbsTurnDeg, "TAXI Y A A1 1R");
        Assert.True(
            taxi.FirstTaxiwayLeft is not null,
            $"the aircraft was still on Y after {TaxiObservationSeconds}s of taxi — it never reached a connector (maxTurn={taxi.MaxAbsTurnDeg:F0}°)"
        );
        Assert.True(
            taxi.FirstTaxiwayLeft!.StartsWith("AY", StringComparison.OrdinalIgnoreCase),
            $"the first taxiway after Y was '{taxi.FirstTaxiwayLeft}' — expected an AY connector, not a direct hop onto Alpha"
        );
    }

    /// <summary>One pushback run: the second it finished and how close to Alpha it ever got.</summary>
    private readonly record struct PushRun(int CompletedSecond, double MinDistanceToAlphaFt);

    /// <summary>The first seconds of a taxi: the largest turn off the start heading and the first taxiway left for.</summary>
    private readonly record struct TaxiStart(double MaxAbsTurnDeg, string? FirstTaxiwayLeft);

    /// <summary>
    /// Issues a pushback and ticks until it completes, sampling the distance to taxiway A every second the
    /// aircraft is actually reversing.
    /// </summary>
    private PushRun Push(SfoGround ground, AircraftState ac, string command)
    {
        var result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, $"'{command}' from B12 failed: {result.Message}");

        bool everPushed = false;
        double minAlphaFt = double.PositiveInfinity;
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => everPushed && (ac.Phases?.CurrentPhase is not PushbackPhase),
            PushBudgetSeconds,
            _ =>
            {
                if (ac.Phases?.CurrentPhase is not PushbackPhase)
                {
                    return;
                }

                everPushed = true;
                minAlphaFt = Math.Min(minAlphaFt, SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "A", ac.Position));
            }
        );

        Assert.True(
            completed > 0,
            $"'{command}' never completed within {PushBudgetSeconds}s (phase={PhaseName(ac)}, ever in PushbackPhase={everPushed})"
        );
        return new PushRun(completed, minAlphaFt);
    }

    /// <summary>The push came to rest on Yankee: within the on-lane tolerance of Y, and nearer Y than Alpha.</summary>
    private void AssertRestingOnYankee(SfoGround ground, AircraftState ac, PushRun push, string command)
    {
        double distYFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "Y", ac.Position);
        double distAFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "A", ac.Position);
        _output.WriteLine(
            $"'{command}' completed t={push.CompletedSecond}s: distY={distYFt:F0}ft distA={distAFt:F0}ft "
                + $"minDistA={push.MinDistanceToAlphaFt:F0}ft heading={ac.TrueHeading.Degrees:F0}° phase={PhaseName(ac)}"
        );

        Assert.True(
            distYFt <= OnTaxiwayToleranceFt,
            $"the push ended {distYFt:F0}ft off taxilane Y, past the {OnTaxiwayToleranceFt:F0}ft on-lane tolerance"
        );
        Assert.True(distYFt < distAFt, $"the push ended closer to A ({distAFt:F0}ft) than to Y ({distYFt:F0}ft) — it pushed onto the wrong taxiway");
    }

    /// <summary>Issues a taxi clearance, asserts it was accepted, and returns the route it resolved to.</summary>
    private TaxiRoute SendTaxi(SfoGround ground, AircraftState ac, string command)
    {
        var result = ground.Engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, $"'{command}' after the pushback failed: {result.Message}");

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(_output, route);
        return route;
    }

    /// <summary>
    /// Ticks the taxi out and reports how far the nose ever swung off the heading it started from, plus the
    /// first named taxiway it moved onto after <paramref name="startTaxiway"/> (ramp lead-outs and junction
    /// arcs are transitions, not a change of taxiway).
    /// </summary>
    private TaxiStart ObserveTaxiStart(SfoGround ground, AircraftState ac, string startTaxiway)
    {
        double startHeadingDeg = ac.TrueHeading.Degrees;
        double maxTurnDeg = 0;
        string? firstLeft = null;

        SfoGroundHarness.TickUntil(
            ground.Engine,
            () => false,
            TaxiObservationSeconds,
            _ =>
            {
                maxTurnDeg = Math.Max(maxTurnDeg, Math.Abs(GeoMath.SignedBearingDifference(startHeadingDeg, ac.TrueHeading.Degrees)));
                string? taxiway = ac.Ground.CurrentTaxiway;
                if (
                    (firstLeft is null)
                    && (taxiway is not null)
                    && IsNamedTaxiway(taxiway)
                    && !string.Equals(taxiway, startTaxiway, StringComparison.OrdinalIgnoreCase)
                )
                {
                    firstLeft = taxiway;
                }
            }
        );

        _output.WriteLine(
            $"taxi from {startTaxiway}: start heading {startHeadingDeg:F0}°, max turn {maxTurnDeg:F0}° over "
                + $"{TaxiObservationSeconds}s, first taxiway left for = {firstLeft ?? "(still on start taxiway)"}"
        );
        return new TaxiStart(maxTurnDeg, firstLeft);
    }

    /// <summary>
    /// The turn out of the gate is one continuous turn onto the lane: an aircraft that swings ~180° has
    /// reversed course (turned back past the stand) rather than taxiing out.
    /// </summary>
    private static void AssertContinuousTurn(double maxAbsTurnDeg, string command)
    {
        Assert.True(
            maxAbsTurnDeg < AboutFaceDeg,
            $"'{command}' swung {maxAbsTurnDeg:F0}° off the heading it started the taxi on — that is an about-face (~180°), "
                + "the aircraft reversed course instead of taxiing out"
        );
        Assert.True(
            maxAbsTurnDeg <= MaxTurnDeg,
            $"'{command}' swung {maxAbsTurnDeg:F0}° off the heading it started the taxi on, past the {MaxTurnDeg:F0}° a continuous turn onto the lane takes"
        );
    }

    /// <summary>
    /// The B12 departure leaves on Yankee and reaches Alpha through an AY connector: the first named leg is
    /// Y, and an AY* leg precedes the first A leg.
    /// </summary>
    private static void AssertLeavesYankeeViaConnector(TaxiRoute route)
    {
        var legs = NamedLegs(route);
        string sequence = string.Join(" ", legs.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.True(legs.Count > 0, "the route has no named-taxiway segments (only ramp lead-outs and junction arcs)");
        Assert.Equal("Y", legs[0]);

        int ayIdx = legs.FindIndex(t => t.StartsWith("AY", StringComparison.OrdinalIgnoreCase));
        int aIdx = legs.FindIndex(t => string.Equals(t, "A", StringComparison.OrdinalIgnoreCase));
        Assert.True(ayIdx >= 0, $"the route reaches A without an AY connector (taxiways: {sequence})");
        Assert.True(aIdx >= 0, $"the route has no A segment (taxiways: {sequence})");
        Assert.True(ayIdx < aIdx, $"the AY connector is at leg {ayIdx}, after the first A leg at {aIdx} (taxiways: {sequence})");
    }

    /// <summary>The position of the node at which the route leaves Yankee for its AY connector.</summary>
    private static LatLon ConnectorEntry(TaxiRoute route, AirportGroundLayout layout)
    {
        var segment = route.Segments.FirstOrDefault(s => s.TaxiwayName.StartsWith("AY", StringComparison.OrdinalIgnoreCase));
        Assert.True(segment is not null, "the route has no AY connector segment to measure the push alignment against");
        Assert.True(layout.Nodes.TryGetValue(segment!.FromNodeId, out var node), $"route node {segment.FromNodeId} is not in the layout");
        return node!.Position;
    }

    /// <summary>
    /// The bearing of the Yankee centerline nearest <paramref name="from"/>, in whichever of its two
    /// directions heads toward <paramref name="target"/> — the heading an aircraft standing on the lane must
    /// be on to taxi toward that connector without turning around.
    /// </summary>
    private static double YankeeBearingToward(AirportGroundLayout layout, LatLon from, LatLon target)
    {
        double bestFt = double.PositiveInfinity;
        LatLon edgeFrom = default;
        LatLon edgeTo = default;
        foreach (var edge in layout.Edges)
        {
            if (!edge.MatchesTaxiway("Y"))
            {
                continue;
            }

            var a = edge.Nodes[0].Position;
            var b = edge.Nodes[1].Position;
            double distFt = GeoMath.DistanceToSegmentFt(from.Lat, from.Lon, a.Lat, a.Lon, b.Lat, b.Lon);
            if (distFt < bestFt)
            {
                bestFt = distFt;
                edgeFrom = a;
                edgeTo = b;
            }
        }

        Assert.True(double.IsFinite(bestFt), "the SFO layout has no straight Y centerline to measure the push alignment against");
        double forwardDeg = GeoMath.BearingTo(edgeFrom, edgeTo);
        double towardTargetDeg = GeoMath.BearingTo(from, target);
        return (Math.Abs(GeoMath.SignedBearingDifference(forwardDeg, towardTargetDeg)) <= 90.0)
            ? forwardDeg
            : new TrueHeading(forwardDeg).ToReciprocal().Degrees;
    }

    /// <summary>The route's segments that are legs of a named taxiway, in route order.</summary>
    private static List<string> NamedLegs(TaxiRoute route) => route.Segments.Select(s => s.TaxiwayName).Where(IsNamedTaxiway).ToList();

    /// <summary>
    /// True for a leg of one named taxiway — not the ramp lead-out and not a junction arc, whose name joins
    /// the two taxiways it transitions between (<c>"Y - AY2"</c>).
    /// </summary>
    private static bool IsNamedTaxiway(string taxiwayName) =>
        !string.Equals(taxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase) && !taxiwayName.Contains(" - ", StringComparison.Ordinal);

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";
}
