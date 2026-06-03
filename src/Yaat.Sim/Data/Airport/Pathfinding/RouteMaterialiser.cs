namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// Phase 3: converts a flat list of directed edges plus search context into a <see cref="TaxiRoute"/>.
/// Annotates hold-short points, truncates trailing pavement, sets destination strings, and emits warnings.
/// </summary>
public static class RouteMaterialiser
{
    /// <summary>
    /// Produce a <see cref="TaxiRoute"/> from a committed edge sequence and search context.
    /// <paramref name="insertions"/> are mandatory connectors the resolver had to bridge between
    /// cleared taxiways with no direct junction — surfaced as informative notifications rather
    /// than "unauthorized taxiway" warnings. Auto-route callers (no named clearance) pass empty.
    /// </summary>
    public static TaxiRoute Materialise(IReadOnlyList<DirectionalEdge> edges, SearchContext ctx, IReadOnlyList<ConnectorInsertion> insertions)
    {
        if (edges.Count == 0)
        {
            return new TaxiRoute
            {
                Segments = [],
                HoldShortPoints = [],
                CurrentSegmentIndex = 0,
                DestinationParking = ctx.Destination.ParkingName,
                DestinationSpot = ctx.Destination.SpotName,
            };
        }

        // Step 1: Build segment list — one per directed edge.
        var segments = BuildSegments(edges);

        // Step 2: Annotate hold-short points.
        var holdShorts = AnnotateHoldShorts(segments, ctx);

        // Step 3: Truncate to one segment past the last required stop.
        int truncateAt = FindTruncationIndex(segments, holdShorts, ctx);
        if (truncateAt >= 0 && truncateAt < segments.Count - 1)
        {
            segments = segments.Take(truncateAt + 1).ToList();
            holdShorts = holdShorts.Where(hs => segments.Any(s => s.ToNodeId == hs.NodeId)).ToList();
        }

        // Step 4: Warnings for unauthorized letter taxiways traversed, plus informative
        // notifications for mandatory connector insertions.
        var warnings = BuildWarnings(segments, ctx, insertions);

        return new TaxiRoute
        {
            Segments = segments,
            HoldShortPoints = holdShorts,
            Warnings = warnings,
            CurrentSegmentIndex = 0,
            DestinationParking = ctx.Destination.ParkingName,
            DestinationSpot = ctx.Destination.SpotName,
        };
    }

    private static List<TaxiRouteSegment> BuildSegments(IReadOnlyList<DirectionalEdge> edges)
    {
        var segments = new List<TaxiRouteSegment>(edges.Count);
        foreach (var edge in edges)
        {
            string taxiwayName = edge.TaxiwayName;
            segments.Add(new TaxiRouteSegment { Edge = edge, TaxiwayName = taxiwayName });
        }

        return segments;
    }

    private static List<HoldShortPoint> AnnotateHoldShorts(List<TaxiRouteSegment> segments, SearchContext ctx)
    {
        var holdShorts = new List<HoldShortPoint>();
        var seen = new HashSet<int>();
        var taxiwayHoldShortTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Entry/exit pairing by encounter order (ported from HoldShortAnnotator): the first
        // RunwayHoldShort node for a runway is the entry side (annotate); the second distinct
        // node for that runway is the exit side of the same crossing (skip). The destination
        // runway is exempt — it is the route terminus, always annotated. When the route begins
        // mid-crossing (e.g. re-routed from a runway hold-short), pre-seed the start node as an
        // entry so its exit-side pair is skipped.
        var enteredRunways = new Dictionary<RunwayIdentifier, int>();
        PreSeedStartCrossing(segments, ctx, enteredRunways);

        foreach (var seg in segments)
        {
            int nodeId = seg.ToNodeId;
            if (!seen.Add(nodeId))
            {
                continue;
            }

            if (!ctx.Layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } runwayId)
            {
                // Precedence: DestinationRunway (taxiing TO this runway — hold for departure, never
                // auto-cross) > ExplicitHoldShort (controller named it in the HS list) > RunwayCrossing.
                // Reciprocal matching: a node's RunwayId is the combined "28R/10L"; clearances name a
                // single end ("28R"), so match via RunwayIdentifier.Contains, not literal string equality.
                if (ctx.Destination.Kind == DestinationKind.Runway && ctx.Destination.RunwayId is { } destRwy && runwayId.Contains(destRwy))
                {
                    holdShorts.Add(
                        new HoldShortPoint
                        {
                            NodeId = nodeId,
                            Reason = HoldShortReason.DestinationRunway,
                            TargetName = destRwy,
                        }
                    );
                    continue;
                }

                // Non-destination runway: pair the crossing's entry and exit sides. The second
                // distinct hold-short node for a runway is the exit side — drop it so the aircraft
                // doesn't stop on the far side of a runway it is cleared through.
                if (enteredRunways.Remove(runwayId))
                {
                    continue;
                }

                enteredRunways[runwayId] = nodeId;

                holdShorts.Add(
                    new HoldShortPoint
                    {
                        NodeId = nodeId,
                        Reason = MatchesExplicitHoldShort(runwayId, ctx.ExplicitHoldShorts)
                            ? HoldShortReason.ExplicitHoldShort
                            : HoldShortReason.RunwayCrossing,
                        TargetName = runwayId.ToString(),
                    }
                );

                continue;
            }

            // Check explicit hold-shorts that target a taxiway name (non-runway). Annotate each
            // target at most once, at the first route node adjacent to it. A hold-short of a
            // taxiway the route runs ALONG (e.g. "TAXI G B HS B") is adjacent to every node on
            // that taxiway; without this per-target guard the summary echoes "HS B" once per node
            // ("G B HS B HS B HS B …"). Mirrors the per-runway enteredRunways guard above.
            foreach (string holdShortTarget in ctx.ExplicitHoldShorts)
            {
                if (taxiwayHoldShortTargets.Contains(holdShortTarget))
                {
                    continue;
                }

                foreach (var edge in node.Edges)
                {
                    if (edge.MatchesTaxiway(holdShortTarget))
                    {
                        holdShorts.Add(
                            new HoldShortPoint
                            {
                                NodeId = nodeId,
                                Reason = HoldShortReason.ExplicitHoldShort,
                                TargetName = holdShortTarget,
                            }
                        );
                        taxiwayHoldShortTargets.Add(holdShortTarget);
                        break;
                    }
                }
            }
        }

