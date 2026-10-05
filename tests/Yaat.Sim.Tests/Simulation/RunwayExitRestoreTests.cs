using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A snapshot taken while an aircraft is following its runway-exit path must restore into a phase that keeps
/// following it.
///
/// <c>ToSnapshot</c> persists the state, the waypoint node ids and the navigator, but the exit <em>route</em> is
/// rebuilt from the live ground layout and is not serialized. <c>TickFollowingExitPath</c> reads a null route as
/// "exit complete", so a restored phase ended immediately — bypassing <c>CompleteExit</c>, which is what inserts
/// <c>HoldingAfterExitPhase</c>, clears <c>IsExpeditingExit</c>, and marks the hold-short node occupied so another
/// arrival cannot plan the same exit.
/// </summary>
public sealed class RunwayExitRestoreTests
{
    private readonly ITestOutputHelper _output;

    public RunwayExitRestoreTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Picks a real hold-short node on <paramref name="runwayId"/> plus a neighbour joined by a named taxiway edge.
    /// Derived from the layout at runtime rather than hardcoded — fillet node ids are geometry-coupled and shift
    /// whenever the fixture is regenerated.
    /// </summary>
    private static (GroundNode Branch, GroundNode HoldShort, string Taxiway)? FindExitPair(AirportGroundLayout layout, string runwayId)
    {
        foreach (GroundNode holdShort in layout.GetRunwayHoldShortNodes(runwayId))
        {
            foreach (IGroundEdge edge in holdShort.Edges)
            {
                if (string.IsNullOrEmpty(edge.TaxiwayName))
                {
                    continue;
                }

                foreach (GroundNode node in edge.Nodes)
                {
                    if (node.Id != holdShort.Id)
                    {
                        return (node, holdShort, edge.TaxiwayName);
                    }
                }
            }
        }

        return null;
    }

