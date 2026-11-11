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
/// YAAT-2036 (S1-SFO-4, SFO landing 19L): an arrival told <c>CROSS 19R</c> after it has turned off 19L, with no taxi
/// instruction, crosses 19R and holds clear past 19R's far-side bar (COMMANDS.md, parallel-runway auto-pull-up: "A bare
/// CROSS (or CROSS 19R) then takes it across without a prior TAXI"). Restored from the recorded snapshots and ticked with
/// no recorded action, so the only instruction the aircraft has is the CROSS.
/// </summary>
public class ArrivalCrossParallelWithoutTaxiTests(ITestOutputHelper output)
{
    private const string BundlePath = "TestData/sfo-arrival-cross-19r-recording.yaat-bug-report-bundle.zip";

    /// <summary>
    /// JBU577 (A321) turning off 19L onto H at t=422, still in its runway exit, 838 ft short of 19R's bar on H (#880):
    /// the mentor's <c>CROSS 19R</c>. It must be taken (the exit's pull-up continues across 19R), not refused for want of a
    /// taxi route, and the aircraft must end stopped past 19R's far bar on H (#879), not held at #880.
    /// </summary>
    [Fact]
    public void Jbu577_Cross19rWhileTurningOffOntoH_CrossesAndHoldsPastFarBar() =>
        AssertCrossesWithoutTaxi("JBU577", restoreAt: 410, crossAt: 422, nearBar: 880, farBar: 879, tickUntil: 600);

    /// <summary>
    /// KLM605 (B78X) off 19L onto M, a crossover too short for it: the exit stops it with its nose at 19R's bar on M (#882)
    /// and the status "holding short of 19R". <c>CROSS 19R</c> there must take it across, past M's far bar (#881).
    /// </summary>
    [Fact]
    public void Klm605_Cross19rHeldShortOf19rAfterExitOnM_CrossesAndHoldsPastFarBar() =>
        AssertCrossesWithoutTaxi("KLM605", restoreAt: 660, crossAt: 682, nearBar: 882, farBar: 881, tickUntil: 800);

