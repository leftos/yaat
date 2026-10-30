using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Simulation.ApproachGoAroundAndWaitTests;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// CM, DM and a forced altitude on an instrument approach: outside the final approach fix the approach is kept and the altitude
/// is held until established (glideslope capture, or the next published fix), then the published altitudes resume; inside the
/// final approach fix the approach is cancelled. Real KOAK, KCCR and KACV navdata.
/// </summary>
public class ApproachAltitudeHoldTests(ITestOutputHelper output)
{
    // ─── The flag survives a snapshot ───

    private static ApproachClearance Clearance(bool held) =>
        new()
        {
            TransitionName = null,
            ApproachId = "I28R",
            AirportCode = "KOAK",
            RunwayId = "28R",
            FinalApproachCourse = new TrueHeading(295.0),
            AltitudeHeldUntilEstablished = held,
        };

    [Fact]
    public void HeldFlag_RoundTripsThroughSnapshot()
    {
        ApproachClearanceDto dto = Clearance(held: true).ToSnapshot();
        string json = JsonSerializer.Serialize(dto);
        output.WriteLine(json);

        var restored = ApproachClearance.FromSnapshot(JsonSerializer.Deserialize<ApproachClearanceDto>(json)!);

        Assert.True(restored.AltitudeHeldUntilEstablished);
    }

    /// <summary>An unheld clearance writes no field, and an older snapshot without it restores unheld.</summary>
    [Fact]
    public void UnheldFlag_IsOmittedAndRestoresFalse()
    {
        string json = JsonSerializer.Serialize(Clearance(held: false).ToSnapshot());

        Assert.DoesNotContain(nameof(ApproachClearanceDto.AltitudeHeldUntilEstablished), json, StringComparison.Ordinal);
        Assert.False(ApproachClearance.FromSnapshot(JsonSerializer.Deserialize<ApproachClearanceDto>(json)!).AltitudeHeldUntilEstablished);
    }

    // ─── Dispatch: inside the FAF cancels, outside keeps and holds ───

