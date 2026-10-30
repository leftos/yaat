using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-509: an aircraft holding short of a runway bar and cleared back over its taxiway must not turn about across the
/// hold line. KOAK and KSFO runway holding positions sit a few feet from a split node of their taxiway (OAK's 15/33 bar on C
/// sits 19 ft past C's split node), so a turn about there would carry its nose, a wingtip or its tail over the line. No part of the aircraft
/// may pass the line (AIM 2-3-5.a.1): from rest it holds where it stands and says "unable, no room to turn around on
/// {taxiway}, holding short of runway {runway}", once.
/// </summary>
public class TurnAboutAtRunwayBarFootprintTests(ITestOutputHelper output)
{
    /// <summary>How far (ft) short of the bar the staged aircraft's nose stands, before any back-off for a leading wingtip.</summary>
    private const double NoseShortOfBarFt = 1.0;

    /// <summary>How long (s) an aircraft at rest is watched after its clearance.</summary>
    private const int AtRestWatchSeconds = 60;

    /// <summary>How long (s) it must have stood still at the end of the watch to count as holding.</summary>
    private const int MinStillSeconds = 20;

    /// <summary>The ground speed (kts) under which it counts as standing still.</summary>
    private const double StillKts = 0.01;

    /// <summary>The words every turn-about refusal carries.</summary>
    private const string UnableToTurnAround = "no room to turn around";

    /// <summary>How far back (ft) from the bar the staging gives up looking for a pose with every part short of the line.</summary>
    private const double MaxBackOffFt = 100.0;

    /// <summary>How many nodes the walk back along the taxiway visits looking for the route's nodes behind the aircraft.</summary>
    private const int MaxWalkNodes = 12;

    /// <summary>How long (s) the restored aircraft is compared with the uninterrupted one.</summary>
    private const int RestoreCompareSeconds = 30;

    /// <summary>How much farther back (ft) than holding short the C172 at KOAK's 15/33 bar on F stands for the mirrored turn about.</summary>
    private const double MirrorExtraBackFt = 15.0;

    /// <summary>How far right (ft) of F's centreline the C172 at KOAK's 15/33 bar on F stands for the mirrored turn about.</summary>
    private const double MirrorRightOfCentrelineFt = 2.0;

    /// <summary>
    /// A <paramref name="type"/> at rest holding short of the <paramref name="barRunway"/> bar on <paramref name="barTaxiway"/>
    /// at <paramref name="airport"/>,
    /// facing it from the taxiway side (<see cref="TaxiwaySideNeighbour"/>) with every part short of the line, is cleared back
    /// along its taxiway to the two nodes behind it. No part of it (nose, both wingtips, tail) is ever inside the runway's hold
    /// line; it stands still where it was, and says it has no room to turn around on the taxiway, holding short of the runway,
    /// once.
    /// </summary>
    [Theory]
    [InlineData("OAK", "33", "C", "J", "C172")]
    [InlineData("OAK", "33", "C", "J", "C208")]
    [InlineData("OAK", "33", "F", null, "C172")]
    [InlineData("OAK", "33", "F", null, "C208")]
    [InlineData("SFO", "01L", "C", "RWY01R/19L", "C172")]
    [InlineData("SFO", "01L", "C", "RWY01R/19L", "C208")]
    public void AtRestHoldingShortOfARunwayBar_TaxiBackOverItsTaxiway_HoldsAndSaysUnable(
        string airport,
        string barRunway,
        string barTaxiway,
        string? nearJunctionWith,
        string type
    )
    {
        if (Build(airport) is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear(ground.Layout, barRunway, barTaxiway, nearJunctionWith);
        (Staged staged, RunwayInfo runway) = StageHoldingShort(ground, airport, bar, type);
        AircraftState aircraft = staged.Aircraft;
        LatLon start = aircraft.Position;
        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);

        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        string expected = $"unable, no room to turn around on {staged.Taxiway}, holding short of runway {ExpectedRunway(ground, aircraft, runway)}";
        output.WriteLine(
            $"{watch}; moved {GeoMath.DistanceNm(start, aircraft.Position) * GeoMath.FeetPerNm:F1} ft; said: {string.Join(" | ", unables)}"
        );

        Assert.True(watch.FirstInside is null, $"the {type}'s {watch.FirstInside} passed runway {runway.Id} hold line at bar #{bar.Id}");
        Assert.True(watch.StillSeconds >= MinStillSeconds, $"the {type} stood still only {watch.StillSeconds}s at the end of the watch");
        string unable = Assert.Single(unables);
        Assert.Contains(expected, unable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The C172 at rest exactly on the C1 node 24 ft short of KOAK's 28R bar on C1, facing the bar with every part short of
    /// the line. No taxi edge's perpendicular falls strictly inside an edge there, so it stands on no occupied edge
    /// (<see cref="AirportGroundLayout.FindOccupiedTaxiEdge"/>). Cleared back over C1, it has no room to turn about short of the
    /// hold line, and its unable still names C1, the taxiway of the edge adjoining the node along its heading.
    /// </summary>
    [Fact]
    public void AtRestOnTheNodeShortOfABar_TaxiBack_UnableNamesTheAdjoiningTaxiway()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        GroundNode bar = GroundNodeLookup.HoldShortBar(ground.Layout, "28R", "C1");
        RunwayInfo runway = BarRunway("OAK", bar);
        GroundNode node = TaxiwaySideNeighbour(ground.Layout, bar, runway);
        Assert.Same(GroundNodeLookup.OnTaxiway(ground.Layout, "C1", bar, 1.0, GroundNodeLookup.Junction(ground.Layout, "C", "F")), node);
        double backFt = GeoMath.DistanceNm(bar.Position, node.Position) * GeoMath.FeetPerNm;
        Staged staged = StageFacingBar(ground, (bar, node), backFt, Type);
        AircraftState aircraft = staged.Aircraft;
        aircraft.Position = node.Position;
        Assert.Null(ground.Layout.FindOccupiedTaxiEdge(aircraft.Position));
        Assert.DoesNotContain(
            BodyPoints(aircraft.Position, aircraft.TrueHeading.Degrees, Type),
            p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point)
        );

        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        Assert.True(watch.FirstInside is null, $"the {Type}'s {watch.FirstInside} passed runway {runway.Id} hold line at bar {bar.Id}");
        string expected = $"unable, no room to turn around on {staged.Taxiway}, holding short of runway {ExpectedRunway(ground, aircraft, runway)}";
        Assert.Contains(expected, Assert.Single(unables), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A B744's nose nearer KOAK runway 10L's centreline than the bars on its side, off the pavement: at the point a B744 turning
    /// back on C reaches with its left wingtip (37.730612, -122.220607), more than <see cref="HoldLineFootprint.MarkedBarReachFt"/>
    /// along the runway from every bar on that side, between bars over the grass, it is not past the hold line; 100 ft along
    /// the runway from the C1 bar and a foot inside its line, it is.
    /// </summary>
    [Fact]
    public void PartInsideARunwaysBarLine_CountsOnlyNearAMarkedBar()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        RunwayInfo runway = RunwayOccupancy.AirportRunways("OAK").First(r => r.Id.Overlaps(RunwayIdentifier.Parse("10L")));
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        double alongDeg = GeoMath.BearingTo(end1, end2);
        double AlongFt(LatLon p) => GeoMath.AlongTrackDistanceNm(p, end1, new TrueHeading(alongDeg)) * GeoMath.FeetPerNm;
        double CentrelineFt(LatLon p) => GeoMath.DistanceToSegmentFt(p, end1, end2);
        bool Right(LatLon p) => GeoMath.SignedBearingDifference(alongDeg, GeoMath.BearingTo(end1, p)) > 0.0;
        GroundNode[] SideBars(LatLon p) =>
            [
                .. layout.Nodes.Values.Where(n =>
                    (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Overlaps(runway.Id) && (Right(n.Position) == Right(p))
                ),
            ];

        var between = new LatLon(37.730612, -122.220607);
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "28R", "C1");
        bool towardBetween = GeoMath.AbsBearingDifference(GeoMath.BearingTo(bar.Position, between), alongDeg) < 90.0;
        var alongRunway = new TrueHeading(towardBetween ? alongDeg : (alongDeg + 180.0) % 360.0);
        LatLon abeamBar = GeoMath.ProjectPoint(bar.Position, alongRunway, 100.0 / GeoMath.FeetPerNm);
        double outwardDeg = Right(bar.Position) ? (alongDeg + 90.0) % 360.0 : (alongDeg + 270.0) % 360.0;
        LatLon nearBar = GeoMath.ProjectPoint(abeamBar, new TrueHeading((outwardDeg + 180.0) % 360.0), 1.0 / GeoMath.FeetPerNm);
        foreach (LatLon point in new[] { between, nearBar })
        {
            double nearestAlongFt = SideBars(point).Min(b => Math.Abs(AlongFt(b.Position) - AlongFt(point)));
            output.WriteLine(
                $"{point}: {CentrelineFt(point):F1} ft off the centreline, {nearestAlongFt:F1} ft along from the nearest bar on its side"
            );
            Assert.True(FollowingPhase.IsInsideHoldLine(layout, runway, point), $"{point} is not inside 10L's bar line");
            Assert.False(RunwayOccupancy.IsWithinPavement(point, runway), $"{point} is on 10L's pavement");
        }

        Assert.True(SideBars(between).All(b => Math.Abs(AlongFt(b.Position) - AlongFt(between)) > HoldLineFootprint.MarkedBarReachFt));

        var body = AircraftBody.Of("B744");
        HoldLineCrossing? NoseCrossing(LatLon nose)
        {
            LatLon start = GeoMath.ProjectPoint(nose, new TrueHeading(outwardDeg), 1000.0 / GeoMath.FeetPerNm);
            var footprint = new HoldLineFootprint(layout, body, (start, outwardDeg), r => !r.Id.Overlaps(runway.Id));
            double headingDeg = (outwardDeg + 180.0) % 360.0;
            LatLon position = GeoMath.ProjectPoint(nose, new TrueHeading(outwardDeg), body.LengthFt / 2.0 / GeoMath.FeetPerNm);
            return footprint.FirstCrossing(position, headingDeg, [AircraftBodyPart.Nose]);
        }

        Assert.Null(NoseCrossing(between));
        Assert.NotNull(NoseCrossing(nearBar));
    }

    /// <summary>
    /// A C172 abeam KOAK's 28R bar on C1, facing along runway 10L/28R, at the nearest pose out from the bar where its runway-side
    /// wingtip alone is inside the runway's hold line (nose, tail and main gear outside it). A footprint begun there judges that
    /// wingtip only against the runway's pavement: carried farther in, until the nose reaches the line, it passes no line
    /// while it stays off the pavement, while the nose, judged as usual, passes the line there. A footprint begun with every
    /// part short of the line refuses that same wingtip at that same pose.
    /// </summary>
    [Fact]
    public void LoneWingtipInsideTheLineAtTheStart_IsJudgedOnlyAgainstThePavement()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        var body = AircraftBody.Of("C172");
        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "28R", "C1");
        RunwayInfo runway = BarRunway("OAK", bar);
        double headingDeg = GeoMath.BearingTo(new LatLon(runway.Lat1, runway.Lon1), new LatLon(runway.Lat2, runway.Lon2));
        TrueHeading outward = new[] { new TrueHeading((headingDeg + 90.0) % 360.0), new TrueHeading((headingDeg + 270.0) % 360.0) }.MaxBy(h =>
            RunwayHoldBars.CentrelineFt(runway, GeoMath.ProjectPoint(bar.Position, h, 10.0 / GeoMath.FeetPerNm))
        );
        LatLon PoseAt(double outFt) => GeoMath.ProjectPoint(bar.Position, outward, outFt / GeoMath.FeetPerNm);
        bool IsInside(AircraftBodyPart part, LatLon pose) => FollowingPhase.IsInsideHoldLine(layout, runway, body.PointOf(part, pose, headingDeg));
        AircraftBodyPart[] Inside(LatLon pose) => [.. AircraftBody.AllParts.Where(part => IsInside(part, pose))];
        bool IsLoneWingtip(AircraftBodyPart[] parts) => parts is [AircraftBodyPart.LeftWingtip] or [AircraftBodyPart.RightWingtip];
        double[] steps = [.. Enumerable.Range(0, (int)MaxBackOffFt).Select(ft => (double)ft)];

