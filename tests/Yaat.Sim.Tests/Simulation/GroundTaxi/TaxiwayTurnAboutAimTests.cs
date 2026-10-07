using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Where the reversal of a turn about on a taxiway rolls out. The route turns about back to a junction node behind the
/// aircraft and then turns onto a branch there. When cutting straight onto the branch keeps the main gear on the
/// taxiways' pavement, the reversal is aimed past the junction at a node on the branch, with no jog. Otherwise it rolls
/// out on the taxiway's own bearing, rather than aimed at the node from a radius off the centreline, which overshoots the
/// centreline and has to be turned back. Rolled out on the inside of the turn at the junction, a short straight holds that
/// bearing to abeam the node; a long one, a wide main gear or a fillet next re-centres on the node as before.
/// </summary>
public class TaxiwayTurnAboutAimTests(ITestOutputHelper output)
{
    private const string N152spRecordingPath = "TestData/n152sp-ifr-cto-vfr-modifier-recording.yaat-bug-report-bundle.zip";
    private const string N152sp = "N152SP";

    /// <summary>The recording second of N152SP's <c>TAXI C E RWY 28R</c>, given about 30 ft past node 366 on D.</summary>
    private const int TaxiCommandSecond = 726;

    /// <summary>
    /// The most yaw (deg, absolute, summed) N152SP may make from the start of the reversal until it is established on H.
    /// </summary>
    private const double MaxN152spYawDeg = 410.0;

    /// <summary>How far (deg) the heading at the end of the reversal may lie from the reversed taxiway edge's bearing.</summary>
    private const double RollOutToleranceDeg = 3.0;

    /// <summary>Heading within this (deg) of the current segment's bearing: established on it.</summary>
    private const double EstablishedDeg = 3.0;

    /// <summary>
    /// Half the 25 ft width of a TDG 1A taxiway (AC 150/5300-13B Table 4-2), the paved half-width the layout gives no
    /// better figure for.
    /// </summary>
    private const double Tdg1AHalfWidthFt = 12.5;

    /// <summary>Layout edges with a node this close (ft) to the junction bound the paved envelope checked.</summary>
    private const double EnvelopeRadiusFt = 400.0;

    /// <summary>Straight pieces each fillet arc's Bézier is sampled as for the envelope and fillet-line checks.</summary>
    private const int ArcSamples = 24;

    private const int MaxReplaySubTicks = 120 * SimulationEngine.PhysicsSubTickRate;

    /// <summary>
    /// How far short of abeam the junction (ft) the held roll-out bearing is checked to: closer in, the rounding onto the
    /// branch may already have begun.
    /// </summary>
    private const double HoldCheckShortOfAbeamFt = 20.0;

    /// <summary>
    /// How far past the junction (ft) the aircraft stands in the junction cases: beyond the at-node tolerance, so its route
    /// starts with the free-space leg back to the junction rather than at the junction itself.
    /// </summary>
    private const double PastJunctionFt = AirportGroundLayout.AtNodeToleranceFt + 3.0;

    /// <summary>The branch leaves the junction at most this far (deg) off the taxiway ahead, so the cut is a shallow veer.</summary>
    private const double MaxBranchAngleDeg = 25.0;

    /// <summary>The branch leaves the junction at least this far (deg) off the taxiway ahead: a junction, not a kink.</summary>
    private const double MinBranchAngleDeg = 10.0;

    /// <summary>Straight edges at least this long (ft) on both sides of the junction.</summary>
    private const double MinEdgeFt = 35.0;

    private const int ReAimTickSeconds = 120;

    /// <summary>Turning this far (deg) away from the branch first is a jog.</summary>
    private const double NoJogToleranceDeg = 1.0;

    /// <summary>A re-aimed cut veers onto the branch; swinging further than this (deg) off the start heading is a turn about.</summary>
    private const double MaxReAimYawDeg = 90.0;

    /// <summary>Seconds compared after a snapshot restore.</summary>
    private const int SnapshotCompareSeconds = 25;

    /// <summary>Once established on the branch, the aircraft is within this (ft) of its centreline: the node turn exited on it.</summary>
    private const double OnCentrelineFt = 2.0;

    /// <summary>Sub-ticks replayed past establishment on H, while it is still on H, to watch it hold H's centreline.</summary>
    private const int SettleSubTicks = 10 * SimulationEngine.PhysicsSubTickRate;

    /// <summary>
    /// Within this (ft) of a segment's end the navigator blends the steer toward the next segment's bearing, so the
    /// centreline check stops there.
    /// </summary>
    private const double PreTurnBlendFt = 50.0;

    /// <summary>
    /// How far (ft) N152SP is moved on along D before its TAXI so the straight after its reversal exceeds six turning radii
    /// (25 ft on the C172's 4.2 ft turn-about radius): from 37 ft short of abeam node 366 to about 57 ft.
    /// </summary>
    private const double CapShiftAlongDFt = 20.0;

    /// <summary>
    /// How far (ft) N152SP is moved on along D before its TAXI, negative being back toward node 366, so the straight after
    /// its reversal is under six of the C172's turning radii (25 ft) and holds the roll-out bearing: from 37 ft short of
    /// abeam node 366 to 24 ft, the aircraft still clear of the node's at-node tolerance.
    /// </summary>
    private const double HeldStraightShiftAlongDFt = -13.0;

    /// <summary>Why the N152SP replays that need a tracked turn about on the C172's own radius are skipped.</summary>
    private const string Yaat438Skip =
        "YAAT-438: a rolling turn-about on the type's own radius runs at ~20 kt against an arc planned at pivot speed; "
        + "unskip when 438 brakes to pivot speed first";

    /// <summary>Six turning radii (<c>GroundNavigator.RollOutHoldMaxRadii</c>): the longest straight that holds the roll-out bearing.</summary>
    private const double RollOutHoldMaxRadii = 6.0;

    /// <summary>A held-short aircraft stops within this (ft) of the bar's node.</summary>
    private const double StopShortOfNodeMaxFt = 60.0;

    /// <summary>A stop square to the bar has the heading within this (deg) of the taxiway's bearing.</summary>
    private const double StopSquareMaxDeg = 3.0;

    /// <summary>The navigator's debug line when the straight after the reversal is laid on the roll-out bearing.</summary>
    private const string HoldingTheRollOutBearing = "; holding it ";

    /// <summary>The navigator's debug line when a node turn is laid from a turn about's offset line.</summary>
    private const string NodeTurnFromTheOffsetLine = "node turn from the offset line";

    /// <summary>The navigator's debug line when a type with no FAA record is refused the re-aim.</summary>
    private const string NoReAimWithoutGearWidth = "no re-aim: no FAA main-gear width for";

    /// <summary>The navigator's debug line when a type with no FAA record is refused the abeam hold.</summary>
    private const string NoHoldWithoutGearWidth = "; the turn about re-centres after its reversal";

    /// <summary>The navigator's debug line when the straight after the reversal is longer than the hold allows.</summary>
    private const string StraightTooLongToHold = "gear fits True, short enough False";

    /// <summary>Piston types a test may stand in for the C172 with; the first with no FAA aircraft record is used.</summary>
    private static readonly string[] PistonTypesMaybeWithoutFaaRecord =
    [
        "RV7",
        "RV6",
        "RV8",
        "RV9",
        "RV10",
        "RV12",
        "LNC2",
        "GLST",
        "VELO",
        "COZY",
        "KR2",
        "SX30",
        "PTS2",
        "EXTR",
        "EDGE",
        "G109",
        "DIMO",
        "AA5",
        "AA1",
        "BL8",
        "CH7A",
        "CH7B",
        "J3",
        "PA18",
        "PA38",
        "C150",
        "C152",
        "BE23",
        "M20P",
        "C77R",
        "P28A",
        "C182",
        "DA40",
        "SR22",
    ];

    /// <summary>
    /// N152SP (C172) is cleared <c>TAXI C E RWY 28R</c> on KOAK taxiway D just past node 366 (moved
    /// <see cref="HeldStraightShiftAlongDFt"/> from its recorded pose about 30 ft past it), heading east, with H
    /// leaving node 366 behind it to the south-southwest. The bend onto H is sharp and against the reversal's sense, but the
    /// cut straight onto H leaves the 8.3 ft the C172's gear allows either side of D's and H's centrelines, so the pavement
    /// check refuses the re-aim and it turns about on D: the jog, then the reversal, which must roll out on D's bearing back
    /// toward node 366 rather than overshoot it aiming at the node. From the reversal's start until established on H it yaws
    /// no more than <see cref="MaxN152spYawDeg"/>.
    /// </summary>
    [Fact]
    public void N152sp_TurnAboutOnD_RollsOutOnDsBearing()
    {
        if (ReplayN152spTurnAbout(null, HeldStraightShiftAlongDFt, null) is not { } run)
        {
            return;
        }

        double rollOutDeg = run.Track.PeakHeadingDeg;
        double reversalYawDeg = run.Track.AbsYawFromDeg(run.Track.JogEndIndex);
        output.WriteLine(
            $"junction {run.Junction.Id}, outgoing {run.Outgoing.TaxiwayName} {run.Outgoing.Edge.DepartureBearing:F1}°, "
                + $"D reversed {run.ReversedBearingDeg:F1}°; yaw {run.Track.TotalAbsDeg:F0}° from the TAXI, {reversalYawDeg:F0}° from the "
                + $"reversal's start; reversal rolled out on {rollOutDeg:F1}°"
        );
        LogReversalStart(run.Poses, run.Track, run.Junction, run.Outgoing);
        output.WriteLine($"headings: {string.Join(" ", run.Poses.Select(p => p.HeadingDeg.ToString("F0")))}");

        Assert.True(
            reversalYawDeg <= MaxN152spYawDeg,
            $"N152SP yawed {reversalYawDeg:F0}° from the reversal's start to established on {run.Outgoing.TaxiwayName}, above {MaxN152spYawDeg:F0}°"
        );
        Assert.True(
            GeoMath.AbsBearingDifference(rollOutDeg, run.ReversedBearingDeg) <= RollOutToleranceDeg,
            $"the reversal rolled out on {rollOutDeg:F1}°, not on D's reversed bearing {run.ReversedBearingDeg:F1}° (±{RollOutToleranceDeg:F0}°)"
        );
    }

