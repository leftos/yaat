using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Simulation.Coordination;

/// <summary>
/// Single dispatch point for the STARS coordination verbs — <c>RD</c> releases an aircraft onto a list,
/// <c>RDH</c> creates a held message (and sends it when one already stands), <c>RDR</c> recalls a sent one or drops
/// an unsent one, <c>RDACK</c> acknowledges as a receiver, <c>RDDEL</c> deletes, <c>RDPOS</c> reorders, <c>RDTXT</c>
/// edits a held message's text, and the global <c>RDAUTO</c> toggles a receiver's auto-acknowledge. Which list a
/// list-less verb means is resolved from the acting position: a sender on exactly one channel needs no list id.
///
/// <para>
/// The bodies are the engine's on every run kind — they read the scenario's channels, its clock and the acting
/// identity, and nothing else — so a replay and a reconstruction reach the same verdicts and build the same lists as
/// the live room. Item ids are <c>{ListId}-{SequenceNumber}</c> off the channel's snapshotted counter rather than a
/// fresh draw, so a rebuilt list holds the same items under the same ids. The host only broadcasts: every successful
/// mutation raises the engine's coordination flag, which the drain hands to
/// <see cref="Spine.IStateChangeConsumer.OnCoordinationChanged"/>.
/// </para>
/// </summary>
public static class CoordinationCommandHandler
{
    private static readonly ILogger Log = SimLog.CreateLogger("CoordinationCommandHandler");

    /// <summary>
    /// The one coordination list <paramref name="identity"/> sends on, or null when it sends on none or several — the
    /// rule a list-less sender verb is resolved by, applied ahead of time so the canonical text a CRC entry records
    /// carries the list explicitly (<c>RDH DR EXPECT 28R</c> cannot be written without one). A position sends on every
    /// list whose sending TCP is its own or consolidated under it (<see cref="CoordinationAuthority"/>).
    /// </summary>
    public static string? InferSenderListId(SimulationEngine engine, TrackOwner identity)
    {
        if ((engine.Scenario is not { } scenario) || (CoordinationAuthority.For(engine, identity) is not { } authority))
        {
            return null;
        }

        var channels = scenario.CoordinationChannels.Values.Where(authority.IsSender).ToList();
        return channels.Count == 1 ? channels[0].ListId : null;
    }

    /// <summary>Applies one aircraft-scoped coordination verb under <paramref name="identity"/>.</summary>
    public static CommandResult Handle(SimulationEngine engine, ParsedCommand cmd, string callsign, TrackOwner identity)
    {
        return cmd switch
        {
            CoordinationReleaseCommand r => HandleRelease(engine, callsign, identity, r.ListId),
            CoordinationHoldCommand h => HandleHold(engine, callsign, identity, h.ListId, h.Text),
            CoordinationRecallCommand r => HandleRecall(engine, callsign, identity, r.ListId),
            CoordinationAcknowledgeCommand a => HandleAcknowledge(engine, callsign, identity, a.ListId),
            CoordinationDeleteCommand d => HandleDelete(engine, callsign, identity, d.ListId),
            CoordinationReorderCommand ro => HandleReorder(engine, callsign, identity, ro.ListId, ro.Position),
            CoordinationModifyCommand m => HandleModify(engine, callsign, identity, m.ListId, m.Text),
            _ => new CommandResult(false, "Unknown coordination command"),
        };
    }

    /// <summary><c>RDAUTO</c> — the auto-acknowledge toggle, which acts on the position rather than on an aircraft.</summary>
    public static CommandResult HandleGlobal(SimulationEngine engine, CoordinationAutoAckCommand cmd, TrackOwner identity) =>
        HandleAutoAck(engine, identity, cmd.ListId, cmd.Enable);

    /// <summary>The scenario a verb acts on and the authority the acting position holds on its lists.</summary>
    private readonly record struct Acting(SimScenarioState Scenario, CoordinationAuthority Authority)
    {
        public string TcpLabel => $"{Authority.Tcp.Subset}{Authority.Tcp.SectorId}";
    }

