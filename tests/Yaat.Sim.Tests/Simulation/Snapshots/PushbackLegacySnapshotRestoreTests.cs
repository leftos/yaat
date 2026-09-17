using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// A pushback snapshot written before tug moves existed — the pre-rework <see cref="PushbackPhaseDto"/> JSON, which
/// carries a target, a heading, a pull-forward point and a recorded start but no move — restores through
/// <see cref="PhaseList"/> as the equivalent <see cref="TugMove"/> and finishes on the real SFO ramp off gate D15:
/// a push with a target and a heading captures the heading's line onto the target; one still owing the pull
/// forward onto a spot drops it with a Warning and stops at the staging point; a heading alone turns onto it; and a
/// restored move that has not ticked yet re-snapshots into JSON that restores to the same move and the same end.
/// The pre-#233 spot pushback (<see cref="PushbackToSpotPhaseDto"/>, the multi-segment reverse whose phase is gone)
/// restores through the same mapping, collapsed to one move onto the spot its route ended on.
/// A missing SFO layout silently skips, the repo's convention for absent test data.
/// </summary>
public class PushbackLegacySnapshotRestoreTests(ITestOutputHelper output)
{
    private const string AircraftType = "B738";
    private const string Gate = "D15";
    private const string AlleySpot = "6A";

    /// <summary>The taxiway the pre-#233 route's segments were named with in the bundle this shape comes from.</summary>
    private const string LegacyRouteTaxiway = "T7A";

    /// <summary>Tick budget for one restored move.</summary>
    private const int BudgetSeconds = 180;

    /// <summary>How far short of its target, along its line, a restored mid-push starts.</summary>
    private const double RunUpFt = 60.0;

    /// <summary>How far a pre-rework straight push had already gone from the stand when it was snapshotted.</summary>
    private const double PushedSoFarFt = 20.0;

    /// <summary>How far a pre-rework heading-only push turns the nose from the stand heading.</summary>
    private const int HeadingOnlyTurnDeg = 90;

    /// <summary>How close to its target a restored move must finish.</summary>
    private const double NearTargetFt = 3.0;

    /// <summary>How close to its heading a restored move's nose must finish.</summary>
    private const double NearHeadingDeg = 1.0;

    /// <summary>The pre-rework JSON value of an active phase's <c>Status</c>.</summary>
    private const int ActiveStatus = (int)PhaseStatus.Active;

    /// <summary>
    /// A push on its way to the 6A staging point with 6A's nose-out as its heading, 60 ft short of it, restores as a
    /// push capturing the line through the staging point along that heading, and stops on the staging point with
    /// the nose on the heading.
    /// </summary>
    [Fact]
    public void TargetedWithHeading_RestoresAsALineCaptureOntoTheTarget()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var spot = SpotApproach(ground.Layout);
        var list = Restore(ground.Layout, LegacyPush(GateNode(ground.Layout).Position, spot.Heading, spot.Staging, pullForward: null));

        var phase = Assert.IsType<PushbackPhase>(list.CurrentPhase);
        Assert.Equal(PushbackLegKind.Push, phase.Kind);
        Assert.Equal(TugMoveShape.ViaLine, phase.Move.Shape);
        Assert.Equal(spot.Staging, phase.Move.StopAt);
        Assert.Equal(spot.Staging, phase.PlannedEnd);

        var ac = Fly(ground, "LEG1", spot.Node, spot.RunUp, pushing: true, list);