    /// <summary>
    /// N152SP's reversal, from <see cref="HeldStraightShiftAlongDFt"/> along D, ends a turning radius off D on the inside of
    /// the turn onto H, 24 ft short of abeam node 366 — under six turning radii — and its gear fits a TDG 1A taxiway at
    /// that offset: it holds D's reversed bearing to abeam the node instead of steering back out to the centreline only to
    /// turn in again at the node.
    /// </summary>
    [Fact(Skip = Yaat438Skip)]
    public void N152sp_TurnAboutOnD_HoldsTheRollOutBearingToAbeamTheNode()
    {
        if (ReplayN152spTurnAbout(null, HeldStraightShiftAlongDFt, null) is not { } run)
        {
            return;
        }

        List<(double ShortFt, double OffDeg)> held =
        [
            .. PosesAfterRollOut(run.Poses, run.Track, run.ReversedBearingDeg)
                .Select(p =>
                    (
                        ShortFt: AlongToJunctionFt(p.Position, run.Junction, run.ReversedBearingDeg),
                        OffDeg: GeoMath.AbsBearingDifference(p.HeadingDeg, run.ReversedBearingDeg)
                    )
                )
                .TakeWhile(p => p.ShortFt > HoldCheckShortOfAbeamFt),
        ];
        output.WriteLine($"short of abeam (ft) / off D's bearing (deg): {string.Join(" ", held.Select(h => $"{h.ShortFt:F0}/{h.OffDeg:F1}"))}");
        Assert.NotEmpty(held);
        Assert.All(held, h => Assert.True(h.OffDeg <= RollOutToleranceDeg, $"{h.ShortFt:F0} ft short of abeam the heading was {h.OffDeg:F1}° off D"));
    }

    /// <summary>
    /// After holding D's reversed bearing a turning radius inside the turn onto H, N152SP's turn onto H is laid tangent to
    /// H's centreline from that offset line: its heading converges on H's bearing without passing it by more than
    /// <see cref="EstablishedDeg"/>, and from the moment it is established it stays within <see cref="OnCentrelineFt"/> of
    /// H's centreline. Rounded as if from D's centreline, the same arc ends inside H's line and pure pursuit swings 15° past
    /// H's bearing to reach it.
    /// </summary>
    [Fact]
    public void N152sp_TurnAboutOnD_TurnsOntoHTangentToItsCentreline()
    {
        if (ReplayN152spTurnAbout(null, HeldStraightShiftAlongDFt, null) is not { } run)
        {
            return;
        }

        int establishedIndex = run.Poses.Count - 1;
        int establishedOnSegment = run.Route.CurrentSegmentIndex;
        for (int sub = 0; (sub < SettleSubTicks) && (run.Route.CurrentSegmentIndex == 1); sub++)
        {
            run.Engine.ReplayOneSubTick();
            if (run.Route.CurrentSegmentIndex == 1)
            {
                run.Poses.Add((run.Aircraft.Position, run.Aircraft.TrueHeading.Degrees));
            }
        }

        double branchBearingDeg = run.Outgoing.Edge.DepartureBearing;
        double sense = Math.Sign(GeoMath.SignedBearingDifference(run.ReversedBearingDeg, branchBearingDeg));
        List<double> pastDeg =
        [
            .. PosesAfterRollOut(run.Poses, run.Track, run.ReversedBearingDeg)
                .TakeWhile(p => GeoMath.AbsBearingDifference(p.HeadingDeg, branchBearingDeg) > EstablishedDeg)
                .Select(p => sense * GeoMath.SignedBearingDifference(branchBearingDeg, p.HeadingDeg)),
        ];
        LatLon branchEnd = run.Outgoing.Edge.ToNode.Position;
        List<double> offLineFt =
        [
            .. run
                .Poses.Skip(establishedIndex)
                .Where(p => (GeoMath.DistanceNm(p.Position, branchEnd) * GeoMath.FeetPerNm) > PreTurnBlendFt)
                .Select(p =>
                    Math.Abs(GeoMath.SignedCrossTrackDistanceNm(p.Position, run.Junction.Position, new TrueHeading(branchBearingDeg)))
                    * GeoMath.FeetPerNm
                ),
        ];
        output.WriteLine(
            $"past {run.Outgoing.TaxiwayName}'s bearing before established (deg): {string.Join(" ", pastDeg.Select(d => d.ToString("F1")))}"
        );
        output.WriteLine(
            $"off {run.Outgoing.TaxiwayName}'s centreline once established (ft): {string.Join(" ", offLineFt.Select(d => d.ToString("F1")))}"
        );
        Assert.True(
            establishedOnSegment == 1,
            $"N152SP left {run.Outgoing.TaxiwayName} (now on segment {establishedOnSegment}) without being established on it"
        );
        Assert.NotEmpty(pastDeg);
        Assert.NotEmpty(offLineFt);
        Assert.True(
            pastDeg.Max() <= EstablishedDeg,
            $"the heading swung {pastDeg.Max():F1}° past {run.Outgoing.TaxiwayName}'s bearing {branchBearingDeg:F1}°"
        );
        Assert.All(
            offLineFt,
            ft => Assert.True(ft <= OnCentrelineFt, $"established on {run.Outgoing.TaxiwayName} but {ft:F1} ft off its centreline")
        );
    }

    /// <summary>
    /// N152SP moved <see cref="CapShiftAlongDFt"/> on along D before its TAXI: its reversal now ends more than six turning
    /// radii short of abeam node 366, so the straight after it re-centres on the node instead of holding the offset, and
    /// the navigator says why.
    /// </summary>
    [Fact]
    public void N152spFurtherAlongD_TurnAboutOnD_ReCentresOverALongStraight()
    {
        var capture = DebugLogCapture.Install(StraightTooLongToHold, "short enough");
        if (ReplayN152spTurnAbout(null, CapShiftAlongDFt, capture) is not { } run)
        {
            return;
        }

        output.WriteLine(string.Join(Environment.NewLine, capture.Lines));
        string type = run.Aircraft.AircraftType;
        double radiusFt = TurnAboutFit.Evaluate(type, AircraftCategorization.Categorize(type)).RadiusFt;
        double straightFt = AlongToJunctionFt(run.Poses[run.Track.JogEndIndex].Position, run.Junction, run.ReversedBearingDeg);
        output.WriteLine($"reversal began {straightFt:F0} ft short of abeam node {run.Junction.Id}; cap {RollOutHoldMaxRadii * radiusFt:F0} ft");
        Assert.Contains(capture.Lines, l => l.Contains(StraightTooLongToHold, StringComparison.Ordinal));
        AssertReCentres(run);
    }

    /// <summary>
    /// N152SP flown as a piston type with no FAA aircraft characteristics record: with no main-gear width to check the
    /// pavement against, the turn about neither re-aims past node 366 nor holds its roll-out bearing — it re-centres on the
    /// node — and the navigator logs both refusals.
    /// </summary>
    [Fact]
    public void N152spAsTypeWithoutFaaRecord_TurnAboutOnD_ReCentresOnTheNode()
    {
        TestVnasData.EnsureInitialized();
        string type = PistonTypeWithoutFaaRecord();
        output.WriteLine($"type {type}: category {AircraftCategorization.Categorize(type)}, FAA record {FaaAircraftDatabase.Get(type) is not null}");
        var capture = DebugLogCapture.Install(NoReAimWithoutGearWidth, NoHoldWithoutGearWidth, "re-aim");
        if (ReplayN152spTurnAbout(type, 0.0, capture) is not { } run)
        {
            return;
        }

        output.WriteLine(string.Join(Environment.NewLine, capture.Lines));
        Assert.Contains(capture.Lines, l => l.Contains(NoReAimWithoutGearWidth, StringComparison.Ordinal));
        Assert.Contains(capture.Lines, l => l.Contains(NoHoldWithoutGearWidth, StringComparison.Ordinal));
        AssertReCentres(run);
    }

    /// <summary>
    /// The first of <see cref="PistonTypesMaybeWithoutFaaRecord"/> the loaded specs call a piston and the loaded FAA
    /// aircraft characteristics data has no record for.
    /// </summary>
    private static string PistonTypeWithoutFaaRecord()
    {
        Assert.True(FaaAircraftDatabase.IsInitialized, "the FAA aircraft characteristics data is not loaded");
        return PistonTypesMaybeWithoutFaaRecord.FirstOrDefault(t =>
                AircraftCategorization.IsKnownType(t)
                && (AircraftCategorization.Categorize(t) == AircraftCategory.Piston)
                && (FaaAircraftDatabase.Get(t) is null)
            ) ?? throw new InvalidOperationException("every candidate piston type has an FAA aircraft record");
    }

    /// <summary>
    /// N152SP snapshotted part-way round its reversal, with the straight after it set to hold the roll-out bearing: the
    /// restored aircraft stays on the original's positions and heading through the reversal, the held straight and the
    /// turn onto H.
    /// </summary>
    [Fact(Skip = Yaat438Skip)]
    public void N152sp_TurnAbout_SurvivesSnapshotRoundTripMidReversal() =>
        AssertSnapshotRoundTrip(s => ReversalPlaying(s) && RollsOutAlongEdge(s), "mid-reversal with the roll-out hold set");

    /// <summary>
    /// N152SP snapshotted on the held straight after its reversal, short of abeam node 366: the snapshot carries the
    /// offset the straight is held at, and the restored aircraft keeps holding the roll-out bearing to abeam the node, on
    /// the original's positions and heading.
    /// </summary>
    [Fact(Skip = Yaat438Skip)]
    public void N152sp_TurnAbout_SurvivesSnapshotRoundTripOnTheHeldStraight()
    {
        bool seenHold = false;
        StateSnapshotDto? picked = AssertSnapshotRoundTrip(
            snapshot =>
            {
                bool holding = RollsOutAlongEdge(snapshot);
                bool onStraight = seenHold && !holding;
                seenHold |= holding;
                return onStraight;
            },
            "on the held straight"
        );
        Assert.True((picked is null) || (RollOutOffsetFt(picked) > 0.0), "the snapshot on the held straight carries no roll-out offset");
    }

    /// <summary>
    /// N152SP snapshotted part-way round its turn onto H, laid from the offset line it held after the reversal (a slow turn
    /// with the roll-out offset still set, which only that node turn carries): the restored aircraft stays on the
    /// original's positions and heading through the rest of the turn and along H.
    /// </summary>
    [Fact(Skip = Yaat438Skip)]
    public void N152sp_TurnAbout_SurvivesSnapshotRoundTripMidNodeTurnFromTheOffsetLine() =>
        AssertSnapshotRoundTrip(
            s => OnSegment(s, 1) && (RollOutOffsetFt(s) > 0.0) && PlaysSlowTurn(s),
            "mid-way round the node turn from the offset line"
        );

