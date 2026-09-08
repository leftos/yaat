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
    /// carries the list explicitly (<c>RDH DR EXPECT 28R</c> cannot be written without one).
    /// </summary>
    public static string? InferSenderListId(SimScenarioState scenario, TrackOwner identity)
    {
        var tcp = FindTcpForIdentity(identity, scenario);
        if (tcp is null)
        {
            return null;
        }

        var channels = scenario.CoordinationChannels.Values.Where(ch => IsSender(ch, tcp)).ToList();
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

    private static CommandResult HandleRelease(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No active scenario");
        }

        var tcp = FindTcpForIdentity(identity, scenario);
        if (tcp is null)
        {
            return new CommandResult(false, "No TCP for identity");
        }

        var channel = ResolveChannelForSender(scenario, tcp, listId, out var error);
        if (channel is null)
        {
            return new CommandResult(false, error!);
        }

        if (!IsSender(channel, tcp))
        {
            return new CommandResult(false, $"TCP {tcp.Subset}{tcp.SectorId} is not a sender on list {channel.ListId}");
        }

        var seq = channel.NextSequence++;
        var item = new CoordinationItem
        {
            Id = $"{channel.ListId}-{seq}",
            AircraftId = callsign,
            Status = StarsCoordinationStatus.Unacknowledged,
            OriginTcp = tcp,
            SequenceNumber = seq,
        };

        // Check auto-acknowledge receivers
        foreach (var receiver in channel.Receivers)
        {
            if (receiver.AutoAcknowledge)
            {
                item.Status = StarsCoordinationStatus.Acknowledged;
                item.WasAutomaticRelease = true;
                item.ExpireTime = scenario.ElapsedSeconds + SimScenarioState.CoordinationAckExpirySeconds;
                break;
            }
        }

        channel.Items.Add(item);
        engine.MarkCoordinationChanged();
        Log.LogInformation("Coordination release: {Callsign} on list {ListId} (status={Status})", callsign, channel.ListId, item.Status);

        var autoAcked = item.Status == StarsCoordinationStatus.Acknowledged;
        var msg = autoAcked ? $"Released {callsign} on list {channel.ListId} (auto-acknowledged)" : $"Released {callsign} on list {channel.ListId}";
        return new CommandResult(true, msg);
    }

    private static CommandResult HandleHold(SimulationEngine engine, string callsign, TrackOwner identity, string? listId, string? text)
    {
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No active scenario");
        }

        var tcp = FindTcpForIdentity(identity, scenario);
        if (tcp is null)
        {
            return new CommandResult(false, "No TCP for identity");
        }

        var channel = ResolveChannelForSender(scenario, tcp, listId, out var error);
        if (channel is null)
        {
            return new CommandResult(false, error!);
        }

        var existing = channel.Items.FirstOrDefault(i =>
            i.AircraftId.Equals(callsign, StringComparison.OrdinalIgnoreCase) && (i.Status == StarsCoordinationStatus.Unsent)
        );

        if (existing is not null)
        {
            // Send a held message
            existing.Status = StarsCoordinationStatus.Unacknowledged;
            engine.MarkCoordinationChanged();
            Log.LogInformation("Coordination hold-send: {Callsign} on list {ListId}", callsign, channel.ListId);
            return new CommandResult(true, $"Hold sent for {callsign} on list {channel.ListId}");
        }

        // Create as unsent
        var seq = channel.NextSequence++;
        var item = new CoordinationItem
        {
            Id = $"{channel.ListId}-{seq}",
            AircraftId = callsign,
            Status = StarsCoordinationStatus.Unsent,
            Message = text ?? "",
            OriginTcp = tcp,
            SequenceNumber = seq,
        };
        channel.Items.Add(item);
        engine.MarkCoordinationChanged();

        Log.LogInformation("Coordination hold-create: {Callsign} on list {ListId}", callsign, channel.ListId);
        return new CommandResult(true, $"Hold created for {callsign} on list {channel.ListId}");
    }

    private static CommandResult HandleRecall(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No active scenario");
        }

        var tcp = FindTcpForIdentity(identity, scenario);
        if (tcp is null)
        {
            return new CommandResult(false, "No TCP for identity");
        }

        var channel = ResolveChannelForSender(scenario, tcp, listId, out var error);
        if (channel is null)
        {
            return new CommandResult(false, error!);
        }

        var item = channel.Items.FirstOrDefault(i =>
            i.AircraftId.Equals(callsign, StringComparison.OrdinalIgnoreCase) && (i.Status is not StarsCoordinationStatus.Recalled)
        );

        if (item is null)
        {
            return new CommandResult(false, $"No active coordination item for {callsign}");
        }

        string msg;
        if (item.Status == StarsCoordinationStatus.Unsent)
        {
            channel.Items.Remove(item);
            Log.LogInformation("Coordination recall-delete: {Callsign} on list {ListId}", callsign, channel.ListId);
            msg = $"Recalled {callsign} from list {channel.ListId} (removed)";
        }
        else
        {
            item.Status = StarsCoordinationStatus.Recalled;
            item.ExpireTime = scenario.ElapsedSeconds + SimScenarioState.CoordinationRecallLingerSeconds;
            Log.LogInformation("Coordination recall: {Callsign} on list {ListId}", callsign, channel.ListId);
            msg = $"Recalled {callsign} on list {channel.ListId}";
        }

        engine.MarkCoordinationChanged();
        return new CommandResult(true, msg);
    }

    private static CommandResult HandleAcknowledge(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No active scenario");
        }

        var tcp = FindTcpForIdentity(identity, scenario);
        if (tcp is null)
        {
            return new CommandResult(false, "No TCP for identity");
        }

        var channel = ResolveChannelForReceiver(scenario, tcp, callsign, listId, out var resolveError);
        if (channel is null)
        {
            return new CommandResult(false, resolveError!);
        }

        if (!IsReceiver(channel, tcp))
        {
            return new CommandResult(false, $"TCP {tcp.Subset}{tcp.SectorId} is not a receiver on list {channel.ListId}");
        }

        var item = channel.Items.FirstOrDefault(i =>
            i.AircraftId.Equals(callsign, StringComparison.OrdinalIgnoreCase) && (i.Status == StarsCoordinationStatus.Unacknowledged)
        );

        if (item is null)
        {
            return new CommandResult(false, $"No unacknowledged coordination for {callsign} on list {channel.ListId}");
        }

        item.Status = StarsCoordinationStatus.Acknowledged;
        item.ExpireTime = scenario.ElapsedSeconds + SimScenarioState.CoordinationAckExpirySeconds;
        engine.MarkCoordinationChanged();

        Log.LogInformation("Coordination acknowledge: {Callsign} on list {ListId}", callsign, channel.ListId);
        return new CommandResult(true, $"Acknowledged {callsign} on list {channel.ListId}");
    }

    /// <summary>
    /// The list a receiver's acknowledge means: the one it named, or — when it named none — the single channel it
    /// receives on that is holding an unacknowledged message for the aircraft.
    /// </summary>
    private static CoordinationChannel? ResolveChannelForReceiver(
        SimScenarioState scenario,
        Tcp tcp,
        string callsign,
        string? listId,
        out string? error
    )
    {
        error = null;
        if (listId is not null)
        {
            if (!scenario.CoordinationChannels.TryGetValue(listId, out var named))
            {
                error = $"Unknown coordination list: {listId}";
                return null;
            }

            return named;
        }

        var matches = new List<CoordinationChannel>();
        foreach (var ch in scenario.CoordinationChannels.Values)
        {
            if (!IsReceiver(ch, tcp))
            {
                continue;
            }

            if (
                ch.Items.Any(i =>
                    i.AircraftId.Equals(callsign, StringComparison.OrdinalIgnoreCase) && (i.Status == StarsCoordinationStatus.Unacknowledged)
                )
            )
            {
                matches.Add(ch);
            }
        }

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

    private static CommandResult HandleAutoAck(SimulationEngine engine, TrackOwner identity, string listId, bool? enable)
    {
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return new CommandResult(false, "No active scenario");
        }

        if (!scenario.CoordinationChannels.TryGetValue(listId, out var channel))
        {
            return new CommandResult(false, $"Unknown coordination list: {listId}");
        }

        var tcp = FindTcpForIdentity(identity, scenario);
        if (tcp is null)
        {
            return new CommandResult(false, "No TCP for identity");
        }

        var receiver = channel.Receivers.FirstOrDefault(r =>
            r.Tcp.Subset == tcp.Subset && r.Tcp.SectorId.Equals(tcp.SectorId, StringComparison.OrdinalIgnoreCase)
        );

        if (receiver is null)
        {
            return new CommandResult(false, $"TCP {tcp.Subset}{tcp.SectorId} is not a receiver on list {listId}");
        }

        receiver.AutoAcknowledge = enable ?? !receiver.AutoAcknowledge;
        engine.MarkCoordinationChanged();
        var state = receiver.AutoAcknowledge ? "ON" : "OFF";
        return new CommandResult(true, $"Auto-acknowledge {state} for list {listId}");
    }

    /// <summary>
    /// Table 34 delete-existing: the same (LISTID)(ACID) grammar that creates a message deletes an
    /// existing one outright — unlike recall, which marks a sent message RCL for the receiver.
    /// </summary>
    private static CommandResult HandleDelete(SimulationEngine engine, string callsign, TrackOwner identity, string? listId)
    {
        var (channel, item, error) = FindSenderItem(engine, callsign, identity, listId);
        if (channel is null || item is null)
        {
            return new CommandResult(false, error!);
        }

        channel.Items.Remove(item);
        engine.MarkCoordinationChanged();
        Log.LogInformation("Coordination delete: {Callsign} on list {ListId}", callsign, channel.ListId);
        return new CommandResult(true, $"Deleted {callsign} from list {channel.ListId}");
    }

    /// <summary>Table 34 reorder: moves the aircraft's message to 1-based line <paramref name="position"/>.</summary>
    private static CommandResult HandleReorder(SimulationEngine engine, string callsign, TrackOwner identity, string? listId, int position)
    {
        var (channel, item, error) = FindSenderItem(engine, callsign, identity, listId);
        if (channel is null || item is null)
        {
            return new CommandResult(false, error!);
        }

        channel.Items.Remove(item);
        var index = Math.Clamp(position - 1, 0, channel.Items.Count);
        channel.Items.Insert(index, item);
        engine.MarkCoordinationChanged();
        Log.LogInformation("Coordination reorder: {Callsign} to line {Position} on list {ListId}", callsign, position, channel.ListId);
        return new CommandResult(true, $"Moved {callsign} to line {position} on list {channel.ListId}");
    }

    /// <summary>Table 34 modify-text: only a held (unsent) message's text may change.</summary>
    private static CommandResult HandleModify(SimulationEngine engine, string callsign, TrackOwner identity, string? listId, string text)
    {
        var (channel, item, error) = FindSenderItem(engine, callsign, identity, listId);
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
        Log.LogInformation("Coordination modify: {Callsign} text on list {ListId}", callsign, channel.ListId);
        return new CommandResult(true, $"Set message text for {callsign} on list {channel.ListId}");
    }

    private static (CoordinationChannel? Channel, CoordinationItem? Item, string? Error) FindSenderItem(
        SimulationEngine engine,
        string callsign,
        TrackOwner identity,
        string? listId
    )
    {
        var scenario = engine.Scenario;
        if (scenario is null)
        {
            return (null, null, "No active scenario");
        }

        var tcp = FindTcpForIdentity(identity, scenario);
        if (tcp is null)
        {
            return (null, null, "No TCP for identity");
        }

        var channel = ResolveChannelForSender(scenario, tcp, listId, out var error);
        if (channel is null)
        {
            return (null, null, error);
        }

        if (!IsSender(channel, tcp))
        {
            return (null, null, $"TCP {tcp.Subset}{tcp.SectorId} is not a sender on list {channel.ListId}");
        }

        var item = channel.Items.FirstOrDefault(i =>
            i.AircraftId.Equals(callsign, StringComparison.OrdinalIgnoreCase) && (i.Status is not StarsCoordinationStatus.Recalled)
        );
        return item is null ? (null, null, $"No coordination item for {callsign} on list {channel.ListId}") : (channel, item, null);
    }

    private static Tcp? FindTcpForIdentity(TrackOwner identity, SimScenarioState scenario)
    {
        var tcp = TrackResolver.FindTcpForOwner(identity, scenario);
        if (tcp is not null)
        {
            return tcp;
        }

        // Fall back to constructing from identity fields
        if (identity.Subset is not null && identity.SectorId is not null)
        {
            return new Tcp(identity.Subset.Value, identity.SectorId, "", null);
        }

        return null;
    }

    private static CoordinationChannel? ResolveChannelForSender(SimScenarioState scenario, Tcp tcp, string? listId, out string? error)
    {
        error = null;

        if (listId is not null)
        {
            if (!scenario.CoordinationChannels.TryGetValue(listId, out var channel))
            {
                error = $"Unknown coordination list: {listId}";
                return null;
            }

            return channel;
        }

        // Auto-detect: find channel where tcp is a sender
        var matches = new List<CoordinationChannel>();
        foreach (var ch in scenario.CoordinationChannels.Values)
        {
            if (IsSender(ch, tcp))
            {
                matches.Add(ch);
            }
        }

        if (matches.Count == 0)
        {
            error = $"TCP {tcp.Subset}{tcp.SectorId} is not a sender on any coordination channel";
            return null;
        }

        if (matches.Count > 1)
        {
            error = $"TCP {tcp.Subset}{tcp.SectorId} is a sender on {matches.Count} channels; specify list ID";
            return null;
        }

        return matches[0];
    }

    private static bool IsSender(CoordinationChannel channel, Tcp tcp)
    {
        return channel.SendingTcps.Any(s => s.Subset == tcp.Subset && s.SectorId.Equals(tcp.SectorId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReceiver(CoordinationChannel channel, Tcp tcp)
    {
        return channel.Receivers.Any(r => r.Tcp.Subset == tcp.Subset && r.Tcp.SectorId.Equals(tcp.SectorId, StringComparison.OrdinalIgnoreCase));
    }
}
