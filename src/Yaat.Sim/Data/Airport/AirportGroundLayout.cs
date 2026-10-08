using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

public enum GroundNodeType
{
    TaxiwayIntersection,
    Parking,
    Spot,
    RunwayHoldShort,
    Helipad,
}

public sealed class GroundNode
{
    public required int Id { get; init; }

    /// <summary>Geographic position of the node.</summary>
    public required LatLon Position { get; init; }

    public required GroundNodeType Type { get; set; }
    public string? Name { get; init; }

    /// <summary>
    /// Parking heading (nose-in direction, degrees true). Only set for Parking nodes.
    /// </summary>
    public TrueHeading? TrueHeading { get; init; }

    /// <summary>
    /// Whether the stand is pushed back or taxied out of, by its geometry alone (<see cref="StandDepartures.Classify"/>),
    /// so never <see cref="Airport.StandDeparture.Either"/>. Only set for Parking nodes, by the layout build; null on a
    /// parking node only in a layout serialised before this field existed, which reads as
    /// <see cref="Airport.StandDeparture.PushBack"/>. Read through <see cref="StandDepartures.StandDepartureOf"/>, which
    /// applies the airport sidecar's per-name overrides and area rules, and may answer any of the three.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StandDeparture? StandDeparture { get; set; }

    /// <summary>
    /// Runway ID that this hold-short node protects. Only set for RunwayHoldShort nodes.
    /// </summary>
    public RunwayIdentifier? RunwayId { get; set; }

    /// <summary>
    /// Adjacent edges for graph traversal. Populated during layout construction.
    /// Not serialized — rebuilt by <see cref="AirportGroundLayout.RebuildAdjacencyLists"/> after deserialization.
    /// </summary>
    [JsonIgnore]
    public List<IGroundEdge> Edges { get; init; } = [];

    /// <summary>
    /// Diagnostic provenance: which code path created or last modified this node.
    /// Not serialized — only populated during layout construction for debugging.
    /// </summary>
    [JsonIgnore]
    public string? Origin { get; set; }

    /// <summary>
    /// For tangent-point nodes created by <see cref="FilletArcGenerator"/>: the position of
    /// the intersection node this tangent was created for. Used during coincident-node merging
    /// to position the merged node at the midpoint between two source intersections.
    /// Null for non-tangent nodes.
    /// </summary>
    [JsonIgnore]
    public (double Lat, double Lon)? SourceIntersectionPosition { get; set; }
}

/// <summary>
/// Common interface for ground graph edges — straight lines and circular arcs.
/// Both <see cref="GroundEdge"/> and <see cref="GroundArc"/> implement this.
/// </summary>
public interface IGroundEdge
{
    /// <summary>The two endpoint nodes. Fixed-size 2, no implied direction.</summary>
    GroundNode[] Nodes { get; }
    string TaxiwayName { get; }
    double DistanceNm { get; }

    /// <summary>
    /// Returns true if this edge belongs to the given taxiway.
    /// For <see cref="GroundArc"/>s at junctions this also checks the secondary taxiway name.
    /// </summary>
    bool MatchesTaxiway(string name);

    /// <summary>
    /// True if this edge is a runway centerline segment — an edge that exists purely
    /// on the runway surface. For straight edges: TaxiwayName starts with "RWY".
    /// For arcs: true only if ALL taxiway names are RWY (same-taxiway runway arc).
    /// Junction arcs between a runway and a taxiway return false — they are transitions,
    /// not centerline segments.
    /// </summary>
    bool IsRunwayCenterline { get; }

    /// <summary>
    /// Returns true if this edge is a runway edge for the given designator.
    /// Runway edge names are "RWY{end1}/{end2}" (e.g., "RWY10L/28R");
    /// this checks if <paramref name="designator"/> matches either end.
    /// </summary>
    bool MatchesRunway(string designator);

    /// <summary>True if this edge is a ramp connection (TaxiwayName is "RAMP").</summary>
    bool IsRamp { get; }

    /// <summary>
    /// Maximum safe speed (kts) for traversing this edge for the given aircraft category.
    /// Straight edges return <see cref="double.MaxValue"/>; arcs compute from a lateral-acceleration
    /// (tire-scrub / comfort) limit on the curvature radius, capped by the angle-based corner speed.
    /// </summary>
    double MaxSafeSpeedKts(AircraftCategory category);

    /// <summary>
    /// Returns true if this edge shares any taxiway name with <paramref name="other"/>.
    /// W overlaps W/W3 → true. Use for "could these be part of the same route?" checks.
    /// </summary>
    bool SharesTaxiway(IGroundEdge other);

    /// <summary>
    /// Returns true if this edge has the exact same taxiway identity as <paramref name="other"/>.
    /// W == W → true, W != W/W3 → false. Use for "is this the same taxiway continuing?" checks.
    /// </summary>
    bool SameTaxiway(IGroundEdge other);

    GroundNode OtherNode(GroundNode node);
    int OtherNodeId(int nodeId);
    bool HasNode(int nodeId);

    /// <summary>
    /// Diagnostic provenance: which code path created or last modified this edge/arc.
    /// Not serialized — only populated during layout construction for debugging.
    /// </summary>
    string? Origin { get; set; }

