using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// Routes a controller action once, in Yaat.Sim. Every entry point — a fresh command from a controller or an AI
/// position (<see cref="Issue(ActionInput, IActionHost)"/>), a command applied back from a recording
/// (<see cref="Apply(RecordedCommand, IActionHost)"/>), a derived record (<see cref="ApplyRecorded(RecordedAction, IActionHost)"/>) —
/// goes through the same stages: strip the <c>AS</c> prefix; refuse a chain containing a non-compoundable verb; split a
/// compound of scoped specials into units and route each; classify; resolve the scope (a global command resolves
/// nothing, an aircraft-scoped one refuses identically on every run kind when the aircraft is missing) and the issuing
/// identity; run the <see cref="ArmTable"/> row. A fresh action's record is appended to the action log through
/// <see cref="SimulationEngine.RecordAction"/> (the one append site, gated by the run profile), accepted or not; an
/// action applied from a record is never re-recorded, and when its live verdict is known and differs from the replay's
/// a <c>replay-fidelity</c> warning is logged — the instrument that turns a bundle's snapshot/log disagreement into a
/// named cause.
/// </summary>
public sealed class ActionRouter
{
    private static readonly ILogger Log = SimLog.CreateLogger("ActionRouter");

    private readonly SimulationEngine _engine;

    internal ActionRouter(SimulationEngine engine)
    {
        _engine = engine;
    }

    /// <summary>The arm the most recent action took; null before the first.</summary>
    public ActionTrace? LastTrace { get; private set; }

    /// <summary>A fresh action on the bare host: consumers discarded.</summary>
    public ActionOutcome Issue(ActionInput input) => Issue(input, _engine.BareHost);

    public ActionOutcome Issue(ActionInput input, IActionHost host)
    {
        if (input.Baked is not null)
        {
            throw new ArgumentException("A fresh action carries no baked draws — use Apply for a recorded command", nameof(input));
        }

        return Route(input, host, record: null);
    }

    /// <summary>A recorded command on the bare host.</summary>
    public ActionOutcome Apply(RecordedCommand record) => Apply(record, _engine.BareHost);

    public ActionOutcome Apply(RecordedCommand record, IActionHost host)
    {
        var input = new ActionInput(record.Callsign, record.Command, record.ConnectionId, record.Initials, BakedDraws.Of(record));
        return Route(input, host, record);
    }

    /// <summary>A recorded action of any type on the bare host.</summary>
    public CommandResult ApplyRecorded(RecordedAction action) => ApplyRecorded(action, _engine.BareHost);

    /// <summary>
    /// Applies one recorded action: a command through <see cref="Apply(RecordedCommand, IActionHost)"/>, a derived
    /// record (spawn, live-traffic sample or removal, flight-plan amendment, beacon recycle, weather, setting,
    /// generators, STARS shared state, clearance, hold annotation, ERAM entry, ERAM CRR group, ASDE-X or SAID
    /// mutation, ASDE-X safety-logic push, strip request, CRC attendance, a <c>.AUTOTRACK</c> roster change) through
    /// its Sim applier with the host told what changed. A chat line and a diagnostic record apply
    /// nothing. A derived record the live room applied whose apply refuses here — its aircraft is gone, an ERAM entry's
    /// guard answers differently — logs a <c>replay-fidelity</c> warning like a command whose verdict changed.
    /// </summary>
    public CommandResult ApplyRecorded(RecordedAction action, IActionHost host)
    {
        switch (action)
        {
            case RecordedAircraftSpawn spawn:
                _engine.ApplyRecordedAircraftSpawn(spawn);
                if (_engine.World.FindAircraft(spawn.Aircraft.Callsign) is { } spawned)
                {
                    HandOverSpawn(host, spawned);
                }
                return Applied;
            case RecordedLiveTrafficSample sample:
                _engine.ApplyRecordedLiveTrafficSample(sample);
                if ((sample.SpawnState is not null) && (_engine.World.FindAircraft(sample.Callsign) is { } shadow))
                {
                    HandOverSpawn(host, shadow);
                }
                return Applied;
            case RecordedLiveTrafficRemoval removal:
                _engine.ApplyRecordedLiveTrafficRemoval(removal);
                host.OnAircraftDeleted(removal.Callsign, lastState: null);
                return Applied;
            case RecordedCommand command:
                return Apply(command, host).Result;
            case RecordedAmendFlightPlan amend:
                _engine.AmendFlightPlan(amend.Callsign, amend.Amendment);
                _engine.ReprintDepartureStripAfterAmendment(amend.Callsign, amend.StripId);
                _engine.DrainStateChangesInto(host);
                return Applied;
            case RecordedRequestNewBeaconCode recycle:
                _engine.RequestNewBeaconCode(recycle.Callsign, recycle.AssignedByFacilityId, recycle.AssignedBySectorId);
                return Applied;
            case RecordedWeatherChange weather:
                _engine.ApplyRecordedWeatherChange(weather);
                host.OnWeatherChanged();
                return Applied;
            case RecordedSettingChange setting:
                _engine.ApplySettingChange(setting);
                return Applied;
            case RecordedArrivalGeneratorsChange generators:
                _engine.ApplyGeneratorsJson(generators.GeneratorsJson);
                return Applied;
            default:
                CommandResult result = ApplyStateRecord(action, host);
                if (!result.Success)
                {
                    WarnOnRefusedRecord(action, result);
                }

                return result;
        }
    }