    /// <summary>The altitude instruction under test.</summary>
    public enum AltitudeVerb
    {
        Climb,
        Descend,
        Force,
    }

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
    }

    private static DispatchContext Context() => TestDispatch.Context(new SerializableRandom(42));

    /// <summary>A climb 1,000 ft above the aircraft, a descent 300 ft below it, or a forced altitude 1,000 ft above it.</summary>
    private static ParsedCommand Instruction(AltitudeVerb verb, AircraftState ac, out int altitude)
    {
        int current = (int)Math.Round(ac.Altitude);
        altitude = verb == AltitudeVerb.Descend ? current - 300 : current + 1000;
        return verb switch
        {
            AltitudeVerb.Climb => new ClimbMaintainCommand(altitude),
            AltitudeVerb.Descend => new DescendMaintainCommand(altitude),
            AltitudeVerb.Force => new ForceAltitudeCommand(altitude),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "no command for this verb"),
        };
    }

    private CommandResult Dispatch(AircraftState ac, ParsedCommand command)
    {
        CommandResult result = CommandDispatcher.DispatchCompound(new CompoundCommand([new ParsedBlock(null, [command])]), ac, Context());
        output.WriteLine(
            $"{CommandDescriber.DescribeCommand(command)} on {ac.Phases?.CurrentPhase?.GetType().Name}: success={result.Success} — {result.Message}"
        );
        return result;
    }

    private static bool InsideFaf(AircraftState ac) => FinalApproachFix.IsInsidePublishedFaf(ac, ac.Phases!.AssignedRunway!, null);

    /// <summary>The aircraft moved onto the KOAK 28R extended centerline, <paramref name="distanceNm"/> from the threshold.</summary>
    private static AircraftState OnCenterline(AircraftState ac, double distanceNm)
    {
        RunwayInfo rwy = Oak28R();
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), distanceNm);
        return ac;
    }

    public enum InsideLeg
    {
        InterceptCourse,
        ApproachNavigation,
        FinalApproach,
    }

    private AircraftState InsideTheFaf(InsideLeg leg)
    {
        SimulationEngine engine = BuildEngine();
        return leg switch
        {
            InsideLeg.InterceptCourse => OnCenterline(OnLeg(engine, ApproachLeg.InterceptCourse), 3.0),
            InsideLeg.ApproachNavigation => OnCenterline(OnLeg(engine, ApproachLeg.ApproachNavigation), 2.5),
            InsideLeg.FinalApproach => OnLeg(engine, ApproachLeg.FinalInsideFaf),
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "no setup for this leg"),
        };
    }

    public static TheoryData<InsideLeg, AltitudeVerb> EveryVerbInsideTheFaf()
    {
        var data = new TheoryData<InsideLeg, AltitudeVerb>();
        foreach (InsideLeg leg in Enum.GetValues<InsideLeg>())
        {
            foreach (AltitudeVerb verb in Enum.GetValues<AltitudeVerb>())
            {
                data.Add(leg, verb);
            }
        }

        return data;
    }

    /// <summary>Inside the final approach fix an altitude instruction cancels the approach.</summary>
    [Theory]
    [MemberData(nameof(EveryVerbInsideTheFaf))]
    public void AltitudeInstruction_InsideTheFaf_CancelsTheApproach(InsideLeg leg, AltitudeVerb verb)
    {
        AircraftState ac = InsideTheFaf(leg);
        Assert.True(InsideFaf(ac));

        CommandResult result = Dispatch(ac, Instruction(verb, ac, out int altitude));

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
        Assert.Equal(altitude, ac.Targets.AssignedAltitude);
    }

    public static TheoryData<ApproachLeg, AltitudeVerb> EveryVerbOutsideTheFaf()
    {
        var data = new TheoryData<ApproachLeg, AltitudeVerb>();
        foreach (
            ApproachLeg leg in new[]
            {
                ApproachLeg.InterceptCourse,
                ApproachLeg.ApproachNavigation,
                ApproachLeg.ProcedureTurn,
                ApproachLeg.HoldInLieu,
                ApproachLeg.FinalOutsideFaf,
            }
        )
        {
            foreach (AltitudeVerb verb in Enum.GetValues<AltitudeVerb>())
            {
                data.Add(leg, verb);
            }
        }

        return data;
    }

    /// <summary>Outside the final approach fix an altitude instruction keeps the approach and is held until established.</summary>
    [Theory]
    [MemberData(nameof(EveryVerbOutsideTheFaf))]
    public void AltitudeInstruction_OutsideTheFaf_KeepsTheApproachAndHolds(ApproachLeg leg, AltitudeVerb verb)
    {
        AircraftState ac = OnLeg(BuildEngine(), leg);
        PhaseList phases = ac.Phases!;
        Phase current = phases.CurrentPhase!;
        ApproachClearance approach = phases.ActiveApproach!;
        Assert.False(InsideFaf(ac));

        CommandResult result = Dispatch(ac, Instruction(verb, ac, out int altitude));

        Assert.True(result.Success, result.Message);
        Assert.Same(phases, ac.Phases);
        Assert.Same(current, ac.Phases!.CurrentPhase);
        Assert.Same(approach, ac.Phases.ActiveApproach);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
        Assert.True(approach.AltitudeHeldUntilEstablished);
        Assert.Equal(altitude, ac.Targets.AssignedAltitude);
    }

    // ─── With no approach clearance the phase's own table decides ───

    /// <summary>An ordinary HOLD (no approach clearance): a CM is the hold's additive adjustment and the hold flies on.</summary>
    [Fact]
    public void OrdinaryHold_ClimbMaintain_KeepsTheHold()
    {
        SimulationEngine engine = BuildEngine();
        LatLon oak = FixPosition("OAK");
        var ac = new AircraftState
        {
            Callsign = "HOLD1",
            AircraftType = "C172",
            Position = GeoMath.ProjectPoint(oak, new TrueHeading(90.0), 4.0),
            TrueHeading = new TrueHeading(270.0),
            TrueTrack = new TrueHeading(270.0),
            Altitude = 6000,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            Declination = 13.0,
        };
        engine.World.AddAircraft(ac);
        ac.Phases = new PhaseList();
        var hold = new HoldingPatternPhase
        {
            FixName = "OAK",
            FixLat = oak.Lat,
            FixLon = oak.Lon,
            InboundCourse = 270,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
        };
        ac.Phases.Add(hold);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        Assert.Null(ac.Phases.ActiveApproach);

        CommandResult result = Dispatch(ac, new ClimbMaintainCommand(8000));

        Assert.True(result.Success, result.Message);
        Assert.Same(hold, ac.Phases?.CurrentPhase);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
        Assert.Equal(8000, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A final with no approach clearance (an aircraft spawned on final): a CM applies and the final's own table decides, as on
    /// base: <see cref="FinalApproachPhase.CanAcceptCommand"/> clears the final for a climb.
    /// </summary>
    [Fact]
    public void FinalWithoutApproachClearance_ClimbMaintain_AppliesAsOnBase()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = FollowSequenceRefusalTests.AddApproachFollower(engine, 8.0);
        ac.Phases!.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        final.DistanceToThresholdNm = 8.0;
        Assert.Null(ac.Phases.ActiveApproach);
        Assert.True(final.CanAcceptCommand(CanonicalCommandType.ClimbMaintain).ClearsThePhase);

        CommandResult result = Dispatch(ac, new ClimbMaintainCommand(3000));

        Assert.True(result.Success, result.Message);
        Assert.Equal(3000, ac.Targets.AssignedAltitude);
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
    }

    // ─── A queued or conditional altitude decides when it fires ───

    /// <summary>Queues <paramref name="command"/> behind an AT OAK condition and fires its block, as the trigger would.</summary>
    private CommandResult FireConditional(AircraftState ac, string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound($"AT OAK {command}");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.True(CommandDispatcher.DispatchCompound(parsed.Value!, ac, Context()).Success);
        CommandBlock block = Assert.Single(ac.Queue.Blocks);

        CommandResult fired = block.ApplyAction!(ac, true);
        output.WriteLine($"{command} fired on {ac.Phases?.CurrentPhase?.GetType().Name}: success={fired.Success} — {fired.Message}");
        return fired;
    }

    /// <summary>A DM queued behind a condition that fires inside the final approach fix cancels the approach.</summary>
    [Fact]
    public void QueuedDescendMaintain_InsideTheFaf_CancelsTheApproach()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalInsideFaf);
        Assert.True(InsideFaf(ac));

        CommandResult fired = FireConditional(ac, "DM 020");

        Assert.True(fired.Success, fired.Message);
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
        Assert.Equal(2000, ac.Targets.AssignedAltitude);
    }

    /// <summary>A DM queued behind a condition that fires outside the final approach fix keeps the approach and is held.</summary>
    [Fact]
    public void QueuedDescendMaintain_OutsideTheFaf_HoldsUntilEstablished()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalOutsideFaf);
        Phase current = ac.Phases!.CurrentPhase!;
        ApproachClearance approach = ac.Phases.ActiveApproach!;
        Assert.False(InsideFaf(ac));

        CommandResult fired = FireConditional(ac, "DM 020");

        Assert.True(fired.Success, fired.Message);
        Assert.Same(current, ac.Phases!.CurrentPhase);
        Assert.Same(approach, ac.Phases.ActiveApproach);
        Assert.True(approach.AltitudeHeldUntilEstablished);
        Assert.Equal(2000, ac.Targets.AssignedAltitude);
    }

    // ─── An expedite with an altitude is an altitude assignment ───

    private CommandResult Send(AircraftState ac, string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Context());
        output.WriteLine($"{command} on {ac.Phases?.CurrentPhase?.GetType().Name}: success={result.Success} — {result.Message}");
        return result;
    }

    /// <summary>EXP 030 inside the final approach fix cancels the approach, as DM or CM would.</summary>
    [Fact]
    public void ExpediteWithAltitude_InsideTheFaf_CancelsTheApproach()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalInsideFaf);
        Assert.True(InsideFaf(ac));

        CommandResult result = Send(ac, "EXP 030");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
        Assert.Equal(3000, ac.Targets.AssignedAltitude);
        Assert.True(ac.Procedure.IsExpediting);
    }

    /// <summary>EXP 030 outside the final approach fix keeps the approach and is held until established.</summary>
    [Fact]
    public void ExpediteWithAltitude_OutsideTheFaf_HoldsUntilEstablished()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalOutsideFaf);
        Phase current = ac.Phases!.CurrentPhase!;
        ApproachClearance approach = ac.Phases.ActiveApproach!;
        Assert.False(InsideFaf(ac));

        CommandResult result = Send(ac, "EXP 030");

        Assert.True(result.Success, result.Message);
        Assert.Same(current, ac.Phases!.CurrentPhase);
        Assert.Same(approach, ac.Phases.ActiveApproach);
        Assert.True(approach.AltitudeHeldUntilEstablished);
        Assert.Equal(3000, ac.Targets.AssignedAltitude);
        Assert.True(ac.Procedure.IsExpediting);
    }

    /// <summary>EXP 020 inside the final approach fix while already level at 2,000 is refused, and the approach stays in place.</summary>
    [Fact]
    public void ExpediteWithAltitude_InsideTheFaf_WhenUnable_KeepsTheApproach()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalInsideFaf);
        ac.Altitude = 2000;
        Phase current = ac.Phases!.CurrentPhase!;
        ApproachClearance approach = ac.Phases.ActiveApproach!;
        Assert.True(InsideFaf(ac));

        CommandResult result = Send(ac, "EXP 020");

        Assert.False(result.Success, result.Message);
        Assert.Same(current, ac.Phases?.CurrentPhase);
        Assert.Same(approach, ac.Phases?.ActiveApproach);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
    }

    /// <summary>
    /// EXP 020 queued behind a condition that fires inside the final approach fix while level at 2,000 is refused when it fires,
    /// and the approach stays in place.
    /// </summary>
    [Fact]
    public void QueuedExpediteWithAltitude_InsideTheFaf_WhenUnable_KeepsTheApproach()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalInsideFaf);
        ac.Altitude = 2000;
        Phase current = ac.Phases!.CurrentPhase!;
        ApproachClearance approach = ac.Phases.ActiveApproach!;
        Assert.True(InsideFaf(ac));

        CommandResult fired = FireConditional(ac, "EXP 020");

        Assert.False(fired.Success, fired.Message);
        Assert.Same(current, ac.Phases?.CurrentPhase);
        Assert.Same(approach, ac.Phases?.ActiveApproach);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
    }

    /// <summary>CLAND, EXP 030 inside the final approach fix: the expedite's altitude cancels the approach, as it does alone.</summary>
    [Fact]
    public void ClearedToLandWithExpedite_InsideTheFaf_CancelsTheApproach()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalInsideFaf);
        Assert.True(InsideFaf(ac));

        CommandResult result = Send(ac, "CLAND, EXP 030");

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Phases?.ActiveApproach);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
        Assert.Equal(3000, ac.Targets.AssignedAltitude);
        Assert.True(ac.Procedure.IsExpediting);
    }

    /// <summary>CLAND, EXP 030 outside the final approach fix: the approach is kept and the expedite's altitude held until established.</summary>
    [Fact]
    public void ClearedToLandWithExpedite_OutsideTheFaf_HoldsUntilEstablished()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.FinalOutsideFaf);
        Phase current = ac.Phases!.CurrentPhase!;
        ApproachClearance approach = ac.Phases.ActiveApproach!;
        Assert.False(InsideFaf(ac));

        CommandResult result = Send(ac, "CLAND, EXP 030");

        Assert.True(result.Success, result.Message);
        Assert.Same(current, ac.Phases!.CurrentPhase);
        Assert.Same(approach, ac.Phases.ActiveApproach);
        Assert.True(approach.AltitudeHeldUntilEstablished);
        Assert.Equal(3000, ac.Targets.AssignedAltitude);
        Assert.True(ac.Procedure.IsExpediting);
    }

    // ─── The missed approach is not an approach leg ───

    /// <summary>
    /// After a go-around inside the FAF on KOAK ILS 28R, a CM during the missed approach is neither held until established nor
    /// released at the next missed-approach fix: the missed-approach navigation keeps it (its own table), and the assigned 5,000
    /// stands after that fix is sequenced.
    /// </summary>
    [Fact]
    public void ClimbMaintain_InTheMissedApproach_IsNotHeldOrReleased()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = OnLeg(engine, ApproachLeg.FinalInsideFaf);
        Assert.True(Dispatch(ac, new GoAroundCommand(null, null, null, false)).Success);

        for (int t = 1; (t <= 900) && (ac.Phases?.CurrentPhase is not ApproachNavigationPhase { IsMissedApproach: true }); t++)
        {
            ac = Tick(engine, ac);
        }

        ApproachNavigationPhase missed = Assert.IsType<ApproachNavigationPhase>(ac.Phases?.CurrentPhase);
        Assert.True(missed.IsMissedApproach);
        Assert.True(missed.CanAcceptCommand(CanonicalCommandType.ClimbMaintain).IsAllowed);
        string fixAtCm = ac.Targets.NavigationRoute[0].Name;
        output.WriteLine($"missed approach toward {fixAtCm}: alt={ac.Altitude:F0}");

        Assert.True(Dispatch(ac, new ClimbMaintainCommand(5000)).Success);
        Assert.Same(missed, ac.Phases!.CurrentPhase);
        Assert.False(IsHeld(ac));

        bool sequenced = false;
        for (int t = 1; (t <= 900) && !sequenced; t++)
        {
            ac = Tick(engine, ac);
            sequenced = (ac.Phases?.CurrentPhase != missed) || (ac.Targets.NavigationRoute.FirstOrDefault()?.Name != fixAtCm);
        }

        output.WriteLine($"past {fixAtCm}: alt={ac.Altitude:F0} target={ac.Targets.TargetAltitude} on {ac.Phases?.CurrentPhase?.GetType().Name}");
        Assert.True(sequenced, $"never sequenced {fixAtCm}");
        Assert.False(IsHeld(ac));
        Assert.Equal(5000, ac.Targets.AssignedAltitude);
    }

    // ─── Held until established, then released ───

    private static bool IsHeld(AircraftState ac) => ac.Phases?.ActiveApproach is { AltitudeHeldUntilEstablished: true };

    private static LatLon FixPosition(string name)
    {
        (double Lat, double Lon) fix = NavigationDatabase.Instance.GetFixPosition(name) ?? throw new InvalidOperationException($"{name} missing");
        return new LatLon(fix.Lat, fix.Lon);
    }

    /// <summary>While held the target is the held altitude, or none once physics has levelled the aircraft there.</summary>
    private static void AssertHolding(AircraftState ac, int held, int t) =>
        Assert.True(
            ac.Targets.TargetAltitude is null || (ac.Targets.TargetAltitude == held),
            $"t={t} on {ac.Phases?.CurrentPhase?.GetType().Name}: target {ac.Targets.TargetAltitude} alt={ac.Altitude:F0}, not the held {held}"
        );

    private static AircraftState Tick(SimulationEngine engine, AircraftState ac)
    {
        engine.TickOneSecond();
        return engine.FindAircraft(ac.Callsign) ?? throw new InvalidOperationException($"{ac.Callsign} left the world");
    }

    /// <summary>The highest published floor (at, at or above, glideslope intercept, the lower bound of a window) on the fixes.</summary>
    private static int HighestPublishedFloor(IEnumerable<ApproachFix> fixes) =>
        fixes
            .Select(f =>
                f.Altitude switch
                {
                    {
                        Type: CifpAltitudeRestrictionType.At
                            or CifpAltitudeRestrictionType.AtOrAbove
                            or CifpAltitudeRestrictionType.GlideSlopeIntercept,
                    } alt => alt.Altitude1Ft,
                    { Type: CifpAltitudeRestrictionType.Between, Altitude2Ft: { } lower } => lower,
                    _ => 0,
                }
            )
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>
    /// A DM below the published floor on the approach's fixes (KCCR VOR RWY 19R cleared direct FAWNE, at or above 2,500) is held
    /// until approach navigation sequences FAWNE; then the hold is released and the published profile resumes.
    /// </summary>
    [Fact]
    public void DescendBelowPublishedFloor_OnApproachNavigation_IsHeldUntilTheNextFix()
    {
        SimulationEngine engine = BuildEngine();
        LatLon fawne = FixPosition("FAWNE");
        var inbound = new TrueHeading(GeoMath.BearingTo(fawne, FixPosition("HUKVI")));
        var setup = new ApproachSetup("KCCR", "S19R", "FAWNE", GeoMath.ProjectPoint(fawne, inbound.ToReciprocal(), 5.0), inbound, 4000, 13.0);
        AircraftState ac = AddClearedForApproach<ApproachNavigationPhase>(engine, setup);
        ApproachNavigationPhase nav = Assert.IsType<ApproachNavigationPhase>(ac.Phases!.CurrentPhase);
        output.WriteLine(string.Join(", ", nav.Fixes.Select(f => $"{f.Name} {f.Altitude?.Type} {f.Altitude?.Altitude1Ft}")));
        int floor = HighestPublishedFloor(nav.Fixes);
        int dm = Math.Min((int)ac.Altitude, floor) - 1000;
        output.WriteLine($"alt={ac.Altitude:F0} floor={floor} DM {dm}");
        Assert.True(floor > 0, "the approach's fixes publish no floor");

        Assert.True(Dispatch(ac, new DescendMaintainCommand(dm)).Success);
        Assert.True(IsHeld(ac));

        bool released = false;
        for (int t = 1; (t <= 1200) && !released; t++)
        {
            ac = Tick(engine, ac);
            Assert.NotNull(ac.Phases?.ActiveApproach);
            if (IsHeld(ac))
            {
                AssertHolding(ac, dm, t);
                Assert.Equal(dm, ac.Targets.AssignedAltitude);
                continue;
            }

            released = true;
            double fromFirstFixNm = GeoMath.DistanceNm(ac.Position, new LatLon(nav.Fixes[0].Latitude, nav.Fixes[0].Longitude));
            output.WriteLine($"released at t={t}: alt={ac.Altitude:F0} {fromFirstFixNm:F2} nm from {nav.Fixes[0].Name}");
            Assert.Same(nav, ac.Phases!.CurrentPhase);
            Assert.True(fromFirstFixNm < 2.0, $"released {fromFirstFixNm:F2} nm from {nav.Fixes[0].Name}, not at its sequencing");
        }

        Assert.True(released, "the held DM was never released");
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    /// <summary>A DM below the procedure turn's minimum (KCCR VOR RWY 19R at CCR) is held: no climb back to the minimum.</summary>
    [Fact]
    public void DescendBelowProcedureTurnMinimum_IsHeldThroughTheTurn()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = OnLeg(engine, ApproachLeg.ProcedureTurn);
        ProcedureTurnPhase pt = Assert.IsType<ProcedureTurnPhase>(ac.Phases!.CurrentPhase);
        int dm = pt.MinAltitudeFt - 500;
        output.WriteLine($"alt={ac.Altitude:F0} PT minimum={pt.MinAltitudeFt} DM {dm}");

        Assert.True(Dispatch(ac, new DescendMaintainCommand(dm)).Success);

        bool reachedFix = false;
        double? turnBeganNm = null;
        for (int t = 1; t <= 1200; t++)
        {
            ac = Tick(engine, ac);
            if (ac.Phases?.CurrentPhase is not ProcedureTurnPhase)
            {
                output.WriteLine($"left the procedure turn at t={t}");
                break;
            }

            double fromFixNm = GeoMath.DistanceNm(ac.Position, new LatLon(pt.FixLat, pt.FixLon));
            reachedFix |= fromFixNm < 1.0;
            if ((turnBeganNm is null) && (((ProcedureTurnPhaseDto)pt.ToSnapshot()).State == (int)ProcedureTurnPhase.PtState.TurnToInbound))
            {
                turnBeganNm = fromFixNm;
                output.WriteLine($"turn inbound began at t={t}, {fromFixNm:F2} nm from {pt.FixName}");
            }

            Assert.True(IsHeld(ac));
            AssertHolding(ac, dm, t);
        }

        Assert.True(reachedFix, $"never reached {pt.FixName} inside the procedure turn");

        // Step 3 (AIM 5-4-9.a.1): the held altitude does not keep the aircraft outbound to the distance cap; the timer turns it.
        double distanceCapNm = pt.MaxOutboundDistanceNm - ProcedureTurnPhase.TurnRadiusReserveNm;
        Assert.NotNull(turnBeganNm);
        Assert.True(turnBeganNm < distanceCapNm - 1.0, $"turned inbound {turnBeganNm:F2} nm out, at the {distanceCapNm:F2} nm distance cap");
    }

    /// <summary>
    /// A CM on the KOAK ILS 28R intercept 10 nm out is held through the final until glideslope capture, then released: the
    /// aircraft follows the glideslope down.
    /// </summary>
    [Fact]
    public void ClimbOnIntercept_IsHeldUntilGlideslopeCapture()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = WithIlsClearance(FollowSequenceRefusalTests.AddApproachFollower(engine, 10.0));
        RunwayInfo rwy = Oak28R();
        LatLon joinPoint = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), 8.5);
        var interceptHeading = new TrueHeading(GeoMath.BearingTo(ac.Position, joinPoint));
        ac.TrueHeading = interceptHeading;
        ac.TrueTrack = interceptHeading;
        ac.Targets.TargetTrueHeading = interceptHeading;
        Assert.False(InsideFaf(ac));
        const int Cm = 1500;

        Assert.True(Dispatch(ac, new ClimbMaintainCommand(Cm)).Success);
        Assert.True(IsHeld(ac));

        bool released = false;
        for (int t = 1; (t <= 900) && !released; t++)
        {
            ac = Tick(engine, ac);
            string warnings = string.Join(" | ", ac.PendingWarnings);
            Assert.True(
                ac.Phases?.ActiveApproach is not null,
                $"approach dropped at t={t} alt={ac.Altitude:F0}: {warnings} / {ac.Phases?.CurrentPhase?.GetType().Name}"
            );
            if (IsHeld(ac))
            {
                if (ac.Phases!.CurrentPhase is FinalApproachPhase)
                {
                    AssertHolding(ac, Cm, t);
                }

                continue;
            }

            released = true;
            output.WriteLine($"released at t={t} on {ac.Phases!.CurrentPhase?.GetType().Name}: alt={ac.Altitude:F0}");
            Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        }

        Assert.True(released, "the held CM was never released");
        Assert.Null(ac.Targets.AssignedAltitude);

        double atCapture = ac.Altitude;
        for (int t = 1; t <= 60; t++)
        {
            ac = Tick(engine, ac);
        }

        output.WriteLine($"60 s after capture: alt={ac.Altitude:F0}");
        Assert.True(ac.Altitude < atCapture - 100, $"did not follow the glideslope down from {atCapture:F0} (now {ac.Altitude:F0})");
    }

    // ─── Glideslope capture and the held altitude ───

    /// <summary>A 3° glideslope over the runway's threshold elevation, <paramref name="distanceNm"/> out (50 ft crossing height).</summary>
    private static double GlideslopeFt(RunwayInfo rwy, double distanceNm) =>
        GlideSlopeGeometry.AltitudeAtDistance(distanceNm, rwy.ElevationFt, 50.0, 3.0);

    private static double FromThresholdNm(AircraftState ac, RunwayInfo rwy) =>
        GeoMath.DistanceNm(ac.Position, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude));

    private static bool GsCaptured(FinalApproachPhase final) => ((FinalApproachPhaseDto)final.ToSnapshot()).GsCaptured;

    /// <summary>The KOAK 28R follower on the ILS 28R clearance, advanced onto its final at <paramref name="distanceNm"/> on the centerline.</summary>
    private static AircraftState OnOak28RFinal(SimulationEngine engine, double distanceNm, double altitude)
    {
        AircraftState ac = WithIlsClearance(FollowSequenceRefusalTests.AddApproachFollower(engine, distanceNm));
        ac.Phases!.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        OnCenterline(ac, distanceNm);
        final.DistanceToThresholdNm = distanceNm;
        ac.Altitude = altitude;
        return ac;
    }

    /// <summary>
    /// A CM 040 on the KOAK ILS 28R final outside the FAF, given below the glideslope: the climb passes through the glideslope
    /// without capturing it, and the hold stays set with a 4,000 target until the aircraft levels there.
    /// </summary>
    [Fact]
    public void ClimbMaintain_AboveTheGlideslope_IsNotReleasedMidClimb()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(ApproachPublishedFafTests.Cifp.Value);
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState ac = OnOak28RFinal(engine, 14.0, 2000);
        Assert.True(ac.Altitude < GlideslopeFt(rwy, 14.0) - 500);
        const int Cm = 4000;

        Assert.True(Dispatch(ac, new ClimbMaintainCommand(Cm)).Success);
        Assert.True(IsHeld(ac));

        bool passedGlideslope = false;
        for (int t = 1; (t <= 600) && (ac.Altitude < Cm - 100); t++)
        {
            ac = Tick(engine, ac);
            double gsFt = GlideslopeFt(rwy, FromThresholdNm(ac, rwy));
            passedGlideslope |= ac.Altitude > gsFt + 100;
            Assert.True(IsHeld(ac), $"t={t}: released mid-climb at {ac.Altitude:F0} (glideslope {gsFt:F0})");
            AssertHolding(ac, Cm, t);
        }

        output.WriteLine($"levelling at {ac.Altitude:F0}, {FromThresholdNm(ac, rwy):F2} nm out");
        Assert.True(passedGlideslope, "the climb never passed the glideslope");
        Assert.True(ac.Altitude >= Cm - 100, $"never reached {Cm} (alt {ac.Altitude:F0})");
    }

    /// <summary>
    /// On the KSFO ILS 28L final outside the published FAF (DUYET, beyond 5.5 nm) with the glideslope captured, a CM 040 clears
    /// the capture and the aircraft climbs at least 1,000 ft toward 4,000, held until it crosses the FAF.
    /// </summary>
    [Fact]
    public void ClimbMaintain_AfterGlideslopeCapture_OutsideTheFaf_IsFlown()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(ApproachPublishedFafTests.Cifp.Value);
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = ApproachPublishedFafTests.Sfo28L();
        double fafNm = ApproachPublishedFafTests.PublishedFafNm();
        double startNm = fafNm + 1.5;
        AircraftState ac = ApproachPublishedFafTests.OnFinal(engine, startNm, GlideslopeFt(rwy, startNm) + 20);
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases!.CurrentPhase);

        for (int t = 1; (t <= 30) && !GsCaptured(final); t++)
        {
            ac = Tick(engine, ac);
        }

        output.WriteLine($"FAF {fafNm:F2} nm; captured={GsCaptured(final)} at {FromThresholdNm(ac, rwy):F2} nm, alt={ac.Altitude:F0}");
        Assert.True(GsCaptured(final), "the glideslope was not captured within 30 s");
        Assert.Same(final, ac.Phases!.CurrentPhase);
        Assert.False(FinalApproachFix.IsInsidePublishedFaf(ac, rwy, ac.Ground.Layout));
        const int Cm = 4000;
        double atClimb = ac.Altitude;

        Assert.True(Dispatch(ac, new ClimbMaintainCommand(Cm)).Success);
        Assert.True(IsHeld(ac));
        ac = Tick(engine, ac);
        Assert.False(GsCaptured(final), "the glideslope capture was not cleared by the held climb");

        // Held through the glideslope and at 4,000 until it crosses the published FAF, which releases it.
        for (int t = 2; (t <= 300) && (ac.Altitude < Cm - 100); t++)
        {
            ac = Tick(engine, ac);
            if (!IsHeld(ac))
            {
                double releasedNm = FromThresholdNm(ac, rwy);
                output.WriteLine($"released at t={t}: {ac.Altitude:F0}, {releasedNm:F2} nm out");
                Assert.True(releasedNm <= fafNm + 0.1, $"t={t}: released at {ac.Altitude:F0}, {releasedNm:F2} nm out, before the FAF");
                break;
            }

            AssertHolding(ac, Cm, t);
        }

        output.WriteLine($"climbed from {atClimb:F0} to {ac.Altitude:F0}, {FromThresholdNm(ac, rwy):F2} nm out");
        Assert.True(ac.Altitude >= atClimb + 1000, $"climbed only from {atClimb:F0} to {ac.Altitude:F0}");
    }

    /// <summary>
    /// A DM 035 on the KOAK ILS 28R final 1 nm outside the published FAF, given at 4,000 (above the glideslope): the aircraft is
    /// still descending to the held altitude when it crosses the FAF, which releases the hold.
    /// </summary>
    [Fact]
    public void HeldAltitudeAboveTheGlideslope_IsReleasedAtTheFaf()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(ApproachPublishedFafTests.Cifp.Value);
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        double fafNm = ApproachGateDatabase.GetFafDistanceNm("KOAK", "28R", 0) ?? throw new InvalidOperationException("KOAK 28R has no FAF");
        AircraftState ac = OnOak28RFinal(engine, fafNm + 1.0, 4000);
        const int Dm = 3500;
        Assert.True(Dm > GlideslopeFt(rwy, fafNm) + 500);

        Assert.True(Dispatch(ac, new DescendMaintainCommand(Dm)).Success);
        Assert.True(IsHeld(ac));

        for (int t = 1; (t <= 120) && IsHeld(ac); t++)
        {
            ac = Tick(engine, ac);
        }

        double releasedNm = FromThresholdNm(ac, rwy);
        output.WriteLine($"FAF {fafNm:F2} nm; released={!IsHeld(ac)} at {releasedNm:F2} nm, alt={ac.Altitude:F0}");
        Assert.False(ac.Phases!.ActiveApproach!.AltitudeHeldUntilEstablished);
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.True(Math.Abs(releasedNm - fafNm) < 0.4, $"released {releasedNm:F2} nm out, not at the {fafNm:F2} nm FAF");
        Assert.True(ac.Altitude > Dm + 50, $"levelled at {ac.Altitude:F0} before the FAF");
    }

    /// <summary>
    /// On the KSFO ILS 28L final outside the published FAF with the glideslope captured, a CM 600 ft above the glideslope there:
    /// the aircraft climbs and levels at it, and the hold stays set with that target at every tick until the aircraft crosses
    /// the published FAF, which releases it. Held above the glideslope, it is not captured from above before the FAF.
    /// </summary>
    [Fact]
    public void HeldAboveTheGlideslope_LevelOff_IsKeptUntilTheFaf()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(ApproachPublishedFafTests.Cifp.Value);
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = ApproachPublishedFafTests.Sfo28L();
        double fafNm = ApproachPublishedFafTests.PublishedFafNm();
        double startNm = fafNm + 2.5;
        AircraftState ac = ApproachPublishedFafTests.OnFinal(engine, startNm, GlideslopeFt(rwy, startNm) + 20);
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases!.CurrentPhase);

        for (int t = 1; (t <= 30) && !GsCaptured(final); t++)
        {
            ac = Tick(engine, ac);
        }

        Assert.True(GsCaptured(final), "the glideslope was not captured within 30 s");
        int cm = (int)(Math.Ceiling((GlideslopeFt(rwy, FromThresholdNm(ac, rwy)) + 600) / 100) * 100);
        output.WriteLine($"FAF {fafNm:F2} nm; CM {cm} at {FromThresholdNm(ac, rwy):F2} nm, alt={ac.Altitude:F0}");
        Assert.True(Dispatch(ac, new ClimbMaintainCommand(cm)).Success);
        Assert.True(IsHeld(ac));

        bool levelled = false;
        for (int t = 1; (t <= 300) && IsHeld(ac); t++)
        {
            ac = Tick(engine, ac);
            if (!IsHeld(ac))
            {
                break;
            }

            AssertHolding(ac, cm, t);
            Assert.Equal(cm, ac.Targets.AssignedAltitude);
            levelled |= Math.Abs(ac.Altitude - cm) <= 50;
        }

        double releasedNm = FromThresholdNm(ac, rwy);
        output.WriteLine($"released={!IsHeld(ac)} at {releasedNm:F2} nm, alt={ac.Altitude:F0}");
        Assert.False(IsHeld(ac), "the held altitude was never released");
        Assert.True(levelled, $"never levelled at {cm}");
        Assert.True(Math.Abs(releasedNm - fafNm) < 0.4, $"released {releasedNm:F2} nm out, not at the {fafNm:F2} nm FAF");
    }

    /// <summary>The glideslope altitude at the aircraft's distance plus 50 ft, rounded up to the next 100.</summary>
    private static int FiftyAboveTheGlideslope(RunwayInfo rwy, AircraftState ac) =>
        (int)(Math.Ceiling((GlideslopeFt(rwy, FromThresholdNm(ac, rwy)) + 50) / 100) * 100);

    /// <summary>
    /// On the KSFO ILS 28L final outside the published FAF with the glideslope captured, a CM to the glideslope altitude plus
    /// 50 ft rounded up to the next 100, given when that is within 100 ft above the aircraft and more than 20 ft from it: the
    /// aircraft climbs to it and holds it for at least 20 s, still held. It is not counted level at once, recaptured and the
    /// instruction dropped unflown.
    /// </summary>
    [Fact]
    public void ClimbMaintain_50FtAboveOnTheGlideslope_IsFlownNotRecaptured()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(ApproachPublishedFafTests.Cifp.Value);
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = ApproachPublishedFafTests.Sfo28L();
        double fafNm = ApproachPublishedFafTests.PublishedFafNm();
        double startNm = fafNm + 3.0;
        AircraftState ac = ApproachPublishedFafTests.OnFinal(engine, startNm, GlideslopeFt(rwy, startNm) + 20);
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases!.CurrentPhase);

        for (int t = 1; (t <= 30) && !GsCaptured(final); t++)
        {
            ac = Tick(engine, ac);
        }

        // The glideslope descends under the aircraft: wait for the tick where the CM is within 100 ft above, more than 20 ft away.
        int cm = FiftyAboveTheGlideslope(rwy, ac);
        for (int t = 1; (t <= 20) && !((cm - ac.Altitude > 20) && (cm - ac.Altitude <= 100)); t++)
        {
            ac = Tick(engine, ac);
            cm = FiftyAboveTheGlideslope(rwy, ac);
        }

        output.WriteLine($"FAF {fafNm:F2} nm; CM {cm} at {FromThresholdNm(ac, rwy):F2} nm, alt={ac.Altitude:F0}");
        Assert.True(GsCaptured(final), "the glideslope was not captured");
        Assert.True((cm - ac.Altitude > 20) && (cm - ac.Altitude <= 100), $"CM {cm} is not within 21-100 ft above {ac.Altitude:F0}");
        Assert.True(Dispatch(ac, new ClimbMaintainCommand(cm)).Success);
        Assert.True(IsHeld(ac));

        int levelSeconds = 0;
        for (int t = 1; (t <= 120) && (levelSeconds < 20); t++)
        {
            ac = Tick(engine, ac);
            Assert.True(IsHeld(ac), $"t={t}: released at {ac.Altitude:F0}, {FromThresholdNm(ac, rwy):F2} nm out");
            Assert.Equal(cm, ac.Targets.AssignedAltitude);
            levelSeconds = Math.Abs(ac.Altitude - cm) <= 20 ? levelSeconds + 1 : 0;
        }

        output.WriteLine($"held at {ac.Altitude:F0} for {levelSeconds} s, {FromThresholdNm(ac, rwy):F2} nm out");
        Assert.True(levelSeconds >= 20, $"held {cm} for only {levelSeconds} s");
    }

    /// <summary>
    /// A DM on the KOAK ILS 28R final outside the published FAF to an altitude below the glideslope: the aircraft levels there,
    /// held, and captures the glideslope outside the FAF when it comes down to meet the aircraft; the capture releases the hold.
    /// </summary>
    [Fact]
    public void HeldBelowTheGlideslope_CapturesWhenTheGlideslopeMeetsIt()
    {
        using IDisposable _ = ApproachGateDatabase.ScopedOverride(ApproachPublishedFafTests.Cifp.Value);
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        double fafNm = ApproachGateDatabase.GetFafDistanceNm("KOAK", "28R", 0) ?? throw new InvalidOperationException("KOAK 28R has no FAF");
        double startNm = fafNm + 4.0;
        int dm = (int)(Math.Floor((GlideslopeFt(rwy, startNm) - 500) / 100) * 100);
        Assert.True(dm > GlideslopeFt(rwy, fafNm) + 200, $"DM {dm} is not above the glideslope at the FAF");
        AircraftState ac = OnOak28RFinal(engine, startNm, dm + 100);
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases!.CurrentPhase);

        Assert.True(Dispatch(ac, new DescendMaintainCommand(dm)).Success);
        Assert.True(IsHeld(ac));

        for (int t = 1; (t <= 300) && !GsCaptured(final); t++)
        {
            ac = Tick(engine, ac);
            if (GsCaptured(final))
            {
                break;
            }

            Assert.True(IsHeld(ac), $"t={t}: released before capture at {ac.Altitude:F0}, {FromThresholdNm(ac, rwy):F2} nm out");
            AssertHolding(ac, dm, t);
        }

        double capturedNm = FromThresholdNm(ac, rwy);
        double gsFt = GlideslopeFt(rwy, capturedNm);
        output.WriteLine($"FAF {fafNm:F2} nm; DM {dm}; captured={GsCaptured(final)} at {capturedNm:F2} nm, alt={ac.Altitude:F0} gs={gsFt:F0}");
        Assert.True(GsCaptured(final), "the glideslope was never captured");
        Assert.False(IsHeld(ac));
        Assert.True(capturedNm > fafNm, $"captured {capturedNm:F2} nm out, inside the {fafNm:F2} nm FAF");
        Assert.True(Math.Abs(ac.Altitude - gsFt) <= 100, $"captured at {ac.Altitude:F0}, {ac.Altitude - gsFt:F0} ft from the glideslope");
    }

    // ─── Back to the published floor after a release ───

    /// <summary>
    /// KOAK ILS 28R cleared direct BBUBB, flying the feeder NAGVY (at or above 4,200), SKYLN (3,800), GROVE (3,400): a DM 030
    /// given on the way to SKYLN is held until SKYLN is sequenced, then the aircraft targets GROVE's published floor and climbs
    /// back toward it (at least 250 ft) before GROVE.
    /// </summary>
    [Fact]
    public void DescendBelowAStepDownFloor_ClimbsBackAfterTheRelease()
    {
        SimulationEngine engine = BuildEngine();
        LatLon bbubb = FixPosition("BBUBB");
        var inbound = new TrueHeading(GeoMath.BearingTo(bbubb, FixPosition("NAGVY")));
        var setup = new ApproachSetup("KOAK", "I28R", "BBUBB", GeoMath.ProjectPoint(bbubb, inbound.ToReciprocal(), 3.0), inbound, 6000, 13.0);
        AircraftState ac = AddClearedForApproach<ApproachNavigationPhase>(engine, setup);
        ApproachNavigationPhase nav = Assert.IsType<ApproachNavigationPhase>(ac.Phases!.CurrentPhase);
        output.WriteLine(string.Join(", ", nav.Fixes.Select(f => $"{f.Name} {f.Altitude?.Type} {f.Altitude?.Altitude1Ft}")));

        int t = 1;
        for (; (t <= 1200) && (ac.Targets.NavigationRoute.FirstOrDefault()?.Name != "SKYLN"); t++)
        {
            ac = Tick(engine, ac);
        }

        output.WriteLine($"toward SKYLN at t={t}: alt={ac.Altitude:F0} on {ac.Phases?.CurrentPhase?.GetType().Name}");
        Assert.Same(nav, ac.Phases?.CurrentPhase);
        Assert.Equal("SKYLN", ac.Targets.NavigationRoute[0].Name);

        // Placed just above the DM altitude, below every floor ahead, so the release finds it under GROVE's.
        ac.Altitude = 3100;
        Assert.True(Dispatch(ac, new DescendMaintainCommand(3000)).Success);
        Assert.True(IsHeld(ac));

        for (; (t <= 2400) && IsHeld(ac); t++)
        {
            ac = Tick(engine, ac);
        }

        output.WriteLine($"released at t={t}: alt={ac.Altitude:F0}, toward {ac.Targets.NavigationRoute.FirstOrDefault()?.Name}");
        Assert.False(IsHeld(ac));
        Assert.Equal("GROVE", ac.Targets.NavigationRoute[0].Name);
        Assert.True(ac.Altitude < 3400, $"already at or above GROVE's floor ({ac.Altitude:F0}) when released");

        double atRelease = ac.Altitude;
        ac = Tick(engine, ac);
        t++;
        Assert.Equal(3400, ac.Targets.TargetAltitude);

        double highest = ac.Altitude;
        for (; (t <= 3600) && (ac.Phases?.CurrentPhase == nav) && (ac.Targets.NavigationRoute.FirstOrDefault()?.Name == "GROVE"); t++)
        {
            ac = Tick(engine, ac);
            highest = Math.Max(highest, ac.Altitude);
        }

        output.WriteLine($"GROVE sequenced at t={t}: highest {highest:F0} after the release");
        Assert.True(highest >= atRelease + 250, $"no climb back toward GROVE's 3,400 floor (from {atRelease:F0}, highest {highest:F0})");
    }
}