        double loneOutFt = steps.FirstOrDefault(ft => IsLoneWingtip(Inside(PoseAt(ft))), -1.0);
        Assert.True(loneOutFt >= 0.0, $"no pose up to {MaxBackOffFt:F0} ft out from the C1 bar has a lone wingtip inside the line");
        LatLon start = PoseAt(loneOutFt);
        AircraftBodyPart wingtip = Assert.Single(Inside(start));
        double noseInFt = steps.FirstOrDefault(ft => IsInside(AircraftBodyPart.Nose, PoseAt(loneOutFt - ft)), -1.0);
        Assert.True(noseInFt > 0.0, $"the nose never reaches the C1 bar's line in from the pose {loneOutFt:F0} ft out");
        LatLon deeper = PoseAt(loneOutFt - noseInFt);
        double clearOutFt = steps.FirstOrDefault(ft => (ft > loneOutFt) && (Inside(PoseAt(ft)).Length == 0), -1.0);
        Assert.True(clearOutFt > 0.0, $"no pose up to {MaxBackOffFt:F0} ft out from the C1 bar has every part short of the line");
        output.WriteLine(
            $"{AircraftBody.Describe(wingtip)} alone inside {loneOutFt:F0} ft out; nose inside {noseInFt:F0} ft farther in; "
                + $"every part short {clearOutFt:F0} ft out"
        );

