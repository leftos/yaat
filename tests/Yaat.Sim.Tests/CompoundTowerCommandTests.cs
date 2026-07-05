using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// E2E tests for compound commands starting with tower commands (CTO, LUAW)
/// followed by post-departure blocks (DCT, FH, CM, etc.).
/// Verifies that blocks after `;` are enqueued and execute after phases complete.
/// </summary>
public class CompoundTowerCommandTests
{
    private readonly ITestOutputHelper _output;

    public CompoundTowerCommandTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );

    private static AircraftState MakeLinedUpAircraft(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            // VFR — the CTO modifiers exercised in these tests (MR270) are VFR-only.
            // IFR aircraft are restricted to bare CTO or a numeric heading vector.
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Altitude = PlannedAltitude.Vfr(5000),
                FlightRules = "VFR",
            },
        };

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase { Departure = new DefaultDeparture(), CruiseAltitude = 5000 });

        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        return ac;
    }

    private static PhaseContext MakePhaseContext(AircraftState ac, double delta = 1.0)
    {
        var runway = ac.Phases?.AssignedRunway;
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = delta,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? 0,
            Logger = NullLogger.Instance,
        };
    }

    /// <summary>
    /// EF 28R, CLAND — both clauses are tower commands that get applied in the same
    /// parallel block. The result message must include both verbs ("Enter final" AND
    /// "Cleared to land") so the RPO sees the full outcome. Pre-fix, only the first
    /// clause's message was returned and the second was silently dropped, which left
    /// the RPO unsure whether the landing clearance had taken effect.
    /// </summary>
    [Fact]
    public void EnterFinal_Comma_ClearedToLand_ReturnsBothMessages()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var rwy = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var ac = new AircraftState
        {
            Callsign = "N42416",
            AircraftType = "C172",
            // Place 0.05° south of the threshold, well into the OAK pattern bounds.
            Position = new LatLon(rwy.ThresholdLatitude - 0.05, rwy.ThresholdLongitude),
            TrueHeading = new TrueHeading(280),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                HasFlightPlan = true,
            },
        };
        var waypoints = PatternGeometry.Compute(rwy, AircraftCategory.Piston, PatternDirection.Left, null, null, null);
        var phases = new PhaseList { AssignedRunway = rwy };
        phases.Add(new Yaat.Sim.Phases.Pattern.DownwindPhase { Waypoints = waypoints });
        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var compound = new CompoundCommand([new ParsedBlock(null, [new EnterFinalCommand("28R"), new ClearedToLandCommand()])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.NotNull(result.Message);
        Assert.Contains("Enter final", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cleared to land", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases?.LandingClearance);
    }

    /// <summary>
    /// CTO MR270; DCT SUNOL — the DCT block must be enqueued after the
    /// tower command (CTO) is processed. Currently the DCT block is silently
    /// dropped because DispatchWithPhase returns early.
    /// </summary>
    [Fact]
    public void Cto_Semicolon_Dct_EnqueuesPostDepartureBlock()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway = Oak28R();
        var ac = MakeLinedUpAircraft(runway);

        // Parse the compound command
        var parseResult = CommandParser.ParseCompound("CTO MR270; DCT SUNOL", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");
        Assert.Equal(2, parseResult.Value!.Blocks.Count);

        // Dispatch the compound command
        var result = CommandDispatcher.DispatchCompound(parseResult.Value, ac, TestDispatch.Context(Random.Shared, validateDctFixes: false));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");

        // The DCT block must be enqueued in the command queue
        Assert.True(ac.Queue.Blocks.Count > 0, "DCT block was not enqueued — remaining compound blocks after tower command were dropped");

        // The queue should not be complete yet (DCT hasn't executed)
        Assert.False(ac.Queue.IsComplete, "Queue should not be complete before phases finish");
    }

    /// <summary>
    /// Full E2E: CTO MR270; DCT SUNOL — aircraft takes off, turns right 270°,
    /// climbs through InitialClimb, then after phases complete the DCT block
    /// executes and sets SUNOL as navigation target.
    /// Logs position/heading/phase each tick for diagnostics.
    /// </summary>
    [Fact]
    public void Cto_MR270_Dct_ExecutesAfterPhasesComplete()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway = Oak28R();
        var ac = MakeLinedUpAircraft(runway);

        // Parse and dispatch
        var parseResult = CommandParser.ParseCompound("CTO MR270; DCT SUNOL", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(Random.Shared, validateDctFixes: false));
        Assert.True(result.Success, $"Dispatch failed: {result.Message}");

        // Tick the simulation: PhaseRunner then FlightPhysics per tick
        bool phasesCompleted = false;
        bool dctApplied = false;
        int maxTicks = 600;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            // PhaseRunner first (matches SimulationEngine order)
            var ctx = MakePhaseContext(ac);
            PhaseRunner.Tick(ac, ctx);

            // FlightPhysics (includes UpdateCommandQueue)
            FlightPhysics.Update(ac, 1.0);

            // Log every 10 ticks or on key events
            string phaseName = ac.Phases?.CurrentPhase?.Name ?? (ac.Phases?.IsComplete == true ? "COMPLETE" : "NONE");
            bool isPhaseTransition = tick == 0 || (tick > 0 && phaseName != (ac.Phases?.CurrentPhase?.Name ?? ""));

            if (tick % 10 == 0 || isPhaseTransition || !phasesCompleted)
            {
                _output.WriteLine(
                    $"t={tick, 4}: phase={phaseName, -20} hdg={ac.TrueHeading.Degrees:F0} alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F0} "
                        + $"onGround={ac.IsOnGround} navRoute={ac.Targets.NavigationRoute.Count} queueBlocks={ac.Queue.Blocks.Count} "
                        + $"queueIdx={ac.Queue.CurrentBlockIndex} queueDone={ac.Queue.IsComplete}"
                );
            }

            // Detect when phases complete
            if (!phasesCompleted && ac.Phases?.CurrentPhase is null)
            {
                phasesCompleted = true;
                _output.WriteLine($"*** Phases completed at tick {tick} ***");
            }

            // Detect when DCT block applies (navigation route gets SUNOL)
            if (phasesCompleted && !dctApplied && ac.Targets.NavigationRoute.Count > 0)
            {
                dctApplied = true;
                _output.WriteLine($"*** DCT applied at tick {tick}: route=[{string.Join(", ", ac.Targets.NavigationRoute.Select(r => r.Name))}] ***");
            }

            if (dctApplied)
            {
                break;
            }
        }

        // Assertions
        Assert.True(phasesCompleted, "Phases should have completed (InitialClimb departure heading reached)");
        Assert.True(dctApplied, "DCT SUNOL should have been applied after phases completed");
        Assert.Contains(ac.Targets.NavigationRoute, t => t.Name == "SUNOL");
    }

    // -------------------------------------------------------------------------
    // Compound pattern entry + tower command (dry-run validation)
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraftWithoutPhases()
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "C172",
            Position = new LatLon(37.72, -122.22),
            TrueHeading = new TrueHeading(280),
            Altitude = 1500,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK", FlightRules = "VFR" },
        };
    }

    [Fact]
    public void Erd_Comma_Cland_Succeeds_WithoutPriorPhases()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ac = MakeAircraftWithoutPhases();

        var parseResult = CommandParser.ParseCompound("ERD 28R, CL", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.NotNull(ac.Phases);
        Assert.Equal("28R", ac.Phases!.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
    }

    [Fact]
    public void Erd_Semicolon_Cland_Succeeds_WithoutPriorPhases()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ac = MakeAircraftWithoutPhases();

        var parseResult = CommandParser.ParseCompound("ERD 28R; CL", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.NotNull(ac.Phases);
        Assert.Equal("28R", ac.Phases!.AssignedRunway?.Designator);
        // CLAND is in a deferred block — it won't be applied until the queue advances.
        // But the dispatch itself should succeed (not be rejected upfront).
    }

    [Fact]
    public void Cland_Alone_Fails_WithoutPhases()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ac = MakeAircraftWithoutPhases();

        var parseResult = CommandParser.ParseCompound("CL", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.False(result.Success, "CLAND without phases should fail");
    }

    [Fact]
    public void Ground_Command_Fails_While_Airborne()
    {
        var ac = MakeAircraftWithoutPhases();
        ac.IsOnGround = false;

        var parseResult = CommandParser.ParseCompound("TAXI A", ac.FlightPlan.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.False(result.Success, "Ground command while airborne should fail");
    }

    // --- Word-alias separators: AND for `,`, THEN for `;` (issue #164) ---

    [Fact]
    public void ParseCompound_AndAlias_ProducesSameStructureAsComma()
    {
        var aliasResult = CommandParser.ParseCompound("CTO MRT 28R, CL");
        var aliasResult2 = CommandParser.ParseCompound("CTO MRT 28R AND CL");

        Assert.True(aliasResult.IsSuccess, $"comma form failed: {aliasResult.Reason}");
        Assert.True(aliasResult2.IsSuccess, $"AND form failed: {aliasResult2.Reason}");

        var commaBlocks = aliasResult.Value!.Blocks;
        var andBlocks = aliasResult2.Value!.Blocks;

        Assert.Equal(commaBlocks.Count, andBlocks.Count);
        for (int b = 0; b < commaBlocks.Count; b++)
        {
            Assert.Equal(commaBlocks[b].Commands.Count, andBlocks[b].Commands.Count);
            for (int c = 0; c < commaBlocks[b].Commands.Count; c++)
            {
                Assert.Equal(commaBlocks[b].Commands[c].GetType(), andBlocks[b].Commands[c].GetType());
            }
        }
    }

    [Fact]
    public void ParseCompound_ThenAlias_ProducesSameStructureAsSemicolon()
    {
        var semiResult = CommandParser.ParseCompound("CTO MRT 28R; CL");
        var thenResult = CommandParser.ParseCompound("CTO MRT 28R THEN CL");

        Assert.True(semiResult.IsSuccess, $"semicolon form failed: {semiResult.Reason}");
        Assert.True(thenResult.IsSuccess, $"THEN form failed: {thenResult.Reason}");

        var semiBlocks = semiResult.Value!.Blocks;
        var thenBlocks = thenResult.Value!.Blocks;

        Assert.Equal(semiBlocks.Count, thenBlocks.Count);
        for (int b = 0; b < semiBlocks.Count; b++)
        {
            Assert.Equal(semiBlocks[b].Commands.Count, thenBlocks[b].Commands.Count);
            for (int c = 0; c < semiBlocks[b].Commands.Count; c++)
            {
                Assert.Equal(semiBlocks[b].Commands[c].GetType(), thenBlocks[b].Commands[c].GetType());
            }
        }
    }

    // --- Immediate takeoff: dispatcher wires CTO IMM → IsExpeditingLineup ---

    private static CompoundCommand Block(ParsedCommand cmd) => new([new ParsedBlock(null, [cmd])]);

    [Fact]
    public void CtoImmediate_SetsExpeditingLineupFlag()
    {
        var ac = MakeLinedUpAircraft(Oak28R());
        var compound = Block(new ClearedForTakeoffCommand(new DefaultDeparture()) { Immediate = true });

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.True(ac.Ground.IsExpeditingLineup);
    }

    [Fact]
    public void PlainCto_DoesNotSetExpeditingLineupFlag()
    {
        var ac = MakeLinedUpAircraft(Oak28R());
        var compound = Block(new ClearedForTakeoffCommand(new DefaultDeparture()));

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new Random(42), validateDctFixes: false));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.False(ac.Ground.IsExpeditingLineup);
    }

    [Fact]
    public void Ctoc_ClearsExpeditingLineupFlag()
    {
        var ac = MakeLinedUpAircraft(Oak28R());
        CommandDispatcher.DispatchCompound(
            Block(new ClearedForTakeoffCommand(new DefaultDeparture()) { Immediate = true }),
            ac,
            TestDispatch.Context(new Random(42), validateDctFixes: false)
        );
        Assert.True(ac.Ground.IsExpeditingLineup);

        CommandDispatcher.DispatchCompound(
            Block(new CancelTakeoffClearanceCommand()),
            ac,
            TestDispatch.Context(new Random(42), validateDctFixes: false)
        );

        Assert.False(ac.Ground.IsExpeditingLineup);
    }
}
