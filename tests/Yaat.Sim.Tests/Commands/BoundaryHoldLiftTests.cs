using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A Class B boundary hold standing in front of a running pattern phase is lifted for every controller command, typed or
/// queued, so the command is judged against the held phase, and goes back only when the held phase survives it. A VFR M20P
/// on the right downwind for LVK 25R, outside the SFO Class B laterally, held in an orbit for the SFO Class B.
/// </summary>
public sealed class BoundaryHoldLiftTests : IDisposable
{
    private const string Sfo = "SFO";

    private readonly IDisposable _navScope;

    public BoundaryHoldLiftTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    [Fact]
    public void AltitudeCommand_AgainstAHeldDownwind_PutsTheSamePairBack()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        int phaseCount = ac.Phases!.Phases.Count;

        DispatchOk(ac, "CM 025");

        AssertPairStands(ac, downwind, hold);
        Assert.Equal(phaseCount, ac.Phases!.Phases.Count);
        Assert.Equal(2500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void ClearedToLand_AgainstAHeldDownwind_IsRecordedForTheDownwind_AndPutsThePairBack()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();

        DispatchOk(ac, "CLAND");

        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases!.LandingClearance);
        Assert.Contains("25R", ac.Phases.ClearedRunwayId ?? "");
        AssertPairStands(ac, downwind, hold);
    }

    [Fact]
    public void FlyHeading_AgainstAHeldDownwind_ClearsTheChain_AndDiscardsTheHold()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        PhaseList list = ac.Phases!;

        DispatchOk(ac, "FH 270");

        AssertChainClearedByHeading(ac, list, downwind, hold);
    }

    [Fact]
    public void TurnBase_AgainstAHeldDownwind_AdvancesToBase_AndDiscardsTheHold()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();

        DispatchOk(ac, "TB");

        Assert.Equal(PhaseStatus.Completed, downwind.Status);
        Assert.IsType<BasePhase>(ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, ac.Phases.CurrentPhase!.Status);
        AssertHoldDiscarded(ac, hold);
    }

    [Fact]
    public void PatternEntry_AgainstAHeldDownwind_ReplacesTheChain_AndDiscardsTheHold()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();

        DispatchOk(ac, "ERD 25L");

        Assert.NotEqual(PhaseStatus.Active, downwind.Status);
        Assert.NotNull(ac.Phases?.CurrentPhase);
        Assert.DoesNotContain(downwind, ac.Phases!.Phases.Where(p => p.Status == PhaseStatus.Active));
        Assert.Contains("25L", ac.Phases.AssignedRunway?.Id.ToString() ?? "");
        Assert.Null(ac.LiftedBoundaryHold);
        AssertHoldDiscarded(ac, hold);
    }

    [Fact]
    public void ClearedIntoBravo_WithAHeldPair_ClearsTheAircraftIntoTheBravoItWaitsOn()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        // Only the hold names the SFO Class B: the aircraft is neither laterally inside it nor projected into it.
        Assert.False(ImplicitBravoClearance.IsLaterallyInside(ac.Position, Sfo, AirspaceDatabase.Default));
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));

        DispatchOk(ac, "CLBRV");

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        AssertPairStands(ac, downwind, hold);
    }

    [Fact]
    public void FlyHeadingThenClearedIntoBravo_WithAHeldPair_ClearsTheAircraftIntoTheBravoItWaitsOn()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        Assert.False(ImplicitBravoClearance.IsLaterallyInside(ac.Position, Sfo, AirspaceDatabase.Default));
        Assert.Null(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, ImplicitBravoClearance.EntryLookaheadSeconds, AirspaceClass.Bravo));

        // The FH clears the chain, list and all, before the CLBRV beside it runs: the pilot still waits on the lifted hold's Bravo.
        DispatchOk(ac, "FH 270, CLBRV");

        Assert.Equal(Sfo, ac.ClearedBravoIdent);
        Assert.Equal(PhaseStatus.Skipped, downwind.Status);
        AssertHoldDiscarded(ac, hold);
    }

    [Fact]
    public void FlyHeadingThenRemainOutsideBravo_WithAHeldPair_TreatsThePilotAsWaitingOnTheBravo()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();

        DispatchOk(ac, "FH 270, RMOB");

        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        Assert.Equal(ImplicitBravoClearance.RemainOutsideAskSeconds, ac.RemainOutsideBravo?.AskDueSeconds);
        Assert.Equal(PhaseStatus.Skipped, downwind.Status);
        AssertHoldDiscarded(ac, hold);
    }

    [Fact]
    public void RemainOutsideBravo_WithAHeldPair_TreatsThePilotAsWaitingOnTheBravo()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();

        DispatchOk(ac, "RMOB");

        Assert.True(ac.IsRemainOutsideBravo(Sfo));
        // A waiting pilot schedules its one "any estimate on the bravo?" ask; a pilot not waiting schedules none.
        Assert.Equal(ImplicitBravoClearance.RemainOutsideAskSeconds, ac.RemainOutsideBravo?.AskDueSeconds);
        AssertPairStands(ac, downwind, hold);
    }

    [Fact]
    public void QueuedFlyHeading_AgainstAHeldDownwind_IsJudgedAsAnUnheldDownwindJudgesIt_AndPutsThePairBack()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();

        CommandResult result = ApplyQueued(ac, "FH 270");

        // A queued non-tower verb never meets the phase gate: on a Downwind, held or not, it assigns the heading and the leg
        // keeps flying, so the held Downwind survives and the hold goes back.
        Assert.True(result.Success, result.Message);
        AssertPairStands(ac, downwind, hold);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading?.Degrees);
    }

    [Fact]
    public void QueuedAltitudeCommand_AgainstAHeldDownwind_PutsTheSamePairBack()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        int phaseCount = ac.Phases!.Phases.Count;

        CommandResult result = ApplyQueued(ac, "CM 025");

        Assert.True(result.Success, result.Message);
        AssertPairStands(ac, downwind, hold);
        Assert.Equal(phaseCount, ac.Phases!.Phases.Count);
        Assert.Equal(2500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void TransparentHeadThenAltitude_AgainstAHeldDownwind_PutsThePairBackOnce()
    {
        (AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold) = HeldDownwind();
        int phaseCount = ac.Phases!.Phases.Count;

        DispatchOk(ac, "SQ 4321; CM 025");

        AssertPairStands(ac, downwind, hold);
        Assert.Equal(phaseCount, ac.Phases!.Phases.Count);
        Assert.Single(ac.Phases.Phases, p => p is AirspaceBoundaryHoldPhase);
        Assert.Equal(2500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void NoHeldPair_PlainDownwind_LiftsNothing_AndTheDownwindJudgesTheCommand()
    {
        AircraftState ac = OnDownwind(out DownwindPhase downwind);

        Assert.Null(ac.Phases!.LiftHold());
        DispatchOk(ac, "CM 025");

        Assert.Same(downwind, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, downwind.Status);
        Assert.Null(ac.LiftedBoundaryHold);
    }

    [Fact]
    public void NoHeldPair_FreeFlightHold_LiftsNothing_AndTheHoldStillJudgesTheCommand()
    {
        AircraftState ac = VfrAircraft();
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases = new PhaseList();
        ac.Phases.Add(hold);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));

        Assert.Null(ac.Phases.LiftHold());
        Assert.True(hold.CanAcceptCommand(CanonicalCommandType.FlyHeading).IsAllowed);
        DispatchOk(ac, "FH 270");

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Null(ac.LiftedBoundaryHold);
    }

    private static void AssertPairStands(AircraftState ac, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold)
    {
        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Same(downwind, ac.Phases.HeldPhase);
        Assert.Equal(PhaseStatus.Active, downwind.Status);
        // Neither ended nor started again: still Active, and still flying from the state its one OnStart saved.
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.True(Assert.IsType<AirspaceBoundaryHoldPhaseDto>(hold.ToSnapshot()).Started);
        Assert.Null(ac.LiftedBoundaryHold);
    }

    private static void AssertChainClearedByHeading(AircraftState ac, PhaseList list, DownwindPhase downwind, AirspaceBoundaryHoldPhase hold)
    {
        Assert.True(ac.Phases?.CurrentPhase is null, $"the chain is cleared, but {ac.Phases?.CurrentPhase?.Name} is current");
        Assert.Equal(PhaseStatus.Skipped, downwind.Status);
        Assert.Null(ac.LiftedBoundaryHold);
        Assert.DoesNotContain(hold, list.Phases);
        AssertHoldDiscarded(ac, hold);
        Assert.Equal(270, ac.Targets.AssignedMagneticHeading?.Degrees);
    }

    private static void AssertHoldDiscarded(AircraftState ac, AirspaceBoundaryHoldPhase hold)
    {
        Assert.Equal(PhaseStatus.Skipped, hold.Status);
        Assert.DoesNotContain(hold, ac.Phases?.Phases ?? []);
        Assert.Null(ac.LiftedBoundaryHold);
        Assert.Null(ac.Phases?.HeldPhase);
    }

    private static (AircraftState Aircraft, DownwindPhase Downwind, AirspaceBoundaryHoldPhase Hold) HeldDownwind()
    {
        AircraftState ac = OnDownwind(out DownwindPhase downwind);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        // Started by hand, as the pending-start path would, so no tick of the hold's own exit test runs before the command.
        hold.Status = PhaseStatus.Active;
        hold.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        Assert.Same(downwind, ac.Phases.HeldPhase);
        return (ac, downwind, hold);
    }

    private static AircraftState OnDownwind(out DownwindPhase downwind)
    {
        AircraftState ac = VfrAircraft();
        DispatchOk(ac, "ERD 25R");
        while ((ac.Phases?.CurrentPhase is not DownwindPhase) && (ac.Phases?.CurrentPhase is not null))
        {
            ac.Phases.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        }

        downwind = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        if (downwind.Status == PhaseStatus.Pending)
        {
            // The entry installs the downwind first, Pending: start it as the pending-start path would on the next tick.
            downwind.Status = PhaseStatus.Active;
            downwind.OnStart(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        }

        ac.PendingWarnings.Clear();
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

    private static DispatchContext Ctx() => TestDispatch.Context(new Random(1), validateDctFixes: false);

    private static void DispatchOk(AircraftState ac, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx());
        Assert.True(result.Success, result.Message);
    }

    /// <summary>The block's apply action, as the queue runs it when the block fires.</summary>
    private static CommandResult ApplyQueued(AircraftState ac, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        Func<AircraftState, bool, CommandResult> apply = CommandDispatcher.BuildApplyAction(
            parsed.Value!.Blocks[0].Commands,
            Ctx(),
            new CommandBlock()
        );
        return apply(ac, true);
    }
}
