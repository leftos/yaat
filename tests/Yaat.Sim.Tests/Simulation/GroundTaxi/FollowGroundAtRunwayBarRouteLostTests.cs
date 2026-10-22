using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A follower whose follow route is lost brakes along it to rest, and never stops inside a hold line or reverses.
/// </summary>
public class FollowGroundAtRunwayBarRouteLostTests(ITestOutputHelper output) : FollowGroundAtRunwayBarTestBase(output)
{
    /// <summary>
    /// A follower rolling along a straight taxiway edge past 1R on its follow route loses that route when its leader is re-routed
    /// along F1 through it: it keeps the route and brakes to rest along it, its centre never more than
    /// <see cref="LostRouteOffsetFt"/> off the route's centreline, and drops the route once at rest.
    /// </summary>
    [Fact]
    public void Following_RouteLostOnAStraight_BrakesAlongTheRouteToRest()
    {
        if (StageLostRoute(onFillet: false) is { } lost)
        {
            AssertBrakesAlongTheLostRouteToRest(lost);
        }
    }

    /// <summary>
    /// A follower partway round a fillet arc on its follow route loses that route when its leader is re-routed along F1 through
    /// it: it brakes to rest round the fillet's curve, its centre never more than <see cref="LostRouteOffsetFt"/> off it, rather
    /// than rolling on along its heading, and drops the route once at rest.
    /// </summary>
    [Fact(Skip = "Needs a fillet long enough to stage mid-arc; re-enabled by YAAT-675 (follower squares the fillet corner via a 14 ft stub).")]
    public void Following_RouteLostMidFillet_BrakesRoundTheFilletToRest()
    {
        if (StageLostRoute(onFillet: true) is { } lost)
        {
            AssertBrakesAlongTheLostRouteToRest(lost);
        }
    }