    /// <summary>A fresh derived record on the bare host.</summary>
    public CommandResult IssueDerived(RecordedAction action) => IssueDerived(action, _engine.BareHost);

    /// <summary>
    /// A derived record produced now rather than read back from the log — a CRC handler's shared-state, clearance,
    /// hold-annotation, ERAM, CRR-group, strip-request, ASDE-X / SAID, safety-logic or <c>.AUTOTRACK</c> write, or the live
    /// host's derived CRC attendance. Applied through the same body a replay uses and appended to the action log only when it applied, so
    /// the log never carries a write the room refused; a refusal here is the live verdict, not a fidelity break, and is
    /// not warned about.
    /// </summary>
    public CommandResult IssueDerived(RecordedAction action, IActionHost host)
    {
        CommandResult result = ApplyStateRecord(action, host);
        if (result.Success)
        {
            _engine.RecordAction(action);
        }

        return result;
    }

    /// <summary>
    /// The records of state a CRC handler writes plus the live host's recorded inputs — the derived attendance and a
    /// <c>.AUTOTRACK</c> roster change — applied through the one body each has, with what the body touched handed to
    /// the host before the verdict is, exactly as <see cref="Finish"/> does for a routed command.
    /// </summary>
    private CommandResult ApplyStateRecord(RecordedAction action, IActionHost host)
    {
        CommandResult result = ApplyStateRecordCore(action, host);
        _engine.DrainStateChangesInto(host);
        return result;
    }

    private CommandResult ApplyStateRecordCore(RecordedAction action, IActionHost host)
    {
        switch (action)
        {
            case RecordedStarsSharedStateChange shared:
                return ApplyToAircraft(shared.Callsign, ac => TrackEngine.ApplySharedState(ac, shared.TcpId, shared.State));
            case RecordedClearanceChange clearance:
                return ApplyToAircraft(clearance.Callsign, ac => ac.Clearance = AircraftClearance.FromSnapshot(clearance.Clearance));
            case RecordedHoldAnnotationChange hold:
                return ApplyToAircraft(
                    hold.Callsign,
                    ac =>
                        ac.HoldAnnotation = hold.HoldAnnotation is null
                            ? new AircraftHoldAnnotation()
                            : AircraftHoldAnnotation.FromSnapshot(hold.HoldAnnotation)
                );
            case RecordedEramEntry entry:
                return ApplyEramEntry(entry);
            case RecordedEramCrrGroup group:
                _engine.ApplyCrrGroup(group);
                return Applied;
            case RecordedAsdexMutation asdex:
                _engine.ApplyAsdexMutation(asdex, host);
                return Applied;
            case RecordedSaidMutation said:
                _engine.ApplySaidMutation(said, host);
                return Applied;
            case RecordedStripRequest request:
                return StripRequests.PrintRequestedStrip(_engine, request);
            case RecordedAsdexSafetyLogicChange safetyLogic:
                _engine.ApplyRecordedAsdexSafetyLogic(safetyLogic);
                return Applied;
            case RecordedAttendanceChange attendance:
                _engine.Attendance.Replace(attendance.AttendedPositionIds, _engine.Scenario?.ArtccConfig);
                return Applied;
            case RecordedAutoTrackChange change:
                return _engine.ApplyAutoTrackChange(change);
            default:
                return Applied;
        }
    }

