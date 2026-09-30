using Microsoft.Extensions.Logging;
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

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The two ends of the CLANDF rollout that no exit ahead can serve, on SFO 28L with real navdata and layout:
/// <see cref="LandingPhase"/>'s runway-end stop (a forced landing whose rollout cannot make any exit at
/// <see cref="ForcedLandingProfile.RolloutMaxDecelKtsPerSec"/> stops on the pavement short of the end) and
/// <see cref="RunwayExitPhase"/>'s backstop (a forced aircraft stopped on the runway with no exit ahead backtracks to an
/// exit behind it rather than sitting on the runway for good; any other aircraft stops and asks, AIM 4-3-21.a).
///
/// 28L's far end carries three turnoffs — Z1 (hold-short 856, left), S3 (857, right) and F2 (859, left) — and the
/// exit before them is R (node 711, ~500 ft back). Every position on 28L has one of the end turnoffs ahead, so each
/// test parks an aircraft holding after exit at all three end hold-shorts: that is what leaves "no free exit ahead" on
/// a real layout.
/// </summary>
public class ForcedRolloutStopAndBackstopTests(ITestOutputHelper output)
{
    private const int LeaveRunwayBudgetSeconds = 240;

    /// <summary>How long (s) a non-forced aircraft with no exit ahead is watched for a backtrack it must not make.</summary>
    private const int NoBacktrackWatchSeconds = 120;

    /// <summary>How far before the 28L pavement end (ft) the backstop aircraft sits, stopped.</summary>
    private const double BackstopShortOfEndFt = 20.0;

    /// <summary>How far back from the 28L pavement end (ft) a hold-short counts as an end-of-runway turnoff's.</summary>
    private const double EndTurnoffWindowFt = 250.0;

    /// <summary>
    /// How far past 28L's last centerline node (ft) the "no exit ahead at all" aircraft sits: beyond the exit finder's
    /// 30 ft behind-the-aircraft tolerance, so the end turnoffs read as behind it.
    /// </summary>
    private const double PastLastCenterlineNodeFt = 40.0;

    /// <summary>28L's end-of-runway turnoff hold-shorts: every 28L hold-short within the last 250 ft of the runway.</summary>
    private static List<GroundNode> EndTurnoffHoldShorts(Sfo28L rwy) =>
        [
            .. rwy
                .Layout.Nodes.Values.Where(n =>
                    (n.Type == GroundNodeType.RunwayHoldShort)
                    && (n.RunwayId?.Contains(rwy.Runway.Designator) == true)
                    && (AlongFt(rwy, n.Position) > rwy.LandingDistanceFt - EndTurnoffWindowFt)
                )
                .OrderBy(n => n.Id),
        ];

    private sealed record Sfo28L(RunwayInfo Runway, AirportGroundLayout Layout, LatLon Threshold, LatLon End, double LandingDistanceFt);

