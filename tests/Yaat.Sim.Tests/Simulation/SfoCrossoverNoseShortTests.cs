using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A runway exit or a <c>CLRWY</c> onto one of SFO's crossovers between 28R and 28L stops with the nose short of 28L's holding
/// position marking (AIM 4-3-21.a/b; 7110.65 3-10-9.b NOTE 1: an aircraft may protrude into a taxiway, never past another
/// runway's holding position marking), giving up the tail-clear margin first. Measured on the committed layout
/// (<see cref="ExitCapacityResolver.GetSegments"/>, <see cref="ExitCapacitySegment.StripLengthFt"/>): K is 190.3 ft bar to bar,
/// D 190.4, L 190.9, P 190.7, R 190.2 (C 190.4, N 190.6, E 175.7, T 238.4, Q 253.7). A B788 is 186.1 ft
/// (<see cref="AircraftLength.ResolveFt"/>): with its nose at 28L's line its tail is 2.2 ft past 28R's on the plan, inside the
/// navigator's stop tolerance, so it is not clear of 28R; a B772 is 209.1 ft, longer than K, so with its nose at 28L's line its
/// tail is over 28R's. Either stays an occupant of 28R and reports holding short of 28L, not clear of 28R. An automatic exit
/// choice skips a crossover shorter than the aircraft plus <see cref="RunwayTailClearance.MarginFt"/>, unless no exit fits.
/// Arrivals are staged with automatic pull-up off.
/// </summary>
public class SfoCrossoverNoseShortTests(ITestOutputHelper output)
{
    private const string NotClearReport = "holding short of runway 28L at K, not clear of runway 28R";

    /// <summary>
    /// A B788 told to exit 28R on K stops with its nose at or short of 28L's holding position marking; its planned tail margin
    /// (2.2 ft) is inside the navigator's stop tolerance, so it reports not clear of 28R and stays its occupant.
    /// </summary>
    [Fact]
    public void ExplicitExitOnK_B788_NoseShortOf28L_StaysOccupantNotClearOf28R()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment k = SegmentFor(layout, "K");
        AircraftState arrival = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", "B788", "28R");
        SendExitK(engine);
        var lines = new List<string>();
        Capture(engine, arrival, lines);

        RunUntil(engine, arrival, phase => phase is HoldingAfterExitPhase, settleSeconds: 5);