    /// <summary>
    /// An aircraft a recorded action put into the world: the engine's spawn hooks first (what a spawn queues is engine
    /// state every run kind carries), then the host's tail. Same order as <see cref="ActionArms"/>' spawn arms and the
    /// pre-physics spawns, so a client learns the callsign before the PDC for it.
    /// </summary>
    private void HandOverSpawn(IActionHost host, AircraftState aircraft)
    {
        _engine.AfterAircraftSpawned(aircraft);
        host.OnAircraftSpawned(aircraft);
    }

    private CommandResult ApplyToAircraft(string callsign, Action<AircraftState> apply)
    {
        AircraftState? aircraft = _engine.FindAircraft(callsign);
        if (aircraft is null)
        {
            return ActionRefusals.AircraftNotFound(callsign);
        }

        apply(aircraft);
        return Applied;
    }

    private CommandResult ApplyEramEntry(RecordedEramEntry entry)
    {
        AircraftState? aircraft = _engine.FindAircraft(entry.Callsign);
        if (aircraft is null)
        {
            return ActionRefusals.AircraftNotFound(entry.Callsign);
        }

        TrackOwner? identity =
            (entry.IdentityCode is null) || (_engine.Scenario is null) ? null : TrackResolver.ResolveTcpToOwner(_engine.Scenario, entry.IdentityCode);
        return EramEntryEngine.Apply(aircraft, entry.Entry, identity);
    }

    private static readonly CommandResult Applied = new(true);

    private static void WarnOnRefusedRecord(RecordedAction record, CommandResult result)
    {
        Log.LogWarning(
            "replay-fidelity: {Record} at t={Seconds} was applied live but refused on replay — {Message}",
            record,
            record.ElapsedSeconds,
            result.Message ?? "(no message)"
        );
    }

    /// <summary>One pass through the router: the action, the host applying it, and the record it came from (null when fresh).</summary>
    private readonly record struct Routing(ActionInput Input, IActionHost Host, RecordedCommand? Record);

    /// <summary>
    /// The server's test for "this diverges a tape being played back": true when the arm that routing
    /// <paramref name="command"/> as a fresh action reaches carries <see cref="RecordingPolicy.Text"/>. That is the policy
    /// question, not a prediction that the log grows — <see cref="Finish"/> appends only when the engine also has a
    /// <see cref="SimulationEngine.Scenario"/>, and <see cref="SimulationEngine.RecordAction"/> only when its
    /// <see cref="RunProfile.RecordsActions"/> is set, so on a replay-profile engine no command appends anything and the
    /// policy is still the answer the server wants. It decomposes the text exactly as <see cref="Route"/> does —
    /// the <c>AS</c> prefix stripped first, the two chain refusals answering true because the refusal is itself recorded,
    /// a compound of scoped specials answering for its units — and reads the verdict off the same <see cref="ArmTable"/>
    /// row <see cref="Route"/> runs, so the two cannot disagree about a verb. A body that parses to nothing classifies to
    /// <see cref="RecordedCommandKind.Compound"/> and records, exactly as it does live. <c>WouldRecord_AgreesWithTheLog</c>
    /// checks the mirror against a log that actually grows, which is why it issues into a recording engine: on a
    /// replay-profile one the log stays empty for every verb and the comparison would prove nothing.
    /// </summary>
    public static bool WouldRecord(string command)
    {
        // Mirrors Route's decomposition below, in the same order: the two must be changed together, and
        // ActionRouterTests.WouldRecord_AgreesWithTheLog is the behavioural check that they still agree.
        (string? remainder, string? _) = TrackResolver.ExtractAsPrefix(command);

        if (CompoundPolicy.FindNonCompoundableInChain(remainder) is not null)
        {
            return true;
        }

        if (CompoundPolicy.FindTakeoffPairedWithImmediateTurn(remainder) is not null)
        {
            return true;
        }

        if (CompoundPolicy.TrySplitSpecialCompound(remainder, out List<CompoundUnit>? units))
        {
            return units.Any(unit => WouldRecord(unit.Text));
        }

        return ArmTable.For(RecordedCommandClassifier.Classify(remainder).Kind).Recording == RecordingPolicy.Text;
    }

