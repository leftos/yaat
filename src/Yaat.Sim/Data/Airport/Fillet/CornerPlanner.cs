using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Data.Airport.Fillet;

internal static class CornerPlanner
{
    private static readonly ILogger Log = SimLog.CreateLogger("CornerPlanner");

    /// <summary>The smallest turn, degrees, a forced pair may make: below it there is no corner to fillet.</summary>
    internal const double MinForcedTurnDeg = 1.0;

    /// <summary>The nearest to the apex, feet, a pinned tangent is honoured: just past the stable-node snap window.</summary>
    internal const double MinPinnedTangentFt = FilletConstants.CoincidentNodeThresholdFt;

    /// <summary>How far short of its arm's far node, feet, a pinned tangent must stay to be honoured.</summary>
    internal const double PinnedTangentFarMarginFt = 5.0;

    /// <summary>
    /// The farthest from the apex, feet, a pinned tangent on <paramref name="arm"/> is honoured:
    /// <see cref="PinnedTangentFarMarginFt"/> short of the first node along the arm another taxiway joins, or of the arm's far
    /// node when that is nearer.
    /// </summary>
    internal static double PinFarLimitFt(TaxiwayArm arm)
    {
        double crossingFt = arm.Walk.Steps.Where(s => s.HasOtherTaxiways).Select(s => s.CumulativeDistFt).DefaultIfEmpty(arm.LengthFt).Min();
        return Math.Min(arm.LengthFt, crossingFt) - PinnedTangentFarMarginFt;
    }

    /// <summary>
    /// Plans every arm pair of the junction, applying <paramref name="edits"/>: a suppressed pair is returned with the
    /// suppressed pairs (so the junction node and every arm's stub stay), an override's radius replaces the edge types'
    /// maximum, and a forced pair skips the collinear, minimum-angle, near-hairpin and radius-floor checks unless it turns
    /// less than <see cref="MinForcedTurnDeg"/>.
    /// </summary>
    public static (
        IReadOnlyList<CornerSpec> Corners,
        IReadOnlyList<(int, int)> CollinearPairs,
        IReadOnlyList<(int, int)> SuppressedPairs
    ) PlanCorners(GroundNode junctionNode, IReadOnlyList<TaxiwayArm> arms, FilletCornerEdits edits, FilletReportCollector report)
    {
        var corners = new List<CornerSpec>();
        var collinear = new List<(int, int)>();
        var suppressed = new List<(int, int)>();
        for (int i = 0; i < arms.Count; i++)
        {
            for (int j = i + 1; j < arms.Count; j++)
            {
                TaxiwayArm armA = arms[i];
                TaxiwayArm armB = arms[j];
                PairPlan pair = PlanEditedPair(junctionNode, armA, armB, corners.Count, edits);
                if (pair.Corner is not null)
                {
                    CornerSpec planned = WithPins(junctionNode, (armA, armB), pair.Corner, edits.At(junctionNode.Id, armA.Id, armB.Id), report);
                    corners.Add(planned);
                    report.CornerPlanned(junctionNode, armA, armB, planned);
                    continue;
                }

                FilletSkipReason skip =
                    pair.Skip ?? throw new UnreachableException($"PlanPair returned neither a corner nor a skip at J{junctionNode.Id}");
                if (skip == FilletSkipReason.Collinear)
                {
                    collinear.Add((armA.Id, armB.Id));
                }
                else if (skip == FilletSkipReason.Suppressed)
                {
                    suppressed.Add((armA.Id, armB.Id));
                }

                report.PairSkipped(junctionNode, armA, armB, skip, pair.RequestedRadiusFt);
            }
        }

        return (RefuseConflictingPins(junctionNode, corners, report), collinear, suppressed);
    }

