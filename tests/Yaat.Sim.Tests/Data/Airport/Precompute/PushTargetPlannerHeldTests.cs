using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerFixtures;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerTests;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// <see cref="PushTargetPlanner.ComputeHeld"/>: the targets of an aircraft held where a push left it, planned from
/// that pose.
/// </summary>
public class PushTargetPlannerHeldTests
{
    public PushTargetPlannerHeldTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// A group-III aircraft pushed from gate 12 to spot C and held there is not offered spot C again: a push to the spot it
    /// sits on would leave it where it is. It is offered other targets, planned from where it stands.
    /// </summary>
    [Fact]
    public void ComputeHeld_AfterPushToSpot_NeverOffersTheSpotItIsOn()
    {
        AirportGroundLayout layout = Oak();
        var spotC = TugGoal.Spot(SpotNode(layout, "C"));
        PushbackPose held = HeldAfter(layout, "12", spotC, AirplaneDesignGroup.III);

        IReadOnlyList<PrecomputedPushTarget> targets = HeldTargets(layout, held, AirplaneDesignGroup.III);

        Assert.NotEmpty(targets);
        Assert.DoesNotContain(targets, t => t.Command == "PUSH $C");

        // The planner itself accepts the push to C, a foot's creep: only the stay-put rule keeps it off the list.
        AircraftFootprint footprint = DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.III);
        TugRequest again = PushTargetPlanner.RequestFor(held, footprint, Classification(layout), spotC, startsAtStand: false);
        TugPlan? stayPut = TugMovePlanner.Plan(layout, again, out string refusal);
        Assert.True(stayPut is not null, refusal);
        // Accepted and ending inside the stay-put radius: so the stay-put rule, not a planner refusal, is what removes C.
        Assert.True((GeoMath.DistanceNm(held.Position, stayPut.End.Position) * GeoMath.FeetPerNm) <= PushTargetPlanner.StayPutDistanceFt);
    }

    /// <summary>
    /// After gate 26's group-III <c>PUSH TE</c> the aircraft is held on TE, its stand still named 26 on the server (taxiway B
    /// is 2,713 ft off, past the tug's sanity guard): its targets are planned from where it is held, so they differ from
    /// gate 26's, and no target repeats a stand push's moves.
    /// </summary>
    [Fact]
    public void ComputeHeld_AfterPushOntoTaxiway_PlansFromTheHeldPoseNotTheStand()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        PushbackPose held = HeldAfter(layout, "26", StraightBack(layout, gate, "TE"), AirplaneDesignGroup.III);

        IReadOnlyList<PrecomputedPushTarget> heldTargets = HeldTargets(layout, held, AirplaneDesignGroup.III);
        PushTargetEntry standEntry = Assert.IsType<PushTargetEntry>(
            PushTargetPlanner.ComputeStand(layout, DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, "26", AirplaneDesignGroup.III)
        );

        Assert.NotEmpty(heldTargets);
        Assert.NotEqual(Json(standEntry.Targets), Json(heldTargets));
        Assert.All(heldTargets, t => Assert.DoesNotContain(standEntry.Targets, s => (s.Command == t.Command) && s.Moves.SequenceEqual(t.Moves)));
    }

    /// <summary>
    /// A held push is planned as one that does not start at a stand: planned from gate 26's own pose, the held plan differs
    /// from the stand plan, though the pose and the candidates are the same.
    /// </summary>
    [Fact]
    public void ComputeHeld_StartsAtStandFalse()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");

        IReadOnlyList<PrecomputedPushTarget> asHeld = HeldTargets(layout, StartOf(gate), AirplaneDesignGroup.III);
        PushTargetEntry asStand = Assert.IsType<PushTargetEntry>(
            PushTargetPlanner.ComputeStand(layout, DesignGroupEnvelopes.LoadShipped(), Sidecars.Value, "26", AirplaneDesignGroup.III)
        );

        Assert.NotEmpty(asHeld);
        Assert.NotEqual(Json(asStand.Targets), Json(asHeld));
    }

    /// <summary>
    /// After gate 12's group-III <c>PUSH $C</c>, a taxiway's exit is the one nearest where the aircraft is held, not the one
    /// nearest spot C's node: the held target's moves are the plan to the held position's exit.
    /// </summary>
    [Fact]
    public void ComputeHeld_TaxiwayExitFromTheHeldPosition()
    {
        AirportGroundLayout layout = Oak();
        GroundNode spotC = SpotNode(layout, "C");
        PushbackPose held = HeldAfter(layout, "12", TugGoal.Spot(spotC), AirplaneDesignGroup.III);
        IReadOnlyList<PrecomputedPushTarget> targets = HeldTargets(layout, held, AirplaneDesignGroup.III);

        GroundNode fromHeld = layout.FindExitByTaxiway(held.Position, "TE") ?? throw new InvalidOperationException("No exit onto TE from spot C");
        Assert.NotEqual(layout.FindExitByTaxiway(spotC.Position, "TE")?.Id, fromHeld.Id);
        AircraftFootprint footprint = DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.III);
        TugRequest request = PushTargetPlanner.RequestFor(
            held,
            footprint,
            Classification(layout),
            TugGoal.StraightBackTo(fromHeld, "TE"),
            startsAtStand: false
        );
        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);
        Assert.True(plan is not null, refusal);

        PrecomputedPushTarget te = Assert.Single(targets, t => t.Name == "TE");
        Assert.Equal(plan.Moves.Select(PushMoveEntry.From), te.Moves);
    }
}
