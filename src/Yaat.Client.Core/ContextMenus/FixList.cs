using System.Globalization;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The fix list the fix flyouts share: the aircraft's remaining route (<see cref="DirectToRoute"/>), each fix's distance and
/// magnetic bearing from the aircraft (<see cref="DistanceAndBearing"/>), the other fixes matching what is typed, nearest first
/// (<see cref="Matches"/>), whether a typed fix is known (<see cref="IsKnownFix"/>) and whether it is a fix-radial-distance
/// (<see cref="IsFrd"/>). Every lookup reads
/// <see cref="NavigationDatabase.InstanceOrNull"/> and answers without it.
/// </summary>
public static class FixList
{
    /// <summary>The most matches <see cref="Matches"/> lists.</summary>
    public const int MaxMatches = 50;

    /// <summary>
    /// The aircraft's route from the fix it is navigating to on. Without a navigating-to fix the list is the whole route
    /// (<see cref="IMenuAircraft.RouteFixNames"/>); otherwise it is the navigation route from that fix on, then the filed
    /// route's fixes after the last of them, then the destination — each fix once, in order, ignoring case, and with the
    /// departure airport never listed. It expands the filed route, so a caller reads it once and keeps it rather than on each
    /// refresh; without the navigation data the filed route adds nothing.
    /// </summary>
    public static IReadOnlyList<string> DirectToRoute(IMenuAircraft aircraft)
    {
        if (string.IsNullOrEmpty(aircraft.NavigatingTo))
        {
            return aircraft.RouteFixNames();
        }

        var fixes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string fix)
        {
            if (!string.IsNullOrWhiteSpace(fix) && seen.Add(fix))
            {
                fixes.Add(fix);
            }
        }