    /// <summary>
    /// The one routing pass. <see cref="WouldRecord"/> mirrors this decomposition to answer whether a command records
    /// without running it; a change to the stages below belongs in both.
    /// </summary>
    private ActionOutcome Route(ActionInput input, IActionHost host, RecordedCommand? record)
    {
        var routing = new Routing(input, host, record);
        (string? remainder, string? asOverrideTcp) = TrackResolver.ExtractAsPrefix(input.Command);

        // A chain containing a rejection-set verb (PAUSE, spawn, flight-plan ops, room-wide commands) has no chained
        // semantics: routed as one compound it would swallow the tail or queue a block that no-ops at fire time.
        if (CompoundPolicy.FindNonCompoundableInChain(remainder) is { } nonCompoundable)
        {
            var refusal = new CommandResult(false, $"{CommandDescriber.DescribeCommand(nonCompoundable)} cannot be part of a chained command");
            var refusalTrace = new ActionTrace(RecordedCommandKind.Compound, ActionScope.Aircraft);
            return Finish(routing, refusal, refusalTrace, RecordingPolicy.Text, ctx: null);
        }

        // `CTO, R270` is the mis-spelling of the departure modifier `CTO MR270`: the turn is refused on the ground,
        // so the block would clear the aircraft for takeoff and drop half of what was typed. Refused whole, naming
        // the modifier. The sequential `CTO; R270` queues the turn until airborne and is left alone.
        if (CompoundPolicy.FindTakeoffPairedWithImmediateTurn(remainder) is { } pairedTurn)
        {
            var refusal = new CommandResult(false, CompoundPolicy.TakeoffPairedWithImmediateTurnMessage(pairedTurn));
            var refusalTrace = new ActionTrace(RecordedCommandKind.Compound, ActionScope.Aircraft);
            return Finish(routing, refusal, refusalTrace, RecordingPolicy.Text, ctx: null);
        }

        // A compound that concatenates a track/coordination/strip/TDLS command with ';'/',' cannot be classified as one
        // command — the single-command parser would swallow the separator tail as an argument. Route each unit.
        if (CompoundPolicy.TrySplitSpecialCompound(remainder, out List<CompoundUnit>? units))
        {
            return RouteUnits(routing, asOverrideTcp, units);
        }

        RecordedCommandClassifier.Classification classification = RecordedCommandClassifier.Classify(remainder);
        ActionArm arm = ArmTable.For(classification.Kind);
        var trace = new ActionTrace(arm.Kind, arm.Scope);

        // A kind that is never recorded (the session clock, bookmarks, the SHOW query) is never applied from a record
        // either: the legacy PAUSE / SIMRATE / BM records older recordings carry must not pause a rewind or re-add a
        // bookmark.
        if ((record is not null) && (arm.Recording == RecordingPolicy.Never))
        {
            string verb = CommandDescriber.DescribeCommand(classification.Parsed!);
            return Finish(routing, new CommandResult(false, $"{verb} is not applied from a recording"), trace, arm.Recording, ctx: null);
        }

        AircraftState? aircraft = null;
        if (arm.Scope == ActionScope.Aircraft)
        {
            aircraft = _engine.FindAircraft(input.Callsign);
            if (aircraft is null)
            {
                return Finish(routing, ActionRefusals.AircraftNotFound(input.Callsign), trace, arm.Recording, ctx: null);
            }
        }

        var ctx = new ArmContext
        {
            Engine = _engine,
            Host = host,
            Input = input,
            Remainder = remainder,
            AsOverrideTcp = asOverrideTcp,
            Parsed = classification.Parsed,
            Aircraft = aircraft,
            Identity = _engine.Scenario is null ? null : _engine.ResolveIdentity(input.ConnectionId, asOverrideTcp),
        };

        return Finish(routing, arm.Run(ctx), trace, arm.Recording, ctx);
    }

