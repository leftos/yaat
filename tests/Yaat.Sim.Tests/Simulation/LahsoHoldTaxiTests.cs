using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A <c>TAXI</c> to an aircraft holding on the runway after a LAHSO stop, over the real SFO layout: land 28R holding short of 1L,
/// stop at the point, then taxi (YAAT-1944).
///
/// <para>
/// AIM 4-3-11.c.2 and 7110.65 §3-7-2.a.1: the aircraft stopped short of the intersecting runway crosses it only on a crossing
/// clearance. A <c>TAXI</c> whose route crosses the held runway is stored, as a <c>TAXI</c> given during the rollout is, and the
/// aircraft keeps holding; <c>CROSS</c> of that runway releases the hold and the stored route runs. A <c>TAXI</c> carrying
/// <c>CROSS</c> of the held runway proceeds at once; one whose route never enters it runs at once and ends the hold.
/// </para>
/// </summary>
public class LahsoHoldTaxiTests(ITestOutputHelper output)
{
    /// <summary>The layouts under <c>TestData</c>, cached across every run in this class.</summary>
    private static readonly TestAirportGroundData GroundData = new();

    private const string AircraftType = "E75L";
    private const double ApproachSpeedKts = 135;

    /// <summary>The taxiway the stored route leaves 28R on, past the 1L crossing.</summary>
    private const string TaxiwayPastHold = "E";

    /// <summary>How far (ft) the held aircraft may move while it keeps holding.</summary>
    private const double HeldDriftFt = 5;

    private sealed record HeldRun(
        SimulationEngine Engine,
        AircraftState Aircraft,
        AirportGroundLayout Layout,
        RunwayInfo Runway,
        string CrossingRunwayId
    );