        AddNavRouteFromNext(aircraft.NavigationRoute, aircraft.NavigatingTo, TryAdd);
        AddFiledRouteAfter(aircraft, aircraft.NavigationRoute, TryAdd);
        TryAdd(aircraft.Destination);
        return fixes;
    }

    /// <summary>
    /// <paramref name="fix"/>'s distance and magnetic bearing from <paramref name="from"/>, <c>31 NM · 184°</c>: the distance to
    /// the whole mile, the bearing made magnetic by the declination at <paramref name="from"/> (so it reads as the heading the
    /// aircraft would fly there), 001 to 360. <paramref name="fix"/> may be a fix-radial-distance. Null when it has no position
    /// or the navigation data is not loaded.
    /// </summary>
    public static string? DistanceAndBearing(LatLon from, string fix)
    {
        if (Position(fix) is not { } position)
        {
            return null;
        }

        double distance = GeoMath.DistanceNm(from, position);
        double bearing = MagneticDeclination.TrueToMagnetic(GeoMath.BearingTo(from, position), from);
        string miles = Math.Round(distance, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        return $"{miles} NM · {HeadingFlyout.WholeHeading(bearing).ToString("D3", CultureInfo.InvariantCulture)}°";
    }

    /// <summary>
    /// Every fix whose name starts with <paramref name="prefix"/>, nearest to <paramref name="from"/> first, leaving out
    /// <paramref name="excluded"/> (ignoring case), at most <see cref="MaxMatches"/>; none for a blank prefix or without the
    /// navigation data.
    /// </summary>
    public static IReadOnlyList<string> Matches(string prefix, LatLon from, IReadOnlyCollection<string> excluded)
    {
        if (string.IsNullOrWhiteSpace(prefix) || (NavigationDatabase.InstanceOrNull is not { } db))
        {
            return [];
        }

        var skip = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        return [.. db.FixesNear(from, prefix.Trim()).Select(fix => fix.Name).Where(name => !skip.Contains(name)).Take(MaxMatches)];
    }

    /// <summary>
    /// Whether <paramref name="text"/> names a fix or a fix-radial-distance the direct-to parser resolves; true without the
    /// navigation data, which cannot tell.
    /// </summary>
    public static bool IsKnownFix(string text)
    {
        if (NavigationDatabase.InstanceOrNull is not { } db)
        {
            return true;
        }

        return (db.GetFixPosition(text) is not null) || (FrdResolver.Resolve(text, db) is not null);
    }

    /// <summary>
    /// The one fix or fix-radial-distance a fix box's <paramref name="text"/> names, trimmed and upper-cased, when the navigation
    /// data knows it (<see cref="IsKnownFix"/>); null for a blank box, several words or an unknown fix.
    /// </summary>
    public static string? TypedFixOrFrd(string text)
    {
        string fix = text.Trim().ToUpperInvariant();
        return ((fix.Length == 0) || fix.Any(char.IsWhiteSpace) || !IsKnownFix(fix)) ? null : fix;
    }

    /// <summary>The typed <paramref name="prefix"/>'s own list row when it is a fix-radial-distance, upper-cased; none otherwise.</summary>
    public static IReadOnlyList<string> FrdRow(string prefix)
    {
        string typed = prefix.ToUpperInvariant();
        return ((typed.Length > 0) && IsFrd(typed)) ? [typed] : [];
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a fix-radial-distance (<c>RBL184031</c>) the direct-to parser resolves rather than a
    /// fix's own name; false without the navigation data.
    /// </summary>
    public static bool IsFrd(string text) =>
        (NavigationDatabase.InstanceOrNull is { } db) && (db.GetFixPosition(text) is null) && (FrdResolver.Resolve(text, db) is not null);

    /// <summary>
    /// <paramref name="fix"/>'s position, a fix's own or a fix-radial-distance's point; null when it has none or the navigation
    /// data is not loaded.
    /// </summary>
    public static LatLon? Position(string fix)
    {
        if (NavigationDatabase.InstanceOrNull is not { } db)
        {
            return null;
        }

        return (db.GetFixPosition(fix) is { } position) ? new LatLon(position.Lat, position.Lon) : FrdResolver.Resolve(fix, db);
    }

    /// <summary>
    /// Adds the navigation route from the first entry equal to <paramref name="navigatingTo"/> on, or that fix alone
    /// when the route does not carry it.
    /// </summary>
    private static void AddNavRouteFromNext(IReadOnlyList<string> navRoute, string navigatingTo, Action<string> tryAdd)
    {
        int start = IndexOfFix(navRoute, navigatingTo);
        if (start < 0)
        {
            tryAdd(navigatingTo);
            return;
        }

        for (int i = start; i < navRoute.Count; i++)
        {
            tryAdd(navRoute[i]);
        }
    }

    /// <summary>
    /// Adds the filed route's fixes after the last of the aircraft's navigation route — or after the fix it is
    /// navigating to when the route is empty — or nothing when the filed route does not carry that fix or the navigation data
    /// is not loaded.
    /// </summary>
    private static void AddFiledRouteAfter(IMenuAircraft aircraft, IReadOnlyList<string> navRoute, Action<string> tryAdd)
    {
        if (string.IsNullOrWhiteSpace(aircraft.Route) || (NavigationDatabase.InstanceOrNull is not { } db))
        {
            return;
        }

        string last = navRoute.Count > 0 ? navRoute[^1] : aircraft.NavigatingTo;
        IReadOnlyList<string> expanded = db.ExpandRoute(aircraft.Route);
        int lastIndex = LastIndexOfFix(expanded, last);
        if (lastIndex < 0)
        {
            return;
        }

        for (int i = lastIndex + 1; i < expanded.Count; i++)
        {
            tryAdd(expanded[i]);
        }
    }

    /// <summary>The index of the first entry of <paramref name="fixes"/> equal to <paramref name="fix"/> ignoring case, or -1.</summary>
    private static int IndexOfFix(IReadOnlyList<string> fixes, string fix)
    {
        for (int i = 0; i < fixes.Count; i++)
        {
            if (string.Equals(fixes[i], fix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The index of the last entry of <paramref name="fixes"/> equal to <paramref name="fix"/> ignoring case, or -1.</summary>
    private static int LastIndexOfFix(IReadOnlyList<string> fixes, string fix)
    {
        for (int i = fixes.Count - 1; i >= 0; i--)
        {
            if (string.Equals(fixes[i], fix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
