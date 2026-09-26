using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// A committed exit restored from a snapshot must still be a drivable chain on the layout it is restored against.
/// Ground-node ids are assigned when the layout is built (fillet tangent cuts are numbered as they are generated), so a
/// snapshot recorded against an older build of the same airport can carry ids that now name other nodes. The S2-OAK
/// recording behind <c>Issue239Lineup33CornerCutTests</c> did exactly that: UPS4723's candidate exit J on OAK 28R came
/// back as a path that, on today's layout, jumps from taxiway J to the 28R centerline to the 28L centerline.
/// <see cref="Phases.Ground.RunwayExitPhase"/> then logged "no edge between nodes", dropped the exit, re-searched from
/// 27 ft short of P's branch at 35 kt, and the navigator's arc playback tripped its teleport check.
///
/// <para>Both cases build their path from today's J exit off OAK 28R, found on the layout at runtime, so neither
/// depends on node ids that a later layout build renumbers.</para>
/// </summary>
public class LandingPhaseStaleExitPathRestoreTests
{
    private const double Oak28rHeadingDeg = 292.3;
    private const double Oak28rThresholdLat = 37.724806;
    private const double Oak28rThresholdLon = -122.204721;

    private static LandingPhaseDto RolloutDto(int branchId, int holdShortId, List<int> pathIds) =>
        new()
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 30,
            FieldElevation = 9,
            RunwayHeadingDeg = Oak28rHeadingDeg,
            ThresholdLat = Oak28rThresholdLat,
            ThresholdLon = Oak28rThresholdLon,
            TouchedDown = true,
            CanGoAround = false,
            LahsoHoldShortDistNm = 0,
            HasLahso = false,
            StoppedForLahso = false,
            CandidateExitBranchPointId = branchId,
            CandidateExitHoldShortId = holdShortId,
            CandidateExitTaxiway = "J",
            CandidateExitTurnOffSpeed = 30,
            CandidateExitPathNodeIds = pathIds,
        };

    /// <summary>Today's J exit off OAK 28R, searched from the threshold; null when the OAK layout is unavailable.</summary>
    private static (AirportGroundLayout Layout, List<GroundNode> Path)? TodaysJExit()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        AirportGroundLayout.CenterlineExitResult? exit = layout.FindOnSidePreferredExit(
            Oak28rThresholdLat,
            Oak28rThresholdLon,
            new TrueHeading(Oak28rHeadingDeg),
            "28R",
            new ExitPreference { Taxiway = "J" },
            sidePref: null
        );

        Assert.NotNull(exit);
        Assert.Equal("J", exit.Value.Taxiway);
        Assert.Equal(exit.Value.HoldShort.Id, exit.Value.Path[^1].Id);
        return (layout, exit.Value.Path);
    }

    private static bool Adjacent(GroundNode from, GroundNode to) => from.Edges.Any(edge => edge.OtherNodeId(from.Id) == to.Id);

    [Fact]
    public void Restore_WithAPathThatIsNotAConnectedChain_DropsTheCandidate()
    {
        if (TodaysJExit() is not { } jExit)
        {
            return;
        }

        List<GroundNode> path = jExit.Path;

        // Precondition: dropping the second node leaves a gap — the branch point and the third node share no edge — so
        // the broken path really is not a chain, and it still starts at the branch and ends at the hold-short.
        Assert.True(path.Count >= 3, $"J exit path has {path.Count} nodes; the broken case needs a middle node to drop");
        Assert.False(Adjacent(path[0], path[2]), $"nodes {path[0].Id} and {path[2].Id} share an edge, so dropping {path[1].Id} breaks nothing");
        List<int> brokenIds = [path[0].Id, .. path.Skip(2).Select(n => n.Id)];

        LandingPhaseDto dto = RolloutDto(branchId: path[0].Id, holdShortId: path[^1].Id, pathIds: brokenIds);

        var restored = LandingPhase.FromSnapshot(dto, jExit.Layout);

        Assert.Null(restored.CandidateExit);
    }

    [Fact]
    public void Restore_WithAConnectedChainFromBranchToHoldShort_KeepsTheCandidate()
    {
        if (TodaysJExit() is not { } jExit)
        {
            return;
        }

        List<GroundNode> path = jExit.Path;

        // Precondition: the search's path is a chain, node to node, from the branch point to the hold-short.
        Assert.True(path.Count >= 2, $"J exit path has {path.Count} nodes");
        for (int i = 0; i < path.Count - 1; i++)
        {
            Assert.True(Adjacent(path[i], path[i + 1]), $"J exit path nodes {path[i].Id} and {path[i + 1].Id} share no edge");
        }

        List<int> pathIds = [.. path.Select(n => n.Id)];
        LandingPhaseDto dto = RolloutDto(branchId: path[0].Id, holdShortId: path[^1].Id, pathIds: pathIds);

        var restored = LandingPhase.FromSnapshot(dto, jExit.Layout);

        Assert.NotNull(restored.CandidateExit);
        Assert.Equal(pathIds, restored.CandidateExit.Path.Select(n => n.Id));
    }
}
