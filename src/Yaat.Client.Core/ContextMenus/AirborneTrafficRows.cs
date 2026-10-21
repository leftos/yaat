using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What the Report traffic in sight… and Follow traffic… flyouts read from the airborne traffic: the rows they list (the
/// <see cref="ListedCount"/> nearest within <see cref="ListedRadiusNm"/>, or, with text typed, every aircraft matching it),
/// the words a row prints for the direction of flight and the pattern leg, and whether the commanded aircraft has reported a
/// row's aircraft in sight or follows it.
/// </summary>
public static class AirborneTrafficRows
{
    /// <summary>How many aircraft a traffic flyout lists with nothing typed.</summary>
    public const int ListedCount = 8;

    /// <summary>How far away, in NM, an aircraft a traffic flyout lists with nothing typed may be.</summary>
    public const double ListedRadiusNm = 10;

    /// <summary>The directions of flight as words, north first and clockwise, in the order of <see cref="SoloAdvisoryText.Directions"/>.</summary>
    public static IReadOnlyList<string> DirectionWords { get; } =
    ["northbound", "northeastbound", "eastbound", "southeastbound", "southbound", "southwestbound", "westbound", "northwestbound"];

    /// <summary>
    /// <paramref name="target"/> as a traffic flyout row seen from <paramref name="from"/>, its direction of flight from
    /// <paramref name="targetTrueTrackDegrees"/> and its leg from <paramref name="targetPattern"/> and its assigned runway.
    /// </summary>
    public static MenuAirborneTrafficRow Row(
        IMenuAircraft from,
        IMenuAircraft target,
        double targetTrueTrackDegrees,
        PatternPosition? targetPattern
    ) => new(RelativeGeometry.TrafficRow(from, target), DirectionWord(targetTrueTrackDegrees), LegWord(targetPattern, target.AssignedRunway));

    /// <summary>
    /// The 8-point direction of flight of a true track, each word 45° wide and centred on its own point, halves away from
    /// zero: <c>northbound</c>, <c>northeastbound</c> … <c>northwestbound</c>.
    /// </summary>
    public static string DirectionWord(double trueTrackDegrees)
    {
        double normalized = ((trueTrackDegrees % 360) + 360) % 360;
        int index = (int)Math.Round(normalized / 45, MidpointRounding.AwayFromZero) % DirectionWords.Count;
        return DirectionWords[index];
    }

    /// <summary>
    /// The pattern leg <paramref name="position"/> names, with its side and <paramref name="assignedRunway"/> where known:
    /// <c>left downwind 28L</c>, <c>right base 28R</c>, <c>final 30</c> (final never names a side); null outside a pattern.
    /// </summary>
    public static string? LegWord(PatternPosition? position, string assignedRunway)
    {
        if (position is null)
        {
            return null;
        }

        string leg = position.Leg switch
        {
            PatternEntryLeg.Upwind => "upwind",
            PatternEntryLeg.Crosswind => "crosswind",
            PatternEntryLeg.Downwind => "downwind",
            PatternEntryLeg.Base => "base",
            PatternEntryLeg.Final => "final",
            _ => throw new ArgumentOutOfRangeException(nameof(position), position.Leg, "Unknown pattern leg"),
        };
        string side =
            ((position.Leg != PatternEntryLeg.Final) && (position.Side is { } direction))
                ? ((direction == PatternDirection.Left) ? "left " : "right ")
                : "";
        string runway = string.IsNullOrWhiteSpace(assignedRunway) ? "" : $" {assignedRunway.Trim()}";
        return $"{side}{leg}{runway}";
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> has reported <paramref name="callsign"/> in sight: the in-sight flag is set and the
    /// traffic it last reported is that aircraft (the callsign outlives the flag, so it alone does not say so).
    /// </summary>
    public static bool IsInSight(IMenuAircraft? aircraft, string callsign) =>
        (aircraft is not null)
        && aircraft.SituationFlags.HasFlag(SituationFlags.HasReportedTrafficInSight)
        && string.Equals(aircraft.LastReportedTrafficCallsign, callsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="aircraft"/> follows <paramref name="callsign"/> (<see cref="IMenuAircraft.FollowingCallsign"/>).</summary>
    public static bool IsFollowing(IMenuAircraft? aircraft, string callsign) =>
        string.Equals(aircraft?.FollowingCallsign, callsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="row"/> matches the box's <paramref name="typed"/> text: blank matches every row, else the
    /// callsign holding it or the type starting with it, ignoring case.
    /// </summary>
    public static bool Matches(MenuAirborneTrafficRow row, string typed) =>
        (typed.Length == 0)
        || row.Callsign.Contains(typed, StringComparison.OrdinalIgnoreCase)
        || row.Traffic.AircraftType.StartsWith(typed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The rows a traffic flyout lists from <paramref name="traffic"/>, nearest first: with nothing typed, the
    /// <see cref="ListedCount"/> nearest within <see cref="ListedRadiusNm"/>; with text typed, every aircraft matching it at
    /// any distance. <paramref name="lead"/>, the aircraft reported in sight that Follow traffic… lists first, goes on top
    /// wherever it is, unless the typed text leaves it out, the rest following it; with nothing typed the list still holds
    /// <see cref="ListedCount"/> rows, so a lead past the nearest takes the last place. Null for no lead.
    /// </summary>
    public static List<MenuAirborneTrafficRow> Listed(IReadOnlyList<MenuAirborneTrafficRow> traffic, string typed, string? lead)
    {
        List<MenuAirborneTrafficRow> nearestFirst = [.. traffic.OrderBy(row => row.Traffic.DistanceNm)];
        MenuAirborneTrafficRow? leadRow =
            (lead is null) ? null : nearestFirst.FirstOrDefault(row => SameCallsign(row.Callsign, lead) && Matches(row, typed));
        IEnumerable<MenuAirborneTrafficRow> others = nearestFirst.Where(row => !ReferenceEquals(row, leadRow));
        int room = (leadRow is null) ? ListedCount : ListedCount - 1;
        List<MenuAirborneTrafficRow> listed =
            (typed.Length == 0)
                ? [.. others.Where(row => row.Traffic.DistanceNm <= ListedRadiusNm).Take(room)]
                : [.. others.Where(row => Matches(row, typed))];
        if (leadRow is not null)
        {
            listed.Insert(0, leadRow);
        }

        return listed;
    }

    private static bool SameCallsign(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
