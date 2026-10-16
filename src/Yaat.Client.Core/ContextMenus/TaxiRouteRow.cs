using Avalonia.Threading;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One taxi route a ground submenu offers as a command row: a preset taxi route, or a runway entry point of Taxi to runway.
/// It shows its badge, name, why it is offered, the taxiways it follows and its distance from the aircraft, sends
/// <see cref="Command"/> (a leaf) or opens <see cref="Variants"/> (a runway entry), and previews <see cref="Preview"/> on hover.
/// </summary>
/// <param name="Badge"><see cref="RunwayEntryBadge"/> for a runway entry point, <see cref="PresetBadge"/> for a preset route.</param>
/// <param name="Name">What the row names: the entry's hold short (<c>At W4</c>) or the preset's name (<c>TERMINAL to 30</c>).</param>
/// <param name="Reason">
/// Why an entry is offered (<c>nearest</c>, <c>full length</c>, <c>nearest, full length</c>); null for a preset and for an
/// intersection entry that is not the nearest.
/// </param>
/// <param name="AvailableFt">
/// The runway an intersection entry leaves ahead of a departure, rounded down to 50 ft, shown as <c>~7,600 ft avail</c>;
/// null for a full-length entry and a preset.
/// </param>
/// <param name="IsHighlighted">Whether the row is drawn bold: an end's full-length entry and its nearest one.</param>
/// <param name="Via">The taxiways the route follows (<c>via S T V W4</c>), or <c>direct</c>.</param>
/// <param name="DistanceFt">The distance from the aircraft along the route to its end, rounded to the nearest 50 ft.</param>
/// <param name="Command">
/// The finished command the row shows: what a preset sends, or the first of a runway entry's <see cref="Variants"/>.
/// </param>
/// <param name="Preview">The route from the aircraft the surface previews while the pointer is over the row.</param>
/// <param name="Variants">
/// The commands a runway entry opens (For departure, Hold short of the runway, and their crossing variants, with
/// <see cref="MenuCommandChoice.Separator"/> between the two kinds); <c>[]</c> for a preset, which sends <see cref="Command"/>.
/// </param>
public sealed record TaxiRouteRow(
    string Badge,
    string Name,
    string? Reason,
    double? AvailableFt,
    bool IsHighlighted,
    string Via,
    double DistanceFt,
    string Command,
    TaxiRoute Preview,
    IReadOnlyList<MenuCommandChoice> Variants
)
{
    /// <summary>The badge of a runway entry point's row.</summary>
    public const string RunwayEntryBadge = "RW";

    /// <summary>The badge of a preset taxi route's row.</summary>
    public const string PresetBadge = "PR";
}

/// <summary>One runway end's rows in the Taxi to runway submenu, under its title.</summary>
/// <param name="Title">
/// The group's title: <c>Runway 30 · assigned runway</c>, <c>Runway 28L · departure runway</c>, or <c>Runway 30</c> for an
/// end offered for neither reason (inline when the room names no active runway, or under Other runways).
/// </param>
/// <param name="Rows">
/// The end's entry points, the full-length one first and then the intersections from the threshold inward, then the
/// presets that end at it.
/// </param>
public sealed record TaxiToRunwayGroup(string Title, IReadOnlyList<TaxiRouteRow> Rows);

/// <summary>
/// The Taxi to runway submenu's content: the groups shown inline, then those under Other runways, found already or
/// searched only when Other runways opens.
/// </summary>
/// <param name="Inline">
/// The assigned runway's end first, then the room's active departure ends in the room's order; with no active departure
/// runway at the airport, the ends nearest the aircraft, up to three counting the assigned one.
/// </param>
/// <param name="Other">Every other end with a row, in runway order, when already found; empty when <paramref name="FindOther"/> is set.</param>
/// <param name="FindOther">
/// Searches the other ends' rows (in runway order) when Other runways opens, for a menu whose inline ends come from the
/// room's active departure runways; null when <paramref name="Other"/> already holds them or no runway end is left.
/// </param>
public sealed record TaxiToRunwayMenu(
    IReadOnlyList<TaxiToRunwayGroup> Inline,
    IReadOnlyList<TaxiToRunwayGroup> Other,
    Func<IReadOnlyList<TaxiToRunwayGroup>>? FindOther
)
{
    /// <summary>No group at all.</summary>
    public static TaxiToRunwayMenu Empty { get; } = new([], [], null);

    /// <summary>Every runway end of the airport, each with where its search stands; <see cref="TaxiToRunwayEndList.None"/> without one.</summary>
    public TaxiToRunwayEndList RunwayEnds { get; init; } = TaxiToRunwayEndList.None;
}

/// <summary>Why Taxi to runway offers a runway end.</summary>
public enum TaxiToRunwayEndRole
{
    /// <summary>The aircraft's assigned runway.</summary>
    Assigned,

    /// <summary>One of the room's active departure runways.</summary>
    Departure,

    /// <summary>Any other end of the airport.</summary>
    Other,
}

/// <summary>Where a runway end's entry search stands.</summary>
public enum TaxiToRunwayEndState
{
    /// <summary>Not searched yet: its runway is searched the first time it is asked for (<see cref="TaxiToRunwayEndList.SearchRunwayOf"/>).</summary>
    NotSearched,

    /// <summary>Its runway's search is running.</summary>
    Searching,

    /// <summary>Searched, with at least one row.</summary>
    Ready,

    /// <summary>Searched, or with no hold short to search, and no row: no runway entry found.</summary>
    Empty,
}