    [Fact]
    public void RestoredMidExit_ContinuesFollowingTheExitPath_InsteadOfReportingComplete()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            ExitWaypointNodeIds = [branch.Id, holdShort.Id],
        };

        var phase = RunwayExitPhase.FromSnapshot(dto, layout);

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = branch.Position,
            TrueHeading = new TrueHeading(281.0),
            Altitude = 9.0,
            IndicatedAirspeed = 25.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            FieldElevation = 9.0,
            Logger = NullLogger.Instance,
        };

        bool completed = phase.OnTick(ctx);

        Assert.False(completed, "a phase restored mid-exit reported the exit complete on its first tick, skipping CompleteExit's cleanup");
    }

    /// <summary>
    /// A restored mid-exit phase whose exit route cannot be rebuilt (its stored path has two nodes with no edge between
    /// them) falls back to rolling on the centreline, and drops the navigator it was restored with: the snapshot taken after
    /// its first tick is out of the exit-path state and no longer carries that navigator. The stale navigator is marked by a
    /// brake rate no live exit sets (<see cref="StaleNavigatorDecelRateKts"/>), which a navigator kept from it would carry.
    /// </summary>
    [Fact]
    public void FailedRebuild_FallsBackToTheCenterline_AndDropsTheRestoredNavigator()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;
        GroundNode unconnected = layout
            .Nodes.Values.Where(node => (node.Id != branch.Id) && branch.Edges.All(edge => edge.OtherNode(branch).Id != node.Id))
            .OrderBy(node => node.Id)
            .First();

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            ExitWaypointNodeIds = [branch.Id, unconnected.Id],
            Navigator = new GroundNavigatorDto { TargetNodeId = holdShort.Id, DecelRateKts = StaleNavigatorDecelRateKts },
        };

        var phase = RunwayExitPhase.FromSnapshot(dto, layout);
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = branch.Position,
            TrueHeading = new TrueHeading(281.0),
            Altitude = 9.0,
            IndicatedAirspeed = 25.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            FieldElevation = 9.0,
            Logger = NullLogger.Instance,
        };

        phase.OnTick(ctx);

        RunwayExitPhaseDto after = Assert.IsType<RunwayExitPhaseDto>(phase.ToSnapshot());
        Assert.NotEqual((int)RunwayExitPhase.ExitState.FollowingExitPath, after.ExitStateValue);
        Assert.NotEqual(StaleNavigatorDecelRateKts, after.Navigator?.DecelRateKts);
    }

    private const double StaleNavigatorDecelRateKts = 0.123;

    /// <summary>Wires SimLog for this test and returns the tap capturing the Warning+ entries it emits.</summary>
    private CapturingSimLogProvider CaptureLogs()
    {
        var tap = new CapturingSimLogProvider(LogLevel.Warning, capacity: 200);
        SimLogBuilder.CreateForTest(_output).EnableCategory("RunwayExitPhase", LogLevel.Warning).CaptureInto(tap).InitializeSimLog();
        return tap;
    }

    /// <summary>Asserts a <c>RunwayExitPhase</c> warning states the restored path was rejected for <paramref name="reasonFragment"/>.</summary>
    private static void AssertRestoredPathRejected(IReadOnlyList<CapturedLogRecord> logs, string reasonFragment)
    {
        Assert.Contains(
            logs,
            r => (r.Category == "RunwayExitPhase") && r.Message.Contains($"restored exit path rejected — {reasonFragment}", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A restored exit path that names a node the current layout no longer has must not be followed with that node
    /// silently dropped. Node ids shift when the fixture is regenerated, so a stale snapshot can name a node that has
    /// vanished; the two nodes that flanked it may still be joined by an edge, leaving a truncated path the rebuild
    /// would accept. The whole chain is rejected and the phase takes the same centreline fallback the unbuildable
    /// path takes.
    /// </summary>
    [Fact]
    public void RestoredExitPath_WithAMissingNode_FallsBackToTheCenterline()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;
        int missingId = layout.Nodes.Keys.Max() + 1000;
        using CapturingSimLogProvider tap = CaptureLogs();

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            // The stored middle node is gone, but branch → hold-short is still a real edge: without the chain check
            // the restored path silently becomes [branch, hold-short] and the exit is followed anyway.
            ExitWaypointNodeIds = [branch.Id, missingId, holdShort.Id],
            Navigator = new GroundNavigatorDto { TargetNodeId = holdShort.Id, DecelRateKts = StaleNavigatorDecelRateKts },
        };

        var phase = RunwayExitPhase.FromSnapshot(dto, layout);
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = branch.Position,
            TrueHeading = new TrueHeading(281.0),
            Altitude = 9.0,
            IndicatedAirspeed = 25.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            FieldElevation = 9.0,
            Logger = NullLogger.Instance,
        };

        phase.OnTick(ctx);

        AssertRestoredPathRejected(tap.Drain(), $"node {missingId} is not on the current layout");

        RunwayExitPhaseDto after = Assert.IsType<RunwayExitPhaseDto>(phase.ToSnapshot());
        Assert.NotEqual((int)RunwayExitPhase.ExitState.FollowingExitPath, after.ExitStateValue);
        Assert.NotEqual(StaleNavigatorDecelRateKts, after.Navigator?.DecelRateKts);
    }

    /// <summary>
    /// A restored exit path whose ids all still exist but whose consecutive nodes are no longer joined by an edge —
    /// the layout was regenerated and the ids moved — has to be rejected the same way. Following it would hand the
    /// navigator a chain of nodes with no paved link between them.
    /// </summary>
    [Fact]
    public void RestoredExitPath_WhoseNodesNoLongerConnect_FallsBackToTheCenterline()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;
        GroundNode unconnected = layout
            .Nodes.Values.Where(node => (node.Id != branch.Id) && branch.Edges.All(edge => edge.OtherNode(branch).Id != node.Id))
            .OrderBy(node => node.Id)
            .First();
        using CapturingSimLogProvider tap = CaptureLogs();

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            ExitWaypointNodeIds = [branch.Id, unconnected.Id, holdShort.Id],
            Navigator = new GroundNavigatorDto { TargetNodeId = holdShort.Id, DecelRateKts = StaleNavigatorDecelRateKts },
        };

        var phase = RunwayExitPhase.FromSnapshot(dto, layout);
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = branch.Position,
            TrueHeading = new TrueHeading(281.0),
            Altitude = 9.0,
            IndicatedAirspeed = 25.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            FieldElevation = 9.0,
            Logger = NullLogger.Instance,
        };

        phase.OnTick(ctx);

        AssertRestoredPathRejected(tap.Drain(), $"nodes {branch.Id} and {unconnected.Id} are not joined by an edge");

        RunwayExitPhaseDto after = Assert.IsType<RunwayExitPhaseDto>(phase.ToSnapshot());
        Assert.NotEqual((int)RunwayExitPhase.ExitState.FollowingExitPath, after.ExitStateValue);
        Assert.NotEqual(StaleNavigatorDecelRateKts, after.Navigator?.DecelRateKts);
    }

    /// <summary>
    /// A snapshot restored in <c>RollingOnCenterline</c> carries its hold-short from <c>ExitNodeId</c> and never enters
    /// the <c>FollowingExitPath</c> rebuild, so a rejected stored path there would otherwise be silent. The warning
    /// still fires on the first tick, and the phase drops the unusable hold-short and falls back to the centerline.
    /// </summary>
    [Fact]
    public void RestoredRollingOnCenterline_WithARejectedExitPath_StillWarns()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;
        int missingId = layout.Nodes.Keys.Max() + 1000;
        using CapturingSimLogProvider tap = CaptureLogs();

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = false,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.RollingOnCenterline,
            ExitWaypointNodeIds = [branch.Id, missingId, holdShort.Id],
        };

        var phase = RunwayExitPhase.FromSnapshot(dto, layout);
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = branch.Position,
            TrueHeading = new TrueHeading(281.0),
            Altitude = 9.0,
            IndicatedAirspeed = 25.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            FieldElevation = 9.0,
            Logger = NullLogger.Instance,
        };

        phase.OnTick(ctx);

        AssertRestoredPathRejected(tap.Drain(), $"node {missingId} is not on the current layout");

        RunwayExitPhaseDto after = Assert.IsType<RunwayExitPhaseDto>(phase.ToSnapshot());
        Assert.Equal((int)RunwayExitPhase.ExitState.RollingOnCenterline, after.ExitStateValue);
    }

    /// <summary>
    /// The restore path rebuilds the exit route from segment 0, so the navigator's own segment index says
    /// nothing about whether the aircraft was already turning. <c>TurnStarted</c> has to round-trip, or an
    /// aircraft restored mid-turn would reopen the window for a late exit change it can no longer honor.
    /// </summary>
    [Fact]
    public void TurnStarted_SurvivesASnapshotRoundTrip()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = 281.0,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            TurnStarted = true,
            ExitWaypointNodeIds = [branch.Id, holdShort.Id],
        };

        var restored = RunwayExitPhase.FromSnapshot(dto, layout);
        Assert.True(restored.TurnStarted);

        RunwayExitPhaseDto round = Assert.IsType<RunwayExitPhaseDto>(restored.ToSnapshot());
        Assert.True(round.TurnStarted);
    }

    /// <summary>
    /// A snapshot can land on the tick before <c>GroundNavigator</c> signals arrival at the branch node, so the
    /// stored segment index alone is not enough: it still reads 0 while the aircraft is physically past the branch.
    /// The rebuild has to notice that and resume on the exit taxiway anyway.
    /// </summary>
    [Fact]
    public void RestoredPastTheBranchNode_ResumesOnTheExitTaxiway_EvenWhenTheStoredIndexIsStillZero()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode Branch, GroundNode HoldShort, string Taxiway)? pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        (GroundNode? branch, GroundNode? holdShort, string? taxiway) = pair.Value;
        var runwayHeading = new TrueHeading(281.0);

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = holdShort.Id,
            ExitTaxiway = taxiway,
            RunwayId = "28R",
            ExitSpeed = 25.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = runwayHeading.Degrees,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            TurnStarted = true,
            ExitWaypointIndex = 0,
            ExitWaypointNodeIds = [branch.Id, holdShort.Id],
        };

        var phase = RunwayExitPhase.FromSnapshot(dto, layout);

        // 50 ft down the runway from the branch — the navigator was one tick from advancing when the snapshot hit.
        var aircraft = new AircraftState
        {
            Callsign = "TEST2",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(branch.Position, runwayHeading, 50.0 / GeoMath.FeetPerNm),
            TrueHeading = runwayHeading,
            Altitude = 9.0,
            IndicatedAirspeed = 25.0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Phases = new PhaseList(),
        };

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            FieldElevation = 9.0,
            Logger = NullLogger.Instance,
        };

        Assert.False(phase.OnTick(ctx));

        RunwayExitPhaseDto round = Assert.IsType<RunwayExitPhaseDto>(phase.ToSnapshot());
        Assert.True(
            round.ExitWaypointIndex >= 1,
            $"rebuilt route resumed on segment {round.ExitWaypointIndex} (the virtual approach leg back to the branch the aircraft already crossed)"
        );
    }

    private static SimScenarioState NewScenario() =>
        new()
        {
            ScenarioId = "test-oak-exit-restore-drift",
            ScenarioName = "OAK Exit Restore Drift",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

    private static AircraftState NewLandingAircraft(RunwayInfo runway)
    {
        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);
        var aircraft = new AircraftState
        {
            Callsign = "TSTAC",
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318,
            IndicatedAirspeed = 130,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        return aircraft;
    }

    /// <summary>
    /// The at-the-branch cases above never exercise the geometry that actually breaks: once the aircraft is
    /// <em>past</em> the branch node, a route rebuilt from segment 0 hands the navigator a virtual approach leg
    /// [current position → branch] that points <em>backward</em>, and the ~180° entry-alignment slow-turn taxis
    /// the restored aircraft back onto the runway it just vacated. Rewind, bug-bundle reconstruction and client
    /// playback all restore from snapshots, so the reconstructed session diverges from what happened live.
    /// </summary>
    [Fact]
    public void RestoredMidExit_PastBranchNode_DoesNotBacktrackTowardTheRunway()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        var engine = new SimulationEngine(new TestAirportGroundData());
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(runway);

        AircraftState aircraft = NewLandingAircraft(runway);
        aircraft.Ground.Layout = layout;
        aircraft.Phases!.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        engine.World.AddAircraft(aircraft);
        engine.Scenario = NewScenario();

        Assert.True(engine.SendCommand("TSTAC", "CLAND").Success);

        AircraftSnapshotDto? dto = null;
        int ticksAfterTurn = 0;
        for (int t = 1; t <= 400; t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is not RunwayExitPhase exit)
            {
                continue;
            }

            if (!exit.TurnStarted || exit.IsOnCenterline)
            {
                continue;
            }

            ticksAfterTurn++;
            if (ticksAfterTurn < 3)
            {
                continue;
            }

            dto = aircraft.ToSnapshot();
            RunwayExitPhaseDto exitDto = Assert.IsType<RunwayExitPhaseDto>(exit.ToSnapshot());
            _output.WriteLine(
                $"snapshot at t={t}: pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}) "
                    + $"hdg={aircraft.TrueHeading.Degrees:F1} gs={aircraft.GroundSpeed:F1} twy={aircraft.Ground.CurrentTaxiway} "
                    + $"seg={exitDto.ExitWaypointIndex} path=[{string.Join("→", exitDto.ExitWaypointNodeIds ?? [])}]"
            );
            break;
        }

        Assert.NotNull(dto);

        const int CompareTicks = 12;
        var liveHeadings = new List<double>();
        var livePositions = new List<LatLon>();
        for (int k = 0; k < CompareTicks; k++)
        {
            engine.TickOneSecond();
            liveHeadings.Add(aircraft.TrueHeading.Degrees);
            livePositions.Add(aircraft.Position);
        }

        var engine2 = new SimulationEngine(new TestAirportGroundData());
        var restored = AircraftState.FromSnapshot(dto!, layout);
        restored.Ground.Layout = layout;
        engine2.World.AddAircraft(restored);
        engine2.Scenario = NewScenario();

        double maxHeadingDrift = 0;
        double finalHeadingDrift = 0;
        double firstPosDriftFt = 0;
        double finalPosDriftFt = 0;
        for (int k = 0; k < CompareTicks; k++)
        {
            engine2.TickOneSecond();
            finalHeadingDrift = new TrueHeading(liveHeadings[k]).AbsAngleTo(restored.TrueHeading);
            finalPosDriftFt = GeoMath.DistanceNm(livePositions[k], restored.Position) * GeoMath.FeetPerNm;
            maxHeadingDrift = Math.Max(maxHeadingDrift, finalHeadingDrift);
            if (k == 0)
            {
                firstPosDriftFt = finalPosDriftFt;
            }

            _output.WriteLine(
                $"k={k}: live hdg={liveHeadings[k]:F1} | restored hdg={restored.TrueHeading.Degrees:F1} "
                    + $"| hdgDrift={finalHeadingDrift:F1} posDrift={finalPosDriftFt:F0}ft"
            );
        }

        Assert.True(
            maxHeadingDrift < 45.0,
            $"restored aircraft heading diverged {maxHeadingDrift:F0} deg from the live exit (backtrack toward the runway)"
        );

        // Rejoining matters as much as not reversing: a reconstruction that merely avoided the U-turn but settled
        // on some other path would still be useless for rewind and bug-bundle triage.
        Assert.True(finalHeadingDrift < 5.0, $"restored aircraft never rejoined the live exit heading (off by {finalHeadingDrift:F0} deg)");

        // Position is checked for *growth*, not for an absolute bound. The backtrack signature is a gap that opens
        // and keeps opening (64 ft → 374 ft over six seconds in the report). This test restores a hand-built DTO with
        // no navigator, so the rebuilt route sets its segment up from the restored pose rather than resuming a saved
        // primitive, and the reconstruction can trail the live aircraft by a fixed amount on the same path; the
        // exact resume of a saved primitive is GroundNavigatorArcRestoreTests' subject.
        Assert.True(
            finalPosDriftFt <= firstPosDriftFt + 25.0,
            $"restored aircraft kept diverging from the live exit path ({firstPosDriftFt:F0} ft → {finalPosDriftFt:F0} ft)"
        );
    }
}
