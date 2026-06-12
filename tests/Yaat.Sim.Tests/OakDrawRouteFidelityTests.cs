using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Guards the server-side contract the ground draw-route tool relies on: a DENSE node-ref
/// path (every node along the drawn route) resolves to exactly the drawn geometry, and a
/// node-ref path that ends in a stand only claims that stand when the @parking / $spot token
/// is present.
///
/// The draw tool previews a route with the auto-router (FewestTurns) but used to send only
/// the sparse clicked waypoints; the server re-routed between them and could substitute a
/// parallel taxiway (drew V, taxied U) or skip a turn into parking. The fix sends the full
/// node list, which pins each leg to a single edge. These tests prove that pinning holds on
/// the real OAK graph.
/// </summary>
public class OakDrawRouteFidelityTests
{
    public OakDrawRouteFidelityTests() => TestVnasData.EnsureInitialized();

    private static AirportGroundLayout? LoadOak()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null) : null;
    }

    // The dense node list a drawn route commits: each segment's ToNodeId, consecutive-deduped
    // (the start node is the aircraft's own position, which the server resolves itself).
    private static List<int> DenseNodeIds(TaxiRoute route)
    {
        var ids = new List<int>();
        foreach (var seg in route.Segments)
        {
            if (ids.Count == 0 || ids[^1] != seg.ToNodeId)
            {
                ids.Add(seg.ToNodeId);
            }
        }

        return ids;
    }

    private static HashSet<int> NodeIdSet(TaxiRoute route)
    {
        var set = new HashSet<int>();
        foreach (var seg in route.Segments)
        {
            set.Add(seg.FromNodeId);
            set.Add(seg.ToNodeId);
        }

        return set;
    }

    [Fact]
    public void DenseNodePath_ReproducesPreviewRoute_OnRealGeometry()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        // Route the length of taxiway V — the cross-field corridor that runs parallel to U,
        // exactly where the sparse draw tool substituted U for V.
        var vNodes = layout.GetNodesOnTaxiway("V");
        if (vNodes.Count < 2)
        {
            return;
        }

        GroundNode a = vNodes[0];
        GroundNode b = vNodes[0];
        double best = -1;
        foreach (var p in vNodes)
        {
            foreach (var q in vNodes)
            {
                double d = GeoMath.DistanceNm(p.Position, q.Position);
                if (d > best)
                {
                    best = d;
                    a = p;
                    b = q;
                }
            }
        }

        const AircraftCategory cat = AircraftCategory.Jet;

        // What the draw tool previews (and what the user sees and shapes).
        var preview = TaxiPathfinder.FindRoute(layout, a.Id, b.Id, cat);
        Assert.NotNull(preview);
        Assert.NotEmpty(preview!.Segments);

        // What the server executes when the full node list is committed.
        var densePath = DenseNodeIds(preview).Select(id => $"#{id}").ToList();
        var resolved = TaxiPathfinder.ResolveExplicitPath(layout, a.Id, densePath, out var fail, new ExplicitPathOptions(), cat);

        Assert.True(resolved is not null, $"dense path failed to resolve: {fail}");
        // Faithful: the dense command visits exactly the previewed nodes — no parallel-taxiway
        // substitution, no skipped turns.
        Assert.True(
            NodeIdSet(preview).SetEquals(NodeIdSet(resolved!)),
            $"dense route node set differs from preview\npreview:  {string.Join(",", NodeIdSet(preview).Order())}\nresolved: {string.Join(",", NodeIdSet(resolved!).Order())}"
        );
    }

    [Fact]
    public void DenseNodePathToParking_ClaimsStandOnlyWithToken()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        var parking = layout.FindParkingByName("8B");
        if (parking is null)
        {
            return;
        }

        // Anchor the taxi at a 28R hold-short and draw a route to stand 8B.
        var holdShorts = layout.GetRunwayHoldShortNodes("28R");
        if (holdShorts.Count == 0)
        {
            return;
        }

        var start = holdShorts[0];
        const AircraftCategory cat = AircraftCategory.Jet;
        var preview = TaxiPathfinder.FindRoute(layout, start.Id, parking.Id, cat);
        if (preview is null || preview.Segments.Count == 0)
        {
            return;
        }

        var densePath = DenseNodeIds(preview).Select(id => $"#{id}").ToList();
        Assert.Equal(parking.Id, preview.Segments[^1].ToNodeId);

        // With the @8B token the aircraft claims the stand.
        var withToken = MakeGroundAircraft(start.Position.Lat, start.Position.Lon);
        var resultWith = GroundCommandHandler.TryTaxi(withToken, new TaxiCommand(densePath, [], DestinationParking: "8B"), layout);
        Assert.True(resultWith.Success, resultWith.Message);
        Assert.NotNull(withToken.Ground.AssignedTaxiRoute);
        Assert.Equal("8B", withToken.Ground.AssignedTaxiRoute!.DestinationParking);
        Assert.Equal(parking.Id, withToken.Ground.AssignedTaxiRoute.Segments[^1].ToNodeId);

        // Without the token the same path taxis to the node but does NOT claim the stand.
        var noToken = MakeGroundAircraft(start.Position.Lat, start.Position.Lon);
        var resultNo = GroundCommandHandler.TryTaxi(noToken, new TaxiCommand(densePath, []), layout);
        Assert.True(resultNo.Success, resultNo.Message);
        Assert.Null(noToken.Ground.AssignedTaxiRoute!.DestinationParking);
    }

    // The readable form the draw-route "Copy to command input" action pastes: clean taxiway names
    // (junction composite labels like "W - W6" decomposed) plus a terminal node-ref pin. These guard
    // that the readable command stays faithful to the drawn geometry — no parallel-taxiway
    // substitution, no overshoot past the drawn endpoint.

    [Fact]
    public void ReadableTaxiPath_CleanNamesReproduceDrawnRoute_NonCrossing()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        const AircraftCategory cat = AircraftCategory.Jet;
        int multi = 0;
        int broken = 0;
        var failures = new System.Text.StringBuilder();

        var nodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).OrderBy(n => n.Id).Take(70).ToList();
        foreach (var a in nodes)
        {
            foreach (var b in nodes)
            {
                if (a.Id >= b.Id)
                {
                    continue;
                }

                var preview = TaxiPathfinder.FindRoute(layout, a.Id, b.Id, cat);
                if (preview is null || preview.Segments.Count == 0)
                {
                    continue;
                }

                bool crosses = preview.Segments.Any(s =>
                    s.Edge.Edge.IsRunwayCenterline || s.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase)
                );
                if (crosses)
                {
                    continue;
                }

                var names = TaxiRouteFormatter.CleanTaxiwaySequence(preview);
                if (names.Count < 2)
                {
                    continue; // require at least one taxiway-to-taxiway transition
                }

                multi++;

                // Composite junction labels ("W - W6") and runway names must never leak into the readable form.
                Assert.DoesNotContain(names, n => n.Contains(" - ") || n.Contains("RWY", StringComparison.OrdinalIgnoreCase) || n.Contains('#'));

                var path = TaxiRouteFormatter
                    .BuildReadableTaxiPath(preview, hasNamedTerminus: false)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                var resolved = TaxiPathfinder.ResolveExplicitPath(layout, a.Id, path, out var fail, new ExplicitPathOptions(), cat);
                if (resolved is null || !NodeIdSet(preview).SetEquals(NodeIdSet(resolved)))
                {
                    broken++;
                    if (broken <= 5)
                    {
                        failures.AppendLine($"{a.Id}->{b.Id}: TAXI {string.Join(" ", path)} -> {(resolved is null ? fail : "wrong nodes")}");
                    }
                }
            }
        }

        Assert.True(multi >= 50, $"expected many multi-taxiway OAK routes, got {multi}");
        Assert.True(broken == 0, $"{broken}/{multi} readable paths did not reproduce the drawn route:\n{failures}");
    }

    [Fact]
    public void ReadableTaxiPath_ToParking_ReachesStandWithoutOvershoot()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        var parking = layout.FindParkingByName("8B");
        if (parking is null)
        {
            return;
        }

        var holdShorts = layout.GetRunwayHoldShortNodes("28R");
        if (holdShorts.Count == 0)
        {
            return;
        }

        var start = holdShorts[0];
        const AircraftCategory cat = AircraftCategory.Jet;
        var preview = TaxiPathfinder.FindRoute(layout, start.Id, parking.Id, cat);
        if (preview is null || preview.Segments.Count == 0)
        {
            return;
        }

        // With a named terminus the @parking token pins the stop, so no node-ref is appended.
        var path = TaxiRouteFormatter
            .BuildReadableTaxiPath(preview, hasNamedTerminus: true)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        Assert.DoesNotContain(path, t => t.Contains(" - ") || t.StartsWith('#'));

        var resolved = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            path,
            out var fail,
            new ExplicitPathOptions { DestinationHintNode = parking },
            cat
        );
        Assert.True(resolved is not null, $"readable parking path failed: {fail}");
        Assert.Equal(parking.Id, resolved!.Segments[^1].ToNodeId);
    }

    [Fact]
    public void ReadableCommand_CrossingRoute_ReachesDrawnEndpoint()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        const AircraftCategory cat = AircraftCategory.Jet;
        var nodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).OrderBy(n => n.Id).Take(90).ToList();
        foreach (var a in nodes)
        {
            foreach (var b in nodes)
            {
                if (a.Id >= b.Id)
                {
                    continue;
                }

                var preview = TaxiPathfinder.FindRoute(layout, a.Id, b.Id, cat);
                if (preview is null || preview.Segments.Count == 0)
                {
                    continue;
                }

                var crossings = preview
                    .HoldShortPoints.Where(hs => (hs.Reason == HoldShortReason.RunwayCrossing) && (hs.TargetName is not null))
                    .Select(hs => RunwayIdentifier.Parse(hs.TargetName!).End1)
                    .ToList();
                if (crossings.Count == 0 || TaxiRouteFormatter.CleanTaxiwaySequence(preview).Count < 2)
                {
                    continue;
                }

                // The command the draw tool copies for a crossing route: readable path + CROSS authorization.
                var path = TaxiRouteFormatter
                    .BuildReadableTaxiPath(preview, hasNamedTerminus: false)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                var ac = MakeGroundAircraft(a.Position.Lat, a.Position.Lon);
                var result = GroundCommandHandler.TryTaxi(ac, new TaxiCommand(path, [], CrossRunways: crossings), layout);

                Assert.True(result.Success, $"{a.Id}->{b.Id} TAXI {string.Join(" ", path)} CROSS {string.Join(",", crossings)}: {result.Message}");
                Assert.NotNull(ac.Ground.AssignedTaxiRoute);
                Assert.Equal(preview.Segments[^1].ToNodeId, ac.Ground.AssignedTaxiRoute!.Segments[^1].ToNodeId);
                return; // one real crossing route exercised
            }
        }
    }

    private static AircraftState MakeGroundAircraft(double lat, double lon)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
        };
        ac.Phases = new PhaseList();
        return ac;
    }
}