    /// <summary>The acting context, or null with the refusal text when there is no scenario or no TCP for the identity.</summary>
    private static Acting? ResolveActing(SimulationEngine engine, TrackOwner identity, out string? error)
    {
        error = null;
        if (engine.Scenario is not { } scenario)
        {
            error = "No active scenario";
            return null;
        }

        if (CoordinationAuthority.For(engine, identity) is not { } authority)
        {
            error = "No TCP for identity";
            return null;
        }

        return new Acting(scenario, authority);
    }

    private static bool IsFor(CoordinationItem item, string callsign) => item.AircraftId.Equals(callsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>RD</c>. One item per aircraft per list: an item the sender still holds refuses the release (a recall's linger
    /// reverts its item to Unsent, and re-sending that is RDH's held branch, clearing it RDDEL's), while an item still
    /// inside its recall linger is sent again in place — the same item under the same id, as a fresh release would set
    /// it — rather than stacking a second one beside it. Only the item's own sender revives it.
    /// </summary>
    private static CommandResult HandleRelease(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        if ((ResolveActing(engine, identity, out string? error) is not { } acting) || (SenderChannel(acting, listId, out error) is not { } channel))
        {
            return new CommandResult(false, error!);
        }

        if (channel.Items.Any(i => IsFor(i, callsign) && (i.Status is not StarsCoordinationStatus.Recalled)))
        {
            return new CommandResult(false, AlreadyOnList(callsign, channel));
        }

        CoordinationItem? recalled = channel.Items.FirstOrDefault(i => IsFor(i, callsign));
        if (RecallRefusal(acting, recalled, callsign) is { } refusal)
        {
            return new CommandResult(false, refusal);
        }

        CoordinationItem item = recalled ?? AddNewItem(channel, callsign, acting.Authority.Tcp);
        ApplyRelease(channel, item, acting.Scenario.ElapsedSeconds);
        engine.MarkCoordinationChanged();
        LogRelease(callsign, channel, item, revived: recalled is not null);
        bool autoAcked = item.Status == StarsCoordinationStatus.Acknowledged;
        return new CommandResult(
            true,
            autoAcked ? $"Released {callsign} on list {channel.ListId} (auto-acknowledged)" : $"Released {callsign} on list {channel.ListId}"
        );
    }

    /// <summary>
    /// A recalled item is its own sender's to send again: the refusal when <paramref name="recalled"/> is one and the
    /// acting position does not answer for its origin TCP, else null.
    /// </summary>
    private static string? RecallRefusal(Acting acting, CoordinationItem? recalled, string callsign) =>
        ((recalled is not null) && !acting.Authority.AnswersFor(recalled.OriginTcp)) ? $"{callsign} is recalled by {recalled.OriginTcp}" : null;

    private static string AlreadyOnList(string callsign, CoordinationChannel channel) =>
        $"{callsign} already has a coordination item on list {channel.ListId}";

    /// <summary>
    /// What refuses a hold that is not a send of the held message: an item already sent on the list (one item per
    /// aircraft per list, the rule <c>RD</c> keeps), else a recalled item the acting position does not answer for.
    /// </summary>
    private static string? HoldRefusal(Acting acting, CoordinationChannel channel, CoordinationItem? recalled, string callsign) =>
        channel.Items.Any(i => IsFor(i, callsign) && (i.Status is not (StarsCoordinationStatus.Unsent or StarsCoordinationStatus.Recalled)))
            ? AlreadyOnList(callsign, channel)
            : RecallRefusal(acting, recalled, callsign);

    /// <summary>Adds a new held item to <paramref name="channel"/> under the next id off its snapshotted counter.</summary>
    private static CoordinationItem AddNewItem(CoordinationChannel channel, string callsign, Tcp originTcp)
    {
        int seq = channel.NextSequence++;
        var item = new CoordinationItem
        {
            Id = $"{channel.ListId}-{seq}",
            AircraftId = callsign,
            Status = StarsCoordinationStatus.Unsent,
            OriginTcp = originTcp,
            SequenceNumber = seq,
        };
        channel.Items.Add(item);
        return item;
    }

    /// <summary>
    /// What a release sets: unacknowledged with no clock, or — when a receiver auto-acknowledges — acknowledged as an
    /// automatic release with the acknowledgement's expiry running.
    /// </summary>
    private static void ApplyRelease(CoordinationChannel channel, CoordinationItem item, double now)
    {
        bool autoAck = channel.Receivers.Any(r => r.AutoAcknowledge);
        item.Status = autoAck ? StarsCoordinationStatus.Acknowledged : StarsCoordinationStatus.Unacknowledged;
        item.WasAutomaticRelease = autoAck;
        item.ExpireTime = autoAck ? now + SimScenarioState.CoordinationAckExpirySeconds : null;
    }

    private static void LogRelease(string callsign, CoordinationChannel channel, CoordinationItem item, bool revived)
    {
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation(
                "Coordination release: {Callsign} on list {ListId} (status={Status}, revived={Revived})",
                callsign,
                channel.ListId,
                item.Status,
                revived
            );
        }
    }

    /// <summary>
    /// <c>RDH</c>: sends the aircraft's held message when one stands, holds its recalled item again in place when one is
    /// inside its recall linger and the position answers for its origin (as a fresh hold would set it, keeping its text
    /// unless new text is given), refuses when the aircraft's item on the list is already sent, and otherwise creates a
    /// held message. A position that does not send on the list is refused, whether it named the list or not.
    /// </summary>
    private static CommandResult HandleHold(SimulationEngine engine, string callsign, TrackOwner identity, string? listId, string? text)
    {
        if ((ResolveActing(engine, identity, out string? error) is not { } acting) || (SenderChannel(acting, listId, out error) is not { } channel))
        {
            return new CommandResult(false, error!);
        }

        if (channel.Items.FirstOrDefault(i => IsFor(i, callsign) && (i.Status == StarsCoordinationStatus.Unsent)) is { } existing)
        {
            // Send a held message
            existing.Status = StarsCoordinationStatus.Unacknowledged;
            engine.MarkCoordinationChanged();
            LogCoordination("hold-send", callsign, channel);
            return new CommandResult(true, $"Hold sent for {callsign} on list {channel.ListId}");
        }

        CoordinationItem? recalled = channel.Items.FirstOrDefault(i => IsFor(i, callsign) && (i.Status == StarsCoordinationStatus.Recalled));
        if (HoldRefusal(acting, channel, recalled, callsign) is { } refusal)
        {
            return new CommandResult(false, refusal);
        }

        CoordinationItem item = recalled ?? AddNewItem(channel, callsign, acting.Authority.Tcp);
        item.Status = StarsCoordinationStatus.Unsent;
        item.ExpireTime = null;
        item.WasAutomaticRelease = false;
        item.Message = text ?? item.Message;
        engine.MarkCoordinationChanged();
        (string kind, string outcome) = recalled is null ? ("hold-create", "created") : ("hold-revive", "revived");
        LogCoordination(kind, callsign, channel);
        return new CommandResult(true, $"Hold {outcome} for {callsign} on list {channel.ListId}");
    }

    private static void LogCoordination(string kind, string callsign, CoordinationChannel channel)
    {
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Coordination {Kind}: {Callsign} on list {ListId}", kind, callsign, channel.ListId);
        }
    }