        return holdShorts;
    }

    /// <summary>
    /// When the route begins at a runway hold-short and the aircraft is mid-crossing (there is a
    /// paired hold-short for the same runway further along the starting taxiway), pre-seed the
    /// start node as an entry so the next encounter of that runway's hold-short is treated as the
    /// exit side and skipped. A route that begins at a single-sided exit hold-short (just vacated
    /// the runway, no paired hold-short ahead on the same taxiway) is NOT pre-seeded — its next
    /// runway hold-short is a genuine new crossing entry. Mirrors HoldShortAnnotator's start-node
    /// pre-seed.
    /// </summary>
    private static void PreSeedStartCrossing(List<TaxiRouteSegment> segments, SearchContext ctx, Dictionary<RunwayIdentifier, int> enteredRunways)
    {
        if (segments.Count == 0)
        {
            return;
        }

        int startNodeId = segments[0].FromNodeId;
        if (
            !ctx.Layout.Nodes.TryGetValue(startNodeId, out var startNode)
            || startNode.Type != GroundNodeType.RunwayHoldShort
            || startNode.RunwayId is not { } startRwyId
        )
        {
            return;
        }

        string startTaxiway = segments[0].TaxiwayName;
        foreach (var seg in segments)
        {
            if (seg.TaxiwayName != startTaxiway)
            {
                break;
            }

            if (seg.ToNodeId == startNodeId)
            {
                continue;
            }

            if (
                ctx.Layout.Nodes.TryGetValue(seg.ToNodeId, out var segToNode)
                && segToNode.Type == GroundNodeType.RunwayHoldShort
                && segToNode.RunwayId is { } segRwyId
                && segRwyId.Equals(startRwyId)
            )
            {
                enteredRunways[startRwyId] = startNodeId;
                return;
            }
        }
    }

    /// <summary>
    /// True when any controller-named explicit hold-short designator matches this runway,
    /// reciprocal-aware (a "28R" hold matches a node whose RunwayId is "28R/10L").
    /// </summary>
    private static bool MatchesExplicitHoldShort(RunwayIdentifier runwayId, IReadOnlySet<string> explicitHoldShorts)
    {
        foreach (string designator in explicitHoldShorts)
        {
            if (runwayId.Contains(designator))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindTruncationIndex(List<TaxiRouteSegment> segments, List<HoldShortPoint> holdShorts, SearchContext ctx)
    {
        // Find last required stop: destination node or last explicit hold-short.
        int lastRequiredIdx = -1;

        // Check destination node.
        if (ctx.Destination.TargetNodeId is { } destNodeId)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].ToNodeId == destNodeId)
                {
                    lastRequiredIdx = Math.Max(lastRequiredIdx, i);
                }
            }
        }

        // Runway destination: the route terminus is the lineup hold-short on the destination runway —
        // the FIRST hold-short of that runway the route reaches. A departure taxis up to its runway and
        // holds; it never crosses its own departure runway, so the near-side hold-short (first encountered)
        // is the lineup. Taking the last match instead would extend the route across the runway to the
        // far-side hold-short of the same crossing (both physical sides share the combined "28R/10L"
        // RunwayId). Match reciprocal-aware via Contains.
        int runwayDestHoldIdx = -1;
        if (ctx.Destination.RunwayId is { } runwayId && ctx.Destination.Kind == DestinationKind.Runway)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (!ctx.Layout.Nodes.TryGetValue(segments[i].ToNodeId, out var node))
                {
                    continue;
                }

                if (
                    node.Type == GroundNodeType.RunwayHoldShort
                    && node.RunwayId is { } nodeRunwayId
                    && (nodeRunwayId.ToString() ?? string.Empty).Contains(runwayId, StringComparison.OrdinalIgnoreCase)
                )
                {
                    runwayDestHoldIdx = i;
                    break;
                }
            }
        }

        // Explicit hold-shorts. A hold-short is the route terminus only when no cleared taxiway
        // lies beyond it. "TAXI G C HS 28R" holds short of 28R while crossing from G onto C: the
        // hold-short is a stop point mid-route, not the end, so the route must cross and stop just
        // onto the last cleared taxiway C. Truncating at the hold-short would strand the aircraft
        // on the near side, dropping the crossing and all of C. When the route reaches the last
        // cleared taxiway AFTER the hold-short (crossed en route), stop AT the first segment onto
        // that taxiway — directly, with no trailing buffer, so the aircraft settles just past the
        // junction. Otherwise (the hold-short is on/after the last cleared taxiway) it is the
        // terminus — stop one segment past it like any other required stop.
        int lastClearedEntryIdx = FindLastClearedTaxiwayEntry(segments, ctx);
        int crossHoldTruncateAt = -1;
        foreach (var hs in holdShorts)
        {
            if (hs.Reason != HoldShortReason.ExplicitHoldShort)
            {
                continue;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].ToNodeId != hs.NodeId)
                {
                    continue;
                }

                if (lastClearedEntryIdx > i)
                {
                    crossHoldTruncateAt = Math.Max(crossHoldTruncateAt, lastClearedEntryIdx);
                }
                else
                {
                    lastRequiredIdx = Math.Max(lastRequiredIdx, i);
                }
            }
        }

        // Parking/spot: stop at the destination node.
        if (
            ctx.Destination.Kind is DestinationKind.Parking or DestinationKind.Spot or DestinationKind.Helipad
            && ctx.Destination.TargetNodeId is { } parkingNodeId
        )
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].ToNodeId == parkingNodeId)
                {
                    lastRequiredIdx = Math.Max(lastRequiredIdx, i);
                }
            }
        }

        // A runway destination ends EXACTLY at its lineup hold-short — the true terminus. No "+1" buffer
        // (that would step onto the runway), and en-route truncations never apply: an explicit hold-short
        // crossed on the way (crossHold, which would otherwise stop the route on the last cleared taxiway)
        // is moot here because the destination runway lies beyond that taxiway. Departing onto or across
        // the runway is clearance-gated by LineUp / CrossingRunway phases, never baked into the taxi route.
        if (runwayDestHoldIdx >= 0)
        {
            return Math.Min(runwayDestHoldIdx, segments.Count - 1);
        }

        // One past the last required stop; a crossed-hold stop is exact (no trailing buffer).
        int normalTruncate = lastRequiredIdx < 0 ? -1 : lastRequiredIdx + 1;
        int result = Math.Max(normalTruncate, crossHoldTruncateAt);

        return result < 0 ? segments.Count - 1 : Math.Min(result, segments.Count - 1);
    }

    /// <summary>
    /// Index of the first segment that lies purely on the last cleared taxiway (the final named
    /// waypoint), or -1 when there is no named last waypoint or it is never reached on a single-name
    /// segment. "Purely" excludes the membership junction arc onto the taxiway (e.g. a "C - G" corner
    /// arc) so the cross-and-hold stop lands on the taxiway proper — leaving the aircraft reporting
    /// the cleared taxiway as its current one rather than a transitional corner. Used by the
    /// explicit-hold-short truncation to stop one segment onto the taxiway the route crossed to reach.
    /// </summary>
    private static int FindLastClearedTaxiwayEntry(List<TaxiRouteSegment> segments, SearchContext ctx)
    {
        if (ctx.WaypointSequence.Count == 0)
        {
            return -1;
        }

        string last = ctx.WaypointSequence[^1];
        if (last.StartsWith('#'))
        {
            return -1;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].TaxiwayName.Equals(last, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> BuildWarnings(List<TaxiRouteSegment> segments, SearchContext ctx, IReadOnlyList<ConnectorInsertion> insertions)
    {
        var warnings = new List<string>();

        // Turn-direction hints (issue #172 W7) the resolver couldn't honor — advise the controller that
        // the aircraft turned the other way. De-duplicated in case a hint was evaluated more than once.
        foreach (string advisory in ctx.TurnHintAdvisories.Distinct())
        {
            warnings.Add(advisory);
        }

        // Mandatory connectors the resolver had to bridge between cleared taxiways with no direct
        // junction. Notify the controller of each insertion, and suppress the generic
        // "unauthorized" warning for those connector taxiways — they were not a deviation.
        var mandatoryConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var insertion in insertions)
        {
            warnings.Add(
                $"{insertion.FromTaxiway} and {insertion.ToTaxiway} do not connect directly — taxi via {string.Join(", ", insertion.Connectors)}"
            );
            foreach (string connector in insertion.Connectors)
            {
                mandatoryConnectors.Add(connector);
            }
        }

        AddOneWayWarnings(segments, ctx, warnings);

        if (ctx.AuthorizedTaxiways is null)
        {
            return warnings;
        }

        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in segments)
        {
            // Junction arcs ("X - Y") are transitions between taxiways, not a traversal of one —
            // never an "unauthorized taxiway" deviation.
            if (seg.Edge.Edge is GroundArc { TaxiwayNames.Length: >= 2 })
            {
                continue;
            }

            string name = seg.TaxiwayName;

            // RAMP (apron / parking access) is never an unauthorized deviation — it is excluded by
            // IsLetterOnlyTaxiway, so the parking-bridge and arrival RAMP legs are not flagged here.
            if (
                SearchContext.IsLetterOnlyTaxiway(name)
                && !ctx.AuthorizedTaxiways.Contains(name)
                && !mandatoryConnectors.Contains(name)
                && warned.Add(name)
            )
            {
                warnings.Add($"Taxiing via {name} (not in authorized path)");
            }
        }

        return warnings;
    }

    /// <summary>
    /// In one-way <see cref="OneWayMode.Warn"/> mode (explicit clearances and the auto fallback), flag any
    /// materialised segment that traverses a one-way span against its allowed direction. Auto routes
    /// (<see cref="OneWayMode.HardExclude"/>) never reach here with a wrong-way segment — they are gated
    /// in <see cref="AutoRouter"/> — so no warning is needed for them.
    /// </summary>
    private static void AddOneWayWarnings(List<TaxiRouteSegment> segments, SearchContext ctx, List<string> warnings)
    {
        if (ctx.OneWayMode != OneWayMode.Warn || ctx.ForbiddenOneWayMoves.Count == 0)
        {
            return;
        }

        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in segments)
        {
            if (ctx.ForbiddenOneWayMoves.Contains((seg.FromNodeId, seg.ToNodeId)) && warned.Add(seg.TaxiwayName))
            {
                warnings.Add($"Taxiing {seg.TaxiwayName} against one-way direction");
            }
        }
    }

    /// <summary>
    /// Pick the full-length lineup hold-short on <paramref name="runwayId"/> — the one
    /// geographically nearest to the requested designator's threshold (the departure end you line
    /// up at for a full-length takeoff). The threshold is resolved authoritatively from
    /// <see cref="NavigationDatabase"/> so the correct end is chosen for the named designator
    /// (the 28R end, not the reciprocal 10L end). When the runway is unknown to nav-data, falls
    /// back to the hold-short nearest <paramref name="startNode"/>.
    /// </summary>
    public static GroundNode FindFullLengthLineupHoldShort(
        AirportGroundLayout layout,
        GroundNode startNode,
        string runwayId,
        List<GroundNode> holdShortNodes
    )
    {
        if (holdShortNodes.Count == 0)
        {
            return startNode;
        }

        if (holdShortNodes.Count == 1)
        {
            return holdShortNodes[0];
        }

        LatLon reference = ResolveRunwayThreshold(layout.AirportId, runwayId) ?? startNode.Position;

        GroundNode best = holdShortNodes[0];
        double bestDist = GeoMath.DistanceNm(reference, best.Position);
        for (int i = 1; i < holdShortNodes.Count; i++)
        {
            double dist = GeoMath.DistanceNm(reference, holdShortNodes[i].Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = holdShortNodes[i];
            }
        }

        return best;
    }

    /// <summary>
    /// The geographic threshold of <paramref name="runwayId"/>'s requested designator (e.g. the 28R
    /// end, not the reciprocal 10L end), from <see cref="NavigationDatabase"/>. Null when nav-data is
    /// uninitialized or the runway is unknown to it.
    /// </summary>
    internal static LatLon? ResolveRunwayThreshold(string airportId, string runwayId)
    {
        var runway = NavigationDatabase.InstanceOrNull?.GetRunway(airportId, runwayId);
        return runway is null ? null : new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
    }
}
