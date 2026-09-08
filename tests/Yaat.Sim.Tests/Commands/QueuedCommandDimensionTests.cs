using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Pins the declared <c>CommandDefinition.QueuedDimension</c> table — what a command occupies while it is
/// still waiting in the queue, i.e. which incoming commands displace it before it fires.
///
/// Before the table, <c>CommandDescriber.GetQueuedCommandDimension</c> was a hand-maintained <c>or</c> chain
/// (pattern entries, approaches, holds, and — after #422 — MLT/MRT) and every other verb fell through
/// <c>ClassifyCommand</c> to <c>Immediate</c> → <c>None</c>. Meanwhile <c>CommandBlock.Dimensions</c> is built
/// from <c>GetCommandDimension</c>, which reports a tower verb as <c>All</c> and a surface verb as <c>Ground</c>. So in
/// <c>SplitBlockNonConflicting</c> the block reported a conflict in aggregate while every one of its commands
/// tested as non-conflicting, and the whole block survived the supersede — for ~50 verbs.
///
/// The cases below are the behaviours that were wrong, not a restatement of the table.
/// </summary>
public class QueuedCommandDimensionTests
{
    public QueuedCommandDimensionTests() => TestVnasData.EnsureInitialized();

    private static AircraftState Airborne() =>
        new()
        {
            Callsign = "N929AW",
            AircraftType = "BE33",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            TrueTrack = new TrueHeading(090),
            Altitude = 3000,
            IndicatedAirspeed = 120,
            IsOnGround = false,
        };

    /// <summary>
    /// The same airborne aircraft, operating at KOAK so <c>CommandDispatcher.ResolveRunway</c> can resolve
    /// "28R" — <c>Airborne()</c> files no airport, and an unresolvable runway would reject RWY for a reason
    /// that has nothing to do with the dimension under test. Kept separate so the shared factory stays
    /// airport-free for every other case.
    /// </summary>
    private static AircraftState AirborneAtOakland()
    {
        var ac = Airborne();
        ac.AirportId = "KOAK";
        return ac;
    }

    private static DispatchContext Ctx(bool preserveConditionals) =>
        TestDispatch.Context(Random.Shared, validateDctFixes: false, preserveConditionals: preserveConditionals);