    private static CommandResult HandleRecall(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        if ((ResolveActing(engine, identity, out string? error) is not { } acting) || (SenderChannel(acting, listId, out error) is not { } channel))
        {
            return new CommandResult(false, error!);
        }

        CoordinationItem? item = channel.Items.FirstOrDefault(i => IsFor(i, callsign) && (i.Status is not StarsCoordinationStatus.Recalled));
        if (item is null)
        {
            return new CommandResult(false, $"No active coordination item for {callsign}");
        }

        string msg;
        if (item.Status == StarsCoordinationStatus.Unsent)
        {
            channel.Items.Remove(item);
            LogCoordination("recall-delete", callsign, channel);
            msg = $"Recalled {callsign} from list {channel.ListId} (removed)";
        }
        else
        {
            item.Status = StarsCoordinationStatus.Recalled;
            item.ExpireTime = acting.Scenario.ElapsedSeconds + SimScenarioState.CoordinationRecallLingerSeconds;
            LogCoordination("recall", callsign, channel);
            msg = $"Recalled {callsign} on list {channel.ListId}";
        }

        engine.MarkCoordinationChanged();
        return new CommandResult(true, msg);
    }

    private static CommandResult HandleAcknowledge(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        if (
            (ResolveActing(engine, identity, out string? error) is not { } acting)
            || (ResolveChannelForReceiver(acting, callsign, listId, out error) is not { } channel)
        )
        {
            return new CommandResult(false, error!);
        }

        if (!acting.Authority.IsReceiver(channel))
        {
            return new CommandResult(false, $"TCP {acting.TcpLabel} is not a receiver on list {channel.ListId}");
        }

        CoordinationItem? item = channel.Items.FirstOrDefault(i => IsFor(i, callsign) && (i.Status == StarsCoordinationStatus.Unacknowledged));
        if (item is null)
        {
            return new CommandResult(false, $"No unacknowledged coordination for {callsign} on list {channel.ListId}");
        }

        item.Status = StarsCoordinationStatus.Acknowledged;
        item.ExpireTime = acting.Scenario.ElapsedSeconds + SimScenarioState.CoordinationAckExpirySeconds;
        engine.MarkCoordinationChanged();
        LogCoordination("acknowledge", callsign, channel);
        return new CommandResult(true, $"Acknowledged {callsign} on list {channel.ListId}");
    }

