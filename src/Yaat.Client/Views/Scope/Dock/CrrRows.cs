using System.Globalization;
using Avalonia.Media;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope.Dock;

/// <summary>One row of the CRR view: its text, and the group colour it draws in (a group's label row) or null (a member row).</summary>
/// <param name="Text">The whole text the row shows.</param>
/// <param name="GroupColor">The group's colour for its label row (<see cref="ScopeCrrReadout.ColorOf"/>); null for a member row.</param>
public sealed record CrrRow(string Text, SKColor? GroupColor)
{
    /// <summary>The brush the row's text draws in: its group's colour, or the views' text colour for a member row.</summary>
    public IBrush Foreground =>
        GroupColor is { } c ? new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue)) : ScopeViewCard.ViewTextBrush;
}

/// <summary>
/// The CRR view's rows (CRC <c>ViewCrr</c>, list mode): each group's label in its colour, then its members, each with its
/// distance to the group's point.
/// </summary>
public static class CrrRows
{
    /// <summary>
    /// The rows of <paramref name="groups"/> in their order, each a label row followed by one row per aircraft in
    /// <paramref name="aircraft"/> whose <see cref="AircraftModel.EramCrrGroup"/> is the group's label, nearest first, as
    /// <c>{callsign,-7}{distance,7}</c> (<see cref="ScopeCrrReadout.DistanceText"/>).
    /// </summary>
    public static IReadOnlyList<CrrRow> Build(IReadOnlyList<EramCrrGroupHubDto> groups, IEnumerable<AircraftModel> aircraft)
    {
        AircraftModel[] members = [.. aircraft.Where(a => a.EramCrrGroup is not null)];
        var rows = new List<CrrRow>();
        foreach (EramCrrGroupHubDto group in groups)
        {
            rows.Add(new CrrRow(group.Label, ScopeCrrReadout.ColorOf(group.Color)));
            IEnumerable<(string Callsign, double Nm)> inGroup = members
                .Where(a => a.EramCrrGroup == group.Label)
                .Select(a => (a.Callsign, Nm: GeoMath.DistanceNm(group.Latitude, group.Longitude, a.Position.Lat, a.Position.Lon)))
                .OrderBy(m => m.Nm)
                .ThenBy(m => m.Callsign, StringComparer.Ordinal);
            foreach ((string callsign, double nm) in inGroup)
            {
                rows.Add(new CrrRow(string.Create(CultureInfo.InvariantCulture, $"{callsign, -7}{ScopeCrrReadout.DistanceText(nm), 7}"), null));
            }
        }

        return rows;
    }
}
