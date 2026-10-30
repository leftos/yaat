using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetLiveCheckFixtures;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The live check of SFO gate D2's stored targets: the GC 28/01 field case and an aircraft held near a path's end.
/// </summary>
public class PushTargetLiveCheckSfoTests(ITestOutputHelper output)
{
    /// <summary>The targets of SFO gate D2.</summary>
    private static readonly Lazy<IReadOnlyList<PushTargetEntry>> SfoD2Entries = new(() =>
        PushTargetPlanner.ComputeStands(
            PushTargetPlannerTests.Sfo(),
            Envelopes.Value,
            PushTargetPlannerTests.Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "D2" },
            PushTargetPlannerTests.Sequential
        )
    );

    /// <summary>
    /// The SFO GC 28/01 field case: an E75L on D2 with an E75L on D1. Group III stores <c>PUSH $5A</c> from D2: its
    /// envelope plan is 611.0 ft, inside the group's 700.0 ft cap (<see cref="PushTargetPlanner.PathCapFt"/>), as is the
    /// E75L's own live plan with SKW3398 at D1, 591.0 ft. D2's group-III <c>PUSH T5A</c> is offered: the live planner plans
    /// it for the E75L with SKW3398 at D1 as a neighbour, so the E75L's own plan keeps D1's floor, and the stored group-III
    /// moves flown and swept for the E75L keep it too, so both are clear.
    /// </summary>
    [Fact]
    public void Sfo_D2_E75LAtD1_Spot5AOffered()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Sfo();
        PushbackPose start = StartOf(Stand(layout, "D2"));
        NeighbourCandidate subject = Parked("SKW3396", Regional, start, "D2");
        List<NeighbourCandidate> others = [Parked("SKW3398", Regional, StartOf(Stand(layout, "D1")), "D1")];
        IReadOnlyList<TugParkedNeighbour> neighbours = TugParkedNeighbours.Build(subject, others);
        Assert.Single(neighbours);
        string group = GroupOf(Regional);
        AircraftFootprint envelope = EnvelopeFootprint(group);
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);

        Assert.Contains(Target(SfoD2Entries.Value, "D2", group), t => t.Command == "PUSH $5A");
        GroundNode spot5A = Assert.IsType<GroundNode>(layout.FindSpotNodeByName("5A"));
        TugPlan envelopePlan = Assert.IsType<TugPlan>(
            TugMovePlanner.Plan(
                layout,
                PushTargetPlanner.RequestFor(start, envelope, classification, TugGoal.Spot(spot5A), startsAtStand: true),
                out _
            )
        );
        double envelopePathFt = envelopePlan.Moves.Sum(m => m.PathLengthFt);
        output.WriteLine($"PUSH $5A for group {group}: {envelopePathFt:F1} ft, cap {PushTargetPlanner.PathCapFt(envelope):F1} ft");
        Assert.True(envelopePathFt <= PushTargetPlanner.PathCapFt(envelope), $"group {group}'s PUSH $5A is {envelopePathFt:F1} ft, over the cap");

        PrecomputedPushTarget t5A = Target(SfoD2Entries.Value, "D2", group, "T5A");
        var actual = AircraftFootprint.FromType(Regional);
        PushTargetLiveCheckRequest request = Request(subject, actual, others);
        TugRequest live = PushTargetPlanner.RequestFor(start, actual, classification, GoalOf(layout, start, t5A), startsAtStand: true) with
        {
            ParkedNeighbours = neighbours,
        };
        TugPlan livePlan = Assert.IsType<TugPlan>(TugMovePlanner.Plan(layout, live, out _));

        PushTargetLiveVerdict stored = PushTargetLiveCheck.Check(t5A, request);
        PushTargetLiveVerdict livePath = PushTargetLiveCheck.Check(t5A with { Moves = [.. livePlan.Moves.Select(PushMoveEntry.From)] }, request);

        output.WriteLine($"{t5A.Command}: stored plan {stored}; live plan {livePath}");
        Assert.Equal(PushTargetLiveVerdict.Clear, livePath);
        Assert.Equal(PushTargetLiveVerdict.Clear, stored);
    }

    /// <summary>
    /// SFO D2's group-V <c>PUSH $5A</c>, a 554 ft stand-to-spot move: an aircraft held on the taxilane just ahead of the
    /// tug where the move ends, more than <see cref="TugParkedNeighbours.RangeFt"/> from the stand (so the live push
    /// planner's neighbour list leaves it out), is within reach of the path and blocks the target.
    /// </summary>
    [Fact]
    public void HeldAircraftOnTheTaxilaneNearThePathEnd_BlocksTheTarget()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Sfo();
        PushbackPose start = StartOf(Stand(layout, "D2"));
        PrecomputedPushTarget spot5A = Target(SfoD2Entries.Value, "D2", "V", "5A");
        Assert.True(spot5A.PathLengthFt >= 500.0, $"{spot5A.Command} is {spot5A.PathLengthFt} ft");
        AircraftFootprint envelope = EnvelopeFootprint("V");
        TugSimulation flown = Refly(spot5A, start, envelope);
        Assert.True(flown.Completed);
        Assert.Equal(PushbackLegKind.Pull, flown.Moves[^1].Move.Kind);
        double aheadFt = (envelope.LengthFt / 2.0) + GroundOutline.TugLeadFt + HeldGapFt + (AircraftFootprint.FromType(Narrowbody).LengthFt / 2.0);
        var heldPose = new PushbackPose(Offset(flown.End, 0.0, aheadFt), flown.End.NoseTrueDeg);
        NeighbourCandidate subject = Parked(Subject, Narrowbody, start, "D2");
        NeighbourCandidate held = Parked(Neighbour, Narrowbody, heldPose, null) with { IsImmobile = true, PhaseName = null };
        double fromStandFt = GeoMath.DistanceNm(start.Position, heldPose.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"{spot5A.Command}: {spot5A.PathLengthFt} ft; held aircraft {fromStandFt:F1} ft from the stand");
        Assert.True(fromStandFt > TugParkedNeighbours.RangeFt, $"the held aircraft is {fromStandFt:F1} ft from the stand");
        Assert.Empty(TugParkedNeighbours.Build(subject, [held]));

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(spot5A, Request(subject, envelope, [held]));

        Assert.Equal(PushTargetLiveVerdict.Blocked(Neighbour), verdict);
    }
}