    /// <summary>
    /// N152SP snapshotted part-way round its turn onto H from the offset line, the turn then marked as aimed past H's first
    /// leg at a later leg that turns off H's bearing, as an entry-alignment reversal aimed at a node beyond its own segment
    /// is: when the turn completes, the legs it was aimed past are retired and the later leg is set up with an alignment
    /// turn of its own. That set-up is not laid from the offset line, so the restored navigator logs no node turn from the
    /// offset line onto the later leg.
    ///
    /// <para>
    /// The marking stands in for a real case: a node turn of at least <c>GroundNavigator.ReversalEntryThresholdDeg</c>
    /// laid from a held straight, which would aim past its own leg, was searched for in the committed KOAK and KSFO
    /// layouts by code-path analysis and none was found, since the hold only runs into a turn to the side the reversal
    /// ends on, onto a straight edge.
    /// </para>
    /// </summary>
    [Fact(Skip = Yaat438Skip)]
    public void N152sp_NodeTurnAimedPastItsLeg_DropsTheRollOutOffsetWhenItRetiresTheLegs()
    {
        if (StartRestoredN152spTaxi() is not { } live)
        {
            return;
        }

        TaxiRoute liveRoute = Assert.IsType<TaxiRoute>(live.Aircraft.Ground.AssignedTaxiRoute);
        StateSnapshotDto picked = Assert.IsType<StateSnapshotDto>(TickUntilPicked(live.Engine, s => (RollOutOffsetFt(s) > 0.0) && PlaysSlowTurn(s)));
        double hBearingDeg = liveRoute.Segments[1].Edge.DepartureBearing;
        int aimedPast = Enumerable
            .Range(2, liveRoute.Segments.Count - 2)
            .First(i =>
                (liveRoute.Segments[i].Edge.Edge is GroundEdge)
                && (
                    GeoMath.AbsBearingDifference(liveRoute.Segments[i].Edge.DepartureBearing, hBearingDeg)
                    > GroundNavigator.EntryAlignmentThresholdDeg
                )
            );
        JsonNode json = Assert.IsAssignableFrom<JsonNode>(JsonSerializer.SerializeToNode(picked, RecordingJsonOptions.Default));
        JsonObject playback = Assert.Single(
            GroundNavigatorArcRestoreTests.PlaybackObjects(json),
            p => p[nameof(GroundNavigatorPlaybackDto.TurnAboutRollOutOffsetFt)] is not null
        );
        playback[nameof(GroundNavigatorPlaybackDto.AimedAtRouteNode)] = true;
        playback[nameof(GroundNavigatorPlaybackDto.NodeAimSegmentIndex)] = aimedPast;
        playback[nameof(GroundNavigatorPlaybackDto.AimedPastThroughSegmentIndex)] = aimedPast;
        int laterLegFromNode = liveRoute.Segments[aimedPast].FromNodeId;
        output.WriteLine(
            $"node turn marked as aimed past H at segment {aimedPast} from node {laterLegFromNode} "
                + $"({liveRoute.Segments[aimedPast].Edge.DepartureBearing:F1}°)"
        );

        var capture = DebugLogCapture.Install(NodeTurnFromTheOffsetLine);
        var restoredEngine = new SimulationEngine(new TestAirportGroundData()) { Scenario = live.Engine.Scenario };
        restoredEngine.RestoreFromSnapshot(Assert.IsType<StateSnapshotDto>(json.Deserialize<StateSnapshotDto>(RecordingJsonOptions.Default)));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(N152sp));
        TaxiRoute restoredRoute = Assert.IsType<TaxiRoute>(restored.Ground.AssignedTaxiRoute);
        for (int second = 0; (second < SnapshotCompareSeconds) && (restoredRoute.CurrentSegmentIndex < aimedPast); second++)
        {
            restoredEngine.TickOneSecond();
        }

