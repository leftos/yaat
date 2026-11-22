using System.Diagnostics.CodeAnalysis;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// Records what one fillet pass did at each junction and corner it visited, as the pass decides it, and builds the pass's
/// <see cref="FilletReport"/>. Recording never changes a decision: every call is made after the decision it records.
/// </summary>
internal sealed class FilletReportCollector
{
    private readonly SortedDictionary<int, JunctionRows> _junctions = [];
    private readonly Dictionary<ResolvedCorner, PinRefusal> _pinRefusals = [];
    private readonly Dictionary<ResolvedCorner, FilletSkipReason> _planPinRefusals = [];

    /// <summary>Records that the planner refused the pinned tangents of the arm pair, for <paramref name="refusal"/>.</summary>
    public void PinRefused(ResolvedCorner corner, PinRefusal refusal) => _pinRefusals[corner] = refusal;

    /// <summary>Why the planner refused the arm pair's pinned tangents, or null when it refused none.</summary>
    public PinRefusal? PinRefusalOf(ResolvedCorner corner) => _pinRefusals.GetValueOrDefault(corner);

    /// <summary>
    /// Records that a later stage of the plan refused a pinned cut of the planned corner, for <paramref name="reason"/>; the
    /// first reason recorded for the corner is kept. The pass is planned again without the corner's pins.
    /// </summary>
    public void PlanPinRefused(int junctionNodeId, int cornerId, FilletSkipReason reason)
    {
        if (TryCorner(junctionNodeId, cornerId, out PairRow? row))
        {
            _planPinRefusals.TryAdd(
                new ResolvedCorner(junctionNodeId, Math.Min(row.ArmA.Id, row.ArmB.Id), Math.Max(row.ArmA.Id, row.ArmB.Id)),
                reason
            );
        }
    }

    /// <summary>Why a later stage of the plan refused a pinned cut of the arm pair, or null when none did.</summary>
    public FilletSkipReason? PlanPinRefusalOf(ResolvedCorner corner) =>
        _planPinRefusals.TryGetValue(corner, out FilletSkipReason reason) ? reason : null;

    /// <summary>
    /// Records that the post-execute normalizer removed the built arc of a pinned corner (its sampled radius is under the
    /// radius floor): the corner has no arc after all.
    /// </summary>
    public void PinnedArcDropped(int junctionNodeId, int cornerId)
    {
        if (TryCorner(junctionNodeId, cornerId, out PairRow? row))
        {
            row.Outcome = FilletCornerOutcome.Skipped;
            row.Skip = FilletSkipReason.DegenerateRadius;
            row.AchievedRadiusFt = null;
        }
    }

    /// <summary>
    /// Records that the pass skipped <paramref name="node"/> whole, for <paramref name="reason"/>, when it is a place an
    /// override could name (a taxiway intersection with two or more edges, as <see cref="FilletOverrideResolver"/> searches);
    /// any other node is left out of the report.
    /// </summary>
    public void JunctionSkipped(GroundNode node, FilletSkipReason reason)
    {
        if ((node.Type == GroundNodeType.TaxiwayIntersection) && (node.Edges.Count >= 2))
        {
            RowsOf(node).JunctionSkip = reason;
        }
    }

    /// <summary>Records that the planner emitted no corner for the arm pair, for <paramref name="reason"/>.</summary>
    public void PairSkipped(GroundNode junction, TaxiwayArm armA, TaxiwayArm armB, FilletSkipReason reason, double? requestedRadiusFt) =>
        RowsOf(junction).Pairs.Add(new PairRow(armA, armB, cornerId: null) { Skip = reason, RequestedRadiusFt = requestedRadiusFt });

    /// <summary>Records that the planner emitted <paramref name="corner"/> for the arm pair; its outcome comes later.</summary>
    public void CornerPlanned(GroundNode junction, TaxiwayArm armA, TaxiwayArm armB, CornerSpec corner)
    {
        JunctionRows rows = RowsOf(junction);
        var row = new PairRow(armA, armB, corner.CornerId) { RequestedRadiusFt = corner.RequestedRadiusFt };
        rows.Pairs.Add(row);
        rows.ByCorner[corner.CornerId] = row;
    }

