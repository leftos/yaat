using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The derived rule that decided a name's movement-area verdict, in the order they are tried; the first that matches
/// decides.
/// </summary>
public enum MovementAreaRule
{
    /// <summary>
    /// Rule 1: <c>RAMP</c>, a runway key, a single letter, or a name that is no taxiway designator — never a ramp
    /// taxilane. A single letter is always a movement-area taxiway; <c>RAMP</c> is apron.
    /// </summary>
    NotALane = 1,

    /// <summary>Rule 2: a runway holding position on the lane's own edges — movement area.</summary>
    RunwayHoldShort = 2,

    /// <summary>Rule 3: a parking gate one edge off the lane, by the gate lead-in rule — a ramp taxilane.</summary>
    GateOffLane = 3,

    /// <summary>Rule 4: the lane joins two or more distinct single-letter taxiways — movement area.</summary>
    JoinsTwoTaxiways = 4,

    /// <summary>Rule 5: any other lane, joined to at most one single-letter taxiway — a ramp taxilane.</summary>
    Taxilane = 5,
}

/// <summary>
/// Which of an airport's taxiway names are non-movement ramp taxilanes. The layout carries no movement-area
/// boundary (the GeoJSON has no AIM 2-3-6.c markings, and only apron edges are named <c>RAMP</c>), so the verdict
/// is derived, by the first of these that matches (<see cref="MovementAreaRule"/>):
/// <list type="number">
/// <item><c>RAMP</c>, a runway key, or a single letter is never a ramp taxilane: a single letter is a movement-area
/// taxiway, <c>RAMP</c> is apron;</item>
/// <item>a lane carrying a runway holding position on its own edges is movement area, whatever hangs off it;</item>
/// <item>a lane with a parking gate one edge off it (<see cref="GateOffLane"/>) is a ramp taxilane, whatever it
/// joins (OAK's TE and TC join T and U);</item>
/// <item>a lane joined to two or more distinct single-letter taxiways is movement area — a connector;</item>
/// <item>any other lane, joined to at most one single-letter taxiway, is a ramp taxilane.</item>
/// </list>
/// Rule 4 counts only single-letter neighbours, so a multi-character connector between a lettered taxiway and another
/// multi-character movement-area lane falls to rule 5 and reads as a ramp taxilane. The airport sidecar's
/// <c>movementAreaTaxiways</c> and <c>nonMovementTaxilanes</c> lists exist for exactly that case, and override the
/// derived verdict both ways. One instance serves the ramp-lane cut (<see cref="RampLaneReposition"/>) and the tug
/// planner's movement-area refusal (<see cref="TugPavementClassifier"/>), so the taxi router and the push planner agree.
/// The verdict is a pure function of the layout and the sidecar catalog, cached per pair of instances.
/// </summary>
public sealed class MovementAreaClassification
{
    private static readonly ILogger Log = SimLog.CreateLogger("MovementAreaClassification");

    private static readonly ConditionalWeakTable<
        AirportGroundLayout,
        ConditionalWeakTable<AirportSidecarCatalog, MovementAreaClassification>
    > Cache = [];

    private readonly Dictionary<string, MovementAreaRule> _ruleByName;
    private readonly Dictionary<string, GroundNode> _gateByLane;
    private readonly IReadOnlySet<string> _forcedMovementArea;
    private readonly IReadOnlySet<string> _forcedNonMovement;

    private MovementAreaClassification(
        Dictionary<string, MovementAreaRule> ruleByName,
        Dictionary<string, GroundNode> gateByLane,
        IReadOnlySet<string> forcedMovementArea,
        IReadOnlySet<string> forcedNonMovement
    )
    {
        _ruleByName = ruleByName;
        _gateByLane = gateByLane;
        _forcedMovementArea = forcedMovementArea;
        _forcedNonMovement = forcedNonMovement;
    }

    /// <summary>
    /// The classification for <paramref name="layout"/> against the sidecar catalog of the
    /// <see cref="NavigationDatabase"/> current in this async flow, cached per layout and catalog instance, so a scoped
    /// database with other overrides gets its own. With no database initialized only the derived verdict applies.
    /// </summary>
    public static MovementAreaClassification For(AirportGroundLayout layout)
    {
        AirportSidecarCatalog sidecars = NavigationDatabase.InstanceOrNull?.AirportSidecars ?? AirportSidecarCatalog.Empty;
        ConditionalWeakTable<AirportSidecarCatalog, MovementAreaClassification> byCatalog = Cache.GetOrCreateValue(layout);
        return byCatalog.GetValue(sidecars, catalog => Build(layout, catalog));
    }