        AssertNoseShortOf28L(layout, k, arrival);
        AssertReportsNotClear(lines);
        AssertOccupantOf28R(arrival);
    }

    /// <summary>
    /// A B772 told to exit 28R on K stops with its nose at 28L's line and its tail still over 28R's: it is an occupant of 28R,
    /// makes no clear-of-runway call, reports holding short of 28L, not clear of 28R, makes no taxi-in call, and its status line
    /// says where it holds.
    /// </summary>
    [Fact]
    public void ExplicitExitOnK_B772_StaysOccupantAndReportsNotClear()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment k = SegmentFor(layout, "K");
        AircraftState arrival = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", "B772", "28R");
        SendExitK(engine);
        var lines = new List<string>();
        Capture(engine, arrival, lines);

        RunUntil(engine, arrival, phase => phase is HoldingAfterExitPhase, settleSeconds: 5);

        AssertNoseShortOf28L(layout, k, arrival);
        AssertReportsNotClear(lines);
        AssertOccupantOf28R(arrival);
        Assert.False(arrival.Ground.AwaitingTaxiInCall, "the B772 is not clear of 28R, so it owes no taxi-in call yet");
        Assert.Equal("Holding short of 28L on K, not clear of 28R", AircraftStatusDescriber.Describe(arrival, AircraftStatusContext.None).Text);
    }

    /// <summary>
    /// A B772's exit on K saved mid-exit (route built, not yet stopped) comes back with the same tail-inside verdict the live
    /// phase holds, before its first tick rebuilds the route.
    /// </summary>
    [Fact]
    public void ExplicitExitOnK_B772_RestoredMidExit_KeepsTheTailInsideVerdict()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        AircraftState arrival = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", "B772", "28R");
        SendExitK(engine);

        RunUntil(engine, arrival, phase => phase is RunwayExitPhase { StopsTailInsideRunwayLine: true }, settleSeconds: 0);
        Assert.NotNull(arrival.Phases);
        RunwayExitPhase live = Assert.IsType<RunwayExitPhase>(arrival.Phases.CurrentPhase);

        var restored = PhaseList.FromSnapshot(arrival.Phases.ToSnapshot(), layout);

        RunwayExitPhase restoredExit = Assert.IsType<RunwayExitPhase>(restored.CurrentPhase);
        Assert.Equal(live.StopsTailInsideRunwayLine, restoredExit.StopsTailInsideRunwayLine);
    }

    /// <summary>
    /// A B772 holding short on K with its centre at 28R's bar, its tail over 28R, is told <c>CLRWY</c>: it pulls forward only
    /// until its nose reaches 28L's line, holds in position short of 28L with its tail inside 28R, and reports as the exit does.
    /// The state is staged directly: no command sequence reaches CLRWY on K, since CLRWY needs a taxiway hold-short with
    /// <see cref="HoldShortPoint.TailOverRunwayNodeId"/>, which <see cref="HoldShortAnnotator"/> sets only just past a crossed
    /// runway, and K has no taxiway intersection between 28R and 28L.
    /// </summary>
    [Fact]
    public void ClearRunwayOnK_B772_HoldsInPositionShortOf28LNotClearOf28R()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment k = SegmentFor(layout, "K");
        AircraftState aircraft = SpawnTailOver28ROnK(engine, layout, k, "B772", pastBarFt: 0.0);
        var lines = new List<string>();
        Capture(engine, aircraft, lines);

        CommandResult result = engine.SendCommand("UAL1", "CLRWY");
        Assert.True(result.Success, $"CLRWY failed: {result.Message}");
        RunUntil(engine, aircraft, phase => phase is not ClearRunwayPhase, settleSeconds: 5);

        AssertNoseShortOf28L(layout, k, aircraft);
        AssertHeldShortOf28LTailInside28R(aircraft);
        AssertReportsNotClear(lines);
        Assert.Equal("Holding short of 28L on K, not clear of 28R", AircraftStatusDescriber.Describe(aircraft, AircraftStatusContext.None).Text);
    }

    /// <summary>
    /// A B772 already 100 ft past 28R's bar on K, beyond the 83.8 ft its clamped stop allows, its nose about 14 ft past 28L's line
    /// (well beyond the 3.8 ft a stop at that line may read past it), is told
    /// <c>CLRWY</c>: it does not turn round to back up to the stop, it stays where it is and, not holding short of 28L, reports
    /// holding position, not clear of 28R; its status line says only that it is not clear of 28R.
    /// Staged directly for the reason <see cref="ClearRunwayOnK_B772_HoldsInPositionShortOf28LNotClearOf28R"/> gives.
    /// </summary>
    [Fact]
    public void ClearRunwayOnK_B772_AlreadyPastTheClampedStop_StaysAndReports()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment k = SegmentFor(layout, "K");
        AircraftState aircraft = SpawnTailOver28ROnK(engine, layout, k, "B772", pastBarFt: 100.0);
        GroundNode bar28R = layout.Nodes[k.ExitBarFor("28R")];
        GroundNode bar28L = layout.Nodes[k.ExitBarFor("28L")];
        double nosePastBarFt =
            100.0 + (AircraftLength.ResolveFt("B772") / 2.0) - (GeoMath.DistanceNm(bar28R.Position, bar28L.Position) * GeoMath.FeetPerNm);
        output.WriteLine($"nose {nosePastBarFt:F1} ft past 28L's bar");
        Assert.True(nosePastBarFt >= 10.0, $"the nose is only {nosePastBarFt:F1} ft past 28L's bar, too close to the stop's slack");
        double startPastFt = GeoMath.DistanceNm(bar28R.Position, aircraft.Position) * GeoMath.FeetPerNm;
        TrueHeading startHeading = aircraft.TrueHeading;
        var lines = new List<string>();
        Capture(engine, aircraft, lines);

        CommandResult result = engine.SendCommand("UAL1", "CLRWY");
        Assert.True(result.Success, $"CLRWY failed: {result.Message}");
        RunUntil(engine, aircraft, phase => phase is not ClearRunwayPhase, settleSeconds: 5);

        double endPastFt = GeoMath.DistanceNm(bar28R.Position, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"past 28R's bar: {startPastFt:F1} ft -> {endPastFt:F1} ft; heading {startHeading} -> {aircraft.TrueHeading}");
        Assert.True(startHeading.AbsAngleTo(aircraft.TrueHeading) < 5.0, $"the B772 turned from {startHeading} to {aircraft.TrueHeading}");
        Assert.True(endPastFt >= startPastFt - 1.0, $"the B772 backed up from {startPastFt:F1} ft to {endPastFt:F1} ft past 28R's bar");
        HoldingInPositionPhase hold = Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Null(hold.HeldShortOfRunway);
        Assert.Equal("28R", hold.TailInsideRunway);
        foreach (string line in lines)
        {
            output.WriteLine($"pilot: {line}");
        }

        Assert.DoesNotContain(lines, line => line.Contains("holding short of runway 28L", StringComparison.OrdinalIgnoreCase));
        Assert.Single(lines, line => line.Contains("Holding position, not clear of runway 28R", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("On K, not clear of 28R", AircraftStatusDescriber.Describe(aircraft, AircraftStatusContext.None).Text);
    }

    /// <summary>
    /// A B772 told <c>CLRWY</c> on K while already at its clamped stop, with its nose 1 ft past 28L's bar on the line from 28R's:
    /// the cut-back stop aims the nose <c>GroundNavigator.SetBackStopMarginFt</c> short of the bar and the navigator arrives within
    /// <see cref="GroundNavigator.FinalNodeArrivalThresholdNm"/>, and on a curved connector the straight-line reading of such a
    /// stop can land a little past the bar. A nose within that slack is at the stop: the aircraft still reports holding short of
    /// 28L, not clear of 28R. Staged directly for the reason <see cref="ClearRunwayOnK_B772_HoldsInPositionShortOf28LNotClearOf28R"/>
    /// gives.
    /// </summary>
    [Fact]
    public void ClearRunwayOnK_B772_NoseWithinTheStopSlackOf28LsBar_StillHoldsShortOf28L()
    {
        const double NosePastBarFt = 1.0;
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment k = SegmentFor(layout, "K");
        GroundNode bar28R = layout.Nodes[k.ExitBarFor("28R")];
        GroundNode bar28L = layout.Nodes[k.ExitBarFor("28L")];
        double barToBarFt = GeoMath.DistanceNm(bar28R.Position, bar28L.Position) * GeoMath.FeetPerNm;
        double noseAtBarFt = barToBarFt - (AircraftLength.ResolveFt("B772") / 2.0);
        AircraftState aircraft = SpawnTailOver28ROnK(engine, layout, k, "B772", pastBarFt: noseAtBarFt + NosePastBarFt);
        var lines = new List<string>();
        Capture(engine, aircraft, lines);

        CommandResult result = engine.SendCommand("UAL1", "CLRWY");
        Assert.True(result.Success, $"CLRWY failed: {result.Message}");
        RunUntil(engine, aircraft, phase => phase is not ClearRunwayPhase, settleSeconds: 5);

        AssertHeldShortOf28LTailInside28R(aircraft);
        AssertReportsNotClear(lines);
        Assert.Equal("Holding short of 28L on K, not clear of 28R", AircraftStatusDescriber.Describe(aircraft, AircraftStatusContext.None).Text);
    }

    /// <summary>A <c>CLRWY</c>'s hold short of another runway survives a snapshot round trip.</summary>
    [Fact]
    public void HoldingInPositionShortOfAnotherRunway_SnapshotRoundTrip_KeepsBothRunways()
    {
        var hold = new HoldingInPositionPhase("28L", "28R", PilotResponder.SoloPositionsTower);

        HoldingInPositionPhaseDto dto = Assert.IsType<HoldingInPositionPhaseDto>(hold.ToSnapshot());
        var restored = HoldingInPositionPhase.FromSnapshot(dto);
        var plain = HoldingInPositionPhase.FromSnapshot(new HoldingInPositionPhaseDto { Status = 0, ElapsedSeconds = 0 });

        Assert.Equal("28L", restored.HeldShortOfRunway);
        Assert.Equal("28R", restored.TailInsideRunway);
        Assert.Null(plain.HeldShortOfRunway);
        Assert.Null(plain.TailInsideRunway);
    }

    /// <summary>
    /// A B738 told <c>CLRWY</c> at the same spot fits K (129.5 + 10 ft against 190.3 ft): it clears 28R in full and holds in
    /// position with no runway assigned and no other runway held short of, as before.
    /// </summary>
    [Fact]
    public void ClearRunwayOnK_B738_ClearsFullyAndHoldsInPosition()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        ExitCapacitySegment k = SegmentFor(layout, "K");
        AircraftState aircraft = SpawnTailOver28ROnK(engine, layout, k, "B738", pastBarFt: 0.0);

        CommandResult result = engine.SendCommand("UAL1", "CLRWY");
        Assert.True(result.Success, $"CLRWY failed: {result.Message}");
        RunUntil(engine, aircraft, phase => phase is not ClearRunwayPhase, settleSeconds: 5);

        HoldingInPositionPhase hold = Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Null(aircraft.Phases?.AssignedRunway);
        Assert.Null(hold.HeldShortOfRunway);
        Assert.Null(hold.TailInsideRunway);
    }

    /// <summary>
    /// A B772 told to exit 28R on K is an occupant of 28R while it is still moving along K, off the centreline: its stop leaves the
    /// tail inside 28R's holding position marking, so it never clears the runway (AIM 4-3-21.b; 7110.65 3-10-3.a.1).
    /// </summary>
    [Fact]
    public void ExplicitExitOnK_B772_MovingOffTheCentreline_IsAnOccupantOf28R()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        AircraftState arrival = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", "B772", "28R");
        SendExitK(engine);

        RunUntil(
            engine,
            arrival,
            phase => (phase is RunwayExitPhase { StopsTailInsideRunwayLine: true, IsOnCenterline: false }) && (arrival.GroundSpeed > 1.0),
            settleSeconds: 0
        );

        output.WriteLine($"B772 at {arrival.GroundSpeed:F1} kt on {arrival.Ground.CurrentTaxiway}");
        AssertOccupantOf28R(arrival);
    }

    /// <summary>
    /// A B772's <c>CLRWY</c> hold on K, nose short of 28L and tail inside 28R, is an occupant of 28R. Staged directly for the reason
    /// <see cref="ClearRunwayOnK_B772_HoldsInPositionShortOf28LNotClearOf28R"/> gives.
    /// </summary>
    [Fact]
    public void ClearRunwayOnK_B772_HoldIsAnOccupantOf28R()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        AircraftState aircraft = SpawnTailOver28ROnK(engine, layout, SegmentFor(layout, "K"), "B772", pastBarFt: 0.0);

        CommandResult result = engine.SendCommand("UAL1", "CLRWY");
        Assert.True(result.Success, $"CLRWY failed: {result.Message}");
        RunUntil(engine, aircraft, phase => phase is not ClearRunwayPhase, settleSeconds: 2);

        AssertHeldShortOf28LTailInside28R(aircraft);
        AssertOccupantOf28R(aircraft);
    }

    /// <summary>
    /// A B772's <c>CLRWY</c> hold on K, tail inside 28R, makes no taxi-in call though one is owed and SFO Ground answers: the
    /// aircraft is not clear of the runway (AIM 4-3-21.c). Staged directly for the reason
    /// <see cref="ClearRunwayOnK_B772_HoldsInPositionShortOf28LNotClearOf28R"/> gives.
    /// </summary>
    [Fact]
    public void ClearRunwayOnK_B772_HoldMakesNoTaxiInCall()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        Assert.NotNull(zoa);
        SimulationEngine engine = BuildEngine();
        Assert.NotNull(engine.Scenario);
        engine.Scenario.ArtccConfig = zoa;
        engine.Scenario.SoloTrainingMode = true;
        engine.Scenario.SetAiStaffedPositions([TestAiPositions.SfoGround(zoa)]);
        AirportGroundLayout layout = SfoLayout();
        AircraftState aircraft = SpawnTailOver28ROnK(engine, layout, SegmentFor(layout, "K"), "B772", pastBarFt: 0.0);
        aircraft.Ground.AwaitingTaxiInCall = true;
        aircraft.Ground.ReleasedToGround = true;

        CommandResult result = engine.SendCommand("UAL1", "CLRWY");
        Assert.True(result.Success, $"CLRWY failed: {result.Message}");
        RunUntil(engine, aircraft, phase => phase is not ClearRunwayPhase, settleSeconds: 15);

        AssertHeldShortOf28LTailInside28R(aircraft);
        Assert.False(
            aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Taxi },
            $"the B772 asked for taxi-in: {aircraft.PendingPilotRequest?.LastPilotLine}"
        );
        Assert.True(aircraft.Ground.AwaitingTaxiInCall, "the taxi-in call is still owed once the B772 is clear");
    }

    /// <summary>
    /// A landing clearance for 28R while a B772 holds on K after its exit, nose short of 28L and tail inside 28R, draws the
    /// stopped-traffic warning: the runway is occupied (7110.65 3-10-3.a).
    /// </summary>
    [Fact]
    public void LandingClearanceFor28R_WithAB772HeldTailInsideAtK_Warns()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        AircraftState occupant = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", "B772", "28R");
        SendExitK(engine);
        RunUntil(engine, occupant, phase => phase is HoldingAfterExitPhase, settleSeconds: 5);
        RunwayInfo? runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);
        var cleared = new AircraftState
        {
            Callsign = "UAL2",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };
        DispatchContext ctx = TestDispatch.Context(new Random(1), groundLayout: layout, listAircraft: () => [cleared, occupant]);

        RunwaySafetyAdvisor.WarnIfRunwayOccupied(cleared, runway28R, ctx);

        foreach (string warning in cleared.PendingWarnings)
        {
            output.WriteLine($"warning: {warning}");
        }

        Assert.Contains(cleared.PendingWarnings, warning => warning.StartsWith("traffic stopped on runway 28R (UAL1)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A B772 landing on 28R told to exit on K, still in its landing rollout with K committed, predicts no clear time: its stop on K
    /// leaves the tail inside 28R's marking, so it never clears, as on the exit itself.
    /// </summary>
    [Fact]
    public void LandingWithExitKCommitted_B772_PredictsNoClearTime()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        AircraftState arrival = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", "B772", "28R");
        SendExitK(engine);

        RunUntil(
            engine,
            arrival,
            phase =>
                arrival.IsOnGround
                && (phase is LandingPhase { CandidateExit: { } exit })
                && string.Equals(exit.TaxiwayName, "K", StringComparison.OrdinalIgnoreCase),
            settleSeconds: 0
        );

        Assert.Null(SameRunwayArrivalProtection.TryBuildRollout(arrival, 0.0));
    }

    /// <summary>
    /// A B738 rolling out on OAK 28R told to exit on a taxiway that bends back (turn-off angle over 100°) has that exit's
    /// hold-short behind its heading as it starts the exit; the predicted vacate is still the run along the exit to the bar plus
    /// half the fuselage, not the heading's reading of the bar as passed.
    /// </summary>
    [Fact]
    public void ReverseExit_HoldShortBehindTheHeading_PredictsTheRunToTheBarPlusTheTail()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        Assert.NotNull(layout);
        Assert.NotNull(runway);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon searchFrom = GeoMath.ProjectPoint(threshold, runway.TrueHeading, 1500 / GeoMath.FeetPerNm);
        AirportGroundLayout.CenterlineExitResult reverse = Assert.IsType<AirportGroundLayout.CenterlineExitResult>(
            FindReverseExit(layout, runway, searchFrom)
        );
        output.WriteLine($"reverse exit {reverse.Taxiway} at {reverse.ExitAngle:F1}°, HS #{reverse.HoldShort.Id}");
        double branchAlongNm = GeoMath.AlongTrackDistanceNm(reverse.Path[0].Position, threshold, runway.TrueHeading);
        AircraftState arrival = OnRunway(
            "OAK",
            "B738",
            runway,
            GeoMath.ProjectPoint(threshold, runway.TrueHeading, branchAlongNm - (800 / GeoMath.FeetPerNm)),
            15
        );
        Assert.NotNull(arrival.Phases);
        arrival.Phases.RequestedExit = new ExitPreference { Side = reverse.Side, Taxiway = reverse.Taxiway };
        StartRollout(engine, layout, arrival);

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            if (
                (arrival.Phases.CurrentPhase is RunwayExitPhase { TargetHoldShortNode: { } bar, HoldShortApproachHeading: { } edge })
                && (GeoMath.AlongTrackDistanceNm(arrival.Position, bar.Position, arrival.TrueHeading) > 0.0)
                && (-GeoMath.AlongTrackDistanceNm(arrival.Position, bar.Position, edge) is var toBarNm and > 0.0)
            )
            {
                if (SameRunwayArrivalProtection.TryBuildRollout(arrival, 0.0) is not { } rollout)
                {
                    Assert.Fail("the leader on its exit has no rollout");
                    return;
                }

                double remainderNm = rollout.BrakingLegNm + rollout.SteadyLegNm;
                double neededNm = toBarNm + SameRunwayArrivalProtection.TailClearanceNm(arrival.AircraftType);
                output.WriteLine(
                    $"t={t}s: remainder {remainderNm * GeoMath.FeetPerNm:F1} ft; bar {toBarNm * GeoMath.FeetPerNm:F1} ft along the exit"
                );
                Assert.True(
                    remainderNm >= neededNm - 1e-9,
                    $"remainder {remainderNm * GeoMath.FeetPerNm:F1} ft < {neededNm * GeoMath.FeetPerNm:F1} ft"
                );
                return;
            }
        }

        Assert.Fail($"the B738 never started exit {reverse.Taxiway} with its hold-short behind it; phase {arrival.Phases.CurrentPhase?.Name}");
    }

    /// <summary>
    /// A B772 rolling out on 28R short of K, told only to exit left, never chooses a crossover shorter than its 209.1 ft plus the
    /// 10 ft margin (K, D, L, P, R and their kin are about 190 ft); it takes a later exit that fits (the runway exit's own
    /// search, <c>RunwayExitPhase.RunSearchWithLookahead</c>).
    /// </summary>
    [Fact]
    public void AutomaticExit_B772_SkipsCrossoversTooShortForIt()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        (int exitBar, _) = RollOutLeft(engine, layout, "B772");

        AssertFitsCrossover(layout, exitBar, "B772");
    }

    /// <summary>
    /// A B772 landing on 28R with no exit instruction plans its exit in the landing (<c>LandingPhase.JudgeCandidate</c>) and
    /// never plans a crossover too short for it.
    /// </summary>
    [Fact]
    public void AutomaticLandingExit_B772_PlansNoCrossoverTooShortForIt()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        AircraftState arrival = SfoExitCapacityTests.AddArrival(engine, layout, "UAL1", "B772", "28R");

        int exitBar = ExitBarTaken(engine, arrival, 600);

        AssertFitsCrossover(layout, exitBar, "B772");
    }

    /// <summary>A B738 (129.5 + 10 ft) fits a 190 ft crossover, so its automatic exit still takes one a B772 skips.</summary>
    [Fact]
    public void AutomaticExit_B738_StillTakesAShortCrossover()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        (int exitBar, _) = RollOutLeft(engine, layout, "B738");

        ExitCapacitySegment? segment = SegmentByBar(layout, exitBar);
        output.WriteLine($"B738 left 28R via HS #{exitBar} ({segment?.Rule.Taxiway ?? "no crossover"}, {segment?.StripLengthFt:F1} ft)");
        Assert.NotNull(segment);
        Assert.True(
            segment.StripLengthFt < AircraftLength.ResolveFt("B772") + RunwayTailClearance.MarginFt,
            $"{segment.Rule.Taxiway} fits a B772 too"
        );
    }

    /// <summary>
    /// A B772 rolling out on 28R short of T (a 22° high-speed exit that fits it, 238.4 ft bar to bar) too fast to make T at its
    /// default-selection rate but slow enough at the firm rate, with Q, S1, S2 and C3 given up, takes T. Past T only crossovers
    /// too short for it are left (the 28R connections of D, K and R toward 28L, each met before its taxiway's other side, and
    /// E), and D is reachable at the default rate; an exit the aircraft fits at the firm rate comes before one it does not fit at
    /// any rate (owner ruling 5).
    /// </summary>
    [Fact]
    public void AutomaticLandingExit_B772_FitsOnlyAtFirmBraking_TakesTheFittingExitOverATooShortCrossover()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        RunwayInfo runway = Runway28R();
        AirportGroundLayout.CenterlineExitResult t = FirstConnection(layout, runway, "T", ExitSide.Left);
        AircraftCategory category = AircraftCategorization.Categorize("B772");
        double turnOffKts = CategoryPerformance.ExitTurnOffSpeed(category, t.ExitAngle);
        double leadNm = RolloutBraking.TurnEntryLeadNm(category, turnOffKts, t.ExitAngle);

        // A high-speed exit is chosen at the comfortable rate by default; placed where T needs a rate halfway to the firm one.
        double betweenRates = (CategoryPerformance.ComfortableExitDecelRate(category) + CategoryPerformance.FirmBrakingRate(category)) / 2.0;
        const double SpeedKts = 80.0;
        double shortOfBranchNm = leadNm + (5.0 / GeoMath.FeetPerNm);
        while (RolloutBraking.RequiredDecelKtsPerSec(SpeedKts, turnOffKts, shortOfBranchNm - leadNm, category) > betweenRates)
        {
            shortOfBranchNm += 5.0 / GeoMath.FeetPerNm;
        }

        double startAlongNm = AlongFromThresholdNm(runway, t.Path[0].Position) - shortOfBranchNm;
        AircraftState arrival = RollingOn28R(engine, layout, startAlongNm, SpeedKts);
        Assert.NotNull(arrival.Phases);
        arrival.Phases.GivenUpExitTaxiways.UnionWith(["Q", "S1", "S2", "C3"]);

        // The setup this case rests on: D, past T, is too short for the B772 yet reachable at the comfortable rate, so only the
        // order of the filtered firm-rate pass and the unfiltered retry keeps the choice off it.
        AirportGroundLayout.CenterlineExitResult d = FirstConnection(layout, runway, "D", ExitSide.Left);
        Assert.True(RunwayTailClearance.IsTooShortForAutomaticExit(d, arrival), "D fits the B772, so it proves nothing about the retry");
        double dTurnOffKts = CategoryPerformance.ExitTurnOffSpeed(category, d.ExitAngle);
        double toDTurnNm =
            AlongFromThresholdNm(runway, d.Path[0].Position) - startAlongNm - RolloutBraking.TurnEntryLeadNm(category, dTurnOffKts, d.ExitAngle);
        Assert.True(toDTurnNm > 0.0, $"D's turn starts {toDTurnNm * GeoMath.FeetPerNm:F0} ft ahead, not ahead of the B772");
        double dRate = RolloutBraking.RequiredDecelKtsPerSec(SpeedKts, dTurnOffKts, toDTurnNm, category);
        Assert.True(
            dRate <= CategoryPerformance.ComfortableExitDecelRate(category),
            $"D needs {dRate:F2} kt/s, above the comfortable {CategoryPerformance.ComfortableExitDecelRate(category):F2} kt/s"
        );

        engine.TickOneSecond();

        LandingPhase landing = Assert.IsType<LandingPhase>(arrival.Phases.CurrentPhase);
        output.WriteLine(
            $"{shortOfBranchNm * GeoMath.FeetPerNm:F0} ft short of T at {SpeedKts} kt (turn-off {turnOffKts:F0} kt): candidate "
                + $"{landing.CandidateExit?.TaxiwayName} {landing.CandidateExit?.Side} at {landing.CandidateExit?.SelectionDecelRate:F1} kt/s"
        );
        Assert.Equal("T", landing.CandidateExit?.TaxiwayName);
        Assert.Equal(CategoryPerformance.FirmBrakingRate(category), landing.CandidateExit?.SelectionDecelRate);
    }

    /// <summary>
    /// A B772 rolling out on 28R past N with every exit that fits it ahead given up has only the too-short crossover E ahead: it
    /// still plans E (owner ruling 5, the unfiltered retry) and will stop with its tail inside 28R's line, rather than planning
    /// no exit.
    /// </summary>
    [Fact]
    public void AutomaticLandingExit_B772_NoExitAheadFitsIt_StillPlansTheTooShortCrossover()
    {
        SimulationEngine engine = BuildEngine();
        AirportGroundLayout layout = SfoLayout();
        RunwayInfo runway = Runway28R();
        AirportGroundLayout.CenterlineExitResult e = FirstConnection(layout, runway, "E", ExitSide.Left);
        AircraftState arrival = RollingOn28R(engine, layout, AlongFromThresholdNm(runway, e.Path[0].Position) - (3000 / GeoMath.FeetPerNm), 60.0);
        Assert.NotNull(arrival.Phases);
        arrival.Phases.GivenUpExitTaxiways.UnionWith(FittingExitsPastE);

        engine.TickOneSecond();

        LandingPhase landing = Assert.IsType<LandingPhase>(arrival.Phases.CurrentPhase);
        output.WriteLine($"candidate {landing.CandidateExit?.TaxiwayName} {landing.CandidateExit?.Side}");
        Assert.Equal("E", landing.CandidateExit?.TaxiwayName);
        Assert.Equal(ExitSide.Left, landing.CandidateExit?.Side);
        Assert.True(landing.CandidateExit?.StopsTailInsideRunwayLine, "E is too short for a B772: it stops with its tail inside 28R's line");
    }

    /// <summary>
    /// The taxiways off 28R past E with a connection a B772 fits (T, Q, S1, S2 on the left; D, K, R, C3 on the right), given up so
    /// that no exit ahead of E fits it.
    /// </summary>
    private static readonly string[] FittingExitsPastE = ["T", "D", "K", "Q", "R", "C3", "S1", "S2"];

    private static RunwayInfo Runway28R()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);
        return runway;
    }

    private static double AlongFromThresholdNm(RunwayInfo runway, LatLon point) =>
        GeoMath.AlongTrackDistanceNm(point, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading);

    /// <summary>The first connection of <paramref name="taxiway"/> on <paramref name="side"/> of 28R from its threshold.</summary>
    private static AirportGroundLayout.CenterlineExitResult FirstConnection(
        AirportGroundLayout layout,
        RunwayInfo runway,
        string taxiway,
        ExitSide side
    )
    {
        AirportGroundLayout.CenterlineExitResult? found = layout.FindOnSidePreferredExit(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading,
            runway.Designator,
            new ExitPreference { Taxiway = taxiway, Side = side },
            side,
            filter: candidate => (candidate.Side == side) ? AirportGroundLayout.CandidateVerdict.Accept : AirportGroundLayout.CandidateVerdict.Skip
        );
        Assert.NotNull(found);
        return found.Value;
    }

    /// <summary>
    /// Puts a B772 on 28R's centreline <paramref name="alongNm"/> from the threshold, rolling out of its landing at
    /// <paramref name="speedKts"/> with no exit instruction, so the landing's own exit choice runs on its next tick.
    /// </summary>
    private static AircraftState RollingOn28R(SimulationEngine engine, AirportGroundLayout layout, double alongNm, double speedKts)
    {
        RunwayInfo runway = Runway28R();
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        AircraftState arrival = OnRunway("SFO", "B772", runway, GeoMath.ProjectPoint(threshold, runway.TrueHeading, alongNm), speedKts);
        Assert.NotNull(arrival.Phases);
        var landing = new LandingPhase();
        arrival.Phases.Add(landing);
        arrival.Phases.Add(new RunwayExitPhase());
        arrival.Phases.Add(new HoldingAfterExitPhase());
        arrival.Ground.Layout = layout;
        arrival.Phases.Start(CommandDispatcher.BuildMinimalContext(arrival, layout));
        landing.CurrentState = LandingPhase.State.Rollout;
        engine.World.AddAircraft(arrival);
        return arrival;
    }

    private void AssertFitsCrossover(AirportGroundLayout layout, int exitBar, string type)
    {
        ExitCapacitySegment? segment = SegmentByBar(layout, exitBar);
        double neededFt = AircraftLength.ResolveFt(type) + RunwayTailClearance.MarginFt;
        output.WriteLine(
            $"{type} left 28R via HS #{exitBar} ({segment?.Rule.Taxiway ?? "no crossover"}, {segment?.StripLengthFt:F1} ft); needs {neededFt:F1} ft"
        );
        Assert.True(
            (segment is null) || (segment.StripLengthFt >= neededFt),
            $"the {type} chose {segment?.Rule.Taxiway}, {segment?.StripLengthFt:F1} ft bar to bar"
        );
    }

    private void AssertNoseShortOf28L(AirportGroundLayout layout, ExitCapacitySegment k, AircraftState aircraft)
    {
        RunwayInfo? runway28L = NavigationDatabase.Instance.GetRunway("SFO", "28L");
        Assert.NotNull(runway28L);
        GroundNode bar28L = layout.Nodes[k.ExitBarFor("28L")];
        double halfFt = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0;
        LatLon nose = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, halfFt / GeoMath.FeetPerNm);
        double depthFt = RunwayHoldBars.DepthPastMarkingFt(layout, runway28L, bar28L, nose, aircraft.Position);
        output.WriteLine(
            $"{aircraft.AircraftType}: nose {depthFt:F1} ft past 28L's marking at HS #{bar28L.Id}; phase {aircraft.Phases?.CurrentPhase?.Name}"
        );
        Assert.True(depthFt <= 0.0, $"the {aircraft.AircraftType}'s nose is {depthFt:F1} ft past 28L's holding position marking");
    }

    private static void AssertHeldShortOf28LTailInside28R(AircraftState aircraft)
    {
        HoldingInPositionPhase hold = Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal("28L", hold.HeldShortOfRunway);
        Assert.Equal("28R", hold.TailInsideRunway);
    }

    private void AssertReportsNotClear(List<string> lines)
    {
        foreach (string line in lines)
        {
            output.WriteLine($"pilot: {line}");
        }

        Assert.DoesNotContain(lines, line => line.Contains("clear of runway 28R at", StringComparison.OrdinalIgnoreCase));
        Assert.Single(lines, line => line.Contains(NotClearReport, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertOccupantOf28R(AircraftState aircraft)
    {
        RunwayInfo? runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);
        Assert.Equal(RunwayUseKind.OnSurface, RunwayOccupancy.ClassifyByPhase(aircraft, runway28R));
    }

    private static void SendExitK(SimulationEngine engine)
    {
        CommandResult exit = engine.SendCommand("UAL1", "EL K");
        Assert.True(exit.Success, $"EL K failed: {exit.Message}");
    }

    /// <summary>
    /// Spawns UAL1, landed on 28R, holding short of 28L on K with its centre <paramref name="pastBarFt"/> past 28R's bar, facing
    /// 28L, its tail over 28R (the state <c>CLRWY</c> applies to), its route the K leg from the 28R side through 28R's bar to 28L's.
    /// </summary>
    private static AircraftState SpawnTailOver28ROnK(
        SimulationEngine engine,
        AirportGroundLayout layout,
        ExitCapacitySegment k,
        string type,
        double pastBarFt
    )
    {
        GroundNode bar28R = layout.Nodes[k.ExitBarFor("28R")];
        GroundNode bar28L = layout.Nodes[k.ExitBarFor("28L")];
        IGroundEdge throughEdge = Assert.Single(bar28R.Edges, e => e.OtherNode(bar28R).Id == bar28L.Id);
        IGroundEdge approachEdge = bar28R
            .Edges.Where(e => !e.IsRunwayCenterline && e.MatchesTaxiway("K") && (e.OtherNode(bar28R).Id != bar28L.Id))
            .OrderByDescending(e => GeoMath.DistanceNm(e.OtherNode(bar28R).Position, bar28L.Position))
            .First();
        GroundNode approach = approachEdge.OtherNode(bar28R);

        var holdShort = new HoldShortPoint
        {
            NodeId = bar28L.Id,
            Reason = HoldShortReason.DestinationRunway,
            TargetName = "28L",
            TailOverRunwayNodeId = bar28R.Id,
        };
        var heading = new TrueHeading(GeoMath.BearingTo(bar28R.Position, bar28L.Position));
        LatLon spot = GeoMath.ProjectPoint(bar28R.Position, heading, pastBarFt / GeoMath.FeetPerNm);
        GroundNode placement = pastBarFt > 0.0 ? VirtualNode.Create(spot.Lat, spot.Lon) : bar28R;
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            "UAL1",
            type,
            (placement, heading),
            new HoldingShortPhase(holdShort)
        );
        Assert.NotNull(aircraft.Phases);
        aircraft.Phases.AssignedRunway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        aircraft.Ground.CurrentTaxiway = "K";
        aircraft.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments =
            [
                new TaxiRouteSegment { TaxiwayName = "K", Edge = approachEdge.Directed(approach, bar28R) },
                new TaxiRouteSegment { TaxiwayName = "K", Edge = throughEdge.Directed(bar28R, bar28L) },
            ],
            HoldShortPoints = [holdShort],
            CurrentSegmentIndex = 1,
        };
        return aircraft;
    }

    /// <summary>
    /// Puts <paramref name="type"/> on 28R's centreline 800 ft short of abeam K's 28R bar, rolling at 20 kt at the hand-off from
    /// its landing to its runway exit and told only to exit left, so K is the first exit it can take and the taxiway is its own
    /// choice; returns the bar it leaves the runway by, and the aircraft.
    /// </summary>
    private static (int ExitBar, AircraftState Arrival) RollOutLeft(SimulationEngine engine, AirportGroundLayout layout, string type)
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        GroundNode bar = layout.Nodes[SegmentFor(layout, "K").ExitBarFor("28R")];
        (LatLon _, double barAlongNm, bool _) = GeoMath.FootOfPerpendicular(
            bar.Position,
            threshold,
            GeoMath.ProjectPoint(threshold, runway.TrueHeading, 3.0)
        );

        AircraftState arrival = OnRunway(
            "SFO",
            type,
            runway,
            GeoMath.ProjectPoint(threshold, runway.TrueHeading, barAlongNm - (800 / GeoMath.FeetPerNm)),
            20
        );
        Assert.NotNull(arrival.Phases);
        arrival.Phases.RequestedExit = new ExitPreference { Side = ExitSide.Left };
        StartRollout(engine, layout, arrival);

        return (ExitBarTaken(engine, arrival, 400), arrival);
    }

    /// <summary>Starts <paramref name="arrival"/> at the hand-off from its landing to its runway exit and adds it to the engine.</summary>
    private static void StartRollout(SimulationEngine engine, AirportGroundLayout layout, AircraftState arrival)
    {
        Assert.NotNull(arrival.Phases);
        var landing = new LandingPhase();
        arrival.Phases.Add(landing);
        arrival.Phases.Add(new RunwayExitPhase());
        arrival.Phases.Add(new HoldingAfterExitPhase());
        arrival.Ground.Layout = layout;
        arrival.Phases.Start(CommandDispatcher.BuildMinimalContext(arrival, layout));
        landing.CurrentState = LandingPhase.State.Handoff;
        engine.World.AddAircraft(arrival);
    }

    private static AircraftState OnRunway(string airport, string type, RunwayInfo runway, LatLon position, double speedKts)
    {
        var aircraft = new AircraftState
        {
            Callsign = "UAL1",
            AircraftType = type,
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = speedKts,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = airport,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Targets.TargetSpeed = speedKts;
        return aircraft;
    }

    /// <summary>Ticks until <paramref name="arrival"/> has left the runway by an exit bar and returns that bar.</summary>
    private static int ExitBarTaken(SimulationEngine engine, AircraftState arrival, int maxSeconds)
    {
        int? lastTarget = null;
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            Phase? phase = arrival.Phases?.CurrentPhase;
            if (phase is RunwayExitPhase { TargetHoldShortNodeId: { } target })
            {
                lastTarget = target;
            }

            if ((phase is HoldingAfterExitPhase or TaxiingPhase or HoldingShortPhase) && (lastTarget is { } exitBar))
            {
                return exitBar;
            }
        }

        Assert.Fail($"the {arrival.AircraftType} did not leave 28R via an exit bar in {maxSeconds} s; phase {arrival.Phases?.CurrentPhase?.Name}");
        return 0;
    }

    private static void Capture(SimulationEngine engine, AircraftState aircraft, List<string> lines)
    {
        engine.WarningEmitted += (callsign, line) =>
        {
            if (callsign == aircraft.Callsign)
            {
                lines.Add(line);
            }
        };
        engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Callsign == aircraft.Callsign)
            {
                lines.Add(entry.Message);
            }
        };
    }

    /// <summary>
    /// Ticks until <paramref name="done"/> holds for the current phase, then <paramref name="settleSeconds"/> more for the calls to
    /// be sent.
    /// </summary>
    private static void RunUntil(SimulationEngine engine, AircraftState aircraft, Func<Phase?, bool> done, int settleSeconds)
    {
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            if (done(aircraft.Phases?.CurrentPhase))
            {
                for (int s = 0; s < settleSeconds; s++)
                {
                    engine.TickOneSecond();
                }

                return;
            }
        }

        Assert.Fail($"{aircraft.Callsign} never got there in 400 s; phase {aircraft.Phases?.CurrentPhase?.Name}");
    }

    private SimulationEngine BuildEngine()
    {
        SimulationEngine engine = SfoExitCapacityTests.CreateEngine(output);
        Assert.NotNull(engine.Scenario);
        engine.Scenario.AutoPullUpToParallel = false;
        return engine;
    }

    /// <summary>
    /// The first connection ahead of <paramref name="from"/> on <paramref name="runway"/>, of any taxiway the rollout passes and on
    /// either side, whose turn-off angle is over 100° (it bends back); null when there is none.
    /// </summary>
    private AirportGroundLayout.CenterlineExitResult? FindReverseExit(AirportGroundLayout layout, RunwayInfo runway, LatLon from)
    {
        var taxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        layout.FindOnSidePreferredExit(
            from.Lat,
            from.Lon,
            runway.TrueHeading,
            runway.Designator,
            preference: null,
            sidePref: null,
            filter: candidate =>
            {
                taxiways.Add(candidate.Taxiway);
                return AirportGroundLayout.CandidateVerdict.Skip;
            },
            maxIterations: 200
        );

        foreach (string taxiway in taxiways)
        {
            foreach (ExitSide side in (ExitSide[])[ExitSide.Left, ExitSide.Right])
            {
                AirportGroundLayout.CenterlineExitResult? first = layout.FindOnSidePreferredExit(
                    from.Lat,
                    from.Lon,
                    runway.TrueHeading,
                    runway.Designator,
                    new ExitPreference { Side = side, Taxiway = taxiway },
                    side,
                    filter: candidate =>
                        (candidate.Side == side) && (GeoMath.AlongTrackDistanceNm(candidate.Path[0].Position, from, runway.TrueHeading) > 0)
                            ? AirportGroundLayout.CandidateVerdict.Accept
                            : AirportGroundLayout.CandidateVerdict.Skip
                );
                output.WriteLine($"first {taxiway} {side}: {first?.ExitAngle:F1}°");
                if (first is { ExitAngle: > 100 } reverse)
                {
                    return reverse;
                }
            }
        }

        return null;
    }

    private static AirportGroundLayout SfoLayout()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        return layout;
    }

    private static ExitCapacitySegment SegmentFor(AirportGroundLayout layout, string taxiway) =>
        Assert.Single(ExitCapacityResolver.GetSegments(layout), s => (s.Rule.Taxiway == taxiway) && s.AppliesToRunway("28R"));

    private static ExitCapacitySegment? SegmentByBar(AirportGroundLayout layout, int bar) =>
        ExitCapacityResolver.GetSegments(layout).FirstOrDefault(s => (s.ExitBarNodeId == bar) || (s.ParallelBarNodeId == bar));
}
