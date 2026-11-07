using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A LAHSO clearance past the landing's hand-off to the runway exit, on the real SFO layout (28R holding short of 1L).
///
/// <para>
/// AIM 4-3-11.b.6: exit at the first convenient taxiway before the hold-short point, otherwise stop and hold at it. The limit is
/// kept on <c>Phases.LahsoHoldShort</c> until the aircraft is clear of the runway, so every runway-exit search and late retarget is
/// judged against it; with no exit before the point the nose stops short of it and the aircraft holds there. After touchdown a
/// crew that can no longer stop the nose by the point (the deceleration needed exceeds the type's max-effort rate) says so once,
/// the clearance is void, and the aircraft rolls on to the first available exit beyond. Exits are selected by name and geometry,
/// never by node id.
/// </para>
/// </summary>
public class LahsoPastHandoffTests(ITestOutputHelper output)
{
    /// <summary>The layouts under <c>TestData</c>, cached across every run in this class.</summary>
    private static readonly TestAirportGroundData GroundData = new();

    private const string Callsign = "TST001";

    private sealed record Setup(
        SimulationEngine Engine,
        AircraftState Aircraft,
        AirportGroundLayout Layout,
        RunwayInfo Runway,
        LatLon Threshold,
        LahsoTarget Target
    )
    {
        /// <summary>The aircraft's warning lines as the engine emits them (RPO mode without speech: pilot calls and instructor lines).</summary>
        public List<string> Lines { get; } = [];

        public double HoldShortFt => Target.DistFromThresholdNm * GeoMath.FeetPerNm;

        /// <summary>The same length source and 60 ft unknown-type fallback the phases use for the nose offset.</summary>
        public double NoseOffsetFt => (FaaAircraftDatabase.Get(Aircraft.AircraftType)?.LengthFt ?? 60.0) / 2.0;

        public double AlongFt => GeoMath.AlongTrackDistanceNm(Aircraft.Position, Threshold, Runway.TrueHeading) * GeoMath.FeetPerNm;

        public double NoseAlongFt => AlongFt + NoseOffsetFt;
    }

    /// <summary>An exit ahead on 28R: its taxiway, branch and bar, the branch's distance from the landing threshold, and whether its
    /// turn-off fits before the LAHSO stop limit by the landing's own judge.</summary>
    private sealed record ExitAhead(
        string Taxiway,
        ExitSide Side,
        GroundNode Branch,
        GroundNode HoldShort,
        double BranchAlongFt,
        bool Fits,
        List<GroundNode> Path
    );

    /// <summary>The exit as the landing commits it at the hand-off (<c>Phases.ResolvedExit</c>), for
    /// <c>OnStart</c>'s committed-exit branch.</summary>
    private static ResolvedExitInfo Committed(ExitAhead exit) =>
        new()
        {
            HoldShortNode = exit.HoldShort,
            TaxiwayName = exit.Taxiway,
            TurnOffSpeed = 15.0,
            Path = [.. exit.Path],
            BranchPointNode = exit.Branch,
            Side = exit.Side,
            SelectionDecelRate = null,
            StopsTailInsideRunwayLine = false,
        };

    /// <summary>
    /// Spawns <paramref name="aircraftType"/> on 1 nm final for SFO 28R and clears it to land and hold short of 1L, the production
    /// command computing the target. Null when the layout or nav data is absent (silent skip).
    /// </summary>
    private Setup? Spawn(string aircraftType, double approachSpeedKts)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("LandingPhase", LogLevel.Debug)
            .EnableCategory("RunwayExitPhase", LogLevel.Debug)
            .InitializeSimLog();
        var engine = new SimulationEngine(GroundData, PushPlanStore.CreateShipped());

