using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #475a: a push off OAK gate 27 was held forever by the B738 on gate 29, which stands parallel to 27 and further
/// back along the push line. A straight push passes that neighbour's wing abeam at the row's own wingtip gap and never
/// closer, so the tug may tow past it; an aircraft parked on the push line itself still stops the push short of it.
/// </summary>
public class Issue475StaggeredStandPushTests(ITestOutputHelper output)
{
    private const string Airport = "OAK";
    private const string Pusher = "SWA1905";
    private const string Neighbour = "SWA5456";
    private const string Narrowbody = "B738";
    private const string PushingStand = "27";
    private const string StaggeredStand = "29";

    /// <summary>How long a push may take, seconds; with gate 29 empty the push completes in about 51 s.</summary>
    private const int BudgetSeconds = 120;

    /// <summary>How long the tow to spot E may take, seconds: several hundred feet of pushing and pulling at 5 kt.</summary>
    private const int SpotBudgetSeconds = 300;

    /// <summary>The longest run of seconds a push that should pass may sit at a zero speed limit; a judgement call.</summary>
    private const int MaxHoldSeconds = 5;

    /// <summary>
    /// How far under the row's wingtip gap the flown outlines may come, feet: the floor's 0.5 ft slack, plus the stop
    /// curve's and the outline sampling's own play. Shared with the SFO F5/F6 row tests.
    /// </summary>
    internal const double RowGapToleranceFt = 1.5;

    /// <summary>How long a command that ends or redirects the tow is retried, a second at a time, seconds.</summary>
    private const int CommandRetrySeconds = 60;

    /// <summary>How far into the push its stand push-off is still running, seconds: the moment a facing amendment is sent.</summary>
    private const int AmendSecond = 2;

    /// <summary>How far into the push the tow is under way past its push-off, seconds: when it is ended or redirected.</summary>
    private const int MidTowSecond = 15;

    /// <summary>How long any tow may take to end before its test fails it, seconds: twice the spot budget.</summary>
    private const int TowEndBudgetSeconds = 600;

    /// <summary>OAK's RON6 and RON5: a parallel pair whose wings pass abeam further back than the two B738s' reaches together.</summary>
    private const string DeepRowStand = "RON6";

    private const string DeepStaggeredStand = "RON5";

    /// <summary>The least outline clearance a push stopped for an aircraft on its push line may leave it, feet.</summary>
    private const double MinStopGapFt = 10.0;

    /// <summary>How far behind gate 27 along its push line the push-line neighbour is parked, feet.</summary>
    private const double PushLineNeighbourAftFt = 230.0;

