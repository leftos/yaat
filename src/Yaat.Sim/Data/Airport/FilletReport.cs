namespace Yaat.Sim.Data.Airport;

/// <summary>
/// What one fillet pass (<see cref="IFilletArcGenerator.Apply"/>) did at each corner it visited and how it matched each
/// per-corner override (<see cref="CornerFilletOverrides"/>). Returned on <see cref="FilletStatistics.Report"/> only: it is
/// never stored on the layout, so it reaches no recording, snapshot or wire message.
/// </summary>
/// <param name="Corners">
/// One row per junction-level skip of a taxiway intersection with two or more edges (the places an override can name) and
/// per arm pair the corner loop visited, by junction node id, a junction's own row first and then its arm pairs in the
/// order the planner visits them.
/// </param>
/// <param name="Overrides">One row per override, in <see cref="CornerFilletOverrides.Entries"/> order.</param>
public sealed record FilletReport(IReadOnlyList<FilletCornerReport> Corners, IReadOnlyList<FilletOverrideReport> Overrides)
{
    /// <summary>The report of a pass that visited no corner and had no override (<see cref="FilletMode.None"/>).</summary>
    public static FilletReport Empty { get; } = new([], []);
}

/// <summary>
/// One corner of a fillet pass, or one junction the pass skipped whole. A junction-level row has null arm fields.
/// </summary>
/// <param name="JunctionNodeId">The pre-fillet junction node's id.</param>
/// <param name="ApexLat">The junction's pre-fillet latitude, degrees.</param>
/// <param name="ApexLon">The junction's pre-fillet longitude, degrees.</param>
/// <param name="TaxiwayA">The first arm's taxiway name, or null for a junction-level row.</param>
/// <param name="TaxiwayB">The second arm's taxiway name, or null for a junction-level row.</param>
/// <param name="BearingA">The first arm's true bearing from the junction, degrees, or null for a junction-level row.</param>
/// <param name="BearingB">The second arm's true bearing from the junction, degrees, or null for a junction-level row.</param>
/// <param name="Outcome">What the pass did at the corner.</param>
/// <param name="SkipReason">
/// Why the corner was skipped, or, for a <see cref="FilletCornerOutcome.ChordDegraded"/> corner, why it got a straight
/// connector instead of an arc (null when the executor degraded a too-tight arc); null for a built corner.
/// </param>
/// <param name="RequestedRadiusFt">The radius the generator asked of the corner, feet, or null when it never chose one.</param>
/// <param name="AchievedRadiusFt">The built arc's minimum radius of curvature, feet, or null when no arc was built.</param>
/// <param name="Warnings">The planner warnings raised for the corner or its junction, each as "{Code}: {Message}".</param>
public sealed record FilletCornerReport(
    int JunctionNodeId,
    double ApexLat,
    double ApexLon,
    string? TaxiwayA,
    string? TaxiwayB,
    double? BearingA,
    double? BearingB,
    FilletCornerOutcome Outcome,
    FilletSkipReason? SkipReason,
    double? RequestedRadiusFt,
    double? AchievedRadiusFt,
    IReadOnlyList<string> Warnings
);

/// <summary>What a fillet pass did at one corner.</summary>
public enum FilletCornerOutcome
{
    /// <summary>A fillet arc was built.</summary>
    Built,

    /// <summary>
    /// The fillet was too tight to keep: the executor joined the corner's tangent cuts by a straight chord, or the planner
    /// planned a straight connector between them (a later redirect or dedup may drop it);
    /// <see cref="FilletCornerReport.SkipReason"/> names the planner's reason when it made that call.
    /// </summary>
    ChordDegraded,

    /// <summary>No fillet was built; <see cref="FilletCornerReport.SkipReason"/> says why.</summary>
    Skipped,
}

/// <summary>Why a fillet pass built no fillet at a junction or corner, one value per place the pass skips one.</summary>
public enum FilletSkipReason
{
    /// <summary>The node is a shape point of a hand-drawn arc.</summary>
    ShapePoint,

    /// <summary>The intersection is not one the pass fillets (a same-name bend, a lone runway edge, a centerline projection).</summary>
    Ineligible,

    /// <summary>The junction had no corner to fillet and no collinear pair.</summary>
    JunctionSkipped,

    /// <summary>No corner of the junction survived its tangent cuts and it had no collinear pair.</summary>
    NoCornerPairs,

    /// <summary>Both arms reach the same neighbouring node.</summary>
    SameFarNode,

