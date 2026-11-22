using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// Readers that run outside a dispatch (tick-time checks, the pilot, the queue's triggers, the visual-detection pass) judge an
/// aircraft held behind a Class B boundary hold by the phase it holds (<see cref="PhaseList.EffectivePhase"/>), and the list's
/// own mutators refuse a standing pair outside a lift. Pairs are built with <see cref="PhaseList.InsertHoldInFront"/>; the
/// pattern cases fly a VFR M20P on the LVK 25R right downwind held for the SFO Class B, on real navdata.
/// </summary>
public sealed class HeldReaderTests : IDisposable
{
    private const string Sfo = "SFO";

    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navScope;

    public HeldReaderTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    [Fact]
    public void EffectiveIndex_WithNoPair_IsTheCurrentIndex()
    {
        var final = new FinalApproachPhase { Status = PhaseStatus.Active };
        var list = new PhaseList();
        list.Add(final);

        Assert.Equal(list.CurrentIndex, list.EffectiveIndex);
        Assert.Same(final, list.Phases[list.EffectiveIndex]);
    }

    [Fact]
    public void EffectiveIndex_WithAPair_IsTheHeldPhasesIndex()
    {
        var final = new FinalApproachPhase();
        PhaseList list = PairOver(final);

        Assert.Equal(list.CurrentIndex + 1, list.EffectiveIndex);
        Assert.Same(final, list.Phases[list.EffectiveIndex]);
    }

    [Fact]
    public void EffectiveIndex_MidLift_IsTheCurrentIndex_OnTheHeldPhase()
    {
        var final = new FinalApproachPhase();
        PhaseList list = PairOver(final);

        HoldLift lift = Assert.IsType<HoldLift>(list.LiftHold());
        Assert.Equal(list.CurrentIndex, list.EffectiveIndex);
        Assert.Same(final, list.Phases[list.EffectiveIndex]);
        Assert.True(list.PutBackHold(lift));
    }

    [Fact]
    public void IsEstablishedOnApproach_WithAHeldFinal_IsTrue() => Assert.True(PairOver(new FinalApproachPhase()).IsEstablishedOnApproach());

    [Fact]
    public void IsEstablishedOnApproach_WithAHeldGoAround_IsFalse() => Assert.False(PairOver(new GoAroundPhase()).IsEstablishedOnApproach());

    [Fact]
    public void AfterCycleTerminator_LatchedBlock_DoesNotFireWhileAGoAroundIsHeld()
    {
        AircraftState ac = Airborne(PairOver(new GoAroundPhase()));
        var block = new CommandBlock
        {
            Trigger = new BlockTrigger { Type = BlockTriggerType.AfterCycleTerminator },
            TriggerTerminatorObserved = true,
        };

        Assert.False(FlightPhysics.IsAfterCycleTerminatorMet(ac, block), "a held go-around is still the terminator, not the climb-out after it");
    }

    [Fact]
    public void AfterCycleTerminator_AHeldGoAround_SetsTheLatch()
    {
        AircraftState ac = Airborne(PairOver(new GoAroundPhase()));
        var block = new CommandBlock { Trigger = new BlockTrigger { Type = BlockTriggerType.AfterCycleTerminator } };
        Assert.False(block.TriggerTerminatorObserved);

        Assert.False(FlightPhysics.IsAfterCycleTerminatorMet(ac, block));

        Assert.True(block.TriggerTerminatorObserved, "the held go-around is the terminator the block waits behind");
    }

    [Fact]
    public void VisualPass_FieldLostOnAHeldFinal_GoesAroundAsAnUnheldFinalDoes()
    {
        SimulationEngine engine = BuildOakEngine();
        AircraftState ac = EstablishOakCvaWithFieldInSight(engine);
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases!.CurrentPhase);
        AirspaceBoundaryHoldPhase hold = InsertStartedHold(ac);
        MoveToShortFinal(ac);
        engine.World.Weather = OakWeather("1SM");

        engine.TickVisualDetection();