    private (SimulationEngine Engine, AirportGroundLayout Layout)? Build()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout(Airport) is not { } layout))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-issue-475-staggered-push",
                ScenarioName = "Push past a staggered neighbour stand",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = Airport,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        return (engine, layout);
    }

    private static AircraftState Spawn(SimulationEngine engine, AirportGroundLayout layout, string callsign, PushbackPose pose)
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
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static PushbackPose StandPose(AirportGroundLayout layout, string standName)
    {
        GroundNode stand =
            layout.FindParkingByName(standName) ?? throw new InvalidOperationException($"{layout.AirportId} has no stand '{standName}'");
        return new PushbackPose(stand.Position, Assert.NotNull(stand.TrueHeading).Degrees);
    }

    /// <summary>
    /// The wingtip gap of the 27/29 row, feet — gate 29's offset from gate 27's axis less a B738's wingspan — after
    /// asserting the layout still has the shape the issue is about: 29 parallel to 27 and further back along the push
    /// line, the row gap between 10 ft and the wingtip buffer, and the pair starting further apart than the buffer.
    /// </summary>
    private double AssertStaggeredRow(AirportGroundLayout layout, AircraftState pusher, AircraftState neighbour)
    {
        PushbackPose pushing = StandPose(layout, PushingStand);
        PushbackPose staggered = StandPose(layout, StaggeredStand);
        var nose = new TrueHeading(pushing.NoseTrueDeg);
        double lateralFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(staggered.Position, pushing.Position, nose) * GeoMath.FeetPerNm);
        double behindFt = -GeoMath.AlongTrackDistanceNm(staggered.Position, pushing.Position, nose) * GeoMath.FeetPerNm;
        double rowGapFt = lateralFt - GroundOutlineSize.Of(AircraftFootprint.FromType(Narrowbody), towedNoseFirst: false).WingspanFt;
        double startFt = GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, neighbour);
        double noseDiffDeg = Math.Abs(nose.SignedAngleTo(new TrueHeading(staggered.NoseTrueDeg)));
        output.WriteLine(
            $"gate {StaggeredStand} sits {lateralFt:F1} ft beside gate {PushingStand}'s axis and {behindFt:F1} ft behind it, noses {noseDiffDeg:F1}° "
                + $"apart; row gap {rowGapFt:F1} ft, outlines start {startFt:F1} ft apart"
        );
        Assert.True(noseDiffDeg <= 10.0, $"gates {PushingStand} and {StaggeredStand} are no longer parallel: noses {noseDiffDeg:F1}° apart");
        Assert.True(behindFt > 0.0, $"gate {StaggeredStand} no longer sits behind gate {PushingStand} along the push line ({behindFt:F1} ft)");
        Assert.InRange(rowGapFt, 10.0, GroundOutlineSweep.WingtipBufferFt);
        Assert.True(startFt > GroundOutlineSweep.WingtipBufferFt, $"the pair starts {startFt:F1} ft apart, inside the wingtip buffer");
        return rowGapFt;
    }

    /// <summary>What a push next to a neighbour did.</summary>
    private sealed record PushRun(int CompletedSecond, int LongestHoldSeconds, double ClosestFt);

    /// <summary>
    /// Ticks until the pusher has no tug move left — it holds after the push or rests on the spot — or
    /// <paramref name="budgetSeconds"/> pass, tracking the longest run of seconds at a zero speed limit and the closest
    /// the two outlines came.
    /// </summary>
    private static PushRun TickTow(SimulationEngine engine, AircraftState pusher, AircraftState neighbour, int budgetSeconds)
    {
        int holdSeconds = 0;
        int longestHold = 0;
        double closestFt = GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, neighbour);
        for (int t = 1; t <= budgetSeconds; t++)
        {
            engine.TickOneSecond();
            closestFt = Math.Min(closestFt, GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, neighbour));
            if (pusher.Phases?.CurrentPhase is not (PushbackPhase or TugReleasePhase))
            {
                return new PushRun(t, longestHold, closestFt);
            }

            bool held = pusher.Ground.SpeedLimit is <= 0.0;
            holdSeconds = held ? holdSeconds + 1 : 0;
            longestHold = Math.Max(longestHold, holdSeconds);
        }

        return new PushRun(-1, longestHold, closestFt);
    }

    private void Report(string scenario, AircraftState pusher, AircraftState neighbour, PushRun run)
    {
        output.WriteLine(
            $"{scenario}: completed at t={run.CompletedSecond}s, longest hold {run.LongestHoldSeconds}s, closest outline {run.ClosestFt:F1} ft, "
                + $"phase={pusher.Phases?.CurrentPhase?.Name ?? "none"}, limit={pusher.Ground.SpeedLimit?.ToString("F1") ?? "none"}, "
                + $"gs={pusher.GroundSpeed:F2}kt, yield={pusher.Ground.AutoYieldTarget ?? "-"}, "
                + $"now {GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, neighbour):F1} ft apart"
        );
    }

    [Fact]
    public void PushTe_FromGate27_PastB738OnStaggeredGate29_CompletesWithoutBeingHeld()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        AircraftState neighbour = Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        double rowGapFt = AssertStaggeredRow(layout, pusher, neighbour);

        CommandResult result = engine.SendCommand(Pusher, "PUSH TE");
        Assert.True(result.Success, $"PUSH TE off gate {PushingStand} was refused: {result.Message}");

        PushRun run = TickTow(engine, pusher, neighbour, BudgetSeconds);
        Report("PUSH TE past the staggered gate", pusher, neighbour, run);

        Assert.True(
            run.CompletedSecond > 0,
            $"{Pusher} never completed PUSH TE within {BudgetSeconds}s: held by {pusher.Ground.AutoYieldTarget ?? "nobody"} at "
                + $"{GroundOutline.ClearanceBetween(pusher, aTowedNoseFirst: false, neighbour):F1} ft"
        );
        Assert.True(run.LongestHoldSeconds <= MaxHoldSeconds, $"the push sat at a zero speed limit for {run.LongestHoldSeconds}s");
        Assert.True(
            run.ClosestFt >= rowGapFt - RowGapToleranceFt,
            $"the push came {run.ClosestFt:F1} ft from {Neighbour}, under the row's {rowGapFt:F1} ft wingtip gap"
        );
    }

    [Fact]
    public void PushTe_FromGate27_IntoParallelAircraftOnThePushLine_StopsShortOfIt()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        PushbackPose stand = StandPose(layout, PushingStand);
        LatLon onPushLine = GeoMath.ProjectPoint(
            stand.Position,
            new TrueHeading(stand.NoseTrueDeg + 180.0),
            PushLineNeighbourAftFt / GeoMath.FeetPerNm
        );
        AircraftState pusher = Spawn(engine, layout, Pusher, stand);
        AircraftState neighbour = Spawn(engine, layout, Neighbour, new PushbackPose(onPushLine, stand.NoseTrueDeg));

        CommandResult result = engine.SendCommand(Pusher, "PUSH TE");
        Assert.True(result.Success, $"PUSH TE off gate {PushingStand} was refused: {result.Message}");

        PushRun run = TickTow(engine, pusher, neighbour, BudgetSeconds);
        Report("PUSH TE into an aircraft on the push line", pusher, neighbour, run);

        Assert.True(run.CompletedSecond < 0, $"{Pusher} completed PUSH TE at t={run.CompletedSecond}s through {Neighbour} on its push line");
        Assert.Equal(Neighbour, pusher.Ground.AutoYieldTarget);
        Assert.True(run.ClosestFt >= MinStopGapFt, $"the push came {run.ClosestFt:F1} ft from {Neighbour} before it stopped");
    }

    /// <summary>How long the interrupted push runs before its snapshot, seconds: under way, short of the row's wingtips.</summary>
    private const int SnapshotSecond = 8;

    /// <summary>
    /// The row anchor is measured where the tow began and carried through the tow, so it must survive a snapshot round
    /// trip: a push snapshotted mid-move and restored from the snapshot's JSON finishes exactly as the uninterrupted one
    /// does. Restored without it, the floor falls back to the start clearance and the push is held at gate 29 again.
    /// </summary>
    [Fact]
    public void PushTe_PastStaggeredGate29_RestoredFromAMidPushSnapshot_CompletesAsTheUninterruptedPush()
    {
        if ((Build() is not var (straightEngine, layout)) || (Build() is not var (interruptedEngine, _)))
        {
            return;
        }

        AircraftState straightPusher = Spawn(straightEngine, layout, Pusher, StandPose(layout, PushingStand));
        AircraftState straightNeighbour = Spawn(straightEngine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(straightEngine.SendCommand(Pusher, "PUSH TE").Success);
        PushRun straight = TickTow(straightEngine, straightPusher, straightNeighbour, BudgetSeconds);
        Report("uninterrupted", straightPusher, straightNeighbour, straight);
        Assert.True(
            straight.CompletedSecond > 0,
            $"the uninterrupted push never completed: held by {straightPusher.Ground.AutoYieldTarget ?? "nobody"}"
        );

        Spawn(interruptedEngine, layout, Pusher, StandPose(layout, PushingStand));
        Spawn(interruptedEngine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(interruptedEngine.SendCommand(Pusher, "PUSH TE").Success);
        for (int t = 0; t < SnapshotSecond; t++)
        {
            interruptedEngine.TickOneSecond();
        }

        AircraftState? beforeSnapshot = interruptedEngine.FindAircraft(Pusher);
        Assert.NotNull(beforeSnapshot);
        Assert.IsType<PushbackPhase>(beforeSnapshot.Phases?.CurrentPhase);
        TugRowAnchor anchor = Assert.NotNull(beforeSnapshot.Ground.TowRowAnchor);
        string json = JsonSerializer.Serialize(interruptedEngine.CaptureSnapshot(), RecordingJsonOptions.Default);
        StateSnapshotDto? reread = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default);
        Assert.NotNull(reread);
        interruptedEngine.RestoreFromSnapshot(reread);

        AircraftState? restoredPusher = interruptedEngine.FindAircraft(Pusher);
        AircraftState? restoredNeighbour = interruptedEngine.FindAircraft(Neighbour);
        Assert.NotNull(restoredPusher);
        Assert.NotNull(restoredNeighbour);
        Assert.NotSame(beforeSnapshot, restoredPusher);
        Assert.Equal(anchor, restoredPusher.Ground.TowRowAnchor);
        PushRun restored = TickTow(interruptedEngine, restoredPusher, restoredNeighbour, BudgetSeconds);
        Report("restored", restoredPusher, restoredNeighbour, restored);

        Assert.True(restored.CompletedSecond > 0, $"the restored push never completed: held by {restoredPusher.Ground.AutoYieldTarget ?? "nobody"}");
        Assert.Equal(straight.CompletedSecond, SnapshotSecond + restored.CompletedSecond);
        double endsApartFt = GeoMath.DistanceNm(straightPusher.Position, restoredPusher.Position) * GeoMath.FeetPerNm;
        Assert.True(endsApartFt <= 0.1, $"the restored push ended {endsApartFt:F2} ft from where the uninterrupted one did");
        Assert.Null(restoredPusher.Ground.TowRowAnchor);
    }

    /// <summary>
    /// The planner judges a turning push against the same row-anchored floor the detector holds the tow to, so a push
    /// to spot E off gate 27 — which swings past the B738 on gate 29 no closer than the row's wingtip gap — is no
    /// longer refused, and the tow it plans is flown through without being held.
    /// </summary>
    [Fact]
    public void PushToSpotE_FromGate27_WithB738OnStaggeredGate29_IsPlannedAndFlown()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        AircraftState neighbour = Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        double rowGapFt = AssertStaggeredRow(layout, pusher, neighbour);

        CommandResult result = engine.SendCommand(Pusher, "PUSH $E");
        output.WriteLine($"PUSH $E -> success={result.Success}, message={result.Message}");
        Assert.True(result.Success, $"PUSH $E off gate {PushingStand} with {Neighbour} on gate {StaggeredStand} was refused: {result.Message}");

        PushRun run = TickTow(engine, pusher, neighbour, SpotBudgetSeconds);
        Report("PUSH $E past the staggered gate", pusher, neighbour, run);
        Assert.True(
            run.CompletedSecond > 0,
            $"{Pusher} never finished PUSH $E within {SpotBudgetSeconds}s: held by {pusher.Ground.AutoYieldTarget ?? "nobody"}"
        );
        Assert.True(run.LongestHoldSeconds <= MaxHoldSeconds, $"the push sat at a zero speed limit for {run.LongestHoldSeconds}s");
        Assert.True(
            run.ClosestFt >= rowGapFt - RowGapToleranceFt,
            $"the push came {run.ClosestFt:F1} ft from {Neighbour}, under the row's {rowGapFt:F1} ft wingtip gap"
        );
    }

    /// <summary>
    /// The detector's floor against gate 29 is read off a row clearance measured once for the tow and reused every
    /// physics sub-tick; it must be the very floor a cold measurement gives, or a restored tow (which measures it
    /// afresh) would be held to a different floor than the uninterrupted one.
    /// </summary>
    [Fact]
    public void PushTe_PastStaggeredGate29_DetectorSweepIsTheSameWithTheRowClearanceColdAndWarm()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        AircraftState neighbour = Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(engine.SendCommand(Pusher, "PUSH TE").Success);
        for (int t = 0; t < SnapshotSecond; t++)
        {
            engine.TickOneSecond();
        }

        PushbackPhase tugMove = Assert.IsType<PushbackPhase>(pusher.Phases?.CurrentPhase);
        TugRowAnchor anchor = Assert.NotNull(pusher.Ground.TowRowAnchor);
        GroundOutlineSweepResult afterTicks = GroundConflictDetector.TugMoveSweep(pusher, tugMove, neighbour);
        pusher.Ground.TowRowClearances.Clear();
        GroundOutlineSweepResult cold = GroundConflictDetector.TugMoveSweep(pusher, tugMove, neighbour);
        GroundOutlineSweepResult warm = GroundConflictDetector.TugMoveSweep(pusher, tugMove, neighbour);
        output.WriteLine(
            $"cold: row {cold.RowClearanceFt:F3} ft, floor {cold.FloorFt:F3} ft; warm: row {warm.RowClearanceFt:F3} ft, floor {warm.FloorFt:F3} ft"
        );

        double? pureRowFt = GroundOutlineSweep.RowClearanceFt(
            anchor,
            AircraftFootprint.FromType(Narrowbody),
            StandPose(layout, StaggeredStand),
            AircraftFootprint.FromType(Narrowbody)
        );
        Assert.NotNull(cold.RowClearanceFt);
        Assert.Equal(pureRowFt, cold.RowClearanceFt);
        Assert.Equal(cold, warm);
        Assert.Equal(cold, afterTicks);
    }

    /// <summary>
    /// The row clearance slides the mover's outline past the neighbour's abeam point, however far back it stands: OAK's
    /// RON5 stands parallel to RON6 further back than two B738s' reaches together, and the row clearance is the row's
    /// wingtip gap, not the larger clearance the slide would read if it stopped at the reaches.
    /// </summary>
    [Fact]
    public void RowClearance_PastANeighbourStaggeredBeyondTheReaches_IsMeasuredWhereTheWingsPass()
    {
        if (Build() is not var (_, layout))
        {
            return;
        }

        PushbackPose mover = StandPose(layout, DeepRowStand);
        PushbackPose staggered = StandPose(layout, DeepStaggeredStand);
        var nose = new TrueHeading(mover.NoseTrueDeg);
        double lateralFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(staggered.Position, mover.Position, nose) * GeoMath.FeetPerNm);
        double behindFt = -GeoMath.AlongTrackDistanceNm(staggered.Position, mover.Position, nose) * GeoMath.FeetPerNm;
        double reachesFt = 2.0 * GroundOutlineSize.Of(AircraftFootprint.FromType(Narrowbody), towedNoseFirst: false).ReachFt;
        double rowGapFt = lateralFt - GroundOutlineSize.Of(AircraftFootprint.FromType(Narrowbody), towedNoseFirst: false).WingspanFt;
        double noseDiffDeg = Math.Abs(nose.SignedAngleTo(new TrueHeading(staggered.NoseTrueDeg)));

        double? rowFt = GroundOutlineSweep.RowClearanceFt(
            new TugRowAnchor(mover, PushbackLegKind.Push),
            AircraftFootprint.FromType(Narrowbody),
            staggered,
            AircraftFootprint.FromType(Narrowbody)
        );
        output.WriteLine(
            $"{DeepStaggeredStand} sits {lateralFt:F1} ft beside {DeepRowStand}'s axis and {behindFt:F1} ft behind it (reaches {reachesFt:F1} ft), "
                + $"noses {noseDiffDeg:F1}° apart; row gap {rowGapFt:F1} ft, row clearance {rowFt?.ToString("F1") ?? "none"} ft"
        );
        Assert.True(noseDiffDeg <= GroundOutlineSweep.RowAnchorNoseToleranceDeg, $"the stands are no longer parallel: {noseDiffDeg:F1}°");
        Assert.True(behindFt > reachesFt, $"{DeepStaggeredStand} no longer stands further back than the reaches: {behindFt:F1} ft");
        Assert.InRange(rowGapFt, GroundOutlineSweep.RowAnchorMinFt, GroundOutlineSweep.WingtipBufferFt);
        Assert.NotNull(rowFt);
        Assert.InRange(rowFt.Value, rowGapFt - RowGapToleranceFt, rowGapFt + RowGapToleranceFt);
    }

    /// <summary>
    /// A facing amendment mid-push to spot E re-plans the moves behind the running push-off and carries the tow's row anchor: the
    /// push still completes past gate 29, no closer than the row's wingtip gap allows.
    /// </summary>
    [Fact]
    public void PushToSpotE_AmendedFacingMidPush_CarriesTheRowAnchorPastStaggeredGate29()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        AircraftState neighbour = Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        double rowGapFt = AssertStaggeredRow(layout, pusher, neighbour);
        Assert.True(engine.SendCommand(Pusher, "PUSH $E").Success);
        TugRowAnchor anchor = Assert.NotNull(pusher.Ground.TowRowAnchor);
        for (int t = 0; t < AmendSecond; t++)
        {
            engine.TickOneSecond();
        }

        string? amendment = null;
        foreach (string facing in new[] { "N", "S", "E", "W" })
        {
            CommandResult result = engine.SendCommand(Pusher, $"PUSH FACE {facing}");
            output.WriteLine($"PUSH FACE {facing} -> success={result.Success}, message={result.Message}");
            if (result.Success)
            {
                amendment = facing;
                break;
            }
        }

        Assert.NotNull(amendment);
        Assert.Equal(anchor, pusher.Ground.TowRowAnchor);
        PushRun run = TickTow(engine, pusher, neighbour, SpotBudgetSeconds);
        Report($"PUSH $E amended to face {amendment}", pusher, neighbour, run);
        Assert.True(run.CompletedSecond > 0, $"the amended push never completed: held by {pusher.Ground.AutoYieldTarget ?? "nobody"}");
        Assert.True(run.LongestHoldSeconds <= MaxHoldSeconds, $"the push sat at a zero speed limit for {run.LongestHoldSeconds}s");
        Assert.True(
            run.ClosestFt >= rowGapFt - RowGapToleranceFt,
            $"the push came {run.ClosestFt:F1} ft from {Neighbour}, under the row's {rowGapFt:F1} ft wingtip gap"
        );
        Assert.Null(pusher.Ground.TowRowAnchor);
    }

    /// <summary>A command that ends the tug move mid-tow — a TAXI — ends the tow, and its row anchor with it.</summary>
    [Fact]
    public void PushTe_EndedMidTowByATaxi_ClearsTheRowAnchor()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(engine.SendCommand(Pusher, "PUSH TE").Success);
        for (int t = 0; t < MidTowSecond; t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<PushbackPhase>(pusher.Phases?.CurrentPhase);
        Assert.NotNull(pusher.Ground.TowRowAnchor);
        CommandResult? result = SendUntilTaken(engine, pusher, "TAXI TE", null);
        Assert.True(result?.Success, $"TAXI TE was never taken mid-tow: {result?.Message}");
        Assert.IsNotType<PushbackPhase>(pusher.Phases?.CurrentPhase);
        Assert.Null(pusher.Ground.TowRowAnchor);
    }

    /// <summary>
    /// A PUSHM mid-tow is a new tug instruction: it ends the tow under way and starts a new one, anchored at the predicted
    /// stop pose — where the tug brakes the cleared tow to rest — rather than at the stand the first tow left.
    /// </summary>
    [Fact]
    public void PushTe_RedirectedMidTowByPushm_ReanchorsFromThePredictedStopPose()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        PushbackPose stand = StandPose(layout, PushingStand);
        AircraftState pusher = Spawn(engine, layout, Pusher, stand);
        Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(engine.SendCommand(Pusher, "PUSH TE").Success);
        for (int t = 0; t < MidTowSecond; t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<PushbackPhase>(pusher.Phases?.CurrentPhase);
        Assert.Equal(stand, Assert.NotNull(pusher.Ground.TowRowAnchor).TowStartPose);
        PushbackPose redirectedAt = default;
        // The expected anchor is the predicted stop pose; TugReleaseTests.PushmMidTow_PlansFromThePredictedStopPose checks the formula itself.
        CommandResult? result = SendUntilTaken(engine, pusher, "PUSHM $E $C", () => redirectedAt = TugReleasePhase.PredictedStopPose(pusher));
        Assert.True(result?.Success, $"PUSHM $E $C was never taken mid-tow: {result?.Message}");

        TugRowAnchor reanchored = Assert.NotNull(pusher.Ground.TowRowAnchor);
        output.WriteLine($"re-anchored at {reanchored.TowStartPose} ({reanchored.FirstKind}); the stand is {stand}");
        Assert.Equal(redirectedAt, reanchored.TowStartPose);
        Assert.NotEqual(stand, reanchored.TowStartPose);
    }

    /// <summary>
    /// A one-target <c>PUSHM</c> mid-tow is the same redirect with a single point: the tow re-plans from the pose the
    /// instruction is given at, re-anchored there, and ends holding on that point.
    /// </summary>
    [Fact]
    public void PushTe_RedirectedMidTowByOneTargetPushm_ReplansToThatTarget()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        PushbackPose stand = StandPose(layout, PushingStand);
        AircraftState pusher = Spawn(engine, layout, Pusher, stand);
        AircraftState neighbour = Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(engine.SendCommand(Pusher, "PUSH TE").Success);
        for (int t = 0; t < MidTowSecond; t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<PushbackPhase>(pusher.Phases?.CurrentPhase);
        PushbackPose redirectedAt = default;
        // The expected anchor is the predicted stop pose; TugReleaseTests.PushmMidTow_PlansFromThePredictedStopPose checks the formula itself.
        CommandResult? result = SendUntilTaken(engine, pusher, "PUSHM $E", () => redirectedAt = TugReleasePhase.PredictedStopPose(pusher));
        Assert.True(result?.Success, $"PUSHM $E was never taken mid-tow: {result?.Message}");
        CommandResult taken = result ?? throw new InvalidOperationException("PUSHM $E returned no result");
        output.WriteLine($"PUSHM $E -> {taken.Message}");
        Assert.Equal("Push to spot E", taken.Message);
        Assert.Equal(redirectedAt, Assert.NotNull(pusher.Ground.TowRowAnchor).TowStartPose);

        PushRun run = TickTow(engine, pusher, neighbour, TowEndBudgetSeconds);
        Assert.True(run.CompletedSecond > 0, $"{Pusher} never finished the tow within {TowEndBudgetSeconds}s");
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
        GroundNode spotE = layout.FindSpotNodeByName("E") ?? throw new InvalidOperationException($"{layout.AirportId} has no spot 'E'");
        // A spot stop leaves the nosewheel on the mark and the reference point half a fuselage behind it.
        double halfLenNm = (AircraftFootprint.FromType(pusher.AircraftType).LengthFt / 2.0) / GeoMath.FeetPerNm;
        LatLon nosewheel = GeoMath.ProjectPoint(pusher.Position, pusher.TrueHeading, halfLenNm);
        double noseFt = GeoMath.DistanceNm(nosewheel, spotE.Position) * GeoMath.FeetPerNm;
        Assert.True(noseFt <= 30.0, $"{Pusher}'s nosewheel ended {noseFt:F1} ft from spot E");
    }

    /// <summary>A forced one-target <c>PUSHM</c> mid-tow is taken the same way: the same redirect, the same end state.</summary>
    [Fact]
    public void PushTe_RedirectedMidTowByOneTargetPushmf_IsAccepted()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        AircraftState neighbour = Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(engine.SendCommand(Pusher, "PUSH TE").Success);
        for (int t = 0; t < MidTowSecond; t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<PushbackPhase>(pusher.Phases?.CurrentPhase);
        PushbackPose redirectedAt = default;
        // The expected anchor is the predicted stop pose; TugReleaseTests.PushmMidTow_PlansFromThePredictedStopPose checks the formula itself.
        CommandResult? result = SendUntilTaken(engine, pusher, "PUSHMF $E", () => redirectedAt = TugReleasePhase.PredictedStopPose(pusher));
        Assert.True(result?.Success, $"PUSHMF $E was never taken mid-tow: {result?.Message}");
        CommandResult taken = result ?? throw new InvalidOperationException("PUSHMF $E returned no result");
        output.WriteLine($"PUSHMF $E -> {taken.Message}");
        Assert.Equal("Push to spot E (forced: will not stop for parked aircraft)", taken.Message);
        Assert.Equal(redirectedAt, Assert.NotNull(pusher.Ground.TowRowAnchor).TowStartPose);

        PushRun run = TickTow(engine, pusher, neighbour, TowEndBudgetSeconds);
        Assert.True(run.CompletedSecond > 0, $"{Pusher} never finished the tow within {TowEndBudgetSeconds}s");
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
    }

    /// <summary>Off tow a one-target tug move is plain <c>PUSH</c>'s job, and the refusal says so.</summary>
    [Theory]
    [InlineData("PUSHM $E")]
    [InlineData("PUSHMF $E")]
    public void OneTargetPushm_AtParking_StillRefused(string command)
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        CommandResult result = engine.SendCommand(Pusher, command);
        Assert.False(result.Success);
        Assert.Equal("Unable, a tug move needs at least two points — use PUSH to reach a single one", result.Message);
    }

    /// <summary>Holding after a push is off tow too, so a one-target tug move is refused there as well.</summary>
    [Theory]
    [InlineData("PUSHM $E")]
    [InlineData("PUSHMF $E")]
    public void OneTargetPushm_HoldingAfterPushback_StillRefused(string command)
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        AircraftState neighbour = Spawn(engine, layout, Neighbour, StandPose(layout, StaggeredStand));
        Assert.True(engine.SendCommand(Pusher, "PUSH TE").Success);
        PushRun run = TickTow(engine, pusher, neighbour, TowEndBudgetSeconds);
        Assert.True(run.CompletedSecond > 0, $"{Pusher} never finished the tow within {TowEndBudgetSeconds}s");
        Assert.IsType<HoldingAfterPushbackPhase>(pusher.Phases?.CurrentPhase);
        CommandResult result = engine.SendCommand(Pusher, command);
        Assert.False(result.Success);
        Assert.Equal("Unable, a tug move needs at least two points — use PUSH to reach a single one", result.Message);
    }

    /// <summary>
    /// A TAXI TE issued as the push reaches its turn-off node onto TE — the first move boundary the tow flies through at
    /// speed — on the first sub-tick the node is inside the brake distance (v² / 2a) at 2 kt or more, so the brake to rest
    /// carries the aircraft past it: the taxi, planned from the live pose, still follows its route to its end.
    /// </summary>
    [Fact]
    public void PushTe_TaxiAtTheTurnOffNode_BrakesPastIt_AndStillFollowsTheRoute()
    {
        if (Build() is not var (engine, layout))
        {
            return;
        }

        AircraftState pusher = Spawn(engine, layout, Pusher, StandPose(layout, PushingStand));
        Assert.True(engine.SendCommand(Pusher, "PUSH TE").Success);
        PushbackPhase? flownThrough = pusher.Phases!.Phases.OfType<PushbackPhase>().FirstOrDefault(p => p.ContinuesIntoNextMove);
        LatLon turnOff = (flownThrough ?? throw new InvalidOperationException("PUSH TE flies through no move boundary")).PlannedEnd;
        double decel = CategoryPerformance.TugDecelRate(AircraftCategory.Jet);
        CommandResult? taxi = null;
        string skipped = "";
        SubTickDriver.TickSubTicksUntil(
            engine,
            Pusher,
            _ => taxi is { Success: true },
            BudgetSeconds,
            a =>
            {
                if (taxi is { Success: true })
                {
                    return;
                }

                double toNodeFt = GeoMath.DistanceNm(a.Position, turnOff) * GeoMath.FeetPerNm;
                double brakeFt = a.GroundSpeed * a.GroundSpeed / (2.0 * decel) * GeoMath.FeetPerNm / 3600.0;
                if ((a.Phases?.CurrentPhase is not PushbackPhase) || (a.GroundSpeed < 2.0) || (toNodeFt > brakeFt))
                {
                    skipped = $"{a.Phases?.CurrentPhase?.Name} gs={a.GroundSpeed:F2} toNode={toNodeFt:F1} brake={brakeFt:F1}";
                    return;
                }

                taxi = engine.SendCommand(Pusher, "TAXI TE");
                output.WriteLine($"TAXI TE {toNodeFt:F1} ft short of the turn-off node at {a.GroundSpeed:F2} kt -> {taxi.Success}: {taxi.Message}");
            }
        );
        Assert.True(
            taxi is { Success: true },
            $"TAXI TE was never taken inside the brake distance; last skip: {skipped}; last result: {taxi?.Message}"
        );
        TaxiRoute route = pusher.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException("TAXI TE assigned no route");
        LatLon routeEnd = route.Segments[^1].Edge.ToNode.Position;

        bool taxied = false;
        int done = SfoGroundHarness.TickUntil(
            engine,
            () =>
            {
                taxied |= pusher.Phases?.CurrentPhase is TaxiingPhase;
                return taxied && (pusher.Phases?.CurrentPhase is not TaxiingPhase);
            },
            SpotBudgetSeconds,
            null
        );
        double offEndFt = GeoMath.DistanceNm(pusher.Position, routeEnd) * GeoMath.FeetPerNm;
        output.WriteLine($"taxi ended t={done}s in {pusher.Phases?.CurrentPhase?.Name ?? "null"}, {offEndFt:F1} ft from the route's end");
        Assert.True(done > 0, $"the taxi never finished within {SpotBudgetSeconds}s (phase={pusher.Phases?.CurrentPhase?.Name})");
        Assert.True(
            offEndFt <= (AircraftFootprint.FromType(Narrowbody).LengthFt / 2.0) + 25.0,
            $"the taxi ended {offEndFt:F1} ft from its route's end"
        );
    }

    /// <summary>
    /// Sends <paramref name="command"/> a second at a time, while the tow is under way, until it is taken or
    /// <see cref="CommandRetrySeconds"/> pass; <paramref name="beforeEachSend"/> runs just before each send.
    /// </summary>
    private static CommandResult? SendUntilTaken(SimulationEngine engine, AircraftState pusher, string command, Action? beforeEachSend)
    {
        CommandResult? result = null;
        for (int second = 0; (second < CommandRetrySeconds) && (pusher.Phases?.CurrentPhase is PushbackPhase); second++)
        {
            beforeEachSend?.Invoke();
            result = engine.SendCommand(Pusher, command);
            if (result.Success)
            {
                return result;
            }

            engine.TickOneSecond();
        }

        return result;
    }
}
