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
}

/// <summary>How one per-corner override matched the pre-fillet layout.</summary>
/// <param name="Override">The override.</param>
/// <param name="Match">Whether it matched exactly one corner, none, or more than one.</param>
/// <param name="JunctionNodeId">The matched junction's node id, or null when the override matched no single junction.</param>
/// <param name="Detail">A human-readable account of the match.</param>
public sealed record FilletOverrideReport(CornerFilletOverride Override, FilletOverrideMatch Match, int? JunctionNodeId, string Detail);

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