    /// <summary>
    /// <paramref name="corner"/> with the tangents <paramref name="edit"/> pins on its arms, each matched to its arm by taxiway
    /// name and bearing. A pin under <see cref="MinPinnedTangentFt"/> from the apex or past
    /// <see cref="PinnedTangentFarMarginFt"/> short of its arm's far node is refused, with the entry's other pin: the corner
    /// is returned as planned and the refusal recorded. This is the one place a pin's bounds are judged.
    /// </summary>
    private static CornerSpec WithPins(
        GroundNode junctionNode,
        (TaxiwayArm A, TaxiwayArm B) arms,
        CornerSpec corner,
        CornerFilletOverride? edit,
        FilletReportCollector report
    )
    {
        if (edit is null or { TangentAFt: null, TangentBFt: null })
        {
            return corner;
        }

        bool sameOrder =
            FilletOverrideResolver.ArmMatches(edit.TaxiwayA, edit.BearingA, arms.A)
            && FilletOverrideResolver.ArmMatches(edit.TaxiwayB, edit.BearingB, arms.B);
        double? pinA = sameOrder ? edit.TangentAFt : edit.TangentBFt;
        double? pinB = sameOrder ? edit.TangentBFt : edit.TangentAFt;
        (double a, double b) = BothPins(pinA, pinB, arms);
        if ((PinProblem(a, arms.A, pinA is null) ?? PinProblem(b, arms.B, pinB is null)) is { } problem)
        {
            report.PinRefused(new ResolvedCorner(junctionNode.Id, arms.A.Id, arms.B.Id), problem);
            LogPin("refused", corner, a, b, problem.DetailOrReason);
            return corner;
        }

        LogPin("applied", corner, a, b, $"arms {arms.A.LengthFt:F1}/{arms.B.LengthFt:F1} ft");
        return corner with { PinAFt = a, PinBFt = b };
    }

    /// <summary>
    /// The pins on both arms: a pin on one arm only is mirrored onto the other at the same tangent length (a symmetric
    /// fillet), clamped to that arm's <see cref="PinFarLimitFt"/>.
    /// </summary>
    private static (double A, double B) BothPins(double? pinA, double? pinB, (TaxiwayArm A, TaxiwayArm B) arms) =>
        (pinA, pinB) switch
        {
            ({ } a, { } b) => (a, b),
            ({ } a, null) => (a, Math.Min(a, PinFarLimitFt(arms.B))),
            (null, { } b) => (Math.Min(b, PinFarLimitFt(arms.A)), b),
            _ => throw new UnreachableException("BothPins called with no pin"),
        };

    /// <summary>
    /// Why a pin of <paramref name="pin"/> on <paramref name="arm"/> is refused, or null when it is honoured; a refusal of a
    /// <paramref name="mirrored"/> pin names the arm it was mirrored onto.
    /// </summary>
    private static PinRefusal? PinProblem(double pin, TaxiwayArm arm, bool mirrored)
    {
        string? mirrorNote = mirrored ? $"mirrored pin {pin:F2} ft on {arm.TaxiwayName}" : null;
        if (PinBoundsProblem(pin, arm) is { } reason)
        {
            return new PinRefusal(reason, null, (mirrorNote is null) ? null : $"{mirrorNote} refused ({reason})");
        }

        if (NodeNearPin(pin, arm) is not { } step)
        {
            return null;
        }

        string what = mirrorNote ?? $"pin {pin:F2} ft on {arm.TaxiwayName}";
        double offFt = Math.Abs(step.CumulativeDistFt - pin);
        return new PinRefusal(FilletSkipReason.PinOnNode, null, $"{what} lies {offFt:F2} ft from node {step.FarNode.Id}: refused (PinOnNode)");
    }

    /// <summary>Why a pin of <paramref name="pin"/> on <paramref name="arm"/> is out of bounds, or null when it is within them.</summary>
    private static FilletSkipReason? PinBoundsProblem(double pin, TaxiwayArm arm)
    {
        if (pin < MinPinnedTangentFt)
        {
            return FilletSkipReason.PinTooClose;
        }

        if (pin > (arm.LengthFt - PinnedTangentFarMarginFt))
        {
            return FilletSkipReason.PinBeyondArm;
        }

        return (pin > PinFarLimitFt(arm)) ? FilletSkipReason.PinPastIntersection : null;
    }

    /// <summary>
    /// The nearest interior node of the arm's walk (a shape point or an intersection) within
    /// <see cref="GeometricAdmissibility.NoOpEdgeThresholdNm"/> of the pin, or null.
    /// </summary>
    private static TaxiwayWalk.WalkStep? NodeNearPin(double pin, TaxiwayArm arm)
    {
        const double noOpFt = GeometricAdmissibility.NoOpEdgeThresholdNm * GeoMath.FeetPerNm;
        return arm
            .Walk.Steps.Take(arm.Walk.Steps.Count - 1)
            .Where(s => Math.Abs(s.CumulativeDistFt - pin) < noOpFt)
            .OrderBy(s => Math.Abs(s.CumulativeDistFt - pin))
            .FirstOrDefault();
    }