    /// <summary>
    /// A follower braking to rest along a lost route brakes for its lead (<see cref="FollowingPhase.LostRouteLeadGapDecelRate"/>)
    /// at the max-effort rate only when the taxi rate would end inside the stop gap behind a lead ahead of it, and at the taxi
    /// rate for a lead ahead outside that stop, a lead abeam or behind it however close, a lead off the ground, and no lead. No
    /// real-layout fixture brings a lead within a follower's stop on a lost route — the follow's gap curve keeps a follower
    /// outside its taxi-rate stop behind a standing lead — so the choice is checked on B738s placed around the follower.
    /// </summary>
    [Fact]
    public void Following_RouteLost_BrakesFirmOnlyForALeadAheadInsideTheTaxiRateStop()
    {
        TestVnasData.EnsureInitialized();
        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double stopGapFt = FollowGap.StopGapFt(Type, category, Type, category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        double maxEffortRate = CategoryPerformance.ExpediteExitDecelRate(category);
        const double SpeedKts = 15.0;
        double routineStopFt = GroundStopBraking.StoppingDistanceFt(SpeedKts, taxiRate);
        double maxEffortStopFt = GroundStopBraking.StoppingDistanceFt(SpeedKts, maxEffortRate);
        Output.WriteLine($"stop gap {stopGapFt:F1} ft, stop at {SpeedKts} kt: taxi rate {routineStopFt:F1} ft, max effort {maxEffortStopFt:F1} ft");
        Assert.True(maxEffortStopFt < routineStopFt, "the max-effort rate stops no shorter than the taxi rate");

        AircraftState follower = OnGround("FOL9", Origin, 0.0, SpeedKts);
        Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(follower, category, DeadAhead(follower, stopGapFt + routineStopFt + 20.0)));
        double firmGapFt = stopGapFt + ((routineStopFt + maxEffortStopFt) / 2.0);
        Assert.Equal(maxEffortRate, FollowingPhase.LostRouteLeadGapDecelRate(follower, category, DeadAhead(follower, firmGapFt)));
        Assert.Equal(maxEffortRate, FollowingPhase.LostRouteLeadGapDecelRate(follower, category, DeadAhead(follower, stopGapFt)));

        Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(follower, category, null));
        AircraftState airborne = DeadAhead(follower, stopGapFt);
        airborne.IsOnGround = false;
        Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(follower, category, airborne));

        // Abeam and behind, a follower rolling fast enough that the gap alone would call for the max-effort rate.
        const double FastKts = 35.0;
        AircraftState fast = OnGround("FOL9", Origin, 0.0, FastKts);
        double fastRoutineStopFt = GroundStopBraking.StoppingDistanceFt(FastKts, taxiRate);
        double halfFt = AircraftLength.ResolveFt(Type) / 2.0;
        AircraftState abeam = OnGround("LED9", GeoMath.ProjectPoint(Origin, new TrueHeading(90.0), 80.0 / GeoMath.FeetPerNm), 0.0, 0.0);
        double behindFt = halfFt + 20.0 + halfFt;
        AircraftState behind = OnGround("LED9", GeoMath.ProjectPoint(Origin, new TrueHeading(180.0), behindFt / GeoMath.FeetPerNm), 180.0, 0.0);
        foreach ((string where, AircraftState lead) in new[] { ("abeam", abeam), ("behind", behind) })
        {
            double toStopFt = FollowingPhase.NoseToTailFt(fast, lead) - stopGapFt;
            Output.WriteLine($"lead {where}: {toStopFt:F1} ft past the stop gap, taxi-rate stop from {FastKts} kt {fastRoutineStopFt:F1} ft");
            Assert.True(fastRoutineStopFt > toStopFt, $"the lead {where} is outside the taxi-rate stop, so the sector proves nothing");
            Assert.Null(FollowingPhase.LostRouteLeadGapDecelRate(fast, category, lead));
        }
    }

    /// <summary>
    /// A follower on 1R's pavement, crossing it on its follow route under <c>CROSS 1L 1R</c>, loses that route when its leader is
    /// re-routed along F1 through it: its clearing route takes over the same tick rather than braking along the lost route, and
    /// it stops clear past the 1R hold line, never stopped on the runway.
    /// </summary>
    [Fact]
    public void Following_RouteLostOnARunway_ClearsPastTheHoldLine()
    {
        if (Stage(TaxiToOneLeftBar, "1L") is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        StandLeaderWithNoRoute(ground, leader, ToFJunction);
        AssertSent(ground, Follower, $"FOLLOWG {Leader}; CROSS 1L 1R");
        int onRunwayAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute.IsComplete: false })
                && RunwayOccupancy.IsOnPavement(follower, runway1R)
                && (follower.GroundSpeed > SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            second => LogFollow(second, follower, leader)
        );
        Assert.True(onRunwayAt > 0, $"the follower never rolled on 1R on its follow route within {CrossBudgetSeconds}s of CROSS");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);

        RerouteThroughTheFollower(ground, leader, follower);
        ground.Engine.TickOneSecond();
        LogFollow(onRunwayAt + 1, follower, leader);
        Assert.Null(follow.FollowRoute);
        Assert.False(follow.IsBrakingLostRoute, "the follower on 1R braked along its lost route rather than clearing the runway");
        Assert.NotNull(follow.ClearingRoute);

        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follow.ClearingRoute is null) && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                RunwayInfo? under = RunwayOccupancy
                    .AirportRunways(ground.Layout.AirportId)
                    .FirstOrDefault(r => RunwayOccupancy.IsOnPavement(follower, r));
                Assert.False(
                    (under is not null) && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
                    $"+{second}s: stopped on runway {under?.Id} pavement"
                );
            }
        );
        Assert.True(restAt > 0, $"the follower never came to rest clear of 1R within {CrossBudgetSeconds}s of losing its route");
        AssertStoppedClearPastOneRight(ground, follower);
    }

    /// <summary>
    /// A follower crossing 1L and 1R under <c>CROSS 1L 1R</c> loses its follow route between the two runways, where braking at
    /// the taxi rate would stop it inside 1R's hold line but the firm rate stops it short: it brakes at the firm rate, harder
    /// than the taxi rate, and comes to rest short of 1R, no part of it ever at rest inside a runway's hold line.
    /// </summary>
    [Fact]
    public void Following_RouteLostShortOfACrossedBar_StopsShortAtTheFirmRate()
    {
        if (StageLostApproachingOneRight(firmStopMakesTheLine: true) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        RunwayInfo runway1R = SfoGroundHarness.Runway("1R");
        double worstDropKts = 0.0;
        double lastKts = follower.GroundSpeed;
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => follower.GroundSpeed < RestKts,
            HeldSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                worstDropKts = Math.Max(worstDropKts, lastKts - follower.GroundSpeed);
                lastKts = follower.GroundSpeed;
                AssertNeverAtRestInsideAHoldLine(ground, follower, second);
                Assert.False(RunwayOccupancy.IsOnPavement(follower, runway1R), $"+{second}s: the follower rolled onto 1R");
            }
        );

        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        Output.WriteLine($"rest at +{restAt}s; hardest one-second speed drop {worstDropKts:F2} kt (taxi rate {taxiRate:F1} kt/s)");
        Assert.True(restAt > 0, $"the follower is still rolling at {follower.GroundSpeed:F1} kt {HeldSeconds}s after losing its route");
        Assert.True(worstDropKts > taxiRate, $"the follower braked no harder than the taxi rate ({worstDropKts:F2} kt in a second)");
        Assert.Null(FollowingPhase.RunwayInsideHoldLine(follower, ground.Layout));
    }

    /// <summary>
    /// A follower crossing 1L and 1R under <c>CROSS 1L 1R</c> loses its follow route between the two runways too close to 1R's
    /// hold line for even the firm rate to stop it short: it carries on across 1R under its crossing clearance and comes to rest
    /// clear past 1R's far hold line, no part of it ever at rest inside a runway's hold line.
    /// </summary>
    [Fact]
    public void Following_RouteLostTooCloseToACrossedBar_CrossesAndClearsBeyond()
    {
        if (StageLostApproachingOneRight(firmStopMakesTheLine: false) is not { } staged)
        {
            return;
        }

        (SfoGround ground, AircraftState follower, AircraftState leader) = staged;
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        int restAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (follow.ClearingRoute is null) && !follow.IsBrakingLostRoute && (follower.GroundSpeed < SfoGroundHarness.StationarySpeedKts),
            CrossBudgetSeconds,
            second =>
            {
                LogFollow(second, follower, leader);
                AssertNeverAtRestInsideAHoldLine(ground, follower, second);
            }
        );
        Assert.True(restAt > 0, $"the follower never came to rest within {CrossBudgetSeconds}s of losing its route");
        AssertStoppedClearPastOneRight(ground, follower);
    }

    /// <summary>
    /// A follower rolling on its follow route past 1R whose leader is re-routed onto a path that joins behind it, where the
    /// follower has already been: it never turns about on its own edge — while it moves on that edge, its heading
    /// stays within 90° of the edge's bearing, and its follow route never drives the edge back — and it ends at rest, or on a
    /// route that leads on ahead of it. Off the edge, a forward turn onto another taxiway may swing it further.
    /// </summary>
    [Fact]
    public void Following_LeadReroutedBehindTheFollower_NeverReversesOnItsOwnEdge()
    {
        if (StageRolling(onFillet: false) is { } rolling)
        {
            AssertNeverReversesOnItsOwnEdge(rolling.Ground, rolling.Follower, rolling.Leader, rolling.Route, RerouteBehindTheFollower);
        }
    }

    /// <summary>
    /// A follower partway round the F1-F fillet arc on its follow route whose leader is re-routed along F1 behind it: the
    /// re-plan's route starts at the arc's far end and runs straight back round the arc, the way the follower came. A route the
    /// follow would install from its start, leaving back across the heading, is never adopted: it never turns about on the arc
    /// it is rolling round, and its follow route never drives the arc back.
    /// </summary>
    [Fact(Skip = "Needs a fillet long enough to stage mid-arc; re-enabled by YAAT-675 (follower squares the fillet corner via a 14 ft stub).")]
    public void Following_LeadReroutedBehindTheFollowerMidFillet_NeverReversesOnTheArc()
    {
        if (StageRolling(onFillet: true) is { } rolling)
        {
            AssertNeverReversesOnItsOwnEdge(rolling.Ground, rolling.Follower, rolling.Leader, rolling.Route, RerouteThroughTheFollower);
        }
    }

    /// <summary>
    /// A follower braking along the follow route its leader's re-route lost, snapshotted through the recording JSON and restored
    /// into a second engine, keeps braking along it: ticked side by side, the two stand at the same position every second, and
    /// both drop the route once at rest.
    /// </summary>
    [Fact]
    public void Following_SnapshotWhileBrakingAlongTheLostRoute_StopsAsTheOriginal()
    {
        if (StageLostRoute(onFillet: false) is not { } lost)
        {
            return;
        }

        string json = JsonSerializer.Serialize(lost.Ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        Assert.Contains("BrakingLostRoute", json, StringComparison.OrdinalIgnoreCase);
        SfoGround restoredGround = Assert.IsType<SfoGround>(SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off));
        restoredGround.Engine.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Follower));
        FollowingPhase restoredFollow = Assert.IsType<FollowingPhase>(restored.Phases?.CurrentPhase);
        Assert.True(restoredFollow.IsBrakingLostRoute, "the restored follow forgot it was braking along its lost route");
        Assert.NotNull(restoredFollow.FollowRoute);

        for (int second = 1; second <= HeldSeconds; second++)
        {
            lost.Ground.Engine.TickOneSecond();
            restoredGround.Engine.TickOneSecond();
            Assert.Equal(lost.Follower.Phases?.CurrentPhase?.Name, restored.Phases?.CurrentPhase?.Name);
            Assert.Equal(lost.Follower.Position, restored.Position);
        }

        Output.WriteLine($"after {HeldSeconds}s: original at {lost.Follower.Position}, restored at {restored.Position}");
        Assert.True(restored.GroundSpeed < RestKts, $"the restored follower is still rolling at {restored.GroundSpeed:F1} kt");
        Assert.False(restoredFollow.IsBrakingLostRoute, "the restored follower at rest still holds the route it lost");
    }

    /// <summary>
    /// A follow not braking along a lost route writes no <c>BrakingLostRoute</c> into its snapshot, so its phase JSON is the
    /// same as before the field existed.
    /// </summary>
    [Fact]
    public void Following_SnapshotWhileNotBraking_OmitsBrakingLostRoute()
    {
        if (StageRolling(onFillet: false) is not { } rolling)
        {
            return;
        }

        FollowingPhase follow = rolling.Follow;
        Assert.False(follow.IsBrakingLostRoute);
        string json = JsonSerializer.Serialize(Assert.IsType<FollowingPhaseDto>(follow.ToSnapshot()), RecordingJsonOptions.Default);
        Output.WriteLine(json.Length > 400 ? json[..400] : json);
        Assert.DoesNotContain("BrakingLostRoute", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The brake rate a follower braking along a lost route uses onto the route's end: the firm rate
    /// (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>) when the end lies inside its stopping distance at the taxi rate,
    /// else none (the taxi rate). No real layout puts a lost route's end inside the stop (a KOAK or SFO follower keeps planning
    /// ahead), so the choice is pinned as the pure function the brake uses.
    /// </summary>
    [Theory]
    [InlineData(AircraftCategory.Jet, 20.0, 50.0, true)]
    [InlineData(AircraftCategory.Jet, 20.0, 80.0, false)]
    [InlineData(AircraftCategory.Jet, 5.0, 50.0, false)]
    [InlineData(AircraftCategory.Piston, 20.0, 100.0, true)]
    [InlineData(AircraftCategory.Piston, 20.0, 200.0, false)]
    [InlineData(AircraftCategory.Turboprop, 15.0, 0.0, true)]
    public void LostRouteEndDecelRate_EndInsideTheTaxiStop_IsTheFirmRate(AircraftCategory category, double speedKts, double toEndFt, bool firm)
    {
        double taxiStopFt = speedKts * speedKts / (2.0 * CategoryPerformance.TaxiDecelRate(category)) * (GeoMath.FeetPerNm / 3600.0);
        Output.WriteLine($"{category} at {speedKts} kt stops in {taxiStopFt:F0} ft at the taxi rate; route end {toEndFt} ft ahead");
        double? rate = FollowingPhase.LostRouteEndDecelRate(category, speedKts, toEndFt);
        Assert.Equal(firm ? CategoryPerformance.ExpediteExitDecelRate(category) : null, rate);
    }

    /// <summary>
    /// A crossed bar ahead of a follower braking along its lost route is one it crosses only when the route reaches it
    /// (<see cref="FollowingPhase.BarsOnRestOfRoute"/>): at SFO, a route along G toward 1L that turns onto B at the G/B junction,
    /// short of 1L's bar on G, does not cross that bar, and a route on along G to the bar does. Pinned on the routes rather than
    /// end to end: on KOAK and SFO no turn-off lies close enough to a bar for a follower's taxi-rate stop to reach the line.
    /// </summary>
    [Fact]
    public void BarsOnRestOfRoute_RouteTurningOffShortOfTheBar_DoesNotCrossIt()
    {
        if (SfoGroundHarness.Build(Output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        List<GroundNode> bars = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "1L", "G");
        GroundNode junction = layout
            .Nodes.Values.Where(n => StraightNeighboursOn(n, "G").Any() && StraightNeighboursOn(n, "B").Any())
            .MinBy(n => bars.Min(bar => GeoMath.DistanceNm(bar.Position, n.Position)))!;
        GroundNode bar = bars.MinBy(b => GeoMath.DistanceNm(b.Position, junction.Position))!;
        GroundNode gBack = StraightNeighboursOn(junction, "G").MaxBy(n => GeoMath.DistanceNm(n.Position, bar.Position))!;
        GroundNode bSide = StraightNeighboursOn(junction, "B").OrderBy(n => n.Id).First();
        WakeTurbulenceData.WakeClass wake = WakeTurbulenceData.WakeClassForType(Type, AircraftCategory.Jet);
        TaxiRoute turnOff = Assert.IsType<TaxiRoute>(TaxiPathfinder.FindRoute(layout, gBack.Id, bSide.Id, AircraftCategory.Jet, wake));
        TaxiRoute onToTheBar = Assert.IsType<TaxiRoute>(TaxiPathfinder.FindRoute(layout, gBack.Id, bar.Id, AircraftCategory.Jet, wake));
        double barFt = GeoMath.DistanceNm(junction.Position, bar.Position) * GeoMath.FeetPerNm;
        Output.WriteLine($"G/B junction #{junction.Id}, 1L bar #{bar.Id} {barFt:F0} ft on");
        Output.WriteLine($"turn-off {turnOff.ToSummary()}, on to the bar {onToTheBar.ToSummary()}");

        Assert.Empty(FollowingPhase.BarsOnRestOfRoute(turnOff, [bar]));
        Assert.Equal([bar.Id], FollowingPhase.BarsOnRestOfRoute(onToTheBar, [bar]).Select(b => b.Id));
    }
}