        AssertWentAround(ac, final, hold);
    }

    [Fact]
    public void VisualPass_FieldLostOnAHeldBase_GoesAroundAsAnUnheldBaseDoes()
    {
        SimulationEngine engine = BuildOakEngine();
        AircraftState ac = EstablishOakCvaWithFieldInSight(engine);
        BasePhase basePhase = ReplaceCurrentWith(ac, new BasePhase());
        AirspaceBoundaryHoldPhase hold = InsertStartedHold(ac);
        MoveToShortFinal(ac);
        engine.World.Weather = OakWeather("1SM");

        engine.TickVisualDetection();

        AssertWentAround(ac, basePhase, hold);
    }

    [Fact]
    public void VisualPass_FieldLostNotCommitted_WithAPair_ClearsBothPhases_EachEndedOnce()
    {
        SimulationEngine engine = BuildOakEngine();
        AircraftState ac = EstablishOakCvaWithFieldInSight(engine);
        LifecycleProbePhase probe = ReplaceCurrentWith(ac, new LifecycleProbePhase());
        AirspaceBoundaryHoldPhase hold = InsertStartedHold(ac);
        engine.World.Weather = OakWeather("2SM");
        ac.PendingWarnings.Clear();

        engine.TickVisualDetection();

        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Equal([PhaseStatus.Skipped], probe.Ends);
        Assert.Equal(PhaseStatus.Skipped, probe.Status);
        AssertHoldDiscarded(ac, hold);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("unable the visual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VisualPass_BackstopOnAHeldFinal_GoesAroundAsAnUnheldFinalDoes()
    {
        SimulationEngine engine = BuildOakEngine();
        AircraftState ac = EstablishOakCvaWithFieldInSight(engine);
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases!.CurrentPhase);
        AirspaceBoundaryHoldPhase hold = InsertStartedHold(ac);
        MoveToShortFinal(ac);
        // Nothing in sight and no loss this pass: only the backstop sees it.
        ac.Approach.HasReportedFieldInSight = false;
        engine.World.Weather = OakWeather("1SM");

        engine.TickVisualDetection();

        AssertWentAround(ac, final, hold);
    }

    [Fact]
    public void VisualPass_TrafficLostOnAHeldFollower_EndsTheVisual_AndDiscardsTheHold()
    {
        SimulationEngine engine = BuildOakEngine();
        AircraftState trailer = EstablishOakFollowWithFieldNeverHeld(engine);
        Phase held = trailer.Phases!.CurrentPhase!;
        AirspaceBoundaryHoldPhase hold = InsertStartedHold(trailer);
        engine.World.Weather = OakWeather("2SM");
        trailer.PendingWarnings.Clear();

        engine.TickVisualDetection();

        Assert.Null(trailer.Approach.FollowingCallsign);
        Assert.Null(trailer.Phases?.ActiveApproach);
        Assert.Null(trailer.Phases?.CurrentPhase);
        Assert.Equal(PhaseStatus.Skipped, held.Status);
        AssertHoldDiscarded(trailer, hold);
        Assert.Contains(trailer.PendingWarnings, w => w.Contains("unable the visual", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ReplaceUpcoming")]
    [InlineData("InsertAfterCurrent")]
    [InlineData("InsertAfterCurrentRange")]
    [InlineData("AdvanceToNext")]
    public void GuardedMutator_WithAPairOutsideALift_Throws(string op)
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        int count = ac.Phases!.Phases.Count;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => RunMutator(ac, op));

        Assert.Contains(op.Replace("Range", "", StringComparison.Ordinal), ex.Message, StringComparison.Ordinal);
        Assert.Equal(count, ac.Phases.Phases.Count);
        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Same(downwind, ac.Phases.HeldPhase);
    }

    [Theory]
    [InlineData("ReplaceUpcoming")]
    [InlineData("InsertAfterCurrent")]
    [InlineData("InsertAfterCurrentRange")]
    [InlineData("AdvanceToNext")]
    public void GuardedMutator_InsideALift_ActsOnTheHeldPhase(string op)
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        HoldLift? lift = CommandDispatcher.BeginHoldLift(ac);
        Assert.NotNull(lift);
        try
        {
            RunMutator(ac, op);
        }
        finally
        {
            CommandDispatcher.EndHoldLift(ac, lift);
        }

        Assert.Null(ac.LiftedBoundaryHold);
        PhaseList list = ac.Phases!;
        if (op == "AdvanceToNext")
        {
            Assert.Equal(PhaseStatus.Completed, downwind.Status);
            Assert.IsType<BasePhase>(list.CurrentPhase);
            AssertHoldDiscarded(ac, hold);
            return;
        }

        Assert.Same(downwind, list.HeldPhase);
        Phase? after = list.Phases.ElementAtOrDefault(list.EffectiveIndex + 1);
        Assert.True((op == "ReplaceUpcoming") ? (after is null) : (after is GoAroundPhase), $"{op}: phase after the held one is {after?.Name}");
    }

    [Fact]
    public void BravoCapScope_WithAHeldPatternLeg_Applies()
    {
        (AircraftState ac, _, _) = HeldDownwind();

        Assert.True(BravoCapScope.Applies(ac, AirspaceDatabase.Default));
    }

    [Fact]
    public void BravoCapScope_WithAFreeFlightHold_DoesNotApply()
    {
        AircraftState ac = VfrAircraft();
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        hold.Status = PhaseStatus.Active;
        ac.Phases = new PhaseList();
        ac.Phases.Add(hold);

        Assert.False(BravoCapScope.Applies(ac, AirspaceDatabase.Default));
    }

    [Fact]
    public void IsOnFinal_WithAHeldFinal_IsTrue()
    {
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        AircraftState ac = Airborne(PairOver(new FinalApproachPhase()));
        ac.Position = PointOnFinal(rwy, 5.0);
        ac.TrueTrack = new TrueHeading((rwy.TrueHeading.Degrees + 180) % 360);

        Assert.True(ApproachCommandHandler.IsOnFinal(ac, rwy, null));
    }

    [Fact]
    public void IsInboundToLand_WithAHeldGoAround_IsFalse()
    {
        PhaseList list = PairOver(new GoAroundPhase());
        list.LandingClearance = ClearanceType.ClearedToLand;

        Assert.False(ApproachCommandHandler.IsInboundToLand(Airborne(list)));
    }

    [Fact]
    public void IsInboundToLand_WithAHeldFinal_IsTrue() =>
        Assert.True(ApproachCommandHandler.IsInboundToLand(Airborne(PairOver(new FinalApproachPhase()))));

    [Fact]
    public void AirborneCheckIn_WithAHeldPatternLeg_IsNotMade()
    {
        (AircraftState ac, _, _) = HeldDownwind();
        ac.HasMadeInitialContact = false;
        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "KLVK",
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };

        PilotProactive.TickAirborneCheckIn(ac, scenario, AirspaceDatabase.Default, _ => new LatLon(37.6934, -121.8204));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void ExtendReadback_OnAHeldDownwind_NamesTheDownwind()
    {
        (AircraftState ac, _, _) = HeldDownwind();
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("EXT");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        PilotSpeechText? readback = PilotResponder.BuildReadback(parsed.Value!, ac, ActiveRunways.Empty);

        _output.WriteLine(readback?.Terminal ?? "(none)");
        Assert.Contains("extend downwind", readback?.Terminal ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static void RunMutator(AircraftState ac, string op)
    {
        PhaseList list = ac.Phases!;
        switch (op)
        {
            case "ReplaceUpcoming":
                list.ReplaceUpcoming([]);
                break;
            case "InsertAfterCurrent":
                list.InsertAfterCurrent(new GoAroundPhase());
                break;
            case "InsertAfterCurrentRange":
                IEnumerable<Phase> range = [new GoAroundPhase()];
                list.InsertAfterCurrent(range);
                break;
            case "AdvanceToNext":
                list.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
                break;
            default:
                throw new ArgumentException($"No such guarded mutator: {op}", nameof(op));
        }
    }

    /// <summary>A list whose current phase is a Pending Orbit hold standing in front of <paramref name="held"/>, Active.</summary>
    private static PhaseList PairOver(Phase held)
    {
        held.Status = PhaseStatus.Active;
        var list = new PhaseList();
        list.Add(held);
        list.InsertHoldInFront(SfoBravoHold());
        Assert.Same(held, list.HeldPhase);
        return list;
    }

    private static AircraftState Airborne(PhaseList list)
    {
        AircraftState ac = VfrAircraft();
        ac.Phases = list;
        return ac;
    }

    private static AirspaceBoundaryHoldPhase InsertStartedHold(AircraftState ac)
    {
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        // Started by hand, as the pending-start path would, so no tick of the hold's own exit test runs.
        hold.Status = PhaseStatus.Active;
        hold.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        Assert.NotNull(ac.Phases.HeldPhase);
        return hold;
    }

    private static (AircraftState Aircraft, DownwindPhase Downwind, AirspaceBoundaryHoldPhase Hold) HeldDownwind()
    {
        AircraftState ac = OnDownwind(out DownwindPhase downwind);
        AirspaceBoundaryHoldPhase hold = InsertStartedHold(ac);
        Assert.Same(downwind, ac.Phases!.HeldPhase);
        return (ac, downwind, hold);
    }

    private static AircraftState OnDownwind(out DownwindPhase downwind)
    {
        AircraftState ac = VfrAircraft();
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("ERD 25R");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1), validateDctFixes: false));
        Assert.True(result.Success, result.Message);
        while ((ac.Phases?.CurrentPhase is not DownwindPhase) && (ac.Phases?.CurrentPhase is not null))
        {
            ac.Phases.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        }

        downwind = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        if (downwind.Status == PhaseStatus.Pending)
        {
            downwind.Status = PhaseStatus.Active;
            downwind.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        }

        ac.PendingWarnings.Clear();
        ac.PendingPilotTransmissions.Clear();
        return ac;
    }

    /// <summary>North of LVK on the 25R right downwind, eastbound at pattern altitude, east of the SFO Class B.</summary>
    private static AircraftState VfrAircraft() =>
        new()
        {
            Callsign = "N435C",
            AircraftType = "M20P",
            Position = new LatLon(37.71, -121.80),
            TrueHeading = new TrueHeading(70),
            TrueTrack = new TrueHeading(70),
            Altitude = 1400,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KLVK",
            },
        };

    private static AirspaceBoundaryHoldPhase SfoBravoHold() =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = Sfo,
            NameText = Sfo,
            ReferencePosition = new LatLon(37.6213, -122.3790),
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
        };

    private static LatLon PointOnFinal(RunwayInfo rwy, double distanceNm)
    {
        double reciprocal = (rwy.TrueHeading.Degrees + 180) % 360;
        (double lat, double lon) = GeoMath.ProjectPointRaw(rwy.ThresholdLatitude, rwy.ThresholdLongitude, reciprocal, distanceNm);
        return new LatLon(lat, lon);
    }

    private static SimulationEngine BuildOakEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-held-visual-lost-reference",
                ScenarioName = "Held Visual Lost Reference",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

    /// <summary>An IFR B738 six miles out on the OAK 30 final at 2000 ft, cleared for the visual with the field in sight.</summary>
    private static AircraftState EstablishOakCvaWithFieldInSight(SimulationEngine engine)
    {
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        var ac = new AircraftState
        {
            Callsign = "FLD1",
            AircraftType = "B738",
            Position = PointOnFinal(rwy, 6.0),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = 2000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(2000),
            },
        };
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand("FLD1", "RFIS").Success);
        Assert.True(engine.SendCommand("FLD1", "CVA 30").Success);
        for (int t = 1; (t <= 15) && !ac.Approach.HasReportedFieldInSight; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(ac.Approach.HasReportedFieldInSight, "field should be in sight in clear weather on final");
        return ac;
    }

    /// <summary>
    /// TRAIL1 15 miles out on the OAK 30 final, cleared for the visual following LEAD1 four miles ahead, with the traffic in
    /// sight and the field never reported (5SM caps field acquisition short of the trailer).
    /// </summary>
    private static AircraftState EstablishOakFollowWithFieldNeverHeld(SimulationEngine engine)
    {
        RunwayInfo rwy = NavigationDatabase.Instance.GetRunway("OAK", "30")!;
        engine.World.Weather = OakWeather("5SM");
        AircraftState leader = OakIfrB738("LEAD1", PointOnFinal(rwy, 11.0), rwy.TrueHeading, 3000);
        AircraftState trailer = OakIfrB738("TRAIL1", PointOnFinal(rwy, 15.0), rwy.TrueHeading, 3500);
        engine.World.AddAircraft(leader);
        engine.World.AddAircraft(trailer);
        Assert.True(engine.SendCommand("LEAD1", "RFISF").Success);
        Assert.True(engine.SendCommand("LEAD1", "CVA 30").Success);
        Assert.True(engine.SendCommand("TRAIL1", "RTIS LEAD1").Success);
        TickUntilTrafficInSight(engine, trailer);
        Assert.True(engine.SendCommand("TRAIL1", "CVA 30 FOLLOW LEAD1").Success);
        TickUntilTrafficInSight(engine, trailer);
        Assert.False(trailer.Approach.HasReportedFieldInSight, "setup: the field must never have been reported");
        Assert.Equal(PhaseStatus.Active, trailer.Phases?.CurrentPhase?.Status);
        return trailer;
    }

    private static void TickUntilTrafficInSight(SimulationEngine engine, AircraftState ac)
    {
        for (int t = 1; (t <= 15) && !ac.Approach.HasReportedTrafficInSight; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(ac.Approach.HasReportedTrafficInSight);
    }

    private static AircraftState OakIfrB738(string callsign, LatLon position, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr((int)altitude),
            },
        };

    private static WeatherProfile OakWeather(string visibility) => new() { Metars = [$"KOAK 121853Z 27012KT {visibility} CLR 20/12 A2992"] };

    /// <summary>2.5 miles out on the OAK 30 final at 800 ft: inside the committed height of a visual.</summary>
    private static void MoveToShortFinal(AircraftState ac)
    {
        ac.Position = PointOnFinal(NavigationDatabase.Instance.GetRunway("OAK", "30")!, 2.5);
        ac.Altitude = 800;
    }

    /// <summary>Puts <paramref name="phase"/>, Active, in the current phase's place, the kind the test holds.</summary>
    private static T ReplaceCurrentWith<T>(AircraftState ac, T phase)
        where T : Phase
    {
        PhaseList list = ac.Phases!;
        phase.Status = PhaseStatus.Active;
        list.Phases[list.CurrentIndex] = phase;
        return phase;
    }

    private static void AssertWentAround(AircraftState ac, Phase held, AirspaceBoundaryHoldPhase hold)
    {
        Assert.True(ac.Phases?.CurrentPhase is GoAroundPhase, $"expected GoAroundPhase, got {ac.Phases?.CurrentPhase?.Name ?? "no phase"}");
        Assert.Equal(PhaseStatus.Completed, held.Status);
        Assert.Null(ac.Phases!.ActiveApproach);
        AssertHoldDiscarded(ac, hold);
    }

    private static void AssertHoldDiscarded(AircraftState ac, AirspaceBoundaryHoldPhase hold)
    {
        Assert.Equal(PhaseStatus.Skipped, hold.Status);
        Assert.DoesNotContain(hold, ac.Phases?.Phases ?? []);
        Assert.Null(ac.Phases?.HeldPhase);
        Assert.Null(ac.LiftedBoundaryHold);
    }

    /// <summary>Counts its own lifecycle hooks, so a test can assert each ran exactly once.</summary>
    private sealed class LifecycleProbePhase : Phase
    {
        public List<PhaseStatus> Ends { get; } = [];

        public override string Name => "LifecycleProbe";

        public override PhaseDto ToSnapshot() => throw new NotSupportedException();

        public override void OnStart(PhaseContext ctx) { }

        public override bool OnTick(PhaseContext ctx) => false;

        public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus) => Ends.Add(endStatus);
    }
}