    private static void Dispatch(string text, AircraftState ac, bool preserveConditionals)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx(preserveConditionals));
        Assert.True(result.Success, $"dispatch failed for '{text}': {result.Message}");
    }

    /// <summary>True when a not-yet-fired block still holds a command of <paramref name="type"/>.</summary>
    private static bool StillQueued(AircraftState ac, CanonicalCommandType type) =>
        ac.Queue.Blocks.Any(b =>
            !b.IsApplied && (b.ParsedCommands ?? []).Any(c => c is not UnsupportedCommand && CommandDescriber.ToCanonicalType(c) == type)
        );

    /// <summary>Queues "AT 5000 {queued}", then issues {incoming} as a fresh command.</summary>
    private static AircraftState QueueThen(string queued, string incoming) => QueueThen(Airborne(), queued, incoming);

    /// <summary>
    /// As <see cref="QueueThen(string, string)"/>, on a caller-supplied aircraft — the runway cases need one
    /// operating at KOAK so the designator resolves.
    /// </summary>
    private static AircraftState QueueThen(AircraftState ac, string queued, string incoming)
    {
        Dispatch($"AT 5000 {queued}", ac, preserveConditionals: false);
        Dispatch(incoming, ac, preserveConditionals: false);
        return ac;
    }

    // ---------------------------------------------------------------------------------------------------
    // The family the finding named: pattern modifiers are lateral plans while they wait.
    // 7110.65 §3-8-1 lists EXTEND DOWNWIND / MAKE SHORT APPROACH / CIRCLE THE AIRPORT / MAKE LEFT THREE-SIXTY
    // under SEQUENCE/SPACING APPLICATION — each adjusts the horizontal path flown, so a fresh vector replaces
    // the plan. TryMakeTurn in particular never rejects: with no Phases it builds its own PhaseList and starts
    // the turn on any airborne aircraft, so a surviving queued L360 would spin the aircraft off the new vector.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("TD", CanonicalCommandType.TurnDownwind)]
    [InlineData("EXT", CanonicalCommandType.ExtendPattern)]
    [InlineData("SA", CanonicalCommandType.MakeShortApproach)]
    [InlineData("L360", CanonicalCommandType.MakeLeft360)]
    [InlineData("P270", CanonicalCommandType.Plan270)]
    [InlineData("CA", CanonicalCommandType.CircleAirport)]
    public void FreshVector_CancelsQueuedPatternModifier(string verb, CanonicalCommandType type)
    {
        var ac = QueueThen(verb, "FH 270");
        Assert.False(StillQueued(ac, type), $"the fresh FH vector should have cancelled the queued {verb}");
    }

    [Fact]
    public void AltitudeAssignment_LeavesQueuedPatternModifierAlone()
    {
        // The other half of the contract: a queued lateral plan occupies ONLY the lateral axis, so an
        // altitude assignment issued on the way to the trigger must not disturb it.
        var ac = QueueThen("EXT", "CM 7000");
        Assert.True(StillQueued(ac, CanonicalCommandType.ExtendPattern), "CM must not cancel a queued EXT — different axis");
    }

    // ---------------------------------------------------------------------------------------------------
    // Landing options are landing clearances, not annotations (7110.65 §3-8-1 phraseology block, §3-8-2
    // "arriving aircraft"). They were None, which did not preserve them: TrySetupOptionClearance ->
    // ReplaceApproachEnding hard-rejects with no pending terminal, so surviving a vector only converted a
    // clean warned supersede into a fire-time rejection that also aborts the rest of the chain.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("TG", CanonicalCommandType.TouchAndGo)]
    [InlineData("SG", CanonicalCommandType.StopAndGo)]
    [InlineData("LA", CanonicalCommandType.LowApproach)]
    [InlineData("COPT", CanonicalCommandType.ClearedForOption)]
    public void FreshVector_CancelsQueuedLandingOption(string verb, CanonicalCommandType type)
    {
        var ac = QueueThen(verb, "FH 270");
        Assert.False(StillQueued(ac, type), $"the fresh FH vector should have cancelled the queued {verb}");
    }

    // ---------------------------------------------------------------------------------------------------
    // The Ground bit. 7110.65 §5-8-2.a REQUIRES the initial heading be assigned before departure when a
    // departing aircraft is to be vectored off the runway ("FLY RUNWAY HEADING / TURN LEFT HEADING (degrees)").
    // If a takeoff clearance were Lateral, a controller complying with §5-8-2.a would delete their own
    // clearance. §3-7-2 taxi clearances and §5-6-2 vectors are disjoint clearance domains.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("CTO", CanonicalCommandType.ClearedForTakeoff)]
    [InlineData("LUAW", CanonicalCommandType.LineUpAndWait)]
    [InlineData("HS 28R", CanonicalCommandType.HoldShort)]
    [InlineData("PUSH", CanonicalCommandType.Pushback)]
    public void DepartureHeading_LeavesQueuedSurfaceClearanceAlone(string verb, CanonicalCommandType type)
    {
        var ac = QueueThen(verb, "FH 270");
        Assert.True(StillQueued(ac, type), $"a departure heading must not cancel the queued {verb} (7110.65 §5-8-2.a)");
    }

    [Fact]
    public void AltitudeAssignment_LeavesQueuedSurfaceClearanceAlone()
    {
        var ac = QueueThen("HS 28R", "CM 7000");
        Assert.True(StillQueued(ac, CanonicalCommandType.HoldShort), "an altitude assignment must not cancel a queued hold-short");
    }

    // ---------------------------------------------------------------------------------------------------
    // ...but a surface clearance still supersedes a queued surface clearance. This is the regression the
    // Ground bit could have introduced: a ground verb fires as Ground, and Ground & Ground is not None, so
    // the queued Ground value stays reachable by the only family that should reach it. It matters on the
    // reaction-delay path, where DeferForReaction re-dispatches with PreserveConditionals: true and the
    // All/None clear-everything fast path is skipped, leaving the per-command QueuedDimension test decisive.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void FreshSurfaceClearance_ReachesAQueuedSurfaceClearance()
    {
        // Asserted on the classifier rather than end-to-end because a fresh HS/TAXI needs a real ground
        // layout, and a layout-dependent test silently skips when the fixture is absent — which would hide
        // exactly this regression. The end-to-end direction is covered by
        // DepartureHeading_LeavesQueuedSurfaceClearanceAlone above.
        var freshTaxi = CommandParser.Parse("TAXI 28R").Value!;
        var queuedHoldShort = CommandParser.Parse("HS 28R").Value!;
        var freshVector = CommandParser.Parse("FH 270").Value!;

        var queued = CommandDescriber.GetQueuedCommandDimension(queuedHoldShort);
        Assert.Equal(CommandDimension.Ground, queued);

        // A surface clearance must still be able to displace a queued surface clearance. A ground verb fires
        // as Ground and the queued value is Ground, so the two overlap and the queued clearance stays
        // reachable. This matters on the reaction-delay path: DeferForReaction re-dispatches with
        // PreserveConditionals: true, which skips the clear-everything fast path and leaves this test decisive.
        Assert.NotEqual(CommandDimension.None, CommandDescriber.GetCommandDimension(freshTaxi) & queued);

        // ...while a vector cannot reach it at all. This is the whole point of the separate bit.
        Assert.Equal(CommandDimension.None, CommandDescriber.GetCommandDimension(freshVector) & queued);
    }

    // ---------------------------------------------------------------------------------------------------
    // RWY is a lateral re-plan, not a surface clearance. TryAssignRunway (GroundCommandHandler) carries no
    // ground guard: airborne it re-assigns the arrival runway and clears a pending approach clearance held
    // for a different runway, because the approach clearance names the runway it is issued for (7110.65
    // §3-10-5.c). That is lateral work (plus the Ground half, for the on-the-ground departure-runway
    // branch) and nothing more — a runway re-assignment must not cancel queued altitude or speed work.
    // TAXIAUTO was the same gap from the other side: until 2026-09-08 it was missing from IsGroundCommand, so
    // it fired as None and the fast path wiped the queue on its way to a rejection. It is in the predicate now,
    // and both cases below assert the taxi clearance behaviour it inherits from that membership.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RunwayAssignment_LeavesQueuedSpeedAlone()
    {
        // Through the compound helper, which is the production path (ActionArms dispatches every controller
        // command through DispatchCompound) and asserts the dispatch succeeded — so the queued speed surviving
        // is the outcome of an APPLIED runway assignment, not of one that was harmlessly rejected.
        var ac = AirborneAtOakland();
        Dispatch("AT 5000 SPD 180", ac, preserveConditionals: false);
        Dispatch("RWY 28R", ac, preserveConditionals: false);

        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
        Assert.True(StillQueued(ac, CanonicalCommandType.Speed), "a runway assignment must not cancel a queued speed");
    }

    [Fact]
    public void RunwayAssignment_CancelsQueuedLateralPlan()
    {
        // The other half: RWY does claim the lateral axis, so a queued pattern modifier goes. This pins the
        // Lateral bit rather than catching a regression — it passed before too, back when RWY read None and
        // ClearConflictingBlocks wiped the queue wholesale.
        var ac = AirborneAtOakland();
        Dispatch("AT 5000 EXT", ac, preserveConditionals: false);
        Dispatch("RWY 28R", ac, preserveConditionals: false);

        Assert.False(StillQueued(ac, CanonicalCommandType.ExtendPattern), "a runway assignment supersedes a queued lateral plan");
    }

    [Fact]
    public void FreshVector_CancelsQueuedRunwayAssignment()
    {
        // The production-visible half of the same bit: while it waits, a queued RWY holds the lateral axis, so
        // a fresh vector must take it out. It survived a vector before, because the block's aggregate
        // Dimensions came from GetCommandDimension and read None.
        var ac = QueueThen(AirborneAtOakland(), "RWY 28R", "FH 270");

        Assert.False(StillQueued(ac, CanonicalCommandType.AssignRunway), "the fresh FH vector should have cancelled the queued RWY");
    }

    [Fact]
    public void AltitudeAssignment_LeavesQueuedRunwayAssignmentAlone()
    {
        // ...and only the lateral axis: an altitude issued on the way to the trigger leaves it queued.
        var ac = QueueThen(AirborneAtOakland(), "RWY 28R", "CM 7000");

        Assert.True(StillQueued(ac, CanonicalCommandType.AssignRunway), "CM must not cancel a queued RWY — different axis");
    }

    [Theory]
    [InlineData("TAXIAUTO 28R")]
    [InlineData("TAXI 28R")]
    public void RejectedSurfaceClearance_LeavesQueueIntact(string text)
    {
        // A taxi clearance to an airborne aircraft must fail without side effects. No ground verb has a
        // phase-less arm in ApplyCommandCore (they all live in TryApplyTowerCommandCore), so the dry run's
        // ApplyCommand comes back NoDispatcherArm — which DryRunApplyCommand otherwise reads as "cannot
        // validate, assume valid". Dispatch then enqueues the block, ApplyBlock rejects it for real, and the
        // first-block failure path clears the WHOLE queue. The dry-run ground guard is what keeps the
        // rejection ahead of the clear. Asserted on DispatchCompound because that is where the dry run is;
        // CommandDispatcher.Dispatch routes ground verbs straight into it.
        var ac = Airborne();
        Dispatch("AT 5000 SPD 180", ac, preserveConditionals: false);

        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, Ctx(false));

        Assert.False(result.Success, $"{text} must reject an airborne aircraft, got: {result.Message}");
        Assert.True(result.NoDispatcherArm, $"the rejection must carry the no-arm flag, got: {result.Message}");
        Assert.Contains("on the ground", result.Message);
        Assert.True(
            StillQueued(ac, CanonicalCommandType.Speed),
            $"a rejected '{text}' must not have destroyed the queue (rejection: {result.Message})"
        );
    }

    [Fact]
    public void FreshTaxiAuto_ReachesAQueuedSurfaceClearance()
    {
        // TAXIAUTO is a taxi clearance, so it must displace a queued surface clearance exactly as TAXI does
        // (see FreshSurfaceClearance_ReachesAQueuedSurfaceClearance for why this is asserted on the
        // classifier rather than end-to-end).
        var freshTaxiAuto = CommandParser.Parse("TAXIAUTO 28R").Value!;
        var queuedHoldShort = CommandParser.Parse("HS 28R").Value!;

        Assert.NotEqual(
            CommandDimension.None,
            CommandDescriber.GetCommandDimension(freshTaxiAuto) & CommandDescriber.GetQueuedCommandDimension(queuedHoldShort)
        );
    }

    // ---------------------------------------------------------------------------------------------------
    // CrossFix declared the wrong axis. 7110.65 §4-2-5.b NOTE 1: "If altitude to 'maintain' is changed or
    // restated ... previously issued altitude restrictions are canceled." Queued CFIX read Lateral (via
    // ClassifyCommand -> Navigation) while it fires as Vertical [| Speed], so a restated altitude did NOT
    // cancel it — the case §4-2-5.b actually governs. The vector direction happened to behave correctly
    // even then, because the block's aggregate Dimensions (Vertical, from GetCommandDimension) never
    // overlapped an incoming Lateral and the per-command test was never reached; it is pinned here so the
    // two sides cannot drift apart again. DispatchCrossFix never truncates or replaces the route.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Vector_LeavesQueuedCrossingRestrictionAlone()
    {
        var ac = QueueThen("CFIX SUNOL 8000", "FH 270");
        Assert.True(StillQueued(ac, CanonicalCommandType.CrossFix), "a vector must not cancel a queued crossing restriction");
    }

    [Fact]
    public void AltitudeAssignment_CancelsQueuedCrossingRestriction()
    {
        var ac = QueueThen("CFIX SUNOL 8000", "DM 5000");
        Assert.False(StillQueued(ac, CanonicalCommandType.CrossFix), "a restated altitude cancels the crossing restriction (§4-2-5.b NOTE 1)");
    }

    // ---------------------------------------------------------------------------------------------------
    // The two verbs whose dimension depends on which optional argument was given, so no table value can
    // describe them and they stay as code in GetQueuedCommandDimension.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BareExpedite_OccupiesNoAxis_ButExpediteWithAltitudeIsVertical()
    {
        var bare = CommandParser.Parse("EXP");
        var withAltitude = CommandParser.Parse("EXP 5000");
        Assert.NotNull(bare.Value);
        Assert.NotNull(withAltitude.Value);

        Assert.Equal(CommandDimension.None, CommandDescriber.GetQueuedCommandDimension(bare.Value!));
        Assert.Equal(CommandDimension.Vertical, CommandDescriber.GetQueuedCommandDimension(withAltitude.Value!));
    }

    // ---------------------------------------------------------------------------------------------------
    // Structural invariant: a command cannot occupy an axis while it waits that it does not seize when it
    // fires. This is what catches a misdeclared table row — the compiler only forces a value to exist, not
    // for it to be right. (ForceSpeedFinal is in the list because SPEEDF parses to SpeedCommand(Force: true)
    // and a table row of None there would silently have dropped its Speed axis.)
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("FH 270")]
    [InlineData("CM 7000")]
    [InlineData("SPD 210")]
    [InlineData("SPEEDF 180")]
    [InlineData("EXT")]
    [InlineData("TD")]
    [InlineData("L360")]
    [InlineData("TG")]
    [InlineData("CTO")]
    [InlineData("LUAW")]
    [InlineData("HS 28R")]
    [InlineData("PUSH")]
    [InlineData("TAXI 28R")]
    [InlineData("TAXIAUTO 28R")]
    [InlineData("RWY 28R")]
    [InlineData("CFIX SUNOL 8000")]
    [InlineData("EXP 5000")]
    [InlineData("HPPL")]
    [InlineData("ELD")]
    public void QueuedDimension_IsNeverBroaderThanFiredDimension(string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.NotNull(parsed.Value);
        Assert.IsNotType<UnsupportedCommand>(parsed.Value);

        var queued = CommandDescriber.GetQueuedCommandDimension(parsed.Value!);
        var fired = CommandDescriber.GetCommandDimension(parsed.Value!);

        Assert.Equal(CommandDimension.None, queued & ~fired);
    }

    /// <summary>
    /// Every registry entry declares a queued dimension the compiler cannot check the shape of: a value
    /// outside the defined flags would silently never match an incoming command.
    /// </summary>
    [Fact]
    public void EveryRegistryEntry_DeclaresAKnownDimension()
    {
        const CommandDimension Known = CommandDimension.All;
        var bad = CommandRegistry.All.Values.Where(d => (d.QueuedDimension & ~Known) != CommandDimension.None).ToList();

        Assert.Empty(bad);
    }

    // ---------------------------------------------------------------------------------------------------
    // The registry-wide form of the same invariant. The curated theory above pins the verbs a reader needs
    // to see; this sweep catches the ones nobody listed. A verb in no family predicate and no ClassifyCommand
    // arm fires as None while its registry row declares a real queued axis — "occupies the surface plan while
    // it waits, seizes nothing when it fires" — and that None also trips the clear-everything fast path in
    // ClearConflictingBlocks, so the queue is wiped on the way to a command that claims no axis at all.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryCommandType_QueuedDimensionIsNeverBroaderThanFired()
    {
        var violators = new List<string>();
        var unconstructible = new List<string>();

        foreach (var type in ParsedCommandDummies.ConcreteTypes())
        {
            var dummy = ParsedCommandDummies.Create(type);
            if (dummy is UnsupportedCommand)
            {
                continue;
            }

            // A type no dummy can be built for is not a pass: it would leave its row unchecked and defeat the
            // guardrail silently. Fail on it too, the way CommandDescriberCompletenessTests does.
            if (dummy is null)
            {
                unconstructible.Add(type.Name);
                continue;
            }

            var queued = CommandDescriber.GetQueuedCommandDimension(dummy);
            var fired = CommandDescriber.GetCommandDimension(dummy);
            if ((queued & ~fired) != CommandDimension.None)
            {
                violators.Add($"{type.Name} (queued {queued}, fired {fired})");
            }
        }

        Assert.True(
            violators.Count == 0,
            "queued dimension broader than fired — the verb occupies an axis while it waits that it never seizes: " + string.Join(", ", violators)
        );
        Assert.True(
            unconstructible.Count == 0,
            "ParsedCommandDummies.Create cannot build (extend MakeDummyArg): " + string.Join(", ", unconstructible)
        );
    }

    // ---------------------------------------------------------------------------------------------------
    // What each family seizes when it fires. A surface clearance takes the surface plan and nothing else:
    // 7110.65 §3-7-2 taxi clearances, the departure clearance's altitude (§4-3-2.e) and its initial heading
    // (§5-8-2.a) are disjoint instruments, so a taxi never amends airborne work. The exit verbs are taxi
    // instructions too (§3-10-9.a/.b) — they sit in the tower family only because the tower issues them.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("TAXI 28R")]
    [InlineData("PUSH")]
    [InlineData("HS 28R")]
    [InlineData("CROSS 28R")]
    [InlineData("EL D")]
    [InlineData("ATXI 28R")]
    public void SurfaceClearance_SeizesTheGroundAxisOnly(string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        Assert.Equal(CommandDimension.Ground, CommandDescriber.GetCommandDimension(parsed.Value!));
    }

    // ...while the clearances that commit an aircraft to a runway own every axis from that point: GO releases
    // a stopped departure into its takeoff roll (§3-8-2 — it is then a departing aircraft), CTOPP is a takeoff
    // clearance (§3-11-2.a) and LAND is a landing clearance (§3-11-6.a).
    [Theory]
    [InlineData("GO")]
    [InlineData("CTOPP")]
    [InlineData("LAND @H1")]
    public void RunwayCommitment_SeizesEveryAxis(string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        Assert.Equal(CommandDimension.All, CommandDescriber.GetCommandDimension(parsed.Value!));
    }

    // ...and the two re-plans that take the lateral axis alone. APT/DEST replaces the destination, and
    // ChangeDestinationCommand's handler clears the arrival procedure state with it — a route amendment
    // (§4-2-5.a.3), which is lateral work and nothing more. FOLLOW installs VfrFollowPhase: also a lateral
    // plan, and one that accepts altitude and speed adjustments without being cancelled by them
    // (VfrFollowPhase.CanAcceptCommand), so it must not claim those axes on the way in either.
    [Theory]
    [InlineData("APT KSFO")]
    [InlineData("FOLLOW")]
    public void LateralReplan_SeizesTheLateralAxisOnly(string text)
    {
        var parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        Assert.Equal(CommandDimension.Lateral, CommandDescriber.GetCommandDimension(parsed.Value!));
    }

    // ---------------------------------------------------------------------------------------------------
    // End-to-end on the real OAK layout, on the shape that actually reaches the queue. A BARE surface
    // clearance never does: with a ground phase active, DispatchWithPhase hands the TAXI to
    // TryApplyTowerCommand and DispatchCompound returns that result directly, so ClearConflictingBlocks is
    // never called at all. On that path the clear runs only for a compound of more than one block — the
    // Blocks.Count > 1 guard in CommandDispatcher.cs:244-247, on the union GetCompoundDimensions reports — so a chained
    // ground compound is where the fired dimension bites, and where the union of two ground verbs used to
    // add up to All and take the clear-everything fast path through queued airborne work.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>The chained surface clearance under test: two ground verbs, so the dispatcher reaches the queue.</summary>
    private const string TaxiChain = "TAXI D; CROSS 28L";

    private static AirportGroundLayout? OaklandLayout() => new TestAirportGroundData().GetLayout("OAK");

    /// <summary>A jet parked at OAK NEW7 in <see cref="AtParkingPhase"/> — the state a TAXI is issued from.</summary>
    private static AircraftState ParkedAtNew7(AirportGroundLayout layout)
    {
        var parking = layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot) && string.Equals(n.Name, "NEW7", StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(parking);

        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = parking.Position,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    private static void DispatchOnGround(string text, AircraftState ac, AirportGroundLayout layout, bool preserveConditionals)
    {
        var parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        var ctx = TestDispatch.Context(Random.Shared, validateDctFixes: false, groundLayout: layout, preserveConditionals: preserveConditionals);
        var result = CommandDispatcher.DispatchCompound(parsed.Value!, ac, ctx);
        Assert.True(result.Success, $"dispatch failed for '{text}': {result.Message}");
    }

    [Fact]
    public void ChainedTaxiClearance_LeavesQueuedAirborneBlockAlone()
    {
        var layout = OaklandLayout();
        if (layout is null)
        {
            return;
        }

        var ac = ParkedAtNew7(layout);
        DispatchOnGround("AT 5000 SPD 180", ac, layout, preserveConditionals: false);
        DispatchOnGround(TaxiChain, ac, layout, preserveConditionals: false);

        Assert.True(StillQueued(ac, CanonicalCommandType.Speed), "a taxi clearance must not cancel queued airborne work (§3-7-2)");
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("queue cleared by", StringComparison.Ordinal));
    }

    [Fact]
    public void ChainedTaxiClearance_LeavesUntriggeredAirborneBlockAlone_OnReactionDelayPath()
    {
        var layout = OaklandLayout();
        if (layout is null)
        {
            return;
        }

        var ac = ParkedAtNew7(layout);
        DispatchOnGround("AT 5000 SPD 180; CM 7000", ac, layout, preserveConditionals: false);

        // The chained second block carries no trigger of its own, so the reaction-delay path below cannot
        // spare it as a pending conditional: SimulationEngine.DeferForReaction re-dispatches with
        // PreserveConditionals: true, which skips the clear-everything fast path and leaves the per-command
        // dimension test decisive for this block.
        var climb = ac.Queue.Blocks.FirstOrDefault(b =>
            (b.ParsedCommands ?? []).Any(c =>
                c is not UnsupportedCommand && CommandDescriber.ToCanonicalType(c) == CanonicalCommandType.ClimbMaintain
            )
        );
        Assert.NotNull(climb);
        Assert.Null(climb.Trigger);

        DispatchOnGround(TaxiChain, ac, layout, preserveConditionals: true);

        Assert.True(StillQueued(ac, CanonicalCommandType.ClimbMaintain), "a taxi clearance must not cancel a queued altitude (§3-7-2 vs §4-3-2.e)");
    }

    [Fact]
    public void ChainedTaxiClearance_StillDisplacesQueuedSurfaceClearance()
    {
        // The half the narrowing must not break: a taxi clearance still supersedes queued surface work.
        // This passes on both sides of the change — before, the All/None fast path wiped the whole queue;
        // after, the incoming Ground overlaps the queued hold-short's Ground and the per-block test drops it.
        var layout = OaklandLayout();
        if (layout is null)
        {
            return;
        }

        var ac = ParkedAtNew7(layout);
        DispatchOnGround("AT 5000 HS 28L", ac, layout, preserveConditionals: false);
        DispatchOnGround(TaxiChain, ac, layout, preserveConditionals: false);

        Assert.False(StillQueued(ac, CanonicalCommandType.HoldShort), "a fresh taxi clearance supersedes a queued hold-short");
    }

    // ---------------------------------------------------------------------------------------------------
    // The lateral half of the same contract, on the reaction-delay path: DeferForReaction re-dispatches with
    // PreserveConditionals: true, which skips the clear-everything fast path, so the per-command test decides.
    // A destination change is a route amendment and takes the lateral axis; a queued altitude is on another
    // axis and must survive it.
    //
    // A pin, not a regression catcher: this case is green on both sides of APT's None → Lateral change
    // (verified by reverting the arm). With the fast path skipped, an incoming None overlaps nothing either,
    // so the queued altitude survived for the wrong reason before. It is here so the right reason is asserted.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void LateralReplan_LeavesUntriggeredAltitudeAlone_OnReactionDelayPath()
    {
        var ac = Airborne();
        Dispatch("AT 5000 SPD 180; CM 7000", ac, preserveConditionals: false);
        Dispatch("APT KSFO", ac, preserveConditionals: true);

        Assert.True(StillQueued(ac, CanonicalCommandType.ClimbMaintain), "a destination change must not cancel a queued altitude — different axis");
    }

    // ---------------------------------------------------------------------------------------------------
    // Restore. CommandBlock.Dimensions IS serialized, so a queue restored from a rewind, a session restore or
    // a bug bundle carries whatever the writing build computed — a surface block written before this change
    // comes back claiming All, and would wipe airborne work the first time a fresh command tested against it.
    // RehydrateRestoredBlock re-derives the aggregate from the reparsed commands, so the live table wins.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RestoredSurfaceBlock_TakesItsDimensionsFromTheLiveTable()
    {
        var parsed = CommandParser.Parse("HS 28L");
        Assert.True(parsed.IsSuccess, $"parse failed for 'HS 28L': {parsed.Reason}");

        // The snapshot an older build would have written: this block's own source text, and All for the
        // aggregate because every ground verb fired All then.
        var dto = new CommandBlockDto
        {
            Commands = [new TrackedCommandDto { Type = (int)CommandDescriber.ClassifyCommand(parsed.Value!), IsComplete = false }],
            IsApplied = false,
            TriggerMet = false,
            TriggerClosestApproach = double.MaxValue,
            TriggerMissed = false,
            IsWaitBlock = false,
            WaitRemainingSeconds = 0,
            WaitRemainingDistanceNm = 0,
            Description = CommandDescriber.DescribeCommand(parsed.Value!),
            NaturalDescription = CommandDescriber.DescribeNatural(parsed.Value!),
            SourceCommandText = "HS 28L",
            Dimensions = (int)CommandDimension.All,
        };

        var block = CommandBlock.FromSnapshot(dto);
        Assert.Equal(CommandDimension.All, block.Dimensions);

        Assert.True(
            CommandDispatcher.RehydrateRestoredBlock(block, Airborne(), Ctx(preserveConditionals: true)),
            "the block must rehydrate from its source text"
        );
        Assert.Equal(CommandDimension.Ground, block.Dimensions);
    }
}
