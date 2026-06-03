using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="OneWayResolver"/> — resolving authored coordinate-polyline one-way constraints
/// against a real airport layout into a set of forbidden directed node moves. Uses real SFO taxiway-A
/// geometry (exact GeoJSON vertices in <c>TestData/sfo.geojson</c>).
/// </summary>
public class OneWayResolverTests
{
    // Confirmed exact SFO GeoJSON vertices (see docs / investigation): taxiway A run, A x B1 junction.
    private static readonly OneWayPoint A0 = new(37.619842, -122.392652, "A"); // also the T9 crossing
    private static readonly OneWayPoint A1 = new(37.620439, -122.392258, "A"); // also the B4/B5/T8 crossing
    private static readonly OneWayPoint A7 = new(37.622476, -122.390117, "A");
    private static readonly OneWayPoint A8 = new(37.622706, -122.389297, "A"); // == B1[0], the A x B1 junction
    private static readonly OneWayPoint B1_1 = new(37.622814, -122.389559, "B1");

    private static AirportGroundLayout? LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null) : null;
    }

    [Fact]
    public void SingleEdge_Reverse_ForbidsAgainstDirectionOnly()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var fwd = OneWayResolver.Resolve(layout, [new OneWayConstraint([A0, A1], BlockBoth: false, Notes: null)]);
        Assert.NotEmpty(fwd);

        var n0 = layout.FindNearestNode(A0.Lat, A0.Lon)!;
        var n1 = layout.FindNearestNode(A1.Lat, A1.Lon)!;
        if (n0.Edges.Any(e => e.HasNode(n1.Id)))
        {
            // Directly connected: the reverse move is forbidden, the with-flow move is not.
            Assert.Contains((n1.Id, n0.Id), fwd);
            Assert.DoesNotContain((n0.Id, n1.Id), fwd);
        }
    }

    [Fact]
    public void Block_Both_ForbidsEitherDirection()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var fwd = OneWayResolver.Resolve(layout, [new OneWayConstraint([A0, A1], BlockBoth: false, Notes: null)]);
        var both = OneWayResolver.Resolve(layout, [new OneWayConstraint([A0, A1], BlockBoth: true, Notes: null)]);

        // "both" forbids each span edge in either direction — exactly twice the one-way set, and a superset.
        Assert.Equal(fwd.Count * 2, both.Count);
        foreach (var move in fwd)
        {
            Assert.Contains(move, both);
            Assert.Contains((move.To, move.From), both); // the forward direction is now forbidden too
        }
    }

    [Fact]
    public void SameTaxiwaySpan_TwoEndpoints_FillsTheSpan()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        // A0 -> A8 spans several A edges; the resolver BFS fills the whole span, not just the endpoints.
        var single = OneWayResolver.Resolve(layout, [new OneWayConstraint([A0, A1], BlockBoth: false, Notes: null)]);
        var span = OneWayResolver.Resolve(layout, [new OneWayConstraint([A0, A8], BlockBoth: false, Notes: null)]);

        Assert.True(span.Count > single.Count, "A0->A8 must forbid more edges than the single A0->A1 segment.");
    }

    [Fact]
    public void CrossTaxiwayTurn_ForbidsBothLegs()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        // A7 -> A8 (an A edge) then A8 -> B1_1 (a B1 edge): a turn through the A x B1 junction.
        var forbidden = OneWayResolver.Resolve(layout, [new OneWayConstraint([A7, A8, B1_1], BlockBoth: false, Notes: null)]);

        Assert.True(forbidden.Count >= 2, "A two-leg turn must forbid the reverse of each leg.");
    }

    [Fact]
    public void WrongTaxiwayTag_StillResolves()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        // Tagging A0/A1 as "B" is a validation mismatch (logged) but must not stop resolution.
        var mistagged = new OneWayConstraint([A0 with { Taxiway = "B" }, A1 with { Taxiway = "B" }], BlockBoth: false, Notes: null);
        var forbidden = OneWayResolver.Resolve(layout, [mistagged]);

        Assert.NotEmpty(forbidden);
    }
}