    /// <summary>
    /// The list a receiver's acknowledge means: the one it named, or — when it named none — the single channel it
    /// receives on that is holding an unacknowledged message for the aircraft.
    /// </summary>
    private static CoordinationChannel? ResolveChannelForReceiver(Acting acting, string callsign, string? listId, out string? error)
    {
        if (listId is not null)
        {
            return NamedChannel(acting.Scenario, listId, out error);
        }

        error = null;
        var matches = acting
            .Scenario.CoordinationChannels.Values.Where(ch =>
                acting.Authority.IsReceiver(ch) && ch.Items.Any(i => IsFor(i, callsign) && (i.Status == StarsCoordinationStatus.Unacknowledged))
            )
            .ToList();

        if (matches.Count == 0)
        {
            error = $"No unacknowledged coordination for {callsign}";
            return null;
        }

        if (matches.Count > 1)
        {
            error = $"Ambiguous: {callsign} has items on {matches.Count} lists; specify list ID";
            return null;
        }

        return matches[0];
    }

    /// <summary>
    /// <c>RDAUTO</c>: toggles auto-acknowledge on every receiver of the list the position answers for, all to the same
    /// value — the one given, or the opposite of the first such receiver's.
    /// </summary>
    private static CommandResult HandleAutoAck(SimulationEngine engine, TrackOwner identity, string listId, bool? enable)
    {
        if (engine.Scenario is not { } scenario)
        {
            return new CommandResult(false, "No active scenario");
        }

        if (!scenario.CoordinationChannels.TryGetValue(listId, out CoordinationChannel? channel))
        {
            return new CommandResult(false, $"Unknown coordination list: {listId}");
        }

        if (ResolveActing(engine, identity, out string? error) is not { } acting)
        {
            return new CommandResult(false, error!);
        }

        var receivers = channel.Receivers.Where(r => acting.Authority.AnswersFor(r.Tcp)).ToList();
        if (receivers.Count == 0)
        {
            return new CommandResult(false, $"TCP {acting.TcpLabel} is not a receiver on list {listId}");
        }

        bool value = enable ?? !receivers[0].AutoAcknowledge;
        receivers.ForEach(r => r.AutoAcknowledge = value);
        engine.MarkCoordinationChanged();
        string state = value ? "ON" : "OFF";
        return new CommandResult(true, $"Auto-acknowledge {state} for list {listId}");
    }

    /// <summary>
    /// Table 34 delete-existing: the same (LISTID)(ACID) grammar that creates a message deletes an
    /// existing one outright — unlike recall, which marks a sent message RCL for the receiver.
    /// </summary>
    private static CommandResult HandleDelete(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        (CoordinationChannel? channel, CoordinationItem? item, string? error) = FindSenderItem(engine, callsign, identity, listId);
        if (channel is null || item is null)
        {
            return new CommandResult(false, error!);
        }

        channel.Items.Remove(item);
        engine.MarkCoordinationChanged();
        LogCoordination("delete", callsign, channel);
        return new CommandResult(true, $"Deleted {callsign} from list {channel.ListId}");
    }

