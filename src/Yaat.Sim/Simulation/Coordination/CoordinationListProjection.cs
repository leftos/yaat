using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Simulation.Coordination;

/// <summary>
/// One STARS coordination (RD) list as the room's displays read it: the channel's adapted title and TCPs, whether any
/// receiver auto-acknowledges, and every item in ascending sequence order.
/// </summary>
public sealed record CoordinationListView(
    string ListId,
    string Title,
    bool AutoAcknowledge,
    IReadOnlyList<string> SenderTcps,
    IReadOnlyList<string> ReceiverTcps,
    IReadOnlyList<CoordinationItemView> Items
);

/// <summary>
/// One coordination item with the aircraft fields a display line shows (type, beacon, category) filled from the engine.
/// <see cref="ExpireElapsedSeconds"/> is sim-elapsed seconds, so a paused sim holds the countdown.
/// </summary>
public sealed record CoordinationItemView(
    string Id,
    string Callsign,
    StarsCoordinationStatus Status,
    string AircraftType,
    string Beacon,
    string Category,
    string ExitFix,
    string Message,
    string OriginTcp,
    double? ExpireElapsedSeconds
);

/// <summary>
/// Projects the engine's coordination channels into the read-only lists a display draws. Every channel and every item
/// is kept (Unsent included); channels stay in the order given, items sort by <see cref="CoordinationItem.SequenceNumber"/>.
/// Type, beacon and category follow CRC: the flight plan's type, the assigned beacon code padded to four digits, and
/// the CWT code for that type. An item whose aircraft the engine no longer holds gets empty strings for all three.
/// </summary>
public static class CoordinationListProjection
{
    private static readonly ILogger Log = SimLog.CreateLogger("CoordinationListProjection");

    public static IReadOnlyList<CoordinationListView> Build(IReadOnlyList<CoordinationChannel> channels, Func<string, AircraftState?> findAircraft)
    {
        var lists = new List<CoordinationListView>(channels.Count);
        foreach (CoordinationChannel channel in channels)
        {
            lists.Add(ToView(channel, findAircraft));
        }

        return lists;
    }

    private static CoordinationListView ToView(CoordinationChannel channel, Func<string, AircraftState?> findAircraft)
    {
        List<CoordinationItemView> items = [.. channel.Items.OrderBy(i => i.SequenceNumber).Select(i => ToView(i, findAircraft))];
        return new CoordinationListView(
            channel.ListId,
            channel.Title,
            channel.Receivers.Any(r => r.AutoAcknowledge),
            [.. channel.SendingTcps.Select(t => t.ToString())],
            [.. channel.Receivers.Select(r => r.Tcp.ToString())],
            items
        );
    }

    private static CoordinationItemView ToView(CoordinationItem item, Func<string, AircraftState?> findAircraft)
    {
        (string type, string beacon, string category) = AircraftFields(item.AircraftId, findAircraft);
        return new CoordinationItemView(
            item.Id,
            item.AircraftId,
            item.Status,
            type,
            beacon,
            category,
            item.ExitFix,
            item.Message,
            item.OriginTcp.ToString(),
            item.ExpireTime
        );
    }

    private static (string Type, string Beacon, string Category) AircraftFields(string callsign, Func<string, AircraftState?> findAircraft)
    {
        if (findAircraft(callsign) is not { } aircraft)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Coordination item for {Callsign}: the engine holds no such aircraft, so type, beacon and category are empty", callsign);
            }

            return ("", "", "");
        }

        string type = aircraft.FlightPlan.AircraftType;
        string beacon = aircraft.Transponder.AssignedCode.ToString("D4", CultureInfo.InvariantCulture);
        return (type, beacon, Category(type));
    }

    private static string Category(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "";
        }

        return WakeTurbulenceData.GetCwt(type) ?? FaaAircraftDatabase.Get(type)?.Cwt ?? "";
    }
}