    private void AssertCrossesWithoutTaxi(string callsign, int restoreAt, int crossAt, int nearBar, int farBar, int tickUntil)
    {
        // Pinned to the recorded layout: the restored exits and the bars named here are the recorded graph's node ids.
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "an arrival crossing the parallel on CROSS alone");
        if (groundData.GetLayout("SFO") is not { } layout)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        using RecordingArchive archive = Assert.IsType<RecordingArchive>(RecordingLoader.OpenArchive(BundlePath));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(archive.ToBaseSessionRecording(), 0);
        TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(restoreAt));
        engine.RestoreFromSnapshot(snapshot.State);

        int t = (int)snapshot.ElapsedSeconds;
        for (; t < crossAt; t++)
        {
            engine.TickOneSecond();
        }

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(callsign));
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"t={t} {callsign} {aircraft.Phases?.CurrentPhase?.GetType().Name} gs={aircraft.GroundSpeed:F1}");

        CommandResult cross = engine.SendCommand(callsign, "CROSS 19R");
        output.WriteLine($"CROSS 19R: {cross.Success} '{cross.Message}'");
        Assert.True(cross.Success, $"CROSS 19R to {callsign} with no taxi route was refused: {cross.Message}");

        bool crossed = false;
        for (; t < tickUntil; t++)
        {
            engine.TickOneSecond();
            crossed |= aircraft.Phases?.CurrentPhase is CrossingRunwayPhase;
        }

        double barsApartFt = GeoMath.DistanceNm(layout.Nodes[nearBar].Position, layout.Nodes[farBar].Position) * GeoMath.FeetPerNm;
        double fromNearFt = GeoMath.DistanceNm(aircraft.Position, layout.Nodes[nearBar].Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"t={t} {aircraft.Phases?.CurrentPhase?.GetType().Name} gs={aircraft.GroundSpeed:F1} fromNear={fromNearFt:F0} barsApart={barsApartFt:F0}"
        );
        Assert.True(crossed, $"{callsign} never crossed 19R after CROSS 19R");
        Assert.True(
            fromNearFt > barsApartFt,
            $"{callsign} ended {fromNearFt:F0} ft from 19R's near bar, short of the far bar {barsApartFt:F0} ft away"
        );
        Assert.True(aircraft.GroundSpeed < 0.5, $"{callsign} should hold clear past the far bar; gs={aircraft.GroundSpeed:F1}");
    }

    /// <summary>
    /// JBU577's <c>CROSS 19R</c> during the turn-off onto H is read back with where it crosses, as a CROSS at a taxiway reads
    /// ("cross runway 19R at H"), never as a hold short.
    /// </summary>
    [Fact]
    public void Jbu577_Cross19rWhileTurningOffOntoH_ReadsBackCrossRunwayAtH()
    {
        CrossRun? run = RunCross(new CrossCase("JBU577", RestoreAt: 410, CrossAt: 422, NearBar: 880, FarBar: 879, TickUntil: 422));
        if (run is null)
        {
            return;
        }

        Assert.True(run.Cross.Success, run.Cross.Message);
        string? readback = run.Cross.PilotReadback?.Speech.Terminal;
        output.WriteLine($"readback: '{readback}' / '{run.Cross.PilotReadback?.Speech.Tts}'");
        Assert.NotNull(readback);
        Assert.Contains("cross runway 19R at H", readback, StringComparison.Ordinal);
        Assert.DoesNotContain("hold short", readback, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// KLM605 sent <c>CROSS 19R</c> while still in its runway exit onto M, a crossover too short for it: the clearance is kept,
    /// and where the exit would have stopped it with the nose at 19R's bar it crosses instead and holds past M's far bar.
    /// </summary>
    [Fact]
    public void Klm605_Cross19rDuringItsExitOntoTooShortM_CrossesAndHoldsPastFarBar()
    {
        CrossRun? run = RunCross(new CrossCase("KLM605", RestoreAt: 660, CrossAt: 660, NearBar: 882, FarBar: 881, TickUntil: 800));
        run?.AssertEndedPastFarBar(output, stopped: true);
    }

    /// <summary>
    /// JBU577 on its 19L rollout before its exit is chosen, sent <c>CROSS 19R</c>: the exit is taken on the 19R side and the
    /// aircraft crosses 19R without stopping at the near bar, then holds past the far bar.
    /// </summary>
    [Fact]
    public void Jbu577_Cross19rOnTheRolloutBeforeAnExit_ExitsToward19rAndCrossesWithoutStopping()
    {
        CrossRun? run = RunCross(new CrossCase("JBU577", RestoreAt: 410, CrossAt: 410, NearBar: 880, FarBar: 879, TickUntil: 600));
        if (run is null)
        {
            return;
        }

        run.AssertEndedPastFarBar(output, stopped: true);
        Assert.False(run.HeldShortOfNearBar, "JBU577 stopped holding short of 19R's near bar despite its pre-clearance");
    }

    /// <summary>
    /// JBU577 given <c>TAXI H B</c> from the rollout end, a route ending at 19R's near bar on H, with auto-cross All (set at
    /// t=286): the route-ending bar is held, and <c>CROSS 19R</c> at t=490 takes the aircraft across 19R, not an OK that moves
    /// it nowhere.
    /// </summary>
    [Fact]
    public void Jbu577_TaxiRouteEndingAt19rBar_Cross19r_CrossesPastFarBar()
    {
        CrossRun? run = RunCross(new CrossCase("JBU577", RestoreAt: 485, CrossAt: 490, NearBar: 880, FarBar: 879, TickUntil: 650));
        run?.AssertEndedPastFarBar(output, stopped: false);
    }

    /// <summary>
    /// TAI562 given <c>TAXI H A</c> at t=861 from the rollout end (as recorded), then <c>CROSS 19R</c> at t=897: it crosses 19R
    /// and ends past the far bar, continuing its issued route if that carries on across.
    /// </summary>
    [Fact]
    public void Tai562_TaxiHAThenCross19r_CrossesPastFarBar()
    {
        var crossCase = new CrossCase("TAI562", RestoreAt: 855, CrossAt: 897, NearBar: 880, FarBar: 879, TickUntil: 1050)
        {
            Script = [(861, "TAXI H A")],
        };
        CrossRun? run = RunCross(crossCase);
        if (run is null)
        {
            return;
        }

        Assert.IsType<HoldingShortPhase>(run.PhaseAtCross);
        run.AssertEndedPastFarBar(output, stopped: false);
    }

    /// <summary>
    /// JBU577 told <c>EL</c> on its 19L rollout, so its exit leads away from 19R, then sent <c>CROSS 19R</c> during that exit: the
    /// crossing is refused naming the exit, and nothing is stored.
    /// </summary>
    [Fact]
    public void Jbu577_ExitingLeftAwayFrom19r_Cross19r_RefusedAndNothingStored()
    {
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "an arrival exiting away from the parallel");
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        using RecordingArchive archive = Assert.IsType<RecordingArchive>(RecordingLoader.OpenArchive(BundlePath));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(archive.ToBaseSessionRecording(), 0);
        engine.RestoreFromSnapshot(Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(410)).State);
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft("JBU577"));
        CommandResult left = engine.SendCommand("JBU577", "EL");
        output.WriteLine($"EL: {left.Success} '{left.Message}'");
        Assert.True(left.Success, left.Message);

        for (int s = 0; (s < 90) && (aircraft.Phases?.CurrentPhase is not RunwayExitPhase { HoldTaxiway: not null }); s++)
        {
            engine.TickOneSecond();
        }

        RunwayExitPhase exit = Assert.IsType<RunwayExitPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"exiting onto {exit.HoldTaxiway}");
        CommandResult cross = engine.SendCommand("JBU577", "CROSS 19R");
        output.WriteLine($"CROSS 19R: {cross.Success} '{cross.Message}'");
        Assert.False(cross.Success, "CROSS 19R accepted for an exit leading away from 19R");
        Assert.Equal($"CROSS 19R: exit {exit.HoldTaxiway} does not lead to runway 19R", cross.Message);
        Assert.Null(aircraft.Phases?.PreClearedCrossing);
    }

    /// <summary>
    /// JBU577 pre-cleared across 19R on its rollout with the <c>AutoPullUpToParallel</c> setting off: the exit hands off to the
    /// pull-up at speed, at the speed it planned (<see cref="RunwayExitPhase.PlannedHandOffKts"/>, the vacate predictor's input).
    /// </summary>
    [Fact]
    public void Jbu577_PreClearedWithAutoPullUpOff_HandsOffAtSpeedAsPlanned()
    {
        if (Restore(410) is not { } session)
        {
            return;
        }

        session.Engine.Scenario!.AutoPullUpToParallel = false;
        AircraftState aircraft = session.Aircraft("JBU577");
        Assert.True(session.Engine.SendCommand("JBU577", "CROSS 19R").Success);
        double? planned = null;
        double? handOffGs = null;
        for (int s = 0; (s < 200) && (handOffGs is null); s++)
        {
            bool wasExiting = aircraft.Phases?.CurrentPhase is RunwayExitPhase;
            if (aircraft.Phases?.CurrentPhase is RunwayExitPhase exit)
            {
                planned = exit.PlannedHandOffKts(aircraft) ?? planned;
            }

            session.Engine.TickOneSecond();
            handOffGs = (wasExiting && (aircraft.Phases?.CurrentPhase is not RunwayExitPhase)) ? aircraft.GroundSpeed : null;
        }

        output.WriteLine($"planned={planned:F1} hand-off gs={handOffGs:F1} phase={aircraft.Phases?.CurrentPhase?.GetType().Name}");
        Assert.NotNull(planned);
        Assert.NotNull(handOffGs);
        Assert.True(handOffGs > 1.0, $"JBU577 stopped at the exit (gs {handOffGs:F1}) though it planned a hand-off at {planned:F1} kts");
        Assert.True(Math.Abs(handOffGs.Value - planned.Value) <= 3.0, $"hand-off at {handOffGs:F1} kts, planned {planned:F1} kts");
    }

    /// <summary>
    /// An arrival still carrying the route of an earlier taxi-out (the assigned taxi route is not cleared
    /// at takeoff) gets <c>CROSS 19R</c> during its exit taken as for any arrival: only the rollout's stored route counts.
    /// </summary>
    [Fact]
    public void Jbu577_StaleTaxiOutRoute_Cross19rDuringItsExit_IsStored()
    {
        var crossCase = new CrossCase("JBU577", RestoreAt: 410, CrossAt: 422, NearBar: 880, FarBar: 879, TickUntil: 422)
        {
            Before = AssignStaleTaxiOutRoute,
        };
        CrossRun? run = RunCross(crossCase);
        if (run is null)
        {
            return;
        }

        Assert.True(run.Cross.Success, run.Cross.Message);
        Assert.Equal("19R", run.PreClearedAfterCross);
    }

    /// <summary>A CROSS of the landing runway itself on the rollout is no pre-clearance of a parallel: today's refusal stands.</summary>
    [Fact]
    public void Jbu577_Cross19lItsOwnRunwayOnTheRollout_KeepsTodaysRefusal()
    {
        var crossCase = new CrossCase("JBU577", RestoreAt: 410, CrossAt: 410, NearBar: 880, FarBar: 879, TickUntil: 410) { Command = "CROSS 19L" };
        CrossRun? run = RunCross(crossCase);
        if (run is null)
        {
            return;
        }

        Assert.False(run.Cross.Success, "CROSS 19L on the 19L rollout was accepted");
        Assert.Equal("No taxi route assigned", run.Cross.Message);
        Assert.Null(run.PreClearedAfterCross);
    }

    /// <summary>
    /// JBU577 crossed 19R on H on its <c>TAXI H A</c> (t=494, CROSS 19R at t=496, as recorded) and taxiing on past the far bar:
    /// another <c>CROSS 19R</c> has no bar left to clear and is refused, not answered OK with no movement.
    /// </summary>
    [Fact]
    public void Jbu577_PastThe19rBarsOnItsRoute_Cross19r_RefusedNotHoldingShort()
    {
        var crossCase = new CrossCase("JBU577", RestoreAt: 485, CrossAt: 497, NearBar: 880, FarBar: 879, TickUntil: 497)
        {
            Script = [(494, "TAXI H A"), (496, "CROSS 19R")],
            CrossWhen = (aircraft, layout) =>
                (aircraft.Phases?.CurrentPhase is TaxiingPhase)
                && (aircraft.Ground.AssignedTaxiRoute is { IsComplete: false })
                && (
                    FeetBetween(aircraft.Position, layout.Nodes[880].Position)
                    > FeetBetween(layout.Nodes[879].Position, layout.Nodes[880].Position) + 100.0
                ),
        };
        CrossRun? run = RunCross(crossCase);
        if (run is null)
        {
            return;
        }

        Assert.False(run.Cross.Success, $"CROSS 19R past both 19R bars was accepted: {run.Cross.Message}");
        Assert.Equal("CROSS 19R: not holding short of runway 19R", run.Cross.Message);
    }

    /// <summary>
    /// KLM605's crossing from the hold after its exit arms the taxi-in call, as for an arrival holding past a parallel it had to
    /// cross first.
    /// </summary>
    [Fact]
    public void Klm605_Cross19rHeldShortAfterExit_ArmsTheTaxiInCall()
    {
        CrossRun? run = RunCross(new CrossCase("KLM605", RestoreAt: 660, CrossAt: 682, NearBar: 882, FarBar: 881, TickUntil: 682));
        if (run is null)
        {
            return;
        }

        Assert.True(run.Cross.Success, run.Cross.Message);
        Assert.False(run.AwaitingTaxiInCallBeforeCross, "KLM605 stopped short of clearing 19L already owed the taxi-in call");
        Assert.True(run.AwaitingTaxiInCallAfterCross, "KLM605's crossing from the hold after its exit did not arm the taxi-in call");
    }

    /// <summary>
    /// KLM605 held short of 19R after its exit onto M, still carrying an unfinished route of an earlier taxi-out: that route can
    /// only be stale there, so <c>CROSS 19R</c> crosses from the hold as for any arrival.
    /// </summary>
    [Fact]
    public void Klm605_StaleTaxiOutRouteHeldAfterExit_Cross19r_Crosses()
    {
        var crossCase = new CrossCase("KLM605", RestoreAt: 660, CrossAt: 682, NearBar: 882, FarBar: 881, TickUntil: 800)
        {
            Before = AssignStaleTaxiOutRoute,
        };
        RunCross(crossCase)?.AssertEndedPastFarBar(output, stopped: true);
    }

    /// <summary>
    /// JBU577 at rest at the end of its <c>TAXI H B</c> (restored t=485), a route ending at 19R's bar on H, sent <c>CROSS 19R</c>:
    /// it crosses, and the crossing arms no taxi-in call; an aircraft that taxied there on a clearance owes none.
    /// </summary>
    [Fact]
    public void Jbu577_CrossFromItsRouteEndBar_DoesNotArmTheTaxiInCall()
    {
        CrossRun? run = RunCross(new CrossCase("JBU577", RestoreAt: 485, CrossAt: 490, NearBar: 880, FarBar: 879, TickUntil: 490));
        if (run is null)
        {
            return;
        }

        Assert.True(run.Cross.Success, run.Cross.Message);
        Assert.False(run.AwaitingTaxiInCallBeforeCross);
        Assert.False(run.AwaitingTaxiInCallAfterCross, "a CROSS from a taxi route's end bar armed the taxi-in call");
    }

    /// <summary>
    /// A rollout <c>TAXI</c> after a rollout <c>CROSS 19R</c> drops the exit side the CROSS chose with the crossing: the TAXI's
    /// own exit governs.
    /// </summary>
    [Fact]
    public void Jbu577_TaxiAfterARolloutCross19r_DropsTheExitSideTheCrossChose()
    {
        CrossRun? run = RunCross(new CrossCase("JBU577", RestoreAt: 410, CrossAt: 410, NearBar: 880, FarBar: 879, TickUntil: 410));
        if (run is null)
        {
            return;
        }

        ExitPreference? crossSide = run.Aircraft.Phases?.RequestedExit;
        Assert.NotNull(crossSide);
        CommandResult taxi = run.Engine.SendCommand("JBU577", "TAXI H B");
        ExitPreference? after = run.Aircraft.Phases?.RequestedExit;
        string seen = $"TAXI H B: {taxi.Success} '{taxi.Message}' exit={after?.Side}/{after?.Taxiway} same={ReferenceEquals(crossSide, after)}";
        output.WriteLine(seen);
        Assert.True(taxi.Success, taxi.Message);
        Assert.False(ReferenceEquals(crossSide, after), seen);
        Assert.Null(run.Aircraft.Phases?.CrossExitPreference);
    }

    /// <summary>
    /// TAI562 held at 19R's bar on H where its <c>TAXI H A</c> ends (a runway bar a route with no destination ends at): a RES there
    /// is refused naming CROSS, which releases that bar, and a new TAXI.
    /// </summary>
    [Fact]
    public void Tai562_HeldAtItsRouteEndRunwayBar_ResRefusalNamesCross()
    {
        var crossCase = new CrossCase("TAI562", RestoreAt: 855, CrossAt: 897, NearBar: 880, FarBar: 879, TickUntil: 897)
        {
            Script = [(861, "TAXI H A")],
            Command = "RES",
        };
        CrossRun? run = RunCross(crossCase);
        if (run is null)
        {
            return;
        }

        Assert.IsType<HoldingShortPhase>(run.PhaseAtCross);
        Assert.False(run.Cross.Success);
        Assert.Contains("CROSS", run.Cross.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("issue a TAXI that includes", run.Cross.Message, StringComparison.Ordinal);
    }

    /// <summary>An unfinished route of an earlier taxi-out on B, nowhere near 19R, as an arrival may still carry it.</summary>
    private static void AssignStaleTaxiOutRoute(SimulationEngine engine, AircraftState aircraft)
    {
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(engine.ResolveGroundLayout(aircraft));
        GroundNode from = layout.Nodes.Values.First(n =>
            (n.Type != GroundNodeType.RunwayHoldShort) && n.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway("B"))
        );
        IGroundEdge edge = from.Edges.First(e => (e is not GroundArc) && e.MatchesTaxiway("B"));
        GroundNode to = layout.Nodes[edge.OtherNodeId(from.Id)];
        aircraft.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = "B", Edge = edge.Directed(from, to) }],
            HoldShortPoints = [],
        };
    }

    /// <summary>
    /// JBU577 told <c>EL</c>, then <c>CROSS 19R</c> on the rollout: the exit instruction stands, and once the left exit is taken the
    /// stored crossing is dropped with the instructor's warning.
    /// </summary>
    [Fact]
    public void Jbu577_ToldElThenCross19rOnTheRollout_DropsTheCrossingWithAWarning()
    {
        CrossRun? run = RunCross(ExitLeftThenCross19r(tickUntil: 600));
        if (run is null)
        {
            return;
        }

        Assert.True(run.Cross.Success, run.Cross.Message);
        output.WriteLine($"warnings: {string.Join(" | ", run.Warnings)}");
        Assert.Contains(
            run.Warnings,
            w => w.StartsWith("CROSS 19R dropped: exit ", StringComparison.Ordinal) && w.EndsWith(" does not lead to runway 19R")
        );
        Assert.Null(run.Aircraft.Phases?.PreClearedCrossing);
    }

    /// <summary>
    /// JBU577 held after its left exit (<c>EL</c>), away from 19R, sent <c>CROSS 19R</c>: the taxiway it holds on does not cross
    /// 19R, so the crossing is refused naming it.
    /// </summary>
    [Fact]
    public void Jbu577_HeldAfterALeftExit_Cross19r_RefusedTaxiwayDoesNotCross()
    {
        var crossCase = new CrossCase("JBU577", RestoreAt: 410, CrossAt: 411, NearBar: 880, FarBar: 879, TickUntil: 411)
        {
            Script = [(410, "EL")],
            CrossWhen = (aircraft, _) => (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase) && (aircraft.GroundSpeed < 0.5),
        };
        CrossRun? run = RunCross(crossCase);
        if (run is null)
        {
            return;
        }

        string taxiway = Assert.IsType<string>(run.TaxiwayAtCross);
        Assert.False(run.Cross.Success, $"CROSS 19R from {taxiway} was accepted");
        Assert.Equal($"CROSS 19R: taxiway {taxiway} does not cross runway 19R", run.Cross.Message);
    }

    /// <summary>
    /// A rollout <c>TAXI</c> after a rollout <c>CROSS 19R</c> replaces it: the TAXI's own clearance governs, and the stored
    /// crossing is dropped at once.
    /// </summary>
    [Fact]
    public void Jbu577_TaxiAfterARolloutCross19r_DropsTheStoredCrossing()
    {
        CrossRun? run = RunCross(new CrossCase("JBU577", RestoreAt: 410, CrossAt: 410, NearBar: 880, FarBar: 879, TickUntil: 410));
        if (run is null)
        {
            return;
        }

        Assert.Equal("19R", run.PreClearedAfterCross);
        CommandResult taxi = run.Engine.SendCommand("JBU577", "TAXI H B");
        output.WriteLine($"TAXI H B: {taxi.Success} '{taxi.Message}'");
        Assert.True(taxi.Success, taxi.Message);
        Assert.Null(run.Aircraft.Phases?.PreClearedCrossing);
    }

    /// <summary>
    /// JBU577 given <c>TAXI H B</c> at t=436 (as recorded), a route ending at 19R's near bar on H, sent <c>CROSS 19R</c> while still
    /// taxiing toward that bar: the route is extended across 19R, the bar cleared and no longer its end, and the aircraft crosses.
    /// </summary>
    [Fact]
    public void Jbu577_TaxiHBRolling_Cross19rBeforeTheEndBar_ExtendsTheRouteAcross()
    {
        var crossCase = new CrossCase("JBU577", RestoreAt: 410, CrossAt: 437, NearBar: 880, FarBar: 879, TickUntil: 650)
        {
            Script = [(436, "TAXI H B")],
            CrossWhen = (aircraft, _) =>
                (aircraft.Phases?.CurrentPhase is TaxiingPhase)
                && (aircraft.Ground.AssignedTaxiRoute is { IsComplete: false } route)
                && route.HoldShortPoints.Exists(hs => (hs.NodeId == 880) && hs.EndsNoDestinationRoute)
                && (route.SegmentIndexAhead(880) != int.MaxValue),
        };
        CrossRun? run = RunCross(crossCase);
        if (run is null)
        {
            return;
        }

        Assert.True(run.Cross.Success, run.Cross.Message);
        Assert.NotNull(run.EndBarAfterCross);
        Assert.True(run.EndBarAfterCross.IsCleared, "the bar the route ended at was not cleared");
        Assert.False(run.EndBarAfterCross.EndsNoDestinationRoute, "the bar still ends the route");
        Assert.NotEqual(880, run.RouteEndAfterCross);
        run.AssertEndedPastFarBar(output, stopped: false);
    }

    private static CrossCase ExitLeftThenCross19r(int tickUntil) =>
        new("JBU577", RestoreAt: 410, CrossAt: 410, NearBar: 880, FarBar: 879, TickUntil: tickUntil)
        {
            Before = (engine, _) => Assert.True(engine.SendCommand("JBU577", "EL").Success),
        };

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private sealed record CrossCase(string Callsign, int RestoreAt, int CrossAt, int NearBar, int FarBar, int TickUntil)
    {
        public IReadOnlyList<(int At, string Command)> Script { get; init; } = [];

        public string Command { get; init; } = "CROSS 19R";

        /// <summary>Run just before the CROSS is sent.</summary>
        public Action<SimulationEngine, AircraftState>? Before { get; init; }

        /// <summary>When set, ticking goes on past <see cref="CrossAt"/> (up to <see cref="MaxWaitSeconds"/>) until it holds.</summary>
        public Func<AircraftState, AirportGroundLayout, bool>? CrossWhen { get; init; }

        public int MaxWaitSeconds { get; init; } = 240;
    }

    private sealed record Session(SimulationEngine Engine, AirportGroundLayout Layout, int ElapsedSeconds)
    {
        public AircraftState Aircraft(string callsign) => Assert.IsType<AircraftState>(Engine.FindAircraft(callsign));
    }

    private static Session? Restore(int restoreAt)
    {
        var groundData = RecordingGroundData.VersionPinned(BundlePath, "an arrival crossing the parallel on CROSS");
        if (groundData.GetLayout("SFO") is not { } layout)
        {
            return null;
        }

        TestVnasData.EnsureInitialized();
        using RecordingArchive archive = Assert.IsType<RecordingArchive>(RecordingLoader.OpenArchive(BundlePath));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(archive.ToBaseSessionRecording(), 0);
        TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(restoreAt));
        engine.RestoreFromSnapshot(snapshot.State);
        return new Session(engine, layout, (int)snapshot.ElapsedSeconds);
    }

    private sealed record CrossRun(CrossCase Case, AircraftState Aircraft, AirportGroundLayout Layout, CommandResult Cross, LatLon CrossedFrom)
    {
        public required SimulationEngine Engine { get; init; }

        public bool HeldShortOfNearBar { get; init; }

        public bool Crossed { get; init; }

        public string? PreClearedAfterCross { get; init; }

        public bool AwaitingTaxiInCallBeforeCross { get; init; }

        public bool AwaitingTaxiInCallAfterCross { get; init; }

        public string? TaxiwayAtCross { get; init; }

        public Phase? PhaseAtCross { get; init; }

        public HoldShortPoint? EndBarAfterCross { get; init; }

        public int? RouteEndAfterCross { get; init; }

        public List<string> Warnings { get; init; } = [];

        public void AssertEndedPastFarBar(ITestOutputHelper output, bool stopped)
        {
            Assert.True(Cross.Success, $"CROSS 19R to {Case.Callsign} was refused: {Cross.Message}");
            LatLon near = Layout.Nodes[Case.NearBar].Position;
            double barsApartFt = GeoMath.DistanceNm(near, Layout.Nodes[Case.FarBar].Position) * GeoMath.FeetPerNm;
            double fromNearFt = GeoMath.DistanceNm(Aircraft.Position, near) * GeoMath.FeetPerNm;
            double movedFt = GeoMath.DistanceNm(Aircraft.Position, CrossedFrom) * GeoMath.FeetPerNm;
            output.WriteLine($"end: moved={movedFt:F0} fromNear={fromNearFt:F0} barsApart={barsApartFt:F0} gs={Aircraft.GroundSpeed:F1}");
            Assert.True(movedFt > 100.0, $"{Case.Callsign} moved {movedFt:F0} ft after CROSS 19R");
            Assert.True(
                fromNearFt > barsApartFt,
                $"{Case.Callsign} ended {fromNearFt:F0} ft from the near bar, short of the far bar {barsApartFt:F0} ft away"
            );
            if (stopped)
            {
                Assert.True(Crossed, $"{Case.Callsign} never crossed 19R after CROSS 19R");
                Assert.True(Aircraft.GroundSpeed < 0.5, $"{Case.Callsign} should hold clear past the far bar; gs={Aircraft.GroundSpeed:F1}");
            }
        }
    }

    private CrossRun? RunCross(CrossCase c)
    {
        if (Restore(c.RestoreAt) is not { } session)
        {
            return null;
        }

        (SimulationEngine engine, AirportGroundLayout layout, int t) = session;
        List<string> warnings = [];
        engine.WarningEmitted += (callsign, text) =>
        {
            if (callsign == c.Callsign)
            {
                warnings.Add(text);
            }
        };
        AircraftState aircraft = session.Aircraft(c.Callsign);
        int waitUntil = c.CrossAt + c.MaxWaitSeconds;
        for (; (t < c.CrossAt) || ((c.CrossWhen is { } ready) && !ready(aircraft, layout) && (t < waitUntil)); t++)
        {
            foreach ((int _, string command) in c.Script.Where(s => s.At == t))
            {
                CommandResult scripted = engine.SendCommand(c.Callsign, command);
                output.WriteLine($"t={t} {command}: {scripted.Success} '{scripted.Message}'");
            }

            engine.TickOneSecond();
        }

        c.Before?.Invoke(engine, aircraft);
        Phase? phaseAtCross = aircraft.Phases?.CurrentPhase;
        bool awaitingBefore = aircraft.Ground.AwaitingTaxiInCall;
        string? taxiwayAtCross = aircraft.Ground.CurrentTaxiway;
        output.WriteLine(
            $"t={t} {c.Callsign} {aircraft.Phases?.CurrentPhase?.GetType().Name} gs={aircraft.GroundSpeed:F1} twy={aircraft.Ground.CurrentTaxiway}"
        );
        if (aircraft.ActiveOrStoredTaxiRoute is { } before)
        {
            SfoGroundHarness.DumpRoute(output, before);
        }

        LatLon from = aircraft.Position;
        CommandResult cross = engine.SendCommand(c.Callsign, c.Command);
        output.WriteLine($"{c.Command}: {cross.Success} '{cross.Message}' readback='{cross.PilotReadback?.Speech.Terminal}'");
        string? preCleared = aircraft.Phases?.PreClearedCrossing;
        bool awaitingTaxiInCall = aircraft.Ground.AwaitingTaxiInCall;
        HoldShortPoint? endBar = aircraft.ActiveOrStoredTaxiRoute?.HoldShortPoints.Find(hs => hs.NodeId == c.NearBar);
        int? routeEnd = (aircraft.ActiveOrStoredTaxiRoute is { Segments.Count: > 0 } after) ? after.Segments[^1].ToNodeId : null;
        bool crossed = false;
        bool heldAtNear = false;
        for (; t < c.TickUntil; t++)
        {
            engine.TickOneSecond();
            crossed |= aircraft.Phases?.CurrentPhase is CrossingRunwayPhase;
            heldAtNear |= aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.NodeId: var held } && (held == c.NearBar);
        }

        output.WriteLine($"t={t} {aircraft.Phases?.CurrentPhase?.GetType().Name} crossed={crossed} heldAtNear={heldAtNear}");
        return new CrossRun(c, aircraft, layout, cross, from)
        {
            Engine = engine,
            Crossed = crossed,
            HeldShortOfNearBar = heldAtNear,
            PreClearedAfterCross = preCleared,
            AwaitingTaxiInCallBeforeCross = awaitingBefore,
            AwaitingTaxiInCallAfterCross = awaitingTaxiInCall,
            TaxiwayAtCross = taxiwayAtCross,
            PhaseAtCross = phaseAtCross,
            EndBarAfterCross = endBar,
            RouteEndAfterCross = routeEnd,
            Warnings = warnings,
        };
    }
}
