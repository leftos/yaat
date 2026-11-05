using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A CFIX on a SID's coded-leg fix, given while the departure phase flies its coded legs, is honoured from its issue until
/// that fix sequences: an "at" or at-or-below altitude caps the target, earlier legs' in-force caps still bind, and when the
/// leg sequences at the fix the target goes back, once, to the clearance in force (the climb via, the assigned altitude,
/// the SID hold altitude). Under a climb via the crossing assigns nothing and the via's top caps it. Off a via, an "at" or
/// at-or-above floor above the cleared altitude (or the SID hold altitude with none assigned) is assigned, and one the
/// cleared altitude already meets changes nothing. A CFIX on a coded fix already behind is refused. Real KOAK COAST9 and
/// KSFO MOLEN9 navdata and the committed ZOA ARTCC snapshot.
/// </summary>
public class CfixCodedLegWindowTests
{
    private const double LevelToleranceFt = 50.0;
    private const double OvershootToleranceFt = 100.0;
    private const double WindowCeilingFt = 2000.0;

    /// <summary>COAST9's top altitude: the TDLS climb-via text at KOAK is "CLB VIA SID EXC MAINT 10000FT".</summary>
    private const int Coast9TopAltitudeFt = 10000;

    /// <summary>MOLEN9 ENI's top altitude: the TDLS climb-via text is "CLB VIA SID EXC MAINT 15000FT".</summary>
    private const int Molen9EniTopAltitudeFt = 15000;

    /// <summary>The hand-set SID hold altitude of the TDLS-maintain staging.</summary>
    private const int HoldFt = 5000;

    private readonly ITestOutputHelper _output;

    public CfixCodedLegWindowTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private sealed record Departure(AircraftState Aircraft, PhaseContext Context, RunwayInfo Runway);

    /// <summary>A departure to stage: from <paramref name="Runway"/> at <paramref name="Airport"/> on <paramref name="Route"/>.</summary>
    private sealed record DepartureSpec(string Airport, string Runway, string Destination, string Route, int? CtoAltitude, bool ClimbViaOnTheGround);

    /// <summary>What a flight to a coded-leg fix saw: the highest altitude and target before the fix, and the target after.</summary>
    private sealed record FixPassage(double MaxAltitude, double MaxTarget, double TargetAfter);

    private static DepartureSpec Coast9(int? ctoAltitude = null) => new("KOAK", "30", "KSAN", "COAST9 MCKEY LAX COMIX2", ctoAltitude, false);

    private static DepartureSpec Molen9(string runway, bool climbViaOnTheGround) =>
        new("KSFO", runway, "KRNO", "MOLEN9 ENI", null, climbViaOnTheGround);

    // ---- COAST9 under the ZOA TDLS climb via (top 10,000) ----