    /// <summary>Records that a planned corner was dropped, for <paramref name="reason"/>, unless an earlier stage already did.</summary>
    public void CornerDropped(int junctionNodeId, int cornerId, FilletSkipReason reason)
    {
        if (TryCorner(junctionNodeId, cornerId, out PairRow? row) && (row.Outcome is null))
        {
            row.Outcome = FilletCornerOutcome.Skipped;
            row.Skip = reason;
        }
    }

    /// <summary>
    /// Records that a planned corner gets no arc and its tangent cuts are joined by a straight connector instead, for
    /// <paramref name="reason"/>, unless an earlier stage already decided its outcome.
    /// </summary>
    public void CornerStraightened(int junctionNodeId, int cornerId, FilletSkipReason reason)
    {
        if (TryCorner(junctionNodeId, cornerId, out PairRow? row) && (row.Outcome is null))
        {
            row.Outcome = FilletCornerOutcome.ChordDegraded;
            row.Skip = reason;
        }
    }

    /// <summary>Records every corner arc in <paramref name="before"/> that <paramref name="after"/> no longer holds as dropped.</summary>
    public void CornerOpsDropped(IReadOnlyList<CornerArcOp> before, IReadOnlyList<CornerArcOp> after, FilletSkipReason reason)
    {
        var kept = after.Select(op => (op.JunctionNodeId, op.CornerId)).ToHashSet();
        foreach (CornerArcOp op in before.Where(op => !kept.Contains((op.JunctionNodeId, op.CornerId))))
        {
            CornerDropped(op.JunctionNodeId, op.CornerId, reason);
        }
    }

    /// <summary>Records that the executor built the corner's arc, of minimum radius <paramref name="radiusFt"/>.</summary>
    public void ArcBuilt(int junctionNodeId, int cornerId, double radiusFt) =>
        SetOutcome(junctionNodeId, cornerId, FilletCornerOutcome.Built, radiusFt);

    /// <summary>Records that the executor joined the corner's tangent cuts with a straight chord.</summary>
    public void ChordDegraded(int junctionNodeId, int cornerId) => SetOutcome(junctionNodeId, cornerId, FilletCornerOutcome.ChordDegraded, null);

    /// <summary>The arm pair's outcome as the report gives it, or null when the pass never visited it.</summary>
    public FilletCornerOutcome? OutcomeOf(ResolvedCorner corner) => PairOf(corner) is { } row ? (row.Outcome ?? FilletCornerOutcome.Skipped) : null;

    /// <summary>The corner id the planner gave the arm pair, or null when it planned no corner for it.</summary>
    public int? CornerIdOf(ResolvedCorner corner) => PairOf(corner)?.CornerId;

    /// <summary>True when the planner visited the arm pair of <paramref name="corner"/> (it has a row).</summary>
    public bool Visited(ResolvedCorner corner) => PairOf(corner) is not null;

    /// <summary>
    /// Why the arm pair of <paramref name="corner"/> has no arc so far: the planner's skip, or the reason a later stage
    /// dropped its corner; null while its corner is planned, built or straightened, and when the pass never visited it.
    /// </summary>
    public FilletSkipReason? SkipOf(ResolvedCorner corner) =>
        PairOf(corner) switch
        {
            { CornerId: null } row => row.Skip,
            { Outcome: FilletCornerOutcome.Skipped } row => row.Skip,
            _ => null,
        };

