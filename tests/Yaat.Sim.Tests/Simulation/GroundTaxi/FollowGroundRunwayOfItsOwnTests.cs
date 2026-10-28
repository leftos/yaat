using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Which runway bars a <c>FOLLOWG</c> follower with no taxi route of its own holds at as a departure hold, at SFO in the 28/28
/// configuration. A runway assigned with <c>RWY</c> is its own departure runway: that runway's bar is a departure hold, and the
/// lead's departure bar of another runway stays a crossing. A bar the lead only crosses is a crossing, with no ready call
/// (7110.65 3-7-2.d).
/// </summary>
public class FollowGroundRunwayOfItsOwnTests(ITestOutputHelper output)
{
    private const string Type = "B738";
    private const string Follower = "FOL1";
    private const string Leader = "LED1";
    private const int StageBudgetSeconds = 120;
    private const int HoldBudgetSeconds = 600;

    /// <summary>How far (ft) the leader is let go from the spot before the follower is spawned on it.</summary>
    private const double LeaderClearOfSpotFt = 300.0;

    /// <summary>How far (ft) a follower once across a runway may come back toward the bar it crossed at: position noise, not a turn.</summary>
    private const double TurnBackToleranceFt = 20.0;

    [Fact]
    public void FollowerAssignedTheLeadsDepartureRunway_HoldsAtItsBarAsADepartureHoldForThatEnd()
    {
        if (StageBehindLeader("1", "TAXI A L F 28L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "RWY 28L");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");

        HoldingShortPhase hold = TickUntilFollowerHolds(ground, follower, leader);
        Assert.True(SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"), $"held short of {hold.HoldShort.TargetName}, not 28L");
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.Equal("28L", hold.HoldShort.TargetName);
    }

    [Fact]
    public void FollowerAssignedAnotherRunway_HoldsAtTheLeadsDepartureBarAsACrossing()
    {
        if (StageBehindLeader("1", "TAXI A L F 28L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "RWY 1R");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");

        HoldingShortPhase hold = TickUntilFollowerHolds(ground, follower, leader);
        Assert.True(SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"), $"held short of {hold.HoldShort.TargetName}, not 28L");
        Assert.Equal(HoldShortReason.RunwayCrossing, hold.HoldShort.Reason);
        Assert.NotEqual(PilotPendingRequestKind.Takeoff, follower.PendingPilotRequest?.Kind);
    }

    [Fact]
    public void FollowerWithNoRunway_BehindALeadCrossingOneRight_HoldsAtTheOneRightBarAsACrossing()
    {
        if (StageBehindLeader("3", "TAXI A F1 F RWY 28L CROSS 1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");

        // The follower crosses 1L once held at its bar; the leader, held short of 1R as a crossing, is then cleared across it.
        bool followerCrossedOneLeft = false;
        bool leaderCrossedOneRight = false;
        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follower.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R"),
            HoldBudgetSeconds,
            second =>
            {
                if (!followerCrossedOneLeft && (follower.Phases?.CurrentPhase is HoldingShortPhase atOneLeft))
                {
                    Assert.True(
                        SfoGroundHarness.HoldShortMatches(atOneLeft.HoldShort, "1L"),
                        $"t={second}: held at {atOneLeft.HoldShort.TargetName} first"
                    );
                    AssertSent(ground, Follower, "CROSS 1L");
                    followerCrossedOneLeft = true;
                }

                if (!leaderCrossedOneRight && (leader.Phases?.CurrentPhase is HoldingShortPhase leaderHold))
                {
                    Assert.Equal(HoldShortReason.RunwayCrossing, leaderHold.HoldShort.Reason);
                    AssertSent(ground, Leader, "CROSS 1R");
                    leaderCrossedOneRight = true;
                }
            }
        );
        Assert.True(held > 0, $"{Follower} never held short of 1R within {HoldBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        output.WriteLine($"held at t={held}s: node {hold.HoldShort.NodeId} {hold.HoldShort.Reason} {hold.HoldShort.TargetName}");
        Assert.True(leaderCrossedOneRight, $"{Leader} never held short of 1R as a crossing");
        Assert.Equal(HoldShortReason.RunwayCrossing, hold.HoldShort.Reason);
        Assert.NotEqual(PilotPendingRequestKind.Takeoff, follower.PendingPilotRequest?.Kind);
    }

    [Fact]
    public void FollowerWithNoDepartureAirportFiled_AssignedTheLeadsRunway_HoldsAtItsBarAsADepartureHold()
    {
        if (StageBehindLeader("1", "TAXI A L F 28L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        follower.FlightPlan.Departure = "";
        AssertSent(ground, Follower, "RWY 28L");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");

        HoldingShortPhase hold = TickUntilFollowerHolds(ground, follower, leader);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.Equal("28L", hold.HoldShort.TargetName);
    }

    /// <summary>
    /// The lead holds at its departure bar, so the follower knows it, and is then cleared across that runway there: a lead
    /// crossing keeps today's crossing behaviour, so the follower with no runway of its own holds at that bar as a crossing.
    /// </summary>
    [Fact]
    public void LeadCrossingAtItsDepartureBar_FollowerWithNoRunwayHoldsThereAsACrossing()
    {
        if (StageBehindLeader("1", "TAXI A L F 28L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");

        bool leaderCrossing = false;
        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is HoldingShortPhase,
            HoldBudgetSeconds,
            second =>
            {
                if (!leaderCrossing && (leader.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway }))
                {
                    AssertSent(ground, Leader, "CROSS 28L");
                    leaderCrossing = true;
                }
            }
        );
        Assert.True(held > 0, $"{Follower} never held short within {HoldBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        Assert.True(leaderCrossing, $"{Leader} never held at its departure bar");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        output.WriteLine($"held at t={held}s: node {hold.HoldShort.NodeId} {hold.HoldShort.Reason} {hold.HoldShort.TargetName}");
        Assert.True(SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"), $"held short of {hold.HoldShort.TargetName}, not 28L");
        Assert.Equal(HoldShortReason.RunwayCrossing, hold.HoldShort.Reason);
        Assert.NotEqual(PilotPendingRequestKind.Takeoff, follower.PendingPilotRequest?.Kind);
    }

    /// <summary>
    /// The follower with no runway holds at the lead's 28L bar as a departure hold, the lead departs, and the controller clears
    /// the follower across 28L there: it is crossing at that bar, not departing from it, so once across it never turns back
    /// toward the bar, holds at no bar of 28L and does not cross 28L again; it comes to rest on the far side.
    /// </summary>
    [Fact]
    public void CrossTwentyEightLeftAtTheLeadsDepartureBar_FollowerStaysAcrossAndStopsThere()
    {
        if (StageBehindLeader("1", "TAXI A L F 28L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        HoldingShortPhase hold = TickUntilFollowerHolds(ground, follower, leader);
        AssertDepartureHoldForTwentyEightLeft(hold);
        GroundNode leadBar = ground.Layout.Nodes[hold.HoldShort.NodeId];

        output.WriteLine($"{Leader} staged departed from {leader.Phases?.CurrentPhase?.Name}");
        leader.Phases = new PhaseList();
        leader.IsOnGround = false;
        leader.Altitude += 1500;
        AssertSent(ground, Follower, "CROSS 28L");

        Phase stop = TickUntilStoppedAcrossTwentyEightLeft(ground, follower, leadBar);
        output.WriteLine($"stopped in {stop.Name} at gs={follower.GroundSpeed:F1}");
        Assert.IsType<HoldingInPositionPhase>(stop);
        Assert.True(follower.GroundSpeed < 0.5, $"{Follower} is still moving at {follower.GroundSpeed:F1} kt");
        PhaseList phases = Assert.IsType<PhaseList>(follower.Phases);
        List<Phase> ahead = [.. phases.Phases.Skip(phases.CurrentIndex)];
        Assert.DoesNotContain(ahead, p => p is TaxiingPhase or CrossingRunwayPhase or FollowingPhase);
    }

    /// <summary>
    /// Ticks the follower cleared across 28L until it holds in position or short, to <see cref="HoldBudgetSeconds"/>; once its
    /// crossing has ended, asserts every second that it comes back no closer to <paramref name="leadBar"/> than
    /// <see cref="TurnBackToleranceFt"/> from the farthest it got, holds at no bar of 28L and does not cross a runway again. The
    /// phase it stops in.
    /// </summary>
    private Phase TickUntilStoppedAcrossTwentyEightLeft(SfoGround ground, AircraftState follower, GroundNode leadBar)
    {
        var twentyEightLeft = RunwayIdentifier.Parse("28L");
        bool crossing = false;
        bool across = false;
        double farthestFt = 0;
        for (int second = 1; second <= HoldBudgetSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            Phase? phase = follower.Phases?.CurrentPhase;
            double barFt = GeoMath.DistanceNm(follower.Position, leadBar.Position) * GeoMath.FeetPerNm;
            output.WriteLine($"+{second}s {Follower} {phase?.Name} gs={follower.GroundSpeed:F1} bar {barFt:F0} ft");
            crossing |= phase is CrossingRunwayPhase;
            if (!across)
            {
                across = crossing && (phase is not CrossingRunwayPhase);
                farthestFt = barFt;
                continue;
            }

            Assert.IsNotType<CrossingRunwayPhase>(phase);
            farthestFt = Math.Max(farthestFt, barFt);
            Assert.True(barFt >= farthestFt - TurnBackToleranceFt, $"+{second}s: turned back toward the bar, {barFt:F0} ft of {farthestFt:F0}");
            if (phase is HoldingShortPhase held)
            {
                bool atTwentyEightLeft = ground.Layout.Nodes[held.HoldShort.NodeId].RunwayId?.Overlaps(twentyEightLeft) ?? false;
                Assert.False(atTwentyEightLeft, $"+{second}s: held at a bar of 28L again");
            }

            if (phase is HoldingInPositionPhase or HoldingShortPhase)
            {
                return phase;
            }
        }

        Assert.Fail($"{Follower} never stopped across 28L within {HoldBudgetSeconds}s (crossed: {across})");
        return null!;
    }

    /// <summary>
    /// The lead departs while the follower with no runway waits at the 1R bar for its crossing; once cleared across, the follower
    /// taxis on along the lead's path to the lead's 28L bar and holds there as a departure hold.
    /// </summary>
    [Fact]
    public void LeadDepartsWhileTheFollowerWaitsToCrossOneRight_CrossedFollowerHoldsAtTheLeadsBar()
    {
        if (CrossOneRightAfterTheLeadDeparted(keepLeadRoute: true) is { } run)
        {
            AssertDepartureHoldForTwentyEightLeft(Assert.IsType<HoldingShortPhase>(run.Stop));
        }
    }

    /// <summary>
    /// As <see cref="LeadDepartsWhileTheFollowerWaitsToCrossOneRight_CrossedFollowerHoldsAtTheLeadsBar"/>, with the lead's path
    /// gone (the departed lead shows no taxi route): a lead with no path left is a missing route, not a reason to stay inside
    /// the 1R hold line, so the follower's crossing ends with its centre ½ its length plus
    /// <see cref="RunwayCrossingFarSideGate.MarginFt"/> past the far 1R bar (AIM 2-3-5.a.1, AIM 4-3-21.b), and it then taxis on to
    /// the lead's 28L bar and holds there as a departure hold, never crossing 28L.
    /// </summary>
    [Fact]
    public void LeadDepartsWithNoPathLeft_FollowerClearsOneRightAndHoldsAtTheLeadsBar()
    {
        if (CrossOneRightAfterTheLeadDeparted(keepLeadRoute: false) is not { } run)
        {
            return;
        }

        double clearFt = (AircraftLength.ResolveFt(Type) / 2.0) + RunwayCrossingFarSideGate.MarginFt;
        Assert.True(run.CentrePastFarBarFt is not null, $"{Follower}'s 1R crossing never ended");
        Assert.True(
            run.CentrePastFarBarFt >= clearFt,
            $"{Follower} ended its 1R crossing with its centre {run.CentrePastFarBarFt:F1} ft past the far bar, short of {clearFt:F1} ft"
        );
        AssertDepartureHoldForTwentyEightLeft(Assert.IsType<HoldingShortPhase>(run.Stop));
        var twentyEightLeft = RunwayIdentifier.Parse("28L");
        Assert.DoesNotContain(run.CrossedRunways, runway => runway.Overlaps(twentyEightLeft));
    }

    /// <summary>
    /// The lead is cleared for takeoff on its way to its bar; while it rolls, a follower with no runway of its own goes on to that
    /// bar and holds there as a departure hold.
    /// </summary>
    [Fact]
    public void LeadRollingForTakeoff_FollowerWithNoRunwayHoldsAtTheLeadsBar()
    {
        if (StageBehindLeader("1", "TAXI A L F 28L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        (HoldingShortPhase hold, bool sawDepartingWhileRolling) = TickUntilFollowerStopsBehindALeadTakingOff(ground, follower, leader);
        AssertDepartureHoldForTwentyEightLeft(hold);
        Assert.True(sawDepartingWhileRolling, $"{Follower}'s follow did not take {Leader} rolling for takeoff as departed");
    }

    /// <summary>
    /// A follower with its own runway, the lead's, keeps following while the lead rolls for takeoff, rather than stopping where it
    /// is, and holds at its own bar; it does not take the rolling lead as departed.
    /// </summary>
    [Fact]
    public void LeadRollingForTakeoff_FollowerAssignedTheSameRunwayKeepsFollowingToItsBar()
    {
        if (StageBehindLeader("1", "TAXI A L F 28L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, "RWY 28L");
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        (HoldingShortPhase hold, bool sawDepartingWhileRolling) = TickUntilFollowerStopsBehindALeadTakingOff(ground, follower, leader);
        AssertDepartureHoldForTwentyEightLeft(hold);
        Assert.False(sawDepartingWhileRolling, $"{Follower}'s follow took {Leader} rolling for takeoff as departed");
    }

    private static void AssertDepartureHoldForTwentyEightLeft(HoldingShortPhase hold)
    {
        Assert.Equal(HoldShortReason.DestinationRunway, hold.HoldShort.Reason);
        Assert.Equal("28L", hold.HoldShort.TargetName);
    }

    /// <summary>
    /// With the follower waiting at the 1R bar and the lead at its 28L bar (<see cref="WaitAtOneRightWithTheLeadAtItsBar"/>), the
    /// lead is put in the air (keeping its taxi route or not), and the follower is cleared across 1R. What the follower did
    /// until it stopped; null on the silent-skip path.
    /// </summary>
    private FollowerPastOneRight? CrossOneRightAfterTheLeadDeparted(bool keepLeadRoute)
    {
        if (WaitAtOneRightWithTheLeadAtItsBar() is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        output.WriteLine($"{Leader} staged departed from {leader.Phases?.CurrentPhase?.Name}, keeping its route: {keepLeadRoute}");
        leader.Phases = new PhaseList();
        leader.IsOnGround = false;
        leader.Altitude += 1500;
        if (!keepLeadRoute)
        {
            leader.Ground.AssignedTaxiRoute = null;
        }

        AssertSent(ground, Follower, "CROSS 1R");
        return TickUntilFollowerStopsPastOneRight(ground, follower);
    }

    /// <summary>
    /// What a follower cleared across 1R did until it stopped: the phase it stopped in, how far (ft) its centre was past the far
    /// bar of its first crossing, along the way across, at the second that crossing ended (null if it never ended), and the runways
    /// of every bar it crossed to.
    /// </summary>
    private sealed record FollowerPastOneRight(Phase Stop, double? CentrePastFarBarFt, List<RunwayIdentifier> CrossedRunways);

    /// <summary>
    /// Ticks until the follower stops anywhere but the 1R bar: holding short, or holding in position; asserts its first crossing
    /// is of 1R. What it did on the way.
    /// </summary>
    private FollowerPastOneRight TickUntilFollowerStopsPastOneRight(SfoGround ground, AircraftState follower)
    {
        CrossingRunwayPhase? first = null;
        double? centrePastFarBarFt = null;
        List<RunwayIdentifier> crossedRunways = [];
        int stopped = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (follower.Phases?.CurrentPhase is HoldingInPositionPhase)
                || ((follower.Phases?.CurrentPhase is HoldingShortPhase hold) && !SfoGroundHarness.HoldShortMatches(hold.HoldShort, "1R")),
            HoldBudgetSeconds,
            second =>
            {
                if (follower.Phases?.CurrentPhase is CrossingRunwayPhase crossing)
                {
                    if ((ground.Layout.Nodes[crossing.TargetNodeId].RunwayId is { } runway) && !crossedRunways.Contains(runway))
                    {
                        crossedRunways.Add(runway);
                    }

                    first ??= crossing;
                    return;
                }

                if ((first is not null) && (centrePastFarBarFt is null))
                {
                    centrePastFarBarFt = CentrePastFarBarFt(ground.Layout, follower, first);
                    output.WriteLine($"t={second}s: crossing #{first.ApproachNodeId}->#{first.TargetNodeId} ended {centrePastFarBarFt:F1} ft past");
                }
            }
        );
        Assert.True(stopped > 0, $"{Follower} never stopped within {HoldBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        output.WriteLine($"stopped at t={stopped}s: {follower.Phases?.CurrentPhase?.Name}");
        Assert.True(first is not null, $"{Follower} never crossed 1R");
        Assert.True(
            ground.Layout.Nodes[first.TargetNodeId].RunwayId?.Overlaps(RunwayIdentifier.Parse("1R")) ?? false,
            $"{Follower}'s first crossing ends at #{first.TargetNodeId}, not a 1R bar"
        );
        return new FollowerPastOneRight(Assert.IsAssignableFrom<Phase>(follower.Phases?.CurrentPhase), centrePastFarBarFt, crossedRunways);
    }

    /// <summary>How far (ft) the aircraft's centre is past <paramref name="crossing"/>'s far bar, along the way across from its near bar.</summary>
    private static double CentrePastFarBarFt(AirportGroundLayout layout, AircraftState aircraft, CrossingRunwayPhase crossing)
    {
        GroundNode nearBar = layout.Nodes[crossing.ApproachNodeId];
        GroundNode farBar = layout.Nodes[crossing.TargetNodeId];
        var across = new TrueHeading(GeoMath.BearingTo(nearBar.Position, farBar.Position));
        return GeoMath.AlongTrackDistanceNm(aircraft.Position, farBar.Position, across) * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// Stages a follower with no runway behind a lead taxiing across 1L and 1R to 28L: the follower is cleared across 1L and
    /// waits at the 1R bar until the lead holds at its 28L bar. Null on the silent-skip path.
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? WaitAtOneRightWithTheLeadAtItsBar()
    {
        if (StageBehindLeader("3", "TAXI A F1 F RWY 28L CROSS 1L 1R") is not { } staged)
        {
            return null;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        AssertSent(ground, Follower, $"FOLLOWG {Leader}");
        bool followerCrossedOneLeft = false;
        int waiting = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (follower.Phases?.CurrentPhase is HoldingShortPhase atOneRight)
                && SfoGroundHarness.HoldShortMatches(atOneRight.HoldShort, "1R")
                && (leader.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway }),
            HoldBudgetSeconds,
            second =>
            {
                if (!followerCrossedOneLeft && (follower.Phases?.CurrentPhase is HoldingShortPhase atOneLeft))
                {
                    Assert.True(
                        SfoGroundHarness.HoldShortMatches(atOneLeft.HoldShort, "1L"),
                        $"t={second}: held at {atOneLeft.HoldShort.TargetName}"
                    );
                    AssertSent(ground, Follower, "CROSS 1L");
                    followerCrossedOneLeft = true;
                }
            }
        );
        Assert.True(waiting > 0, $"{Follower} never waited at 1R with {Leader} at its bar: {follower.Phases?.CurrentPhase?.Name}");
        return (ground, follower, leader);
    }

    /// <summary>
    /// Clears the leader for takeoff once the follow is under way, then ticks until the follower stops; asserts the follower was
    /// still following while the leader rolled for takeoff. The follower's hold, and whether its follow took the leader as
    /// departed (<see cref="FollowingPhase.LeadSeenDeparting"/>) while the leader was still rolling on the ground.
    /// </summary>
    private (HoldingShortPhase Hold, bool SawDepartingWhileRolling) TickUntilFollowerStopsBehindALeadTakingOff(
        SfoGround ground,
        AircraftState follower,
        AircraftState leader
    )
    {
        bool leaderCleared = false;
        bool followedWhileLeadRolled = false;
        bool sawDepartingWhileRolling = false;
        int stopped = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is HoldingShortPhase or HoldingInPositionPhase,
            HoldBudgetSeconds,
            second =>
            {
                if (!leaderCleared && (leader.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway }))
                {
                    AssertSent(ground, Leader, "CTO");
                    leaderCleared = true;
                }

                if ((leader.Phases?.CurrentPhase is TakeoffPhase) && leader.IsOnGround && (follower.Phases?.CurrentPhase is FollowingPhase follow))
                {
                    followedWhileLeadRolled = true;
                    sawDepartingWhileRolling |= follow.LeadSeenDeparting;
                }
            }
        );
        Assert.True(stopped > 0, $"{Follower} never stopped within {HoldBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        output.WriteLine($"stopped at t={stopped}s: {follower.Phases?.CurrentPhase?.Name}");
        Assert.True(followedWhileLeadRolled, $"{Follower} was not following while {Leader} rolled for takeoff");
        return (Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase), sawDepartingWhileRolling);
    }

    /// <summary>
    /// Spawns the leader at <paramref name="spot"/>, clears it on <paramref name="leaderTaxi"/>, and once it is
    /// <see cref="LeaderClearOfSpotFt"/> away spawns the follower on the same spot with no taxi route. Null on the repo's
    /// silent-skip path (no navdata / no SFO layout).
    /// </summary>
    private (SfoGround Ground, AircraftState Follower, AircraftState Leader)? StageBehindLeader(string spot, string leaderTaxi)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            output.WriteLine("SKIP: SFO layout or navdata unavailable");
            return null;
        }

        AircraftState leader = SfoGroundHarness.SpawnAtSpot(ground, Leader, Type, spot);
        LatLon spotPosition = leader.Position;
        AssertSent(ground, Leader, leaderTaxi);
        int clear = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (GeoMath.DistanceNm(spotPosition, leader.Position) * GeoMath.FeetPerNm) > LeaderClearOfSpotFt,
            StageBudgetSeconds,
            null
        );
        Assert.True(clear > 0, $"{Leader} never got {LeaderClearOfSpotFt} ft from spot {spot} within {StageBudgetSeconds}s");

        AircraftState follower = SfoGroundHarness.SpawnAtSpot(ground, Follower, Type, spot);
        Assert.Null(follower.Ground.AssignedTaxiRoute);
        return (ground, follower, leader);
    }

    /// <summary>Ticks until the follower holds short, lining the leader up once it holds at its departure bar; the hold.</summary>
    private HoldingShortPhase TickUntilFollowerHolds(SfoGround ground, AircraftState follower, AircraftState leader)
    {
        bool leaderLinedUp = false;
        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.Phases?.CurrentPhase is HoldingShortPhase,
            HoldBudgetSeconds,
            second =>
            {
                if ((second % 10) == 0)
                {
                    output.WriteLine(
                        $"t={second} {Follower} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} "
                            + $"{Leader} {leader.Phases?.CurrentPhase?.Name} gs={leader.GroundSpeed:F1}"
                    );
                }

                // Line the leader up so the follower comes up to the departure bar rather than stopping behind it.
                if (!leaderLinedUp && (leader.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway }))
                {
                    leaderLinedUp = ground.Engine.SendCommand(Leader, "LUAW").Success;
                }
            }
        );
        Assert.True(held > 0, $"{Follower} never held short within {HoldBudgetSeconds}s: {follower.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(follower.Phases?.CurrentPhase);
        output.WriteLine($"held at t={held}s: node {hold.HoldShort.NodeId} {hold.HoldShort.Reason} {hold.HoldShort.TargetName}");
        return hold;
    }

    private void AssertSent(SfoGround ground, string callsign, string command)
    {
        CommandResult result = ground.Engine.SendCommand(callsign, command);
        output.WriteLine($"{callsign} {command} -> success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"{callsign} {command}: {result.Message}");
    }
}
