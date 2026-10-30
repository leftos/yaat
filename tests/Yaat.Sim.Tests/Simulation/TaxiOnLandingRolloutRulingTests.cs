using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Oracle;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A TAXI or TAXIAUTO sent while an arrival is on its landing rollout (YAAT-334) is an instructed exit at the route's first
/// turn-off from the landing runway plus a taxi route stored on <see cref="PhaseList.TaxiRoute"/>, judged up front at the firm
/// braking limit, refused with the crew's "unable" when the turn-off cannot be made, and started once the exit is complete
/// without the aircraft stopping at the landing runway's own bar.
/// </summary>
public sealed class TaxiOnLandingRolloutRulingTests
{
    private const string Arrival = "ARR1";
    private const int DispatchWindowSeconds = 60;
    private const int AfterDispatchSeconds = 150;

    /// <summary>How long after an exit instruction the rollout's committed exit is compared between a live and a restored engine.</summary>
    private const int ExitChoiceSeconds = 5;

    /// <summary>A TAXI via V, SFO 19L's first turn-off after touchdown: too close to make at touchdown, behind two seconds later.</summary>
    private const string TaxiViaV = "TAXI V";

    private readonly ITestOutputHelper _output;

    public TaxiOnLandingRolloutRulingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>The arrival on the ground in its landing rollout, and the TAXI the rollout first accepted, a second at a time.</summary>
    private sealed record Accepted(ShortFinalArrival.Spawned Spawned, CommandResult Result, double DispatchKts);

    [Fact]
    public void TaxiAuto_OnceTheTurnOffIsMakeable_IsStoredBehindAnInstructedExit()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        TaxiRoute stored = aircraft.Phases!.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        ExitPreference exit = aircraft.Phases.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        Assert.NotNull(exit.Taxiway);
        Assert.NotNull(exit.Side);
        Assert.Equal(exit.Taxiway, stored.Segments[0].TaxiwayName, StringComparer.OrdinalIgnoreCase);
        GroundNode first = aircraft.Ground.Layout!.Nodes[stored.Segments[0].FromNodeId];
        Assert.Equal(GroundNodeType.RunwayHoldShort, first.Type);