    [Fact]
    public void Coast9Via_CfixSegulAtBelowTop_HoldsVdCapThenLevelsAtX_ViaResumesAtSegul()
    {
        const int x = 5000;
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        Assert.Equal(Coast9TopAltitudeFt, ac.Procedure.ClimbViaTargetFt);

        Dispatch(ac, $"CFIX SEGUL {x / 100:D3}");
        Assert.Null(ac.Targets.AssignedAltitude);

        double maxOnVd = FlyThroughVdLeg(departure);
        Assert.True(maxOnVd < WindowCeilingFt + OvershootToleranceFt, $"the VD cap did not bind under the crossing: {maxOnVd:F0}");

        FlyFor(departure, 240);
        AssertLevel(ac, x, "on the VM leg under the crossing");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.True(passage.MaxTarget <= x, $"the target passed the crossing's {x} before SEGUL: {passage.MaxTarget:F0}");
        Assert.Equal(Coast9TopAltitudeFt, passage.TargetAfter);
        AssertRevertsCleared(PendingFix(rewound, "SEGUL"));
        Assert.Equal(Coast9TopAltitudeFt, ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void Coast9Via_CfixSegulAtOrAboveBelowTop_MetByViaTop_NoChange()
    {
        Departure baseline = FlyIntoClimbWindow(LoadZoa());
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        NavigationTarget segul = PendingFix(CurrentProcedure(ac), "SEGUL");

        Dispatch(ac, "CFIX SEGUL A050");

        Assert.Null(segul.RevertAltitude);
        Assert.Null(segul.RevertAssignedAltitude);
        Assert.Null(ac.Targets.AssignedAltitude);
        AssertSameFlight(baseline, departure, 420);
    }

    [Fact]
    public void Coast9_CfixSegulAtOrAboveAboveHold_StopsAtViaTop() => AssertStopsAtViaTop("CFIX SEGUL A120");

    [Fact]
    public void Coast9_CfixSegulAtAboveHold_StopsAtViaTop() => AssertStopsAtViaTop("CFIX SEGUL 120");

    // ---- Off a via: TDLS-maintain staging (hold 5,000 set by hand) and CTO 050 ----

    [Fact]
    public void TdlsMaintain_CfixSegulAtOrAboveBelowHold_NoChange()
    {
        Departure baseline = StartInDeparturePhase(TdlsMaintainCoast9());
        Departure departure = StartInDeparturePhase(TdlsMaintainCoast9());
        AircraftState ac = departure.Aircraft;
        NavigationTarget segul = PendingFix(CurrentProcedure(ac), "SEGUL");

        Dispatch(ac, "CFIX SEGUL A040");

        Assert.Null(segul.RevertAltitude);
        Assert.Null(segul.RevertAssignedAltitude);
        Assert.Null(ac.Targets.AssignedAltitude);
        AssertSameFlight(baseline, departure, 300);
    }

    [Fact]
    public void TdlsMaintain_CfixSegulAtOrAboveAboveHold_AssignsXAndStays()
    {
        const int x = 8000;
        Departure departure = StartInDeparturePhase(TdlsMaintainCoast9());
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, $"CFIX SEGUL A{x / 100:D3}");
        Assert.Equal(x, ac.Targets.AssignedAltitude);

        FlyFor(departure, 360);
        AssertLevel(ac, x, "before SEGUL");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.Equal(x, passage.TargetAfter);
        AssertRevertsCleared(PendingFix(rewound, "SEGUL"));

        double minAfter = MinAltitudeOver(departure, 300);
        Assert.True(minAfter > x - LevelToleranceFt, $"descended past SEGUL and the handover: {minAfter:F0}");
        AssertLevel(ac, x, "past SEGUL and the handover");
        Assert.Equal(x, ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void TdlsMaintain_CfixSegulAtBelowHold_CapsThenRevertsToHold()
    {
        const int x = 4000;
        Departure departure = StartInDeparturePhase(TdlsMaintainCoast9());
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, $"CFIX SEGUL {x / 100:D3}");
        Assert.Null(ac.Targets.AssignedAltitude);

        FlyFor(departure, 240);
        AssertLevel(ac, x, "under the crossing");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.True(passage.MaxAltitude < x + OvershootToleranceFt, $"climbed through the crossing before SEGUL: {passage.MaxAltitude:F0}");
        Assert.Equal(HoldFt, passage.TargetAfter);
        AssertRevertsCleared(PendingFix(rewound, "SEGUL"));
    }

    [Fact]
    public void Cto050_CfixSegulAtBelow_CapsThenRevertsToAssigned()
    {
        const int x = 4000;
        Departure departure = StartInDeparturePhase(LoadZoa(), Coast9(ctoAltitude: 5000));
        AircraftState ac = departure.Aircraft;
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Equal(5000, ac.Targets.AssignedAltitude);

        Dispatch(ac, $"CFIX SEGUL {x / 100:D3}");
        Assert.Equal(5000, ac.Targets.AssignedAltitude);

        FlyFor(departure, 240);
        AssertLevel(ac, x, "under the crossing");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.True(passage.MaxAltitude < x + OvershootToleranceFt, $"climbed through the crossing before SEGUL: {passage.MaxAltitude:F0}");
        Assert.Equal(5000, passage.TargetAfter);
    }

    // ---- KSFO MOLEN9 ----

    [Fact]
    public void Molen9Rw10_CfixSiplyAt_RevertFiresAtSiply_BeforeVi()
    {
        const int x = 4000;
        Departure departure = StartInDeparturePhase(artccConfig: null, Molen9("10L", climbViaOnTheGround: false), HoldFt);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        int siply = CodedFixLeg(procedure, "SIPLY");
        Assert.Equal(procedure.ActiveLegIndex, siply);

        Dispatch(ac, $"CFIX SIPLY {x / 100:D3}");

        FixPassage passage = FlyUntilLegSequenced(departure, procedure, siply);
        Assert.True(passage.MaxTarget <= x, $"the target passed the crossing's {x} before SIPLY: {passage.MaxTarget:F0}");
        Assert.True(passage.MaxAltitude < x + OvershootToleranceFt, $"climbed through the crossing before SIPLY: {passage.MaxAltitude:F0}");
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Equal(HoldFt, passage.TargetAfter);
        AssertRevertsCleared(PendingFix(procedure, "SIPLY"));

        FlyFor(departure, 30);
        PhaseRunner.Tick(ac, departure.Context);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Equal(HoldFt, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void Molen9Rw10Cvia_CfixSiplyReplacesPublished()
    {
        const int x = 4000;
        Departure departure = StartInDeparturePhase(LoadZoa(), Molen9("10L", climbViaOnTheGround: true));
        AircraftState ac = departure.Aircraft;
        Assert.Equal(Molen9EniTopAltitudeFt, ac.Procedure.ClimbViaTargetFt);
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        int siply = CodedFixLeg(procedure, "SIPLY");
        int stins = CodedFixLeg(procedure, "STINS");

        Dispatch(ac, $"CFIX SIPLY {x / 100:D3}");
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, x) { Source = CifpAltitudeRestrictionSource.Atc },
            PendingFix(procedure, "SIPLY").AltitudeRestriction
        );

        FixPassage passage = FlyUntilLegSequenced(departure, procedure, siply);
        Assert.True(passage.MaxTarget <= x, $"the target passed the crossing's {x} before SIPLY: {passage.MaxTarget:F0}");
        Assert.True(passage.TargetAfter > x, $"the crossing outlived SIPLY: target {passage.TargetAfter:F0}");
        Assert.Equal(Molen9EniTopAltitudeFt, ac.Procedure.ClimbViaTargetFt);

        FlyUntil(departure, () => procedure.ActiveLegIndex >= stins, 900);
        FlyFor(departure, 1);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        double expected = Math.Min(Molen9EniTopAltitudeFt, CeilingFt(procedure.Legs[stins].AltitudeRestriction) ?? double.MaxValue);
        Assert.Equal(expected, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void Molen9Rw19Cvia_CfixStins_SiplyPublishedStillBinds()
    {
        const int x = 6000;
        Departure departure = StartInDeparturePhase(LoadZoa(), Molen9("19L", climbViaOnTheGround: true));
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        Assert.Equal(ProcedureLegType.CourseToAltitude, procedure.Legs[procedure.ActiveLegIndex].Type);
        int stins = CodedFixLeg(procedure, "STINS");
        CodedFixLeg(procedure, "SIPLY");
        NavigationTarget siplyFix = PendingFix(procedure, "SIPLY");
        CifpAltitudeRestriction? siplyPublished = siplyFix.AltitudeRestriction;
        _output.WriteLine($"SIPLY published={siplyPublished}");

        Dispatch(ac, $"CFIX STINS {x / 100:D3}");
        Assert.Equal(siplyPublished, siplyFix.AltitudeRestriction);

        FixPassage passage = FlyUntilLegSequenced(departure, procedure, stins);
        Assert.Equal(siplyPublished, siplyFix.AltitudeRestriction);
        Assert.True(passage.MaxTarget <= x, $"the target passed the crossing's {x} before STINS: {passage.MaxTarget:F0}");
        Assert.True(passage.MaxAltitude < x + OvershootToleranceFt, $"climbed through the crossing before STINS: {passage.MaxAltitude:F0}");
        Assert.True(passage.TargetAfter > x, $"the crossing outlived STINS: target {passage.TargetAfter:F0}");
        Assert.Equal(Molen9EniTopAltitudeFt, ac.Procedure.ClimbViaTargetFt);
    }

    // ---- A maintain given in the window, then the crossing ----

    [Fact]
    public void WindowCm_ThenCfixAtBelowCm_CapsUntilFixThenCm()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        Dispatch(ac, "CM 060");
        Dispatch(ac, "CFIX SEGUL 040");
        Assert.Equal(6000, ac.Targets.AssignedAltitude);

        FlyFor(departure, 300);
        AssertLevel(ac, 4000, "under the crossing");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.True(passage.MaxAltitude < 4000 + OvershootToleranceFt, $"climbed through the crossing before SEGUL: {passage.MaxAltitude:F0}");
        Assert.Equal(6000, passage.TargetAfter);
    }

    [Fact]
    public void WindowCm_ThenCfixAtOrAboveBelowCm_NoChange()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        Dispatch(ac, "CM 060");
        NavigationTarget segul = PendingFix(CurrentProcedure(ac), "SEGUL");

        Dispatch(ac, "CFIX SEGUL A040");

        Assert.Equal(6000, ac.Targets.AssignedAltitude);
        Assert.Null(segul.RevertAltitude);
        Assert.Null(segul.RevertAssignedAltitude);
        FlyFor(departure, 300);
        AssertLevel(ac, 6000, "under the maintain");
    }

    [Fact]
    public void WindowCm_ThenCfixAboveCm_ClimbsAndStays()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        Dispatch(ac, "CM 040");
        Dispatch(ac, "CFIX SEGUL A060");
        Assert.Equal(6000, ac.Targets.AssignedAltitude);

        FlyFor(departure, 300);
        AssertLevel(ac, 6000, "before SEGUL");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.Equal(6000, passage.TargetAfter);
        double minAfter = MinAltitudeOver(departure, 180);
        Assert.True(minAfter > 6000 - LevelToleranceFt, $"descended past SEGUL: {minAfter:F0}");
    }

    // ---- A later maintain cancels an earlier crossing's altitude; one in the same transmission keeps it ----

    [Fact]
    public void WindowCfix_ThenCmOmitting_CancelsAltitudeKeepsSpeed()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        NavigationTarget segul = PendingFix(CurrentProcedure(ac), "SEGUL");

        Dispatch(ac, "CFIX SEGUL 040 230");
        Dispatch(ac, "CM 060");

        Assert.NotEqual(CifpAltitudeRestrictionSource.Atc, segul.AltitudeRestriction?.Source);
        Assert.Null(segul.CrossingTransmissionId);
        Assert.Null(segul.RevertAltitude);
        Assert.Null(segul.RevertAssignedAltitude);
        Assert.Equal(new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow), segul.SpeedRestriction);
        Assert.NotNull(segul.SpeedTransmissionId);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(segul), "the maintain dropped the crossing speed's revert");
        Assert.Equal(230, ac.Targets.AssignedSpeed);

        FlyFor(departure, 300);
        AssertLevel(ac, 6000, "under the maintain");
    }

    [Fact]
    public void WindowCfixAndCm_SameTransmission_KeepsTheCrossing()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        NavigationTarget segul = PendingFix(CurrentProcedure(ac), "SEGUL");

        Dispatch(ac, "CFIX SEGUL 040, CM 060");

        Assert.Equal(AtAtc(4000), segul.AltitudeRestriction);
        Assert.Equal(6000, ac.Targets.AssignedAltitude);
        FlyFor(departure, 300);
        AssertLevel(ac, 4000, "under the crossing");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.True(passage.MaxTarget <= 4000, $"the target passed the crossing's 4000 before SEGUL: {passage.MaxTarget:F0}");
        Assert.Equal(6000, passage.TargetAfter);
    }

    [Fact]
    public void InitialClimb_CfixThenCm_CancelsAltitudeKeepsSpeed()
    {
        Departure departure = StartDeparture(LoadZoa(), Coast9(), holdFt: null);
        AircraftState ac = departure.Aircraft;
        InitialClimbPhase climb = Assert.IsType<InitialClimbPhase>(ac.Phases?.CurrentPhase);
        NavigationTarget segul = Assert.IsType<NavigationTarget>(climb.PendingRoute?.Find(f => f.Name == "SEGUL"));

        Dispatch(ac, "CFIX SEGUL 040 230");
        Dispatch(ac, "CM 060");
        Assert.Same(climb, ac.Phases?.CurrentPhase);

        Assert.NotEqual(CifpAltitudeRestrictionSource.Atc, segul.AltitudeRestriction?.Source);
        Assert.Null(segul.CrossingTransmissionId);
        Assert.Null(segul.RevertAltitude);
        Assert.Null(segul.RevertAssignedAltitude);
        Assert.Equal(new CifpSpeedRestriction(230, CifpSpeedRestrictionType.AtOrBelow), segul.SpeedRestriction);
        Assert.True(NavigationCommandHandler.HasSpeedRevert(segul), "the maintain dropped the crossing speed's revert");

        StartInDeparturePhase(departure);
        FlyFor(departure, 300);
        Assert.IsType<DepartureProcedurePhase>(ac.Phases?.CurrentPhase);
        AssertLevel(ac, 6000, "under the maintain");
    }

    [Fact]
    public void QueuedCm_FromEarlierTransmission_KeepsALaterCrossing()
    {
        Departure departure = StartInDeparturePhase(TdlsMaintainCoast9());
        AircraftState ac = departure.Aircraft;
        Assert.True(ac.Altitude < 2500, $"setup: the departure must start its coded legs below 2,500: {ac.Altitude:F0}");
        NavigationTarget segul = PendingFix(CurrentProcedure(ac), "SEGUL");

        // Transmission N carries a CFIX, so it takes an id, and queues a maintain that fires later at 3,000.
        Dispatch(ac, "CFIX SEGUL A030; AT 3000 CM 060");
        int queued = Assert.IsType<int>(segul.CrossingTransmissionId);
        // A conditional transmission is additive, so it keeps the queued maintain; its crossing fires first, at 2,500.
        Dispatch(ac, "AT 2500 CFIX SEGUL 040");
        FlyUntil(departure, () => segul.CrossingTransmissionId > queued, 300);
        Assert.Null(ac.Targets.AssignedAltitude);

        FlyUntil(departure, () => ac.Targets.AssignedAltitude == 6000, 300);
        Assert.Equal(AtAtc(4000), segul.AltitudeRestriction);
        FlyFor(departure, 240);
        AssertLevel(ac, 4000, "under the later crossing");
    }

    // ---- The constrained direct-to keeps its assignment rule in the window ----

    [Fact]
    public void WindowDctf_UnmetBound_AssignsAsBeforeTheWindowBranch()
    {
        Departure departure = StartInDeparturePhase(TdlsMaintainCoast9());
        AircraftState ac = departure.Aircraft;
        FlyUntil(departure, () => ac.Altitude >= 3000, 300);
        Assert.Null(ac.Targets.AssignedAltitude);

        CommandResult result = DispatchConstrainedDirect(ac, "SAC", new ConstrainedFixAltitude(8000, CrossFixAltitudeType.AtOrBelow));

        Assert.True(result.Success, result.Message);
        Assert.Equal(8000, ac.Targets.AssignedAltitude);
    }

    // ---- The assignment rule off a via ----

    [Fact]
    public void TdlsMaintain_CfixSegulAtAboveHold_AssignsXAndStays() => AssertAssignsAndStays(TdlsMaintainCoast9(), "CFIX SEGUL 080", 8000);

    [Fact]
    public void Cto050_CfixSegulAtOrAboveAboveAssigned_AssignsXAndStays() =>
        AssertAssignsAndStays(StartDeparture(LoadZoa(), Coast9(ctoAltitude: 5000), holdFt: null), "CFIX SEGUL A070", 7000);

    [Fact]
    public void TdlsMaintain_CfixSegulAtOrBelowBelowHold_CapsThenRevertsToHold()
    {
        const int x = 4000;
        Departure departure = StartInDeparturePhase(TdlsMaintainCoast9());
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, $"CFIX SEGUL B{x / 100:D3}");
        Assert.Null(ac.Targets.AssignedAltitude);

        FlyFor(departure, 240);
        AssertLevel(ac, x, "under the crossing");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.True(passage.MaxTarget <= x, $"the target passed the crossing's {x} before SEGUL: {passage.MaxTarget:F0}");
        Assert.Equal(HoldFt, passage.TargetAfter);
        AssertRevertsCleared(PendingFix(rewound, "SEGUL"));
    }

    [Fact]
    public void WindowNoKnownAltitude_CfixAssignsNothing()
    {
        Departure departure = StartInDeparturePhase(artccConfig: null, Coast9());
        AircraftState ac = departure.Aircraft;
        Assert.Null(ac.Procedure.SidInitialAltitudeFt);
        Assert.Null(ac.Procedure.ClimbViaTargetFt);
        Assert.Null(ac.Targets.AssignedAltitude);

        Dispatch(ac, "CFIX SEGUL A080");

        Assert.Null(ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void WindowCfix_OnPostRouteFixBeyondCodedLegs_OffVia_AssignsAsAwayFromWindow()
    {
        Departure departure = StartInDeparturePhase(TdlsMaintainCoast9());
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        var coded = new HashSet<string>(procedure.Legs.Where(l => l.FixName is not null).Select(l => l.FixName!), StringComparer.OrdinalIgnoreCase);
        NavigationTarget beyond = Assert.IsType<NavigationTarget>(procedure.PostRoute.Find(f => !coded.Contains(f.Name)));
        _output.WriteLine($"pending fix beyond the coded legs: {beyond.Name}");

        Dispatch(ac, $"CFIX {beyond.Name} A080");

        Assert.Equal(8000, ac.Targets.AssignedAltitude);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
    }

    // ---- Cancel and refusal ----

    [Fact]
    public void WindowCfix_ThenDctf_CancelsProcedure()
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        Dispatch(ac, "CFIX SEGUL 050");

        (double Lat, double Lon) sac = Assert.NotNull(NavigationDatabase.Instance.GetFixPosition("SAC"));
        var direct = new ConstrainedForceDirectToCommand(
            [new ResolvedFix("SAC", sac.Lat, sac.Lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(8000, CrossFixAltitudeType.AtOrAbove) },
            [],
            null
        );
        CommandResult result = CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [direct])]),
            ac,
            TestDispatch.Context(Random.Shared, artccConfig: LoadZoa())
        );
        Assert.True(result.Success, result.Message);

        FlyFor(departure, 2);

        Assert.False(ReferenceEquals(procedure, ac.Phases?.CurrentPhase), "the DCTF left the coded procedure flying");
        Assert.DoesNotContain(ac.Targets.NavigationRoute, f => f.Name == "SEGUL");
        NavigationTarget live = Assert.Single(ac.Targets.NavigationRoute, f => f.Name == "SAC");
        Assert.Equal(
            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 8000) { Source = CifpAltitudeRestrictionSource.Atc },
            live.AltitudeRestriction
        );
    }

    [Fact]
    public void WindowCfix_OnPassedCodedFix_RefusedUnable()
    {
        Departure departure = StartInDeparturePhase(LoadZoa(), Molen9("19L", climbViaOnTheGround: false));
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        int siply = CodedFixLeg(procedure, "SIPLY");
        FlyUntil(departure, () => procedure.ActiveLegIndex > siply, 900);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Equal(ProcedureLegType.HeadingToIntercept, procedure.Legs[procedure.ActiveLegIndex].Type);

        NavigationTarget siplyFix = PendingFix(procedure, "SIPLY");
        CifpAltitudeRestriction? before = siplyFix.AltitudeRestriction;
        double? assignedBefore = ac.Targets.AssignedAltitude;
        double? targetBefore = ac.Targets.TargetAltitude;

        CommandResult result = DispatchResult(ac, "CFIX SIPLY 040");

        Assert.False(result.Success, $"the CFIX on a passed fix was accepted: {result.Message}");
        Assert.Equal($"unable, we're already past {PhraseologyVerbalizer.FixDisplayTextUpper("SIPLY")}", result.Message);
        Assert.Same(procedure, ac.Phases?.CurrentPhase);
        Assert.Equal(before, siplyFix.AltitudeRestriction);
        Assert.Null(siplyFix.CrossingTransmissionId);
        Assert.Equal(assignedBefore, ac.Targets.AssignedAltitude);
        Assert.Equal(targetBefore, ac.Targets.TargetAltitude);
    }

    // ---- Helpers ----

    /// <summary>
    /// Off a via, a CFIX floor above the cleared altitude assigns <paramref name="x"/>: the aircraft climbs to it and stays
    /// there past SEGUL and the handover.
    /// </summary>
    private void AssertAssignsAndStays(Departure staged, string command, int x)
    {
        Departure departure = StartInDeparturePhase(staged);
        AircraftState ac = departure.Aircraft;
        Assert.Null(ac.Procedure.ClimbViaTargetFt);

        Dispatch(ac, command);
        Assert.Equal(x, ac.Targets.AssignedAltitude);

        FlyFor(departure, 360);
        AssertLevel(ac, x, "before SEGUL");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.Equal(x, passage.TargetAfter);
        AssertRevertsCleared(PendingFix(rewound, "SEGUL"));

        double minAfter = MinAltitudeOver(departure, 300);
        Assert.True(minAfter > x - LevelToleranceFt, $"descended past SEGUL and the handover: {minAfter:F0}");
        AssertLevel(ac, x, "past SEGUL and the handover");
        Assert.Equal(x, ac.Targets.AssignedAltitude);
    }

    /// <summary>
    /// A constrained direct-to <paramref name="fix"/> at <paramref name="altitude"/>, built as ClimbViaTopAltitudeTests builds
    /// it, since the text parser builds no constrained direct-to.
    /// </summary>
    private CommandResult DispatchConstrainedDirect(AircraftState ac, string fix, ConstrainedFixAltitude altitude)
    {
        (double Lat, double Lon) position = Assert.NotNull(NavigationDatabase.Instance.GetFixPosition(fix));
        var direct = new ConstrainedForceDirectToCommand(
            [new ResolvedFix(fix, position.Lat, position.Lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = altitude },
            [],
            null
        );
        CommandResult result = CommandDispatcher.DispatchCompound(
            new CompoundCommand([new ParsedBlock(null, [direct])]),
            ac,
            TestDispatch.Context(Random.Shared, artccConfig: LoadZoa())
        );
        _output.WriteLine($"DCTF {fix}/{altitude} -> {result.Message}");
        return result;
    }

    private static CifpAltitudeRestriction AtAtc(int altitudeFt) =>
        new(CifpAltitudeRestrictionType.At, altitudeFt) { Source = CifpAltitudeRestrictionSource.Atc };

    /// <summary>Under the COAST9 climb via, a crossing above its top assigns nothing and the aircraft stops at the top.</summary>
    private void AssertStopsAtViaTop(string command)
    {
        Departure departure = FlyIntoClimbWindow(LoadZoa());
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, command);
        Assert.Null(ac.Targets.AssignedAltitude);
        Assert.Equal(Coast9TopAltitudeFt, ac.Procedure.ClimbViaTargetFt);

        double maxOnVd = FlyThroughVdLeg(departure);
        Assert.True(maxOnVd < WindowCeilingFt + OvershootToleranceFt, $"the VD cap did not bind under the crossing: {maxOnVd:F0}");

        DepartureProcedurePhase rewound = RewindToLeg(departure, "SEGUL");
        FixPassage passage = FlyUntilLegSequenced(departure, rewound);
        Assert.True(passage.MaxTarget <= Coast9TopAltitudeFt, $"the target passed the via top before SEGUL: {passage.MaxTarget:F0}");
        Assert.Equal(Coast9TopAltitudeFt, passage.TargetAfter);

        FlyFor(departure, 420);
        AssertLevel(ac, Coast9TopAltitudeFt, "past SEGUL and the handover");
        Assert.Null(ac.Targets.AssignedAltitude);
    }

    /// <summary>The COAST9 departure off a via, with no ARTCC config and the SID hold altitude set to <see cref="HoldFt"/> by hand.</summary>
    private Departure TdlsMaintainCoast9() => StartDeparture(artccConfig: null, Coast9(), HoldFt);

    private Departure StartInDeparturePhase(Departure departure)
    {
        FlyUntil(departure, () => departure.Aircraft.Phases?.CurrentPhase is DepartureProcedurePhase, 180);
        _output.WriteLine($"departure phase at alt={departure.Aircraft.Altitude:F0} tgt={departure.Aircraft.Targets.TargetAltitude:F0}");
        return departure;
    }

    private Departure StartInDeparturePhase(ArtccConfigRoot? artccConfig, DepartureSpec spec, int? holdFt = null) =>
        StartInDeparturePhase(StartDeparture(artccConfig, spec, holdFt));

    /// <summary>
    /// Clears the departure <paramref name="spec"/> describes for takeoff and puts it airborne just past the DER in its
    /// initial climb, its TDLS initial altitude resolved from <paramref name="artccConfig"/>; with <paramref name="holdFt"/>
    /// the SID hold altitude is set to it by hand, as the TDLS-maintain tests of ClimbViaTopAltitudeTests do.
    /// </summary>
    private Departure StartDeparture(ArtccConfigRoot? artccConfig, DepartureSpec spec, int? holdFt)
    {
        RunwayInfo rwy = Runway(spec.Airport, spec.Runway);
        var ac = new AircraftState
        {
            Callsign = "SWA1822",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = spec.Airport,
                Destination = spec.Destination,
                Route = spec.Route,
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        DepartureClearanceHandler.StoreSidInitialAltitude(
            ac,
            TestDispatch.Context(Random.Shared, artccConfig: artccConfig),
            spec.CtoAltitude,
            isTakeoffClearance: true
        );

        var holding = new HoldingInPositionPhase();
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        if (spec.ClimbViaOnTheGround)
        {
            CommandResult cvia = NavigationCommandHandler.DispatchClimbVia(
                new ClimbViaCommand(null),
                ac,
                TestDispatch.Context(Random.Shared, artccConfig: artccConfig)
            );
            Assert.True(cvia.Success, cvia.Message);
        }

        CommandResult cto = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: spec.CtoAltitude,
            requestedRunway: null,
            TestDispatch.Context(Random.Shared),
            TestDispatch.EmptyTaxi,
            NullLogger.Instance
        );
        Assert.True(cto.Success, cto.Message);

        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.2);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + 450;
        ac.IndicatedAirspeed = 170;
        if (holdFt is { } hold)
        {
            ac.Procedure.SidInitialAltitudeFt = hold;
        }

        PhaseContext ctx = NewContext(ac, rwy);
        ac.Phases.SkipTo<InitialClimbPhase>(ctx);
        return new Departure(ac, ctx, rwy);
    }

    /// <summary>Flies the COAST9 departure under the ZOA config until it is levelled in the 2,000 ft climb window.</summary>
    private Departure FlyIntoClimbWindow(ArtccConfigRoot artccConfig)
    {
        Departure departure = StartDeparture(artccConfig, Coast9(), holdFt: null);
        AircraftState ac = departure.Aircraft;
        for (int t = 0; t < 360; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
            bool inProcedure = ac.Phases?.CurrentPhase is DepartureProcedurePhase;
            bool cappedAtWindow = (ac.Targets.TargetAltitude ?? double.MaxValue) <= WindowCeilingFt + LevelToleranceFt;
            if (inProcedure && (ac.Altitude >= WindowCeilingFt - 150) && cappedAtWindow)
            {
                _output.WriteLine($"reached window at t={t}: alt={ac.Altitude:F0} tgtAlt={ac.Targets.TargetAltitude:F0}");
                return departure;
            }
        }

        Assert.Fail("the aircraft never leveled in the COAST9 climb window under the DepartureProcedurePhase");
        return departure;
    }

    /// <summary>Flies the COAST9 departure until its VD leg (to OAK 4 DME) sequences; returns the highest altitude flown on it.</summary>
    private static double FlyThroughVdLeg(Departure departure)
    {
        DepartureProcedurePhase procedure = CurrentProcedure(departure.Aircraft);
        int vd = procedure.Legs.FindIndex(l => l.Type == ProcedureLegType.HeadingToDistance);
        Assert.True(vd >= 0, "setup: COAST9 must code a VD leg");
        double max = departure.Aircraft.Altitude;
        FlyUntil(
            departure,
            () =>
            {
                max = Math.Max(max, departure.Aircraft.Altitude);
                return procedure.ActiveLegIndex > vd;
            },
            600
        );
        return max;
    }

    /// <summary>
    /// The index of the coded CF leg ending at <paramref name="fix"/>; fails, listing the legs, when the phase keeps no such
    /// leg (a fix stripped near the airport).
    /// </summary>
    private int CodedFixLeg(DepartureProcedurePhase procedure, string fix)
    {
        _output.WriteLine($"legs={string.Join(",", procedure.Legs.Select(l => $"{l.Type}:{l.FixName}"))}");
        _output.WriteLine($"pending={string.Join(" ", procedure.PostRoute.Select(f => f.Name))}");
        int index = procedure.Legs.FindIndex(l => (l.Type == ProcedureLegType.CourseToFix) && (l.FixName == fix) && (l.FixPosition is not null));
        Assert.True(index >= 0, $"setup: the phase keeps no coded CF {fix} leg with its fix");
        return index;
    }

    /// <summary>
    /// COAST9's VM leg holds until a controller vector, so its CF SEGUL leg never starts unaided: replaces the phase with its
    /// own snapshot rewound to that leg, entered at the aircraft's position, as ClimbViaTopAltitudeTests' RunFinish does.
    /// </summary>
    private static DepartureProcedurePhase RewindToLeg(Departure departure, string fix)
    {
        AircraftState ac = departure.Aircraft;
        PhaseList phases = Assert.IsType<PhaseList>(ac.Phases);
        DepartureProcedurePhase procedure = CurrentProcedure(ac);
        int leg = procedure.Legs.FindIndex(l => l.FixName == fix);
        Assert.True(leg >= 0, $"setup: no coded leg ends at {fix}");
        DepartureProcedurePhaseDto snapshot = Assert.IsType<DepartureProcedurePhaseDto>(procedure.ToSnapshot());
        Assert.False(snapshot.Overridden, "setup: the aircraft must still be flying the coded procedure");
        var rewound = DepartureProcedurePhase.FromSnapshot(
            new DepartureProcedurePhaseDto
            {
                Status = snapshot.Status,
                ElapsedSeconds = snapshot.ElapsedSeconds,
                Requirements = snapshot.Requirements,
                Legs = snapshot.Legs,
                PostRoute = snapshot.PostRoute,
                AssignedAltitude = snapshot.AssignedAltitude,
                CruiseAltitude = snapshot.CruiseAltitude,
                LegIndex = leg,
                Overridden = false,
                LegEntryPosition = ac.Position,
                PreviousSignedCrossTrack = null,
                LegElapsedSeconds = 0,
                ControllerAltitude = snapshot.ControllerAltitude,
            }
        );
        phases.Phases[phases.Phases.IndexOf(procedure)] = rewound;
        return rewound;
    }

    /// <summary>Flies the rewound phase's last coded leg until it sequences and the phase hands over.</summary>
    private FixPassage FlyUntilLegSequenced(Departure departure, DepartureProcedurePhase procedure)
    {
        Assert.Equal(procedure.Legs.Count - 1, procedure.ActiveLegIndex);
        return FlyUntilLegSequenced(departure, procedure, procedure.ActiveLegIndex);
    }

    /// <summary>
    /// Flies until <paramref name="procedure"/> sequences leg <paramref name="leg"/> (or completes); returns the highest
    /// altitude and target before, and the target on the tick it sequenced.
    /// </summary>
    private FixPassage FlyUntilLegSequenced(Departure departure, DepartureProcedurePhase procedure, int leg)
    {
        AircraftState ac = departure.Aircraft;
        double maxAltitude = ac.Altitude;
        double maxTarget = 0;
        for (int t = 0; t < 1200; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            bool sequenced = (procedure.ActiveLegIndex > leg) || !ReferenceEquals(procedure, ac.Phases?.CurrentPhase);
            if (sequenced)
            {
                double after = Assert.NotNull(ac.Targets.TargetAltitude);
                _output.WriteLine(
                    $"leg {leg} sequenced at t={t}: alt={ac.Altitude:F0} maxAlt={maxAltitude:F0} maxTgt={maxTarget:F0} after={after:F0}"
                );
                FlightPhysics.Update(ac, 1.0);
                return new FixPassage(maxAltitude, maxTarget, after);
            }

            maxTarget = Math.Max(maxTarget, ac.Targets.TargetAltitude ?? 0);
            FlightPhysics.Update(ac, 1.0);
            maxAltitude = Math.Max(maxAltitude, ac.Altitude);
        }

        Assert.Fail($"leg {leg} did not sequence within 1200 s");
        return new FixPassage(maxAltitude, maxTarget, 0);
    }

    /// <summary>Flies the same seconds with and without the crossing and asserts the two flights match.</summary>
    private static void AssertSameFlight(Departure baseline, Departure crossed, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            foreach (Departure d in new[] { baseline, crossed })
            {
                PhaseRunner.Tick(d.Aircraft, d.Context);
                FlightPhysics.Update(d.Aircraft, 1.0);
            }

            Assert.Equal(baseline.Aircraft.Targets.TargetAltitude, crossed.Aircraft.Targets.TargetAltitude);
            Assert.Equal(baseline.Aircraft.Altitude, crossed.Aircraft.Altitude, 1.0);
        }
    }

    private static void FlyFor(Departure departure, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(departure.Aircraft, departure.Context);
            FlightPhysics.Update(departure.Aircraft, 1.0);
        }
    }

    private static double MinAltitudeOver(Departure departure, int seconds)
    {
        double min = departure.Aircraft.Altitude;
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(departure.Aircraft, departure.Context);
            FlightPhysics.Update(departure.Aircraft, 1.0);
            min = Math.Min(min, departure.Aircraft.Altitude);
        }

        return min;
    }

    private static void FlyUntil(Departure departure, Func<bool> done, int maxSeconds)
    {
        for (int t = 0; t < maxSeconds; t++)
        {
            if (done())
            {
                return;
            }

            PhaseRunner.Tick(departure.Aircraft, departure.Context);
            FlightPhysics.Update(departure.Aircraft, 1.0);
        }

        Assert.True(done(), $"the condition did not hold within {maxSeconds} s");
    }

    private void AssertLevel(AircraftState ac, double altitudeFt, string where)
    {
        _output.WriteLine($"{where}: alt={ac.Altitude:F0} tgt={ac.Targets.TargetAltitude:F0} assigned={ac.Targets.AssignedAltitude}");
        Assert.InRange(ac.Altitude, altitudeFt - LevelToleranceFt, altitudeFt + LevelToleranceFt);
    }

    private static void AssertRevertsCleared(NavigationTarget fix)
    {
        Assert.Null(fix.RevertAltitude);
        Assert.Null(fix.RevertAssignedAltitude);
    }

    private static double? CeilingFt(CifpAltitudeRestriction? restriction) =>
        restriction is { Type: CifpAltitudeRestrictionType.At or CifpAltitudeRestrictionType.AtOrBelow or CifpAltitudeRestrictionType.Between }
            ? restriction.Altitude1Ft
            : null;

    private static ArtccConfigRoot LoadZoa() => Assert.IsType<ArtccConfigRoot>(TestArtccConfig.LoadZoa());

    private static DepartureProcedurePhase CurrentProcedure(AircraftState aircraft) =>
        Assert.IsType<DepartureProcedurePhase>(Assert.IsType<PhaseList>(aircraft.Phases).CurrentPhase);

    private static NavigationTarget PendingFix(DepartureProcedurePhase procedure, string name) =>
        procedure.PostRoute.Find(f => f.Name == name)
        ?? throw new Xunit.Sdk.XunitException(
            $"setup: {name} is not on the pending route {string.Join(" ", procedure.PostRoute.Select(f => f.Name))}"
        );

    /// <summary>The runway <paramref name="end"/> at <paramref name="airport"/> as the navdata publishes it, that end as end 1.</summary>
    private static RunwayInfo Runway(string airport, string end)
    {
        RunwayInfo phys = Assert.IsType<RunwayInfo>(
            NavigationDatabase.Instance.GetRunways(airport).FirstOrDefault(r => (r.Id.End1 == end) || (r.Id.End2 == end))
        );
        bool isEnd2 = phys.Id.End2 == end;
        return new RunwayInfo
        {
            AirportId = airport,
            Id = phys.Id,
            Designator = end,
            Lat1 = isEnd2 ? phys.Lat2 : phys.Lat1,
            Lon1 = isEnd2 ? phys.Lon2 : phys.Lon1,
            Elevation1Ft = isEnd2 ? phys.Elevation2Ft : phys.Elevation1Ft,
            TrueHeading1 = isEnd2 ? phys.TrueHeading2 : phys.TrueHeading1,
            Lat2 = isEnd2 ? phys.Lat1 : phys.Lat2,
            Lon2 = isEnd2 ? phys.Lon1 : phys.Lon2,
            Elevation2Ft = isEnd2 ? phys.Elevation1Ft : phys.Elevation2Ft,
            TrueHeading2 = isEnd2 ? phys.TrueHeading1 : phys.TrueHeading2,
            WidthFt = phys.WidthFt,
        };
    }

    private static PhaseContext NewContext(AircraftState ac, RunwayInfo rwy) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };

    private void Dispatch(AircraftState aircraft, string text)
    {
        CommandResult result = DispatchResult(aircraft, text);
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }

    private CommandResult DispatchResult(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(
            Assert.IsType<CompoundCommand>(parsed.Value),
            aircraft,
            TestDispatch.Context(Random.Shared, artccConfig: LoadZoa())
        );
        _output.WriteLine($"{text} -> {result.Message}");
        return result;
    }
}
