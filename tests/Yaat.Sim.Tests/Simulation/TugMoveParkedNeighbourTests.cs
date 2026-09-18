using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tug moves against parked neighbours on real layouts: the conflict detector sweeps the moved aircraft's outline along
/// the rest of its move and lets it carry on unless some part would come within the 25 ft wingtip buffer of the
/// neighbour's outline (or, for a neighbour already that close, closer than it is now). A move that would run into a
/// parked aircraft still stops short of it and shows who it waits for.
/// </summary>
public class TugMoveParkedNeighbourTests(ITestOutputHelper output)
{
    private const string Mover = "TUG1";
    private const string Parked = "PRK1";
    private const string Narrowbody = "B738";

    /// <summary>How long each scenario runs, seconds; every move here finishes in well under a minute at 5 kt.</summary>
    private const int BudgetSeconds = 120;

    /// <summary>The longest run of seconds a mover that should pass may sit at a zero speed limit; a judgement call.</summary>
    private const int MaxHoldSeconds = 5;

    /// <summary>
    /// The least outline clearance a tug move stopped for a parked aircraft may leave it, feet — a stop that leaves less
    /// than this has the two aircraft close enough to touch on any of the outline's judgement calls.
    /// </summary>
    private const double MinStopGapFt = 10.0;

    /// <summary>How far apart the outlines of the abeam pair start, feet: inside the wingtip buffer, clear of contact.</summary>
    private const double StartingGapFt = 15.0;

    /// <summary>
    /// How much more than the towbar braking rate (<see cref="CategoryPerformance.TugDecelRate"/>) one second's fall in
    /// ground speed may measure, knots. The limit is re-measured every quarter second against a path sampled about a
    /// foot apart, so a second's fall lands a little either side of the rate; this is that sampling, not slack in the
    /// rate itself.
    /// </summary>
    private const double BrakingToleranceKt = 0.05;

    /// <summary>
    /// The crawl a tow is down to as it comes to rest, knots: a second that <em>ends</em> at or below this and starts at
    /// or below twice it is the tow stopping, not a braking event, and is not judged against the towbar rate.
    ///
    /// <para>The detector's outline limit is a √ curve that ends at zero, hard-clamped onto the speed four times a
    /// second, with the mover running each quarter second at the value that quarter second opened with. The tow
    /// therefore reaches the crawl part-way through a second, and that straddling second sheds up to about 1.2 kt —
    /// 0.057 g against the towbar rate's 0.052 g. A second that falls from the commanded 5 kt is still judged: only one
    /// that both ends on the crawl and starts within twice it is the stop.</para>
    ///
    /// <para><c>PushbackMoveBoundaryTests</c> excepts the end of a move under the same name; its tow is already at
    /// <see cref="PushbackPhase.FinalApproachKts"/> when the last second opens, because a move's own stop is a target
    /// speed physics approaches at the towbar rate rather than a clamp.</para>
    /// </summary>
    private const double CrawlBeforeStopKts = 1.5;