    /// <summary>
    /// Builds an uncached classification of <paramref name="layout"/> against <paramref name="sidecars"/>. A sidecar
    /// name the layout does not carry is logged at Warning and otherwise ignored.
    /// </summary>
    public static MovementAreaClassification Build(AirportGroundLayout layout, AirportSidecarCatalog sidecars)
    {
        Dictionary<string, HashSet<GroundNode>> nodesByLane = NodesByLane(layout);
        IReadOnlySet<string> forcedMovementArea = sidecars.GetMovementAreaTaxiways(layout.AirportId);
        IReadOnlySet<string> forcedNonMovement = sidecars.GetNonMovementTaxilanes(layout.AirportId);
        WarnUnknownNames(layout.AirportId, nodesByLane, forcedMovementArea, "movementAreaTaxiways");
        WarnUnknownNames(layout.AirportId, nodesByLane, forcedNonMovement, "nonMovementTaxilanes");

        var ruleByName = new Dictionary<string, MovementAreaRule>(StringComparer.OrdinalIgnoreCase);
        var gateByLane = new Dictionary<string, GroundNode>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, HashSet<GroundNode> nodes) in nodesByLane)
        {
            MovementAreaRule rule = Decide(layout, name, nodes, out GroundNode? gate);
            ruleByName[name] = rule;
            if (gate is not null)
            {
                gateByLane[name] = gate;
            }

            Log.LogDebug("{Airport}: {Name} is decided by rule {Rule} ({RuleName})", layout.AirportId, name, (int)rule, rule);
        }

        return new MovementAreaClassification(ruleByName, gateByLane, forcedMovementArea, forcedNonMovement);
    }

    /// <summary>
    /// A non-movement ramp taxilane. A sidecar list wins (a name in both lists is movement area); otherwise the derived
    /// verdict decides.
    /// </summary>
    public bool IsRampTaxilane(string name)
    {
        if (_forcedMovementArea.Contains(name))
        {
            return false;
        }

        return _forcedNonMovement.Contains(name) || (DerivedRule(name) is MovementAreaRule.GateOffLane or MovementAreaRule.Taxilane);
    }

    /// <summary>Movement-area pavement: neither apron (<c>RAMP</c>) nor a ramp taxilane.</summary>
    public bool IsMovementArea(string name) => !name.Equals("RAMP", StringComparison.OrdinalIgnoreCase) && !IsRampTaxilane(name);

    /// <summary>
    /// The derived rule that decided <paramref name="name"/>'s verdict, or null when the layout carries no such name.
    /// Ignores the sidecar overrides; it is the evidence behind the derived verdict.
    /// </summary>
    public MovementAreaRule? DerivedRule(string name) => _ruleByName.TryGetValue(name, out MovementAreaRule rule) ? rule : null;

    /// <summary>
    /// The gate that decided rule 3 for <paramref name="lane"/>, or null when another rule decided. Ignores the sidecar
    /// overrides; it is the evidence behind the derived verdict.
    /// </summary>
    public GroundNode? GateOf(string lane) => _gateByLane.GetValueOrDefault(lane);

    /// <summary>The first rule that matches <paramref name="name"/>, and the gate hanging off it when rule 3 decides.</summary>
    private static MovementAreaRule Decide(AirportGroundLayout layout, string name, HashSet<GroundNode> nodes, out GroundNode? gate)
    {
        gate = null;
        if (!IsLaneDesignator(name))
        {
            return MovementAreaRule.NotALane;
        }

        if (HasRunwayHoldShort(layout, name))
        {
            return MovementAreaRule.RunwayHoldShort;
        }

        gate = GateOffLane(name, nodes);
        if (gate is not null)
        {
            return MovementAreaRule.GateOffLane;
        }

        return JoinedSingleLetterTaxiways(layout, name).Count >= 2 ? MovementAreaRule.JoinsTwoTaxiways : MovementAreaRule.Taxilane;
    }

    /// <summary>
    /// The distinct single-letter taxiways the lane joins: every name on an edge, straight or arc, at a node of the
    /// lane's own edges.
    /// </summary>
    private static HashSet<string> JoinedSingleLetterTaxiways(AirportGroundLayout layout, string lane) =>
        new(
            layout.GetNodesOnTaxiway(lane).SelectMany(node => node.Edges).SelectMany(RampLaneReposition.EdgeNames).Where(IsSingleLetterTaxiway),
            StringComparer.OrdinalIgnoreCase
        );

    private static bool IsSingleLetterTaxiway(string name) => (name.Length == 1) && char.IsAsciiLetter(name[0]);

    /// <summary>Every node an edge carrying each name touches, arcs included (an arc carries both taxiways it joins).</summary>
    private static Dictionary<string, HashSet<GroundNode>> NodesByLane(AirportGroundLayout layout)
    {
        var nodesByLane = new Dictionary<string, HashSet<GroundNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (IGroundEdge edge in layout.AllEdges)
        {
            foreach (string name in RampLaneReposition.EdgeNames(edge))
            {
                if (!nodesByLane.TryGetValue(name, out HashSet<GroundNode>? nodes))
                {
                    nodes = [];
                    nodesByLane[name] = nodes;
                }

                nodes.Add(edge.Nodes[0]);
                nodes.Add(edge.Nodes[1]);
            }
        }

        return nodesByLane;
    }

    /// <summary>
    /// A runway holding position on the lane's own straight edges: the lane is a runway connector, which is movement
    /// area however close a gate sits (SFO <c>M1</c>, <c>A1</c>; OAK <c>W1</c>–<c>W7</c>).
    /// </summary>
    private static bool HasRunwayHoldShort(AirportGroundLayout layout, string lane) =>
        layout.GetNodesOnTaxiway(lane).Any(node => node.Type == GroundNodeType.RunwayHoldShort);

    /// <summary>
    /// The first parking node (lowest id, for determinism) one edge away from a node of <paramref name="lane"/>, where
    /// that edge is the lane's own or a gate lead-in — every name it carries is <c>RAMP</c>, blank, or the gate's own
    /// name. A gate reached only along another named taxiway does not hang off this lane. Unlike rules 2 and 4, the
    /// lane's nodes include the ends of the fillet arcs carrying its name: SFO <c>M2</c> and OAK <c>TE</c> and
    /// <c>TC</c> have their gates only off arc ends, and read as movement area without them.
    /// </summary>
    private static GroundNode? GateOffLane(string lane, HashSet<GroundNode> laneNodes)
    {
        GroundNode? gate = null;
        foreach (GroundNode node in laneNodes)
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                GroundNode other = edge.Nodes[0].Id == node.Id ? edge.Nodes[1] : edge.Nodes[0];
                if ((other.Type == GroundNodeType.Parking) && IsGateLeadIn(edge, lane, other) && ((gate is null) || (other.Id < gate.Id)))
                {
                    gate = other;
                }
            }
        }

        return gate;
    }

    private static bool IsGateLeadIn(IGroundEdge edge, string lane, GroundNode gate) =>
        RampLaneReposition
            .EdgeNames(edge)
            .All(name =>
                string.IsNullOrWhiteSpace(name)
                || name.Equals("RAMP", StringComparison.OrdinalIgnoreCase)
                || name.Equals(lane, StringComparison.OrdinalIgnoreCase)
                || name.Equals(gate.Name, StringComparison.OrdinalIgnoreCase)
            );

    /// <summary>A name that can be a ramp taxilane: a <see cref="IsTaxiwayName"/> longer than one character (a bare letter is a taxiway).</summary>
    private static bool IsLaneDesignator(string name) => (name.Length > 1) && IsTaxiwayName(name);

    /// <summary>
    /// A taxiway designator: a letter followed by letters and digits only, and neither <c>RAMP</c> nor a runway
    /// centerline key (<c>RWY28R/10L</c>). Node references (<c>#12</c>) are not taxiways.
    /// </summary>
    private static bool IsTaxiwayName(string name) =>
        (name.Length > 0)
        && char.IsAsciiLetter(name[0])
        && name.All(char.IsAsciiLetterOrDigit)
        && !name.Equals("RAMP", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    private static void WarnUnknownNames(
        string airportId,
        Dictionary<string, HashSet<GroundNode>> nodesByLane,
        IReadOnlySet<string> names,
        string list
    )
    {
        foreach (string name in names)
        {
            if (!nodesByLane.ContainsKey(name))
            {
                Log.LogWarning("{Airport}: sidecar {List} names {Name}, which the ground layout does not carry; ignored", airportId, list, name);
            }
        }
    }
}
