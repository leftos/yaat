using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The follow-up block a navigation verb queues behind itself (JRADO/JRADI/JAWY intercept, DEPART
/// reach-fix) holds no <c>ApplyAction</c> across a snapshot restore, so
/// <c>SimulationEngine.RehydrateRestoredQueueBlocks</c> rebuilds it from the instruction's canonical
/// <c>SourceCommandText</c>. Without that text the restore used to drop the block silently and the queued
/// instruction — fly the radial, join the airway, turn at the fix — never happened.
/// <c>RestoredBlockWithNoSourceText</c> covers the block that is still unrecoverable and must be dropped
/// with an RPO warning instead.
/// </summary>
[Collection("NavDbMutator")]
public class NavigationQueuedBlockRestoreTests
{
    private const string Callsign = "N429YA";
    private const int TickBudgetSeconds = 90;

    /// <summary>A long-standing Bay Area Victor airway, present in <c>TestData/NavData.dat</c>.</summary>
    private const string Airway = "V27";

    private const int InterceptPreTickSeconds = 25;
    private const double InterceptOffsetNm = 4.0;
    private const double InterceptAlongNm = 11.0;
    private const double MinSegmentNm = 20.0;

    public NavigationQueuedBlockRestoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void JradoInterceptBlock_RestoredFromSnapshot_FliesTheRadial()
    {
        // On the OAK 090 radial (the trigger compares the true bearing from the fix to the aircraft),
        // heading north so the block has a heading of its own to fly once it fires.
        AircraftState aircraft = AircraftAt(Project(Fix("OAK"), 90, 10), 360);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, "JRADO OAK090");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal("JRADO OAK090", queued.SourceCommandText);
        Assert.NotEqual(90, aircraft.Targets.AssignedMagneticHeading?.Degrees);

        StateSnapshotDto snapshot = live.CaptureSnapshot();

        // The same follow-up fires twice: live on the engine that queued it, and after a restore.
        TickUntil(live, () => aircraft.Targets.AssignedMagneticHeading?.Degrees == 90, "the live radial heading");

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        restored.TickOneSecond();

        Assert.Equal(90, restoredAircraft.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Empty(restoredAircraft.Targets.NavigationRoute);
        AssertSameQueueAs(aircraft, restoredAircraft);
    }

    [Fact]
    public void JradiInterceptBlock_RestoredFromSnapshot_NavigatesToTheFix()
    {
        AircraftState aircraft = AircraftAt(Project(Fix("OAK"), 90, 10), 360);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, "JRADI OAK090");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal("JRADI OAK090", queued.SourceCommandText);
        Assert.Empty(aircraft.Targets.NavigationRoute);

        StateSnapshotDto snapshot = live.CaptureSnapshot();
        TickUntil(live, () => aircraft.Targets.NavigationRoute.Count == 1, "the live inbound navigation route");

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        restored.TickOneSecond();

        Assert.Equal("OAK", Assert.Single(restoredAircraft.Targets.NavigationRoute).Name);
        Assert.Null(restoredAircraft.Targets.AssignedMagneticHeading);
        AssertSameQueueAs(aircraft, restoredAircraft);
    }

    [Fact]
    public void DepartBlock_RestoredFromSnapshot_FliesTheDepartHeading()
    {
        AircraftState aircraft = AircraftAt(Fix("SUNOL"), 360);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, "DEPART SUNOL 090");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal("DEPART SUNOL 090", queued.SourceCommandText);
        Assert.Equal("SUNOL", Assert.Single(aircraft.Targets.NavigationRoute).Name);

        StateSnapshotDto snapshot = live.CaptureSnapshot();
        TickUntil(live, () => aircraft.Targets.AssignedMagneticHeading?.Degrees == 90, "the live depart heading");

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        restored.TickOneSecond();

        Assert.Equal(90, restoredAircraft.Targets.AssignedMagneticHeading?.Degrees);
        Assert.Empty(restoredAircraft.Targets.NavigationRoute);
        AssertSameQueueAs(aircraft, restoredAircraft);
    }

    /// <summary>
    /// A heading of 360 is stored as 0, so the canonical text has to print it the way the block's own
    /// Description does (<c>ToDisplayInt</c>, 001..360): <c>DEPART SUNOL 000</c> is not a heading the parser
    /// accepts, and the restored block would be dropped.
    /// </summary>
    [Fact]
    public void DepartHeading360Block_RestoredFromSnapshot_FliesTheDepartHeading()
    {
        AircraftState aircraft = AircraftAt(Fix("SUNOL"), 360);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, "DEPART SUNOL 360");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal("DEPART SUNOL 360", queued.SourceCommandText);

        StateSnapshotDto snapshot = live.CaptureSnapshot();
        TickUntil(live, () => aircraft.Targets.AssignedMagneticHeading?.ToDisplayInt() == 360, "the live 360 depart heading");

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        restored.TickOneSecond();

        Assert.Equal(360, restoredAircraft.Targets.AssignedMagneticHeading?.ToDisplayInt());
        Assert.Empty(restoredAircraft.Targets.NavigationRoute);
        AssertSameQueueAs(aircraft, restoredAircraft);
    }

    /// <summary>
    /// The depart block publishes a crossing-restriction speed already stamped on the fix (issue #184) as
    /// the aircraft's speed ceiling on the turn. That lives only in the block's apply closure, so it is lost
    /// unless the restored block is rebuilt from the same closure. The ceiling is pre-set lower than the
    /// crossing speed, because the fix-sequencing pass publishes a crossed restriction itself only by
    /// <c>Math.Min</c> — the block's ATC-assigned crossing speed overwrites it instead, which is what
    /// distinguishes the block's closure from the physics.
    /// </summary>
    [Fact]
    public void DepartBlock_RestoredFromSnapshot_LiftsTheCeilingToTheCrossingSpeed()
    {
        AircraftState aircraft = AircraftAt(Project(Fix("SUNOL"), 180, 1.2), 360);
        aircraft.Targets.SpeedCeiling = 180;

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, "CFIX SUNOL 6000 210");
        Dispatch(live, aircraft, "DEPART SUNOL 090");
        Assert.Equal(210, Assert.Single(aircraft.Targets.NavigationRoute).SpeedRestriction?.SpeedKts);

        StateSnapshotDto snapshot = live.CaptureSnapshot();

        // The live block proves the premise: reaching the fix publishes the ATC-assigned crossing speed over
        // the ceiling already standing.
        TickUntil(live, () => aircraft.Targets.SpeedCeiling == 210, "the live depart block never published the crossing speed ceiling");

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        TickUntil(
            restored,
            () => restoredAircraft.Targets.SpeedCeiling == 210,
            "the restored depart block never published the crossing speed ceiling"
        );
    }

    /// <summary>
    /// A restored JAWY block flies the fix sequence the live one planned. The aircraft starts off the airway on
    /// an intercept heading and is flown 25 s before the snapshot, so the restore cannot re-derive the live plan
    /// from its position: the block's own trigger names the segment, and the sequence is walked from there.
    /// </summary>
    [Fact]
    public void AirwayInterceptBlock_RestoredFromSnapshot_FliesTheLiveAirwayRoute()
    {
        IReadOnlyList<string> fixes = AirwayFixes(Airway);
        (int originIdx, LatLon origin, LatLon ahead, double course) = AirwaySegment(fixes);

        // Off the airway on an intercept heading: 11 NM along the segment, 4 NM left of it, turning 60° right
        // so the segment's radial is crossed about 50 s in.
        AircraftState aircraft = AircraftAt(Project(Project(origin, course, InterceptAlongNm), course - 90, InterceptOffsetNm), course + 60);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, $"JAWY {Airway}");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal($"JAWY {Airway}", queued.SourceCommandText);
        Assert.Equal(fixes[originIdx], queued.Trigger!.FixName);
        Assert.Equal((int)Math.Round(course) % 360, queued.Trigger!.Radial);
        Assert.Empty(aircraft.Targets.NavigationRoute);

        TickFor(live, InterceptPreTickSeconds);
        Assert.False(queued.IsApplied, "test setup: the intercept should not have fired within the pre-snapshot tick");

        StateSnapshotDto snapshot = live.CaptureSnapshot();

        TickUntil(live, () => aircraft.Targets.NavigationRoute.Count > 0, "the live airway route");
        List<string> liveRoute = [.. aircraft.Targets.NavigationRoute.Select(t => t.Name)];

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        TickUntil(restored, () => restoredAircraft.Targets.NavigationRoute.Count > 0, "the restored airway route");

        Assert.Equal(liveRoute, restoredAircraft.Targets.NavigationRoute.Select(t => t.Name));
        Assert.Equal(fixes[originIdx + 1], restoredAircraft.Targets.NavigationRoute[0].Name);
        AssertSameQueueAs(aircraft, restoredAircraft);
    }

    /// <summary>
    /// Inbound along the airway's extended centreline to its first fix: the live plan finds no fix behind, so the
    /// trigger names the first fix still to fly and its radial is the bearing from the aircraft — which on the
    /// centreline is the segment course the *next* fix's bearing from that fix also matches. The rebuild must start
    /// at the named fix, not one fix late.
    /// </summary>
    [Fact]
    public void AirwayFirstFixInterceptBlock_RestoredFromSnapshot_RebuildsTheLiveFixSequence()
    {
        IReadOnlyList<string> fixes = AirwayFixes(Airway);
        (int firstIdx, LatLon first, double course) = FirstFixAndCourse(fixes);

        AircraftState aircraft = AircraftAt(Project(first, course + 180, 5.0), course);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, $"JAWY {Airway}");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal(fixes[firstIdx], queued.Trigger!.FixName);

        StateSnapshotDto snapshot = live.CaptureSnapshot();

        // The live block's own sequence, and the premise that it starts at the fix the trigger names.
        List<string> liveRoute = ApplyAndReadRoute(aircraft, queued);
        Assert.Equal(queued.Trigger!.FixName, liveRoute[0]);

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        restored.TickOneSecond();
        CommandBlock restoredQueued = QueuedFollowUpBlock(restoredAircraft);

        List<string> restoredRoute = ApplyAndReadRoute(restoredAircraft, restoredQueued);
        Assert.Equal(queued.Trigger!.FixName, restoredRoute[0]);
        Assert.Equal(liveRoute, restoredRoute);
    }

    /// <summary>
    /// Inbound to the airway's far end from beyond it: the live plan again finds no fix behind, and the fix it
    /// names is the last one, whose only neighbour lies the way the aircraft came from — a rebuild that trusted the
    /// radial's direction would start at that neighbour and walk the whole airway backwards.
    /// </summary>
    [Fact]
    public void AirwayFarEndInterceptBlock_RestoredFromSnapshot_RebuildsTheLiveFixSequence()
    {
        IReadOnlyList<string> fixes = AirwayFixes(Airway);
        (int lastIdx, LatLon last, double course) = LastFixAndCourse(fixes);

        AircraftState aircraft = AircraftAt(Project(last, course, 5.0), course + 180);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, $"JAWY {Airway}");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal(fixes[lastIdx], queued.Trigger!.FixName);

        StateSnapshotDto snapshot = live.CaptureSnapshot();

        List<string> liveRoute = ApplyAndReadRoute(aircraft, queued);
        Assert.Equal(queued.Trigger!.FixName, liveRoute[0]);

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        restored.TickOneSecond();
        CommandBlock restoredQueued = QueuedFollowUpBlock(restoredAircraft);

        List<string> restoredRoute = ApplyAndReadRoute(restoredAircraft, restoredQueued);
        Assert.Equal(queued.Trigger!.FixName, restoredRoute[0]);
        Assert.Equal(liveRoute, restoredRoute);
    }

    /// <summary>
    /// A saved trigger naming a fix this navdata no longer holds cannot rebuild the sequence: the block is dropped
    /// with the RPO warning rather than throwing on the missing fix.
    /// </summary>
    [Fact]
    public void RestoredAirwayBlockWhoseTriggerFixIsUnknown_IsDroppedWithAnRpoWarning()
    {
        AircraftState aircraft = AircraftAt(Fix(AirwayFixes(Airway)[0]), 090);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, $"JAWY {Airway}");

        StateSnapshotDto snapshot = live.CaptureSnapshot();
        int followUpIdx = snapshot.Aircraft[0].Queue.Blocks.FindIndex(b => b.Trigger is not null);
        Assert.InRange(followUpIdx, 0, snapshot.Aircraft[0].Queue.Blocks.Count - 1);
        CommandBlockDto block = snapshot.Aircraft[0].Queue.Blocks[followUpIdx];
        BlockTriggerDto trigger = Assert.IsType<BlockTriggerDto>(block.Trigger);
        snapshot.Aircraft[0].Queue.Blocks[followUpIdx] = WithTrigger(
            block,
            new BlockTriggerDto
            {
                Type = trigger.Type,
                FixName = "ZZZZZ",
                FixLat = trigger.FixLat,
                FixLon = trigger.FixLon,
                Radial = trigger.Radial,
            }
        );

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(snapshot);
        List<string> warnings = [];
        restored.WarningEmitted += (_, warning) => warnings.Add(warning);
        restored.TickOneSecond();

        Assert.DoesNotContain(restoredAircraft.Queue.Blocks, b => b.Description.StartsWith($"intercept {Airway}:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("queued command lost after restore", StringComparison.Ordinal));
    }

    /// <summary>
    /// A block the controller's own condition queued is not one of the four follow-up blocks: it carries the
    /// JRADO instruction, and firing it runs the whole instruction — the immediate vector and a fresh intercept
    /// block — so it keeps the ordinary rehydration path, its own description, and one queued follow-up.
    /// </summary>
    [Fact]
    public void ConditionalJradoBlock_RestoredFromSnapshot_RunsTheWholeInstruction()
    {
        AircraftState aircraft = AircraftAt(Fix("SUNOL"), 360);

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Dispatch(live, aircraft, "AT SUNOL JRADO OAK090");

        CommandBlock queued = QueuedFollowUpBlock(aircraft);
        Assert.Equal("at SUNOL: JRADO OAK090", queued.Description);
        Assert.Equal("AT SUNOL JRADO OAK090", queued.SourceCommandText);

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(live.CaptureSnapshot());
        CommandBlock restoredQueued = QueuedFollowUpBlock(restoredAircraft);
        restored.TickOneSecond();

        Assert.Equal("at SUNOL: JRADO OAK090", restoredQueued.Description);
        Assert.Equal(2, restoredAircraft.Queue.Blocks.Count);
        CommandBlock followUp = Assert.Single(restoredAircraft.Queue.Blocks, b => b.Description == "at OAK R090: FH 090");
        Assert.Equal("JRADO OAK090", followUp.SourceCommandText);
    }

    [Fact]
    public void RestoredBlockWithNoSourceText_IsDroppedWithAnRpoWarning()
    {
        AircraftState aircraft = AircraftAt(Fix("OAK"), 090);
        aircraft.Queue.Blocks.Add(new CommandBlock { Description = "SPD 250", NaturalDescription = "Reduce to 250" });

        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);
        Assert.Single(aircraft.Queue.Blocks);

        (SimulationEngine restored, AircraftState restoredAircraft) = Restore(live.CaptureSnapshot());
        List<string> warnings = [];
        restored.WarningEmitted += (_, warning) => warnings.Add(warning);
        restored.TickOneSecond();

        // No apply action and no source text: nothing can rebuild it, so it is dropped and the RPO is told
        // rather than left with a queue entry that fires as a silent no-op.
        Assert.Empty(restoredAircraft.Queue.Blocks);
        Assert.Contains(warnings, w => w.Contains("queued command lost after restore", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("SPD 250", StringComparison.Ordinal));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static AircraftState AircraftAt(LatLon position, double trueHeadingDeg) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(trueHeadingDeg),
            TrueTrack = new TrueHeading(trueHeadingDeg),
            Altitude = 8000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            Transponder = new AircraftTransponder
            {
                Code = 4521,
                AssignedCode = 4521,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                Destination = "OAK",
                FlightRules = "IFR",
                HasFlightPlan = true,
            },
            Track = new AircraftTrack(),
        };

    private static SimulationEngine NewEngine() =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "nav-queued-block-restore",
                ScenarioName = "Navigation queued block restore",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };

    private static LatLon Fix(string name)
    {
        (double Lat, double Lon) position = NavigationDatabase.Instance.GetFixPosition(name)!.Value;
        return new LatLon(position.Lat, position.Lon);
    }

    /// <summary>The pinned airway's fix sequence, asserting the pinned id still exists in this navdata.</summary>
    private static IReadOnlyList<string> AirwayFixes(string airwayId) =>
        Assert.IsAssignableFrom<IReadOnlyList<string>>(NavigationDatabase.Instance.GetAirwayFixes(airwayId));

    /// <summary>
    /// The pinned airway's first segment long enough to lay an intercept geometry on (both fixes resolving). The
    /// aircraft flies at a fixed speed, so a short segment cannot give the ~50 s intercept the test needs.
    /// </summary>
    private static (int OriginIdx, LatLon Origin, LatLon Ahead, double Course) AirwaySegment(IReadOnlyList<string> fixes)
    {
        for (int i = 0; i + 1 < fixes.Count; i++)
        {
            if (!TryFix(fixes[i], out LatLon origin) || !TryFix(fixes[i + 1], out LatLon ahead))
            {
                continue;
            }

            if (GeoMath.DistanceNm(origin, ahead) >= MinSegmentNm)
            {
                return (i, origin, ahead, GeoMath.BearingTo(origin, ahead));
            }
        }

        throw new InvalidOperationException($"airway {Airway} has no segment of at least {MinSegmentNm} NM between resolvable fixes");
    }

    /// <summary>The airway's first fix with a resolvable successor, and the course from it to that successor.</summary>
    private static (int Idx, LatLon Fix, double Course) FirstFixAndCourse(IReadOnlyList<string> fixes)
    {
        for (int i = 0; i + 1 < fixes.Count; i++)
        {
            if (!TryFix(fixes[i], out LatLon from) || !TryFix(fixes[i + 1], out LatLon to))
            {
                continue;
            }

            return (i, from, GeoMath.BearingTo(from, to));
        }

        throw new InvalidOperationException($"airway {Airway} has no two resolvable fixes in sequence");
    }

    /// <summary>The airway's last fix with a resolvable predecessor, and the course into it from that predecessor.</summary>
    private static (int Idx, LatLon Fix, double Course) LastFixAndCourse(IReadOnlyList<string> fixes)
    {
        for (int i = fixes.Count - 1; i > 0; i--)
        {
            if (!TryFix(fixes[i - 1], out LatLon from) || !TryFix(fixes[i], out LatLon to))
            {
                continue;
            }

            return (i, to, GeoMath.BearingTo(from, to));
        }

        throw new InvalidOperationException($"airway {Airway} has no two resolvable fixes in sequence");
    }

    /// <summary>A copy of a saved block DTO carrying a different trigger — the shape an older or drifted save has.</summary>
    private static CommandBlockDto WithTrigger(CommandBlockDto block, BlockTriggerDto trigger) =>
        new()
        {
            Trigger = trigger,
            Commands = block.Commands,
            IsApplied = block.IsApplied,
            TriggerMet = block.TriggerMet,
            TriggerClosestApproach = block.TriggerClosestApproach,
            TriggerMissed = block.TriggerMissed,
            TriggerCrossingObserved = block.TriggerCrossingObserved,
            TriggerTerminatorObserved = block.TriggerTerminatorObserved,
            IsWaitBlock = block.IsWaitBlock,
            WaitRemainingSeconds = block.WaitRemainingSeconds,
            WaitRemainingDistanceNm = block.WaitRemainingDistanceNm,
            Description = block.Description,
            NaturalDescription = block.NaturalDescription,
            SourceCommandText = block.SourceCommandText,
            Dimensions = block.Dimensions,
            HasTrackCommand = block.HasTrackCommand,
            TrackApplied = block.TrackApplied,
            HasDeleteCommand = block.HasDeleteCommand,
            IsScenarioScripted = block.IsScenarioScripted,
        };

    /// <summary>The route a block's own apply closure installs, in order.</summary>
    private static List<string> ApplyAndReadRoute(AircraftState aircraft, CommandBlock block)
    {
        Assert.NotNull(block.ApplyAction);
        Assert.True(block.ApplyAction!(aircraft).Success);
        return [.. aircraft.Targets.NavigationRoute.Select(t => t.Name)];
    }

    private static bool TryFix(string name, out LatLon position)
    {
        if (NavigationDatabase.Instance.GetFixPosition(name) is { } point)
        {
            position = new LatLon(point.Lat, point.Lon);
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>A point <paramref name="distanceNm"/> from the fix along <paramref name="bearingDeg"/>.</summary>
    private static LatLon Project(LatLon from, double bearingDeg, double distanceNm)
    {
        (double Lat, double Lon) point = GeoMath.ProjectPointRaw(from.Lat, from.Lon, bearingDeg, distanceNm);
        return new LatLon(point.Lat, point.Lon);
    }

    private static void Dispatch(SimulationEngine engine, AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            aircraft,
            engine.BuildDispatchContext(aircraft, false, facilityHint: null)
        );
        Assert.True(result.Success, result.Message);
    }

    /// <summary>The follow-up block the dispatched verb queued: the one carrying a trigger.</summary>
    private static CommandBlock QueuedFollowUpBlock(AircraftState aircraft) => Assert.Single(aircraft.Queue.Blocks, b => b.Trigger is not null);

    private static (SimulationEngine Engine, AircraftState Aircraft) Restore(StateSnapshotDto snapshot)
    {
        SimulationEngine restored = NewEngine();
        restored.RestoreFromSnapshot(snapshot);
        return (restored, Assert.IsType<AircraftState>(restored.FindAircraft(Callsign)));
    }

    private static void TickUntil(SimulationEngine engine, Func<bool> reached, string what)
    {
        for (int second = 0; (second < TickBudgetSeconds) && !reached(); second++)
        {
            engine.TickOneSecond();
        }

        Assert.True(reached(), $"the queued instruction never took effect: {what}");
    }

    private static void TickFor(SimulationEngine engine, int seconds)
    {
        for (int second = 0; second < seconds; second++)
        {
            engine.TickOneSecond();
        }
    }

    /// <summary>
    /// The restored queue holds exactly the live one's blocks, with one triggered follow-up — a rehydration that
    /// dispatched the instruction instead of rebuilding the block would have queued a second one.
    /// </summary>
    private static void AssertSameQueueAs(AircraftState live, AircraftState restored)
    {
        Assert.Single(restored.Queue.Blocks, b => b.Trigger is not null);
        Assert.Equal(live.Queue.Blocks.Count, restored.Queue.Blocks.Count);
    }
}
