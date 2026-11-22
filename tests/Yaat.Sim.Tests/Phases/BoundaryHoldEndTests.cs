using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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
/// How a Class B boundary hold standing in front of a running phase (<see cref="PhaseList.InsertHoldInFront"/>) ends: its
/// natural end on the tick goes through <see cref="PhaseRunner.EndHold"/> and resumes the held phase as it declares; a
/// heading already standing when the hold went in is not a vector; a new heading that keeps the aircraft outside the Class B
/// ends the orbit and is flown, one into it is held through and flown once cleared, and a later heading is judged the same way
/// in its turn; a new direct follows the same rule (flown at once when it keeps outside, stashed and put back once cleared when
/// it enters), and with neither left standing the held phase's route is put back; a hold with no held phase keeps the
/// free-flight path; and the visual backstop leaves a held go-around alone. Solo, on real navdata: a VFR C172 on a pattern
/// entry or a final to OAK 28R, or west of the SFO Class B ring, or a VFR M20P on the LVK 25R right downwind, held for the SFO
/// Class B, and an IFR B738 on the OAK 30 visual for the backstop; the gate is opened by granting the Class B clearance the hold
/// waits for.
/// </summary>
public sealed class BoundaryHoldEndTests : IDisposable
{
    private const string Sfo = "SFO";

    /// <summary>West of the SFO Class B's outer ring at 7500 ft: east is into it, west keeps outside.</summary>
    private static readonly LatLon NearWestRing = new(37.62, -122.885);

    /// <summary>The fix the held initial climb was told direct to before the hold went in: Woodside VOR, southeast.</summary>
    private const string HeldRouteFix = "OSI";

    /// <summary>A direct that keeps the aircraft outside the SFO Class B from <see cref="NearWestRing"/>: Point Reyes VOR.</summary>
    private const string OutsideFix = "PYE";

    /// <summary>A direct that takes the aircraft into the SFO Class B from <see cref="NearWestRing"/>: San Francisco VOR.</summary>
    private const string IntoFix = "SFO";

    private readonly IDisposable _navScope;

    public BoundaryHoldEndTests(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        _navScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
    }

    public void Dispose() => _navScope.Dispose();

    [Fact]
    public void PhaseRunnerTick_PairedHoldCompletes_ResumesAnInPlacePhase_WithoutASecondOnStart()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        // PatternEntryPhase.OnStart writes its pattern altitude here: a second start would overwrite this. A controller
        // altitude command, so the hold's end leaves it standing.
        ac.NoteControllerAltitudeCommand();
        ac.Targets.TargetAltitude = 4500;
        ac.GrantBravoClearance(Sfo);

        Exception? thrown = Record.Exception(() => PhaseRunner.Tick(ac, ctx));

