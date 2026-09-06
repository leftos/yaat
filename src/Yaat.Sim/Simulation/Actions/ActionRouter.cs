using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

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

    /// <summary>A fresh action on the bare host: slots refused, consumers discarded.</summary>
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
    /// generators, STARS shared state, clearance, hold annotation, ERAM entry) through its Sim applier with the host
    /// told what changed, and a record of host-owned state (an ASDE-X or SAID mutation, a CRR group) through the
    /// host's slot. A chat line and a diagnostic record apply nothing. A derived record the live room applied whose
    /// apply refuses here — its aircraft is gone, an ERAM entry's guard answers differently — logs a
    /// <c>replay-fidelity</c> warning like a command whose verdict changed.
    /// </summary>
    public CommandResult ApplyRecorded(RecordedAction action, IActionHost host)
    {
        switch (action)
        {
            case RecordedAircraftSpawn spawn:
                _engine.ApplyRecordedAircraftSpawn(spawn);
                if (_engine.World.FindAircraft(spawn.Aircraft.Callsign) is { } spawned)
                {
                    host.OnAircraftSpawned(spawned);
                }
                return Applied;
            case RecordedLiveTrafficSample sample:
                _engine.ApplyRecordedLiveTrafficSample(sample);
                if ((sample.SpawnState is not null) && (_engine.World.FindAircraft(sample.Callsign) is { } shadow))
                {
                    host.OnAircraftSpawned(shadow);
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
                host.OnFlightPlanAmended(amend.Callsign);
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
            case RecordedAsdexMutation asdex:
                host.ApplyRecordedAsdexMutation(asdex);
                return Applied;
            case RecordedSaidMutation said:
                host.ApplyRecordedSaidMutation(said);
                return Applied;
            default:
                var result = ApplyStateRecord(action, host);
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
    /// hold-annotation, ERAM or CRR-group write. Applied through the same body a replay uses and appended to the
    /// action log only when it applied, so the log never carries a write the room refused; a refusal here is the
    /// live verdict, not a fidelity break, and is not warned about.
    /// </summary>
    public CommandResult IssueDerived(RecordedAction action, IActionHost host)
    {
        var result = ApplyStateRecord(action, host);
        if (result.Success)
        {
            _engine.RecordAction(action);
        }

        return result;
    }

    /// <summary>The records of state a CRC handler writes, applied through the one body each has.</summary>
    private CommandResult ApplyStateRecord(RecordedAction action, IActionHost host)
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
                host.ApplyRecordedEramCrrGroup(group);
                return Applied;
            default:
                return Applied;
        }
    }

    private CommandResult ApplyToAircraft(string callsign, Action<AircraftState> apply)
    {
        var aircraft = _engine.FindAircraft(callsign);
        if (aircraft is null)
        {
            return ActionRefusals.AircraftNotFound(callsign);
        }

        apply(aircraft);
        return Applied;
    }

    private CommandResult ApplyEramEntry(RecordedEramEntry entry)
    {
        var aircraft = _engine.FindAircraft(entry.Callsign);
        if (aircraft is null)
        {
            return ActionRefusals.AircraftNotFound(entry.Callsign);
        }

        var identity =
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

    private ActionOutcome Route(ActionInput input, IActionHost host, RecordedCommand? record)
    {
        var routing = new Routing(input, host, record);
        var (remainder, asOverrideTcp) = TrackResolver.ExtractAsPrefix(input.Command);

        // A chain containing a rejection-set verb (PAUSE, spawn, flight-plan ops, room-wide commands) has no chained
        // semantics: routed as one compound it would swallow the tail or queue a block that no-ops at fire time.
        if (CompoundPolicy.FindNonCompoundableInChain(remainder) is { } nonCompoundable)
        {
            var refusal = new CommandResult(false, $"{CommandDescriber.DescribeCommand(nonCompoundable)} cannot be part of a chained command");
            var refusalTrace = new ActionTrace(RecordedCommandKind.Compound, ActionScope.Aircraft, IsHostSlot: false);
            return Finish(routing, refusal, refusalTrace, RecordingPolicy.Text, ctx: null);
        }

        // A compound that concatenates a track/coordination/strip/TDLS command with ';'/',' cannot be classified as one
        // command — the single-command parser would swallow the separator tail as an argument. Route each unit.
        if (CompoundPolicy.TrySplitSpecialCompound(remainder, out var units))
        {
            return RouteUnits(routing, asOverrideTcp, units);
        }

        var classification = RecordedCommandClassifier.Classify(remainder);
        var arm = ArmTable.For(classification.Kind);
        var trace = new ActionTrace(arm.Kind, arm.Scope, arm.IsHostSlot);

        // A kind that is never recorded (the room's clock, bookmarks, the SHOW query) is never applied from a record
        // either: the legacy PAUSE / SIMRATE / BM records older recordings carry must not pause a rewind or re-add a
        // bookmark.
        if ((record is not null) && (arm.Recording == RecordingPolicy.Never))
        {
            var verb = CommandDescriber.DescribeCommand(classification.Parsed!);
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
        var (input, host, record) = routing;
        var prefix = asOverrideTcp is null ? "" : $"AS {asOverrideTcp} ";
        var messages = new List<(int BlockIndex, string Message)>();
        bool allSuccess = true;
        ActionTrace last = default;
        foreach (var unit in units)
        {
            var unitCommand = prefix + unit.Text;
            var unitInput = input with { Command = unitCommand };
            var unitRecord = record is null ? null : record with { Command = unitCommand, Accepted = null };
            var sub = Route(unitInput, host, unitRecord);
            allSuccess &= sub.Result.Success;
            if (!string.IsNullOrEmpty(sub.Result.Message))
            {
                messages.Add((unit.BlockIndex, sub.Result.Message));
            }

            last = sub.Trace;
        }

        var combined = string.Join(
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
        if (routing.Record is { } record)
        {
            WarnOnVerdictChange(record, result);
            return new ActionOutcome(result, null, trace);
        }

        var input = routing.Input;
        RecordedCommand? toRecord = null;
        if ((recording == RecordingPolicy.Text) && _engine.Scenario is { } scenario)
        {
            toRecord = new RecordedCommand(scenario.ElapsedSeconds, input.Callsign, input.Command, input.Initials, input.ConnectionId)
            {
                ReactionDelaySeconds = ctx?.ReactionDelaySeconds,
                SpawnJitterSeconds = ctx?.SpawnJitterSeconds,
                SpawnedAircraft = ctx?.SpawnedAircraft,
                IssuedAtUtc = ctx?.IssuedAtUtc,
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
