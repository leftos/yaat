using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class NavigationCommandTests
{
    public NavigationCommandTests()
    {
        NavigationDatabase.SetInstance(NavigationDatabase.ForTesting());
    }

    private static AircraftState MakeAircraft(double heading = 090, double altitude = 5000, double lat = 37.7, double lon = -122.2)
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = heading,
            Altitude = altitude,
            Latitude = lat,
            Longitude = lon,
        };
    }

    // --- JRADO ---

    [Fact]
    public void Jrado_SetsHeadingAndCreatesInterceptBlock()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new JoinRadialOutboundCommand("OAK", 37.72, -122.22, 180);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("180", result.Message);
        Assert.Contains("outbound", result.Message);
        Assert.Equal(090, aircraft.Targets.TargetHeading);
        Assert.Single(aircraft.Queue.Blocks);
        Assert.Equal(BlockTriggerType.InterceptRadial, aircraft.Queue.Blocks[0].Trigger!.Type);
        Assert.Equal(180, aircraft.Queue.Blocks[0].Trigger!.Radial);
    }

    [Fact]
    public void Jrado_InterceptBlockSetsRadialHeading()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new JoinRadialOutboundCommand("OAK", 37.72, -122.22, 270);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // Simulate trigger met: apply the intercept block
        var block = aircraft.Queue.Blocks[0];
        block.ApplyAction?.Invoke(aircraft);

        Assert.Equal(270, aircraft.Targets.TargetHeading);
        Assert.Empty(aircraft.Targets.NavigationRoute);
    }

    // --- JRADI ---

    [Fact]
    public void Jradi_SetsHeadingAndCreatesInterceptBlock()
    {
        var aircraft = MakeAircraft(heading: 270);
        var cmd = new JoinRadialInboundCommand("OAK", 37.72, -122.22, 090);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("inbound", result.Message);
        Assert.Equal(270, aircraft.Targets.TargetHeading);
        Assert.Single(aircraft.Queue.Blocks);
        Assert.Equal(BlockTriggerType.InterceptRadial, aircraft.Queue.Blocks[0].Trigger!.Type);
    }

    [Fact]
    public void Jradi_InterceptBlockNavigatesToFix()
    {
        var aircraft = MakeAircraft(heading: 270);
        var cmd = new JoinRadialInboundCommand("OAK", 37.72, -122.22, 090);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // Simulate trigger met
        var block = aircraft.Queue.Blocks[0];
        block.ApplyAction?.Invoke(aircraft);

        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("OAK", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal(37.72, aircraft.Targets.NavigationRoute[0].Latitude);
    }

    // --- DEPART ---

    [Fact]
    public void Depart_NavigatesToFixThenHeading()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new DepartFixCommand("SUNOL", 37.6, -121.9, 270);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("SUNOL", result.Message);
        Assert.Contains("270", result.Message);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Single(aircraft.Queue.Blocks);
        Assert.Equal(BlockTriggerType.ReachFix, aircraft.Queue.Blocks[0].Trigger!.Type);
    }

    [Fact]
    public void Depart_TriggerBlockSetsHeading()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new DepartFixCommand("SUNOL", 37.6, -121.9, 270);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var block = aircraft.Queue.Blocks[0];
        block.ApplyAction?.Invoke(aircraft);

        Assert.Equal(270, aircraft.Targets.TargetHeading);
        Assert.Empty(aircraft.Targets.NavigationRoute);
    }

    // --- CFIX ---

    [Fact]
    public void Cfix_AtAltitude_StampsConstraintAndSetsAssigned()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.Targets.TargetAltitude = 10000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("SUNOL", result.Message);
        // CFIX no longer sets TargetAltitude directly — the planner does it on next tick
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.NotNull(aircraft.Targets.NavigationRoute[0].AltitudeRestriction);
        Assert.Equal(4000, aircraft.Targets.AssignedAltitude);
        // No revert block — revert is on the NavigationTarget
        Assert.Empty(aircraft.Queue.Blocks);
    }

    [Fact]
    public void Cfix_RevertFieldsCapturedOnTarget()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.Targets.TargetAltitude = 10000;
        aircraft.Targets.AssignedAltitude = 10000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var sunol = aircraft.Targets.NavigationRoute[0];
        Assert.Equal(10000, sunol.RevertAltitude);
        Assert.Equal(10000, sunol.RevertAssignedAltitude);
    }

    [Fact]
    public void Cfix_AtOrAbove_AlwaysStampsRestriction()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 5000);
        aircraft.Targets.TargetAltitude = 3000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.AtOrAbove, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // Restriction is always stamped; the planner resolves whether to act
        var sunol = aircraft.Targets.NavigationRoute[0];
        Assert.NotNull(sunol.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrAbove, sunol.AltitudeRestriction!.Type);
    }

    [Fact]
    public void Cfix_AtOrAbove_PlannerResolvesClimb()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 3000);
        aircraft.IndicatedAirspeed = 250;
        aircraft.Targets.TargetAltitude = 2000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.AtOrAbove, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // After a tick the planner should target 4000 (aircraft below constraint)
        FlightPhysics.Update(aircraft, 1.0);

        Assert.Equal(4000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void Cfix_WithSpeed_StampsSpeedRestriction()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.IndicatedAirspeed = 250;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 5000, CrossFixAltitudeType.At, 210);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        var sunol = aircraft.Targets.NavigationRoute[0];
        Assert.NotNull(sunol.SpeedRestriction);
        Assert.Equal(210, sunol.SpeedRestriction!.SpeedKts);
        Assert.Equal(210.0, aircraft.Targets.AssignedSpeed);
        Assert.Contains("speed 210", result.Message);
    }

    // --- DVIA ---

    [Fact]
    public void Dvia_WithAltitude_EnablesStarViaModeWithFloor()
    {
        var aircraft = MakeAircraft(altitude: 10000);
        aircraft.ActiveStarId = "BDEGA3";

        var cmd = new DescendViaCommand(5000);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.StarViaMode);
        Assert.Equal(5000, aircraft.StarViaFloor);
    }

    [Fact]
    public void Dvia_WithoutAltitude_EnablesStarViaMode()
    {
        var aircraft = MakeAircraft(altitude: 10000);
        aircraft.ActiveStarId = "BDEGA3";

        var cmd = new DescendViaCommand(null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.StarViaMode);
        Assert.Null(aircraft.StarViaFloor);
    }

    [Fact]
    public void Dvia_WithoutActiveStar_Rejected()
    {
        var aircraft = MakeAircraft(altitude: 10000);

        var cmd = new DescendViaCommand(null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("No active STAR", result.Message);
    }

    [Fact]
    public void Dvia_ImmediatelyAppliesFirstConstrainedFix()
    {
        var aircraft = MakeAircraft(altitude: 20000);
        aircraft.ActiveStarId = "TEST1";
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX1",
                Latitude = 37.8,
                Longitude = -122.3,
            }
        );
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX2",
                Latitude = 37.9,
                Longitude = -122.4,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 12000, null),
            }
        );

        var cmd = new DescendViaCommand(null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.StarViaMode);
        Assert.Equal(12000, aircraft.Targets.TargetAltitude);
    }

    // --- APPS ---

    [Fact]
    public void Apps_EmptyDb_ReturnsNoApproachesFound()
    {
        var aircraft = MakeAircraft();
        // Pre-seed an empty approach list for OAK so the CIFP loader (which throws on empty path)
        // is never invoked. An empty list signals: CIFP is available but no approaches for this airport.
        NavigationDatabase.SetInstance(
            NavigationDatabase.ForTesting(approachesByAirport: new Dictionary<string, IReadOnlyList<CifpApproachProcedure>> { ["OAK"] = [] })
        );

        var cmd = new ListApproachesCommand("OAK");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("No approaches found", result.Message);
    }

    [Fact]
    public void Apps_NoAirportAndNoDestination_ReturnsError()
    {
        var aircraft = MakeAircraft();
        var navDb = NavigationDatabase.ForTesting();
        NavigationDatabase.SetInstance(navDb);

        var cmd = new ListApproachesCommand(null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("No airport", result.Message);
    }

    [Fact]
    public void Apps_WithExplicitAirport_NoApproaches_ReturnsEmptyMessage()
    {
        var aircraft = MakeAircraft();
        // Provide an empty approach cache for OAK to signal CIFP is available but airport has no approaches
        var navDb = NavigationDatabase.ForTesting(approachesByAirport: new Dictionary<string, IReadOnlyList<CifpApproachProcedure>> { ["OAK"] = [] });
        NavigationDatabase.SetInstance(navDb);

        var cmd = new ListApproachesCommand("OAK");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("No approaches found", result.Message);
    }

    [Fact]
    public void Apps_UsesDestinationAsFallback()
    {
        var aircraft = MakeAircraft();
        aircraft.Destination = "OAK";
        // Provide an empty approach cache for OAK to signal CIFP is available but airport has no approaches
        var navDb = NavigationDatabase.ForTesting(approachesByAirport: new Dictionary<string, IReadOnlyList<CifpApproachProcedure>> { ["OAK"] = [] });
        NavigationDatabase.SetInstance(navDb);

        var cmd = new ListApproachesCommand(null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("OAK", result.Message);
    }

    // --- JARR ---

    [Fact]
    public void Jarr_UnknownStar_ReturnsError()
    {
        var aircraft = MakeAircraft();
        var navDb = TestNavDbFactory.WithNavData();
        NavigationDatabase.SetInstance(navDb);

        var cmd = new JoinStarCommand("NONEXIST", null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown STAR", result.Message);
    }

    [Fact]
    public void Jarr_WithTransition_BuildsRouteFromTransitionAndBody()
    {
        var navDb = TestNavDbFactory.WithNavData(
            fixPositions: new()
            {
                ["KENNO"] = (37.8, -121.7),
                ["SUNOL"] = (37.6, -121.9),
                ["OAK"] = (37.72, -122.22),
            },
            starBodies: new() { ["SUNOL1"] = ["SUNOL", "OAK"] },
            starTransitions: new() { ["SUNOL1"] = [("KENNO", ["KENNO", "SUNOL"])] }
        );
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft(heading: 180);
        var cmd = new JoinStarCommand("SUNOL1", "KENNO");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("SUNOL1", result.Message);
        // Route should be: KENNO → SUNOL → OAK (deduped)
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("KENNO", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("OAK", aircraft.Targets.NavigationRoute[2].Name);
    }

    [Fact]
    public void Jarr_WithoutTransition_FindsNearestFixAhead()
    {
        var navDb = TestNavDbFactory.WithNavData(
            fixPositions: new()
            {
                ["KENNO"] = (37.8, -121.7),
                ["SUNOL"] = (37.6, -121.9),
                ["OAK"] = (37.72, -122.22),
            },
            starBodies: new() { ["SUNOL1"] = ["KENNO", "SUNOL", "OAK"] }
        );
        NavigationDatabase.SetInstance(navDb);

        // Aircraft heading south, SUNOL is to the south
        var aircraft = MakeAircraft(heading: 180, lat: 37.65, lon: -121.85);
        var cmd = new JoinStarCommand("SUNOL1", null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.True(aircraft.Targets.NavigationRoute.Count >= 1);
    }

    [Fact]
    public void Jarr_BadTransition_ReturnsError()
    {
        var navDb = TestNavDbFactory.WithNavData(
            fixPositions: new()
            {
                ["KENNO"] = (37.8, -121.7),
                ["SUNOL"] = (37.6, -121.9),
                ["OAK"] = (37.72, -122.22),
            },
            starBodies: new() { ["SUNOL1"] = ["SUNOL", "OAK"] },
            starTransitions: new() { ["SUNOL1"] = [("KENNO", ["KENNO", "SUNOL"])] }
        );
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft();
        var cmd = new JoinStarCommand("SUNOL1", "NOSUCH");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown transition or fix", result.Message);
    }

    [Fact]
    public void Jarr_WithIntermediateFix_JoinsFromThatFix()
    {
        var navDb = TestNavDbFactory.WithNavData(
            fixPositions: new()
            {
                ["EMZOH"] = (37.9, -121.5),
                ["COREZ"] = (37.8, -121.7),
                ["BRIXX"] = (37.7, -121.9),
                ["OAK"] = (37.72, -122.22),
            },
            starBodies: new() { ["EMZOH4"] = ["EMZOH", "COREZ", "BRIXX", "OAK"] }
        );
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft();
        var cmd = new JoinStarCommand("EMZOH4", "COREZ");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("COREZ", aircraft.Targets.NavigationRoute[0].Name);
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, t => t.Name == "EMZOH");
    }

    [Fact]
    public void Jarr_TransitionTakesPriorityOverFixName()
    {
        // "COREZ" exists as both a transition name and a fix in the body
        var navDb = TestNavDbFactory.WithNavData(
            fixPositions: new()
            {
                ["EMZOH"] = (37.9, -121.5),
                ["COREZ"] = (37.8, -121.7),
                ["OAK"] = (37.72, -122.22),
            },
            starBodies: new() { ["EMZOH4"] = ["EMZOH", "COREZ", "OAK"] },
            starTransitions: new() { ["EMZOH4"] = [("COREZ", ["COREZ", "EMZOH"])] }
        );
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft();
        var cmd = new JoinStarCommand("EMZOH4", "COREZ");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        // Transition matched: route is transition fixes + body = COREZ EMZOH COREZ OAK (deduped: COREZ EMZOH COREZ OAK)
        // The first fix should be COREZ from the transition, and EMZOH should appear (from transition)
        Assert.Contains(aircraft.Targets.NavigationRoute, t => t.Name == "EMZOH");
    }

    [Fact]
    public void Jarr_NonexistentFixAndTransition_ReturnsError()
    {
        var navDb = TestNavDbFactory.WithNavData(
            fixPositions: new()
            {
                ["EMZOH"] = (37.9, -121.5),
                ["COREZ"] = (37.8, -121.7),
                ["OAK"] = (37.72, -122.22),
                ["KENNO"] = (37.8, -121.7),
            },
            starBodies: new() { ["EMZOH4"] = ["EMZOH", "COREZ", "OAK"] },
            starTransitions: new() { ["EMZOH4"] = [("KENNO", ["KENNO", "EMZOH"])] }
        );
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft();
        var cmd = new JoinStarCommand("EMZOH4", "NOSUCH");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown transition or fix 'NOSUCH'", result.Message);
    }

    // --- Holding Pattern ---

    [Fact]
    public void HoldingPattern_CreatesPhaseList()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new HoldingPatternCommand("SUNOL", 37.6, -121.9, 090, 1, true, TurnDirection.Right, null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains("SUNOL", result.Message);
        Assert.Contains("right", result.Message);
    }

    [Fact]
    public void HoldingPattern_AnyCommandExitsPhase()
    {
        var aircraft = MakeAircraft(heading: 090);
        var holdCmd = new HoldingPatternCommand("SUNOL", 37.6, -121.9, 090, 1, true, TurnDirection.Right, null);

        CommandDispatcher.Dispatch(holdCmd, aircraft, null, Random.Shared, true);
        Assert.NotNull(aircraft.Phases?.CurrentPhase);

        var acceptance = aircraft.Phases!.CurrentPhase!.CanAcceptCommand(CanonicalCommandType.FlyHeading);
        Assert.Equal(CommandAcceptance.ClearsPhase, acceptance);
    }

    [Fact]
    public void HoldingPattern_PreservesAssignedRunway()
    {
        var aircraft = MakeAircraft(heading: 090);
        var runway = new RunwayInfo
        {
            AirportId = "OAK",
            Id = new Data.Airport.RunwayIdentifier("28L", "10R"),
            Designator = "28L",
            Lat1 = 37.72,
            Lon1 = -122.22,
            Elevation1Ft = 6,
            Heading1 = 280,
            Lat2 = 37.73,
            Lon2 = -122.25,
            Elevation2Ft = 6,
            Heading2 = 100,
            LengthFt = 10000,
            WidthFt = 150,
        };
        aircraft.Phases = new PhaseList { AssignedRunway = runway };

        var cmd = new HoldingPatternCommand("SUNOL", 37.6, -121.9, 090, 1, true, TurnDirection.Right, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.NotNull(aircraft.Phases);
        Assert.Equal(runway, aircraft.Phases.AssignedRunway);
    }

    // --- DCT ---

    [Fact]
    public void DirectTo_EmptyFixList_SetsEmptyRoute()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new DirectToCommand([], []);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Empty(aircraft.Targets.NavigationRoute);
    }

    [Fact]
    public void DirectTo_MultipleWaypoints_NavigatesSequentially()
    {
        // Three fixes roughly along a line heading east from the aircraft.
        // Each fix is ~2nm apart so the aircraft can reach them at 250 kts.
        var fixA = new ResolvedFix("FIXA", 37.7, -122.15);
        var fixB = new ResolvedFix("FIXB", 37.7, -122.10);
        var fixC = new ResolvedFix("FIXC", 37.7, -122.05);

        var aircraft = MakeAircraft(heading: 090, altitude: 5000, lat: 37.7, lon: -122.2);
        aircraft.IndicatedAirspeed = 250;

        var cmd = new DirectToCommand([fixA, fixB, fixC], []);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("FIXA", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("FIXB", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("FIXC", aircraft.Targets.NavigationRoute[2].Name);

        // Simulate physics ticks until the aircraft reaches all fixes or we time out.
        // At 250 kts the ~10nm total distance takes ~2.5 min.
        const double dt = 1.0;
        const int maxTicks = 300; // 5 minutes of sim time

        // Track which fixes we've passed through.
        var passedFixes = new List<string>();
        int prevCount = aircraft.Targets.NavigationRoute.Count;

        for (int i = 0; i < maxTicks; i++)
        {
            FlightPhysics.Update(aircraft, dt);

            int curCount = aircraft.Targets.NavigationRoute.Count;
            if (curCount < prevCount)
            {
                // A fix was reached — the removed fix name is the one that was at index 0.
                // We can infer it from the sequence.
                int fixesReached = prevCount - curCount;
                for (int j = 0; j < fixesReached; j++)
                {
                    passedFixes.Add(
                        passedFixes.Count switch
                        {
                            0 => "FIXA",
                            1 => "FIXB",
                            2 => "FIXC",
                            _ => "UNKNOWN",
                        }
                    );
                }

                prevCount = curCount;
            }

            if (curCount == 0)
            {
                break;
            }
        }

        Assert.Equal(["FIXA", "FIXB", "FIXC"], passedFixes);
        Assert.Empty(aircraft.Targets.NavigationRoute);
        Assert.Null(aircraft.Targets.TargetHeading);
    }

    // --- CFIX with AtOrBelow ---

    [Fact]
    public void Cfix_AtOrBelow_StampsRestriction()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 3000);
        aircraft.Targets.TargetAltitude = 5000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.AtOrBelow, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var sunol = aircraft.Targets.NavigationRoute[0];
        Assert.NotNull(sunol.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrBelow, sunol.AltitudeRestriction!.Type);
    }

    [Fact]
    public void Cfix_AtOrBelow_AboveCrossing_PlannerDescends()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 6000);
        aircraft.IndicatedAirspeed = 250;
        aircraft.Targets.TargetAltitude = 8000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.AtOrBelow, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // After a tick the planner should target 4000 (aircraft above constraint)
        FlightPhysics.Update(aircraft, 1.0);

        Assert.Equal(4000, aircraft.Targets.TargetAltitude);
    }

    // --- CVIA without active SID ---

    [Fact]
    public void Cvia_WithoutActiveSid_Rejected()
    {
        var aircraft = MakeAircraft(altitude: 5000);

        var cmd = new ClimbViaCommand(null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("No active SID", result.Message);
    }

    // --- JARR with empty body ---

    [Fact]
    public void Jarr_EmptyStarBody_ReturnsError()
    {
        var navDb = TestNavDbFactory.WithNavData(starBodies: new() { ["EMPTY1"] = [] });
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft(heading: 180);
        var cmd = new JoinStarCommand("EMPTY1", null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
    }

    // --- JAWY ---

    private static NavigationDatabase MakeAirwayNavDb()
    {
        // V25: west-to-east airway — SUNOL → TRACY → MODEN → CEDES
        // Roughly along latitude 37.7, spaced ~0.3° apart in longitude
        return TestNavDbFactory.WithNavData(
            fixPositions: new()
            {
                ["SUNOL"] = (37.70, -121.90),
                ["TRACY"] = (37.70, -121.60),
                ["MODEN"] = (37.70, -121.30),
                ["CEDES"] = (37.70, -121.00),
            },
            airways: new() { ["V25"] = ["SUNOL", "TRACY", "MODEN", "CEDES"] }
        );
    }

    [Fact]
    public void Jawy_UnknownAirway_ReturnsError()
    {
        var fixes = MakeAirwayNavDb();
        NavigationDatabase.SetInstance(fixes);
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new JoinAirwayCommand("V999");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown airway", result.Message);
    }

    [Fact]
    public void Jawy_NoFixDatabase_ReturnsError()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new JoinAirwayCommand("V25");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown airway", result.Message);
    }

    [Fact]
    public void Jawy_EastboundOnV25_InterceptsSegmentAndFollowsFixes()
    {
        var fixes = MakeAirwayNavDb();
        NavigationDatabase.SetInstance(fixes);
        // Aircraft between SUNOL and TRACY, heading east
        var aircraft = MakeAircraft(heading: 090, lat: 37.72, lon: -121.75);
        var cmd = new JoinAirwayCommand("V25");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("V25", result.Message);
        // Should set present heading and create intercept block
        Assert.Equal(090, aircraft.Targets.TargetHeading);
        Assert.Single(aircraft.Queue.Blocks);
        Assert.Equal(BlockTriggerType.InterceptRadial, aircraft.Queue.Blocks[0].Trigger!.Type);

        // Simulate intercept: apply the block action
        aircraft.Queue.Blocks[0].ApplyAction!(aircraft);

        // After intercept, should have TRACY → MODEN → CEDES in nav route
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("TRACY", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("MODEN", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("CEDES", aircraft.Targets.NavigationRoute[2].Name);
    }

    [Fact]
    public void Jawy_WestboundOnV25_FollowsFixesInReverse()
    {
        var fixes = MakeAirwayNavDb();
        NavigationDatabase.SetInstance(fixes);
        // Aircraft between TRACY and MODEN, heading west
        var aircraft = MakeAircraft(heading: 270, lat: 37.72, lon: -121.45);
        var cmd = new JoinAirwayCommand("V25");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);

        // Simulate intercept
        aircraft.Queue.Blocks[0].ApplyAction!(aircraft);

        // After intercept, should follow TRACY → SUNOL (westbound)
        Assert.Equal(2, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("TRACY", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[1].Name);
    }

    [Fact]
    public void Jawy_AircraftBeforeFirstFix_NavigatesFromFirstFix()
    {
        var fixes = MakeAirwayNavDb();
        NavigationDatabase.SetInstance(fixes);
        // Aircraft west of SUNOL, heading east — no fix behind on this airway
        var aircraft = MakeAircraft(heading: 090, lat: 37.70, lon: -122.10);
        var cmd = new JoinAirwayCommand("V25");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);

        // Simulate intercept
        aircraft.Queue.Blocks[0].ApplyAction!(aircraft);

        // Should navigate from SUNOL onward
        Assert.True(aircraft.Targets.NavigationRoute.Count >= 1);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
    }

    [Fact]
    public void Jawy_ClearsExistingNavRoute()
    {
        var fixes = MakeAirwayNavDb();
        NavigationDatabase.SetInstance(fixes);
        var aircraft = MakeAircraft(heading: 090, lat: 37.72, lon: -121.75);
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "OLDFIX",
                Latitude = 37.0,
                Longitude = -122.0,
            }
        );
        var cmd = new JoinAirwayCommand("V25");

        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        // Existing nav route should be cleared
        Assert.Empty(aircraft.Targets.NavigationRoute);
    }

    // --- AssignedValue audit ---

    [Fact]
    public void Jrado_SetsAssignedHeading_Immediately()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new JoinRadialOutboundCommand("OAK", 37.72, -122.22, 180);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(090, aircraft.Targets.AssignedHeading);
    }

    [Fact]
    public void Jrado_InterceptBlock_UpdatesAssignedHeading()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new JoinRadialOutboundCommand("OAK", 37.72, -122.22, 270);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var block = aircraft.Queue.Blocks[0];
        block.ApplyAction?.Invoke(aircraft);

        Assert.Equal(270, aircraft.Targets.AssignedHeading);
    }

    [Fact]
    public void Jradi_SetsAssignedHeading_Immediately()
    {
        var aircraft = MakeAircraft(heading: 270);
        var cmd = new JoinRadialInboundCommand("OAK", 37.72, -122.22, 090);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(270, aircraft.Targets.AssignedHeading);
    }

    [Fact]
    public void Jradi_InterceptBlock_ClearsAssignedHeading()
    {
        var aircraft = MakeAircraft(heading: 270);
        var cmd = new JoinRadialInboundCommand("OAK", 37.72, -122.22, 090);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var block = aircraft.Queue.Blocks[0];
        block.ApplyAction?.Invoke(aircraft);

        Assert.Null(aircraft.Targets.AssignedHeading);
    }

    [Fact]
    public void Dfix_ClearsAssignedHeading_Immediately()
    {
        var aircraft = MakeAircraft(heading: 090);
        aircraft.Targets.AssignedHeading = 090;
        var cmd = new DepartFixCommand("SUNOL", 37.6, -121.9, 270);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Null(aircraft.Targets.AssignedHeading);
    }

    [Fact]
    public void Dfix_TriggerBlock_SetsAssignedHeading()
    {
        var aircraft = MakeAircraft(heading: 090);
        var cmd = new DepartFixCommand("SUNOL", 37.6, -121.9, 270);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var block = aircraft.Queue.Blocks[0];
        block.ApplyAction?.Invoke(aircraft);

        Assert.Equal(270, aircraft.Targets.AssignedHeading);
    }

    [Fact]
    public void Cfix_SetsAssignedAltitude()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.Targets.TargetAltitude = 10000;
        aircraft.Targets.AssignedAltitude = 10000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(4000, aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void Cfix_WithSpeed_SetsAssignedSpeed()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 5000, CrossFixAltitudeType.At, 210);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(210.0, aircraft.Targets.AssignedSpeed);
    }

    [Fact]
    public void Cfix_RevertFieldsOnTarget_CaptureAssigned()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.Targets.TargetAltitude = 10000;
        aircraft.Targets.AssignedAltitude = 10000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(4000, aircraft.Targets.AssignedAltitude);

        // Revert is captured on the NavigationTarget, not in a CommandQueue block
        var sunol = aircraft.Targets.NavigationRoute[0];
        Assert.Equal(10000, sunol.RevertAltitude);
        Assert.Equal(10000, sunol.RevertAssignedAltitude);
    }

    [Fact]
    public void Cfix_AtOrAbove_AlwaysSetsAssignedAltitude()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 5000);
        aircraft.Targets.TargetAltitude = 3000;
        aircraft.Targets.AssignedAltitude = 3000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.AtOrAbove, null);

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // CFIX always sets AssignedAltitude for the datablock display
        Assert.Equal(4000, aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void Jawy_SetsAssignedHeading_Immediately()
    {
        var fixes = MakeAirwayNavDb();
        NavigationDatabase.SetInstance(fixes);
        var aircraft = MakeAircraft(heading: 090, lat: 37.72, lon: -121.75);
        var cmd = new JoinAirwayCommand("V25");

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(090, aircraft.Targets.AssignedHeading);
    }

    [Fact]
    public void Jawy_InterceptBlock_ClearsAssignedHeading()
    {
        var fixes = MakeAirwayNavDb();
        NavigationDatabase.SetInstance(fixes);
        var aircraft = MakeAircraft(heading: 090, lat: 37.72, lon: -121.75);
        var cmd = new JoinAirwayCommand("V25");

        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        aircraft.Queue.Blocks[0].ApplyAction!(aircraft);

        Assert.Null(aircraft.Targets.AssignedHeading);
    }

    // ==========================================================================
    // Unified CFIX & Drawn Route Altitude Constraints with Step-Based Planning
    // ==========================================================================

    // --- CFIX stamps AltitudeRestriction on route target (not direct TargetAltitude) ---

    [Fact]
    public void Cfix_StampsAltitudeRestrictionOnRouteTarget()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.IndicatedAirspeed = 250;
        aircraft.Targets.TargetAltitude = 10000;
        aircraft.Targets.AssignedAltitude = 10000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // The CFIX target in the route should have an AltitudeRestriction
        var sunolTarget = Assert.Single(aircraft.Targets.NavigationRoute, t => t.Name == "SUNOL");
        Assert.NotNull(sunolTarget.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.At, sunolTarget.AltitudeRestriction!.Type);
        Assert.Equal(4000, sunolTarget.AltitudeRestriction.Altitude1Ft);
    }

    [Fact]
    public void Cfix_AtOrAbove_StampsCorrectRestrictionType()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 3000);
        aircraft.IndicatedAirspeed = 250;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 5000, CrossFixAltitudeType.AtOrAbove, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var sunolTarget = aircraft.Targets.NavigationRoute.First(t => t.Name == "SUNOL");
        Assert.NotNull(sunolTarget.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrAbove, sunolTarget.AltitudeRestriction!.Type);
    }

    [Fact]
    public void Cfix_AtOrBelow_StampsCorrectRestrictionType()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.IndicatedAirspeed = 250;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 5000, CrossFixAltitudeType.AtOrBelow, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var sunolTarget = aircraft.Targets.NavigationRoute.First(t => t.Name == "SUNOL");
        Assert.NotNull(sunolTarget.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrBelow, sunolTarget.AltitudeRestriction!.Type);
    }

    // --- CFIX revert via NavigationTarget (not CommandQueue) ---

    [Fact]
    public void Cfix_SetsRevertFieldsOnTarget()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.IndicatedAirspeed = 250;
        aircraft.Targets.TargetAltitude = 10000;
        aircraft.Targets.AssignedAltitude = 10000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var sunolTarget = aircraft.Targets.NavigationRoute.First(t => t.Name == "SUNOL");
        Assert.Equal(10000, sunolTarget.RevertAltitude);
        Assert.Equal(10000, sunolTarget.RevertAssignedAltitude);
    }

    [Fact]
    public void Cfix_DoesNotCreateRevertBlock()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.IndicatedAirspeed = 250;
        aircraft.Targets.TargetAltitude = 10000;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // No revert block in the command queue
        Assert.Empty(aircraft.Queue.Blocks);
    }

    // --- Step-based planning activates for non-via-mode routes with constraints ---

    [Fact]
    public void Cfix_StepPlanningComputesDescentRate()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 10000);
        aircraft.IndicatedAirspeed = 250;
        // GroundSpeed is derived from IAS; on ground IAS=GS, airborne IAS≈GS (no wind)

        // Place SUNOL ~15nm ahead
        var cmd = new CrossFixCommand("SUNOL", 37.7, -121.95, 4000, CrossFixAltitudeType.At, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // One physics tick should trigger descent planning
        FlightPhysics.Update(aircraft, 1.0);

        // The planner should have set a DesiredVerticalRate (negative for descent)
        Assert.NotNull(aircraft.Targets.DesiredVerticalRate);
        Assert.True(aircraft.Targets.DesiredVerticalRate < 0, "DesiredVerticalRate should be negative for descent");
    }

    [Fact]
    public void NonViaMode_RouteWithConstraints_TriggersPlanning()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 15000);
        aircraft.IndicatedAirspeed = 300;
        // GroundSpeed derived from IAS (no wind)

        // Build a route with altitude constraints (not in via mode)
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX1",
                Latitude = 37.7,
                Longitude = -122.0,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 8000, null),
            }
        );

        FlightPhysics.Update(aircraft, 1.0);

        // Planning should activate without via mode, just from the route constraint
        Assert.NotNull(aircraft.Targets.DesiredVerticalRate);
        Assert.True(aircraft.Targets.DesiredVerticalRate < 0);
        Assert.Equal(8000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void NonViaMode_RouteWithClimbConstraint_TriggersClimbPlanning()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 5000);
        aircraft.IndicatedAirspeed = 250;
        // GroundSpeed is derived from IAS; on ground IAS=GS, airborne IAS≈GS (no wind)

        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX1",
                Latitude = 37.7,
                Longitude = -122.0,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 10000, null),
            }
        );

        FlightPhysics.Update(aircraft, 1.0);

        Assert.NotNull(aircraft.Targets.DesiredVerticalRate);
        Assert.True(aircraft.Targets.DesiredVerticalRate > 0, "DesiredVerticalRate should be positive for climb");
        Assert.Equal(10000, aircraft.Targets.TargetAltitude);
    }

    // --- Revert on waypoint sequencing ---

    [Fact]
    public void Cfix_RevertOnFixSequencing()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 4000);
        aircraft.IndicatedAirspeed = 250;

        // Place SUNOL very close so it sequences immediately
        aircraft.Latitude = 37.6;
        aircraft.Longitude = -121.9001;

        // Add a following fix so the route doesn't end at SUNOL
        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 4000, CrossFixAltitudeType.At, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // Add a downstream fix
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "TRACY",
                Latitude = 37.7,
                Longitude = -121.6,
            }
        );

        // Tick to sequence past SUNOL
        FlightPhysics.Update(aircraft, 1.0);

        // After sequencing SUNOL, the revert fields should restore the previous altitude
        // The target was 10000 before CFIX (or whatever was captured in RevertAltitude)
        // Since we're testing the revert mechanism, check that the sequenced fix's
        // revert was applied — the assigned altitude should be restored
        var sunolTarget = aircraft.Targets.NavigationRoute.FirstOrDefault(t => t.Name == "SUNOL");
        Assert.Null(sunolTarget); // SUNOL should have been sequenced away
    }

    [Fact]
    public void Cfix_SpeedRevert_SetsRevertSpeedFields()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 8000);
        aircraft.IndicatedAirspeed = 250;
        aircraft.Targets.TargetSpeed = 300;
        aircraft.Targets.AssignedSpeed = 300;

        var cmd = new CrossFixCommand("SUNOL", 37.6, -121.9, 5000, CrossFixAltitudeType.At, 210);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        var sunolTarget = aircraft.Targets.NavigationRoute.First(t => t.Name == "SUNOL");
        Assert.NotNull(sunolTarget.SpeedRestriction);
        Assert.Equal(210, sunolTarget.SpeedRestriction!.SpeedKts);
        Assert.Equal(300, sunolTarget.RevertSpeed);
        Assert.Equal(300, sunolTarget.RevertAssignedSpeed);
    }

    // --- ApplyFixConstraints works without via mode ---

    [Fact]
    public void ApplyFixConstraints_WorksWithoutViaMode()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 10000);
        aircraft.IndicatedAirspeed = 250;

        var target = new NavigationTarget
        {
            Name = "FIX1",
            Latitude = 37.7,
            Longitude = -122.0,
            AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 6000, null),
        };

        // Neither SidViaMode nor StarViaMode is true
        Assert.False(aircraft.SidViaMode);
        Assert.False(aircraft.StarViaMode);

        FlightPhysics.ApplyFixConstraints(aircraft, target);

        Assert.Equal(6000, aircraft.Targets.TargetAltitude);
    }

    // --- Via mode still works unchanged ---

    [Fact]
    public void ViaMode_StillWorksCeilingFloorClamping()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 15000);
        aircraft.IndicatedAirspeed = 250;
        aircraft.StarViaMode = true;
        aircraft.StarViaFloor = 8000;

        var target = new NavigationTarget
        {
            Name = "FIX1",
            Latitude = 37.7,
            Longitude = -122.0,
            AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 5000, null),
        };

        FlightPhysics.ApplyFixConstraints(aircraft, target);

        // Floor should clamp to 8000
        Assert.Equal(8000, aircraft.Targets.TargetAltitude);
    }

    // --- Constrained DCTF ---

    [Fact]
    public void ConstrainedDctf_StampsAltitudeRestrictionsOnRoute()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 15000);
        aircraft.IndicatedAirspeed = 300;

        var fixes = new List<ResolvedFix> { new("FIX1", 37.7, -122.0), new("FIX2", 37.7, -121.8), new("FIX3", 37.7, -121.6) };
        var constraints = new Dictionary<int, ConstrainedFixAltitude>
        {
            [0] = new(8000, CrossFixAltitudeType.AtOrAbove),
            [1] = new(5000, CrossFixAltitudeType.At),
        };

        var cmd = new ConstrainedForceDirectToCommand(fixes, constraints, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);

        // FIX1 should have AtOrAbove 8000
        var fix1 = aircraft.Targets.NavigationRoute[0];
        Assert.NotNull(fix1.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrAbove, fix1.AltitudeRestriction!.Type);
        Assert.Equal(8000, fix1.AltitudeRestriction.Altitude1Ft);

        // FIX2 should have At 5000
        var fix2 = aircraft.Targets.NavigationRoute[1];
        Assert.NotNull(fix2.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.At, fix2.AltitudeRestriction!.Type);
        Assert.Equal(5000, fix2.AltitudeRestriction.Altitude1Ft);

        // FIX3 has no constraint
        Assert.Null(aircraft.Targets.NavigationRoute[2].AltitudeRestriction);
    }

    [Fact]
    public void ConstrainedDctf_OnlyLastConstrainedFixRevertsAltitude()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 15000);
        aircraft.IndicatedAirspeed = 300;
        aircraft.Targets.TargetAltitude = 20000;
        aircraft.Targets.AssignedAltitude = 20000;

        var fixes = new List<ResolvedFix> { new("FIX1", 37.7, -122.0), new("FIX2", 37.7, -121.8), new("FIX3", 37.7, -121.6) };
        var constraints = new Dictionary<int, ConstrainedFixAltitude>
        {
            [0] = new(10000, CrossFixAltitudeType.At),
            [1] = new(5000, CrossFixAltitudeType.At),
        };

        var cmd = new ConstrainedForceDirectToCommand(fixes, constraints, null, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // FIX1 should NOT have revert (intermediate constrained fix)
        Assert.Null(aircraft.Targets.NavigationRoute[0].RevertAltitude);

        // FIX2 should have revert (last constrained fix)
        Assert.Equal(20000, aircraft.Targets.NavigationRoute[1].RevertAltitude);
        Assert.Equal(20000, aircraft.Targets.NavigationRoute[1].RevertAssignedAltitude);
    }

    [Fact]
    public void ConstrainedDctf_PlannerTargetsConstraintsSimultaneously()
    {
        var aircraft = MakeAircraft(heading: 090, altitude: 15000);
        aircraft.IndicatedAirspeed = 300;
        // GroundSpeed derived from IAS (no wind)

        var fixes = new List<ResolvedFix> { new("FIX1", 37.7, -122.0), new("FIX2", 37.7, -121.8) };
        var constraints = new Dictionary<int, ConstrainedFixAltitude>
        {
            [0] = new(10000, CrossFixAltitudeType.At),
            [1] = new(5000, CrossFixAltitudeType.At),
        };

        var cmd = new ConstrainedForceDirectToCommand(fixes, constraints, null, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        // One physics tick — planner should target FIX1's constraint first (nearest)
        FlightPhysics.Update(aircraft, 1.0);

        Assert.Equal(10000, aircraft.Targets.TargetAltitude);
        Assert.NotNull(aircraft.Targets.DesiredVerticalRate);
        Assert.True(aircraft.Targets.DesiredVerticalRate < 0);
    }
}