    /// <summary>
    /// The report: junctions by node id, each junction's own row first and then its arm pairs in planner order. A row with a
    /// corner id carries its junction's and its own warnings; a junction-level row its junction's; a pair the planner
    /// skipped none.
    /// </summary>
    public FilletReport Build(IReadOnlyList<PlanWarning> warnings, IReadOnlyList<FilletOverrideReport> overrides)
    {
        ILookup<(int?, int?), string> byKey = warnings.ToLookup(w => (w.JunctionNodeId, w.CornerId), w => $"{w.Code}: {w.Message}");
        var corners = new List<FilletCornerReport>();
        foreach ((int junctionId, JunctionRows rows) in _junctions)
        {
            IReadOnlyList<string> junctionWarnings = [.. byKey[(junctionId, null)]];
            if (rows.JunctionSkip is { } skip)
            {
                corners.Add(JunctionRow(rows.Node, skip, junctionWarnings));
            }

            foreach (PairRow row in rows.Pairs)
            {
                IReadOnlyList<string> rowWarnings = (row.CornerId is { } id) ? [.. junctionWarnings, .. byKey[(junctionId, id)]] : [];
                corners.Add(PairReport(rows.Node, row, rowWarnings));
            }
        }

        return new FilletReport(corners, overrides);
    }

    private static FilletCornerReport JunctionRow(GroundNode node, FilletSkipReason skip, IReadOnlyList<string> warnings) =>
        new(node.Id, node.Position.Lat, node.Position.Lon, null, null, null, null, FilletCornerOutcome.Skipped, skip, null, null, warnings);

    private static FilletCornerReport PairReport(GroundNode node, PairRow row, IReadOnlyList<string> warnings)
    {
        FilletCornerOutcome outcome = row.Outcome ?? FilletCornerOutcome.Skipped;
        return new FilletCornerReport(
            node.Id,
            node.Position.Lat,
            node.Position.Lon,
            row.ArmA.TaxiwayName,
            row.ArmB.TaxiwayName,
            row.ArmA.BearingFromJunctionDeg,
            row.ArmB.BearingFromJunctionDeg,
            outcome,
            (outcome == FilletCornerOutcome.Built) ? null : row.Skip,
            row.RequestedRadiusFt,
            row.AchievedRadiusFt,
            warnings
        );
    }

    private void SetOutcome(int junctionNodeId, int cornerId, FilletCornerOutcome outcome, double? achievedRadiusFt)
    {
        if (TryCorner(junctionNodeId, cornerId, out PairRow? row))
        {
            row.Outcome = outcome;
            row.AchievedRadiusFt = achievedRadiusFt;
        }
    }

    private bool TryCorner(int junctionNodeId, int cornerId, [NotNullWhen(true)] out PairRow? row)
    {
        row = null;
        return _junctions.TryGetValue(junctionNodeId, out JunctionRows? rows) && rows.ByCorner.TryGetValue(cornerId, out row);
    }

    private PairRow? PairOf(ResolvedCorner corner) =>
        _junctions.TryGetValue(corner.JunctionNodeId, out JunctionRows? rows)
            ? rows.Pairs.FirstOrDefault(p => (p.ArmA.Id == corner.ArmIdA) && (p.ArmB.Id == corner.ArmIdB))
            : null;

    private JunctionRows RowsOf(GroundNode node)
    {
        if (!_junctions.TryGetValue(node.Id, out JunctionRows? rows))
        {
            rows = new JunctionRows(node);
            _junctions[node.Id] = rows;
        }

        return rows;
    }

    private sealed class JunctionRows(GroundNode node)
    {
        public GroundNode Node { get; } = node;

        public FilletSkipReason? JunctionSkip { get; set; }

        public List<PairRow> Pairs { get; } = [];

        public Dictionary<int, PairRow> ByCorner { get; } = [];
    }

    private sealed class PairRow(TaxiwayArm armA, TaxiwayArm armB, int? cornerId)
    {
        public TaxiwayArm ArmA { get; } = armA;

        public TaxiwayArm ArmB { get; } = armB;

        public int? CornerId { get; } = cornerId;

        public FilletCornerOutcome? Outcome { get; set; }

        public FilletSkipReason? Skip { get; set; }

        public double? RequestedRadiusFt { get; set; }

        public double? AchievedRadiusFt { get; set; }
    }
}
