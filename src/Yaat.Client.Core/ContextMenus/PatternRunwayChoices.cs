using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Client.ContextMenus;

/// <summary>One runway end a pattern entry's flyout offers.</summary>
/// <param name="Designator">The end in display form (<c>8R</c>, not <c>08R</c>), which the row names and its command sends.</param>
/// <param name="End">The runway with this end active, as the navigation data has it.</param>
/// <param name="IsActive">Whether the room has the end active for arrivals or both ways.</param>
/// <param name="IsLandable">
/// Whether the aircraft's physical type can land on the end (<see cref="RunwayLandability.IsLandable"/>).
/// </param>
public sealed record PatternRunwayChoice(string Designator, RunwayInfo End, bool IsActive, bool IsLandable);

/// <summary>
/// The runway ends a pattern entry (enter left or right downwind, left or right base, straight-in final) offers, in the
/// order its flyout lists them, and the glyph each is drawn with: the entry's lands-west glyph rotated by the end's true
/// course less 270°, so the arrowhead points the landing direction, with Make straight-in mirrored onto the right-traffic
/// side when that is the end's default pattern side (<see cref="PatternGeometry.InferDefaultPatternDirection"/>: an L/R
/// parallel sends its pattern outboard, else left traffic).
/// </summary>
public static class PatternRunwayChoices
{
    private static readonly FrozenSet<string> PatternEntryIds = FrozenSet.Create(
        StringComparer.Ordinal,
        MenuIds.PatternEnterLeftDownwind,
        MenuIds.PatternEnterRightDownwind,
        MenuIds.PatternEnterLeftBase,
        MenuIds.PatternEnterRightBase,
        MenuIds.PatternEnterFinal
    );

    private static readonly ILogger Log = AppLog.CreateLogger("PatternRunwayChoices");

    /// <summary>The airport a pattern entry offers runways at: the aircraft's destination, else its departure airport; null without either.</summary>
    public static string? AirportOf(IMenuAircraft? aircraft)
    {
        if (aircraft is null)
        {
            return null;
        }

        string airport = !string.IsNullOrEmpty(aircraft.Destination) ? aircraft.Destination : aircraft.Departure;
        return !string.IsNullOrEmpty(airport) ? airport : null;
    }

    /// <summary>
    /// Every runway end at <paramref name="airport"/>, the ends in <paramref name="activeArrivalEnds"/> first and the rest
    /// after them; within each group the ends <paramref name="aircraftType"/> can land on first and the short ones last,
    /// each part in runway order (<see cref="RunwayDesignators.ForAirport"/>). An end's landing distance available is the
    /// navigation data's declared figure, else its pavement length (<see cref="RunwayLandability.LandingDistanceAvailableFt"/>).
    /// Empty when the navigation data has no runways there; an end it lists but cannot resolve is logged and left out.
    /// </summary>
    /// <param name="airport">The airport, as the aircraft files it.</param>
    /// <param name="aircraftType">The physical type the landing distance is judged for (<see cref="IMenuAircraft.BaseAircraftType"/>).</param>
    /// <param name="activeArrivalEnds">The room's arrival or both-way active ends there (<see cref="ActiveArrivalEnds"/>).</param>
    public static IReadOnlyList<PatternRunwayChoice> For(string airport, string aircraftType, IReadOnlyList<string> activeArrivalEnds)
    {
        var active = new HashSet<string>(activeArrivalEnds.Select(RunwayIdentifier.NormalizeDesignator), StringComparer.OrdinalIgnoreCase);
        List<PatternRunwayChoice> choices = [];
        foreach (string designator in RunwayDesignators.ForAirport(airport))
        {
            if (End(airport, designator) is not { } end)
            {
                Log.LogWarning(
                    "Pattern entry: runway {Runway} is listed at {Airport} but the navigation data does not resolve it; left out",
                    designator,
                    airport
                );
                continue;
            }

            bool isLandable = RunwayLandability.IsLandable(
                RunwayLandability.LandingDistanceAvailableFt(NavigationDatabase.Instance, end),
                aircraftType
            );
            choices.Add(new PatternRunwayChoice(designator, end, active.Contains(end.Designator), isLandable));
        }

        return [.. choices.OrderBy(choice => !choice.IsActive).ThenBy(choice => !choice.IsLandable)];
    }

