using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;

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
    /// <c>SAY</c> queries. Reaction-delayed when the policy says so (a baked delay always wins), else dispatched;
    /// either way followed by <see cref="SimulationEngine.ApplyPostDispatch"/>, so read-backs, the "unable" response,
    /// the frequency gates, contact registration and evaluator scoring are sim state on every run kind. A landing
    /// clearance caches the arrival airport's ground layout for the rollout, and a destination change (<c>APT</c>) is
    /// a flight-plan amendment the host reprints the strip for.
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

        if (result.Success)
        {
            // After CTL: the arrival airport's ground layout for the runway exit after landing. ResolveGroundLayout
            // falls back to the assigned runway / airport context, so a VFR aircraft with no filed destination still
            // gets a layout.
            if ((aircraft.Phases?.LandingClearance is not null) || (aircraft.Pattern.PendingLandingClearance is not null))
            {
                aircraft.Ground.Layout ??= engine.ResolveGroundLayout(aircraft);
            }

            if (compound.Blocks.Any(block => block.Commands.Any(command => command is ChangeDestinationCommand)))
            {
                ctx.Host.OnFlightPlanAmended(aircraft.Callsign);
            }
        }

        engine.ApplyPostDispatch(aircraft, compound, result, origin);
        return result;
    }

    /// <summary>
    /// <c>SHOWAT</c> / <c>SHOWCOND</c>: the aircraft's pending conditionals with live countdowns, handed to the host
    /// for the issuing connection alone. A query — never recorded, no state effect.
    /// </summary>
    public static CommandResult ShowQueued(ArmContext ctx)
    {
        var lines = ConditionalList.ToLines(ctx.Aircraft!, liveCountdown: true);
        if (lines.Count == 0)
        {
            lines.Add("No pending commands");
        }

        ctx.Host.OnQueuedCommandsShown(ctx.Input.ConnectionId, ctx.Input.Callsign, lines);
        return new CommandResult(true, null);
    }

    /// <summary>
    /// <c>FP</c> / <c>VP</c> / <c>DA</c> / <c>RMK</c>. A fresh action normalises the typed fields into a flight-plan
    /// amendment (the same normalization the CRC editor uses), applies it through the engine and records the
    /// <see cref="RecordedAmendFlightPlan"/> the state travels in; <c>DA</c> is create-only (<c>DUP NEW ID</c> on an
    /// aircraft that already has a plan) while <c>FP</c> / <c>VP</c> create or amend. The filing position becomes the
    /// plan's creator, which the STARS auto-track acquires when the aircraft squawks its assigned code. An action from
    /// a recording applies nothing but the creator tag — the amendment recorded beside it carries the plan, and a
    /// re-derivation would put a flight-plan edit through today's normalization rather than the one live used.
    /// </summary>
    public static CommandResult FlightPlan(ArmContext ctx)
    {
        var callsign = ctx.Input.Callsign;
        var engine = ctx.Engine;
        if (ctx.IsRecorded)
        {
            TagCreator(engine.FindAircraft(callsign), ctx.Identity);
            return new CommandResult(true, null);
        }

        if (!Callsign.IsValid(callsign))
        {
            return new CommandResult(false, "INVALID CALLSIGN");
        }

        var aircraft = engine.FindAircraft(callsign);
        if (aircraft is null)
        {
            return ActionRefusals.AircraftNotFound(callsign);
        }

        FlightPlanAmendment amendment;
        switch (ctx.Parsed)
        {
            case SetRemarksCommand remarks:
                amendment = new FlightPlanAmendment(Remarks: remarks.Text);
                break;
            case CreateAbbreviatedFlightPlanCommand abbreviated:
                if (aircraft.FlightPlan.HasFlightPlan)
                {
                    return new CommandResult(false, "DUP NEW ID");
                }

                amendment = FlightPlanNormalization.FromCreateAbbreviatedCommand(abbreviated);
                break;
            case CreateFlightPlanCommand create:
                amendment = FlightPlanNormalization.FromCreateCommand(create);
                break;
            default:
                return ActionRefusals.HostOnly(ctx.Parsed!);
        }

        engine.AmendFlightPlan(callsign, amendment);
        if (engine.Scenario is { } scenario)
        {
            engine.RecordAction(new RecordedAmendFlightPlan(scenario.ElapsedSeconds, callsign, amendment));
        }

        string response;
        if (ctx.Parsed is SetRemarksCommand)
        {
            response = "Remarks updated";
        }
        else
        {
            TagCreator(aircraft, ctx.Identity);
            var (line1, line2) = FlightPlanEcho.Build(aircraft, FlightPlanEcho.HasRoute(ctx.Parsed));
            response = $"{line1} {line2}";
        }

        ctx.Host.OnFlightPlanAmended(callsign);
        return new CommandResult(true, response);
    }

    private static void TagCreator(AircraftState? aircraft, TrackOwner? identity)
    {
        if ((aircraft is not null) && (identity is not null))
        {
            aircraft.FlightPlan.CreatedByOwner = identity;
        }
    }

    /// <summary>
    /// <c>DEL</c>. A live-traffic shadow is hidden rather than deleted: its feed suppression is room state, so the host
    /// is told and the live run records the <see cref="RecordedLiveTrafficRemoval"/> that removes it on replay.
    /// </summary>
    public static CommandResult Delete(ArmContext ctx)
    {
        var engine = ctx.Engine;
        var callsign = ctx.Input.Callsign;
        var existing = engine.World.FindAircraft(callsign);
        if (existing is { IsShadow: true })
        {
            ctx.Host.OnLiveTrafficHidden(callsign);
            return new CommandResult(true, $"Hid live traffic {callsign}");
        }

        bool queued = engine.Scenario?.DelayedQueue.Any(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase)) ?? false;
        if (existing is null && !queued)
        {
            return ActionRefusals.AircraftNotFound(callsign);
        }

        engine.DeleteAircraft(callsign);
        ctx.Host.OnAircraftDeleted(callsign, existing);
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

    /// <summary>An instructor note on the aircraft — never projected to CRC; the next tick's change tracker carries it.</summary>
    public static CommandResult Note(ArmContext ctx)
    {
        var aircraft = ctx.Aircraft!;
        aircraft.Note = AircraftState.TruncateNote(((NoteCommand)ctx.Parsed!).Text);
        return new CommandResult(true, string.IsNullOrEmpty(aircraft.Note) ? "Note cleared" : "Note updated");
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

        var tcpCode = ((SetActivePositionCommand)ctx.Parsed!).TcpCode;
        var result = ctx.Engine.SelectPosition(connectionId, tcpCode);
        if (result.Success && ctx.Engine.PositionSelections.TryGet(connectionId, out var owner))
        {
            // The host keys the position's display config on its real TCP code, which the argument need not be — a
            // position can be named by its callsign (OAK_GND) or callsign@tcp (NCT_APP@1M).
            ctx.Host.OnPositionSelected(connectionId, owner, TrackResolver.AsPrefixCode(owner));
        }

        return result;
    }

    /// <summary>
    /// A STARS track verb under the resolved identity — the one track table (<see cref="TrackEngine.Dispatch"/>), plus
    /// <c>CAACK</c>, whose state is the engine's conflict-alert set rather than the aircraft's. A handoff or point-out
    /// to an unattended TCP lands on its attended consolidation owner; attendance is the host's answer. The tails a
    /// track verb has beyond the track itself run here on every run kind: a <c>TRACK</c> applies the facility's
    /// scratchpad rules and voids the aircraft's coordination items; an <c>INHCA</c> drops its active conflicts; a
    /// <c>DROP</c> of a ghost lifts the overlay off a real aircraft or deletes a pure phantom.
    /// </summary>
    public static CommandResult Track(ArmContext ctx)
    {
        var engine = ctx.Engine;
        if (engine.Scenario is not { } scenario)
        {
            return ActionRefusals.NoScenario();
        }

        var aircraft = ctx.Aircraft!;
        if (ctx.Parsed is AcknowledgeConflictAlertCommand)
        {
            return TrackEngine.AcknowledgeConflictAlert(aircraft, engine.ConflictAlerts);
        }

        var redirect = new ConsolidationRedirect(scenario, engine.ConsolidationState, ctx.Host.IsPositionAttended);
        var result = TrackEngine.Dispatch(ctx.Parsed!, aircraft, ctx.Identity, scenario, redirect) ?? ActionRefusals.HostOnly(ctx.Parsed!);
        if (!result.Success)
        {
            return result;
        }

        switch (ctx.Parsed)
        {
            case TrackAircraftCommand:
                ScratchpadRuleEngine.Apply(aircraft, scenario.ArtccConfig?.GetStarsConfigForFacility(scenario.StudentPosition?.FacilityId ?? ""));
                ctx.Host.OnTrackAcquired(aircraft.Callsign);
                break;
            case InhibitConflictAlertCommand when aircraft.Stars.IsCaInhibited:
                var inhibited = engine
                    .ConflictAlerts.Conflicts.Values.Where(c => (c.CallsignA == aircraft.Callsign) || (c.CallsignB == aircraft.Callsign))
                    .Select(c => c.Id)
                    .ToList();
                foreach (var id in inhibited)
                {
                    engine.ConflictAlerts.Conflicts.Remove(id);
                }

                break;
            case AsdexVerbCommand { Verb: AsdexVerb.Terminate }:
                ctx.Host.OnAsdexTrackTerminated(aircraft.Callsign);
                break;
            case DropTrackCommand when aircraft.Ghost.IsUnsupported:
                DropGhost(ctx, aircraft);
                break;
        }

        return result;
    }

    /// <summary>
    /// The ghost half of a <c>DROP</c>: an overlay on a real aircraft comes off (the aircraft stays, visible to the
    /// tower and surface displays as itself); a pure phantom leaves the world outright — nothing coasts.
    /// </summary>
    private static void DropGhost(ArmContext ctx, AircraftState aircraft)
    {
        if (aircraft.Ghost.Latitude is not null)
        {
            aircraft.Ghost.IsUnsupported = false;
            aircraft.Ghost.IsOverlay = false;
            aircraft.Ghost.Latitude = null;
            aircraft.Ghost.Longitude = null;
            ctx.Host.OnGhostOverlayRemoved(aircraft.Callsign);
            return;
        }

        ctx.Engine.World.RemoveAircraft(aircraft.Callsign);
        ctx.Host.OnAircraftDeleted(aircraft.Callsign, lastState: null);
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

    /// <summary>
    /// <c>CON</c> / <c>CON+</c>. Which of the sender's descendants move with a full consolidation depends on CRC attendance,
    /// which only the host knows (<see cref="IActionHost.IsPositionAttended"/>); a bare or replay run attends nobody.
    /// </summary>
    public static CommandResult Consolidate(ArmContext ctx) =>
        ConsolidationChanged(ctx, ctx.Engine.Consolidate((ConsolidateCommand)ctx.Parsed!, ctx.Host.IsPositionAttended));

    public static CommandResult Deconsolidate(ArmContext ctx) =>
        ConsolidationChanged(ctx, ctx.Engine.Deconsolidate((DeconsolidateCommand)ctx.Parsed!));

    private static CommandResult ConsolidationChanged(ArmContext ctx, CommandResult result)
    {
        if (result.Success)
        {
            ctx.Host.OnConsolidationChanged();
        }

        return result;
    }

    private static CommandResult HeldDeparturesChanged(ArmContext ctx, HeldReleaseResult result)
    {
        if (result.Success)
        {
            ctx.Host.OnHeldDeparturesChanged();
        }

        return new CommandResult(result.Success, result.Message);
    }
}