    /// <summary>Table 34 reorder: moves the aircraft's message to 1-based line <paramref name="position"/>.</summary>
    private static CommandResult HandleReorder(SimulationEngine engine, string callsign, TrackOwner identity, string? listId, int position)
    {
        (CoordinationChannel? channel, CoordinationItem? item, string? error) = FindSenderItem(engine, callsign, identity, listId);
        if (channel is null || item is null)
        {
            return new CommandResult(false, error!);
        }

        channel.Items.Remove(item);
        int index = Math.Clamp(position - 1, 0, channel.Items.Count);
        channel.Items.Insert(index, item);
        engine.MarkCoordinationChanged();
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Coordination reorder: {Callsign} to line {Position} on list {ListId}", callsign, position, channel.ListId);
        }
        return new CommandResult(true, $"Moved {callsign} to line {position} on list {channel.ListId}");
    }

    /// <summary>Table 34 modify-text: only a held (unsent) message's text may change.</summary>
    private static CommandResult HandleModify(SimulationEngine engine, string callsign, TrackOwner identity, string? listId, string text)
    {
        (CoordinationChannel? channel, CoordinationItem? item, string? error) = FindSenderItem(engine, callsign, identity, listId);
        if (channel is null || item is null)
        {
            return new CommandResult(false, error!);
        }

        if (item.Status != StarsCoordinationStatus.Unsent)
        {
            return new CommandResult(false, $"Message for {callsign} on list {channel.ListId} is already sent");
        }

        item.Message = text;
        engine.MarkCoordinationChanged();
        LogCoordination("modify", callsign, channel);
        return new CommandResult(true, $"Set message text for {callsign} on list {channel.ListId}");
    }

    /// <summary>
    /// The sender's item for the aircraft, a recalled one inside its linger included: it is still the one item the
    /// aircraft holds on the list. An item that is not recalled wins over one that is.
    /// </summary>
    private static (CoordinationChannel? Channel, CoordinationItem? Item, string? Error) FindSenderItem(
        SimulationEngine engine,
        string callsign,
        TrackOwner identity,
        string? listId
    )
    {
        if ((ResolveActing(engine, identity, out string? error) is not { } acting) || (SenderChannel(acting, listId, out error) is not { } channel))
        {
            return (null, null, error);
        }

        CoordinationItem? item =
            channel.Items.FirstOrDefault(i => IsFor(i, callsign) && (i.Status is not StarsCoordinationStatus.Recalled))
            ?? channel.Items.FirstOrDefault(i => IsFor(i, callsign));
        return item is null ? (null, null, $"No coordination item for {callsign} on list {channel.ListId}") : (channel, item, null);
    }

    /// <summary>The sender's channel, refused when the position does not send on the one it resolves to.</summary>
    private static CoordinationChannel? SenderChannel(Acting acting, string? listId, out string? error)
    {
        CoordinationChannel? channel = ResolveChannelForSender(acting, listId, out error);
        if ((channel is not null) && !acting.Authority.IsSender(channel))
        {
            error = $"TCP {acting.TcpLabel} is not a sender on list {channel.ListId}";
            return null;
        }

        return channel;
    }

    private static CoordinationChannel? NamedChannel(SimScenarioState scenario, string listId, out string? error)
    {
        error = null;
        if (scenario.CoordinationChannels.TryGetValue(listId, out CoordinationChannel? channel))
        {
            return channel;
        }

        error = $"Unknown coordination list: {listId}";
        return null;
    }

    private static CoordinationChannel? ResolveChannelForSender(Acting acting, string? listId, out string? error)
    {
        if (listId is not null)
        {
            return NamedChannel(acting.Scenario, listId, out error);
        }

        // Auto-detect: find channel where tcp is a sender
        error = null;
        var matches = acting.Scenario.CoordinationChannels.Values.Where(acting.Authority.IsSender).ToList();
        if (matches.Count == 0)
        {
            error = $"TCP {acting.TcpLabel} is not a sender on any coordination channel";
            return null;
        }

        if (matches.Count > 1)
        {
            error = $"TCP {acting.TcpLabel} is a sender on {matches.Count} channels; specify list ID";
            return null;
        }

        return matches[0];
    }
}
