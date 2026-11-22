using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Pinned tangent points (a corner fillet override's tangentFt) applied by the Standard pass on the committed layouts.</summary>
public class FilletPinnedTangentTests
{
    /// <summary>How close a pinned cut must sit to its pinned distance along the arm, and a pinned radius to its value, feet.</summary>
    private const double ToleranceFt = 1e-6;

    [Fact]
    public void PinWithinBounds_PutsEachCutAtItsPinnedDistance_AndTheArcEndsThere()
    {
        CornerCase corner = FirstCase(["oak"], c => Turns(c, 45, 135) && InBounds(c.ArmA, 40.0) && InBounds(c.ArmB, 45.0) && (c.ArmB.LengthFt >= 80));
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 40.0, 45.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        AssertArcEndsAt(ArcOf(layout, corner.Row), At(corner, corner.ArmA, 40.0), At(corner, corner.ArmB, 45.0));
    }

    [Fact]
    public void PinUnderFiveFeet_IsRefusedPinTooClose_AndTheCornerBuildsAsGenerated() =>
        AssertPinRefused(c => (4.9, 40.0), FilletSkipReason.PinTooClose);

    [Fact]
    public void PinPastTheArmLessFiveFeet_IsRefusedPinBeyondArm_AndTheCornerBuildsAsGenerated() =>
        AssertPinRefused(c => (40.0, c.ArmB.LengthFt - 4.9), FilletSkipReason.PinBeyondArm);

    [Fact]
    public void PinBeyond150Ft_WithinTheArm_IsHonoured()
    {
        CornerCase corner = FirstCase(["oak", "sfo"], c => Turns(c, 60, 120) && InBounds(c.ArmA, 160.0) && InBounds(c.ArmB, 160.0));
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 160.0, 160.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        AssertArcEndsAt(ArcOf(layout, corner.Row), At(corner, corner.ArmA, 160.0), At(corner, corner.ArmB, 160.0));
    }

    [Fact]
    public void PinnedAndUnpinnedCutsCoalescing_OnASharedArm_KeepThePinnedCut()
    {
        SharedArmCase shared = FirstSharedArmCase();
        double pin = shared.SharedCutFt + 3.0;
        (AirportGroundLayout layout, FilletReport report) = Run(shared.P.Pre, PinnedOnShared(shared, pin));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        LatLon pinned = At(shared.P, shared.Shared, pin);
        Assert.Contains(ArcOf(layout, shared.P.Row).Nodes, n => FeetBetween(n.Position, pinned) <= ToleranceFt);
        Assert.Contains(ArcOf(layout, shared.Q.Row).Nodes, n => FeetBetween(n.Position, pinned) <= ToleranceFt);
    }

    [Fact]
    public void TwoConflictingPins_OnOneArm_AreBothRefused_AndBothCornersBuildAsGenerated()
    {
        SharedArmCase shared = FirstSharedArmCase();
        CornerFilletOverride p = PinnedOnShared(shared, shared.SharedCutFt + 3.0);
        CornerFilletOverride q = PinnedOn(shared.Q, shared.Shared, shared.SharedCutFt, shared.QOtherCutFt);
        (AirportGroundLayout layout, FilletReport report) = Run(shared.P.Pre, p, q);

        Assert.All(
            report.Overrides,
            o => Assert.Equal((FilletOverrideEffect.Rejected, FilletSkipReason.PinConflict), (o.Effect!.Value, o.RejectedReason))
        );
        Assert.Equal(PlainSerialized(shared.P.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void PinWithinFiveFeetOfAStableNode_IsNotRedirectedOntoIt()
    {
        (CornerCase corner, TaxiwayWalk.WalkStep stable) = FirstStableStepCase();
        double pin = stable.CumulativeDistFt - 2.0;
        (AirportGroundLayout layout, _) = Run(corner.Pre, Pinned(corner, pin, pin));

        GroundArc arc = ArcOf(layout, corner.Row);
        GroundNode end = Assert.Single(arc.Nodes, n => FeetBetween(n.Position, At(corner, corner.ArmA, pin)) <= ToleranceFt);
        Assert.NotEqual(stable.FarNode.Id, end.Id);
        Assert.InRange(FeetBetween(end.Position, stable.FarNode.Position), 1.0, FilletConstants.CoincidentNodeThresholdFt);
    }

    [Fact]
    public void SuppressAndPin_OnOneEntry_SuppressesTheCorner_AndWarnsTheIgnoredEdits()
    {
        CornerCase corner = FirstCase(["oak"], c => (c.ArmA.LengthFt >= 50) && (c.ArmB.LengthFt >= 50));
        CornerFilletOverride suppress = FilletReportLayouts.OverrideFor(corner.Row) with { Suppress = true };
        (AirportGroundLayout layout, FilletReport report) = Run(
            corner.Pre,
            suppress with
            {
                RadiusFt = 25.0,
                Force = true,
                TangentAFt = 40.0,
                TangentBFt = 45.0,
            }
        );

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal(FilletOverrideEffect.Applied, row.Effect);
        Assert.Equal(["suppress wins: radiusFt, force and tangentFt ignored"], row.Warnings);
        Assert.Equal(FilletSkipReason.Suppressed, RowLike(report, corner.Row).SkipReason);
        Assert.Equal(GroundLayoutSerializer.Serialize(Run(corner.Pre, suppress).Layout), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void PinAlone_OnASkippedCorner_BuildsNothing()
    {
        CornerCase corner = OakJunction16WW2();
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 30.0, 30.0));

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal((FilletOverrideEffect.Rejected, FilletSkipReason.Collinear), (row.Effect!.Value, row.RejectedReason!.Value));
        Assert.Equal(FilletCornerOutcome.Skipped, RowLike(report, corner.Row).Outcome);
        Assert.Equal(PlainSerialized(corner.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void PinAndForce_OnASkippedCorner_BuildsTheCornerAtThePins()
    {
        CornerCase corner = OakJunction16WW2();
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 30.0, 30.0) with { Force = true });

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        Assert.Equal(FilletCornerOutcome.Built, RowLike(report, corner.Row).Outcome);
        AssertArcEndsAt(ArcOf(layout, corner.Row), At(corner, corner.ArmA, 30.0), At(corner, corner.ArmB, 30.0));
    }

    [Fact]
    public void BothArmsPinned_WithARadius_IgnoreTheRadius_AndWarn()
    {
        CornerCase corner = FirstCase(["oak"], c => Turns(c, 45, 135) && InBounds(c.ArmA, 40.0) && InBounds(c.ArmB, 45.0) && (c.ArmB.LengthFt >= 80));
        (AirportGroundLayout pinnedOnly, _) = Run(corner.Pre, Pinned(corner, 40.0, 45.0));
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 40.0, 45.0) with { RadiusFt = 10.0 });

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal(FilletOverrideEffect.Applied, row.Effect);
        Assert.Equal(["both tangents pinned: radiusFt ignored"], row.Warnings);
        double expected = ArcOf(pinnedOnly, corner.Row).MinRadiusOfCurvatureFt;
        Assert.True(expected > 20.0, $"pinned radius {expected}");
        Assert.InRange(ArcOf(layout, corner.Row).MinRadiusOfCurvatureFt, expected - ToleranceFt, expected + ToleranceFt);
    }

    [Fact]
    public void AdjacentJunctionPins_LeavingAShortStraight_BuildWithAWarningOnBothRows()
    {
        (CornerCase p, TaxiwayArm armP, CornerCase q, TaxiwayArm armQ, double straightFt) = SfoJ34B4ToJ121();
        double pin = (straightFt - 25.0) / 2.0;
        (_, FilletReport report) = Run(p.Pre, PinnedOne(p, armP, pin), PinnedOne(q, armQ, pin));

        Assert.All(report.Overrides, o => Assert.Equal(FilletOverrideEffect.Applied, o.Effect));
        foreach (CornerCase corner in new[] { p, q })
        {
            FilletCornerReport row = RowLike(report, corner.Row);
            Assert.Equal(FilletCornerOutcome.Built, row.Outcome);
            Assert.Contains(row.Warnings, w => w.StartsWith(PlanWarning.PinnedShortStraight, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void PinExactlyAtTheFarLimitBeforeACrossing_IsApplied()
    {
        CornerCase corner = OakJ300B5Ramp();
        double limit = FirstCrossingFt(corner.ArmA) - CornerPlanner.PinnedTangentFarMarginFt;
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, limit, limit));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        AssertArcEndsAt(ArcOf(layout, corner.Row), At(corner, corner.ArmA, limit), At(corner, corner.ArmB, limit));
    }

    [Fact]
    public void PinOneFootPastTheFirstCrossing_IsRefusedPinPastIntersection() =>
        AssertRefused(
            OakJ300B5Ramp(),
            c => Pinned(c, FirstCrossingFt(c.ArmA) + 1.0, FirstCrossingFt(c.ArmA) - CornerPlanner.PinnedTangentFarMarginFt),
            FilletSkipReason.PinPastIntersection
        );

    [Fact]
    public void PinOfExactlyFiveFeet_IsApplied()
    {
        CornerCase corner = FirstCase(
            ["oak", "sfo"],
            c => Turns(c, 20, 40) && (c.ArmA.Walk.Steps[0].CumulativeDistFt >= 10.0) && (c.ArmB.Walk.Steps[0].CumulativeDistFt >= 10.0)
        );
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 5.0, 5.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        AssertArcEndsAt(ArcOf(layout, corner.Row), At(corner, corner.ArmA, 5.0), At(corner, corner.ArmB, 5.0));
    }

    [Fact]
    public void PinTenFeetPastTheNeighboursCut_IsRefusedPinOverlapsNeighbour()
    {
        (CornerCase p, TaxiwayArm armP, CornerCase q, TaxiwayArm armQ, double straightFt) = SfoJ34B4ToJ121();
        double neighbourCutFt = FarthestPlainCutFt(Run(q.Pre).Layout, q, armQ) ?? throw new InvalidOperationException("no neighbour cut");
        double pin = straightFt - neighbourCutFt + 10.0;
        Assert.True(InBounds(armP, pin) && InBounds(p.Other(armP), pin), $"pin {pin} ft");

        AssertRefused(p, c => PinnedOne(c, armP, pin), FilletSkipReason.PinOverlapsNeighbour);
    }

    [Fact]
    public void PinThatRemovesASharedSizedCut_LeavesTheCrossJunctionNeighboursRadii()
    {
        (CornerCase p, TaxiwayArm armP, _, _, _) = SfoJ34B4ToJ121();
        LatLon sized = At(p, armP, PlainCutFt(Run(p.Pre).Layout, p, armP) ?? throw new InvalidOperationException("no sized cut on B4"));
        (AirportGroundLayout layout, FilletReport report) = Run(p.Pre, Pinned(p, 100.0, 100.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        Assert.DoesNotContain(layout.Nodes.Values, n => FeetBetween(n.Position, sized) <= ToleranceFt);
        foreach (FilletCornerReport plain in Standard("sfo").Corners.Where(c => (c.Outcome == FilletCornerOutcome.Built) && (c.TaxiwayA is not null)))
        {
            if (SamePair(plain, p.Row))
            {
                continue;
            }

            FilletCornerReport after = RowLike(report, plain);
            Assert.Equal(FilletCornerOutcome.Built, after.Outcome);
            Assert.InRange(after.AchievedRadiusFt!.Value, plain.AchievedRadiusFt!.Value - ToleranceFt, plain.AchievedRadiusFt.Value + ToleranceFt);
        }
    }

    [Fact]
    public void UnequalPinsThatBuildAChord_AreRefusedUnsteerable()
    {
        CornerCase corner = OakJ136UT();
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 100.0, 20.0));

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal((FilletOverrideEffect.Rejected, FilletSkipReason.Unsteerable), (row.Effect!.Value, row.RejectedReason!.Value));
        Assert.Equal(0.0, row.RejectedRadiusFt);
        Assert.Equal(PlainSerialized(corner.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void EightFootPinsAtARightAngle_AreRefusedUnsteerableWithTheirRadius()
    {
        CornerCase corner = OakJ136UT();
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 8.0, 8.0));

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal((FilletOverrideEffect.Rejected, FilletSkipReason.Unsteerable), (row.Effect!.Value, row.RejectedReason!.Value));
        Assert.True(
            (row.RejectedRadiusFt > 0.0) && (row.RejectedRadiusFt < GeometricAdmissibility.MinSteerableArcRadiusFt),
            $"{row.RejectedRadiusFt}"
        );
        Assert.Equal(PlainSerialized(corner.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void NearEqualPinsAtARightAngle_AreAppliedWithASteerableRadius()
    {
        CornerCase corner = OakJ136UT();
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 45.0, 40.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        Assert.True(ArcOf(layout, corner.Row).MinRadiusOfCurvatureFt >= GeometricAdmissibility.MinSteerableArcRadiusFt);
    }

    [Fact]
    public void PinHalfAFootFromAShapePoint_IsRefusedPinOnNode_NamingTheNode()
    {
        CornerCase corner = OakJ136UT();
        TaxiwayWalk.WalkStep shape = corner.ArmA.Walk.Steps.SkipLast(1).First(s => !s.HasOtherTaxiways);
        double pin = shape.CumulativeDistFt + 0.5;
        FilletOverrideReport row = AssertRefused(corner, c => Pinned(c, pin, pin), FilletSkipReason.PinOnNode);

        Assert.Contains(row.Warnings, w => w.Contains($"node {shape.FarNode.Id}", StringComparison.Ordinal));
    }

    [Fact]
    public void IdenticalPins_OnOneArm_AreBothRefusedPinConflict()
    {
        SharedArmCase shared = FirstSharedArmCase();
        CornerFilletOverride p = PinnedOnShared(shared, shared.SharedCutFt);
        CornerFilletOverride q = PinnedOn(shared.Q, shared.Shared, shared.SharedCutFt, shared.QOtherCutFt);
        (AirportGroundLayout layout, FilletReport report) = Run(shared.P.Pre, p, q);

        AssertAllRefused(report, FilletSkipReason.PinConflict);
        Assert.Equal(PlainSerialized(shared.P.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void FiveFootPins_OnArmsUnderSixtyDegreesApart_AreRefusedPinConflict_WithNoSelfLoop()
    {
        CornerCase corner = Named("oak", 28, "W6", "W7", _ => true);
        double apartDeg = GeoMath.AbsBearingDifference(corner.ArmA.BearingFromJunctionDeg, corner.ArmB.BearingFromJunctionDeg);
        Assert.True(apartDeg < 60.0, $"W6/W7 arms {apartDeg} degrees apart");

        AssertRefused(corner, c => Pinned(c, 5.0, 5.0), FilletSkipReason.PinConflict);
    }

    [Fact]
    public void PinnedCutsOfTwoJunctions_WithinFiveFeet_AreBothRefusedPinConflict()
    {
        (CornerCase p, TaxiwayArm armP, CornerCase q, TaxiwayArm armQ, double straightFt) = SfoJ34B4ToJ121();
        (AirportGroundLayout layout, FilletReport report) = Run(p.Pre, PinnedOne(p, armP, straightFt / 2.0), PinnedOne(q, armQ, straightFt / 2.0));

        Assert.Equal(2, report.Overrides.Count);
        AssertAllRefused(report, FilletSkipReason.PinConflict);
        Assert.Equal(PlainSerialized(p.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void PinnedCutsChainedThroughAnUnpinnedCut_AreBothRefusedPinConflict_AndNoPinMoves()
    {
        (CornerCase p, TaxiwayArm armP, CornerCase q, TaxiwayArm armQ, double straightFt) = SfoJ34B4ToJ121();
        AirportGroundLayout plain = Run(p.Pre).Layout;
        List<double> sharedCuts = PlainCutsAlongFt(plain, q, armQ);
        Assert.True((sharedCuts.Count >= 2) && (sharedCuts.Max() - sharedCuts.Min() <= ToleranceFt), $"J121 B4 cuts {string.Join(", ", sharedCuts)}");
        double unpinnedFt = sharedCuts[0];
        double pinQ = unpinnedFt - 4.0;
        double pinP = straightFt - (unpinnedFt + 4.0);
        double otherP = PlainCutFt(plain, p, p.Other(armP)) ?? throw new InvalidOperationException("no cut on P's other arm");
        double otherQ = FarthestPlainCutFt(plain, q, q.Other(armQ)) ?? throw new InvalidOperationException("no cut on Q's other arm");
        Assert.True(InBounds(armP, pinP) && InBounds(armQ, pinQ), $"pins {pinP} / {pinQ} ft");

        (AirportGroundLayout layout, FilletReport report) = Run(p.Pre, PinnedOn(p, armP, pinP, otherP), PinnedOn(q, armQ, pinQ, otherQ));

        Assert.Equal(2, report.Overrides.Count);
        AssertAllRefused(report, FilletSkipReason.PinConflict);
        Assert.Equal(PlainSerialized(p.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void PinRefusedOverlappingTheNeighbour_LeavesTheNeighboursForceStanding()
    {
        (CornerCase p, TaxiwayArm armP, CornerCase q, TaxiwayArm armQ, double straightFt) = SfoJ34B4ToJ121();
        double neighbourCutFt = FarthestPlainCutFt(Run(q.Pre).Layout, q, armQ) ?? throw new InvalidOperationException("no neighbour cut");
        CornerFilletOverride pin = PinnedOne(p, armP, straightFt - neighbourCutFt + 10.0);
        CornerFilletOverride force = FilletReportLayouts.OverrideFor(q.Row) with { Force = true };

        (AirportGroundLayout layout, FilletReport report) = Run(p.Pre, pin, force);

        FilletOverrideReport pinRow = Assert.Single(report.Overrides, o => o.Override == pin);
        Assert.Equal((FilletOverrideEffect.Rejected, FilletSkipReason.PinOverlapsNeighbour), (pinRow.Effect!.Value, pinRow.RejectedReason!.Value));
        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides, o => o.Override == force).Effect);
        Assert.Equal(GroundLayoutSerializer.Serialize(Run(p.Pre, force).Layout), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void DedupRank_PutsAPinnedCornerAboveEveryUnpinnedOne()
    {
        (_, CornerSpec single, CornerSpec two, _) = OakJunctionWithBothNameKinds();

        Assert.True(FilletPlanBuilder.DedupRank(PinnedForced(two)) > FilletPlanBuilder.DedupRank(single));
        Assert.True(FilletPlanBuilder.DedupRank(single with { PinAFt = 30.0, PinBFt = 30.0 }) > FilletPlanBuilder.DedupRank(single));
        Assert.True(FilletPlanBuilder.DedupRank(two with { PinAFt = 30.0 }) > FilletPlanBuilder.DedupRank(two));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PinnedForcedTwoNameCorner_WinsItsEndpointPair_FromAnUnpinnedSingleNameOne(bool pinnedFirst)
    {
        (JunctionPlan junction, CornerSpec single, CornerSpec two, FilletReportCollector report) = OakJunctionWithBothNameKinds();
        CornerSpec pinned = PinnedForced(two);
        JunctionPlan withPin = junction with { Corners = [.. junction.Corners.Select(c => (c.CornerId == two.CornerId) ? pinned : c)] };
        var endA = new FilletEndpoint.Cut(new CutId(1));
        var endB = new FilletEndpoint.Cut(new CutId(2));
        var pinnedOp = new CornerArcOp(junction.JunctionNodeId, pinned.CornerId, endA, endB);
        var singleOp = new CornerArcOp(junction.JunctionNodeId, single.CornerId, endB, endA);

        List<CornerArcOp> kept = FilletPlanBuilder.DedupCornerArcs(pinnedFirst ? [pinnedOp, singleOp] : [singleOp, pinnedOp], [withPin], report);

        Assert.Equal(pinnedOp, Assert.Single(kept));
        Assert.Equal(FilletSkipReason.Deduplicated, report.SkipOf(ResolvedOf(single)));
        Assert.Null(report.SkipOf(ResolvedOf(pinned)));
    }

    [Fact]
    public void OneArmPin_IsMirroredOntoTheOtherArm_AsASymmetricFillet()
    {
        CornerCase corner = Named("oak", 42, "W1", "RWY30/12", _ => true);
        Assert.InRange(FilletGeometry.ComputeTurnAngle(corner.ArmA.BearingFromJunctionDeg, corner.ArmB.BearingFromJunctionDeg), 85.0, 95.0);
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, PinnedOne(corner, corner.ArmA, 30.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        GroundArc arc = ArcOf(layout, corner.Row);
        AssertArcEndsAt(arc, At(corner, corner.ArmA, 30.0), At(corner, corner.ArmB, 30.0));
        (AirportGroundLayout symmetric, _) = Run(corner.Pre, Pinned(corner, 30.0, 30.0));
        double expected = ArcOf(symmetric, corner.Row).MinRadiusOfCurvatureFt;
        Assert.InRange(arc.MinRadiusOfCurvatureFt, expected - ToleranceFt, expected + ToleranceFt);
    }

    [Fact]
    public void OneArmPin_WhoseMirrorPassesTheOtherArmsLimit_IsClampedToThatLimit()
    {
        CornerCase corner = OakJ300B5Ramp();
        double limit = FirstCrossingFt(corner.ArmA) - CornerPlanner.PinnedTangentFarMarginFt;
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, PinnedOne(corner, corner.ArmB, limit + 10.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        AssertArcEndsAt(ArcOf(layout, corner.Row), At(corner, corner.ArmA, limit), At(corner, corner.ArmB, limit + 10.0));
    }

    [Fact]
    public void BothArmsPinned_MirrorNothing()
    {
        CornerCase corner = OakJ136UT();
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 40.0, 45.0));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        AssertArcEndsAt(ArcOf(layout, corner.Row), At(corner, corner.ArmA, 40.0), At(corner, corner.ArmB, 45.0));
    }

    [Fact]
    public void PinnedArcTheNormalizerWouldDrop_IsRefusedBeforeItIsBuilt()
    {
        CornerCase corner = OakJ136UT();
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, 100.0, 20.0) with { Force = true });

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal((FilletOverrideEffect.Rejected, FilletSkipReason.Unsteerable), (row.Effect!.Value, row.RejectedReason!.Value));
        Assert.DoesNotContain(
            report.Corners,
            c => (c.JunctionNodeId == corner.Row.JunctionNodeId) && (c.SkipReason == FilletSkipReason.DegenerateRadius)
        );
        Assert.Equal(PlainSerialized(corner.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    [Fact]
    public void PinnedCorner_LeavesItsNeighboursRadii()
    {
        (CornerCase p, double pinA, double pinB) = FirstNeighbourCase();
        (_, FilletReport report) = Run(p.Pre, Pinned(p, pinA, pinB));

        Assert.Equal(FilletOverrideEffect.Applied, Assert.Single(report.Overrides).Effect);
        List<FilletCornerReport> others = [.. BuiltRowsAt(p.Row.JunctionNodeId).Where(r => r != p.Row)];
        Assert.NotEmpty(others);
        foreach (FilletCornerReport plain in others)
        {
            FilletCornerReport after = RowLike(report, plain);
            Assert.Equal(FilletCornerOutcome.Built, after.Outcome);
            Assert.InRange(after.AchievedRadiusFt!.Value, plain.AchievedRadiusFt!.Value - ToleranceFt, plain.AchievedRadiusFt.Value + ToleranceFt);
        }
    }

    [Fact]
    public void SamePinsTwice_BuildByteIdenticalLayouts()
    {
        AirportGroundLayout pre = Committed("oak");
        CornerFilletOverride[] entries =
        [
            .. BuiltCases("oak").Where(c => (c.ArmA.LengthFt >= 45) && (c.ArmB.LengthFt >= 45)).Take(20).Select(c => Pinned(c, 30.0, 35.0)),
        ];

        (AirportGroundLayout first, _) = Run(pre, entries);
        (AirportGroundLayout second, _) = Run(pre, entries);

        Assert.Equal(GroundLayoutSerializer.Serialize(first), GroundLayoutSerializer.Serialize(second));
    }

    [Theory]
    [InlineData("oak", "69196841DD96484943A523AF19C4733E2F87653B6420350A")]
    [InlineData("sfo", "D03343A7F31BF919ECA693D2AA647690DC16FBF1A4DFE385")]
    public void NoEntries_BuildTheBaseLayout(string shortId, string baseFingerprint)
    {
        AirportGroundLayout layout = LayoutCloner.DeepClone(Committed(shortId));
        new FilletArcGenerator().Apply(layout, CornerFilletOverrides.None);

        Assert.Equal(baseFingerprint, Fingerprint(layout));
    }

    /// <summary>A built corner with a taxiway pair no other arm pair of its junction carries, and its two pre-fillet arms.</summary>
    private sealed record CornerCase(AirportGroundLayout Pre, FilletCornerReport Row, GroundNode Apex, TaxiwayArm ArmA, TaxiwayArm ArmB)
    {
        public TaxiwayArm Other(TaxiwayArm arm) => (arm.Id == ArmA.Id) ? ArmB : ArmA;
    }

    /// <summary>Two built corners P and Q of one junction sharing one cut on <see cref="Shared"/>, at their plain distances.</summary>
    private sealed record SharedArmCase(CornerCase P, CornerCase Q, TaxiwayArm Shared, double SharedCutFt, double POtherCutFt, double QOtherCutFt);

    private static void AssertPinRefused(Func<CornerCase, (double A, double B)> pins, FilletSkipReason reason)
    {
        CornerCase corner = FirstCase(["oak"], c => Turns(c, 45, 135) && InBounds(c.ArmA, 40.0) && InBounds(c.ArmB, 45.0) && (c.ArmB.LengthFt >= 80));
        (double a, double b) = pins(corner);
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, Pinned(corner, a, b));

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal((FilletOverrideEffect.Rejected, reason), (row.Effect!.Value, row.RejectedReason!.Value));
        Assert.Equal(PlainSerialized(corner.Pre), GroundLayoutSerializer.Serialize(layout));
    }

    private static CornerCase OakJunction16WW2()
    {
        AirportGroundLayout pre = Committed("oak");
        FilletCornerReport row = Standard("oak").Corners.First(c => (c.JunctionNodeId == 16) && (c.TaxiwayA == "W") && (c.TaxiwayB == "W2"));
        Assert.Equal(FilletSkipReason.Collinear, row.SkipReason);
        CornerCase corner = CaseOf(pre, row);
        Assert.True((corner.ArmA.LengthFt >= 35) && (corner.ArmB.LengthFt >= 35), $"arms {corner.ArmA.LengthFt} / {corner.ArmB.LengthFt} ft");
        return corner;
    }

    private static SharedArmCase FirstSharedArmCase()
    {
        foreach (string shortId in new[] { "oak", "sfo" })
        {
            AirportGroundLayout plain = Run(Committed(shortId)).Layout;
            List<CornerCase> cases = BuiltCases(shortId);
            foreach (CornerCase p in cases.Where(c => Turns(c, 30, 150)))
            {
                foreach (CornerCase q in cases.Where(c => (c.Row.JunctionNodeId == p.Row.JunctionNodeId) && (c.Row != p.Row) && Turns(c, 30, 150)))
                {
                    if (SharedArmCaseOf(plain, p, q) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        throw new InvalidOperationException("no junction with two built corners sharing one arm cut");
    }

    private static SharedArmCase? SharedArmCaseOf(AirportGroundLayout plain, CornerCase p, CornerCase q)
    {
        TaxiwayArm? shared = new[] { p.ArmA, p.ArmB }.FirstOrDefault(a => (a.Id == q.ArmA.Id) || (a.Id == q.ArmB.Id));
        if (shared is null)
        {
            return null;
        }

        double? sharedP = PlainCutFt(plain, p, shared);
        double? sharedQ = PlainCutFt(plain, q, q.ArmA.Id == shared.Id ? q.ArmA : q.ArmB);
        double? otherP = PlainCutFt(plain, p, p.Other(shared));
        double? otherQ = PlainCutFt(plain, q, q.Other(q.ArmA.Id == shared.Id ? q.ArmA : q.ArmB));
        if ((sharedP is not { } s) || (sharedQ is not { } sq) || (Math.Abs(s - sq) > 1e-6) || (otherP is not { } op) || (otherQ is not { } oq))
        {
            return null;
        }

        bool inBounds = InBounds(shared, s + 3.0) && InBounds(shared, s) && InBounds(p.Other(shared), op) && InBounds(q.Other(shared), oq);
        return inBounds ? new SharedArmCase(p, q, shared, s, op, oq) : null;
    }

    private static (CornerCase Corner, TaxiwayWalk.WalkStep Stable) FirstStableStepCase()
    {
        foreach (string shortId in new[] { "oak", "sfo" })
        {
            foreach (CornerCase corner in BuiltCases(shortId).Where(c => Turns(c, 45, 135) && (c.ArmB.LengthFt >= 50)))
            {
                IReadOnlyList<TaxiwayWalk.WalkStep> steps = corner.ArmA.Walk.Steps;
                TaxiwayWalk.WalkStep? stable = steps
                    .Take(steps.Count - 1)
                    .FirstOrDefault(s =>
                        FilletPlanCutRedirect.IsStableAnchorTarget(s.FarNode)
                        && (s.CumulativeDistFt >= 30.0)
                        && InBounds(corner.ArmA, s.CumulativeDistFt - 2.0)
                        && InBounds(corner.ArmB, s.CumulativeDistFt - 2.0)
                    );
                if (stable is not null)
                {
                    return (corner, stable);
                }
            }
        }

        throw new InvalidOperationException("no built corner whose arm passes a stable node");
    }

    /// <summary>
    /// The planned corners of the first OAK junction, by node id, that plans both a single-name corner and a two-name one,
    /// neither forced nor pinned, with the collector that recorded them.
    /// </summary>
    private static (JunctionPlan Junction, CornerSpec Single, CornerSpec Two, FilletReportCollector Report) OakJunctionWithBothNameKinds()
    {
        AirportGroundLayout copy = LayoutCloner.DeepClone(Committed("oak"));
        copy.RebuildAdjacencyLists();
        HashSet<int> manualArcNodes = ManualArcDetector.Detect(copy);
        var none = FilletCornerEdits.From([]);
        foreach (GroundNode node in copy.Nodes.Values.OrderBy(n => n.Id).Where(n => (n.Edges.Count >= 2) && !manualArcNodes.Contains(n.Id)))
        {
            if (!FilletEligibility.IsEligible(node, out bool preserve))
            {
                continue;
            }

            var report = new FilletReportCollector();
            JunctionPlan junction = JunctionClassifier.Classify(node, preserve, manualArcNodes, none, report);
            CornerSpec? single = junction.Corners.FirstOrDefault(c => c.EdgeA.SharesTaxiway(c.EdgeB));
            CornerSpec? two = junction.Corners.FirstOrDefault(c => !c.EdgeA.SharesTaxiway(c.EdgeB));
            if ((junction.Kind != JunctionKind.Skip) && (single is not null) && (two is not null))
            {
                Assert.False(single.IsPinned || single.IsForced || two.IsPinned || two.IsForced);
                return (junction, single, two, report);
            }
        }

        throw new InvalidOperationException("no OAK junction plans both a single-name and a two-name corner");
    }

    private static CornerSpec PinnedForced(CornerSpec corner) => corner with { PinAFt = 30.0, PinBFt = 35.0, Force = CornerForce.Kept };

    private static ResolvedCorner ResolvedOf(CornerSpec corner) =>
        new(corner.JunctionNodeId, Math.Min(corner.ArmIdA, corner.ArmIdB), Math.Max(corner.ArmIdA, corner.ArmIdB));

    /// <summary>The corner's override pinning <paramref name="arm"/> alone.</summary>
    private static CornerFilletOverride PinnedOne(CornerCase corner, TaxiwayArm arm, double pin) =>
        FilletReportLayouts.OverrideFor(corner.Row) with
        {
            TangentAFt = (arm.Id == corner.ArmA.Id) ? pin : null,
            TangentBFt = (arm.Id == corner.ArmB.Id) ? pin : null,
        };

    private static (CornerCase P, double PinA, double PinB) FirstNeighbourCase()
    {
        AirportGroundLayout plain = Run(Committed("oak")).Layout;
        foreach (CornerCase p in BuiltCases("oak").Where(c => Turns(c, 30, 150) && (BuiltRowsAt(c.Row.JunctionNodeId).Count >= 2)))
        {
            double? reachA = FarthestPlainCutFt(plain, p, p.ArmA);
            double? reachB = FarthestPlainCutFt(plain, p, p.ArmB);
            if ((reachA is { } a) && (reachB is { } b) && InBounds(p.ArmA, a + 10.0) && InBounds(p.ArmB, b + 10.0))
            {
                return (p, a + 10.0, b + 10.0);
            }
        }

        throw new InvalidOperationException("no built corner with a built neighbour and room past its cuts");
    }

    /// <summary>How far along <paramref name="arm"/> the plain pass put <paramref name="corner"/>'s arc end, feet; null when off it.</summary>
    private static double? PlainCutFt(AirportGroundLayout plain, CornerCase corner, TaxiwayArm arm) =>
        ArcOf(plain, corner.Row).Nodes.Select(n => AlongArmFt(corner, arm, n.Position)).FirstOrDefault(d => d is not null);

    /// <summary>The farthest any arc end of the plain pass at the corner's junction lies along <paramref name="arm"/>, feet.</summary>
    private static double? FarthestPlainCutFt(AirportGroundLayout plain, CornerCase corner, TaxiwayArm arm)
    {
        List<double> along = PlainCutsAlongFt(plain, corner, arm);
        return (along.Count > 0) ? along.Max() : null;
    }

    /// <summary>How far along <paramref name="arm"/> each arc end of the plain pass at the corner's junction lies, feet.</summary>
    private static List<double> PlainCutsAlongFt(AirportGroundLayout plain, CornerCase corner, TaxiwayArm arm)
    {
        string prefix = $"corner@J{corner.Row.JunctionNodeId}/";
        return
        [
            .. plain
                .Arcs.Where(a => a.Origin?.StartsWith(prefix, StringComparison.Ordinal) == true)
                .SelectMany(a => a.Nodes)
                .Select(n => AlongArmFt(corner, arm, n.Position))
                .OfType<double>(),
        ];
    }

    /// <summary>The distance of <paramref name="position"/> along <paramref name="arm"/>'s walk from the apex, or null when off it.</summary>
    private static double? AlongArmFt(CornerCase corner, TaxiwayArm arm, LatLon position)
    {
        double before = 0.0;
        GroundNode from = corner.Apex;
        foreach (TaxiwayWalk.WalkStep step in arm.Walk.Steps)
        {
            double offFt =
                FeetBetween(from.Position, position)
                + FeetBetween(position, step.FarNode.Position)
                - FeetBetween(from.Position, step.FarNode.Position);
            if ((offFt < 0.01) && (FeetBetween(from.Position, position) > 0.5))
            {
                return before + FeetBetween(from.Position, position);
            }

            before = step.CumulativeDistFt;
            from = step.FarNode;
        }

        return null;
    }

    private static bool InBounds(TaxiwayArm arm, double pinFt) =>
        (pinFt >= CornerPlanner.MinPinnedTangentFt) && (pinFt <= CornerPlanner.PinFarLimitFt(arm));

    /// <summary>How far along <paramref name="arm"/> its walk first reaches a node another taxiway joins, feet.</summary>
    private static double FirstCrossingFt(TaxiwayArm arm) => arm.Walk.Steps.First(s => s.HasOtherTaxiways).CumulativeDistFt;

    private static FilletOverrideReport AssertRefused(CornerCase corner, Func<CornerCase, CornerFilletOverride> entry, FilletSkipReason reason)
    {
        (AirportGroundLayout layout, FilletReport report) = Run(corner.Pre, entry(corner));

        FilletOverrideReport row = Assert.Single(report.Overrides);
        Assert.Equal((FilletOverrideEffect.Rejected, reason), (row.Effect!.Value, row.RejectedReason!.Value));
        Assert.Equal(PlainSerialized(corner.Pre), GroundLayoutSerializer.Serialize(layout));
        return row;
    }

    private static void AssertAllRefused(FilletReport report, FilletSkipReason reason) =>
        Assert.All(report.Overrides, o => Assert.Equal((FilletOverrideEffect.Rejected, reason), (o.Effect!.Value, o.RejectedReason)));

    /// <summary>The first row at the junction with the taxiway pair, in either outcome, whose case <paramref name="pick"/> accepts.</summary>
    private static CornerCase Named(string shortId, int junctionNodeId, string taxiwayA, string taxiwayB, Func<CornerCase, bool> pick)
    {
        AirportGroundLayout pre = Committed(shortId);
        return Standard(shortId)
            .Corners.Where(c => (c.JunctionNodeId == junctionNodeId) && (c.TaxiwayA == taxiwayA) && (c.TaxiwayB == taxiwayB))
            .Select(c => CaseOf(pre, c))
            .First(pick);
    }

    /// <summary>OAK J300 B5/RAMP, a right angle whose B5 arm crosses another taxiway 115 ft out (node 301).</summary>
    private static CornerCase OakJ300B5Ramp()
    {
        CornerCase corner = Named("oak", 300, "B5", "RAMP", c => Turns(c, 85, 95));
        Assert.True(FirstCrossingFt(corner.ArmA) < corner.ArmA.LengthFt - 10.0, $"B5 crossing {FirstCrossingFt(corner.ArmA)} ft");
        return corner;
    }

    /// <summary>OAK J136 U/T, a right angle with straight first edges (110 ft on U, 171 ft on T).</summary>
    private static CornerCase OakJ136UT() => Named("oak", 136, "U", "T", c => Turns(c, 85, 95));

    /// <summary>
    /// SFO J34 B4/B5 and its B4 arm, which ends at junction J121; a built corner of J121 on its B4 arm back to J34; and the
    /// straight between the junctions, feet.
    /// </summary>
    private static (CornerCase P, TaxiwayArm ArmP, CornerCase Q, TaxiwayArm ArmQ, double StraightFt) SfoJ34B4ToJ121()
    {
        CornerCase p = Named("sfo", 34, "B4", "B5", _ => true);
        TaxiwayArm armP = (p.ArmA.TaxiwayName == "B4") ? p.ArmA : p.ArmB;
        AirportGroundLayout pre = Committed("sfo");
        CornerCase q = Standard("sfo")
            .Corners.Where(c => (c.JunctionNodeId == armP.TerminalNode.Id) && (c.TaxiwayA is not null) && (c.Outcome == FilletCornerOutcome.Built))
            .Select(c => CaseOf(pre, c))
            .First(c => new[] { c.ArmA, c.ArmB }.Any(a => (a.TerminalNode.Id == p.Apex.Id) && (a.TaxiwayName == "B4")));
        TaxiwayArm armQ = new[] { q.ArmA, q.ArmB }.First(a => (a.TerminalNode.Id == p.Apex.Id) && (a.TaxiwayName == "B4"));
        return (p, armP, q, armQ, FeetBetween(p.Apex.Position, q.Apex.Position));
    }

    private static bool Turns(CornerCase corner, double minDeg, double maxDeg) =>
        FilletGeometry.ComputeTurnAngle(corner.ArmA.BearingFromJunctionDeg, corner.ArmB.BearingFromJunctionDeg) is var turn
        && (turn >= minDeg)
        && (turn <= maxDeg);

    private static CornerCase FirstCase(string[] shortIds, Func<CornerCase, bool> predicate) =>
        shortIds.SelectMany(BuiltCases).FirstOrDefault(predicate) ?? throw new InvalidOperationException("no built corner matches");

    /// <summary>The built corners of the plain pass whose taxiway pair no other arm pair of the junction carries.</summary>
    private static List<CornerCase> BuiltCases(string shortId)
    {
        AirportGroundLayout pre = Committed(shortId);
        FilletReport standard = Standard(shortId);
        return
        [
            .. standard
                .Corners.Where(c =>
                    (c.TaxiwayA is not null) && (c.Outcome == FilletCornerOutcome.Built) && (standard.Corners.Count(o => SamePair(o, c)) == 1)
                )
                .Select(c => CaseOf(pre, c)),
        ];
    }

    private static List<FilletCornerReport> BuiltRowsAt(int junctionNodeId) =>
        [
            .. Standard("oak")
                .Corners.Where(c => (c.JunctionNodeId == junctionNodeId) && (c.TaxiwayA is not null) && (c.Outcome == FilletCornerOutcome.Built)),
        ];

    private static CornerCase CaseOf(AirportGroundLayout pre, FilletCornerReport row)
    {
        AirportGroundLayout copy = LayoutCloner.DeepClone(pre);
        copy.RebuildAdjacencyLists();
        GroundNode apex = copy.Nodes[row.JunctionNodeId];
        IReadOnlyList<TaxiwayArm> arms = TaxiwayArmBuilder.BuildArms(apex, ManualArcDetector.Detect(copy));
        TaxiwayArm armA = arms.First(a => (a.TaxiwayName == row.TaxiwayA) && (a.BearingFromJunctionDeg == row.BearingA));
        TaxiwayArm armB = arms.First(a => (a.TaxiwayName == row.TaxiwayB) && (a.BearingFromJunctionDeg == row.BearingB) && (a.Id != armA.Id));
        return new CornerCase(pre, row, apex, armA, armB);
    }

    /// <summary>The corner's override pinning <paramref name="a"/> on its first arm and <paramref name="b"/> on its second.</summary>
    private static CornerFilletOverride Pinned(CornerCase corner, double a, double b) =>
        FilletReportLayouts.OverrideFor(corner.Row) with
        {
            TangentAFt = a,
            TangentBFt = b,
        };

    private static CornerFilletOverride PinnedOn(CornerCase corner, TaxiwayArm arm, double onArm, double onOther) =>
        (arm.Id == corner.ArmA.Id) ? Pinned(corner, onArm, onOther) : Pinned(corner, onOther, onArm);

    private static CornerFilletOverride PinnedOnShared(SharedArmCase shared, double pin) =>
        PinnedOn(shared.P, shared.Shared, pin, shared.POtherCutFt);

    private static LatLon At(CornerCase corner, TaxiwayArm arm, double ft) => TaxiwayWalk.InterpolateAtDistanceFt(arm.Walk, corner.Apex, ft).Position;

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static void AssertArcEndsAt(GroundArc arc, LatLon a, LatLon b)
    {
        string ends = string.Join(", ", arc.Nodes.Select(n => $"{FeetBetween(n.Position, a):R}/{FeetBetween(n.Position, b):R}"));
        Assert.True(arc.Nodes.Any(n => FeetBetween(n.Position, a) <= ToleranceFt), $"no arc end at the first pin: {ends}");
        Assert.True(arc.Nodes.Any(n => FeetBetween(n.Position, b) <= ToleranceFt), $"no arc end at the second pin: {ends}");
    }

    private static GroundArc ArcOf(AirportGroundLayout layout, FilletCornerReport row)
    {
        int id = row.JunctionNodeId;
        string a = row.TaxiwayA!;
        string b = row.TaxiwayB!;
        return Assert.Single(layout.Arcs, arc => (arc.Origin == $"corner@J{id}/{a}/{b}") || (arc.Origin == $"corner@J{id}/{b}/{a}"));
    }

    private static AirportGroundLayout Committed(string shortId)
    {
        AirportGroundLayout? pre = FilletReportLayouts.LoadPreFillet(shortId);
        Assert.NotNull(pre);
        return pre;
    }

    private static FilletReport Standard(string shortId)
    {
        FilletReport? report = FilletReportLayouts.StandardReport(shortId);
        Assert.NotNull(report);
        return report;
    }

    private static (AirportGroundLayout Layout, FilletReport Report) Run(AirportGroundLayout pre, params CornerFilletOverride[] entries)
    {
        AirportGroundLayout layout = LayoutCloner.DeepClone(pre);
        FilletStatistics stats = new FilletArcGenerator().Apply(layout, new CornerFilletOverrides(entries));
        return (layout, stats.Report);
    }

    private static byte[] PlainSerialized(AirportGroundLayout pre) => GroundLayoutSerializer.Serialize(Run(pre).Layout);

    private static bool SamePair(FilletCornerReport first, FilletCornerReport second) =>
        (first.JunctionNodeId == second.JunctionNodeId)
        && (
            ((first.TaxiwayA == second.TaxiwayA) && (first.TaxiwayB == second.TaxiwayB))
            || ((first.TaxiwayA == second.TaxiwayB) && (first.TaxiwayB == second.TaxiwayA))
        );

    private static FilletCornerReport RowLike(FilletReport report, FilletCornerReport like)
    {
        FilletCornerReport? row = report.Corners.FirstOrDefault(c =>
            (c.JunctionNodeId == like.JunctionNodeId)
            && (c.TaxiwayA == like.TaxiwayA)
            && (c.TaxiwayB == like.TaxiwayB)
            && (c.BearingA == like.BearingA)
            && (c.BearingB == like.BearingB)
        );
        Assert.True(row is not null, $"J{like.JunctionNodeId} {like.TaxiwayA}/{like.TaxiwayB}: no row");
        return row;
    }

    /// <summary>
    /// The first 48 hex digits of a SHA-256 of the filleted layout's nodes, edges and arcs, positions rounded to 1e-7 degrees
    /// and radii to 1e-3 ft, so the last bits of trigonometry that differ between the x64 and arm64 builders do not change it.
    /// </summary>
    private static string Fingerprint(AirportGroundLayout layout)
    {
        var text = new StringBuilder();
        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            text.Append(CultureInfo.InvariantCulture, $"N{node.Id} {node.Type} {Deg(node.Position.Lat)} {Deg(node.Position.Lon)}\n");
        }

        foreach (string edge in layout.Edges.Select(e => $"E{e.Nodes[0].Id}-{e.Nodes[1].Id} {e.TaxiwayName}").Order(StringComparer.Ordinal))
        {
            text.Append(edge).Append('\n');
        }

        foreach (string arc in layout.Arcs.Select(ArcText).Order(StringComparer.Ordinal))
        {
            text.Append(arc).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..48];
    }

    private static string ArcText(GroundArc a) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"A{a.Nodes[0].Id}-{a.Nodes[1].Id} {a.Origin} {Deg(a.P1Lat)} {Deg(a.P1Lon)} {Deg(a.P2Lat)} {Deg(a.P2Lon)} {a.MinRadiusOfCurvatureFt:F3}"
        );

    private static string Deg(double value) => value.ToString("F7", CultureInfo.InvariantCulture);
}
