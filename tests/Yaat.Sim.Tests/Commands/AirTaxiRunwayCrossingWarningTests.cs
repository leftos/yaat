using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// An <c>ATXI</c> flies direct, but an air taxi is a ground movement that needs a clearance for every runway it
/// crosses. The direct path is kept, and the success line warns the instructor once per runway the straight path
/// crosses, in the order the path meets them: <c>Air taxi to spot D1 [direct path crosses runway 28R - 10L]</c>.
/// <para>
/// The fixtures come from KOAK's real layout: each test searches its named stands, helipads and spots for a start
/// and a destination whose straight path has the row's geometry, measured here against the runway centerlines with
/// a <see cref="ClearMarginFt"/> margin either side of the pavement edge, so a fixture can never sit on the
/// boundary and can never silently stop crossing.
/// </para>
/// </summary>
public class AirTaxiRunwayCrossingWarningTests(ITestOutputHelper output)
{
    private const double KoakFieldElevFt = 6.0;

    /// <summary>How far past the pavement edge a path must stay, or cut, for a fixture to count as clear or crossing.</summary>
    private const double ClearMarginFt = 100.0;

    /// <summary>How close to the pavement edge a path must come for a fixture to count as running beside the runway.</summary>
    private const double BesideWithinFt = 600.0;

