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
/// The live check of OAK gate 26's group-III targets with B738s on gates 25 and 27 gives the live planner's verdict.
/// </summary>
public class PushTargetLiveCheckNeighbourTests(ITestOutputHelper output)
{
    /// <summary>
    /// With B738s on gates 25 and 27, gate 26's group-III targets (the B738's group: a group-IV envelope at a 737 gate
    /// already overlaps its neighbours) get the live planner's verdict, planned like for like with the envelope: a target
    /// the planner refuses naming a neighbour is blocked by that neighbour, a spot target it plans as stored passed its
    /// sweep and is clear, and a target a forced tow plans as stored is blocked by one of the neighbours the forced tow notes
    /// passing inside the floor, or clear when it notes none.
    ///
    /// <para>The plain planner's keep is read as clearance for spot targets only, the faced goals. A taxilane or taxiway
    /// target's plan is a bare <c>PUSH &lt;twy&gt;</c>, and <c>TugMovePlanner.NeighbourRefusal</c> judges no non-faced
    /// goal: the tug stops short of a neighbour at fly time instead, so that keep carries no neighbour judgement at all.
    /// For those targets the cross-check is the forced plan's <see cref="TugForcedPassesNeighbour"/> instead — forced as
    /// stored with none noted is clear, and forced as stored with one noted is blocked by the neighbour it notes.</para>
    /// </summary>
    [Fact]
    public void GateNeighbours_LiveCheckAgreesWithTheLivePlanner()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        PushbackPose start = StartOf(Stand(layout, "26"));
        string group = GroupOf(Narrowbody);
        AircraftFootprint envelope = EnvelopeFootprint(group);
        NeighbourCandidate subject = Parked(Subject, Narrowbody, start, "26");
        List<NeighbourCandidate> others =
        [
            Parked("SWA25", Narrowbody, StartOf(Stand(layout, "25")), "25"),
            Parked("SWA27", Narrowbody, StartOf(Stand(layout, "27")), "27"),
        ];
        IReadOnlyList<TugParkedNeighbour> neighbours = TugParkedNeighbours.Build(subject, others);
        Assert.Equal(2, neighbours.Count);
        PushTargetLiveCheckRequest request = Request(subject, envelope, others);
        var classification = MovementAreaClassification.Build(layout, PushTargetPlannerTests.Sidecars.Value);
        int crossChecked = 0;
        foreach (PrecomputedPushTarget target in Target(PushTargetPlannerFixtures.OakEntries.Value, "26", group))
        {
            PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(target, request);
            TugRequest live = PushTargetPlanner.RequestFor(start, envelope, classification, GoalOf(layout, start, target), startsAtStand: true) with
            {
                ParkedNeighbours = neighbours,
            };
            TugPlan? plain = TugMovePlanner.Plan(layout, live, out string refusal);
            TugPlan? forced = TugMovePlanner.Plan(layout, live with { Forced = true }, out _);
            output.WriteLine(
                $"{target.Command}: {verdict}; planner {(plain is null ? $"refused ({refusal})" : "accepted")}"
                    + $"{(((plain is not null) && SameMoves(plain, target)) ? " as stored" : "")}; forced "
                    + $"{((forced is null) ? "refused" : (SameMoves(forced, target) ? "as stored" : "replanned"))}"
            );
            if ((plain is null) && (neighbours.FirstOrDefault(n => refusal.Contains(n.Callsign, StringComparison.Ordinal)) is { } named))
            {
                Assert.Equal(PushTargetLiveVerdict.Blocked(named.Callsign), verdict);
                crossChecked++;
            }
            else if ((plain is not null) && (target.Kind == PushTargetKind.Spot) && SameMoves(plain, target))
            {
                Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
                crossChecked++;
            }
            else if ((forced is not null) && SameMoves(forced, target))
            {
                HashSet<string> passedInside = [.. forced.ForcedOverrides.OfType<TugForcedPassesNeighbour>().Select(o => o.Callsign)];
                if (passedInside.Count == 0)
                {
                    Assert.Equal(PushTargetLiveVerdict.Clear, verdict);
                }
                else
                {
                    Assert.Equal(PushTargetLiveOutcome.Blocked, verdict.Outcome);
                    Assert.NotNull(verdict.BlockerCallsign);
                    Assert.Contains(verdict.BlockerCallsign, passedInside);
                }

                crossChecked++;
            }
        }

        Assert.True(crossChecked > 0, "no group-III target of gate 26 could be cross-checked against the live planner");
    }
}