    private static Sfo28L? Load()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28L");
        Assert.NotNull(runway);
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        return new Sfo28L(
            runway,
            layout,
            LandingThreshold.Resolve(runway, layout),
            new LatLon(runway.EndLatitude, runway.EndLongitude),
            ForcedLandingProfile.LandingDistanceFt(runway, layout)
        );
    }

    private static double AlongFt(Sfo28L rwy, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, rwy.Threshold, rwy.Runway.TrueHeading) * GeoMath.FeetPerNm;

    private static double CrossTrackFt(Sfo28L rwy, LatLon position) =>
        GeoMath.SignedCrossTrackDistanceNm(position, rwy.Threshold, rwy.Runway.TrueHeading) * GeoMath.FeetPerNm;

    private static AircraftState NewCrj7(string callsign, LatLon position, TrueHeading heading, double altitudeFt, double iasKts, bool onGround) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "CRJ7",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitudeFt,
            IndicatedAirspeed = iasKts,
            IsOnGround = onGround,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

    /// <summary>
    /// Adds <paramref name="aircraft"/> with its phase list still pending: the engine's first tick starts the current
    /// phase with the full tick context, so a <see cref="RunwayExitPhase"/> first in the list searches with the
    /// occupied hold-shorts excluded — a minimal context would let it pick an occupied exit.
    /// </summary>
    private static void AddPending(SimulationEngine engine, AircraftState aircraft, AirportGroundLayout layout)
    {
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
    }

    /// <summary>An engine at SFO with no traffic.</summary>
    private SimulationEngine NewEngine(Sfo28L rwy)
    {
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("RunwayExitPhase", LogLevel.Information)
            .EnableCategory("LandingPhase", LogLevel.Information)
            .InitializeSimLog();

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-sfo-forced-rollout",
                ScenarioName = "SFO forced rollout test",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SFO",
            },
        };

        output.WriteLine($"threshold {rwy.Threshold}, end {rwy.End} at {AlongFt(rwy, rwy.End):F0} ft, heading {rwy.Runway.TrueHeading.Degrees:F1}");
        return engine;
    }

    /// <summary>An engine at SFO with an aircraft holding after exit at each of 28L's end-of-runway hold-shorts.</summary>
    private SimulationEngine NewEngineWithEndTurnoffsBlocked(Sfo28L rwy)
    {
        SimulationEngine engine = NewEngine(rwy);
        List<GroundNode> endBars = EndTurnoffHoldShorts(rwy);
        foreach (GroundNode bar in endBars)
        {
            output.WriteLine($"blocking hold-short {bar.Id} at {bar.Position}, {AlongFt(rwy, bar.Position):F0} ft along");
            AircraftState blocker = NewCrj7($"BLK{bar.Id}", bar.Position, rwy.Runway.TrueHeading, rwy.Runway.ElevationFt, iasKts: 0, onGround: true);
            blocker.Phases = new PhaseList();
            blocker.Phases.Add(new HoldingAfterExitPhase(rwy.Runway.Designator, exitTaxiway: null, bar.Id));
            AddPending(engine, blocker, rwy.Layout);
        }

        Assert.NotEmpty(endBars);
        Assert.Equal([.. endBars.Select(b => b.Id).Order()], engine.ComputeOccupiedHoldShortNodes().Order());
        return engine;
    }

    /// <summary>A CRJ7 stopped on 28L at <paramref name="position"/> in <see cref="RunwayExitPhase"/>, flagged as a forced rollout or not.</summary>
    private static AircraftState AddStoppedExiting(SimulationEngine engine, Sfo28L rwy, LatLon position, bool forcedRollout)
    {
        AircraftState aircraft = NewCrj7("TSTAC", position, rwy.Runway.TrueHeading, rwy.Runway.ElevationFt, iasKts: 0, onGround: true);
        aircraft.Phases = new PhaseList { AssignedRunway = rwy.Runway, ForcedRollout = forcedRollout };
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        AddPending(engine, aircraft, rwy.Layout);
        return aircraft;
    }

    /// <summary>Every warning and pilot-speech line the engine emits for <paramref name="callsign"/>, in order.</summary>
    private static List<string> CaptureMessages(SimulationEngine engine, string callsign)
    {
        List<string> messages = [];
        engine.WarningEmitted += (cs, text) =>
        {
            if (cs == callsign)
            {
                messages.Add(text);
            }
        };
        engine.PilotSpeechEmitted += (cs, text) =>
        {
            if (cs == callsign)
            {
                messages.Add(text);
            }
        };
        return messages;
    }

    private static LatLon ShortOfEnd(Sfo28L rwy) =>
        GeoMath.ProjectPoint(rwy.End, rwy.Runway.TrueHeading.ToReciprocal(), BackstopShortOfEndFt / GeoMath.FeetPerNm);

    /// <summary>
    /// A forced (CLANDF) CRJ7 stopped on 28L 20 ft short of the pavement end with every end turnoff occupied has no free
    /// exit ahead: on its first tick it commits to an exit behind it, and it is off the runway within the budget without
    /// rolling off the end.
    /// </summary>
    [Fact]
    public void RunwayExitBackstop_ForcedStoppedWithNoExitAhead_BacktracksToExitBehindAndVacates()
    {
        if (Load() is not { } rwy)
        {
            return;
        }

        SimulationEngine engine = NewEngineWithEndTurnoffsBlocked(rwy);
        LatLon start = ShortOfEnd(rwy);
        AircraftState aircraft = AddStoppedExiting(engine, rwy, start, forcedRollout: true);
        var exitPhase = (RunwayExitPhase)aircraft.Phases!.Phases[0];

        double startAlongFt = AlongFt(rwy, start);
        engine.TickOneSecond();
        Assert.True(exitPhase.TargetHoldShortNodeId is not null, "the stopped aircraft committed to no exit on its first tick");
        GroundNode holdShort = rwy.Layout.Nodes[exitPhase.TargetHoldShortNodeId!.Value];
        double holdShortAlongFt = AlongFt(rwy, holdShort.Position);
        output.WriteLine($"start {startAlongFt:F0} ft along, committed hold-short {holdShort.Id} at {holdShortAlongFt:F0} ft along");
        Assert.True(
            holdShortAlongFt < startAlongFt,
            $"committed exit at {holdShortAlongFt:F0} ft is not behind the aircraft at {startAlongFt:F0} ft"
        );

        int? leftAt = null;
        double maxAlongFt = startAlongFt;
        for (int t = 2; (t <= LeaveRunwayBudgetSeconds) && (leftAt is null); t++)
        {
            engine.TickOneSecond();
            maxAlongFt = Math.Max(maxAlongFt, AlongFt(rwy, aircraft.Position));
            if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase or TaxiingPhase)
            {
                leftAt = t;
            }
        }

        output.WriteLine($"left the runway at t={leftAt}, max along {maxAlongFt:F0} ft of {rwy.LandingDistanceFt:F0} ft");
        Assert.True(leftAt is not null, $"still on the runway after {LeaveRunwayBudgetSeconds} s in {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(
            maxAlongFt <= rwy.LandingDistanceFt,
            $"rolled past the runway end: max along {maxAlongFt:F0} ft of {rwy.LandingDistanceFt:F0} ft"
        );
        Assert.False(aircraft.Phases?.ForcedRollout ?? false, "the forced-rollout flag outlived the runway exit");
    }

    /// <summary>
    /// A CRJ7 that is not a forced rollout, stopped on 28L past its last centerline node — no exit ahead of it at all —
    /// stays where it is: it reports once that it cannot exit and asks for a back-taxi to the exit behind it, tells the
    /// instructor it is waiting, and never backtracks on its own (AIM 4-3-21.a).
    /// </summary>
    [Fact]
    public void RunwayExitBackstop_NotForcedStoppedWithNoExitAhead_ReportsOnceAndNeverBacktracks()
    {
        if (Load() is not { } rwy)
        {
            return;
        }

        SimulationEngine engine = NewEngine(rwy);
        double lastCenterlineAlongFt = rwy
            .Layout.Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesRunway(rwy.Runway.Designator)))
            .Max(n => AlongFt(rwy, n.Position));
        LatLon start = GeoMath.ProjectPoint(
            rwy.Threshold,
            rwy.Runway.TrueHeading,
            (lastCenterlineAlongFt + PastLastCenterlineNodeFt) / GeoMath.FeetPerNm
        );
        AircraftState aircraft = AddStoppedExiting(engine, rwy, start, forcedRollout: false);
        var exitPhase = (RunwayExitPhase)aircraft.Phases!.Phases[0];
        List<string> messages = CaptureMessages(engine, "TSTAC");
        output.WriteLine($"last centerline node at {lastCenterlineAlongFt:F0} ft along, aircraft at {AlongFt(rwy, start):F0} ft");

        double maxMovedFt = 0;
        for (int t = 1; t <= NoBacktrackWatchSeconds; t++)
        {
            engine.TickOneSecond();
            maxMovedFt = Math.Max(maxMovedFt, GeoMath.DistanceNm(start, aircraft.Position) * GeoMath.FeetPerNm);
            Assert.True(exitPhase.TargetHoldShortNodeId is null, $"t={t}: committed to hold-short {exitPhase.TargetHoldShortNodeId} on its own");
        }

        foreach (string message in messages)
        {
            output.WriteLine($"message: {message}");
        }

        Assert.IsType<RunwayExitPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(maxMovedFt < 5.0, $"moved {maxMovedFt:F1} ft while waiting");
        string report = Assert.Single(messages, m => m.Contains("unable to exit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("request back-taxi to", report, StringComparison.OrdinalIgnoreCase);
        Assert.Single(messages, m => m.Contains("no exit ahead", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A CRJ7 that is not a forced rollout, stopped 20 ft short of the 28L end with every end turnoff occupied, holds where
    /// it is — no backtrack, no report — and takes an end turnoff ahead once the occupants have gone.
    /// </summary>
    [Fact]
    public void RunwayExitBackstop_NotForcedStoppedWithExitsAheadOccupied_HoldsThenExitsAheadWhenCleared()
    {
        if (Load() is not { } rwy)
        {
            return;
        }

        SimulationEngine engine = NewEngineWithEndTurnoffsBlocked(rwy);
        LatLon start = ShortOfEnd(rwy);
        AircraftState aircraft = AddStoppedExiting(engine, rwy, start, forcedRollout: false);
        var exitPhase = (RunwayExitPhase)aircraft.Phases!.Phases[0];
        List<string> messages = CaptureMessages(engine, "TSTAC");

        const int HoldSeconds = 30;
        for (int t = 1; t <= HoldSeconds; t++)
        {
            engine.TickOneSecond();
            Assert.True(
                exitPhase.TargetHoldShortNodeId is null,
                $"t={t}: committed to hold-short {exitPhase.TargetHoldShortNodeId} while every exit ahead is occupied"
            );
        }

        double heldMovedFt = GeoMath.DistanceNm(start, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(heldMovedFt < 5.0, $"moved {heldMovedFt:F1} ft while holding");
        Assert.DoesNotContain(messages, m => m.Contains("unable to exit", StringComparison.OrdinalIgnoreCase));

        foreach (AircraftState blocker in engine.World.GetSnapshot().Where(ac => ac.Callsign.StartsWith("BLK", StringComparison.Ordinal)))
        {
            engine.World.RemoveAircraft(blocker.Callsign);
        }

        double startAlongFt = AlongFt(rwy, start);
        double? committedAlongFt = null;
        int? leftAt = null;
        for (int t = HoldSeconds + 1; (t <= HoldSeconds + LeaveRunwayBudgetSeconds) && (leftAt is null); t++)
        {
            engine.TickOneSecond();
            if ((committedAlongFt is null) && (exitPhase.TargetHoldShortNodeId is { } hsId))
            {
                committedAlongFt = AlongFt(rwy, rwy.Layout.Nodes[hsId].Position);
                output.WriteLine($"t={t}: committed to hold-short {hsId} at {committedAlongFt:F0} ft along (start {startAlongFt:F0} ft)");
            }

            if (aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase or TaxiingPhase)
            {
                leftAt = t;
            }
        }

        Assert.True(committedAlongFt is not null, "never committed to an exit after the occupants left");
        Assert.True(committedAlongFt > startAlongFt - EndTurnoffWindowFt, $"took an exit at {committedAlongFt:F0} ft, not an end turnoff");
        Assert.True(leftAt is not null, $"never vacated the runway: still in {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.DoesNotContain(messages, m => m.Contains("unable to exit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An instructor HOLD on a forced CRJ7 stopped 20 ft short of the 28L end with every end turnoff occupied keeps it
    /// where it is: the held aircraft reads as stopped, but the backstop must not move it.
    /// </summary>
    [Fact]
    public void RunwayExitBackstop_HoldPosition_DoesNotBacktrack()
    {
        if (Load() is not { } rwy)
        {
            return;
        }

        SimulationEngine engine = NewEngineWithEndTurnoffsBlocked(rwy);
        LatLon start = ShortOfEnd(rwy);
        AircraftState aircraft = AddStoppedExiting(engine, rwy, start, forcedRollout: true);
        var exitPhase = (RunwayExitPhase)aircraft.Phases!.Phases[0];

        CommandResult hold = engine.SendCommand("TSTAC", "HOLD");
        Assert.True(hold.Success, $"HOLD failed: {hold.Message}");

        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            Assert.True(
                exitPhase.TargetHoldShortNodeId is null,
                $"t={t}: the held aircraft committed to hold-short {exitPhase.TargetHoldShortNodeId}"
            );
        }

        double movedFt = GeoMath.DistanceNm(start, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.IsType<RunwayExitPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(movedFt < 5.0, $"the held aircraft moved {movedFt:F1} ft");
    }

    /// <summary>
    /// CLANDF on a CRJ7 1.55 nm past the 28L threshold at 20 ft and 120 kt, with every end turnoff occupied: it touches
    /// down ~9,600 ft down the runway, too fast to make R at 6 kt/s, so the landing brakes to a stop on the centerline at
    /// least 300 ft short of the end and never leaves the pavement; the runway exit's backstop then takes it back to R.
    /// </summary>
    [Fact]
    public void ForcedRollout_NoUsableExit_StopsShortOfRunwayEndThenBackstopVacates()
    {
        if (Load() is not { } rwy)
        {
            return;
        }

        SimulationEngine engine = NewEngineWithEndTurnoffsBlocked(rwy);
        AircraftState aircraft = AddForcedArrival(engine, rwy);
        var exitPhase = (RunwayExitPhase)aircraft.Phases!.Phases[2];

        double? landingStopAlongFt = null;
        double? exitHoldShortAlongFt = null;
        double maxAlongFt = double.MinValue;
        double maxLandingCrossTrackFt = 0;
        int? leftAt = null;
        for (int t = 1; (t <= LeaveRunwayBudgetSeconds) && (leftAt is null); t++)
        {
            engine.TickOneSecond();
            Phase? phase = aircraft.Phases?.CurrentPhase;
            if (aircraft.IsOnGround && (phase is LandingPhase or RunwayExitPhase))
            {
                maxAlongFt = Math.Max(maxAlongFt, AlongFt(rwy, aircraft.Position));
            }

            if (aircraft.IsOnGround && (phase is LandingPhase))
            {
                maxLandingCrossTrackFt = Math.Max(maxLandingCrossTrackFt, Math.Abs(CrossTrackFt(rwy, aircraft.Position)));
                output.WriteLine(
                    $"t={t} landing rollout: {AlongFt(rwy, aircraft.Position):F0} ft along, gs={aircraft.GroundSpeed:F1} kt, "
                        + $"decel={aircraft.Targets.DesiredDecelRate:F1} kt/s"
                );
            }

            // The landing completes on the tick it stops, so the one-second sample may already read the runway exit.
            if (aircraft.IsOnGround && (phase is LandingPhase or RunwayExitPhase) && (landingStopAlongFt is null) && (aircraft.GroundSpeed < 1.0))
            {
                landingStopAlongFt = AlongFt(rwy, aircraft.Position);
            }

            if ((exitHoldShortAlongFt is null) && (exitPhase.TargetHoldShortNodeId is { } hsId))
            {
                exitHoldShortAlongFt = AlongFt(rwy, rwy.Layout.Nodes[hsId].Position);
                output.WriteLine($"t={t}: runway exit committed to hold-short {hsId} at {exitHoldShortAlongFt:F0} ft along");
            }

            if (phase is HoldingAfterExitPhase or TaxiingPhase)
            {
                leftAt = t;
            }
        }

        string summary =
            $"landing stop {landingStopAlongFt?.ToString("F0") ?? "none"} ft, max along {maxAlongFt:F0} ft of {rwy.LandingDistanceFt:F0} ft, "
            + $"max landing |xte| {maxLandingCrossTrackFt:F0} ft, exit hold-short {exitHoldShortAlongFt?.ToString("F0") ?? "none"} ft, left runway t={leftAt}";
        output.WriteLine(summary);

        Assert.True(landingStopAlongFt is not null, $"the landing never stopped on the runway (it made an exit instead): {summary}");
        Assert.True(
            landingStopAlongFt <= rwy.LandingDistanceFt - ForcedLandingProfile.RunwayEndStopMarginFt,
            $"stopped within 300 ft of the end: {summary}"
        );
        Assert.True(maxAlongFt <= rwy.LandingDistanceFt, $"rolled past the runway end: {summary}");
        Assert.True(maxLandingCrossTrackFt <= rwy.Runway.WidthFt / 2.0, $"left the side of the runway: {summary}");
        Assert.True(exitHoldShortAlongFt < landingStopAlongFt, $"the runway exit did not take an exit behind the stop: {summary}");
        Assert.True(leftAt is not null, $"never vacated the runway within {LeaveRunwayBudgetSeconds} s: {summary}");
    }

    /// <summary>
    /// The forced rollout above, snapshotted and restored mid-rollout in <see cref="LandingPhase"/>, follows exactly the
    /// trajectory of the uninterrupted run through the stop, the backtrack commit and the exit.
    /// </summary>
    [Fact]
    public void ForcedRollout_SnapshotRoundTrips_MatchUninterruptedTrajectory()
    {
        if (Load() is not { } rwy)
        {
            return;
        }

        List<string> uninterrupted = RunForcedRolloutTrace(rwy, roundTrip: false);
        List<string> roundTripped = RunForcedRolloutTrace(rwy, roundTrip: true);

        Assert.Equal(uninterrupted.Count, roundTripped.Count);
        for (int i = 0; i < uninterrupted.Count; i++)
        {
            Assert.True(
                uninterrupted[i] == roundTripped[i],
                $"diverged at sample {i}:{Environment.NewLine}{uninterrupted[i]}{Environment.NewLine}{roundTripped[i]}"
            );
        }
    }

    /// <summary>A CRJ7 1.55 nm past the 28L threshold at 20 ft and 120 kt, on final for 28L, with CLANDF issued.</summary>
    private static AircraftState AddForcedArrival(SimulationEngine engine, Sfo28L rwy)
    {
        LatLon start = GeoMath.ProjectPoint(rwy.Threshold, rwy.Runway.TrueHeading, 1.55);
        AircraftState aircraft = NewCrj7("TSTAC", start, rwy.Runway.TrueHeading, rwy.Runway.ElevationFt + 20, iasKts: 120, onGround: false);
        aircraft.Targets.TargetSpeed = 120;
        aircraft.Phases = new PhaseList { AssignedRunway = rwy.Runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, rwy.Layout));
        AddPending(engine, aircraft, rwy.Layout);

        CommandResult result = engine.SendCommand("TSTAC", "CLANDF");
        Assert.True(result.Success, $"CLANDF failed: {result.Message}");
        return aircraft;
    }

    /// <summary>
    /// One line per second of the forced rollout until the aircraft leaves the runway: position, heading, ground speed
    /// and phase at full precision. With <paramref name="roundTrip"/> the engine is snapshotted and restored on the first
    /// second on the ground in <see cref="LandingPhase"/>.
    /// </summary>
    private List<string> RunForcedRolloutTrace(Sfo28L rwy, bool roundTrip)
    {
        SimulationEngine engine = NewEngineWithEndTurnoffsBlocked(rwy);
        AddForcedArrival(engine, rwy);

        // No round trip once the runway exit follows its route — backlog: "GroundNavigatorDto omits arc/Bezier/reversal/
        // turn-accumulator state, so a mid-route restore is lossy".
        List<string> trace = [];
        bool restoredMidRollout = false;
        for (int t = 1; t <= LeaveRunwayBudgetSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState aircraft = engine.World.FindAircraft("TSTAC") ?? throw new InvalidOperationException("TSTAC is gone");
            Phase? phase = aircraft.Phases?.CurrentPhase;
            trace.Add(
                $"t={t} {aircraft.Position.Lat:R},{aircraft.Position.Lon:R} hdg={aircraft.TrueHeading.Degrees:R} gs={aircraft.GroundSpeed:R} {phase?.Name}"
            );

            if (phase is HoldingAfterExitPhase or TaxiingPhase)
            {
                break;
            }

            if (roundTrip && !restoredMidRollout && aircraft.IsOnGround && (phase is LandingPhase))
            {
                RoundTrip(engine, t);
                restoredMidRollout = true;
            }
        }

        if (roundTrip)
        {
            Assert.True(restoredMidRollout, "never snapshotted mid-rollout");
        }

        return trace;
    }

    private void RoundTrip(SimulationEngine engine, int t)
    {
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        engine.RestoreFromSnapshot(snapshot);
        output.WriteLine($"t={t}: snapshot round trip");
    }

    /// <summary>
    /// The runway-end stop's braking: the forced rollout's floor with plenty of runway; the rate that stops 300 ft short
    /// of the end while that is between the floor and the 10 kt/s cap; the cap when stopping 300 ft short needs more but
    /// the cap still stops the aircraft on the runway; and the rate that stops it at the end when even the cap would not.
    /// </summary>
    [Fact]
    public void ForcedRunwayEndStopDecel_FloorStopShortCapAndEndRegimes()
    {
        Assert.Equal(ForcedLandingProfile.RolloutMinDecelKtsPerSec, LandingPhase.ForcedRunwayEndStopDecelKtsPerSec(30, 5000), 6);

        double stopShort = RolloutBraking.RequiredDecelKtsPerSec(60, 0, 700 / GeoMath.FeetPerNm);
        Assert.InRange(stopShort, ForcedLandingProfile.RolloutMinDecelKtsPerSec, ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec);
        Assert.Equal(stopShort, LandingPhase.ForcedRunwayEndStopDecelKtsPerSec(60, 1000), 6);

        Assert.True(RolloutBraking.RequiredDecelKtsPerSec(60, 0, 400 / GeoMath.FeetPerNm) < ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec);
        Assert.Equal(ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec, LandingPhase.ForcedRunwayEndStopDecelKtsPerSec(60, 400), 6);

        double toEnd = RolloutBraking.RequiredDecelKtsPerSec(80, 0, 400 / GeoMath.FeetPerNm);
        Assert.True(toEnd > ForcedLandingProfile.RunwayEndStopMaxDecelKtsPerSec);
        Assert.Equal(toEnd, LandingPhase.ForcedRunwayEndStopDecelKtsPerSec(80, 400), 6);
    }
}
