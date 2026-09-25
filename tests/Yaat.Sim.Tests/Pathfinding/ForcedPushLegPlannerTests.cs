using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// A forced leg kind (<c>/PUSH</c>, <c>/PULL</c>) and a marked point (<c>~lat/lon[/facing]</c>) through the tug move
/// planner, on the real SFO layout (issue #462). A forced kind is a hard constraint: after the stand push-off every
/// move of the leg is of that kind, a <c>/PUSH</c> onto a spot ends on the stop itself, and a leg no such plan can fly is
/// refused. A marked point is planned like a node goal with no exempt pavement, and one placed on protected pavement
/// is refused outright. On a pull the tug's lead ahead of the nose counts for the movement-area check.
/// </summary>
public class ForcedPushLegPlannerTests(ITestOutputHelper output)
{
    private const string Narrowbody = "B738";

    /// <summary>How far past the tug lead's reach a marked point is set in the lead's control case, feet.</summary>
    private const double LeadClearMarginFt = 25.0;

    /// <summary>How far short of the tug lead's reach the refused marked point is set, feet.</summary>
    private const double LeadInsideMarginFt = 15.0;

    /// <summary>How far behind the marked point the straight pull onto it starts, feet.</summary>
    private const double PullRunUpFt = 150.0;

    /// <summary>How far ahead of a spot's stop, along its lane, the lane push starts, feet.</summary>
    private const double LaneRunUpFt = 100.0;

    /// <summary>
    /// How far off a taxiway centreline the wing-across marked point is set, feet: past the 25 ft centreline corridor a
    /// marked point is refused within, inside a B738's half span (59 ft).
    /// </summary>
    private const double WingsAcrossOffsetFt = 35.0;

    /// <summary>How far ahead of a stand's nose the unfaced marked point is set, feet.</summary>
    private const double AheadOfStandFt = 100.0;

    private const double EndToleranceFt = 3.0;

    private const double EndFacingToleranceDeg = 2.0;