    /// <summary>
    /// Routes the units of a scoped-special compound in order — each is recorded on its own, so replay stays
    /// per-unit — and joins their messages the way an aviation compound's response reads: parallel units with
    /// <c>", "</c>, sequential blocks with <c>" ; then "</c>. A recorded compound's verdict is checked once, on the whole.
    /// </summary>
    private ActionOutcome RouteUnits(Routing routing, string? asOverrideTcp, List<CompoundUnit> units)
    {
        (ActionInput? input, IActionHost? host, RecordedCommand? record) = routing;
        string prefix = asOverrideTcp is null ? "" : $"AS {asOverrideTcp} ";
        var messages = new List<(int BlockIndex, string Message)>();
        bool allSuccess = true;
        ActionTrace last = default;
        foreach (CompoundUnit unit in units)
        {
            string unitCommand = prefix + unit.Text;
            ActionInput unitInput = input with { Command = unitCommand };
            RecordedCommand? unitRecord = record is null ? null : record with { Command = unitCommand, Accepted = null };
            ActionOutcome sub = Route(unitInput, host, unitRecord);
            allSuccess &= sub.Result.Success;
            if (!string.IsNullOrEmpty(sub.Result.Message))
            {
                messages.Add((unit.BlockIndex, sub.Result.Message));
            }

            last = sub.Trace;
        }

        string combined = string.Join(
            " ; then ",
            messages.GroupBy(m => m.BlockIndex).OrderBy(g => g.Key).Select(g => string.Join(", ", g.Select(m => m.Message)))
        );
        var result = new CommandResult(allSuccess, combined);
        if (record is not null)
        {
            WarnOnVerdictChange(record, result);
        }

        LastTrace = last;
        return new ActionOutcome(result, null, last);
    }

    private ActionOutcome Finish(Routing routing, CommandResult result, ActionTrace trace, RecordingPolicy recording, ArmContext? ctx)
    {
        LastTrace = trace;

        // Whatever the arm touched reaches the host before the result does — fresh or recorded, accepted or refused —
        // so a live command's broadcast still precedes its response and a playback pushes per record.
        _engine.DrainStateChangesInto(routing.Host);

        if (routing.Record is { } record)
        {
            WarnOnVerdictChange(record, result);
            return new ActionOutcome(result, null, trace);
        }

        ActionInput input = routing.Input;
        RecordedCommand? toRecord = null;
        if ((recording == RecordingPolicy.Text) && _engine.Scenario is { } scenario)
        {
            toRecord = new RecordedCommand(scenario.ElapsedSeconds, input.Callsign, input.Command, input.Initials, input.ConnectionId)
            {
                ReactionDelaySeconds = ctx?.ReactionDelaySeconds,
                SpawnJitterSeconds = ctx?.SpawnJitterSeconds,
                SpawnedAircraft = ctx?.SpawnedAircraft,
                IssuedAtUtc = ctx?.IssuedAtUtc,
                StripId = ctx?.StripId,
                Accepted = result.Success,
            };
            _engine.RecordAction(toRecord);
        }

        return new ActionOutcome(result, toRecord, trace);
    }

    /// <summary>
    /// A recorded command rejected on replay is usually expected — the live session rejected it too (a <c>TDLSS</c> to a
    /// parked aircraft, a verb the bare host refuses). Logged at Debug so a command that stopped taking effect because
    /// the replay layout drifted from the captured one can be surfaced; a verdict the record contradicts is a warning.
    /// </summary>
    private static void WarnOnVerdictChange(RecordedCommand record, CommandResult result)
    {
        if (!result.Success)
        {
            Log.LogDebug(
                "replay: '{Command}' for {Callsign} at t={Seconds} was rejected — {Message}",
                record.Command,
                record.Callsign,
                record.ElapsedSeconds,
                result.Message ?? "(no message)"
            );
        }

        if (record.Accepted is bool accepted && accepted != result.Success)
        {
            Log.LogWarning(
                "replay-fidelity: '{Command}' for {Callsign} at t={Seconds} was {LiveVerdict} live but {ReplayVerdict} on replay — {Message}",
                record.Command,
                record.Callsign,
                record.ElapsedSeconds,
                accepted ? "accepted" : "rejected",
                result.Success ? "accepted" : "rejected",
                result.Message ?? "(no message)"
            );
        }
    }
}