    /// <summary>
    /// Create a <see cref="DirectionalEdge"/> capturing a specific traversal direction.
    /// </summary>
    DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode);

    /// <summary>
    /// Checks if a runway edge name (e.g., "RWY10L/28R") contains the given designator
    /// as an exact segment match. Strips the "RWY" prefix, splits by "/", and checks each part.
    /// </summary>
    static bool RunwayNameContainsDesignator(string rwyEdgeName, string designator)
    {
        // "RWY10L/28R" → "10L/28R" → ["10L", "28R"]. Normalize each end and the query so a
        // single-digit designator matches its zero-padded form ("8R" == "08R"). RunwayIdentifier
        // normalizes the same way; the two matchers must agree or auto-routing mis-resolves a
        // bare "RWY 8R" against an "08R/26L" edge.
        ReadOnlySpan<char> name = rwyEdgeName.AsSpan();
        if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
        {
            name = name[3..];
        }

        string normalizedDesignator = RunwayIdentifier.NormalizeDesignator(designator);
        foreach (Range part in name.Split('/'))
        {
            if (RunwayIdentifier.NormalizeDesignator(name[part].ToString()).Equals(normalizedDesignator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A non-directional straight edge in the airport ground graph connecting two nodes.
/// <c>Nodes[0]</c> and <c>Nodes[1]</c> are the two endpoints — no implied direction.
/// For directional traversal (routes, navigation), wrap via <see cref="Directed"/>.
/// </summary>
public sealed class GroundEdge : IGroundEdge
{
    /// <summary>The two endpoint nodes. Fixed-size 2, no implied direction.</summary>
    public required GroundNode[] Nodes { get; init; }
    public required string TaxiwayName { get; init; }
    public required double DistanceNm { get; set; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string? Origin { get; set; }

    /// <summary>
    /// Intermediate coordinates along this edge (lat, lon pairs) for curved paths.
    /// Does NOT include endpoint node positions — those are looked up from <see cref="Nodes"/>.
    /// </summary>
    public List<(double Lat, double Lon)> IntermediatePoints { get; init; } = [];

    public bool MatchesTaxiway(string name) => string.Equals(TaxiwayName, name, StringComparison.OrdinalIgnoreCase);

    public double MaxSafeSpeedKts(AircraftCategory category) => double.MaxValue;

    public bool SharesTaxiway(IGroundEdge other) => other.MatchesTaxiway(TaxiwayName);

    public bool SameTaxiway(IGroundEdge other) =>
        other switch
        {
            GroundEdge e => string.Equals(TaxiwayName, e.TaxiwayName, StringComparison.OrdinalIgnoreCase),
            GroundArc { TaxiwayNames.Length: 1 } a => string.Equals(TaxiwayName, a.TaxiwayNames[0], StringComparison.OrdinalIgnoreCase),
            _ => false, // junction arc has multiple names — never "same" as a single-name edge
        };

    public bool IsRunwayCenterline => TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !TaxiwayName.Contains(":link");

    /// <summary>
    /// True if this edge is a runway-crossing connector (<c>RWY…:link</c>) joining a taxiway
    /// hold-short representative to the runway centerline. These are connectivity artifacts, not
    /// taxi corners — fillet must ignore them (never curve a taxiway onto a runway crossing link).
    /// </summary>
    public bool IsRunwayCrossingLink => TaxiwayName.Contains(":link", StringComparison.OrdinalIgnoreCase);

    public bool MatchesRunway(string designator) => IsRunwayCenterline && IGroundEdge.RunwayNameContainsDesignator(TaxiwayName, designator);

    public bool IsRamp => string.Equals(TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase);

    public GroundNode OtherNode(GroundNode node) => Nodes[0].Id == node.Id ? Nodes[1] : Nodes[0];

    public int OtherNodeId(int nodeId) => Nodes[0].Id == nodeId ? Nodes[1].Id : Nodes[0].Id;

    public bool HasNode(int nodeId) => Nodes[0].Id == nodeId || Nodes[1].Id == nodeId;

    public DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode) =>
        new()
        {
            Edge = this,
            FromNode = fromNode,
            ToNode = toNode,
        };
}

/// <summary>
/// A circular arc edge connecting two tangent-point nodes at a filleted intersection.
/// Bidirectional — can be traversed in either direction. The navigator follows
/// the curve using lookahead-based path tracking rather than point-to-point steering.
/// <para>
/// The arc is always the minor arc (shorter path around the circle, ≤180°).
/// Sweep direction is determined at traversal time by which node is "from" vs "to".
/// Start/end angles are derived from <c>BearingTo(Center, Node)</c> — not stored.
/// </para>
/// </summary>
public sealed class GroundArc : IGroundEdge
{
    public required GroundNode[] Nodes { get; init; }

    /// <inheritdoc/>
    [JsonIgnore]
    public string? Origin { get; set; }

    /// <summary>
    /// Bezier control points P1 and P2. P0 = Nodes[0].Lat/Lon, P3 = Nodes[1].Lat/Lon.
    /// P1 lies along edge-A direction from P0; P2 lies along edge-B direction from P3.
    /// </summary>
    public required double P1Lat { get; set; }
    public required double P1Lon { get; set; }
    public required double P2Lat { get; set; }
    public required double P2Lon { get; set; }

    /// <summary>
    /// Tightest radius of curvature along the bezier, precomputed at construction time.
    /// Used for worst-case speed constraint back-propagation.
    /// </summary>
    public required double MinRadiusOfCurvatureFt { get; set; }

    // --- Fillet construction parameters ---
    // Stored so that later passes (e.g., MergeCoincidentNodes) can recompute P1/P2
    // from the new node positions instead of translating stale control points.
    // Serialized, so a layout read back from an archive gives the corner speeds and
    // route costs of the parsed one bit for bit.

    /// <summary>
    /// Bearing (degrees true) from Nodes[0] toward the fillet intersection center.
    /// This is the direction P1 was projected along during construction. May differ
    /// from the simple reverse of the outbound edge bearing when the tangent point
    /// was placed past shape-point nodes during the taxiway walk.
    /// </summary>
    public double EdgeBearingAtNode0Deg { get; set; }

    /// <summary>
    /// Bearing (degrees true) from Nodes[1] toward the fillet intersection center.
    /// This is the direction P2 was projected along during construction.
    /// </summary>
    public double EdgeBearingAtNode1Deg { get; set; }

    /// <summary>
    /// Turn angle (degrees) between the two edges that this arc bridges.
    /// Used with kappa = (4/3) * tan(sweep/4) to compute control point depth.
    /// </summary>
    public double TurnAngleDeg { get; set; }

    public required double DistanceNm { get; set; }

    /// <summary>
    /// The taxiway(s) this arc belongs to. Length 1 when both edges share the same name,
    /// length 2 at a junction between different taxiways (e.g., ["W", "W3"]).
    /// No implied precedence — the arc belongs equally to both.
    /// </summary>
    public required string[] TaxiwayNames { get; init; }

    /// <summary>
    /// Display name for the arc: single name for same-taxiway arcs, "W - W3" for junctions.
    /// Uses " - " separator to avoid collision with "/" in runway identifiers (e.g., "RWY30/12").
    /// For membership checks, use <see cref="MatchesTaxiway"/> instead.
    /// </summary>
    public string TaxiwayName => TaxiwayNames.Length == 1 ? TaxiwayNames[0] : string.Join(" - ", TaxiwayNames);

    public bool MatchesTaxiway(string name)
    {
        foreach (string twName in TaxiwayNames)
        {
            if (string.Equals(twName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Construct a <see cref="CubicBezier"/> from this arc's control points and node positions.
    /// </summary>
    public CubicBezier ToBezier() =>
        new(Nodes[0].Position.Lat, Nodes[0].Position.Lon, P1Lat, P1Lon, P2Lat, P2Lon, Nodes[1].Position.Lat, Nodes[1].Position.Lon);

    /// <summary>Lateral acceleration ceiling for taxi turns (m/s²) ≈ 0.13 g — a tire-scrub / passenger-comfort
    /// limit, matching the ~0.12–0.13 g implied by ICAO Annex 14's rapid-exit taxiway radius/speed pairs
    /// (and FAA AC 150/5300-13B).</summary>
    private const double TaxiLateralAccelMps2 = 0.13 * 9.80665;
    private const double MetersPerFoot = 0.3048;
    private const double KnotsToMetersPerSecond = 0.514444;

    /// <summary>
    /// Maximum safe taxi speed (kts) along this arc, the min of three curvature limits, floored at
    /// <see cref="CategoryPerformance.SlowTurnSpeedKts"/> so a degenerate-radius arc never commands a stop:
    /// <list type="bullet">
    /// <item>a lateral-acceleration limit — the speed at which centripetal acceleration v²/r reaches
    /// <see cref="TaxiLateralAccelMps2"/> (degrades as √r: ~4.7 kt @15 ft, ~7.6 kt @40 ft, ~9.1 kt @56 ft);</item>
    /// <item>the angle-based <see cref="CategoryPerformance.CornerSpeedForAngle"/> ceiling;</item>
    /// <item>a yaw-rate limit <c>v = ω·r</c> at the gear-limited <see cref="CategoryPerformance.GroundTurnRate"/>
    /// (<see cref="CategoryPerformance.TurnRateLimitedSpeedKts"/>) — without it a tight fillet whose lateral-accel
    /// speed exceeds ω·r is traversed faster than the nose wheel can track, and the aircraft yaws past the ceiling
    /// (the KOAK taxi-out fillet swung 45 °/s at 20 kt). This is the arc analogue of the straight-segment
    /// <see cref="CategoryPerformance.GroundYawRateAtSpeed"/> coupling.</item>
    /// </list>
    /// </summary>
    public double MaxSafeSpeedKts(AircraftCategory category) => SafeSpeedForRadiusKts(category, MinRadiusOfCurvatureFt);

    /// <summary>
    /// The <see cref="MaxSafeSpeedKts"/> limits evaluated at one radius of curvature: lateral acceleration
    /// and the yaw-rate coupling at that radius, and the angle-comfort ceiling for the arc's <em>whole</em> turn
    /// angle — a per-corner workload ceiling, not a local term, so it applies uniformly along the curve and
    /// bounds how much a gentle stretch may run ahead of a tight one — floored at
    /// <see cref="CategoryPerformance.SlowTurnSpeedKts"/>.
    /// </summary>
    public double SafeSpeedForRadiusKts(AircraftCategory category, double radiusFt)
    {
        double radiusM = radiusFt * MetersPerFoot;
        double lateralAccelKts = Math.Sqrt(TaxiLateralAccelMps2 * radiusM) / KnotsToMetersPerSecond;
        double cornerCeilingKts = CategoryPerformance.CornerSpeedForAngle(category, TurnAngleDeg);
        double yawRateKts = CategoryPerformance.TurnRateLimitedSpeedKts(category, radiusFt);
        double curvatureCapKts = Math.Min(Math.Min(lateralAccelKts, cornerCeilingKts), yawRateKts);
        return Math.Max(curvatureCapKts, CategoryPerformance.SlowTurnSpeedKts);
    }

    /// <summary>One point of <see cref="SpeedProfile"/>: arc length from <c>Nodes[0]</c> and the local cornering speed there.</summary>
    public readonly record struct SpeedSample(double LengthFt, double SpeedKts);

    private const int SpeedProfileSamples = 16;
    private readonly Dictionary<AircraftCategory, SpeedSample[]> _speedProfiles = [];

    /// <summary>
    /// Local cornering-speed profile along the stored curve (from <c>Nodes[0]</c>): evenly spaced parameter
    /// samples with their arc length and <see cref="SafeSpeedForRadiusKts"/> at the local radius of curvature.
    /// A distorted cubic — one long gentle sweep with a single tight stretch, which the fillet generator emits
    /// at asymmetric junctions — is slow only where it is tight; <see cref="MaxSafeSpeedKts"/>, the tightest
    /// point alone, would hold the whole length at walking pace (SFO junction J133's B bend: 107 ft and 56°
    /// with a 22 ft minimum radius, 21 s at 3 kt). Samples are evenly spaced in the curve parameter, not in
    /// arc length, so a curvature minimum falling between two samples is not seen; the whole-arc cap is the
    /// conservative bound the profile is always at or above. Cached per category; the geometry is fixed once
    /// the layout is built.
    /// </summary>
    public IReadOnlyList<SpeedSample> SpeedProfile(AircraftCategory category)
    {
        lock (_speedProfiles)
        {
            if (!_speedProfiles.TryGetValue(category, out SpeedSample[]? profile))
            {
                profile = BuildSpeedProfile(category);
                _speedProfiles[category] = profile;
            }

            return profile;
        }
    }

    private SpeedSample[] BuildSpeedProfile(AircraftCategory category)
    {
        CubicBezier curve = ToBezier();
        double refLat = Nodes[0].Position.Lat;
        var samples = new SpeedSample[SpeedProfileSamples + 1];
        double lengthFt = 0.0;
        (double Lat, double Lon) previous = curve.Evaluate(0.0);
        for (int i = 0; i <= SpeedProfileSamples; i++)
        {
            double t = (double)i / SpeedProfileSamples;
            if (i > 0)
            {
                (double Lat, double Lon) point = curve.Evaluate(t);
                lengthFt += GeoMath.DistanceNm(new LatLon(previous.Lat, previous.Lon), new LatLon(point.Lat, point.Lon)) * GeoMath.FeetPerNm;
                previous = point;
            }

            samples[i] = new SpeedSample(lengthFt, SafeSpeedForRadiusKts(category, curve.RadiusOfCurvatureFt(t, refLat)));
        }

        return samples;
    }

    /// <summary>
    /// Seconds to play the whole arc at its local cornering speed, stepping between profile samples with no
    /// acceleration or braking between them — a lower bound on what the navigator actually flies, used to rank
    /// route candidates, not to predict a taxi time.
    /// </summary>
    public double TraversalSeconds(AircraftCategory category)
    {
        IReadOnlyList<SpeedSample> profile = SpeedProfile(category);
        double seconds = 0.0;
        for (int i = 1; i < profile.Count; i++)
        {
            double stepFt = profile[i].LengthFt - profile[i - 1].LengthFt;
            double speedKts = Math.Min(profile[i].SpeedKts, profile[i - 1].SpeedKts);
            seconds += stepFt / (speedKts * GeoMath.FeetPerNm / 3600.0);
        }

        return seconds;
    }

    public bool SharesTaxiway(IGroundEdge other)
    {
        foreach (string twName in TaxiwayNames)
        {
            if (other.MatchesTaxiway(twName))
            {
                return true;
            }
        }

        return false;
    }

    public bool SameTaxiway(IGroundEdge other)
    {
        if (other is GroundArc otherArc)
        {
            // Same if name sets are identical
            if (TaxiwayNames.Length != otherArc.TaxiwayNames.Length)
            {
                return false;
            }

            foreach (string name in TaxiwayNames)
            {
                if (!otherArc.MatchesTaxiway(name))
                {
                    return false;
                }
            }

            return true;
        }

        // Arc vs straight edge: same only if the arc has a single name that matches
        return TaxiwayNames.Length == 1 && other.MatchesTaxiway(TaxiwayNames[0]);
    }

    /// <summary>
    /// Always false — a runway centerline is straight by definition. Arcs are never
    /// centerline segments; they are either taxiway junctions or same-taxiway curves.
    /// </summary>
    public bool IsRunwayCenterline => false;

    /// <summary>
    /// True if this arc connects a runway edge to a taxiway edge — the transition
    /// between the runway surface and a taxiway. Exactly one name is RWY, at least one is not.
    /// </summary>
    public bool IsRunwayJunction
    {
        get
        {
            if (TaxiwayNames.Length < 2)
            {
                return false;
            }

            bool hasRwy = false;
            bool hasNonRwy = false;
            foreach (string name in TaxiwayNames)
            {
                if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    hasRwy = true;
                }
                else
                {
                    hasNonRwy = true;
                }
            }

            return hasRwy && hasNonRwy;
        }
    }

    /// <summary>
    /// True if this is a membership junction arc between two TAXIWAYS (e.g. "A - Q1", "A - RAMP")
    /// — a turn OFF the current taxiway onto a crossing one, not a continuation of it. Excludes
    /// runway-crossing arcs (<see cref="IsRunwayJunction"/>, e.g. "H - RWY01L/19R"), which DO
    /// continue the taxiway across a runway. Used by requirement ① to rank a single-name
    /// continuation above such an arc when walking a named taxiway.
    /// </summary>
    public bool IsMembershipTaxiwayJunctionArc => TaxiwayNames.Length >= 2 && !IsRunwayJunction;

    public bool MatchesRunway(string designator)
    {
        foreach (string name in TaxiwayNames)
        {
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && IGroundEdge.RunwayNameContainsDesignator(name, designator))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsRamp => string.Equals(TaxiwayNames[0], "RAMP", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the first non-runway taxiway name from this arc.
    /// E.g., for TaxiwayNames = ["G", "RWY28R/10L"], returns "G".
    /// Falls back to TaxiwayName if all names are runway names.
    /// </summary>
    public string FirstNonRunwayName()
    {
        foreach (string name in TaxiwayNames)
        {
            if (!name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return TaxiwayName;
    }

    public GroundNode OtherNode(GroundNode node) => Nodes[0].Id == node.Id ? Nodes[1] : Nodes[0];

    public int OtherNodeId(int nodeId) => Nodes[0].Id == nodeId ? Nodes[1].Id : Nodes[0].Id;

    public bool HasNode(int nodeId) => Nodes[0].Id == nodeId || Nodes[1].Id == nodeId;

    public DirectionalEdge Directed(GroundNode fromNode, GroundNode toNode) =>
        new()
        {
            Edge = this,
            FromNode = fromNode,
            ToNode = toNode,
        };

    /// <summary>
    /// Returns the tangent bearing at <paramref name="atNode"/> when traversing the arc
    /// from <paramref name="fromNode"/> toward the arc's other end.
    /// Computed from the bezier tangent direction at the relevant endpoint.
    /// <paramref name="atNode"/> must be one of the arc's two nodes.
    /// </summary>
    public double TangentBearingAt(GroundNode atNode, GroundNode fromNode)
    {
        CubicBezier bezier = ToBezier();
        bool forward = fromNode.Id == Nodes[0].Id;

        if (forward)
        {
            // Forward traversal: P0→P3. t=0 at fromNode, t=1 at toNode.
            return atNode.Id == fromNode.Id ? bezier.TangentBearing(0.0) : bezier.TangentBearing(1.0);
        }

        // Reversed traversal: P3→P0. Tangent directions flip 180°.
        double t = atNode.Id == fromNode.Id ? 1.0 : 0.0;
        return (bezier.TangentBearing(t) + 180.0) % 360.0;
    }
}

/// <summary>
/// A directional view of an <see cref="IGroundEdge"/> — captures a specific traversal direction.
/// Created when building routes/paths. Multiple instances can reference the same edge
/// (different directions, or same direction for U-turns).
/// </summary>
public sealed class DirectionalEdge
{
    public required IGroundEdge Edge { get; init; }
    public required GroundNode FromNode { get; init; }
    public required GroundNode ToNode { get; init; }

    public string TaxiwayName => Edge.TaxiwayName;
    public double DistanceNm => Edge.DistanceNm;
    public int FromNodeId => FromNode.Id;
    public int ToNodeId => ToNode.Id;

    /// <summary>
    /// Bearing at the start of traversal (departing FromNode).
    /// For arcs: tangent at FromNode in the sweep direction.
    /// For straight edges: bearing from FromNode to ToNode.
    /// </summary>
    public double DepartureBearing =>
        Edge is GroundArc arc ? arc.TangentBearingAt(FromNode, FromNode) : GeoMath.BearingTo(FromNode.Position, ToNode.Position);

    /// <summary>
    /// Bearing at the end of traversal (arriving at ToNode).
    /// For arcs: tangent at ToNode continuing in the same sweep direction as the traversal.
    /// For straight edges: bearing from FromNode to ToNode (same as departure).
    /// </summary>
    public double ArrivalBearing =>
        Edge is GroundArc arc ? arc.TangentBearingAt(ToNode, FromNode) : GeoMath.BearingTo(FromNode.Position, ToNode.Position);
}

public sealed class GroundRunway
{
    public required string Name { get; init; }
    public required List<(double Lat, double Lon)> Coordinates { get; init; }
    public required double WidthFt { get; init; }

    /// <summary>
    /// Author-specified runway holding-position standoff in feet from centerline (vNAS map
    /// <c>holdShortDistance</c>). Null when unset, in which case the width-based FAA Table 3-2
    /// heuristic is used. See <see cref="RunwayCrossingDetector.HoldShortDistanceForWidth"/>.
    /// </summary>
    public double? HoldShortDistanceFt { get; init; }

    /// <summary>
    /// Canonical per-end identity parsed from <see cref="Name"/> (zero-pad-normalized ends, e.g.
    /// "9 - 27" → End1 "09", End2 "27"). Use this — and the <see cref="MatchesEnd"/> /
    /// <see cref="TurnoffForEnd"/> / <see cref="NoTurnoffForEnd"/> helpers — for any per-end lookup,
    /// never a raw string compare against <see cref="EndDesignators"/>, so single-digit runways
    /// match whether the caller passes the FAA "9" or the normalized "09" form.
    /// </summary>
    public RunwayIdentifier Id => RunwayIdentifier.Parse(Name);

    /// <summary>
    /// The two end designators parsed from <see cref="Name"/> (e.g. "28R - 10L" → ["28R", "10L"]).
    /// Accepts the canonical dash-with-spaces form authored in GeoJSON as well as a bare "X-Y"
    /// fallback for any publisher that omits the spaces. Use this everywhere a runway name needs
    /// to be split into ends so the separator stays consistent across the codebase.
    /// </summary>
    public IReadOnlyList<string> EndDesignators => Name.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Author-specified preferred turn-off side per landing-end designator (left/right of nose at rollout).
    /// In ATCTrainer airport files, "turnoff" is one value per physical runway, expressed relative to the
    /// first-named end's heading. Parsing flips it for the second end so the same physical side resolves
    /// regardless of which direction the aircraft lands. Empty when no turnoff is authored. Keyed by the
    /// zero-pad-normalized designator; read it via <see cref="TurnoffForEnd"/>, never the raw map.
    /// </summary>
    [JsonInclude]
    public IReadOnlyDictionary<string, ExitSide> TurnoffByEnd
    {
        private get;
        init => field = new Dictionary<string, ExitSide>(value, StringComparer.OrdinalIgnoreCase);
    } = new Dictionary<string, ExitSide>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Author-specified pattern altitude in feet AGL above field elevation. Null when unset.</summary>
    public double? PatternAltitudeAglFt { get; init; }

    /// <summary>Author-specified downwind offset from runway centerline in nm. Null when unset.</summary>
    public double? PatternSizeNm { get; init; }

    /// <summary>
    /// Forbidden exit taxiways keyed by landing end designator (e.g. "10L"). Exact-name match.
    /// Empty for ends without restrictions. Keyed by the zero-pad-normalized designator; read it
    /// via <see cref="NoTurnoffForEnd"/>, never the raw map.
    /// </summary>
    [JsonInclude]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NoTurnoffByEnd
    {
        private get;
        init => field = new Dictionary<string, IReadOnlyList<string>>(value, StringComparer.OrdinalIgnoreCase);
    } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Author-specified displaced-threshold distance in feet per landing-end designator (vNAS map
    /// <c>threshold</c>, e.g. <c>"0 - 957"</c>). Absent ends carry no displacement. Keyed by the
    /// zero-pad-normalized designator; read it via <see cref="ThresholdDisplacementForEnd"/>, never
    /// the raw map.
    /// </summary>
    [JsonInclude]
    public IReadOnlyDictionary<string, double> ThresholdDisplacementFtByEnd
    {
        private get;
        init => field = new Dictionary<string, double>(value, StringComparer.OrdinalIgnoreCase);
    } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when either end of this runway is <paramref name="designator"/> (zero-pad-normalized, so "9" matches "09").</summary>
    public bool MatchesEnd(string designator) => Id.Contains(designator);

    /// <summary>Author-specified preferred turn-off side for the given landing end, or null when none is authored.</summary>
    public ExitSide? TurnoffForEnd(string designator) =>
        TurnoffByEnd.TryGetValue(RunwayIdentifier.NormalizeDesignator(designator), out ExitSide side) ? side : null;

    /// <summary>Author-specified forbidden turn-off taxiways for the given landing end (empty when none).</summary>
    public IReadOnlyList<string> NoTurnoffForEnd(string designator) =>
        NoTurnoffByEnd.TryGetValue(RunwayIdentifier.NormalizeDesignator(designator), out IReadOnlyList<string>? names) ? names : [];

    /// <summary>Displaced-threshold distance in feet for the given landing end; 0 when none is authored.</summary>
    public double ThresholdDisplacementForEnd(string designator) =>
        ThresholdDisplacementFtByEnd.TryGetValue(RunwayIdentifier.NormalizeDesignator(designator), out double ft) ? ft : 0;

    /// <summary>
    /// The landing threshold of <paramref name="designator"/> and the true course flown onto it.
    /// <see cref="Coordinates"/> endpoints are <em>pavement</em> ends, so the authored displacement is
    /// applied downfield along the landing course. Null when this runway does not have that end, or
    /// when it carries no geometry.
    /// </summary>
    public (LatLon Threshold, double LandingCourseDeg)? LandingThresholdForEnd(string designator)
    {
        if ((Coordinates.Count < 2) || !MatchesEnd(designator))
        {
            return null;
        }

        string end = RunwayIdentifier.NormalizeDesignator(designator);
        bool isEnd1 = string.Equals(end, Id.End1, StringComparison.OrdinalIgnoreCase);
        (double approachLat, double approachLon) = isEnd1 ? Coordinates[0] : Coordinates[^1];
        (double departureLat, double departureLon) = isEnd1 ? Coordinates[^1] : Coordinates[0];

        double landingCourse = GeoMath.BearingTo(approachLat, approachLon, departureLat, departureLon);
        double displacementNm = ThresholdDisplacementForEnd(end) / GeoMath.FeetPerNm;
        (double lat, double lon) = GeoMath.ProjectPointRaw(approachLat, approachLon, landingCourse, displacementNm);
        return (new LatLon(lat, lon), landingCourse);
    }
}

public sealed class AirportGroundLayout
{
    private static readonly ILogger Log = SimLog.CreateLogger("AirportGroundLayout");

    public required string AirportId { get; init; }

    public Dictionary<int, GroundNode> Nodes { get; init; } = [];
    public List<GroundEdge> Edges { get; init; } = [];
    public List<GroundArc> Arcs { get; init; } = [];
    public List<GroundRunway> Runways { get; init; } = [];

    /// <summary>
    /// Find the runway whose two-end name (e.g. "10L - 28R") matches the given designator on either end.
    /// Returns null when no runway in the layout names this end.
    /// </summary>
    public GroundRunway? FindRunway(string designator) => Runways.FirstOrDefault(rwy => rwy.MatchesEnd(designator));

    /// <summary>All edges (straight and arc) for iteration.</summary>
    public IEnumerable<IGroundEdge> AllEdges => Edges.Cast<IGroundEdge>().Concat(Arcs);

    /// <summary>
    /// Returns true if the node with <paramref name="nodeId"/> has any edge
    /// whose taxiway matches <paramref name="taxiwayName"/> (case-insensitive,
    /// includes secondary names on junction arcs). Returns false when the node
    /// is not in this layout.
    /// </summary>
    public bool NodeHasEdgeTo(int nodeId, string taxiwayName)
    {
        if (!Nodes.TryGetValue(nodeId, out GroundNode? node))
        {
            return false;
        }

        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.MatchesTaxiway(taxiwayName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuild <see cref="GroundNode.Edges"/> adjacency lists from the <see cref="Edges"/> collection, and clear the exit search's
    /// memos (<see cref="SearchMemo"/>, <see cref="WalkStart"/>), which the rebuilt graph would leave stale.
    /// Call after constructing all edges (e.g., in tests or client-side layout reconstruction).
    /// </summary>
    public void RebuildAdjacencyLists()
    {
        foreach (GroundNode node in Nodes.Values)
        {
            node.Edges.Clear();
        }

        foreach (IGroundEdge edge in AllEdges)
        {
            if (Nodes.TryGetValue(edge.Nodes[0].Id, out GroundNode? nodeA))
            {
                nodeA.Edges.Add(edge);
            }

            if (Nodes.TryGetValue(edge.Nodes[1].Id, out GroundNode? nodeB))
            {
                nodeB.Edges.Add(edge);
            }
        }

        // Build the taxiway-node index eagerly so concurrent readers don't race.
        var index = new Dictionary<string, List<GroundNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode node in Nodes.Values)
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (!index.TryGetValue(edge.TaxiwayName, out List<GroundNode>? list))
                {
                    list = [];
                    index[edge.TaxiwayName] = list;
                }

                if (list.Count == 0 || list[^1].Id != node.Id)
                {
                    list.Add(node);
                }
            }
        }

        _nodesByTaxiway = index;
        Volatile.Write(ref _searchMemo, null);
        Volatile.Write(ref _lastWalkStart, null);
    }

    /// <summary>
    /// Returns all nodes that have at least one edge on the named taxiway.
    /// Index is built eagerly by <see cref="RebuildAdjacencyLists"/>.
    /// </summary>
    public List<GroundNode> GetNodesOnTaxiway(string taxiwayName) => _nodesByTaxiway?.GetValueOrDefault(taxiwayName) ?? [];

    private Dictionary<string, List<GroundNode>>? _nodesByTaxiway;

    /// <summary>Every taxiway / runway-centerline / RAMP name that at least one edge carries.</summary>
    public IEnumerable<string> AllTaxiwayNames => _nodesByTaxiway?.Keys ?? Enumerable.Empty<string>();

    /// <summary>
    /// True when the straight line from <paramref name="from"/> to <paramref name="to"/> crosses a runway
    /// centerline — the target sits on the far side of a runway, however close it is. Keeps gate-adjacent
    /// lookups from reaching across a runway hold line (OAK has gates within 400 ft of a holding-position bar).
    /// </summary>
    public bool RunwayCenterlineBetween(LatLon from, LatLon to)
    {
        foreach (GroundRunway runway in Runways)
        {
            for (int i = 1; i < runway.Coordinates.Count; i++)
            {
                var a = new LatLon(runway.Coordinates[i - 1].Lat, runway.Coordinates[i - 1].Lon);
                var b = new LatLon(runway.Coordinates[i].Lat, runway.Coordinates[i].Lon);
                if (GeoMath.SegmentsIntersect(from, to, a, b) is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a runway designator (e.g. <c>"28R"</c>) to the canonical centerline edge name used as a
    /// taxiway-graph key (e.g. <c>"RWY28R/10L"</c>), so a runway can be routed as a named taxi segment.
    /// Uses the same zero-pad normalization as <see cref="IGroundEdge.MatchesRunway"/>. Returns false when
    /// no runway centerline edge carries the designator (i.e. the token is not a runway at this airport).
    /// </summary>
    public bool TryGetRunwayCenterlineName(string designator, [NotNullWhen(true)] out string? centerlineName)
    {
        if (_nodesByTaxiway is not null)
        {
            foreach (string key in _nodesByTaxiway.Keys)
            {
                if (
                    key.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                    && !key.Contains(":link", StringComparison.OrdinalIgnoreCase)
                    && IGroundEdge.RunwayNameContainsDesignator(key, designator)
                )
                {
                    centerlineName = key;
                    return true;
                }
            }
        }

        centerlineName = null;
        return false;
    }

    public GroundNode? FindParkingByName(string name)
    {
        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Parking && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    public GroundNode? FindHelipadByName(string name)
    {
        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Helipad && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a named spot, searching helipads first, then parking, then spot nodes.
    /// Used by LAND command to resolve destination by name.
    /// </summary>
    public GroundNode? FindSpotByName(string name) => FindHelipadByName(name) ?? FindParkingByName(name) ?? FindSpotNodeByName(name);

    /// <summary>
    /// Find a named spot node (GroundNodeType.Spot only).
    /// Used by $ prefix commands to resolve spot-only destinations.
    /// </summary>
    public GroundNode? FindSpotNodeByName(string name)
    {
        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Spot && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the node where two named taxiways cross. Scans nodes on
    /// <paramref name="taxiA"/> and returns one whose adjacent edges include at least one
    /// matching <paramref name="taxiB"/>. When multiple candidates exist and
    /// <paramref name="near"/> is provided, returns the closest by great-circle distance;
    /// otherwise returns the lowest node id for determinism.
    /// </summary>
    public GroundNode? FindIntersectionNode(string taxiA, string taxiB, LatLon? near = null)
    {
        if (string.IsNullOrEmpty(taxiA) || string.IsNullOrEmpty(taxiB))
        {
            return null;
        }

        List<GroundNode> candidates = GetNodesOnTaxiway(taxiA);
        GroundNode? best = null;
        double bestMetric = double.MaxValue;

        foreach (GroundNode node in candidates)
        {
            bool matchesB = false;
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.MatchesTaxiway(taxiB))
                {
                    matchesB = true;
                    break;
                }
            }
            if (!matchesB)
            {
                continue;
            }

            double metric = near is { } pos ? GeoMath.DistanceNm(pos, node.Position) : node.Id;
            if (metric < bestMetric)
            {
                bestMetric = metric;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Nearest node within <paramref name="maxDistFt"/> that has at least one edge on
    /// <paramref name="taxiwayName"/>, or null when none is in range. Used to anchor a taxi
    /// command's start node to the first cleared taxiway when the heading-biased start-node
    /// heuristic lands on an adjacent parallel taxiway (post-pushback).
    /// </summary>
    public GroundNode? FindNearestNodeOnTaxiway(LatLon position, string taxiwayName, double maxDistFt)
    {
        double maxDistNm = maxDistFt / GeoMath.FeetPerNm;
        GroundNode? best = null;
        double bestDistNm = double.MaxValue;
        foreach (GroundNode node in GetNodesOnTaxiway(taxiwayName))
        {
            double dist = GeoMath.DistanceNm(position, node.Position);
            if (dist > maxDistNm || dist >= bestDistNm)
            {
                continue;
            }

            bestDistNm = dist;
            best = node;
        }

        return best;
    }

    /// <summary>
    /// The nearest node an aircraft can leave. A node with no edges (a GeoJSON spot marker joined to no taxiway,
    /// e.g. SFO spot "30") is skipped: starting a route there makes every taxi infeasible.
    /// </summary>
    /// <param name="lat">Query latitude.</param>
    /// <param name="lon">Query longitude.</param>
    /// <returns>The nearest connected node, or null for a layout with none.</returns>
    public GroundNode? FindNearestNode(double lat, double lon)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Edges.Count == 0)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Result of a nearest-taxi-edge lookup.
    /// </summary>
    public readonly record struct NearestTaxiEdge
    {
        /// <summary>The straight <see cref="GroundEdge"/> nearest to the query point.</summary>
        public required GroundEdge Edge { get; init; }

        /// <summary>Perpendicular distance from the query point to the foot-of-perpendicular on the edge (clamped to endpoints).</summary>
        public required double DistNm { get; init; }

        /// <summary>Latitude of the foot-of-perpendicular.</summary>
        public required double FootLat { get; init; }

        /// <summary>Longitude of the foot-of-perpendicular.</summary>
        public required double FootLon { get; init; }

        /// <summary>Distance from <c>Edge.Nodes[0]</c> to the foot along the edge direction.</summary>
        public required double AlongNm { get; init; }

        /// <summary>True when the perpendicular foot fell outside the edge and was clamped to an end node.</summary>
        public required bool Clamped { get; init; }
    }

    /// <summary>
    /// Find the nearest straight taxi edge to a query point: straight edges only; arcs are
    /// <see cref="FindOccupiedFilletArc"/>'s. Filters out:
    /// <list type="bullet">
    /// <item><see cref="GroundArc"/> (fillet curves at junctions)</item>
    /// <item>runway-centerline edges (<see cref="IGroundEdge.IsRunwayCenterline"/>)</item>
    /// <item>ramp connector edges (<see cref="IGroundEdge.IsRamp"/>)</item>
    /// </list>
    /// Used by the ground-spawn snap to realign off-graph ground-coord aircraft
    /// onto a taxi surface before the first tick fires, and by <see cref="FindOccupiedTaxiEdge"/>.
    /// </summary>
    public NearestTaxiEdge? FindNearestTaxiEdge(double lat, double lon)
    {
        GroundEdge? bestEdge = null;
        double bestDistNm = double.MaxValue;
        double bestFootLat = 0;
        double bestFootLon = 0;
        double bestAlongNm = 0;
        bool bestClamped = false;

        var seen = new HashSet<IGroundEdge>();
        foreach (GroundNode node in Nodes.Values)
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (!seen.Add(edge))
                {
                    continue;
                }
                if (edge is not GroundEdge straight)
                {
                    continue;
                }
                if (edge.IsRunwayCenterline || edge.IsRamp)
                {
                    continue;
                }

                (double footLat, double footLon, double alongNm, bool clamped) = GeoMath.FootOfPerpendicular(
                    lat,
                    lon,
                    straight.Nodes[0].Position.Lat,
                    straight.Nodes[0].Position.Lon,
                    straight.Nodes[1].Position.Lat,
                    straight.Nodes[1].Position.Lon
                );
                double distNm = GeoMath.DistanceNm(lat, lon, footLat, footLon);
                if (distNm < bestDistNm)
                {
                    bestDistNm = distNm;
                    bestEdge = straight;
                    bestFootLat = footLat;
                    bestFootLon = footLon;
                    bestAlongNm = alongNm;
                    bestClamped = clamped;
                }
            }
        }

        return bestEdge is null
            ? null
            : new NearestTaxiEdge
            {
                Edge = bestEdge,
                DistNm = bestDistNm,
                FootLat = bestFootLat,
                FootLon = bestFootLon,
                AlongNm = bestAlongNm,
                Clamped = bestClamped,
            };
    }

    /// <summary>
    /// The distance inside which an aircraft counts as standing AT a node: bearing-to-node is undefined this
    /// close, and an aircraft this near the node its route starts at has nothing left to taxi to.
    /// </summary>
    public const double AtNodeToleranceFt = 15.0;

    /// <summary>
    /// Pick the start node for a taxi command, biased by heading. Unlike
    /// <see cref="FindNearestNode(LatLon)"/> — which returns the absolute
    /// nearest node and can land on the wrong branch when an aircraft rests
    /// between graph nodes after a directional pushback (issue #161) — this
    /// returns, for an aircraft neither at a parking node nor on a taxi edge
    /// (both below), the nearest node within <paramref name="maxDistFt"/> that has
    /// at least one non-RAMP, non-runway-centerline outbound edge whose
    /// bearing is within 90° of the aircraft's <paramref name="heading"/>.
    /// <para>
    /// Returns null when no qualifying node exists in the radius — the caller
    /// should fall back to <see cref="FindNearestNode(LatLon)"/>. The maximum
    /// distance is generous because the helper is only a discriminator across
    /// the small set of candidate nodes any near-graph aircraft has within
    /// reach; selection is by closest-of-the-qualifying, not by absolute
    /// nearest, so a far node with the right alignment never beats a close
    /// one.
    /// </para>
    /// <para>
    /// Aircraft positioned <em>at</em> a node (HoldingShortPhase fuselage tip
    /// at the hold-short line, AtParkingPhase at the spot) still resolve to
    /// that node because their forward heading aligns with the outbound edge
    /// the route continues along — the existing-edge test admits the same
    /// node <see cref="FindNearestNode(LatLon)"/> would have picked.
    /// </para>
    /// <para>
    /// An aircraft away from every node but on a straight taxi edge
    /// (<see cref="FindOccupiedTaxiEdge"/>) starts at that edge's endpoint ahead,
    /// so a long edge never hands the start to a nearer node on a parallel
    /// taxiway. That branch applies neither <paramref name="maxDistFt"/> nor the
    /// heading-aligned-edge test: the endpoint is on the taxiway the aircraft is
    /// on, however far along it. An aircraft on no straight edge but on a fillet
    /// arc (<see cref="FindOccupiedFilletArc"/>), past the end of the edge it
    /// came off, starts at the arc's end ahead, never the node it entered by.
    /// </para>
    /// </summary>
    public GroundNode? FindNearestNodeForTaxi(LatLon position, TrueHeading heading, double maxDistFt = 100.0)
    {
        // Below this distance the candidate node and the aircraft are
        // effectively co-located — bearing-to-node is undefined and the
        // existing HoldingShortPhase / AtParkingPhase behaviour (start at the
        // node the aircraft is sitting at) must be preserved.
        double atNodeNm = AtNodeToleranceFt / GeoMath.FeetPerNm;

        if (FindAtParkingStart(position, atNodeNm) is { } parkingStart)
        {
            return parkingStart;
        }

        // On a taxiway's pavement, away from every node: start on that taxiway, at the end of the edge or fillet ahead.
        // The nearest node can sit on a parallel taxiway (issue #880: KOAK C/D, KSFO B/A) or behind the aircraft.
        if (!HasTaxiNodeWithin(position, atNodeNm))
        {
            if (FindOccupiedTaxiEdge(position) is { } occupied)
            {
                return TaxiEdgeEndpointAhead(position, heading, occupied);
            }

            if (FindOccupiedFilletArc(position) is { } arc)
            {
                return FilletArcEndAhead(position, heading, arc);
            }
        }

        return FindNearestAlignedNode(position, heading, maxDistFt / GeoMath.FeetPerNm, atNodeNm);
    }

    /// <summary>
    /// The straight taxi edge an aircraft stands mid-way along when <see cref="FindNearestNodeForTaxi"/> starts its taxi
    /// at that edge's endpoint ahead: at no parking node, within <see cref="AtNodeToleranceFt"/> of no taxi node, and on
    /// the edge (<see cref="FindOccupiedTaxiEdge"/>). Null otherwise.
    /// </summary>
    public GroundEdge? FindMidEdgeTaxiStart(LatLon position)
    {
        double atNodeNm = AtNodeToleranceFt / GeoMath.FeetPerNm;
        if ((FindAtParkingStart(position, atNodeNm) is not null) || HasTaxiNodeWithin(position, atNodeNm))
        {
            return null;
        }

        return FindOccupiedTaxiEdge(position);
    }

    /// <summary>
    /// The start node for an aircraft essentially at a Parking/Helipad node (within <paramref name="atNodeNm"/>), or null
    /// when it is at none.
    /// <para>
    /// The parking node is the start UNLESS it has a co-located non-parking neighbor (a fillet phase-d-shorten endpoint at
    /// near-zero distance). The co-located neighbor is the natural exit point — using it lets the route skip the
    /// degenerate near-zero parking-exit edge while still keeping the route's first segment anchored at the aircraft's
    /// actual position. When no co-located neighbor exists (e.g. SFO 42-4 where the only edge from 1047 is a 42 ft RAMP
    /// to 2718), the parking node itself is the start so the route's first segment IS the parking-exit RAMP — otherwise
    /// the resolver picks a fillet vertex 90+ ft away, leaving the aircraft off-line from segment 0 and unable to
    /// converge under the short-route speed cap (slow-creep spin observed at SFO 42-4 → 10L).
    /// </para>
    /// </summary>
    private GroundNode? FindAtParkingStart(LatLon position, double atNodeNm)
    {
        foreach (GroundNode parkingNode in Nodes.Values)
        {
            if (parkingNode.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad))
            {
                continue;
            }
            if (GeoMath.DistanceNm(position, parkingNode.Position) > atNodeNm)
            {
                continue;
            }

            return FindColocatedNonParkingNeighbor(parkingNode, atNodeNm) ?? parkingNode;
        }

        return null;
    }

    /// <summary>The first non-parking neighbor of <paramref name="parkingNode"/> within <paramref name="atNodeNm"/> of it, or null.</summary>
    private static GroundNode? FindColocatedNonParkingNeighbor(GroundNode parkingNode, double atNodeNm)
    {
        foreach (IGroundEdge edge in parkingNode.Edges)
        {
            GroundNode other = edge.OtherNode(parkingNode);
            if (other.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
            {
                continue;
            }
            if (GeoMath.DistanceNm(parkingNode.Position, other.Position) <= atNodeNm)
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>
    /// The nearest non-parking node within <paramref name="maxDistNm"/> that is not behind the aircraft (unless within
    /// <paramref name="atNodeNm"/> of it) and has a taxi edge aligned with <paramref name="heading"/>
    /// (<see cref="HasHeadingAlignedTaxiEdge"/>), or null.
    /// </summary>
    private GroundNode? FindNearestAlignedNode(LatLon position, TrueHeading heading, double maxDistNm, double atNodeNm)
    {
        GroundNode? best = null;
        double bestDistNm = double.MaxValue;

        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(position, node.Position);
            if (dist > maxDistNm || dist >= bestDistNm)
            {
                continue;
            }

            // Reject candidates behind the aircraft. Skipped when essentially
            // at the node so an aircraft parked on a hold-short still starts
            // there even though "bearing to self" is meaningless.
            if (dist > atNodeNm)
            {
                double bearingToNode = GeoMath.BearingTo(position, node.Position);
                if (GeoMath.AbsBearingDifference(bearingToNode, heading.Degrees) >= 90.0)
                {
                    continue;
                }
            }

            if (!HasHeadingAlignedTaxiEdge(node, heading))
            {
                continue;
            }

            bestDistNm = dist;
            best = node;
        }

        return best;
    }

    /// <summary>
    /// Lateral distance from a straight taxi edge's centreline within which an aircraft is on that taxiway (half a TDG 3/4
    /// taxiway's 50 ft width).
    /// </summary>
    public const double OnTaxiEdgeMaxOffsetFt = 25.0;

    /// <summary>
    /// True when a non-parking node an aircraft can leave lies within <paramref name="withinNm"/> of
    /// <paramref name="position"/>.
    /// </summary>
    private bool HasTaxiNodeWithin(LatLon position, double withinNm)
    {
        foreach (GroundNode node in Nodes.Values)
        {
            if ((node.Type is GroundNodeType.Parking or GroundNodeType.Helipad) || (node.Edges.Count == 0))
            {
                continue;
            }
            if (GeoMath.DistanceNm(position, node.Position) <= withinNm)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The endpoint of <paramref name="edge"/>, the straight taxi edge <paramref name="position"/> lies on, ahead of
    /// <paramref name="heading"/> (<see cref="IsNodeAhead"/>); when both or neither endpoint is ahead (a heading across the
    /// edge), the nearer one.
    /// </summary>
    private static GroundNode TaxiEdgeEndpointAhead(LatLon position, TrueHeading heading, GroundEdge edge)
    {
        GroundNode first = edge.Nodes[0];
        GroundNode second = edge.Nodes[1];
        bool firstAhead = IsNodeAhead(position, heading, first);
        bool secondAhead = IsNodeAhead(position, heading, second);
        GroundNode endpoint;
        if (firstAhead != secondAhead)
        {
            endpoint = firstAhead ? first : second;
        }
        else
        {
            endpoint = (GeoMath.DistanceNm(position, first.Position) <= GeoMath.DistanceNm(position, second.Position)) ? first : second;
        }

        Log.LogDebug(
            "[TaxiStart] on {Taxiway} edge {First}-{Second}, away from every node: the taxi starts at its endpoint ahead, node {NodeId}",
            edge.TaxiwayName,
            first.Id,
            second.Id,
            endpoint.Id
        );
        return endpoint;
    }

    /// <summary>
    /// The straight taxi edge an aircraft at <paramref name="position"/> is on: the nearest one
    /// (<see cref="FindNearestTaxiEdge(LatLon)"/>), when it lies within <see cref="OnTaxiEdgeMaxOffsetFt"/> of the position
    /// and the perpendicular from the position falls strictly inside it. Null otherwise: a position past an edge's end
    /// (into a fillet arc, or beside a runway junction) is not on that edge, however close its end node.
    /// </summary>
    public GroundEdge? FindOccupiedTaxiEdge(LatLon position)
    {
        if (FindNearestTaxiEdge(position) is not { } nearest)
        {
            return null;
        }

        bool onEdge = !nearest.Clamped && ((nearest.DistNm * GeoMath.FeetPerNm) <= OnTaxiEdgeMaxOffsetFt);
        return onEdge ? nearest.Edge : null;
    }

    /// <summary>Refinement passes <see cref="CubicBezier.ClosestT(LatLon, int)"/> runs for the on-fillet test.</summary>
    private const int FilletClosestIterations = 24;

    /// <summary>
    /// The fillet arc an aircraft at <paramref name="position"/> is on: the nearest taxi arc (no ramp connector, no runway
    /// centreline) whose curve passes within <see cref="OnTaxiEdgeMaxOffsetFt"/> of the position, the nearest point of the
    /// curve lying strictly between its end nodes. Null when there is none.
    /// </summary>
    private GroundArc? FindOccupiedFilletArc(LatLon position)
    {
        double maxOffsetNm = OnTaxiEdgeMaxOffsetFt / GeoMath.FeetPerNm;
        GroundArc? best = null;
        double bestDistNm = double.MaxValue;
        var seen = new HashSet<IGroundEdge>();
        foreach (GroundNode node in Nodes.Values)
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if (
                    seen.Add(edge)
                    && (TryGetTaxiArc(edge) is { } arc)
                    && (OnArcDistanceNm(position, arc, maxOffsetNm) is { } distNm)
                    && (distNm < bestDistNm)
                )
                {
                    best = arc;
                    bestDistNm = distNm;
                }
            }
        }

        return best;
    }

    /// <summary><paramref name="edge"/> as a taxi fillet arc (no ramp connector, no runway centreline), or null.</summary>
    private static GroundArc? TryGetTaxiArc(IGroundEdge edge) => ((edge is GroundArc arc) && !arc.IsRamp && !arc.IsRunwayCenterline) ? arc : null;

    /// <summary>
    /// Distance from <paramref name="position"/> to <paramref name="arc"/>'s curve when it is within
    /// <paramref name="maxOffsetNm"/> and the nearest point lies strictly inside the arc (<see cref="DistanceInsideArcNm"/>),
    /// else null.
    /// </summary>
    private static double? OnArcDistanceNm(LatLon position, GroundArc arc, double maxOffsetNm)
    {
        // Every point of the curve lies within its own length of an end node.
        if (GeoMath.DistanceNm(position, arc.Nodes[0].Position) > (arc.DistanceNm + maxOffsetNm))
        {
            return null;
        }

        return (DistanceInsideArcNm(position, arc) is { } distNm) && (distNm <= maxOffsetNm) ? distNm : null;
    }

    /// <summary>
    /// Distance from <paramref name="position"/> to the nearest point of <paramref name="arc"/>'s curve, or null when that
    /// point is one of the curve's ends: the position lies past the arc, not beside it.
    /// </summary>
    private static double? DistanceInsideArcNm(LatLon position, GroundArc arc)
    {
        const double endTolerance = 1e-3;
        CubicBezier curve = arc.ToBezier();
        double t = curve.ClosestT(position, FilletClosestIterations);
        if ((t <= endTolerance) || (t >= (1.0 - endTolerance)))
        {
            return null;
        }

        (double lat, double lon) = curve.Evaluate(t);
        return GeoMath.DistanceNm(position, new LatLon(lat, lon));
    }

    /// <summary>
    /// The end node of <paramref name="arc"/>, the fillet <paramref name="position"/> lies on, whose bearing from the
    /// position is nearest <paramref name="heading"/>: the end the aircraft is driving toward, never the one it entered by.
    /// </summary>
    private static GroundNode FilletArcEndAhead(LatLon position, TrueHeading heading, GroundArc arc)
    {
        GroundNode first = arc.Nodes[0];
        GroundNode second = arc.Nodes[1];
        double firstOffDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, first.Position), heading.Degrees);
        double secondOffDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, second.Position), heading.Degrees);
        GroundNode endpoint = (firstOffDeg <= secondOffDeg) ? first : second;

        Log.LogDebug(
            "[TaxiStart] on fillet {Taxiway} {First}-{Second}, away from every node: the taxi starts at its end ahead, node {NodeId}",
            arc.TaxiwayName,
            first.Id,
            second.Id,
            endpoint.Id
        );
        return endpoint;
    }

    /// <summary>
    /// True when <paramref name="node"/> lies less than 90° off <paramref name="heading"/> as seen from
    /// <paramref name="position"/>.
    /// </summary>
    private static bool IsNodeAhead(LatLon position, TrueHeading heading, GroundNode node) =>
        GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, node.Position), heading.Degrees) < 90.0;

    /// <summary>
    /// Returns true when <paramref name="node"/> has at least one outbound
    /// taxi edge (not RAMP, not runway centerline) whose bearing from the
    /// node toward its neighbor is within 90° of <paramref name="heading"/>.
    /// Used by <see cref="FindNearestNodeForTaxi"/> to reject candidate
    /// start nodes whose only taxi connections head away from the aircraft.
    /// </summary>
    private static bool HasHeadingAlignedTaxiEdge(GroundNode node, TrueHeading heading)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRunwayCenterline || edge.IsRamp)
            {
                continue;
            }

            GroundNode other = edge.OtherNode(node);
            double bearing = GeoMath.BearingTo(node.Position, other.Position);
            if (GeoMath.AbsBearingDifference(bearing, heading.Degrees) < 90.0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Find the nearest runway centerline node that is ahead of or abeam the
    /// aircraft along the given heading. When <paramref name="runwayDesignator"/>
    /// is provided, only considers nodes with RWY edges matching that runway.
    /// Falls back to the nearest matching centerline node if none is ahead.
    /// </summary>
    public GroundNode? FindNearestCenterlineNode(double lat, double lon, TrueHeading runwayHeading, string? runwayDesignator = null)
    {
        GroundNode? bestAhead = null;
        double bestAheadDist = double.MaxValue;
        GroundNode? bestAny = null;
        double bestAnyDist = double.MaxValue;

        foreach (GroundNode node in CenterlineNodes(runwayDesignator))
        {
            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);

            if (dist < bestAnyDist)
            {
                bestAnyDist = dist;
                bestAny = node;
            }

            double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            if (diff <= 90 && dist < bestAheadDist)
            {
                bestAheadDist = dist;
                bestAhead = node;
            }
        }

        return bestAhead ?? bestAny;
    }

    /// <summary>
    /// The nodes with a runway centerline edge — of <paramref name="runwayDesignator"/> when given, of any runway when null — in
    /// <see cref="Nodes"/> order, memoized per designator (<see cref="SearchMemo"/>). Only null stands for any runway: every other
    /// designator, the empty one included, is matched as given (<see cref="HasRunwayEdgeForDesignator"/>).
    /// </summary>
    private GroundNode[] CenterlineNodes(string? runwayDesignator) =>
        CurrentSearchMemo()
            .CenterlineNodes.GetOrAdd(
                (AnyRunway: runwayDesignator is null, Designator: runwayDesignator ?? ""),
                static (key, layout) =>
                    [
                        .. layout.Nodes.Values.Where(node =>
                            HasRunwayCenterlineEdge(node) && (key.AnyRunway || HasRunwayEdgeForDesignator(node, key.Designator))
                        ),
                    ],
                this
            );

    /// <summary>
    /// Returns true if the node has a RWY edge whose name contains the given
    /// runway designator (e.g., "RWY10L/28R" contains "28R").
    /// </summary>
    private static bool HasRunwayEdgeForDesignator(GroundNode node, string designator)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.MatchesRunway(designator))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// From a runway centerline node, find the next centerline node ahead along
    /// the given heading. Walks RWY-prefixed edges and picks the neighbor whose
    /// bearing is closest to the runway heading (within 90°).
    /// </summary>
    public GroundNode? FindCenterlineNeighborAhead(GroundNode currentNode, TrueHeading runwayHeading, string? runwayDesignator = null)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;

        foreach (IGroundEdge edge in currentNode.Edges)
        {
            if (!edge.IsRunwayCenterline)
            {
                continue;
            }

            if (runwayDesignator is not null && !edge.MatchesRunway(runwayDesignator))
            {
                continue;
            }

            GroundNode neighbor = edge.OtherNode(currentNode);

            double bearing = GeoMath.BearingTo(currentNode.Position, neighbor.Position);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            if (diff < 90 && diff < bestDiff)
            {
                bestDiff = diff;
                best = neighbor;
            }
        }

        return best;
    }

    /// <summary>Result of <see cref="FindExitFromCenterline"/> — a single hold-short with metadata.</summary>
    public readonly record struct CenterlineExitResult(
        GroundNode HoldShort,
        string Taxiway,
        List<GroundNode> Path,
        double ExitAngle,
        ExitSide Side,
        GroundNode WalkCenterline
    );

    /// <summary>
    /// Caller-supplied verdict on a candidate exit during
    /// <see cref="FindOnSidePreferredExit"/>. <c>Accept</c> commits, <c>Skip</c>
    /// excludes the entire taxiway and continues, <c>Defer</c> remembers it as
    /// an off-side fallback (used internally for the off-side rule and may be
    /// returned by callers that want the same fallback semantics for their own
    /// predicate).
    /// </summary>
    public enum CandidateVerdict
    {
        Accept,
        Skip,
        Defer,
    }

    /// <summary>
    /// Walk centerlines ahead of the aircraft and pick the next exit that
    /// satisfies the side preference. Off-side candidates (relative to
    /// <paramref name="sidePref"/>) are deferred — the search continues, and
    /// the deferred candidate is only committed if no on-side option is found.
    /// The optional <paramref name="filter"/> lets the caller veto candidates
    /// (e.g. comfort-braking checks); a returned <see cref="CandidateVerdict.Skip"/>
    /// excludes the candidate's taxiway from subsequent iterations in this call.
    /// Returns <see langword="null"/> when no exit (on-side or off-side fallback)
    /// is found.
    /// </summary>
    public CenterlineExitResult? FindOnSidePreferredExit(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference,
        ExitSide? sidePref,
        HashSet<int>? excludeBranchPoints = null,
        HashSet<int>? excludeHoldShortNodes = null,
        Func<CenterlineExitResult, CandidateVerdict>? filter = null,
        int maxIterations = 30
    )
    {
        var localTaxiwayExclusion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CenterlineExitResult? deferredOffSide = null;

        for (int i = 0; i < maxIterations; i++)
        {
            (GroundNode HoldShort, string Taxiway, List<GroundNode> Path, double ExitAngle, ExitSide Side, GroundNode WalkCenterline)? raw =
                FindExitFromCenterline(
                    lat,
                    lon,
                    runwayHeading,
                    runwayDesignator,
                    preference,
                    excludeBranchPoints,
                    excludeHoldShortNodes,
                    localTaxiwayExclusion.Count > 0 ? localTaxiwayExclusion : null
                );
            if (raw is null)
            {
                break;
            }

            var candidate = new CenterlineExitResult(
                raw.Value.HoldShort,
                raw.Value.Taxiway,
                raw.Value.Path,
                raw.Value.ExitAngle,
                raw.Value.Side,
                raw.Value.WalkCenterline
            );

            if (filter is not null)
            {
                CandidateVerdict verdict = filter(candidate);
                if (verdict == CandidateVerdict.Skip)
                {
                    localTaxiwayExclusion.Add(candidate.Taxiway);
                    continue;
                }
                if (verdict == CandidateVerdict.Defer)
                {
                    deferredOffSide ??= candidate;
                    localTaxiwayExclusion.Add(candidate.Taxiway);
                    continue;
                }
            }

            // Off-side relative to the side preference: defer and keep walking.
            // Excluding the entire taxiway from subsequent iterations is required
            // because BFS clusters runway centerlines that are tangent-link
            // neighbors — excluding only the walking centerline still re-finds
            // the same hold-short via the next centerline's cluster expansion.
            if ((sidePref is not null) && (candidate.Side != sidePref))
            {
                deferredOffSide ??= candidate;
                localTaxiwayExclusion.Add(candidate.Taxiway);
                continue;
            }

            return candidate;
        }

        return deferredOffSide;
    }

    /// <summary>
    /// Walk centerline nodes ahead of the aircraft and search outward at each one
    /// for an exit matching the preference. Returns the first match with its path
    /// (starting at the centerline branch point, ending at the hold-short).
    /// This is the correct search direction: runway → taxiway → hold-short.
    /// </summary>
    public (
        GroundNode HoldShort,
        string Taxiway,
        List<GroundNode> Path,
        double ExitAngle,
        ExitSide Side,
        GroundNode WalkCenterline
    )? FindExitFromCenterline(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference,
        HashSet<int>? excludeBranchPoints = null,
        HashSet<int>? excludeHoldShortNodes = null,
        HashSet<string>? excludeTaxiways = null
    )
    {
        // Authored noTurnoff: forbid named taxiways for this landing direction. Applied only
        // when the controller hasn't explicitly named a taxiway — explicit EXIT commands win.
        HashSet<string>? forbiddenTaxiways = null;
        if ((preference?.Taxiway is null) && (FindRunway(runwayDesignator) is { } authoredRwy))
        {
            IReadOnlyList<string> forbidden = authoredRwy.NoTurnoffForEnd(runwayDesignator);
            if (forbidden.Count > 0)
            {
                forbiddenTaxiways = new HashSet<string>(forbidden, StringComparer.OrdinalIgnoreCase);
            }
        }

        // Caller-supplied exclusion (e.g. LandingPhase deferring an entire taxiway
        // after seeing an off-side hold-short there) merges with the airport noTurnoff list.
        if (excludeTaxiways is { Count: > 0 })
        {
            forbiddenTaxiways = forbiddenTaxiways is null
                ? new HashSet<string>(excludeTaxiways, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(forbiddenTaxiways.Concat(excludeTaxiways), StringComparer.OrdinalIgnoreCase);
        }

        // Walk along-track: only consider centerline nodes ahead of the aircraft.
        // When no taxiway preference is set, defer any back-exit (>100°) and keep
        // walking — a real pilot wouldn't U-turn on the runway to reach E if G or
        // H is available further ahead. Commit to the deferred back-exit only if
        // nothing forward turns up.
        const double BackExitAngleThreshold = 100.0;
        (GroundNode Node, string Taxiway, List<GroundNode> Path, double ExitAngle, ExitSide Side, GroundNode WalkCenterline)? deferredBackExit = null;
        foreach (GroundNode current in CenterlineWalkAhead(lat, lon, runwayHeading, runwayDesignator, excludeBranchPoints))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "[ExitCL] Checking centerline node #{Id} at ({Lat:F6}, {Lon:F6}), pref={PrefTwy}/{PrefSide}",
                    current.Id,
                    current.Position.Lat,
                    current.Position.Lon,
                    preference?.Taxiway ?? "any",
                    preference?.Side?.ToString() ?? "any"
                );
            }
            (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? result = FindAdjacentHoldShort(
                current,
                runwayDesignator,
                runwayHeading,
                preference,
                excludeHoldShortNodes,
                forbiddenTaxiways
            );
            if (result is not null)
            {
                double? exitAngle =
                    ComputePathExitAngle(result.Value.Path, result.Value.Taxiway, runwayHeading)
                    ?? ComputeExitAngle(result.Value.Node, result.Value.Taxiway, runwayHeading);
                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.LogDebug(
                        "[ExitCL] Found exit: twy={Twy} HS=#{HsId} angle={Angle:F0}° path=[{Path}]",
                        result.Value.Taxiway,
                        result.Value.Node.Id,
                        exitAngle,
                        string.Join("→", result.Value.Path.Select(n => n.Id))
                    );
                }

                bool isBackExit = (exitAngle is not null) && (exitAngle.Value > BackExitAngleThreshold);
                bool hasTaxiwayPreference = preference?.Taxiway is not null;
                if (isBackExit && !hasTaxiwayPreference)
                {
                    // Remember the nearest back-exit but keep walking for a forward one.
                    deferredBackExit ??= (result.Value.Node, result.Value.Taxiway, result.Value.Path, exitAngle!.Value, result.Value.Side, current);
                    continue;
                }

                return (result.Value.Node, result.Value.Taxiway, result.Value.Path, exitAngle ?? 90, result.Value.Side, current);
            }
        }

        return deferredBackExit;
    }

    /// <summary>Most centerline nodes an exit search's walk steps through (<see cref="CenterlineWalkAhead"/>), passed-over ones included.</summary>
    private const int MaxCenterlineHops = 30;

    /// <summary>How far behind the search point a centerline node may lie and still be checked for an exit.</summary>
    private const double CenterlineBehindToleranceNm = 0.005;

    /// <summary>
    /// The centerline nodes of <paramref name="runwayDesignator"/> an exit search from (<paramref name="lat"/>, <paramref name="lon"/>)
    /// checks, in walk order: from the nearest centerline node (<see cref="FindNearestCenterlineNode(double, double, TrueHeading, string?)"/>)
    /// forward along <paramref name="runwayHeading"/> for at most <see cref="MaxCenterlineHops"/> nodes, passing over those more than
    /// <see cref="CenterlineBehindToleranceNm"/> behind the point and the branch points in <paramref name="excludeBranchPoints"/>
    /// (where the aircraft has said "unable").
    /// </summary>
    private IEnumerable<GroundNode> CenterlineWalkAhead(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        HashSet<int>? excludeBranchPoints
    )
    {
        var from = new LatLon(lat, lon);
        foreach (GroundNode node in WalkFrom(lat, lon, runwayHeading, runwayDesignator).Chain)
        {
            bool behind = GeoMath.AlongTrackDistanceNm(node.Position, from, runwayHeading) < -CenterlineBehindToleranceNm;
            bool unable = (excludeBranchPoints is not null) && excludeBranchPoints.Contains(node.Id);
            if (!behind && !unable)
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// The last point an exit search walked from (<see cref="WalkFrom"/>), for the node count it was found at, and its walk: the
    /// centerline nodes it steps through (<see cref="BuildCenterlineChain"/>) from the nearest centerline node, empty with none.
    /// Every search of one exits-ahead list walks from the same point, so the one slot serves the whole list and holds one walk at
    /// most; <see cref="RebuildAdjacencyLists"/> clears it.
    /// </summary>
    private sealed record WalkStart(int NodeCount, double Lat, double Lon, double HeadingDeg, string Designator, GroundNode[] Chain)
    {
        public bool IsFor(int nodeCount, double lat, double lon, TrueHeading runwayHeading, string designator) =>
            (NodeCount == nodeCount)
            && (Lat == lat)
            && (Lon == lon)
            && (HeadingDeg == runwayHeading.Degrees)
            && string.Equals(Designator, designator, StringComparison.Ordinal);
    }

    private WalkStart? _lastWalkStart;

    /// <summary>
    /// The walk of an exit search from (<paramref name="lat"/>, <paramref name="lon"/>): the nearest centerline node
    /// (<see cref="FindNearestCenterlineNode(double, double, TrueHeading, string?)"/>) and the chain ahead of it, kept for the last point
    /// asked: a whole-runway scan and a graph walk that every search from one point would otherwise repeat.
    /// </summary>
    private WalkStart WalkFrom(double lat, double lon, TrueHeading runwayHeading, string runwayDesignator)
    {
        WalkStart? last = Volatile.Read(ref _lastWalkStart);
        if ((last is not null) && last.IsFor(Nodes.Count, lat, lon, runwayHeading, runwayDesignator))
        {
            return last;
        }

        GroundNode[] chain = FindNearestCenterlineNode(lat, lon, runwayHeading, runwayDesignator) is { } start
            ? BuildCenterlineChain(start, runwayHeading, runwayDesignator)
            : [];
        var walk = new WalkStart(Nodes.Count, lat, lon, runwayHeading.Degrees, runwayDesignator, chain);
        Volatile.Write(ref _lastWalkStart, walk);
        return walk;
    }

    /// <summary>
    /// The centerline nodes an exit search's walk steps through from <paramref name="start"/>: it and each next node ahead
    /// (<see cref="FindCenterlineNeighborAhead"/>), at most <see cref="MaxCenterlineHops"/> of them.
    /// </summary>
    private GroundNode[] BuildCenterlineChain(GroundNode start, TrueHeading runwayHeading, string runwayDesignator)
    {
        List<GroundNode> chain = [];
        for (GroundNode? current = start; (current is not null) && (chain.Count < MaxCenterlineHops); )
        {
            chain.Add(current);
            current = FindCenterlineNeighborAhead(current, runwayHeading, runwayDesignator);
        }

        return [.. chain];
    }

    /// <summary>
    /// The taxiways a named exit search from (<paramref name="lat"/>, <paramref name="lon"/>) can start down: every taxiway name on
    /// an edge at the tangent cluster (<see cref="ClusterSeedTaxiways"/>) of a centerline node its walk checks
    /// (<see cref="CenterlineWalkAhead"/>). A search naming any other taxiway finds nothing, so a caller judging many taxiways from
    /// one point passes over the rest without searching. Case-insensitive, as <see cref="IGroundEdge.MatchesTaxiway"/> is.
    /// </summary>
    public HashSet<string> TaxiwaysSeededAhead(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        HashSet<int>? excludeBranchPoints
    )
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode node in CenterlineWalkAhead(lat, lon, runwayHeading, runwayDesignator, excludeBranchPoints))
        {
            names.UnionWith(ClusterSeedTaxiways(node));
        }

        return names;
    }

    /// <summary>
    /// The taxiways an exits-ahead list judges on <paramref name="runwayDesignator"/>: every taxiway name on an edge at one of the
    /// runway's centerline nodes — straight branches and junction arcs alike, so a branch that ends without a bar of its own and hops
    /// to the joining taxiway's is judged too — less the end's authored no-turnoff taxiways, which an exit is never offered onto.
    /// Sorted and de-duplicated case-insensitively (<c>OrdinalIgnoreCase</c>), memoized per runway end (<see cref="SearchMemo"/>).
    /// </summary>
    public IReadOnlyList<string> ExitListTaxiways(string runwayDesignator) =>
        CurrentSearchMemo()
            .ExitListTaxiways.GetOrAdd(runwayDesignator, static (designator, layout) => layout.CollectExitListTaxiways(designator), this);

    private IReadOnlyList<string> CollectExitListTaxiways(string runwayDesignator)
    {
        HashSet<string> noTurnoff = new(FindRunway(runwayDesignator)?.NoTurnoffForEnd(runwayDesignator) ?? [], StringComparer.OrdinalIgnoreCase);
        SortedSet<string> taxiways = new(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode node in Nodes.Values)
        {
            if (!node.Edges.Any(edge => edge.IsRunwayCenterline && edge.MatchesRunway(runwayDesignator)))
            {
                continue;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                IEnumerable<string> names = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
                taxiways.UnionWith(names.Where(name => !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) && !noTurnoff.Contains(name)));
            }
        }

        return [.. taxiways];
    }

    /// <summary>
    /// Every taxiway name on a non-centerline edge at <paramref name="centerlineNode"/>'s tangent cluster
    /// (<see cref="ExpandCenterlineCluster"/>): the edges an exit search from the node seeds, so a search naming a taxiway outside
    /// the set has no edge to start down. Case-insensitive, memoized per node instance (<see cref="SearchMemo"/>).
    /// </summary>
    private IReadOnlySet<string> ClusterSeedTaxiways(GroundNode centerlineNode) =>
        CurrentSearchMemo()
            .ClusterSeedTaxiways.GetOrAdd(
                centerlineNode,
                static node =>
                {
                    HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                    foreach (GroundNode clusterNode in ExpandCenterlineCluster(node, [node.Id]))
                    {
                        foreach (IGroundEdge edge in clusterNode.Edges.Where(edge => !edge.IsRunwayCenterline))
                        {
                            names.UnionWith(edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName]);
                        }
                    }

                    return names;
                }
            );

    /// <summary>
    /// From a runway centerline node, find a hold-short node reachable via
    /// non-RWY edges using BFS (max 12 hops). Each branch is constrained by
    /// the taxiway name of its first non-RWY edge. Optionally filters by
    /// runway designator, exit side, or taxiway name preference.
    /// Returns the hold-short node, taxiway name, and path from centerline.
    /// A preference naming a taxiway is the controller's instruction: a bar short of the holding distance with nothing
    /// beyond it is an exit only then.
    /// </summary>
    public (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? FindAdjacentHoldShort(
        GroundNode centerlineNode,
        string? runwayDesignator,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        HashSet<int>? excludeHoldShortNodes = null,
        HashSet<string>? forbiddenTaxiways = null
    ) =>
        FindAdjacentHoldShort(
            new ExitSearch
            {
                Centerline = centerlineNode,
                RunwayDesignator = runwayDesignator,
                RunwayHeading = runwayHeading,
                Preference = preference,
                ExcludeHoldShortNodes = excludeHoldShortNodes,
                ForbiddenTaxiways = forbiddenTaxiways,
                Instructed = preference?.Taxiway is not null,
            }
        );

    /// <summary>
    /// <see cref="FindAdjacentHoldShort(GroundNode, string?, TrueHeading, ExitPreference?, HashSet{int}?, HashSet{string}?)"/>
    /// for listing a runway's exits taxiway by taxiway: the preference narrows the search but is nobody's instruction, so
    /// a bar short of the holding distance with nothing beyond it is not listed.
    /// </summary>
    public (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? FindAdjacentHoldShortForListing(
        GroundNode centerlineNode,
        string runwayDesignator,
        TrueHeading runwayHeading,
        ExitPreference preference
    ) =>
        FindAdjacentHoldShort(
            new ExitSearch
            {
                Centerline = centerlineNode,
                RunwayDesignator = runwayDesignator,
                RunwayHeading = runwayHeading,
                Preference = preference,
                ExcludeHoldShortNodes = null,
                ForbiddenTaxiways = null,
                Instructed = false,
            }
        );

    private (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? FindAdjacentHoldShort(ExitSearch search)
    {
        const int maxDepth = 20;
        GroundNode centerlineNode = search.Centerline;
        ExitPreference? preference = search.Preference;

        // A named search seeds only its own taxiway's edges (SeedEdgesFromCluster): with none at the cluster there is nothing to search.
        if ((preference?.Taxiway is { } named) && !ClusterSeedTaxiways(centerlineNode).Contains(named))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("[ExitBFS] RESULT: no {Pref} edge at centerline #{Id}'s cluster", named, centerlineNode.Id);
            }

            return null;
        }

        var visited = new HashSet<int> { centerlineNode.Id };
        List<GroundNode> clusterNodes = ExpandCenterlineCluster(centerlineNode, visited);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[ExitBFS] Cluster from #{CL}: [{Nodes}] pref={PrefTwy}/{PrefSide} instructed={Instructed}",
                centerlineNode.Id,
                string.Join(",", clusterNodes.Select(n => n.Id)),
                preference?.Taxiway ?? "any",
                preference?.Side?.ToString() ?? "any",
                search.Instructed
            );
        }

        var queue = new Queue<(GroundNode Node, string Taxiway, List<GroundNode> Path, double TotalDist, int Depth)>();

        // Seed in two passes: arcs first, then straight edges. Fillet arcs are the
        // geometrically correct path through intersections — the straight edges
        // preserved by the fillet generator are shortcuts that skip the curve.
        // By seeding arcs first, they claim the visited set and straights to the
        // same node are skipped.
        SeedEdgesFromCluster(clusterNodes, visited, queue, search.RunwayHeading, preference, arcsOnly: true);
        SeedEdgesFromCluster(clusterNodes, visited, queue, search.RunwayHeading, preference, arcsOnly: false);

        var best = new ExitBfsBest();
        while (queue.Count > 0)
        {
            (GroundNode? current, string? branchTwy, List<GroundNode>? path, double totalDist, int depth) = queue.Dequeue();
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "[ExitBFS] dequeue #{Id} twy={Twy} depth={Depth} dist={Dist:F4} type={Type}",
                    current.Id,
                    branchTwy,
                    depth,
                    totalDist,
                    current.Type
                );
            }

            if ((current.Type == GroundNodeType.RunwayHoldShort) && !IsOtherRunwayBarLeftBehind(search, current, path))
            {
                if (ResolveBarCandidate(search, new ExitCandidate(current, branchTwy, path, totalDist)) is { } barCandidate)
                {
                    OfferExitCandidate(search, best, barCandidate);
                }

                continue;
            }

            if (IsBranchDeadEnd(current, branchTwy, path))
            {
                if (HopToJoiningBar(search, new ExitCandidate(current, branchTwy, path, totalDist)) is { } hop)
                {
                    OfferExitCandidate(search, best, hop);
                }

                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            EnqueueBranchSteps(queue, visited, (current, branchTwy, path, totalDist, depth));
        }

        if (best.Chosen is not { } exit)
        {
            Log.LogDebug("[ExitBFS] RESULT: no exit for centerline #{Id} pref={Pref}", centerlineNode.Id, preference?.Taxiway ?? "any");
            return null;
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[ExitBFS] RESULT: centerline #{CL} → HS #{HS} via {Twy} onSide={OnSide} actualSide={Side} path=[{Path}]",
                centerlineNode.Id,
                exit.Bar.Id,
                exit.Taxiway,
                best.OnSide is not null,
                best.ChosenSide,
                string.Join("→", exit.Path.Select(n => n.Id))
            );
        }
        return (exit.Bar, exit.Taxiway, exit.Path, best.ChosenSide);
    }

    /// <summary>
    /// The starting node plus every node reachable from it over short runway tangent-link edges, each added to
    /// <paramref name="visited"/>. Fillets create separate tangent nodes for each arc pair at the same intersection —
    /// e.g., #1293 connects to the south arc while #1289 connects to the north arc. Both are part of the same crossing and
    /// the BFS must see arcs from all of them.
    /// </summary>
    private static List<GroundNode> ExpandCenterlineCluster(GroundNode centerlineNode, HashSet<int> visited)
    {
        const double tangentLinkThresholdNm = 0.03;
        var clusterNodes = new List<GroundNode> { centerlineNode };
        for (int ci = 0; ci < clusterNodes.Count; ci++)
        {
            foreach (IGroundEdge edge in clusterNodes[ci].Edges)
            {
                if (!edge.IsRunwayCenterline)
                {
                    continue;
                }

                GroundNode neighbor = edge.OtherNode(clusterNodes[ci]);
                if ((edge.DistanceNm <= tangentLinkThresholdNm) && visited.Add(neighbor.Id))
                {
                    clusterNodes.Add(neighbor);
                }
            }
        }

        return clusterNodes;
    }

    /// <summary>The fixed inputs of one <see cref="FindAdjacentHoldShort"/> search.</summary>
    private sealed record ExitSearch
    {
        public required GroundNode Centerline { get; init; }
        public required string? RunwayDesignator { get; init; }
        public required TrueHeading RunwayHeading { get; init; }
        public required ExitPreference? Preference { get; init; }
        public required HashSet<int>? ExcludeHoldShortNodes { get; init; }
        public required HashSet<string>? ForbiddenTaxiways { get; init; }

        /// <summary>
        /// The controller named this exit. Set by the public entry points; a search listing a runway's exits is never
        /// instructed, whatever taxiway its preference names.
        /// </summary>
        public required bool Instructed { get; init; }
    }

    /// <summary>A bar the exit search can end at, with the branch taxiway that names the exit and the path to the bar.</summary>
    private readonly record struct ExitCandidate(GroundNode Bar, string Taxiway, List<GroundNode> Path, double TotalDistNm);

    /// <summary>
    /// The two best candidates of an exit search: on the requested side and off it. The on-side one wins; the off-side one
    /// is the fallback when no on-side exit exists (e.g., C3 at SFO).
    /// </summary>
    private sealed class ExitBfsBest
    {
        public ExitCandidate? OnSide { get; private set; }
        public ExitSide OnSideSide { get; private set; } = ExitSide.Right;
        public ExitCandidate? OffSide { get; private set; }
        public ExitSide OffSideSide { get; private set; } = ExitSide.Right;
        private double _onSideScore = double.MaxValue;
        private double _offSideScore = double.MaxValue;

        /// <summary>The on-side candidate, else the off-side fallback (for single-sided taxiways like C3).</summary>
        public ExitCandidate? Chosen => OnSide ?? OffSide;

        /// <summary>The side <see cref="Chosen"/> lies on.</summary>
        public ExitSide ChosenSide => (OnSide is not null) ? OnSideSide : OffSideSide;

        public bool IsNewBest(bool onRequestedSide, double score) => onRequestedSide ? (score < _onSideScore) : (score < _offSideScore);

        public void Take(ExitCandidate candidate, ExitSide side, bool onRequestedSide, double score)
        {
            if (onRequestedSide)
            {
                OnSide = candidate;
                OnSideSide = side;
                _onSideScore = score;
            }
            else
            {
                OffSide = candidate;
                OffSideSide = side;
                _offSideScore = score;
            }
        }
    }

    /// <summary>Queues the unvisited next steps along the branch taxiway from a BFS node.</summary>
    private static void EnqueueBranchSteps(
        Queue<(GroundNode Node, string Taxiway, List<GroundNode> Path, double TotalDist, int Depth)> queue,
        HashSet<int> visited,
        (GroundNode Node, string Taxiway, List<GroundNode> Path, double TotalDist, int Depth) at
    )
    {
        foreach (IGroundEdge edge in at.Node.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (!edge.MatchesTaxiway(at.Taxiway))
            {
                Log.LogDebug(
                    "[ExitBFS]   skip walk #{From}→#{To}: twy {Twy} != {Branch}",
                    at.Node.Id,
                    edge.OtherNode(at.Node).Id,
                    edge.TaxiwayName,
                    at.Taxiway
                );
                continue;
            }

            GroundNode next = edge.OtherNode(at.Node);
            if (!visited.Add(next.Id))
            {
                Log.LogDebug("[ExitBFS]   skip walk #{From}→#{To}: already visited", at.Node.Id, next.Id);
                continue;
            }

            var nextPath = new List<GroundNode>(at.Path) { next };
            queue.Enqueue((next, at.Taxiway, nextPath, at.TotalDist + edge.DistanceNm, at.Depth + 1));
            Log.LogDebug(
                "[ExitBFS]   walk #{From}→#{To} via {Twy} depth={Depth} type={Type}",
                at.Node.Id,
                next.Id,
                at.Taxiway,
                at.Depth + 1,
                next.Type
            );
        }
    }

    /// <summary>
    /// Scores a candidate and keeps it when it beats the best one on its side. The score is the path length plus a
    /// parking-proximity bias, a penalty for exits that go backward (more than 100° from the runway heading) and a bonus
    /// for high-speed exits (45° or less).
    /// </summary>
    private void OfferExitCandidate(ExitSearch search, ExitBfsBest best, ExitCandidate candidate)
    {
        if (IsOnAnotherRunway(search, candidate.Bar))
        {
            Log.LogDebug("[ExitBFS] HS #{Id} twy={Twy}: skip (on another runway)", candidate.Bar.Id, candidate.Taxiway);
            return;
        }

        // Negative relative bearing = Left, positive = Right.
        double absBearing = GeoMath.BearingTo(search.Centerline.Position, candidate.Bar.Position);
        double absRelative = search.RunwayHeading.SignedAngleTo(new TrueHeading(absBearing));
        ExitSide actualSide = (absRelative < 0) ? ExitSide.Left : ExitSide.Right;
        bool onRequestedSide = (search.Preference?.Side is not { } side) || (actualSide == side);

        double parkingBias = AverageNearestParkingDistanceNm(candidate.Bar, ParkingSampleCount) * ParkingProximityWeight;
        ExitAngleScore angle = ScoreExitAngle(search, candidate);
        double score = candidate.TotalDistNm + parkingBias + angle.Penalty - angle.Bonus;
        bool isNewBest = best.IsNewBest(onRequestedSide, score);
        Log.LogDebug(
            "[ExitBFS] HS #{Id} twy={Twy} angle={ExAngle:F0}° side={Side}: score={Score:F4} "
                + "(dist={Dist:F4} parking={Park:F4} anglePen={AngPen:F2} hsBonus={Hs:F2}){Result}",
            candidate.Bar.Id,
            candidate.Taxiway,
            angle.Angle ?? 0,
            onRequestedSide ? "ON" : "OFF",
            score,
            candidate.TotalDistNm,
            parkingBias,
            angle.Penalty,
            angle.Bonus,
            isNewBest ? " [NEW BEST]" : ""
        );
        if (isNewBest)
        {
            best.Take(candidate, actualSide, onRequestedSide, score);
        }
    }

    /// <summary>An exit's angle from the runway heading, with the score penalty and bonus it earns.</summary>
    private readonly record struct ExitAngleScore(double? Angle, double Penalty, double Bonus);

    /// <summary>
    /// Without the backward penalty (more than 100°) a short backward exit (E at 111° from node 230 at SFO) can outscore a
    /// longer forward exit (T at 19°) on distance alone. The high-speed bonus (45° or less) reflects the higher turn-off
    /// speed (30 kt vs 15 kt): T (19°, 0.11 nm) beats E (70°, 0.03 nm) at the same centerline node. A named exit earns
    /// neither.
    /// </summary>
    private ExitAngleScore ScoreExitAngle(ExitSearch search, ExitCandidate candidate)
    {
        double? exitAngle =
            ComputePathExitAngle(candidate.Path, candidate.Taxiway, search.RunwayHeading)
            ?? ComputeExitAngle(candidate.Bar, candidate.Taxiway, search.RunwayHeading);
        if ((exitAngle is not { } angle) || (search.Preference?.Taxiway is not null))
        {
            return new ExitAngleScore(exitAngle, 0, 0);
        }

        double penalty = (angle > 100) ? 10.0 : 0;
        double bonus = (angle <= 45.0) ? HighSpeedExitBonus : 0;
        return new ExitAngleScore(exitAngle, penalty, bonus);
    }

    /// <summary>
    /// Another runway's bar the branch reaches moving away from that runway (the step onto the bar ends farther from its
    /// centerline than it started): the branch has just left that runway's holding area, so the search carries on through
    /// the bar to the landing runway's own bar beyond it. COS B1 passes runway 13/31's bar on its way to the 17R/35L bar.
    /// </summary>
    private bool IsOtherRunwayBarLeftBehind(ExitSearch search, GroundNode bar, List<GroundNode> path)
    {
        if ((search.RunwayDesignator is not { } designator) || (bar.RunwayId is not { } rwyId) || rwyId.Contains(designator) || (path.Count < 2))
        {
            return false;
        }

        if (FindRunway(rwyId.End1) is not { } otherRunway)
        {
            return false;
        }

        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(otherRunway);
        double fromFt = DistanceFromCenterlineSegmentFt(rect, path[^2].Position);
        double barFt = DistanceFromCenterlineSegmentFt(rect, bar.Position);
        bool leftBehind = barFt > fromFt;
        Log.LogDebug(
            "[ExitBFS] HS #{Id} rwy={Rwy}: {From:F0} → {Bar:F0} ft from its centerline, {Verdict}",
            bar.Id,
            rwyId,
            fromFt,
            barFt,
            leftBehind ? "moving away, passing through" : "moving toward it"
        );
        return leftBehind;
    }

    /// <summary>Distance from a runway's centerline segment: cross-track beside the runway, to the nearer end beyond it.</summary>
    private static double DistanceFromCenterlineSegmentFt(in RunwayRectangle rect, LatLon position)
    {
        var start = new LatLon(rect.RefLat, rect.RefLon);
        double alongNm = GeoMath.AlongTrackDistanceNm(position, start, rect.TrueHeading);
        if (alongNm < 0)
        {
            return GeoMath.DistanceNm(position, start) * GeoMath.FeetPerNm;
        }

        if (alongNm > rect.LengthNm)
        {
            return GeoMath.DistanceNm(position, GeoMath.ProjectPoint(start, rect.TrueHeading, rect.LengthNm)) * GeoMath.FeetPerNm;
        }

        return Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, start, rect.TrueHeading)) * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// Whether an exit ending at <paramref name="bar"/> would stop on another runway: the bar lies on another runway's
    /// centerline or pavement. AIM 4-3-21.a: an aircraft never exits onto another runway without ATC authorization.
    /// </summary>
    private bool IsOnAnotherRunway(ExitSearch search, GroundNode bar)
    {
        if (bar.Edges.Any(edge => edge.IsRunwayCenterline))
        {
            return true;
        }

        return Runways.Any(runway =>
            ((search.RunwayDesignator is null) || !runway.Id.Contains(search.RunwayDesignator))
            && RunwayCrossingDetector.IsOnRunway(bar.Position, RunwayCrossingDetector.BuildRunwayRectangle(runway))
        );
    }

    /// <summary>
    /// A bar the BFS reached: rejected when it belongs to another runway, is occupied, or sits on a forbidden taxiway;
    /// otherwise resolved by <see cref="ResolveShortBar"/>.
    /// </summary>
    private ExitCandidate? ResolveBarCandidate(ExitSearch search, ExitCandidate candidate)
    {
        GroundNode bar = candidate.Bar;
        if ((search.RunwayDesignator is { } designator) && (bar.RunwayId is { } rwyId) && !rwyId.Contains(designator))
        {
            Log.LogDebug("[ExitBFS] HS #{Id} rwy={Rwy}: skip (wrong runway)", bar.Id, rwyId);
            return null;
        }

        if ((search.ExcludeHoldShortNodes is not null) && search.ExcludeHoldShortNodes.Contains(bar.Id))
        {
            Log.LogDebug("[ExitBFS] HS #{Id}: skip (occupied)", bar.Id);
            return null;
        }

        // Per-end noTurnoff from the airport file.
        if ((search.ForbiddenTaxiways is not null) && search.ForbiddenTaxiways.Contains(candidate.Taxiway))
        {
            Log.LogDebug("[ExitBFS] HS #{Id} twy={Twy}: skip (noTurnoff)", bar.Id, candidate.Taxiway);
            return null;
        }

        return ResolveShortBar(search, candidate);
    }

    /// <summary>
    /// How far short of a runway's holding distance a hold-short bar may sit and still count as at it; also how far beyond
    /// that distance the continuation from a branch end may reach.
    /// </summary>
    public const double HoldingDistanceToleranceFt = 5.0;

    /// <summary>Least outward progress, away from the runway centerline, each continuation step must make.</summary>
    private const double MinOutwardProgressFt = 1.0;

    /// <summary>
    /// A bar at the runway's holding distance is the candidate as it is. A bar inside it (a dead-end fallback bar with no
    /// bar of the runway beyond it; ATL keeps several) is an exit only when the controller named its taxiway: an
    /// uninstructed exit never stops inside the holding distance.
    /// </summary>
    private ExitCandidate? ResolveShortBar(ExitSearch search, ExitCandidate candidate)
    {
        if ((SearchRunway(search, candidate.Bar) is not { } runway) || IsAtHoldingDistance(runway.Rect, candidate.Bar))
        {
            return candidate;
        }

        Log.LogDebug(
            "[ExitBFS] HS #{HS} {Rwy}: {Bar:F0} ft from centerline, inside the {Holding:F0} ft holding distance; {Verdict}",
            candidate.Bar.Id,
            runway.Designator,
            CrossTrackFromCenterlineFt(runway.Rect, candidate.Bar),
            runway.Rect.HoldShortNm * GeoMath.FeetPerNm,
            search.Instructed ? "named exit, stops here" : "not an uninstructed exit"
        );
        return search.Instructed ? candidate : null;
    }

    /// <summary>
    /// A branch taxiway that ends inside the runway's holding distance at a junction without a bar of its own (its bar
    /// was dropped because the joining taxiway carries the runway's bar) turns onto the joining taxiway, always moving
    /// away from the centerline, and ends at that bar (AIM 4-3-21.b: absent instructions the pilot taxis beyond the runway
    /// holding position markings, even if that requires entering another taxiway). The exit keeps the branch's name. Null
    /// when the bar is occupied or out of reach.
    /// </summary>
    private ExitCandidate? HopToJoiningBar(ExitSearch search, ExitCandidate deadEnd)
    {
        if ((search.ForbiddenTaxiways is not null) && search.ForbiddenTaxiways.Contains(deadEnd.Taxiway))
        {
            return null;
        }

        if ((SearchRunway(search, deadEnd.Bar) is not { } runway) || IsAtHoldingDistance(runway.Rect, deadEnd.Bar))
        {
            return null;
        }

        OutwardWalk walk = BuildOutwardWalk(runway, search);
        if (WalkOutwardToHoldShort(deadEnd.Bar, walk) is not { } continuation)
        {
            Log.LogDebug(
                "[ExitBFS] {Twy} ends at #{Node} {Ft:F0} ft from the {Rwy} centerline; no free bar at the holding distance beyond it",
                deadEnd.Taxiway,
                deadEnd.Bar.Id,
                CrossTrackFromCenterlineFt(walk.Rect, deadEnd.Bar),
                walk.Designator
            );
            return null;
        }

        GroundNode target = continuation.Steps[^1];
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[ExitBFS] {Twy} ends at #{From} {FromFt:F0} ft from the {Rwy} centerline; "
                    + "continuing to HS #{Target} at {TargetFt:F0} ft via [{Steps}]",
                deadEnd.Taxiway,
                deadEnd.Bar.Id,
                CrossTrackFromCenterlineFt(walk.Rect, deadEnd.Bar),
                walk.Designator,
                target.Id,
                CrossTrackFromCenterlineFt(walk.Rect, target),
                string.Join("→", continuation.Steps.Select(n => n.Id))
            );
        }
        return new ExitCandidate(
            target,
            deadEnd.Taxiway,
            [.. deadEnd.Path, .. continuation.Steps],
            deadEnd.TotalDistNm + (continuation.LengthFt / GeoMath.FeetPerNm)
        );
    }

    /// <summary>
    /// The branch taxiway ends at <paramref name="node"/>: the BFS arrived on one of the branch's own edges (not a corner arc
    /// onto another taxiway), no other edge of the branch leaves it, and another taxiway does.
    /// </summary>
    private static bool IsBranchDeadEnd(GroundNode node, string branchTwy, List<GroundNode> path)
    {
        if (path.Count < 2)
        {
            return false;
        }

        int fromId = path[^2].Id;
        bool arrivedOnBranch = node.Edges.Any(e =>
            (e.OtherNode(node).Id == fromId) && e.MatchesTaxiway(branchTwy) && (e is not GroundArc { TaxiwayNames.Length: > 1 })
        );
        if (!arrivedOnBranch)
        {
            return false;
        }

        bool branchContinues = node.Edges.Any(e => !e.IsRunwayCenterline && (e.OtherNode(node).Id != fromId) && e.MatchesTaxiway(branchTwy));
        return !branchContinues && node.Edges.Any(e => !e.IsRunwayCenterline && !e.MatchesTaxiway(branchTwy));
    }

    /// <summary>A runway the exit search measures against: its designator and rectangle.</summary>
    private readonly record struct SearchedRunway(string Designator, RunwayRectangle Rect);

    /// <summary>
    /// The runway the search measures <paramref name="node"/> against: the search's runway, else the one the node is a
    /// bar of. Null when neither names a runway in the layout.
    /// </summary>
    private SearchedRunway? SearchRunway(ExitSearch search, GroundNode node)
    {
        string? designator = search.RunwayDesignator ?? node.RunwayId?.End1;
        if ((designator is null) || (FindRunway(designator) is not { } runway))
        {
            return null;
        }

        return new SearchedRunway(designator, RunwayCrossingDetector.BuildRunwayRectangle(runway));
    }

    /// <summary>The outward walk to <paramref name="runway"/>'s bar at the holding distance, bounded by the search's exclusions.</summary>
    private OutwardWalk BuildOutwardWalk(SearchedRunway runway, ExitSearch search)
    {
        IReadOnlySet<(int From, int To)> forbiddenMoves = NavigationDatabase.InstanceOrNull is null
            ? new HashSet<(int From, int To)>()
            : OneWayResolver.GetForbiddenMoves(this, OneWayResolver.AircraftlessWakeClass);
        return new OutwardWalk(
            runway.Rect,
            runway.Designator,
            runway.Rect.HoldShortNm * GeoMath.FeetPerNm,
            search.ExcludeHoldShortNodes,
            search.ForbiddenTaxiways,
            forbiddenMoves
        );
    }

    /// <summary>
    /// Whether <paramref name="bar"/> sits at the holding distance of <paramref name="runwayDesignator"/> (within
    /// <see cref="HoldingDistanceToleranceFt"/>). True when the runway is unknown, since there is nothing to measure.
    /// </summary>
    public bool IsAtRunwayHoldingDistance(GroundNode bar, string runwayDesignator) =>
        (FindRunway(runwayDesignator) is not { } runway) || IsAtHoldingDistance(RunwayCrossingDetector.BuildRunwayRectangle(runway), bar);

    private static bool IsAtHoldingDistance(in RunwayRectangle rect, GroundNode node) =>
        CrossTrackFromCenterlineFt(rect, node) >= (rect.HoldShortNm * GeoMath.FeetPerNm) - HoldingDistanceToleranceFt;

    /// <summary>The nodes a continuation adds after its start, ending at the bar, and its length.</summary>
    private readonly record struct OutwardContinuation(List<GroundNode> Steps, double LengthFt);

    /// <summary>What bounds the continuation from a branch end: the runway, its holding distance and the exclusions.</summary>
    private readonly record struct OutwardWalk(
        RunwayRectangle Rect,
        string Designator,
        double HoldingFt,
        HashSet<int>? ExcludeHoldShortNodes,
        HashSet<string>? ForbiddenTaxiways,
        IReadOnlySet<(int From, int To)> ForbiddenMoves
    );

    /// <summary>
    /// Shortest-path search from <paramref name="start"/> over outward steps (<see cref="TryOutwardStep"/>), no longer in
    /// total than the holding distance, to a free bar of the runway at the holding distance; equal lengths go to the lower
    /// node id. Returns the nodes after <paramref name="start"/>, ending at that bar, with their length, or null when none
    /// is within reach.
    /// </summary>
    private static OutwardContinuation? WalkOutwardToHoldShort(GroundNode start, OutwardWalk walk)
    {
        var bestFt = new Dictionary<int, double> { [start.Id] = 0 };
        var previous = new Dictionary<int, GroundNode>();
        var targets = new HashSet<int>();
        var queue = new PriorityQueue<GroundNode, (double DistFt, int NodeId)>();
        queue.Enqueue(start, (0, start.Id));
        while (queue.TryDequeue(out GroundNode? node, out (double DistFt, int NodeId) priority))
        {
            if (priority.DistFt > bestFt[node.Id])
            {
                continue;
            }

            if (targets.Contains(node.Id))
            {
                return new OutwardContinuation(TracePath(start, node, previous), priority.DistFt);
            }

            double crossFt = CrossTrackFromCenterlineFt(walk.Rect, node);
            foreach (IGroundEdge edge in node.Edges)
            {
                if (!TryOutwardStep(node, edge, crossFt, walk, out GroundNode next, out bool isTarget))
                {
                    continue;
                }

                double distFt = priority.DistFt + (edge.DistanceNm * GeoMath.FeetPerNm);
                if ((distFt > walk.HoldingFt) || (bestFt.TryGetValue(next.Id, out double knownFt) && (knownFt <= distFt)))
                {
                    continue;
                }

                bestFt[next.Id] = distFt;
                previous[next.Id] = node;
                if (isTarget)
                {
                    targets.Add(next.Id);
                }

                queue.Enqueue(next, (distFt, next.Id));
            }
        }

        return null;
    }

    private static List<GroundNode> TracePath(GroundNode start, GroundNode end, Dictionary<int, GroundNode> previous)
    {
        var steps = new List<GroundNode>();
        for (GroundNode node = end; node.Id != start.Id; node = previous[node.Id])
        {
            steps.Add(node);
        }

        steps.Reverse();
        return steps;
    }

    /// <summary>
    /// One continuation step: off the centerline, not forbidden, at least <see cref="MinOutwardProgressFt"/> farther from
    /// the centerline and no more than <see cref="HoldingDistanceToleranceFt"/> beyond the holding distance, and not onto
    /// another runway's bar or an occupied bar. <paramref name="isTarget"/> is set when the step reaches the bar sought.
    /// </summary>
    private static bool TryOutwardStep(GroundNode node, IGroundEdge edge, double crossFt, OutwardWalk walk, out GroundNode next, out bool isTarget)
    {
        next = edge.OtherNode(node);
        isTarget = false;
        if (IsForbiddenOutwardEdge(node, next, edge, walk))
        {
            return false;
        }

        double nextFt = CrossTrackFromCenterlineFt(walk.Rect, next);
        if ((nextFt < crossFt + MinOutwardProgressFt) || (nextFt > walk.HoldingFt + HoldingDistanceToleranceFt))
        {
            return false;
        }

        OutwardStep step = ClassifyOutwardStep(next, nextFt, walk);
        isTarget = step == OutwardStep.Target;
        return step != OutwardStep.Blocked;
    }

    private static bool IsForbiddenOutwardEdge(GroundNode node, GroundNode next, IGroundEdge edge, OutwardWalk walk)
    {
        if (edge.IsRunwayCenterline || walk.ForbiddenMoves.Contains((node.Id, next.Id)))
        {
            return true;
        }

        return (walk.ForbiddenTaxiways is not null) && walk.ForbiddenTaxiways.Any(edge.MatchesTaxiway);
    }

    private enum OutwardStep
    {
        PassThrough,
        Blocked,
        Target,
    }

    /// <summary>
    /// A non-bar node or a short bar of the same runway is passed through; the same runway's free bar at the holding
    /// distance is the target; another runway's bar or an occupied bar ends the branch.
    /// </summary>
    private static OutwardStep ClassifyOutwardStep(GroundNode node, double crossFt, OutwardWalk walk)
    {
        if (node.Type != GroundNodeType.RunwayHoldShort)
        {
            return OutwardStep.PassThrough;
        }

        bool sameRunway = (node.RunwayId is { } rwyId) && rwyId.Contains(walk.Designator);
        bool excluded = (walk.ExcludeHoldShortNodes is not null) && walk.ExcludeHoldShortNodes.Contains(node.Id);
        if ((!sameRunway) || excluded)
        {
            return OutwardStep.Blocked;
        }

        return (crossFt >= walk.HoldingFt - HoldingDistanceToleranceFt) ? OutwardStep.Target : OutwardStep.PassThrough;
    }

    private static double CrossTrackFromCenterlineFt(in RunwayRectangle rect, GroundNode node) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(node.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading)) * GeoMath.FeetPerNm;

    /// <summary>
    /// From a landing-runway exit hold-short, find an adjacent parallel runway to cross after
    /// vacating. Walks the same exit taxiway forward (away from <paramref name="comeFromNode"/>)
    /// to the parallel runway's near-side hold-short, then continues across that runway to its
    /// far-side hold-short. Returns null when there is no parallel runway ahead, when an
    /// intervening taxiway intersection is reached before the parallel hold-short (the controller
    /// may want to route the aircraft down that taxiway), or when the next runway is not
    /// (anti-)parallel to the landing runway.
    /// </summary>
    /// <param name="landingHoldShortNode">The hold-short the aircraft stopped at after vacating.</param>
    /// <param name="comeFromNode">The node the aircraft arrived from (defines "forward").</param>
    /// <param name="exitTaxiwayName">The taxiway the aircraft exited on.</param>
    /// <param name="landingRunwayDesignator">The runway just landed on (e.g. "28L").</param>
    public (
        GroundNode NearHoldShort,
        GroundNode FarHoldShort,
        string ParallelRunwayId,
        List<GroundNode> PullUpPath,
        List<GroundNode> CrossingPath
    )? FindParallelRunwayCrossing(GroundNode landingHoldShortNode, GroundNode comeFromNode, string exitTaxiwayName, string landingRunwayDesignator)
    {
        List<GroundNode>? pullUpPath = WalkToParallelNearHoldShort(landingHoldShortNode, comeFromNode.Id, exitTaxiwayName, landingRunwayDesignator);
        if (pullUpPath is null)
        {
            return null;
        }

        GroundNode nearHoldShort = pullUpPath[^1];
        if (nearHoldShort.RunwayId is not { } nearRunwayId || !IsParallelRunway(landingRunwayDesignator, nearRunwayId))
        {
            return null;
        }

        List<GroundNode>? crossingPath = WalkAcrossToFarHoldShort(nearHoldShort, pullUpPath[^2].Id, exitTaxiwayName, nearRunwayId);
        if (crossingPath is null)
        {
            return null;
        }

        Log.LogDebug(
            "[ParallelXing] {Land} HS #{Near} → cross {Rwy} → HS #{Far} via {Twy} pullUp=[{Pull}] crossing=[{Cross}]",
            landingRunwayDesignator,
            nearHoldShort.Id,
            nearRunwayId,
            crossingPath[^1].Id,
            exitTaxiwayName,
            string.Join("→", pullUpPath.Select(n => n.Id)),
            string.Join("→", crossingPath.Select(n => n.Id))
        );

        return (nearHoldShort, crossingPath[^1], nearRunwayId.ToString(), pullUpPath, crossingPath);
    }

    /// <summary>
    /// Walk the exit taxiway forward from the landing-runway hold-short to the first hold-short of
    /// a different runway (the parallel near side). Returns the path [landingHS, …, nearHS] or null
    /// at a dead end, an intervening taxiway intersection, or another landing-runway hold-short.
    /// </summary>
    private List<GroundNode>? WalkToParallelNearHoldShort(
        GroundNode landingHoldShort,
        int comeFromId,
        string exitTaxiwayName,
        string landingRunwayDesignator
    )
    {
        const int maxHops = 10;
        var path = new List<GroundNode> { landingHoldShort };
        GroundNode current = landingHoldShort;
        int prevId = comeFromId;

        for (int hop = 0; hop < maxHops; hop++)
        {
            GroundNode? next = StepForwardOnTaxiway(current, prevId, exitTaxiwayName);
            if (next is null)
            {
                return null;
            }

            path.Add(next);

            if (next.Type == GroundNodeType.RunwayHoldShort && next.RunwayId is { } rid)
            {
                // Another hold-short of the runway we just left is not a parallel crossing.
                return rid.Contains(landingRunwayDesignator) ? null : path;
            }

            if (HasForeignTaxiwayBranch(next, exitTaxiwayName))
            {
                return null;
            }

            prevId = current.Id;
            current = next;
        }

        return null;
    }

    /// <summary>
    /// Walk the exit taxiway forward from the parallel runway's near-side hold-short across the
    /// runway to its far-side hold-short (same <see cref="RunwayIdentifier"/>, different node).
    /// Returns the path [nearHS, …, farHS] or null.
    /// </summary>
    private List<GroundNode>? WalkAcrossToFarHoldShort(
        GroundNode nearHoldShort,
        int comeFromId,
        string exitTaxiwayName,
        RunwayIdentifier parallelRunway
    )
    {
        const int maxHops = 10;
        var path = new List<GroundNode> { nearHoldShort };
        GroundNode current = nearHoldShort;
        int prevId = comeFromId;

        for (int hop = 0; hop < maxHops; hop++)
        {
            GroundNode? next = StepForwardOnTaxiway(current, prevId, exitTaxiwayName);
            if (next is null)
            {
                return null;
            }

            path.Add(next);

            if (next.Type == GroundNodeType.RunwayHoldShort && next.RunwayId is { } rid && rid.Equals(parallelRunway) && next.Id != nearHoldShort.Id)
            {
                return path;
            }

            prevId = current.Id;
            current = next;
        }

        return null;
    }

    /// <summary>
    /// Step to the single same-taxiway node ahead of <paramref name="current"/>, excluding the
    /// node we came from (<paramref name="prevId"/>). Straight edges are preferred over fillet
    /// arcs (the arcs are corner cuts onto the runway; the straight edge is the through-line).
    /// Returns null at a dead end (no forward edge) or an ambiguous fork (more than one).
    /// </summary>
    private static GroundNode? StepForwardOnTaxiway(GroundNode current, int prevId, string taxiwayName)
    {
        GroundNode? straight = null;
        int straightCount = 0;
        GroundNode? arc = null;
        int arcCount = 0;

        foreach (IGroundEdge edge in current.Edges)
        {
            if (edge.IsRunwayCenterline || !edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            GroundNode other = edge.OtherNode(current);
            if (other.Id == prevId)
            {
                continue;
            }

            if (edge is GroundArc)
            {
                arc = other;
                arcCount++;
            }
            else
            {
                straight = other;
                straightCount++;
            }
        }

        if (straightCount == 1)
        {
            return straight;
        }

        if (straightCount == 0 && arcCount == 1)
        {
            return arc;
        }

        return null;
    }

    /// <summary>
    /// True if <paramref name="node"/> has an incident edge belonging to a different taxiway — a
    /// junction where another taxiway crosses or joins. Runway centerline edges and runway-crossing
    /// junction arcs (which continue the taxiway across a runway) do not count.
    /// </summary>
    private static bool HasForeignTaxiwayBranch(GroundNode node, string taxiwayName)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRunwayCenterline || edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            if (edge is GroundArc { IsRunwayJunction: true })
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// True if a runway end of <paramref name="candidate"/> is (anti-)parallel to
    /// <paramref name="landingRunwayDesignator"/> — same magnetic orientation within 20°.
    /// </summary>
    private static bool IsParallelRunway(string landingRunwayDesignator, RunwayIdentifier candidate)
    {
        if (RunwayDesignatorHeading(landingRunwayDesignator) is not { } landingHeading)
        {
            return false;
        }

        return IsParallelHeading(landingHeading, RunwayDesignatorHeading(candidate.End1))
            || IsParallelHeading(landingHeading, RunwayDesignatorHeading(candidate.End2));
    }

    private static bool IsParallelHeading(double landingHeading, double? candidateHeading)
    {
        if (candidateHeading is not { } heading)
        {
            return false;
        }

        const double toleranceDeg = 20.0;
        double diff = Math.Abs(landingHeading - heading) % 360.0;
        return (diff <= toleranceDeg) || (Math.Abs(diff - 180.0) <= toleranceDeg) || (diff >= 360.0 - toleranceDeg);
    }

    /// <summary>
    /// Magnetic heading (degrees) implied by a runway designator's leading number (e.g. "28L" → 280).
    /// Returns null when no leading digits are present.
    /// </summary>
    private static double? RunwayDesignatorHeading(string designator)
    {
        int numLen = 0;
        while (numLen < designator.Length && char.IsAsciiDigit(designator[numLen]))
        {
            numLen++;
        }

        if (numLen == 0)
        {
            return null;
        }

        return (int.Parse(designator[..numLen]) % 36) * 10.0;
    }

    /// <summary>
    /// Seed the BFS queue from cluster nodes. When <paramref name="arcsOnly"/> is true,
    /// only arc edges are seeded; when false, only straight edges. Called in two passes
    /// (arcs first) so arcs claim the visited set before straight shortcuts can.
    /// </summary>
    private void SeedEdgesFromCluster(
        List<GroundNode> clusterNodes,
        HashSet<int> visited,
        Queue<(GroundNode Node, string Taxiway, List<GroundNode> Path, double TotalDist, int Depth)> queue,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        bool arcsOnly
    )
    {
        foreach (GroundNode clusterNode in clusterNodes)
        {
            foreach (IGroundEdge edge in clusterNode.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }

                bool isArc = edge is GroundArc;
                if (arcsOnly != isArc)
                {
                    continue;
                }

                GroundNode neighbor = edge.OtherNode(clusterNode);

                if (visited.Contains(neighbor.Id))
                {
                    LogSeedSkip("already visited", clusterNode, neighbor, edge);
                    continue;
                }

                // A named search seeds only its own taxiway: checked before the arc geometry, which it makes moot.
                if (preference?.Taxiway is { } prefTwy && !edge.MatchesTaxiway(prefTwy))
                {
                    LogSeedSkip("taxiway doesn't match the preference", clusterNode, neighbor, edge);
                    continue;
                }

                if (edge is GroundArc arc)
                {
                    double departureBearing = arc.TangentBearingAt(clusterNode, clusterNode);
                    double bearingDiff = runwayHeading.AbsAngleTo(new TrueHeading(departureBearing));
                    if (bearingDiff > 95)
                    {
                        if (Log.IsEnabled(LogLevel.Debug))
                        {
                            Log.LogDebug(
                                "[ExitBFS]   skip arc #{From}->{To} via {Twy}: departure {Dep:F1} diff={Diff:F1} > 95",
                                clusterNode.Id,
                                neighbor.Id,
                                edge.TaxiwayName,
                                departureBearing,
                                bearingDiff
                            );
                        }

                        continue;
                    }

                    // A reverse-corner arc can be tangent to the centerline at its entry (so the
                    // departure check passes) yet sweep around to point backward — when the exit
                    // corner and the reverse corner share one fused tangent node, both arcs depart
                    // along the runway. Check the arrival tangent too: an arc that leaves the
                    // aircraft pointing >95° off the runway heading is a doubled-back turn, not an
                    // exit. The hold-short beyond it stays reachable through the preserved straight
                    // edges (seeded in the second pass) and scores as the back-exit it is.
                    double arrivalBearing = arc.TangentBearingAt(neighbor, clusterNode);
                    double arrivalDiff = runwayHeading.AbsAngleTo(new TrueHeading(arrivalBearing));
                    if (arrivalDiff > 95)
                    {
                        if (Log.IsEnabled(LogLevel.Debug))
                        {
                            Log.LogDebug(
                                "[ExitBFS]   skip arc #{From}->{To} via {Twy}: arrival {Arr:F1} diff={Diff:F1} > 95",
                                clusterNode.Id,
                                neighbor.Id,
                                edge.TaxiwayName,
                                arrivalBearing,
                                arrivalDiff
                            );
                        }

                        continue;
                    }

                    if (Log.IsEnabled(LogLevel.Debug))
                    {
                        Log.LogDebug(
                            "[ExitBFS]   seed arc #{From}->{To} via {Twy}: departure {Dep:F1} diff={Diff:F1}",
                            clusterNode.Id,
                            neighbor.Id,
                            edge.TaxiwayName,
                            departureBearing,
                            bearingDiff
                        );
                    }
                }
                else if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.LogDebug("[ExitBFS]   seed edge #{From}->{To} via {Twy}", clusterNode.Id, neighbor.Id, edge.TaxiwayName);
                }

                string branchName = edge is GroundArc { IsRunwayJunction: true } ja ? ja.FirstNonRunwayName() : edge.TaxiwayName;
                visited.Add(neighbor.Id);
                queue.Enqueue((neighbor, branchName, [clusterNode, neighbor], edge.DistanceNm, 1));
            }
        }
    }

    /// <summary>Logs a cluster edge the exit search does not seed, building the line only when debug logging is on.</summary>
    private static void LogSeedSkip(string reason, GroundNode clusterNode, GroundNode neighbor, IGroundEdge edge)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("[ExitBFS]   skip #{From}->{To} via {Twy}: {Reason}", clusterNode.Id, neighbor.Id, edge.TaxiwayName, reason);
        }
    }

    /// <summary>
    /// Walk backward from a hold-short node through same-name taxiway edges
    /// until reaching a runway centerline node. Returns the ordered path
    /// [branch-point, intermediates..., hold-short]. Returns null if no path
    /// to the centerline is found.
    /// </summary>
    public List<GroundNode>? FindExitPath(GroundNode holdShortNode, string taxiwayName)
    {
        const int maxDepth = 15;
        var visited = new HashSet<int> { holdShortNode.Id };
        var queue = new Queue<(GroundNode Node, List<GroundNode> Path)>();
        queue.Enqueue((holdShortNode, [holdShortNode]));

        while (queue.Count > 0)
        {
            (GroundNode? current, List<GroundNode>? path) = queue.Dequeue();
            if (path.Count > maxDepth)
            {
                continue;
            }

            foreach (IGroundEdge edge in current.Edges)
            {
                if (!edge.MatchesTaxiway(taxiwayName))
                {
                    continue;
                }

                GroundNode neighbor = edge.OtherNode(current);
                if (!visited.Add(neighbor.Id))
                {
                    continue;
                }

                var nextPath = new List<GroundNode>(path) { neighbor };

                bool onCenterline = false;
                foreach (IGroundEdge nEdge in neighbor.Edges)
                {
                    if (nEdge.IsRunwayCenterline)
                    {
                        onCenterline = true;
                        break;
                    }
                }

                if (onCenterline)
                {
                    nextPath.Reverse();
                    return nextPath;
                }

                queue.Enqueue((neighbor, nextPath));
            }
        }

        return null;
    }

    /// <summary>
    /// Find the nearest ahead hold-short node for the given runway, measured by
    /// along-track distance. Used by LandingPhase for exit-aware braking.
    /// </summary>
    public GroundNode? FindNearestHoldShortAhead(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        string runwayDesignator,
        ExitPreference? preference
    )
    {
        GroundNode? best = null;
        double bestAlongTrack = double.MaxValue;

        foreach (GroundNode node in GetRunwayHoldShortNodes(runwayDesignator))
        {
            double alongTrack = GeoMath.AlongTrackDistanceNm(node.Position, new LatLon(lat, lon), runwayHeading);
            if (alongTrack <= 0)
            {
                continue;
            }

            if (preference?.Side is { } side)
            {
                double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
                double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));
                bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
                if (!isOnRequestedSide)
                {
                    continue;
                }
            }

            if (preference?.Taxiway is { } taxiway)
            {
                bool hasMatchingEdge = false;
                foreach (IGroundEdge edge in node.Edges)
                {
                    if (!edge.IsRunwayCenterline && edge.MatchesTaxiway(taxiway))
                    {
                        hasMatchingEdge = true;
                        break;
                    }
                }

                if (!hasMatchingEdge)
                {
                    continue;
                }
            }

            // Score by along-track distance + parking proximity bias
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = alongTrack + parkingBias;
            if (score < bestAlongTrack)
            {
                bestAlongTrack = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find a GroundRunway where either end matches the given designator (e.g., "28L").
    /// GroundRunway.Name format: "10R/28L".
    /// </summary>
    public GroundRunway? FindGroundRunway(string designator)
    {
        foreach (GroundRunway rwy in Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);
            if (id.Contains(designator))
            {
                return rwy;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the nearest taxiway node suitable as a runway exit, considering aircraft heading.
    /// Prefers exits that don't require turns greater than 90 degrees.
    /// When <paramref name="runwayDesignator"/> is provided, filters out exits that are closer
    /// to a different parallel runway's centerline.
    /// </summary>
    public GroundNode? FindNearestExit(double lat, double lon, TrueHeading runwayHeading, string? runwayDesignator, double maxSearchNm = 0.5)
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (GroundNode node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
            double turnAngle = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0) + parkingBias;

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find the nearest exit on the specified side of the runway heading.
    /// Falls back to FindNearestExit if no exits match the requested side.
    /// </summary>
    public GroundNode? FindExitBySide(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        ExitSide side,
        string? runwayDesignator,
        double maxSearchNm = 0.5
    )
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (GroundNode node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
            double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));

            // Left = negative relative angle, Right = positive
            bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
            if (!isOnRequestedSide)
            {
                continue;
            }

            double turnAngle = Math.Abs(relative);
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0) + parkingBias;

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        // Fall back to nearest exit if none found on the requested side
        return best ?? FindNearestExit(lat, lon, runwayHeading, runwayDesignator, maxSearchNm);
    }

    /// <summary>
    /// Find an exit node connected to the named taxiway.
    /// Uses a wider search radius since the taxiway might be further ahead.
    /// </summary>
    public GroundNode? FindExitByTaxiway(double lat, double lon, string taxiwayName, double maxSearchNm = 1.0)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
            {
                continue;
            }

            // Skip nodes that sit on the runway surface — they're not valid exit points
            if (HasRunwayCenterlineEdge(node))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            // Only count straight GroundEdges, not GroundArcs. A node connected to the
            // requested taxiway only via fillet arcs (e.g. the curved entry from a RAMP
            // into T5B) is not a useful pushback target — the aircraft center would
            // stop on the curve instead of on the taxiway proper. Issue #162.
            bool hasMatchingEdge = false;
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge is GroundEdge straight && !straight.IsRunwayCenterline && straight.MatchesTaxiway(taxiwayName))
                {
                    hasMatchingEdge = true;
                    break;
                }
            }

            if (!hasMatchingEdge)
            {
                continue;
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Get the heading along the named taxiway at the given node, choosing the
    /// direction closest to <paramref name="preferredBearing"/>. When every edge of the taxiway at the node
    /// points away from the preference (the node is the taxiway's end), the taxiway's line extended past the
    /// end is the answer: an aircraft lined up there can face either way along the line.
    /// Returns null if no matching taxiway edge exists at the node.
    /// </summary>
    public double? GetEdgeBearingForTaxiway(GroundNode node, string taxiwayName, double preferredBearing)
    {
        if (GetNearestEdgeBearingForTaxiway(node, taxiwayName, preferredBearing) is not { } bearing)
        {
            return null;
        }

        if (GeoMath.AbsBearingDifference(bearing, preferredBearing) < 90.0)
        {
            return bearing;
        }

        return new TrueHeading(bearing + 180.0).Degrees;
    }

    private static double? GetNearestEdgeBearingForTaxiway(GroundNode node, string taxiwayName, double preferredBearing)
    {
        double? bestBearing = null;
        double bestDiff = double.MaxValue;

        foreach (IGroundEdge edge in node.Edges)
        {
            // Skip arcs — only straight GroundEdges define a meaningful taxiway bearing.
            // An arc's chord bearing is not the taxiway's direction. Issue #162.
            if (edge is not GroundEdge straight)
            {
                continue;
            }

            if (straight.IsRunwayCenterline)
            {
                continue;
            }

            if (!straight.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            GroundNode otherNode = straight.OtherNode(node);

            double bearing = GeoMath.BearingTo(node.Position, otherNode.Position);
            double diff = GeoMath.AbsBearingDifference(bearing, preferredBearing);

            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestBearing = bearing;
            }
        }

        return bestBearing;
    }

    /// <summary>
    /// Computes the outbound heading of a ramp spot — the direction along its sub-lane toward the parent
    /// movement-area taxiway (away from the ramp interior). A departure pushed onto a spot faces this way,
    /// ready to taxi out. Of the spot's two sub-lane neighbours, the outbound one reaches a movement-area
    /// taxiway (an edge naming a taxiway that is not the sub-lane, not RAMP, and not a runway) in the fewest
    /// hops; the other dead-ends in the ramp. Returns false if neither side reaches a movement area.
    /// Used by <c>PUSH $spot</c> (see docs/ground/pushback.md).
    /// </summary>
    public bool TryGetSpotOutboundHeading(GroundNode spot, out double bearing)
    {
        (double Bearing, string Taxiway)? outbound = FindSpotOutbound(spot);
        bearing = outbound?.Bearing ?? 0;
        return outbound is not null;
    }

    /// <summary>
    /// The movement-area taxiway a ramp spot's outbound side leads to — the taxiway whose first reachable edge decides
    /// <see cref="TryGetSpotOutboundHeading"/>: the nose-out spot faces toward it. Returns false, with an empty name, if
    /// neither side of the spot reaches a movement area.
    /// </summary>
    /// <param name="spot">The spot node.</param>
    /// <param name="taxiway">The taxiway's name, as its edge carries it.</param>
    /// <returns>Whether the spot has an outbound side.</returns>
    public bool TryGetSpotOutboundTaxiway(GroundNode spot, out string taxiway)
    {
        (double Bearing, string Taxiway)? outbound = FindSpotOutbound(spot);
        taxiway = outbound?.Taxiway ?? string.Empty;
        return outbound is not null;
    }

    /// <summary>
    /// The outbound side of a ramp spot: the bearing to the sub-lane neighbour that reaches a movement-area taxiway in
    /// the fewest hops, and that taxiway's name; null when neither side reaches one.
    /// </summary>
    private static (double Bearing, string Taxiway)? FindSpotOutbound(GroundNode spot)
    {
        var subLaneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGroundEdge edge in spot.Edges)
        {
            if (edge is GroundEdge straight && !straight.IsRunwayCenterline && !straight.IsRamp)
            {
                subLaneNames.Add(straight.TaxiwayName);
            }
        }

        (double Bearing, string Taxiway)? best = null;
        int bestHops = int.MaxValue;
        foreach (IGroundEdge edge in spot.Edges)
        {
            if (edge is not GroundEdge first || first.IsRunwayCenterline || first.IsRamp)
            {
                continue;
            }

            if ((HopsToMovementArea(first.OtherNode(spot), spot, subLaneNames) is { } reached) && (reached.Hops < bestHops))
            {
                bestHops = reached.Hops;
                best = (GeoMath.BearingTo(spot.Position, first.OtherNode(spot).Position), reached.Taxiway);
            }
        }

        return best;
    }

    private const int SpotOutboundMaxHops = 8;

    /// <summary>
    /// BFS from <paramref name="start"/> (never crossing back through <paramref name="blocked"/>, the spot)
    /// along non-runway edges; returns the hop count at which an edge naming a movement-area taxiway is
    /// first seen (not a sub-lane name, not RAMP) with that taxiway's name, or null if none within
    /// <see cref="SpotOutboundMaxHops"/>.
    /// </summary>
    private static (int Hops, string Taxiway)? HopsToMovementArea(GroundNode start, GroundNode blocked, HashSet<string> subLaneNames)
    {
        var visited = new HashSet<int> { blocked.Id, start.Id };
        var frontier = new Queue<(GroundNode Node, int Depth)>();
        frontier.Enqueue((start, 0));

        while (frontier.Count > 0)
        {
            (GroundNode? node, int depth) = frontier.Dequeue();
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }
                if (MovementAreaTaxiwayName(edge, subLaneNames) is { } taxiway)
                {
                    return (depth, taxiway);
                }
                if (depth + 1 > SpotOutboundMaxHops)
                {
                    continue;
                }
                GroundNode other = edge.OtherNode(node);
                if (visited.Add(other.Id))
                {
                    frontier.Enqueue((other, depth + 1));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The movement-area taxiway the edge names — a taxiway that is neither one of the spot's sub-lane names, nor RAMP
    /// (nonmovement), nor a runway — or null when it names none.
    /// </summary>
    private static string? MovementAreaTaxiwayName(IGroundEdge edge, HashSet<string> subLaneNames)
    {
        if (edge.IsRunwayCenterline)
        {
            return null;
        }

        string[] names = edge is GroundArc arc ? arc.TaxiwayNames : [((GroundEdge)edge).TaxiwayName];
        foreach (string name in names)
        {
            if (string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (subLaneNames.Contains(name))
            {
                continue;
            }
            return name;
        }

        return null;
    }

    /// <summary>
    /// Get the taxiway name for the edge connected to a node that leads away from the runway.
    /// </summary>
    public string? GetExitTaxiwayName(GroundNode exitNode)
    {
        foreach (IGroundEdge edge in exitNode.Edges)
        {
            if (!edge.IsRunwayCenterline)
            {
                return edge.TaxiwayName;
            }
        }

        return null;
    }

    /// <summary>
    /// Get all hold-short nodes for a specific runway.
    /// </summary>
    public List<GroundNode> GetRunwayHoldShortNodes(string runwayId)
    {
        var result = new List<GroundNode>();
        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } id && id.Contains(runwayId))
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>
    /// Find the next node along the taxiway past the exit intersection, so the
    /// aircraft can roll clear of the runway surface. Follows the non-runway edge
    /// whose heading is closest to the aircraft's exit bearing.
    /// </summary>
    public GroundNode? FindClearNode(GroundNode exitNode, string taxiwayName, TrueHeading runwayHeading)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;

        foreach (IGroundEdge edge in exitNode.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            GroundNode otherNode = edge.OtherNode(exitNode);

            // Prefer the direction that doesn't require turning back toward the runway
            double bearing = GeoMath.BearingTo(exitNode.Position, otherNode.Position);
            double diff = runwayHeading.AbsAngleTo(new TrueHeading(bearing));

            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = otherNode;
            }
        }

        return best;
    }

    /// <summary>
    /// Exit angle from the actual traversal direction: the bearing of the path's final hop into the
    /// hold-short vs the runway heading (arc-aware — uses the arc's arrival tangent when the final
    /// hop is a <see cref="GroundArc"/>). Unlike <see cref="ComputeExitAngle"/>, which inspects edge
    /// orientations at the hold-short node and takes the smallest angle, this reflects the direction
    /// the aircraft is actually traveling when it reaches the hold-short — a hold-short reached by
    /// doubling back through a reverse corner scores as the back-exit it is (~140°), not as the
    /// shallow angle of the taxiway's other direction. Null when the path has fewer than two nodes.
    /// </summary>
    public static double? ComputePathExitAngle(IReadOnlyList<GroundNode> path, string exitTaxiway, TrueHeading runwayHeading)
    {
        if (path.Count < 2)
        {
            return null;
        }

        // The exit is the branch taxiway up to its first bar or to where the path leaves it; a path continued onto a
        // joining taxiway from the branch's end keeps the branch's angle, not the joining taxiway's.
        int endIndex = 1;
        while (
            (endIndex < path.Count - 1)
            && (path[endIndex].Type != GroundNodeType.RunwayHoldShort)
            && StepMatchesTaxiway(path[endIndex], path[endIndex + 1], exitTaxiway)
        )
        {
            endIndex++;
        }

        GroundNode from = path[endIndex - 1];
        GroundNode to = path[endIndex];
        // Prefer the arc when both a preserved straight shortcut and a corner arc join the same node
        // pair — the arc's arrival tangent is the real traversal direction; the chord would understate
        // a reverse corner's sweep.
        IGroundEdge? edge =
            from.Edges.FirstOrDefault(e => (e is GroundArc) && (e.OtherNode(from).Id == to.Id))
            ?? from.Edges.FirstOrDefault(e => e.OtherNode(from).Id == to.Id);
        double bearing = edge is GroundArc arc ? arc.TangentBearingAt(to, from) : GeoMath.BearingTo(from.Position, to.Position);
        return runwayHeading.AbsAngleTo(new TrueHeading(bearing));
    }

    private static bool StepMatchesTaxiway(GroundNode from, GroundNode to, string taxiway) =>
        from.Edges.Any(e => (e.OtherNode(from).Id == to.Id) && e.MatchesTaxiway(taxiway));

    /// <summary>
    /// Compute the angle between the runway heading and the exit taxiway at the given node.
    /// Returns the absolute angle in degrees (0 = aligned with runway, 90 = perpendicular).
    /// Returns null if no taxiway edge heading can be determined.
    /// </summary>
    public double? ComputeExitAngle(GroundNode exitNode, string taxiwayName, TrueHeading runwayHeading)
    {
        // Find the edge that leads AWAY from the runway (neighbor is not on the
        // centerline) and return its angle from the runway heading. This is the
        // actual exit direction — a high-speed exit has a small angle (~30°), a
        // standard exit has a larger angle (~90°).
        double? bestAngle = null;

        foreach (IGroundEdge edge in exitNode.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                continue;
            }

            if (!edge.MatchesTaxiway(taxiwayName))
            {
                continue;
            }

            GroundNode otherNode = edge.OtherNode(exitNode);

            // Skip edges going toward the runway centerline — we want the away direction
            if (HasRunwayCenterlineEdge(otherNode))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(exitNode.Position, otherNode.Position);
            double angle = runwayHeading.AbsAngleTo(new TrueHeading(bearing));

            if (bestAngle is null || angle < bestAngle.Value)
            {
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>
    /// Returns the preferred exit side for a runway, reading any per-end sidecar override from the
    /// global <see cref="NavigationDatabase"/>. See the explicit-catalog overload for the priority order.
    /// </summary>
    public ExitSide? InferPreferredExitSide(string runwayDesignator, TrueHeading runwayHeading) =>
        InferPreferredExitSide(runwayDesignator, runwayHeading, NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty);

    /// <summary>
    /// Returns the preferred exit side for a runway. Priority:
    /// 1. Sidecar <c>exitDirections</c> override for this end (<see cref="AirportSidecarCatalog.GetExitDirection"/>)
    /// 2. Airport-authored GeoJSON "turnoff" (<see cref="GroundRunway.TurnoffForEnd"/>)
    /// 3. High-speed exits (≤45°), validated by parking proximity
    /// 4. Parking proximity
    /// 5. Parallel runway HS inheritance (for runways with no HS exits)
    /// Returns null if no preference can be determined.
    /// </summary>
    public ExitSide? InferPreferredExitSide(string runwayDesignator, TrueHeading runwayHeading, AirportSidecarCatalog sidecars)
    {
        // The sidecar override is authored per end, so it beats the GeoJSON turnoff — whose
        // reciprocal-end value is only derived by flipping the first-named end's side.
        if (sidecars.GetExitDirection(AirportId, runwayDesignator) is { } overridden)
        {
            return overridden;
        }

        // Authored data wins: when the airport file specifies a side for this end, trust it over inference.
        if (FindRunway(runwayDesignator)?.TurnoffForEnd(runwayDesignator) is { } authored)
        {
            return authored;
        }

        // Enumerate all exits on both sides
        List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> exits = EnumerateExitsBothSides(runwayDesignator, runwayHeading);
        if (exits.Count == 0)
        {
            return null;
        }

        int hsLeft = exits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Left));
        int hsRight = exits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Right));

        // Parking proximity per side: average distance from hold-shorts to nearest parking
        double avgParkLeft = AvgParkingDistForSide(exits.Where(e => e.Side == ExitSide.Left));
        double avgParkRight = AvgParkingDistForSide(exits.Where(e => e.Side == ExitSide.Right));

        ExitSide? hsSide =
            (hsLeft > hsRight) ? ExitSide.Left
            : (hsRight > hsLeft) ? ExitSide.Right
            : null;
        ExitSide? parkingSide =
            (avgParkLeft < avgParkRight) ? ExitSide.Left
            : (avgParkRight < avgParkLeft) ? ExitSide.Right
            : null;

        if (hsSide is not null)
        {
            // HS exits are a strong signal, but if parking proximity favors the
            // other side, the HS exit leads to a dead end (e.g., OAK 28R J exits
            // left toward 28L with no parking). Override with parking side.
            return (parkingSide is not null) && (parkingSide != hsSide) ? parkingSide : hsSide;
        }

        // Parking proximity is the strongest non-HS signal — airports are designed
        // so exit taxiways lead toward the terminal/ramp area. Only fall back to
        // parallel-runway HS inference when parking proximity is inconclusive.
        if (parkingSide is not null)
        {
            return parkingSide;
        }

        ExitSide? parallelHsSide = FindParallelRunwayHsSide(runwayDesignator, runwayHeading);
        if (parallelHsSide is not null)
        {
            return parallelHsSide;
        }

        return null;
    }

    /// <summary>
    /// Enumerate all exits for a runway, searching both sides per taxiway.
    /// </summary>
    private List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> EnumerateExitsBothSides(string designator, TrueHeading rwyHeading)
    {
        var exits = new List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)>();
        var seen = new HashSet<(string Taxiway, int HoldShortId)>();

        foreach (GroundNode node in Nodes.Values)
        {
            bool isCenterline = node.Edges.Any(e => e.MatchesRunway(designator));
            if (!isCenterline)
            {
                continue;
            }

            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IGroundEdge edge in node.Edges)
            {
                if (edge.IsRunwayCenterline)
                {
                    continue;
                }

                if (!searched.Add(edge.TaxiwayName))
                {
                    continue;
                }

                ExitSide[] sides = [ExitSide.Left, ExitSide.Right];
                foreach (ExitSide side in sides)
                {
                    var pref = new ExitPreference { Taxiway = edge.TaxiwayName, Side = side };
                    (GroundNode Node, string Taxiway, List<GroundNode> Path, ExitSide Side)? result = FindAdjacentHoldShortForListing(
                        node,
                        designator,
                        rwyHeading,
                        pref
                    );
                    if (result is null)
                    {
                        continue;
                    }

                    // FindAdjacentHoldShort falls back to off-side when no on-side match
                    // exists. For enumeration we need strict side matching.
                    if (result.Value.Side != side)
                    {
                        continue;
                    }

                    // Dedupe on the exit taxiway and its bar: a branch continued onto a joining taxiway ends at that
                    // taxiway's bar and is still its own exit.
                    if (!seen.Add((result.Value.Taxiway, result.Value.Node.Id)))
                    {
                        continue;
                    }

                    double? angle =
                        ComputePathExitAngle(result.Value.Path, result.Value.Taxiway, rwyHeading)
                        ?? ComputeExitAngle(result.Value.Node, result.Value.Taxiway, rwyHeading);
                    bool isHighSpeed = (angle is not null) && (angle.Value <= 45.0);
                    exits.Add((result.Value.Node.Id, side, isHighSpeed));
                }
            }
        }

        return exits;
    }

    /// <summary>
    /// Average distance from a side's hold-short nodes to their 3 nearest parking nodes.
    /// </summary>
    private double AvgParkingDistForSide(IEnumerable<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> sideExits)
    {
        var holdShortIds = sideExits.Select(e => e.HoldShortId).Distinct().ToList();
        if (holdShortIds.Count == 0)
        {
            return double.MaxValue;
        }

        double totalAvg = 0;
        int counted = 0;
        foreach (int hsId in holdShortIds)
        {
            if (!Nodes.TryGetValue(hsId, out GroundNode? hsNode))
            {
                continue;
            }

            totalAvg += AverageNearestParkingDistanceNm(hsNode, ParkingSampleCount);
            counted++;
        }

        return counted > 0 ? totalAvg / counted : double.MaxValue;
    }

    /// <summary>
    /// Find a parallel runway (same heading ±10°) and return the side where its
    /// high-speed exits are. Used for traffic flow inheritance when this runway
    /// has no high-speed exits of its own.
    /// </summary>
    public ExitSide? FindParallelRunwayHsSide(string designator, TrueHeading runwayHeading)
    {
        foreach (GroundRunway rwy in Runways)
        {
            var id = RunwayIdentifier.Parse(rwy.Name);

            if (
                string.Equals(id.End1, designator, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id.End2, designator, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            double rwBearing = GeoMath.BearingTo(rwy.Coordinates[0].Lat, rwy.Coordinates[0].Lon, rwy.Coordinates[^1].Lat, rwy.Coordinates[^1].Lon);
            double end1Heading = rwBearing;
            double end2Heading = (rwBearing + 180) % 360;

            double diff1 = Math.Abs(new TrueHeading(end1Heading).SignedAngleTo(runwayHeading));
            double diff2 = Math.Abs(new TrueHeading(end2Heading).SignedAngleTo(runwayHeading));

            string? parallelDesignator = null;
            double parallelBearing = 0;
            if (diff1 <= 10)
            {
                parallelDesignator = id.End1;
                parallelBearing = end1Heading;
            }
            else if (diff2 <= 10)
            {
                parallelDesignator = id.End2;
                parallelBearing = end2Heading;
            }

            if (parallelDesignator is null)
            {
                continue;
            }

            List<(int HoldShortId, ExitSide Side, bool IsHighSpeed)> parallelExits = EnumerateExitsBothSides(
                parallelDesignator,
                new TrueHeading(parallelBearing)
            );
            int pHsLeft = parallelExits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Left));
            int pHsRight = parallelExits.Count(e => e.IsHighSpeed && (e.Side == ExitSide.Right));

            if (pHsLeft > pHsRight)
            {
                return ExitSide.Left;
            }

            if (pHsRight > pHsLeft)
            {
                return ExitSide.Right;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a runway exit that is ahead of the aircraft along the runway heading.
    /// Applies the given exit preference (taxiway name, side, or nearest).
    /// Returns the exit node and its taxiway name, or null if no suitable exit is ahead.
    /// A node whose taxiway is in <paramref name="excludeTaxiways"/> (compared without regard to case) is never returned: the
    /// rollout passes the taxiways the crew has given up on this landing.
    /// </summary>
    public (GroundNode Node, string Taxiway)? FindExitAheadOnRunway(
        double lat,
        double lon,
        TrueHeading runwayHeading,
        ExitPreference? preference,
        string? runwayDesignator,
        IReadOnlySet<string>? excludeTaxiways,
        double maxSearchNm = 1.5
    )
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;
        GroundRunway? targetRunway = runwayDesignator is not null ? FindGroundRunway(runwayDesignator) : null;

        foreach (GroundNode node in Nodes.Values)
        {
            if (!IsValidExitCandidate(node, targetRunway) || IsOnExcludedTaxiway(node, excludeTaxiways))
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(new LatLon(lat, lon), node.Position);
            if (dist > maxSearchNm)
            {
                continue;
            }

            // Only consider exits ahead of the aircraft along the runway
            double alongTrack = GeoMath.AlongTrackDistanceNm(node.Position, new LatLon(lat, lon), runwayHeading);
            if (alongTrack <= 0)
            {
                continue;
            }

            // Check for taxiway preference match
            bool matchesPreference = false;

            if (preference?.Taxiway is { } taxiway)
            {
                foreach (IGroundEdge edge in node.Edges)
                {
                    if (!edge.IsRunwayCenterline && edge.MatchesTaxiway(taxiway))
                    {
                        matchesPreference = true;
                        break;
                    }
                }
            }

            // Apply preference filters
            if (preference?.Taxiway is not null && !matchesPreference)
            {
                continue;
            }

            if (preference?.Side is { } side)
            {
                double bearing = GeoMath.BearingTo(new LatLon(lat, lon), node.Position);
                double relative = runwayHeading.SignedAngleTo(new TrueHeading(bearing));
                bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
                if (!isOnRequestedSide)
                {
                    continue;
                }
            }

            // Score by along-track distance (prefer nearest ahead exit), biased toward parking
            double parkingBias = AverageNearestParkingDistanceNm(node, ParkingSampleCount) * ParkingProximityWeight;
            double score = alongTrack + parkingBias;
            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        if (best is null)
        {
            return null;
        }

        string? taxiwayName = GetExitTaxiwayName(best);
        if (taxiwayName is null)
        {
            return null;
        }

        return (best, taxiwayName);
    }

    /// <summary>
    /// True when the exit taxiway <paramref name="node"/> leads onto is one of <paramref name="excludeTaxiways"/>, compared without
    /// regard to case.
    /// </summary>
    private bool IsOnExcludedTaxiway(GroundNode node, IReadOnlySet<string>? excludeTaxiways) =>
        (excludeTaxiways is { Count: > 0 })
        && (GetExitTaxiwayName(node) is { } nodeTaxiway)
        && excludeTaxiways.Any(excluded => string.Equals(excluded, nodeTaxiway, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Number of nearest parking nodes to average when computing parking proximity bias.
    /// </summary>
    private const int ParkingSampleCount = 3;

    /// <summary>
    /// Weight applied to average parking distance when scoring exit candidates.
    /// Higher values make exits near parking more strongly preferred.
    /// </summary>
    private const double ParkingProximityWeight = 2.0;

    /// <summary>
    /// Score bonus (subtracted from score) for high-speed exits (≤45°) when no
    /// specific taxiway is requested. Ensures high-speed exits beat steeper exits
    /// at the same centerline node despite longer taxiway paths.
    /// </summary>
    private const double HighSpeedExitBonus = 0.15;

    /// <summary>
    /// Memoized results of the exit search's whole-layout scans and graph walks — each a pure function of the node set — for the node
    /// count they were built at: parking distances and the taxiways at a centerline node's cluster per node, and the centerline nodes
    /// and the taxiways an exits-ahead list judges per runway end. Every exit search on every rollout tick repeats them, and the
    /// exits-ahead list runs one per taxiway and side. Built once the layout is frozen; <see cref="RebuildAdjacencyLists"/> clears it,
    /// and a layout whose node set changes size starts afresh. Shared across threads: two threads filling the same entry compute the
    /// same value.
    /// </summary>
    private sealed record SearchMemo(
        int NodeCount,
        ConcurrentDictionary<(int NodeId, int Count), double> ParkingDistanceNm,
        ConcurrentDictionary<(bool AnyRunway, string Designator), GroundNode[]> CenterlineNodes,
        ConcurrentDictionary<GroundNode, IReadOnlySet<string>> ClusterSeedTaxiways,
        ConcurrentDictionary<string, IReadOnlyList<string>> ExitListTaxiways
    );

    private SearchMemo? _searchMemo;

    private SearchMemo CurrentSearchMemo()
    {
        SearchMemo? memo = Volatile.Read(ref _searchMemo);
        if ((memo is null) || (memo.NodeCount != Nodes.Count))
        {
            memo = new SearchMemo(Nodes.Count, new(), new(), new(ReferenceEqualityComparer.Instance), new(StringComparer.Ordinal));
            Volatile.Write(ref _searchMemo, memo);
        }

        return memo;
    }

    /// <summary>
    /// Whether the exit search holds a memo of this layout (<see cref="SearchMemo"/> or the last walk, <see cref="WalkStart"/>): false
    /// until a search builds one and after <see cref="RebuildAdjacencyLists"/> clears them. Read-only.
    /// </summary>
    public bool HoldsExitSearchMemo() => (Volatile.Read(ref _searchMemo) is not null) || (Volatile.Read(ref _lastWalkStart) is not null);

    /// <summary>
    /// The average distance from <paramref name="exitNode"/> to its <paramref name="count"/> nearest parking nodes
    /// (<see cref="ComputeAverageNearestParkingDistanceNm"/>), memoized per node.
    /// </summary>
    public double AverageNearestParkingDistanceNm(GroundNode exitNode, int count) =>
        CurrentSearchMemo()
            .ParkingDistanceNm.GetOrAdd(
                (exitNode.Id, count),
                static (key, state) => state.Layout.ComputeAverageNearestParkingDistanceNm(state.ExitNode, key.Count),
                (Layout: this, ExitNode: exitNode)
            );

    /// <summary>
    /// Compute the average distance from a node to the N nearest parking nodes.
    /// Returns 0 if there are no parking nodes in the layout.
    /// </summary>
    public double ComputeAverageNearestParkingDistanceNm(GroundNode exitNode, int count)
    {
        // Collect distances to all parking nodes, keep the N smallest
        Span<double> nearest = stackalloc double[count];
        nearest.Fill(double.MaxValue);

        bool anyParking = false;
        foreach (GroundNode node in Nodes.Values)
        {
            if (node.Type != GroundNodeType.Parking)
            {
                continue;
            }

            anyParking = true;
            double dist = GeoMath.DistanceNm(exitNode.Position, node.Position);

            // Insert into sorted top-N if smaller than the current largest
            if (dist < nearest[count - 1])
            {
                nearest[count - 1] = dist;
                // Bubble down to maintain sorted order
                for (int i = count - 2; i >= 0; i--)
                {
                    if (nearest[i] > nearest[i + 1])
                    {
                        (nearest[i], nearest[i + 1]) = (nearest[i + 1], nearest[i]);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        if (!anyParking)
        {
            return 0;
        }

        // Average only the slots that were filled (handles layouts with fewer than N parking nodes)
        double sum = 0;
        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            if (nearest[i] < double.MaxValue)
            {
                sum += nearest[i];
                filled++;
            }
        }

        return filled > 0 ? sum / filled : 0;
    }

    /// <summary>
    /// Returns true if the node is closer to <paramref name="targetRunway"/>'s centerline
    /// than to any other runway's centerline. If there are no other runways, returns true.
    /// </summary>
    private bool IsCloserToRunway(GroundNode node, GroundRunway targetRunway)
    {
        double targetDist = MinDistanceToRunwayCenterline(node, targetRunway);

        foreach (GroundRunway rwy in Runways)
        {
            if (ReferenceEquals(rwy, targetRunway))
            {
                continue;
            }

            double otherDist = MinDistanceToRunwayCenterline(node, rwy);
            if (otherDist < targetDist)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compute the minimum distance from a node to a runway's centerline polyline.
    /// Uses point-to-segment distances for each consecutive pair of coordinates.
    /// </summary>
    private static double MinDistanceToRunwayCenterline(GroundNode node, GroundRunway runway)
    {
        double minDist = double.MaxValue;
        List<(double Lat, double Lon)> coords = runway.Coordinates;

        for (int i = 0; i < coords.Count - 1; i++)
        {
            double dist = PointToSegmentDistanceNm(
                node.Position.Lat,
                node.Position.Lon,
                coords[i].Lat,
                coords[i].Lon,
                coords[i + 1].Lat,
                coords[i + 1].Lon
            );
            if (dist < minDist)
            {
                minDist = dist;
            }
        }

        // Fallback: if runway has only one coordinate, use point-to-point distance
        if (coords.Count == 1)
        {
            minDist = GeoMath.DistanceNm(node.Position, new LatLon(coords[0].Lat, coords[0].Lon));
        }

        return minDist;
    }

    /// <summary>
    /// Approximate distance from a point to a line segment on the Earth's surface.
    /// Projects the point onto the segment and returns the distance to the nearest point
    /// (endpoint or projected point).
    /// </summary>
    private static double PointToSegmentDistanceNm(double pLat, double pLon, double aLat, double aLon, double bLat, double bLon)
    {
        // Use a flat-earth approximation (valid for short distances like runway widths)
        double cosLat = Math.Cos(pLat * Math.PI / 180.0);
        double dx = (bLon - aLon) * cosLat;
        double dy = bLat - aLat;
        double px = (pLon - aLon) * cosLat;
        double py = pLat - aLat;

        double segLenSq = (dx * dx) + (dy * dy);
        if (segLenSq < 1e-20)
        {
            return GeoMath.DistanceNm(pLat, pLon, aLat, aLon);
        }

        double t = Math.Clamp(((px * dx) + (py * dy)) / segLenSq, 0.0, 1.0);
        double closestLat = aLat + (t * (bLat - aLat));
        double closestLon = aLon + (t * (bLon - aLon));

        return GeoMath.DistanceNm(pLat, pLon, closestLat, closestLon);
    }

    /// <summary>
    /// Returns true if the node is a valid runway exit candidate. Filters out:
    /// - Parking/Helipad nodes
    /// - Nodes on the runway centerline (with RWY edges)
    /// - Nodes with no taxiway edges
    /// - Nodes that are closer to a different parallel runway
    /// - Nodes within the runway surface width (intermediate routing vertices
    ///   from GeoJSON LineStrings that sit just off the centerline but aren't
    ///   real taxiway exit points)
    /// </summary>
    private bool IsValidExitCandidate(GroundNode node, GroundRunway? targetRunway)
    {
        if (node.Type is GroundNodeType.Parking or GroundNodeType.Helipad)
        {
            return false;
        }

        if (HasRunwayCenterlineEdge(node))
        {
            return false;
        }

        bool hasTaxiwayEdge = false;
        foreach (IGroundEdge edge in node.Edges)
        {
            if (!edge.IsRunwayCenterline)
            {
                hasTaxiwayEdge = true;
                break;
            }
        }

        if (!hasTaxiwayEdge)
        {
            return false;
        }

        if (targetRunway is not null && !IsCloserToRunway(node, targetRunway))
        {
            return false;
        }

        // Filter out nodes within the runway surface. These are intermediate
        // GeoJSON routing vertices that sit just off the centerline but aren't
        // real taxiway intersections where an aircraft can exit.
        if (targetRunway is not null)
        {
            double crossTrackNm = MinDistanceToRunwayCenterline(node, targetRunway);
            double runwayHalfWidthNm = (targetRunway.WidthFt / 2.0) / 6076.12;
            double minExitDistanceNm = runwayHalfWidthNm + (50.0 / 6076.12);
            if (crossTrackNm < minExitDistanceNm)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool HasRunwayCenterlineEdge(GroundNode node)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRunwayCenterline)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The graph node a taxi from <paramref name="position"/>, facing <paramref name="heading"/>, starts at: the heading-aligned
    /// endpoint of the nearest taxi edge (<see cref="FindNearestNodeForTaxi"/>, which handles post-pushback poses where the
    /// aircraft rests between graph nodes — see issue #161), or the absolute nearest node when the position is genuinely
    /// off-graph. Null when the layout has no node. TAXI and FOLLOWG both start here.
    /// </summary>
    public GroundNode? FindTaxiStartNode(LatLon position, TrueHeading heading) =>
        FindNearestNodeForTaxi(position, heading) ?? FindNearestNode(position);

    // LatLon-shaped overloads of the find methods. Thin wrappers around the scalar forms above.

    public GroundNode? FindNearestNode(LatLon position) => FindNearestNode(position.Lat, position.Lon);

    public NearestTaxiEdge? FindNearestTaxiEdge(LatLon position) => FindNearestTaxiEdge(position.Lat, position.Lon);

    public GroundNode? FindNearestCenterlineNode(LatLon position, TrueHeading runwayHeading, string? runwayDesignator = null) =>
        FindNearestCenterlineNode(position.Lat, position.Lon, runwayHeading, runwayDesignator);

    public GroundNode? FindNearestExit(LatLon position, TrueHeading runwayHeading, string? runwayDesignator, double maxSearchNm = 0.5) =>
        FindNearestExit(position.Lat, position.Lon, runwayHeading, runwayDesignator, maxSearchNm);

    public GroundNode? FindExitByTaxiway(LatLon position, string taxiwayName, double maxSearchNm = 1.0) =>
        FindExitByTaxiway(position.Lat, position.Lon, taxiwayName, maxSearchNm);
}