    /// <summary>
    /// <paramref name="corners"/> with the pins of every two corners whose pins on one arm lie within
    /// <see cref="FilletConstants.CoincidentNodeThresholdFt"/> of each other refused (<see cref="FilletSkipReason.PinConflict"/>):
    /// neither is merged into the other.
    /// </summary>
    private static List<CornerSpec> RefuseConflictingPins(GroundNode junctionNode, List<CornerSpec> corners, FilletReportCollector report)
    {
        HashSet<int> conflicted = ConflictingCornerIds(corners);
        if (conflicted.Count == 0)
        {
            return corners;
        }

        List<CornerSpec> result = [];
        foreach (CornerSpec corner in corners)
        {
            if (!conflicted.Contains(corner.CornerId))
            {
                result.Add(corner);
                continue;
            }

            report.PinRefused(
                new ResolvedCorner(junctionNode.Id, corner.ArmIdA, corner.ArmIdB),
                new PinRefusal(FilletSkipReason.PinConflict, null, null)
            );
            LogPin("refused", corner, corner.PinAFt, corner.PinBFt, nameof(FilletSkipReason.PinConflict));
            result.Add(corner with { PinAFt = null, PinBFt = null });
        }

        return result;
    }

    private static HashSet<int> ConflictingCornerIds(List<CornerSpec> corners)
    {
        var conflicted = new HashSet<int>();
        foreach (int armId in corners.SelectMany(c => new[] { c.ArmIdA, c.ArmIdB }).Distinct())
        {
            List<(int CornerId, double PinFt)> pins = PinsOn(corners, armId);
            for (int i = 0; i < pins.Count; i++)
            {
                foreach ((int otherId, double otherPin) in pins.Skip(i + 1))
                {
                    if (Math.Abs(pins[i].PinFt - otherPin) <= FilletConstants.CoincidentNodeThresholdFt)
                    {
                        conflicted.Add(pins[i].CornerId);
                        conflicted.Add(otherId);
                    }
                }
            }
        }

        return conflicted;
    }

    /// <summary>The pin each of <paramref name="corners"/> that pins arm <paramref name="armId"/> puts on it, by corner, in order.</summary>
    private static List<(int CornerId, double PinFt)> PinsOn(List<CornerSpec> corners, int armId)
    {
        List<(int CornerId, double PinFt)> pins = [];
        foreach (CornerSpec corner in corners)
        {
            if (corner.PinOn(armId) is { } pin)
            {
                pins.Add((corner.CornerId, pin));
            }
        }

        return pins;
    }