/// <summary>One runway end Taxi to runway names, found or not.</summary>
/// <param name="Designator">The end as a title shows it (<c>1L</c>, <c>28R</c>).</param>
/// <param name="Runway">The runway the end belongs to; one search finds both its ends' rows.</param>
/// <param name="Role">Why the end is offered.</param>
/// <param name="State">Where the end's search stands.</param>
/// <param name="Rows">
/// Once <see cref="TaxiToRunwayEndState.Ready"/>: the end's entry points, the full-length one first and then the intersections
/// from the threshold inward, then the presets that end at it; empty in every other state.
/// </param>
public sealed record TaxiToRunwayEnd(
    string Designator,
    RunwayIdentifier Runway,
    TaxiToRunwayEndRole Role,
    TaxiToRunwayEndState State,
    IReadOnlyList<TaxiRouteRow> Rows
);

/// <summary>
/// Every runway end Taxi to runway offers, in order: the assigned end, the room's active departure ends in the room's
/// order, then the rest by runway number. Used on the UI thread only. <see cref="SearchRunwayOf"/> searches a runway that
/// was not searched when the menu opened.
/// </summary>
/// <param name="ends">The ends as the menu opened.</param>
/// <param name="searchRunway">
/// Searches one runway, completing on the UI thread with each of its ends' rows, or with null when the result no longer
/// applies: the layout changed, or the route search or the row build failed (logged). A null leaves its ends to be
/// searched again.
/// </param>
public sealed class TaxiToRunwayEndList(
    IReadOnlyList<TaxiToRunwayEnd> ends,
    Func<RunwayIdentifier, Task<IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>?>> searchRunway
)
{
    /// <summary>No runway end at all: no layout, no start node, or an unknown aircraft.</summary>
    public static TaxiToRunwayEndList None { get; } =
        new([], runway => throw new InvalidOperationException($"No runway end to search: runway {runway} is not in an empty end list."));

    private readonly Dictionary<RunwayIdentifier, Task> _searching = [];

    /// <summary>The ends as they stand now: a runway asked for reads <see cref="TaxiToRunwayEndState.Searching"/> until its search lands.</summary>
    public IReadOnlyList<TaxiToRunwayEnd> Ends { get; private set; } = ends;

    /// <summary>
    /// Searches the runway of <paramref name="designator"/> when no search of it has run: both its ends read
    /// <see cref="TaxiToRunwayEndState.Searching"/> at once, and the task completes on the UI thread once <see cref="Ends"/>
    /// holds their rows (<see cref="TaxiToRunwayEndState.Ready"/> or <see cref="TaxiToRunwayEndState.Empty"/>). A runway
    /// whose search is running gives back that search; one already searched, a completed task. A search that no longer
    /// applies (the layout changed, or the route search or the row build failed, logged) puts that runway's ends back to
    /// <see cref="TaxiToRunwayEndState.NotSearched"/>, so a later select searches again rather than leaving them Searching.
    /// </summary>
    /// <exception cref="ArgumentException">The list names no end <paramref name="designator"/>.</exception>
    public Task SearchRunwayOf(string designator)
    {
        Dispatcher.UIThread.VerifyAccess();
        TaxiToRunwayEnd end =
            Ends.FirstOrDefault(e => SameEnd(e.Designator, designator))
            ?? throw new ArgumentException($"Taxi to runway names no runway end {designator}.", nameof(designator));
        if (_searching.TryGetValue(end.Runway, out Task? running))
        {
            return running;
        }

        if (end.State is TaxiToRunwayEndState.Ready or TaxiToRunwayEndState.Empty)
        {
            return Task.CompletedTask;
        }

        Task search = SearchAsync(end.Runway);
        if (!search.IsCompleted)
        {
            _searching[end.Runway] = search;
        }

        return search;
    }

    private async Task SearchAsync(RunwayIdentifier runway)
    {
        Ends = [.. Ends.Select(e => (e.Runway == runway) ? e with { State = TaxiToRunwayEndState.Searching } : e)];
        try
        {
            IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>? found = await searchRunway(runway);
            Ends = [.. Ends.Select(e => (e.Runway == runway) ? Searched(e, found) : e)];
        }
        finally
        {
            _searching.Remove(runway);
        }
    }

    /// <summary>
    /// <paramref name="end"/> once its runway's search lands: the rows <paramref name="found"/> gives it
    /// (<see cref="Found"/>), or back to <see cref="TaxiToRunwayEndState.NotSearched"/> when <paramref name="found"/> is
    /// null — the layout changed, or the route search or the row build failed (logged) — so its runway is searched again.
    /// </summary>
    private static TaxiToRunwayEnd Searched(TaxiToRunwayEnd end, IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)>? found) =>
        found is null ? end with { State = TaxiToRunwayEndState.NotSearched, Rows = [] } : Found(end, found);

    /// <summary><paramref name="end"/> with the rows <paramref name="found"/> gives it: Ready with rows, Empty without.</summary>
    private static TaxiToRunwayEnd Found(TaxiToRunwayEnd end, IReadOnlyList<(string Designator, IReadOnlyList<TaxiRouteRow> Rows)> found)
    {
        IReadOnlyList<TaxiRouteRow> rows = found.Where(f => SameEnd(f.Designator, end.Designator)).Select(f => f.Rows).FirstOrDefault() ?? [];
        return end with { State = (rows.Count > 0) ? TaxiToRunwayEndState.Ready : TaxiToRunwayEndState.Empty, Rows = rows };
    }

    private static bool SameEnd(string a, string b) =>
        string.Equals(RunwayIdentifier.NormalizeDesignator(a), RunwayIdentifier.NormalizeDesignator(b), StringComparison.OrdinalIgnoreCase);
}