    /// <summary>The arms are close enough to straight that the pair is a through route.</summary>
    Collinear,

    /// <summary>The turn is below the smallest angle the pass fillets.</summary>
    BelowMinAngle,

    /// <summary>The arms are nearly parallel: a hairpin no aircraft turns through.</summary>
    NearHairpin,

    /// <summary>The radius the ideal tangent allows is below the radius floor.</summary>
    RadiusFloor,

    /// <summary>
    /// The radius the resolved tangent cuts allow is below the radius floor; the planner planned a straight connector
    /// between the cuts instead (a later redirect or dedup may drop it).
    /// </summary>
    DegenerateRadius,

    /// <summary>The corner has no tangent cut on one of its arms.</summary>
    NoOwningCut,

    /// <summary>The corner's two tangent cuts are the same cut or resolve to the same node.</summary>
    CoincidentCuts,

    /// <summary>Another corner's arc joins the same two endpoints and was kept instead.</summary>
    Deduplicated,

    /// <summary>One of the corner's arc endpoints resolved to no node when the arc was built.</summary>
    UnresolvedEndpoint,

    /// <summary>
    /// A corner fillet override suppresses the corner: suppressing keeps the junction node and every arm's stub, so the
    /// pair stays a sharp corner, and the junction's other corners are planned as if this one asked for no tangent cut.
    /// </summary>
    Suppressed,

    /// <summary>
    /// A forced corner's arc would be tighter than any aircraft can steer
    /// (<see cref="Pathfinding.GeometricAdmissibility.MinSteerableArcRadiusFt"/>), so the force is refused.
    /// </summary>
    Unsteerable,

    /// <summary>A forced pair turns less than one degree: there is no corner to fillet, so the force is refused.</summary>
    NoTurn,

    /// <summary>
    /// A forced corner's tangent cut would leave a straight shorter than the shared-arm clear gap to the neighbouring
    /// junction's cuts, so the force is refused.
    /// </summary>
    SharedArmClearGap,
}

/// <summary>How one per-corner override matched the pre-fillet layout.</summary>
/// <param name="Override">The override.</param>
/// <param name="Match">Whether it matched exactly one corner, none, or more than one.</param>
/// <param name="JunctionNodeId">The matched junction's node id, or null when the override matched no single junction.</param>
/// <param name="Detail">A human-readable account of the match.</param>
/// <param name="Effect">
/// What applying the override did, or null when <paramref name="Match"/> is not <see cref="FilletOverrideMatch.Matched"/>.
/// </param>
/// <param name="RejectedReason">
/// Why the pass refused a forced corner, set only when <paramref name="Effect"/> is <see cref="FilletOverrideEffect.Rejected"/>.
/// </param>
/// <param name="RejectedRadiusFt">
/// The refused forced arc's tightest radius, feet, set only when <paramref name="RejectedReason"/> is
/// <see cref="FilletSkipReason.Unsteerable"/>.
/// </param>
public sealed record FilletOverrideReport(
    CornerFilletOverride Override,
    FilletOverrideMatch Match,
    int? JunctionNodeId,
    string Detail,
    FilletOverrideEffect? Effect,
    FilletSkipReason? RejectedReason,
    double? RejectedRadiusFt
);

/// <summary>What applying a matched per-corner override did to its corner.</summary>
public enum FilletOverrideEffect
{
    /// <summary>
    /// The override's edits were applied (an override with no edit changes nothing and is applied too);
    /// <see cref="FilletCornerReport"/> says what the corner became.
    /// </summary>
    Applied,

    /// <summary>
    /// The pass never visits the corner, so its edits cannot apply: the junction is a hand-drawn arc's shape point, has
    /// fewer than two edges, is a runway centerline projection, or is an intersection the pass does not fillet and the
    /// override does not force.
    /// </summary>
    NotReachable,

    /// <summary>
    /// The override forces the corner but a check force never bypasses still gives it no arc, or would give it one no
    /// aircraft can steer (<see cref="FilletOverrideReport.RejectedReason"/>); the corner is planned as if the override did
    /// not force it.
    /// </summary>
    Rejected,
}

/// <summary>How a per-corner override matched the pre-fillet layout.</summary>
public enum FilletOverrideMatch
{
    /// <summary>Exactly one corner matches the override, and no other override names that corner.</summary>
    Matched,

    /// <summary>No corner matches the override (the map moved); it is never applied elsewhere.</summary>
    Orphaned,

    /// <summary>More than one corner matches the override, or more than one override names its corner.</summary>
    Ambiguous,
}