    [Fact]
    public void PathCrossingOneRunway_WarnsOnceNamingIt()
    {
        AirportGroundLayout layout = Layout();
        (Candidate start, Candidate dest, List<GroundRunway> crossed) = FindPath(layout, Stands(layout), Stands(layout), (c, _) => c.Count == 1);
        Assert.True(OppositeSides(crossed[0], start.Node.Position, dest.Node.Position), "the endpoints must lie on opposite sides of the runway");

        CommandResult result = Dispatch(layout, start.Node.Position, dest.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Air taxi to {dest.Name} [direct path crosses runway {crossed[0].Name}]", result.Message);
    }

    [Fact]
    public void PathCrossingTwoRunways_WarnsForEachInPathOrder()
    {
        AirportGroundLayout layout = Layout();
        (Candidate start, Candidate dest, List<GroundRunway> crossed) = FindPath(layout, Stands(layout), Stands(layout), (c, _) => c.Count == 2);
        Assert.All(crossed, r => Assert.True(OppositeSides(r, start.Node.Position, dest.Node.Position), $"{r.Name}: endpoints on one side"));

        CommandResult outbound = Dispatch(layout, start.Node.Position, dest.Token);
        CommandResult back = Dispatch(layout, dest.Node.Position, start.Token);

        Assert.True(outbound.Success, outbound.Message);
        Assert.Equal(
            $"Air taxi to {dest.Name} [direct path crosses runway {crossed[0].Name}; direct path crosses runway {crossed[1].Name}]",
            outbound.Message
        );
        Assert.True(back.Success, back.Message);
        Assert.Equal(
            $"Air taxi to {start.Name} [direct path crosses runway {crossed[1].Name}; direct path crosses runway {crossed[0].Name}]",
            back.Message
        );
    }

    [Fact]
    public void PathBesideARunway_NeverWithinItsHalfWidth_DrawsNoWarning()
    {
        AirportGroundLayout layout = Layout();
        (Candidate start, Candidate dest, List<GroundRunway> _) = FindPath(
            layout,
            Stands(layout),
            Stands(layout),
            (c, path) => (c.Count == 0) && layout.Runways.Any(r => ClosestApproachFt(r, path.Start, path.End) <= (r.WidthFt / 2) + BesideWithinFt)
        );

        CommandResult result = Dispatch(layout, start.Node.Position, dest.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Air taxi to {dest.Name}", result.Message);
    }

    /// <summary>
    /// A located runway air taxi whose taxiway has bars only across the runway from the heli: the path cuts the
    /// destination runway's centerline, and an assigned runway does not authorize crossing it (AIM 4-3-18.a.6).
    /// </summary>
    [Fact]
    public void FarSideDestinationBar_IsWarnedLikeAnyOtherRunway()
    {
        AirportGroundLayout layout = Layout();
        foreach (Candidate start in Stands(layout))
        {
            foreach (GroundRunway runway in layout.Runways)
            {
                LatLon from = start.Node.Position;
                if (DistanceToCenterlineFt(runway, from) <= (runway.WidthFt / 2) + ClearMarginFt)
                {
                    continue;
                }

                foreach (string taxiway in FarSideOnlyTaxiways(layout, runway, from))
                {
                    string token = $"{runway.Id.End1}@{taxiway}";
                    if (
                        !GroundCommandHandler.TryResolveAirTaxiDestination(layout, token, from, out GroundCommandHandler.AirTaxiDestination? r)
                        || !OppositeSides(runway, from, r.Target)
                        || (DistanceToCenterlineFt(runway, r.Target) <= (runway.WidthFt / 2) + ClearMarginFt)
                        || layout.Runways.Any(other => !ReferenceEquals(other, runway) && !IsClear(other, from, r.Target))
                    )
                    {
                        continue;
                    }

                    Assert.NotNull(CenterlineCut(runway, from, r.Target));
                    output.WriteLine($"ATXI {token} from {start.Token}");
                    CommandResult result = Dispatch(layout, from, token);

                    Assert.True(result.Success, result.Message);
                    Assert.Equal($"Air taxi to {r.Name}, holding short [direct path crosses runway {runway.Name}]", result.Message);
                    return;
                }
            }
        }

        Assert.Fail("no KOAK stand has a located runway destination whose bar is across that runway");
    }

    /// <summary>
    /// A guard: a runway air taxi to a bar on the heli's own side of the destination runway, whose path never cuts
    /// that runway's centerline, draws no warning naming the destination. No real layout draws a hold line inside the
    /// half-width band, so this path never enters it; the destination clause is pinned from the other side by
    /// <see cref="FarSideDestinationBar_IsWarnedLikeAnyOtherRunway"/>.
    /// </summary>
    [Fact]
    public void RunwayAirTaxi_NearSideBar_DrawsNoWarningForTheDestination()
    {
        AirportGroundLayout layout = Layout();
        foreach (Candidate start in Stands(layout))
        {
            foreach (GroundRunway runway in layout.Runways)
            {
                LatLon from = start.Node.Position;
                foreach (string token in (string[])[runway.Id.End1, runway.Id.End2])
                {
                    if (
                        !GroundCommandHandler.TryResolveAirTaxiDestination(layout, token, from, out GroundCommandHandler.AirTaxiDestination? r)
                        || (CenterlineCut(runway, from, r.Target) is not null)
                        || (DistanceToCenterlineFt(runway, from) <= (runway.WidthFt / 2) + ClearMarginFt)
                        || layout.Runways.Any(other => !ReferenceEquals(other, runway) && !IsClear(other, from, r.Target))
                    )
                    {
                        continue;
                    }

                    Assert.False(OppositeSides(runway, from, r.Target), "the bar must be on the heli's own side of the runway");
                    output.WriteLine($"ATXI {token} from {start.Token}");
                    CommandResult result = Dispatch(layout, from, token);

                    Assert.True(result.Success, result.Message);
                    Assert.Equal($"Air taxi to {r.Name}, holding short", result.Message);
                    Assert.DoesNotContain($"crosses runway {runway.Name}", result.Message, StringComparison.Ordinal);
                    return;
                }
            }
        }

        Assert.Fail("no KOAK stand reaches a near-side runway bar without crossing another runway");
    }

    /// <summary>
    /// A heli on a runway's pavement, a little off the centerline on the side away from its destination: the path
    /// cuts that centerline as it leaves, which is leaving the runway, not crossing it.
    /// </summary>
    [Fact]
    public void StartOnARunwaysPavement_DestinationOffIt_DrawsNoWarningForThatRunway()
    {
        AirportGroundLayout layout = Layout();
        foreach (GroundRunway runway in layout.Runways)
        {
            (LatLon a, LatLon b) = (Centerline(runway)[0].A, Centerline(runway)[^1].B);
            double bearing = GeoMath.BearingTo(a, b);
            foreach (
                GroundNode anchor in layout
                    .Nodes.Values.OrderBy(n => n.Id)
                    .Where(n => DistanceToCenterlineFt(runway, n.Position) <= runway.WidthFt / 16)
            )
            {
                foreach (Candidate dest in Stands(layout))
                {
                    double side = GeoMath.SignedCrossTrackDistanceNmRaw(dest.Node.Position, a, bearing);
                    double away = (((bearing - (Math.Sign(side) * 90)) % 360) + 360) % 360;
                    LatLon from = GeoMath.ProjectPoint(anchor.Position, new TrueHeading(away), 3 * runway.WidthFt / 16 / GeoMath.FeetPerNm);
                    double offFt = DistanceToCenterlineFt(runway, from);
                    if (
                        (offFt <= runway.WidthFt / 8)
                        || (offFt >= runway.WidthFt / 4)
                        || !OppositeSides(runway, from, dest.Node.Position)
                        || (DistanceToCenterlineFt(runway, dest.Node.Position) <= (runway.WidthFt / 2) + ClearMarginFt)
                        || layout.Runways.Any(other => !ReferenceEquals(other, runway) && !IsClear(other, from, dest.Node.Position))
                    )
                    {
                        continue;
                    }

                    Assert.NotNull(CenterlineCut(runway, from, dest.Node.Position));
                    output.WriteLine($"start {offFt:F0} ft off {runway.Name} by node {anchor.Id}, ATXI {dest.Token}");
                    CommandResult result = Dispatch(layout, from, dest.Token);

                    Assert.True(result.Success, result.Message);
                    Assert.Equal($"Air taxi to {dest.Name}", result.Message);
                    return;
                }
            }
        }

        Assert.Fail("no KOAK runway has a point on its pavement and a stand across it reachable without crossing another runway");
    }

    [Fact]
    public void CrossingWarningOnASpotAirTaxi_ReadsSpotThenTheWarning()
    {
        AirportGroundLayout layout = Layout();
        List<Candidate> spots = [.. Candidates(layout).Where(c => c.Node.Type == GroundNodeType.Spot)];
        (Candidate start, Candidate dest, List<GroundRunway> crossed) = FindPath(layout, Stands(layout), spots, (c, _) => c.Count == 1);
        Assert.True(OppositeSides(crossed[0], start.Node.Position, dest.Node.Position), "the endpoints must lie on opposite sides of the runway");

        CommandResult result = Dispatch(layout, start.Node.Position, dest.Token);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Air taxi to spot {dest.Name} [direct path crosses runway {crossed[0].Name}]", result.Message);
    }

    /// <summary>A named stand, helipad or spot the ATXI marker resolves back to this same node.</summary>
    private sealed record Candidate(GroundNode Node, string Name, string Token);

    private readonly record struct StraightPath(LatLon Start, LatLon End);

    private static AirportGroundLayout Layout()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        return layout;
    }

    private static List<Candidate> Candidates(AirportGroundLayout layout) =>
        [
            .. layout
                .Nodes.Values.Where(n => (n.Name is { Length: > 0 } name) && name.Equals(name.ToUpperInvariant(), StringComparison.Ordinal))
                .Select(n => ToCandidate(layout, n))
                .OfType<Candidate>()
                .OrderBy(c => c.Token, StringComparer.Ordinal),
        ];

    private static Candidate? ToCandidate(AirportGroundLayout layout, GroundNode node)
    {
        string name = node.Name!;
        return node.Type switch
        {
            GroundNodeType.Parking
            or GroundNodeType.Helipad when ReferenceEquals(layout.FindHelipadByName(name) ?? layout.FindParkingByName(name), node) => new Candidate(
                node,
                name,
                "@" + name
            ),
            GroundNodeType.Spot when ReferenceEquals(layout.FindSpotNodeByName(name), node) => new Candidate(node, name, "$" + name),
            _ => null,
        };
    }

    private static List<Candidate> Stands(AirportGroundLayout layout) => [.. Candidates(layout).Where(c => c.Token.StartsWith('@'))];

    /// <summary>The taxiways a hold-short bar sits on — straight taxiway edges only, never a junction arc or a runway.</summary>
    private static IEnumerable<string> TaxiwaysOf(GroundNode bar) =>
        bar
            .Edges.Select(e => e.TaxiwayName)
            .Where(n => !n.Contains(" - ", StringComparison.Ordinal) && !n.StartsWith("RWY", StringComparison.OrdinalIgnoreCase));

    /// <summary>The taxiways whose every bar on <paramref name="runway"/> lies across it from <paramref name="from"/>.</summary>
    private static List<string> FarSideOnlyTaxiways(AirportGroundLayout layout, GroundRunway runway, LatLon from)
    {
        List<GroundNode> bars = layout.GetRunwayHoldShortNodes(runway.Id.End1);
        return
        [
            .. bars.SelectMany(TaxiwaysOf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .Where(t => bars.Where(bar => bar.Edges.Any(e => e.MatchesTaxiway(t))).All(bar => OppositeSides(runway, from, bar.Position))),
        ];
    }

    /// <summary>
    /// The first start and destination (in token order) whose straight path is clearly clear of, or clearly across,
    /// every runway, and whose crossed runways — in the order the path meets them — satisfy <paramref name="accept"/>.
    /// </summary>
    private (Candidate Start, Candidate Dest, List<GroundRunway> Crossed) FindPath(
        AirportGroundLayout layout,
        List<Candidate> starts,
        List<Candidate> destinations,
        Func<List<GroundRunway>, StraightPath, bool> accept
    )
    {
        foreach (Candidate start in starts)
        {
            foreach (Candidate dest in destinations)
            {
                var path = new StraightPath(start.Node.Position, dest.Node.Position);
                if (!ReferenceEquals(start, dest) && (Classify(layout, path) is { } crossed) && accept(crossed, path))
                {
                    output.WriteLine($"{start.Token} -> {dest.Token} crosses [{string.Join(", ", crossed.Select(r => r.Name))}]");
                    return (start, dest, crossed);
                }
            }
        }

        Assert.Fail("no KOAK start/destination pair has the geometry this test needs");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The runways the path clearly crosses, in path order, or null when any runway is too close to call.</summary>
    private static List<GroundRunway>? Classify(AirportGroundLayout layout, StraightPath path)
    {
        var crossed = new List<(GroundRunway Runway, double T)>();
        foreach (GroundRunway runway in layout.Runways)
        {
            if (IsClear(runway, path.Start, path.End))
            {
                continue;
            }

            double edgeFt = (runway.WidthFt / 2) + ClearMarginFt;
            if (
                (CenterlineCut(runway, path.Start, path.End) is not { } t)
                || (DistanceToCenterlineFt(runway, path.Start) <= edgeFt)
                || (DistanceToCenterlineFt(runway, path.End) <= edgeFt)
            )
            {
                return null;
            }

            crossed.Add((runway, t));
        }

        return [.. crossed.OrderBy(c => c.T).Select(c => c.Runway)];
    }

    private static bool IsClear(GroundRunway runway, LatLon a, LatLon b) => ClosestApproachFt(runway, a, b) > (runway.WidthFt / 2) + ClearMarginFt;

    private static List<(LatLon A, LatLon B)> Centerline(GroundRunway runway)
    {
        var points = runway.Coordinates.Select(c => new LatLon(c.Lat, c.Lon)).ToList();
        return [.. points.Zip(points.Skip(1))];
    }

    /// <summary>The fraction along a→b where it cuts the runway centerline first, or null when it never does.</summary>
    private static double? CenterlineCut(GroundRunway runway, LatLon a, LatLon b) =>
        Centerline(runway).Select(s => GeoMath.SegmentsIntersect(a, b, s.A, s.B)?.T).Where(t => t is not null).Min();

    private static double DistanceToCenterlineFt(GroundRunway runway, LatLon point) =>
        Centerline(runway).Min(s => GeoMath.DistanceToSegmentFt(point, s.A, s.B));

    /// <summary>How close the straight path a→b comes to the runway centerline, zero where it cuts it.</summary>
    private static double ClosestApproachFt(GroundRunway runway, LatLon a, LatLon b)
    {
        if (CenterlineCut(runway, a, b) is not null)
        {
            return 0;
        }

        return Centerline(runway)
            .Min(s =>
                Math.Min(
                    Math.Min(GeoMath.DistanceToSegmentFt(a, s.A, s.B), GeoMath.DistanceToSegmentFt(b, s.A, s.B)),
                    Math.Min(GeoMath.DistanceToSegmentFt(s.A, a, b), GeoMath.DistanceToSegmentFt(s.B, a, b))
                )
            );
    }

    /// <summary>Both points lie off the pavement on opposite sides of the runway, by the centerline's own bearing.</summary>
    private static bool OppositeSides(GroundRunway runway, LatLon a, LatLon b)
    {
        (LatLon first, LatLon last) = (Centerline(runway)[0].A, Centerline(runway)[^1].B);
        double bearing = GeoMath.BearingTo(first, last);
        double sideA = GeoMath.SignedCrossTrackDistanceNmRaw(a, first, bearing);
        double sideB = GeoMath.SignedCrossTrackDistanceNmRaw(b, first, bearing);
        return Math.Sign(sideA) == -Math.Sign(sideB);
    }

    private static CommandResult Dispatch(AirportGroundLayout layout, LatLon start, string token)
    {
        var heli = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "EC35",
            Position = start,
            TrueHeading = new TrueHeading(280),
            Altitude = KoakFieldElevFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        heli.Ground.Layout = layout;
        heli.Phases = new PhaseList();

        ParsedCommand parsed = CommandParser.Parse($"ATXI {token}").Value!;
        return CommandDispatcher.Dispatch(parsed, heli, TestDispatch.Context(Random.Shared, groundLayout: layout));
    }
}
