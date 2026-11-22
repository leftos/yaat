using System.Diagnostics.CodeAnalysis;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// Records what one fillet pass did at each junction and corner it visited, as the pass decides it, and builds the pass's
/// <see cref="FilletReport"/>. Recording never changes a decision: every call is made after the decision it records.
/// </summary>
internal sealed class FilletReportCollector
{
    private readonly SortedDictionary<int, JunctionRows> _junctions = [];

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
