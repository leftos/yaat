using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Actions;

/// <summary>
/// The Sim bodies of the <see cref="ArmTable"/> rows. Each takes the resolved <see cref="ArmContext"/>, mutates
/// engine state, notifies the host's consumers, and returns the controller-facing result. Nothing here reads
/// <see cref="RunProfile"/>: what differs between run kinds is the host a body notifies and the draws it finds baked.
/// </summary>
internal static class ActionArms
{
    /// <summary>
    /// The aviation arm: every instruction to an aircraft that <see cref="CommandDispatcher"/> owns, plus the
    /// <c>SAY</c>/<c>SHOW</c> queries. Reaction-delayed when the policy says so (a baked delay always wins), else
    /// dispatched; either way followed by <see cref="SimulationEngine.ApplyPostDispatch"/>, so read-backs, the
    /// "unable" response, the frequency gates, contact registration and evaluator scoring are sim state on every run kind.
    /// </summary>
    public static CommandResult Aviation(ArmContext ctx)
    {
        var engine = ctx.Engine;
        var aircraft = ctx.Aircraft!;
        var parse = CommandParser.ParseCompound(ctx.Remainder, aircraft.FlightPlan.Route);
        if (!parse.IsSuccess)
        {
            return new CommandResult(false, $"Failed to parse command: {ctx.Remainder} — {parse.Reason}");
        }

        var compound = parse.Value!;
        var origin = ctx.Origin;
        var scenario = engine.Scenario;
        double? delay = scenario is null
            ? null
            : ReactionDelayPolicy.Decide(scenario, engine.World, aircraft, compound, ctx.Input.Baked?.ReactionDelaySeconds);

        CommandResult result;
        if (delay is double seconds)
        {
            engine.DeferForReaction(aircraft, compound, seconds, origin);
            ctx.ReactionDelaySeconds = seconds;
            // In solo training mode the student is the pilot's only audience: showing the exact sampled delay would
            // reveal how long the aircraft will take to comply. The pilot's read-back is the acknowledgement.
            result =
                scenario?.SoloTrainingMode == true
                    ? new CommandResult(true, null)
                    : new CommandResult(true, $"Pilot complying in {(int)Math.Round(seconds)}s");
        }
        else
        {
            result = CommandDispatcher.DispatchCompound(
                compound,
                aircraft,
                engine.BuildDispatchContext(aircraft, origin == DispatchOrigin.ControllerAi)
            );
        }

        engine.ApplyPostDispatch(aircraft, compound, result, origin);
        return result;
    }

    public static CommandResult FlightPlan(ArmContext ctx) => ctx.Host.ApplyFlightPlanCommand(ctx.Input.Callsign, ctx.Parsed!, ctx.Identity);

    /// <summary>
    /// <c>DEL</c>. A live-traffic shadow is hidden by the host (its feed suppression is room state, and the live run
    /// records the <see cref="RecordedLiveTrafficRemoval"/> that removes it), so the Sim leaves it in place here.
    /// </summary>
    public static CommandResult Delete(ArmContext ctx)
    {
        var engine = ctx.Engine;
        var callsign = ctx.Input.Callsign;
        var existing = engine.World.FindAircraft(callsign);
        if (existing is { IsShadow: true })
        {
            return new CommandResult(true, $"Hid live traffic {callsign}");
        }

        bool queued = engine.Scenario?.DelayedQueue.Any(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase)) ?? false;
        if (existing is null && !queued)
        {
            return ActionRefusals.AircraftNotFound(callsign);
        }

