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
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

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
        foreach (var holdShort in layout.GetRunwayHoldShortNodes(runwayId))
        {
            foreach (var edge in holdShort.Edges)
            {
                if (string.IsNullOrEmpty(edge.TaxiwayName))
                {
                    continue;
                }

                foreach (var node in edge.Nodes)
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
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        var (branch, holdShort, taxiway) = pair.Value;

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
        };
        aircraft.Phases = new PhaseList();

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
    /// The restore path rebuilds the exit route from segment 0, so the navigator's own segment index says
    /// nothing about whether the aircraft was already turning. <c>TurnStarted</c> has to round-trip, or an
    /// aircraft restored mid-turn would reopen the window for a late exit change it can no longer honor.
    /// </summary>
    [Fact]
    public void TurnStarted_SurvivesASnapshotRoundTrip()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        var (branch, holdShort, taxiway) = pair.Value;

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

        var round = Assert.IsType<RunwayExitPhaseDto>(restored.ToSnapshot());
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
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var pair = FindExitPair(layout, "28R");
        if (pair is null)
        {
            return;
        }

        var (branch, holdShort, taxiway) = pair.Value;
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
        };
        aircraft.Phases = new PhaseList();

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

        var round = Assert.IsType<RunwayExitPhaseDto>(phase.ToSnapshot());
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
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);
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
        };
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
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

        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        var engine = new SimulationEngine(new TestAirportGroundData());
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "30");
        Assert.NotNull(runway);

        var aircraft = NewLandingAircraft(runway);
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
            var exitDto = Assert.IsType<RunwayExitPhaseDto>(exit.ToSnapshot());
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
        // and keeps opening (64 ft → 374 ft over six seconds in the report); what remains after the fix is a fixed
        // lag, because GroundNavigator is deliberately non-round-tripping — it does not persist Bézier progress, so
        // a restore mid-fillet replays that arc from its start and stays a couple of seconds behind on the same
        // path. Asserting a small absolute drift here would be asserting on that separate limitation.
        Assert.True(
            finalPosDriftFt <= firstPosDriftFt + 25.0,
            $"restored aircraft kept diverging from the live exit path ({firstPosDriftFt:F0} ft → {finalPosDriftFt:F0} ft)"
        );
    }
}
