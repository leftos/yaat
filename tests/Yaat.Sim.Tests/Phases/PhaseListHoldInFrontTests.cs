using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// The <see cref="PhaseList"/> primitives for a Class B boundary hold put in front of a running phase: the hold goes in at the
/// current index, the running phase stays Active directly behind it (<see cref="PhaseList.HeldPhase"/>) and is not ticked, and
/// the hold's end resumes it in place or swaps in a restart copy. A VFR C172 on a pattern entry to OAK 28R, held outside the
/// SFO Class B.
/// </summary>
public class PhaseListHoldInFrontTests
{
    [Fact]
    public void InsertHoldInFront_KeepsTheHeldPhaseActiveBehindTheHold()
    {
        (AircraftState ac, _, PatternEntryPhase entry) = AircraftOnPatternEntry();
        Assert.Null(ac.Phases!.HeldPhase);
        Assert.Same(entry, ac.Phases.EffectivePhase);

        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases.InsertHoldInFront(hold);

        Assert.Equal(0, ac.Phases.CurrentIndex);
        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Pending, hold.Status);
        Assert.Same(entry, ac.Phases.Phases[1]);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Same(entry, ac.Phases.HeldPhase);
        Assert.Same(entry, ac.Phases.EffectivePhase);
    }

    [Fact]
    public void HeldPair_HoldStartsOnTheNextTick_AndTheHeldPhaseIsNotTicked()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);
        double entryElapsed = entry.ElapsedSeconds;
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(PhaseStatus.Active, hold.Status);
        // The hold's OnStart saved the entry's route and cleared the live one.
        AirspaceBoundaryHoldPhaseDto holdDto = Assert.IsType<AirspaceBoundaryHoldPhaseDto>(hold.ToSnapshot());
        Assert.True(holdDto.Started);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(holdDto.OriginalRoute!).Name);
        Assert.Empty(ac.Targets.NavigationRoute);

        PhaseRunner.Tick(ac, ctx);
        PhaseRunner.Tick(ac, ctx);

        Assert.Same(hold, ac.Phases.CurrentPhase);
        Assert.Equal(3.0, hold.ElapsedSeconds);
        Assert.Equal(entryElapsed, entry.ElapsedSeconds);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Same(entry, ac.Phases.HeldPhase);
    }

    [Fact]
    public void InsertHoldInFront_WithAHoldStanding_Throws()
    {
        (AircraftState ac, _, _) = AircraftOnPatternEntry();
        ac.Phases!.InsertHoldInFront(SfoBravoHold());

        Assert.Throws<InvalidOperationException>(() => ac.Phases.InsertHoldInFront(SfoBravoHold()));
        Assert.Equal(2, ac.Phases.Phases.Count);
    }

    [Fact]
    public void InsertHoldInFront_WithNoCurrentPhase_Throws()
    {
        var list = new PhaseList();

        Assert.Throws<InvalidOperationException>(() => list.InsertHoldInFront(SfoBravoHold()));
        Assert.Empty(list.Phases);
    }

    [Fact]
    public void InsertHoldInFront_BeforeTheCurrentPhaseStarted_Throws()
    {
        (AircraftState ac, _, _) = AircraftWithUnstartedPatternEntry();

        Assert.Throws<InvalidOperationException>(() => ac.Phases!.InsertHoldInFront(SfoBravoHold()));
        Assert.Single(ac.Phases!.Phases);
    }

    [Fact]
    public void InsertHoldInFront_AHoldThatIsNotPending_Throws()
    {
        (AircraftState ac, _, PatternEntryPhase entry) = AircraftOnPatternEntry();
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        hold.Status = PhaseStatus.Active;

        Assert.Throws<InvalidOperationException>(() => ac.Phases!.InsertHoldInFront(hold));
        Assert.Same(entry, Assert.Single(ac.Phases!.Phases));
    }

    [Fact]
    public void InsertHoldInFront_ALevelOffHold_Throws()
    {
        (AircraftState ac, _, PatternEntryPhase entry) = AircraftOnPatternEntry();
        var levelOff = new AirspaceBoundaryHoldPhase
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = "SFO",
            NameText = "SFO",
            ReferencePosition = new LatLon(37.6213, -122.3790),
            Mode = AirspaceHoldMode.LevelOff,
            LevelOffCeilingFtMsl = 2500,
        };

        Assert.Throws<InvalidOperationException>(() => ac.Phases!.InsertHoldInFront(levelOff));
        Assert.Same(entry, Assert.Single(ac.Phases!.Phases));
    }

    [Fact]
    public void EndHold_WithNoHeldPair_Throws()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();

        Assert.Throws<InvalidOperationException>(() => PhaseRunner.EndHold(ac, ctx, HoldResume.InPlace));
        Assert.Same(entry, ac.Phases!.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
    }

    [Fact]
    public void EndHoldInPlace_ResumesTheHeldPhaseWithoutASecondOnStart_AndItTicksAgain()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        PhaseRunner.Tick(ac, ctx);
        double entryElapsed = entry.ElapsedSeconds;
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);
        // PatternEntryPhase.OnStart writes its pattern altitude here: a second start would overwrite this.
        ac.Targets.TargetAltitude = 4500;

        PhaseRunner.EndHold(ac, ctx, HoldResume.InPlace);

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(1, ac.Phases.CurrentIndex);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Null(ac.Phases.HeldPhase);
        Assert.Equal(4500, ac.Targets.TargetAltitude);
        // The hold's OnEnd(Completed) put the entry's route back.
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(entryElapsed + 1.0, entry.ElapsedSeconds);
        Assert.Equal(4500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void EndHoldInPlace_BeforeTheHoldStarted_DropsTheHold_AndLeavesTheHeldPhaseAlone()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        // The hold's OnEnd(Completed) would put back its saved speed (none, as it never started) and its saved route (empty).
        ac.Targets.TargetSpeed = 95;
        double? targetAltitude = ac.Targets.TargetAltitude;
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);

        PhaseRunner.EndHold(ac, ctx, HoldResume.InPlace);

        Assert.Same(entry, Assert.Single(ac.Phases.Phases));
        Assert.Equal(0, ac.Phases.CurrentIndex);
        Assert.Same(entry, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, entry.Status);
        Assert.Equal(PhaseStatus.Pending, hold.Status);
        Assert.Equal(95, ac.Targets.TargetSpeed);
        Assert.Equal(targetAltitude, ac.Targets.TargetAltitude);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);
    }

    [Fact]
    public void EndHoldWithRestartCopy_BeforeTheHoldStarted_DropsTheHold_AndSwapsInTheCopy()
    {
        var original = new LifecycleProbePhase();
        (AircraftState ac, PhaseContext ctx) = AircraftWith(original);
        ac.Targets.TargetSpeed = 95;
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        var copy = new LifecycleProbePhase();

        PhaseRunner.EndHold(ac, ctx, HoldResume.RestartWith(copy));

        Assert.Same(copy, Assert.Single(ac.Phases.Phases));
        Assert.Same(copy, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Pending, hold.Status);
        Assert.Equal(95, ac.Targets.TargetSpeed);
        Assert.Equal(PhaseStatus.Completed, original.Status);
        Assert.Equal([PhaseStatus.Completed], original.Ends);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(1, copy.Starts);
    }

    [Fact]
    public void EndHoldWithRestartCopy_EndsTheOriginalOnce_AndStartsTheCopyOnce()
    {
        var original = new LifecycleProbePhase();
        (AircraftState ac, PhaseContext ctx) = AircraftWith(original);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);
        var copy = new LifecycleProbePhase();

        PhaseRunner.EndHold(ac, ctx, HoldResume.RestartWith(copy));

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Equal(PhaseStatus.Completed, original.Status);
        Assert.Equal(1, original.Starts);
        Assert.Equal([PhaseStatus.Completed], original.Ends);
        Assert.Equal(0, original.Ticks);
        Assert.Equal([hold, copy], ac.Phases.Phases);
        Assert.Same(copy, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(1, copy.Starts);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(1, copy.Starts);
        Assert.Equal(1, copy.Ticks);
        Assert.Empty(copy.Ends);
        Assert.Equal([PhaseStatus.Completed], original.Ends);
    }

    [Fact]
    public void EndHoldWithRestartCopy_OfAPatternEntry_StartsTheCopyAfterTheHoldEnds()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        PhaseRunner.Tick(ac, ctx);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);
        PatternEntryPhase copy = PatternEntryTo(Oak28R());
        // The hold's OnEnd leaves the altitude alone; the copy's OnStart writes its pattern altitude.
        ac.Targets.TargetAltitude = 4500;

        PhaseRunner.EndHold(ac, ctx, HoldResume.RestartWith(copy));

        Assert.Equal(PhaseStatus.Completed, hold.Status);
        Assert.Equal(PhaseStatus.Completed, entry.Status);
        Assert.Equal([hold, copy], ac.Phases.Phases);
        Assert.Same(copy, ac.Phases.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, copy.Status);
        Assert.Equal(copy.PatternAltitude, ac.Targets.TargetAltitude);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);
    }

    [Fact]
    public void EndHoldWithRestartCopy_WhenTheCopyIsTheHeldPhase_Throws()
    {
        var original = new LifecycleProbePhase();
        (AircraftState ac, PhaseContext ctx) = AircraftWith(original);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);

        Assert.Throws<InvalidOperationException>(() => PhaseRunner.EndHold(ac, ctx, HoldResume.RestartWith(original)));
        Assert.Equal([hold, original], ac.Phases.Phases);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Same(original, ac.Phases.HeldPhase);
        Assert.Empty(original.Ends);
    }

    [Fact]
    public void EndHoldWithRestartCopy_WhenTheCopyIsNotPending_Throws()
    {
        var original = new LifecycleProbePhase();
        (AircraftState ac, PhaseContext ctx) = AircraftWith(original);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);
        var copy = new LifecycleProbePhase { Status = PhaseStatus.Active };

        Assert.Throws<InvalidOperationException>(() => PhaseRunner.EndHold(ac, ctx, HoldResume.RestartWith(copy)));
        Assert.Equal([hold, original], ac.Phases.Phases);
        Assert.Equal(PhaseStatus.Active, hold.Status);
        Assert.Empty(original.Ends);
        Assert.Equal(0, copy.Starts);
    }

    [Fact]
    public void Clear_WithAHeldPair_EndsTheHeldPhaseOnceSkipped_AndTheHoldAsUsual()
    {
        var held = new LifecycleProbePhase();
        var pending = new LifecycleProbePhase();
        (AircraftState ac, PhaseContext ctx) = AircraftWith(held, pending);
        AirspaceBoundaryHoldPhase hold = SfoBravoHold();
        ac.Phases!.InsertHoldInFront(hold);
        PhaseRunner.Tick(ac, ctx);

        ac.Phases.Clear(ctx);

        Assert.Equal(PhaseStatus.Skipped, hold.Status);
        Assert.Equal(PhaseStatus.Skipped, held.Status);
        Assert.Equal([PhaseStatus.Skipped], held.Ends);
        Assert.Equal(PhaseStatus.Skipped, pending.Status);
        Assert.Empty(pending.Ends);
        Assert.Equal(0, pending.Starts);
        Assert.True(ac.Phases.IsComplete);
        Assert.Null(ac.Phases.HeldPhase);
    }

    [Fact]
    public void SnapshotRoundTrip_OfAHeldPair_KeepsTheHeldPhase_AndItResumesFromItsSavedState()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftOnPatternEntry();
        PhaseRunner.Tick(ac, ctx);
        double entryElapsed = entry.ElapsedSeconds;
        ac.Phases!.InsertHoldInFront(SfoBravoHold());
        PhaseRunner.Tick(ac, ctx);

        string json = JsonSerializer.Serialize(ac.Phases!.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = PhaseList.FromSnapshot(JsonSerializer.Deserialize<PhaseListDto>(json, RecordingJsonOptions.Default)!, null);

        Assert.Equal(0, restored.CurrentIndex);
        AirspaceBoundaryHoldPhase restoredHold = Assert.IsType<AirspaceBoundaryHoldPhase>(restored.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, restoredHold.Status);
        PatternEntryPhase restoredEntry = Assert.IsType<PatternEntryPhase>(restored.HeldPhase);
        Assert.Equal(PhaseStatus.Active, restoredEntry.Status);
        Assert.Same(restoredEntry, restored.EffectivePhase);

        ac.Phases = restored;
        PhaseRunner.EndHold(ac, ctx, HoldResume.InPlace);

        // The restored hold's OnEnd(Completed) puts back the entry route it saved before the snapshot.
        Assert.Same(restoredEntry, restored.CurrentPhase);
        Assert.Equal(PatternEntryPhase.PatternEntryTargetName, Assert.Single(ac.Targets.NavigationRoute).Name);
        Assert.Equal(entryElapsed, restoredEntry.ElapsedSeconds);

        PhaseRunner.Tick(ac, ctx);

        Assert.Equal(entryElapsed + 1.0, restoredEntry.ElapsedSeconds);
    }

    private static (AircraftState Aircraft, PhaseContext Context, PatternEntryPhase Entry) AircraftOnPatternEntry()
    {
        (AircraftState ac, PhaseContext ctx, PatternEntryPhase entry) = AircraftWithUnstartedPatternEntry();
        ac.Phases!.Start(ctx);
        return (ac, ctx, entry);
    }

    private static (AircraftState Aircraft, PhaseContext Context, PatternEntryPhase Entry) AircraftWithUnstartedPatternEntry()
    {
        RunwayInfo runway = Oak28R();
        PatternEntryPhase entry = PatternEntryTo(runway);
        (AircraftState ac, PhaseContext ctx) = AircraftAt(runway, GeoMath.ProjectPoint(EntryPoint(runway), runway.TrueHeading + 180, 6.0));
        ac.Phases!.Add(entry);
        return (ac, ctx, entry);
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("OAK", "28R") ?? throw new InvalidOperationException("OAK 28R missing");

    /// <summary>A left-traffic downwind for 28R lies to the south; the entry point is 1 nm abeam the threshold.</summary>
    private static LatLon EntryPoint(RunwayInfo runway) =>
        GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading + (-90), 1.0);

    private static PatternEntryPhase PatternEntryTo(RunwayInfo runway)
    {
        LatLon entryPoint = EntryPoint(runway);
        return new PatternEntryPhase
        {
            EntryLat = entryPoint.Lat,
            EntryLon = entryPoint.Lon,
            PatternAltitude = runway.ElevationFt + 1000,
            Kind = PatternEntryKind.Direct,
        };
    }

    private static (AircraftState Aircraft, PhaseContext Context) AircraftWith(params Phase[] phases)
    {
        (AircraftState ac, PhaseContext ctx) = AircraftAt(Oak28R(), new LatLon(37.62, -122.12));
        foreach (Phase phase in phases)
        {
            ac.Phases!.Add(phase);
        }

        ac.Phases!.Start(ctx);
        return (ac, ctx);
    }

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

    private static AirspaceBoundaryHoldPhase SfoBravoHold() =>
        new()
        {
            AirspaceClass = AirspaceClass.Bravo,
            Ident = "SFO",
            NameText = "SFO",
            ReferencePosition = new LatLon(37.6213, -122.3790),
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
        };

    /// <summary>
    /// A phase that counts its lifecycle calls. The pattern and approach phases have no <c>OnEnd</c> of their own, so a real
    /// one cannot show how many times it was ended; this one records each call.
    /// </summary>
    private sealed class LifecycleProbePhase : Phase
    {
        public int Starts { get; private set; }

        public int Ticks { get; private set; }

        public List<PhaseStatus> Ends { get; } = [];

        public override string Name => "LifecycleProbe";

        public override PhaseDto ToSnapshot() => throw new NotSupportedException();

        public override void OnStart(PhaseContext ctx) => Starts++;

        public override bool OnTick(PhaseContext ctx)
        {
            Ticks++;
            return false;
        }

        public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus) => Ends.Add(endStatus);
    }
}