        engine.DeleteAircraft(callsign);
        ctx.Host.OnAircraftDeleted(callsign);
        return new CommandResult(true, $"Deleted {callsign}");
    }

    /// <summary><c>DELAT</c> / <c>DELCOND</c>: one numbered conditional, or every deletable one.</summary>
    public static CommandResult DeleteQueued(ArmContext ctx)
    {
        int? number = ((DeleteQueuedCommand)ctx.Parsed!).BlockNumber;
        var outcome = ConditionalList.Delete(ctx.Aircraft!, number);
        if (!outcome.Success)
        {
            return number is { } outOfRange && outcome.DeletableCount > 0
                ? new CommandResult(false, $"Conditional {outOfRange} out of range (1-{outcome.DeletableCount})")
                : new CommandResult(false, "No pending commands");
        }

        if (number is { } index)
        {
            var description = string.IsNullOrEmpty(outcome.Description) ? $"conditional {index}" : outcome.Description;
            return new CommandResult(true, $"Deleted [{index}] {description}");
        }

        return new CommandResult(true, $"Deleted all {outcome.DeletedCount} conditional(s)");
    }

    public static CommandResult Note(ArmContext ctx)
    {
        ctx.Aircraft!.Note = AircraftState.TruncateNote(((NoteCommand)ctx.Parsed!).Text);
        return new CommandResult(true, null);
    }

    /// <summary><c>SPAWN</c>: pulls a still-queued delayed spawn into the world now.</summary>
    public static CommandResult SpawnNow(ArmContext ctx)
    {
        var callsign = ctx.Input.Callsign;
        var spawned = ctx.Engine.SpawnNow(callsign);
        if (spawned is null)
        {
            return new CommandResult(false, $"No queued spawn for {callsign}");
        }

        ctx.Host.OnAircraftSpawned(spawned);
        return new CommandResult(true, $"Spawned {callsign}");
    }

    /// <summary><c>SPAWNDELAY</c>: re-times a still-queued delayed spawn.</summary>
    public static CommandResult SpawnDelay(ArmContext ctx)
    {
        var callsign = ctx.Input.Callsign;
        int seconds = ((SpawnDelayCommand)ctx.Parsed!).Seconds;
        return ctx.Engine.SpawnDelay(callsign, seconds)
            ? new CommandResult(true, $"{callsign} spawns in {seconds}s")
            : new CommandResult(false, $"No queued spawn for {callsign}");
    }

    /// <summary>A bare <c>AS {tcp}</c>: the issuing connection acts as that position from now on.</summary>
    public static CommandResult SetActivePosition(ArmContext ctx)
    {
        var connectionId = ctx.Input.ConnectionId;
        if (connectionId.Length == 0)
        {
            return new CommandResult(false, "AS needs an issuing connection to select a position for");
        }

        var result = ctx.Engine.SelectPosition(connectionId, ((SetActivePositionCommand)ctx.Parsed!).TcpCode);
        if (result.Success && ctx.Engine.PositionSelections.TryGet(connectionId, out var owner))
        {
            ctx.Host.OnPositionSelected(connectionId, owner);
        }

        return result;
    }

    /// <summary>
    /// A STARS track verb under the resolved identity — the one track table (<see cref="TrackEngine.Dispatch"/>), plus
    /// <c>CAACK</c>, whose state is the engine's conflict-alert set rather than the aircraft's.
    /// </summary>
    public static CommandResult Track(ArmContext ctx)
    {
        if (ctx.Engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        if (ctx.Parsed is AcknowledgeConflictAlertCommand)
        {
            return TrackEngine.AcknowledgeConflictAlert(ctx.Aircraft!, ctx.Engine.ConflictAlerts);
        }

        return TrackEngine.Dispatch(ctx.Parsed!, ctx.Aircraft!, ctx.Identity, scenario) ?? ActionRefusals.HostOnly(ctx.Parsed!);
    }

    /// <summary><c>ACCEPTALL</c> / <c>HOALL</c> under the resolved identity.</summary>
    public static CommandResult GlobalTrack(ArmContext ctx)
    {
        if (ctx.Engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        return TrackEngine.DispatchGlobal(ctx.Parsed!, ctx.Engine.World, scenario, ctx.Identity);
    }

    /// <summary><c>GHOST</c>: a phantom aircraft (handed to the host as a spawn) or an overlay on an existing one.</summary>
    public static CommandResult GhostTrack(ArmContext ctx)
    {
        if (ctx.Engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        if (ctx.Identity is not { } identity)
        {
            return ActionRefusals.NoActivePosition();
        }

        var outcome = TrackEngine.CreateGhostTrack((GhostTrackCommand)ctx.Parsed!, ctx.Engine.World, scenario, identity);
        if (outcome.Created is { } created)
        {
            ctx.Host.OnAircraftSpawned(created);
        }

        return outcome.Result;
    }

    /// <summary><c>RPOSLOC</c> / <c>RPOSMOVE</c>: park or re-associate the aircraft's datablock under the resolved identity.</summary>
    public static CommandResult Reposition(ArmContext ctx)
    {
        if (ctx.Identity is not { } identity)
        {
            return ActionRefusals.NoActivePosition();
        }

        return ctx.Parsed switch
        {
            RepositionToLocationCommand toLocation => TrackEngine.RepositionToLocation(toLocation, ctx.Engine.World, identity),
            RepositionMoveCommand move => TrackEngine.RepositionMove(move, ctx.Engine.World, identity),
            _ => ActionRefusals.HostOnly(ctx.Parsed!),
        };
    }

    public static CommandResult SquawkAll(ArmContext ctx) => ctx.Engine.SquawkAll(ctx.Parsed!);

    public static CommandResult TaxiAll(ArmContext ctx) => ctx.Engine.TaxiAll((TaxiAllCommand)ctx.Parsed!);

    /// <summary>
    /// <c>ADD</c>. Derived on every run kind from the shared RNG and beacon pool; a recorded action's baked aircraft
    /// is the authority when the derivation disagrees (<see cref="SimulationEngine.AddAircraft"/>). The aircraft that
    /// ended up in the world is baked onto a fresh action's record.
    /// </summary>
    public static CommandResult AddAircraft(ArmContext ctx)
    {
        var outcome = ctx.Engine.AddAircraft(((AddAircraftCommand)ctx.Parsed!).Args, ctx.Input.Baked?.SpawnedAircraft);
        if (outcome.Aircraft is null)
        {
            return new CommandResult(false, outcome.Error);
        }

        ctx.SpawnedAircraft = outcome.Spawned;
        ctx.Host.OnAircraftSpawned(outcome.Aircraft);
        return new CommandResult(true, $"Spawned {outcome.Aircraft.Callsign} ({outcome.Aircraft.AircraftType})");
    }

    public static CommandResult HoldForRelease(ArmContext ctx)
    {
        if (ctx.Engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        var armed = HeldReleaseService.Arm(scenario, ctx.Engine.World, ((HoldForReleaseCommand)ctx.Parsed!).Airport);
        return HeldDeparturesChanged(ctx, armed);
    }

    public static CommandResult DisarmHoldForRelease(ArmContext ctx)
    {
        if (ctx.Engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        var disarmed = HeldReleaseService.Disarm(scenario, ctx.Engine.World, ((DisarmHoldForReleaseCommand)ctx.Parsed!).Airport);
        return HeldDeparturesChanged(ctx, disarmed);
    }

    /// <summary>
    /// <c>REL</c>. A fresh release draws its airborne spawn jitter from the live-only jitter RNG and bakes it; a
    /// recorded one reproduces the baked jitter (or the legacy fixed minimum) and draws nothing.
    /// </summary>
    public static CommandResult ReleaseDeparture(ArmContext ctx)
    {
        if (ctx.Engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        var release = (ReleaseDepartureCommand)ctx.Parsed!;
        var world = ctx.Engine.World;
        HeldReleaseResult released;
        if (ctx.IsRecorded)
        {
            released = HeldReleaseService.ReplayRelease(
                scenario,
                world,
                release.Target,
                release.IntervalSeconds,
                ctx.Input.Baked!.SpawnJitterSeconds
            );
        }
        else
        {
            released = HeldReleaseService.Release(scenario, world, world.ReleaseJitterRng, release.Target, release.IntervalSeconds);
            ctx.SpawnJitterSeconds = released.SpawnJitterSeconds;
        }

        return HeldDeparturesChanged(ctx, released);
    }

    /// <summary>
    /// <c>CFR</c>. The window is anchored to a wall clock the live run read once and bakes onto the record, so a
    /// replay anchors to the same instant; the window never affects the simulation (issue #230).
    /// </summary>
    public static CommandResult Cfr(ArmContext ctx)
    {
        var cfr = (CfrDepartureCommand)ctx.Parsed!;
        var aircraft = ctx.Aircraft!;
        if (cfr.Action == CfrAction.Check)
        {
            return new CommandResult(true, CfrDepartureService.DescribeStatus(aircraft, DateTime.UtcNow));
        }

        if (cfr.Action == CfrAction.Set && !aircraft.IsOnGround)
        {
            return new CommandResult(false, $"{aircraft.Callsign} is already airborne — nothing to release");
        }

        var issuedAtUtc = ctx.Input.Baked?.IssuedAtUtc ?? DateTime.UtcNow;
        ctx.IssuedAtUtc = issuedAtUtc;
        return new CommandResult(true, CfrDepartureService.Apply(aircraft, cfr, issuedAtUtc));
    }

    public static CommandResult Timer(ArmContext ctx)
    {
        if (ctx.Engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        var result = TimerCommandApplier.Apply((TimerCommand)ctx.Parsed!, scenario, ctx.Engine.World, ctx.Input.Callsign);
        if (result.Success)
        {
            ctx.Host.OnTimersChanged();
        }

        return result;
    }

    /// <summary>A row whose body has not crossed into the Sim yet: refused with the host-only message on every Sim run.</summary>
    public static CommandResult NotAvailable(ArmContext ctx) => ActionRefusals.HostOnly(ctx.Parsed!);

    private static CommandResult HeldDeparturesChanged(ArmContext ctx, HeldReleaseResult result)
    {
        if (result.Success)
        {
            ctx.Host.OnHeldDeparturesChanged();
        }

        return new CommandResult(result.Success, result.Message);
    }
}