        output.WriteLine($"segment {restoredRoute.CurrentSegmentIndex}; {NodeTurnFromTheOffsetLine} lines: {string.Join(" | ", capture.Lines)}");
        Assert.Equal(aimedPast, restoredRoute.CurrentSegmentIndex);
        Assert.DoesNotContain(capture.Lines, l => l.Contains($"{NodeTurnFromTheOffsetLine}: node {laterLegFromNode} ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same N152SP turn about on D: from the TAXI until established on H, at every sample, both main-gear edges stay
    /// inside the paved envelope of D, H and their fillet.
    /// </summary>
    [Fact(Skip = "YAAT-438: the turn-about plays at ~19 kt; the brake leg to pivot speed makes this pass")]
    public void N152sp_TurnAboutOnD_GearStaysOnPavement()
    {
        if (ReplayN152spTurnAbout(null, 0.0, null) is not { } run)
        {
            return;
        }

        double gearHalfFt = GearHalfWidthFt(run.Aircraft.AircraftType);
        List<(LatLon A, LatLon B)> pavement = PavedCentrelines(run.Layout, run.Junction, run.D.TaxiwayName, run.Outgoing.TaxiwayName);
        output.WriteLine(
            $"gear half {gearHalfFt:F2} ft; excess per sample: "
                + string.Join(" ", run.Poses.Select(p => WorstGearExcessFt([p], pavement, gearHalfFt).ToString("F1")))
        );
        double worstExcessFt = WorstGearExcessFt(run.Poses, pavement, gearHalfFt);
        Assert.True(
            worstExcessFt <= 0.0,
            $"a main-gear edge ran {worstExcessFt:F1} ft outside the {Tdg1AHalfWidthFt:F1} ft half-width of D, "
                + $"{run.Outgoing.TaxiwayName} and their fillet"
        );
    }

    /// <summary>
    /// A C172 stopped on a real KOAK or SFO taxiway edge a few feet past a junction where a second taxiway leaves at an acute
    /// angle behind it, cleared onto that branch. The bend at the junction is a reversal opposite to the turn about's, and
    /// the cut onto the branch stays within the branch's or the taxiway's centreline less half the main-gear width, so the
    /// reversal is aimed past the junction at a node on the branch: no jog, no turn back, the gear on pavement throughout.
    /// </summary>
    [Fact]
    public void TurnAbout_ReAimsPastTheNode_WhenTheCutStaysOnPavement()
    {
        (string airportId, AirportGroundLayout layout, AcuteBranch branch, SimulationEngine engine, AircraftState aircraft, TaxiRoute route) =
            StartAcuteBranchTaxi("C172");
        GroundNode junction = branch.Junction;
        double headingDeg = aircraft.TrueHeading.Degrees;
        double gearHalfFt = GearHalfWidthFt(aircraft.AircraftType);

        var track = new YawTrack(headingDeg);
        List<(LatLon Position, double HeadingDeg)> poses = [(aircraft.Position, headingDeg)];
        int establishedAt = SfoGroundHarness.TickUntil(
            engine,
            () => EstablishedOnOrPast(aircraft, route, 1),
            ReAimTickSeconds,
            _ =>
            {
                track.Add(aircraft.TrueHeading.Degrees);
                poses.Add((aircraft.Position, aircraft.TrueHeading.Degrees));
            }
        );
        double towardBranch = Math.Sign(
            GeoMath.SignedBearingDifference(headingDeg, branch.Branch.Directed(junction, branch.Branch.OtherNode(junction)).DepartureBearing)
        );
        output.WriteLine(
            $"established after {establishedAt}s at seg {route.CurrentSegmentIndex}; yaw {track.TotalAbsDeg:F0}°, "
                + $"most against the branch {track.MostAgainstDeg(towardBranch):F1}°, peak {track.PeakAbsDeg:F0}°"
        );
        Assert.True(establishedAt > 0, $"the aircraft was not established on {branch.Branch.TaxiwayName} within {ReAimTickSeconds}s");

        List<(LatLon A, LatLon B)> pavement = PavedCentrelines(layout, junction, branch.Occupied.TaxiwayName, branch.Branch.TaxiwayName);
        double worstExcessFt = WorstGearExcessFt(poses, pavement, gearHalfFt);
        Assert.True(
            track.MostAgainstDeg(towardBranch) <= NoJogToleranceDeg,
            $"the aircraft turned {track.MostAgainstDeg(towardBranch):F1}° away from the branch first: a jog was played"
        );
        Assert.True(
            track.PeakAbsDeg <= MaxReAimYawDeg,
            $"the aircraft swung {track.PeakAbsDeg:F0}° off its heading: it turned about to the junction"
        );
        Assert.True(worstExcessFt <= 0.0, $"a main-gear edge ran {worstExcessFt:F1} ft outside the {Tdg1AHalfWidthFt:F1} ft taxiway half-width");
    }

    /// <summary>
    /// The acute-branch taxi flown by small types whose nose gear, a wheelbase ahead of the main gear, swings further out
    /// than the main gear on a tight turn (√(R² + WB²) − R above half the main-gear width, R the type's turn-about radius):
    /// from the TAXI until established on the branch, whether the reversal is aimed past the junction or turned about, the
    /// nose gear stays within the type's TDG half-width of the occupied taxiway's, the branch's or their fillet's
    /// centreline.
    /// </summary>
    [Theory]
    [InlineData("A5")]
    [InlineData("C240")]
    [InlineData("C77R")]
    [InlineData("BE20")]
    public void TurnAbout_AcuteBranch_NoseGearStaysOnPavement(string type)
    {
        (_, AirportGroundLayout layout, AcuteBranch branch, SimulationEngine engine, AircraftState aircraft, TaxiRoute route) = StartAcuteBranchTaxi(
            type
        );
        List<(LatLon Position, double HeadingDeg)> poses = [(aircraft.Position, aircraft.TrueHeading.Degrees)];
        int establishedAt = SfoGroundHarness.TickUntil(
            engine,
            () => EstablishedOnOrPast(aircraft, route, 1),
            ReAimTickSeconds,
            _ => poses.Add((aircraft.Position, aircraft.TrueHeading.Degrees))
        );
        Assert.True(establishedAt > 0, $"the {type} was not established on {branch.Branch.TaxiwayName} within {ReAimTickSeconds}s");

        double wheelbaseFt =
            FaaAircraftDatabase.Get(type)?.WheelbaseFt ?? throw new InvalidOperationException($"the FAA database has no {type} wheelbase");
        double halfWidthFt = TurnAboutFit.Evaluate(type, AircraftCategorization.Categorize(type)).HalfWidthFt;
        List<(LatLon A, LatLon B)> pavement = PavedCentrelines(layout, branch.Junction, branch.Occupied.TaxiwayName, branch.Branch.TaxiwayName);
        double worstExcessFt = poses.Max(p =>
            pavement.Min(l =>
                GeoMath.DistanceToSegmentFt(
                    GeoMath.ProjectPoint(p.Position, new TrueHeading(p.HeadingDeg), wheelbaseFt / GeoMath.FeetPerNm),
                    l.A,
                    l.B
                )
            ) - halfWidthFt
        );
        output.WriteLine(
            $"{type}: established after {establishedAt}s; nose gear worst {worstExcessFt:F2} ft outside the {halfWidthFt:F1} ft half-width"
        );
        Assert.True(worstExcessFt <= 0.0, $"the {type}'s nose gear ran {worstExcessFt:F2} ft outside the {halfWidthFt:F1} ft half-width");
    }

    /// <summary>
    /// The same acute-branch taxi with a hold short at the junction itself, put on the route as the parser's <c>HS</c> would
    /// (the command surface cannot place one at the bend node) and the taxi phase started again on the route so its set-up
    /// sees the bar: a cut past the junction would drive through the bar without arriving at it, so the reversal is not
    /// re-aimed. The aircraft turns about and stops at the bar.
    /// </summary>
    [Fact]
    public void TurnAbout_HoldingShortAtTheBendNode_IsNotReAimedPastIt()
    {
        AcuteBranchTaxi taxi = StartAcuteBranchTaxi("C172");
        GroundNode junction = taxi.Branch.Junction;
        taxi.Route.HoldShortPoints.Add(
            new HoldShortPoint
            {
                NodeId = junction.Id,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = taxi.Branch.Branch.TaxiwayName,
            }
        );
        RestartTaxiOnRoute(taxi.Aircraft, taxi.Layout);
        var track = new YawTrack(taxi.Aircraft.TrueHeading.Degrees);
        int heldAt = SfoGroundHarness.TickUntil(
            taxi.Engine,
            () => taxi.Aircraft.Phases?.CurrentPhase is HoldingShortPhase,
            ReAimTickSeconds,
            _ => track.Add(taxi.Aircraft.TrueHeading.Degrees)
        );
        double toJunctionFt = GeoMath.DistanceNm(taxi.Aircraft.Position, junction.Position) * GeoMath.FeetPerNm;
        double towardJunctionDeg = GeoMath.BearingTo(taxi.Branch.Occupied.OtherNode(junction).Position, junction.Position);
        double shortFt = AlongToJunctionFt(taxi.Aircraft.Position, junction, towardJunctionDeg);
        double offCentrelineFt = OffCentrelineFt(taxi.Aircraft.Position, junction, towardJunctionDeg);
        output.WriteLine(
            $"holding after {heldAt}s at seg {taxi.Route.CurrentSegmentIndex}, {toJunctionFt:F1} ft from node {junction.Id} "
                + $"({shortFt:F1} ft short of it, {offCentrelineFt:F1} ft off the centreline); yaw {track.TotalAbsDeg:F0}°, "
                + $"peak {track.PeakAbsDeg:F0}°"
        );
        Assert.True(heldAt > 0, $"the aircraft was not holding short within {ReAimTickSeconds}s");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(taxi.Aircraft.Phases?.CurrentPhase);
        Assert.Equal(junction.Id, holding.HoldShort.NodeId);
        Assert.True(
            track.PeakAbsDeg > MaxReAimYawDeg,
            $"the aircraft swung only {track.PeakAbsDeg:F0}°: it cut past the bar instead of turning about"
        );
        Assert.True(shortFt >= 0.0, $"the aircraft stopped {-shortFt:F1} ft past node {junction.Id}");
        Assert.True(toJunctionFt <= StopShortOfNodeMaxFt, $"the aircraft stopped {toJunctionFt:F1} ft from node {junction.Id}");
        Assert.True(
            offCentrelineFt <= OnCentrelineFt,
            $"the aircraft stopped {offCentrelineFt:F1} ft off {taxi.Branch.Occupied.TaxiwayName}'s centreline at the bar"
        );
    }

    /// <summary>
    /// N152SP's turn about on D with a hold short at node 366, put on the route as the parser's <c>HS</c> would and the taxi
    /// phase started again on it from rest (the replay carries it into the turn about at its taxi speed, too fast to stop at
    /// the node: YAAT-438): the straight after the reversal ends in a stop, so it re-centres rather than holding D's
    /// bearing to abeam the node, laid on D's centreline through the stop: the aircraft stops within
    /// <see cref="OnCentrelineFt"/> of D's centreline and within <see cref="StopSquareMaxDeg"/> of D's bearing. A stop
    /// beside the bar would leave the turn from rest onto H to start a radius inside it.
    /// </summary>
    [Fact]
    public void N152spHoldingShortAtTheNode_TurnAboutOnD_StopsOnTheCentreline()
    {
        if (HoldN152spAtNode366() is not { } held)
        {
            return;
        }

        AircraftState aircraft = held.Aircraft;
        double stopOffFt = OffCentrelineFt(aircraft.Position, held.Junction, held.DBearingDeg);
        double stopShortFt = AlongToJunctionFt(aircraft.Position, held.Junction, held.DBearingDeg);
        double stopFromNodeFt = GeoMath.DistanceNm(aircraft.Position, held.Junction.Position) * GeoMath.FeetPerNm;
        double stopOffDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, held.DBearingDeg);
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine(
            $"held at node {holding.HoldShort.NodeId}, {stopFromNodeFt:F1} ft from node "
                + $"{held.Junction.Id} ({stopShortFt:F1} ft short of it along D {held.DBearingDeg:F1}°), {stopOffFt:F1} ft off D's centreline, "
                + $"heading {stopOffDeg:F1}° off D's bearing"
        );
        Assert.Equal(held.Junction.Id, holding.HoldShort.NodeId);
        Assert.True(stopShortFt >= 0.0, $"N152SP stopped {-stopShortFt:F1} ft past node {held.Junction.Id}");
        Assert.True(stopOffFt <= OnCentrelineFt, $"N152SP stopped {stopOffFt:F1} ft off D's centreline at the bar");
        Assert.True(stopOffDeg <= StopSquareMaxDeg, $"N152SP stopped with its heading {stopOffDeg:F1}° off D's bearing {held.DBearingDeg:F1}°");
    }

    /// <summary>
    /// N152SP held short at node 366 after its turn about on D (<see cref="HoldN152spAtNode366"/>), cleared on (<c>RES</c>):
    /// it turns onto H from rest, and from the moment it is established on H up to <see cref="PreTurnBlendFt"/> short of H's
    /// next node it stays within <see cref="OnCentrelineFt"/> of H's centreline.
    /// </summary>
    [Fact(Skip = "YAAT-467: the from-rest turn at a junction node starts at the vertex and ends ~19 ft off H's centreline")]
    public void N152spFromRestAtNode366_TurnsOntoH_StaysWithin2FtOnceEstablished()
    {
        if (HoldN152spAtNode366() is not { } held)
        {
            return;
        }

        TaxiRouteSegment outgoing = held.Route.Segments[1];
        CommandResult resume = held.Engine.SendCommand(N152sp, "RES");
        Assert.True(resume.Success, $"RES was refused: {resume.Message}");
        List<double> exitOffFt = ExitOffCentrelineFt(ReplayPosesOnSegmentOne(held.Engine, held.Aircraft, held.Route), outgoing, held.Junction);
        output.WriteLine(
            $"off {outgoing.TaxiwayName}'s centreline from established to {PreTurnBlendFt:F0} ft short of node {outgoing.ToNodeId} (ft): "
                + string.Join(" ", exitOffFt.Select(d => d.ToString("F1")))
        );
        Assert.NotEmpty(exitOffFt);
        Assert.All(exitOffFt, ft => Assert.True(ft <= OnCentrelineFt, $"established on {outgoing.TaxiwayName} but {ft:F1} ft off its centreline"));
    }

    private sealed record HeldAtNode366(SimulationEngine Engine, AircraftState Aircraft, TaxiRoute Route, GroundNode Junction, double DBearingDeg);

    /// <summary>
    /// N152SP's turn about on D with a hold short at node 366 on the route from the start, the taxi phase started again on
    /// it from rest, replayed until it holds short there; with D's bearing toward the node. Null when the recording is not
    /// available.
    /// </summary>
    private HeldAtNode366? HoldN152spAtNode366()
    {
        if (StartN152spTaxiToTheBarAtNode366() is not { } held)
        {
            return null;
        }

        for (int sub = 0; (sub < MaxReplaySubTicks) && (held.Aircraft.Phases?.CurrentPhase is not HoldingShortPhase); sub++)
        {
            held.Engine.ReplayOneSubTick();
        }

        Assert.IsType<HoldingShortPhase>(held.Aircraft.Phases?.CurrentPhase);
        return held;
    }

    /// <summary>
    /// N152SP's TAXI replayed until its route is assigned, a hold short at node 366 put on the route and the taxi phase
    /// started again on it from rest; with D's bearing toward the node. Null when the recording is not available.
    /// </summary>
    private HeldAtNode366? StartN152spTaxiToTheBarAtNode366()
    {
        if (StartN152spReplay(null, 0.0, null) is not { } started)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft) = started;
        (LatLon Position, double HeadingDeg) atTaxi = (aircraft.Position, aircraft.TrueHeading.Degrees);
        ReplayUntilNewRoute(engine, aircraft);
        return PutBarOnNode366FromRest(engine, aircraft, atTaxi);
    }

    /// <summary>
    /// Put a hold short at node 366, the to-node of <paramref name="aircraft"/>'s taxi route's first segment, on that route
    /// and start the taxi phase again on it from rest; with D's bearing toward the node, D being the edge N152SP stood
    /// inside at its TAXI (<paramref name="atTaxi"/>).
    /// </summary>
    private static HeldAtNode366 PutBarOnNode366FromRest(SimulationEngine engine, AircraftState aircraft, (LatLon Position, double HeadingDeg) atTaxi)
    {
        AirportGroundLayout layout = aircraft.Ground.Layout ?? throw new InvalidOperationException("N152SP has no ground layout");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException("N152SP has no taxi route");
        List<(LatLon Position, double HeadingDeg)> poses = [atTaxi];
        GroundNode junction = route.Segments[0].Edge.ToNode;
        TaxiRouteSegment outgoing = route.Segments[1];
        // A bar on the node itself: the parser's setback would land on the 30 ft free-space leg's start, where N152SP stands.
        route.HoldShortPoints.Add(
            new HoldShortPoint
            {
                NodeId = junction.Id,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = outgoing.TaxiwayName,
            }
        );
        // From rest: at the replay's taxi speed the turn about cannot stop at the node (YAAT-438).
        aircraft.IndicatedAirspeed = 0.0;
        RestartTaxiOnRoute(aircraft, layout);
        poses.Add((aircraft.Position, aircraft.TrueHeading.Degrees));
        GroundEdge d = OccupiedEdgeTo(layout, poses, junction);
        return new HeldAtNode366(engine, aircraft, route, junction, GeoMath.BearingTo(d.OtherNode(junction).Position, junction.Position));
    }

    /// <summary>
    /// N152SP's taxi to the bar at node 366 (<see cref="AssertHeldSnapshotRoundTrip"/>) snapshotted after its reversal
    /// completes, on the straight laid on D's centreline through the stop, before it holds short: the snapshot carries the
    /// square-stop line, and the restored N152SP stays on the live one's position and heading every second through the
    /// last look-ahead window before the stop, then holds at node 366 at the live one's pose, square to the bar.
    /// </summary>
    [Fact]
    public void N152spHoldingShortAtTheNode_SurvivesSnapshotRoundTripOnTheSquareStopLine()
    {
        bool seenReversal = false;
        StateSnapshotDto? picked = AssertHeldSnapshotRoundTrip(
            snapshot =>
            {
                bool playing = ReversalPlaying(snapshot);
                bool completed = seenReversal && !playing;
                seenReversal |= playing;
                return completed;
            },
            "on the square-stop line after the reversal"
        );
        Assert.True((picked is null) || SquareStopLine(picked), "the snapshot after the reversal carries no square-stop line");
    }

