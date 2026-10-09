using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Proves, against the real sim (real navdata and airport layouts, the real dispatcher), every phase a client quick-command
/// list is widened into: each case puts an aircraft in the phase and dispatches the command the quick entry sends. A quick
/// list never offers a command the sim would refuse, so a phase is only added to a list once its case here passes.
/// </summary>
public sealed class QuickCommandSimAcceptanceTests
{
    private const string Pusher = "PSH1";
    private const string Departure = "DEP1";
    private const string Arrival = "ARR1";
    private const int StageBudgetSeconds = 600;
    private const int PhaseBudgetSeconds = 300;

    /// <summary>The pattern entries the Go-around and Holding lists carry, each naming OAK 28R.</summary>
    public static TheoryData<string> PatternEntryVerbs() => ["ELD", "ERD", "ELB", "ERB", "EF"];

    /// <summary>
    /// The pattern entries the sim takes over the runway, where a go-around or low approach starts. A right base and a
    /// straight-in final are refused there by geometry (too close for base, short final), not by the phase.
    /// </summary>
    public static TheoryData<string> ClimbOutPatternEntryVerbs() => ["ELD", "ERD", "ELB"];

    private readonly ITestOutputHelper _output;

    public QuickCommandSimAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    // --- Pushing back: Push route… (PUSHM) and Push back to… (PUSH $spot) ---

    [Fact]
    public void PushRoute_DuringThePush_IsAccepted()
    {
        if (StartPush() is not { } ground)
        {
            return;
        }

        AssertAccepted(ground.Engine.SendCommand(Pusher, "PUSHM $7A $7B"), "PUSHM during Pushback");
    }

    /// <summary>A push already under way takes only a facing or tail amendment, so Push back to… stays off the Pushing back list.</summary>
    [Fact]
    public void PushbackTo_DuringThePush_IsRefused()
    {
        if (StartPush() is not { } ground)
        {
            return;
        }

        AssertRefused(ground.Engine.SendCommand(Pusher, "PUSH $7B"), "PUSH $spot during Pushback", "only face/tail amendment");
    }

    // --- Draw taxi route (a TAXI) in the line-up and rollout phases ---

    [Fact]
    public void Taxi_WhileLiningUp_IsAccepted()
    {
        if (HoldShortOf28L() is not { } staged)
        {
            return;
        }

        AssertAccepted(staged.Ground.Engine.SendCommand(Departure, "LUAW"), "LUAW at the 28L bar");
        int liningUp = SfoGroundHarness.TickUntil(staged.Ground.Engine, () => staged.Aircraft.Phases?.CurrentPhase is LineUpPhase, 30, null);
        Assert.True(liningUp > 0, $"never started lining up: {staged.Aircraft.Phases?.CurrentPhase?.Name}");

        AssertAccepted(staged.Ground.Engine.SendCommand(Departure, "TAXIAUTO $1"), "TAXI during LiningUp");
    }

    [Fact]
    public void Taxi_WhileLinedUpAndWaiting_IsAccepted()
    {
        if (HoldShortOf28L() is not { } staged)
        {
            return;
        }

        AssertAccepted(staged.Ground.Engine.SendCommand(Departure, "LUAW"), "LUAW at the 28L bar");
        int linedUp = SfoGroundHarness.TickUntil(
            staged.Ground.Engine,
            () => staged.Aircraft.Phases?.CurrentPhase is LinedUpAndWaitingPhase,
            PhaseBudgetSeconds,
            null
        );
        Assert.True(linedUp > 0, $"never lined up within {PhaseBudgetSeconds}s: {staged.Aircraft.Phases?.CurrentPhase?.Name}");

        AssertAccepted(staged.Ground.Engine.SendCommand(Departure, "TAXIAUTO $1"), "TAXI during LinedUpAndWaiting");
    }