    private (SimulationEngine Engine, AirportGroundLayout Layout)? Build(string airport)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout(airport) is not { } layout))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = $"test-tug-parked-{airport}",
                ScenarioName = "Tug move vs parked neighbour",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
                AutoCrossRunway = false,
            },
        };
        return (engine, layout);
    }

    /// <summary>An aircraft at <paramref name="position"/>, nose on <paramref name="noseTrueDeg"/>, in <paramref name="phase"/>.</summary>
    private static AircraftState Spawn(
        SimulationEngine engine,
        AirportGroundLayout layout,
        string callsign,
        (LatLon Position, double NoseTrueDeg) pose,
        Phase phase
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = Narrowbody,
            Position = pose.Position,
            TrueHeading = new TrueHeading(pose.NoseTrueDeg),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = layout.AirportId,
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(phase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static AircraftState SpawnOnStand(SimulationEngine engine, AirportGroundLayout layout, string callsign, string standName)
    {
        GroundNode stand =
            layout.FindParkingByName(standName) ?? throw new InvalidOperationException($"{layout.AirportId} has no stand '{standName}'");
        return Spawn(engine, layout, callsign, (stand.Position, Assert.NotNull(stand.TrueHeading).Degrees), new AtParkingPhase());
    }

    /// <summary>A tug move of one pull, straight ahead for <paramref name="distanceFt"/>, not from a stand.</summary>
    private static PushbackPhase StraightPull((LatLon Position, double NoseTrueDeg) start, double distanceFt)
    {
        var move = TugMove.Straight(PushbackLegKind.Pull, distanceFt);
        LatLon end = TugKinematics.Simulate(new TugPose(start.Position, start.NoseTrueDeg), [move], Narrowbody, 1.0).End.Position;
        return new PushbackPhase
        {
            Move = move,
            PlannedEnd = end,
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
        };
    }

    /// <summary>What a run of a tug move next to a parked aircraft did.</summary>
    private sealed record Run(int CompletedSecond, int LongestHoldSeconds, double ClosestFt, double ClosestWithTugFt, double LargestSpeedDropKt);

    /// <summary>
    /// Ticks until the mover's tug moves are all done or <see cref="BudgetSeconds"/> pass, tracking the longest run of
    /// seconds at a zero speed limit, the closest the two outlines came, and the largest fall in ground speed between
    /// two consecutive seconds (a tug brakes at <see cref="CategoryPerformance.TugDecelRate"/>; it never drops the
    /// aircraft's speed in one step). A second that ends at or below <see cref="CrawlBeforeStopKts"/> having started
    /// within twice it is the tow stopping, not braking, and is left out of that figure.
    /// </summary>
    private Run TickPast(SimulationEngine engine, AircraftState mover, AircraftState parked, bool pulled)
    {
        int holdSeconds = 0;
        int longestHold = 0;
        double closestFt = GroundOutline.ClearanceBetween(mover, aTowedNoseFirst: false, parked);
        double closestWithTugFt = GroundOutline.ClearanceBetween(mover, aTowedNoseFirst: pulled, parked);
        double previousSpeedKts = mover.GroundSpeed;
        double largestDropKt = 0.0;
        for (int t = 1; t <= BudgetSeconds; t++)
        {
            engine.TickOneSecond();
            closestFt = Math.Min(closestFt, GroundOutline.ClearanceBetween(mover, aTowedNoseFirst: false, parked));
            closestWithTugFt = Math.Min(closestWithTugFt, GroundOutline.ClearanceBetween(mover, aTowedNoseFirst: pulled, parked));
            bool comingToRest = (mover.GroundSpeed <= CrawlBeforeStopKts) && (previousSpeedKts <= (2.0 * CrawlBeforeStopKts));
            if (!comingToRest)
            {
                largestDropKt = Math.Max(largestDropKt, previousSpeedKts - mover.GroundSpeed);
            }

            previousSpeedKts = mover.GroundSpeed;
            if (mover.Phases?.CurrentPhase is not PushbackPhase)
            {
                return new Run(t, longestHold, closestFt, closestWithTugFt, largestDropKt);
            }

            bool held = mover.Ground.SpeedLimit is <= 0.0;
            holdSeconds = held ? holdSeconds + 1 : 0;
            longestHold = Math.Max(longestHold, holdSeconds);
        }

        return new Run(-1, longestHold, closestFt, closestWithTugFt, largestDropKt);
    }

    /// <summary>
    /// The most a second's fall in ground speed may measure for a tug move braking for a parked neighbour, knots: the
    /// towbar braking rate plus <see cref="BrakingToleranceKt"/> of sampling.
    /// </summary>
    private static double MaxSpeedDropKt => CategoryPerformance.TugDecelRate(AircraftCategory.Jet) + BrakingToleranceKt;

    private void Report(string scenario, AircraftState mover, AircraftState parked, Run run)
    {
        double nowFt = GroundOutline.ClearanceBetween(mover, aTowedNoseFirst: false, parked);
        output.WriteLine(
            $"{scenario}: completed at t={run.CompletedSecond}s, longest hold {run.LongestHoldSeconds}s, closest outline {run.ClosestFt:F1} ft "
                + $"({run.ClosestWithTugFt:F1} ft counting a tug), phase={mover.Phases?.CurrentPhase?.Name ?? "none"}, "
                + $"limit={mover.Ground.SpeedLimit?.ToString("F1") ?? "none"}, gs={mover.GroundSpeed:F2}kt, "
                + $"largest fall {run.LargestSpeedDropKt:F2}kt in a second, "
                + $"yield={mover.Ground.AutoYieldTarget ?? "-"}, now {nowFt:F1} ft apart"
        );
    }

    /// <summary>
    /// The least outline clearance the mover's queued tug moves would reach against <paramref name="parked"/>, flown
    /// from where it is now, feet (a pull counts its tug): zero means the moves, left alone, would run into it.
    /// </summary>
    private static double PlannedPathClosestFt(AircraftState mover, AircraftState parked)
    {
        var moves = mover.Phases!.Phases.OfType<PushbackPhase>().Where(p => p.Status != PhaseStatus.Completed).Select(p => p.Move).ToList();
        TugSimulation simulation = TugKinematics.Simulate(new TugPose(mover.Position, mover.TrueHeading.Degrees), moves, mover.AircraftType, 1.0);
        var frame = new GroundOutlineFrame(mover.Position);
        var parkedOutline = GroundOutline.At(
            frame.ToLocal(parked.Position),
            parked.TrueHeading.Degrees,
            GroundOutlineSize.Of(parked.AircraftType, false)
        );
        return simulation
            .Moves.SelectMany(trace =>
                trace.Samples.Select(pose =>
                    GroundOutline.Clearance(
                        GroundOutline.At(
                            frame.ToLocal(pose.Position),
                            pose.NoseTrueDeg,
                            GroundOutlineSize.Of(mover.AircraftType, towedNoseFirst: trace.Move.Kind == PushbackLegKind.Pull)
                        ),
                        parkedOutline
                    )
                )
            )
            .Min();
    }

    /// <summary>
    /// OAK gate 25, <c>PUSH TE</c> (issue #222's push), with a B738 parked crossways on the push line 230 ft behind the
    /// stand: the push would sweep through it, so the tug stops the aircraft short of it and the pusher shows the parked
    /// aircraft as what it waits for.
    /// </summary>
    [Fact]
    public void PushIntoAircraftParkedAcrossThePushLine_StopsShortOfItAndShowsIt()
    {
        if (Build("OAK") is not { } built)
        {
            return;
        }

        (SimulationEngine? engine, AirportGroundLayout? layout) = built;

        AircraftState pusher = SpawnOnStand(engine, layout, Mover, "25");
        double pushDeg = new TrueHeading(pusher.TrueHeading.Degrees + 180.0).Degrees;
        LatLon blockPosition = GeoMath.ProjectPoint(pusher.Position, new TrueHeading(pushDeg), 230.0 / GeoMath.FeetPerNm);
        AircraftState parked = Spawn(engine, layout, Parked, (blockPosition, pushDeg + 90.0), new AtParkingPhase());

        CommandResult push = engine.SendCommand(Mover, "PUSH TE");
        Assert.True(push.Success, $"PUSH TE off gate 25 was refused: {push.Message}");
        double wouldReachFt = PlannedPathClosestFt(pusher, parked);
        output.WriteLine($"left alone, the push would bring the outlines to {wouldReachFt:F1} ft");
        Assert.True(wouldReachFt <= 0.0, $"test setup: the planned push passes {wouldReachFt:F1} ft clear of the parked aircraft");

        Run run = TickPast(engine, pusher, parked, pulled: false);
        Report("push into a parked aircraft", pusher, parked, run);

        Assert.True(run.CompletedSecond < 0, $"the push completed at t={run.CompletedSecond}s through the parked aircraft");
        Assert.Equal(0.0, pusher.Ground.SpeedLimit);
        Assert.True(pusher.GroundSpeed <= 0.01, $"the pusher is still rolling at {pusher.GroundSpeed:F2} kt");
        Assert.Equal(Parked, pusher.Ground.AutoYieldTarget);

        double finalGapFt = GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, parked);
        output.WriteLine($"closest over the run {run.ClosestWithTugFt:F1} ft; at rest the outlines are {finalGapFt:F1} ft apart");
        Assert.True(run.ClosestWithTugFt >= MinStopGapFt, $"the pusher's outline came within {run.ClosestWithTugFt:F1} ft of the parked aircraft's");
        Assert.True(finalGapFt >= MinStopGapFt, $"the pusher came to rest {finalGapFt:F1} ft from the parked aircraft");
        Assert.True(
            run.LargestSpeedDropKt <= MaxSpeedDropKt,
            $"the push lost {run.LargestSpeedDropKt:F2} kt in one second braking for the parked aircraft, past the towbar rate "
                + $"({MaxSpeedDropKt:F2} kt/s)"
        );
    }

    /// <summary>
    /// A B738 pulled 97 ft onto SFO spot 6A's stop point from its staging point, past a B738 parked 120 ft to its left
    /// and 60 ft ahead of where the pull ends, pointing the same way. The two outlines never come closer than about
    /// 38 ft, so the pull is not held — although the parked aircraft sits ahead of the nose inside the stop distance
    /// with less than two half-spans plus 25 ft of lateral room.
    /// </summary>
    [Fact]
    public void PullPastAircraftParkedClearOfItsPath_IsNotHeld()
    {
        if (Build("SFO") is not { } built)
        {
            return;
        }

        (SimulationEngine? engine, AirportGroundLayout? layout) = built;

        (LatLon start, LatLon stop, double noseDeg) = SixAPull(layout);
        double pullFt = GeoMath.DistanceNm(start, stop) * GeoMath.FeetPerNm;
        LatLon clearSpot = Offset(stop, noseDeg, aheadFt: 60.0, rightFt: -120.0);
        AircraftState parked = Spawn(engine, layout, Parked, (clearSpot, noseDeg), new AtParkingPhase());
        AircraftState puller = Spawn(engine, layout, Mover, (start, noseDeg), StraightPull((start, noseDeg), pullFt));
        output.WriteLine($"pull {pullFt:F1} ft on {noseDeg:F1}°; left alone the pull stays {PlannedPathClosestFt(puller, parked):F1} ft clear");

        Run run = TickPast(engine, puller, parked, pulled: true);
        Report("pull past a parked aircraft", puller, parked, run);

        Assert.True(run.CompletedSecond > 0, $"the pull never finished within {BudgetSeconds}s");
        Assert.True(run.LongestHoldSeconds <= MaxHoldSeconds, $"the pull was held at a zero speed limit for {run.LongestHoldSeconds}s");
        Assert.True(run.ClosestWithTugFt >= GroundConflictDetector.WingtipBufferFt, $"the outlines came within {run.ClosestWithTugFt:F1} ft");
    }

    /// <summary>
    /// A B738 pulled 150 ft from SFO spot 6A's staging point toward a B738 parked crossways on its line 200 ft ahead of
    /// the start: the pull would run the tug and nose into it, so it stops short and shows the parked aircraft as what
    /// it waits for.
    /// </summary>
    [Fact]
    public void PullIntoAircraftParkedOnItsPath_StopsShortOfItAndShowsIt()
    {
        if (Build("SFO") is not { } built)
        {
            return;
        }

        (SimulationEngine? engine, AirportGroundLayout? layout) = built;

        (LatLon start, LatLon _, double noseDeg) = SixAPull(layout);
        LatLon blockPosition = Offset(start, noseDeg, aheadFt: 200.0, rightFt: 0.0);
        AircraftState parked = Spawn(engine, layout, Parked, (blockPosition, noseDeg + 90.0), new AtParkingPhase());
        AircraftState puller = Spawn(engine, layout, Mover, (start, noseDeg), StraightPull((start, noseDeg), 150.0));
        double wouldReachFt = PlannedPathClosestFt(puller, parked);
        output.WriteLine($"left alone, the pull would bring the outlines (tug included) to {wouldReachFt:F1} ft");
        Assert.True(wouldReachFt <= 0.0, $"test setup: the pull passes {wouldReachFt:F1} ft clear of the parked aircraft");

        Run run = TickPast(engine, puller, parked, pulled: true);
        Report("pull into a parked aircraft", puller, parked, run);

        Assert.True(run.CompletedSecond < 0, $"the pull completed at t={run.CompletedSecond}s through the parked aircraft");
        Assert.Equal(0.0, puller.Ground.SpeedLimit);
        Assert.True(puller.GroundSpeed <= 0.01, $"the puller is still rolling at {puller.GroundSpeed:F2} kt");
        Assert.Equal(Parked, puller.Ground.AutoYieldTarget);

        double finalGapFt = GroundOutline.ClearanceBetween(puller, aTowedNoseFirst: true, parked);
        output.WriteLine($"closest over the run {run.ClosestWithTugFt:F1} ft counting the tug; at rest the outlines are {finalGapFt:F1} ft apart");
        Assert.True(
            run.ClosestWithTugFt >= MinStopGapFt,
            $"the pulled aircraft's outline came within {run.ClosestWithTugFt:F1} ft of the parked aircraft's"
        );
        Assert.True(finalGapFt >= MinStopGapFt, $"the tow came to rest {finalGapFt:F1} ft from the parked aircraft");
        Assert.True(
            run.LargestSpeedDropKt <= MaxSpeedDropKt,
            $"the pull lost {run.LargestSpeedDropKt:F2} kt in one second braking for the parked aircraft, past the towbar rate "
                + $"({MaxSpeedDropKt:F2} kt/s)"
        );
    }

    /// <summary>
    /// A B738 pushing off SFO gate D15 with a second B738 parked abeam it, about <see cref="StartingGapFt"/> off the
    /// wingtip — already inside the 25 ft wingtip buffer when the push starts, and clear of the push line. The floor's
    /// "no closer than it is now" rule has to hold that pair apart without holding the push: every part of the move
    /// draws the two outlines further apart as the tail comes back, so the push runs to its end.
    /// </summary>
    [Fact]
    public void PushAwayFromANeighbourAlreadyInsideTheBuffer_IsNotHeld()
    {
        if (Build("SFO") is not { } built)
        {
            return;
        }

        (SimulationEngine? engine, AirportGroundLayout? layout) = built;

        AircraftState pusher = SpawnOnStand(engine, layout, Mover, "D15");
        double abeamFt = GroundOutlineSize.Of(Narrowbody, towedNoseFirst: false).WingspanFt + StartingGapFt;
        LatLon abeam = Offset(pusher.Position, pusher.TrueHeading.Degrees, aheadFt: 0.0, rightFt: abeamFt);
        AircraftState parked = Spawn(engine, layout, Parked, (abeam, pusher.TrueHeading.Degrees), new AtParkingPhase());
        double startClearanceFt = GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, parked);
        output.WriteLine($"a B738 parked {abeamFt:F1} ft abeam starts {startClearanceFt:F1} ft off the pusher's outline");
        Assert.InRange(startClearanceFt, 5.0, 25.0);

        CommandResult push = engine.SendCommand(Mover, "PUSH");
        Assert.True(push.Success, $"PUSH off gate D15 was refused: {push.Message}");

        Run run = TickPast(engine, pusher, parked, pulled: false);
        Report("push away from a neighbour inside the buffer", pusher, parked, run);

        Assert.True(run.CompletedSecond > 0, $"the push never finished within {BudgetSeconds}s");
        Assert.True(run.LongestHoldSeconds <= MaxHoldSeconds, $"the push was held at a zero speed limit for {run.LongestHoldSeconds}s");

        // The floor allows the slack (0.5 ft) under the clearance it starts with, and the sweep's samples sit about a
        // foot apart, so the run may measure a foot of closing that the sweep never saw.
        double allowedFt = startClearanceFt - 1.5;
        Assert.True(
            run.ClosestFt >= allowedFt,
            $"the push closed on the parked aircraft: {run.ClosestFt:F1} ft against {startClearanceFt:F1} ft at the start"
        );
    }

    /// <summary>
    /// A half-span less <see cref="StartingGapFt"/> abeam: each aircraft's wing reaches <see cref="StartingGapFt"/>
    /// past the other's fuselage, so the two outlines cross and the clearance is a clean zero.
    /// </summary>
    private static double CrossedWingsAbeamFt => (GroundOutlineSize.Of(Narrowbody, towedNoseFirst: false).WingspanFt / 2.0) - StartingGapFt;

    /// <summary>
    /// A full span less <see cref="StartingGapFt"/> abeam: the wingtips overlap by that much with the two wing
    /// segments collinear, which the clearance measures as a few ten-billionths of a foot rather than zero — contact
    /// inside <see cref="GroundConflictDetector.OutlineClearanceSlackFt"/> all the same.
    /// </summary>
    private static double CollinearWingtipsAbeamFt => GroundOutlineSize.Of(Narrowbody, towedNoseFirst: false).WingspanFt - StartingGapFt;

    /// <summary>
    /// A B738 on SFO gate D15 with a second B738 parked <paramref name="abeamFt"/> abeam — the abeam arithmetic of
    /// <see cref="PushAwayFromANeighbourAlreadyInsideTheBuffer_IsNotHeld"/> taken the other way, near enough that the
    /// two outlines are in contact before the tug moves anything.
    /// </summary>
    private (AircraftState Pusher, AircraftState Neighbour) OverlappingPairOnD15(SimulationEngine engine, AirportGroundLayout layout, double abeamFt)
    {
        AircraftState pusher = SpawnOnStand(engine, layout, Mover, "D15");
        LatLon abeam = Offset(pusher.Position, pusher.TrueHeading.Degrees, aheadFt: 0.0, rightFt: abeamFt);
        AircraftState parked = Spawn(engine, layout, Parked, (abeam, pusher.TrueHeading.Degrees), new AtParkingPhase());
        double startClearanceFt = GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, parked);
        output.WriteLine($"a B738 parked {abeamFt:F1} ft abeam leaves {startClearanceFt:E3} ft of the pusher's outline");
        Assert.True(
            startClearanceFt < GroundConflictDetector.OutlineClearanceSlackFt,
            $"test setup: the pair starts {startClearanceFt:F1} ft apart, clear of the refusal"
        );
        return (pusher, parked);
    }

    /// <summary>
    /// <c>PUSH</c> off SFO gate D15 with a second B738 parked close enough abeam that the two outlines already overlap:
    /// refused at command time, naming both aircraft, with the pusher left parked. Nothing is towed through a modelled
    /// collision.
    /// </summary>
    [Fact]
    public void PushWithANeighbourOverlappingAtTheStart_IsRefusedNamingBoth()
    {
        if (Build("SFO") is not { } built)
        {
            return;
        }

        (SimulationEngine? engine, AirportGroundLayout? layout) = built;
        (AircraftState? pusher, AircraftState _) = OverlappingPairOnD15(engine, layout, CrossedWingsAbeamFt);

        CommandResult push = engine.SendCommand(Mover, "PUSH");
        output.WriteLine($"PUSH: success={push.Success}, message={push.Message}");

        Assert.False(push.Success, "the push was accepted although the pusher's outline already overlaps the parked aircraft's");
        string message = push.Message ?? string.Empty;
        Assert.Contains(Mover, message, StringComparison.Ordinal);
        Assert.Contains(Parked, message, StringComparison.Ordinal);
        Assert.Contains("overlap", message, StringComparison.Ordinal);
        Assert.IsType<AtParkingPhase>(pusher.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The same overlapping pair on SFO gate D15, given the multi-point form <c>PUSHM $6A $6B</c>: refused identically,
    /// with the aircraft left parked.
    /// </summary>
    [Fact]
    public void PushmWithANeighbourOverlappingAtTheStart_IsRefusedNamingBoth()
    {
        if (Build("SFO") is not { } built)
        {
            return;
        }

        (SimulationEngine? engine, AirportGroundLayout? layout) = built;
        (AircraftState? pusher, AircraftState _) = OverlappingPairOnD15(engine, layout, CrossedWingsAbeamFt);

        CommandResult move = engine.SendCommand(Mover, "PUSHM $6A $6B");
        output.WriteLine($"PUSHM $6A $6B: success={move.Success}, message={move.Message}");

        Assert.False(move.Success, "the tug move was accepted although the aircraft's outline already overlaps the parked aircraft's");
        string message = move.Message ?? string.Empty;
        Assert.Contains(Mover, message, StringComparison.Ordinal);
        Assert.Contains(Parked, message, StringComparison.Ordinal);
        Assert.Contains("overlap", message, StringComparison.Ordinal);
        Assert.IsType<AtParkingPhase>(pusher.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The same gate with the neighbour placed wingtip to wingtip instead: the wing segments are collinear, so the
    /// overlap measures a few ten-billionths of a foot rather than a clean zero. Contact inside
    /// <see cref="GroundConflictDetector.OutlineClearanceSlackFt"/> is contact — the push is refused like any other
    /// overlap, rather than slipping through an exact-zero test.
    /// </summary>
    [Fact]
    public void PushWithANeighbourWingtipToWingtipAtTheStart_IsRefused()
    {
        if (Build("SFO") is not { } built)
        {
            return;
        }

        (SimulationEngine? engine, AirportGroundLayout? layout) = built;
        (AircraftState? pusher, AircraftState _) = OverlappingPairOnD15(engine, layout, CollinearWingtipsAbeamFt);

        CommandResult push = engine.SendCommand(Mover, "PUSH");
        output.WriteLine($"PUSH: success={push.Success}, message={push.Message}");

        Assert.False(push.Success, "the push was accepted although the pusher's wingtip is inside the parked aircraft's");
        string message = push.Message ?? string.Empty;
        Assert.Contains(Mover, message, StringComparison.Ordinal);
        Assert.Contains(Parked, message, StringComparison.Ordinal);
        Assert.Contains("overlap", message, StringComparison.Ordinal);
        Assert.IsType<AtParkingPhase>(pusher.Phases?.CurrentPhase);
    }

    /// <summary>SFO spot 6A's staging point, stop point and nose-out heading for a B738.</summary>
    private static (LatLon Start, LatLon Stop, double NoseTrueDeg) SixAPull(AirportGroundLayout layout)
    {
        GroundNode spot = layout.FindSpotNodeByName("6A") ?? throw new InvalidOperationException("SFO has no spot 6A");
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double noseDeg), "spot 6A has no nose-out heading");
        (LatLon stop, LatLon staging) = TugMovePlanner.SpotStopGeometry(spot, noseDeg, Narrowbody);
        return (staging, stop, noseDeg);
    }

    private static LatLon Offset(LatLon from, double noseTrueDeg, double aheadFt, double rightFt)
    {
        LatLon ahead = GeoMath.ProjectPoint(from, new TrueHeading(noseTrueDeg), aheadFt / GeoMath.FeetPerNm);
        return GeoMath.ProjectPoint(ahead, new TrueHeading(noseTrueDeg + 90.0), rightFt / GeoMath.FeetPerNm);
    }
}