    /// <summary>
    /// Spawns the aircraft on 1 nm final for SFO 28R, clears it to land and hold short of 1L, and ticks the production loop
    /// until it is stopped in the runway hold. With <paramref name="forcedRolloutTaxi"/>, the landing is also forced
    /// (<c>CLANDF</c>) and that TAXI is sent on the first tick of the rollout, where it must be accepted and stored. Null when
    /// the layout is absent (silent skip).
    /// </summary>
    private HeldRun? StageHeld(string? forcedRolloutTaxi)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("LandingPhase", LogLevel.Debug)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .InitializeSimLog();
        var engine = new SimulationEngine(GroundData, PushPlanStore.CreateShipped());

        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        RunwayInfo? runway28R = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway28R);

        double reciprocal = (runway28R.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway28R.ThresholdLatitude, runway28R.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = "TST001",
            AircraftType = AircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway28R.TrueHeading,
            TrueTrack = runway28R.TrueHeading,
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(
                GeoMath.DistanceNm(new LatLon(acLat, acLon), LandingThreshold.Resolve(runway28R, layout)),
                runway28R.ElevationFt,
                AircraftCategorization.Categorize(AircraftType)
            ),
            IndicatedAirspeed = ApproachSpeedKts,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway28R },
        };

        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-lahso-hold-taxi",
            ScenarioName = "SFO 28R LAHSO hold taxi",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        CommandResult lahso = engine.SendCommand("TST001", "LAHSO 1L");
        Assert.True(lahso.Success, $"LAHSO 1L failed: {lahso.Message}");
        LahsoTarget? target = aircraft.Phases.LahsoHoldShort;
        Assert.NotNull(target);
        string crossingRunwayId = target.CrossingRunwayId;
        if (forcedRolloutTaxi is not null)
        {
            CommandResult clandf = engine.SendCommand("TST001", "CLANDF");
            Assert.True(clandf.Success, $"CLANDF failed: {clandf.Message}");
        }

        bool rolloutTaxiSent = forcedRolloutTaxi is null;
        for (int t = 1; t <= 420; t++)
        {
            engine.TickOneSecond();
            if (
                !rolloutTaxiSent
                && aircraft.IsOnGround
                && (aircraft.Phases.CurrentPhase is LandingPhase)
                && (aircraft.GroundSpeed <= RolloutTaxiSpeedKts)
            )
            {
                rolloutTaxiSent = true;
                CommandResult rolloutTaxi = engine.SendCommand("TST001", forcedRolloutTaxi!);
                output.WriteLine($"t+{t}s on the forced rollout, {forcedRolloutTaxi}: success={rolloutTaxi.Success} message={rolloutTaxi.Message}");
                Assert.True(rolloutTaxi.Success, $"{forcedRolloutTaxi} on the forced rollout refused: {rolloutTaxi.Message}");
                Assert.True(aircraft.Phases.ForceLanding, "the forced landing must be in force on the rollout");
                Assert.NotNull(aircraft.Phases.TaxiRoute);
            }

            if ((aircraft.Phases.CurrentPhase is RunwayHoldingPhase) && (aircraft.GroundSpeed < 0.5))
            {
                output.WriteLine($"held at t+{t}s: {aircraft.Phases.CurrentPhase.Name}");
                Assert.True(rolloutTaxiSent, "held before the rollout TAXI was sent");
                return new HeldRun(engine, aircraft, layout, runway28R, crossingRunwayId);
            }
        }

        Assert.Fail($"never stopped in the runway hold; final phase {aircraft.Phases.CurrentPhase?.Name ?? "none"}");
        return null;
    }

    /// <summary>The held runway as the controller reads it back.</summary>
    private static string HeldRunway(HeldRun run) => RunwayIdentifier.ToDisplayDesignator(run.CrossingRunwayId);

    /// <summary>The signed cross-track distance (nm) of <paramref name="position"/> from the held runway's centerline.</summary>
    private static double SideOfHeldRunway(HeldRun run, LatLon position)
    {
        RunwayInfo? held = NavigationDatabase.Instance.GetRunway("SFO", HeldRunway(run));
        Assert.NotNull(held);
        return GeoMath.SignedCrossTrackDistanceNm(position, new LatLon(held.ThresholdLatitude, held.ThresholdLongitude), held.TrueHeading);
    }

    private CommandResult Send(HeldRun run, string command)
    {
        CommandResult result = run.Engine.SendCommand("TST001", command);
        string readback = result.PilotReadback is HandlerReadback handler ? handler.Speech.Terminal : "(none)";
        output.WriteLine($"{command}: success={result.Success} message={result.Message} readback={readback}");
        return result;
    }

    /// <summary>
    /// A TAXI that is stored keeps the hold: success, still <see cref="RunwayHoldingPhase"/>, the route on
    /// <see cref="PhaseList.TaxiRoute"/>, the pilot reading back the hold it stays in, and the aircraft at rest for 30 s.
    /// </summary>
    private void AssertStoredAndHolding(HeldRun run, CommandResult result, string command)
    {
        Assert.True(result.Success, $"{command} refused: {result.Message}");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.NotNull(run.Aircraft.Phases.TaxiRoute);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        (string terminal, string tts) = StoredReadback(command);
        Assert.Equal(terminal, readback.Speech.Terminal);
        Assert.Equal(tts, readback.Speech.Tts);
        Assert.Equal(command.StartsWith("TAXIAUTO", StringComparison.Ordinal), result.TaxiRouteReadback is null);

        LatLon rest = run.Aircraft.Position;
        for (int t = 0; t < 30; t++)
        {
            run.Engine.TickOneSecond();
        }

        double driftFt = GeoMath.DistanceNm(rest, run.Aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"after 30 s: phase={run.Aircraft.Phases.CurrentPhase?.Name ?? "none"} drift={driftFt:F1} ft");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases.CurrentPhase);
        Assert.True(driftFt <= HeldDriftFt, $"moved {driftFt:F1} ft while holding");
        Assert.NotNull(run.Aircraft.Phases.TaxiRoute);
    }

    /// <summary>
    /// Ticks until the aircraft taxis its route (a <see cref="TaxiingPhase"/> or a <see cref="HoldingShortPhase"/> on it) past the
    /// held runway onto <see cref="TaxiwayPastHold"/>, at most 400 s, and asserts it crossed the held runway and is on
    /// <see cref="TaxiwayPastHold"/>.
    /// </summary>
    private void AssertCrossesAndTaxisOnE(HeldRun run)
    {
        double startSide = SideOfHeldRunway(run, run.Aircraft.Position);
        for (int t = 0; t < 400; t++)
        {
            run.Engine.TickOneSecond();
            if (
                (run.Aircraft.Phases!.CurrentPhase is TaxiingPhase or HoldingShortPhase)
                && ((startSide * SideOfHeldRunway(run, run.Aircraft.Position)) < 0)
                && (run.Aircraft.Ground.CurrentTaxiway == TaxiwayPastHold)
            )
            {
                break;
            }
        }

        Phase? current = run.Aircraft.Phases!.CurrentPhase;
        double endSide = SideOfHeldRunway(run, run.Aircraft.Position);
        output.WriteLine(
            $"after release: phase={current?.Name ?? "none"} taxiway={run.Aircraft.Ground.CurrentTaxiway ?? "(none)"} "
                + $"side {startSide:F4} -> {endSide:F4}"
        );
        Assert.True(current is TaxiingPhase or HoldingShortPhase, $"not taxiing the route: {current?.Name ?? "none"}");
        Assert.True((startSide * endSide) < 0, "the aircraft did not cross the held runway");
        Assert.Equal(TaxiwayPastHold, run.Aircraft.Ground.CurrentTaxiway);
    }

    [Fact]
    public void TaxiE_FromTheLahsoHold_IsStoredAndKeepsHolding()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold}");
        AssertStoredAndHolding(run, taxi, $"TAXI {TaxiwayPastHold}");
    }

    [Fact]
    public void TaxiE_ThenCross1L_RunsTheStoredRouteAcross1L()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold}");
        AssertStoredAndHolding(run, taxi, $"TAXI {TaxiwayPastHold}");

        CommandResult cross = Send(run, "CROSS 1L");
        Assert.True(cross.Success, $"CROSS 1L refused: {cross.Message}");
        AssertCrossesAndTaxisOnE(run);
    }

    [Fact]
    public void TaxiECross1L_FromTheLahsoHold_ProceedsAtOnce()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold} CROSS 1L");
        Assert.True(taxi.Success, $"TAXI {TaxiwayPastHold} CROSS 1L refused: {taxi.Message}");
        Assert.Null(run.Aircraft.Phases!.LahsoHoldShort);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        Assert.Contains($"cross runway {HeldRunway(run)}", readback.Speech.Terminal);

        run.Engine.TickOneSecond();
        Assert.IsNotType<RunwayHoldingPhase>(run.Aircraft.Phases.CurrentPhase);
        AssertCrossesAndTaxisOnE(run);
    }

    [Fact]
    public void TaxiAuto_FromTheLahsoHold_IsStored()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, "TAXIAUTO @B2");
        AssertStoredAndHolding(run, taxi, "TAXIAUTO @B2");
    }

    [Fact]
    public void Taxi_RouteNotEntering1L_RunsAtOnceAndEndsTheHold()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        string behind = ExitBehindHold(run);
        double startSide = SideOfHeldRunway(run, run.Aircraft.Position);
        CommandResult taxi = Send(run, $"TAXI 28R {behind}");
        Assert.True(taxi.Success, $"TAXI {behind} refused: {taxi.Message}");
        Assert.IsType<TaxiingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Null(run.Aircraft.Phases.TaxiRoute);

        for (int t = 0; t < 120; t++)
        {
            run.Engine.TickOneSecond();
            Assert.True((startSide * SideOfHeldRunway(run, run.Aircraft.Position)) > 0, $"crossed the held runway at t+{t}s");
        }
    }

    /// <summary>
    /// Stopped in the runway hold after the LAHSO landing: the nearest exit behind is a back-taxi, so a bare <c>TAXI</c> to it is
    /// queried ("confirm back-taxi on runway 28R to …?") and the aircraft keeps holding with no route.
    /// </summary>
    [Fact]
    public void BareTaxiBehind_FromTheLahsoHold_QueriesBackTaxiAndKeepsHolding()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        string behind = ExitBehindHold(run);
        CommandResult taxi = Send(run, $"TAXI {behind}");
        Assert.False(taxi.Success, $"a bare TAXI {behind} to an exit behind the hold was accepted");
        Assert.Equal($"confirm back-taxi on runway 28R to {behind}?", taxi.Message);
        Assert.IsType<PilotSpeechText>(taxi.PilotUnable);
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Null(run.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>A second TAXI whose route also enters the held runway replaces the stored one: the new route is stored, the old gone.</summary>
    [Fact]
    public void SecondTaxi_ReplacesTheStoredTaxi()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        AssertStoredAndHolding(run, Send(run, $"TAXI {TaxiwayPastHold}"), $"TAXI {TaxiwayPastHold}");
        TaxiRoute first = run.Aircraft.Phases!.TaxiRoute!;
        Assert.Null(first.DestinationParking);

        AssertStoredAndHolding(run, Send(run, "TAXIAUTO @B2"), "TAXIAUTO @B2");
        Assert.NotSame(first, run.Aircraft.Phases.TaxiRoute);
        Assert.Equal("B2", run.Aircraft.Phases.TaxiRoute!.DestinationParking);
    }

    /// <summary>A second TAXI whose route never enters the held runway runs at once, ends the hold and drops the stored route.</summary>
    [Fact]
    public void SecondTaxiNotEntering_RunsAndDropsTheStore()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        AssertStoredAndHolding(run, Send(run, $"TAXI {TaxiwayPastHold}"), $"TAXI {TaxiwayPastHold}");

        string behind = ExitBehindHold(run);
        CommandResult taxi = Send(run, $"TAXI 28R {behind}");
        Assert.True(taxi.Success, $"TAXI 28R {behind} refused: {taxi.Message}");
        Assert.IsType<TaxiingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Null(run.Aircraft.Phases.TaxiRoute);
        Assert.NotNull(run.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>An EXIT given while a TAXI is stored at the hold replaces it: the stored route is dropped and the exit stored.</summary>
    [Fact]
    public void Exit_ReplacesAStoredTaxiAtTheHold()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        AssertStoredAndHolding(run, Send(run, $"TAXI {TaxiwayPastHold}"), $"TAXI {TaxiwayPastHold}");

        CommandResult exit = Send(run, $"EXIT {TaxiwayPastHold}");
        Assert.True(exit.Success, $"EXIT {TaxiwayPastHold} refused: {exit.Message}");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Null(run.Aircraft.Phases.TaxiRoute);
        Assert.Equal(TaxiwayPastHold, run.Aircraft.Phases.RequestedExit?.Taxiway);
    }

    /// <summary>
    /// A TAXI stored at the hold survives a snapshot round trip: the restored aircraft keeps holding with the route stored, and
    /// <c>CROSS</c> of the held runway then starts the route across it.
    /// </summary>
    [Fact]
    public void StoredTaxi_SurvivesSnapshotAndStartsOnCross()
    {
        HeldRun? live = StageHeld(null);
        if (live is null)
        {
            return;
        }

        AssertStoredAndHolding(live, Send(live, $"TAXI {TaxiwayPastHold}"), $"TAXI {TaxiwayPastHold}");

        live.Engine.RestoreFromSnapshot(live.Engine.CaptureSnapshot());
        AircraftState restored = live.Engine.World.FindAircraft("TST001") ?? throw new Xunit.Sdk.XunitException("the aircraft was not restored");
        HeldRun run = live with { Aircraft = restored };
        Assert.IsType<RunwayHoldingPhase>(restored.Phases?.CurrentPhase);
        Assert.NotNull(restored.Phases!.TaxiRoute);

        for (int t = 0; t < 5; t++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayHoldingPhase>(restored.Phases.CurrentPhase);
        Assert.NotNull(restored.Phases.TaxiRoute);

        CommandResult cross = Send(run, "CROSS 1L");
        Assert.True(cross.Success, $"CROSS 1L refused: {cross.Message}");
        AssertCrossesAndTaxisOnE(run);
    }

    /// <summary>
    /// The entry test on the real SFO 1L: a path that stays in 1L's holding area (between the pavement edge and the hold line)
    /// does not enter the runway; a leg between the holding areas on either side, whose ends are both off the pavement, does.
    /// </summary>
    [Fact]
    public void RouteEntersRunway_HoldingAreaOnlyIsOut_CrossingThePavementIsIn()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        GroundRunway runway = layout.FindRunway("1L") ?? throw new Xunit.Sdk.XunitException("SFO layout has no runway 1L");
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(runway);
        double halfWidthFt = rect.HalfWidthNm * GeoMath.FeetPerNm;
        double holdLineFt = rect.HoldShortNm * GeoMath.FeetPerNm;
        double inHoldingAreaFt = (halfWidthFt + holdLineFt) / 2.0;
        output.WriteLine($"1L half-width {halfWidthFt:F0} ft, hold line {holdLineFt:F0} ft, sample {inHoldingAreaFt:F0} ft off the centreline");
        Assert.True(inHoldingAreaFt > halfWidthFt + 25.0, "the holding area is too narrow to sample");

        LatLon mid = GeoMath.ProjectPoint(new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading, rect.LengthNm / 2.0);
        var right = new TrueHeading((rect.TrueHeading.Degrees + 90.0) % 360.0);
        var left = new TrueHeading((rect.TrueHeading.Degrees + 270.0) % 360.0);
        LatLon rightSide = GeoMath.ProjectPoint(mid, right, inHoldingAreaFt / GeoMath.FeetPerNm);
        LatLon rightSideAlong = GeoMath.ProjectPoint(rightSide, rect.TrueHeading, 300.0 / GeoMath.FeetPerNm);
        LatLon leftSide = GeoMath.ProjectPoint(mid, left, inHoldingAreaFt / GeoMath.FeetPerNm);

        Assert.False(GroundCommandHandler.PolylineEntersRunway(layout, [rightSide, rightSideAlong], "1L"));
        Assert.True(GroundCommandHandler.PolylineEntersRunway(layout, [rightSide, leftSide], "1L"));
    }

    /// <summary>
    /// The pilot readback, terminal and spoken, of each stored TAXI these tests send: the route read back as any taxi route, whose
    /// first stop is E's own hold short of 1L, so the hold already reads and nothing is appended.
    /// </summary>
    private static (string Terminal, string Tts) StoredReadback(string command) =>
        command switch
        {
            $"TAXI {TaxiwayPastHold}" => ("taxi via E, hold short of runway 1L", "taxi via echo, hold short of runway one left"),
            "TAXIAUTO @B2" => ("taxi to gate B2, holding short of runway 1L", "taxi to gate bravo two, holding short of runway one left"),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "no stored readback pinned"),
        };

    /// <summary>
    /// <c>CROSS 1L, TAXI E</c> in one block: the CROSS releases the hold, so the TAXI in the same block is not stored behind a
    /// crossing already cleared; it runs at once across 1L.
    /// </summary>
    [Fact]
    public void CrossThenTaxi_InOneBlock_RunsAtOnceAcross1L()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        CommandResult block = Send(run, $"CROSS 1L, TAXI {TaxiwayPastHold}");
        Assert.True(block.Success, $"CROSS 1L, TAXI {TaxiwayPastHold} refused: {block.Message}");
        Assert.Null(run.Aircraft.Phases!.TaxiRoute);
        Assert.NotNull(run.Aircraft.Ground.AssignedTaxiRoute);
        AssertCrossesAndTaxisOnE(run);
    }

    /// <summary>A TAXI sent after CROSS and before the hold's next tick ends it is not stored: the crossing is cleared; it runs at once.</summary>
    [Fact]
    public void CrossThenTaxi_BeforeTheNextTick_RunsAtOnce()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        Assert.True(Send(run, "CROSS 1L").Success);
        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold}");
        Assert.True(taxi.Success, $"TAXI {TaxiwayPastHold} refused: {taxi.Message}");
        Assert.Null(run.Aircraft.Phases!.TaxiRoute);
        Assert.NotNull(run.Aircraft.Ground.AssignedTaxiRoute);
        AssertCrossesAndTaxisOnE(run);
    }

    /// <summary><c>CROSS 1L EXP</c> starting a TAXI stored at the hold expedites the route, not a runway exit that never runs.</summary>
    [Fact]
    public void CrossExp_AtTheHold_StartsTheStoredRouteExpedited_NoExitExpedite()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        AssertStoredAndHolding(run, Send(run, $"TAXI {TaxiwayPastHold}"), $"TAXI {TaxiwayPastHold}");
        Assert.True(Send(run, "CROSS 1L EXP").Success);
        Assert.IsType<TaxiingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.True(run.Aircraft.Ground.IsExpeditingTaxi);
        Assert.False(run.Aircraft.Ground.IsExpeditingExit);
    }

    /// <summary>A TAXI that replaces an <c>EXIT … EXP</c> stored at the hold drops that exit's expedite with it.</summary>
    [Fact]
    public void StoredExitExp_ReplacedByTaxi_ClearsTheExitExpedite()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        Assert.True(Send(run, $"EXIT {TaxiwayPastHold} EXP").Success);
        Assert.True(run.Aircraft.Ground.IsExpeditingExit);
        AssertStoredAndHolding(run, Send(run, $"TAXI {TaxiwayPastHold}"), $"TAXI {TaxiwayPastHold}");
        Assert.Null(run.Aircraft.Phases!.RequestedExit);
        Assert.False(run.Aircraft.Ground.IsExpeditingExit);
    }

    /// <summary><c>CROSS 1L EXP</c> at the hold clears no bar of the stored route, so it marks none of them expedited.</summary>
    [Fact]
    public void CrossExp_AtTheHold_MarksNoStoredBarExpedited()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        AssertStoredAndHolding(run, Send(run, $"TAXI {TaxiwayPastHold}"), $"TAXI {TaxiwayPastHold}");
        TaxiRoute stored = run.Aircraft.Phases!.TaxiRoute!;
        Assert.True(Send(run, "CROSS 1L EXP").Success);
        Assert.All(stored.HoldShortPoints, hs => Assert.False(hs.IsExpedited, $"bar {hs.TargetName}@{hs.NodeId} marked expedited"));
    }

    /// <summary>
    /// A TAXI given on a forced (<c>CLANDF</c>) LAHSO rollout (<c>TAXI V</c>) is stored behind the rollout's implied exit, and the
    /// aircraft still stops at the LAHSO point with it stored. That route starts at the exit's hold-short node, not at the
    /// aircraft, so <c>CROSS 1L</c> releases the hold into the runway exit with the route still stored, and nothing installs it
    /// from where the aircraft stands. Known gap, booked separately: the runway exit then drops the route ("unable V") instead of
    /// joining it, so this test asserts nothing past the release.
    /// </summary>
    [Fact]
    public void RolloutStoredTaxi_ThenLahsoHoldCross_IsLeftToTheRunwayExit()
    {
        HeldRun? run = StageHeld(forcedRolloutTaxi: $"TAXI {RolloutTaxiway}");
        if (run is null)
        {
            return;
        }

        TaxiRoute stored = Assert.IsType<TaxiRoute>(run.Aircraft.Phases!.TaxiRoute);
        output.WriteLine($"stored at the hold: {stored.ToSummary()}");

        CommandResult cross = Send(run, "CROSS 1L");
        Assert.True(cross.Success, $"CROSS 1L refused: {cross.Message}");
        Assert.Null(run.Aircraft.Phases.LahsoHoldShort);
        for (int t = 0; (t < 5) && (run.Aircraft.Phases.CurrentPhase is RunwayHoldingPhase); t++)
        {
            run.Engine.TickOneSecond();
        }

        Assert.IsType<RunwayExitPhase>(run.Aircraft.Phases.CurrentPhase);
        Assert.Same(stored, run.Aircraft.Phases.TaxiRoute);
        Assert.Null(run.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// <c>TAXI E HS 1R</c> from the LAHSO hold: the route holds short of 1L (its first uncleared bar) and then of 1R. The route
    /// readback stops at that first bar, so it already says the hold the aircraft stays in, and 1L is named once.
    /// </summary>
    [Fact]
    public void TaxiAcross1LThen1R_ReadsOneHoldShortOf1L()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold} HS 1R");
        Assert.True(taxi.Success, $"TAXI {TaxiwayPastHold} HS 1R refused: {taxi.Message}");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        TaxiRoute stored = Assert.IsType<TaxiRoute>(run.Aircraft.Phases.TaxiRoute);
        List<HoldShortPoint> uncleared = [.. stored.HoldShortPoints.Where(static hs => !hs.IsCleared)];
        Assert.True(uncleared.Count >= 2, "the route must hold short of 1L and then of 1R");
        Assert.True(HoldShortAnnotator.TargetMatches(uncleared[0].TargetName, "1L"), $"first hold short is {uncleared[0].TargetName}");
        Assert.True(HoldShortAnnotator.TargetMatches(uncleared[1].TargetName, "1R"), $"second hold short is {uncleared[1].TargetName}");

        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        Assert.Equal("taxi via E, hold short of runway 1L", readback.Speech.Terminal);
        Assert.Equal("taxi via echo, hold short of runway one left", readback.Speech.Tts);
    }

    /// <summary>
    /// <c>TAXI E HS 1L</c> from the LAHSO hold: the explicit hold short re-arms the route's 1L crossing bar, which the route
    /// readback stops at, so the readback already says the hold the aircraft stays in, and 1L is named once.
    /// </summary>
    [Fact]
    public void TaxiEHs1L_FromTheLahsoHold_ReadsOneHoldShortOf1L()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        CommandResult taxi = Send(run, $"TAXI {TaxiwayPastHold} HS 1L");
        Assert.True(taxi.Success, $"TAXI {TaxiwayPastHold} HS 1L refused: {taxi.Message}");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);

        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        Assert.Equal("taxi via E, hold short of runway 1L", readback.Speech.Terminal);
        Assert.Equal("taxi via echo, hold short of runway one left", readback.Speech.Tts);
    }

    /// <summary>
    /// <c>TAXI E HS V V HS 1R</c> from the LAHSO hold: the route's first stop is the hold short of taxiway V, before its 1L
    /// crossing, so the route readback stops there without saying the runway hold, and the hold the aircraft stays in follows it.
    /// </summary>
    [Fact]
    public void TaxiHoldingShortOfATaxiwayBefore1L_KeepsTheHoldSuffix()
    {
        HeldRun? run = StageHeld(null);
        if (run is null)
        {
            return;
        }

        const string command = "TAXI E HS V V HS 1R";
        CommandResult taxi = Send(run, command);
        Assert.True(taxi.Success, $"{command} refused: {taxi.Message}");
        Assert.IsType<RunwayHoldingPhase>(run.Aircraft.Phases!.CurrentPhase);
        TaxiRoute stored = Assert.IsType<TaxiRoute>(run.Aircraft.Phases.TaxiRoute);
        List<HoldShortPoint> uncleared = [.. stored.HoldShortPoints.Where(static hs => !hs.IsCleared)];
        Assert.Equal("V", uncleared[0].TargetName);
        Assert.Contains(uncleared.Skip(1), hs => HoldShortAnnotator.TargetMatches(hs.TargetName, "1L"));

        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        Assert.Equal("taxi via E, hold short of taxiway V, holding short of runway 1L", readback.Speech.Terminal);
        Assert.Equal("taxi via echo, hold short of taxiway victor, holding short of runway one left", readback.Speech.Tts);
    }

    /// <summary>The taxiway the forced-rollout TAXI names, the route's own turn-off past the LAHSO point.</summary>
    private const string RolloutTaxiway = "V";

    /// <summary>The ground speed (kt) the forced rollout has slowed to when its TAXI is sent, the exits before the hold behind it.</summary>
    private const double RolloutTaxiSpeedKts = 30;

    /// <summary>The nearest 28R exit behind the held aircraft (toward the landing threshold). Prints every candidate.</summary>
    private string ExitBehindHold(HeldRun run)
    {
        LatLon threshold = LandingThreshold.Resolve(run.Runway, run.Layout);
        double heldFt = GeoMath.AlongTrackDistanceNm(run.Aircraft.Position, threshold, run.Runway.TrueHeading) * GeoMath.FeetPerNm;
        output.WriteLine($"held {heldFt:F0} ft from the 28R threshold");

        var candidates = new List<(string Taxiway, double AlongFt)>();
        foreach (GroundNode node in run.Layout.Nodes.Values)
        {
            if ((node.Type != GroundNodeType.RunwayHoldShort) || (node.RunwayId is not { } id) || !id.Contains("28R"))
            {
                continue;
            }

            string? taxiway = node.Edges.Select(e => e.TaxiwayName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            if (taxiway is null)
            {
                continue;
            }

            double alongFt = GeoMath.AlongTrackDistanceNm(node.Position, threshold, run.Runway.TrueHeading) * GeoMath.FeetPerNm;
            output.WriteLine($"  candidate {taxiway} node #{node.Id} at {alongFt:F0} ft");
            candidates.Add((taxiway, alongFt));
        }

        (string Taxiway, double AlongFt) behind = candidates.Where(c => c.AlongFt < heldFt).MaxBy(c => c.AlongFt);
        Assert.NotNull(behind.Taxiway);
        return behind.Taxiway;
    }
}