        var fromLoneWingtip = new HoldLineFootprint(layout, body, (start, headingDeg), _ => false);
        var fromClear = new HoldLineFootprint(layout, body, (PoseAt(clearOutFt), headingDeg), _ => false);
        Assert.False(RunwayOccupancy.IsWithinPavement(body.PointOf(wingtip, deeper, headingDeg), runway));
        Assert.Null(fromLoneWingtip.FirstCrossing(start, headingDeg, [wingtip]));
        Assert.Null(fromLoneWingtip.FirstCrossing(deeper, headingDeg, [wingtip]));
        Assert.Equal(AircraftBodyPart.Nose, fromLoneWingtip.FirstCrossing(deeper, headingDeg, [AircraftBodyPart.Nose])?.Part);
        Assert.Equal(wingtip, fromClear.FirstCrossing(deeper, headingDeg, [wingtip])?.Part);
    }

    /// <summary>
    /// The C172 at rest holding short of KOAK's 15/33 bar on C with no room to turn about, snapshotted
    /// <paramref name="secondsBeforeSnapshot"/> into its hold and restored into a fresh engine: the snapshot carries the runway
    /// it holds short of, and the restored aircraft holds exactly where the uninterrupted one does. Its unable is said on the
    /// hold's first tick, once: a restore taken after that says nothing again (<paramref name="restoredUnableCalls"/> 0), one
    /// taken before it says it once (1), as the uninterrupted one does.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void AtRestHoldingShortOfABar_UnableHold_SurvivesSnapshotRoundTrip(int secondsBeforeSnapshot, int restoredUnableCalls)
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        (Staged staged, RunwayInfo runway) = StageHoldingShort(ground, "OAK", Oak33BarOnCAtJ(ground.Layout), "C172");
        AircraftState aircraft = staged.Aircraft;
        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        for (int second = 0; second < secondsBeforeSnapshot; second++)
        {
            ground.Engine.TickOneSecond();
        }

        StateSnapshotDto snapshot = Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );
        string expectedRunway = ExpectedRunway(ground, aircraft, runway);
        JsonObject hold = Assert.Single(
            GroundNavigatorArcRestoreTests
                .PlaybackObjects(JsonSerializer.SerializeToNode(snapshot, RecordingJsonOptions.Default))
                .Select(p => p[nameof(GroundNavigatorPlaybackDto.TurnAboutHold)])
                .OfType<JsonObject>()
        );
        Assert.Equal(expectedRunway, hold[nameof(TurnAboutHoldDto.Runway)]?.GetValue<string>());

        SfoGround? rebuilt = Build("OAK");
        Assert.NotNull(rebuilt);
        SfoGround restored = rebuilt.Value;
        restored.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restoredAircraft = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(aircraft.Callsign));
        List<string> restoredUnables = CollectUnables(restored, restoredAircraft);
        for (int second = 1; second <= RestoreCompareSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            restored.Engine.TickOneSecond();
            Assert.Equal(aircraft.Position, restoredAircraft.Position);
            Assert.Equal(aircraft.TrueHeading.Degrees, restoredAircraft.TrueHeading.Degrees);
        }

        output.WriteLine($"said: {string.Join(" | ", unables)}; restored said: {string.Join(" | ", restoredUnables)}");
        Assert.True(restoredAircraft.GroundSpeed < StillKts, $"the restored C172 was moving at {restoredAircraft.GroundSpeed:F2} kt");
        Assert.Single(unables);
        Assert.Equal(restoredUnableCalls, restoredUnables.Count);
        Assert.All(restoredUnables, u => Assert.Contains($"holding short of runway {expectedRunway}", u, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A C172 at rest facing KOAK's 15/33 bar on F, where F meets runway 15/33 at an angle, <see cref="MirrorExtraBackFt"/> farther back
    /// than the nearest pose with every part short of the line and <see cref="MirrorRightOfCentrelineFt"/> right of F's
    /// centreline. Cleared back over F, its turn about to the right would carry a wingtip past the hold line, the same turn
    /// about to the left keeps every part short of it: it turns about to the left, finishes the leg, and says nothing.
    /// </summary>
    [Fact]
    public void AtRestOffCentreShortOfAnAngledBar_OneWayCrosses_TurnsAboutTheOtherWay()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        GroundNode bar = GroundNodeLookup.HoldShortBar(ground.Layout, "33", "F");
        RunwayInfo runway = BarRunway("OAK", bar);
        GroundNode approach = TaxiwaySideNeighbour(ground.Layout, bar, runway);
        double backFt = HoldingShortBackFt(ground.Layout, (bar, approach), runway, Type) + MirrorExtraBackFt;
        Staged staged = StageFacingBar(ground, (bar, approach), backFt, Type);
        AircraftState aircraft = staged.Aircraft;
        var right = new TrueHeading((aircraft.TrueHeading.Degrees + 90.0) % 360.0);
        aircraft.Position = GeoMath.ProjectPoint(aircraft.Position, right, MirrorRightOfCentrelineFt / GeoMath.FeetPerNm);

        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        Assert.True(watch.FirstInside is null, $"the {Type}'s {watch.FirstInside} passed runway {runway.Id} hold line at bar {bar.Id}");
        Assert.Empty(unables);
        Assert.True(watch.LegDone, $"the {Type} did not finish its turn about back over {staged.Taxiway}");
    }

    /// <summary>KOAK's 15/33 bar on C on the C/J side of the runway.</summary>
    private static GroundNode Oak33BarOnCAtJ(AirportGroundLayout layout) => BarNear(layout, "33", "C", "J");

    /// <summary>KOAK 28R's bar on G on the C side of the runway.</summary>
    private static GroundNode Oak28RBarOnGAtC(AirportGroundLayout layout) => BarNear(layout, "28R", "G", "C");

    /// <summary>
    /// The <paramref name="runway"/> bar on <paramref name="taxiway"/>: the one nearest its junction with
    /// <paramref name="nearJunctionWith"/>, or its only one when that is null.
    /// </summary>
    private static GroundNode BarNear(AirportGroundLayout layout, string runway, string taxiway, string? nearJunctionWith)
    {
        if (nearJunctionWith is null)
        {
            return GroundNodeLookup.HoldShortBar(layout, runway, taxiway);
        }

        return GroundNodeLookup.HoldShortBar(layout, runway, taxiway, GroundNodeLookup.Junction(layout, taxiway, nearJunctionWith));
    }

    /// <summary>
    /// A C172 taxiing from rest <see cref="TaxiStartBackFt"/> back from the <paramref name="barRunway"/> bar on
    /// <paramref name="barTaxiway"/> is stopped short of it
    /// by a controller's <c>TAXI #bar #next HS runway</c>, then cleared back over its taxiway to the first two nodes more than
    /// <see cref="TaxiBackMinBehindFt"/> behind it. Its HS stop holds every part short of the line; a turn about from there
    /// would carry a part past it, so it holds where it stopped and says once that it has no room to turn around on the
    /// taxiway, holding short of the runway. The turn-about-when-clear branch cannot follow a real HS stop at KOAK or KSFO:
    /// the nose stops 0 to 2.8 ft short of the bar, and of 52 bars whose back route resolved, all 52 held; the clear case
    /// from rest is <see cref="AtRestOffCentreShortOfAnAngledBar_OneWayCrosses_TurnsAboutTheOtherWay"/>.
    /// </summary>
    [Theory]
    [InlineData("OAK", "33", "C", "J")]
    [InlineData("OAK", "28L", "J", null)]
    public void StoppedShortByATaxiHoldShort_ClearedBackTheWayItCame_HoldsAndSaysUnable(
        string airport,
        string barRunway,
        string barTaxiway,
        string? nearJunctionWith
    )
    {
        if (Build(airport) is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        GroundNode bar = BarNear(ground.Layout, barRunway, barTaxiway, nearJunctionWith);
        RunwayInfo runway = BarRunway(airport, bar);
        GroundNode approach = TaxiwaySideNeighbour(ground.Layout, bar, runway);
        GroundNode next = bar.Edges.OfType<GroundEdge>().Select(e => e.OtherNode(bar)).First(n => n != approach);
        Staged staged = StageFacingBar(ground, (bar, approach), TaxiStartBackFt, Type);
        AircraftState aircraft = staged.Aircraft;
        string runwayEnd = ExpectedRunway(ground, aircraft, runway);
        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, $"TAXI #{bar.Id} #{next.Id} HS {runwayEnd}");
        int still = 0;
        for (int second = 1; (second <= HoldShortWatchSeconds) && (still < StoppedSeconds); second++)
        {
            ground.Engine.TickOneSecond();
            still = (aircraft.GroundSpeed < StillKts) ? still + 1 : 0;
        }

        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.DoesNotContain(
            BodyPoints(aircraft.Position, aircraft.TrueHeading.Degrees, Type),
            p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point)
        );

        var backDeg = new TrueHeading((aircraft.TrueHeading.Degrees + 180.0) % 360.0);
        IGroundEdge barEdge = bar.Edges.First(e => (e is GroundEdge) && (e.OtherNode(bar) == approach));
        GroundNode[] behind = NodesBehind((bar, barEdge), (aircraft.Position, TaxiBackMinBehindFt), backDeg);
        Send(ground, aircraft, $"TAXI #{behind[0].Id} #{behind[1].Id}");
        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        Assert.True(watch.FirstInside is null, $"the {Type}'s {watch.FirstInside} passed runway {runway.Id} hold line at bar {bar.Id}");
        Assert.True(watch.StillSeconds >= MinStillSeconds, $"the {Type} stood still only {watch.StillSeconds}s at the end of the watch");
        string expected = $"unable, no room to turn around on {staged.Taxiway}, holding short of runway {runwayEnd}";
        Assert.Contains(expected, Assert.Single(unables), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How far back (ft) from the bar the C172 starts its taxi to the HS stop.</summary>
    private const double TaxiStartBackFt = 150.0;

    /// <summary>How far behind (ft) its HS stop the nodes it is cleared back to lie.</summary>
    private const double TaxiBackMinBehindFt = 60.0;

    /// <summary>How long (s) the taxi to the HS stop is watched.</summary>
    private const int HoldShortWatchSeconds = 240;

    /// <summary>How long (s) it must stand still to count as stopped at the HS.</summary>
    private const int StoppedSeconds = 5;

    /// <summary>
    /// A C172 holding short of a KOAK runway bar and cleared back over its taxiway refuses its turn about by one of two paths,
    /// told apart by the navigator's log lines starting "from rest,". Standing on the C1 node short of 28R's C1 bar
    /// (<paramref name="onTheNode"/>), on no occupied taxi edge, no taxiway turn about solves and the fallback entry arc is
    /// refused, which logs only the warning that it holds and says unable. Holding short of 15/33's bar on C inside C's edge, 64 ft
    /// past C's split node and clear of its ends, the turn about's jog and reversal are refused, which first logs which part would
    /// pass the line turning about each way.
    /// </summary>
    [Theory]
    [InlineData("28R", "C1", null, true)]
    [InlineData("33", "C", "J", false)]
    public void AtRestShortOfABar_RefusedTurnAbout_LogsWhichPathTripped(string barRunway, string barTaxiway, string? nearJunctionWith, bool onTheNode)
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        GroundNode bar = BarNear(ground.Layout, barRunway, barTaxiway, nearJunctionWith);
        RunwayInfo runway = BarRunway("OAK", bar);
        GroundNode node = TaxiwaySideNeighbour(ground.Layout, bar, runway);
        double backFt = onTheNode
            ? GeoMath.DistanceNm(bar.Position, node.Position) * GeoMath.FeetPerNm
            : HoldingShortBackFt(ground.Layout, (bar, node), runway, Type);
        Staged staged = StageFacingBar(ground, (bar, node), backFt, Type);
        AircraftState aircraft = staged.Aircraft;
        if (onTheNode)
        {
            aircraft.Position = node.Position;
        }

        Assert.Equal(onTheNode, ground.Layout.FindOccupiedTaxiEdge(aircraft.Position) is null);
        var log = DebugLogCapture.Install(FromRestLog);
        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; logged: {string.Join(" | ", log.Lines)}");

        Assert.Single(unables);
        Assert.Single(log.Lines, l => l.Contains(HoldsAndSaysUnableLog, StringComparison.Ordinal));
        Assert.Equal(onTheNode ? 0 : 1, log.Lines.Count(l => l.Contains(EachWayLog, StringComparison.Ordinal)));
    }

    /// <summary>The start of every navigator log line about a turn about refused from rest.</summary>
    private const string FromRestLog = "from rest,";

    /// <summary>The navigator's warning when an aircraft at rest holds and says unable.</summary>
    private const string HoldsAndSaysUnableLog = "holds and says unable";

    /// <summary>The navigator's debug line when a jog and reversal from rest were tried each way.</summary>
    private const string EachWayLog = "; the other way";

    /// <summary>
    /// The C172 holding short of KOAK's 15/33 bar on C, turned <see cref="CornerTurnOffDeg"/> off the bar's bearing in place, backed off
    /// a foot at a time until every part of the turned body is still short of the line, then cleared back over C: its entry is
    /// a corner of 110°, under the 135° of a reversal, so it is never refused at the hold line, even though its outer wingtip
    /// sweeps inside the line through the corner. It finishes the leg and says nothing.
    /// </summary>
    [Fact]
    public void AtRestShortOfABar_CornerUnderAReversal_IsNeverRefused()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        GroundNode bar = Oak33BarOnCAtJ(ground.Layout);
        RunwayInfo runway = BarRunway("OAK", bar);
        GroundNode approach = TaxiwaySideNeighbour(ground.Layout, bar, runway);
        double barBearingDeg = GeoMath.BearingTo(approach.Position, bar.Position);
        double turnedDeg = (barBearingDeg + CornerTurnOffDeg) % 360.0;
        var backDeg = new TrueHeading((barBearingDeg + 180.0) % 360.0);
        double backFt = HoldingShortBackFt(ground.Layout, (bar, approach), runway, Type);
        while (
            BodyPoints(GeoMath.ProjectPoint(bar.Position, backDeg, backFt / GeoMath.FeetPerNm), turnedDeg, Type)
                .Any(p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point))
        )
        {
            backFt += 1.0;
            Assert.True(backFt < MaxBackOffFt, $"the turned {Type} is still inside the hold line of bar {bar.Id} {backFt:F0} ft back from it");
        }

        Staged staged = StageFacingBar(ground, (bar, approach), backFt, Type);
        AircraftState aircraft = staged.Aircraft;
        aircraft.TrueHeading = new TrueHeading(turnedDeg);
        aircraft.TrueTrack = aircraft.TrueHeading;
        Assert.DoesNotContain(
            BodyPoints(aircraft.Position, aircraft.TrueHeading.Degrees, Type),
            p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point)
        );

        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        Assert.Empty(unables);
        Assert.True(watch.LegDone, $"the {Type} did not finish its corner back onto {staged.Taxiway}");
        Assert.True(watch.FirstInside is not null, "the corner kept every part short of the line: nothing to refuse");
    }

    /// <summary>How far (deg) the C172 is turned off the bar's bearing before its corner: a 110° entry back over C.</summary>
    private const double CornerTurnOffDeg = 70.0;

    /// <summary>
    /// A C172 at rest on KOAK G between runways 28R and 28L, its tail just short of 28R's hold line at G's bar on the C side and facing
    /// away from 28R, cleared to taxi back across 28R (G's bar on the far side of 28R and the G node past it,
    /// <see cref="FarBarAcross"/>). Without a crossing clearance its
    /// turn about would carry a part past 28R's line: it holds and says unable, holding short of runway 28R. With
    /// <c>CROSS 28R</c> the route holds a cleared crossing of 28R, whose hold line no longer binds it: it turns about,
    /// finishes the leg and says nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtRestFacingAwayFromABar_ReversalUnderACrossing_IsAllowedOnlyWhenCleared(bool cleared)
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        (Staged staged, RunwayInfo runway) = StageHoldingShort(ground, "OAK", Oak28RBarOnGAtC(ground.Layout), Type);
        AircraftState aircraft = staged.Aircraft;
        aircraft.TrueHeading = new TrueHeading((aircraft.TrueHeading.Degrees + 180.0) % 360.0);
        aircraft.TrueTrack = aircraft.TrueHeading;
        Assert.DoesNotContain(
            BodyPoints(aircraft.Position, aircraft.TrueHeading.Degrees, Type),
            p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point)
        );

        GroundNode gBar = Oak28RBarOnGAtC(ground.Layout);
        (GroundNode farBar, GroundNode pastFarBar) = FarBarAcross(ground.Layout, gBar, runway);
        string taxi = $"TAXI #{farBar.Id} #{pastFarBar.Id}";
        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, cleared ? $"{taxi} CROSS 28R" : taxi);
        Assert.Contains(
            aircraft.Ground.AssignedTaxiRoute!.HoldShortPoints,
            hs => (hs.NodeId == gBar.Id) && (hs.Reason == HoldShortReason.RunwayCrossing) && (hs.IsCleared == cleared)
        );
        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        if (cleared)
        {
            Assert.Empty(unables);
            Assert.True(watch.LegDone, $"the {Type} did not finish its turn about under its 28R crossing");
            return;
        }

        Assert.True(watch.FirstInside is null, $"the {Type}'s {watch.FirstInside} passed runway 28R's hold line uncleared");
        Assert.Contains("holding short of runway 28R", Assert.Single(unables), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The C172 holding short of KOAK's 28R bar on C1 (C1, runway 10L/28R), moved <see cref="OffTheTaxiwayFt"/> right of C1 so it
    /// stands on no taxi edge and more than <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/> from every node, every part
    /// still short of the line, is cleared back: its turn about would pass the hold line, so it holds and says unable,
    /// holding short of the runway, with no "on" taxiway clause.
    /// </summary>
    [Fact]
    public void AtRestOffEveryTaxiway_NoRoomToTurnAbout_UnableNamesNoTaxiway()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        (Staged staged, RunwayInfo runway) = StageHoldingShort(ground, "OAK", GroundNodeLookup.HoldShortBar(ground.Layout, "28R", "C1"), Type);
        AircraftState aircraft = staged.Aircraft;
        var right = new TrueHeading((aircraft.TrueHeading.Degrees + 90.0) % 360.0);
        aircraft.Position = GeoMath.ProjectPoint(aircraft.Position, right, OffTheTaxiwayFt / GeoMath.FeetPerNm);
        GroundNode nearest = Assert.IsType<GroundNode>(ground.Layout.FindNearestNode(aircraft.Position));
        Assert.Null(ground.Layout.FindOccupiedTaxiEdge(aircraft.Position));
        Assert.True(GeoMath.DistanceNm(nearest.Position, aircraft.Position) * GeoMath.FeetPerNm > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt);
        Assert.DoesNotContain(
            BodyPoints(aircraft.Position, aircraft.TrueHeading.Degrees, Type),
            p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point)
        );

        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        Assert.True(watch.FirstInside is null, $"the {Type}'s {watch.FirstInside} passed runway {runway.Id} hold line at the C1 bar");
        string expected = $"unable, no room to turn around, holding short of runway {ExpectedRunway(ground, aircraft, runway)}";
        Assert.Contains(expected, Assert.Single(unables), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How far (ft) right of C1 the C172 stands, off every taxi edge.</summary>
    private const double OffTheTaxiwayFt = 30.0;

    /// <summary>
    /// YAAT-509: the C172 standing on no taxiway beside KOAK's 28R bar on C1, creeping at its pivot speed, whose firm-rate stop still
    /// carries a leading part past the bar's hold line. Its stop overruns, so its unable names no runway, and standing on no
    /// taxiway it names no taxiway either: it asks for a route ahead. The hold it plans carries neither through the snapshot.
    /// </summary>
    [Fact]
    public void CreepingOffEveryTaxiway_FirmRateStopPassesTheLine_UnableNamesNeitherRunwayNorTaxiway()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        (Staged staged, RunwayInfo runway) = StageHoldingShort(ground, "OAK", GroundNodeLookup.HoldShortBar(ground.Layout, "28R", "C1"), Type);
        AircraftState aircraft = staged.Aircraft;
        var right = new TrueHeading((aircraft.TrueHeading.Degrees + 90.0) % 360.0);
        aircraft.Position = GeoMath.ProjectPoint(aircraft.Position, right, OffTheTaxiwayFt / GeoMath.FeetPerNm);
        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double speedKts = CategoryPerformance.TurnAboutSpeedKts(category, TurnAboutFit.Evaluate(Type, category).RadiusFt) + CreepOverPivotKts;
        double taxiStopFt = StoppingDistanceFt(speedKts, CategoryPerformance.TaxiDecelRate(category));
        double firmStopFt = StoppingDistanceFt(speedKts, CategoryPerformance.ExpediteExitDecelRate(category));
        double headingDeg = aircraft.TrueHeading.Degrees;
        LatLon staging = aircraft.Position;
        LatLon Along(double ft) =>
            GeoMath.ProjectPoint(staging, new TrueHeading((ft >= 0.0) ? headingDeg : (headingDeg + 180.0) % 360.0), Math.Abs(ft) / GeoMath.FeetPerNm);
        bool LeadingPartInside(LatLon position) =>
            BodyPoints(position, headingDeg, Type)
                .Where(p => p.Part != "tail")
                .Any(p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point));
        double gapFt = Enumerable
            .Range(0, (int)(MaxBackOffFt * 100.0))
            .Select(i => i / 100.0)
            .FirstOrDefault(ft => LeadingPartInside(Along(ft)), -1.0);
        double shortOfLineFt = firmStopFt / 2.0;
        aircraft.Position = Along(gapFt - shortOfLineFt);
        aircraft.IndicatedAirspeed = speedKts;
        Assert.Null(ground.Layout.FindOccupiedTaxiEdge(aircraft.Position));
        output.WriteLine(
            $"{Type} creeping off every taxiway at {speedKts:F2} kt, leading parts {shortOfLineFt:F2} ft short of the line; "
                + $"taxi-rate stop {taxiStopFt:F2} ft, firm-rate stop {firmStopFt:F2} ft; first leading part inside {gapFt:F2} ft ahead"
        );
        Assert.True(
            gapFt > 0.0,
            $"no pose up to {MaxBackOffFt:F0} ft ahead of the off-taxiway staging has a leading part inside runway {runway.Id}'s hold line"
        );
        Assert.True(taxiStopFt > shortOfLineFt, "the taxi-rate stop does not pass the line");

        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        JsonObject hold = Assert.Single(
            GroundNavigatorArcRestoreTests
                .PlaybackObjects(JsonSerializer.SerializeToNode(ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default))
                .Select(p => p[nameof(GroundNavigatorPlaybackDto.TurnAboutHold)])
                .OfType<JsonObject>()
        );
        double Number(string name) => Assert.IsAssignableFrom<JsonValue>(hold[name]).GetValue<double>();
        var stopPoint = new LatLon(Number(nameof(TurnAboutHoldDto.StopLat)), Number(nameof(TurnAboutHoldDto.StopLon)));
        double stopFt = GeoMath.DistanceNm(aircraft.Position, stopPoint) * GeoMath.FeetPerNm;
        output.WriteLine($"hold: {hold.ToJsonString()}; stop {stopFt:F2} ft ahead");
        Assert.Equal(CategoryPerformance.ExpediteExitDecelRate(category), Number(nameof(TurnAboutHoldDto.DecelRateKts)));
        Assert.Equal(firmStopFt, stopFt, 0.01);
        Assert.Null(hold[nameof(TurnAboutHoldDto.Runway)]);
        Assert.Null(hold[nameof(TurnAboutHoldDto.Taxiway)]);

        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        Assert.True(watch.StillSeconds >= MinStillSeconds, $"the {Type} stood still only {watch.StillSeconds}s at the end of the watch");
        Assert.Contains("unable, no room to turn around, request a route ahead", Assert.Single(unables), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The C172 holding short of KOAK's 15/33 bar on C with no room to turn about, still creeping toward the bar at
    /// <see cref="CreepOverPivotKts"/> over its pivot speed when it is cleared back over C, placed so its nose and wingtips
    /// are past the line where its taxi-rate stop ends. Its stop is judged where it ends, as a rolling aircraft's is: when
    /// its firm-rate stop keeps them short of the line (<paramref name="overrun"/> false: the leading parts stand half-way
    /// between the two stops short of the line), it brakes firmly, stops with every part short of the line and says it is
    /// holding short of the runway; when even the firm-rate stop passes the line (the leading parts stand half the firm stop
    /// short of it), it is an overrun and the unable names no runway. Either way the hold it plans stops it at the firm rate,
    /// where the firm-rate stop ends.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreepingShortOfABar_TaxiRateStopPassesTheLine_IsJudgedWhereItStops(bool overrun)
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        (Staged staged, RunwayInfo runway) = StageHoldingShort(ground, "OAK", Oak33BarOnCAtJ(ground.Layout), Type);
        AircraftState aircraft = staged.Aircraft;
        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double speedKts = CategoryPerformance.TurnAboutSpeedKts(category, TurnAboutFit.Evaluate(Type, category).RadiusFt) + CreepOverPivotKts;
        double taxiStopFt = StoppingDistanceFt(speedKts, CategoryPerformance.TaxiDecelRate(category));
        double firmStopFt = StoppingDistanceFt(speedKts, CategoryPerformance.ExpediteExitDecelRate(category));
        double headingDeg = aircraft.TrueHeading.Degrees;
        LatLon staging = aircraft.Position;
        LatLon Along(double ft) =>
            GeoMath.ProjectPoint(staging, new TrueHeading((ft >= 0.0) ? headingDeg : (headingDeg + 180.0) % 360.0), Math.Abs(ft) / GeoMath.FeetPerNm);
        bool LeadingPartInside(LatLon position) =>
            BodyPoints(position, headingDeg, Type)
                .Where(p => p.Part != "tail")
                .Any(p => FollowingPhase.IsInsideHoldLine(ground.Layout, runway, p.Point));
        double gapFt = Enumerable.Range(0, (int)(MaxBackOffFt * 100.0)).Select(i => i / 100.0).First(ft => LeadingPartInside(Along(ft)));
        double shortOfLineFt = overrun ? firmStopFt / 2.0 : (firmStopFt + taxiStopFt) / 2.0;
        aircraft.Position = Along(gapFt - shortOfLineFt);
        aircraft.IndicatedAirspeed = speedKts;
        output.WriteLine(
            $"{Type} creeping at {speedKts:F2} kt, leading parts {shortOfLineFt:F2} ft short of the line; "
                + $"taxi-rate stop {taxiStopFt:F2} ft, firm-rate stop {firmStopFt:F2} ft"
        );
        Assert.True(taxiStopFt > shortOfLineFt, "the taxi-rate stop does not pass the line");

        List<string> unables = CollectUnables(ground, aircraft);
        Send(ground, aircraft, staged.Command);
        JsonObject hold = Assert.Single(
            GroundNavigatorArcRestoreTests
                .PlaybackObjects(JsonSerializer.SerializeToNode(ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default))
                .Select(p => p[nameof(GroundNavigatorPlaybackDto.TurnAboutHold)])
                .OfType<JsonObject>()
        );
        double Number(string name) => Assert.IsAssignableFrom<JsonValue>(hold[name]).GetValue<double>();
        var stopPoint = new LatLon(Number(nameof(TurnAboutHoldDto.StopLat)), Number(nameof(TurnAboutHoldDto.StopLon)));
        double stopFt = GeoMath.DistanceNm(aircraft.Position, stopPoint) * GeoMath.FeetPerNm;
        output.WriteLine($"hold: {hold.ToJsonString()}; stop {stopFt:F2} ft ahead");
        Assert.Equal(CategoryPerformance.ExpediteExitDecelRate(category), Number(nameof(TurnAboutHoldDto.DecelRateKts)));
        Assert.Equal(firmStopFt, stopFt, 0.01);
        Assert.Equal(overrun ? null : ExpectedRunway(ground, aircraft, runway), hold[nameof(TurnAboutHoldDto.Runway)]?.GetValue<string>());

        Watch watch = WatchAgainstTheLine(ground, aircraft, (ground.Layout, runway), AtRestWatchSeconds);
        output.WriteLine($"{watch}; said: {string.Join(" | ", unables)}");

        string unable = Assert.Single(unables);
        Assert.True(watch.StillSeconds >= MinStillSeconds, $"the {Type} stood still only {watch.StillSeconds}s at the end of the watch");
        if (overrun)
        {
            Assert.Contains($"unable, no room to turn around on {staged.Taxiway}, request a route ahead", unable, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.True(watch.FirstInside is null, $"the {Type}'s {watch.FirstInside} passed runway {runway.Id} hold line at C's 15/33 bar");
        string expected = $"unable, no room to turn around on {staged.Taxiway}, holding short of runway {ExpectedRunway(ground, aircraft, runway)}";
        Assert.Contains(expected, unable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How far (kts) over its pivot speed the creeping C172 rolls: under the navigator's 0.2 kt at-pivot overshoot.</summary>
    private const double CreepOverPivotKts = 0.15;

    /// <summary>How far (ft) an aircraft rolls braking from <paramref name="speedKts"/> to rest at <paramref name="rateKtsPerSec"/>.</summary>
    private static double StoppingDistanceFt(double speedKts, double rateKtsPerSec) =>
        (speedKts * speedKts) / (2.0 * rateKtsPerSec) * GeoMath.FeetPerNm / 3600.0;

    /// <summary>
    /// The C172 standing on KOAK C's split node about 19 ft from 15/33's bar on C, on the taxiway side, facing
    /// <see cref="LineUpFacingDeg"/>, cleared to line up and wait on runway 33. Its line-up route starts at the node it stands
    /// on whatever its heading, so it opens with a reversal whose fallback arc, at its pivot speed, swings a part past runway
    /// 15/33's hold line; the runway its phase is cleared onto does not bind it: it lines up and says nothing.
    /// </summary>
    [Fact]
    public void OnTheNodeShortOfABar_LineUpAndWaitOnItsRunway_LinesUpWithNoUnable()
    {
        if (Build("OAK") is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        GroundNode bar = Oak33BarOnCAtJ(ground.Layout);
        GroundNode node = GroundNodeLookup.OnTaxiway(ground.Layout, "C", bar, 1.0, GroundNodeLookup.Junction(ground.Layout, "C", "J"));
        Staged staged = StageFacingBar(ground, (bar, node), GeoMath.DistanceNm(bar.Position, node.Position) * GeoMath.FeetPerNm, Type);
        AircraftState aircraft = staged.Aircraft;
        aircraft.Position = node.Position;
        aircraft.TrueHeading = new TrueHeading(LineUpFacingDeg);
        aircraft.TrueTrack = aircraft.TrueHeading;
        aircraft.Phases!.AssignedRunway = Assert.IsType<RunwayInfo>(CommandDispatcher.ResolveRunway(aircraft, "33"));

        List<string> unables = CollectUnables(ground, aircraft);
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, "LUAW");
        output.WriteLine($"LUAW: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'LUAW' was refused: {result.Message}");
        Assert.IsType<LineUpPhase>(aircraft.Phases?.CurrentPhase);
        int second = 0;
        while ((second < LineUpWatchSeconds) && (aircraft.Phases?.CurrentPhase is LineUpPhase))
        {
            ground.Engine.TickOneSecond();
            second++;
        }

        output.WriteLine($"{aircraft.Phases?.CurrentPhase?.GetType().Name} after {second}s; said: {string.Join(" | ", unables)}");
        Assert.Empty(unables);
        Assert.IsType<LinedUpAndWaitingPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>The heading (deg true) the C172 on C's split node faces when cleared to line up.</summary>
    private const double LineUpFacingDeg = 112.0;

    /// <summary>How long (s) the line-up is watched.</summary>
    private const int LineUpWatchSeconds = 180;

    /// <summary>An aircraft staged facing a runway bar: the aircraft, the taxiway it stands on, and the TAXI back over it.</summary>
    private sealed record Staged(AircraftState Aircraft, string Taxiway, string Command);

    /// <summary>What a watched aircraft did (<see cref="WatchAgainstTheLine"/>).</summary>
    /// <param name="FirstInside">The first part, and the second, found inside the hold line; null when none ever was.</param>
    /// <param name="StillSeconds">How long (s) it had stood still when the watch ended.</param>
    /// <param name="LegDone">Whether it finished the route's first segment.</param>
    private sealed record Watch(string? FirstInside, int StillSeconds, bool LegDone);

    /// <summary>
    /// Ticks the engine up to <paramref name="maxSeconds"/> seconds, or until the aircraft finishes the route's first segment,
    /// testing its nose and tail (half its length ahead and behind) and both wingtips (half its span abeam) against
    /// <paramref name="line"/>'s runway hold line after every physics sub-tick (<see cref="TickOneSecondBySubTick"/>).
    /// </summary>
    private static Watch WatchAgainstTheLine(
        SfoGround ground,
        AircraftState aircraft,
        (AirportGroundLayout Layout, RunwayInfo Runway) line,
        int maxSeconds
    )
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        string? firstInside = null;
        int still = 0;
        for (int second = 1; (second <= maxSeconds) && (route.CurrentSegmentIndex == 0); second++)
        {
            int thisSecond = second;
            TickOneSecondBySubTick(
                ground.Engine,
                subTick =>
                {
                    string? part = BodyPoints(aircraft.Position, aircraft.TrueHeading.Degrees, aircraft.AircraftType)
                        .FirstOrDefault(p => FollowingPhase.IsInsideHoldLine(line.Layout, line.Runway, p.Point))
                        .Part;
                    firstInside ??= (part is null) ? null : $"{part} at {thisSecond}s sub-tick {subTick}";
                }
            );
            still = (aircraft.GroundSpeed < StillKts) ? still + 1 : 0;
        }

        return new Watch(firstInside, still, route.CurrentSegmentIndex > 0);
    }

    /// <summary>
    /// One sim-second of <paramref name="engine"/> as <see cref="SimulationEngine.TickOneSecond"/> runs it
    /// (<see cref="SimulationEngine.RunSecond"/> under the bare host), segment by segment, calling
    /// <paramref name="afterSubTick"/> with the sub-tick's index after each of its <see cref="SimulationEngine.PhysicsSubTickRate"/>
    /// physics sub-ticks. The engine has no sub-tick hook, so this is the finest step a bare test engine offers.
    /// </summary>
    internal static void TickOneSecondBySubTick(SimulationEngine engine, Action<int> afterSubTick)
    {
        ISimulationHost host = engine.BareHost;
        engine.BeginSecond();
        engine.OpenSecond(host);
        engine.RunPrePhysics(host);
        double subDelta = 1.0 / SimulationEngine.PhysicsSubTickRate;
        for (int subTick = 0; subTick < SimulationEngine.PhysicsSubTickRate; subTick++)
        {
            engine.RunPhysicsSubTick(subDelta, subTick);
            afterSubTick(subTick);
        }

        engine.RunPostPhysics(host);
        engine.RunEndOfSecond(host);
    }

    /// <summary>
    /// The nose, tail and wingtips of a <paramref name="type"/> at <paramref name="position"/> facing
    /// <paramref name="headingDeg"/>.
    /// </summary>
    internal static (string Part, LatLon Point)[] BodyPoints(LatLon position, double headingDeg, string type)
    {
        double halfLengthNm = AircraftLength.ResolveFt(type) / 2.0 / GeoMath.FeetPerNm;
        double halfSpanNm = AircraftFootprint.ResolveWingspanFt(type) / 2.0 / GeoMath.FeetPerNm;
        LatLon At(double offDeg, double distanceNm) => GeoMath.ProjectPoint(position, new TrueHeading((headingDeg + offDeg) % 360.0), distanceNm);
        return
        [
            ("nose", At(0.0, halfLengthNm)),
            ("right wingtip", At(90.0, halfSpanNm)),
            ("left wingtip", At(270.0, halfSpanNm)),
            ("tail", At(180.0, halfLengthNm)),
        ];
    }

    /// <summary>
    /// A <paramref name="type"/> at rest facing <paramref name="bar"/> from the taxiway side, holding short of its
    /// runway: its nose <see cref="NoseShortOfBarFt"/> short of the bar, backed off a foot at a time until no part of it (a
    /// wingtip leads on a taxiway meeting the runway at an angle) is inside the hold line. With it, the bar's runway.
    /// </summary>
    private (Staged Staged, RunwayInfo Runway) StageHoldingShort(SfoGround ground, string airport, GroundNode bar, string type)
    {
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar.Type);
        RunwayInfo runway = BarRunway(airport, bar);
        GroundNode approach = TaxiwaySideNeighbour(ground.Layout, bar, runway);
        double backFt = HoldingShortBackFt(ground.Layout, (bar, approach), runway, type);
        return (StageFacingBar(ground, (bar, approach), backFt, type), runway);
    }

    /// <summary>
    /// How far back (ft) from bar <c>bars.Bar</c>, on the taxiway leading from <c>bars.Approach</c>, a <paramref name="type"/>
    /// facing the bar holds short of it: its nose <see cref="NoseShortOfBarFt"/> short of the bar, backed off a foot at a time
    /// until no part of it is inside the hold line.
    /// </summary>
    private static double HoldingShortBackFt(AirportGroundLayout layout, (GroundNode Bar, GroundNode Approach) bars, RunwayInfo runway, string type)
    {
        double bearingDeg = GeoMath.BearingTo(bars.Approach.Position, bars.Bar.Position);
        var backDeg = new TrueHeading((bearingDeg + 180.0) % 360.0);
        double backFt = (AircraftLength.ResolveFt(type) / 2.0) + NoseShortOfBarFt;
        LatLon PositionAt(double ft) => GeoMath.ProjectPoint(bars.Bar.Position, backDeg, ft / GeoMath.FeetPerNm);
        while (BodyPoints(PositionAt(backFt), bearingDeg, type).Any(p => FollowingPhase.IsInsideHoldLine(layout, runway, p.Point)))
        {
            backFt += 1.0;
            Assert.True(backFt < MaxBackOffFt, $"the {type} is still inside the hold line of bar {bars.Bar.Id} {backFt:F0} ft back from it");
        }

        return backFt;
    }

    /// <summary>
    /// A <paramref name="type"/> at rest <paramref name="backFt"/> back from bar <c>bars.Bar</c> on the taxiway leading from
    /// <c>bars.Approach</c>, facing the bar, and the TAXI back to the first two nodes behind it.
    /// </summary>
    private Staged StageFacingBar(SfoGround ground, (GroundNode Bar, GroundNode Approach) bars, double backFt, string type)
    {
        (GroundNode bar, GroundNode approach) = bars;
        IGroundEdge barEdge = bar.Edges.First(e => (e is GroundEdge) && (e.OtherNode(bar) == approach));
        string taxiway = barEdge.TaxiwayName;
        double bearingDeg = GeoMath.BearingTo(approach.Position, bar.Position);
        var backDeg = new TrueHeading((bearingDeg + 180.0) % 360.0);
        LatLon position = GeoMath.ProjectPoint(bar.Position, backDeg, backFt / GeoMath.FeetPerNm);
        GroundNode[] behind = NodesBehind((bar, barEdge), (position, 1.0), backDeg);
        string command = $"TAXI #{behind[0].Id} #{behind[1].Id}";
        output.WriteLine(
            $"{type} at rest on {taxiway}, {backFt:F1} ft back from bar {bar.Id} ({bar.RunwayId}), "
                + $"{GeoMath.DistanceNm(bar.Position, approach.Position) * GeoMath.FeetPerNm:F1} ft past node {approach.Id}; "
                + $"heading {bearingDeg:F1}; "
                + $"nodes behind {behind[0].Id} at {GeoMath.DistanceNm(position, behind[0].Position) * GeoMath.FeetPerNm:F1} ft, "
                + $"{behind[1].Id} at {GeoMath.DistanceNm(position, behind[1].Position) * GeoMath.FeetPerNm:F1} ft"
        );

        var aircraft = new AircraftState
        {
            Callsign = "N509TA",
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(bearingDeg),
            TrueTrack = new TrueHeading(bearingDeg),
            Altitude = 6,
            IndicatedAirspeed = 0.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = ground.Layout.AirportId, Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        aircraft.Ground.CurrentTaxiway = taxiway;
        ground.Engine.World.AddAircraft(aircraft);
        return new Staged(aircraft, taxiway, command);
    }

    /// <summary>Sends <paramref name="command"/> to the aircraft, asserting it is accepted, and logs its route.</summary>
    private void Send(SfoGround ground, AircraftState aircraft, string command)
    {
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        SfoGroundHarness.DumpRoute(output, Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute));
    }

    /// <summary>The turn-about refusals <paramref name="aircraft"/> says on <paramref name="ground"/>'s engine from now on.</summary>
    private static List<string> CollectUnables(SfoGround ground, AircraftState aircraft)
    {
        List<string> unables = [];
        ground.Engine.WarningEmitted += (callsign, message) =>
        {
            if ((callsign == aircraft.Callsign) && message.Contains(UnableToTurnAround, StringComparison.OrdinalIgnoreCase))
            {
                unables.Add(message);
            }
        };
        return unables;
    }

    /// <summary>The runway end a hold short of <paramref name="runway"/> names for the aircraft where it stands.</summary>
    private static string ExpectedRunway(SfoGround ground, AircraftState aircraft, RunwayInfo runway) =>
        RunwayCrossingEnd.Spoken(aircraft, runway.Id.ToString(), ground.Layout, ActiveRunways.Empty);

    /// <summary>
    /// The first two nodes more than <c>start.MinBehindFt</c> behind <c>start.Position</c> walking back from the bar, away
    /// along <paramref name="backDeg"/>:
    /// at each node the straightest edge of the bar's taxiway, or of any taxiway when the bar's ends there.
    /// </summary>
    private static GroundNode[] NodesBehind((GroundNode Bar, IGroundEdge Edge) from, (LatLon Position, double MinBehindFt) start, TrueHeading backDeg)
    {
        List<GroundNode> behind = [];
        GroundNode node = from.Bar;
        IGroundEdge edge = from.Edge;
        for (int i = 0; (i < MaxWalkNodes) && (behind.Count < 2); i++)
        {
            GroundNode next = edge.OtherNode(node);
            if ((GeoMath.AlongTrackDistanceNm(next.Position, start.Position, backDeg) * GeoMath.FeetPerNm) > start.MinBehindFt)
            {
                behind.Add(next);
            }

            double inDeg = GeoMath.BearingTo(node.Position, next.Position);
            IGroundEdge[] straight = [.. next.Edges.Where(e => (e != edge) && (e is GroundEdge))];
            IGroundEdge[] sameTaxiway = [.. straight.Where(e => e.MatchesTaxiway(from.Edge.TaxiwayName))];
            IGroundEdge? onward = (sameTaxiway.Length > 0 ? sameTaxiway : straight).MinBy(e =>
                GeoMath.AbsBearingDifference(GeoMath.BearingTo(next.Position, e.OtherNode(next).Position), inDeg)
            );
            Assert.NotNull(onward);
            node = next;
            edge = onward;
        }

        Assert.Equal(2, behind.Count);
        return [.. behind];
    }

    /// <summary>
    /// The neighbour of <paramref name="bar"/> along a straight edge on the taxiway side of its hold line: the lowest-id one a
    /// point <see cref="NoseShortOfBarFt"/> from the bar toward which is outside the line.
    /// </summary>
    private static GroundNode TaxiwaySideNeighbour(AirportGroundLayout layout, GroundNode bar, RunwayInfo runway) =>
        bar
            .Edges.OfType<GroundEdge>()
            .Select(e => e.OtherNode(bar))
            .Where(n =>
            {
                var toward = new TrueHeading(GeoMath.BearingTo(bar.Position, n.Position));
                return !FollowingPhase.IsInsideHoldLine(
                    layout,
                    runway,
                    GeoMath.ProjectPoint(bar.Position, toward, NoseShortOfBarFt / GeoMath.FeetPerNm)
                );
            })
            .OrderBy(n => n.Id)
            .First();

    /// <summary>
    /// The bar of <paramref name="runway"/> on <paramref name="nearBar"/>'s taxiway on the other side of the runway, the nearest
    /// to <paramref name="nearBar"/>, and its neighbour on that taxiway further from the runway's centreline.
    /// </summary>
    private (GroundNode Bar, GroundNode Past) FarBarAcross(AirportGroundLayout layout, GroundNode nearBar, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.Lat1, runway.Lon1);
        double Side(GroundNode n) => GeoMath.SignedCrossTrackDistanceNm(n.Position, threshold, runway.TrueHeading1);
        string taxiway = nearBar.Edges.OfType<GroundEdge>().First(e => !e.IsRunwayCenterline).TaxiwayName;
        GroundNode farBar = layout
            .Nodes.Values.Where(n =>
                (n.Type == GroundNodeType.RunwayHoldShort)
                && (n.RunwayId is { } id)
                && id.Overlaps(runway.Id)
                && (Math.Sign(Side(n)) == -Math.Sign(Side(nearBar)))
                && n.Edges.OfType<GroundEdge>().Any(e => e.MatchesTaxiway(taxiway))
            )
            .MinBy(n => GeoMath.DistanceNm(n.Position, nearBar.Position))!;
        GroundNode past = farBar
            .Edges.OfType<GroundEdge>()
            .Where(e => e.MatchesTaxiway(taxiway))
            .Select(e => e.OtherNode(farBar))
            .MaxBy(n => Math.Abs(Side(n)))!;
        output.WriteLine($"far {runway.Id} bar on {taxiway}: {farBar.Id}, past it {past.Id}");
        return (farBar, past);
    }

    /// <summary>The runway <paramref name="bar"/> holds short of, from the airport's runways.</summary>
    private static RunwayInfo BarRunway(string airport, GroundNode bar) =>
        RunwayOccupancy.AirportRunways(airport).First(r => r.Id.Overlaps(bar.RunwayId!.Value));

    /// <summary>An engine over the committed layout of <paramref name="airport"/>, or null when it or navdata is unavailable.</summary>
    private SfoGround? Build(string airport)
    {
        if (airport == "SFO")
        {
            return SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off);
        }

        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout(airport) is not { } layout)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = $"test-{airport.ToLowerInvariant()}-turn-about-at-a-bar",
                ScenarioName = $"{airport} turn about at a runway bar",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        return new SfoGround(engine, layout);
    }
}