        AssertEndsNear(ac, spot.Staging, spot.Heading);
    }

    /// <summary>
    /// The same push still owing its pull forward onto the 6A mark: the restore logs a Warning that the pull forward
    /// is dropped, and the aircraft stops at the staging point rather than going on to the mark.
    /// </summary>
    [Fact]
    public void PendingPullForward_DroppedWithAWarning_StopsAtTheStagingPoint()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var spot = SpotApproach(ground.Layout);
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        var list = Restore(ground.Layout, LegacyPush(GateNode(ground.Layout).Position, spot.Heading, spot.Staging, spot.Stop));

        var warnings = tap.Drain().Where(r => (r.Level == LogLevel.Warning) && (r.Category == "PushbackPhase")).ToList();
        warnings.ForEach(w => output.WriteLine($"warning: {w.Message}"));
        Assert.Contains(warnings, w => w.Message.Contains("the pull forward onto the spot is dropped", StringComparison.Ordinal));
        var phase = Assert.IsType<PushbackPhase>(list.CurrentPhase);
        Assert.Equal(spot.Staging, phase.PlannedEnd);

        var ac = Fly(ground, "LEG2", spot.Node, spot.RunUp, pushing: true, list);

        AssertEndsNear(ac, spot.Staging, spot.Heading);
    }

    /// <summary>
    /// A heading-only push on gate D15, snapshotted before it had moved, restores as a push turning onto the heading
    /// and ends with the nose on it.
    /// </summary>
    [Fact]
    public void HeadingOnly_RestoresAsATurnOntoTheHeading()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var stand = GateNode(ground.Layout);
        double standDeg = Assert.NotNull(stand.TrueHeading).Degrees;
        int heading = (int)Math.Round(new TrueHeading(standDeg - HeadingOnlyTurnDeg).Degrees);
        var list = Restore(ground.Layout, LegacyPush(stand.Position, heading, target: null, pullForward: null));

        var phase = Assert.IsType<PushbackPhase>(list.CurrentPhase);
        Assert.Equal(TugMoveShape.TurnTo, phase.Move.Shape);
        Assert.Equal(heading, phase.Move.FacingTrueDeg, 6);

        var ac = Fly(ground, "LEG3", stand, new TugPose(stand.Position, standDeg), pushing: false, list);

        double offDeg = new TrueHeading(heading).AbsAngleTo(ac.TrueHeading);
        output.WriteLine($"nose {ac.TrueHeading.Degrees:F2}° vs heading {heading}° ({offDeg:F2}° off)");
        Assert.True(offDeg <= NearHeadingDeg, $"the restored turn ended {offDeg:F2}° off its heading");
    }

    /// <summary>
    /// A plain push 20 ft off gate D15 restores with its remaining distance still to be worked out on its first
    /// tick. Snapshotted again before that tick, its JSON restores to a phase that snapshots to the same JSON, and
    /// both fly to the same end: the simple pushback distance from the stand.
    /// </summary>
    [Fact]
    public void PendingRestoredMove_ReSnapshottedBeforeItsFirstTick_RestoresIdentically()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var stand = GateNode(ground.Layout);
        double standDeg = Assert.NotNull(stand.TrueHeading).Degrees;
        var pushed = new TugPose(
            GeoMath.ProjectPoint(stand.Position, new TrueHeading(standDeg).ToReciprocal(), PushedSoFarFt / GeoMath.FeetPerNm),
            standDeg
        );
        var first = Restore(ground.Layout, LegacyPush(stand.Position, heading: null, target: null, pullForward: null));

        var firstPhase = Assert.IsType<PushbackPhase>(first.CurrentPhase);
        string firstJson = JsonSerializer.Serialize<PhaseDto>(firstPhase.ToSnapshot(), RecordingJsonOptions.Default);
        output.WriteLine(firstJson);
        var second = Restore(ground.Layout, JsonNode.Parse(firstJson)!.AsObject());
        var secondPhase = Assert.IsType<PushbackPhase>(second.CurrentPhase);
        string secondJson = JsonSerializer.Serialize<PhaseDto>(secondPhase.ToSnapshot(), RecordingJsonOptions.Default);

        Assert.Equal(firstJson, secondJson);

        var original = Fly(ground, "LEG4", stand, pushed, pushing: true, first);
        var twinGround = SfoGroundHarness.Build(output, autoCross: false)!.Value;
        var twin = Fly(twinGround, "LEG4", stand, pushed, pushing: true, second);

        Assert.Equal(original.Position, twin.Position);
        Assert.Equal(original.TrueHeading.Degrees, twin.TrueHeading.Degrees);
        double pushedFt = GeoMath.DistanceNm(stand.Position, original.Position) * GeoMath.FeetPerNm;
        double owedFt = CategoryPerformance.SimplePushbackDistanceNm(AircraftType) * GeoMath.FeetPerNm;
        output.WriteLine($"ended {pushedFt:F2} ft from the stand; the simple pushback is {owedFt:F2} ft");
        Assert.True(Math.Abs(pushedFt - owedFt) <= NearTargetFt, $"the restored push ended {pushedFt:F2} ft from the stand, not {owedFt:F2} ft");
    }

    /// <summary>
    /// A pre-#233 spot pushback part-way along its route — the current segment ends on a ramp node short of spot 6A,
    /// the last segment on 6A itself — restores as one push onto the spot, not onto the node it was steering for.
    /// </summary>
    [Fact]
    public void LegacySpotPushback_RestoresAsOneMoveOntoTheSpot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var spot = SpotApproach(ground.Layout);
        var mid = MidRouteNode(ground.Layout, spot);
        output.WriteLine($"route: stand {Gate} → node {mid.Id} → spot {AlleySpot} (node {spot.Node.Id})");
        var list = Restore(ground.Layout, LegacySpotPush(GateNode(ground.Layout), mid, spot.Node, arrived: false));

        var phase = Assert.IsType<PushbackPhase>(list.CurrentPhase);
        Assert.Equal(PushbackLegKind.Push, phase.Kind);
        Assert.Equal(TugMoveShape.ToPoint, phase.Move.Shape);
        Assert.NotEqual(mid.Position, spot.Node.Position);
        Assert.Equal(spot.Node.Position, phase.Move.Point);
        Assert.Equal(spot.Node.Position, phase.PlannedEnd);
    }

    /// <summary>
    /// The same pushback once it had reached the final node restores completed, standing on the spot: the zero-length
    /// move every arrived pre-tug-move push restores as.
    /// </summary>
    [Fact]
    public void LegacySpotPushback_ThatHadArrived_RestoresCompletedOnTheSpot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var spot = SpotApproach(ground.Layout);
        var list = Restore(ground.Layout, LegacySpotPush(GateNode(ground.Layout), MidRouteNode(ground.Layout, spot), spot.Node, arrived: true));

        var phase = Assert.IsType<PushbackPhase>(list.CurrentPhase);
        Assert.Equal(TugMoveShape.Straight, phase.Move.Shape);
        Assert.Equal(0.0, phase.Move.StraightDistanceFt);
        Assert.Equal(spot.Node.Position, phase.PlannedEnd);
        Assert.Equal(PhaseStatus.Completed, phase.Status);
    }

    /// <summary>Spot 6A's approach: its node, its nose-out heading rounded as the old snapshot stored it, and the points on that line.</summary>
    private sealed record SpotLine(GroundNode Node, int Heading, LatLon Stop, LatLon Staging, TugPose RunUp);

    private static SpotLine SpotApproach(AirportGroundLayout layout)
    {
        var node = layout.FindSpotNodeByName(AlleySpot) ?? throw new InvalidOperationException($"the SFO layout has no spot '{AlleySpot}'");
        Assert.True(layout.TryGetSpotOutboundHeading(node, out double outDeg), $"spot {AlleySpot} has no nose-out heading");
        int heading = (int)Math.Round(outDeg);
        var (stop, staging) = TugMovePlanner.SpotStopGeometry(node, heading, AircraftType);
        var runUp = GeoMath.ProjectPoint(staging, new TrueHeading(heading), RunUpFt / GeoMath.FeetPerNm);
        return new SpotLine(node, heading, stop, staging, new TugPose(runUp, heading));
    }

    /// <summary>
    /// The ramp node the old route's current segment ended on — the layout node nearest the spot's staging point that
    /// is not the spot itself, i.e. the last corner the pre-#233 push pivoted at before backing onto the mark.
    /// </summary>
    private static GroundNode MidRouteNode(AirportGroundLayout layout, SpotLine spot) =>
        layout.Nodes.Values.Where(n => n.Id != spot.Node.Id).OrderBy(n => GeoMath.DistanceNm(n.Position, spot.Staging)).First();

    /// <summary>
    /// A pre-#233 spot pushback as its snapshot JSON was written — the shape the <c>sfo-s1-ground-control-28-01</c>
    /// bundle carries: a two-segment route from the stand through <paramref name="mid"/> onto <paramref name="spot"/>,
    /// with the current segment's end (not the spot) as the target. <paramref name="arrived"/> gives the form taken
    /// once the push reached the spot: the route run out, the final node as the target, and the phase completed.
    /// </summary>
    private static JsonObject LegacySpotPush(GroundNode stand, GroundNode mid, GroundNode spot, bool arrived)
    {
        var target = arrived ? spot : mid;
        return new JsonObject
        {
            [Discriminator()] = "PushbackToSpot",
            ["Route"] = new JsonObject
            {
                ["Segments"] = new JsonArray(LegacySegment(stand, mid), LegacySegment(mid, spot)),
                ["CurrentSegmentIndex"] = arrived ? 2 : 0,
                ["HoldShortPoints"] = null,
                ["Description"] = null,
                ["DestinationNodeId"] = null,
            },
            ["TargetHeading"] = null,
            ["TargetNodeId"] = target.Id,
            ["TargetLat"] = target.Position.Lat,
            ["TargetLon"] = target.Position.Lon,
            ["Initialized"] = true,
            ["ReachedFinalNode"] = arrived,
            ["Pivoting"] = false,
            ["PivotTargetHeadingDeg"] = 0.0,
            ["TimeSinceLastLog"] = 2.0,
            ["Status"] = arrived ? (int)PhaseStatus.Completed : ActiveStatus,
            ["ElapsedSeconds"] = 16.75,
            ["Requirements"] = null,
        };
    }

    private static JsonObject LegacySegment(GroundNode from, GroundNode to) =>
        new()
        {
            ["FromNodeId"] = from.Id,
            ["ToNodeId"] = to.Id,
            ["TaxiwayName"] = LegacyRouteTaxiway,
        };

    private static GroundNode GateNode(AirportGroundLayout layout) =>
        layout.FindParkingByName(Gate) ?? throw new InvalidOperationException($"the SFO layout has no gate '{Gate}'");

    /// <summary>A pre-rework pushback phase, active, as its snapshot JSON was written: no move fields, no <c>Kind</c>.</summary>
    private static JsonObject LegacyPush(LatLon start, int? heading, LatLon? target, LatLon? pullForward) =>
        new()
        {
            [Discriminator()] = "Pushback",
            ["Status"] = ActiveStatus,
            ["ElapsedSeconds"] = 12.0,
            ["TargetHeading"] = heading,
            ["TargetLatitude"] = target?.Lat,
            ["TargetLongitude"] = target?.Lon,
            ["PullForwardLatitude"] = pullForward?.Lat,
            ["PullForwardLongitude"] = pullForward?.Lon,
            ["PullingForward"] = false,
            ["StartLat"] = start.Lat,
            ["StartLon"] = start.Lon,
            ["TotalDistToTarget"] = target is { } point ? GeoMath.DistanceNm(start, point) * GeoMath.FeetPerNm : 0.0,
            ["ReachedTarget"] = false,
            ["IsAligned"] = true,
            ["TimeSinceLastLog"] = 0.0,
        };

    /// <summary>
    /// Restores a phase list holding <paramref name="pushback"/> (current) and the holding phase behind it, from JSON.
    /// </summary>
    private static PhaseList Restore(AirportGroundLayout layout, JsonObject pushback)
    {
        var holding = JsonNode.Parse(
            JsonSerializer.Serialize<PhaseDto>(new HoldingAfterPushbackPhaseDto { Status = 0, ElapsedSeconds = 0 }, RecordingJsonOptions.Default)
        );
        var json = new JsonObject { ["CurrentIndex"] = 0, ["Phases"] = new JsonArray(pushback, holding) };
        var dto = Assert.IsType<PhaseListDto>(JsonSerializer.Deserialize<PhaseListDto>(json.ToJsonString(), RecordingJsonOptions.Default));
        return PhaseList.FromSnapshot(dto, layout);
    }

    /// <summary>The JSON property a phase snapshot names its type with, read off a snapshot this build writes.</summary>
    private static string Discriminator()
    {
        string json = JsonSerializer.Serialize<PhaseDto>(
            new HoldingAfterPushbackPhaseDto { Status = 0, ElapsedSeconds = 0 },
            RecordingJsonOptions.Default
        );
        return JsonNode.Parse(json)!.AsObject().First().Key;
    }

    /// <summary>
    /// Puts an aircraft on <paramref name="pose"/> with the restored phases — rolling tail-first at the push speed
    /// when <paramref name="pushing"/>, as a snapshot taken mid-push holds it — and ticks until no pushback runs.
    /// </summary>
    private AircraftState Fly(SfoGround ground, string callsign, GroundNode node, TugPose pose, bool pushing, PhaseList phases)
    {
        var ac = SfoGroundHarness.SpawnAt(ground, callsign, AircraftType, (node, new TrueHeading(pose.NoseTrueDeg)), new HoldingAfterPushbackPhase());
        ac.Position = pose.Position;
        ac.IndicatedAirspeed = pushing ? CategoryPerformance.PushbackSpeed(AircraftCategory.Jet) : 0.0;
        ac.Ground.PushbackTrueHeading = pushing ? new TrueHeading(pose.NoseTrueDeg).ToReciprocal() : null;
        ac.Phases = phases;

        int finished = SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is not PushbackPhase, BudgetSeconds, null);
        output.WriteLine(
            $"{callsign}: finished t={finished}s at ({ac.Position.Lat:F7},{ac.Position.Lon:F7}) nose {ac.TrueHeading.Degrees:F3}°, "
                + $"phase={ac.Phases?.CurrentPhase?.Name ?? "null"}"
        );
        Assert.True(finished > 0, $"the restored pushback never finished within {BudgetSeconds}s");
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        return ac;
    }

    private void AssertEndsNear(AircraftState ac, LatLon target, int heading)
    {
        double offFt = GeoMath.DistanceNm(ac.Position, target) * GeoMath.FeetPerNm;
        double offDeg = new TrueHeading(heading).AbsAngleTo(ac.TrueHeading);
        output.WriteLine($"ended {offFt:F2} ft off the target, nose {offDeg:F2}° off {heading}°");
        Assert.True(offFt <= NearTargetFt, $"the restored push ended {offFt:F2} ft from its target");
        Assert.True(offDeg <= NearHeadingDeg, $"the restored push ended with the nose {offDeg:F2}° off its heading");
    }
}