        AirportGroundLayout? layout = GroundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        (double acLat, double acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);
        LatLon threshold = LandingThreshold.Resolve(runway, layout);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = aircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(
                GeoMath.DistanceNm(new LatLon(acLat, acLon), threshold),
                runway.ElevationFt,
                AircraftCategorization.Categorize(aircraftType)
            ),
            IndicatedAirspeed = approachSpeedKts,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-lahso-past-handoff",
            ScenarioName = "SFO 28R LAHSO past the hand-off",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };

        CommandResult lahso = engine.SendCommand(Callsign, "LAHSO 1L");
        Assert.True(lahso.Success, $"LAHSO 1L failed: {lahso.Message}");
        LahsoTarget? target = aircraft.Phases.LahsoHoldShort;
        Assert.NotNull(target);

        var setup = new Setup(engine, aircraft, layout, runway, threshold, target);
        engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == Callsign)
            {
                setup.Lines.Add(warning);
            }
        };
        return setup;
    }

    /// <summary>
    /// Puts the aircraft on the 28R centreline <paramref name="alongFt"/> past the landing threshold, rolling at
    /// <paramref name="speedKts"/>, still under its LAHSO clearance, with <paramref name="phases"/> as its list. The list is left
    /// unstarted so the engine's first tick starts it with the live occupancy, as a hand-off does.
    /// </summary>
    private static void PutOnRunway(Setup setup, double alongFt, double speedKts, params Phase[] phases)
    {
        AircraftState aircraft = setup.Aircraft;
        (double lat, double lon) = GeoMath.ProjectPointRaw(
            setup.Threshold.Lat,
            setup.Threshold.Lon,
            setup.Runway.TrueHeading.Degrees,
            alongFt / GeoMath.FeetPerNm
        );
        aircraft.Position = new LatLon(lat, lon);
        aircraft.TrueHeading = setup.Runway.TrueHeading;
        aircraft.TrueTrack = setup.Runway.TrueHeading;
        aircraft.Altitude = setup.Runway.ElevationFt;
        aircraft.VerticalSpeed = 0;
        aircraft.IsOnGround = true;
        aircraft.IndicatedAirspeed = speedKts;
        aircraft.Phases = new PhaseList { AssignedRunway = setup.Runway, LahsoHoldShort = setup.Target };
        foreach (Phase phase in phases)
        {
            aircraft.Phases.Add(phase);
        }
    }

    /// <summary>
    /// Every forward exit off 28R ahead of <paramref name="from"/>, occupied or not, nearest first, each judged against the LAHSO
    /// stop limit by the landing's own rule (<see cref="LandingPhase.TurnOffFitsBefore"/>).
    /// </summary>
    private static List<ExitAhead> ExitsAhead(Setup setup, LatLon from)
    {
        double limitNm = LandingPhase.LahsoStopLimitNm(setup.Target.DistFromThresholdNm, setup.Aircraft.AircraftType);
        var found = new HashSet<int>();
        var exits = new List<ExitAhead>();
        for (int walk = 0; walk < 64; walk++)
        {
            AirportGroundLayout.CenterlineExitResult? hit = setup.Layout.FindOnSidePreferredExit(
                from.Lat,
                from.Lon,
                setup.Runway.TrueHeading,
                setup.Runway.Designator,
                preference: null,
                sidePref: null,
                excludeBranchPoints: null,
                excludeHoldShortNodes: found,
                filter: candidate =>
                    candidate.ExitAngle > 100 ? AirportGroundLayout.CandidateVerdict.Skip : AirportGroundLayout.CandidateVerdict.Accept,
                maxIterations: AirportGroundLayout.DefaultMaxIterations
            );
            if ((hit is not { } exit) || !found.Add(exit.HoldShort.Id))
            {
                break;
            }

            double branchNm = GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, setup.Threshold, setup.Runway.TrueHeading);
            exits.Add(
                new ExitAhead(
                    exit.Taxiway,
                    exit.Side,
                    exit.Path[0],
                    exit.HoldShort,
                    branchNm * GeoMath.FeetPerNm,
                    LandingPhase.TurnOffFitsBefore(branchNm, exit.ExitAngle, setup.Runway.WidthFt, limitNm),
                    exit.Path
                )
            );
        }

        return [.. exits.OrderBy(e => e.BranchAlongFt)];
    }

    private string Describe(Setup setup, IEnumerable<ExitAhead> exits)
    {
        string text = string.Join("; ", exits.Select(e => $"{e.Taxiway}@{e.BranchAlongFt:F0}ft{(e.Fits ? "" : " (past limit)")}"));
        output.WriteLine($"hold-short point {setup.HoldShortFt:F0} ft; exits ahead: {text}");
        return text;
    }

    /// <summary>The pilot and instructor lines for the aircraft since the last call (RPO mode without speech: both are warning lines).</summary>
    private static List<string> DrainLines(Setup setup)
    {
        List<string> lines = [.. setup.Lines];
        setup.Lines.Clear();
        return lines;
    }

    /// <summary>
    /// An aircraft that has already cleared <paramref name="runway"/> and is holding at <paramref name="holdShort"/>, the state the
    /// engine reports as a claimed hold-short.
    /// </summary>
    private static AircraftState HoldingAfterExitBlocker(string callsign, RunwayInfo runway, AirportGroundLayout layout, GroundNode holdShort)
    {
        var blocker = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = holdShort.Position,
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        blocker.Ground.Layout = layout;
        blocker.Phases.Add(new HoldingAfterExitPhase(runway.Designator, "N", holdShort.Id, heldShortOfRunway: null));
        blocker.Phases.Start(CommandDispatcher.BuildMinimalContext(blocker, ActiveRunways.Empty, layout));
        return blocker;
    }

    /// <summary>
    /// The limit outlives the hand-off: a light aircraft that exits before the point still carries <c>Phases.LahsoHoldShort</c> in
    /// <see cref="RunwayExitPhase"/> (the evaluator's §3-7-2.g.3 crossing permission and the exit filter both read it), and loses it
    /// once clear of the runway (<c>CompleteExit</c>).
    /// </summary>
    [Fact]
    public void HandOff_KeepsTheLimit_UntilClearOfTheRunway()
    {
        Setup? setup = Spawn("C172", 75);
        if (setup is null)
        {
            return;
        }

        bool? setAtHandOff = null;
        bool reachedHoldingAfterExit = false;
        for (int t = 1; t <= 420; t++)
        {
            setup.Engine.TickOneSecond();
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            if ((current is RunwayExitPhase) && (setAtHandOff is null))
            {
                setAtHandOff = setup.Aircraft.Phases!.LahsoHoldShort is not null;
            }

            if (current is HoldingAfterExitPhase)
            {
                reachedHoldingAfterExit = true;
                break;
            }
        }

        Assert.True(setAtHandOff, "the LAHSO limit must still be set when the runway exit takes over");
        Assert.True(reachedHoldingAfterExit, $"expected the exit to complete, ended in {setup.Aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Null(setup.Aircraft.Phases!.LahsoHoldShort);
    }

    /// <summary>
    /// Puts a C172 rolling in <see cref="RunwayExitPhase"/> early on 28R and ticks until it has committed to its exit and handed the
    /// route to the navigator, still on the approach leg. Returns the phase and the committed taxiway.
    /// </summary>
    private (RunwayExitPhase Phase, string Committed) CommittedOnApproachLeg(Setup setup)
    {
        PutOnRunway(setup, alongFt: 800, speedKts: 25, new RunwayExitPhase(), new HoldingAfterExitPhase());
        for (int t = 1; t <= 3; t++)
        {
            setup.Engine.TickOneSecond();
        }

        RunwayExitPhase phase = Assert.IsType<RunwayExitPhase>(setup.Aircraft.Phases!.CurrentPhase);
        Assert.True(phase.IsOnApproachLeg, "the aircraft should be on the exit route's approach leg");
        Assert.NotNull(phase.ExitTaxiway);
        return (phase, phase.ExitTaxiway);
    }

    /// <summary>
    /// A late <c>EXIT</c> naming a taxiway that leaves 28R only past the hold-short point is refused with the reason ("unable, beyond
    /// the hold short point"), not with a braking-limit "unable", and the committed exit stands (AIM 4-3-11.b.6).
    /// </summary>
    [Fact]
    public void Retarget_ToAnExitPastThePoint_IsRefused_AndTheCommittedExitStands()
    {
        Setup? setup = Spawn("C172", 75);
        if (setup is null)
        {
            return;
        }

        (RunwayExitPhase phase, string committed) = CommittedOnApproachLeg(setup);
        List<ExitAhead> exits = ExitsAhead(setup, setup.Aircraft.Position);
        string listing = Describe(setup, exits);
        ExitAhead? past = exits.FirstOrDefault(e =>
            !e.Fits
            && !string.Equals(e.Taxiway, committed, StringComparison.OrdinalIgnoreCase)
            && exits.Where(o => string.Equals(o.Taxiway, e.Taxiway, StringComparison.OrdinalIgnoreCase)).All(o => !o.Fits)
        );
        Assert.True(past is not null, $"no taxiway leaves 28R only past the point: {listing}");

        CommandResult result = setup.Engine.SendCommand(Callsign, $"EXIT {past.Taxiway}");
        output.WriteLine($"EXIT {past.Taxiway}: success={result.Success} message={result.Message}");

        Assert.False(result.Success, $"EXIT {past.Taxiway} (past the hold-short point) must be refused");
        Assert.Contains("beyond the hold short point", result.Message, StringComparison.OrdinalIgnoreCase);

        setup.Engine.TickOneSecond();
        Assert.Equal(committed, phase.ExitTaxiway, ignoreCase: true);
    }

    /// <summary>
    /// A late exit instruction naming an exit that turns off before the hold-short point is honoured as before. At SFO 28R the only
    /// exit before the 1L point is N, on both sides, so the retarget names the other side's (<c>EL N</c> / <c>ER N</c>).
    /// </summary>
    [Fact]
    public void Retarget_ToAnExitBeforeThePoint_IsAccepted()
    {
        Setup? setup = Spawn("C172", 75);
        if (setup is null)
        {
            return;
        }

        (RunwayExitPhase phase, string _) = CommittedOnApproachLeg(setup);
        double alongFt = setup.AlongFt;
        List<ExitAhead> exits = ExitsAhead(setup, setup.Aircraft.Position);
        string listing = Describe(setup, exits);
        ExitAhead? before = exits.FirstOrDefault(e => e.Fits && (e.HoldShort.Id != phase.TargetHoldShortNodeId) && (e.BranchAlongFt > alongFt + 600));
        Assert.True(before is not null, $"no other exit turns off before the point: {listing}");

        string command = $"{(before.Side == ExitSide.Left ? "EL" : "ER")} {before.Taxiway}";
        CommandResult result = setup.Engine.SendCommand(Callsign, command);
        output.WriteLine($"{command}: success={result.Success} message={result.Message}");
        Assert.True(result.Success, $"{command} (before the point) must be accepted: {result.Message}");

        setup.Engine.TickOneSecond();
        setup.Engine.TickOneSecond();
        Assert.Equal(before.HoldShort.Id, phase.TargetHoldShortNodeId);
    }

    /// <summary>
    /// The committed exit is occupied once the runway exit takes over and no other exit turns off before the point: the nose stops
    /// short of the point, the aircraft holds in <see cref="RunwayHoldingPhase"/>, and its only call is the holding-short one — the
    /// clearance is the approval to stop there, so no back-taxi request (AIM 4-3-21.a, 4-3-11.b.6).
    /// </summary>
    [Fact]
    public void CommittedExitOccupiedAfterHandOff_NoOtherExitBeforeThePoint_StopsShortAndHolds()
    {
        Setup? setup = Spawn("C172", 75);
        if (setup is null)
        {
            return;
        }

        LatLon early = PositionAlong(setup, 500);
        List<ExitAhead> exits = ExitsAhead(setup, early);
        string listing = Describe(setup, exits);
        List<ExitAhead> before = [.. exits.Where(e => e.Fits)];
        Assert.True(before.Count > 0, $"no exit turns off before the point: {listing}");

        // Every exit before the point is claimed (the committed one and its other-side twin), so none is left to take.
        for (int i = 0; i < before.Count; i++)
        {
            setup.Engine.World.AddAircraft(HoldingAfterExitBlocker($"BLK{i:D3}", setup.Runway, setup.Layout, before[i].HoldShort));
        }

        ExitAhead lastBefore = before[^1];
        PutOnRunway(setup, alongFt: lastBefore.BranchAlongFt - 10, speedKts: 12, new RunwayExitPhase(), new HoldingAfterExitPhase());
        // The landing committed this exit at the hand-off; its bar is claimed by the time the runway exit starts (OnStart's
        // committed-exit branch, occupied).
        setup.Aircraft.Phases!.ResolvedExit = Committed(lastBefore);
        DrainLines(setup);

        var lines = new List<string>();
        bool sawRunwayHolding = false;
        for (int t = 1; t <= 300; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            Assert.True(
                setup.NoseAlongFt <= setup.HoldShortFt,
                $"t+{t}s: nose {setup.NoseAlongFt - setup.HoldShortFt:F0} ft past the hold-short point in {current?.Name}"
            );
            sawRunwayHolding = sawRunwayHolding || (current is RunwayHoldingPhase);
            if ((current is RunwayHoldingPhase) || (current is HoldingAfterExitPhase))
            {
                break;
            }
        }

        foreach (string line in lines)
        {
            output.WriteLine($"  line: {line}");
        }

        Assert.True(sawRunwayHolding, $"expected the runway hold, ended in {setup.Aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Single(lines, l => l.Contains("holding short runway 1L", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("back-taxi", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("unable to exit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("no exit ahead", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A snapshot restored mid-<see cref="RunwayExitPhase"/> whose exit route cannot be rebuilt falls back to the centreline search,
    /// and that search still honours the carried limit: with only exits past the point ahead it takes none, and the aircraft stops
    /// short of the point.
    /// </summary>
    [Fact]
    public void RestoredExit_RebuildFallback_PicksNoExitPastThePoint()
    {
        Setup? setup = Spawn("C172", 75);
        if (setup is null)
        {
            return;
        }

        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        string listing = Describe(setup, exits);
        ExitAhead? lastBefore = exits.LastOrDefault(e => e.Fits);
        ExitAhead? firstPast = exits.FirstOrDefault(e => !e.Fits);
        Assert.True((lastBefore is not null) && (firstPast is not null), $"need exits on both sides of the limit: {listing}");

        double startAlongFt = lastBefore.BranchAlongFt + 600;
        List<ExitAhead> fromStart = ExitsAhead(setup, PositionAlong(setup, startAlongFt));
        Assert.True(fromStart.Count > 0 && !fromStart[0].Fits, $"from {startAlongFt:F0} ft the first exit ahead must be past the limit");

        GroundNode unconnected = setup
            .Layout.Nodes.Values.Where(node =>
                (node.Id != firstPast.Branch.Id) && firstPast.Branch.Edges.All(edge => edge.OtherNode(firstPast.Branch).Id != node.Id)
            )
            .OrderBy(node => node.Id)
            .First();
        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = true,
            ExitNodeId = firstPast.HoldShort.Id,
            ExitTaxiway = firstPast.Taxiway,
            RunwayId = "28R",
            ExitSpeed = 15.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = setup.Runway.TrueHeading.Degrees,
            ExitStateValue = (int)RunwayExitPhase.ExitState.FollowingExitPath,
            ExitWaypointNodeIds = [firstPast.Branch.Id, unconnected.Id],
        };
        var restored = RunwayExitPhase.FromSnapshot(dto, setup.Layout);
        PutOnRunway(setup, startAlongFt, speedKts: 15, restored, new HoldingAfterExitPhase());

        setup.Engine.TickOneSecond();
        Assert.True(
            restored.TargetHoldShortNodeId is null,
            $"the fallback search committed {restored.ExitTaxiway} (node {restored.TargetHoldShortNodeId}), past the LAHSO point"
        );

        for (int t = 2; t <= 400; t++)
        {
            setup.Engine.TickOneSecond();
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            Assert.True(setup.NoseAlongFt <= setup.HoldShortFt, $"t+{t}s: nose past the hold-short point in {current?.Name}");
            if ((current is RunwayHoldingPhase) || (current is HoldingAfterExitPhase))
            {
                break;
            }
        }

        Assert.IsType<RunwayHoldingPhase>(setup.Aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The predicted overrun (AIM 4-3-11.b.5/b.6): a jet rolling too fast to stop its nose by the point even at its max-effort rate
    /// says "unable to hold short of runway 1L" once, the instructor gets a warning line, the clearance is void, and the aircraft
    /// rolls on to an exit beyond the point without ever stopping on the crossing runway.
    /// </summary>
    [Fact]
    public void PredictedOverrun_CallsUnableOnce_VoidsTheLimit_AndExitsBeyond()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        const double speedKts = 110;
        double speedFps = speedKts * GeoMath.FeetPerNm / 3600.0;
        double maxRateFps2 = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet) * GeoMath.FeetPerNm / 3600.0;
        double maxEffortStopFt = speedFps * speedFps / (2.0 * maxRateFps2);

        // Placed so stopping the nose at the point would need a quarter more than the max-effort rate.
        double startAlongFt = setup.HoldShortFt - setup.NoseOffsetFt - (0.8 * maxEffortStopFt);
        output.WriteLine($"hold-short {setup.HoldShortFt:F0} ft, max-effort stop {maxEffortStopFt:F0} ft, start {startAlongFt:F0} ft");
        Assert.True(startAlongFt > 0);

        PutOnRunway(setup, startAlongFt, speedKts, new LandingPhase());
        DrainLines(setup);

        var lines = new List<string>();
        bool reachedHoldingAfterExit = false;
        bool sawRunwayHolding = false;
        for (int t = 1; t <= 400; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            sawRunwayHolding = sawRunwayHolding || (current is RunwayHoldingPhase);
            bool onTheRunway = (current is LandingPhase) || (current is RunwayExitPhase { IsOnCenterline: true });
            if (onTheRunway && (setup.NoseAlongFt >= setup.HoldShortFt))
            {
                Assert.True(
                    setup.Aircraft.GroundSpeed >= 1.0,
                    $"t+{t}s: stopped on the runway with the nose {setup.NoseAlongFt - setup.HoldShortFt:F0} ft past the point"
                );
            }

            if (current is HoldingAfterExitPhase)
            {
                reachedHoldingAfterExit = true;
                break;
            }
        }

        foreach (string line in lines)
        {
            output.WriteLine($"  line: {line}");
        }

        Assert.Single(lines, l => l.Contains("unable to hold short of runway 1L", StringComparison.OrdinalIgnoreCase));
        Assert.Single(lines, l => l.Contains("unable to hold short of RWY 1L", StringComparison.Ordinal));
        Assert.Null(setup.Aircraft.Phases!.LahsoHoldShort);
        Assert.False(sawRunwayHolding, "a voided clearance never holds at the point");
        Assert.True(reachedHoldingAfterExit, $"expected an exit beyond the point, ended in {setup.Aircraft.Phases.CurrentPhase?.Name}");
        Assert.True(setup.NoseAlongFt > setup.HoldShortFt, "the exit taken lies beyond the hold-short point");
    }

    /// <summary>
    /// R2: a jet already at its coast speed past the last exit before the point, some 3,000 ft from it, coasts until stopping the
    /// nose at the point less the 50 ft tick guard needs the softest rollout rate, then brakes continuously: its speed never rises
    /// once braking starts, and the nose comes to rest within the tick guard of the point (not hundreds of feet short of it).
    /// </summary>
    [Fact]
    public void FarFromThePoint_AtCoastSpeed_BrakesContinuously_AndStopsTheNoseAtThePoint()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        string listing = Describe(setup, exits);
        ExitAhead? lastBefore = exits.LastOrDefault(e => e.Fits);
        Assert.True(lastBefore is not null, $"no exit turns off before the point: {listing}");

        double coastKts = CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Jet);
        double startAlongFt = setup.HoldShortFt - setup.NoseOffsetFt - 2750;
        double noseToPointFt = setup.HoldShortFt - startAlongFt - setup.NoseOffsetFt;
        output.WriteLine(
            $"coast {coastKts:F0} kt, start {startAlongFt:F0} ft, nose {noseToPointFt:F0} ft from the point, last fitting exit branches at {lastBefore.BranchAlongFt:F0} ft"
        );
        Assert.True(noseToPointFt > 2700, "the start must be farther from the point than the soft-rate stop from coast speed");
        Assert.True(
            startAlongFt > lastBefore.BranchAlongFt,
            $"the start ({startAlongFt:F0} ft) must be past the last exit that fits before the point ({lastBefore.BranchAlongFt:F0} ft), so no exit is committed"
        );

        PutOnRunway(setup, startAlongFt, coastKts, new LandingPhase());
        double previousKts = coastKts;
        bool braking = false;
        for (int t = 1; t <= 400; t++)
        {
            setup.Engine.TickOneSecond();
            double kts = setup.Aircraft.GroundSpeed;
            if (braking)
            {
                Assert.True(kts <= previousKts + 0.05, $"t+{t}s: speed rose from {previousKts:F2} to {kts:F2} kt inside the stop");
            }

            braking = braking || (kts < coastKts - 0.5);
            previousKts = kts;
            if (setup.Aircraft.Phases?.CurrentPhase is RunwayHoldingPhase)
            {
                break;
            }
        }

        double shortFt = setup.HoldShortFt - setup.NoseAlongFt;
        output.WriteLine($"nose rests {shortFt:F0} ft short of the point in {setup.Aircraft.Phases?.CurrentPhase?.Name}");
        Assert.IsType<RunwayHoldingPhase>(setup.Aircraft.Phases?.CurrentPhase);
        Assert.InRange(shortFt, 0, 50 + 25);
    }

    /// <summary>
    /// R1: a jet with a committed exit before the point that it can reach at no more than its max-effort rate makes no "unable to
    /// hold short" call and takes that exit.
    /// </summary>
    [Fact]
    public void CommittedExitBeforeThePoint_ReachableAtMaxEffort_NoOverrunCall()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        string listing = Describe(setup, exits);
        ExitAhead? lastBefore = exits.LastOrDefault(e => e.Fits);
        Assert.True(lastBefore is not null, $"no exit turns off before the point: {listing}");

        PutOnRunway(setup, lastBefore.BranchAlongFt - 1500, 70, new LandingPhase());
        DrainLines(setup);
        var lines = new List<string>();
        bool reachedHoldingAfterExit = false;
        for (int t = 1; t <= 400; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            if (setup.Aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase)
            {
                reachedHoldingAfterExit = true;
                break;
            }
        }

        Assert.DoesNotContain(lines, l => l.Contains("unable to hold short", StringComparison.OrdinalIgnoreCase));
        Assert.True(reachedHoldingAfterExit, $"expected the exit before the point, ended in {setup.Aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(setup.NoseAlongFt < setup.HoldShortFt, "the exit taken lies before the hold-short point");
    }

    /// <summary>
    /// Finding 3: rolling on the centreline in <see cref="RunwayExitPhase"/> with no exit committed (every exit before the point
    /// claimed), a late <c>EXIT</c> naming a taxiway that leaves 28R only past the point is refused with the reason, and the aircraft
    /// still stops short of the point and holds there.
    /// </summary>
    [Fact]
    public void OnTheCentreline_LateExitPastThePoint_IsRefused_AndTheAircraftStillStopsAtThePoint()
    {
        Setup? setup = Spawn("C172", 75);
        if (setup is null)
        {
            return;
        }

        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        string listing = Describe(setup, exits);
        List<ExitAhead> before = [.. exits.Where(e => e.Fits)];
        Assert.True(before.Count > 0, $"no exit turns off before the point: {listing}");
        for (int i = 0; i < before.Count; i++)
        {
            setup.Engine.World.AddAircraft(HoldingAfterExitBlocker($"BLK{i:D3}", setup.Runway, setup.Layout, before[i].HoldShort));
        }

        ExitAhead? past = exits.FirstOrDefault(e =>
            !e.Fits && exits.Where(o => string.Equals(o.Taxiway, e.Taxiway, StringComparison.OrdinalIgnoreCase)).All(o => !o.Fits)
        );
        Assert.True(past is not null, $"no taxiway leaves 28R only past the point: {listing}");

        PutOnRunway(setup, alongFt: before[^1].BranchAlongFt + 200, speedKts: 20, new RunwayExitPhase(), new HoldingAfterExitPhase());
        setup.Engine.TickOneSecond();
        RunwayExitPhase phase = Assert.IsType<RunwayExitPhase>(setup.Aircraft.Phases!.CurrentPhase);
        Assert.Null(phase.TargetHoldShortNodeId);

        CommandResult result = setup.Engine.SendCommand(Callsign, $"EXIT {past.Taxiway}");
        output.WriteLine($"EXIT {past.Taxiway}: success={result.Success} message={result.Message}");
        Assert.False(result.Success, $"EXIT {past.Taxiway} (past the hold-short point) must be refused");
        Assert.Contains("beyond the hold short point", result.Message, StringComparison.OrdinalIgnoreCase);

        for (int t = 2; t <= 300; t++)
        {
            setup.Engine.TickOneSecond();
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            Assert.True(setup.NoseAlongFt <= setup.HoldShortFt, $"t+{t}s: nose past the hold-short point in {current?.Name}");
            if ((current is RunwayHoldingPhase) || (current is HoldingAfterExitPhase))
            {
                break;
            }
        }

        Assert.IsType<RunwayHoldingPhase>(setup.Aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// Finding 2 / R3: in <see cref="RunwayExitPhase"/> with every exit before the point claimed and too fast for even max effort to
    /// stop the nose by the point, the crew calls "unable to hold short of runway 1L" once, the clearance is void, the aircraft never
    /// enters the LAHSO hold, never comes to rest with its nose or tail between 1L's pavement edges, and takes an exit beyond.
    /// </summary>
    [Fact]
    public void RunwayExit_CannotStopByThePoint_CallsUnableOnce_AndNeverRestsOnTheCrossingRunway()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        string listing = Describe(setup, exits);
        List<ExitAhead> before = [.. exits.Where(e => e.Fits)];
        for (int i = 0; i < before.Count; i++)
        {
            setup.Engine.World.AddAircraft(HoldingAfterExitBlocker($"BLK{i:D3}", setup.Runway, setup.Layout, before[i].HoldShort));
        }

        const double speedKts = 60;
        double speedFps = speedKts * GeoMath.FeetPerNm / 3600.0;
        double maxRateFps2 = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet) * GeoMath.FeetPerNm / 3600.0;
        double maxEffortStopFt = speedFps * speedFps / (2.0 * maxRateFps2);
        double startAlongFt = setup.HoldShortFt - setup.NoseOffsetFt - (0.8 * maxEffortStopFt);
        (double nearEdgeFt, double farEdgeFt) = CrossingEdgesAlongFt(setup);
        output.WriteLine($"{listing}; start {startAlongFt:F0} ft; 1L pavement {nearEdgeFt:F0}-{farEdgeFt:F0} ft");
        Assert.True(before.Count > 0, $"no exit turns off before the point: {listing}");
        Assert.True(startAlongFt > before[^1].BranchAlongFt, "the start must be past the last exit before the point");

        PutOnRunway(setup, startAlongFt, speedKts, new RunwayExitPhase(), new HoldingAfterExitPhase());
        // The landing committed the last exit before the point at the hand-off; its bar is claimed by the time the runway exit
        // starts (OnStart's committed-exit branch, occupied).
        setup.Aircraft.Phases!.ResolvedExit = Committed(before[^1]);
        DrainLines(setup);
        var lines = new List<string>();
        bool sawRunwayHolding = false;
        bool reachedHoldingAfterExit = false;
        for (int t = 1; t <= 400; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            sawRunwayHolding = sawRunwayHolding || (current is RunwayHoldingPhase);
            AssertNotRestingOnTheCrossingRunway(setup, current, t, nearEdgeFt, farEdgeFt);
            if (current is HoldingAfterExitPhase)
            {
                reachedHoldingAfterExit = true;
                break;
            }
        }

        foreach (string line in lines)
        {
            output.WriteLine($"  line: {line}");
        }

        Assert.Single(lines, l => l.Contains("unable to hold short of runway 1L", StringComparison.OrdinalIgnoreCase));
        Assert.Null(setup.Aircraft.Phases!.LahsoHoldShort);
        Assert.False(sawRunwayHolding, "a voided clearance never holds at the point");
        Assert.True(reachedHoldingAfterExit, $"expected an exit beyond the point, ended in {setup.Aircraft.Phases.CurrentPhase?.Name}");
    }

    /// <summary>
    /// The voided clearance's crossing runway survives a snapshot of <see cref="RunwayExitPhase"/>: a phase restored after the overrun
    /// call carries it, and with every exit ahead claimed its no-exit stop still never rests on 1L.
    /// </summary>
    [Fact]
    public void RestoredExit_AfterTheOverrunCall_KeepsTheCrossingRunwayKeepOut()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        (double nearEdgeFt, double farEdgeFt) = CrossingEdgesAlongFt(setup);
        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        for (int i = 0; i < exits.Count; i++)
        {
            setup.Engine.World.AddAircraft(HoldingAfterExitBlocker($"BLK{i:D3}", setup.Runway, setup.Layout, exits[i].HoldShort));
        }

        var dto = new RunwayExitPhaseDto
        {
            Status = (int)PhaseStatus.Active,
            ElapsedSeconds = 4.0,
            ReachedExitNode = false,
            RunwayId = "28R",
            ExitSpeed = 15.0,
            TimeSinceLastLog = 0.0,
            RunwayHeadingDeg = setup.Runway.TrueHeading.Degrees,
            ExitStateValue = (int)RunwayExitPhase.ExitState.RollingOnCenterline,
            VoidedLahsoCrossingRunwayId = "1L",
        };
        var restored = RunwayExitPhase.FromSnapshot(dto, setup.Layout);
        Assert.Equal("1L", restored.VoidedLahsoCrossingRunwayId);
        Assert.Equal("1L", Assert.IsType<RunwayExitPhaseDto>(restored.ToSnapshot()).VoidedLahsoCrossingRunwayId);

        PutOnRunway(setup, nearEdgeFt - setup.NoseOffsetFt - 150, 30, restored, new HoldingAfterExitPhase());
        setup.Aircraft.Phases!.LahsoHoldShort = null;
        for (int t = 1; t <= 300; t++)
        {
            setup.Engine.TickOneSecond();
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            AssertNotRestingOnTheCrossingRunway(setup, current, t, nearEdgeFt, farEdgeFt);
            if ((current is not RunwayExitPhase) || (setup.Aircraft.GroundSpeed < 0.1))
            {
                break;
            }
        }

        output.WriteLine($"rests {setup.AlongFt:F0} ft along at {setup.Aircraft.GroundSpeed:F1} kt; 1L pavement {nearEdgeFt:F0}-{farEdgeFt:F0} ft");

        Assert.IsType<RunwayExitPhase>(setup.Aircraft.Phases?.CurrentPhase);
        Assert.True(setup.Aircraft.GroundSpeed < 0.1, $"expected the aircraft at rest, at {setup.Aircraft.GroundSpeed:F1} kt");
        Assert.True(
            (setup.NoseAlongFt < nearEdgeFt) || (setup.NoseAlongFt > farEdgeFt),
            $"nose rests on 1L: nose {setup.NoseAlongFt:F0} ft along, 1L pavement {nearEdgeFt:F0}-{farEdgeFt:F0} ft"
        );
    }

    /// <summary>
    /// Finding 4: one exit judged against the LAHSO limit by the runway exit and by the landing gives the same verdict when the walk
    /// that found it ran on a heading two degrees off the runway's: both measure the exit angle against the runway's true heading.
    /// The limit is set midway between the run-ons the two headings would give, so a judge measuring on the walk's heading disagrees.
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(-2.0)]
    public void OneExit_JudgedByBothPhases_GivesOneVerdict_WhenTheHeadingIsOffTheRunway(double headingOffsetDeg)
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        var walkHeading = new TrueHeading(setup.Runway.TrueHeading.Degrees + headingOffsetDeg);
        LatLon from = PositionAlong(setup, 500);
        AirportGroundLayout.CenterlineExitResult? angled = null;
        var seen = new HashSet<int>();
        for (int walk = 0; walk < 64 && angled is null; walk++)
        {
            AirportGroundLayout.CenterlineExitResult? hit = setup.Layout.FindOnSidePreferredExit(
                from.Lat,
                from.Lon,
                walkHeading,
                setup.Runway.Designator,
                preference: null,
                sidePref: null,
                excludeBranchPoints: null,
                excludeHoldShortNodes: seen,
                filter: _ => AirportGroundLayout.CandidateVerdict.Accept,
                maxIterations: AirportGroundLayout.DefaultMaxIterations
            );
            if ((hit is not { } exit) || !seen.Add(exit.HoldShort.Id))
            {
                break;
            }

            double onRunway = setup.Layout.MeasureExitAngle(exit.Path, exit.HoldShort, exit.Taxiway, setup.Runway.TrueHeading);
            if ((onRunway < 80) && (Math.Abs(onRunway - exit.ExitAngle) > 0.2))
            {
                angled = exit;
            }
        }

        Assert.True(angled is not null, "no angled exit whose measured angle depends on the walk's heading");
        AirportGroundLayout.CenterlineExitResult candidate = angled.Value;
        double runwayAngle = setup.Layout.MeasureExitAngle(candidate.Path, candidate.HoldShort, candidate.Taxiway, setup.Runway.TrueHeading);
        double branchNm = GeoMath.AlongTrackDistanceNm(candidate.Path[0].Position, setup.Threshold, setup.Runway.TrueHeading);
        double runOnRunwayNm = LandingPhase.TurnOffRunOnNm(runwayAngle, setup.Runway.WidthFt);
        double runOnWalkNm = LandingPhase.TurnOffRunOnNm(candidate.ExitAngle, setup.Runway.WidthFt);
        double limitNm = branchNm + ((runOnRunwayNm + runOnWalkNm) / 2.0);
        output.WriteLine($"{candidate.Taxiway}: angle {runwayAngle:F1} on the runway, {candidate.ExitAngle:F1} on the walk");

        bool landing = LandingPhase.TurnOffFitsBefore(branchNm, runwayAngle, setup.Runway.WidthFt, limitNm);
        bool runwayExit = RunwayExitPhase.FitsLahsoLimit(setup.Aircraft, setup.Layout, candidate, limitNm);
        Assert.Equal(landing, runwayExit);
    }

    /// <summary>
    /// R3 across the hand-off: the landing makes the overrun call, then hands off to the <see cref="RunwayExitPhase"/> the runner adds
    /// once the landing completes, whose exits beyond 1L are all claimed. The added phase carries the voided crossing runway, and its
    /// no-exit stop never brings the aircraft to rest with its nose or tail between 1L's pavement edges.
    /// </summary>
    [Fact]
    public void OverrunInTheLanding_CarriesTheCrossingRunwayToTheAddedRunwayExit_AndNeverRestsOnIt()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        (double nearEdgeFt, double farEdgeFt) = CrossingEdgesAlongFt(setup);
        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        string listing = Describe(setup, exits);
        List<ExitAhead> beyond = [.. exits.Where(e => e.BranchAlongFt > farEdgeFt)];
        Assert.True(beyond.Count > 0, $"no exit beyond 1L: {listing}");
        for (int i = 0; i < beyond.Count; i++)
        {
            setup.Engine.World.AddAircraft(HoldingAfterExitBlocker($"BLK{i:D3}", setup.Runway, setup.Layout, beyond[i].HoldShort));
        }

        const double speedKts = 110;
        double speedFps = speedKts * GeoMath.FeetPerNm / 3600.0;
        double maxRateFps2 = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet) * GeoMath.FeetPerNm / 3600.0;
        double startAlongFt = setup.HoldShortFt - setup.NoseOffsetFt - (0.8 * speedFps * speedFps / (2.0 * maxRateFps2));
        PutOnRunway(setup, startAlongFt, speedKts, new LandingPhase());

        // The crew asked for the first exit beyond 1L: the landing's named-taxiway search plans it without regard to its claimed
        // bar, and the runway exit, the last moment, finds it and every other exit ahead claimed.
        setup.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = beyond[0].Taxiway, Side = beyond[0].Side };
        DrainLines(setup);

        var lines = new List<string>();
        RunwayExitPhase? handedOff = null;
        for (int t = 1; t <= 400; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            Phase? current = setup.Aircraft.Phases?.CurrentPhase;
            handedOff ??= current as RunwayExitPhase;
            AssertNotRestingOnTheCrossingRunway(setup, current, t, nearEdgeFt, farEdgeFt);
            if ((current is not LandingPhase and not RunwayExitPhase) || ((handedOff is not null) && (setup.Aircraft.GroundSpeed < 0.1)))
            {
                break;
            }
        }

        output.WriteLine($"rests {setup.AlongFt:F0} ft along in {setup.Aircraft.Phases?.CurrentPhase?.Name}; 1L {nearEdgeFt:F0}-{farEdgeFt:F0} ft");
        Assert.Single(lines, l => l.Contains("unable to hold short of runway 1L", StringComparison.OrdinalIgnoreCase));
        Assert.True(handedOff is not null, "expected the landing to hand off to the runway exit");
        Assert.Equal("1L", handedOff.VoidedLahsoCrossingRunwayId);
    }

    /// <summary>
    /// R3: a landing at rest with its nose past the hold-short point never enters the LAHSO hold; that stop runs the overrun handling
    /// instead (one "unable to hold short of runway 1L" call, the clearance void).
    /// </summary>
    [Fact]
    public void LandingAtRest_NosePastThePoint_CallsUnable_AndNeverEntersTheHold()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        PutOnRunway(setup, setup.HoldShortFt - setup.NoseOffsetFt + 30, 0.3, new LandingPhase());
        DrainLines(setup);
        var lines = new List<string>();
        bool sawRunwayHolding = false;
        for (int t = 1; t <= 20; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            sawRunwayHolding = sawRunwayHolding || (setup.Aircraft.Phases?.CurrentPhase is RunwayHoldingPhase);
        }

        foreach (string line in lines)
        {
            output.WriteLine($"  line: {line}");
        }

        Assert.False(sawRunwayHolding, "a nose past the point never enters the LAHSO hold");
        Assert.Single(lines, l => l.Contains("unable to hold short of runway 1L", StringComparison.OrdinalIgnoreCase));
        Assert.Null(setup.Aircraft.Phases!.LahsoHoldShort);
    }

    /// <summary>
    /// Under a live LAHSO limit a runway exit at rest well short of the point (300 ft) with no exit before it enters the LAHSO hold
    /// with the holding-short call, never the no-exit or back-taxi call.
    /// </summary>
    [Fact]
    public void RunwayExit_AtRestFarShortOfThePoint_NoExitBeforeIt_HoldsWithTheHoldingShortCall()
    {
        Setup? setup = Spawn("C172", 75);
        if (setup is null)
        {
            return;
        }

        List<ExitAhead> exits = ExitsAhead(setup, PositionAlong(setup, 500));
        string listing = Describe(setup, exits);
        List<ExitAhead> before = [.. exits.Where(e => e.Fits)];
        Assert.True(before.Count > 0, $"no exit turns off before the point: {listing}");
        for (int i = 0; i < before.Count; i++)
        {
            setup.Engine.World.AddAircraft(HoldingAfterExitBlocker($"BLK{i:D3}", setup.Runway, setup.Layout, before[i].HoldShort));
        }

        double startAlongFt = setup.HoldShortFt - setup.NoseOffsetFt - 300;
        Assert.True(startAlongFt > before[^1].BranchAlongFt, "the start must be past the last exit before the point");
        PutOnRunway(setup, startAlongFt, 0, new RunwayExitPhase(), new HoldingAfterExitPhase());
        DrainLines(setup);
        var lines = new List<string>();
        for (int t = 1; t <= 10; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            if (setup.Aircraft.Phases?.CurrentPhase is RunwayHoldingPhase)
            {
                break;
            }
        }

        foreach (string line in lines)
        {
            output.WriteLine($"  line: {line}");
        }

        Assert.IsType<RunwayHoldingPhase>(setup.Aircraft.Phases?.CurrentPhase);
        Assert.Single(lines, l => l.Contains("holding short runway 1L", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("back-taxi", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("no exit ahead", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// YAAT-1942: a forced landing (<c>CLANDF</c>) issued after a LAHSO clearance stays a planned rollout. The forced exit search
    /// never runs while the clearance is live — it has no LAHSO filter and commits the last exit on the runway, well past the point —
    /// and once a predicted overrun voids the clearance the rollout keeps the planned braking and the crossing-runway keep-out, so the
    /// aircraft never back-tracks across 1L and the runway exit it hands off to is never a forced one (which would commit an exit
    /// behind the aircraft and re-cross the LAHSO runway).
    /// </summary>
    [Fact]
    public void Clandf_AfterLahso_StaysAPlannedRollout_AndNeverBacktracksAcross1L()
    {
        Setup? setup = Spawn("E75L", 140);
        if (setup is null)
        {
            return;
        }

        CommandResult clandf = setup.Engine.SendCommand(Callsign, "CLANDF");
        Assert.True(clandf.Success, $"CLANDF failed: {clandf.Message}");
        Assert.True(setup.Aircraft.Phases?.ForceLanding == true, "the forced landing must be in force");

        LandingPhase landing = setup.Aircraft.Phases!.Phases.OfType<LandingPhase>().Single();
        double limitNm = LandingPhase.LahsoStopLimitNm(setup.Target.DistFromThresholdNm, setup.Aircraft.AircraftType);
        (double nearEdgeFt, double farEdgeFt) = CrossingEdgesAlongFt(setup);
        output.WriteLine($"hold-short point {setup.HoldShortFt:F0} ft; 1L pavement {nearEdgeFt:F0}-{farEdgeFt:F0} ft");
        DrainLines(setup);

        var lines = new List<string>();
        bool sawLanding = false;
        bool sawOverrunCall = false;
        double previousAlongFt = setup.AlongFt;
        Phase? current = null;
        for (int t = 1; t <= 420; t++)
        {
            setup.Engine.TickOneSecond();
            lines.AddRange(DrainLines(setup));
            current = setup.Aircraft.Phases?.CurrentPhase;
            sawLanding = sawLanding || (current is LandingPhase);

            // While the clearance is live, any committed exit has to turn off at or before the hold-short point; the forced search
            // commits the last exit on the runway (past 1L) and so defeats the stop at the point.
            if ((setup.Aircraft.Phases?.LahsoHoldShort is not null) && (landing.CandidateExit is { } candidate))
            {
                double branchNm = GeoMath.AlongTrackDistanceNm(candidate.BranchPointNode.Position, setup.Threshold, setup.Runway.TrueHeading);
                double angle = setup.Layout.MeasureExitAngle(
                    candidate.Path,
                    candidate.HoldShortNode,
                    candidate.TaxiwayName,
                    setup.Runway.TrueHeading
                );
                Assert.True(
                    LandingPhase.TurnOffFitsBefore(branchNm, angle, setup.Runway.WidthFt, limitNm),
                    $"t+{t}s: the landing committed {candidate.TaxiwayName} at {branchNm * GeoMath.FeetPerNm:F0} ft, past the 1L hold-short limit"
                );
            }

            sawOverrunCall = sawOverrunCall || lines.Any(l => l.Contains("unable to hold short of runway 1L", StringComparison.OrdinalIgnoreCase));

            // A voided clearance keeps rolling forward: the back-track (an exit behind the aircraft) is the re-crossing of 1L.
            if (sawOverrunCall && (current is LandingPhase or RunwayExitPhase))
            {
                Assert.True(
                    setup.AlongFt >= previousAlongFt - 0.5,
                    $"t+{t}s: the centroid moved back from {previousAlongFt:F0} to {setup.AlongFt:F0} ft in {current.Name}"
                );
            }

            previousAlongFt = setup.AlongFt;
            AssertNotRestingOnTheCrossingRunway(setup, current, t, nearEdgeFt, farEdgeFt);

            if (sawLanding && (current is not LandingPhase))
            {
                Assert.False(setup.Aircraft.Phases?.ForcedRollout == true, $"t+{t}s: the runway exit was handed a forced rollout in {current?.Name}");
            }

            if ((current is RunwayHoldingPhase) && (setup.Aircraft.GroundSpeed < 0.5))
            {
                break;
            }

            if (sawLanding && (current is not LandingPhase and not RunwayExitPhase and not RunwayHoldingPhase))
            {
                break;
            }
        }

        foreach (string line in lines)
        {
            output.WriteLine($"  line: {line}");
        }
        output.WriteLine($"ended in {current?.Name} at {setup.AlongFt:F0} ft along; 1L pavement {nearEdgeFt:F0}-{farEdgeFt:F0} ft");

        if (current is RunwayHoldingPhase)
        {
            Assert.True(
                setup.NoseAlongFt <= setup.HoldShortFt,
                $"the runway hold rests with the nose {setup.NoseAlongFt - setup.HoldShortFt:F0} ft past the hold-short point"
            );
            Assert.False(sawOverrunCall, "the hold at the point is under the live clearance, not a voided one");
            return;
        }

        Assert.True(sawOverrunCall, $"left the runway with no overrun call (ended in {current?.Name})");
        Assert.True(setup.AlongFt > farEdgeFt, $"the exit taken rests at {setup.AlongFt:F0} ft along, not beyond 1L's pavement ({farEdgeFt:F0} ft)");
    }

    /// <summary>1L's pavement along 28R (ft from the landing threshold): its centreline crossing ± half its width over the crossing angle.</summary>
    private static (double NearFt, double FarFt) CrossingEdgesAlongFt(Setup setup)
    {
        RunwayInfo? crossing = NavigationDatabase.Instance.GetRunway("SFO", "1L");
        Assert.NotNull(crossing);
        (double Lat, double Lon, double FirstDistFromThresholdNm, double SecondDistFromThresholdNm)? x =
            RunwayIntersectionCalculator.FindIntersection(setup.Runway, crossing);
        Assert.NotNull(x);
        double centreFt =
            GeoMath.AlongTrackDistanceNm(new LatLon(x.Value.Lat, x.Value.Lon), setup.Threshold, setup.Runway.TrueHeading) * GeoMath.FeetPerNm;
        double sin = Math.Abs(Math.Sin((crossing.TrueHeading.Degrees - setup.Runway.TrueHeading.Degrees) * Math.PI / 180.0));
        double halfFt = crossing.WidthFt / 2.0 / sin;
        return (centreFt - halfFt, centreFt + halfFt);
    }

    /// <summary>Fails when the aircraft is at rest on the runway with its nose or tail between the crossing runway's edges.</summary>
    private static void AssertNotRestingOnTheCrossingRunway(Setup setup, Phase? current, int t, double nearEdgeFt, double farEdgeFt)
    {
        bool onTheRunway = (current is LandingPhase) || (current is RunwayExitPhase { IsOnCenterline: true }) || (current is RunwayHoldingPhase);
        double tailFt = setup.AlongFt - setup.NoseOffsetFt;
        bool overlaps = (setup.NoseAlongFt > nearEdgeFt) && (tailFt < farEdgeFt);
        Assert.False(
            onTheRunway && overlaps && (setup.Aircraft.GroundSpeed < 0.5),
            $"t+{t}s: at rest on 1L, nose {setup.NoseAlongFt:F0} ft / tail {tailFt:F0} ft along, 1L {nearEdgeFt:F0}-{farEdgeFt:F0} ft"
        );
    }

    private static LatLon PositionAlong(Setup setup, double alongFt)
    {
        (double lat, double lon) = GeoMath.ProjectPointRaw(
            setup.Threshold.Lat,
            setup.Threshold.Lon,
            setup.Runway.TrueHeading.Degrees,
            alongFt / GeoMath.FeetPerNm
        );
        return new LatLon(lat, lon);
    }
}