    /// <summary>
    /// (a) D15 → 6A plans a pull-side plan today (the pull-side T0, <c>docs/ground/pushback.md</c>). Forced
    /// <c>/PUSH</c>, no plan of pushes alone lines up on 6A, and the leg is refused rather than finished with a pull.
    /// </summary>
    [Fact]
    public void SpotForcedPush_OffD15_WhereOnlyAPullLinesUp_Refused()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "6A");
        TugPlan unforced = PlanOrFail(layout, StandStart(Parking(layout, "D15"), TugGoal.Spot(spot)));
        Assert.Contains(unforced.Moves, m => m.Move.Kind == PushbackLegKind.Pull);

        string refusal = Refusal(layout, StandStart(Parking(layout, "D15"), TugGoal.Spot(spot) with { ForcedKind = PushbackLegKind.Push }));

        Assert.Equal("Unable, spot 6A cannot be reached by a push", refusal);
    }

    /// <summary>
    /// (c) An aircraft on 7A's lane, <see cref="LaneRunUpFt"/> ahead of the stop and nose out: today it pushes past the
    /// stop to the staging point and creeps forward onto it. Forced <c>/PUSH</c>, the push ends on the stop itself.
    /// </summary>
    [Fact]
    public void SpotForcedPush_FromAheadOnTheLane_EndsOnTheStopWithNoCreepPull()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "7A");
        TugPose stop = SpotStopPose(layout, spot);
        var start = new TugPose(Project(stop.Position, stop.NoseTrueDeg, LaneRunUpFt), stop.NoseTrueDeg);
        TugPlan unforced = PlanOrFail(layout, OffStand(start, TugGoal.Spot(spot)));
        Assert.True(unforced.Moves[^1].Move is { Kind: PushbackLegKind.Pull, Creep: true }, "today the push stages and creeps forward");

        TugPlan plan = PlanOrFail(layout, OffStand(start, TugGoal.Spot(spot) with { ForcedKind = PushbackLegKind.Push }));

        Assert.All(plan.Moves, m => Assert.Equal(PushbackLegKind.Push, m.Move.Kind));
        Assert.Equal(TugMoveShape.ViaLine, plan.Moves[^1].Move.Shape);
        AssertEndsOnSpotStop(layout, spot, plan);
    }

    /// <summary>
    /// (b) The mirror: F8 → 7A, forced <c>/PULL</c>. After the push-off run every move is a pull, and the plan ends
    /// creeping onto 7A's stop.
    /// </summary>
    [Fact]
    public void SpotForcedPull_OffF8_EveryMoveAfterThePushOffRunIsAPull()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "7A");
        TugPlan unforced = PlanOrFail(layout, StandStart(Parking(layout, "F8"), TugGoal.Spot(spot)));
        output.WriteLine($"unforced: {Describe(unforced)}");

        TugPlan plan = PlanOrFail(layout, StandStart(Parking(layout, "F8"), TugGoal.Spot(spot) with { ForcedKind = PushbackLegKind.Pull }));

        AssertPushOffRunThen(plan, PushbackLegKind.Pull);
        Assert.True(plan.Moves[^1].Move.Creep, "the final pull onto a spot is a creep");
        AssertEndsOnSpotStop(layout, spot, plan);
    }

    /// <summary>
    /// (b) Forced <c>/PULL</c> from spot 6A onto 6B, which sits abeam it: a pull alone cannot capture 6B's line before
    /// its stop, and no push may precede it off a spot, so the leg is refused rather than flown with a push.
    /// </summary>
    [Fact]
    public void SpotForcedPull_FromAbeamSpot_RefusedNamingTheKind()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugPose start = SpotStopPose(layout, Spot(layout, "6A"));
        TugPlan unforced = PlanOrFail(layout, OffStand(start, TugGoal.Spot(Spot(layout, "6B"))));
        output.WriteLine($"unforced: {Describe(unforced)}");
        Assert.Contains(unforced.Moves, m => m.Move.Kind == PushbackLegKind.Push);

        string refusal = Refusal(layout, OffStand(start, TugGoal.Spot(Spot(layout, "6B")) with { ForcedKind = PushbackLegKind.Pull }));

        Assert.Equal("Unable, spot 6B cannot be reached by a pull", refusal);
    }

    /// <summary>(d) A <c>/PULL</c> onto a stand is a nose-in pull: B12 → B13 pushes off, then pulls onto B13's heading.</summary>
    [Fact]
    public void StandForcedPull_B12ToB13_PullsNoseInOntoTheStand()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode stand = Parking(layout, "B13");
        TugPlan plan = PlanOrFail(layout, StandStart(Parking(layout, "B12"), TugGoal.Stand(stand) with { ForcedKind = PushbackLegKind.Pull }));

        AssertPushOffRunThen(plan, PushbackLegKind.Pull);
        Assert.Equal(PushbackLegKind.Pull, plan.Moves[^1].Move.Kind);
        Assert.True(
            FeetBetween(plan.End.Position, stand.Position) <= EndToleranceFt,
            $"ended {FeetBetween(plan.End.Position, stand.Position):F1} ft off B13"
        );
        Assert.True(AbsDiffDeg(plan.End.NoseTrueDeg, stand.TrueHeading!.Value.Degrees) <= EndFacingToleranceDeg);
    }

    /// <summary>
    /// (e) An unfaced node behind the aircraft is reached by a push; forced <c>/PULL</c> the planner does not substitute
    /// the push it would have chosen, it refuses. Forced <c>/PUSH</c> it is the push it would have chosen. The aircraft
    /// stands on 7A's stop nose-in, so gate F8, ahead of a nose-out aircraft there, is behind it.
    /// </summary>
    [Fact]
    public void UnfacedNodeForcedAgainstTheDerivedKind_Refused_ForcedWithItPlans()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugPose noseOut = SpotStopPose(layout, Spot(layout, "7A"));
        var start = new TugPose(noseOut.Position, new TrueHeading(noseOut.NoseTrueDeg).ToReciprocal().Degrees);
        GroundNode behind = Parking(layout, "F8");

        string refusal = Refusal(layout, OffStand(start, TugGoal.AtNode(behind, facingTrueDeg: null) with { ForcedKind = PushbackLegKind.Pull }));
        TugPlan pushed = PlanOrFail(layout, OffStand(start, TugGoal.AtNode(behind, facingTrueDeg: null) with { ForcedKind = PushbackLegKind.Push }));

        Assert.Equal("Unable, F8 cannot be reached by a pull", refusal);
        Assert.All(pushed.Moves, m => Assert.Equal(PushbackLegKind.Push, m.Move.Kind));
    }

    /// <summary>
    /// (f) A marked point with a facing, on the ramp where spot 6B's stop is, reached from spot 6A: the plan ends on the
    /// point, on the facing, within the faced-goal end tolerance.
    /// </summary>
    [Fact]
    public void MarkedPointWithFacing_OnTheRamp_PlansOntoThePose()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugPose target = SpotStopPose(layout, Spot(layout, "6B"));
        TugGoal goal = MarkedPoint(target.Position, target.NoseTrueDeg);

        TugPlan plan = PlanOrFail(layout, OffStand(SpotStopPose(layout, Spot(layout, "6A")), goal));

        Assert.True(
            FeetBetween(plan.End.Position, target.Position) <= EndToleranceFt,
            $"ended {FeetBetween(plan.End.Position, target.Position):F1} ft off"
        );
        Assert.True(AbsDiffDeg(plan.End.NoseTrueDeg, target.NoseTrueDeg) <= EndFacingToleranceDeg);
    }

    /// <summary>
    /// (g) A marked point on protected pavement is refused outright, naming the pavement: a taxiway centreline, a
    /// runway, a runway holding position.
    /// </summary>
    [Fact]
    public void MarkedPointOnProtectedPavement_RefusedNamingThePavement()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugRequest FromD15(LatLon point) => StandStart(Parking(layout, "D15"), MarkedPoint(point, null));
        GroundNode d15 = Parking(layout, "D15");
        GroundEdge alpha = NearestMovementAreaEdge(layout, "A", d15.Position);
        LatLon onAlpha = Midpoint(alpha.Nodes[0].Position, alpha.Nodes[1].Position);
        GroundNode hold = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).MinBy(n => FeetBetween(n.Position, d15.Position))!;
        (GroundRunway runway, LatLon onRunway) = layout
            .Runways.SelectMany(r =>
                r.Coordinates.Zip(r.Coordinates.Skip(1))
                    .Select(pair =>
                        (r, NearestPointOn(new LatLon(pair.First.Lat, pair.First.Lon), new LatLon(pair.Second.Lat, pair.Second.Lon), hold.Position))
                    )
            )
            .MinBy(p => FeetBetween(p.Item2, hold.Position));
        TugRequest fromHold = OffStand(new TugPose(hold.Position, 0.0), MarkedPoint(onRunway, null));

        Assert.Equal("Unable, the marked point is on taxiway A", Refusal(layout, FromD15(onAlpha)));
        Assert.Equal("Unable, the marked point is a runway holding position", Refusal(layout, FromD15(hold.Position)));
        Assert.Equal($"Unable, the marked point is on runway {runway.Name}", Refusal(layout, fromHold));
        output.WriteLine($"runway named as '{runway.Name}'");
    }

    /// <summary>
    /// (h) A straight pull onto a marked point short of taxiway A, square to it. With the fuselage alone the aircraft
    /// stops clear of A either way; the tug 30 ft ahead of the nose reaches A's centreline when the point is closer than
    /// half a fuselage plus the lead, and the pull is then refused. Set further back, the same pull is accepted.
    /// </summary>
    [Fact]
    public void PullEndingWithTheTugLeadOnATaxiway_Refused_AndAcceptedWithTheLeadClear()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "7A");
        GroundEdge alpha = NearestMovementAreaEdge(layout, "A", spot.Position);
        LatLon onAlpha = NearestPointOn(alpha, spot.Position);
        double awayFromAlphaDeg = GeoMath.BearingTo(onAlpha, spot.Position);
        double towardAlphaDeg = new TrueHeading(awayFromAlphaDeg).ToReciprocal().Degrees;
        double leadReachFt = (TugMovePlanner.FuselageLengthFt(Narrowbody) / 2.0) + GroundOutline.TugLeadFt;

        TugRequest PullTo(double fromAlphaFt)
        {
            LatLon point = Project(onAlpha, awayFromAlphaDeg, fromAlphaFt);
            var start = new TugPose(Project(onAlpha, awayFromAlphaDeg, fromAlphaFt + PullRunUpFt), towardAlphaDeg);
            output.WriteLine($"pull onto a point {fromAlphaFt:F0} ft from A's centreline (lead reaches {leadReachFt:F0} ft ahead of the centre)");
            return OffStand(start, MarkedPoint(point, towardAlphaDeg) with { ForcedKind = PushbackLegKind.Pull });
        }

        string refusal = Refusal(layout, PullTo(leadReachFt - LeadInsideMarginFt));
        TugPlan clear = PlanOrFail(layout, PullTo(leadReachFt + LeadClearMarginFt));

        Assert.Equal("Unable, the move to the marked point would put the aircraft on taxiway A", refusal);
        Assert.All(clear.Moves, m => Assert.Equal(PushbackLegKind.Pull, m.Move.Kind));
    }

    /// <summary>
    /// A marked point <see cref="WingsAcrossOffsetFt"/> off taxiway A's centreline, beside spot 7A, facing along A: the
    /// point itself is clear of A's centreline corridor, but a B738 resting on it has a wing across A. Its end footprint
    /// is on the taxiway, so it is refused naming A — a marked point is no stand or spot whose pavement the move may
    /// arrive across.
    /// </summary>
    [Fact]
    public void MarkedPointWithAWingAcrossATaxiway_RefusedNamingTheTaxiway()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode spot = Spot(layout, "7A");
        GroundEdge alpha = NearestMovementAreaEdge(layout, "A", spot.Position);
        LatLon onAlpha = NearestPointOn(alpha, spot.Position);
        double alongAlphaDeg = GeoMath.BearingTo(alpha.Nodes[0].Position, alpha.Nodes[1].Position);
        double towardSpotDeg = GeoMath.BearingTo(onAlpha, spot.Position);
        double offAlphaDeg = new[] { alongAlphaDeg + 90.0, alongAlphaDeg - 90.0 }.MinBy(d => AbsDiffDeg(d, towardSpotDeg));
        LatLon point = Project(onAlpha, offAlphaDeg, WingsAcrossOffsetFt);
        var start = new TugPose(Project(point, alongAlphaDeg + 180.0, PullRunUpFt), new TrueHeading(alongAlphaDeg).Degrees);
        output.WriteLine($"point {WingsAcrossOffsetFt:F0} ft off A's centreline, facing {alongAlphaDeg:F0}° along it");

        string refusal = Refusal(layout, OffStand(start, MarkedPoint(point, alongAlphaDeg)));

        Assert.Equal("Unable, the marked point is on taxiway A", refusal);
    }

    /// <summary>
    /// A <c>/PULL</c> to an unfaced marked point <see cref="AheadOfStandFt"/> ahead of D15's nose: the stand needs its
    /// push-off first and no pull may follow it to an unfaced point, so the leg is refused naming the pull it was forced
    /// to — not the push the planner would otherwise have asked for.
    /// </summary>
    [Fact]
    public void UnfacedMarkedPointAheadOfAStand_ForcedPull_RefusedNamingThePull()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode d15 = Parking(layout, "D15");
        LatLon ahead = Project(d15.Position, d15.TrueHeading!.Value.Degrees, AheadOfStandFt);

        string refusal = Refusal(layout, StandStart(d15, MarkedPoint(ahead, null) with { ForcedKind = PushbackLegKind.Pull }));

        Assert.Equal("Unable, the marked point cannot be reached by a pull", refusal);
    }

    private static TugGoal MarkedPoint(LatLon point, double? facingTrueDeg) =>
        TugGoal.FreePose(VirtualNode.Create(point.Lat, point.Lon), null, facingTrueDeg, "the marked point");

    /// <summary>Asserts the plan opens with pushes only (the stand push-off run) and every move after it is <paramref name="kind"/>.</summary>
    private void AssertPushOffRunThen(TugPlan plan, PushbackLegKind kind)
    {
        output.WriteLine($"forced {kind}: {Describe(plan)}");
        int firstOther = 0;
        while ((firstOther < plan.Moves.Count) && (plan.Moves[firstOther].Move.Kind == PushbackLegKind.Push))
        {
            firstOther++;
        }

        Assert.True(firstOther > 0, "a plan off a stand opens with the push-off");
        Assert.All(plan.Moves.Skip(firstOther), m => Assert.Equal(kind, m.Move.Kind));
    }

    private void AssertEndsOnSpotStop(AirportGroundLayout layout, GroundNode spot, TugPlan plan)
    {
        TugPose stop = SpotStopPose(layout, spot);
        double offFt = FeetBetween(plan.End.Position, stop.Position);
        output.WriteLine($"{Describe(plan)}; ends {offFt:F1} ft off the stop, nose {AbsDiffDeg(plan.End.NoseTrueDeg, stop.NoseTrueDeg):F1}° off");
        Assert.True(offFt <= EndToleranceFt, $"ended {offFt:F1} ft off the stop");
        Assert.True(AbsDiffDeg(plan.End.NoseTrueDeg, stop.NoseTrueDeg) <= EndFacingToleranceDeg);
    }

    /// <summary>Where a narrowbody rests on a spot: its stop point, nose on the spot's outbound heading.</summary>
    private static TugPose SpotStopPose(AirportGroundLayout layout, GroundNode spot)
    {
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outbound), $"spot {spot.Name} has no outbound heading");
        return new TugPose(TugMovePlanner.SpotStopGeometry(spot, outbound, Narrowbody).Stop, outbound);
    }

    private static GroundEdge NearestMovementAreaEdge(AirportGroundLayout layout, string taxiway, LatLon near)
    {
        var pavement = new TugPavementClassifier(layout);
        return layout
            .AllEdges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway(taxiway) && (pavement.MovementAreaName(e) is not null))
            .MinBy(e => FeetBetween(NearestPointOn(e, near), near))!;
    }

    private static LatLon NearestPointOn(GroundEdge edge, LatLon point) => NearestPointOn(edge.Nodes[0].Position, edge.Nodes[1].Position, point);

    private static LatLon NearestPointOn(LatLon a, LatLon b, LatLon point)
    {
        var course = new TrueHeading(GeoMath.BearingTo(a, b));
        double alongNm = Math.Clamp(GeoMath.AlongTrackDistanceNm(point, a, course), 0.0, GeoMath.DistanceNm(a, b));
        return GeoMath.ProjectPoint(a, course, alongNm);
    }

    private static LatLon Project(LatLon from, double bearingDeg, double feet) =>
        GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), feet / GeoMath.FeetPerNm);

    private static LatLon Midpoint(LatLon a, LatLon b) => new((a.Lat + b.Lat) / 2.0, (a.Lon + b.Lon) / 2.0);

    private TugPlan PlanOrFail(AirportGroundLayout layout, TugRequest request)
    {
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.True(plan is not null, $"refused: {refusal}");
        output.WriteLine(Describe(plan));
        return plan;
    }

    private string Refusal(AirportGroundLayout layout, TugRequest request)
    {
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.True(plan is null, $"planned: {(plan is null ? "" : Describe(plan))}");
        output.WriteLine($"refused: {refusal}");
        return refusal;
    }

    private static string Describe(TugPlan plan) =>
        string.Join(", ", plan.Moves.Select(m => $"{m.Move.Kind} {m.Move.Shape}{(m.Move.Creep ? " creep" : "")} {m.PathLengthFt:F0} ft"));

    private static TugRequest StandStart(GroundNode stand, TugGoal goal) =>
        new()
        {
            Start = new TugPose(stand.Position, stand.TrueHeading!.Value.Degrees),
            StartsAtStand = true,
            AircraftType = Narrowbody,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    private static TugRequest OffStand(TugPose start, TugGoal goal) =>
        new()
        {
            Start = start,
            StartsAtStand = false,
            AircraftType = Narrowbody,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    private static AirportGroundLayout? LoadSfo() => new TestAirportGroundData().GetLayout("SFO");

    private static GroundNode Spot(AirportGroundLayout layout, string name) =>
        layout.FindSpotNodeByName(name) ?? throw new InvalidOperationException($"SFO spot {name} missing");

    private static GroundNode Parking(AirportGroundLayout layout, string name) =>
        layout.FindParkingByName(name) ?? throw new InvalidOperationException($"SFO parking {name} missing");

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static double AbsDiffDeg(double a, double b) => new TrueHeading(a).AbsAngleTo(new TrueHeading(b));
}
