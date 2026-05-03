using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Assigned runway picker. Lists every runway end at the relevant airport (departure when
/// on the ground, destination when airborne) sorted numerically. Selection dispatches
/// RWY &lt;designator&gt;.
/// </summary>
internal static class RunwayFlyout
{
    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();
        menu.Items.Add(
            new MenuItem
            {
                Header = $"Runway — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        if (!string.IsNullOrEmpty(aircraft.AssignedRunway))
        {
            menu.Items.Add(
                new MenuItem
                {
                    Header = $"Currently assigned: {aircraft.AssignedRunway}",
                    IsEnabled = false,
                    FontStyle = FontStyle.Italic,
                }
            );
        }
        menu.Items.Add(new Separator());

        var clear = new MenuItem { Header = "Clear assigned runway" };
        clear.Click += async (_, _) => await radarVm.SendRawCommandAsync(aircraft.Callsign, initials, "RWY");
        menu.Items.Add(clear);

        // Pick the relevant airport: on-ground aircraft want a departure runway, airborne aircraft
        // want an approach runway. Fall back to the other if the primary is empty.
        string primary = aircraft.IsOnGround ? aircraft.Departure : aircraft.Destination;
        string fallback = aircraft.IsOnGround ? aircraft.Destination : aircraft.Departure;
        string airport = !string.IsNullOrEmpty(primary) ? primary : fallback;
        if (!string.IsNullOrEmpty(airport))
        {
            var ends = CollectRunwayEnds(airport);
            if (ends.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(
                    new MenuItem
                    {
                        Header = $"{airport} runways ({(aircraft.IsOnGround ? "departure" : "approach")})",
                        IsEnabled = false,
                        FontStyle = FontStyle.Italic,
                    }
                );
                foreach (var end in ends)
                {
                    var designator = end;
                    string label = string.Equals(aircraft.AssignedRunway, designator, System.StringComparison.OrdinalIgnoreCase)
                        ? $"▶ {designator}"
                        : designator;
                    var item = new MenuItem { Header = label };
                    item.Click += async (_, _) => await radarVm.SendRawCommandAsync(aircraft.Callsign, initials, $"RWY {designator}");
                    menu.Items.Add(item);
                }
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(
            new MenuItem
            {
                Header = "(For other airports: type 'RWY 28L' in the command bar)",
                IsEnabled = false,
                FontStyle = FontStyle.Italic,
            }
        );
        return menu;
    }

    /// <summary>
    /// Returns the unique runway-end designators for an airport, sorted numerically
    /// (e.g. 01L, 01R, 09, 10, 27R, 28L, 28R). NavigationDatabase returns one RunwayInfo
    /// per physical runway with both ends as End1/End2 — both ends are included.
    /// </summary>
    private static List<string> CollectRunwayEnds(string airport)
    {
        var runways = NavigationDatabase.Instance.GetRunways(airport);
        var unique = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var r in runways)
        {
            if (!string.IsNullOrEmpty(r.Id.End1))
            {
                unique.Add(r.Id.End1);
            }
            if (!string.IsNullOrEmpty(r.Id.End2))
            {
                unique.Add(r.Id.End2);
            }
        }
        return [.. unique.OrderBy(d => d, RunwayDesignatorComparer.Instance)];
    }
}

internal sealed class RunwayDesignatorComparer : IComparer<string>
{
    public static readonly RunwayDesignatorComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.Compare(x, y, System.StringComparison.OrdinalIgnoreCase);
        }
        var (xn, xs) = Split(x);
        var (yn, ys) = Split(y);
        int byNum = xn.CompareTo(yn);
        return byNum != 0 ? byNum : string.Compare(xs, ys, System.StringComparison.OrdinalIgnoreCase);
    }

    private static (int Num, string Suffix) Split(string designator)
    {
        int i = 0;
        while (i < designator.Length && char.IsDigit(designator[i]))
        {
            i++;
        }
        if (i == 0)
        {
            return (0, designator);
        }
        int num = int.Parse(designator[..i], System.Globalization.CultureInfo.InvariantCulture);
        return (num, designator[i..]);
    }
}