    [Theory]
    [InlineData("Landing")]
    [InlineData("Runway Exit")]
    public void Taxi_OnTheRolloutAndExit_IsAccepted(string phaseName)
    {
        if (ArriveAtSfo19L() is not { } spawned)
        {
            return;
        }

        int reached = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase?.Name == phaseName),
            PhaseBudgetSeconds,
            null
        );
        Assert.True(reached > 0, $"never reached {phaseName} within {PhaseBudgetSeconds}s: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");

        AssertAccepted(spawned.Engine.SendCommand(Arrival, "TAXIAUTO $1"), $"TAXI during {phaseName}");
    }

    // --- Cross runway off a hold-short: CROSS for the runway to cross next, before the aircraft reaches its bar ---

    /// <summary>An OAK departure taxiing from SIG1 to 28L crosses a runway first; CROSS for it clears that bar while still moving.</summary>
    [Fact]
    public void CrossNextRunway_WhileTaxiing_ClearsTheCrossing()
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, zoa, 7, []);
        AssertAccepted(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 28L"), "TAXIAUTO 28L from SIG1");

        AssertCrossClearsTheNextCrossing(engine, AiTestFixture.Callsign, "while taxiing");
    }

    /// <summary>
    /// An OAK 28R arrival given a taxi to runway 30 crosses 28L on the way; CROSS for the runway to cross next is accepted
    /// on the rollout and in the exit, and clears that bar.
    /// </summary>
    [Theory]
    [InlineData("Landing")]
    [InlineData("Runway Exit")]
    public void CrossNextRunway_OnTheRolloutAndExit_ClearsTheCrossing(string phaseName)
    {
        if (ArriveAtOak28R() is not { } spawned)
        {
            return;
        }

        int reached = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase?.Name == phaseName),
            PhaseBudgetSeconds,
            null
        );
        Assert.True(reached > 0, $"never reached {phaseName} within {PhaseBudgetSeconds}s: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");
        AssertAccepted(spawned.Engine.SendCommand(Arrival, "TAXIAUTO 30"), $"TAXI 30 during {phaseName}");

        AssertCrossClearsTheNextCrossing(spawned.Engine, Arrival, $"during {phaseName}");
    }

    /// <summary>
    /// The holding-on-ground and the other rollout/exit phases, each standing in for the taxi phase of an OAK departure whose
    /// SIG1-to-28L route crosses a runway first: <c>CROSS</c> for that runway, as the widened Cross runway sends it, is
    /// accepted in every one and clears the bar (the sim takes it as a route amendment, whatever the phase).
    /// </summary>
    public static TheoryData<string> GroundHoldAndRunwayPhases() =>
        ["Holding In Position", "Holding After Exit", "Holding After Pushback", "Clearing Runway", "Rejected Takeoff"];

    [Theory]
    [MemberData(nameof(GroundHoldAndRunwayPhases))]
    public void CrossNextRunway_InTheGroundHoldsAndRunwayPhases(string phaseName)
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, zoa, 7, []);
        AssertAccepted(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 28L"), "TAXIAUTO 28L from SIG1");
        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        engine.TickSituation();
        string runway = aircraft.Situation.NextCrossingRunway ?? throw new Xunit.Sdk.XunitException("no runway to cross next");
        HoldShortPoint bar = aircraft.Ground.AssignedTaxiRoute!.HoldShortPoints.First(h => !h.IsCleared);
        var phases = new PhaseList { AssignedRunway = aircraft.Phases!.AssignedRunway };
        phases.Add(PhaseNamed(phaseName));
        aircraft.Phases = phases;
        Assert.Equal(phaseName, aircraft.Phases.CurrentPhase?.Name);

        AssertAccepted(engine.SendCommand(AiTestFixture.Callsign, $"CROSS {runway}"), $"CROSS {runway} during {phaseName}");

        Assert.True(bar.IsCleared, $"CROSS {runway} during {phaseName} left the {bar.TargetName} bar uncleared");
    }

    // --- Cancel takeoff clearance while taxiing with a stored takeoff clearance ---

    /// <summary>A departure cleared for takeoff while taxiing (the clearance stored for the runway) takes <c>CTOC</c>.</summary>
    [Fact]
    public void CancelTakeoff_WhileTaxiingClearedForTakeoff_IsAccepted()
    {
        if (TestArtccConfig.LoadZoa() is not { } zoa)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(ParkedAt29, zoa, 7, []);
        AssertAccepted(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30"), "TAXIAUTO 30 from parking 29");
        AircraftState aircraft = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, a => a.Phases?.CurrentPhase is TaxiingPhase, 120);
        AssertAccepted(engine.SendCommand(AiTestFixture.Callsign, "CTO"), "CTO while taxiing");
        Assert.True(aircraft.Phases?.DepartureClearance is { Type: ClearanceType.ClearedForTakeoff }, "the takeoff clearance was not stored");

        AssertAccepted(engine.SendCommand(AiTestFixture.Callsign, "CTOC"), "CTOC while taxiing");
    }

    // --- Pattern entries after a go-around or low approach, in the airspace-boundary holds and the AR anchor ---

    [Theory]
    [MemberData(nameof(ClimbOutPatternEntryVerbs))]
    public void PatternEntry_AfterAGoAround_IsAccepted(string verb)
    {
        if (ArriveAtOak28R() is not { } spawned)
        {
            return;
        }

        AssertAccepted(spawned.Engine.SendCommand(Arrival, "GA"), "GA on final");
        Assert.IsType<GoAroundPhase>(spawned.Aircraft.Phases?.CurrentPhase);

        AssertAccepted(spawned.Engine.SendCommand(Arrival, $"{verb} 28R"), $"{verb} during GoAround");
    }

    [Theory]
    [MemberData(nameof(ClimbOutPatternEntryVerbs))]
    public void PatternEntry_OnALowApproach_IsAccepted(string verb)
    {
        if (ArriveAtOak28R() is not { } spawned)
        {
            return;
        }

        AssertAccepted(spawned.Engine.SendCommand(Arrival, "LA"), "LA on final");
        int low = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.Phases?.CurrentPhase is LowApproachPhase,
            PhaseBudgetSeconds,
            null
        );
        Assert.True(low > 0, $"never flew the low approach within {PhaseBudgetSeconds}s: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");

        AssertAccepted(spawned.Engine.SendCommand(Arrival, $"{verb} 28R"), $"{verb} during LowApproach");
    }

    [Theory]
    [MemberData(nameof(PatternEntryVerbs))]
    public void PatternEntry_LevelBelowClassBravo_IsAccepted(string verb) => AssertPatternEntryInBoundaryHold(AirspaceHoldMode.LevelOff, verb);

    [Theory]
    [MemberData(nameof(PatternEntryVerbs))]
    public void PatternEntry_HoldingOutsideClassBravo_IsAccepted(string verb) => AssertPatternEntryInBoundaryHold(AirspaceHoldMode.Orbit, verb);

    [Theory]
    [MemberData(nameof(PatternEntryVerbs))]
    public void PatternEntry_InTheArAnchor_IsAccepted(string verb)
    {
        MilitaryRouteVariant variant = NavigationDatabase.Instance.GetMilitaryRoute("AR601")!.Variants[0];
        var aircraft = new AircraftState
        {
            Callsign = "ETHAN41",
            AircraftType = "K35R",
            Position = GeoMath.ProjectPoint(variant.Points[0].Position, new TrueHeading(0), 15),
            TrueHeading = new TrueHeading(180),
            Altitude = 20000,
            IndicatedAirspeed = 280,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(20000),
            },
        };
        AssertAccepted(Dispatch(aircraft, "CAR AR601"), "CAR AR601");
        Assert.IsType<AerialRefuelingAnchorPhase>(aircraft.Phases?.CurrentPhase);

        AssertAccepted(Dispatch(aircraft, $"{verb} 28R"), $"{verb} in the AR anchor");
    }

    // --- Ground follow and give way while following ---

    [Fact]
    public void FollowGround_WhileFollowing_IsAccepted()
    {
        if (FollowingAtSfo() is not { } staged)
        {
            return;
        }

        AssertAccepted(staged.Ground.Engine.SendCommand(Departure, "FOLLOWG LEAD2"), "FOLLOWG while Following");
    }

    [Fact]
    public void GiveWay_WhileFollowingWithATaxiRoute_IsAccepted()
    {
        if (FollowingAtSfo() is not { } staged)
        {
            return;
        }

        Assert.NotNull(staged.Aircraft.Ground.AssignedTaxiRoute);
        AssertAccepted(staged.Ground.Engine.SendCommand(Departure, "GW LEAD2"), "GW while Following");
    }

    // --- Landing and option clearances in the touch-and-go, stop-and-go and VFR follow phases ---
    //
    // Refused: none of these phases has a landing phase pending behind it for the clearance to replace, so the Pattern
    // list does not offer Cleared to land, Cleared for the option or Touch and go in them.

    [Theory]
    [InlineData("TG", "CLAND")]
    [InlineData("TG", "COPT")]
    [InlineData("TG", "TG")]
    [InlineData("SG", "CLAND")]
    [InlineData("SG", "COPT")]
    [InlineData("SG", "TG")]
    public void LandingClearance_InTheOptionClimbOut_IsRefused(string option, string clearance)
    {
        if (ArriveAtOak28R() is not { } spawned)
        {
            return;
        }

        AircraftState aircraft = spawned.Aircraft;
        AssertAccepted(spawned.Engine.SendCommand(Arrival, option), $"{option} on final");
        int climbing = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => !aircraft.IsOnGround && (aircraft.Phases?.CurrentPhase is TouchAndGoPhase or StopAndGoPhase),
            PhaseBudgetSeconds,
            null
        );
        Assert.True(climbing > 0, $"never airborne in the {option} phase within {PhaseBudgetSeconds}s: {aircraft.Phases?.CurrentPhase?.Name}");

        AssertRefused(
            spawned.Engine.SendCommand(Arrival, clearance),
            $"{clearance} during {aircraft.Phases?.CurrentPhase?.Name}",
            "pending approach"
        );
    }

    [Theory]
    [InlineData("CLAND")]
    [InlineData("COPT")]
    [InlineData("TG")]
    public void LandingClearance_InVfrFollow_IsRefused(string clearance)
    {
        AircraftState aircraft = AirborneNearOak(new VfrFollowPhase("LEAD1", null));
        Assert.Equal("VFR Follow", aircraft.Phases?.CurrentPhase?.Name);

        AssertRefused(Dispatch(aircraft, clearance), $"{clearance} during VFR Follow", "pending approach");
    }

    // --- Staging ---

    private void AssertPatternEntryInBoundaryHold(AirspaceHoldMode mode, string verb)
    {
        AircraftState aircraft = AirborneNearOak(
            new AirspaceBoundaryHoldPhase
            {
                AirspaceClass = AirspaceClass.Bravo,
                Ident = "SFO",
                Mode = mode,
            }
        );
        _output.WriteLine($"hold phase {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.IsType<AirspaceBoundaryHoldPhase>(aircraft.Phases?.CurrentPhase);

        AssertAccepted(Dispatch(aircraft, $"{verb} 28R"), $"{verb} during {aircraft.Phases?.CurrentPhase?.Name}");
    }

    // --- Exit left / right on final, while the exits-ahead list is stored (5 NM down to 1 NM) ---

    /// <summary>The Final list's pilot's-choice rows: a bare <c>EL</c>/<c>ER</c> on a 3 NM final, with the list stored, is accepted.</summary>
    [Theory]
    [InlineData("EL")]
    [InlineData("ER")]
    public void ExitPilotsChoice_OnFinalInsideFiveMiles_IsAccepted(string verb)
    {
        if (ListedOnOak30Final() is not { } final)
        {
            return;
        }

        AssertAccepted(final.Engine.SendCommand(Arrival, verb), $"{verb} on a 3 NM final");
    }

    /// <summary>
    /// Every row the Final list's flyouts offer: <c>EL</c>/<c>ER &lt;twy&gt;</c> for each listed exit, each on a fresh final, is
    /// accepted.
    /// </summary>
    [Fact]
    public void ExitNamed_OnFinalInsideFiveMiles_EveryListedRowIsAccepted()
    {
        if (ListedOnOak30Final() is not { } listing)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> rows = listing.Aircraft.Situation.ExitsAhead!;
        Assert.NotEmpty(rows);
        foreach (ExitAheadDto row in rows)
        {
            string command = $"{((row.Side == ExitSide.Left) ? "EL" : "ER")} {row.Taxiway}";
            ShortFinalArrival.Spawned final = ListedOnOak30Final()!;
            AssertAccepted(final.Engine.SendCommand(Arrival, command), $"{command} on a 3 NM final");
        }
    }

    /// <summary>
    /// A B738 on a 3 NM final to OAK 30 in an engine, cleared to land, after one tick, so the <c>Situation</c> step has stored
    /// its exits-ahead list; null when the OAK data is missing.
    /// </summary>
    private ShortFinalArrival.Spawned? ListedOnOak30Final()
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand("OAK", "30", "B738", Arrival, 3.0) is not { } final)
        {
            return null;
        }

        final.Aircraft.Targets.TargetSpeed = ShortFinalArrival.ApproachIas;
        final.Engine.TickOneSecond();

        Assert.IsType<FinalApproachPhase>(final.Aircraft.Phases?.CurrentPhase);
        Assert.NotNull(final.Aircraft.Situation.ExitsAhead);
        return final;
    }

    private static AircraftState AirborneNearOak(Phase phase)
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            // Eight miles out on the extended centerline: far enough for every entry, base and straight-in final included.
            Position = GeoMath.ProjectPoint(threshold, new TrueHeading((runway.TrueHeading.Degrees + 180) % 360), 8),
            TrueHeading = runway.TrueHeading,
            Altitude = 2500,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2500),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(phase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft));
        return aircraft;
    }

    private static readonly string ParkedAt29 = AiTestFixture.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"29\"");

    private static Phase PhaseNamed(string phaseName) =>
        phaseName switch
        {
            "Holding In Position" => new HoldingInPositionPhase(),
            "Holding After Exit" => new HoldingAfterExitPhase(),
            "Holding After Pushback" => new HoldingAfterPushbackPhase(),
            "Clearing Runway" => new ClearRunwayPhase(0, 0),
            "Rejected Takeoff" => new RejectedTakeoffPhase(0),
            _ => throw new ArgumentOutOfRangeException(nameof(phaseName), phaseName, "no phase staged under this name"),
        };

    private static CommandResult Dispatch(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
    }

    private void AssertAccepted(CommandResult result, string what)
    {
        _output.WriteLine($"{what} -> success={result.Success} msg={result.Message}");
        Assert.True(result.Success, $"{what} refused: {result.Message}");
    }

    /// <summary>
    /// Runs the situation step, reads the runway to cross next it stored, sends <c>CROSS</c> for it (what the quick list's
    /// Cross sends off a hold-short) and checks the first uncleared bar, the crossing, is now cleared.
    /// </summary>
    private void AssertCrossClearsTheNextCrossing(SimulationEngine engine, string callsign, string what)
    {
        AircraftState aircraft = engine.FindAircraft(callsign)!;
        Assert.IsNotType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        engine.TickSituation();
        string runway = aircraft.Situation.NextCrossingRunway ?? throw new Xunit.Sdk.XunitException($"no runway to cross next {what}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        HoldShortPoint bar = route.HoldShortPoints.First(h => !h.IsCleared);

        AssertAccepted(engine.SendCommand(callsign, $"CROSS {runway}"), $"CROSS {runway} {what}");

        Assert.True(bar.IsCleared, $"CROSS {runway} {what} left the {bar.TargetName} bar uncleared");
    }

    private void AssertRefused(CommandResult result, string what, string reason)
    {
        _output.WriteLine($"{what} -> success={result.Success} msg={result.Message}");
        Assert.False(result.Success, $"{what} was accepted: {result.Message}");
        Assert.Contains(reason, result.Message ?? "", StringComparison.Ordinal);
    }

    /// <summary>An SFO B738 pushed off F8 toward spot 7A, the tug moving; null when the SFO data is missing.</summary>
    private SfoGround? StartPush()
    {
        if (SfoGroundHarness.Build(_output, autoCross: false) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Pusher, "B738", "F8");
        AssertAccepted(ground.Engine.SendCommand(Pusher, "PUSH $7A"), "PUSH $7A from F8");
        int moving = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (aircraft.Phases?.CurrentPhase is PushbackPhase) && (aircraft.GroundSpeed >= 1.0),
            60,
            null
        );
        Assert.True(moving > 0, $"the push never got moving: {aircraft.Phases?.CurrentPhase?.Name}");
        return ground;
    }

    /// <summary>An SFO B738 taxied from spot 1 to the 28L departure bar; null when the SFO data is missing.</summary>
    private (SfoGround Ground, AircraftState Aircraft)? HoldShortOf28L()
    {
        if (SfoGroundHarness.Build(_output, autoCross: false) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAtSpot(ground, Departure, "B738", "1");
        AssertAccepted(ground.Engine.SendCommand(Departure, "TAXI A L F 28L"), "TAXI A L F 28L");
        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (aircraft.Phases?.CurrentPhase is HoldingShortPhase hold) && SfoGroundHarness.HoldShortMatches(hold.HoldShort, "28L"),
            StageBudgetSeconds,
            null
        );
        Assert.True(held > 0, $"never held short of 28L within {StageBudgetSeconds}s: {aircraft.Phases?.CurrentPhase?.Name}");
        return (ground, aircraft);
    }

    /// <summary>An SFO B738 cleared to taxi from spot 1, then following LEAD1, with LEAD2 parked nearby.</summary>
    private (SfoGround Ground, AircraftState Aircraft)? FollowingAtSfo()
    {
        if (SfoGroundHarness.Build(_output, autoCross: false) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAtSpot(ground, Departure, "B738", "1");
        SfoGroundHarness.SpawnAtSpot(ground, "LEAD1", "B738", "3");
        SfoGroundHarness.SpawnAtSpot(ground, "LEAD2", "B738", "2");
        AssertAccepted(ground.Engine.SendCommand(Departure, "TAXI A L F 28L"), "TAXI A L F 28L");
        AssertAccepted(ground.Engine.SendCommand(Departure, "FOLLOWG LEAD1"), "FOLLOWG LEAD1");
        Assert.IsType<FollowingPhase>(aircraft.Phases?.CurrentPhase);
        return (ground, aircraft);
    }

    private ShortFinalArrival.Spawned? ArriveAtSfo19L()
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        return ShortFinalArrival.SpawnClearedToLand("SFO", "19L", "A320", Arrival, 1.0);
    }

    private ShortFinalArrival.Spawned? ArriveAtOak28R()
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        return ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "B738", Arrival, 1.0);
    }
}
