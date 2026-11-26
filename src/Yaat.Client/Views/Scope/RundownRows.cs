using System.Globalization;
using System.Text;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope;

/// <summary>The colour a RUNDOWN row draws in: CRC's list text (green) or its caution colour (yellow).</summary>
public enum RundownRowColor
{
    /// <summary>CRC's list-text colour, the STARS green.</summary>
    ListText,

    /// <summary>CRC's caution colour, the STARS yellow.</summary>
    Caution,
}

/// <summary>One row of the Radar view's merged RUNDOWN list: every coordination channel of the room in one list.</summary>
/// <param name="ListId">The coordination channel the item belongs to, the row's channel column.</param>
/// <param name="ItemId">The item's id, which the row's context menu acts on.</param>
/// <param name="Callsign">The item's aircraft callsign.</param>
/// <param name="Status">The item's <c>StarsCoordinationStatus</c> name, as the training hub carries it.</param>
/// <param name="Text">The whole text the row shows: the channel column, then CRC's coordination line.</param>
/// <param name="Color">The row's colour.</param>
/// <param name="Blinks">Whether the row blinks; CRC draws its off phase at a quarter brightness.</param>
public sealed record RundownRow(string ListId, string ItemId, string Callsign, string Status, string Text, RundownRowColor Color, bool Blinks);

/// <summary>
/// The Radar view's merged RUNDOWN list: every coordination channel in the order the server sends them, each item as the
/// channel's list id in a column as wide as the widest list id, then CRC's STARS coordination line
/// (<c>[time][line][+][callsign][type][beacon][category][exit fix][message]</c>), coloured and blinking by its status.
/// </summary>
public static class RundownRows
{
    private const string Unsent = "Unsent";
    private const string Unacknowledged = "Unacknowledged";
    private const string Acknowledged = "Acknowledged";
    private const string Recalled = "Recalled";
    private const string DepartureExpirationWarning = "DepartureExpirationWarning";
    private const string VoidUnacknowledged = "VoidUnacknowledged";

    /// <summary>
    /// The rows of every list in <paramref name="lists"/>, in the server's order, with the countdown of each item that
    /// shows one taken against <paramref name="scenarioElapsedSeconds"/> (the sim's elapsed time, so a pause holds it).
    /// </summary>
    public static IReadOnlyList<RundownRow> Build(IReadOnlyList<CoordinationListHubDto> lists, double scenarioElapsedSeconds)
    {
        int listIdWidth = 0;
        foreach (CoordinationListHubDto list in lists)
        {
            listIdWidth = Math.Max(listIdWidth, list.ListId.Length);
        }

        var rows = new List<RundownRow>();
        foreach (CoordinationListHubDto list in lists)
        {
            foreach (CoordinationItemHubDto item in list.Items)
            {
                rows.Add(Row(list.ListId, listIdWidth, item, scenarioElapsedSeconds));
            }
        }

        return rows;
    }

    /// <summary>Whether <paramref name="status"/> is one of the STARS coordination statuses the rows know.</summary>
    public static bool IsKnownStatus(string status) =>
        status is Unsent or Unacknowledged or Acknowledged or Recalled or DepartureExpirationWarning or VoidUnacknowledged;

    private static RundownRow Row(string listId, int listIdWidth, CoordinationItemHubDto item, double scenarioElapsedSeconds)
    {
        var text = new StringBuilder();
        text.Append(listId.PadRight(listIdWidth)).Append(' ');
        text.Append(TimeText(item, scenarioElapsedSeconds));
        text.Append(item.LineNumber?.ToString(CultureInfo.InvariantCulture).PadLeft(2) ?? "  ");
        text.Append(item.Status is Acknowledged or DepartureExpirationWarning ? '+' : ' ');
        text.Append(item.Callsign.PadLeft(8)).Append(' ');
        AppendField(text, item.AircraftType);
        AppendField(text, item.Beacon);
        AppendField(text, item.Category);
        AppendField(text, item.ExitFix);
        text.Append(item.Status == Recalled ? "RECALL" : item.Message);

        (RundownRowColor color, bool blinks) = Style(item.Status);
        return new RundownRow(listId, item.Id, item.Callsign, item.Status, text.ToString(), color, blinks);
    }

    /// <summary>
    /// CRC's time column: the remaining time as <c>m:ss </c> for the statuses that show a countdown, else five spaces.
    /// An expired or absent expiry reads <c>0:00 </c>.
    /// </summary>
    private static string TimeText(CoordinationItemHubDto item, double scenarioElapsedSeconds)
    {
        if (item.Status is not (Acknowledged or DepartureExpirationWarning or VoidUnacknowledged))
        {
            return "     ";
        }

        if (item.ExpireElapsedSeconds is not { } expire)
        {
            return "0:00 ";
        }

        int seconds = (int)Math.Floor(expire - scenarioElapsedSeconds);
        if (seconds <= 0)
        {
            return "0:00 ";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00} ");
    }

    private static void AppendField(StringBuilder text, string value) => text.Append(value).Append(' ');

    private static (RundownRowColor Color, bool Blinks) Style(string status) =>
        status switch
        {
            Unsent => (RundownRowColor.ListText, false),
            Unacknowledged => (RundownRowColor.ListText, true),
            Acknowledged => (RundownRowColor.Caution, false),
            Recalled => (RundownRowColor.ListText, true),
            DepartureExpirationWarning => (RundownRowColor.Caution, true),
            VoidUnacknowledged => (RundownRowColor.ListText, true),
            _ => (RundownRowColor.ListText, false),
        };
}
