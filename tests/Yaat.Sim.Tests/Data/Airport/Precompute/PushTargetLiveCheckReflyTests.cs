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
/// Every OAK sample target re-flown with the smallest and largest member of its design group ends where it should.
/// </summary>
public class PushTargetLiveCheckReflyTests(ITestOutputHelper output)
{
    /// <summary>
    /// Every target the OAK sample stands store for a design group, re-flown with the smallest and the largest member of
    /// that group (<see cref="PushTargetLiveCheckFixtures.MemberTypes"/>), completes, and every one whose last move is not a floating-stop line ends
    /// less than half the type's length from the stored end (<see cref="PushTargetLiveCheck.EndDriftFt"/>). Measured by
    /// this test, which prints each group's largest end gap over every target of gates 25, 26 and 27 for both types. A
    /// target whose last move is a floating-stop line is held to completion alone: completing the line puts the aircraft
    /// on it, and its end slides along the line with the turn radius (gate 26's group-II <c>PUSH TE</c> ends 68.9 ft
    /// further along TE for a BE95, more than five times half its length), which is not part of the plan.
    /// </summary>
    [Fact]
    public void ActualRadiusRefly_EndsWithinHalfALength_ForEveryGroup()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        int reflown = 0;
        foreach (IGrouping<string, PushTargetEntry> group in PushTargetPlannerFixtures.OakEntries.Value.GroupBy(e => e.DesignGroup))
        {
            (string smallest, string largest) = Assert.NotNull(MemberTypes(group.Key));
            double endGapMaxFt = 0.0;
            foreach (PushTargetEntry entry in group)
            {
                PushbackPose start = StartOf(Stand(layout, entry.StandName));
                foreach ((PrecomputedPushTarget target, string type) in entry.Targets.SelectMany(t => new[] { (t, smallest), (t, largest) }))
                {
                    AircraftFootprint footprint = PinnedFootprint(type);
                    TugSimulation flown = Refly(target, start, footprint);
                    Assert.True(flown.Completed, $"{target.Command} from {entry.StandName} (group {group.Key}) did not complete for a {type}");
                    reflown++;
                    if (PushTargetLiveCheck.EndsOnAFloatingLine(target))
                    {
                        continue;
                    }

                    double driftFt = PushTargetLiveCheck.EndDriftFt(target, flown);
                    Assert.True(
                        driftFt < PushTargetLiveCheck.EndDriftLimitFt(footprint),
                        $"{target.Command} from {entry.StandName} (group {group.Key}) drifted {driftFt:F2} ft for a {type}, "
                            + $"limit {PushTargetLiveCheck.EndDriftLimitFt(footprint):F1} ft"
                    );
                    endGapMaxFt = Math.Max(endGapMaxFt, driftFt);
                }
            }

            output.WriteLine($"group {group.Key} ({smallest}, {largest}): end gap at most {endGapMaxFt:F2} ft");
        }

        Assert.True(reflown > 0, "the OAK sample stands store no targets");
    }
}
