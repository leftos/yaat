using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The tug motion body, flown against the real SFO six alley. The lane is spot 6B's nose-out direction
/// (≈27.7° true); spot 6A sits abeam it, ≈140 ft across, which is the reference geometry for the 6A → 6B
/// reposition. A start 70 ft abeam on the way to 6A covers the shorter capture.
/// Every case resolves its spots by name — node ids renumber whenever the layout is regenerated.
/// </summary>
public class TugKinematicsTests
{
    private const string Narrowbody = "B738";
    private const string Widebody = "B77W";
    private const double StepFt = 1.0;
    private const double LaneRunUpFt = 150.0;
    private const double CaptureFirstStopFt = 250.0;
    private const double SixAAbeamFt = 140.5;

    /// <summary>How far down the 6B lane, past 6B, the far-stop pull ends.</summary>
    private const double FarStopFt = 300.0;

    /// <summary>The stretch before a far stop over which a captured line must be held steady.</summary>
    private const double SettledStretchFt = 100.0;

    private readonly ITestOutputHelper _output;

    public TugKinematicsTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViaLineCaptureFromNinetyOff_NeverTurnsTighterThanTheRadius(bool tight)
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        double travel = lane.TravelDeg + 90.0;
        var start = new TugPose(OnLaneBeforeSixB(lane), travel);
        TugMove move = TugMove.ViaLine(PushbackLegKind.Pull, lane.SixB.Position, lane.TravelDeg, stopAt: null) with { Tight = tight };
        double radiusFt = TugKinematics.TurnRadiusFt(Narrowbody, tight);

        TugMoveTrace trace = Assert.Single(TugKinematics.Simulate(start, [move], Narrowbody, StepFt).Moves);

        Assert.True(trace.Completed, "the capture never completed");
        double worstCurvature = 0.0;
        for (int i = 1; i < trace.Samples.Count; i++)
        {
            TugPose from = trace.Samples[i - 1];
            TugPose to = trace.Samples[i];
            double turnRad = TravelChangeDeg(from, to, PushbackLegKind.Pull) * Math.PI / 180.0;
            worstCurvature = Math.Max(worstCurvature, turnRad / FeetBetween(from.Position, to.Position));
        }

