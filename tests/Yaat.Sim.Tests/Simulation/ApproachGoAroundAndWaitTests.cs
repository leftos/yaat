using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A bare WAIT on an approach leg keeps the approach; a bare go-around outside the final approach fix is refused with the
/// pilot's "unable go-around, {where}, request vectors" and keeps the approach, whether issued now or fired later from a WAIT
/// or a condition; a go-around with a heading or an altitude is accepted anywhere and ends the approach clearance; a bare
/// go-around inside the final approach fix still flies the missed approach. Real KOAK, KCCR and KACV navdata.
/// </summary>
public class ApproachGoAroundAndWaitTests(ITestOutputHelper output)
{
    private const string Callsign = "APCH1";
    private const string InProcedureTurn = "unable go-around, we're in the procedure turn, request vectors.";
    private const string InHold = "unable go-around, we're in the hold, request vectors.";
    private const string OutsideFaf = "unable go-around, we're outside the final approach fix, request vectors.";

    /// <summary>The approach leg an aircraft is flying when the command arrives.</summary>
    public enum ApproachLeg
    {
        InterceptCourse,
        ApproachNavigation,
        ProcedureTurn,
        HoldInLieu,
        FinalOutsideFaf,
        FinalInsideFaf,
    }

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    internal static AircraftState OnLeg(SimulationEngine engine, ApproachLeg leg) =>
        leg switch
        {
            ApproachLeg.InterceptCourse => WithIlsClearance(FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0)),
            ApproachLeg.ApproachNavigation => WithIlsClearance(FollowSequenceRefusalTests.AddApproachNavigationFollower(engine)),
            ApproachLeg.ProcedureTurn => AddClearedForApproach<ProcedureTurnPhase>(engine, KccrS19RFromSouth),
            ApproachLeg.HoldInLieu => AddClearedForApproach<HoldingPatternPhase>(engine, KacvR01DctSegve()),
            ApproachLeg.FinalOutsideFaf => AddOnFinal(engine, 8.0),
            ApproachLeg.FinalInsideFaf => AddOnCenterlineFinal(engine, 3.0),
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "no setup for this approach leg"),
        };

    internal static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");

    /// <summary>The follower flies IFR on the real KOAK ILS 28R clearance, its published missed approach included.</summary>
    internal static AircraftState WithIlsClearance(AircraftState ac)
    {
        RunwayInfo rwy = Oak28R();
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach("KOAK", "I28R") ?? throw new InvalidOperationException("KOAK I28R missing from CIFP");
        ac.FlightPlan.FlightRules = "IFR";
        ac.Phases!.ActiveApproach = new ApproachClearance
        {
            TransitionName = null,
            ApproachId = "I28R",
            AirportCode = "KOAK",
            RunwayId = "28R",
            FinalApproachCourse = rwy.TrueHeading,
            Procedure = procedure,
            MissedApproachFixes = ApproachCommandHandler.BuildMissedApproachFixes(procedure),
        };
        return ac;
    }

    /// <summary>The KOAK 28R intercept follower advanced onto its final, <paramref name="distanceNm"/> from the threshold.</summary>
    private static AircraftState AddOnFinal(SimulationEngine engine, double distanceNm)
    {
        AircraftState ac = WithIlsClearance(FollowSequenceRefusalTests.AddApproachFollower(engine, distanceNm));
        ac.Phases!.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        FinalApproachPhase final = Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        final.DistanceToThresholdNm = distanceNm;
        return ac;
    }

    /// <summary>The final follower moved onto the 28R extended centerline, <paramref name="distanceNm"/> from the threshold.</summary>
    private static AircraftState AddOnCenterlineFinal(SimulationEngine engine, double distanceNm)
    {
        AircraftState ac = AddOnFinal(engine, distanceNm);
        RunwayInfo rwy = Oak28R();
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), distanceNm);
        return ac;
    }

    /// <summary>Where and how an aircraft built by <see cref="AddClearedForApproach{T}"/> is cleared.</summary>
    internal sealed record ApproachSetup(
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
    internal static AircraftState AddClearedForApproach<T>(SimulationEngine engine, ApproachSetup setup)
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

    private static DispatchContext Context() => TestDispatch.Context(new SerializableRandom(42));

    private static bool InsideFaf(AircraftState ac) => FinalApproachFix.IsInside(ac, ac.Phases!.AssignedRunway!, null);

    private CommandResult Dispatch(AircraftState ac, CompoundCommand compound, DispatchContext ctx)
    {
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, ctx);
        output.WriteLine($"on {ac.Phases?.CurrentPhase?.GetType().Name}: success={result.Success} — {result.Message}");
        return result;
    }

    private CommandResult Dispatch(AircraftState ac, string command) => Dispatch(ac, Parse(command), Context());

    private static CompoundCommand Parse(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return parsed.Value!;
    }

    private (AircraftState Aircraft, PhaseList Phases, Phase Current, ApproachClearance Approach) Establish(ApproachLeg leg)
    {
        AircraftState ac = OnLeg(BuildEngine(), leg);
        return (ac, ac.Phases!, ac.Phases!.CurrentPhase!, ac.Phases!.ActiveApproach!);
    }

    /// <summary>The approach's phase list, current phase and clearance are the ones the aircraft had before, and nothing was cancelled.</summary>
    private static void AssertApproachKept(AircraftState ac, PhaseList phases, Phase current, ApproachClearance approach)
    {
        Assert.Same(phases, ac.Phases);
        Assert.Same(current, ac.Phases!.CurrentPhase);
        Assert.Same(approach, ac.Phases.ActiveApproach);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("cancelled by", StringComparison.Ordinal));
    }

    private static void AssertRefused(CommandResult result, string terminal)
    {
        Assert.False(result.Success);
        Assert.Equal(terminal, result.Message);
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal(terminal, unable.Terminal);
        Assert.EndsWith($", {terminal}", unable.Tts);
    }

    // ─── A bare WAIT keeps the approach ───

    public static TheoryData<ApproachLeg, bool> BareWaitOnEveryApproachLeg()
    {
        var data = new TheoryData<ApproachLeg, bool>();
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
            data.Add(leg, false);
            data.Add(leg, true);
        }

        return data;
    }

    /// <summary>A bare WAIT (time or distance, nothing chained) is queued and the approach flies on.</summary>
    [Theory]
    [MemberData(nameof(BareWaitOnEveryApproachLeg))]
    public void BareWait_KeepsTheApproach(ApproachLeg leg, bool byDistance)
    {
        (AircraftState ac, PhaseList phases, Phase current, ApproachClearance approach) = Establish(leg);
        ParsedCommand wait = byDistance ? new WaitDistanceCommand(2) : new WaitCommand(30);

        CommandResult result = Dispatch(ac, new CompoundCommand([new ParsedBlock(null, [wait])]), Context());

        Assert.True(result.Success, result.Message);
        AssertApproachKept(ac, phases, current, approach);
    }

    // ─── A bare go-around outside the FAF is refused ───

    public static TheoryData<ApproachLeg, string> BareGoAroundOutsideTheFaf() =>
        new()
        {
            { ApproachLeg.ProcedureTurn, InProcedureTurn },
            { ApproachLeg.HoldInLieu, InHold },
            { ApproachLeg.InterceptCourse, OutsideFaf },
            { ApproachLeg.ApproachNavigation, OutsideFaf },
            { ApproachLeg.FinalOutsideFaf, OutsideFaf },
        };

    /// <summary>Outside the FAF a bare go-around has no published missed approach to fly from here: the pilot asks for vectors.</summary>
    [Theory]
    [MemberData(nameof(BareGoAroundOutsideTheFaf))]
    public void BareGoAround_OutsideTheFaf_IsRefusedAndKeepsTheApproach(ApproachLeg leg, string terminal)
    {
        (AircraftState ac, PhaseList phases, Phase current, ApproachClearance approach) = Establish(leg);
        Assert.False(InsideFaf(ac));

        CommandResult result = Dispatch(ac, "GA");

        AssertRefused(result, terminal);
        AssertApproachKept(ac, phases, current, approach);
    }

    /// <summary>Inside the FAF a bare go-around still flies the published missed approach.</summary>
    [Fact]
    public void BareGoAround_InsideTheFaf_FliesTheMissedApproach()
    {
        (AircraftState ac, _, _, _) = Establish(ApproachLeg.FinalInsideFaf);
        Assert.True(InsideFaf(ac));

        CommandResult result = Dispatch(ac, "GA");

        Assert.True(result.Success, result.Message);
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.Contains(ac.Phases.Phases, p => p is ApproachNavigationPhase);
    }

    /// <summary>A visual approach has no final approach fix and no published missed approach: a bare go-around 6 nm out stands.</summary>
    [Fact]
    public void BareGoAround_OnVisualApproachOutsideFiveMiles_GoesAround()
    {
        AircraftState ac = OnVisualApproach(AddOnFinal(BuildEngine(), 6.0));
        Assert.False(InsideFaf(ac));

        CommandResult result = Dispatch(ac, "GA");

        Assert.True(result.Success, result.Message);
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
    }

    // ─── A go-around with a heading or an altitude is accepted and ends the approach ───

    public static TheoryData<ApproachLeg, string> AssignedGoAroundOutsideTheFaf()
    {
        var data = new TheoryData<ApproachLeg, string>();
        foreach (ApproachLeg leg in new[] { ApproachLeg.ProcedureTurn, ApproachLeg.InterceptCourse })
        {
            data.Add(leg, "GA 270");
            data.Add(leg, "GA RH 30");
            data.Add(leg, "GA RH");
        }

        return data;
    }

    /// <summary>A go-around with the controller's heading or altitude replaces the approach: accepted, and the clearance ends.</summary>
    [Theory]
    [MemberData(nameof(AssignedGoAroundOutsideTheFaf))]
    public void AssignedGoAround_OutsideTheFaf_IsAcceptedAndEndsTheApproach(ApproachLeg leg, string command)
    {
        (AircraftState ac, _, _, _) = Establish(leg);
        Assert.False(InsideFaf(ac));

        CommandResult result = Dispatch(ac, command);

        Assert.True(result.Success, result.Message);
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.Null(ac.Phases.ActiveApproach);
    }

    /// <summary>A go-around on runway heading reads back the runway heading.</summary>
    [Fact]
    public void RunwayHeadingGoAround_ReadsBackRunwayHeading()
    {
        (AircraftState ac, _, _, _) = Establish(ApproachLeg.InterceptCourse);

        CommandResult result = Dispatch(ac, "GA RH");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Go around, fly runway heading", result.Message);
    }

    /// <summary>
    /// A go-around with a heading and no altitude on the KCCR VOR RWY 19R procedure turn at 4,000 climbs to the highest of its
    /// altitude, the published missed-approach altitude and 2,000 ft above the field: it never descends.
    /// </summary>
    [Fact]
    public void GoAroundWithHeading_FromTheProcedureTurnAt4000_DoesNotDescend()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = OnLeg(engine, ApproachLeg.ProcedureTurn);
        ac.Altitude = 4000;

        CommandResult result = Dispatch(ac, "GA 270");

        Assert.True(result.Success, result.Message);
        GoAroundPhase goAround = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        output.WriteLine($"go-around target {goAround.TargetAltitude}");
        Assert.True(goAround.TargetAltitude >= 4000, $"go-around target {goAround.TargetAltitude} is below 4,000");

        double lowest = ac.Altitude;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign) ?? throw new InvalidOperationException($"{Callsign} left the world");
            lowest = Math.Min(lowest, ac.Altitude);
        }

        output.WriteLine($"lowest {lowest:F0}, now {ac.Altitude:F0}");
        Assert.True(lowest >= 4000 - 50, $"descended to {lowest:F0} after the go-around");
    }

    /// <summary>
    /// A go-around with a heading and no altitude on a visual approach (KOAK 28R, 6 nm, below the climb-out) targets the base
    /// climb-out of <see cref="GoAroundHelper.ResolveClimbOutAltitude"/>, or field elevation + 2,000 ft when it has none.
    /// </summary>
    [Fact]
    public void GoAroundWithHeading_OnAVisualApproach_KeepsTheClimbOutAltitude()
    {
        AircraftState ac = OnVisualApproach(AddOnFinal(BuildEngine(), 6.0));
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);
        int? climbOut = GoAroundHelper.ResolveClimbOutAltitude(ctx, ac.Phases!.TrafficDirection is not null, []);
        int expected = (int)Math.Ceiling(climbOut ?? (ctx.FieldElevation + 2000));
        output.WriteLine($"alt={ac.Altitude:F0} climb-out {climbOut} field {ctx.FieldElevation:F0}: expected {expected}");
        Assert.True(ac.Altitude < expected, $"already at or above the climb-out {expected} ({ac.Altitude:F0})");

        CommandResult result = Dispatch(ac, "GA 270");

        Assert.True(result.Success, result.Message);
        GoAroundPhase goAround = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        output.WriteLine($"go-around target {goAround.TargetAltitude}");
        Assert.Equal(expected, goAround.TargetAltitude);
    }

    /// <summary>
    /// A go-around with a heading and no altitude on a visual approach (KOAK 28R, 8 nm) at 3,000 ft, above the base climb-out:
    /// it never descends, its target at or above 3,000.
    /// </summary>
    [Fact]
    public void GoAroundWithHeading_OnAVisualApproach_AboveTheClimbOut_NeverDescends()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = OnVisualApproach(AddOnFinal(engine, 8.0));
        ac.Altitude = 3000;
        double atGoAround = ac.Altitude;

        CommandResult result = Dispatch(ac, "GA 270");

        Assert.True(result.Success, result.Message);
        GoAroundPhase goAround = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        output.WriteLine($"go-around target {goAround.TargetAltitude}");
        Assert.True(goAround.TargetAltitude >= atGoAround, $"go-around target {goAround.TargetAltitude} is below {atGoAround:F0}");

        string callsign = ac.Callsign;
        double lowest = ac.Altitude;
        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(callsign) ?? throw new InvalidOperationException($"{callsign} left the world");
            lowest = Math.Min(lowest, ac.Altitude);
        }

        output.WriteLine($"lowest {lowest:F0}, now {ac.Altitude:F0}");
        Assert.True(lowest >= atGoAround - 1.0, $"descended to {lowest:F0} after the go-around at {atGoAround:F0}");
    }

    /// <summary>
    /// A go-around with a traffic-pattern direction on a visual approach (KOAK 28R, 6 nm) is an instruction: it ends the approach
    /// clearance, and the aircraft goes around to re-enter left traffic.
    /// </summary>
    [Fact]
    public void GoAroundMakeLeftTraffic_OnAVisualApproach_EndsTheApproachClearance()
    {
        AircraftState ac = OnVisualApproach(AddOnFinal(BuildEngine(), 6.0));

        CommandResult result = Dispatch(ac, "GA MLT");

        Assert.True(result.Success, result.Message);
        GoAroundPhase goAround = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.Null(ac.Phases.ActiveApproach);
        Assert.True(goAround.ReenterPattern);
        Assert.Equal(PatternDirection.Left, ac.Phases.TrafficDirection);
    }

    /// <summary>
    /// A go-around with a traffic-pattern direction from a VFR aircraft in the KCCR VOR RWY 19R procedure turn is an instruction:
    /// it ends the instrument approach clearance, as a go-around with a heading does.
    /// </summary>
    [Fact]
    public void GoAroundMakeLeftTraffic_VfrInTheProcedureTurn_EndsTheApproachClearance()
    {
        AircraftState ac = OnLeg(BuildEngine(), ApproachLeg.ProcedureTurn);
        Assert.True(ac.FlightPlan.IsVfr);
        Assert.False(VisualApproachHelper.IsOnVisualApproach(ac));

        CommandResult result = Dispatch(ac, "GA MLT");

        Assert.True(result.Success, result.Message);
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.Null(ac.Phases.ActiveApproach);
    }

    /// <summary>The aircraft's approach clearance replaced by a visual approach to KOAK 28R (no procedure, no missed approach).</summary>
    private static AircraftState OnVisualApproach(AircraftState ac)
    {
        RunwayInfo rwy = Oak28R();
        ac.Phases!.ActiveApproach = new ApproachClearance
        {
            TransitionName = null,
            ApproachId = $"VIS{rwy.Designator}",
            AirportCode = rwy.AirportId,
            RunwayId = rwy.Designator,
            FinalApproachCourse = rwy.TrueHeading,
            Procedure = null,
        };
        Assert.True(VisualApproachHelper.IsOnVisualApproach(ac));
        return ac;
    }

    // ─── A queued or conditional bare go-around refuses the same way when it fires ───

    /// <summary>A bare go-around behind a WAIT is accepted as a deferral, then refused when it fires outside the FAF.</summary>
    [Fact]
    public void BareGoAround_FiredFromWait_OutsideTheFaf_IsRefusedAndKeepsTheApproach()
    {
        (AircraftState ac, PhaseList phases, Phase current, ApproachClearance approach) = Establish(ApproachLeg.ProcedureTurn);
        Assert.True(Dispatch(ac, "WAIT 5; GA").Success);
        DeferredDispatch deferred = Assert.Single(ac.DeferredDispatches);

        CommandResult fired = Dispatch(ac, deferred.Payload, Context() with { PreserveConditionals = true });

        AssertRefused(fired, InProcedureTurn);
        AssertApproachKept(ac, phases, current, approach);
    }

    /// <summary>A bare go-around queued behind a condition is refused when its block fires outside the FAF.</summary>
    [Fact]
    public void BareGoAround_FiredFromCondition_OutsideTheFaf_IsRefusedAndKeepsTheApproach()
    {
        (AircraftState ac, PhaseList phases, Phase current, ApproachClearance approach) = Establish(ApproachLeg.ProcedureTurn);
        Assert.True(Dispatch(ac, "AT CCR GA").Success);
        CommandBlock block = Assert.Single(ac.Queue.Blocks);

        CommandResult fired = block.ApplyAction!(ac, true);

        AssertRefused(fired, InProcedureTurn);
        AssertApproachKept(ac, phases, current, approach);
    }
}