    /// <summary>
    /// N152SP's taxi to the bar at node 366 (<see cref="AssertHeldSnapshotRoundTrip"/>) snapshotted mid-reversal, the
    /// reversal rolling out on D's bearing with no roll-out hold (the straight after it ends in the stop): the restored
    /// N152SP still lays the straight on D's centreline through the stop, stays on the live one's position and heading
    /// every second, and holds at node 366 at the live one's pose, square to the bar.
    /// </summary>
    [Fact]
    public void N152spHoldingShortAtTheNode_SurvivesSnapshotRoundTripMidReversal() =>
        AssertHeldSnapshotRoundTrip(
            s => ReversalPlaying(s) && ReversalOnEdgeBearing(s) && !RollsOutAlongEdge(s),
            "mid-reversal on D's bearing toward the stop"
        );

    /// <summary>
    /// Restore N152SP's replay at the assignment of its taxi route into a live engine and put the bar at node 366 on the
    /// route there, the taxi restarted from rest (<see cref="PutBarOnNode366FromRest"/>), so the turn about is planned in
    /// the live engine and none of its playback has been through a snapshot; tick it second by second until
    /// <paramref name="capture"/> picks a snapshot taken before it holds short, round-trip that snapshot into a second
    /// engine and tick both until both hold short: the restored N152SP stays on the live one's position and heading every
    /// second, and both hold at node 366 at the same pose, on D's centreline (<see cref="OnCentrelineFt"/>), within
    /// <see cref="StopSquareMaxDeg"/> of D's bearing and short of the node. Returns the picked snapshot, or null when the
    /// recording is not available.
    /// </summary>
    private StateSnapshotDto? AssertHeldSnapshotRoundTrip(Func<StateSnapshotDto, bool> capture, string where)
    {
        if (StartN152spReplay(null, 0.0, null) is not { } started)
        {
            return null;
        }

        (LatLon Position, double HeadingDeg) atTaxi = (started.Aircraft.Position, started.Aircraft.TrueHeading.Degrees);
        ReplayUntilNewRoute(started.Engine, started.Aircraft);
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = started.Engine.Scenario };
        engine.RestoreFromSnapshot(RoundTrip(started.Engine.CaptureSnapshot()));
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(N152sp));
        HeldAtNode366 setUp = PutBarOnNode366FromRest(engine, aircraft, atTaxi);
        StateSnapshotDto? picked = TickUntilPicked(engine, s => (aircraft.Phases?.CurrentPhase is not HoldingShortPhase) && capture(s));
        Assert.True(picked is not null, $"N152SP was never {where} before holding short at node {setUp.Junction.Id}");
        var restoredEngine = new SimulationEngine(new TestAirportGroundData()) { Scenario = engine.Scenario };
        restoredEngine.RestoreFromSnapshot(RoundTrip(picked));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(N152sp));
        int seconds = 0;
        for (; (seconds < ReAimTickSeconds) && !BothHoldingShort(aircraft, restored); seconds++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(aircraft.Position, restored.Position);
            Assert.Equal(aircraft.TrueHeading.Degrees, restored.TrueHeading.Degrees);
        }

        HoldingShortPhase liveHold = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        HoldingShortPhase restoredHold = Assert.IsType<HoldingShortPhase>(restored.Phases?.CurrentPhase);
        double shortFt = AlongToJunctionFt(restored.Position, setUp.Junction, setUp.DBearingDeg);
        double offFt = OffCentrelineFt(restored.Position, setUp.Junction, setUp.DBearingDeg);
        double offDeg = GeoMath.AbsBearingDifference(restored.TrueHeading.Degrees, setUp.DBearingDeg);
        output.WriteLine(
            $"snapshot {where}; both held after {seconds}s at node {restoredHold.HoldShort.NodeId}, {shortFt:F1} ft short of it, "
                + $"{offFt:F1} ft off D's centreline, heading {offDeg:F1}° off D's bearing"
        );
        Assert.Equal(setUp.Junction.Id, liveHold.HoldShort.NodeId);
        Assert.Equal(setUp.Junction.Id, restoredHold.HoldShort.NodeId);
        Assert.Equal(aircraft.Position, restored.Position);
        Assert.Equal(aircraft.TrueHeading.Degrees, restored.TrueHeading.Degrees);
        Assert.True(shortFt >= 0.0, $"the restored N152SP stopped {-shortFt:F1} ft past node {setUp.Junction.Id}");
        Assert.True(offFt <= OnCentrelineFt, $"the restored N152SP stopped {offFt:F1} ft off D's centreline at the bar");
        Assert.True(offDeg <= StopSquareMaxDeg, $"the restored N152SP stopped with its heading {offDeg:F1}° off D's bearing");
        return picked;
    }

    private static bool BothHoldingShort(AircraftState live, AircraftState restored) =>
        (live.Phases?.CurrentPhase is HoldingShortPhase) && (restored.Phases?.CurrentPhase is HoldingShortPhase);

    /// <summary>
    /// N152SP's turn about on D holding the roll-out bearing to abeam node 366, with an uncleared bar painted at the to-node
    /// of H's first leg: the bar is aimed at as the node turn onto H is set up from the offset line, which is not the held
    /// straight, so no square-stop line is laid — no navigator in a snapshot taken on H's first leg carries
    /// <see cref="GroundNavigatorPlaybackDto.TurnAboutSquareStopLine"/>.
    /// </summary>
    [Fact]
    public void N152spBarAtTheEndOfHsFirstLeg_NodeTurnFromTheOffsetLine_LaysNoSquareStopLine()
    {
        var capture = DebugLogCapture.Install(NodeTurnFromTheOffsetLine);
        if (StartN152spReplay(null, HeldStraightShiftAlongDFt, capture) is not { } started)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = started;
        TaxiRoute route = ReplayUntilNewRoute(engine, aircraft);
        TaxiRouteSegment outgoing = route.Segments[1];
        GroundNode barNode = outgoing.Edge.ToNode;
        route.HoldShortPoints.Add(
            new HoldShortPoint
            {
                NodeId = barNode.Id,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = outgoing.TaxiwayName,
                Latitude = barNode.Position.Lat,
                Longitude = barNode.Position.Lon,
            }
        );
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase).NotifyHoldShortsChanged();
        int snapshotsOnH = 0;
        bool squareStopLine = false;
        for (
            int sub = 1;
            (sub <= MaxReplaySubTicks) && (route.CurrentSegmentIndex <= 1) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase);
            sub++
        )
        {
            engine.ReplayOneSubTick();
            if ((route.CurrentSegmentIndex == 1) && ((sub % SimulationEngine.PhysicsSubTickRate) == 0))
            {
                snapshotsOnH++;
                squareStopLine |= SquareStopLine(engine.CaptureSnapshot());
            }
        }

        output.WriteLine($"{snapshotsOnH} snapshots on {outgoing.TaxiwayName} to the bar at node {barNode.Id}; {string.Join(" | ", capture.Lines)}");
        Assert.True(capture.Lines.Count > 0, $"N152SP never laid its node turn onto {outgoing.TaxiwayName} from the offset line");
        Assert.True(snapshotsOnH > 0, $"N152SP never taxied on {outgoing.TaxiwayName} toward the bar at node {barNode.Id}");
        Assert.False(squareStopLine, $"the square-stop line was laid on {outgoing.TaxiwayName}, after the node turn rather than a turn about");
    }

    /// <summary>
    /// N152SP's turn about on D from rest, the straight after its reversal laid on D's reversed bearing a turning radius
    /// inside the turn onto H, given a hold short at node 366 (a bar painted on the node) while it holds that straight, as
    /// the <c>HS</c> amendment re-aims a taxi under way: the stop re-centres the straight on D's centreline instead of
    /// aiming at the bar from the offset line, so N152SP stops within <see cref="OnCentrelineFt"/> of D's centreline and
    /// within <see cref="StopSquareMaxDeg"/> of D's bearing, square to the bar, holding at node 366 and short of it.
    ///
    /// <para>
    /// The bar on the node stands in for the real route to this re-lay: a taxiway <c>HS H</c> puts the stop the aircraft's
    /// length plus 30 ft back from node 366 (<c>HoldShortAnnotator.cs:452,493</c>), behind the start of a held straight
    /// capped at six turning radii (25 ft for the C172), so it takes the set-back stop path instead. The real route here is
    /// a runway-style bar set back half the aircraft's length, re-armed after a pre-cleared crossing.
    /// </para>
    /// </summary>
    [Fact(Skip = Yaat438Skip)]
    public void N152spHoldShortIssuedOnTheHeldStraight_TurnAboutOnD_StopsSquareToTheBar()
    {
        var capture = DebugLogCapture.Install(HoldingTheRollOutBearing);
        if (StartN152spReplay(null, HeldStraightShiftAlongDFt, capture) is not { } started)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = started;
        AirportGroundLayout layout = aircraft.Ground.Layout ?? throw new InvalidOperationException("N152SP has no ground layout");
        List<(LatLon Position, double HeadingDeg)> poses = [(aircraft.Position, aircraft.TrueHeading.Degrees)];
        TaxiRoute route = ReplayUntilNewRoute(engine, aircraft);
        GroundNode junction = route.Segments[0].Edge.ToNode;
        // From rest: at the replay's taxi speed the turn about cannot stop at the node (YAAT-438).
        aircraft.IndicatedAirspeed = 0.0;
        RestartTaxiOnRoute(aircraft, layout);
        for (int sub = 0; (sub < MaxReplaySubTicks) && (capture.Lines.Count == 0); sub++)
        {
            engine.ReplayOneSubTick();
            poses.Add((aircraft.Position, aircraft.TrueHeading.Degrees));
        }

        Assert.True(capture.Lines.Count > 0, "N152SP never held D's reversed bearing after its reversal");
        output.WriteLine(capture.Lines[0]);
        route.HoldShortPoints.Add(
            new HoldShortPoint
            {
                NodeId = junction.Id,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = route.Segments[1].TaxiwayName,
                Latitude = junction.Position.Lat,
                Longitude = junction.Position.Lon,
            }
        );
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase).NotifyHoldShortsChanged();
        for (int sub = 0; (sub < MaxReplaySubTicks) && (aircraft.Phases?.CurrentPhase is not HoldingShortPhase); sub++)
        {
            engine.ReplayOneSubTick();
            poses.Add((aircraft.Position, aircraft.TrueHeading.Degrees));
        }

        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        GroundEdge d = OccupiedEdgeTo(layout, poses, junction);
        double dBearingDeg = GeoMath.BearingTo(d.OtherNode(junction).Position, junction.Position);
        double shortFt = AlongToJunctionFt(aircraft.Position, junction, dBearingDeg);
        double stopOffFt = OffCentrelineFt(aircraft.Position, junction, dBearingDeg);
        double stopOffDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, dBearingDeg);
        output.WriteLine(
            $"held at node {holding.HoldShort.NodeId}, {shortFt:F1} ft short of node {junction.Id} along D {dBearingDeg:F1}°, "
                + $"{stopOffFt:F1} ft off D's centreline, heading {stopOffDeg:F1}° off D's bearing"
        );
        Assert.Equal(junction.Id, holding.HoldShort.NodeId);
        Assert.True(shortFt >= 0.0, $"N152SP stopped {-shortFt:F1} ft past node {junction.Id}");
        Assert.True(stopOffFt <= OnCentrelineFt, $"N152SP stopped {stopOffFt:F1} ft off D's centreline at the bar");
        Assert.True(stopOffDeg <= StopSquareMaxDeg, $"N152SP stopped with its heading {stopOffDeg:F1}° off D's bearing {dBearingDeg:F1}°");
    }

    /// <summary>Replay sub-ticks until <paramref name="aircraft"/> is assigned a taxi route other than the one it had.</summary>
    private static TaxiRoute ReplayUntilNewRoute(SimulationEngine engine, AircraftState aircraft)
    {
        TaxiRoute? before = aircraft.Ground.AssignedTaxiRoute;
        for (int sub = 0; sub < MaxReplaySubTicks; sub++)
        {
            engine.ReplayOneSubTick();
            if ((aircraft.Ground.AssignedTaxiRoute is { } route) && !ReferenceEquals(route, before))
            {
                return route;
            }
        }

        throw new InvalidOperationException($"{aircraft.Callsign} was never assigned a new taxi route");
    }

    /// <summary>
    /// Replay sub-ticks while <paramref name="aircraft"/> has not passed the route's segment 1, returning every pose it
    /// stood at on segment 1.
    /// </summary>
    private static List<(LatLon Position, double HeadingDeg)> ReplayPosesOnSegmentOne(
        SimulationEngine engine,
        AircraftState aircraft,
        TaxiRoute route
    )
    {
        List<(LatLon Position, double HeadingDeg)> poses = [];
        for (int sub = 0; (sub < MaxReplaySubTicks) && (route.CurrentSegmentIndex <= 1); sub++)
        {
            if (route.CurrentSegmentIndex == 1)
            {
                poses.Add((aircraft.Position, aircraft.TrueHeading.Degrees));
            }

            engine.ReplayOneSubTick();
        }

        Assert.True(route.CurrentSegmentIndex > 1, $"{aircraft.Callsign} never reached node {route.Segments[1].ToNodeId}");
        return poses;
    }

    /// <summary>
    /// How far (ft) each of <paramref name="poses"/> on <paramref name="segment"/> lies off its centreline through
    /// <paramref name="from"/>, from the first pose established on it (heading within <see cref="EstablishedDeg"/> of its
    /// bearing) up to <see cref="PreTurnBlendFt"/> short of its to-node, where the steer blends toward the next segment.
    /// When no pose before that limit is established, from the first within <see cref="OnCentrelineFt"/> of the centreline
    /// instead; empty when there is neither.
    /// </summary>
    private static List<double> ExitOffCentrelineFt(List<(LatLon Position, double HeadingDeg)> poses, TaxiRouteSegment segment, GroundNode from)
    {
        double bearingDeg = segment.Edge.DepartureBearing;
        LatLon end = segment.Edge.ToNode.Position;
        List<(double OffFt, double OffDeg)> beforeBlend =
        [
            .. poses
                .Where(p => (GeoMath.DistanceNm(p.Position, end) * GeoMath.FeetPerNm) > PreTurnBlendFt)
                .Select(p => (OffCentrelineFt(p.Position, from, bearingDeg), GeoMath.AbsBearingDifference(p.HeadingDeg, bearingDeg))),
        ];
        int start = beforeBlend.FindIndex(p => p.OffDeg <= EstablishedDeg);
        if (start < 0)
        {
            start = beforeBlend.FindIndex(p => p.OffFt <= OnCentrelineFt);
        }

        return (start < 0) ? [] : [.. beforeBlend.Skip(start).Select(p => p.OffFt)];
    }

    /// <summary>
    /// How far (ft) <paramref name="position"/> lies off the line through <paramref name="node"/> on <paramref name="bearingDeg"/>.
    /// </summary>
    private static double OffCentrelineFt(LatLon position, GroundNode node, double bearingDeg) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, node.Position, new TrueHeading(bearingDeg))) * GeoMath.FeetPerNm;

    /// <summary>
    /// Replace <paramref name="aircraft"/>'s phases with a taxi phase started afresh on its assigned route, so the set-up
    /// sees a hold short added to the route after the TAXI. The ended phases are removed first: <see cref="PhaseList.Start"/>
    /// starts the list's first phase, which would otherwise be the old taxi phase, its navigator still holding any playback
    /// a snapshot restore left it.
    /// </summary>
    private static void RestartTaxiOnRoute(AircraftState aircraft, AirportGroundLayout layout)
    {
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        PhaseList phases = Assert.IsType<PhaseList>(aircraft.Phases);
        phases.Clear(ctx);
        phases.Phases.Clear();
        phases.Add(new TaxiingPhase());
        phases.Start(ctx);
    }

    private sealed record AcuteBranchTaxi(
        string AirportId,
        AirportGroundLayout Layout,
        AcuteBranch Branch,
        SimulationEngine Engine,
        AircraftState Aircraft,
        TaxiRoute Route
    );

    /// <summary>
    /// A <paramref name="type"/> placed past the first acute branch's junction (<see cref="FindAcuteBranch"/>) and cleared
    /// onto the branch, its route checked to be the free-space leg back to the junction and then the branch; nothing has
    /// ticked yet.
    /// </summary>
    private AcuteBranchTaxi StartAcuteBranchTaxi(string type)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundNavigator", LogLevel.Debug)
            .EnableCategory("TaxiingPhase", LogLevel.Debug)
            .InitializeSimLog();
        (string airportId, AirportGroundLayout layout, AcuteBranch branch) = FindAcuteBranch();
        SimulationEngine engine = BuildEngine(airportId);
        AircraftState aircraft = PlacePastJunction(engine, layout, branch, airportId, type);
        GroundNode junction = branch.Junction;

        string command = $"TAXI {branch.Occupied.TaxiwayName} {branch.Branch.TaxiwayName} {TaxiwayBeyond(branch)}";
        CommandResult result = engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine(
            $"{airportId}: {branch.Occupied.TaxiwayName} edge {junction.Id}-{branch.Occupied.OtherNode(junction).Id}, branch "
                + $"{branch.Branch.TaxiwayName} to {branch.Branch.OtherNode(junction).Id} at {branch.AngleDeg:F1}°; "
                + $"{command}: {result.Success} — {result.Message}"
        );
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.True(VirtualNode.IsVirtualEdge(route.Segments[0].Edge.Edge), "segment 0 is not the free-space leg back to the junction");
        Assert.Equal(junction.Id, route.Segments[0].ToNodeId);
        Assert.True(ReferenceEquals(route.Segments[1].Edge.Edge, branch.Branch), "segment 1 is not the branch");
        return new AcuteBranchTaxi(airportId, layout, branch, engine, aircraft, route);
    }

    private sealed record N152spRun(
        SimulationEngine Engine,
        AircraftState Aircraft,
        TaxiRoute Route,
        AirportGroundLayout Layout,
        YawTrack Track,
        List<(LatLon Position, double HeadingDeg)> Poses,
        GroundNode Junction,
        TaxiRouteSegment Outgoing,
        GroundEdge D
    )
    {
        /// <summary>The bearing along D toward the junction: the way the turn about faces once reversed.</summary>
        public double ReversedBearingDeg => GeoMath.BearingTo(D.OtherNode(Junction).Position, Junction.Position);
    }

    /// <summary>
    /// Replay N152SP to just before its TAXI, its type replaced by <paramref name="aircraftType"/> when that is not null and
    /// moved <paramref name="shiftAlongHeadingFt"/> on along its heading, then sub-tick by sub-tick until it is established
    /// on the segment after the turn about, sampling every pose. <paramref name="capture"/>, when not null, is the log the
    /// replay runs under in place of the test output. Null when the recording is not available.
    /// </summary>
    private N152spRun? ReplayN152spTurnAbout(string? aircraftType, double shiftAlongHeadingFt, DebugLogCapture? capture)
    {
        if (StartN152spReplay(aircraftType, shiftAlongHeadingFt, capture) is not { } started)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft) = started;
        AirportGroundLayout layout = aircraft.Ground.Layout ?? throw new InvalidOperationException("N152SP has no ground layout");
        var track = new YawTrack(aircraft.TrueHeading.Degrees);
        List<(LatLon Position, double HeadingDeg)> poses = [(aircraft.Position, aircraft.TrueHeading.Degrees)];
        TaxiRoute? before = aircraft.Ground.AssignedTaxiRoute;
        TaxiRoute? route = null;
        for (int sub = 0; (sub < MaxReplaySubTicks) && ((route is null) || !EstablishedOnOrPast(aircraft, route, 1)); sub++)
        {
            engine.ReplayOneSubTick();
            route ??= ReferenceEquals(aircraft.Ground.AssignedTaxiRoute, before) ? null : aircraft.Ground.AssignedTaxiRoute;
            track.Add(aircraft.TrueHeading.Degrees);
            poses.Add((aircraft.Position, aircraft.TrueHeading.Degrees));
        }

        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.True(EstablishedOnOrPast(aircraft, route, 1), $"N152SP was not established on {route.Segments[1].TaxiwayName} in time");
        GroundNode junction = route.Segments[0].Edge.ToNode;
        return new N152spRun(engine, aircraft, route, layout, track, poses, junction, route.Segments[1], OccupiedEdgeTo(layout, poses, junction));
    }

    /// <summary>
    /// The N152SP replay at the second before its TAXI, its type replaced by <paramref name="aircraftType"/> when that is
    /// not null and moved <paramref name="shiftAlongHeadingFt"/> on along its heading, logging to <paramref name="capture"/>
    /// when that is not null; null when the recording is not available.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft)? StartN152spReplay(
        string? aircraftType,
        double shiftAlongHeadingFt,
        DebugLogCapture? capture
    )
    {
        SessionRecording? recording = RecordingLoader.Load(N152spRecordingPath);
        if (recording is null)
        {
            output.WriteLine("Skipped: recording not available");
            return null;
        }

        TestVnasData.EnsureInitialized();
        if (capture is null)
        {
            SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();
        }

        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, TaxiCommandSecond - 1);
        AircraftState aircraft = engine.FindAircraft(N152sp) ?? throw new InvalidOperationException("N152SP is not in the replay");
        if (aircraftType is not null)
        {
            aircraft.AircraftType = aircraftType;
        }

        if (shiftAlongHeadingFt != 0.0)
        {
            aircraft.Position = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, shiftAlongHeadingFt / GeoMath.FeetPerNm);
        }

        return (engine, aircraft);
    }

    /// <summary>
    /// Restore N152SP into a live engine through the second of its TAXI (<see cref="StartRestoredN152spTaxi"/>), then tick
    /// it second by second until <paramref name="capture"/> picks a snapshot; round-trip that snapshot into a second engine
    /// and tick both for <see cref="SnapshotCompareSeconds"/> seconds: the restored N152SP stays on the live one's positions
    /// and heading. Both engines run from a restore, so they differ only in the snapshot taken mid-manoeuvre. Returns the
    /// picked snapshot, or null when the recording is not available.
    /// </summary>
    private StateSnapshotDto? AssertSnapshotRoundTrip(Func<StateSnapshotDto, bool> capture, string where)
    {
        if (StartRestoredN152spTaxi() is not { } live)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft) = live;
        StateSnapshotDto? picked = TickUntilPicked(engine, capture);
        Assert.True(picked is not null, $"N152SP was never {where} within {ReAimTickSeconds}s of its TAXI");
        var restoredEngine = new SimulationEngine(new TestAirportGroundData()) { Scenario = engine.Scenario };
        restoredEngine.RestoreFromSnapshot(RoundTrip(picked));
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(N152sp));
        for (int second = 1; second <= SnapshotCompareSeconds; second++)
        {
            engine.TickOneSecond();
            restoredEngine.TickOneSecond();
            Assert.Equal(aircraft.Position, restored.Position);
            Assert.Equal(aircraft.TrueHeading.Degrees, restored.TrueHeading.Degrees);
        }

        output.WriteLine($"snapshot {where}; after {SnapshotCompareSeconds}s: hdg={aircraft.TrueHeading.Degrees:F1} at {aircraft.Position}");
        return picked;
    }

    /// <summary>
    /// Replay N152SP through the second of its TAXI and restore that state into a live engine; null when the recording is
    /// not available.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft)? StartRestoredN152spTaxi()
    {
        if (StartN152spReplay(null, HeldStraightShiftAlongDFt, null) is not { } started)
        {
            return null;
        }

        for (int sub = 0; sub < SimulationEngine.PhysicsSubTickRate; sub++)
        {
            started.Engine.ReplayOneSubTick();
        }

        Assert.NotNull(started.Aircraft.Ground.AssignedTaxiRoute);
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = started.Engine.Scenario };
        engine.RestoreFromSnapshot(RoundTrip(started.Engine.CaptureSnapshot()));
        return (engine, Assert.IsType<AircraftState>(engine.FindAircraft(N152sp)));
    }

    /// <summary>
    /// Tick <paramref name="engine"/> second by second, up to <see cref="ReAimTickSeconds"/>, until <paramref name="capture"/>
    /// picks a snapshot taken after a second; null when none is picked.
    /// </summary>
    private static StateSnapshotDto? TickUntilPicked(SimulationEngine engine, Func<StateSnapshotDto, bool> capture)
    {
        for (int second = 0; second < ReAimTickSeconds; second++)
        {
            engine.TickOneSecond();
            StateSnapshotDto snapshot = engine.CaptureSnapshot();
            if (capture(snapshot))
            {
                return snapshot;
            }
        }

        return null;
    }

    private static IEnumerable<JsonObject> Playbacks(StateSnapshotDto snapshot) =>
        GroundNavigatorArcRestoreTests.PlaybackObjects(JsonSerializer.SerializeToNode(snapshot, RecordingJsonOptions.Default));

    /// <summary>Whether a navigator in <paramref name="snapshot"/> is set to hold its turn about's roll-out bearing.</summary>
    private static bool RollsOutAlongEdge(StateSnapshotDto snapshot) =>
        Playbacks(snapshot).Any(p => (bool?)p[nameof(GroundNavigatorPlaybackDto.TurnAboutRollsOutAlongEdge)] == true);

    /// <summary>Whether a navigator in <paramref name="snapshot"/> is playing a turn about's reversal arc.</summary>
    private static bool ReversalPlaying(StateSnapshotDto snapshot) =>
        Playbacks(snapshot).Any(p => (bool?)p[nameof(GroundNavigatorPlaybackDto.TurnAboutReversalPlaying)] == true);

    /// <summary>Whether a navigator in <paramref name="snapshot"/> is playing a turn about's reversal that rolls out on its edge's bearing.</summary>
    private static bool ReversalOnEdgeBearing(StateSnapshotDto snapshot) =>
        Playbacks(snapshot).Any(p => (bool?)p[nameof(GroundNavigatorPlaybackDto.TurnAboutReversalOnEdgeBearing)] == true);

    /// <summary>Whether a navigator in <paramref name="snapshot"/> has its straight laid on the centreline square to a stop.</summary>
    private static bool SquareStopLine(StateSnapshotDto snapshot) =>
        Playbacks(snapshot).Any(p => (bool?)p[nameof(GroundNavigatorPlaybackDto.TurnAboutSquareStopLine)] == true);

    /// <summary>Whether a navigator in <paramref name="snapshot"/> is playing a slow turn.</summary>
    private static bool PlaysSlowTurn(StateSnapshotDto snapshot) => Playbacks(snapshot).Any(p => (string?)p["Primitive"]?["$type"] == "SlowTurn");

    /// <summary>The largest roll-out offset (ft) a navigator in <paramref name="snapshot"/> carries; 0 when none does.</summary>
    private static double RollOutOffsetFt(StateSnapshotDto snapshot) =>
        Playbacks(snapshot).Select(p => (double?)p[nameof(GroundNavigatorPlaybackDto.TurnAboutRollOutOffsetFt)] ?? 0.0).DefaultIfEmpty(0.0).Max();

    /// <summary>Whether N152SP in <paramref name="snapshot"/> is on its taxi route's segment <paramref name="index"/>.</summary>
    private static bool OnSegment(StateSnapshotDto snapshot, int index) =>
        snapshot.Aircraft.Single(a => a.Callsign == N152sp).Ground.AssignedTaxiRoute is { } route && (route.CurrentSegmentIndex == index);

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    /// <summary>
    /// The poses from the first one after the jog whose heading is within <see cref="RollOutToleranceDeg"/> of
    /// <paramref name="reversedBearingDeg"/>: the reversal's roll-out.
    /// </summary>
    private static IEnumerable<(LatLon Position, double HeadingDeg)> PosesAfterRollOut(
        List<(LatLon Position, double HeadingDeg)> poses,
        YawTrack track,
        double reversedBearingDeg
    ) => poses.Skip(track.JogEndIndex).SkipWhile(p => GeoMath.AbsBearingDifference(p.HeadingDeg, reversedBearingDeg) > RollOutToleranceDeg);

    /// <summary>How far (ft) <paramref name="position"/> is short of abeam <paramref name="junction"/> along <paramref name="bearingDeg"/>.</summary>
    private static double AlongToJunctionFt(LatLon position, GroundNode junction, double bearingDeg) =>
        GeoMath.AlongTrackDistanceNm(junction.Position, position, new TrueHeading(bearingDeg)) * GeoMath.FeetPerNm;

    private void AssertReCentres(N152spRun run) => AssertReCentres(run.Poses, run.Track, run.Junction, run.ReversedBearingDeg);

    /// <summary>
    /// After the reversal rolls out within <see cref="RollOutToleranceDeg"/> of <paramref name="reversedBearingDeg"/>
    /// (<see cref="PosesAfterRollOut"/>), some pose further than <see cref="HoldCheckShortOfAbeamFt"/> short of abeam the
    /// junction has turned more than <see cref="RollOutToleranceDeg"/> off that bearing toward the junction's side: the
    /// straight re-centres on the node rather than holding the bearing to abeam it.
    /// </summary>
    private void AssertReCentres(List<(LatLon Position, double HeadingDeg)> poses, YawTrack track, GroundNode junction, double reversedBearingDeg)
    {
        List<(double ShortFt, double TowardDeg)> straight =
        [
            .. PosesAfterRollOut(poses, track, reversedBearingDeg)
                .Select(p =>
                    (
                        ShortFt: AlongToJunctionFt(p.Position, junction, reversedBearingDeg),
                        TowardDeg: Math.Sign(GeoMath.SignedBearingDifference(reversedBearingDeg, GeoMath.BearingTo(p.Position, junction.Position)))
                            * GeoMath.SignedBearingDifference(reversedBearingDeg, p.HeadingDeg)
                    )
                )
                .Where(p => p.ShortFt > HoldCheckShortOfAbeamFt),
        ];
        output.WriteLine(
            $"short of abeam (ft) / turned toward the node (deg): {string.Join(" ", straight.Select(s => $"{s.ShortFt:F0}/{s.TowardDeg:F1}"))}"
        );
        Assert.NotEmpty(straight);
        Assert.Contains(straight, s => s.TowardDeg > RollOutToleranceDeg);
    }

    /// <summary>
    /// The first junction, in node order, of KOAK then SFO where two straight turn-about taxiways of different names meet
    /// at an angle between <see cref="MinBranchAngleDeg"/> and <see cref="MaxBranchAngleDeg"/>, both at least
    /// <see cref="MinEdgeFt"/> long, the junction not a hold-short.
    /// </summary>
    private static (string AirportId, AirportGroundLayout Layout, AcuteBranch Branch) FindAcuteBranch()
    {
        foreach (string airportId in (string[])["OAK", "SFO"])
        {
            AirportGroundLayout layout = LoadLayout(airportId);
            if (FirstAcuteBranch(layout) is { } branch)
            {
                return (airportId, layout, branch);
            }
        }

        throw new InvalidOperationException("no KOAK or SFO junction has an acute straight taxiway branch");
    }

    private static AirportGroundLayout LoadLayout(string airportId) =>
        new TestAirportGroundData().GetLayout(airportId) ?? throw new InvalidOperationException($"no {airportId} layout");

    private sealed record AcuteBranch(GroundEdge Occupied, GroundNode Junction, GroundEdge Branch, double AngleDeg);

    private static AcuteBranch? FirstAcuteBranch(AirportGroundLayout layout)
    {
        foreach (GroundNode junction in layout.Nodes.Values.Where(n => n.Type == GroundNodeType.TaxiwayIntersection).OrderBy(n => n.Id))
        {
            List<GroundEdge> straights =
            [
                .. junction
                    .Edges.OfType<GroundEdge>()
                    .Where(e => GroundNavigator.IsTurnAboutTaxiway(e, layout) && ((e.DistanceNm * GeoMath.FeetPerNm) >= MinEdgeFt)),
            ];
            foreach (GroundEdge occupied in straights)
            {
                foreach (GroundEdge branch in straights.Where(b => !b.MatchesTaxiway(occupied.TaxiwayName)))
                {
                    double angleDeg = GeoMath.AbsBearingDifference(
                        GeoMath.BearingTo(junction.Position, occupied.OtherNode(junction).Position),
                        GeoMath.BearingTo(junction.Position, branch.OtherNode(junction).Position)
                    );
                    if ((angleDeg >= MinBranchAngleDeg) && (angleDeg <= MaxBranchAngleDeg))
                    {
                        return new AcuteBranch(occupied, junction, branch, angleDeg);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A C172 standing on <paramref name="occupied"/> <see cref="PastJunctionFt"/> past <paramref name="junction"/>, facing away
    /// from it.
    /// </summary>
    private static AircraftState PlacePastJunction(
        SimulationEngine engine,
        AirportGroundLayout layout,
        AcuteBranch branch,
        string airportId,
        string type
    )
    {
        GroundEdge occupied = branch.Occupied;
        GroundNode junction = branch.Junction;
        GroundNode ahead = occupied.OtherNode(junction);
        double headingDeg = GeoMath.BearingTo(junction.Position, ahead.Position);
        LatLon position = GeoMath.ProjectPoint(junction.Position, new TrueHeading(headingDeg), PastJunctionFt / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeAircraft(layout, position, headingDeg, airportId, occupied.TaxiwayName, type);
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// A taxiway, first by name, meeting the branch at its far end other than the two the junction joins: the route is cleared
    /// on to it, so it drives the branch rather than ending at the junction.
    /// </summary>
    private static string TaxiwayBeyond(AcuteBranch branch) =>
        branch
            .Branch.OtherNode(branch.Junction)
            .Edges.SelectMany(e => (e is GroundArc arc) ? arc.TaxiwayNames : (string[])[e.TaxiwayName])
            .Where(n =>
                !branch.Occupied.MatchesTaxiway(n) && !branch.Branch.MatchesTaxiway(n) && !n.Equals("RAMP", StringComparison.OrdinalIgnoreCase)
            )
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
        ?? throw new InvalidOperationException($"no taxiway meets {branch.Branch.TaxiwayName} beyond the junction {branch.Junction.Id}");

    private static SimulationEngine BuildEngine(string airportId) =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = $"test-{airportId}-turn-about-aim",
                ScenarioName = $"{airportId} turn about aim",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airportId.ToUpperInvariant(),
                AutoCrossRunway = false,
            },
        };

    private static AircraftState MakeAircraft(
        AirportGroundLayout layout,
        LatLon position,
        double headingDeg,
        string airportId,
        string taxiway,
        string aircraftType
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = "N437TA",
            AircraftType = aircraftType,
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = airportId, Destination = "SAC" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = taxiway;
        return aircraft;
    }

    /// <summary>Half the type's main-gear width (ft) from its FAA aircraft characteristics record.</summary>
    private static double GearHalfWidthFt(string type) =>
        (FaaAircraftDatabase.Get(type)?.MainGearWidthFt ?? throw new InvalidOperationException($"no FAA main-gear width for {type}")) / 2.0;

    /// <summary>
    /// On the route's segment <paramref name="index"/> with the heading within <see cref="EstablishedDeg"/> of its bearing,
    /// or on a later segment.
    /// </summary>
    private static bool EstablishedOnOrPast(AircraftState aircraft, TaxiRoute route, int index) =>
        (route.CurrentSegmentIndex > index)
        || (
            (route.CurrentSegmentIndex == index)
            && (GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, route.Segments[index].Edge.DepartureBearing) <= EstablishedDeg)
        );

    /// <summary>The straight taxi edge ending at <paramref name="junction"/> the aircraft stood inside when the turn about began.</summary>
    private static GroundEdge OccupiedEdgeTo(AirportGroundLayout layout, List<(LatLon Position, double HeadingDeg)> poses, GroundNode junction) =>
        poses
            .Select(p => (Edge: layout.FindOccupiedTaxiEdge(p.Position), p.HeadingDeg))
            .FirstOrDefault(o =>
                (o.Edge is { } e)
                && e.HasNode(junction.Id)
                && (GeoMath.AbsBearingDifference(GeoMath.BearingTo(junction.Position, e.OtherNode(junction).Position), o.HeadingDeg) < 90.0)
            )
            .Edge
        ?? throw new InvalidOperationException($"the aircraft never stood inside a taxi edge ending at node {junction.Id}");

    /// <summary>
    /// The centrelines whose <see cref="Tdg1AHalfWidthFt"/> strips are the paved envelope round <paramref name="junction"/>:
    /// the straight edges of either taxiway and the fillet arcs joining the two, within <see cref="EnvelopeRadiusFt"/>.
    /// </summary>
    private static List<(LatLon A, LatLon B)> PavedCentrelines(AirportGroundLayout layout, GroundNode junction, string taxiway, string outgoing)
    {
        bool Near(IGroundEdge e) => e.Nodes.Any(n => (GeoMath.DistanceNm(n.Position, junction.Position) * GeoMath.FeetPerNm) <= EnvelopeRadiusFt);
        List<(LatLon A, LatLon B)> lines =
        [
            .. layout
                .Edges.Where(e => Near(e) && (e.MatchesTaxiway(taxiway) || e.MatchesTaxiway(outgoing)))
                .Select(e => (e.Nodes[0].Position, e.Nodes[1].Position)),
        ];
        foreach (GroundArc arc in layout.Arcs.Where(a => Near(a) && a.MatchesTaxiway(taxiway) && a.MatchesTaxiway(outgoing)))
        {
            LatLon previous = arc.Nodes[0].Position;
            for (int i = 1; i <= ArcSamples; i++)
            {
                LatLon next = BezierPoint(arc, (double)i / ArcSamples);
                lines.Add((previous, next));
                previous = next;
            }
        }

        return lines;
    }

    private static LatLon BezierPoint(GroundArc arc, double t)
    {
        double u = 1.0 - t;
        double w0 = u * u * u;
        double w1 = 3.0 * u * u * t;
        double w2 = 3.0 * u * t * t;
        double w3 = t * t * t;
        LatLon p0 = arc.Nodes[0].Position;
        LatLon p3 = arc.Nodes[1].Position;
        return new LatLon(
            (w0 * p0.Lat) + (w1 * arc.P1Lat) + (w2 * arc.P2Lat) + (w3 * p3.Lat),
            (w0 * p0.Lon) + (w1 * arc.P1Lon) + (w2 * arc.P2Lon) + (w3 * p3.Lon)
        );
    }

    /// <summary>
    /// The furthest (ft) either main-gear edge — the centre offset half the gear width either side, square to the heading —
    /// ran outside every centreline's <see cref="Tdg1AHalfWidthFt"/> strip, over every sampled pose; negative when inside.
    /// </summary>
    private static double WorstGearExcessFt(List<(LatLon Position, double HeadingDeg)> poses, List<(LatLon A, LatLon B)> pavement, double gearHalfFt)
    {
        double worst = double.NegativeInfinity;
        foreach ((LatLon position, double headingDeg) in poses)
        {
            foreach (double side in (double[])[90.0, -90.0])
            {
                LatLon gear = GeoMath.ProjectPoint(position, new TrueHeading(headingDeg + side), gearHalfFt / GeoMath.FeetPerNm);
                double offFt = pavement.Min(l => GeoMath.DistanceToSegmentFt(gear, l.A, l.B));
                worst = Math.Max(worst, offFt - Tdg1AHalfWidthFt);
            }
        }

        return worst;
    }

    /// <summary>
    /// Where the reversal began (the pose at the jog's furthest swing): its distance past the junction along the taxiway and
    /// its perpendicular distance off the outgoing branch's centreline.
    /// </summary>
    private void LogReversalStart(List<(LatLon Position, double HeadingDeg)> poses, YawTrack track, GroundNode junction, TaxiRouteSegment outgoing)
    {
        (LatLon position, double headingDeg) = poses[track.JogEndIndex];
        double pastFt = GeoMath.DistanceNm(position, junction.Position) * GeoMath.FeetPerNm;
        double offBranchFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, junction.Position, new TrueHeading(outgoing.Edge.DepartureBearing)))
            * GeoMath.FeetPerNm;
        output.WriteLine(
            $"reversal began at heading {headingDeg:F1}°, {pastFt:F1} ft from node {junction.Id}, {offBranchFt:F1} ft off the branch centreline"
        );
    }

    /// <summary>The heading trace of a manoeuvre, unwrapped: total yaw, the furthest swing either way and where each was.</summary>
    private sealed class YawTrack(double startHeadingDeg)
    {
        private readonly double _startHeadingDeg = startHeadingDeg;
        private readonly List<double> _cumulative = [0.0];
        private double _lastHeadingDeg = startHeadingDeg;

        public double TotalAbsDeg { get; private set; }

        public void Add(double headingDeg)
        {
            double delta = GeoMath.SignedBearingDifference(_lastHeadingDeg, headingDeg);
            TotalAbsDeg += Math.Abs(delta);
            _cumulative.Add(_cumulative[^1] + delta);
            _lastHeadingDeg = headingDeg;
        }

        /// <summary>The yaw (deg, absolute, summed) from sample <paramref name="start"/> on.</summary>
        public double AbsYawFromDeg(int start) => _cumulative.Skip(start).Zip(_cumulative.Skip(start + 1), (a, b) => Math.Abs(b - a)).Sum();

        private int PeakIndex => _cumulative.IndexOf(_cumulative.MaxBy(Math.Abs));

        /// <summary>The largest swing (deg) off the start heading either way.</summary>
        public double PeakAbsDeg => Math.Abs(_cumulative[PeakIndex]);

        /// <summary>The heading at the largest swing off the start: where the reversal rolled out.</summary>
        public double PeakHeadingDeg => new TrueHeading(_startHeadingDeg + _cumulative[PeakIndex]).Degrees;

        /// <summary>The sample before the peak furthest the other way: where the jog ended and the reversal began.</summary>
        public int JogEndIndex
        {
            get
            {
                int peak = PeakIndex;
                double sense = Math.Sign(_cumulative[peak]);
                List<double> before = [.. _cumulative.Take(peak + 1)];
                return before.IndexOf(before.MinBy(c => c * sense));
            }
        }

        /// <summary>The furthest (deg) the heading swung against <paramref name="sense"/> (+1 right, -1 left).</summary>
        public double MostAgainstDeg(double sense) => Math.Max(0.0, -_cumulative.Min(c => c * sense));
    }
}

/// <summary>
/// Captures the Debug-level and higher <see cref="SimLog"/> lines containing any of <paramref name="fragments"/> while it is
/// installed as the test's log factory; every other line is dropped.
/// </summary>
internal sealed class DebugLogCapture(string[] fragments) : ILoggerProvider, ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Installs a fresh capture of the lines containing any of <paramref name="fragments"/> as the SimLog factory.</summary>
    public static DebugLogCapture Install(params string[] fragments)
    {
        var capture = new DebugLogCapture(fragments);
        SimLog.InitializeForTest(LoggerFactory.Create(builder => builder.AddProvider(capture).SetMinimumLevel(LogLevel.Debug)));
        return capture;
    }

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string line = formatter(state, exception);
        if (fragments.Any(f => line.Contains(f, StringComparison.Ordinal)))
        {
            _lines.Add(line);
        }
    }

    public void Dispose() { }
}