    /// <summary>
    /// The glyph pattern entry <paramref name="entryId"/> is drawn with for landing on <paramref name="end"/>'s active end:
    /// its lands-west glyph rotated by the end's true course less 270°, Make straight-in mirrored to right traffic when that
    /// is the end's default pattern side.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="entryId"/> is not a pattern entry.</exception>
    public static QuickCommandGlyph Glyph(string entryId, RunwayInfo end)
    {
        if (!PatternEntryIds.Contains(entryId) || (QuickCommandGlyphs.For(entryId) is not { } landsWest))
        {
            throw new ArgumentException($"'{entryId}' is not a pattern entry with a glyph.", nameof(entryId));
        }

        bool rightTrafficFinal =
            (entryId == MenuIds.PatternEnterFinal) && (PatternGeometry.InferDefaultPatternDirection(end) == PatternDirection.Right);
        return landsWest with { RotationDegrees = end.TrueHeading.Degrees - 270, MirroredTopToBottom = rightTrafficFinal };
    }

    /// <summary>
    /// The glyph the strip shows for <paramref name="entryId"/> on <paramref name="aircraft"/>'s menu: for a pattern entry
    /// with a runway assigned that resolves at the aircraft's airport (<see cref="AirportOf"/>), that runway's rotated glyph;
    /// otherwise <paramref name="glyph"/>, the entry's own.
    /// </summary>
    public static QuickCommandGlyph StripGlyph(string entryId, QuickCommandGlyph glyph, IMenuAircraft aircraft)
    {
        if (!PatternEntryIds.Contains(entryId) || (aircraft.AssignedRunway.Length == 0) || (AirportOf(aircraft) is not { } airport))
        {
            return glyph;
        }

        return (End(airport, aircraft.AssignedRunway) is { } end) ? Glyph(entryId, end) : glyph;
    }

    /// <summary>
    /// The room's active runway ends at <paramref name="airportId"/> that it lands on — used for arrivals or both ways
    /// (<c>A28R</c>, <c>30</c>), never a departure-only end (<c>D28L</c>) — in the room's order and zero-padded
    /// (<c>01L</c>).
    /// </summary>
    /// <param name="runways">The room's active runways, as <see cref="ReadRoomActiveRunways"/> reads them.</param>
    /// <param name="airportId">The airport, in any form <see cref="ActiveRunways.For"/> accepts (<c>KOAK</c>, <c>OAK</c>).</param>
    public static IReadOnlyList<string> ActiveArrivalEnds(ActiveRunways runways, string airportId) =>
        [
            .. runways
                .For(airportId)
                .Where(runway => runway.Use is ActiveRunwayUse.Both or ActiveRunwayUse.Arrival)
                .Select(runway => runway.Designator),
        ];

    /// <summary>
    /// The room's active runways, read from its token lists by <see cref="ActiveRunwayListParser.FromTokenLists"/> under
    /// <paramref name="source"/>; every token it cannot read names no runway and is reported to <paramref name="warn"/>,
    /// once per call. The one reading every client surface shares (a menu once per build, the ground view per question).
    /// </summary>
    /// <param name="roomActiveRunways">The room's active-runway tokens by airport, as the server sends them.</param>
    /// <param name="source">What the list is read for, which every warning names.</param>
    /// <param name="warn">Receives each warning line.</param>
    public static ActiveRunways ReadRoomActiveRunways(
        IReadOnlyDictionary<string, IReadOnlyList<string>> roomActiveRunways,
        string source,
        Action<string> warn
    )
    {
        var byAirport = roomActiveRunways.ToDictionary(entry => entry.Key, entry => (List<string>?)[.. entry.Value], StringComparer.Ordinal);
        var warnings = new List<string>();
        ActiveRunways runways = ActiveRunwayListParser.FromTokenLists(byAirport, source, warnings);
        foreach (string warning in warnings)
        {
            warn(warning);
        }

        return runways;
    }

    private static RunwayInfo? End(string airport, string designator) =>
        NavigationDatabase.Instance.GetRunway(airport, RunwayIdentifier.NormalizeDesignator(designator));
}
