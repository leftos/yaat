using System.Globalization;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The field the advisory names: the aircraft's destination, as the sim's <c>RFIS</c> handler takes it, with its reference point,
/// its name and its runway ends from the navigation data.
/// </summary>
/// <param name="Id">The destination as filed, upper-cased: <c>KOAK</c>.</param>
/// <param name="Name">The airport's name in title case (<c>Oakland San Francisco Bay</c>); empty when the data has none.</param>
/// <param name="Position">The airport reference point.</param>
/// <param name="AllRunways">Every runway end, sorted by number then side: <c>10L 10R 12 15 28L 28R 30 33</c>.</param>
public sealed record AdvisoryField(string Id, string Name, LatLon Position, IReadOnlyList<string> AllRunways)
{
    /// <summary>
    /// The destination field of <paramref name="aircraft"/>; null with no aircraft, no destination, or one the navigation data
    /// has no position or elevation for (the sim refuses <c>RFIS</c> for both).
    /// </summary>
    public static AdvisoryField? Of(IMenuAircraft? aircraft)
    {
        string id = (aircraft?.Destination ?? "").Trim().ToUpperInvariant();
        if ((id.Length == 0) || (NavigationDatabase.InstanceOrNull is not { } navigation))
        {
            return null;
        }

        if ((navigation.GetAirportPosition(id) is not (double lat, double lon)) || (navigation.GetAirportElevation(id) is null))
        {
            return null;
        }

        string name = navigation.GetAirportName(id) is { Length: > 0 } raw
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.ToLowerInvariant())
            : "";
        IReadOnlyList<RunwayInfo> known = navigation.GetRunways(id);
        return new AdvisoryField(
            id,
            name,
            new LatLon(lat, lon),
            Sorted(known.Select(runway => runway.Id.End1).Concat(known.Select(runway => runway.Id.End2)))
        );
    }

    /// <summary>The runway ends the field row names: the room's arrival ends at the field when it has any, else every end.</summary>
    public IReadOnlyList<string> RunwaysIn(IMenuHost host) => (host.GetActiveArrivalEnds(Id) is { Count: > 0 } active) ? Sorted(active) : AllRunways;

    /// <summary>
    /// Where the field is from <paramref name="aircraft"/>: the clock off its true heading and the whole miles, as the
    /// traffic-advisory matcher rounds them (<see cref="TrafficAdvisoryMatcher.ClockFrom"/>,
    /// <see cref="TrafficAdvisoryMatcher.MilesFrom"/>).
    /// </summary>
    public FieldAdvisoryDetails TruthFrom(IMenuAircraft aircraft)
    {
        TrafficAdvisoryCandidate me = AdvisoryCandidates.From(aircraft);
        var field = new TrafficAdvisoryCandidate(Id, Position, 0, 0, 0, "", "", false, null, null);
        return new FieldAdvisoryDetails(TrafficAdvisoryMatcher.ClockFrom(me, field), TrafficAdvisoryMatcher.MilesFrom(me, field));
    }

    private static List<string> Sorted(IEnumerable<string> ends) =>
        [.. ends.Distinct(StringComparer.Ordinal).Order(RunwayDesignatorComparer.Instance)];
}