    private static void LogPin(string what, CornerSpec corner, double? pinA, double? pinB, string detail)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "Pinned tangents {What} at J{Junction} arms {A}/{B}: {PinA}/{PinB} ft ({Detail})",
                what,
                corner.JunctionNodeId,
                corner.ArmIdA,
                corner.ArmIdB,
                pinA,
                pinB,
                detail
            );
        }
    }

    /// <summary>The corner the arm pair gets, or why it gets none (with the radius chosen before a radius-floor skip).</summary>
    private readonly record struct PairPlan(CornerSpec? Corner, FilletSkipReason? Skip, double? RequestedRadiusFt);

    private static PairPlan PlanEditedPair(GroundNode junctionNode, TaxiwayArm armA, TaxiwayArm armB, int cornerId, FilletCornerEdits edits)
    {
        CornerFilletOverride? edit = edits.At(junctionNode.Id, armA.Id, armB.Id);
        return (edit is { Suppress: true })
            ? new PairPlan(null, FilletSkipReason.Suppressed, null)
            : PlanPair(junctionNode, armA, armB, cornerId, edit);
    }

    private static PairPlan PlanPair(GroundNode junctionNode, TaxiwayArm armA, TaxiwayArm armB, int cornerId, CornerFilletOverride? edit)
    {
        bool force = edit is { Force: true };
        double turnAngle = FilletGeometry.ComputeTurnAngle(armA.BearingFromJunctionDeg, armB.BearingFromJunctionDeg);
        if (EarlySkip(junctionNode, armA, armB, force, turnAngle) is { } early)
        {
            return new PairPlan(null, early, null);
        }

        double requestedRadiusFt = edit?.RadiusFt ?? FilletGeometry.SelectMaxRadius(armA.RootEdge, armB.RootEdge, turnAngle);
        double idealTangentFt = IdealTangentFt(armA, armB, turnAngle, requestedRadiusFt);
        bool belowFloor = (idealTangentFt / Math.Tan((turnAngle / 2.0) * (Math.PI / 180.0))) < FilletConstants.RadiusFloorFt;
        if ((!force) && belowFloor)
        {
            return new PairPlan(null, FilletSkipReason.RadiusFloor, requestedRadiusFt);
        }

        bool plannedOnlyByForce = (AngleSkip(turnAngle) is not null) || belowFloor || (!FilletEligibility.IsEligible(junctionNode));
        var corner = new CornerSpec(
            CornerId: cornerId,
            JunctionNodeId: junctionNode.Id,
            ArmIdA: armA.Id,
            ArmIdB: armB.Id,
            EdgeA: armA.RootEdge,
            EdgeB: armB.RootEdge,
            TurnAngleDeg: turnAngle,
            RequestedRadiusFt: requestedRadiusFt,
            IdealTangentFt: idealTangentFt,
            BearingAToJunctionDeg: armA.BearingFromJunctionDeg,
            BearingBToJunctionDeg: armB.BearingFromJunctionDeg,
            Force: ForceOf(force, plannedOnlyByForce)
        );
        return new PairPlan(corner, null, null);
    }

    /// <summary>
    /// The skip the pair gets before any radius is chosen: two arms reaching one node (never forceable), a failed angle
    /// check (unless forced), or, for a forced pair, a turn below <see cref="MinForcedTurnDeg"/>.
    /// </summary>
    private static FilletSkipReason? EarlySkip(GroundNode junctionNode, TaxiwayArm armA, TaxiwayArm armB, bool force, double turnAngle)
    {
        if (armA.RootEdge.OtherNode(junctionNode).Id == armB.RootEdge.OtherNode(junctionNode).Id)
        {
            return FilletSkipReason.SameFarNode;
        }

        if (!force)
        {
            return AngleSkip(turnAngle);
        }

        return (turnAngle < MinForcedTurnDeg) ? FilletSkipReason.NoTurn : null;
    }

    /// <summary>How an override forces the corner: not at all, as a corner the planner plans anyway, or only by the force.</summary>
    private static CornerForce ForceOf(bool force, bool plannedOnlyByForce)
    {
        if (!force)
        {
            return CornerForce.None;
        }

        return plannedOnlyByForce ? CornerForce.Added : CornerForce.Kept;
    }

    /// <summary>The angle check a turn of <paramref name="turnAngle"/> degrees fails, or null when it is a corner to fillet.</summary>
    private static FilletSkipReason? AngleSkip(double turnAngle)
    {
        if (turnAngle < FilletConstants.CollinearThresholdDeg)
        {
            return FilletSkipReason.Collinear;
        }

        if (turnAngle < FilletConstants.MinFilletAngleDeg)
        {
            return FilletSkipReason.BelowMinAngle;
        }

        // Near-hairpin: the arms are within (180 − threshold)° of parallel — two taxiways
        // grazing at an acute angle, not a real turn. The fillet would collapse to an
        // un-taxiable straight chord (a corner no aircraft can fly through), so emit no
        // corner; the pair stays connected via the taxiway that already bridges them.
        return (turnAngle > FilletConstants.NearHairpinThresholdDeg) ? FilletSkipReason.NearHairpin : null;
    }

    /// <summary>The tangent distance a fillet of <paramref name="radiusFt"/> needs at the turn, capped by both arms.</summary>
    private static double IdealTangentFt(TaxiwayArm armA, TaxiwayArm armB, double turnAngle, double radiusFt)
    {
        bool capA = FilletEligibility.IsEligible(armA.Walk.TerminalNode) && (armA.Walk.TerminalNode.SourceIntersectionPosition is null);
        bool capB = FilletEligibility.IsEligible(armB.Walk.TerminalNode) && (armB.Walk.TerminalNode.SourceIntersectionPosition is null);
        return FilletGeometry.ComputeIdealTangentFt(
            turnAngle,
            radiusFt,
            armA.LengthFt,
            armB.LengthFt,
            capA,
            capB,
            armA.IntersectionCapFt,
            armB.IntersectionCapFt
        );
    }
}
