using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A <c>FOLLOWG</c> follower drives the taxi graph on the real KOAK layout: it gives way short of a merge its lead has not
/// passed, joins behind a lead on the lead's own edge without giving way (turning about first when it faces away), holds in
/// position when no taxi path reaches the lead, and carries its follow route across a snapshot.
/// </summary>
public class FollowGroundOnGraphTests(ITestOutputHelper output)
{
    private const string LeadCallsign = "N1LED";
    private const string FollowerCallsign = "N2FOL";
    private const int BudgetSeconds = 240;

    /// <summary>
    /// A follower on a taxiway crossing B, short of a junction the lead has still to reach on its route, stops short of the
    /// junction clear of the lead's path, and moves on past the junction only after the lead's tail is past it.
    /// </summary>
    [Fact]
    public void Following_MergeAheadOfLead_GivesWayUntilTheLeadTailClears()
    {
        if (StartMergeAhead() is not { } run)
        {
            return;
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.True(follow.IsGivingWay, "the follower did not give way at a merge ahead of its lead");
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        int mergeIndex = follow.MergeSegmentIndex;
        Assert.True(mergeIndex > 0, "the follower's route to the merge is empty");
        List<DirectionalEdge> leadPath = [.. route.Segments.Skip(mergeIndex).Select(s => s.Edge)];
        GroundNode merge = route.Segments[mergeIndex].Edge.FromNode;

        int tailPastAt = -1;
        int followerPastAt = -1;
        bool stoppedShort = false;
        for (int second = 1; (second <= BudgetSeconds) && (followerPastAt < 0); second++)
        {
            run.Engine.TickOneSecond();
            bool tailPast =
                (FollowRoutePlanner.LocateOnPath(run.Layout, leadPath, run.Lead) is { } at)
                && FollowRoutePlanner.LeadTailPastMerge(leadPath, at, run.Lead.AircraftType);
            tailPastAt = (tailPastAt < 0) && tailPast ? second : tailPastAt;
            bool followerPast = route.CurrentSegmentIndex >= mergeIndex;
            followerPastAt = followerPast ? second : -1;
            double toMergeFt = GeoMath.DistanceNm(run.Follower.Position, merge.Position) * GeoMath.FeetPerNm;
            stoppedShort |= (tailPastAt < 0) && (run.Follower.GroundSpeed < 0.05) && (second > 5);
            output.WriteLine(
                $"t={second} follower gs={run.Follower.GroundSpeed:F1} toMerge={toMergeFt:F0} seg={route.CurrentSegmentIndex}/{route.Segments.Count} "
                    + $"givingWay={follow.IsGivingWay} lead gs={run.Lead.GroundSpeed:F1} leadTailPast={tailPast}"
            );
        }

        Assert.True(tailPastAt > 0, $"the lead's tail never passed merge node {merge.Id}");
        Assert.True(stoppedShort, "the follower never stood short of the merge while the lead's tail was still short of it");
        Assert.True(followerPastAt > 0, $"the follower never passed merge node {merge.Id} within {BudgetSeconds}s");
        Assert.True(followerPastAt > tailPastAt, $"the follower passed the merge at t={followerPastAt}, before the lead's tail at t={tailPastAt}");
    }

    /// <summary>
    /// A follower behind its lead on the lead's own B edge, facing the lead, follows along the edge without giving way and
    /// stops behind the stationary lead at the stop gap.
    /// </summary>
    [Fact]
    public void Following_BehindOnLeadsEdge_FollowsWithoutGivingWay() => FollowBehindOnLeadsEdge(followerFacesLead: true);

    /// <summary>
    /// The same follower facing away from its lead turns about through the navigator's entry alignment, then follows along the
    /// edge without giving way and stops behind the lead at the stop gap.
    /// </summary>
    [Fact]
    public void Following_BehindOnLeadsEdgeFacingAway_TurnsAboutAndFollows() => FollowBehindOnLeadsEdge(followerFacesLead: false);

    /// <summary>
    /// A follower standing mid-way along a long taxiway edge off its lead's path, facing the next node, drives on from where it stands: its
    /// follow route starts on the edge it is on, so it rolls up past a walking pace before that edge's end, rather than
    /// crawling to the edge's end at the navigator's re-acquire speed as though the stretch back to its route were a centreline
    /// it is off.
    /// </summary>
    [Fact]
    public void Following_MidEdgeShortOfItsRouteStart_DrivesOnFromWhereItStands()
    {
        // GroundNavigator's re-acquire cap, which an aircraft short of its route's first node is held to.
        const double reacquireSpeedKts = 5.0;
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        // The longest straight taxiway edge off the lead's route and trail, entered from its end farther from the lead.
        TaxiRoute leadRoute = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        HashSet<int> leadNodes =
        [
            .. leadRoute.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId }),
            .. run.Lead.Ground.TaxiEdgeTrail.Edges.SelectMany(e => new[] { e.NodeA, e.NodeB }),
        ];
        GroundEdge edge = run
            .Layout.Nodes.Values.SelectMany(n => n.Edges.OfType<GroundEdge>())
            .Where(e => !e.IsRunwayCenterline && !e.IsRamp && !string.IsNullOrEmpty(e.TaxiwayName) && e.Nodes.All(n => !leadNodes.Contains(n.Id)))
            .MaxBy(e => e.DistanceNm)!;
        (GroundNode behind, GroundNode ahead) =
            GeoMath.DistanceNm(edge.Nodes[0].Position, run.Lead.Position) > GeoMath.DistanceNm(edge.Nodes[1].Position, run.Lead.Position)
                ? (edge.Nodes[0], edge.Nodes[1])
                : (edge.Nodes[1], edge.Nodes[0]);
        output.WriteLine($"follower 0.3 along {edge.TaxiwayName} #{behind.Id}>#{ahead.Id}, {edge.DistanceNm * GeoMath.FeetPerNm:F0} ft");
        Assert.True(edge.DistanceNm * GeoMath.FeetPerNm >= 200.0, "no taxiway edge off the lead's path is long enough to roll up to speed on");
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            KoakFollowGeometry.Between(behind.Position, ahead.Position, 0.3),
            KoakFollowGeometry.Facing(behind, ahead)
        );
        follower.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(follower);
        CommandResult result = run.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {run.Lead.Callsign}");
        Assert.True(result.Success, result.Message);

        run.Engine.TickOneSecond();
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        output.WriteLine($"follow route {string.Join(" ", route.Segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}");
        Assert.Equal((behind.Id, ahead.Id), (route.Segments[0].FromNodeId, route.Segments[0].ToNodeId));

        double fastestOnTheEdgeKts = 0.0;
        for (int second = 2; (second <= 60) && (route.CurrentSegmentIndex == 0); second++)
        {
            fastestOnTheEdgeKts = Math.Max(fastestOnTheEdgeKts, follower.GroundSpeed);
            output.WriteLine($"t={second - 1} gs={follower.GroundSpeed:F1} seg={route.CurrentSegmentIndex}");
            run.Engine.TickOneSecond();
        }

        Assert.True(
            fastestOnTheEdgeKts > reacquireSpeedKts + 0.5,
            $"the follower crawled along the edge it stood on at {fastestOnTheEdgeKts:F1} kt, no faster than the re-acquire speed"
        );
    }

    /// <summary>
    /// A follow that finds no plan to join at runtime holds in position in its follow, with no follow route, rather than
    /// steering across the field at the lead. KOAK's taxi graph is connected, so no follower there lacks a taxi path to its
    /// lead; the non-joinable plan built here is the other one a follow meets at runtime and holds for the same way: the
    /// follower ahead of its lead on the lead's route, which <c>FOLLOWG</c> itself rejects, so the follow is installed directly.
    /// </summary>
    [Fact]
    public void Following_NoPathAtRuntime_HoldsInPosition()
    {
        if (StartFollowerAhead() is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        LatLon start = follower.Position;
        List<string> calls = CaptureUnableToFollowCalls(run.Engine);
        for (int second = 1; second <= 15; second++)
        {
            run.Engine.TickOneSecond();
            output.WriteLine(
                $"t={second} follower {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2} lead gs={run.Lead.GroundSpeed:F1}"
            );
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.Null(follow.FollowRoute);
        Assert.Equal(0.0, follower.GroundSpeed, 0.01);
        Assert.True(GeoMath.DistanceNm(start, follower.Position) * GeoMath.FeetPerNm < 1.0, "the follower moved with no taxi path to its lead");
        Assert.Empty(calls);
    }

    /// <summary>
    /// A follower made unjoinable by a plan ahead of its lead (<see cref="FollowRoutePlan.FollowerAhead"/>), which is not the
    /// "cannot follow at all" call, with an assigned route of its own, whose lead is then deleted: the pilot says once that it
    /// lost the traffic and is holding, and never the unable call — only a plan that joins nothing says that
    /// (<see cref="Following_NoPathAtRuntime_SaysUnableOnceAndHolds"/>).
    /// </summary>
    [Fact]
    public void Following_UnjoinableAheadOfItsLead_SaysLostTrafficWhenTheLeadIsDeleted()
    {
        if (StartFollowerAhead() is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        follower.Ground.AssignedTaxiRoute = IslandRouteAhead(run.Layout, follower);
        List<string> lost = CaptureCalls(run.Engine, FollowerCallsign, "lost sight of");
        List<string> unable = CaptureUnableToFollowCalls(run.Engine);
        for (int second = 1; second <= 5; second++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.True(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).IsUnjoinable, "the follower ahead of its lead never latched");
        run.Engine.World.RemoveAircraft(LeadCallsign);
        for (int second = 6; second <= 40; second++)
        {
            run.Engine.TickOneSecond();
            output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2}");
        }

        Assert.Equal([$"Lost sight of {LeadCallsign}, holding position, request taxi instructions"], lost);
        Assert.Empty(unable);
    }

    /// <summary>
    /// A follow whose plan at runtime finds no taxi path onto its lead's path — the follower on a part of the graph no edge
    /// joins to the lead's, installed directly as <c>FOLLOWG</c> itself rejects it — holds in position and says once that it
    /// cannot follow and wants taxi instructions: as a terminal warning naming the lead in an RPO room, and as a transmission to
    /// a solo student on ground, in the traffic's words.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Following_NoPathAtRuntime_SaysUnableOnceAndHolds(bool soloOnGround)
    {
        if (StartFollowerOnIsland(soloOnGround) is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        LatLon start = follower.Position;
        List<string> calls = CaptureUnableToFollowCalls(run.Engine);
        List<string> lost = CaptureCalls(run.Engine, FollowerCallsign, "lost sight of");
        for (int second = 1; second <= 15; second++)
        {
            run.Engine.TickOneSecond();
            output.WriteLine($"t={second} follower {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2}");
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(follow.IsUnjoinable, "the follow with no taxi path to its lead did not hold for good");
        Assert.Null(follow.FollowRoute);
        Assert.Equal(0.0, follower.GroundSpeed, 0.01);
        Assert.True(GeoMath.DistanceNm(start, follower.Position) * GeoMath.FeetPerNm < 1.0, "the follower moved with no taxi path to its lead");
        string expected = soloOnGround
            ? "Unable to follow traffic, no taxi route to its path, request taxi instructions"
            : $"Unable to follow {LeadCallsign}, no taxi route to its path, request taxi instructions";
        Assert.Equal([expected], calls);
        Assert.Empty(lost);
    }

    /// <summary>
    /// The follower's "unable to follow" calls <paramref name="engine"/> surfaces from here on, in order: warnings, and the
    /// terminal entries RPO pilot speech and solo transmissions become.
    /// </summary>
    private static List<string> CaptureUnableToFollowCalls(SimulationEngine engine) => CaptureCalls(engine, FollowerCallsign, "no taxi route");

    /// <summary>
    /// Every call <paramref name="engine"/> surfaces from here on, by <paramref name="callsign"/>, whose text contains
    /// <paramref name="phrase"/>, in order: the warnings an RPO room's terminal shows, and the SAY entries a solo student's radio
    /// transmissions become.
    /// </summary>
    private static List<string> CaptureCalls(SimulationEngine engine, string callsign, string phrase)
    {
        List<string> calls = [];
        void Note(string from, string text)
        {
            if (SameCallsign(from, callsign) && text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                calls.Add(text);
            }
        }

        engine.WarningEmitted += Note;
        engine.TerminalEntryEmitted += entry => Note(entry.Callsign, entry.Message);
        return calls;
    }

    /// <summary>
    /// The SAY entries <paramref name="engine"/> surfaces from here on, by <paramref name="callsign"/>, whose message contains
    /// <paramref name="phrase"/>: the radio transmissions a solo student's room makes of the pilot's call, on the SAY channel
    /// alone, never the orange warning channel an RPO room uses.
    /// </summary>
    private static List<string> CaptureSayCalls(SimulationEngine engine, string callsign, string phrase)
    {
        List<string> calls = [];
        engine.TerminalEntryEmitted += entry =>
        {
            if (IsSayKind(entry.Kind) && SameCallsign(entry.Callsign, callsign) && entry.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                calls.Add(entry.Message);
            }
        };
        return calls;
    }

    /// <summary>Every warning <paramref name="engine"/> surfaces from here on, by <paramref name="callsign"/>.</summary>
    private static List<string> CaptureWarnings(SimulationEngine engine, string callsign)
    {
        List<string> warnings = [];
        engine.WarningEmitted += (from, text) =>
        {
            if (SameCallsign(from, callsign))
            {
                warnings.Add(text);
            }
        };
        return warnings;
    }

    private static bool SameCallsign(string callsign, string expected) => callsign.Equals(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a terminal entry's kind is one of the SAY channel's (<see cref="SimulationEngine"/>'s pilot transmissions).</summary>
    private static bool IsSayKind(string kind) => kind is "SayPilot" or "SayReadback";

    /// <summary>
    /// A follow giving way short of its merge, snapshotted and restored through the recording JSON, comes back with the same
    /// follow route and segment, merge index, lead edge into the merge, give-way state and navigator state.
    /// </summary>
    [Fact]
    public void Following_SnapshotMidFollow_RoundTripsRouteMergeAndNavigator()
    {
        if (StartMergeAhead() is not { } run)
        {
            return;
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        run.Engine.TickOneSecond();
        run.Engine.TickOneSecond();
        FollowingPhaseDto dto = Assert.IsType<FollowingPhaseDto>(follow.ToSnapshot());
        Assert.NotNull(dto.FollowRoute);
        Assert.NotNull(dto.Navigator);
        Assert.True(dto.MergeSegmentIndex > 0, "no route to the merge to round-trip");
        Assert.NotNull(dto.LeadEdgeIntoMerge);
        Assert.True(dto.GivingWay, "the follow was not giving way when snapshotted");

        string json = JsonSerializer.Serialize<PhaseDto>(dto, RecordingJsonOptions.Default);
        FollowingPhaseDto read = Assert.IsType<FollowingPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var restored = FollowingPhase.FromSnapshot(read, run.Layout);

        Assert.Equal(json, JsonSerializer.Serialize<PhaseDto>(restored.ToSnapshot(), RecordingJsonOptions.Default));
        Assert.Equal(follow.FollowRoute!.CurrentSegmentIndex, restored.FollowRoute?.CurrentSegmentIndex);
        Assert.Equal(follow.MergeSegmentIndex, restored.MergeSegmentIndex);
        Assert.True(restored.IsGivingWay);

        // The runways a follow is exiting round-trip too: none here, so the snapshot is given one.
        JsonObject withExit = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        string exitKey = withExit
            .Select(p => p.Key)
            .Single(k => string.Equals(k, nameof(FollowingPhaseDto.ExitingRunways), StringComparison.OrdinalIgnoreCase));
        withExit[exitKey] = new JsonArray("28R");
        FollowingPhaseDto readExit = Assert.IsType<FollowingPhaseDto>(
            JsonSerializer.Deserialize<PhaseDto>(withExit.ToJsonString(), RecordingJsonOptions.Default)
        );
        FollowingPhaseDto back = Assert.IsType<FollowingPhaseDto>(FollowingPhase.FromSnapshot(readExit, run.Layout).ToSnapshot());
        Assert.True(RunwayIdentifier.Parse(Assert.Single(back.ExitingRunways)).Overlaps(RunwayIdentifier.Parse("28R")));
    }

    /// <summary>
    /// A follow held in position for good — the follower ahead of its lead on the lead's route — snapshotted and restored through
    /// the recording JSON, stays held for good: the restored follow does not plan again, so a replay holds where the live run
    /// held.
    /// </summary>
    [Fact]
    public void Following_SnapshotWhileUnjoinable_StaysHeld()
    {
        if (StartFollowerAhead() is not { } run)
        {
            return;
        }

        run.Engine.TickOneSecond();
        run.Engine.TickOneSecond();
        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.True(follow.IsUnjoinable, "the follow ahead of its lead was not held for good");

        string json = JsonSerializer.Serialize<PhaseDto>(follow.ToSnapshot(), RecordingJsonOptions.Default);
        FollowingPhaseDto read = Assert.IsType<FollowingPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var restored = FollowingPhase.FromSnapshot(read, run.Layout);

        Assert.True(restored.IsUnjoinable, "the restored follow forgot it was held for good and would plan again");
        Assert.Null(restored.FollowRoute);
        Assert.Equal(json, JsonSerializer.Serialize<PhaseDto>(restored.ToSnapshot(), RecordingJsonOptions.Default));
    }

    /// <summary>
    /// A follower on 28R's pavement at the runway's end, facing off it, with no bar ahead to clear to, has tried its clearing of
    /// 28R; snapshotted through the recording JSON and restored, the follow keeps that attempt, and the restored engine's follower
    /// holds where it stood rather than trying again.
    /// </summary>
    [Fact]
    public void Following_SnapshotAfterAFailedClearing_KeepsItsAttemptAndStaysHeld()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        RunwayInfo runway = Runway28R(layout);
        (LatLon position, TrueHeading heading) = OffTheEndPose(layout, runway);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", position, heading);
        follower.Ground.Layout = layout;
        engine.World.AddAircraft(follower);
        engine.World.AddAircraft(KoakFollowGeometry.SpawnAtStand(layout, LeadCallsign));
        CommandResult result = engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);
        engine.TickOneSecond();
        engine.TickOneSecond();

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.Contains(follow.ClearingAttemptedRunways, r => r.Overlaps(runway.Id));
        string json = JsonSerializer.Serialize<PhaseDto>(follow.ToSnapshot(), RecordingJsonOptions.Default);
        FollowingPhaseDto read = Assert.IsType<FollowingPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var restoredPhase = FollowingPhase.FromSnapshot(read, layout);
        Assert.Contains(restoredPhase.ClearingAttemptedRunways, r => r.Overlaps(runway.Id));
        Assert.Equal(json, JsonSerializer.Serialize<PhaseDto>(restoredPhase.ToSnapshot(), RecordingJsonOptions.Default));

        string engineJson = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = KoakFollowGeometry.NewEngine(output, autoCross: false)!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(engineJson, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        LatLon heldAt = restoredFollower.Position;
        for (int second = 1; second <= 5; second++)
        {
            restored.TickOneSecond();
        }

        FollowingPhase restoredFollow = Assert.IsType<FollowingPhase>(restoredFollower.Phases?.CurrentPhase);
        Assert.Null(restoredFollow.ClearingRoute);
        Assert.Equal(0.0, restoredFollower.GroundSpeed, 0.01);
        Assert.True(GeoMath.DistanceNm(heldAt, restoredFollower.Position) * GeoMath.FeetPerNm < 1.0, "the restored follower moved");
    }

    /// <summary>How long (s) a restored follow is ticked beside the run it was snapshotted from.</summary>
    private const int RestoreCompareSeconds = 40;

    /// <summary>
    /// A follow snapshotted through the recording JSON mid-segment, and again on the tick its follow route ran out and was
    /// replaced, then restored into a second engine, drives exactly as the run it was taken from: the same position, heading
    /// and ground speed every tick after. Phases tick each physics sub-tick and a snapshot is taken between seconds, so the
    /// route-drop snapshot wants a route that ran out on a second's last sub-tick: the follower's start along its edge is moved
    /// (<see cref="ChainStartFractions"/>) to find one, and failing that the snapshot is taken the second its first route was
    /// replaced.
    /// </summary>
    [Fact]
    public void Following_RestoredMidSegmentAndAfterRouteDrop_TicksIdentically()
    {
        ChainRun? reference = null;
        ChainSecond? drop = null;
        foreach (double fraction in ChainStartFractions)
        {
            if (RunRouteEnd(ChainBudgetSeconds, fraction) is not { } run)
            {
                return;
            }

            ChainSecond? emptyAtSecondEnd = run.Seconds.FirstOrDefault(s => s.RouteReplaced && !s.HasRoute);
            if ((reference is null) || (emptyAtSecondEnd is not null))
            {
                reference = run;
                drop = emptyAtSecondEnd ?? run.Seconds.FirstOrDefault(s => s.RouteReplaced);
            }

            if (emptyAtSecondEnd is not null)
            {
                break;
            }
        }

        ChainRun chain = Assert.IsType<ChainRun>(reference);
        ChainSecond dropAt = Assert.IsType<ChainSecond>(drop);
        int midAt = chain.Seconds.First(s => s.HasRoute && (s.SpeedKts > 5.0)).Second;
        output.WriteLine(
            $"follower started {chain.StartFraction:F1} along its edge: mid-segment snapshot at t={midAt}, route-drop snapshot at {dropAt}"
        );
        AssertRestoredRunMatches(midAt, chain.StartFraction);
        AssertRestoredRunMatches(dropAt.Second, chain.StartFraction);
    }

    /// <summary>
    /// A follow giving way at a merge ahead of its lead — its plan holding the lead's edge into the merge — snapshotted through
    /// the recording JSON while the follower is still rolling along its route to the merge, and restored into a second engine,
    /// drives exactly as the run it was taken from: the same position, heading, ground speed, phase and give-way state every tick
    /// after, through the stop short of the merge and the release behind the lead.
    /// </summary>
    [Fact]
    public void Following_RestoredMidRouteToMergeWhileGivingWay_TicksIdentically()
    {
        if (StartMergeAhead() is not { } original)
        {
            return;
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(original.Follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        Assert.NotNull(Assert.IsType<FollowingPhaseDto>(follow.ToSnapshot()).LeadEdgeIntoMerge);
        int snapshotAt = 1;
        while (follow.IsGivingWay && (original.Follower.GroundSpeed < 1.0) && (snapshotAt < BudgetSeconds))
        {
            original.Engine.TickOneSecond();
            snapshotAt++;
        }

        Assert.True(route.CurrentSegmentIndex < follow.MergeSegmentIndex, "the follower reached the merge before it was snapshotted");
        Assert.True(follow.IsGivingWay, $"the follower was released from its give-way at t={snapshotAt} before it rolled toward the merge");
        output.WriteLine(
            $"snapshot at t={snapshotAt}: gs={original.Follower.GroundSpeed:F1} seg={route.CurrentSegmentIndex}/{follow.MergeSegmentIndex}"
        );

        string json = JsonSerializer.Serialize(original.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = Assert.IsType<SimulationEngine>(KoakFollowGeometry.NewEngine(output, autoCross: true)?.Engine);
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        FollowingPhase restoredFollow = Assert.IsType<FollowingPhase>(restoredFollower.Phases?.CurrentPhase);
        Assert.True(restoredFollow.IsGivingWay, "the restored follow forgot it was giving way");
        Assert.NotNull(Assert.IsType<FollowingPhaseDto>(restoredFollow.ToSnapshot()).LeadEdgeIntoMerge);

        int releasedAt = -1;
        for (int second = snapshotAt + 1; (second <= snapshotAt + BudgetSeconds) && ((releasedAt < 0) || (second <= releasedAt + 10)); second++)
        {
            original.Engine.TickOneSecond();
            restored.TickOneSecond();
            AircraftState live = original.Follower;
            bool liveGivingWay = (live.Phases?.CurrentPhase as FollowingPhase)?.IsGivingWay ?? false;
            bool restoredGivingWay = (restoredFollower.Phases?.CurrentPhase as FollowingPhase)?.IsGivingWay ?? false;
            string at = $"t={second} (snapshot at t={snapshotAt})";
            Assert.True(live.Position == restoredFollower.Position, $"{at}: restored at {restoredFollower.Position}, live at {live.Position}");
            Assert.True(
                live.TrueHeading.Degrees == restoredFollower.TrueHeading.Degrees,
                $"{at}: restored heading {restoredFollower.TrueHeading.Degrees}, live {live.TrueHeading.Degrees}"
            );
            Assert.True(
                live.GroundSpeed == restoredFollower.GroundSpeed,
                $"{at}: restored at {restoredFollower.GroundSpeed} kt, live at {live.GroundSpeed} kt"
            );
            Assert.True(
                live.Phases?.CurrentPhase?.Name == restoredFollower.Phases?.CurrentPhase?.Name,
                $"{at}: restored in {restoredFollower.Phases?.CurrentPhase?.Name}, live in {live.Phases?.CurrentPhase?.Name}"
            );
            Assert.True(liveGivingWay == restoredGivingWay, $"{at}: restored giving way {restoredGivingWay}, live {liveGivingWay}");
            releasedAt = (releasedAt < 0) && !liveGivingWay ? second : releasedAt;
        }

        Assert.True(releasedAt > 0, $"the follower was never released from its give-way within {BudgetSeconds}s of the snapshot");
        output.WriteLine($"released from the give-way at t={releasedAt}; the restored run matched through t={releasedAt + 10}");
    }

    /// <summary>
    /// A follow whose route runs out where a new plan joins the lead's path again is planned again in the same tick: at no
    /// second's end is it left without a route, the tick with no route that publishes a stop.
    /// </summary>
    [Fact]
    public void Following_RouteRunsOutAndReplans_IsNeverLeftWithoutARoute()
    {
        foreach (double fraction in ChainStartFractions)
        {
            if (RunRouteEnd(ChainBudgetSeconds, fraction) is not { } run)
            {
                return;
            }

            List<ChainSecond> ends = [.. run.Seconds.Where(s => s.RouteReplaced)];
            Assert.True(ends.Count > 0, $"start {fraction:F1}: the follower's route never ran out");
            foreach (ChainSecond end in ends)
            {
                Assert.True(end.HasRoute, $"start {fraction:F1}: t={end.Second}: the follower's route ran out and it was left with none");
            }
        }
    }

    /// <summary>
    /// A follower keeps its speed across a follow route's end while the next plan joins the lead's path: it never loses
    /// more than a sub-tick of taxi braking in the second the route is replaced.
    /// </summary>
    [Fact]
    public void Following_ChainedFollowAcrossRouteEnd_KeepsSpeed()
    {
        double subTickDecelKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston) / SimulationEngine.PhysicsSubTickRate;
        foreach (double fraction in ChainStartFractions)
        {
            if (RunRouteEnd(ChainBudgetSeconds, fraction) is not { } run)
            {
                return;
            }

            foreach (ChainSecond end in run.Seconds.Where(s => s.RouteReplaced))
            {
                int second = end.Second;
                double before = run.Seconds[second - 1].SpeedKts;
                double after = run.Seconds[second].SpeedKts;
                Assert.True(
                    after >= before - subTickDecelKts - 1e-9,
                    $"t={second}: the follower slowed from {before:F2} to {after:F2} kt across the route end at t={end.Second}"
                );
            }
        }
    }

    /// <summary>How long (s) the route-end follow is played a sub-tick at a time once its lead has been sent on.</summary>
    private const int NodeArrivalSteppedSeconds = 60;

    /// <summary>How little (kt) a speed may change across a sub-tick and still count as held: physics settles onto a target to within it.</summary>
    private const double SteadyKts = 1e-3;

    /// <summary>
    /// A follower holding a steady speed on its follow route keeps it on the physics sub-tick it arrives at a node: the navigator
    /// publishes no speed on that sub-tick, and physics has cleared the target it reached, so a missing target must read as the
    /// speed held, never as a stop the follower brakes toward for a sub-tick. Played on the route-end follow
    /// (<see cref="RunRouteEnd"/>) past the second its lead is sent on, then a sub-tick at a time.
    /// </summary>
    [Fact]
    public void Following_SteadyAcrossANodeArrival_NeverBrakesForTheSubTick()
    {
        if (RunRouteEnd(30, 0.0) is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        SimulationEngine engine = run.Engine;
        double previousKts = follower.IndicatedAirspeed;
        double beforeKts = follower.IndicatedAirspeed;
        int arrivals = 0;
        for (int second = 31; second <= 30 + NodeArrivalSteppedSeconds; second++)
        {
            engine.BeginSecond();
            engine.OpenSecond(engine.BareHost);
            engine.RunPrePhysics(engine.BareHost);
            for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
            {
                var follow = follower.Phases?.CurrentPhase as FollowingPhase;
                TaxiRoute? route = follow?.FollowRoute;
                int segmentBefore = route?.CurrentSegmentIndex ?? -1;
                engine.RunPhysicsSubTick(1.0 / SimulationEngine.PhysicsSubTickRate, sub);
                double afterKts = follower.IndicatedAirspeed;
                bool arrived = (route is not null) && ReferenceEquals(route, follow?.FollowRoute) && (route.CurrentSegmentIndex > segmentBefore);
                bool steady = Math.Abs(beforeKts - previousKts) < SteadyKts;
                if (arrived && steady)
                {
                    arrivals++;
                    Assert.True(
                        afterKts >= beforeKts - SteadyKts,
                        $"t={second} sub-tick {sub}: arriving at segment {route!.CurrentSegmentIndex}, the follower braked from {beforeKts:F3} to "
                            + $"{afterKts:F3} kt"
                    );
                }

                previousKts = beforeKts;
                beforeKts = afterKts;
            }

            engine.RunPostPhysics(engine.BareHost);
            engine.RunEndOfSecond(engine.BareHost);
        }

        output.WriteLine($"{arrivals} node arrivals at a steady speed");
        Assert.True(arrivals > 0, $"the follower never arrived at a node at a steady speed in {NodeArrivalSteppedSeconds}s");
    }

    /// <summary>
    /// A follower giving way at a junction ahead of its lead on the lead's route, whose lead is then cleared on a <c>TAXI</c> that
    /// turns off its route before that junction, plans again the tick the lead's route changes: the junction is no longer its
    /// merge, and the give-way latched for it is released, so the follower joins the lead's new path rather than holding for a
    /// lead that never reaches the junction.
    /// </summary>
    [Fact]
    public void Following_GivingWay_LeadReroutedOffThePath_ReplansThatTickAndIsReleased()
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        TurnOff turnOff = FindTurnOff(run.Lead);
        AircraftState follower = SpawnFollowing(run, turnOff, run.Lead.Callsign);
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(follow.IsGivingWay, $"the follower at #{turnOff.Far.Id} was not giving way at #{turnOff.Junction.Id} ahead of its lead");
        TaxiRoute before = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        Assert.Equal(turnOff.Junction.Id, MergeNodeOf(follow));

        Reroute(run.Engine, run.Lead, turnOff);
        run.Engine.TickOneSecond();

        FollowingPhase replanned = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        TaxiRoute after = Assert.IsType<TaxiRoute>(replanned.FollowRoute);
        Assert.False(ReferenceEquals(before, after), "the follower did not plan again the tick its lead was re-routed");
        Assert.NotEqual(turnOff.Junction.Id, MergeNodeOf(replanned));
        AssertReleasedBehindTheLead(run.Engine, follower, run.Lead);
    }

    /// <summary>
    /// A follower giving way at a junction ahead of a lead that is itself following: the C560 taxis north on B and along C to J
    /// (no runway on the way), a C172 behind it on B is told <c>FOLLOWG</c> it, and a second C172 on a taxiway off C is told
    /// <c>FOLLOWG</c> the first, giving way at the junction ahead of it. When the C560 is re-routed off C before the junction, the
    /// middle aircraft plans a new follow route, and the last follower, whose lead path is that follow route, plans again the
    /// same tick: it leaves the merge its lead now never passes and is released from its give-way.
    /// </summary>
    [Fact]
    public void Following_GivingWayBehindAFollower_LeadLeavesThePlannedPath_ReleasesTheGiveWay()
    {
        if (StartTaxiingLeadAlongC() is not { } run)
        {
            return;
        }

        AircraftState middle = KoakFollowGeometry.Spawn(
            MiddleCallsign,
            "C172",
            run.Chain[2].Position,
            KoakFollowGeometry.Facing(run.Chain[2], run.Chain[3])
        );
        middle.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(middle);
        CommandResult middleFollow = run.Engine.SendCommand(MiddleCallsign, $"FOLLOWG {run.Lead.Callsign}");
        Assert.True(middleFollow.Success, middleFollow.Message);
        run.Engine.TickOneSecond();
        TaxiRoute middleBefore = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(middle.Phases?.CurrentPhase).FollowRoute);

        // The turn-off is onto D; the re-route runs on along D to J, so the middle aircraft taxis well past the merge rather than
        // stopping behind the lead short of it at the near D/G intersection.
        TurnOff found = FindTurnOff(run.Lead);
        Assert.StartsWith("TAXI B C D ", found.Command, StringComparison.Ordinal);
        TurnOff turnOff = found with { Command = "TAXI B C D J" };
        AircraftState follower = SpawnFollowing(run, turnOff, MiddleCallsign);
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(follow.IsGivingWay, $"the follower at #{turnOff.Far.Id} was not giving way at #{turnOff.Junction.Id} ahead of {MiddleCallsign}");
        int oldMerge = MergeNodeOf(follow);

        Reroute(run.Engine, run.Lead, turnOff);
        run.Engine.TickOneSecond();

        FollowingPhase middleFollowing = Assert.IsType<FollowingPhase>(middle.Phases?.CurrentPhase);
        Assert.False(
            ReferenceEquals(middleBefore, middleFollowing.FollowRoute),
            $"{MiddleCallsign} did not plan a new follow route the tick {run.Lead.Callsign} was re-routed"
        );
        FollowingPhase replanned = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.NotNull(replanned.FollowRoute);
        Assert.True(
            MergeNodeOf(replanned) != oldMerge,
            $"the follower still merges at #{oldMerge} the tick {MiddleCallsign}'s follow route stopped passing it"
        );
        AssertReleasedBehindTheLead(run.Engine, follower, middle);
    }

    /// <summary>
    /// A C560 on B north of the 28R bar at <c>Chain[3]</c>, facing away from the runway, cleared <c>TAXI B C J</c> (along C to the
    /// C/J intersection, no runway on the way) and ticked until its trail holds at least two edges.
    /// </summary>
    private KoakFollowGeometry.LeadRun? StartTaxiingLeadAlongC()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return null;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(setup.Layout);
        AircraftState lead = KoakFollowGeometry.AddTaxiing(setup, LeadCallsign, "C560", (chain[3], chain[4]), "TAXI B C J");
        TaxiRoute route = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.Empty(route.HoldShortPoints);
        for (int second = 0; (second < 120) && (lead.Ground.TaxiEdgeTrail.Edges.Count < 2); second++)
        {
            setup.Engine.TickOneSecond();
        }

        Assert.True(lead.Ground.TaxiEdgeTrail.Edges.Count >= 2, "the lead's trail never reached two edges");
        output.WriteLine($"lead route {route.ToSummary()}, segment {route.CurrentSegmentIndex}");
        return new KoakFollowGeometry.LeadRun(setup.Engine, setup.Layout, chain, lead);
    }

    /// <summary>The merge node of <paramref name="follow"/>'s route: where the lead's path from the merge starts.</summary>
    private static int MergeNodeOf(FollowingPhase follow)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        return follow.MergeSegmentIndex < route.Segments.Count ? route.Segments[follow.MergeSegmentIndex].FromNodeId : route.Segments[^1].ToNodeId;
    }

    /// <summary>
    /// A re-route for the lead of a merge-ahead follow: <see cref="Command"/> turns the lead off its route onto another taxiway at
    /// a node before <see cref="Junction"/>, a later node of its route where a taxiway off the route meets it; the follower stands
    /// at <see cref="Far"/>, that taxiway's next node, facing the junction.
    /// </summary>
    private sealed record TurnOff(string Command, GroundNode Junction, GroundNode Far);

    /// <summary>
    /// The first node of <paramref name="lead"/>'s route ahead of it where another named taxiway leaves the route, as a re-route
    /// onto that taxiway (the route's taxiways up to that node, then it), and the first later route node with a straight taxiway
    /// edge of 50 ft or more off the route to put a follower on.
    /// </summary>
    private static TurnOff FindTurnOff(AircraftState lead)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        HashSet<int> onRoute = [.. route.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId })];
        for (int i = route.CurrentSegmentIndex + 1; i < route.Segments.Count - 1; i++)
        {
            GroundNode node = route.Segments[i].Edge.FromNode;
            GroundEdge? turn = OffRouteEdges(node, onRoute)
                .FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.TaxiwayName)
                    && !string.Equals(e.TaxiwayName, route.Segments[i - 1].TaxiwayName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(e.TaxiwayName, route.Segments[i].TaxiwayName, StringComparison.OrdinalIgnoreCase)
                );
            if ((turn?.TaxiwayName is not { } turnOnto) || (NextTaxiwayAlong(turn, node, onRoute) is not { } onward))
            {
                continue;
            }

            for (int j = i + 1; j < route.Segments.Count; j++)
            {
                GroundNode junction = route.Segments[j].Edge.FromNode;
                if (
                    OffRouteEdges(junction, onRoute)
                        .FirstOrDefault(e =>
                            (e.DistanceNm * GeoMath.FeetPerNm >= 50.0) && !string.Equals(e.TaxiwayName, turnOnto, StringComparison.OrdinalIgnoreCase)
                        ) is
                    { } toFar
                )
                {
                    string taxiways = string.Join(" ", TaxiwaysBefore(route, i));
                    return new TurnOff($"TAXI {taxiways} {turnOnto} {onward}", junction, toFar.OtherNode(junction));
                }
            }
        }

        Assert.Fail($"no taxiway leaves {lead.Callsign}'s route {route.ToSummary()} before a later junction");
        return null!;
    }

    /// <summary>
    /// The first other named taxiway met walking along <paramref name="turn"/>'s taxiway away from <paramref name="from"/> and off
    /// the route, so a re-route onto it has somewhere to go on to rather than stopping at the turn; null within 40 nodes.
    /// </summary>
    private static string? NextTaxiwayAlong(GroundEdge turn, GroundNode from, HashSet<int> onRoute)
    {
        string name = turn.TaxiwayName!;
        GroundNode previous = from;
        GroundNode at = turn.OtherNode(from);
        for (int step = 0; step < 40; step++)
        {
            GroundEdge? other = OffRouteEdges(at, onRoute)
                .FirstOrDefault(e => !string.IsNullOrEmpty(e.TaxiwayName) && !e.MatchesTaxiway(name) && (e.OtherNode(at).Id != previous.Id));
            if (other is not null)
            {
                return other.TaxiwayName;
            }

            GroundEdge? next = at.Edges.OfType<GroundEdge>().FirstOrDefault(e => e.MatchesTaxiway(name) && (e.OtherNode(at).Id != previous.Id));
            if (next is null)
            {
                return null;
            }

            previous = at;
            at = next.OtherNode(at);
        }

        return null;
    }

    /// <summary>The straight taxiway edges at <paramref name="node"/> off the route (<paramref name="onRoute"/>): no runway, no ramp.</summary>
    private static IEnumerable<GroundEdge> OffRouteEdges(GroundNode node, HashSet<int> onRoute) =>
        node.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline && !e.IsRamp && !onRoute.Contains(e.OtherNode(node).Id));

    /// <summary>
    /// A C172 at <paramref name="turnOff"/>'s far node facing its junction, told to follow <paramref name="leadCallsign"/>, ticked once.
    /// </summary>
    private AircraftState SpawnFollowing(KoakFollowGeometry.LeadRun run, TurnOff turnOff, string leadCallsign)
    {
        output.WriteLine($"junction #{turnOff.Junction.Id}, follower at #{turnOff.Far.Id}, re-route '{turnOff.Command}'");
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            turnOff.Far.Position,
            KoakFollowGeometry.Facing(turnOff.Far, turnOff.Junction)
        );
        follower.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(follower);
        CommandResult result = run.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {leadCallsign}");
        Assert.True(result.Success, result.Message);
        run.Engine.TickOneSecond();
        return follower;
    }

    /// <summary>Clears <paramref name="lead"/> on <paramref name="turnOff"/>'s re-route, which must keep it off the junction.</summary>
    private void Reroute(SimulationEngine engine, AircraftState lead, TurnOff turnOff)
    {
        CommandResult result = engine.SendCommand(lead.Callsign, turnOff.Command);
        output.WriteLine(
            $"{lead.Callsign} <- '{turnOff.Command}': {result.Success} {result.Message} -> {lead.Ground.AssignedTaxiRoute?.ToSummary()}"
        );
        Assert.True(result.Success, result.Message);
        TaxiRoute rerouted = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        Assert.True(
            rerouted.Segments.All(s => (s.FromNodeId != turnOff.Junction.Id) && (s.ToNodeId != turnOff.Junction.Id)),
            $"'{turnOff.Command}' still takes {lead.Callsign} through #{turnOff.Junction.Id}"
        );
    }

    /// <summary>
    /// Ticks until <paramref name="follower"/> is released from its give-way and rolling, its follow route's merge on its lead's
    /// path; fails if it is still giving way, or stopped, after <see cref="BudgetSeconds"/>.
    /// </summary>
    private void AssertReleasedBehindTheLead(SimulationEngine engine, AircraftState follower, AircraftState lead)
    {
        int releasedAt = -1;
        for (int second = 1; (second <= BudgetSeconds) && (releasedAt < 0); second++)
        {
            engine.TickOneSecond();
            var follow = follower.Phases?.CurrentPhase as FollowingPhase;
            output.WriteLine(
                $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} givingWay={follow?.IsGivingWay} "
                    + $"seg={follow?.FollowRoute?.CurrentSegmentIndex}/{follow?.FollowRoute?.Segments.Count} lead gs={lead.GroundSpeed:F1}"
            );
            releasedAt = ((follow is { IsGivingWay: false, FollowRoute: not null }) && (follower.GroundSpeed > 1.0)) ? second : -1;
        }

        Assert.True(releasedAt > 0, $"the follower was never released from its give-way and rolling within {BudgetSeconds}s");
        AssertKeepsTheStopGap(engine, follower, lead);
    }

    /// <summary>
    /// Ticks <see cref="GapKeptSeconds"/> seconds, asserting each second that the along-path nose-to-tail gap from
    /// <paramref name="follower"/> to <paramref name="lead"/> on its follow route is at or above <see cref="FollowGap.StopGapFt"/>.
    /// </summary>
    private void AssertKeepsTheStopGap(SimulationEngine engine, AircraftState follower, AircraftState lead)
    {
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        AircraftCategory leadCategory = AircraftCategorization.Categorize(lead.AircraftType);
        AircraftCategory followerCategory = AircraftCategorization.Categorize(follower.AircraftType);
        double stopGapFt = FollowGap.StopGapFt(lead.AircraftType, leadCategory, follower.AircraftType, followerCategory);
        for (int second = 1; second <= GapKeptSeconds; second++)
        {
            engine.TickOneSecond();
            FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
            TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
            int merge = follow.MergeSegmentIndex;
            TaxiRoute toMerge = new()
            {
                Segments = [.. route.Segments.Take(merge)],
                HoldShortPoints = [],
                CurrentSegmentIndex = Math.Min(route.CurrentSegmentIndex, merge),
            };
            List<DirectionalEdge> leadPath = [.. route.Segments.Skip(merge).Select(s => s.Edge)];
            double? gapFt = FollowRoutePlanner.AlongPathGapFt(
                FollowRoutePlanner.FollowerToMergeFt(layout, toMerge, leadPath, follower),
                leadPath,
                FollowRoutePlanner.LocateOnPath(layout, leadPath, lead),
                follower.AircraftType,
                lead.AircraftType
            );
            output.WriteLine($"released +{second}s: gap {gapFt:F1} ft (stop gap {stopGapFt:F1}) gs={follower.GroundSpeed:F1}");
            Assert.True(
                (gapFt is { } gap) && (gap >= stopGapFt),
                $"released +{second}s: along-path gap {gapFt:F1} ft, under the stop gap {stopGapFt:F1} ft"
            );
        }
    }

    private const int GapKeptSeconds = 5;

    /// <summary>
    /// A follower giving way on a taxiway short of a junction ahead of its lead, on the edge into the junction, whose lead is then
    /// re-routed to end at that junction: its new plan joins the lead's path where it stands, with nothing to drive. The follower
    /// does not throw installing an empty route: it brakes to rest and holds, planning again (not held for good), as a follow
    /// whose first plan is empty does.
    /// </summary>
    [Fact]
    public void Following_LeadReroutedToEndWhereTheFollowerStands_HoldsAndPlansAgain()
    {
        if (StartConverging(atJunction: true) is not { } run)
        {
            return;
        }

        double toJunctionFt = GeoMath.DistanceNm(run.Follower.Position, run.TurnOff.Junction.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"follower {toJunctionFt:F1} ft from the junction, trail {run.Follower.Ground.TaxiEdgeTrail.Newest}");
        Assert.True(toJunctionFt <= AirportGroundLayout.AtNodeToleranceFt, $"the follower stands {toJunctionFt:F1} ft from the junction");
        EndLeadAtJunction(run);
        for (int second = 1; second <= HeldCheckSeconds; second++)
        {
            run.Run.Engine.TickOneSecond();
            var held = run.Follower.Phases?.CurrentPhase as FollowingPhase;
            output.WriteLine(
                $"t={second} {run.Follower.Phases?.CurrentPhase?.Name} gs={run.Follower.GroundSpeed:F1} route={held?.FollowRoute?.ToSummary()} "
                    + $"seg={held?.FollowRoute?.CurrentSegmentIndex} merge={held?.MergeSegmentIndex} givingWay={held?.IsGivingWay}"
            );
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.False(follow.IsUnjoinable, "a lead path ending where the follower stands held the follow for good");
        Assert.False(follow.IsBrakingLostRoute, "the follower is still braking along its lost route");
        Assert.Null(follow.FollowRoute);
        Assert.True(run.Follower.GroundSpeed < 0.05, $"the follower is still rolling at {run.Follower.GroundSpeed:F1} kt");
    }

    /// <summary>
    /// A follower whose plan merges where its lead's re-routed path ends — both converging on the junction, the follower by its own
    /// route along the taxiway off the lead's route — has no lead path from the merge; the lead's route still matches the plan, so
    /// the follower plans again only on its node arrivals, never on every tick while it waits.
    /// </summary>
    [Fact]
    public void Following_MergeAtTheEndOfTheLeadsRoute_PlansOnlyOnNodeArrivals()
    {
        if (StartConverging(atJunction: false) is not { } run)
        {
            return;
        }

        EndLeadAtJunction(run);
        run.Run.Engine.TickOneSecond();
        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        Assert.Equal(run.TurnOff.Junction.Id, MergeNodeOf(follow));
        Assert.Equal(route.Segments.Count, follow.MergeSegmentIndex);

        int plansBefore = follow.PlanCount;
        int arrivals = 0;
        int segment = route.CurrentSegmentIndex;
        for (int second = 1; (second <= HeldCheckSeconds) && ReferenceEquals(follow.FollowRoute, route); second++)
        {
            run.Run.Engine.TickOneSecond();
            arrivals += route.CurrentSegmentIndex != segment ? 1 : 0;
            segment = route.CurrentSegmentIndex;
            output.WriteLine($"t={second} plans={follow.PlanCount - plansBefore} arrivals={arrivals} gs={run.Follower.GroundSpeed:F1}");
        }

        Assert.True(
            follow.PlanCount - plansBefore <= arrivals,
            $"the follower planned {follow.PlanCount - plansBefore} times over {arrivals} node arrivals"
        );
    }

    private const int HeldCheckSeconds = 30;

    /// <summary>How far (ft) from the junction a follower <see cref="StartConverging"/> puts at the junction stands.</summary>
    private const double AtJunctionFt = 8.0;

    /// <summary>
    /// The lead and the follower of <see cref="StartConverging"/>, the junction they converge on, and <see cref="EndAtJunction"/>,
    /// the lead's re-route along its route to the junction and onto the follower's taxiway, which ends at the junction.
    /// </summary>
    private sealed record Converging(KoakFollowGeometry.LeadRun Run, TurnOff TurnOff, AircraftState Follower, string EndAtJunction);

    /// <summary>
    /// The lead taxiing B (<see cref="KoakFollowGeometry.StartTaxiingLead"/>), and a C172 on the taxiway that meets the lead's route
    /// at the junction (<see cref="FindConvergingJunction"/>), facing the junction, told <c>FOLLOWG</c> the lead and ticked once:
    /// two nodes back and giving way there, or with <paramref name="atJunction"/> <see cref="AtJunctionFt"/> from the junction,
    /// within <see cref="AirportGroundLayout.AtNodeToleranceFt"/> of it.
    /// </summary>
    private Converging? StartConverging(bool atJunction)
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return null;
        }

        (TurnOff turnOff, GroundNode behind, string endAtJunction) = FindConvergingJunction(run);
        output.WriteLine($"junction #{turnOff.Junction.Id}, far #{turnOff.Far.Id}, follower at #{behind.Id}");
        double edgeFt = KoakFollowGeometry.EdgeBetween(turnOff.Junction, turnOff.Far).DistanceNm * GeoMath.FeetPerNm;
        AircraftState follower = atJunction
            ? KoakFollowGeometry.Spawn(
                FollowerCallsign,
                "C172",
                KoakFollowGeometry.Between(turnOff.Junction.Position, turnOff.Far.Position, AtJunctionFt / edgeFt),
                KoakFollowGeometry.Facing(turnOff.Far, turnOff.Junction)
            )
            : KoakFollowGeometry.Spawn(FollowerCallsign, "C172", behind.Position, KoakFollowGeometry.Facing(behind, turnOff.Far));
        follower.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(follower);
        CommandResult result = run.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {run.Lead.Callsign}");
        Assert.True(result.Success, result.Message);
        run.Engine.TickOneSecond();
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(atJunction || follow.IsGivingWay, "the follower is not giving way at the junction");
        return new Converging(run, turnOff, follower, endAtJunction);
    }

    /// <summary>The clearance <see cref="KoakFollowGeometry.StartTaxiingLead"/> gives its lead.</summary>
    private const string KoakLeadTaxi = "TAXI B W 30";

    /// <summary>
    /// A junction where the lead's route meets a taxiway off it, found by clearing the lead along its route onto each taxiway that
    /// leaves the route two segments or more ahead (a <c>TAXI</c> with no onward direction ends where its route meets the last
    /// taxiway), noting where that route ends, and clearing it back onto its own route: the first such end where a straight edge
    /// of that taxiway, 50 ft or more, leaves the route and runs on past its far node to another named taxiway
    /// (<see cref="ConvergingOff"/>). That junction and far node, the node behind the far one, and the re-route that ends at the
    /// junction.
    /// </summary>
    private static (TurnOff TurnOff, GroundNode Behind, string EndAtJunction) FindConvergingJunction(KoakFollowGeometry.LeadRun run)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        HashSet<int> onRoute = [.. route.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId })];
        HashSet<string> tried = [];
        for (int i = route.CurrentSegmentIndex + 2; i < route.Segments.Count; i++)
        {
            List<string> names =
            [
                .. OffRouteEdges(route.Segments[i].Edge.FromNode, onRoute)
                    .Select(e => e.TaxiwayName ?? "")
                    .Where(n => (n.Length > 0) && tried.Add(n)),
            ];
            foreach (string name in names)
            {
                string taxi = $"TAXI {string.Join(" ", TaxiwaysBefore(route, i))} {name}";
                bool cleared = run.Engine.SendCommand(run.Lead.Callsign, taxi).Success;
                int end = run.Lead.Ground.AssignedTaxiRoute?.Segments[^1].ToNodeId ?? -1;
                Assert.True(run.Engine.SendCommand(run.Lead.Callsign, KoakLeadTaxi).Success, "the lead was not cleared back onto its route");
                if (cleared && run.Layout.Nodes.TryGetValue(end, out GroundNode? junction) && (ConvergingOff(junction, name, onRoute) is { } found))
                {
                    return (new TurnOff("", junction, found.Far), found.Behind, taxi);
                }
            }
        }

        Assert.Fail($"no taxiway leaves the route {route.ToSummary()} and runs on to another");
        return default;
    }

    /// <summary>
    /// A straight edge of taxiway <paramref name="name"/>, 50 ft or more, leaving the route at <paramref name="junction"/>, whose far
    /// node has another edge of that taxiway off the route and from which the taxiway runs on to another named one
    /// (<see cref="NextTaxiwayAlong"/>): the far node and the node behind it. Null with none.
    /// </summary>
    private static (GroundNode Far, GroundNode Behind)? ConvergingOff(GroundNode junction, string name, HashSet<int> onRoute)
    {
        foreach (GroundEdge off in OffRouteEdges(junction, onRoute).Where(e => e.MatchesTaxiway(name) && (e.DistanceNm * GeoMath.FeetPerNm >= 50.0)))
        {
            GroundNode far = off.OtherNode(junction);
            GroundEdge? back = OffRouteEdges(far, onRoute).FirstOrDefault(e => e.MatchesTaxiway(name) && (e.OtherNode(far).Id != junction.Id));
            if ((back is not null) && (NextTaxiwayAlong(off, junction, onRoute) is not null))
            {
                return (far, back.OtherNode(far));
            }
        }

        return null;
    }

    /// <summary>
    /// The taxiways of <paramref name="route"/> from its current segment up to segment <paramref name="index"/>, each once in turn.
    /// </summary>
    private static List<string> TaxiwaysBefore(TaxiRoute route, int index)
    {
        List<string> taxiways = [];
        foreach (TaxiRouteSegment segment in route.Segments.Skip(route.CurrentSegmentIndex).Take(index - route.CurrentSegmentIndex))
        {
            if (!string.IsNullOrEmpty(segment.TaxiwayName) && ((taxiways.Count == 0) || (taxiways[^1] != segment.TaxiwayName)))
            {
                taxiways.Add(segment.TaxiwayName);
            }
        }

        return taxiways;
    }

    /// <summary>Clears the lead on <see cref="Converging.EndAtJunction"/>, which must end its route at the junction.</summary>
    private void EndLeadAtJunction(Converging run)
    {
        TaxiRoute route = Reroute(run, run.EndAtJunction);
        Assert.Equal(run.TurnOff.Junction.Id, route.Segments[^1].ToNodeId);
    }

    /// <summary>Clears the lead of <paramref name="run"/> on <paramref name="taxi"/>; the route it was given.</summary>
    private TaxiRoute Reroute(Converging run, string taxi)
    {
        CommandResult result = run.Run.Engine.SendCommand(run.Run.Lead.Callsign, taxi);
        output.WriteLine(
            $"{run.Run.Lead.Callsign} <- '{taxi}': {result.Success} {result.Message} -> {run.Run.Lead.Ground.AssignedTaxiRoute?.ToSummary()}"
        );
        Assert.True(result.Success, result.Message);
        return Assert.IsType<TaxiRoute>(run.Run.Lead.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A follower waiting for its lead to taxi, with no follow route yet, still carries the taxi route it was on before the
    /// <c>FOLLOWG</c>; the ground conflict detector does not read it as driving that route, so it shares no upcoming node with
    /// an aircraft on the same route.
    /// </summary>
    [Fact]
    public void Following_WaitingWithNoFollowRoute_IsNotDrivingItsOldAssignedRoute()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: true) is not { } setup)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(setup.Layout);
        AircraftState follower = KoakFollowGeometry.AddTaxiing(setup, FollowerCallsign, "C172", (chain[5], chain[4]), "TAXI B W 30");
        AircraftState lead = KoakFollowGeometry.SpawnAtStand(setup.Layout, LeadCallsign);
        setup.Engine.World.AddAircraft(lead);
        CommandResult result = setup.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);
        setup.Engine.TickOneSecond();

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.Null(follow.FollowRoute);
        TaxiRoute oldRoute = Assert.IsType<TaxiRoute>(follower.Ground.AssignedTaxiRoute);

        // Oncoming traffic up B: the first segments of the follower's old route, reversed, which meet it at a node ahead.
        List<TaxiRouteSegment> oncoming =
        [
            .. oldRoute
                .Segments.Skip(oldRoute.CurrentSegmentIndex)
                .Take(3)
                .Reverse()
                .Select(s => new TaxiRouteSegment { Edge = s.Edge.Edge.Directed(s.Edge.ToNode, s.Edge.FromNode), TaxiwayName = s.TaxiwayName }),
        ];
        AircraftState other = KoakFollowGeometry.Spawn(
            "N3OTH",
            "C172",
            oncoming[0].Edge.FromNode.Position,
            KoakFollowGeometry.Facing(oncoming[0].Edge.FromNode, oncoming[0].Edge.ToNode)
        );
        other.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = oncoming, HoldShortPoints = [] };
        Assert.NotNull(GroundConflictDetector.FindSharedUpcomingNode(oldRoute, other.Ground.AssignedTaxiRoute));
        Assert.Null(FollowingPhase.DrivenRouteOf(follower));
        Assert.False(GroundConflictDetector.ShareUpcomingNode(follower, other), "the detector read the waiting follower as driving its old route");
    }

    /// <summary>
    /// A follower crossing 28R behind its lead on the lead's own edge, whose lead then leaves the taxi graph, drives its follow
    /// route to its end at the far 28R bar node and finds no new plan with its tail still inside the hold line: it drives on
    /// past the bar until its tail is past the hold line, then holds in position there.
    /// </summary>
    [Fact]
    public void Following_RouteRunsOutInsideTheHoldLineWithTheLeadUnplannable_ClearsPastTheBarThenHolds()
    {
        if (RunRouteOutInsideHoldLine(ClearingBudgetSeconds, deleteLead: false, leadAheadAfter: null) is not { } run)
        {
            return;
        }

        Assert.True(run.Seconds.Any(s => s.Clearing), "the follower never drove a clearing route off 28R");
        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.Null(follow.ClearingRoute);
        Assert.Null(follow.FollowRoute);
        Assert.True(
            run.Seconds.TakeLast(5).All(s => s.SpeedKts < 0.05),
            "the follower was not holding at the end: last speeds "
                + string.Join(", ", run.Seconds.TakeLast(5).Select(s => s.SpeedKts.ToString("F2")))
        );
        (double tailFt, double barFt, bool sameSide) = TailAgainstBar(run);
        Assert.True(sameSide && (tailFt > barFt), $"the follower held with its tail {tailFt:F0} ft from 28R's centreline, the bar is {barFt:F0} ft");
    }

    /// <summary>
    /// A follower on 28R's pavement at the runway's end, facing off it, whose lead is parked at a stand: with no 28R hold-short
    /// bar ahead within 90° of its heading it logs a warning naming itself and the runway, and holds in position.
    /// </summary>
    [Fact]
    public void Following_InsideTheHoldLineWithNoBarAhead_WarnsAndHolds()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        var tap = new CapturingSimLogProvider(LogLevel.Warning, capacity: 200);
        SimLogBuilder.CreateForTest(output).EnableCategory("FollowingPhase", LogLevel.Warning).CaptureInto(tap).InitializeSimLog();
        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        RunwayInfo runway = Runway28R(layout);
        (LatLon position, TrueHeading heading) = OffTheEndPose(layout, runway);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", position, heading);
        follower.Ground.Layout = layout;
        engine.World.AddAircraft(follower);
        engine.World.AddAircraft(KoakFollowGeometry.SpawnAtStand(layout, LeadCallsign));
        Assert.True(RunwayOccupancy.IsOnPavement(follower, runway), "the follower does not start on 28R's pavement");
        CommandResult result = engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);

        for (int second = 1; second <= 10; second++)
        {
            engine.TickOneSecond();
            output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2}");
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.Null(follow.ClearingRoute);
        Assert.Equal(0.0, follower.GroundSpeed, 0.01);
        Assert.True(GeoMath.DistanceNm(position, follower.Position) * GeoMath.FeetPerNm < 1.0, "the follower moved with no bar ahead to clear to");
        Assert.Contains(
            tap.Drain(),
            r =>
                (r.Level == LogLevel.Warning)
                && r.Message.Contains(FollowerCallsign, StringComparison.Ordinal)
                && r.Message.Contains(runway.Id.ToString(), StringComparison.Ordinal)
                && r.Message.Contains("no hold-short bar of it ahead", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// The clearing follower of <see cref="Following_RouteRunsOutInsideTheHoldLineWithTheLeadUnplannable_ClearsPastTheBarThenHolds"/>,
    /// snapshotted through the recording JSON while it drives its clearing route and restored into a second engine, drives
    /// exactly as the run it was taken from: the same position, heading and ground speed every tick after.
    /// </summary>
    [Fact]
    public void Following_RestoredMidClearing_TicksIdentically()
    {
        if (RunRouteOutInsideHoldLine(ClearingBudgetSeconds, deleteLead: false, leadAheadAfter: null) is not { } reference)
        {
            return;
        }

        int snapshotAt = reference.Seconds.First(s => s.Clearing && (s.SpeedKts > 1.0)).Second;
        ClearingRun original = Assert.IsType<ClearingRun>(RunRouteOutInsideHoldLine(snapshotAt, deleteLead: false, leadAheadAfter: null));
        FollowingPhase follow = Assert.IsType<FollowingPhase>(original.Follower.Phases?.CurrentPhase);
        Assert.NotNull(follow.ClearingRoute);
        string json = JsonSerializer.Serialize(original.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = KoakFollowGeometry.NewEngine(output, autoCross: false)!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        Assert.NotNull(Assert.IsType<FollowingPhase>(restoredFollower.Phases?.CurrentPhase).ClearingRoute);

        for (int second = snapshotAt + 1; second <= snapshotAt + RestoreCompareSeconds; second++)
        {
            original.Engine.TickOneSecond();
            restored.TickOneSecond();
            AircraftState live = original.Follower;
            string at = $"t={second} (snapshot at t={snapshotAt})";
            Assert.True(live.Position == restoredFollower.Position, $"{at}: restored at {restoredFollower.Position}, live at {live.Position}");
            Assert.True(
                live.TrueHeading.Degrees == restoredFollower.TrueHeading.Degrees,
                $"{at}: restored heading {restoredFollower.TrueHeading.Degrees}, live {live.TrueHeading.Degrees}"
            );
            Assert.True(
                live.GroundSpeed == restoredFollower.GroundSpeed,
                $"{at}: restored ground speed {restoredFollower.GroundSpeed}, live {live.GroundSpeed}"
            );
        }
    }

    /// <summary>
    /// The clearing follower of <see cref="Following_RouteRunsOutInsideTheHoldLineWithTheLeadUnplannable_ClearsPastTheBarThenHolds"/>,
    /// snapshotted through the recording JSON once its tail and wingtips are past the far bar's hold line and it is braking along
    /// its clearing route, and restored into a second engine, brakes exactly as the run it was taken from: the same position,
    /// heading and ground speed every second until both are at rest, at the same pose. Whether it is in that braking roll is read
    /// again from where it stands, so the snapshot need not carry it.
    /// </summary>
    [Fact]
    public void Following_RestoredMidBrakingRoll_TicksIdenticallyToRest()
    {
        if (RunRouteOutInsideHoldLine(ClearingBudgetSeconds, deleteLead: false, leadAheadAfter: null) is not { } reference)
        {
            return;
        }

        ClearingSecond? rolling = reference.Seconds.FirstOrDefault(s => s.Clearing && s.PastBar && (s.SpeedKts > 1.0));
        Assert.True(rolling is not null, "the follower was never past the far bar's hold line still rolling on its clearing route");
        int snapshotAt = rolling.Second;
        ClearingSecond next = reference.Seconds.Single(s => s.Second == snapshotAt + 1);
        Assert.True(
            next.SpeedKts < rolling.SpeedKts,
            $"t={snapshotAt}: the follower was not braking past the bar ({rolling.SpeedKts:F2} kt, then {next.SpeedKts:F2} kt)"
        );

        ClearingRun original = Assert.IsType<ClearingRun>(RunRouteOutInsideHoldLine(snapshotAt, deleteLead: false, leadAheadAfter: null));
        Assert.NotNull(Assert.IsType<FollowingPhase>(original.Follower.Phases?.CurrentPhase).ClearingRoute);
        string json = JsonSerializer.Serialize(original.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = KoakFollowGeometry.NewEngine(output, autoCross: false)!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        Assert.NotNull(Assert.IsType<FollowingPhase>(restoredFollower.Phases?.CurrentPhase).ClearingRoute);

        AircraftState live = original.Follower;
        int second = snapshotAt;
        while ((live.GroundSpeed >= 0.05) || (restoredFollower.GroundSpeed >= 0.05))
        {
            Assert.True(
                second < snapshotAt + RestoreCompareSeconds,
                $"the followers were still rolling {RestoreCompareSeconds} s after the snapshot"
            );
            original.Engine.TickOneSecond();
            restored.TickOneSecond();
            second++;
            string at = $"t={second} (snapshot at t={snapshotAt})";
            output.WriteLine($"{at}: live gs={live.GroundSpeed:F2}, restored gs={restoredFollower.GroundSpeed:F2}");
            Assert.True(live.Position == restoredFollower.Position, $"{at}: restored at {restoredFollower.Position}, live at {live.Position}");
            Assert.True(
                live.TrueHeading.Degrees == restoredFollower.TrueHeading.Degrees,
                $"{at}: restored heading {restoredFollower.TrueHeading.Degrees}, live {live.TrueHeading.Degrees}"
            );
            Assert.True(
                live.GroundSpeed == restoredFollower.GroundSpeed,
                $"{at}: restored ground speed {restoredFollower.GroundSpeed}, live {live.GroundSpeed}"
            );
        }

        Assert.True(second > snapshotAt, "the follower was already at rest when snapshotted");
        Assert.True(
            (live.Position == restoredFollower.Position) && (live.TrueHeading.Degrees == restoredFollower.TrueHeading.Degrees),
            $"the followers came to rest apart: restored at {restoredFollower.Position} heading {restoredFollower.TrueHeading.Degrees}, "
                + $"live at {live.Position} heading {live.TrueHeading.Degrees}"
        );
    }

    /// <summary>
    /// The clearing follower of <see cref="Following_RouteRunsOutInsideTheHoldLineWithTheLeadUnplannable_ClearsPastTheBarThenHolds"/>,
    /// whose lead comes back onto the field ahead of it while it brakes along its clearing route past the far bar's hold line,
    /// takes up following again during that braking roll: it drives a follow route while still rolling, never stopping first,
    /// and never slows faster than the taxi brake rate on the way.
    /// </summary>
    [Fact]
    public void Following_LeadPlannableDuringTheBrakingRoll_ResumesWithoutStopping()
    {
        if (BrakingRollSecond() is not { } rollingAt)
        {
            return;
        }

        ClearingRun run = Assert.IsType<ClearingRun>(RunRouteOutInsideHoldLine(rollingAt, deleteLead: false, leadAheadAfter: rollingAt));
        AircraftState follower = run.Follower;
        double rateKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston);
        double lastKts = follower.GroundSpeed;
        int followingAt = -1;
        for (int second = rollingAt + 1; (second <= rollingAt + RestoreCompareSeconds) && (followingAt < 0); second++)
        {
            run.Engine.TickOneSecond();
            var follow = follower.Phases?.CurrentPhase as FollowingPhase;
            output.WriteLine(
                $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2} follow={follow?.FollowRoute is not null} "
                    + $"clearing={follow?.ClearingRoute is not null}"
            );
            Assert.True(follower.GroundSpeed >= 0.05, $"t={second}: the follower came to rest before it took up following again");
            Assert.True(
                lastKts - follower.GroundSpeed <= rateKts + 0.05,
                $"t={second}: ground speed fell from {lastKts:F2} to {follower.GroundSpeed:F2} kt, faster than the {rateKts:F1} kt/s taxi rate"
            );
            lastKts = follower.GroundSpeed;
            followingAt = follow?.FollowRoute is not null ? second : -1;
        }

        Assert.True(followingAt > 0, $"the follower never took up following again within {RestoreCompareSeconds}s of its lead coming back");
        Assert.True(
            followingAt == rollingAt + 1,
            $"the follower braked on along its clearing route until t={followingAt} before it took up following again; its lead came back "
                + $"after t={rollingAt}"
        );
    }

    /// <summary>
    /// The resuming follower of <see cref="Following_LeadPlannableDuringTheBrakingRoll_ResumesWithoutStopping"/>, snapshotted through
    /// the recording JSON in its braking roll the moment its lead comes back, and restored into a second engine, drives exactly as
    /// the run it was taken from: whether it is braking past the bar is read again from where it stands.
    /// </summary>
    [Fact]
    public void Following_RestoredInTheBrakingRollWithTheLeadBack_TicksIdentically()
    {
        if (BrakingRollSecond() is not { } rollingAt)
        {
            return;
        }

        ClearingRun original = Assert.IsType<ClearingRun>(RunRouteOutInsideHoldLine(rollingAt, deleteLead: false, leadAheadAfter: rollingAt));
        string json = JsonSerializer.Serialize(original.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = KoakFollowGeometry.NewEngine(output, autoCross: false)!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        bool followed = false;
        for (int second = rollingAt + 1; second <= rollingAt + RestoreCompareSeconds; second++)
        {
            original.Engine.TickOneSecond();
            restored.TickOneSecond();
            AircraftState live = original.Follower;
            string at = $"t={second} (snapshot at t={rollingAt})";
            Assert.True(live.Position == restoredFollower.Position, $"{at}: restored at {restoredFollower.Position}, live at {live.Position}");
            Assert.True(
                live.TrueHeading.Degrees == restoredFollower.TrueHeading.Degrees,
                $"{at}: restored heading {restoredFollower.TrueHeading.Degrees}, live {live.TrueHeading.Degrees}"
            );
            Assert.True(
                live.GroundSpeed == restoredFollower.GroundSpeed,
                $"{at}: restored {restoredFollower.GroundSpeed} kt, live {live.GroundSpeed} kt"
            );
            followed |= live.Phases?.CurrentPhase is FollowingPhase { FollowRoute: not null };
        }

        Assert.True(followed, "the follower never took up following again after its lead came back");
    }

    /// <summary>
    /// The first second of <see cref="RunRouteOutInsideHoldLine"/>'s clearing follow, with the lead off the field, at which the
    /// follower is past the far bar's hold line still rolling on its clearing route; null when the layout is unavailable.
    /// </summary>
    private int? BrakingRollSecond()
    {
        if (RunRouteOutInsideHoldLine(ClearingBudgetSeconds, deleteLead: false, leadAheadAfter: null) is not { } reference)
        {
            return null;
        }

        ClearingSecond? rolling = reference.Seconds.FirstOrDefault(s => s.Clearing && s.PastBar && (s.SpeedKts > 1.0));
        Assert.True(rolling is not null, "the follower was never past the far bar's hold line still rolling on its clearing route");
        output.WriteLine($"braking roll from t={rolling.Second} at {rolling.SpeedKts:F2} kt");
        return rolling.Second;
    }

    /// <summary>
    /// A clearing bar the layout cannot resolve — no such node, or a node that is no runway's hold-short bar — is one the follower
    /// is not known to be past, so it is not past it; a resolvable bar well behind the follower still is.
    /// </summary>
    [Fact]
    public void IsPastClearingBar_BarTheLayoutCannotResolve_IsNotPast()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        AirportGroundLayout layout = setup.Layout;
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        AircraftState aircraft = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", chain[5].Position, KoakFollowGeometry.Facing(chain[5], chain[6]));
        int missing = layout.Nodes.Keys.Max() + 1;
        Assert.False(FollowingPhase.IsPastClearingBar(layout, missing, aircraft), $"past bar node #{missing}, which the layout does not have");
        Assert.False(FollowingPhase.IsPastClearingBar(layout, chain[5].Id, aircraft), $"past B node #{chain[5].Id}, which is no runway bar");
        Assert.True(FollowingPhase.IsPastClearingBar(layout, chain[0].Id, aircraft), "not past the 28R bar from well up B");
    }

    /// <summary>
    /// A long follower (and a B738 control) clearing a runway on its clearing route after its lead is deleted mid-crossing — at
    /// OAK across 28R on B, at SFO across 01R/19L at the crossing whose far bar's clearing route was the shortest for a B744 —
    /// gets its tail and wingtips past the far bar's hold line and brakes to rest on that route, then holds in position, its nose
    /// never inside another runway's hold line. The SFO B744 is the known short case: 01L/19R's bar lies less than its length
    /// past 01R/19L's, so it holds short of 01L/19R with its tail still inside 01R/19L's hold line, warning once — and the pilot
    /// says once, on that transition into the hold, that it is holding short of 01L/19R and not clear of 01R/19L, and never the
    /// lost-traffic call the run-out hold would otherwise say (the runway call supersedes it). The other three cases clear the
    /// runway on their routes, so they hold with nothing left to drive; each follower is given an assigned route of its own
    /// before the run, which its clearing has already been tried for, so it cannot reach that either and the pilot says once that
    /// it lost the traffic — never the unable call, which is for a plan that joins nothing.
    /// </summary>
    [Theory]
    [InlineData("OAK", "B744")]
    [InlineData("OAK", "B738")]
    [InlineData("SFO", "B744")]
    [InlineData("SFO", "B738")]
    public void Following_LongAircraftOnClearingRoute_TailClearsAndBrakesWithinTheRoute(string airportId, string type)
    {
        if (NewEngineAt(airportId) is not { } probe)
        {
            return;
        }

        ClearingCase crossing = LongClearingCase(airportId, probe.Layout, type, withAssignedRoute: true);
        ClearingRun reference = RunClearingAcross(probe, crossing, ClearingBudgetSeconds, deleteLead: true, leadAheadAfter: null);
        int clearingAt = reference.Seconds.First(s => s.Clearing).Second;
        (SimulationEngine Engine, AirportGroundLayout Layout) setup = NewEngineAt(airportId)!.Value;
        var tap = new CapturingSimLogProvider(LogLevel.Warning, capacity: 200);
        SimLogBuilder.CreateForTest(output).EnableCategory("FollowingPhase", LogLevel.Warning).CaptureInto(tap).InitializeSimLog();
        List<string> calls = CaptureCalls(setup.Engine, FollowerCallsign, "holding short of runway");
        List<string> lostTraffic = CaptureCalls(setup.Engine, FollowerCallsign, "lost sight of");
        List<RunwayInfo> others = [.. RunwayOccupancy.AirportRunways(setup.Layout.AirportId).Where(r => !r.Id.Overlaps(crossing.Runway.Id))];
        ClearingRun run = RunClearingAcross(setup, crossing, clearingAt, deleteLead: true, leadAheadAfter: null);
        TaxiRoute clearing = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase).ClearingRoute);
        int barAt = clearing.Segments.FindIndex(s => s.ToNodeId == crossing.FarBar.Id);
        double pastBarFt = clearing.Segments.Skip(barAt + 1).Sum(s => s.Edge.DistanceNm * GeoMath.FeetPerNm);
        GroundNode end = clearing.Segments[^1].Edge.ToNode;
        for (int second = clearingAt + 1; second <= ClearingBudgetSeconds; second++)
        {
            run.Engine.TickOneSecond();
            LatLon nose = GeoMath.ProjectPoint(
                run.Follower.Position,
                run.Follower.TrueHeading,
                AircraftLength.ResolveFt(type) / 2.0 / GeoMath.FeetPerNm
            );
            RunwayInfo? entered = others.FirstOrDefault(r => FollowingPhase.IsInsideHoldLine(setup.Layout, r, nose));
            Assert.True(entered is null, $"t={second}: the {type}'s nose went inside runway {entered?.Id}'s hold line");
        }

        int ranOut = tap.Drain().Count(r => r.Message.Contains("clearing route ran out", StringComparison.Ordinal));
        (double tailFt, double barFt, bool sameSide) = TailAgainstBar(run);
        double restToEndFt = GeoMath.DistanceNm(run.Follower.Position, end.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"{airportId} {type} ({AircraftLength.ResolveFt(type):F0} ft) across {crossing.Runway.Id} #{crossing.NearBar.Id}>#{crossing.FarBar.Id}: "
                + $"clearing route {clearing.ToSummary()}, {pastBarFt:F0} ft past the bar node (bar at segment {barAt}); at rest tail "
                + $"{tailFt - barFt:F0} ft past the hold line, centre {restToEndFt:F0} ft from the route's end node #{end.Id}"
        );
        Assert.True(barAt >= 0, $"the clearing route does not run through the far bar #{crossing.FarBar.Id}");
        Assert.IsType<HoldingInPositionPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.True(run.Follower.GroundSpeed < 0.05, $"the follower holds rolling at {run.Follower.GroundSpeed:F2} kt");
        bool clear = sameSide && FollowingPhase.IsClearPastBar(run.Follower, crossing.Runway, crossing.FarBar);
        if ((airportId == "SFO") && (type == "B744"))
        {
            // Known short: 01L/19R's bar is less than a B744's length past 01R/19L's, so the clearing route ends short of it
            // and the B744 holds there, its tail still inside 01R/19L's hold line, warning once that it needs to cross 01L/19R.
            Assert.False(clear, $"the B744 cleared 01R/19L between the close parallels, tail {tailFt - barFt:F0} ft past the hold line");
            Assert.Equal(1, ranOut);
            string call = Assert.Single(calls);
            output.WriteLine($"SFO B744 run-out call: {call}");
            string next = RunwayCrossingEnd.Nearest(run.Follower, "01L/19R", setup.Layout);
            string cleared = RunwayCrossingEnd.Nearest(run.Follower, "01R/19L", setup.Layout);
            Assert.Equal($"Holding short of runway {next}, not clear of runway {cleared}", call);
            Assert.Empty(lostTraffic);
            return;
        }

        Assert.True(clear, $"the {type} held with its tail {tailFt:F0} ft from the centreline, the hold line is {barFt:F0} ft");
        Assert.Equal(0, ranOut);
        Assert.Empty(calls);
        Assert.Equal([$"Lost sight of {LeadCallsign}, holding position, request taxi instructions"], lostTraffic);
    }

    /// <summary>
    /// The clearing-route run-out hold of the SFO B744 case in a solo room: the pilot's "holding short of …, not clear of …"
    /// call is a radio transmission to a student on ground or tower, once, on the SAY channel alone — an RPO room gets the same
    /// line as an orange warning — and no warning is raised for that aircraft at all.
    /// </summary>
    [Theory]
    [InlineData("GND")]
    [InlineData("TWR")]
    public void Following_ClearingRouteRunsOut_SaysHoldingShortToASoloStudent(string studentPosition)
    {
        if (NewEngineAt("SFO") is not { } probe)
        {
            return;
        }

        ClearingCase crossing = LongClearingCase("SFO", probe.Layout, "B744", withAssignedRoute: false);
        (SimulationEngine Engine, AirportGroundLayout Layout) setup = NewEngineAt("SFO")!.Value;
        setup.Engine.Scenario!.SoloTrainingMode = true;
        setup.Engine.Scenario.StudentPositionType = studentPosition;
        List<string> calls = CaptureSayCalls(setup.Engine, FollowerCallsign, "holding short of runway");
        List<string> warnings = CaptureWarnings(setup.Engine, FollowerCallsign);

        ClearingRun run = RunClearingAcross(setup, crossing, ClearingBudgetSeconds, deleteLead: true, leadAheadAfter: null);

        string next = RunwayCrossingEnd.Nearest(run.Follower, "01L/19R", setup.Layout);
        string cleared = RunwayCrossingEnd.Nearest(run.Follower, "01R/19L", setup.Layout);
        output.WriteLine($"{studentPosition} solo: {string.Join(" | ", calls)}");
        Assert.Equal([$"Holding short of runway {next}, not clear of runway {cleared}"], calls);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The SFO B744 run-out hold, snapshotted the tick its clearing route is last live and restored into a second engine, runs its
    /// run-out again and says the runway call once, never the lost-traffic call: the follow completes the tick its clearing route
    /// runs out, so the snapshot is taken the tick before and the restore proves the run-out hold's line is the runway one.
    /// </summary>
    [Fact]
    public void Following_ClearingRunOutHold_Restored_SaysNoLostTraffic()
    {
        if (NewEngineAt("SFO") is not { } probe)
        {
            return;
        }

        ClearingCase crossing = LongClearingCase("SFO", probe.Layout, "B744", withAssignedRoute: false);
        ClearingRun reference = RunClearingAcross(probe, crossing, ClearingBudgetSeconds, deleteLead: true, leadAheadAfter: null);
        int clearingAt = reference.Seconds.First(s => s.Clearing).Second;
        int runOutAt = reference.Seconds.First(s => (s.Second > clearingAt) && !s.Clearing).Second;
        (SimulationEngine Engine, AirportGroundLayout Layout) setup = NewEngineAt("SFO")!.Value;
        ClearingRun run = RunClearingAcross(setup, crossing, runOutAt - 1, deleteLead: true, leadAheadAfter: null);
        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.NotNull(follow.ClearingRoute);
        string json = JsonSerializer.Serialize(setup.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = NewEngineAt("SFO")!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        List<string> callsAfterRestore = CaptureCalls(restored, FollowerCallsign, "holding short of runway");
        List<string> lostAfterRestore = CaptureCalls(restored, FollowerCallsign, "lost sight of");
        for (int second = runOutAt; (second <= runOutAt + 30) && (restoredFollower.Phases?.CurrentPhase is FollowingPhase); second++)
        {
            restored.TickOneSecond();
        }

        output.WriteLine($"clearing at t={clearingAt}, run out at t={runOutAt}; restored: {string.Join(" | ", callsAfterRestore)}");
        Assert.Single(callsAfterRestore);
        Assert.Empty(lostAfterRestore);
    }

    /// <summary>
    /// The runway a clearing route that ran out short of the hold line names ahead of it
    /// (<see cref="FollowingPhase.RunwayAheadAtTheHold"/>): the one its way on enters, which the SFO B744's run-out stopped short
    /// of, and none at all where the route's end has no edge into another runway — the case that leaves the pilot holding in
    /// position rather than short of a runway.
    /// </summary>
    [Fact]
    public void RunwayAheadAtTheHold_NamesOnlyTheRunwayTheWayOnEnters()
    {
        if (RunAheadOfTheHold("SFO", "B744") is not { } sfo)
        {
            return;
        }

        output.WriteLine($"SFO B744 clearing route ends #{sfo.Last.FromNodeId}>#{sfo.Last.ToNodeId}");
        RunwayIdentifier? ahead = FollowingPhase.RunwayAheadAtTheHold(sfo.Layout, sfo.Cleared, sfo.Last);
        Assert.NotNull(ahead);
        Assert.True(ahead!.Value.Overlaps(RunwayIdentifier.Parse("01L/19R")), $"the B744's way on from its route end enters {ahead}, not 01L/19R");
        if (RunAheadOfTheHold("OAK", "C172") is not { } oak)
        {
            return;
        }

        output.WriteLine($"OAK C172 clearing route ends #{oak.Last.FromNodeId}>#{oak.Last.ToNodeId}");
        Assert.Null(FollowingPhase.RunwayAheadAtTheHold(oak.Layout, oak.Cleared, oak.Last));
    }

    /// <summary>A clearing route's end at <paramref name="airportId"/>, the runway it clears, and the end's last segment.</summary>
    private (AirportGroundLayout Layout, RunwayIdentifier Cleared, TaxiRouteSegment Last)? RunAheadOfTheHold(string airportId, string type)
    {
        if (NewEngineAt(airportId) is not { } probe)
        {
            return null;
        }

        ClearingCase crossing = LongClearingCase(airportId, probe.Layout, type, withAssignedRoute: false);
        ClearingRun reference = RunClearingAcross(probe, crossing, ClearingBudgetSeconds, deleteLead: true, leadAheadAfter: null);
        int clearingAt = reference.Seconds.First(s => s.Clearing).Second;
        (SimulationEngine Engine, AirportGroundLayout Layout) setup = NewEngineAt(airportId)!.Value;
        ClearingRun run = RunClearingAcross(setup, crossing, clearingAt, deleteLead: true, leadAheadAfter: null);
        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        TaxiRoute clearing = Assert.IsType<TaxiRoute>(follow.ClearingRoute);
        return (setup.Layout, crossing.Runway.Id, clearing.Segments[^1]);
    }

    /// <summary>
    /// The crossing <see cref="Following_LongAircraftOnClearingRoute_TailClearsAndBrakesWithinTheRoute"/> clears at
    /// <paramref name="airportId"/>: OAK's 28R on B, as <see cref="RunRouteOutInsideHoldLine"/>; SFO's 01R/19L from bar #898 to
    /// bar #897, the crossing a B744's clearing route ran out on.
    /// </summary>
    private static ClearingCase LongClearingCase(string airportId, AirportGroundLayout layout, string type, bool withAssignedRoute)
    {
        if (airportId == "OAK")
        {
            GroundNode nearBar = KoakFollowGeometry.BChain(layout)[0];
            GroundNode farBar = TestLayoutNodes
                .RunwayHoldShortsOnTaxiway(layout, "28R", "B")
                .Where(n => n.Id != nearBar.Id)
                .MinBy(n => GeoMath.DistanceNm(n.Position, nearBar.Position))!;
            return new ClearingCase(Runway28R(layout), nearBar, farBar, type, withAssignedRoute);
        }

        RunwayInfo runway = RunwayOccupancy.AirportRunways(layout.AirportId).First(r => r.Id.Overlaps(RunwayIdentifier.Parse("01R")));
        Assert.True(layout.Nodes.TryGetValue(898, out GroundNode? near), "SFO node #898 is no longer in the layout");
        Assert.True(layout.Nodes.TryGetValue(897, out GroundNode? far), "SFO node #897 is no longer in the layout");
        Assert.True(
            (near.RunwayId is { } nearId) && nearId.Overlaps(runway.Id) && (far.RunwayId is { } farId) && farId.Overlaps(runway.Id),
            "SFO nodes #898 and #897 are no longer bars of 01R/19L"
        );
        return new ClearingCase(runway, near, far, type, withAssignedRoute);
    }

    /// <summary>An engine over the committed layout of <paramref name="airportId"/>, or null when the layout is unavailable.</summary>
    private (SimulationEngine Engine, AirportGroundLayout Layout)? NewEngineAt(string airportId)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout(airportId) is not { } layout)
        {
            output.WriteLine($"SKIP: {airportId} layout unavailable");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-follow-nested-clearing",
                ScenarioName = "Follow clearing inside a second runway's hold line",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airportId,
                AutoCrossRunway = false,
            },
        };
        return (engine, layout);
    }

    /// <summary>
    /// A lead that is itself a follower — taxiing <c>TAXI B W 30</c> behind the C560, then told <c>FOLLOWG</c> it with a CROSS of
    /// 28R — keeps the taxi route <c>FOLLOWG</c> left in place. Once the CROSS has put a crossing of 28R in front of its follow,
    /// a plan to follow it joins its trail and ends on the edge it is on: it never reads that assigned route, which predates its
    /// <c>FOLLOWG</c>.
    /// </summary>
    [Fact]
    public void Plan_LeadFollowingIntoACrossing_IgnoresItsPreFollowRoute()
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        AircraftState middle = KoakFollowGeometry.AddTaxiing(
            (run.Engine, run.Layout),
            MiddleCallsign,
            "C172",
            (run.Chain[4], run.Chain[3]),
            "TAXI B W 30"
        );
        CommandResult follow = run.Engine.SendCommand(MiddleCallsign, $"FOLLOWG {run.Lead.Callsign}; CROSS 28R");
        Assert.True(follow.Success, follow.Message);
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            KoakFollowGeometry.Between(run.Chain[5].Position, run.Chain[4].Position, 0.5),
            KoakFollowGeometry.Facing(run.Chain[5], run.Chain[4])
        );
        follower.Ground.Layout = run.Layout;

        for (int second = 1; (second <= ChainBudgetSeconds) && (middle.Phases?.CurrentPhase is not CrossingRunwayPhase); second++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.IsType<CrossingRunwayPhase>(middle.Phases?.CurrentPhase);
        TaxiRoute assigned = Assert.IsType<TaxiRoute>(middle.Ground.AssignedTaxiRoute);
        Assert.False(assigned.IsComplete, "the middle aircraft's pre-follow taxi route is complete, so the plan cannot read it");
        FollowRoutePlan.Joinable plan = Assert.IsType<FollowRoutePlan.Joinable>(FollowRoutePlanner.Plan(run.Layout, follower, middle));
        TaxiTrailEdge? newest = middle.Ground.TaxiEdgeTrail.Newest;
        GroundEdge under = Assert.IsType<GroundEdge>(
            TaxiEdgeLocator.EdgeUnder(run.Layout, middle.Position, newest is { } n ? (n.NodeA, n.NodeB) : null)
        );
        int pathEnd = plan.LeadPathFromMerge.Count > 0 ? plan.LeadPathFromMerge[^1].ToNodeId : plan.MergeNode;
        output.WriteLine(
            $"assigned {assigned.ToSummary()} at {assigned.CurrentSegmentIndex}/{assigned.Segments.Count}; lead path "
                + $"{string.Join(" ", plan.LeadPathFromMerge.Select(e => $"#{e.FromNodeId}>#{e.ToNodeId}"))} ends #{pathEnd}; "
                + $"middle on #{under.Nodes[0].Id}-#{under.Nodes[1].Id}"
        );
        Assert.True(
            under.HasNode(pathEnd),
            $"the lead's path ends at #{pathEnd}, off the edge the middle aircraft is on (#{under.Nodes[0].Id}-#{under.Nodes[1].Id})"
        );
    }

    /// <summary>
    /// The last follower of the chained follow (<see cref="RunChain"/>), started half-way along its straight B edge behind the
    /// middle aircraft, plans a short follow route onto the middle aircraft's trail along B: no detour round the field and no
    /// runway holding position on it.
    /// </summary>
    [Fact]
    public void Following_ChainFollowerHalfwayAlongItsEdge_PlansAShortRouteOntoTheLeadsTrail()
    {
        if (RunChain(ChainFollowerStartSecond, 0.5) is not { } run)
        {
            return;
        }

        FollowingPhase follow = Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
        output.WriteLine($"follow route {route.ToSummary()}: {string.Join(" ", route.Segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}");
        List<TaxiRouteSegment> toTrail = [.. route.Segments.Take(follow.MergeSegmentIndex)];
        Assert.DoesNotContain(
            toTrail,
            s => (s.Edge.FromNode.Type == GroundNodeType.RunwayHoldShort) || (s.Edge.ToNode.Type == GroundNodeType.RunwayHoldShort)
        );
        Assert.True(toTrail.Count <= ShortChainRouteSegments, $"the route onto the trail runs {toTrail.Count} segments");
    }

    /// <summary>
    /// The last follower of the chained follow (<see cref="RunChain"/>) plans along its lead's trail and then the lead's own
    /// follow route: its follow route ends where the middle aircraft's does, past 28R, never at the edge the middle aircraft is on.
    /// </summary>
    [Fact]
    public void Following_ChainFollowerOfAFollowingLead_PlansAlongItsLeadsFollowRoutePastTheRunway()
    {
        if (RunChain(ChainFollowerStartSecond, 0.5) is not { } run)
        {
            return;
        }

        AircraftState middle = Assert.IsType<AircraftState>(run.Engine.FindAircraft(MiddleCallsign));
        TaxiRoute middleRoute = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(middle.Phases?.CurrentPhase).FollowRoute);
        TaxiRoute route = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase).FollowRoute);
        output.WriteLine($"middle {middleRoute.ToSummary()}; follower {route.ToSummary()}");
        Assert.Equal(middleRoute.Segments[^1].ToNodeId, route.Segments[^1].ToNodeId);
        Assert.Contains(
            route.Segments,
            s =>
                (s.Edge.ToNode.Type == GroundNodeType.RunwayHoldShort)
                && (s.Edge.ToNode.RunwayId is { } id)
                && id.Overlaps(RunwayIdentifier.Parse("28R"))
        );
    }

    /// <summary>
    /// A follower crossing 28R behind its lead, whose lead is then deleted, does not stop on the runway: it drives off past the
    /// far bar until its tail is past the hold line, and only then holds in position.
    /// </summary>
    [Fact]
    public void Following_LeadDeletedMidCrossing_ClearsPastTheBarThenHolds()
    {
        if (RunRouteOutInsideHoldLine(ClearingBudgetSeconds, deleteLead: true, leadAheadAfter: null) is not { } run)
        {
            return;
        }

        Assert.True(run.Seconds.Any(s => s.Clearing), "the follower never drove a clearing route off 28R");
        Assert.IsType<HoldingInPositionPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.True(run.Seconds.TakeLast(5).All(s => s.SpeedKts < 0.05), "the follower was not holding at the end");
        (double tailFt, double barFt, bool sameSide) = TailAgainstBar(run);
        Assert.True(sameSide && (tailFt > barFt), $"the follower held with its tail {tailFt:F0} ft from 28R's centreline, the bar is {barFt:F0} ft");
    }

    /// <summary>
    /// A follower cleared <c>TAXI B W 30</c> and then told to follow a lead ahead of it on B, whose lead is deleted or leaves the
    /// ground while the follower rolls on a taxiway clear of every runway's hold line, takes up the rest of its assigned route at
    /// once: a <see cref="TaxiingPhase"/> on that route re-anchored where it stands, which drives on along B and stops at the
    /// uncleared 28R bar, never holding in position on the way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Following_LeadLostOnATaxiwayWithARouteLeft_ResumesTheAssignedRoute(bool leadAirborne)
    {
        if (RunLeadLostOnB(withRoute: true, leadAirborne, soloTraining: false, studentPosition: null) is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        int taxiingAt = -1;
        int heldShortAt = -1;
        bool heldInPosition = false;
        for (int second = run.LostAt + 1; (second <= run.LostAt + BudgetSeconds) && (heldShortAt < 0); second++)
        {
            run.Lead.IsOnGround = !leadAirborne;
            run.Engine.TickOneSecond();
            Phase? phase = follower.Phases?.CurrentPhase;
            TaxiRoute? route = follower.Ground.AssignedTaxiRoute;
            output.WriteLine($"t={second} {phase?.Name} gs={follower.GroundSpeed:F1} seg={route?.CurrentSegmentIndex}/{route?.Segments.Count}");
            if ((taxiingAt < 0) && (phase is TaxiingPhase))
            {
                taxiingAt = second;
                TaxiRoute resumed = Assert.IsType<TaxiRoute>(route);
                output.WriteLine($"resumed on {resumed.ToSummary()}");
                Assert.True(OffRouteFt(resumed, follower.Position) <= 5.0, "the resumed route does not start where the follower stands");
            }

            heldInPosition |= phase is HoldingInPositionPhase;
            heldShortAt = ((phase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28R")) ? second : -1;
        }

        Assert.True(taxiingAt == run.LostAt + 1, $"the follower took up its assigned route at t={taxiingAt}, its lead was lost at t={run.LostAt}");
        Assert.False(heldInPosition, "the follower held in position with its assigned route left");
        Assert.True(heldShortAt > 0, $"the follower never held short of 28R on its assigned route within {BudgetSeconds}s");
    }

    /// <summary>
    /// A follower with no assigned route, told to follow a lead ahead of it on B, whose lead is deleted while the follower rolls on
    /// a taxiway clear of every runway's hold line, brakes to rest where it is and holds in position, saying nothing at all: a
    /// follower with no assigned route left to lose has nothing to ask taxi instructions for.
    /// </summary>
    [Fact]
    public void Following_LeadDeletedOnATaxiwayWithNoRouteLeft_HoldsInPosition()
    {
        if (RunLeadLostOnB(withRoute: false, leadAirborne: false, soloTraining: false, studentPosition: null) is not { } run)
        {
            return;
        }

        List<string> calls = CaptureCalls(run.Engine, FollowerCallsign, "lost sight of");
        for (int second = run.LostAt + 1; (second <= run.LostAt + 60) && (run.Follower.Phases?.CurrentPhase is FollowingPhase); second++)
        {
            run.Engine.TickOneSecond();
            output.WriteLine($"t={second} {run.Follower.Phases?.CurrentPhase?.Name} gs={run.Follower.GroundSpeed:F1}");
        }

        Assert.IsType<HoldingInPositionPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.True(run.Follower.GroundSpeed < 0.05, $"the follower holds in position rolling at {run.Follower.GroundSpeed:F2} kt");
        Assert.Empty(calls);
    }

    /// <summary>
    /// The follower of <see cref="Following_LeadDeletedOnATaxiwayWithNoRouteLeft_HoldsInPosition"/> in a solo room: with no
    /// assigned route left it says nothing — neither a SAY transmission to the student nor an orange warning.
    /// </summary>
    [Theory]
    [InlineData("GND")]
    [InlineData("TWR")]
    public void Following_LeadDeletedWithNoRouteLeft_SaysNothingToASoloStudent(string studentPosition)
    {
        if (RunLeadLostOnB(withRoute: false, leadAirborne: false, soloTraining: true, studentPosition: studentPosition) is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        List<string> calls = CaptureSayCalls(run.Engine, FollowerCallsign, "lost");
        List<string> warnings = CaptureWarnings(run.Engine, FollowerCallsign);
        for (int second = run.LostAt + 1; (second <= run.LostAt + 60) && (follower.Phases?.CurrentPhase is FollowingPhase); second++)
        {
            run.Engine.TickOneSecond();
            output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1}");
        }

        Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        Assert.Empty(calls);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The follower of <see cref="Following_LeadLostWithTheWayBackAcrossARunway_SaysLostTrafficOnce"/>, snapshotted the second it
    /// says the lost-traffic call and restored into a second engine, never says it again: the once-only latch rides the snapshot,
    /// so a restore holds where the live follower held. A clearing-route case cannot show this: a follower that cleared the runway
    /// on its route is at rest when the call is said, so the follow completes into a <see cref="HoldingInPositionPhase"/> in that
    /// same tick and no snapshot can carry the latch already set. This follower is still rolling when the call is said, so it
    /// stays in the follow for the ticks a restore would re-enter <see cref="FollowingPhase.SayLostTrafficHolding"/> on.
    /// </summary>
    [Fact]
    public void Following_RestoredAfterSayingLostTraffic_SaysItOnce()
    {
        if (RunLeadLostWithTheWayBackAcross28R(soloTraining: false, studentPosition: null) is not { } run)
        {
            return;
        }

        List<string> live = CaptureCalls(run.Engine, FollowerCallsign, "lost sight of");
        run.Engine.TickOneSecond();
        Assert.Single(live);
        Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase);
        string json = JsonSerializer.Serialize(run.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = KoakFollowGeometry.NewEngine(output, autoCross: false)!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        List<string> saidAgain = CaptureCalls(restored, FollowerCallsign, "lost sight of");
        for (int second = 2; (second <= 30) && (restoredFollower.Phases?.CurrentPhase is FollowingPhase); second++)
        {
            restored.TickOneSecond();
        }

        Assert.Empty(saidAgain);
    }

    /// <summary>
    /// The follower of <see cref="Following_LeadLostOnATaxiwayWithARouteLeft_ResumesTheAssignedRoute"/>, snapshotted through the
    /// recording JSON while it rolls on its resumed route and restored into a second engine, drives exactly as the run it was taken
    /// from: the same position, heading, ground speed and phase every tick after.
    /// </summary>
    [Fact]
    public void Following_RestoredAfterResumingTheAssignedRoute_TicksIdentically()
    {
        if (RunLeadLostOnB(withRoute: true, leadAirborne: false, soloTraining: false, studentPosition: null) is not { } run)
        {
            return;
        }

        for (int second = 1; second <= 3; second++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.IsType<TaxiingPhase>(run.Follower.Phases?.CurrentPhase);
        string json = JsonSerializer.Serialize(run.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = KoakFollowGeometry.NewEngine(output, autoCross: false)!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));
        for (int second = 1; second <= RestoreCompareSeconds; second++)
        {
            run.Engine.TickOneSecond();
            restored.TickOneSecond();
            AircraftState live = run.Follower;
            string at = $"+{second}s after the snapshot";
            Assert.True(live.Position == restoredFollower.Position, $"{at}: restored at {restoredFollower.Position}, live at {live.Position}");
            Assert.True(
                live.TrueHeading.Degrees == restoredFollower.TrueHeading.Degrees,
                $"{at}: restored heading {restoredFollower.TrueHeading.Degrees}, live {live.TrueHeading.Degrees}"
            );
            Assert.True(
                live.GroundSpeed == restoredFollower.GroundSpeed,
                $"{at}: restored {restoredFollower.GroundSpeed} kt, live {live.GroundSpeed} kt"
            );
            Assert.Equal(live.Phases?.CurrentPhase?.Name, restoredFollower.Phases?.CurrentPhase?.Name);
        }
    }

    /// <summary>
    /// A follower with an assigned route along B north of 28R, told to follow a lead ahead of it on B with a crossing of 28R,
    /// whose lead is deleted once the follower is past 28R clear of every hold line: its way back onto that route, never turning
    /// it about, runs around the runway's end and crosses no runway hold line, so it takes up the route at once and drives it,
    /// never on a runway's pavement, saying nothing.
    /// </summary>
    [Fact]
    public void Following_LeadLostWithABarFreeWayBack_ResumesAndNeverEntersARunway()
    {
        if (RunLeadLostPast28R() is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        List<string> calls = CaptureUnableToFollowCalls(run.Engine);
        List<RunwayInfo> runways = [.. RunwayOccupancy.AirportRunways(KoakFollowGeometry.AirportId)];
        int taxiingAt = -1;
        for (int second = run.LostAt + 1; second <= run.LostAt + BudgetSeconds; second++)
        {
            run.Engine.TickOneSecond();
            Phase? phase = follower.Phases?.CurrentPhase;
            output.WriteLine($"t={second} {phase?.Name} gs={follower.GroundSpeed:F1} route={follower.Ground.AssignedTaxiRoute?.ToSummary()}");
            RunwayInfo? entered = runways.FirstOrDefault(r => RunwayOccupancy.IsOnPavement(follower, r));
            Assert.True(entered is null, $"t={second}: the follower entered runway {entered?.Id} ({phase?.Name})");
            taxiingAt = ((taxiingAt < 0) && (phase is TaxiingPhase)) ? second : taxiingAt;
        }

        Assert.True(taxiingAt == run.LostAt + 1, $"the follower took up its assigned route at t={taxiingAt}, its lead was lost at t={run.LostAt}");
        Assert.Empty(calls);
    }

    /// <summary>
    /// <see cref="RunLeadLostOnB"/> with no route, its follower then given the assigned route on B <em>south</em> of 28R: the way
    /// back onto it from north of the runway enters 28R, which the route holds no stop for.
    /// </summary>
    private LeadLostRun? RunLeadLostWithTheWayBackAcross28R(bool soloTraining, string? studentPosition)
    {
        if (RunLeadLostOnB(withRoute: false, leadAirborne: false, soloTraining, studentPosition) is not { } run)
        {
            return null;
        }

        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(run.Follower.Ground.Layout);
        run.Follower.Ground.AssignedTaxiRoute = RouteSouthOf28ROnB(layout);
        output.WriteLine($"follower assigned {run.Follower.Ground.AssignedTaxiRoute.ToSummary()}");
        return run;
    }

    /// <summary>
    /// A follower told to follow a lead ahead of it on B, whose assigned route lies on B south of 28R, and whose lead is deleted
    /// while it rolls north of 28R clear of every hold line: its way back onto that route enters 28R, which the route holds no
    /// stop for, so it never takes the route up. The follow becomes unjoinable, the pilot says once that it has lost the traffic
    /// and wants taxi instructions, and it ends holding in position — never the "unable to follow" call, which is for a plan that
    /// joins nothing, not a route it cannot reach.
    /// </summary>
    [Fact]
    public void Following_LeadLostWithTheWayBackAcrossARunway_SaysLostTrafficOnce()
    {
        if (RunLeadLostWithTheWayBackAcross28R(soloTraining: false, studentPosition: null) is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        List<string> lost = CaptureCalls(run.Engine, FollowerCallsign, "lost sight of");
        List<string> unable = CaptureCalls(run.Engine, FollowerCallsign, "no taxi route");
        for (int second = run.LostAt + 1; (second <= run.LostAt + 60) && (follower.Phases?.CurrentPhase is FollowingPhase); second++)
        {
            run.Engine.TickOneSecond();
            Phase? phase = follower.Phases?.CurrentPhase;
            output.WriteLine($"t={second} {phase?.Name} gs={follower.GroundSpeed:F1}");
            Assert.False(phase is TaxiingPhase, $"t={second}: the follower took up its assigned route across 28R");
        }

        Assert.True(follow.IsUnjoinable, "the refused follow did not latch unjoinable");
        Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal([$"Lost sight of {LeadCallsign}, holding position, request taxi instructions"], lost);
        Assert.Empty(unable);
    }

    /// <summary>
    /// The refused way back of <see cref="Following_LeadLostWithTheWayBackAcrossARunway_SaysLostTrafficOnce"/> in a solo room: the
    /// same call, once, is a radio transmission on the SAY channel to a student on ground or tower, in the traffic's words, and no
    /// orange warning is raised for the follower at all.
    /// </summary>
    [Theory]
    [InlineData("GND")]
    [InlineData("TWR")]
    public void Following_LeadLostWithTheWayBackAcrossARunway_SaysLostTrafficToASoloStudent(string studentPosition)
    {
        if (RunLeadLostWithTheWayBackAcross28R(soloTraining: true, studentPosition: studentPosition) is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        List<string> calls = CaptureSayCalls(run.Engine, FollowerCallsign, "lost");
        List<string> warnings = CaptureWarnings(run.Engine, FollowerCallsign);
        for (int second = run.LostAt + 1; (second <= run.LostAt + 60) && (follower.Phases?.CurrentPhase is FollowingPhase); second++)
        {
            run.Engine.TickOneSecond();
            output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1}");
        }

        Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(["Lost sight of traffic, holding position, request taxi instructions"], calls);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A C172 at rest on 28R's north bar on B, facing the runway, whose way back onto B south of 28R starts at that bar and goes
    /// on to the runway's south bar: <see cref="FollowingPhase.RunwayEntries"/> names the bar it starts at first, the crossing
    /// the hold-short annotator reads as already under way.
    /// </summary>
    [Fact]
    public void RunwayEntries_WayBackStartingOnABarAcrossItsRunway_NamesThatBarFirst()
    {
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        TaxiRoute southOf28R = RouteSouthOf28ROnB(layout);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", chain[0].Position, KoakFollowGeometry.Facing(chain[1], chain[0]));
        follower.Ground.Layout = layout;
        HashSet<int> goal = [southOf28R.Segments[^1].ToNodeId];
        FollowRoutePlanner.RouteFromHere? onto = FollowRoutePlanner.RouteOnto(layout, follower, goal);
        Assert.True(onto.HasValue, $"no way back from 28R's bar #{chain[0].Id} onto B south of the runway");
        output.WriteLine($"way back {string.Join(" ", onto.Value.Segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}");
        Assert.Equal(chain[0].Id, onto.Value.Segments[0].FromNodeId);
        Assert.Equal(chain[0].Id, FollowingPhase.RunwayEntries(layout, onto.Value)[0]);
    }

    /// <summary>
    /// An aircraft that has not moved yet (no trail edge), mid-way along the B edge into 28R's south bar and facing it, whose only
    /// goal is the node behind it: its way back (<see cref="FollowRoutePlanner.RouteOnto"/>) never turns it about — its first
    /// segment leaves within 90° of its heading and no segment drives straight back along the one before.
    /// </summary>
    [Fact]
    public void RouteOnto_GoalBehindWithNoTrail_NeverTurnsAbout()
    {
        if (LeadInAcross28R() is not { } leadIn)
        {
            return;
        }

        int behind = RouteSouthOf28ROnB(leadIn.Layout).Segments[0].FromNodeId;
        FollowRoutePlanner.RouteFromHere? onto = FollowRoutePlanner.RouteOnto(leadIn.Layout, leadIn.Follower, new HashSet<int> { behind });
        Assert.NotNull(onto);

        List<TaxiRouteSegment> segments = onto.Value.Segments;
        output.WriteLine($"way back onto #{behind}: {string.Join(" ", segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}");
        Assert.True(FollowRoutePlanner.DepartsAhead(leadIn.Follower, segments[0].Edge), "the way back starts back across the heading");
        for (int i = 1; i < segments.Count; i++)
        {
            bool reverses = (segments[i].FromNodeId == segments[i - 1].ToNodeId) && (segments[i].ToNodeId == segments[i - 1].FromNodeId);
            Assert.False(reverses, $"segment {i} drives #{segments[i].FromNodeId}>#{segments[i].ToNodeId} straight back along the one before");
        }
    }

    /// <summary>
    /// An aircraft standing at a B node with no trail edge, facing along B to the next node, plans straight onto it
    /// (<see cref="FollowRoutePlanner.BackAlongEdgeUnder"/> forbids the edge under it, from the node it stands at, only where
    /// that edge's far end lies behind it — here the far end is the node it faces, so nothing is forbidden).
    /// </summary>
    [Fact]
    public void RouteOnto_AtANodeWithNoTrailFacingItsEdge_ForbidsNothing()
    {
        if (KoakFollowGeometry.LoadLayout(output) is not { } layout)
        {
            return;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        GroundNode at = chain[5];
        GroundNode next = chain[6];
        AircraftState aircraft = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", at.Position, KoakFollowGeometry.Facing(at, next));
        aircraft.Ground.Layout = layout;
        FollowRoutePlanner.RouteFromHere? onto = FollowRoutePlanner.RouteOnto(layout, aircraft, new HashSet<int> { next.Id });
        output.WriteLine(
            onto is { } route
                ? $"route {string.Join(" ", route.Segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}"
                : $"no route from #{at.Id} to #{next.Id}"
        );
        Assert.NotNull(onto);
        Assert.Equal(at.Id, onto.Value.Segments[0].FromNodeId);
        Assert.Equal(next.Id, onto.Value.Segments[0].ToNodeId);
    }

    /// <summary>A taxi route along B south of 28R, from the node past its south bar on to the next.</summary>
    private static TaxiRoute RouteSouthOf28ROnB(AirportGroundLayout layout)
    {
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        GroundNode farBar = TestLayoutNodes
            .RunwayHoldShortsOnTaxiway(layout, "28R", "B")
            .Where(n => n.Id != chain[0].Id)
            .MinBy(n => GeoMath.DistanceNm(n.Position, chain[0].Position))!;
        GroundNode south = farBar.Edges.Select(e => e.OtherNode(farBar)).MaxBy(n => GeoMath.DistanceNm(n.Position, chain[0].Position))!;
        GroundNode next = south.Edges.Select(e => e.OtherNode(south)).MaxBy(n => GeoMath.DistanceNm(n.Position, chain[0].Position))!;
        WakeTurbulenceData.WakeClass wake = WakeTurbulenceData.WakeClassForType("C172", AircraftCategory.Piston);
        return Assert.IsType<TaxiRoute>(TaxiPathfinder.FindRoute(layout, south.Id, next.Id, AircraftCategory.Piston, wake));
    }

    /// <summary>
    /// A real way back across a runway — from a C172 south of 28R on B, facing north, onto B north of 28R — enters 28R at its
    /// south bar, which <see cref="FollowingPhase.RunwayEntries"/> finds.
    /// </summary>
    [Fact]
    public void RunwayEntries_WayBackAcross28ROnB_FindsTheBar()
    {
        if (LeadInAcross28R() is not { } leadIn)
        {
            return;
        }

        Assert.Contains(leadIn.FarBar.Id, FollowingPhase.RunwayEntries(leadIn.Layout, leadIn.Onto));
    }

    /// <summary>
    /// The same way back across 28R on B also enters the runway itself: <see cref="FollowingPhase.RunwayEntries"/> lists every node
    /// of it on 28R's pavement, or reached along a runway centreline edge, short of the assigned route's node it joins, so a way
    /// back across a runway is refused even where no hold-short bar marks it (7110.65 3-7-2.c/d).
    /// </summary>
    [Fact]
    public void RunwayEntries_WayBackAcross28ROnB_ListsItsNodesOnTheRunway()
    {
        if (LeadInAcross28R() is not { } leadIn)
        {
            return;
        }

        RunwayInfo runway = Runway28R(leadIn.Layout);
        List<int> onRunway =
        [
            .. leadIn
                .Onto.Segments.Where(s =>
                    (s.ToNodeId != leadIn.Onto.GoalNodeId)
                    && (s.Edge.Edge.IsRunwayCenterline || RunwayOccupancy.IsWithinPavement(s.Edge.ToNode.Position, runway))
                )
                .Select(s => s.ToNodeId),
        ];
        output.WriteLine($"way back nodes on 28R: {string.Join(" ", onRunway.Select(id => $"#{id}"))}");
        Assert.NotEmpty(onRunway);
        List<int> entries = FollowingPhase.RunwayEntries(leadIn.Layout, leadIn.Onto);
        Assert.All(onRunway, id => Assert.Contains(id, entries));
    }

    /// <summary>
    /// A follower whose way back onto its assigned route crosses 28R on B refuses it: the follow becomes unjoinable, and the pilot
    /// says once — however often the way back is refused — that it has lost the traffic and wants taxi instructions, naming the
    /// lead, never the "unable to follow" call a plan that joins nothing gets.
    /// </summary>
    [Fact]
    public void Following_WayBackAcrossARunway_IsRefusedAndSaysLostTrafficOnce()
    {
        if (LeadInAcross28R() is not { } leadIn)
        {
            return;
        }

        leadIn.Follower.Ground.AssignedTaxiRoute = RouteSouthOf28ROnB(leadIn.Layout);
        var follow = new FollowingPhase(LeadCallsign);
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(leadIn.Follower, leadIn.Layout);
        Assert.True(follow.RefuseLeadInAcrossRunway(ctx, leadIn.Layout, leadIn.Onto), "the way back across 28R was not refused");
        Assert.True(follow.RefuseLeadInAcrossRunway(ctx, leadIn.Layout, leadIn.Onto), "the way back across 28R was not refused again");
        Assert.True(follow.IsUnjoinable, "the refused follow did not latch unjoinable");
        Assert.Equal([$"Lost sight of {LeadCallsign}, holding position, request taxi instructions"], leadIn.Follower.PendingWarnings);
    }

    private sealed record LeadIn(AirportGroundLayout Layout, AircraftState Follower, GroundNode FarBar, FollowRoutePlanner.RouteFromHere Onto);

    /// <summary>
    /// A C172 mid-way along the B edge into 28R's south bar from the south, facing the bar, and its way back
    /// (<see cref="FollowRoutePlanner.RouteOnto"/>) onto B's <c>Chain[1]</c> north of 28R; null when the layout is unavailable.
    /// </summary>
    private LeadIn? LeadInAcross28R()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return null;
        }

        AirportGroundLayout layout = setup.Layout;
        List<GroundNode> chain = KoakFollowGeometry.BChain(layout);
        GroundNode farBar = TestLayoutNodes
            .RunwayHoldShortsOnTaxiway(layout, "28R", "B")
            .Where(n => n.Id != chain[0].Id)
            .MinBy(n => GeoMath.DistanceNm(n.Position, chain[0].Position))!;
        GroundNode south = farBar.Edges.Select(e => e.OtherNode(farBar)).MaxBy(n => GeoMath.DistanceNm(n.Position, chain[0].Position))!;
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            KoakFollowGeometry.Between(south.Position, farBar.Position, 0.5),
            KoakFollowGeometry.Facing(south, farBar)
        );
        follower.Ground.Layout = layout;
        FollowRoutePlanner.RouteFromHere? onto = FollowRoutePlanner.RouteOnto(layout, follower, new HashSet<int> { chain[1].Id });
        Assert.True(onto.HasValue, $"no way back from south of 28R's bar #{farBar.Id} onto B node #{chain[1].Id}");
        output.WriteLine($"way back {string.Join(" ", onto.Value.Segments.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}");
        return new LeadIn(layout, follower, farBar, onto.Value);
    }

    /// <summary>
    /// A follow that latched no taxi path onto its lead's path — said once — and so holds for good never takes up its assigned
    /// route on its own when its lead is then deleted: it stays where it is and ends in a <see cref="HoldingInPositionPhase"/>,
    /// saying nothing more.
    /// </summary>
    [Fact]
    public void Following_UnjoinableWhenItsLeadIsDeleted_NeverTakesUpItsAssignedRoute()
    {
        if (StartFollowerOnIsland(soloOnGround: false) is not { } run)
        {
            return;
        }

        AircraftState follower = run.Follower;
        follower.Ground.AssignedTaxiRoute = IslandRouteAhead(run.Layout, follower);
        output.WriteLine($"assigned {follower.Ground.AssignedTaxiRoute.ToSummary()}");
        List<string> calls = CaptureUnableToFollowCalls(run.Engine);
        List<string> lost = CaptureCalls(run.Engine, FollowerCallsign, "lost sight of");
        for (int second = 1; second <= 5; second++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.True(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).IsUnjoinable, "the follow with no taxi path never latched");
        run.Engine.World.RemoveAircraft(LeadCallsign);
        LatLon start = follower.Position;
        for (int second = 6; second <= 40; second++)
        {
            run.Engine.TickOneSecond();
            Phase? phase = follower.Phases?.CurrentPhase;
            output.WriteLine($"t={second} {phase?.Name} gs={follower.GroundSpeed:F2}");
            Assert.False(phase is TaxiingPhase, $"t={second}: the unjoinable follower took up its assigned route on its own");
        }

        Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        Assert.True(GeoMath.DistanceNm(start, follower.Position) * GeoMath.FeetPerNm < 1.0, "the unjoinable follower moved");
        Assert.Single(calls);
        Assert.Empty(lost);
    }

    /// <summary>
    /// A taxi route on <paramref name="follower"/>'s island from the node ahead of it to the island node farthest from that one
    /// that a route reaches; the test fails when no route from that node reaches any other island node.
    /// </summary>
    private static TaxiRoute IslandRouteAhead(AirportGroundLayout layout, AircraftState follower)
    {
        bool IsAhead(GroundNode n) =>
            Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, GeoMath.BearingTo(follower.Position, n.Position))) <= 90.0;
        GroundNode nearest = layout.Nodes.Values.MinBy(n => GeoMath.DistanceNm(n.Position, follower.Position))!;
        HashSet<int> island = GraphComponents.ComponentOf(nearest);
        List<GroundNode> nodes = [.. island.Select(id => layout.Nodes[id])];
        GroundNode ahead = nodes.Where(IsAhead).MinBy(n => GeoMath.DistanceNm(n.Position, follower.Position))!;
        WakeTurbulenceData.WakeClass wake = WakeTurbulenceData.WakeClassForType("C172", AircraftCategory.Piston);
        foreach (GroundNode target in nodes.Where(n => n.Id != ahead.Id).OrderByDescending(n => GeoMath.DistanceNm(n.Position, ahead.Position)))
        {
            if (TaxiPathfinder.FindRoute(layout, ahead.Id, target.Id, AircraftCategory.Piston, wake) is { Segments.Count: > 0 } route)
            {
                return route;
            }
        }

        Assert.Fail($"no taxi route on the island from node #{ahead.Id}");
        return null!;
    }

    /// <summary>
    /// The lead of <see cref="RunLeadLostOnB"/>, cleared to cross 28R, and a C172 behind it at <c>Chain[5]</c> told
    /// <c>FOLLOWG</c> the lead with a <c>CROSS 28R</c> and given the taxi route along B from <c>Chain[4]</c> to <c>Chain[1]</c>.
    /// The second the follower, having been on 28R, rolls at 2 kt or more on its follow route inside no runway's hold line, the lead is deleted.
    /// </summary>
    private LeadLostRun? RunLeadLostPast28R()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return null;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(setup.Layout);
        AircraftState lead = KoakFollowGeometry.AddTaxiing(setup, LeadCallsign, "C560", (chain[3], chain[2]), "TAXI B W 30");
        CommandResult cross = setup.Engine.SendCommand(LeadCallsign, "CROSS 28R");
        Assert.True(cross.Success, cross.Message);
        for (int second = 0; (second < 120) && (lead.Ground.TaxiEdgeTrail.Edges.Count < 2); second++)
        {
            setup.Engine.TickOneSecond();
        }

        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", chain[5].Position, KoakFollowGeometry.Facing(chain[5], chain[4]));
        follower.Ground.Layout = setup.Layout;
        setup.Engine.World.AddAircraft(follower);
        CommandResult result = setup.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}; CROSS 28R");
        Assert.True(result.Success, result.Message);
        WakeTurbulenceData.WakeClass wake = WakeTurbulenceData.WakeClassForType("C172", AircraftCategory.Piston);
        follower.Ground.AssignedTaxiRoute = TaxiPathfinder.FindRoute(setup.Layout, chain[4].Id, chain[1].Id, AircraftCategory.Piston, wake);
        output.WriteLine($"follower assigned {follower.Ground.AssignedTaxiRoute?.ToSummary()}");
        RunwayInfo runway = Runway28R(setup.Layout);
        bool crossed = false;
        for (int second = 1; second <= BudgetSeconds; second++)
        {
            setup.Engine.TickOneSecond();
            crossed |= RunwayOccupancy.IsOnPavement(follower, runway);
            output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} crossed={crossed}");
            bool past =
                crossed
                && (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute: not null })
                && (follower.GroundSpeed >= 2.0)
                && (FollowingPhase.RunwayInsideHoldLine(follower, setup.Layout) is null);
            if (past)
            {
                setup.Engine.World.RemoveAircraft(LeadCallsign);
                return new LeadLostRun(setup.Engine, lead, follower, second);
            }
        }

        Assert.Fail($"the follower never rolled on its follow route past 28R clear of every hold line within {BudgetSeconds}s");
        return null;
    }

    private sealed record LeadLostRun(SimulationEngine Engine, AircraftState Lead, AircraftState Follower, int LostAt);

    /// <summary>
    /// A C560 lead at B <c>Chain[3]</c> cleared <c>TAXI B W 30</c> (no crossing cleared) and a C172 behind it at <c>Chain[5]</c>,
    /// cleared <c>TAXI B W 30</c> too when <paramref name="withRoute"/>, then told to follow the lead. The second the follower
    /// rolls at 5 kt or more on its follow route, inside no runway's hold line, the lead is deleted, or with
    /// <paramref name="leadAirborne"/> taken off the ground. A solo room with the student on <paramref name="studentPosition"/>
    /// when <paramref name="soloTraining"/>.
    /// </summary>
    private LeadLostRun? RunLeadLostOnB(bool withRoute, bool leadAirborne, bool soloTraining, string? studentPosition)
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return null;
        }

        setup.Engine.Scenario!.SoloTrainingMode = soloTraining;
        setup.Engine.Scenario.StudentPositionType = studentPosition;

        List<GroundNode> chain = KoakFollowGeometry.BChain(setup.Layout);
        AircraftState lead = KoakFollowGeometry.AddTaxiing(setup, LeadCallsign, "C560", (chain[3], chain[2]), "TAXI B W 30");
        CommandResult cross = setup.Engine.SendCommand(LeadCallsign, "CROSS 28R");
        Assert.True(cross.Success, cross.Message);
        for (int second = 0; (second < 120) && (lead.Ground.TaxiEdgeTrail.Edges.Count < 2); second++)
        {
            setup.Engine.TickOneSecond();
        }

        AircraftState follower;
        if (withRoute)
        {
            follower = KoakFollowGeometry.AddTaxiing(setup, FollowerCallsign, "C172", (chain[5], chain[4]), "TAXI B W 30");
        }
        else
        {
            follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", chain[5].Position, KoakFollowGeometry.Facing(chain[5], chain[4]));
            follower.Ground.Layout = setup.Layout;
            setup.Engine.World.AddAircraft(follower);
        }

        CommandResult result = setup.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);
        for (int second = 1; second <= BudgetSeconds; second++)
        {
            setup.Engine.TickOneSecond();
            bool rolling =
                (follower.Phases?.CurrentPhase is FollowingPhase { FollowRoute: not null })
                && (follower.GroundSpeed >= 5.0)
                && (FollowingPhase.RunwayInsideHoldLine(follower, setup.Layout) is null);
            output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} lead gs={lead.GroundSpeed:F1}");
            if (!rolling)
            {
                continue;
            }

            if (leadAirborne)
            {
                lead.IsOnGround = false;
            }
            else
            {
                setup.Engine.World.RemoveAircraft(LeadCallsign);
            }

            return new LeadLostRun(setup.Engine, lead, follower, second);
        }

        Assert.Fail($"the follower never rolled on its follow route clear of every hold line within {BudgetSeconds}s");
        return null;
    }

    /// <summary>
    /// A follower clearing 28R at taxi speed after its lead is deleted brakes to its hold at the category's taxi brake rate once
    /// it is clear — its ground speed never falls faster than that rate in a second, so it never drops to rest in a tick — and
    /// then holds in position.
    /// </summary>
    [Fact]
    public void Following_LeadDeletedAndClear_BrakesToItsHoldAtTheTaxiRate()
    {
        if (RunRouteOutInsideHoldLine(ClearingBudgetSeconds, deleteLead: true, leadAheadAfter: null) is not { } run)
        {
            return;
        }

        int brakingFrom = run.Seconds.MaxBy(s => s.SpeedKts)!.Second;
        double rateKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston);
        List<ClearingSecond> braking = [.. run.Seconds.Where(s => s.Second >= brakingFrom)];
        Assert.True(braking[0].SpeedKts > rateKts, $"the follower peaked at only {braking[0].SpeedKts:F1} kt while clearing: nothing to brake from");
        for (int i = 1; i < braking.Count; i++)
        {
            double dropKts = braking[i - 1].SpeedKts - braking[i].SpeedKts;
            Assert.True(
                dropKts <= (rateKts + 0.05),
                $"t={braking[i].Second}: ground speed fell {dropKts:F2} kt in a second, more than the {rateKts:F1} kt/s taxi brake rate"
            );
        }

        Assert.IsType<HoldingInPositionPhase>(run.Follower.Phases?.CurrentPhase);
        Assert.True(run.Seconds.TakeLast(5).All(s => s.SpeedKts < 0.05), "the follower was not holding at the end");
    }

    /// <summary>
    /// A follower on 28R's pavement on G whose lead is deleted clears the runway past G's bar, where G bends some 30 ft beyond
    /// the bar. Once its tail and wingtips are past the hold line it keeps steering along its clearing route while it brakes
    /// (7110.65 §3-10-9.b NOTE 1; AIM 4-3-21.b): it comes to rest on the route's centreline through the bend, off the runway,
    /// short of the route's end node, with its tail past the bar.
    /// </summary>
    [Fact]
    public void Following_LeadDeletedClearingOntoABendingTaxiway_StopsOnTheClearingRouteCentreline()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        RunwayInfo runway = Runway28R(layout);
        GroundNode bar = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "G").MaxBy(b => BendPastBarDeg(runway, b))!;
        (GroundNode start, GroundNode outward) = PavementStartToward(runway, bar);
        double bendDeg = BendPastBarDeg(runway, bar);
        output.WriteLine($"bar #{bar.Id} bends {bendDeg:F0} deg past it; follower at #{start.Id} facing #{outward.Id}");
        Assert.True(
            bendDeg >= 15.0,
            $"G's sharpest bend past a 28R bar (#{bar.Id}) is only {bendDeg:F1} deg: no bend to steer the braking roll through"
        );
        AircraftState lead = KoakFollowGeometry.Spawn(LeadCallsign, "C172", outward.Position, KoakFollowGeometry.Facing(start, outward));
        lead.Ground.Layout = layout;
        engine.World.AddAircraft(lead);
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", start.Position, KoakFollowGeometry.Facing(start, outward));
        follower.Ground.Layout = layout;
        engine.World.AddAircraft(follower);
        CommandResult result = engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);
        engine.World.RemoveAircraft(LeadCallsign);

        TaxiRoute? clearing = null;
        for (int second = 1; (second <= ClearingBudgetSeconds) && (follower.Phases?.CurrentPhase is FollowingPhase); second++)
        {
            engine.TickOneSecond();
            clearing ??= (follower.Phases?.CurrentPhase as FollowingPhase)?.ClearingRoute;
            string offRoute = clearing is null ? "-" : $"{OffRouteFt(clearing, follower.Position):F2}";
            output.WriteLine($"t={second} gs={follower.GroundSpeed:F2} offRoute={offRoute}");
        }

        Assert.NotNull(clearing);
        output.WriteLine($"clearing route {clearing.ToSummary()}");
        Assert.IsType<HoldingInPositionPhase>(follower.Phases?.CurrentPhase);
        double offRouteFt = OffRouteFt(clearing, follower.Position);
        Assert.True(offRouteFt <= 2.0, $"the follower came to rest {offRouteFt:F1} ft off its clearing route's centreline");
        Assert.False(RunwayOccupancy.IsWithinPavement(follower.Position, runway), "the follower came to rest on 28R's pavement");
        var run = new ClearingRun(engine, follower, bar, runway, []);
        GroundNode end = clearing.Segments[^1].Edge.ToNode;
        double halfLengthNm = AircraftLength.ResolveFt(follower.AircraftType) / 2.0 / GeoMath.FeetPerNm;
        LatLon nose = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, halfLengthNm);
        double endOffDeg = Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, GeoMath.BearingTo(nose, end.Position)));
        Assert.True(endOffDeg < 90.0, $"the follower's nose came to rest past the clearing route's end node #{end.Id}");
        (double tailFt, double barFt, bool sameSide) = TailAgainstBar(run);
        Assert.True(sameSide && (tailFt > barFt), $"the follower held with its tail {tailFt:F0} ft from 28R's centreline, the bar is {barFt:F0} ft");
    }

    /// <summary>
    /// How far (deg) <paramref name="bar"/>'s taxiway turns at the first node past it away from <paramref name="runway"/>: the
    /// turn onto its straightest onward edge; 0 with none.
    /// </summary>
    private static double BendPastBarDeg(RunwayInfo runway, GroundNode bar)
    {
        foreach (IGroundEdge edge in bar.Edges.Where(e => !e.IsRunwayCenterline))
        {
            GroundNode away = edge.OtherNode(bar);
            if (CentrelineFt(runway, away.Position) <= CentrelineFt(runway, bar.Position))
            {
                continue;
            }

            double inDeg = GeoMath.BearingTo(bar.Position, away.Position);
            return away
                .Edges.Where(o => !ReferenceEquals(o, edge))
                .Select(o => Math.Abs(GeoMath.SignedBearingDifference(inDeg, GeoMath.BearingTo(away.Position, o.OtherNode(away).Position))))
                .DefaultIfEmpty(0.0)
                .Min();
        }

        return 0.0;
    }

    /// <summary>
    /// The first node on <paramref name="runway"/>'s pavement walking from <paramref name="bar"/> toward the runway, each step to
    /// the neighbour nearest the centreline, and the node it was reached from.
    /// </summary>
    private static (GroundNode Start, GroundNode Outward) PavementStartToward(RunwayInfo runway, GroundNode bar)
    {
        GroundNode outward = bar;
        GroundNode node = bar;
        while (!RunwayOccupancy.IsWithinPavement(node.Position, runway))
        {
            GroundNode current = node;
            GroundNode next = current.Edges.Select(e => e.OtherNode(current)).MinBy(n => CentrelineFt(runway, n.Position))!;
            Assert.True(CentrelineFt(runway, next.Position) < CentrelineFt(runway, current.Position), $"no way onto {runway.Id} from #{bar.Id}");
            outward = current;
            node = next;
        }

        return (node, outward);
    }

    /// <summary>The distance (ft) from <paramref name="point"/> to the nearest point of <paramref name="route"/>'s edges, curves included.</summary>
    internal static double OffRouteFt(TaxiRoute route, LatLon point)
    {
        double nearestFt = double.MaxValue;
        foreach (TaxiRouteSegment segment in route.Segments)
        {
            List<LatLon> line = CentrelineOf(segment.Edge.Edge);
            for (int i = 1; i < line.Count; i++)
            {
                nearestFt = Math.Min(nearestFt, GeoMath.DistanceToSegmentFt(point, line[i - 1], line[i]));
            }
        }

        return nearestFt;
    }

    /// <summary>An edge's centreline as a polyline: a straight edge through its shape points, a fillet arc sampled every 1/64 of its curve.</summary>
    private static List<LatLon> CentrelineOf(IGroundEdge edge)
    {
        if (edge is GroundArc arc)
        {
            CubicBezier curve = arc.ToBezier();
            return [.. Enumerable.Range(0, 65).Select(i => curve.Evaluate(i / 64.0)).Select(p => new LatLon(p.Lat, p.Lon))];
        }

        IEnumerable<LatLon> shape = edge is GroundEdge straight ? straight.IntermediatePoints.Select(p => new LatLon(p.Lat, p.Lon)) : [];
        return [edge.Nodes[0].Position, .. shape, edge.Nodes[1].Position];
    }

    /// <summary>
    /// A follower driving its clearing route off 28R is driving that route as far as the ground conflict detector is concerned:
    /// it is a taxiing aircraft on a route, and shares an upcoming node with traffic coming the other way along it.
    /// </summary>
    [Fact]
    public void Following_ClearingFollower_IsReadAsDrivingItsClearingRoute()
    {
        if (RunRouteOutInsideHoldLine(ClearingBudgetSeconds, deleteLead: false, leadAheadAfter: null) is not { } reference)
        {
            return;
        }

        int clearingAt = reference.Seconds.First(s => s.Clearing).Second;
        ClearingRun run = Assert.IsType<ClearingRun>(RunRouteOutInsideHoldLine(clearingAt, deleteLead: false, leadAheadAfter: null));
        TaxiRoute clearing = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(run.Follower.Phases?.CurrentPhase).ClearingRoute);
        TaxiRoute driven = Assert.IsType<TaxiRoute>(FollowingPhase.DrivenRouteOf(run.Follower));
        Assert.Same(clearing, driven);
        Assert.NotNull(driven.CurrentSegment);

        List<TaxiRouteSegment> oncoming =
        [
            .. clearing
                .Segments.Skip(clearing.CurrentSegmentIndex)
                .Reverse()
                .Select(s => new TaxiRouteSegment { Edge = s.Edge.Edge.Directed(s.Edge.ToNode, s.Edge.FromNode), TaxiwayName = s.TaxiwayName }),
        ];
        AircraftState other = KoakFollowGeometry.Spawn(
            "N3OTH",
            "C172",
            oncoming[0].Edge.FromNode.Position,
            KoakFollowGeometry.Facing(oncoming[0].Edge.FromNode, oncoming[0].Edge.ToNode)
        );
        other.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = oncoming, HoldShortPoints = [] };
        Assert.True(GroundConflictDetector.ShareUpcomingNode(run.Follower, other), "the detector did not read the clearing route as driven");
    }

    /// <summary>
    /// A follower off 28R's pavement but inside its hold line on B, facing the runway, whose lead is parked: it never drives a
    /// clearing route toward or across the runway; it logs a warning naming itself and the runway and holds where it stands.
    /// </summary>
    [Fact]
    public void Following_InsideTheHoldLineFacingTheRunway_WarnsAndHolds()
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        var tap = new CapturingSimLogProvider(LogLevel.Warning, capacity: 200);
        SimLogBuilder.CreateForTest(output).EnableCategory("FollowingPhase", LogLevel.Warning).CaptureInto(tap).InitializeSimLog();
        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        RunwayInfo runway = Runway28R(layout);
        GroundNode nearBar = KoakFollowGeometry.BChain(layout)[0];
        IGroundEdge toward = nearBar.Edges.MinBy(e => CentrelineFt(runway, e.OtherNode(nearBar).Position))!;
        GroundNode next = toward.OtherNode(nearBar);
        TrueHeading heading = KoakFollowGeometry.Facing(nearBar, next);
        double halfLengthNm = AircraftLength.ResolveFt("C172") / 2.0 / GeoMath.FeetPerNm;
        LatLon position = Enumerable
            .Range(1, 19)
            .Select(step => KoakFollowGeometry.Between(nearBar.Position, next.Position, step / 20.0))
            .Last(p => !RunwayOccupancy.IsWithinPavement(GeoMath.ProjectPoint(p, heading, halfLengthNm), runway));
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", position, heading);
        follower.Ground.Layout = layout;
        engine.World.AddAircraft(follower);
        engine.World.AddAircraft(KoakFollowGeometry.SpawnAtStand(layout, LeadCallsign));
        Assert.False(RunwayOccupancy.IsOnPavement(follower, runway), "the follower starts on 28R's pavement");
        Assert.True(
            FollowingPhase.RunwayInsideHoldLine(follower, layout)?.Id.Overlaps(runway.Id) == true,
            "the follower is not inside 28R's hold line"
        );
        CommandResult result = engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);

        for (int second = 1; second <= 10; second++)
        {
            engine.TickOneSecond();
            output.WriteLine($"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2}");
        }

        Assert.Null(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).ClearingRoute);
        Assert.True(GeoMath.DistanceNm(position, follower.Position) * GeoMath.FeetPerNm < 1.0, "the follower moved toward the runway");
        Assert.Contains(
            tap.Drain(),
            r =>
                (r.Level == LogLevel.Warning)
                && r.Message.Contains(FollowerCallsign, StringComparison.Ordinal)
                && r.Message.Contains(runway.Id.ToString(), StringComparison.Ordinal)
                && r.Message.Contains("facing", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A wide-body on the most angled runway exit of KOAK or KSFO, its tail just past the exit's hold line, is not yet clear of
    /// it: the wingtip on the runway side is still inside the line. Far enough on, it is.
    /// </summary>
    [Fact]
    public void IsClearPastBar_WideBodyOnAnAngledExit_NotWhileAWingtipIsInside()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        (RunwayInfo Runway, GroundNode Bar, GroundNode Away, double AngleDeg)? best = null;
        foreach (string airport in new[] { "OAK", "SFO" })
        {
            if (groundData.GetLayout(airport) is not { } layout)
            {
                continue;
            }

            foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(layout.AirportId))
            {
                double axisDeg = GeoMath.BearingTo(new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));
                foreach (GroundNode bar in RunwayBarsOf(layout, runway))
                {
                    foreach (GroundNode away in bar.Edges.OfType<GroundEdge>().Where(e => !e.IsRunwayCenterline).Select(e => e.OtherNode(bar)))
                    {
                        double angle = Math.Abs(GeoMath.SignedBearingDifference(axisDeg, GeoMath.BearingTo(bar.Position, away.Position)));
                        angle = Math.Min(angle, 180.0 - angle);
                        bool leadsAway = CentrelineFt(runway, away.Position) > CentrelineFt(runway, bar.Position) + 20.0;
                        if (leadsAway && (angle >= 15.0) && ((best is null) || (angle < best.Value.AngleDeg)))
                        {
                            best = (runway, bar, away, angle);
                        }
                    }
                }
            }
        }

        if (best is not { } exit)
        {
            output.WriteLine("SKIP: no KOAK or KSFO layout");
            return;
        }

        output.WriteLine($"runway {exit.Runway.Id} bar #{exit.Bar.Id} exit toward #{exit.Away.Id} at {exit.AngleDeg:F0} deg to the runway");
        Assert.True(exit.AngleDeg < 60.0, $"the most angled exit meets its runway at {exit.AngleDeg:F0} deg");
        var heading = new TrueHeading(GeoMath.BearingTo(exit.Bar.Position, exit.Away.Position));
        double halfLengthFt = AircraftLength.ResolveFt("B744") / 2.0;
        AircraftState wideBody = KoakFollowGeometry.Spawn(
            "N744",
            "B744",
            GeoMath.ProjectPoint(exit.Bar.Position, heading, (halfLengthFt + 10.0) / GeoMath.FeetPerNm),
            heading
        );
        Assert.False(FollowingPhase.IsClearPastBar(wideBody, exit.Runway, exit.Bar), "the wide-body was called clear with a wingtip inside the line");
        wideBody.Position = GeoMath.ProjectPoint(exit.Bar.Position, heading, (halfLengthFt + 250.0) / GeoMath.FeetPerNm);
        Assert.True(FollowingPhase.IsClearPastBar(wideBody, exit.Runway, exit.Bar), "the wide-body was never called clear");
    }

    /// <summary>
    /// At every hold-short bar of a KOAK or KSFO runway near one of the runway's ends, a point 15 ft short of the bar toward the
    /// runway is inside that runway's hold line, and a point 15 ft past it away from the runway is not.
    /// </summary>
    [Fact]
    public void IsInsideHoldLine_AtRunwayEndBars_MatchesTheHoldLine()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        List<string> wrong = [];
        int checkedBars = 0;
        foreach (string airport in new[] { "OAK", "SFO" })
        {
            if (groundData.GetLayout(airport) is not { } layout)
            {
                continue;
            }

            foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(layout.AirportId))
            {
                LatLon[] ends = [new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2)];
                foreach (
                    GroundNode bar in RunwayBarsOf(layout, runway)
                        .Where(b => ends.Min(e => GeoMath.DistanceNm(e, b.Position)) * GeoMath.FeetPerNm < 600.0)
                )
                {
                    List<GroundNode> neighbours = [.. bar.Edges.OfType<GroundEdge>().Select(e => e.OtherNode(bar))];
                    if (neighbours.Count < 2)
                    {
                        continue;
                    }

                    GroundNode toward = neighbours.MinBy(n => CentrelineFt(runway, n.Position))!;
                    GroundNode away = neighbours.MaxBy(n => CentrelineFt(runway, n.Position))!;
                    LatLon shortOf = GeoMath.ProjectPoint(
                        bar.Position,
                        new TrueHeading(GeoMath.BearingTo(bar.Position, toward.Position)),
                        15.0 / GeoMath.FeetPerNm
                    );
                    LatLon pastIt = GeoMath.ProjectPoint(
                        bar.Position,
                        new TrueHeading(GeoMath.BearingTo(bar.Position, away.Position)),
                        15.0 / GeoMath.FeetPerNm
                    );
                    checkedBars++;
                    if (!FollowingPhase.IsInsideHoldLine(layout, runway, shortOf))
                    {
                        wrong.Add($"{airport} {runway.Id} bar #{bar.Id}: 15 ft short of it toward #{toward.Id} read outside the hold line");
                    }

                    if (FollowingPhase.IsInsideHoldLine(layout, runway, pastIt))
                    {
                        wrong.Add($"{airport} {runway.Id} bar #{bar.Id}: 15 ft past it toward #{away.Id} read inside the hold line");
                    }
                }
            }
        }

        output.WriteLine($"{checkedBars} runway-end bars checked");
        Assert.True(checkedBars > 0, "no runway-end bar found");
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    private static IEnumerable<GroundNode> RunwayBarsOf(AirportGroundLayout layout, RunwayInfo runway) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Overlaps(runway.Id));

    /// <summary>The most segments a chain follower's route onto the middle aircraft's trail along B may run: a few B edges.</summary>
    private const int ShortChainRouteSegments = 8;

    /// <summary>How long (s) the clearing follow is played.</summary>
    private const int ClearingBudgetSeconds = 120;

    /// <summary>
    /// One second of the clearing follow: the follower's speed, whether it was driving a clearing route, and whether its tail and
    /// wingtips were past the far bar's hold line (<see cref="FollowingPhase.IsClearPastBar"/>), and the runway whose hold line
    /// some part of it was inside, if any (<see cref="FollowingPhase.RunwayInsideHoldLine"/>).
    /// </summary>
    private sealed record ClearingSecond(int Second, double SpeedKts, bool Clearing, bool PastBar, RunwayIdentifier? InsideHoldLineOf);

    private sealed record ClearingRun(
        SimulationEngine Engine,
        AircraftState Follower,
        GroundNode FarBar,
        RunwayInfo Runway,
        List<ClearingSecond> Seconds
    );

    /// <summary>
    /// A C172 follower 0.2 along B's last edge across 28R into the far (south) bar, behind a C172 lead held 0.9 along it, both
    /// facing the bar; the follower is told to follow, plans on the first second (its route ends at the far bar node), and the
    /// lead is then moved off the field, so no plan joins it again, or deleted (<paramref name="deleteLead"/>). Ticked
    /// <paramref name="seconds"/> seconds in all. With <paramref name="leadAheadAfter"/>, the lead is put back on the field after
    /// that second's tick, mid-way along the straightest taxiway edge on past the follower's clearing route
    /// (<see cref="PutLeadAheadOfTheClearing"/>), so a plan joins it again.
    /// </summary>
    private ClearingRun? RunRouteOutInsideHoldLine(int seconds, bool deleteLead, int? leadAheadAfter)
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return null;
        }

        GroundNode nearBar = KoakFollowGeometry.BChain(setup.Layout)[0];
        GroundNode farBar = TestLayoutNodes
            .RunwayHoldShortsOnTaxiway(setup.Layout, "28R", "B")
            .Where(n => n.Id != nearBar.Id)
            .MinBy(n => GeoMath.DistanceNm(n.Position, nearBar.Position))!;
        return RunClearingAcross(
            setup,
            new ClearingCase(Runway28R(setup.Layout), nearBar, farBar, "C172", WithAssignedRoute: false),
            seconds,
            deleteLead,
            leadAheadAfter
        );
    }

    /// <summary>
    /// A runway, a hold-short bar of it, the bar across the runway from that one, the follower's aircraft type, and whether the
    /// follower is given an assigned route before the run (<see cref="IslandRouteAhead"/>).
    /// </summary>
    private sealed record ClearingCase(RunwayInfo Runway, GroundNode NearBar, GroundNode FarBar, string FollowerType, bool WithAssignedRoute);

    /// <summary>
    /// <see cref="RunRouteOutInsideHoldLine"/>'s clearing follow across <paramref name="bars"/>' runway, from its near bar to its
    /// far bar, on <paramref name="setup"/>'s engine.
    /// </summary>
    private ClearingRun RunClearingAcross(
        (SimulationEngine Engine, AirportGroundLayout Layout) setup,
        ClearingCase bars,
        int seconds,
        bool deleteLead,
        int? leadAheadAfter
    )
    {
        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        (RunwayInfo runway, GroundNode nearBar, GroundNode farBar, string followerType, bool withAssignedRoute) = bars;
        AircraftCategory category = AircraftCategorization.Categorize(followerType);
        WakeTurbulenceData.WakeClass wake = WakeTurbulenceData.WakeClassForType(followerType, category);
        GoalRoute? across = TaxiPathfinder.FindRouteToNearestGoal(layout, nearBar.Id, new HashSet<int> { farBar.Id }, category, wake, null);
        List<TaxiRouteSegment> crossing = Assert.IsType<TaxiRoute>(across?.Route).Segments;
        DirectionalEdge lastEdge = crossing[^1].Edge;
        DirectionalEdge followerEdge = crossing.Select(s => s.Edge).Last(e => LastFractionOnPavement(e, runway) >= 0.0);
        double followerFraction = LastFractionOnPavement(followerEdge, runway);
        output.WriteLine(
            $"crossing {string.Join(" ", crossing.Select(s => $"#{s.FromNodeId}>#{s.ToNodeId}"))}; follower {followerFraction:F2} along "
                + $"#{followerEdge.FromNodeId}>#{followerEdge.ToNodeId}, lead on #{lastEdge.FromNodeId}>#{lastEdge.ToNodeId}"
        );
        AircraftState lead = KoakFollowGeometry.Spawn(
            LeadCallsign,
            "C172",
            KoakFollowGeometry.Between(lastEdge.FromNode.Position, lastEdge.ToNode.Position, 0.95),
            KoakFollowGeometry.Facing(lastEdge.FromNode, lastEdge.ToNode)
        );
        lead.Ground.Layout = layout;
        engine.World.AddAircraft(lead);
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            followerType,
            KoakFollowGeometry.Between(followerEdge.FromNode.Position, followerEdge.ToNode.Position, followerFraction),
            KoakFollowGeometry.Facing(followerEdge.FromNode, followerEdge.ToNode)
        );
        follower.Ground.Layout = layout;
        engine.World.AddAircraft(follower);
        Assert.True(RunwayOccupancy.IsOnPavement(follower, runway), $"the follower does not start on {runway.Id}'s pavement");
        if (withAssignedRoute)
        {
            follower.Ground.AssignedTaxiRoute = IslandRouteAhead(layout, follower);
            output.WriteLine($"follower assigned {follower.Ground.AssignedTaxiRoute.ToSummary()}");
        }

        CommandResult result = engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);

        engine.TickOneSecond();
        Assert.NotNull(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).FollowRoute);
        if (deleteLead)
        {
            engine.World.RemoveAircraft(LeadCallsign);
        }
        else
        {
            lead.Position = new LatLon(lead.Position.Lat + 0.05, lead.Position.Lon);
        }

        List<ClearingSecond> log =
        [
            new ClearingSecond(
                1,
                follower.GroundSpeed,
                false,
                FollowingPhase.IsClearPastBar(follower, runway, farBar),
                FollowingPhase.RunwayInsideHoldLine(follower, layout)?.Id
            ),
        ];
        for (int second = 2; second <= seconds; second++)
        {
            engine.TickOneSecond();
            var follow = follower.Phases?.CurrentPhase as FollowingPhase;
            log.Add(
                new ClearingSecond(
                    second,
                    follower.GroundSpeed,
                    follow?.ClearingRoute is not null,
                    FollowingPhase.IsClearPastBar(follower, runway, farBar),
                    FollowingPhase.RunwayInsideHoldLine(follower, layout)?.Id
                )
            );
            output.WriteLine(
                $"t={second} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2} follow={follow?.FollowRoute is not null} "
                    + $"clearing={follow?.ClearingRoute is not null} "
                    + $"toBar={GeoMath.DistanceNm(follower.Position, farBar.Position) * GeoMath.FeetPerNm:F0}"
            );
            if (second == leadAheadAfter)
            {
                PutLeadAheadOfTheClearing(lead, follower);
            }
        }

        return new ClearingRun(engine, follower, farBar, runway, log);
    }

    /// <summary>
    /// Puts <paramref name="lead"/> back on the field mid-way along the straightest taxiway edge on past the end of
    /// <paramref name="follower"/>'s clearing route, facing away from it, with that edge on its trail.
    /// </summary>
    private void PutLeadAheadOfTheClearing(AircraftState lead, AircraftState follower)
    {
        TaxiRoute clearing = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).ClearingRoute);
        DirectionalEdge last = clearing.Segments[^1].Edge;
        GroundNode end = last.ToNode;
        GroundEdge? onward = end
            .Edges.OfType<GroundEdge>()
            .Where(e => !e.IsRunwayCenterline && (e.OtherNode(end).Id != last.FromNodeId))
            .MinBy(e => Math.Abs(GeoMath.SignedBearingDifference(last.ArrivalBearing, GeoMath.BearingTo(end.Position, e.OtherNode(end).Position))));
        Assert.True(onward is not null, $"no taxiway edge leads on past the clearing route's end node #{end.Id}");
        GroundNode far = onward.OtherNode(end);
        lead.Position = KoakFollowGeometry.Between(end.Position, far.Position, 0.5);
        lead.TrueHeading = KoakFollowGeometry.Facing(end, far);
        lead.Ground.TaxiEdgeTrail.Record(onward);
        output.WriteLine($"lead put back mid-way along {onward.TaxiwayName} #{end.Id}>#{far.Id}");
    }

    /// <summary>The follower's tail and the far bar, as distances (ft) from 28R's centreline, and whether they are on the same side of it.</summary>
    private static (double TailFt, double BarFt, bool SameSide) TailAgainstBar(ClearingRun run)
    {
        AircraftState follower = run.Follower;
        LatLon tail = GeoMath.ProjectPoint(
            follower.Position,
            new TrueHeading((follower.TrueHeading.Degrees + 180.0) % 360.0),
            AircraftLength.ResolveFt(follower.AircraftType) / 2.0 / GeoMath.FeetPerNm
        );
        var end1 = new LatLon(run.Runway.Lat1, run.Runway.Lon1);
        var end2 = new LatLon(run.Runway.Lat2, run.Runway.Lon2);
        double alongDeg = GeoMath.BearingTo(end1, end2);
        bool tailRight = GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, tail)) > 0.0;
        bool barRight = GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, run.FarBar.Position)) > 0.0;
        return (GeoMath.DistanceToSegmentFt(tail, end1, end2), GeoMath.DistanceToSegmentFt(run.FarBar.Position, end1, end2), tailRight == barRight);
    }

    private static double CentrelineFt(RunwayInfo runway, LatLon point) =>
        GeoMath.DistanceToSegmentFt(point, new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));

    private static RunwayInfo Runway28R(AirportGroundLayout layout) =>
        RunwayOccupancy.AirportRunways(layout.AirportId).First(r => r.Id.Overlaps(RunwayIdentifier.Parse("28R")));

    /// <summary>How far (ft) past a runway end, along its extended centreline, the off-the-end pose is tried: inboard first.</summary>
    private static readonly double[] OffTheEndOffsetsFt = [-60.0, 0.0, 30.0, 60.0, 90.0];

    /// <summary>
    /// A pose on 28R's pavement near one of its ends, on its extended centreline and facing off that end, with no 28R hold-short
    /// bar within 90° of the heading as seen from the tail of a C172 there.
    /// </summary>
    private static (LatLon Position, TrueHeading Heading) OffTheEndPose(AirportGroundLayout layout, RunwayInfo runway)
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        List<GroundNode> bars =
        [
            .. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Overlaps(runway.Id)),
        ];
        double halfLengthNm = AircraftLength.ResolveFt("C172") / 2.0 / GeoMath.FeetPerNm;
        foreach ((LatLon end, LatLon other) in new[] { (end1, end2), (end2, end1) })
        {
            var heading = new TrueHeading(GeoMath.BearingTo(other, end));
            var back = new TrueHeading(GeoMath.BearingTo(end, other));
            foreach (double offsetFt in OffTheEndOffsetsFt)
            {
                LatLon position = GeoMath.ProjectPoint(end, heading, offsetFt / GeoMath.FeetPerNm);
                LatLon tail = GeoMath.ProjectPoint(position, back, halfLengthNm);
                bool barAhead = bars.Any(b =>
                    Math.Abs(GeoMath.SignedBearingDifference(heading.Degrees, GeoMath.BearingTo(tail, b.Position))) <= 90.0
                );
                if (RunwayOccupancy.IsWithinPavement(position, runway) && !barAhead)
                {
                    return (position, heading);
                }
            }
        }

        Assert.Fail("every end of 28R has a hold-short bar ahead of an aircraft on its pavement facing off it");
        return default;
    }

    /// <summary>
    /// The largest fraction along <paramref name="edge"/>, short of 0.85, whose point lies on <paramref name="runway"/>'s
    /// pavement; negative when none does.
    /// </summary>
    private static double LastFractionOnPavement(DirectionalEdge edge, RunwayInfo runway)
    {
        double best = -1.0;
        for (int step = 0; step <= 85; step++)
        {
            double fraction = step / 100.0;
            best = RunwayOccupancy.IsWithinPavement(KoakFollowGeometry.Between(edge.FromNode.Position, edge.ToNode.Position, fraction), runway)
                ? fraction
                : best;
        }

        return best;
    }

    private const int ChainBudgetSeconds = 180;

    private const string MiddleCallsign = "N3MID";

    /// <summary>
    /// The second the last aircraft of the chain is told to follow: once the middle one is rolling along B, its tail past where
    /// the follower joins.
    /// </summary>
    private const int ChainFollowerStartSecond = 20;

    /// <summary>
    /// One second of the chained follow: the follower's speed, whether it has a follow route, and whether a new one replaced the
    /// last.
    /// </summary>
    private sealed record ChainSecond(int Second, double SpeedKts, bool HasRoute, bool RouteReplaced);

    /// <summary>How far along its B edge toward its lead the follower of a route-end or chained follow starts, one run per fraction.</summary>
    private static readonly double[] ChainStartFractions = [0.0, 0.1, 0.2, 0.3, 0.4];

    private sealed record ChainRun(SimulationEngine Engine, AircraftState Follower, double StartFraction, List<ChainSecond> Seconds);

    /// <summary>
    /// A chained follow on KOAK's B: a C560 taxiing toward 28R (<see cref="KoakFollowGeometry.StartTaxiingLead"/>), a C172 behind
    /// it at <c>Chain[4]</c> following it across 28R, and a C172 behind that, <paramref name="startFraction"/> along the straight B
    /// edge from <c>Chain[5]</c> to the middle one,
    /// told at <see cref="ChainFollowerStartSecond"/> to follow the middle one across 28R too. The middle aircraft's taxi route
    /// predates its <c>FOLLOWG</c> and is never read: the last follower's lead path is the middle aircraft's trail, the edge it is
    /// on, and then its own follow route. Ticked <paramref name="seconds"/> seconds; <c>Seconds[0]</c> is the pose before the first.
    /// </summary>
    private ChainRun? RunChain(int seconds, double startFraction)
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return null;
        }

        // The follower's start is interpolated straight between two chain nodes, so the edge joining them must be straight: B
        // leaves Chain[5] for Chain[6] by a fillet arc, whose chord runs over taxiway C's edge beside it.
        Assert.Contains(run.Chain[5].Edges.OfType<GroundEdge>(), e => e.OtherNode(run.Chain[5]).Id == run.Chain[4].Id);
        AircraftState middle = KoakFollowGeometry.Spawn(
            MiddleCallsign,
            "C172",
            run.Chain[4].Position,
            KoakFollowGeometry.Facing(run.Chain[4], run.Chain[3])
        );
        middle.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(middle);
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            KoakFollowGeometry.Between(run.Chain[5].Position, run.Chain[4].Position, startFraction),
            KoakFollowGeometry.Facing(run.Chain[5], run.Chain[4])
        );
        follower.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(follower);
        CommandResult middleFollow = run.Engine.SendCommand(MiddleCallsign, $"FOLLOWG {run.Lead.Callsign}; CROSS 28R");
        Assert.True(middleFollow.Success, middleFollow.Message);

        List<ChainSecond> log = PlayFollower(
            run.Engine,
            follower,
            seconds,
            (second, _) =>
            {
                if (second == ChainFollowerStartSecond)
                {
                    CommandResult followResult = run.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {MiddleCallsign}; CROSS 28R");
                    Assert.True(followResult.Success, followResult.Message);
                }
            }
        );
        return new ChainRun(run.Engine, follower, startFraction, log);
    }

    /// <summary>How many segments of its <c>TAXI B W 30</c> the route-end lead is first given.</summary>
    private const int ShortLeadRouteSegments = 4;

    /// <summary>
    /// A follow route that runs out and is planned again, on KOAK's B outside every runway's hold lines: a C560 half-way along
    /// the first B edge of a clear stretch of the <c>TAXI B W 30</c> route, cleared <c>TAXI B W 30</c> and then held to the first
    /// <see cref="ShortLeadRouteSegments"/> segments of it, and a C172 <paramref name="startFraction"/> along the B edge behind
    /// it, told to follow it. Once the follower has planned — its route ending where the lead's short route ends — and the lead
    /// is on its last segment, the lead is cleared <c>TAXI B W 30</c> again, so the follower's route runs out at the old end and is
    /// planned again onto the lead's new route. Ticked <paramref name="seconds"/> seconds; <c>Seconds[0]</c> is the pose before
    /// the first.
    /// </summary>
    private ChainRun? RunRouteEnd(int seconds, double startFraction)
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: true) is not { } setup)
        {
            return null;
        }

        List<GroundNode> chain = KoakFollowGeometry.BChain(setup.Layout);
        AircraftState scout = KoakFollowGeometry.AddTaxiing(setup, "N9SCT", "C560", (chain[3], chain[2]), "TAXI B W 30");
        List<TaxiRouteSegment> toThirty = Assert.IsType<TaxiRoute>(scout.Ground.AssignedTaxiRoute).Segments;
        setup.Engine.World.RemoveAircraft(scout.Callsign);
        List<RunwayInfo> runways = [.. RunwayOccupancy.AirportRunways(setup.Layout.AirportId)];
        bool OnClearB(TaxiRouteSegment s) =>
            (s.TaxiwayName == "B")
            && (s.Edge.Edge is GroundEdge)
            && runways.All(r =>
                !RunwayOccupancy.IsWithinPavement(s.Edge.FromNode.Position, r)
                && !FollowingPhase.IsInsideHoldLine(setup.Layout, r, s.Edge.FromNode.Position)
                && !FollowingPhase.IsInsideHoldLine(setup.Layout, r, s.Edge.ToNode.Position)
            );
        output.WriteLine(
            "TAXI B W 30: "
                + string.Join(
                    " ",
                    toThirty.Select((s, i) => $"{i}:{s.TaxiwayName}{(s.Edge.Edge is GroundEdge ? "" : "~")}{(OnClearB(s) ? "+" : "")}")
                )
        );
        int start = Enumerable
            .Range(2, toThirty.Count - ShortLeadRouteSegments - 3)
            .First(i => toThirty.Skip(i - 2).Take(ShortLeadRouteSegments + 3).All(OnClearB));
        DirectionalEdge behind = toThirty[start - 1].Edge;
        DirectionalEdge leadEdge = toThirty[start].Edge;

        AircraftState lead = KoakFollowGeometry.Spawn(
            LeadCallsign,
            "C560",
            KoakFollowGeometry.Between(leadEdge.FromNode.Position, leadEdge.ToNode.Position, 0.5),
            KoakFollowGeometry.Facing(leadEdge.FromNode, leadEdge.ToNode)
        );
        lead.Ground.Layout = setup.Layout;
        setup.Engine.World.AddAircraft(lead);
        CommandResult leadTaxi = setup.Engine.SendCommand(LeadCallsign, "TAXI B W 30");
        Assert.True(leadTaxi.Success, leadTaxi.Message);
        TaxiRoute full = Assert.IsType<TaxiRoute>(lead.Ground.AssignedTaxiRoute);
        lead.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [.. full.Segments.Take(ShortLeadRouteSegments)], HoldShortPoints = [] };
        output.WriteLine($"lead short route {lead.Ground.AssignedTaxiRoute.ToSummary()} of {full.ToSummary()}");
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            KoakFollowGeometry.Between(behind.FromNode.Position, behind.ToNode.Position, startFraction),
            KoakFollowGeometry.Facing(behind.FromNode, behind.ToNode)
        );
        follower.Ground.Layout = setup.Layout;
        setup.Engine.World.AddAircraft(follower);
        CommandResult follow = setup.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(follow.Success, follow.Message);

        bool sentOn = false;
        List<ChainSecond> log = PlayFollower(
            setup.Engine,
            follower,
            seconds,
            (_, followNow) =>
            {
                bool leadOnLastSegment =
                    (lead.Ground.AssignedTaxiRoute is { } leadRoute) && (leadRoute.CurrentSegmentIndex >= (leadRoute.Segments.Count - 1));
                if (!sentOn && (followNow?.FollowRoute is not null) && leadOnLastSegment)
                {
                    CommandResult onward = setup.Engine.SendCommand(LeadCallsign, "TAXI B W 30");
                    Assert.True(onward.Success, onward.Message);
                    output.WriteLine($"lead sent on: {lead.Ground.AssignedTaxiRoute?.ToSummary()}");
                    sentOn = true;
                }
            }
        );
        return new ChainRun(setup.Engine, follower, startFraction, log);
    }

    /// <summary>
    /// Ticks <paramref name="engine"/> <paramref name="seconds"/> seconds, calling <paramref name="beforeTick"/> with the second
    /// and the follower's current follow before each, and logs the follower each second. A route replaced by another in the same
    /// follow counts as replaced; a new follow after a bar hold does not.
    /// </summary>
    private List<ChainSecond> PlayFollower(SimulationEngine engine, AircraftState follower, int seconds, Action<int, FollowingPhase?> beforeTick)
    {
        List<ChainSecond> log = [new ChainSecond(0, follower.GroundSpeed, false, false)];
        TaxiRoute? previous = null;
        FollowingPhase? previousFollow = null;
        for (int second = 1; second <= seconds; second++)
        {
            beforeTick(second, follower.Phases?.CurrentPhase as FollowingPhase);
            engine.TickOneSecond();
            var follow = follower.Phases?.CurrentPhase as FollowingPhase;
            TaxiRoute? route = follow?.FollowRoute;
            bool replaced = (previous is not null) && ReferenceEquals(follow, previousFollow) && !ReferenceEquals(previous, route);
            previousFollow = follow;
            log.Add(new ChainSecond(second, follower.GroundSpeed, route is not null, replaced));
            output.WriteLine(
                $"t={second} follower {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F2} "
                    + $"seg={route?.CurrentSegmentIndex}/{route?.Segments.Count} "
                    + $"replaced={replaced}"
            );
            previous = route;
        }

        return log;
    }

    /// <summary>
    /// Plays the route-end follow (<see cref="RunRouteEnd"/>) to <paramref name="snapshotAt"/>, restores its recording-JSON
    /// snapshot into a second engine, and ticks both <see cref="RestoreCompareSeconds"/> seconds, asserting the follower's
    /// position, heading and ground speed match exactly.
    /// </summary>
    private void AssertRestoredRunMatches(int snapshotAt, double startFraction)
    {
        ChainRun original = Assert.IsType<ChainRun>(RunRouteEnd(snapshotAt, startFraction));
        StateSnapshotDto snapshot = original.Engine.CaptureSnapshot();
        string json = JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default);
        SimulationEngine restored = KoakFollowGeometry.NewEngine(output, autoCross: true)!.Value.Engine;
        restored.RestoreFromSnapshot(
            Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
        );
        AircraftState restoredFollower = Assert.IsType<AircraftState>(restored.FindAircraft(FollowerCallsign));

        for (int second = snapshotAt + 1; second <= snapshotAt + RestoreCompareSeconds; second++)
        {
            original.Engine.TickOneSecond();
            restored.TickOneSecond();
            AircraftState live = original.Follower;
            string at = $"t={second} (snapshot at t={snapshotAt})";
            Assert.True(live.Position == restoredFollower.Position, $"{at}: restored at {restoredFollower.Position}, live at {live.Position}");
            Assert.True(
                live.TrueHeading.Degrees == restoredFollower.TrueHeading.Degrees,
                $"{at}: restored heading {restoredFollower.TrueHeading.Degrees}, live {live.TrueHeading.Degrees}"
            );
            Assert.True(
                live.GroundSpeed == restoredFollower.GroundSpeed,
                $"{at}: restored ground speed {restoredFollower.GroundSpeed}, live {live.GroundSpeed}"
            );
        }
    }

    /// <summary>How long (s) the armed-crossing follow is played.</summary>
    private const int ArmedCrossingBudgetSeconds = 300;

    /// <summary>
    /// A C172 on B behind a C560 taxiing <c>TAXI B W 30</c> across 28R (<see cref="KoakFollowGeometry.StartTaxiingLead"/>), told
    /// <c>FOLLOWG</c> it with a <c>CROSS 28R</c> armed behind the follow: the follow stops at the 28R bar, the armed crossing fires
    /// there, and the follower crosses 28R behind its lead rather than holding at the bar — alone, and with a third C172 behind it
    /// at <c>Chain[5]</c> following it across too (<see cref="RunChain"/>'s pose).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Following_ArmedCrossing_CrossesBehindTheLead(bool withFollowerBehind)
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return;
        }

        if (withFollowerBehind)
        {
            AircraftState behind = KoakFollowGeometry.Spawn(
                MiddleCallsign,
                "C172",
                run.Chain[5].Position,
                KoakFollowGeometry.Facing(run.Chain[5], run.Chain[4])
            );
            behind.Ground.Layout = run.Layout;
            run.Engine.World.AddAircraft(behind);
        }

        RunwayInfo runway = Runway28R(run.Layout);
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            run.Chain[4].Position,
            KoakFollowGeometry.Facing(run.Chain[4], run.Chain[3])
        );
        follower.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(follower);
        CommandResult result = run.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {run.Lead.Callsign}; CROSS 28R");
        Assert.True(result.Success, result.Message);

        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        double alongDeg = GeoMath.BearingTo(end1, new LatLon(runway.Lat2, runway.Lon2));
        bool startSide = GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, follower.Position)) > 0.0;
        int barHoldAt = -1;
        int crossingAt = -1;
        int onRunwayAt = -1;
        int leadClearAt = -1;
        int crossedAt = -1;
        for (int second = 1; (second <= ArmedCrossingBudgetSeconds) && (crossedAt < 0); second++)
        {
            if (withFollowerBehind && (second == ChainFollowerStartSecond))
            {
                CommandResult behindResult = run.Engine.SendCommand(MiddleCallsign, $"FOLLOWG {FollowerCallsign}; CROSS 28R");
                Assert.True(behindResult.Success, behindResult.Message);
            }

            run.Engine.TickOneSecond();
            Phase? phase = follower.Phases?.CurrentPhase;
            var follow = phase as FollowingPhase;
            bool onPavement = RunwayOccupancy.IsOnPavement(follower, runway);
            bool leadOnPavement = RunwayOccupancy.IsOnPavement(run.Lead, runway);
            bool farSide = (GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, follower.Position)) > 0.0) != startSide;
            barHoldAt = (barHoldAt < 0) && (phase is HoldingShortPhase) ? second : barHoldAt;
            crossingAt = (crossingAt < 0) && (phase is CrossingRunwayPhase) ? second : crossingAt;
            onRunwayAt = (onRunwayAt < 0) && onPavement ? second : onRunwayAt;
            leadClearAt =
                (leadClearAt < 0) && (second > 1) && !leadOnPavement && (CentrelineFt(runway, run.Lead.Position) > 300.0) ? second : leadClearAt;
            crossedAt = (onRunwayAt > 0) && !onPavement && farSide ? second : -1;
            output.WriteLine(
                $"t={second} {phase?.Name} gs={follower.GroundSpeed:F1} on28R={onPavement} farSide={farSide} "
                    + $"cleared={follow?.CrossingClearedRunways.Count} "
                    + $"givingWay={follow?.IsGivingWay} seg={follow?.FollowRoute?.CurrentSegmentIndex}/{follow?.FollowRoute?.Segments.Count} "
                    + $"toBar={GeoMath.DistanceNm(follower.Position, run.Chain[0].Position) * GeoMath.FeetPerNm:F0} "
                    + $"lead gs={run.Lead.GroundSpeed:F1} "
                    + $"leadOn28R={leadOnPavement}"
            );
        }

        output.WriteLine(
            $"measured (follower behind: {withFollowerBehind}): bar hold at t={barHoldAt}, lead clear of 28R at t={leadClearAt}, "
                + $"crossing phase at t={crossingAt}, on 28R at t={onRunwayAt}, crossed at t={crossedAt}"
        );
        Assert.True(barHoldAt > 0, "the follower never stopped at the 28R bar");
        Assert.True(
            crossedAt > 0,
            $"the follower never crossed 28R within {ArmedCrossingBudgetSeconds}s; last phase {follower.Phases?.CurrentPhase?.Name}"
        );
    }

    private sealed record MergeRun(SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Lead, AircraftState Follower);

    /// <summary>
    /// A C560 taxiing B toward 28R (<see cref="KoakFollowGeometry.StartTaxiingLead"/>) and a C172 on its route ahead of it,
    /// put straight into a follow of the lead — <c>FOLLOWG</c> itself rejects a follower ahead — whose plan is
    /// <see cref="FollowRoutePlan.FollowerAhead"/>.
    /// </summary>
    private MergeRun? StartFollowerAhead()
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return null;
        }

        AircraftState follower = KoakFollowGeometry.SpawnOnRouteAhead(run.Lead, FollowerCallsign, "C172");
        follower.Ground.Layout = run.Layout;
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(run.Lead.Callsign));
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower, run.Layout));
        run.Engine.World.AddAircraft(follower);
        Assert.IsType<FollowRoutePlan.FollowerAhead>(FollowRoutePlanner.Plan(run.Layout, follower, run.Lead));
        return new MergeRun(run.Engine, run.Layout, run.Lead, follower);
    }

    /// <summary>
    /// A C560 lead mid-way along a straight taxiway edge of the first committed layout with a disconnected island
    /// (<see cref="GraphComponents.FindDisconnectedPair"/>), and a C172 mid-way along a straight taxiway edge of the island, clear
    /// of every runway's hold lines, put straight into a follow of the lead — <c>FOLLOWG</c> itself rejects a follow with no
    /// taxi path — whose plan is <see cref="FollowRoutePlan.NoPath"/>. With <paramref name="soloOnGround"/> the room is a solo
    /// session with the student on ground. Null when no committed layout has an island.
    /// </summary>
    private MergeRun? StartFollowerOnIsland(bool soloOnGround)
    {
        TestVnasData.EnsureInitialized();
        if (GraphComponents.FindDisconnectedPair() is not { } split)
        {
            output.WriteLine("SKIP: no committed layout has a disconnected island");
            return null;
        }

        AirportGroundLayout layout = split.Layout;
        List<RunwayInfo> runways = [.. RunwayOccupancy.AirportRunways(layout.AirportId)];
        bool ClearOfRunways(GroundEdge e) => e.Nodes.All(n => runways.All(r => !FollowingPhase.IsInsideHoldLine(layout, r, n.Position)));
        HashSet<int> island = [.. split.Island.Select(n => n.Id)];
        GroundEdge islandEdge = split
            .Island.SelectMany(n => n.Edges.OfType<GroundEdge>())
            .First(e => !e.IsRamp && !e.IsRunwayCenterline && e.Nodes.All(n => island.Contains(n.Id)) && ClearOfRunways(e));
        HashSet<int> main = GraphComponents.ComponentOf(split.MainNode);
        GroundEdge mainEdge = layout
            .Nodes.Values.Where(n => main.Contains(n.Id))
            .SelectMany(n => n.Edges.OfType<GroundEdge>())
            .First(e => !e.IsRamp && !e.IsRunwayCenterline && !string.IsNullOrEmpty(e.TaxiwayName) && ClearOfRunways(e));
        output.WriteLine(
            $"{layout.AirportId}: island edge {islandEdge.TaxiwayName} #{islandEdge.Nodes[0].Id}-#{islandEdge.Nodes[1].Id}, "
                + $"lead edge {mainEdge.TaxiwayName} #{mainEdge.Nodes[0].Id}-#{mainEdge.Nodes[1].Id}"
        );

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-follow-island",
                ScenarioName = "Follow from an island",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = layout.AirportId,
                SoloTrainingMode = soloOnGround,
                StudentPositionType = soloOnGround ? "GND" : null,
            },
        };
        AircraftState lead = KoakFollowGeometry.Spawn(
            LeadCallsign,
            "C560",
            KoakFollowGeometry.Between(mainEdge.Nodes[0].Position, mainEdge.Nodes[1].Position, 0.5),
            KoakFollowGeometry.Facing(mainEdge.Nodes[0], mainEdge.Nodes[1])
        );
        lead.Ground.Layout = layout;
        engine.World.AddAircraft(lead);
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            KoakFollowGeometry.Between(islandEdge.Nodes[0].Position, islandEdge.Nodes[1].Position, 0.5),
            KoakFollowGeometry.Facing(islandEdge.Nodes[0], islandEdge.Nodes[1])
        );
        follower.Ground.Layout = layout;
        follower.Phases = new PhaseList();
        follower.Phases.Add(new FollowingPhase(LeadCallsign));
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower, layout));
        engine.World.AddAircraft(follower);
        Assert.IsType<FollowRoutePlan.NoPath>(FollowRoutePlanner.Plan(layout, follower, lead));
        return new MergeRun(engine, layout, lead, follower);
    }

    /// <summary>
    /// The planner's merge-ahead pose: a C560 taxiing B toward 28R (<see cref="KoakFollowGeometry.StartTaxiingLead"/>) and a
    /// C172 at the far end of a taxiway crossing B at a junction three or more segments ahead of the lead, facing the junction,
    /// told <c>FOLLOWG</c> the lead and ticked once so the follow has planned.
    /// </summary>
    private MergeRun? StartMergeAhead()
    {
        if (KoakFollowGeometry.StartTaxiingLead(output) is not { } run)
        {
            return null;
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(run.Lead.Ground.AssignedTaxiRoute);
        HashSet<int> routeNodes = [.. route.Segments.SelectMany(s => new[] { s.FromNodeId, s.ToNodeId })];
        (GroundNode junction, GroundNode far) = route
            .Segments.Skip(route.CurrentSegmentIndex + 3)
            .Select(s => s.Edge.FromNode)
            .SelectMany(node => node.Edges.OfType<GroundEdge>().Select(edge => (Junction: node, Far: edge.OtherNode(node), Edge: edge)))
            .Where(c =>
                !c.Edge.IsRunwayCenterline && !c.Edge.IsRamp && !routeNodes.Contains(c.Far.Id) && (c.Edge.DistanceNm * GeoMath.FeetPerNm >= 50.0)
            )
            .Select(c => (c.Junction, c.Far))
            .First();
        output.WriteLine($"junction #{junction.Id} on B, follower at #{far.Id}");
        AircraftState follower = KoakFollowGeometry.Spawn(FollowerCallsign, "C172", far.Position, KoakFollowGeometry.Facing(far, junction));
        follower.Ground.Layout = run.Layout;
        run.Engine.World.AddAircraft(follower);
        CommandResult result = run.Engine.SendCommand(FollowerCallsign, $"FOLLOWG {run.Lead.Callsign}");
        Assert.True(result.Success, result.Message);
        run.Engine.TickOneSecond();
        return new MergeRun(run.Engine, run.Layout, run.Lead, follower);
    }

    /// <summary>
    /// A C172 lead held 0.9 along KOAK's longest straight B edge and a C172 follower 0.1 along it, facing the lead or away; the
    /// follower is told to follow and must stop behind the lead at the stop gap, never giving way, its route starting on the
    /// shared edge.
    /// </summary>
    private void FollowBehindOnLeadsEdge(bool followerFacesLead)
    {
        if (KoakFollowGeometry.NewEngine(output, autoCross: false) is not { } setup)
        {
            return;
        }

        (SimulationEngine engine, AirportGroundLayout layout) = setup;
        GroundEdge shared = layout
            .Nodes.Values.SelectMany(n => n.Edges.OfType<GroundEdge>())
            .Where(e => e.MatchesTaxiway("B") && !e.IsRunwayCenterline && !e.IsRamp)
            .MaxBy(e => e.DistanceNm)!;
        GroundNode from = shared.Nodes[0];
        GroundNode to = shared.Nodes[1];
        GroundEdge before = from.Edges.OfType<GroundEdge>().First(e => (e != shared) && !e.IsRunwayCenterline && !e.IsRamp);
        output.WriteLine($"shared edge #{from.Id}>#{to.Id}, {shared.DistanceNm * GeoMath.FeetPerNm:F0} ft");

        AircraftState lead = KoakFollowGeometry.Spawn(
            LeadCallsign,
            "C172",
            KoakFollowGeometry.Between(from.Position, to.Position, 0.9),
            KoakFollowGeometry.Facing(from, to)
        );
        lead.Ground.Layout = layout;
        lead.Ground.TaxiEdgeTrail.Record(before);
        lead.Ground.TaxiEdgeTrail.Record(shared);
        engine.World.AddAircraft(lead);
        AircraftState follower = KoakFollowGeometry.Spawn(
            FollowerCallsign,
            "C172",
            KoakFollowGeometry.Between(from.Position, to.Position, 0.1),
            followerFacesLead ? KoakFollowGeometry.Facing(from, to) : KoakFollowGeometry.Facing(to, from)
        );
        follower.Ground.Layout = layout;
        engine.World.AddAircraft(follower);
        CommandResult result = engine.SendCommand(FollowerCallsign, $"FOLLOWG {LeadCallsign}");
        Assert.True(result.Success, result.Message);

        double stopGapFt = FollowGap.StopGapFt("C172", AircraftCategory.Piston, "C172", AircraftCategory.Piston);
        double startGapFt = FollowingPhase.NoseToTailFt(follower, lead);
        bool everGaveWay = false;
        int stoppedSince = -1;
        int settledAt = -1;
        for (int second = 1; (second <= BudgetSeconds) && (settledAt < 0); second++)
        {
            engine.TickOneSecond();
            FollowingPhase follow = Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
            everGaveWay |= follow.IsGivingWay;
            if (second == 1)
            {
                TaxiRoute route = Assert.IsType<TaxiRoute>(follow.FollowRoute);
                Assert.Equal(0, follow.MergeSegmentIndex);
                Assert.Equal((from.Id, to.Id), (route.Segments[0].FromNodeId, route.Segments[0].ToNodeId));
            }

            double gapFt = FollowingPhase.NoseToTailFt(follower, lead);
            output.WriteLine($"t={second} gs={follower.GroundSpeed:F1} hdg={follower.TrueHeading.Degrees:F0} noseToTail={gapFt:F1}");
            stoppedSince = (follower.GroundSpeed < 0.05) && (gapFt < startGapFt - 50.0) ? (stoppedSince < 0 ? second : stoppedSince) : -1;
            settledAt = (stoppedSince > 0) && (second - stoppedSince >= 5) ? second : -1;
        }

        Assert.True(settledAt > 0, $"the follower never closed up behind the lead within {BudgetSeconds}s");
        Assert.False(everGaveWay, "the follower gave way on the lead's own edge");
        double alongDeg = Math.Abs(GeoMath.SignedBearingDifference(follower.TrueHeading.Degrees, KoakFollowGeometry.Facing(from, to).Degrees));
        Assert.True(alongDeg <= 15.0, $"the follower ended {alongDeg:F0} deg off the lead's direction");
        double settledGapFt = FollowingPhase.NoseToTailFt(follower, lead);
        Assert.True(
            (settledGapFt >= stopGapFt - 4.0) && (settledGapFt <= stopGapFt + 10.0),
            $"the follower stopped with its nose {settledGapFt:F1} ft from the lead's tail; expected {stopGapFt:F0} to {stopGapFt + 10.0:F0} ft"
        );
    }
}