        string side = exit.Side == ExitSide.Left ? "left" : "right";
        string message = accepted.Result.Message ?? "";
        Assert.StartsWith($"Exit {side} at {exit.Taxiway}, taxi via ", message, StringComparison.Ordinal);
        Assert.NotNull(accepted.Result.PilotReadback);
        Assert.Equal(CanonicalCommandType.TaxiAuto, accepted.Result.PilotReadback!.Command);
    }

    /// <summary>
    /// A TAXIAUTO on the rollout turns off at the first exit the aircraft can make at the firm limit, never the pathfinder's
    /// nearest turn-off (V at SFO 19L, too close to make at touchdown): accepted at once with that exit instructed, and ticked
    /// on the aircraft reaches the route with no fillet rounded faster than its taxi speed plus a margin.
    /// </summary>
    [Theory]
    [InlineData("SFO", "19L", "A320", "TAXIAUTO $1", 0)]
    [InlineData("SFO", "19L", "A320", "TAXIAUTO $1", 4)]
    [InlineData("OAK", "28R", "B738", "TAXIAUTO 30", 0)]
    public void TaxiAuto_OnTheRollout_RoutesViaTheFirstMakeableExit(
        string airport,
        string runway,
        string type,
        string command,
        int secondsAfterTouchdown
    )
    {
        if (Touchdown(airport, runway, type) is not { } spawned)
        {
            return;
        }

        for (int second = 0; second < secondsAfterTouchdown; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        AircraftState aircraft = spawned.Aircraft;
        LandingPhase landing = Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        IReadOnlyList<ExitAheadDto> makeable = landing.ListExitsAhead(aircraft) ?? throw new Xunit.Sdk.XunitException("no exits-ahead list");
        ExitAheadDto firstMakeable = makeable.MinBy(r => r.DistanceFt) ?? throw new Xunit.Sdk.XunitException("no exit ahead to make");
        WriteExitsAhead(aircraft);
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, command);
        _output.WriteLine($"{command} at {aircraft.GroundSpeed:F1} kt -> {taxi.Success} {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        ExitPreference exit = aircraft.Phases!.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        TaxiRoute stored = aircraft.Phases.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        _output.WriteLine($"stored from node {stored.Segments[0].FromNodeId}: {string.Join(", ", stored.HoldShortPoints.Select(h => h.TargetName))}");
        Assert.Equal(firstMakeable.Taxiway, exit.Taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(makeable, r => string.Equals(r.Taxiway, exit.Taxiway, StringComparison.OrdinalIgnoreCase) && (r.Side == exit.Side));
        Assert.NotEqual("V", exit.Taxiway, StringComparer.OrdinalIgnoreCase);

        bool started = false;
        double fastestOnFilletKts = 0;
        for (int second = 1; second <= 120; second++)
        {
            spawned.Engine.TickOneSecond();
            started |= aircraft.Ground.AssignedTaxiRoute is not null;
            if (aircraft.Ground.LastNavDiag is { OnArc: true })
            {
                fastestOnFilletKts = Math.Max(fastestOnFilletKts, aircraft.GroundSpeed);
            }
        }

        Assert.True(started, $"the stored route never started: {aircraft.Phases?.CurrentPhase?.Name}");
        double ceilingKts = CategoryPerformance.TaxiSpeed(AircraftCategorization.Categorize(type)) + 5.0;
        Assert.True(fastestOnFilletKts <= ceilingKts, $"fillet at {fastestOnFilletKts:F1} kt, over {ceilingKts:F0} kt");
    }

    /// <summary>The exact controller-facing message for one accepted rollout TAXIAUTO: the implied exit first, then the taxi.</summary>
    [Fact]
    public void TaxiAuto_Accepted_ReadsTheExitBackFirst()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        Assert.Equal("Exit right at F1, taxi via A M1 $1", accepted.Result.Message);
    }

    /// <summary>
    /// At touchdown a TAXI via V, the first turn-off, is too close to make at the firm limit: the whole TAXI is refused with the
    /// crew's "unable V.", nothing is stored, and the rollout carries on to its own exit and holds clear.
    /// </summary>
    [Fact]
    public void Taxi_AtTouchdownViaATurnOffTooClose_IsRefusedUnableAndTheRolloutHoldsClear()
    {
        if (Touchdown("SFO", "19L", "A320") is not { } spawned)
        {
            return;
        }

        CommandResult taxi = spawned.Engine.SendCommand(Arrival, TaxiViaV);
        _output.WriteLine($"{TaxiViaV} at {spawned.Aircraft.GroundSpeed:F1} kt -> {taxi.Success} {taxi.Message}");
        Assert.False(taxi.Success);
        Assert.NotNull(taxi.PilotUnable);
        Assert.Equal("unable V.", taxi.Message);
        Assert.IsType<LandingPhase>(spawned.Aircraft.Phases?.CurrentPhase);
        Assert.Null(spawned.Aircraft.Phases?.TaxiRoute);
        Assert.Null(spawned.Aircraft.Phases?.RequestedExit);

        TickUntilHoldingAfterExit(spawned);
    }

    /// <summary>A TAXI whose turn-off the aircraft has already rolled past is refused "unable, no {twy} ahead"; never a turn back.</summary>
    [Fact]
    public void Taxi_ViaATurnOffAlreadyPassed_IsRefusedNoExitAhead()
    {
        if (Touchdown("SFO", "19L", "A320") is not { } spawned)
        {
            return;
        }

        CommandResult early = spawned.Engine.SendCommand(Arrival, TaxiViaV);
        Assert.False(early.Success, $"{TaxiViaV} at touchdown was accepted: {early.Message}");
        spawned.Engine.TickOneSecond();
        spawned.Engine.TickOneSecond();

        // The route from the aircraft now turns back down the runway to V.
        Assert.IsType<LandingPhase>(spawned.Aircraft.Phases?.CurrentPhase);
        CommandResult late = spawned.Engine.SendCommand(Arrival, TaxiViaV);
        _output.WriteLine($"{TaxiViaV} after passing V -> {late.Success} {late.Message}");
        Assert.False(late.Success);
        Assert.Equal("Unable, no V ahead", late.Message);
        Assert.NotNull(late.PilotUnable);
        Assert.Null(spawned.Aircraft.Phases?.TaxiRoute);
    }

    /// <summary>A TAXI sent while the arrival is still airborne in its landing phase is refused with the phase's own text.</summary>
    [Fact]
    public void Taxi_AirborneInTheLandingPhase_IsRefusedWithThePhaseText()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand("SFO", "19L", "A320", Arrival, 1.0) is not { } spawned)
        {
            return;
        }

        int landing = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => !spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase is LandingPhase),
            120,
            null
        );
        Assert.True(landing > 0, "never airborne in the landing phase");

        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXIAUTO $1");
        Assert.False(taxi.Success);
        Assert.Equal("aircraft is committed to landing on stabilized approach / flare; only GA, EL/ER/EXIT, or DEL apply", taxi.Message);
    }

    /// <summary>
    /// The accepted route starts once the exit is complete without the aircraft stopping: from dispatch until the taxi route
    /// is under way it never reaches 0 kt, it never holds after the exit or short of the runway it landed on, and no fillet is
    /// rounded faster than the jet taxi speed plus a margin.
    /// </summary>
    [Fact]
    public void TaxiAuto_Accepted_RollsIntoTheRouteWithoutStopping()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        TaxiRoute stored = aircraft.Phases!.TaxiRoute!;
        string landedRunway = accepted.Spawned.Runway.Designator;
        int onRouteFor = 0;
        double slowestKts = double.MaxValue;
        double fastestOnFilletKts = 0;
        for (int second = 1; (second <= AfterDispatchSeconds) && (onRouteFor < 10); second++)
        {
            accepted.Spawned.Engine.TickOneSecond();
            Phase? phase = aircraft.Phases?.CurrentPhase;
            Assert.IsNotType<HoldingAfterExitPhase>(phase);
            if (phase is HoldingShortPhase hold)
            {
                Assert.False(
                    RunwayIdentifier.Parse(hold.HoldShort.TargetName ?? "").Overlaps(RunwayIdentifier.Parse(landedRunway)),
                    $"held short of {hold.HoldShort.TargetName}, the runway it landed on, at t+{second}"
                );
            }

            if (aircraft.Ground.LastNavDiag is { OnArc: true })
            {
                fastestOnFilletKts = Math.Max(fastestOnFilletKts, aircraft.GroundSpeed);
            }

            // Until the route starts; a hold-short on the route after that (F1 A M1 crosses 19R) is the route's own stop.
            if (aircraft.Ground.AssignedTaxiRoute is null)
            {
                slowestKts = Math.Min(slowestKts, aircraft.GroundSpeed);
            }
            else
            {
                Assert.Same(stored, aircraft.Ground.AssignedTaxiRoute);
                onRouteFor++;
            }
        }

        Assert.True(onRouteFor >= 10, $"the stored route never got under way: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Null(aircraft.Phases?.TaxiRoute);
        Assert.True(slowestKts > 1.0, $"the aircraft stopped ({slowestKts:F1} kt) between the dispatch and the route");
        Assert.True(fastestOnFilletKts <= CategoryPerformance.TaxiSpeed(AircraftCategory.Jet) + 5.0, $"fillet at {fastestOnFilletKts:F1} kt");
    }

    /// <summary>The stored route survives a snapshot round trip every second through the rollout; with no layout it is dropped.</summary>
    [Fact]
    public void TaxiAuto_Accepted_StoredRouteSurvivesASnapshotEverySecond()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        int checkedSeconds = 0;
        while (aircraft.Phases?.TaxiRoute is { } stored)
        {
            AircraftSnapshotDto dto = aircraft.ToSnapshot();
            var restored = AircraftState.FromSnapshot(dto, layout);
            Assert.Equal(RouteNodes(stored), RouteNodes(restored.Phases?.TaxiRoute ?? throw new Xunit.Sdk.XunitException("route lost")));
            Assert.Null(AircraftState.FromSnapshot(dto, groundLayout: null).Phases?.TaxiRoute);
            accepted.Spawned.Engine.TickOneSecond();
            checkedSeconds++;
            Assert.True(checkedSeconds < AfterDispatchSeconds, "the stored route never started");
        }

        Assert.True(checkedSeconds > 0);
        Assert.NotNull(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>A different exit after the TAXI was accepted replaces the taxi plan: the aircraft exits there and holds clear.</summary>
    [Fact]
    public void Exit_AfterTheTaxiWasAccepted_DropsTheStoredRoute()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        LandingPhase landing = Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        string implied = aircraft.Phases!.RequestedExit!.Taxiway!;
        ExitAheadDto other =
            landing.ListExitsAhead(aircraft)?.FirstOrDefault(r => !string.Equals(r.Taxiway, implied, StringComparison.OrdinalIgnoreCase))
            ?? throw new Xunit.Sdk.XunitException("no other exit ahead");

        CommandResult exit = accepted.Spawned.Engine.SendCommand(Arrival, $"EXIT {other.Taxiway}");
        Assert.True(exit.Success, exit.Message);
        Assert.Null(aircraft.Phases.TaxiRoute);
        TickUntilHoldingAfterExit(accepted.Spawned);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>The implied exit becomes unmakeable after acceptance (a float): it is given up, and the stored route with it.</summary>
    [Fact]
    public void TaxiAuto_ImpliedExitGivenUp_DropsTheStoredRoute()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        string implied = aircraft.Phases!.RequestedExit!.Taxiway!;
        aircraft.IndicatedAirspeed += 60;
        accepted.Spawned.Engine.TickOneSecond();

        Assert.Contains(implied, aircraft.Phases.GivenUpExitTaxiways);
        Assert.Null(aircraft.Phases.TaxiRoute);
        TickUntilHoldingAfterExit(accepted.Spawned);
    }

    /// <summary>A runway crossing on the stored route still stops the aircraft: OAK 28R to runway 30 crosses 28L.</summary>
    [Fact]
    public void TaxiAuto_Accepted_HoldsShortOfACrossingOnTheRoute()
    {
        if (AcceptOnRollout("OAK", "28R", "C172", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        int exitBarNode = aircraft.Phases!.TaxiRoute!.Segments[0].FromNodeId;
        int held = SfoGroundHarness.TickUntil(accepted.Spawned.Engine, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase, 300, null);
        Assert.True(held > 0, $"never held short on the route: {aircraft.Phases?.CurrentPhase?.Name}");
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.NotEqual(exitBarNode, hold.HoldShort.NodeId);
        Assert.Equal(HoldShortReason.RunwayCrossing, hold.HoldShort.Reason);
    }

    /// <summary>CROSS sent while the route waits clears the next crossing on the stored route, and the clearance survives its start.</summary>
    [Fact]
    public void Cross_WhileTheRouteWaits_ClearsTheStoredCrossing()
    {
        if (AcceptOnRollout("OAK", "28R", "C172", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        TaxiRoute stored = aircraft.Phases!.TaxiRoute!;
        HoldShortPoint bar = stored.HoldShortPoints.First(h => !h.IsCleared && (h.Reason == HoldShortReason.RunwayCrossing));
        CommandResult cross = accepted.Spawned.Engine.SendCommand(Arrival, $"CROSS {bar.TargetName}");
        Assert.True(cross.Success, cross.Message);
        Assert.True(bar.IsCleared);

        int started = SfoGroundHarness.TickUntil(accepted.Spawned.Engine, () => aircraft.Phases?.CurrentPhase is TaxiingPhase, 200, null);
        Assert.True(started > 0, $"the route never started: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Contains(aircraft.Ground.AssignedTaxiRoute!.HoldShortPoints, h => (h.NodeId == bar.NodeId) && h.IsCleared);
    }

    /// <summary>
    /// A standard exit the rollout accepts is one it takes: OAK 28R C172 <c>TAXIAUTO 30</c> at touchdown reads back an exit
    /// right at E, the rollout hands off short of E's turn-entry point and the aircraft leaves the runway at E, with no
    /// "missed exit" or "unable E." and the stored route via C B W W1 still the one it taxies.
    /// </summary>
    [Fact]
    public void TaxiAuto_ImpliedStandardExit_Accepted_IsTheExitTaken()
    {
        if (AcceptOnRollout("OAK", "28R", "C172", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        SimulationEngine engine = accepted.Spawned.Engine;
        AircraftState aircraft = accepted.Spawned.Aircraft;
        ExitPreference implied = aircraft.Phases!.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        Assert.Equal("E", implied.Taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExitSide.Right, implied.Side);
        TaxiRoute stored = aircraft.Phases.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        List<string> storedTaxiways = RouteTaxiways(stored);
        _output.WriteLine($"stored: {stored.ToSummary()}");
        AssertInOrder(storedTaxiways, ["C", "B", "W", "W1"]);

        List<string> messages = CaptureMessages(engine);

        string? exitTaken = TickOffTheRunway(engine, aircraft);
        foreach (string message in messages)
        {
            _output.WriteLine($"  said: {message}");
        }

        Assert.Equal("E", exitTaken, StringComparer.OrdinalIgnoreCase);
        AssertNoMissedExit(messages);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? aircraft.Phases?.TaxiRoute ?? throw new Xunit.Sdk.XunitException("taxi route dropped");
        Assert.Equal(storedTaxiways, RouteTaxiways(route));
    }

    /// <summary>
    /// The rollout hands a standard exit off at coast speed, not only once it is down to turn-off speed: OAK 28R C172
    /// <c>TAXIAUTO 30</c>, implied exit E, ends at most one rollout second at or below coast (the phase sees the speed only the
    /// second after physics takes it there) and is in the runway exit the next, still faster than E's turn-off speed plus the
    /// tolerance.
    /// </summary>
    [Fact]
    public void TaxiAuto_ImpliedStandardExit_HandsOffAtCoast()
    {
        if (AcceptOnRollout("OAK", "28R", "C172", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        double coast = CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Piston);
        double slowEnough = CategoryPerformance.StandardExitSpeed(AircraftCategory.Piston) + RolloutBraking.TurnOffSpeedToleranceKts;
        int rolloutSecondsAtOrBelowCoast = 0;
        int handedOff = SfoGroundHarness.TickUntil(
            accepted.Spawned.Engine,
            () =>
            {
                if ((aircraft.Phases?.CurrentPhase is LandingPhase) && (aircraft.IndicatedAirspeed <= coast))
                {
                    rolloutSecondsAtOrBelowCoast++;
                }

                return aircraft.Phases?.CurrentPhase is RunwayExitPhase;
            },
            120,
            null
        );

        _output.WriteLine($"handed off after {handedOff} s: {rolloutSecondsAtOrBelowCoast} rollout s at or below coast");
        _output.WriteLine($"now {aircraft.IndicatedAirspeed:F2} kt");
        Assert.True(handedOff > 0, $"never handed off: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(
            rolloutSecondsAtOrBelowCoast <= 1,
            $"the rollout stayed in hand for {rolloutSecondsAtOrBelowCoast} s at or below coast {coast} kt"
        );
        Assert.InRange(aircraft.IndicatedAirspeed, slowEnough, coast);
    }

    /// <summary>
    /// An aircraft already at a standard exit's turn-off speed between the exit's turn-entry point and its branch can still take
    /// it: OAK 28R C172 told <c>EXIT E</c>, then placed 5 ft short of E's branch (inside its 11 ft turn-entry lead) at 12 kt, hands
    /// off and turns off at E with no "missed exit" or "unable E.".
    /// </summary>
    [Fact]
    public void ExitE_SlowBetweenTurnEntryAndBranch_IsTakenThere()
    {
        if (Touchdown("OAK", "28R", "C172") is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        CommandResult exit = spawned.Engine.SendCommand(Arrival, "EXIT E");
        Assert.True(exit.Success, exit.Message);
        spawned.Engine.TickOneSecond();
        ResolvedExitInfo candidate =
            Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase).CandidateExit
            ?? throw new Xunit.Sdk.XunitException("no candidate exit after EXIT E");
        Assert.Equal("E", candidate.TaxiwayName, StringComparer.OrdinalIgnoreCase);

        TrueHeading heading = spawned.Runway.TrueHeading;
        aircraft.Position = GeoMath.ProjectPoint(candidate.BranchPointNode.Position, heading.ToReciprocal(), 5.0 / GeoMath.FeetPerNm);
        aircraft.IndicatedAirspeed = candidate.TurnOffSpeed;
        List<string> messages = CaptureMessages(spawned.Engine);

        string? exitTaken = TickOffTheRunway(spawned.Engine, aircraft);
        Assert.Equal("E", exitTaken, StringComparer.OrdinalIgnoreCase);
        AssertNoMissedExit(messages);
    }

    /// <summary>
    /// A standard exit whose hand-off is held at coast speed is released once the aircraft is slow enough: OAK 28R C172 with the
    /// rollout's own candidate exit, placed at 22 kt (below coast) where reaching the turn-off speed by the turn-entry point needs
    /// a little more than the rate the exit was chosen at but no more than the rollout accepts. The hand-off waits while the
    /// rollout brakes, then the aircraft turns off at that exit.
    /// </summary>
    [Fact]
    public void StandardExit_HandOffHeldAtCoast_IsReleasedOnceSlow()
    {
        if (Touchdown("OAK", "28R", "C172") is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        spawned.Engine.TickOneSecond();
        ResolvedExitInfo candidate =
            Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase).CandidateExit
            ?? throw new Xunit.Sdk.XunitException("no candidate exit after touchdown");
        Assert.True(RolloutBraking.IsStandardExit(AircraftCategory.Piston, candidate.TurnOffSpeed), $"{candidate.TaxiwayName} is not standard");
        double selectionRate = candidate.SelectionDecelRate ?? throw new Xunit.Sdk.XunitException("no selection rate");

        TrueHeading heading = spawned.Runway.TrueHeading;
        double angle = aircraft.Ground.Layout!.MeasureExitAngle(candidate.Path, candidate.HoldShortNode, candidate.TaxiwayName, heading);
        double leadFt = RolloutBraking.TurnEntryLeadNm(AircraftCategory.Piston, candidate.TurnOffSpeed, angle) * GeoMath.FeetPerNm;
        const double StartKts = 22.0;
        double rateFps2 = (selectionRate + (LandingPhase.CommittedExitDecelToleranceKtsPerSec / 2.0)) * GeoMath.FeetPerNm / 3600.0;
        double startFps = StartKts * GeoMath.FeetPerNm / 3600.0;
        double turnOffFps = candidate.TurnOffSpeed * GeoMath.FeetPerNm / 3600.0;
        double brakingFt = ((startFps * startFps) - (turnOffFps * turnOffFps)) / (2.0 * rateFps2);
        aircraft.Position = GeoMath.ProjectPoint(
            candidate.BranchPointNode.Position,
            heading.ToReciprocal(),
            (leadFt + brakingFt) / GeoMath.FeetPerNm
        );
        aircraft.IndicatedAirspeed = StartKts;
        _output.WriteLine($"{candidate.TaxiwayName}: selection {selectionRate:F2} kt/s, placed {leadFt + brakingFt:F0} ft short of the branch");

        spawned.Engine.TickOneSecond();
        Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(aircraft.IndicatedAirspeed <= CategoryPerformance.RolloutCoastSpeed(AircraftCategory.Piston));

        int handedOff = SfoGroundHarness.TickUntil(spawned.Engine, () => aircraft.Phases?.CurrentPhase is RunwayExitPhase, 120, null);
        Assert.True(handedOff > 0, $"never handed off: {aircraft.Phases?.CurrentPhase?.Name}");
        double releasedAtMost = candidate.TurnOffSpeed + RolloutBraking.TurnOffSpeedToleranceKts;
        Assert.True(
            aircraft.IndicatedAirspeed <= releasedAtMost,
            $"handed off at {aircraft.IndicatedAirspeed:F2} kt, above {candidate.TurnOffSpeed} kt turn-off speed plus the tolerance"
        );

        string? exitTaken = TickOffTheRunway(spawned.Engine, aircraft);
        Assert.Equal(candidate.TaxiwayName, exitTaken, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A TAXI whose first taxiway does not touch the landing runway and no route from the runway resolves for: resolved again from
    /// each exit the rollout can make, and joined from the first whose taxiway reaches that first taxiway with no other taxiway or
    /// runway between. SFO 19L 8 s after touchdown: A lies beyond 19R from every exit, so "unable A."; OAK 28R 6 s after touchdown:
    /// the parallel C is reached straight from J, so a TAXI via C is accepted, stored, and the aircraft rolls into it after the exit.
    /// </summary>
    [Fact]
    public void Taxi_FirstTaxiwayOffTheRunway_JoinsFromAStraightExitOrIsRefused()
    {
        if (Touchdown("SFO", "19L", "A320") is not { } spawned)
        {
            return;
        }

        for (int second = 0; second < 8; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        WriteExitsAhead(spawned.Aircraft);
        CommandResult refused = spawned.Engine.SendCommand(Arrival, "TAXI A $1");
        _output.WriteLine($"TAXI A $1 -> {refused.Success} {refused.Message}");
        Assert.False(refused.Success);
        Assert.Equal("unable A.", refused.Message);
        Assert.NotNull(refused.PilotUnable);
        Assert.Null(spawned.Aircraft.Phases?.TaxiRoute);
        Assert.Null(spawned.Aircraft.Phases?.RequestedExit?.Taxiway);

        // OAK 28R: J and C1 off the right side meet the north-field parallel C with nothing between.
        if (Touchdown("OAK", "28R", "B738") is not { } oak)
        {
            return;
        }

        for (int second = 0; second < 6; second++)
        {
            oak.Engine.TickOneSecond();
        }

        AircraftState aircraft = oak.Aircraft;
        WriteExitsAhead(aircraft);
        CommandResult taxi = oak.Engine.SendCommand(Arrival, "TAXI C B W W1 30");
        _output.WriteLine($"TAXI C B W W1 30 -> {taxi.Success} {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute stored = aircraft.Phases?.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        string? joinedFrom = aircraft.Phases?.RequestedExit?.Taxiway;
        Assert.NotNull(joinedFrom);
        Assert.NotEqual("C", joinedFrom, StringComparer.OrdinalIgnoreCase);

        int started = SfoGroundHarness.TickUntil(oak.Engine, () => aircraft.Ground.AssignedTaxiRoute is not null, 200, null);
        Assert.True(started > 0, $"the route never joined: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Contains(aircraft.Ground.AssignedTaxiRoute!.Segments, s => s.TaxiwayName == "C");
        Assert.Equal(RouteNodes(stored)[^1], RouteNodes(aircraft.Ground.AssignedTaxiRoute)[^1]);
        Assert.IsNotType<HoldingAfterExitPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// A TAXI stored on the landing rollout knows the taxiways its clearance named (the runway left out), and the route the
    /// aircraft starts after the exit keeps them: OAK 28R B738, <c>TAXI C B W W1 30</c> 6 s after touchdown.
    /// </summary>
    [Fact]
    public void Taxi_StoredOnTheRollout_CarriesItsClearanceTaxiwaysIntoTheStartedRoute()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } oak)
        {
            return;
        }

        for (int second = 0; second < 6; second++)
        {
            oak.Engine.TickOneSecond();
        }

        AircraftState aircraft = oak.Aircraft;
        CommandResult taxi = oak.Engine.SendCommand(Arrival, "TAXI C B W W1 30");
        _output.WriteLine($"TAXI C B W W1 30 -> {taxi.Success} {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute stored = aircraft.Phases?.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        Assert.Equal(["C", "B", "W", "W1"], stored.ClearanceTaxiways);

        int started = SfoGroundHarness.TickUntil(oak.Engine, () => aircraft.Ground.AssignedTaxiRoute is not null, 200, null);
        Assert.True(started > 0, $"the route never joined: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Equal(["C", "B", "W", "W1"], aircraft.Ground.AssignedTaxiRoute!.ClearanceTaxiways);
    }

    /// <summary>
    /// A go-around after a rollout TAXI was accepted takes the taxi plan with it: OAK 28R B738 given <c>TAXIAUTO 30</c> at
    /// touchdown, still above its rejected-landing speed, then <c>GA</c>. No route stays stored, <c>CROSS</c> is refused while
    /// airborne, and the next landing on the same phase list exits on its own pick and holds clear.
    /// </summary>
    [Fact]
    public void GoAround_AfterTheTaxiWasAccepted_DropsTheStoredRouteAndTheImpliedExit()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXIAUTO 30");
        Assert.True(taxi.Success, taxi.Message);
        HoldShortPoint? crossingBar = aircraft.Phases!.TaxiRoute?.HoldShortPoints.FirstOrDefault(h =>
            (h.Reason == HoldShortReason.RunwayCrossing) && !h.IsCleared
        );
        Assert.NotNull(crossingBar);
        string crossing = crossingBar.TargetName!;

        // The rollout's first tick judges the go-around speed gate.
        spawned.Engine.TickOneSecond();
        Assert.True(
            aircraft.IndicatedAirspeed >= CategoryPerformance.RejectedLandingMinSpeed(AircraftCategory.Jet),
            $"at {aircraft.IndicatedAirspeed:F0} kt, below the rejected-landing speed"
        );

        CommandResult goAround = spawned.Engine.SendCommand(Arrival, "GA");
        Assert.True(goAround.Success, goAround.Message);
        Assert.Null(aircraft.Phases.TaxiRoute);
        Assert.Null(aircraft.Phases.RequestedExit);

        int airborne = SfoGroundHarness.TickUntil(spawned.Engine, () => !aircraft.IsOnGround, 60, null);
        Assert.True(airborne > 0, $"never airborne after the go-around: {aircraft.Phases.CurrentPhase?.Name}");
        CommandResult cross = spawned.Engine.SendCommand(Arrival, $"CROSS {crossing}");
        _output.WriteLine($"CROSS {crossing} airborne -> {cross.Success} {cross.Message}");
        Assert.False(cross.Success, $"CROSS {crossing} accepted while airborne: {cross.Message}");

        RestageOnFinal(spawned, 2.0);
        Assert.True(spawned.Engine.SendCommand(Arrival, "CLAND").Success);
        bool taxied = false;
        int held = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () =>
            {
                taxied |= aircraft.Phases?.CurrentPhase is TaxiingPhase;
                return aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase;
            },
            400,
            null
        );
        Assert.True(held > 0, $"the next landing never held clear after its exit: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.False(taxied, "the next landing started a taxi nobody gave it");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// An exit that can never complete — the aircraft stopped on the runway past its last exit, the pilot reporting no exit ahead —
    /// drops the taxi route stored behind it, so a <c>CROSS</c> no longer reports success against a route that will never start.
    /// </summary>
    [Fact]
    public void NoExitAhead_WithAStoredRoute_DropsTheRoute()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        ShortFinalArrival.Spawned spawned = accepted.Spawned;
        AircraftState aircraft = spawned.Aircraft;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        TaxiRoute stored = aircraft.Phases!.TaxiRoute!;
        HoldShortPoint? crossingBar = stored.HoldShortPoints.FirstOrDefault(h => (h.Reason == HoldShortReason.RunwayCrossing) && !h.IsCleared);
        Assert.NotNull(crossingBar);
        string crossing = crossingBar.TargetName!;

        // Staged past 19L's last centerline node, stopped, in a runway exit with the stored route still on its phase list.
        RunwayInfo runway = spawned.Runway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        double lastAlongNm = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesRunway(runway.Designator)))
            .Max(n => GeoMath.AlongTrackDistanceNm(n.Position, threshold, runway.TrueHeading));
        aircraft.Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading, lastAlongNm + (40.0 / GeoMath.FeetPerNm));
        aircraft.IndicatedAirspeed = 0;
        var phases = new PhaseList { AssignedRunway = runway, TaxiRoute = stored };
        phases.Add(new RunwayExitPhase());
        phases.Add(new HoldingAfterExitPhase());
        aircraft.Phases = phases;
        phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        List<string> warnings = [];
        spawned.Engine.WarningEmitted += (callsign, text) =>
        {
            if (callsign == Arrival)
            {
                warnings.Add(text);
            }
        };
        for (int second = 0; second < 10; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        Assert.Contains(warnings, w => w.Contains("no exit ahead", StringComparison.OrdinalIgnoreCase));
        Assert.Null(aircraft.Phases.TaxiRoute);
        CommandResult cross = spawned.Engine.SendCommand(Arrival, $"CROSS {crossing}");
        _output.WriteLine($"CROSS {crossing} with no exit ahead -> {cross.Success} {cross.Message}");
        Assert.False(cross.Success, $"CROSS {crossing} accepted against a route that never starts: {cross.Message}");
    }

    /// <summary>
    /// Scenario auto-cross on a route stored on the rollout clears every runway but the landing runway's re-crossing
    /// (7110.65 3-10-9.b.2): OAK 28R, a TAXI joining C north of 28R to runway 30, auto-cross on. The aircraft holds at the
    /// 28R bar, <c>CROSS 28R</c> releases it, and 28L, auto-crossed, is crossed without a stop.
    /// </summary>
    [Fact]
    public void AutoCross_OnAStoredRolloutRoute_LeavesTheLandingRunwaysRecrossing()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return;
        }

        spawned.Engine.Scenario!.AutoCrossRunwayMode = AutoCrossRunwayMode.All;
        for (int second = 0; second < 6; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        AircraftState aircraft = spawned.Aircraft;
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI C B W W1 30");
        _output.WriteLine($"TAXI C B W W1 30 -> {taxi.Success} {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute stored = aircraft.Phases?.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        string dump = string.Join(", ", stored.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason},cleared={h.IsCleared})"));
        _output.WriteLine($"stored bars: {dump}");
        int start = stored.Segments[0].FromNodeId;
        List<HoldShortPoint> crossings = [.. stored.HoldShortPoints.Where(h => (h.Reason == HoldShortReason.RunwayCrossing) && (h.NodeId != start))];
        HoldShortPoint recross =
            crossings.FirstOrDefault(h => RunwayIdentifier.Parse(h.TargetName!).Overlaps(RunwayIdentifier.Parse("28R")))
            ?? throw new Xunit.Sdk.XunitException($"the stored route does not re-cross 28R: {dump}");
        HoldShortPoint other =
            crossings.FirstOrDefault(h => !RunwayIdentifier.Parse(h.TargetName!).Overlaps(RunwayIdentifier.Parse("28R")))
            ?? throw new Xunit.Sdk.XunitException($"the stored route crosses no other runway: {dump}");
        Assert.False(recross.IsCleared, $"auto-cross cleared the landing runway's re-crossing: {dump}");
        Assert.True(other.IsCleared, $"auto-cross left {other.TargetName} uncleared: {dump}");

        int held = SfoGroundHarness.TickUntil(spawned.Engine, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase, 300, null);
        Assert.True(held > 0, $"never held short of 28R: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Equal(recross.NodeId, Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase).HoldShort.NodeId);

        CommandResult cross = spawned.Engine.SendCommand(Arrival, "CROSS 28R");
        Assert.True(cross.Success, cross.Message);
        TaxiRoute active = aircraft.Ground.AssignedTaxiRoute!;
        int otherSegment = active.Segments.FindIndex(s => s.ToNodeId == other.NodeId);
        Assert.True(otherSegment >= 0, $"the active route lost the {other.TargetName} bar");
        int past = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () =>
            {
                if (aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.RunwayCrossing } hold)
                {
                    Assert.Equal(recross.NodeId, hold.HoldShort.NodeId);
                }

                return active.CurrentSegmentIndex > otherSegment;
            },
            300,
            null
        );
        Assert.True(past > 0, $"never crossed {other.TargetName}: {aircraft.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>
    /// Joining a stored route from an exit never runs another runway's bar unchecked: at OAK, G runs from a 28R bar straight to
    /// a 28L bar and across 28L. A route stored from beyond that 28L bar, across 28L, is not joined from G's 28R exit.
    /// </summary>
    [Fact]
    public void JoinStoredRoute_ThroughAnotherRunwaysBar_DoesNotJoin()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode Bar28R, GroundNode Bar28L) bars = layout
            .Nodes.Values.Where(n => IsRunwayBarOn(n, "G", "28R"))
            .SelectMany(a => a.Edges.Select(e => e.OtherNode(a)).Where(b => IsRunwayBarOn(b, "G", "28L")).Select(b => (a, b)))
            .Distinct()
            .Single();
        GroundNode bar28R = bars.Bar28R;
        GroundNode bar28L = bars.Bar28L;
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar28R.Type);
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar28L.Type);
        GroundNode cameFrom = bar28R.Edges.Select(e => e.OtherNode(bar28R)).Single(n => n.Id != bar28L.Id);

        List<TaxiRouteSegment> segments = [];
        GroundNode previous = bar28R;
        GroundNode node = bar28L;
        for (int step = 0; step < 3; step++)
        {
            IGroundEdge? onward = node.Edges.FirstOrDefault(e =>
                e.MatchesTaxiway("G") && !e.IsRunwayCenterline && (e.OtherNode(node).Id != previous.Id)
            );
            if (onward is null)
            {
                break;
            }

            GroundNode next = onward.OtherNode(node);
            segments.Add(new TaxiRouteSegment { TaxiwayName = "G", Edge = onward.Directed(node, next) });
            previous = node;
            node = next;
        }

        Assert.NotEmpty(segments);
        var stored = new TaxiRoute { Segments = segments, HoldShortPoints = [] };

        TaxiRoute? joined = GroundCommandHandler.JoinStoredRoute(stored, new GroundCommandHandler.StoredRouteJoin(bar28R, cameFrom, "G", "28R"));

        Assert.Null(joined);
    }

    /// <summary>
    /// A rollout TAXI naming its destination runway keeps it with the stored route: OAK 28R B738 <c>TAXIAUTO 30</c> stores runway 30
    /// beside the route, and the runway survives a snapshot round trip. When the route starts the stored runway is the one assigned,
    /// not the one the route's last bar names: the stored runway set to 28L before the start, 28L is assigned on a route ending at 30.
    /// </summary>
    [Fact]
    public void TaxiAuto_ToARunway_StoresAndAssignsThatRunway()
    {
        if (AcceptOnRollout("OAK", "28R", "B738", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        Assert.Equal("30", aircraft.Phases!.StoredTaxiDestinationRunway);
        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), aircraft.Ground.Layout!);
        Assert.Equal("30", restored.Phases?.StoredTaxiDestinationRunway);

        aircraft.Phases.StoredTaxiDestinationRunway = "28L";
        int started = SfoGroundHarness.TickUntil(accepted.Spawned.Engine, () => aircraft.Ground.AssignedTaxiRoute is not null, 200, null);
        Assert.True(started > 0, $"the stored route never started: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Null(aircraft.Phases?.StoredTaxiDestinationRunway);
        Assert.Equal("28L", aircraft.Phases?.AssignedRunway?.Designator);
    }

    /// <summary>
    /// The side a TAXI joining from an exit turns the rollout toward is part of a snapshot: OAK 28R B738 6 s after touchdown,
    /// <c>TAXI C B W W1 30</c> joins C from an exit on the right. Restored into a fresh engine from a snapshot taken then and ticked
    /// a second alongside the live one, an <c>EXIT</c> naming another taxiway drops the route in both engines, both give the side
    /// back to the one before the TAXI, and both choose the same exit.
    /// </summary>
    [Fact]
    public void SideTowardTheRoute_RestoredFromASnapshot_RevertsAsLive()
    {
        if (TouchdownOak28RParkedLeft() is not { } live)
        {
            return;
        }

        for (int second = 0; second < 6; second++)
        {
            live.Engine.TickOneSecond();
        }

        AircraftState aircraft = live.Aircraft;
        LandingPhase landing = Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        ExitSide? before = landing.InferredSide;
        CommandResult taxi = live.Engine.SendCommand(Arrival, "TAXI C B W W1 30");
        Assert.True(taxi.Success, taxi.Message);
        ExitSide? toward = landing.InferredSide;
        _output.WriteLine($"side before the TAXI {before?.ToString() ?? "none"}, toward the route {toward?.ToString() ?? "none"}");
        Assert.Equal(ExitSide.Left, before);
        Assert.Equal(ExitSide.Right, toward);

        // The restore fills scenario state only into an engine that already has a scenario, and an engine with none never ticks.
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-short-final-arrival",
                ScenarioName = "Short-final arrival",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.RestoreFromSnapshot(live.Engine.CaptureSnapshot());
        AircraftState restored = engine.World.FindAircraft(Arrival) ?? throw new Xunit.Sdk.XunitException("the arrival was not restored");
        LandingPhase restoredLanding = Assert.IsType<LandingPhase>(restored.Phases?.CurrentPhase);
        Assert.Equal(toward, restoredLanding.InferredSide);

        string implied = aircraft.Phases!.RequestedExit!.Taxiway!;
        string other =
            landing.ListExitsAhead(aircraft)?.FirstOrDefault(r => !string.Equals(r.Taxiway, implied, StringComparison.OrdinalIgnoreCase))?.Taxiway
            ?? throw new Xunit.Sdk.XunitException("no other exit ahead");
        // A playback ticks forward from the snapshot before the next recorded command; the live run ticks alongside.
        live.Engine.TickOneSecond();
        engine.TickOneSecond();
        Assert.Equal(landing.InferredSide, restoredLanding.InferredSide);
        CommandResult liveExit = live.Engine.SendCommand(Arrival, $"EXIT {other}");
        CommandResult restoredExit = engine.SendCommand(Arrival, $"EXIT {other}");
        Assert.True(liveExit.Success, liveExit.Message);
        Assert.True(restoredExit.Success, restoredExit.Message);
        Assert.Null(aircraft.Phases.TaxiRoute);
        Assert.Null(restored.Phases!.TaxiRoute);
        Assert.Equal(before, landing.InferredSide);
        Assert.Equal(landing.InferredSide, restoredLanding.InferredSide);

        Assert.Equal(ExitChosen(live.Engine, aircraft), ExitChosen(engine, restored));
        IReadOnlyList<SnapshotDivergence> differences = SnapshotTreeDiff.CompareNodes(
            JsonSerializer.SerializeToNode(aircraft.ToSnapshot(), RecordingJsonOptions.Default),
            JsonSerializer.SerializeToNode(restored.ToSnapshot(), RecordingJsonOptions.Default)
        );
        foreach (SnapshotDivergence difference in differences.Take(20))
        {
            _output.WriteLine($"live and restored differ at {difference.Path}: {difference.Left} vs {difference.Right}");
        }

        Assert.Empty(differences);
    }

    /// <summary>
    /// A bare <c>EXIT {twy}</c> after a rollout TAXI does not keep the side the TAXI implied: OAK 28R B738, parked left of the
    /// runway, <c>TAXI C B W W1 30</c> 6 s after touchdown implies C1 on the right; a bare <c>EXIT J</c> then reads back no side and
    /// takes J on the side the same scenario's <c>EXIT J</c> with no TAXI takes it.
    /// </summary>
    [Fact]
    public void BareExit_AfterARolloutTaxi_ChoosesItsSideAsWithNoTaxi()
    {
        if ((RolloutOak28RAtSixSeconds(withTaxi: true) is not { } afterTaxi) || (RolloutOak28RAtSixSeconds(withTaxi: false) is not { } noTaxi))
        {
            return;
        }

        CommandResult exit = afterTaxi.Engine.SendCommand(Arrival, "EXIT J");
        _output.WriteLine($"EXIT J after the TAXI -> {exit.Success} {exit.Message}");
        Assert.True(exit.Success, exit.Message);
        Assert.Equal("Exit at J", exit.Message);
        Assert.Null(afterTaxi.Aircraft.Phases!.RequestedExit?.Side);
        CommandResult plain = noTaxi.Engine.SendCommand(Arrival, "EXIT J");
        Assert.True(plain.Success, plain.Message);

        Assert.Equal(ExitChosen(noTaxi.Engine, noTaxi.Aircraft), ExitChosen(afterTaxi.Engine, afterTaxi.Aircraft));
    }

    /// <summary>
    /// A side the controller says keeps after a rollout TAXI: <c>ER J</c> after the TAXI that implied C1 reads back "right", and a
    /// bare <c>EXIT J</c> after that keeps the controller's right.
    /// </summary>
    [Fact]
    public void ExplicitSideExit_AfterARolloutTaxi_KeepsTheControllersSide()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: true) is not { } afterTaxi)
        {
            return;
        }

        CommandResult right = afterTaxi.Engine.SendCommand(Arrival, "ER J");
        Assert.True(right.Success, right.Message);
        Assert.Equal("Exit right at J", right.Message);
        CommandResult bare = afterTaxi.Engine.SendCommand(Arrival, "EXIT J");
        Assert.True(bare.Success, bare.Message);
        Assert.Equal("Exit right at J", bare.Message);
        Assert.Equal(ExitSide.Right, afterTaxi.Aircraft.Phases!.RequestedExit?.Side);
    }

    /// <summary>
    /// A second TAXI on the rollout replaces the first one's plan: OAK 28R B738 parked left of the runway, <c>TAXI C B W W1 30</c> 6 s
    /// after touchdown implies C1 on the right, then <c>TAXIAUTO 30</c> replaces it — the first TAXI's side toward its route is dropped,
    /// so the rollout's side and implied exit are those of the same rollout given only the TAXIAUTO, and the exit C1 implied is
    /// no longer the instruction.
    /// </summary>
    [Fact]
    public void SecondTaxi_ReplacingTheFirst_DropsItsSideTowardTheRoute()
    {
        if ((RolloutOak28RAtSixSeconds(withTaxi: false) is not { } noTaxi) || (RolloutOak28RAtSixSeconds(withTaxi: true) is not { } taxed))
        {
            return;
        }

        AircraftState aircraft = taxed.Aircraft;
        LandingPhase landing = Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        ExitPreference implied = aircraft.Phases!.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        Assert.Equal(ExitSide.Right, landing.InferredSide);

        // The same rollout given only the TAXIAUTO: the side and exit the TAXI'd one must end up with once its own TAXI is replaced.
        LandingPhase control = Assert.IsType<LandingPhase>(noTaxi.Aircraft.Phases?.CurrentPhase);
        CommandResult alone = noTaxi.Engine.SendCommand(Arrival, "TAXIAUTO 30");
        Assert.True(alone.Success, alone.Message);
        ExitPreference controlExit = noTaxi.Aircraft.Phases!.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit");

        CommandResult second = taxed.Engine.SendCommand(Arrival, "TAXIAUTO 30");
        _output.WriteLine($"TAXIAUTO 30 after the TAXI that implied {implied.Taxiway} -> {second.Success} {second.Message}");
        Assert.True(second.Success, second.Message);
        Assert.NotSame(implied, aircraft.Phases!.RequestedExit);
        Assert.Equal(control.InferredSide, landing.InferredSide);
        Assert.Equal(controlExit.Taxiway, aircraft.Phases.RequestedExit?.Taxiway);
        Assert.Equal(controlExit.Side, aircraft.Phases.RequestedExit?.Side);
    }

    /// <summary>
    /// The link between the requested exit and the exit a rollout TAXI implied survives a snapshot restore into the runway exit:
    /// OAK 28R B738, the TAXI accepted 6 s after touchdown, a snapshot 3 s later restored into a fresh engine, both ticked to the
    /// first tick inside <see cref="RunwayExitPhase"/>. The two snapshots then match, and a bare <c>EXIT J</c> reads back
    /// "Exit at J" in both, the implied side not kept.
    /// </summary>
    [Fact]
    public void ImpliedExitLink_RestoredFromASnapshot_SurvivesIntoTheRunwayExit()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: true) is not { } live)
        {
            return;
        }

        for (int second = 0; second < 3; second++)
        {
            live.Engine.TickOneSecond();
        }

        AircraftState aircraft = live.Aircraft;
        Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        (SimulationEngine engine, AircraftState restored) = Restored(live, "OAK");
        for (int second = 1; aircraft.Phases?.CurrentPhase is not RunwayExitPhase; second++)
        {
            Assert.True(second <= 120, $"never reached the runway exit: {aircraft.Phases?.CurrentPhase?.Name}");
            live.Engine.TickOneSecond();
            engine.TickOneSecond();
        }

        Assert.IsType<RunwayExitPhase>(restored.Phases?.CurrentPhase);
        Assert.Empty(SnapshotDifferences(aircraft, restored));
        CommandResult liveExit = live.Engine.SendCommand(Arrival, "EXIT J");
        CommandResult restoredExit = engine.SendCommand(Arrival, "EXIT J");
        _output.WriteLine($"EXIT J in the runway exit: live {liveExit.Message}, restored {restoredExit.Message}");
        Assert.Equal("Exit at J", liveExit.Message);
        Assert.Equal("Exit at J", restoredExit.Message);
    }

    /// <summary>
    /// <c>CROSS … HS …</c> amends a route stored on the rollout as it amends an active one: OAK 28R B738 <c>TAXIAUTO 30</c>, then
    /// <c>CROSS 28L HS W</c> clears the 28L crossing and arms a hold short of W on the stored route. Ticked on (any other runway
    /// bar crossed with a bare <c>CROSS</c>), the aircraft crosses 28L and holds short of W.
    /// </summary>
    [Fact]
    public void CrossWithHoldShort_OnTheStoredRoute_HoldsAtTheTaxiwayAfterTheCrossing()
    {
        if (AcceptOnRollout("OAK", "28R", "B738", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        SimulationEngine engine = accepted.Spawned.Engine;
        AircraftState aircraft = accepted.Spawned.Aircraft;
        TaxiRoute stored = aircraft.Phases!.TaxiRoute!;
        _output.WriteLine($"stored: {stored.ToSummary()}");
        HoldShortPoint crossing = stored.HoldShortPoints.First(h =>
            (h.Reason == HoldShortReason.RunwayCrossing) && RunwayIdentifier.Parse(h.TargetName!).Contains("28L")
        );

        CommandResult cross = engine.SendCommand(Arrival, "CROSS 28L HS W");
        _output.WriteLine($"CROSS 28L HS W -> {cross.Success} {cross.Message}");
        Assert.True(cross.Success, cross.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.True(crossing.IsCleared);
        HoldShortPoint holdW = stored.HoldShortPoints.Single(h => IsExplicitHoldShortOfW(h));

        HoldingShortPhase? held = null;
        for (int second = 1; (second <= 400) && (held is null); second++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is not HoldingShortPhase hold)
            {
                continue;
            }

            _output.WriteLine($"t+{second} holding short of {hold.HoldShort.TargetName} ({hold.HoldShort.Reason})");
            if (IsExplicitHoldShortOfW(hold.HoldShort))
            {
                held = hold;
            }
            else
            {
                Assert.NotEqual(crossing.NodeId, hold.HoldShort.NodeId);
                CommandResult next = engine.SendCommand(Arrival, "CROSS");
                Assert.True(next.Success, next.Message);
            }
        }

        Assert.NotNull(held);
        Assert.Equal(holdW.NodeId, held.HoldShort.NodeId);
        TaxiRoute active = aircraft.Ground.AssignedTaxiRoute ?? throw new Xunit.Sdk.XunitException("the stored route never started");
        int crossingSegment = active.Segments.FindIndex(s => s.ToNodeId == crossing.NodeId);
        Assert.True(crossingSegment >= 0, "the started route lost the 28L crossing");
        Assert.True(active.CurrentSegmentIndex > crossingSegment, "held short of W before crossing 28L");
    }

    /// <summary>A plain <c>HS W</c> on the rollout arms a hold short of W on the stored route, read back as made, not "unable".</summary>
    [Fact]
    public void HoldShort_OnTheStoredRoute_IsAccepted()
    {
        if (AcceptOnRollout("OAK", "28R", "B738", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        CommandResult hs = accepted.Spawned.Engine.SendCommand(Arrival, "HS W");
        Assert.True(hs.Success, hs.Message);
        Assert.Equal("Hold short of W", hs.Message);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint bar = aircraft.Phases!.TaxiRoute!.HoldShortPoints.Single(h => IsExplicitHoldShortOfW(h));
        Assert.False(bar.Unable);
    }

    /// <summary><c>GIVEWAY</c> on the rollout is accepted once a TAXI stored a route, as it is with an active route.</summary>
    [Fact]
    public void GiveWay_OnTheStoredRoute_IsAccepted()
    {
        if (AcceptOnRollout("OAK", "28R", "B738", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        CommandResult giveWay = accepted.Spawned.Engine.SendCommand(Arrival, "GIVEWAY UAL123");
        Assert.True(giveWay.Success, giveWay.Message);
        Assert.Equal("Give way to UAL123", giveWay.Message);
        Assert.Equal(HoldKind.GiveWay, aircraft.Ground.Hold?.Kind);
    }

    /// <summary>
    /// A bare <c>EXIT</c> repeating the taxiway a rollout TAXI implied keeps the stored route and still turns the rollout toward
    /// it: OAK 28R B738 parked left of the runway, a TAXI 6 s after touchdown implying an exit on the right, C1 joined to C
    /// (<c>TAXI C B W W1 30</c>) or J (<c>TAXI J C B W W1 30</c>); no route resolves from the aircraft there, so both join from
    /// the exit. The rollout's side turns right toward the route, the exit reads back "Exit at {twy}" with no side, the route
    /// stays stored, the requested exit is no longer the implied one, and ticked on the aircraft exits there on the right and
    /// rolls into the route.
    /// </summary>
    [Theory]
    [InlineData("TAXI C B W W1 30", "C1")]
    [InlineData("TAXI J C B W W1 30", "J")]
    public void BareExit_RepeatingTheImpliedTaxiway_KeepsTheRouteAndExitsTowardIt(string taxi, string implied)
    {
        if (TouchdownOak28RParkedLeft() is not { } spawned)
        {
            return;
        }

        for (int second = 0; second < 6; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        AircraftState aircraft = spawned.Aircraft;
        CommandResult accepted = spawned.Engine.SendCommand(Arrival, taxi);
        _output.WriteLine($"{taxi} -> {accepted.Success} {accepted.Message}");
        Assert.True(accepted.Success, accepted.Message);
        PhaseList phases = aircraft.Phases!;
        Assert.Equal(implied, phases.RequestedExit?.Taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExitSide.Right, phases.RequestedExit?.Side);
        TaxiRoute stored = phases.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        LandingPhase landing = Assert.IsType<LandingPhase>(phases.CurrentPhase);
        Assert.Equal(ExitSide.Right, landing.InferredSide);

        CommandResult exit = spawned.Engine.SendCommand(Arrival, $"EXIT {implied}");
        Assert.True(exit.Success, exit.Message);
        Assert.Equal($"Exit at {implied}", exit.Message);
        Assert.Null(phases.RequestedExit?.Side);
        Assert.Equal(ExitSide.Right, landing.InferredSide);
        Assert.Same(stored, phases.TaxiRoute);
        Assert.False(ReferenceEquals(phases.RequestedExit, phases.TaxiImpliedExit));

        (string chosenTaxiway, ExitSide? chosenSide, _) = ExitChosen(spawned.Engine, aircraft);
        Assert.Equal(implied, chosenTaxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExitSide.Right, chosenSide);
        int started = SfoGroundHarness.TickUntil(spawned.Engine, () => aircraft.Phases?.CurrentPhase is TaxiingPhase, 200, null);
        Assert.True(started > 0, $"never rolled into the route: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.NotNull(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// The roll-on from an exit into a stored route that turns a short way past the exit's bar: OAK 28R B738 parked left, <c>TAXI C
    /// B W W1 30</c> 6 s after touchdown joins C from C1, whose bar is under 200 ft from the C junction, where the route turns onto
    /// C. The aircraft rolls on from the exit onto C without orbiting: its heading turns less than 270° in all from the start of
    /// the exit to the segment on C, it never holds after the exit, and once at the junction it never strays further from it
    /// than the aircraft is long before it is on C.
    /// </summary>
    [Fact]
    public void RollOn_IntoARouteTurningNearTheExitBar_NeitherOvershootsNorOrbits()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: true) is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        TaxiRoute stored = aircraft.Phases!.TaxiRoute!;
        int onC = stored.Segments.FindIndex(s => string.Equals(s.TaxiwayName, "C", StringComparison.OrdinalIgnoreCase));
        Assert.True(onC >= 0, $"the stored route never reaches C: {stored.ToSummary()}");
        GroundNode bar = layout.Nodes[stored.Segments[0].FromNodeId];
        GroundNode junction = layout.Nodes[stored.Segments[onC].FromNodeId];
        double barToJunctionFt =
            stored.Segments.Take(onC).Sum(s => GeoMath.DistanceNm(layout.Nodes[s.FromNodeId].Position, layout.Nodes[s.ToNodeId].Position))
            * GeoMath.FeetPerNm;
        GroundNode before = layout.Nodes[(onC > 0) ? stored.Segments[onC - 1].FromNodeId : stored.Segments[0].FromNodeId];
        var inbound = new TrueHeading(GeoMath.BearingTo(before.Position, junction.Position));
        var outbound = new TrueHeading(GeoMath.BearingTo(junction.Position, layout.Nodes[stored.Segments[onC].ToNodeId].Position));
        _output.WriteLine(
            $"bar {bar.Id} to the C junction {junction.Id}: {barToJunctionFt:F0} ft, {onC} segments, turn {inbound.AbsAngleTo(outbound):F0} deg"
        );
        Assert.True(barToJunctionFt < 200, $"the C1 bar is {barToJunctionFt:F0} ft from the C junction");
        double lengthFt = Yaat.Sim.Data.Faa.AircraftLength.ResolveFt(aircraft.AircraftType);

        double turnedDeg = 0;
        double previousHeading = aircraft.TrueHeading.Degrees;
        bool exiting = false;
        bool reachedJunction = false;
        double furthestPastJunctionFt = 0;
        for (int second = 1; second <= 200; second++)
        {
            spawned.Engine.TickOneSecond();
            Phase? phase = aircraft.Phases?.CurrentPhase;
            Assert.IsNotType<HoldingAfterExitPhase>(phase);
            exiting |= phase is RunwayExitPhase;
            if (exiting)
            {
                turnedDeg += Math.Abs(new TrueHeading(previousHeading).SignedAngleTo(aircraft.TrueHeading));
            }

            previousHeading = aircraft.TrueHeading.Degrees;
            double toJunctionFt = GeoMath.DistanceNm(aircraft.Position, junction.Position) * GeoMath.FeetPerNm;
            reachedJunction |= toJunctionFt <= 30;
            if (reachedJunction)
            {
                furthestPastJunctionFt = Math.Max(furthestPastJunctionFt, toJunctionFt);
            }

            TaxiRoute? active = aircraft.Ground.AssignedTaxiRoute;
            string where = $"hdg {aircraft.TrueHeading.Degrees:F0} to junction {toJunctionFt:F0} ft seg {active?.CurrentSegmentIndex}";
            _output.WriteLine($"t+{second} {phase?.Name} {aircraft.GroundSpeed:F1} kt {where}");
            if ((active is not null) && (active.CurrentSegmentIndex >= active.Segments.FindIndex(s => s.FromNodeId == junction.Id)))
            {
                break;
            }
        }

        _output.WriteLine(
            $"turned {turnedDeg:F0} deg; furthest from the junction after reaching it {furthestPastJunctionFt:F0} ft (length {lengthFt:F0} ft)"
        );
        Assert.True(reachedJunction, "never reached the C junction");
        Assert.True(turnedDeg < 270, $"turned {turnedDeg:F0} deg between the exit and C");
        Assert.True(furthestPastJunctionFt < lengthFt, $"strayed {furthestPastJunctionFt:F0} ft from the C junction after reaching it");
    }

    /// <summary>
    /// A finished taxi route left on the arrival (its departure's, never cleared on takeoff) does not hide a route a rollout TAXI
    /// stores: OAK 28R B738 with a completed route assigned at touchdown, <c>TAXIAUTO 30</c> on the rollout drops it, so the
    /// controller's view, <c>CROSS 28L</c>, <c>HS W</c> and <c>GIVEWAY</c> all read the stored route.
    /// </summary>
    [Fact]
    public void CompletedEarlierRoute_OnTheRollout_GivesWayToTheStoredRoute()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        var finished = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        Assert.True(finished.IsComplete);
        aircraft.Ground.AssignedTaxiRoute = finished;
        SendUntilAccepted(spawned, "TAXIAUTO 30");

        TaxiRoute stored = aircraft.Phases!.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.Same(stored, aircraft.ActiveOrStoredTaxiRoute);
        HoldShortPoint crossing = stored.HoldShortPoints.First(h =>
            (h.Reason == HoldShortReason.RunwayCrossing) && RunwayIdentifier.Parse(h.TargetName!).Contains("28L")
        );
        CommandResult cross = spawned.Engine.SendCommand(Arrival, "CROSS 28L");
        Assert.True(cross.Success, cross.Message);
        Assert.True(crossing.IsCleared);
        CommandResult hs = spawned.Engine.SendCommand(Arrival, "HS W");
        Assert.True(hs.Success, hs.Message);
        Assert.Single(stored.HoldShortPoints, h => IsExplicitHoldShortOfW(h));
        CommandResult giveWay = spawned.Engine.SendCommand(Arrival, "GIVEWAY UAL123");
        Assert.True(giveWay.Success, giveWay.Message);
    }

    /// <summary>
    /// A <c>GIVEWAY</c> given on the rollout for the stored route waits for the route and survives its start: OAK 28R B738
    /// <c>TAXIAUTO 30</c>, then <c>GIVEWAY</c> to traffic standing at the route's end. The rollout and the exit run second for second
    /// as the same arrival's with no GIVEWAY (same exit, no extra braking on the runway); when the stored route starts the hold is
    /// still the give-way, and the aircraft slows for it instead of taxiing on.
    /// </summary>
    [Fact]
    public void GiveWay_OnTheStoredRoute_SurvivesTheRouteStart()
    {
        if ((RolloutWithTrafficAtTheRouteEnd(giveWay: false) is not { } control) || (RolloutWithTrafficAtTheRouteEnd(giveWay: true) is not { } held))
        {
            return;
        }

        Assert.Equal(control.Trace, held.Trace);
        Assert.Equal(control.Exit, held.Exit);
        SimulationEngine engine = held.Spawned.Engine;
        AircraftState aircraft = held.Spawned.Aircraft;
        Assert.Equal(HoldKind.GiveWay, aircraft.Ground.Hold?.Kind);
        Assert.Equal("UAL123", aircraft.Ground.Hold?.YieldTarget);
        // Unaged through the rollout and the exit: at most the one tick the route started in.
        double heldSeconds = aircraft.Ground.HoldElapsedSeconds;
        Assert.True(heldSeconds <= 1.0, $"the give-way aged {heldSeconds:F2} s before the route started");
        double startKts = aircraft.GroundSpeed;
        engine.TickOneSecond();
        control.Spawned.Engine.TickOneSecond();
        Assert.True(aircraft.Ground.HoldElapsedSeconds > heldSeconds, "the give-way is not judged once the route starts");
        for (int second = 1; second < 5; second++)
        {
            engine.TickOneSecond();
            control.Spawned.Engine.TickOneSecond();
        }

        double controlKts = control.Spawned.Aircraft.GroundSpeed;
        _output.WriteLine(
            $"started at {startKts:F1} kt, 5 s later {aircraft.GroundSpeed:F1} kt (control {controlKts:F1} kt), hold {aircraft.Ground.Hold?.Kind}"
        );
        Assert.True(aircraft.GroundSpeed < startKts, $"taxied on at {aircraft.GroundSpeed:F1} kt instead of giving way");
        Assert.True(aircraft.GroundSpeed < controlKts, $"held at {aircraft.GroundSpeed:F1} kt, no slower than the control at {controlKts:F1} kt");
    }

    /// <summary>
    /// A stored route started at the end of the exit leaves the aircraft as a TAXI issued right after the exit would: OAK 28R B738,
    /// <c>TAXIAUTO 30</c> on the rollout ticked into <see cref="TaxiingPhase"/>, against the same arrival held clear of the runway
    /// and then sent <c>TAXIAUTO 30</c>. The phase lists match field for field, the landing's phases gone, and the runway a
    /// readback falls back to is the departure runway, no longer the landing one.
    /// </summary>
    [Fact]
    public void StoredRouteStart_LeavesTheStateATaxiAfterTheExitLeaves()
    {
        if (AcceptOnRollout("OAK", "28R", "B738", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AircraftState rolledOn = accepted.Spawned.Aircraft;
        int started = SfoGroundHarness.TickUntil(accepted.Spawned.Engine, () => rolledOn.Phases?.CurrentPhase is TaxiingPhase, 200, null);
        Assert.True(started > 0, $"never rolled into the route: {rolledOn.Phases?.CurrentPhase?.Name}");

        if (Touchdown("OAK", "28R", "B738") is not { } control)
        {
            return;
        }

        TickUntilHoldingAfterExit(control);
        CommandResult taxi = control.Engine.SendCommand(Arrival, "TAXIAUTO 30");
        Assert.True(taxi.Success, taxi.Message);

        PhaseList phases = rolledOn.Phases!;
        PhaseList expected = control.Aircraft.Phases!;
        Assert.Equal(expected.Phases.Select(p => p.Name), phases.Phases.Select(p => p.Name));
        Assert.Equal(expected.CurrentIndex, phases.CurrentIndex);
        Assert.Null(phases.ActiveApproach);
        Assert.Null(phases.ClearedRunwayId);
        Assert.Null(phases.LandingClearance);
        Assert.Equal("30", phases.AssignedRunway?.Designator);
        Assert.Empty(PhaseListDifferences(expected, phases));
    }

    /// <summary>
    /// A TAXIAUTO's implied exit turns the rollout's side toward the route, as the other rollout TAXIs do: OAK 28R B738 parked left
    /// of the runway (the rollout's own side left), <c>TAXIAUTO 30</c> 6 s after touchdown implies an exit on the right, and the
    /// rollout's side is then right.
    /// </summary>
    [Fact]
    public void TaxiAuto_ImpliedExit_TurnsTheRolloutsSideTowardTheRoute()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: false) is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        LandingPhase landing = Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(ExitSide.Left, landing.InferredSide);
        WriteExitsAhead(aircraft);

        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXIAUTO 30");
        _output.WriteLine($"TAXIAUTO 30 -> {taxi.Success} {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        ExitPreference implied = aircraft.Phases!.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit instructed");
        Assert.Equal(ExitSide.Right, implied.Side);
        Assert.Equal(ExitSide.Right, landing.InferredSide);

        // J leaves 28R on both sides: a bare EXIT J, which drops the implied side, still takes it toward the route.
        CommandResult exit = spawned.Engine.SendCommand(Arrival, $"EXIT {implied.Taxiway}");
        Assert.True(exit.Success, exit.Message);
        (string taxiway, ExitSide? side, _) = ExitChosen(spawned.Engine, aircraft);
        Assert.Equal(implied.Taxiway, taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExitSide.Right, side);
    }

    /// <summary>
    /// A <c>GIVEWAY</c> on the rollout with no TAXI never stops the aircraft on the runway: OAK 28R B738 with traffic standing at a
    /// stand, the rollout and exit run second for second as with no GIVEWAY until the aircraft holds clear, the give-way pending
    /// (neither judged nor released) the whole time it is on the runway.
    /// </summary>
    [Fact]
    public void GiveWay_OnTheRolloutWithNoTaxi_NeverStopsTheAircraftOnTheRunway()
    {
        if ((Touchdown("OAK", "28R", "B738") is not { } control) || (Touchdown("OAK", "28R", "B738") is not { } held))
        {
            return;
        }

        (ShortFinalArrival.Spawned Spawned, List<string> Trace, string? Exit) plain = RolloutWithTraffic(
            control,
            false,
            () => control.Aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase
        );
        (ShortFinalArrival.Spawned Spawned, List<string> Trace, string? Exit) givingWay = RolloutWithTraffic(
            held,
            true,
            () => held.Aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase
        );

        Assert.Equal(plain.Trace, givingWay.Trace);
        Assert.Equal(plain.Exit, givingWay.Exit);
        Assert.NotEmpty(givingWay.Trace);
    }

    /// <summary>
    /// A bare <c>ER</c> on the side of the exit a rollout TAXI implied confirms it: OAK 28R B738 <c>TAXI C B W W1 30</c> implying C1
    /// on the right, then <c>ER</c> reads back "Exit right", keeps the stored route and the implied exit, and the aircraft turns off
    /// at C1 and rolls into the route.
    /// </summary>
    [Fact]
    public void BareSideExit_OnTheImpliedSide_KeepsTheRouteAndTheImpliedExit()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: true) is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        PhaseList phases = aircraft.Phases!;
        ExitPreference implied = phases.RequestedExit!;
        TaxiRoute stored = phases.TaxiRoute!;

        CommandResult right = spawned.Engine.SendCommand(Arrival, "ER");
        Assert.True(right.Success, right.Message);
        Assert.Equal("Exit right", right.Message);
        Assert.Same(stored, phases.TaxiRoute);
        Assert.Same(implied, phases.RequestedExit);
        Assert.Same(phases.TaxiImpliedExit, phases.RequestedExit);

        (string taxiway, ExitSide? side, _) = ExitChosen(spawned.Engine, aircraft);
        Assert.Equal("C1", taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExitSide.Right, side);
        int started = SfoGroundHarness.TickUntil(spawned.Engine, () => aircraft.Phases?.CurrentPhase is TaxiingPhase, 200, null);
        Assert.True(started > 0, $"never rolled into the route: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.NotNull(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>A bare <c>EL</c> against the right exit a rollout TAXI implied is a new exit instruction: the stored route is dropped.</summary>
    [Fact]
    public void BareSideExit_OnTheOtherSide_DropsTheRoute()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: true) is not { } spawned)
        {
            return;
        }

        CommandResult left = spawned.Engine.SendCommand(Arrival, "EL");
        Assert.True(left.Success, left.Message);
        Assert.Equal("Exit left", left.Message);
        Assert.Null(spawned.Aircraft.Phases!.TaxiRoute);
        Assert.Equal(ExitSide.Left, spawned.Aircraft.Phases.RequestedExit?.Side);
    }

    /// <summary>
    /// A rollout TAXI whose first taxiway is the exit it joins from names that taxiway once in every form of its readback: OAK 28R
    /// B738 <c>TAXI J C B W W1 30</c> 6 s after touchdown, with and without the controller's <c>ER J</c> before it (the exit clause
    /// leads only after it): the controller-facing message, and with the clause the pilot's terminal and spoken readback.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TaxiFromTheExitItNames_ReadsTheExitTaxiwayBackOnce(bool controllerNamedTheExit)
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: false) is not { } spawned)
        {
            return;
        }

        if (controllerNamedTheExit)
        {
            CommandResult er = spawned.Engine.SendCommand(Arrival, "ER J");
            Assert.True(er.Success, er.Message);
        }

        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI J C B W W1 30");
        PilotSpeechText? speech = taxi.PilotReadback?.Speech;
        _output.WriteLine($"TAXI J C B W W1 30 -> {taxi.Success} {taxi.Message} | {speech?.Terminal} | {speech?.Tts}");
        Assert.True(taxi.Success, taxi.Message);
        Assert.Equal("J", spawned.Aircraft.Phases!.RequestedExit?.Taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(taxi.Message ?? "", @"\bJ\b"));
        if (controllerNamedTheExit)
        {
            Assert.StartsWith("Exit right at J, ", taxi.Message, StringComparison.Ordinal);
            Assert.NotNull(speech);
            Assert.Single(
                System.Text.RegularExpressions.Regex.Matches(speech.Terminal, @"\bJ\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            );
            Assert.Single(
                System.Text.RegularExpressions.Regex.Matches(speech.Tts, @"\b(J|juliett?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            );
        }
    }

    /// <summary>
    /// A joining TAXI tries the controller's standing exit side first: OAK 28R B738 parked left, <c>EL</c> then
    /// <c>TAXI J C B W W1 30</c> 6 s after touchdown. J leaves 28R on both sides; the route is joined from the left J when it can be,
    /// and from the right J only when it cannot.
    /// </summary>
    [Fact]
    public void TaxiJoiningFromAnExit_TriesTheControllersStandingSideFirst()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: false) is not { } spawned)
        {
            return;
        }

        CommandResult left = spawned.Engine.SendCommand(Arrival, "EL");
        Assert.True(left.Success, left.Message);
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI J C B W W1 30");
        _output.WriteLine($"EL; TAXI J C B W W1 30 -> {taxi.Success} {taxi.Message}; exit {spawned.Aircraft.Phases?.RequestedExit?.Side}");
        Assert.True(taxi.Success, taxi.Message);

        // The left J leads back across 28R to the route, so the join falls back to the right J.
        Assert.Equal("J", spawned.Aircraft.Phases!.RequestedExit?.Taxiway, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExitSide.Right, spawned.Aircraft.Phases.RequestedExit?.Side);
    }

    /// <summary>
    /// The rollout TAXI's readback leads with a plain exit clause, "exit right at J", spoken "exit right at juliett": OAK 28R B738
    /// <c>ER J</c> then <c>TAXI J C B W W1 30</c> 6 s after touchdown.
    /// </summary>
    [Fact]
    public void RolloutTaxi_Readback_LeadsWithAPlainExitClause()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: false) is not { } spawned)
        {
            return;
        }

        CommandResult er = spawned.Engine.SendCommand(Arrival, "ER J");
        Assert.True(er.Success, er.Message);
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI J C B W W1 30");
        PilotSpeechText speech = taxi.PilotReadback?.Speech ?? throw new Xunit.Sdk.XunitException($"no readback: {taxi.Message}");
        _output.WriteLine($"{speech.Terminal} | {speech.Tts}");
        Assert.StartsWith("exit right at J, ", speech.Terminal, StringComparison.Ordinal);
        Assert.StartsWith("exit right at juliett, ", speech.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exit clause of a rollout TAXI's readback names the side and the taxiway and nothing else, on either side: no "when",
    /// "if able", "on" or "to" around the taxiway.
    /// </summary>
    [Theory]
    [InlineData(ExitSide.Right, "right")]
    [InlineData(ExitSide.Left, "left")]
    public void ImpliedExitReadback_NamesTheSideAndTheTaxiwayOnly(ExitSide side, string spokenSide)
    {
        var exit = new ExitPreference { Side = side, Taxiway = "J" };
        var taxi = new TaxiCommand(["J", "C", "B", "W", "W1"], [], Expedite: false, DestinationRunway: "30");

        PilotSpeechText speech = GroundCommandHandler.ImpliedExitReadback(exit, taxi) ?? throw new Xunit.Sdk.XunitException("no readback");

        _output.WriteLine($"{speech.Terminal} | {speech.Tts}");
        Assert.StartsWith($"exit {spokenSide} at J, ", speech.Terminal, StringComparison.Ordinal);
        Assert.StartsWith($"exit {spokenSide} at juliett, ", speech.Tts, StringComparison.Ordinal);
        foreach (string word in new[] { "when", "if", "able", "on", "to J", "to juliett" })
        {
            Assert.DoesNotContain($" {word} ", $" {speech.Terminal.Split(", ")[0]} ", StringComparison.Ordinal);
            Assert.DoesNotContain($" {word} ", $" {speech.Tts.Split(", ")[0]} ", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The exit clause leaves out only the clearance's leading exit taxiway: <c>TAXI J C J W</c> with J the exit reads back
    /// "C J W", its turn hints kept in step.
    /// </summary>
    [Fact]
    public void WithoutTaxiway_DropsOnlyTheLeadingExitTaxiway()
    {
        var taxi = new TaxiCommand(["J", "C", "J", "W"], [], Expedite: false, PathTurnHints: [null, TurnDirection.Left, TurnDirection.Right, null]);

        TaxiCommand rest = GroundCommandHandler.WithoutTaxiway(taxi, "J");

        Assert.Equal(["C", "J", "W"], rest.Path);
        Assert.Equal([TurnDirection.Left, TurnDirection.Right, null], rest.PathTurnHints!);
    }

    /// <summary>
    /// The exit clause leaves the exit taxiway in when the clearance leads with another taxiway: <c>TAXI C J W</c> with J the
    /// exit reads back "C J W", its turn hints kept in step.
    /// </summary>
    [Fact]
    public void WithoutTaxiway_KeepsTheExitTaxiwayWhenItDoesNotLead()
    {
        var taxi = new TaxiCommand(["C", "J", "W"], [], Expedite: false, PathTurnHints: [null, TurnDirection.Left, null]);

        TaxiCommand rest = GroundCommandHandler.WithoutTaxiway(taxi, "J");

        Assert.Equal(["C", "J", "W"], rest.Path);
        Assert.Equal([null, TurnDirection.Left, null], rest.PathTurnHints!);
    }

    /// <summary>
    /// A rollout TAXI's readback whose clearance names the exit taxiway again later drops it only where it leads: <c>TAXI J C J W</c>
    /// with J the exit reads back "exit right at J, taxi to runway 30 via C J W".
    /// </summary>
    [Fact]
    public void ImpliedExitReadback_RepeatingTheExitTaxiway_DropsItOnlyWhereItLeads()
    {
        var exit = new ExitPreference { Side = ExitSide.Right, Taxiway = "J" };
        var taxi = new TaxiCommand(["J", "C", "J", "W"], [], Expedite: false, DestinationRunway: "30");

        PilotSpeechText speech = GroundCommandHandler.ImpliedExitReadback(exit, taxi) ?? throw new Xunit.Sdk.XunitException("no readback");

        _output.WriteLine($"{speech.Terminal} | {speech.Tts}");
        Assert.StartsWith("exit right at J, ", speech.Terminal, StringComparison.Ordinal);
        Assert.Contains("via C J W", speech.Terminal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rollout TAXI whose clearance does not lead with the exit taxiway drops only the leading mention in either text: OAK 28R
    /// B738 <c>ER J</c> then <c>TAXI C F K J A B W 30</c> — joined from the exit J, whose route drives J C F K J C A B W W1, the
    /// J→A bridge through C, named earlier in the clearance. The echo follows the route driven: "Exit right at J, taxi via
    /// C F K J C A B W RWY 30" (the route's leading J dropped, its later one kept, the second C the clearance left out shown).
    /// The readback follows the clearance: "exit right at J, taxi to runway 30 via C F K J A B W". The two differ by that second
    /// C, so neither text can stand in for the other. The clearance names each lettered taxiway of the route's bridges (C→J
    /// through F and K, J→A through C, A→W through B): the shorter <c>TAXI C J W 30</c> leaves them out and is refused as not
    /// connecting (YAAT-899 R6), and from the exit J every J/D junction lies behind the aircraft.
    /// </summary>
    [Fact]
    public void RolloutTaxi_ExitTaxiwayNotFirst_DropsOnlyTheLeadingMention()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: false) is not { } spawned)
        {
            return;
        }

        CommandResult er = spawned.Engine.SendCommand(Arrival, "ER J");
        Assert.True(er.Success, er.Message);
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI C F K J A B W 30");
        PilotSpeechText? speech = taxi.PilotReadback?.Speech;
        _output.WriteLine($"TAXI C F K J A B W 30 -> {taxi.Success} {taxi.Message} | {speech?.Terminal} | {speech?.Tts}");
        Assert.True(taxi.Success, taxi.Message);
        Assert.Equal("J", spawned.Aircraft.Phases!.RequestedExit?.Taxiway, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("Exit right at J, taxi via C F K J C A B W RWY 30", taxi.Message);
        Assert.Equal("exit right at J, taxi to runway 30 via C F K J A B W", speech?.Terminal);
    }

    /// <summary>
    /// A rollout TAXI whose two cleared taxiways do not connect passes that refusal through rather than "unable to exit": OAK 28R
    /// B738 <c>ER J</c> then <c>TAXI C J W 30</c>, where J and W meet only through lettered taxiways the clearance left out
    /// (YAAT-899 R6). The refusal names J and W, the pair the first refusing exit's route could not bridge, as its pilot's
    /// spoken refusal does.
    /// </summary>
    [Fact]
    public void RolloutTaxi_WhoseClearedTaxiwaysDoNotConnect_PassesTheRefusalThrough()
    {
        if (RolloutOak28RAtSixSeconds(withTaxi: false) is not { } spawned)
        {
            return;
        }

        CommandResult er = spawned.Engine.SendCommand(Arrival, "ER J");
        Assert.True(er.Success, er.Message);
        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI C J W 30");
        _output.WriteLine($"TAXI C J W 30 -> {taxi.Success} {taxi.Message} | {taxi.PilotUnable?.Tts}");

        Assert.False(taxi.Success, taxi.Message);
        Assert.Equal(SegmentExpander.TaxiwaysDoNotConnectReason("J", "W"), taxi.Message);
        Assert.Equal(PilotResponder.BuildUnableTaxiwaysDoNotConnect(spawned.Aircraft, "J", "W"), taxi.PilotUnable);
    }

    /// <summary>
    /// A joining TAXI ranks the controller's standing side first even where the route lies the other way, then the side toward the
    /// route, then the rest (<see cref="GroundCommandHandler.JoinSideRank"/>). No OAK/SFO rollout stages a standing side that joins
    /// and differs from the side toward the route, so the rank is tested directly.
    /// </summary>
    [Fact]
    public void JoinSideRank_PutsTheControllersStandingSideBeforeTheSideTowardTheRoute()
    {
        ExitSide?[] sides = [null, ExitSide.Right, ExitSide.Left];

        List<ExitSide?> ordered = [.. sides.OrderBy(s => GroundCommandHandler.JoinSideRank(s, ExitSide.Left, ExitSide.Right))];
        List<ExitSide?> noStandingSide = [.. sides.OrderBy(s => GroundCommandHandler.JoinSideRank(s, null, ExitSide.Right))];

        Assert.Equal([ExitSide.Left, ExitSide.Right, null], ordered);
        Assert.Equal([ExitSide.Right, null, ExitSide.Left], noStandingSide);
    }

    /// <summary>
    /// A bare <c>ER</c> after the implied exit was given up (the stored route dropped with it) is a fresh exit instruction, not a
    /// confirmation of the given-up exit: SFO 19L A320 <c>TAXIAUTO $1</c> implying F1 on the right, a float gives F1 up, then
    /// <c>ER</c> is accepted as "Exit right" and the given-up exits stay as they were.
    /// </summary>
    [Fact]
    public void BareSideExit_AfterTheImpliedExitWasGivenUp_IsAFreshExitInstruction()
    {
        if (AcceptOnRollout("SFO", "19L", "A320", "TAXIAUTO $1") is not { } accepted)
        {
            return;
        }

        AircraftState aircraft = accepted.Spawned.Aircraft;
        PhaseList phases = aircraft.Phases!;
        Assert.Equal(ExitSide.Right, phases.RequestedExit?.Side);
        aircraft.IndicatedAirspeed += 60;
        accepted.Spawned.Engine.TickOneSecond();
        Assert.Null(phases.TaxiRoute);
        List<string> givenUp = [.. phases.GivenUpExitTaxiways.Order(StringComparer.OrdinalIgnoreCase)];
        _output.WriteLine($"given up: {string.Join(", ", givenUp)}; requested {phases.RequestedExit?.Side} {phases.RequestedExit?.Taxiway}");

        CommandResult right = accepted.Spawned.Engine.SendCommand(Arrival, "ER");

        Assert.True(right.Success, right.Message);
        Assert.Equal("Exit right", right.Message);
        Assert.Equal(givenUp, [.. phases.GivenUpExitTaxiways.Order(StringComparer.OrdinalIgnoreCase)]);
        Assert.Equal(ExitSide.Right, phases.RequestedExit?.Side);
        Assert.Null(phases.RequestedExit?.Taxiway);
    }

    /// <summary>
    /// An arrival rolling out under a <c>GIVEWAY</c> that is still pending (it never slows the aircraft on its landing runway) is
    /// landed traffic, not stopped traffic: OAK 28R B738 rolling out with a give-way to UAL123, then a takeoff clearance on 28R
    /// draws the landed-traffic advisory (7110.65 3-9-6.b), never "traffic stopped on runway".
    /// </summary>
    [Fact]
    public void TakeoffClearance_BehindAnArrivalRollingOutUnderAPendingGiveWay_IsTheLandedTrafficAdvisory()
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return;
        }

        AircraftState arrival = spawned.Aircraft;
        arrival.Ground.Hold = HoldDirective.GiveWay("UAL123");
        spawned.Engine.TickOneSecond();
        spawned.Engine.TickOneSecond();
        Assert.True(arrival.HasGiveWayPendingOnLandingRunway, $"no pending give-way in {arrival.Phases?.CurrentPhase?.Name}");
        Assert.True(arrival.GroundSpeed > 30, $"not rolling out: {arrival.GroundSpeed:F1} kt");

        RunwayInfo runway = spawned.Runway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var departure = new AircraftState
        {
            Callsign = "DEP1",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading + 90, 300 / GeoMath.FeetPerNm),
            TrueHeading = runway.TrueHeading,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway },
        };
        departure.Phases.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 1,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = runway.Designator,
                }
            )
        );
        departure.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        DispatchContext ctx = TestDispatch.Context(new Random(1), groundLayout: arrival.Ground.Layout, listAircraft: () => [departure, arrival]);

        RunwaySafetyAdvisor.WarnIfRunwayOccupiedForTakeoff(departure, runway, ctx);

        string warning = Assert.Single(departure.PendingWarnings);
        _output.WriteLine(warning);
        Assert.StartsWith("traffic on runway", warning, StringComparison.Ordinal);
        Assert.Contains(Arrival, warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A splice keeps what the route carries beyond its segments: a stored route with a spot line-up pull, a turn about and
    /// mandatory connectors, joined one segment in, keeps all three, the pull still on the same segment.
    /// </summary>
    [Fact]
    public void JoinStoredRoute_Spliced_KeepsTheSpotLineUpPullAndTheRoutesShape()
    {
        if (AcceptOnRollout("OAK", "28R", "B738", "TAXIAUTO 30") is not { } accepted)
        {
            return;
        }

        AirportGroundLayout layout = accepted.Spawned.Aircraft.Ground.Layout!;
        TaxiRoute source = accepted.Spawned.Aircraft.Phases!.TaxiRoute!;
        int pull = source.Segments.Count - 1;
        var stored = new TaxiRoute
        {
            Segments = source.Segments,
            HoldShortPoints = source.HoldShortPoints,
            SpotLineUpPullFromSegment = pull,
            MandatoryConnectorCount = 2,
            TurnAboutShape = TaxiTurnAboutShape.FromFarEnd,
            TurnAboutTargetNodeId = source.Segments[0].FromNodeId,
        };
        GroundNode cameFrom = layout.Nodes[source.Segments[0].FromNodeId];
        GroundNode joinAt = layout.Nodes[source.Segments[1].FromNodeId];
        var join = new GroundCommandHandler.StoredRouteJoin(joinAt, cameFrom, source.Segments[0].TaxiwayName, "28R");

        TaxiRoute joined = GroundCommandHandler.JoinStoredRoute(stored, join) ?? throw new Xunit.Sdk.XunitException("no join");

        Assert.Equal(source.Segments.Count - 1, joined.Segments.Count);
        Assert.Equal(pull - 1, joined.SpotLineUpPullFromSegment);
        Assert.Same(source.Segments[pull], joined.Segments[joined.SpotLineUpPullFromSegment!.Value]);
        Assert.Equal(2, joined.MandatoryConnectorCount);
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, joined.TurnAboutShape);
        Assert.Equal(source.Segments[0].FromNodeId, joined.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// OAK 28R B738 <c>TAXIAUTO 30</c> on the rollout with traffic standing at the stored route's end, and with
    /// <paramref name="giveWay"/> a <c>GIVEWAY</c> to it, ticked until the route starts (<see cref="RolloutWithTraffic"/>).
    /// </summary>
    private (ShortFinalArrival.Spawned Spawned, List<string> Trace, string? Exit)? RolloutWithTrafficAtTheRouteEnd(bool giveWay) =>
        AcceptOnRollout("OAK", "28R", "B738", "TAXIAUTO 30") is { } accepted
            ? RolloutWithTraffic(accepted.Spawned, giveWay, () => accepted.Spawned.Aircraft.Phases?.CurrentPhase is TaxiingPhase)
            : null;

    /// <summary>
    /// The arrival on its rollout with traffic standing at its stored route's end (else at the layout's first stand), and with
    /// <paramref name="giveWay"/> a <c>GIVEWAY</c> to it, ticked until <paramref name="done"/>: each second's phase, speed and
    /// position before then, and the exit taken. A give-way stays pending every second before then.
    /// </summary>
    private (ShortFinalArrival.Spawned Spawned, List<string> Trace, string? Exit) RolloutWithTraffic(
        ShortFinalArrival.Spawned spawned,
        bool giveWay,
        Func<bool> done
    )
    {
        SimulationEngine engine = spawned.Engine;
        AircraftState aircraft = spawned.Aircraft;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        TaxiRoute? stored = aircraft.Phases!.TaxiRoute;
        var traffic = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "A320",
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "LAX",
                FlightRules = "IFR",
            },
        };
        if (stored is not null)
        {
            // Head-on on the route just past the exit, so the give-way still has it in the way when the taxi starts.
            GroundNode start = layout.Nodes[stored.Segments[0].FromNodeId];
            GroundNode ahead = layout.Nodes[stored.Segments[Math.Min(2, stored.Segments.Count - 1)].ToNodeId];
            traffic.Position = ahead.Position;
            traffic.TrueHeading = new TrueHeading(GeoMath.BearingTo(ahead.Position, start.Position));
        }
        else
        {
            traffic.Position = layout.Nodes.Values.OrderBy(n => n.Id).First(n => n.Type == GroundNodeType.Parking).Position;
        }

        traffic.Ground.Layout = layout;
        engine.World.AddAircraft(traffic);
        if (giveWay && (stored is not null))
        {
            CommandResult result = engine.SendCommand(Arrival, "GIVEWAY UAL123");
            Assert.True(result.Success, result.Message);
        }
        else if (giveWay)
        {
            // GIVEWAY needs a route: a give-way standing with none (a deferred one firing) is set as the dispatch would set it.
            aircraft.Ground.Hold = HoldDirective.GiveWay("UAL123");
        }

        List<string> trace = [];
        string? exit = null;
        for (int second = 1; ; second++)
        {
            Assert.True(second <= 200, $"never got there: {aircraft.Phases?.CurrentPhase?.Name}");
            engine.TickOneSecond();
            Phase? phase = aircraft.Phases?.CurrentPhase;
            if (done())
            {
                return (spawned, trace, exit);
            }

            Assert.True(
                !giveWay || (aircraft.Ground.Hold?.Kind == HoldKind.GiveWay),
                $"give-way dropped at t+{second} in {phase?.Name} at {aircraft.GroundSpeed:F1} kt"
            );

            exit = aircraft.Ground.CurrentTaxiway ?? exit;
            _output.WriteLine($"t+{second} {phase?.Name} {aircraft.GroundSpeed:F1} kt on {aircraft.Ground.CurrentTaxiway}");
            trace.Add($"{phase?.Name} {aircraft.GroundSpeed:F3} kt {aircraft.Position.Lat:F7} {aircraft.Position.Lon:F7}");
        }
    }

    /// <summary>Where two phase lists' snapshots differ, their phases left out, the first few written out.</summary>
    private IReadOnlyList<SnapshotDivergence> PhaseListDifferences(PhaseList expected, PhaseList actual)
    {
        static System.Text.Json.Nodes.JsonNode Fields(PhaseList list)
        {
            System.Text.Json.Nodes.JsonNode node = JsonSerializer.SerializeToNode(list.ToSnapshot(), RecordingJsonOptions.Default)!;
            node.AsObject().Remove("Phases");
            return node;
        }

        IReadOnlyList<SnapshotDivergence> differences = SnapshotTreeDiff.CompareNodes(Fields(expected), Fields(actual));
        foreach (SnapshotDivergence difference in differences.Take(20))
        {
            _output.WriteLine($"phase lists differ at {difference.Path}: {difference.Left} vs {difference.Right}");
        }

        return differences;
    }

    private static bool IsExplicitHoldShortOfW(HoldShortPoint bar) =>
        (bar.Reason == HoldShortReason.ExplicitHoldShort) && string.Equals(bar.TargetName, "W", StringComparison.OrdinalIgnoreCase);

    /// <summary>A fresh engine restored from a snapshot of <paramref name="live"/>'s engine now, and its copy of the arrival.</summary>
    private static (SimulationEngine Engine, AircraftState Aircraft) Restored(ShortFinalArrival.Spawned live, string airport)
    {
        // The restore fills scenario state only into an engine that already has a scenario, and an engine with none never ticks.
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-short-final-arrival",
                ScenarioName = "Short-final arrival",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airport,
            },
        };
        engine.RestoreFromSnapshot(live.Engine.CaptureSnapshot());
        AircraftState restored = engine.World.FindAircraft(Arrival) ?? throw new Xunit.Sdk.XunitException("the arrival was not restored");
        return (engine, restored);
    }

    /// <summary>Where the snapshots of <paramref name="live"/> and <paramref name="restored"/> differ, the first few written out.</summary>
    private IReadOnlyList<SnapshotDivergence> SnapshotDifferences(AircraftState live, AircraftState restored)
    {
        IReadOnlyList<SnapshotDivergence> differences = SnapshotTreeDiff.CompareNodes(
            JsonSerializer.SerializeToNode(live.ToSnapshot(), RecordingJsonOptions.Default),
            JsonSerializer.SerializeToNode(restored.ToSnapshot(), RecordingJsonOptions.Default)
        );
        foreach (SnapshotDivergence difference in differences.Take(20))
        {
            _output.WriteLine($"live and restored differ at {difference.Path}: {difference.Left} vs {difference.Right}");
        }

        return differences;
    }

    /// <summary>
    /// OAK 28R B738 parked left of the runway, 6 s after touchdown; with <paramref name="withTaxi"/>, <c>TAXI C B W W1 30</c> sent
    /// then and accepted with C1 on the right implied.
    /// </summary>
    private ShortFinalArrival.Spawned? RolloutOak28RAtSixSeconds(bool withTaxi)
    {
        if (TouchdownOak28RParkedLeft() is not { } spawned)
        {
            return null;
        }

        for (int second = 0; second < 6; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        if (withTaxi)
        {
            CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI C B W W1 30");
            Assert.True(taxi.Success, taxi.Message);
            ExitPreference implied = spawned.Aircraft.Phases!.RequestedExit ?? throw new Xunit.Sdk.XunitException("no implied exit");
            Assert.Equal("C1", implied.Taxiway, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(ExitSide.Right, implied.Side);
        }

        return spawned;
    }

    /// <summary>
    /// Scenario auto-cross toggled off and on again once a route stored on the rollout has started (YAAT-665) never clears the
    /// landing runway's re-crossing: OAK 28R, a TAXI joining C north of 28R to runway 30. The aircraft still holds at the 28R bar
    /// until <c>CROSS 28R</c>.
    /// </summary>
    [Fact]
    public void AutoCross_ToggledOffAndOnAfterTheStoredRouteStarts_StillHoldsAtTheLandingRunwaysRecrossing()
    {
        if (StoreAcrossBothOakRunways(AutoCrossRunwayMode.All, []) is not { } stored)
        {
            return;
        }

        SimulationEngine engine = stored.Spawned.Engine;
        AircraftState aircraft = stored.Spawned.Aircraft;
        int started = SfoGroundHarness.TickUntil(engine, () => aircraft.Ground.AssignedTaxiRoute is not null, 200, null);
        Assert.True(started > 0, $"the stored route never started: {aircraft.Phases?.CurrentPhase?.Name}");
        TaxiRoute active = aircraft.Ground.AssignedTaxiRoute!;
        HoldShortPoint recross = active.HoldShortPoints.First(h => h.NodeId == stored.Recross.NodeId);
        HoldShortPoint other = active.HoldShortPoints.First(h => h.NodeId == stored.Other.NodeId);

        ToggleAutoCross(engine, autoCrossMode: AutoCrossRunwayMode.Off);
        Assert.False(other.IsCleared, $"auto-cross off left {other.TargetName} cleared");
        ToggleAutoCross(engine, autoCrossMode: AutoCrossRunwayMode.All);
        Assert.True(other.IsCleared, $"auto-cross on left {other.TargetName} uncleared");
        Assert.False(recross.IsCleared, "auto-cross on cleared the landing runway's re-crossing");

        int held = SfoGroundHarness.TickUntil(engine, () => aircraft.Phases?.CurrentPhase is HoldingShortPhase, 300, null);
        Assert.True(held > 0, $"never held short of 28R: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.Equal(recross.NodeId, Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase).HoldShort.NodeId);
        CommandResult cross = engine.SendCommand(Arrival, "CROSS 28R");
        Assert.True(cross.Success, cross.Message);
        int recrossSegment = active.Segments.FindIndex(s => s.ToNodeId == recross.NodeId);
        int past = SfoGroundHarness.TickUntil(engine, () => active.CurrentSegmentIndex > recrossSegment, 120, null);
        Assert.True(past > 0, $"never crossed 28R after CROSS 28R: {aircraft.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>
    /// Scenario auto-cross toggled while a route stored on the rollout still waits (YAAT-665) reaches that route as it reaches an
    /// assigned one: off un-clears the 28L bar auto-cross cleared, on clears it again, and neither clears the landing runway's
    /// re-crossing.
    /// </summary>
    [Fact]
    public void AutoCross_ToggledDuringTheRollout_UpdatesTheStoredRoute()
    {
        if (StoreAcrossBothOakRunways(AutoCrossRunwayMode.All, []) is not { } stored)
        {
            return;
        }

        SimulationEngine engine = stored.Spawned.Engine;
        AircraftState aircraft = stored.Spawned.Aircraft;
        Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Same(stored.Stored, aircraft.Phases?.TaxiRoute);
        Assert.True(stored.Other.IsCleared && stored.Other.ClearedByAutoCross, $"auto-cross did not clear {stored.Other.TargetName}");

        ToggleAutoCross(engine, autoCrossMode: AutoCrossRunwayMode.Off);
        Assert.False(stored.Other.IsCleared, $"auto-cross off left the stored route's {stored.Other.TargetName} bar cleared");
        Assert.False(stored.Other.ClearedByAutoCross);
        Assert.False(stored.Recross.IsCleared);

        ToggleAutoCross(engine, autoCrossMode: AutoCrossRunwayMode.All);
        Assert.True(stored.Other.IsCleared, $"auto-cross on left the stored route's {stored.Other.TargetName} bar uncleared");
        Assert.False(stored.Recross.IsCleared, "auto-cross on cleared the landing runway's re-crossing");
    }

    /// <summary>The landing runway's re-crossing is marked on the stored route, the mark survives a snapshot, and auto-cross respects it.</summary>
    [Fact]
    public void AutoCross_RecrossingMark_SurvivesASnapshot()
    {
        if (StoreAcrossBothOakRunways(AutoCrossRunwayMode.All, []) is not { } stored)
        {
            return;
        }

        AircraftState aircraft = stored.Spawned.Aircraft;
        Assert.True(stored.Recross.RecrossesLandingRunway, "the landing runway's re-crossing is not marked");
        Assert.False(stored.Other.RecrossesLandingRunway, $"the {stored.Other.TargetName} crossing is marked");

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), aircraft.Ground.Layout!);
        TaxiRoute route = restored.Phases?.TaxiRoute ?? throw new Xunit.Sdk.XunitException("the stored route was not restored");
        HoldShortPoint recross = route.HoldShortPoints.First(h => h.NodeId == stored.Recross.NodeId);
        Assert.True(recross.RecrossesLandingRunway, "the mark did not survive the snapshot");
        TaxiRouteAutoCross.Apply(route, AutoCrossRunwayMode.All, []);
        Assert.False(recross.IsCleared, "auto-cross cleared the restored re-crossing");
    }

    /// <summary>A rollout TAXI stored across both OAK runways: the route, its re-crossing of 28R, and its crossing of 28L.</summary>
    private sealed record StoredAcrossBoth(ShortFinalArrival.Spawned Spawned, TaxiRoute Stored, HoldShortPoint Recross, HoldShortPoint Other);

    /// <summary>
    /// Inactive runways only on a route stored on the rollout leaves the landing runway's re-crossing to a <c>CROSS</c> whether
    /// or not active runways are named (7110.65 3-10-9.b.2), while 28L, not active, is pre-cleared: OAK 28R, a TAXI joining C
    /// north of 28R to runway 30.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("30")]
    public void AutoCross_InactiveOnlyOnAStoredRolloutRoute_LeavesTheLandingRunwaysRecrossing(string activeAtOak)
    {
        if (
            StoreAcrossBothOakRunways(AutoCrossRunwayMode.InactiveOnly, activeAtOak.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            is not { } stored
        )
        {
            return;
        }

        Assert.False(stored.Recross.IsCleared, "Inactive runways only cleared the landing runway's re-crossing");
        Assert.True(stored.Other.IsCleared && stored.Other.ClearedByAutoCross, $"Inactive runways only left {stored.Other.TargetName} uncleared");
    }

    /// <summary>
    /// OAK 28R B738 under the auto-cross <paramref name="mode"/> with <paramref name="activeAtOak"/> the active runways at OAK,
    /// <c>TAXI C B W W1 30</c> 6 s after touchdown: stored behind the rollout's exit, the route joins C north of 28R, re-crosses
    /// 28R and crosses 28L.
    /// </summary>
    private StoredAcrossBoth? StoreAcrossBothOakRunways(AutoCrossRunwayMode mode, string[] activeAtOak)
    {
        if (Touchdown("OAK", "28R", "B738") is not { } spawned)
        {
            return null;
        }

        spawned.Engine.Scenario!.AutoCrossRunwayMode = mode;
        spawned.Engine.Scenario.ActiveRunways = ActiveRunways.Empty.With(
            "OAK",
            [.. activeAtOak.Select(runway => new ActiveRunway(runway, ActiveRunwayUse.Both))]
        );
        for (int second = 0; second < 6; second++)
        {
            spawned.Engine.TickOneSecond();
        }

        CommandResult taxi = spawned.Engine.SendCommand(Arrival, "TAXI C B W W1 30");
        _output.WriteLine($"TAXI C B W W1 30 -> {taxi.Success} {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute stored = spawned.Aircraft.Phases?.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        string dump = string.Join(", ", stored.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason},cleared={h.IsCleared})"));
        int start = stored.Segments[0].FromNodeId;
        List<HoldShortPoint> crossings = [.. stored.HoldShortPoints.Where(h => (h.Reason == HoldShortReason.RunwayCrossing) && (h.NodeId != start))];
        HoldShortPoint recross =
            crossings.FirstOrDefault(h => RunwayIdentifier.Parse(h.TargetName!).Overlaps(RunwayIdentifier.Parse("28R")))
            ?? throw new Xunit.Sdk.XunitException($"the stored route does not re-cross 28R: {dump}");
        HoldShortPoint other =
            crossings.FirstOrDefault(h => !RunwayIdentifier.Parse(h.TargetName!).Overlaps(RunwayIdentifier.Parse("28R")))
            ?? throw new Xunit.Sdk.XunitException($"the stored route crosses no other runway: {dump}");
        return new StoredAcrossBoth(spawned, stored, recross, other);
    }

    /// <summary>Flips the scenario's auto-cross mid-session as the server and a replayed toggle do.</summary>
    private static void ToggleAutoCross(SimulationEngine engine, AutoCrossRunwayMode autoCrossMode)
    {
        engine.Scenario!.AutoCrossRunwayMode = autoCrossMode;
        engine.World.ApplyAutoCrossToActiveTaxiRoutes(autoCrossMode, engine.Scenario.ActiveRunways);
    }

    /// <summary>
    /// The exit the rollout has committed to <see cref="ExitChoiceSeconds"/> after an exit instruction: taxiway, side and
    /// hold-short node.
    /// </summary>
    private (string Taxiway, ExitSide? Side, int HoldShortId) ExitChosen(SimulationEngine engine, AircraftState aircraft)
    {
        for (int second = 0; second < ExitChoiceSeconds; second++)
        {
            engine.TickOneSecond();
        }

        LandingPhase landing = Assert.IsType<LandingPhase>(aircraft.Phases?.CurrentPhase);
        ResolvedExitInfo exit = landing.CandidateExit ?? throw new Xunit.Sdk.XunitException($"no exit committed: {landing.CurrentState}");
        _output.WriteLine($"exit chosen: {exit.TaxiwayName} {exit.Side} hold short {exit.HoldShortNode.Id}");
        return (exit.TaxiwayName, exit.Side, exit.HoldShortNode.Id);
    }

    /// <summary>
    /// The arrival on the ground in its landing phase on OAK 28R, its remarks naming the stand furthest left of the runway, so the
    /// rollout's own exit pick prefers the left (<see cref="ArrivalExitSide"/>).
    /// </summary>
    private ShortFinalArrival.Spawned? TouchdownOak28RParkedLeft()
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "B738", Arrival, 1.0) is not { } spawned)
        {
            return null;
        }

        RunwayInfo runway = spawned.Runway;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        GroundNode stand =
            spawned
                .Aircraft.Ground.Layout!.Nodes.Values.Where(n =>
                    (n.Type == GroundNodeType.Parking)
                    && (n.Name is { } name)
                    && (FlightPlanParking.Parse($"/V/ PARKING {name}, 28R")?.Stand == name.ToUpperInvariant())
                )
                .MinBy(n => GeoMath.SignedCrossTrackDistanceNm(n.Position, threshold, runway.TrueHeading))
            ?? throw new Xunit.Sdk.XunitException("no OAK stand a parking remark can name");
        spawned.Aircraft.FlightPlan.InterfacilityRemarks = $"/V/ PARKING {stand.Name}, 28R";
        _output.WriteLine($"parked at {stand.Name}");

        int touchdown = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase is LandingPhase),
            300,
            null
        );
        Assert.True(touchdown > 0, $"never touched down: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");
        return spawned;
    }

    /// <summary>A runway hold-short node on <paramref name="taxiway"/> protecting <paramref name="runway"/>.</summary>
    private static bool IsRunwayBarOn(GroundNode node, string taxiway, string runway) =>
        (node.Type == GroundNodeType.RunwayHoldShort)
        && node.Edges.Any(e => e.MatchesTaxiway(taxiway))
        && (node.RunwayId is { } protects)
        && protects.Overlaps(RunwayIdentifier.Parse(runway));

    /// <summary>Puts the arrival back on final to its runway, <paramref name="distNm"/> out, its landing chain next on the same phase list.</summary>
    private static void RestageOnFinal(ShortFinalArrival.Spawned spawned, double distNm)
    {
        AircraftState aircraft = spawned.Aircraft;
        RunwayInfo runway = spawned.Runway;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        LatLon threshold = LandingThreshold.Resolve(runway, layout);
        aircraft.Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), distNm);
        aircraft.TrueHeading = runway.TrueHeading;
        aircraft.TrueTrack = runway.TrueHeading;
        aircraft.Altitude = GlideSlopeGeometry.AltitudeAtDistance(
            distNm,
            runway.ElevationFt,
            AircraftCategorization.Categorize(aircraft.AircraftType)
        );
        aircraft.IndicatedAirspeed = ShortFinalArrival.ApproachIas;
        aircraft.IsOnGround = false;
        PhaseList phases = aircraft.Phases!;
        phases.AssignedRunway = runway;
        phases.ReplaceUpcoming([
            new FinalApproachPhase { SkipInterceptCheck = true },
            new LandingPhase(),
            new RunwayExitPhase(),
            new HoldingAfterExitPhase(),
        ]);
        phases.AdvanceToNext(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
    }

    /// <summary>The exits the rollout can make now, as the test output's record of what a rollout TAXI was judged against.</summary>
    private void WriteExitsAhead(AircraftState aircraft)
    {
        if ((aircraft.Phases?.CurrentPhase is LandingPhase landing) && (landing.ListExitsAhead(aircraft) is { } rows))
        {
            _output.WriteLine(
                $"exits ahead at {aircraft.GroundSpeed:F1} kt: {string.Join(", ", rows.Select(r => $"{r.Taxiway} {r.Side} {r.DistanceFt} ft"))}"
            );
        }
    }

    private static List<int> RouteNodes(TaxiRoute route) => [.. route.Segments.Select(s => s.FromNodeId), route.Segments[^1].ToNodeId];

    /// <summary>Every warning and pilot terminal line the engine emits for the arrival from now on, in order.</summary>
    private static List<string> CaptureMessages(SimulationEngine engine)
    {
        List<string> messages = [];
        engine.WarningEmitted += (cs, text) =>
        {
            if (cs == Arrival)
            {
                messages.Add(text);
            }
        };
        engine.PilotSpeechEmitted += (cs, text) =>
        {
            if (cs == Arrival)
            {
                messages.Add(text.Terminal);
            }
        };
        return messages;
    }

    private static void AssertNoMissedExit(List<string> messages) =>
        Assert.DoesNotContain(
            messages,
            m => m.Contains("missed exit", StringComparison.OrdinalIgnoreCase) || m.Contains("unable E.", StringComparison.Ordinal)
        );

    /// <summary>Ticks until the arrival is out of its rollout and runway exit; returns the taxiway the runway exit last committed to.</summary>
    private string? TickOffTheRunway(SimulationEngine engine, AircraftState aircraft)
    {
        string? exitTaken = null;
        int off = SfoGroundHarness.TickUntil(
            engine,
            () =>
            {
                if (aircraft.Phases?.CurrentPhase is RunwayExitPhase exit)
                {
                    exitTaken = exit.ExitTaxiway;
                }

                return aircraft.Phases?.CurrentPhase is not (LandingPhase or RunwayExitPhase);
            },
            300,
            null
        );
        _output.WriteLine($"off the runway after {off} s at {exitTaken}, now {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(off > 0, $"never left the runway: {aircraft.Phases?.CurrentPhase?.Name}");
        return exitTaken;
    }

    /// <summary>The taxiways <paramref name="route"/> runs along, in order, each run of segments on one taxiway named once.</summary>
    private static List<string> RouteTaxiways(TaxiRoute route)
    {
        List<string> names = [];
        foreach (string name in route.Segments.Select(s => s.TaxiwayName))
        {
            if ((names.Count == 0) || !string.Equals(names[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Asserts <paramref name="expected"/> appears in <paramref name="actual"/> in order, other entries allowed between.</summary>
    private static void AssertInOrder(List<string> actual, List<string> expected)
    {
        int next = 0;
        foreach (string name in actual)
        {
            if ((next < expected.Count) && string.Equals(name, expected[next], StringComparison.OrdinalIgnoreCase))
            {
                next++;
            }
        }

        Assert.True(next == expected.Count, $"expected {string.Join(" ", expected)} in order in {string.Join(" ", actual)}");
    }

    private ShortFinalArrival.Spawned? Touchdown(string airport, string runway, string type)
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand(airport, runway, type, Arrival, 1.0) is not { } spawned)
        {
            return null;
        }

        int touchdown = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase is LandingPhase),
            300,
            null
        );
        Assert.True(touchdown > 0, $"never touched down: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");
        return spawned;
    }

    /// <summary>Sends <paramref name="command"/> once a second from touchdown until the rollout accepts it; a refusal changes nothing.</summary>
    private Accepted? AcceptOnRollout(string airport, string runway, string type, string command) =>
        Touchdown(airport, runway, type) is { } spawned ? SendUntilAccepted(spawned, command) : null;

    /// <summary>Sends <paramref name="command"/> to the arrival on its rollout once a second until it is accepted.</summary>
    private Accepted SendUntilAccepted(ShortFinalArrival.Spawned spawned, string command)
    {
        for (int second = 0; second < DispatchWindowSeconds; second++)
        {
            Assert.IsType<LandingPhase>(spawned.Aircraft.Phases?.CurrentPhase);
            double kts = spawned.Aircraft.GroundSpeed;
            CommandResult result = spawned.Engine.SendCommand(Arrival, command);
            _output.WriteLine($"t+{second} {command} at {kts:F1} kt -> {result.Success} {result.Message}");
            if (result.Success)
            {
                return new Accepted(spawned, result, kts);
            }

            spawned.Engine.TickOneSecond();
        }

        throw new Xunit.Sdk.XunitException($"{command} was never accepted on the rollout");
    }

    private static void TickUntilHoldingAfterExit(ShortFinalArrival.Spawned spawned)
    {
        int held = SfoGroundHarness.TickUntil(spawned.Engine, () => spawned.Aircraft.Phases?.CurrentPhase is HoldingAfterExitPhase, 300, null);
        Assert.True(held > 0, $"never held clear after the exit: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");
    }
}