        Assert.Null(thrown);
        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(4500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void PhaseRunnerTick_PairedHoldCompletes_SwapsInARestartCopy_StartedOnceAfterTheHoldEnds()
    {
        AircraftState ac = OnDownwind(out DownwindPhase downwind);
        PhaseContext ctx = MinimalContext(ac);
        // The hold saves these as the targets to put back; the copy's OnStart writes its own over them only if it runs after.
        ac.Targets.TargetTrueHeading = downwind.Waypoints!.DownwindHeading + 40.0;
        ac.Targets.PreferredTurnDirection = TurnDirection.Right;
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        ac.GrantBravoClearance(Sfo);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Equal(PhaseStatus.Completed, downwind.Status);
        DownwindPhase copy = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        Assert.NotSame(downwind, copy);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(downwind.Waypoints.DownwindHeading, ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);

        PhaseRunner.Tick(ac, ctx);

        Assert.Same(copy, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, copy.Status);
    }

    [Fact]
    public void PhaseRunnerTick_PairedFinalResumes_KeepingItsPreFinalSpacingCeiling()
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), PointOnFinal(Oak28R(), 6.0));
        // The final latches its spacing ceiling at entry from the target standing then.
        ac.Targets.TargetSpeed = 180.0;
        var final = new FinalApproachPhase();
        ac.Phases!.Add(final);
        ac.Phases.Start(ctx);
        Assert.Equal(180.0, SpacingCeilingOf(final));
        // The hold saves and puts back this target: a copy that re-latched its ceiling at its start would take it.
        ac.Targets.TargetSpeed = 120.0;
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        ac.GrantBravoClearance(Sfo);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Equal(PhaseStatus.Completed, final.Status);
        FinalApproachPhase copy = Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
        Assert.NotSame(final, copy);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(180.0, SpacingCeilingOf(copy));
    }

    [Fact]
    public void PairedHold_AHeadingStandingBeforeTheHold_IsNotFlown_AndThePatternEntryResumesItsRoute()
    {
        SimulationEngine engine = BuildOakEngine();
        RunwayInfo runway = Oak28R();
        (AircraftState ac, _) = AircraftAt(runway, GeoMath.ProjectPoint(EntryPoint(runway), runway.TrueHeading + 180, 6.0));
        ac.Phases = null;
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand(ac.Callsign, "FH 270").Success);
        Assert.True(engine.SendCommand(ac.Callsign, "ELD 28R").Success);
        Assert.Equal(new MagneticHeading(270), ac.Targets.AssignedMagneticHeading);
        engine.TickOneSecond();
        PatternEntryPhase entry = Assert.IsType<PatternEntryPhase>(ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        List<string> entryRoute = [.. ac.Targets.NavigationRoute.Select(t => t.Name)];
        Assert.NotEmpty(entryRoute);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases.InsertHoldInFront(hold);

        for (int t = 0; t < 4; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(entry, ac.Phases.HeldPhase);

        ac.GrantBravoClearance(Sfo);
        engine.TickOneSecond();

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(entryRoute, ac.Targets.NavigationRoute.Select(t => t.Name));

        for (int t = 0; (t < 900) && (ac.Phases.CurrentPhase == entry); t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal(PhaseStatus.Completed, entry.Status);
    }

    [Fact]
    public void PairedHold_ANewHeadingThatStaysOutsideTheBravo_EndsTheHold_AndIsFlown()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        var assigned = new MagneticHeading(270);
        Assert.Null(BravoEntryOn(ac, assigned));
        Assert.True(engine.SendCommand(ac.Callsign, "FH 270").Success);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, climb.Status);
        Assert.Equal(assigned.ToTrue(ac.Declination), ac.Targets.TargetTrueHeading);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void PairedHold_ANewHeadingIntoTheBravo_IsHeldThrough_AndFlownOnceCleared()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        var assigned = new MagneticHeading(90);
        Assert.NotNull(BravoEntryOn(ac, assigned));
        Assert.True(engine.SendCommand(ac.Callsign, "FH 090").Success);

        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(climb, ac.Phases.HeldPhase);
        Assert.Equal(ac.TrueHeading + (-180.0), ac.Targets.TargetTrueHeading);
        Assert.Equal(TurnDirection.Left, ac.Targets.PreferredTurnDirection);

        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(assigned.ToTrue(ac.Declination), ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void PairedHold_ANewDirectThatStaysOutsideTheBravo_EndsTheHold_AndIsFlown()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        Assert.Null(BravoEntryToward(ac, OutsideFix));
        Assert.True(engine.SendCommand(ac.Callsign, $"DCT {OutsideFix}").Success);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases!.CurrentPhase);
        Assert.Equal(OutsideFix, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.Null(ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PairedHold_ANewDirectIntoTheBravo_IsHeldThrough_AndFlownOnceCleared()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        Assert.NotNull(BravoEntryToward(ac, IntoFix));
        Assert.True(engine.SendCommand(ac.Callsign, $"DCT {IntoFix}").Success);

        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(climb, ac.Phases.HeldPhase);
        // Stashed, so the orbit is what it steers by.
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Equal(ac.TrueHeading + (-180.0), ac.Targets.TargetTrueHeading);
        Assert.True(HoldDto(hold).StashedDirect);

        // A snapshot taken mid-hold keeps the stash, and the restored hold puts the direct back.
        AirspaceBoundaryHoldPhase restored = RoundTripCurrentHold(ac, hold);
        Assert.True(HoldDto(restored).StashedDirect);
        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, restored.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(IntoFix, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.Null(ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PairedHold_AHeadingHeldThrough_ThenANewHeadingOutsideTheBravo_EndsTheHold_AndIsFlown()
    {
        var outside = new MagneticHeading(270);
        var into = new MagneticHeading(90);
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbWestOfTheBravo("FH 270");
        Assert.Null(BravoEntryOn(ac, outside));
        Assert.NotNull(BravoEntryOn(ac, into));
        Assert.Equal(270.0, HoldDto(hold).HeadingStandingAtStartDeg);

        // The heading standing when the hold went in is not a vector.
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(hold, ac.Phases!.CurrentPhase);

        Assert.True(engine.SendCommand(ac.Callsign, "FH 090").Success);
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Same(climb, ac.Phases.HeldPhase);
        AirspaceBoundaryHoldPhaseDto midHold = HoldDto(hold);
        Assert.Null(midHold.HeadingStandingAtStartDeg);
        Assert.True(midHold.HeldThroughVector);
        Assert.False(midHold.StashedDirect);

        // A snapshot taken mid-hold keeps the cleared reference: 270 is new now, and ends the orbit.
        AirspaceBoundaryHoldPhase restored = RoundTripCurrentHold(ac, hold);
        Assert.Null(HoldDto(restored).HeadingStandingAtStartDeg);
        Assert.True(engine.SendCommand(ac.Callsign, "FH 270").Success);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, restored.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(outside.ToTrue(ac.Declination), ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    [Fact]
    public void PairedHold_AHeldThroughHeadingCleared_EndsWithTheHeldPhasesRouteRestored()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbOnADirectWestOfTheBravo();
        AirspaceBoundaryHoldPhaseDto saved = HoldDto(hold);
        Assert.NotNull(BravoEntryOn(ac, new MagneticHeading(90)));
        Assert.True(engine.SendCommand(ac.Callsign, "FH 090").Success);
        PhaseRunner.Tick(ac, ctx);
        Assert.True(HoldDto(hold).HeldThroughVector);

        ac.Targets.AssignedMagneticHeading = null;
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(hold, ac.Phases!.CurrentPhase);
        ac.GrantBravoClearance(Sfo);
        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        Assert.Equal(HeldRouteFix, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.Equal(saved.OriginalTargetHeadingDeg, ac.Targets.TargetTrueHeading?.Degrees);
        Assert.Equal(saved.OriginalTurnDirection, (int?)ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void PairedHold_WhenTheBravoCanNoLongerBeEntered_ResumesTheHeldPhase()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        // Level well beneath this volume, so the hold's first tick finds it can no longer be entered.
        AirspaceBoundaryHoldPhase hold = SfoBravoHoldBetween(8000, 10000);
        ac.Phases!.InsertHoldInFront(hold);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);
    }

    [Fact]
    public void PairedHold_WhoseHeldPhaseIsGone_EndsByTheOldPath()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        entry.Status = PhaseStatus.Completed;
        entry.OnEnd(ctx, PhaseStatus.Completed);
        Assert.True(ac.Phases!.Phases.Remove(entry));
        Assert.Null(ac.Phases.HeldPhase);
        ac.GrantBravoClearance(Sfo);

        Exception? thrown = Record.Exception(() => PhaseRunner.Tick(ac, ctx));

        Assert.Null(thrown);
        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.True(ac.Phases.IsComplete);
    }

    [Fact]
    public void FreeFlightHold_GivenAVector_StillEnds()
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), new LatLon(37.62, -122.12));
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.Add(hold);
        ac.Phases.Start(ctx);
        PhaseRunner.Tick(ac, ctx);
        Assert.Same(hold, ac.Phases.CurrentPhase);
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(10);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.True(ac.Phases.IsComplete);
    }

    [Fact]
    public void SnapshotRoundTrip_OfAPairedHoldWithAStandingHeading_KeepsTheRecordedHeading()
    {
        (AircraftState ac, PhaseContext ctx, _) = GoAroundWestOfTheBravo();
        // West is outside the Class B: read as a new vector it would end the hold.
        var standing = new MagneticHeading(270);
        Assert.Null(BravoEntryOn(ac, standing));
        ac.Targets.AssignedMagneticHeading = standing;
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);

        string json = JsonSerializer.Serialize((AirspaceBoundaryHoldPhaseDto)hold.ToSnapshot(), RecordingJsonOptions.Default);
        AirspaceBoundaryHoldPhaseDto dto = JsonSerializer.Deserialize<AirspaceBoundaryHoldPhaseDto>(json, RecordingJsonOptions.Default)!;
        Assert.Equal(270.0, dto.HeadingStandingAtStartDeg);
        var restored = AirspaceBoundaryHoldPhase.FromSnapshot(dto);
        ac.Phases!.Phases[ac.Phases.CurrentIndex] = restored;

        PhaseRunner.Tick(ac, ctx);

        Assert.Same(restored, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, restored.Status);
        Assert.Equal(270.0, ((AirspaceBoundaryHoldPhaseDto)restored.ToSnapshot()).HeadingStandingAtStartDeg);

        // A hold with no standing heading writes no member for it.
        string bare = JsonSerializer.Serialize((AirspaceBoundaryHoldPhaseDto)SfoBravoHold().ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AirspaceBoundaryHoldPhaseDto.HeadingStandingAtStartDeg), bare, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VisualBackstop_HeldGoAroundOnAVisual_KeepsThePairAndTheApproach()
    {
        SimulationEngine engine = BuildOakEngine();
        AircraftState ac = EstablishOakCvaWithFieldInSight(engine);
        PhaseList list = ac.Phases!;
        var goAround = new GoAroundPhase { Status = PhaseStatus.Active };
        list.Phases[list.CurrentIndex] = goAround;
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        list.InsertHoldInFront(hold);
        // Started by hand, as the pending-start path would, so no tick of the hold's own exit test runs.
        hold.Status = PhaseStatus.Active;
        hold.OnStart(MinimalContext(ac));
        // Nothing in sight and no loss this pass: only the backstop would see it.
        ac.Approach.HasReportedFieldInSight = false;
        engine.World.Weather = new WeatherProfile { Metars = ["KOAK 121853Z 27012KT 1SM CLR 20/12 A2992"] };

        engine.TickVisualDetection();

        Assert.Same(hold, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(goAround, ac.Phases.HeldPhase);
        Assert.Equal(PhaseStatus.Active, goAround.Status);
        Assert.NotNull(ac.Phases.ActiveApproach);
    }

    /// <summary>
    /// <see cref="HeldClimbWestOfTheBravo"/> told DCT <see cref="HeldRouteFix"/> before the hold went in, so the held phase
    /// carries a route the hold saved.
    /// </summary>
    private static (
        SimulationEngine Engine,
        AircraftState Aircraft,
        PhaseContext Context,
        InitialClimbPhase Climb,
        AirspaceBoundaryHoldPhase Hold
    ) HeldClimbOnADirectWestOfTheBravo()
    {
        (SimulationEngine engine, AircraftState ac, PhaseContext ctx, InitialClimbPhase climb, AirspaceBoundaryHoldPhase hold) =
            HeldClimbWestOfTheBravo($"DCT {HeldRouteFix}");
        Assert.Equal(HeldRouteFix, Assert.Single(HoldDto(hold).OriginalRoute!).Name);
        return (engine, ac, ctx, climb, hold);
    }

    private static AirspaceBoundaryHoldPhaseDto HoldDto(AirspaceBoundaryHoldPhase hold) => (AirspaceBoundaryHoldPhaseDto)hold.ToSnapshot();

    /// <summary>Round-trips the current hold through JSON and puts the restored hold in its place in the list.</summary>
    private static AirspaceBoundaryHoldPhase RoundTripCurrentHold(AircraftState ac, AirspaceBoundaryHoldPhase hold)
    {
        string json = JsonSerializer.Serialize(HoldDto(hold), RecordingJsonOptions.Default);
        AirspaceBoundaryHoldPhaseDto dto = JsonSerializer.Deserialize<AirspaceBoundaryHoldPhaseDto>(json, RecordingJsonOptions.Default)!;
        var restored = AirspaceBoundaryHoldPhase.FromSnapshot(dto);
        ac.Phases!.Phases[ac.Phases.CurrentIndex] = restored;
        return restored;
    }

    /// <summary>
    /// A VFR C172 eastbound at 7500 ft west of the SFO Class B ring, on an initial climb given
    /// <paramref name="preHoldCommand"/> before the hold went in, with an SFO Class B hold started in front of it. The climb
    /// is Active and never ticked, so it cannot complete on its own.
    /// </summary>
    private static (
        SimulationEngine Engine,
        AircraftState Aircraft,
        PhaseContext Context,
        InitialClimbPhase Climb,
        AirspaceBoundaryHoldPhase Hold
    ) HeldClimbWestOfTheBravo(string preHoldCommand)
    {
        SimulationEngine engine = BuildOakEngine();
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), NearWestRing);
        ac.Altitude = 7500;
        ac.IndicatedAirspeed = 120;
        ac.TrueHeading = new TrueHeading(90);
        ac.TrueTrack = new TrueHeading(90);
        var climb = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            IsVfr = true,
            CruiseAltitude = 7500,
            Status = PhaseStatus.Active,
        };
        ac.Phases!.Add(climb);
        engine.World.AddAircraft(ac);
        Assert.True(engine.SendCommand(ac.Callsign, preHoldCommand).Success);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        AirspaceBoundaryHoldPhase hold = StartedHoldInFront(ac, ctx);
        Assert.Empty(ac.Targets.NavigationRoute);
        return (engine, ac, ctx, climb, hold);
    }

    /// <summary>The SFO Class B volume a level track toward <paramref name="fix"/> enters, as the hold projects it; null for none.</summary>
    private static AirspaceVolume? BravoEntryToward(AircraftState ac, string fix)
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(fix) ?? throw new InvalidOperationException($"{fix} missing");
        var track = new TrueHeading(GeoMath.BearingTo(ac.Position, new LatLon(lat, lon)));
        return AirspaceDatabase.Default.FindLevelTrackClassBEntry(
            ac.Position,
            ac.Altitude,
            track,
            ac.GroundSpeed,
            ImplicitBravoClearance.WaitingEntryLookaheadSeconds,
            Sfo
        );
    }

    /// <summary>Puts an SFO Class B hold in front of the running phase and starts it with one tick, the gate still shut.</summary>
    private static AirspaceBoundaryHoldPhase StartedHoldInFront(AircraftState ac, PhaseContext ctx)
    {
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        Phase held = ac.Phases!.CurrentPhase!;
        ac.Phases.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(held, ac.Phases.HeldPhase);
        return hold;
    }

    /// <summary>The SFO Class B volume a level track on <paramref name="heading"/> enters, as the hold projects it; null for none.</summary>
    private static AirspaceVolume? BravoEntryOn(AircraftState ac, MagneticHeading heading) =>
        AirspaceDatabase.Default.FindLevelTrackClassBEntry(
            ac.Position,
            ac.Altitude,
            heading.ToTrue(ac.Declination),
            ac.GroundSpeed,
            ImplicitBravoClearance.WaitingEntryLookaheadSeconds,
            Sfo
        );

    /// <summary>A C172 eastbound at 7500 ft west of the SFO Class B ring, flying an Active go-around (never started), unheld.</summary>
    private static (AircraftState Aircraft, PhaseContext Context, GoAroundPhase GoAround) GoAroundWestOfTheBravo()
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), NearWestRing);
        ac.Altitude = 7500;
        ac.IndicatedAirspeed = 120;
        ac.TrueHeading = new TrueHeading(90);
        ac.TrueTrack = new TrueHeading(90);
        var goAround = new GoAroundPhase { Status = PhaseStatus.Active };
        ac.Phases!.Add(goAround);
        return (ac, ctx, goAround);
    }

    private static (AircraftState Aircraft, PhaseContext Context, PatternEntryPhase Entry) AircraftOnPatternEntry()
    {
        RunwayInfo runway = Oak28R();
        LatLon entryPoint = EntryPoint(runway);
        var entry = new PatternEntryPhase
        {
            EntryLat = entryPoint.Lat,
            EntryLon = entryPoint.Lon,
            PatternAltitude = runway.ElevationFt + 1000,
            Kind = PatternEntryKind.Direct,
        };
        (AircraftState ac, PhaseContext ctx) = AircraftAt(runway, GeoMath.ProjectPoint(entryPoint, runway.TrueHeading + 180, 6.0));
        ac.Phases!.Add(entry);
        ac.Phases.Start(ctx);
        PhaseRunner.Tick(ac, ctx);
        return (ac, ctx, entry);
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("OAK 28R missing");

    /// <summary>A left-traffic downwind for 28R lies to the south; the entry point is 1 nm abeam the threshold.</summary>
    private static LatLon EntryPoint(RunwayInfo runway) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + (-90), 1.0);

    private static LatLon PointOnFinal(RunwayInfo runway, double distanceNm) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + 180, distanceNm);

    private static (AircraftState Aircraft, PhaseContext Context) AircraftAt(RunwayInfo runway, LatLon position)
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            AirportId = "OAK",
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 1500,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
            SoloTrainingMode = true,
            StudentPositionType = "TWR",
        };
        return (ac, ctx);
    }

    private static PhaseContext MinimalContext(AircraftState ac) => CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);

    /// <summary>A VFR M20P north of LVK, entered on the 25R right downwind with ERD 25R, the downwind started.</summary>
    private static AircraftState OnDownwind(out DownwindPhase downwind)
    {
        var ac = new AircraftState
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
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("ERD 25R");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(new Random(1), validateDctFixes: false));
        Assert.True(result.Success, result.Message);
        while ((ac.Phases?.CurrentPhase is not DownwindPhase) && (ac.Phases?.CurrentPhase is not null))
        {
            ac.Phases.AdvanceToNext(MinimalContext(ac));
        }

        downwind = Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        if (downwind.Status == PhaseStatus.Pending)
        {
            downwind.Status = PhaseStatus.Active;
            downwind.OnStart(MinimalContext(ac));
        }

        return ac;
    }

    private static SimulationEngine BuildOakEngine() =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-boundary-hold-end",
                ScenarioName = "Boundary Hold End",
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
        Assert.StartsWith("VIS", ac.Phases!.ActiveApproach!.ApproachId, StringComparison.Ordinal);
        return ac;
    }

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

    /// <summary>An SFO Class B hold whose held volume spans <paramref name="lowerFtMsl"/> to <paramref name="upperFtMsl"/>.</summary>
    private static AirspaceBoundaryHoldPhase SfoBravoHoldBetween(int lowerFtMsl, int upperFtMsl) =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = Sfo,
            NameText = Sfo,
            ReferencePosition = new LatLon(37.6213, -122.3790),
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
            VolumeLowerFtMsl = lowerFtMsl,
            VolumeUpperFtMsl = upperFtMsl,
        };

    private static double? SpacingCeilingOf(FinalApproachPhase phase) => ((FinalApproachPhaseDto)phase.ToSnapshot()).SpacingCeilingKts;
}