        _output.WriteLine($"R={radiusFt:F2} ft, worst curvature {worstCurvature:F5}/ft vs bound {1.0 / radiusFt:F5}/ft");
        Assert.True(worstCurvature <= (1.0 / radiusFt) * 1.01, $"curvature {worstCurvature:F5}/ft exceeds 1/R = {1.0 / radiusFt:F5}/ft");
        Assert.True(worstCurvature > 0.5 / radiusFt, "the capture never turned near the radius, so the bound proved nothing");
    }

    [Fact]
    public void SteerTravel_ZeroStep_NeverRotates()
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        var pose = new TugPose(OnLaneBeforeSixB(lane), lane.TravelDeg + 60.0);
        LatLon offset = GeoMath.ProjectPoint(pose.Position, new TrueHeading(lane.TravelDeg - 90.0), 40.0 / GeoMath.FeetPerNm);
        TugMove[] moves =
        [
            TugMove.Straight(PushbackLegKind.Pull, 50.0),
            TugMove.ToPoint(PushbackLegKind.Pull, offset),
            TugMove.ViaLine(PushbackLegKind.Pull, lane.SixB.Position, lane.TravelDeg, stopAt: null),
            TugMove.TurnTo(PushbackLegKind.Pull, lane.TravelDeg),
        ];
        TugPose otherStart = pose with { NoseTrueDeg = lane.TravelDeg };
        double radiusFt = TugKinematics.TurnRadiusFt(Narrowbody, tight: false);

        foreach (TugMove? move in moves)
        {
            var progress = TugMoveProgress.Begin(move.Shape == TugMoveShape.Straight ? otherStart : pose, move);
            double current = pose.TravelTrueDeg(move.Kind);

            double still = TugKinematics.SteerTravel(pose, move, progress, radiusFt, stepFt: 0.0);
            double moving = TugKinematics.SteerTravel(pose, move, progress, radiusFt, stepFt: 5.0);

            Assert.Equal(current, still, 9);
            Assert.True(AbsDiffDeg(current, moving) > 1.0, $"{move.Shape}: the 5 ft step did not turn, so the zero-step case proved nothing");
        }
    }

    [Theory]
    [InlineData(StartAt.OnLane, 0.0)]
    [InlineData(StartAt.OnLane, 45.0)]
    [InlineData(StartAt.OnLane, 90.0)]
    [InlineData(StartAt.SeventyFeetAbeam, 0.0)]
    [InlineData(StartAt.SeventyFeetAbeam, 45.0)]
    [InlineData(StartAt.SeventyFeetAbeam, 90.0)]
    [InlineData(StartAt.SixA, 0.0)]
    [InlineData(StartAt.SixA, 45.0)]
    [InlineData(StartAt.SixA, 90.0)]
    public void ViaLineFloating_CapturesTheSixBLaneWithoutOvershoot(StartAt startAt, double angleOffDeg)
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        (TugPose start, TugMove? move) = startAt switch
        {
            StartAt.OnLane => OnLinePullStart(lane, angleOffDeg),
            StartAt.SeventyFeetAbeam => AbeamPushStart(lane, TowardSixA(lane, 70.0), angleOffDeg),
            _ => AbeamPushStart(lane, lane.SixA.Position, angleOffDeg),
        };

        TugMoveTrace trace = Assert.Single(TugKinematics.Simulate(start, [move], Narrowbody, StepFt).Moves);

        double startCrossFt = CrossTrackFt(start.Position, move);
        double overshootFt = FarSideOvershootFt(trace, move);
        double sixAFt = FeetBetween(lane.SixB.Position, lane.SixA.Position);
        _output.WriteLine(
            $"{startAt} {angleOffDeg}°: 6B→6A {sixAFt:F1} ft at {GeoMath.BearingTo(lane.SixB.Position, lane.SixA.Position):F1}°, "
                + $"start cross {startCrossFt:F1} ft, path {trace.PathLengthFt:F0} ft, end cross {trace.EndCrossTrackFt:F2} ft, "
                + $"end Δχ {trace.EndLineTravelErrorDeg:F2}°, overshoot {overshootFt:F2} ft"
        );
        if (startAt == StartAt.SeventyFeetAbeam)
        {
            Assert.InRange(Math.Abs(startCrossFt), 65.0, 70.5);
        }
        else if (startAt == StartAt.SixA)
        {
            Assert.InRange(Math.Abs(startCrossFt), 0.9 * sixAFt, sixAFt + 0.5);
        }

        Assert.True(trace.Completed, "the capture never completed");
        double endCrossFt = Assert.NotNull(trace.EndCrossTrackFt);
        double endErrorDeg = Assert.NotNull(trace.EndLineTravelErrorDeg);
        Assert.True(Math.Abs(endCrossFt) <= 1.0, $"ended {endCrossFt:F2} ft off the lane");
        Assert.True(endErrorDeg <= 1.0, $"ended {endErrorDeg:F2}° off the lane direction");
        Assert.True(overshootFt <= 3.0, $"overshot the lane by {overshootFt:F2} ft");
    }

    /// <summary>
    /// The roll-out law's path economy: pushing down the 6B lane from 6A (140.5 ft abeam, parallel to it), a
    /// floating capture turns in, crosses and rolls out within 2.5 roll-out radii of path beyond the 140.5 ft it
    /// has to cross.
    /// </summary>
    [Fact]
    public void ViaLineFloatingFromSixA_CapturesWithinTwoAndAHalfRolloutRadiiOfTheCrossing()
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        (TugPose start, TugMove? move) = AbeamPushStart(lane, lane.SixA.Position, 0.0);
        double rolloutFt = TugKinematics.RolloutMarginRadii * TugKinematics.TurnRadiusFt(Narrowbody, tight: false);
        double boundFt = (2.5 * rolloutFt) + SixAAbeamFt;

        TugMoveTrace trace = Assert.Single(TugKinematics.Simulate(start, [move], Narrowbody, StepFt).Moves);

        double startCrossFt = Math.Abs(CrossTrackFt(start.Position, move));
        _output.WriteLine(
            $"R_c={rolloutFt:F2} ft, start {startCrossFt:F2} ft abeam, path {trace.PathLengthFt:F0} ft vs bound {boundFt:F1} ft, "
                + $"end cross {trace.EndCrossTrackFt:F2} ft, end Δχ {trace.EndLineTravelErrorDeg:F2}°"
        );
        Assert.InRange(startCrossFt, SixAAbeamFt - 1.0, SixAAbeamFt + 1.0);
        Assert.True(trace.Completed, "the capture never completed");
        Assert.True(trace.PathLengthFt <= boundFt, $"the capture flew {trace.PathLengthFt:F0} ft, over the {boundFt:F1} ft bound");
    }

    /// <summary>
    /// Pushing down the 6B lane from 70 ft abeam, parallel to it, with the stop far enough back that the capture
    /// comes first: the move carries on along the lane and ends at the stop.
    /// </summary>
    [Fact]
    public void ViaLineWithStopAt_CaptureBeforeTheStop_EndsAtTheStop()
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        double pushLineDeg = lane.TravelDeg + 180.0;
        LatLon stop = GeoMath.ProjectPoint(lane.SixB.Position, new TrueHeading(pushLineDeg), CaptureFirstStopFt / GeoMath.FeetPerNm);
        var start = new TugPose(TowardSixA(lane, 70.0), lane.TravelDeg);
        var move = TugMove.ViaLine(PushbackLegKind.Push, lane.SixB.Position, pushLineDeg, stop);

        TugMoveTrace trace = Assert.Single(TugKinematics.Simulate(start, [move], Narrowbody, StepFt).Moves);

        double alongFt = AlongFt(trace.End.Position, stop, pushLineDeg);
        bool capturedShortOfStop = trace.Samples.Any(s =>
            (Math.Abs(CrossTrackFt(s.Position, move)) <= 1.0) && (AlongFt(s.Position, stop, pushLineDeg) < -3.0)
        );
        _output.WriteLine(
            $"path {trace.PathLengthFt:F0} ft, along-line from the stop {alongFt:F2} ft, overshoot {trace.EndOvershootFt:F2} ft, "
                + $"end cross {trace.EndCrossTrackFt:F2} ft, on the lane short of the stop: {capturedShortOfStop}"
        );
        Assert.True(capturedShortOfStop, "the lane was not captured short of the stop, so the case does not pin capture-then-stop");
        Assert.True(trace.Completed, "the stop was never reached");
        Assert.True(Math.Abs(alongFt) <= 3.0, $"stopped {alongFt:F2} ft along the lane from the stop");
        Assert.Equal(alongFt, Assert.NotNull(trace.EndOvershootFt), 6);
        Assert.True(Math.Abs(Assert.NotNull(trace.EndCrossTrackFt)) <= 1.0, $"ended {trace.EndCrossTrackFt:F2} ft off the lane");
    }

    /// <summary>
    /// The 6A → 6B push with the stop 100 ft behind 6B: the stop comes before the capture, so the move carries on
    /// until it has captured the lane and reports how far past the stop that left it.
    /// </summary>
    [Fact]
    public void ViaLineWithStopAt_StopBeforeCapture_CarriesOnUntilCaptured()
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        double pushLineDeg = lane.TravelDeg + 180.0;
        LatLon staging = GeoMath.ProjectPoint(lane.SixB.Position, new TrueHeading(pushLineDeg), 100.0 / GeoMath.FeetPerNm);
        var start = new TugPose(lane.SixA.Position, lane.TravelDeg);
        var move = TugMove.ViaLine(PushbackLegKind.Push, lane.SixB.Position, pushLineDeg, staging);

        TugMoveTrace trace = Assert.Single(TugKinematics.Simulate(start, [move], Narrowbody, StepFt).Moves);

        double endCrossFt = Assert.NotNull(trace.EndCrossTrackFt);
        double endErrorDeg = Assert.NotNull(trace.EndLineTravelErrorDeg);
        double overshootFt = Assert.NotNull(trace.EndOvershootFt);
        _output.WriteLine($"path {trace.PathLengthFt:F0} ft, overshoot {overshootFt:F2} ft, end cross {endCrossFt:F2} ft, end Δχ {endErrorDeg:F2}°");
        Assert.True(trace.Completed, "the move never completed");
        Assert.True(Math.Abs(endCrossFt) <= 1.0, $"ended {endCrossFt:F2} ft off the lane — the move stopped before capturing it");
        Assert.True(endErrorDeg <= 1.0, $"ended {endErrorDeg:F2}° off the lane direction");
        Assert.True(overshootFt > 0.0, $"ended {overshootFt:F2} ft along from the stop, but the stop comes before the capture");
    }

    /// <summary>
    /// A pull that captures the 6B lane from 70 ft abeam, 45° in, and follows it to a stop 300 ft down the lane:
    /// once on the line it stays there. Near the line the roll-out angle blends to zero, so the nose settles on the
    /// lane instead of hunting from side to side.
    /// </summary>
    [Fact]
    public void ViaLinePullFollowedToAFarStop_HoldsTheLineWithoutChatter()
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        LatLon stop = GeoMath.ProjectPoint(lane.SixB.Position, new TrueHeading(lane.TravelDeg), FarStopFt / GeoMath.FeetPerNm);
        var move = TugMove.ViaLine(PushbackLegKind.Pull, lane.SixB.Position, lane.TravelDeg, stop);
        LatLon position = TowardSixA(lane, 70.0);
        double towardLine = CrossTrackFt(position, move) > 0.0 ? -45.0 : 45.0;
        var start = new TugPose(position, lane.TravelDeg + towardLine);

        TugMoveTrace trace = Assert.Single(TugKinematics.Simulate(start, [move], Narrowbody, StepFt).Moves);

        var lastStretch = trace.Samples.Where(s => AlongFt(s.Position, stop, lane.TravelDeg) >= -SettledStretchFt).ToList();
        double worstNoseDeg = lastStretch.Max(s => AbsDiffDeg(s.NoseTrueDeg, lane.TravelDeg));
        double overshootFt = FarSideOvershootFt(trace, move);
        _output.WriteLine(
            $"path {trace.PathLengthFt:F0} ft, {lastStretch.Count} samples in the last {SettledStretchFt:F0} ft, worst nose error there "
                + $"{worstNoseDeg:F3}°, far-side overshoot {overshootFt:F3} ft, end overshoot {trace.EndOvershootFt:F2} ft"
        );
        Assert.True(trace.Completed, "the move never reached its stop");
        Assert.True(lastStretch.Count >= 10, $"only {lastStretch.Count} samples in the last {SettledStretchFt:F0} ft");
        Assert.True(worstNoseDeg <= 0.1, $"the nose was {worstNoseDeg:F3}° off the lane in the last {SettledStretchFt:F0} ft");
        Assert.True(overshootFt <= 0.5, $"the path overshot the lane by {overshootFt:F3} ft");
    }

    [Theory]
    [InlineData(PushbackLegKind.Push)]
    [InlineData(PushbackLegKind.Pull)]
    public void EveryStep_BodyFollowsTheDirectionOfTravel(PushbackLegKind kind)
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        (TugPose start, TugMove _) = AbeamPushStart(lane, lane.SixA.Position, 45.0);
        var move = TugMove.ViaLine(kind, lane.SixB.Position, lane.TravelDeg + 180.0, stopAt: null);
        TugPose pose = kind == PushbackLegKind.Push ? start : start with { NoseTrueDeg = start.NoseTrueDeg + 180.0 };
        var progress = TugMoveProgress.Begin(pose, move);
        double radiusFt = TugKinematics.TurnRadiusFt(Narrowbody, tight: false);

        int steps = 0;
        for (; (steps < 600) && !TugKinematics.IsComplete(pose, move, progress); steps++)
        {
            double travel = TugKinematics.SteerTravel(pose, move, progress, radiusFt, StepFt);
            TugPose next = TugKinematics.Advance(pose, move, travel, StepFt);
            progress = TugKinematics.Record(progress, next, move, StepFt);

            double movedDeg = GeoMath.BearingTo(pose.Position, next.Position);
            double expectedNose = kind == PushbackLegKind.Push ? movedDeg + 180.0 : movedDeg;
            Assert.True(AbsDiffDeg(movedDeg, travel) <= 0.01, $"step {steps}: moved along {movedDeg:F3}°, steered {travel:F3}°");
            Assert.True(AbsDiffDeg(next.NoseTrueDeg, expectedNose) <= 0.01, $"step {steps}: nose {next.NoseTrueDeg:F3}° crabs off {movedDeg:F3}°");
            pose = next;
        }

        Assert.True(TugKinematics.IsComplete(pose, move, progress), "the capture never completed");
        Assert.True(steps > 20, "the move was too short to exercise the body");
    }

    [Theory]
    [InlineData(PushbackLegKind.Push)]
    [InlineData(PushbackLegKind.Pull)]
    public void TurnTo_FliesAConstantRadiusArcOntoTheFacing(PushbackLegKind kind)
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        var start = new TugPose(lane.SixB.Position, lane.TravelDeg);
        double facingDeg = lane.TravelDeg + 90.0;
        var move = TugMove.TurnTo(kind, facingDeg);
        double radiusFt = TugKinematics.TurnRadiusFt(Narrowbody, tight: false);

        TugMoveTrace trace = Assert.Single(TugKinematics.Simulate(start, [move], Narrowbody, StepFt).Moves);

        Assert.True(trace.Completed, "the turn never completed");
        Assert.True(AbsDiffDeg(trace.End.NoseTrueDeg, facingDeg) <= 0.5, $"ended with the nose on {trace.End.NoseTrueDeg:F2}°");
        Assert.True(trace.Samples.Count >= 5, "too few samples to fit an arc");

        LatLon origin = start.Position;
        (double X, double Y) centre = Circumcentre(
            LocalFt(origin, trace.Samples[0].Position),
            LocalFt(origin, trace.Samples[trace.Samples.Count / 2].Position),
            LocalFt(origin, trace.Samples[^1].Position)
        );
        double worstErrorFt = 0.0;
        foreach (TugPose sample in trace.Samples)
        {
            (double X, double Y) p = LocalFt(origin, sample.Position);
            worstErrorFt = Math.Max(worstErrorFt, Math.Abs(Math.Sqrt(Sq(p.X - centre.X) + Sq(p.Y - centre.Y)) - radiusFt));
        }

        _output.WriteLine($"{kind}: path {trace.PathLengthFt:F1} ft, R={radiusFt:F1} ft, worst radius error {worstErrorFt:F3} ft");
        Assert.True(worstErrorFt <= radiusFt * 0.01, $"arc deviates {worstErrorFt:F3} ft from R={radiusFt:F1} ft");
    }

    [Fact]
    public void TurnRadius_ScalesWithWheelbaseAndFallsBackByCategory()
    {
        if (!FaaAircraftDatabase.IsInitialized)
        {
            return;
        }

        double widebody = TugKinematics.TurnRadiusFt(Widebody, tight: false);
        double narrowbody = TugKinematics.TurnRadiusFt(Narrowbody, tight: false);
        double narrowbodyTight = TugKinematics.TurnRadiusFt(Narrowbody, tight: true);
        double tightFactor = 1.0 / Math.Tan(67.5 * Math.PI / 180.0);

        Assert.True(widebody > narrowbody, $"B77W R={widebody:F1} ft is not wider than B738 R={narrowbody:F1} ft");
        Assert.True(narrowbodyTight < narrowbody, $"tight R={narrowbodyTight:F1} ft is not below routine R={narrowbody:F1} ft");
        Assert.Equal(narrowbody * tightFactor, narrowbodyTight, 6);

        Assert.Null(FaaAircraftDatabase.Get("ZZZZ"));
        Assert.Equal(AircraftCategory.Jet, AircraftCategorization.Categorize("ZZZZ"));
        Assert.Equal(50.0, TugKinematics.TurnRadiusFt("ZZZZ", tight: false), 6);
        Assert.Equal(50.0 * tightFactor, TugKinematics.TurnRadiusFt("ZZZZ", tight: true), 6);
    }

    [Fact]
    public void ToPointInsideTheTurningCircle_ExceedsTheBudgetAndSkipsTheRest()
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        var start = new TugPose(lane.SixB.Position, lane.TravelDeg);
        LatLon abeam = GeoMath.ProjectPoint(start.Position, new TrueHeading(lane.TravelDeg + 90.0), 10.0 / GeoMath.FeetPerNm);
        TugMove[] moves = [TugMove.ToPoint(PushbackLegKind.Pull, abeam), TugMove.Straight(PushbackLegKind.Pull, 10.0)];

        TugSimulation simulation = TugKinematics.Simulate(start, moves, Widebody, StepFt);

        Assert.Equal(2, simulation.Moves.Count);
        _output.WriteLine($"first move flew {simulation.Moves[0].PathLengthFt:F0} ft before giving up");
        Assert.False(simulation.Moves[0].Completed, "a point inside the turning circle was reported reachable");
        Assert.True(simulation.Moves[0].PathLengthFt > 600.0, "the move gave up before its budget");
        Assert.False(simulation.Moves[1].Completed, "the move after an unflyable one was reported completed");
        Assert.Empty(simulation.Moves[1].Samples);
    }

    /// <summary>
    /// A to-point move whose steps are longer than the 1 ft stop window must still end on its point: a widebody
    /// 11 ft short of a point 2 ft off its track cannot turn onto it in time, so it passes the point abeam at more
    /// than 1 ft. It completes there (within 3 ft, with the point abeam or behind) instead of orbiting back.
    /// </summary>
    [Fact]
    public void ToPointPassedAbeamWithLongSteps_CompletesInsteadOfOrbiting()
    {
        if (LoadLane() is not { } lane)
        {
            return;
        }

        const double longStepFt = 2.0;
        var start = new TugPose(OnLaneBeforeSixB(lane), lane.TravelDeg);
        LatLon ahead = GeoMath.ProjectPoint(start.Position, new TrueHeading(lane.TravelDeg), 11.0 / GeoMath.FeetPerNm);
        LatLon target = GeoMath.ProjectPoint(ahead, new TrueHeading(lane.TravelDeg + 90.0), 2.0 / GeoMath.FeetPerNm);

        TugMoveTrace trace = Assert.Single(
            TugKinematics.Simulate(start, [TugMove.ToPoint(PushbackLegKind.Pull, target)], Widebody, longStepFt).Moves
        );
        double endOffFt = FeetBetween(trace.End.Position, target);
        _output.WriteLine($"completed={trace.Completed}, flew {trace.PathLengthFt:F1} ft, ended {endOffFt:F2} ft off the point");

        Assert.True(trace.Completed, $"the move never completed; it flew {trace.PathLengthFt:F0} ft");
        Assert.True(trace.PathLengthFt <= 20.0, $"the move flew {trace.PathLengthFt:F1} ft for a point 11 ft ahead — it went round again");
        Assert.True(endOffFt <= 3.0, $"the move ended {endOffFt:F2} ft off its point");
    }

    private sealed record Lane(GroundNode SixA, GroundNode SixB, double TravelDeg);

    private static Lane? LoadLane()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        GroundNode sixB = Spot(layout, "6B");
        Assert.True(layout.TryGetSpotOutboundHeading(sixB, out double laneDeg), "SFO layout gives spot 6B no outbound heading");
        return new Lane(Spot(layout, "6A"), sixB, laneDeg);
    }

    /// <summary>A point on the 6B lane <see cref="LaneRunUpFt"/> before 6B, so a pull along the lane has room ahead.</summary>
    private static LatLon OnLaneBeforeSixB(Lane lane) =>
        GeoMath.ProjectPoint(lane.SixB.Position, new TrueHeading(lane.TravelDeg + 180.0), LaneRunUpFt / GeoMath.FeetPerNm);

    /// <summary>On the lane, pulling, with the travel <paramref name="angleOffDeg"/> off the lane direction.</summary>
    private static (TugPose Start, TugMove Move) OnLinePullStart(Lane lane, double angleOffDeg)
    {
        var move = TugMove.ViaLine(PushbackLegKind.Pull, lane.SixB.Position, lane.TravelDeg, stopAt: null);
        return (new TugPose(OnLaneBeforeSixB(lane), lane.TravelDeg + angleOffDeg), move);
    }

    /// <summary>
    /// Off the lane at <paramref name="position"/>, pushing down the 6B lane (travel = lane reciprocal), with the
    /// travel turned <paramref name="angleOffDeg"/> toward the lane — the 6A → 6B reposition.
    /// </summary>
    private static (TugPose Start, TugMove Move) AbeamPushStart(Lane lane, LatLon position, double angleOffDeg)
    {
        double lineDeg = lane.TravelDeg + 180.0;
        var move = TugMove.ViaLine(PushbackLegKind.Push, lane.SixB.Position, lineDeg, stopAt: null);
        double towardLine = CrossTrackFt(position, move) > 0.0 ? -angleOffDeg : angleOffDeg;
        double travel = lineDeg + towardLine;
        return (new TugPose(position, travel + 180.0), move);
    }

    /// <summary>The point <paramref name="distanceFt"/> from 6B on the way to 6A (6A sits abeam the lane).</summary>
    private static LatLon TowardSixA(Lane lane, double distanceFt) =>
        GeoMath.ProjectPoint(
            lane.SixB.Position,
            new TrueHeading(GeoMath.BearingTo(lane.SixB.Position, lane.SixA.Position)),
            distanceFt / GeoMath.FeetPerNm
        );

    public enum StartAt
    {
        OnLane,
        SeventyFeetAbeam,
        SixA,
    }

    /// <summary>
    /// How far the path passes beyond the lane on the side opposite the one it approached from. The approach side
    /// is the first sample more than half a foot off the lane; a path that never leaves the lane cannot overshoot.
    /// </summary>
    private static double FarSideOvershootFt(TugMoveTrace trace, TugMove move)
    {
        double nearSign = 0.0;
        double overshootFt = 0.0;
        foreach (TugPose sample in trace.Samples.Append(trace.End))
        {
            double crossFt = CrossTrackFt(sample.Position, move);
            if ((nearSign == 0.0) && (Math.Abs(crossFt) > 0.5))
            {
                nearSign = Math.Sign(crossFt);
            }

            overshootFt = Math.Max(overshootFt, -nearSign * crossFt);
        }

        return overshootFt;
    }

    private static double AlongFt(LatLon point, LatLon reference, double lineDeg) =>
        GeoMath.AlongTrackDistanceNm(point, reference, new TrueHeading(lineDeg)) * GeoMath.FeetPerNm;

    private static double CrossTrackFt(LatLon point, TugMove move) =>
        GeoMath.SignedCrossTrackDistanceNm(point, move.Point, new TrueHeading(move.LineTravelTrueDeg)) * GeoMath.FeetPerNm;

    private static double TravelChangeDeg(TugPose from, TugPose to, PushbackLegKind kind) =>
        AbsDiffDeg(from.TravelTrueDeg(kind), to.TravelTrueDeg(kind));

    private static double AbsDiffDeg(double a, double b) => new TrueHeading(a).AbsAngleTo(new TrueHeading(b));

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>Flat east/north feet from <paramref name="origin"/>, the same frame <c>GeoMath.ProjectPoint</c> steps in.</summary>
    private static (double X, double Y) LocalFt(LatLon origin, LatLon point)
    {
        double y = (point.Lat - origin.Lat) * 60.0 * GeoMath.FeetPerNm;
        double x = (point.Lon - origin.Lon) * 60.0 * Math.Cos(origin.Lat * Math.PI / 180.0) * GeoMath.FeetPerNm;
        return (x, y);
    }

    private static (double X, double Y) Circumcentre((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double d = 2.0 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y)));
        double aa = Sq(a.X) + Sq(a.Y);
        double bb = Sq(b.X) + Sq(b.Y);
        double cc = Sq(c.X) + Sq(c.Y);
        double x = ((aa * (b.Y - c.Y)) + (bb * (c.Y - a.Y)) + (cc * (a.Y - b.Y))) / d;
        double y = ((aa * (c.X - b.X)) + (bb * (a.X - c.X)) + (cc * (b.X - a.X))) / d;
        return (x, y);
    }

    private static double Sq(double v) => v * v;

    private static GroundNode Spot(AirportGroundLayout layout, string name) =>
        layout.FindSpotNodeByName(name) ?? throw new InvalidOperationException($"SFO layout carries no spot named {name}");
}
