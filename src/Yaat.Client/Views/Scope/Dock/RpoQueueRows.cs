using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>One row of the RPO queue: the whole text it shows, and the callsign a click on it selects.</summary>
public sealed record RpoQueueRow(string Callsign, string Text);

/// <summary>
/// The pilot lens's RPO queue rows, what the RPO must answer or watch: each aircraft's check-in exception
/// (<see cref="ScopeCheckIn"/>), then each queued pilot command still waiting on its trigger (<see cref="ScopeQueueBadge"/>).
/// </summary>
public static class RpoQueueRows
{
    /// <summary>
    /// The check-in rows, aircraft by callsign (ordinal), then the queued-command rows, aircraft by callsign and each
    /// aircraft's lines in their order, seen from <paramref name="viewer"/>.
    /// </summary>
    public static IReadOnlyList<RpoQueueRow> Build(IEnumerable<AircraftModel> aircraft, ScopeViewer viewer)
    {
        AircraftModel[] sorted = [.. aircraft.OrderBy(a => a.Callsign, StringComparer.Ordinal)];
        var rows = new List<RpoQueueRow>();
        foreach (AircraftModel ac in sorted)
        {
            if (CheckInText(ac, ScopeCheckIn.StateOf(ac, viewer)) is { } text)
            {
                rows.Add(new RpoQueueRow(ac.Callsign, text));
            }
        }

        foreach (AircraftModel ac in sorted)
        {
            foreach (string line in ScopeQueueBadge.PendingLines(ac.PendingCommands))
            {
                rows.Add(new RpoQueueRow(ac.Callsign, $"{ac.Callsign} {WithoutIndex(line)} wait"));
            }
        }

        return rows;
    }

    private static string? CheckInText(AircraftModel ac, ScopeCheckInState state) =>
        state switch
        {
            ScopeCheckInState.Calling => $"{ac.Callsign} calling {(ac.PendingPilotRequest?.Kind ?? "").ToUpperInvariant()} !",
            ScopeCheckInState.VciMismatch => $"{ac.Callsign} not checked in (VCI lit) !",
            ScopeCheckInState.NotCheckedIn => $"{ac.Callsign} not checked in (no VCI) !",
            ScopeCheckInState.VciUnset => $"{ac.Callsign} checked in, VCI not set",
            _ => null,
        };

    /// <summary><paramref name="line"/> without its leading <c>[n] </c>; a line without one is kept whole.</summary>
    private static string WithoutIndex(string line)
    {
        int end = line.IndexOf("] ", StringComparison.Ordinal);
        return (line.StartsWith('[') && (end > 0)) ? line[(end + 2)..] : line;
    }
}
