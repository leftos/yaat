using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Commands that must not cancel an instrument approach: a forced speed (SPDN, SPDN RFAS) on every approach leg, the
/// landing and option clearances and the runway exits on a procedure turn or a hold-in-lieu, CLC / TG / SG / LA on the
/// intercept, the approach's fixes and the final, and the track, sim and flight-plan verbs that only touch bookkeeping
/// (CIFR included). Each is sent through the engine on an aircraft established on the approach, and the approach's phase
/// list and current phase must survive with no "cancelled by" warning. Real KOAK, KCCR and KACV navdata.
/// </summary>
public class ApproachKeepingCommandTests(ITestOutputHelper output)
{
    private const string Callsign = "APCH1";
    private const string FinalGateRefusal = "unable, inside 5nm final [7110.65 §5-7-1.b.4]";

    /// <summary>The approach leg an aircraft is flying when the command arrives.</summary>
    public enum ApproachLeg
    {
        InterceptCourse,
        ApproachNavigation,
        ProcedureTurn,
        HoldInLieu,
        FinalOutsideGate,
        FinalInsideGate,
    }

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    private static AircraftState OnLeg(SimulationEngine engine, ApproachLeg leg) =>
        leg switch
        {
            ApproachLeg.InterceptCourse => FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0),
            ApproachLeg.ApproachNavigation => FollowSequenceRefusalTests.AddApproachNavigationFollower(engine),
            ApproachLeg.ProcedureTurn => AddClearedForApproach<ProcedureTurnPhase>(engine, KccrS19RFromSouth),
            ApproachLeg.HoldInLieu => AddHoldInLieu(engine),
            ApproachLeg.FinalOutsideGate => AddOnFinal(engine, 8.0),
            ApproachLeg.FinalInsideGate => AddOnFinal(engine, 3.0),
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "no setup for this approach leg"),
        };

    /// <summary>The KOAK 28R intercept follower advanced onto its final, <paramref name="distanceNm"/> from the threshold.</summary>
    private static AircraftState AddOnFinal(SimulationEngine engine, double distanceNm)
    {
        AircraftState ac = FollowSequenceRefusalTests.AddApproachFollower(engine, distanceNm);
        ac.Phases!.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        final.DistanceToThresholdNm = distanceNm;
        return ac;
    }

    private static AircraftState AddHoldInLieu(SimulationEngine engine)
    {
        AircraftState ac = AddClearedForApproach<HoldingPatternPhase>(engine, KacvR01DctSegve());
        Assert.True(Assert.IsType<HoldingPatternPhase>(ac.Phases!.CurrentPhase).IsHoldInLieu);
        return ac;
    }

    /// <summary>Where and how an aircraft built by <see cref="AddClearedForApproach{T}"/> is cleared.</summary>
    private sealed record ApproachSetup(
        string Airport,
        string ApproachId,
        string DctFix,
        LatLon Position,
        TrueHeading Heading,
        double Altitude,
        double Declination
    );

    /// <summary>KCCR VOR RWY 19R from south of CCR, direct CCR: the chain engages the procedure turn at CCR.</summary>
    private static readonly ApproachSetup KccrS19RFromSouth = new(
        "KCCR",
        "S19R",
        "CCR",
        new LatLon(37.88, -122.28),
        new TrueHeading(41.0),
        6000,
        13.0
    );

    /// <summary>A KACV RNAV (GPS) RWY 1 clearance direct SEGVE from south-west of it: the chain flies the hold-in-lieu at SEGVE.</summary>
    private static ApproachSetup KacvR01DctSegve()
    {
        (double Lat, double Lon) segve = NavigationDatabase.Instance.GetFixPosition("SEGVE") ?? throw new InvalidOperationException("SEGVE missing");
        return new ApproachSetup("KACV", "R01", "SEGVE", new LatLon(segve.Lat - 0.15, segve.Lon - 0.1), new TrueHeading(30.0), 4000, 15.0);
    }

    /// <summary>A C172 cleared for the approach (the real CAPP chain), its phase list advanced to the first <typeparamref name="T"/>.</summary>
    private static AircraftState AddClearedForApproach<T>(SimulationEngine engine, ApproachSetup setup)
        where T : Phase
    {
        (double Lat, double Lon) fix =
            NavigationDatabase.Instance.GetFixPosition(setup.DctFix) ?? throw new InvalidOperationException($"{setup.DctFix} missing");
        var ac = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = setup.Position,
            TrueHeading = setup.Heading,
            TrueTrack = setup.Heading,
            Altitude = setup.Altitude,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            Declination = setup.Declination,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = setup.Airport,
                Destination = setup.Airport,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
            Procedure = new AircraftProcedure { DestinationRunway = null },
        };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = setup.DctFix, Position = new LatLon(fix.Lat, fix.Lon) });
        var cmd = new ClearedApproachCommand(
            setup.ApproachId,
            setup.Airport,
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: setup.DctFix,
            DctFixLat: fix.Lat,
            DctFixLon: fix.Lon,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        CommandResult capp = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(capp.Success, capp.Message);
        engine.World.AddAircraft(ac);

        PhaseList phases = ac.Phases!;
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);
        if (phases.CurrentPhase is { Status: PhaseStatus.Pending })
        {
            phases.Start(ctx);
        }

        while ((phases.CurrentPhase is not null) && (phases.CurrentPhase is not T))
        {
            phases.AdvanceToNext(ctx);
        }

        Assert.IsType<T>(phases.CurrentPhase);
        return ac;
    }

    private CommandResult Send(SimulationEngine engine, AircraftState ac, string command)
    {
        CommandResult result = engine.SendCommand(ac.Callsign, command);
        output.WriteLine($"{command} on {ac.Phases?.CurrentPhase?.GetType().Name}: success={result.Success} — {result.Message}");
        return result;
    }

    /// <summary>The approach's phase list and current phase are the ones the aircraft had before, and nothing was cancelled.</summary>
    private static void AssertApproachKept(AircraftState ac, PhaseList phases, Phase current)
    {
        Assert.Same(phases, ac.Phases);
        Assert.Same(current, ac.Phases!.CurrentPhase);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
    }

    private (AircraftState Aircraft, PhaseList Phases, Phase Current) Establish(SimulationEngine engine, ApproachLeg leg)
    {
        AircraftState ac = OnLeg(engine, leg);
        return (ac, ac.Phases!, ac.Phases!.CurrentPhase!);
    }

    // ─── SPDN keeps the approach ───

    public static TheoryData<ApproachLeg, string> ForcedSpeedOnEveryLeg()
    {
        var data = new TheoryData<ApproachLeg, string>();
        foreach (ApproachLeg leg in Enum.GetValues<ApproachLeg>())
        {
            data.Add(leg, "SPDN 100");
            data.Add(leg, "SPDN RFAS");
        }

        return data;
    }

    /// <summary>A forced speed is the instructor's override: it retargets speed on every approach leg, inside the final gate too.</summary>
    [Theory]
    [MemberData(nameof(ForcedSpeedOnEveryLeg))]
    public void ForcedSpeed_KeepsTheApproach(ApproachLeg leg, string command)
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, leg);

        CommandResult result = Send(engine, ac, command);

        Assert.True(result.Success, result.Message);
        AssertApproachKept(ac, phases, current);
    }

    /// <summary>A plain SPD inside the 5 nm final gate is still refused with the pilot's "unable", and the final survives.</summary>
    [Fact]
    public void PlainSpeedInsideTheFinalGate_IsStillRefused()
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, ApproachLeg.FinalInsideGate);

        CommandResult result = Send(engine, ac, "SPD 100");

        Assert.False(result.Success);
        Assert.Contains(FinalGateRefusal, result.Message);
        AssertApproachKept(ac, phases, current);
    }

    // ─── Landing clearances and exits keep a procedure turn and a hold-in-lieu ───

    public static TheoryData<ApproachLeg, string, ClearanceType?> LandingClearancesOnCourseReversals()
    {
        var data = new TheoryData<ApproachLeg, string, ClearanceType?>();
        foreach (ApproachLeg leg in new[] { ApproachLeg.ProcedureTurn, ApproachLeg.HoldInLieu })
        {
            data.Add(leg, "CLAND", ClearanceType.ClearedToLand);
            data.Add(leg, "COPT", ClearanceType.ClearedForOption);
            data.Add(leg, "TG", ClearanceType.ClearedTouchAndGo);
            data.Add(leg, "SG", ClearanceType.ClearedStopAndGo);
            data.Add(leg, "LA", ClearanceType.ClearedLowApproach);
        }

        return data;
    }

    /// <summary>A landing or option clearance on a course reversal only sets the clearance the approach lands on.</summary>
    [Theory]
    [MemberData(nameof(LandingClearancesOnCourseReversals))]
    public void LandingClearance_KeepsCourseReversalAndSetsTheClearance(ApproachLeg leg, string command, ClearanceType? expected)
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, leg);

        CommandResult result = Send(engine, ac, command);

        Assert.True(result.Success, result.Message);
        AssertApproachKept(ac, phases, current);
        Assert.Equal(expected, ac.Phases!.LandingClearance);
    }

    public static TheoryData<ApproachLeg, string> ExitsOnCourseReversals()
    {
        var data = new TheoryData<ApproachLeg, string>();
        foreach (ApproachLeg leg in new[] { ApproachLeg.ProcedureTurn, ApproachLeg.HoldInLieu })
        {
            data.Add(leg, "EL");
            data.Add(leg, "ER");
            data.Add(leg, "EXIT B");
        }

        return data;
    }

    /// <summary>A runway exit on a course reversal only records the exit the landing will take.</summary>
    [Theory]
    [MemberData(nameof(ExitsOnCourseReversals))]
    public void Exit_KeepsCourseReversalAndRecordsTheExit(ApproachLeg leg, string command)
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, leg);

        CommandResult result = Send(engine, ac, command);

        Assert.True(result.Success, result.Message);
        AssertApproachKept(ac, phases, current);
        Assert.NotNull(ac.Phases!.RequestedExit);
    }

    // ─── CLC, TG, SG and LA keep the approach ───

    public static TheoryData<ApproachLeg, string> ClearanceChangesOnTheApproach()
    {
        var data = new TheoryData<ApproachLeg, string>();
        foreach (ApproachLeg leg in new[] { ApproachLeg.InterceptCourse, ApproachLeg.ApproachNavigation, ApproachLeg.FinalOutsideGate })
        {
            data.Add(leg, "CLC");
            data.Add(leg, "TG");
            data.Add(leg, "SG");
            data.Add(leg, "LA");
        }

        return data;
    }

    /// <summary>Cancelling the landing clearance, or turning it into an option, changes what happens at the runway, not the approach.</summary>
    [Theory]
    [MemberData(nameof(ClearanceChangesOnTheApproach))]
    public void ClearanceChange_KeepsTheApproach(ApproachLeg leg, string command)
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, leg);

        CommandResult result = Send(engine, ac, command);

        Assert.True(result.Success, result.Message);
        AssertApproachKept(ac, phases, current);
    }

    /// <summary>A touch-and-go on the final keeps the final and records the option.</summary>
    [Fact]
    public void TouchAndGoOnFinal_KeepsTheApproach()
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, ApproachLeg.FinalOutsideGate);

        CommandResult result = Send(engine, ac, "TG");

        Assert.True(result.Success, result.Message);
        AssertApproachKept(ac, phases, current);
        Assert.Equal(ClearanceType.ClearedTouchAndGo, ac.Phases!.LandingClearance);
    }

    // ─── Track, sim and flight-plan verbs never touch the approach ───

    /// <summary>
    /// A verb that only touches track ownership, the sim clock, a bookmark, a note or the flight plan leaves the approach
    /// as it was, whether it applies or is refused. The aircraft is IFR with a plan on file so CIFR applies.
    /// </summary>
    [Theory]
    [InlineData("ASSUME")]
    [InlineData("UNASSUME")]
    [InlineData("TRACK")]
    [InlineData("DROP")]
    [InlineData("HO 2B")]
    [InlineData("ACCEPT")]
    [InlineData("PO 2B")]
    [InlineData("PAUSE")]
    [InlineData("UNPAUSE")]
    [InlineData("SIMRATE 2")]
    [InlineData("TIMER 60")]
    [InlineData("BM")]
    [InlineData("NOTE expect delay")]
    [InlineData("REMARKS student flight")]
    [InlineData("FP C172 6000 KOAK KSFO")]
    [InlineData("CIFR")]
    [InlineData("TDLSS Sid")]
    [InlineData("RD")]
    public void BookkeepingVerb_KeepsTheApproach(string command)
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, ApproachLeg.InterceptCourse);
        ac.FlightPlan.HasFlightPlan = true;
        ac.FlightPlan.FlightRules = "IFR";

        Send(engine, ac, command);

        AssertApproachKept(ac, phases, current);
    }

    /// <summary>CIFR only flips the flight rules: the aircraft is VFR afterwards and still on its approach.</summary>
    [Fact]
    public void CancelIfr_FlipsFlightRulesAndKeepsTheApproach()
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, PhaseList phases, Phase current) = Establish(engine, ApproachLeg.InterceptCourse);
        ac.FlightPlan.HasFlightPlan = true;
        ac.FlightPlan.FlightRules = "IFR";

        CommandResult result = Send(engine, ac, "CIFR");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.FlightPlan.IsVfr);
        AssertApproachKept(ac, phases, current);
    }
}
