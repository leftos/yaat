using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Pilot;

/// <summary>
/// A controller position type, ordered from the lowest (clearance delivery) to the highest (center). The numbers are
/// persisted in snapshots (<c>ScenarioSnapshotDto.StudentLowestWorkedLevel</c>) and must never be renumbered.
/// </summary>
public enum AtcPositionLevel
{
    Delivery = 0,
    Ground = 1,
    Tower = 2,
    Approach = 3,
    Center = 4,
}

/// <summary>Maps callsigns, position types and level codes to <see cref="AtcPositionLevel"/>.</summary>
public static class AtcPositionLevels
{
    /// <summary>
    /// The level of the position with this callsign: a <c>…_DEL</c> callsign is <see cref="AtcPositionLevel.Delivery"/>,
    /// any other takes the level of its <see cref="AtcPositionTypeClassifier"/> type.
    /// </summary>
    /// <param name="callsign">A position callsign such as <c>OAK_GND</c>.</param>
    /// <returns>The level, or null when the callsign names no controller position type.</returns>
    public static AtcPositionLevel? LevelOf(string? callsign)
    {
        if (callsign is not null && callsign.EndsWith("_DEL", StringComparison.OrdinalIgnoreCase))
        {
            return AtcPositionLevel.Delivery;
        }

        string? positionType = AtcPositionTypeClassifier.Classify(callsign);
        return positionType is null ? null : LevelOfType(positionType);
    }

    /// <summary>The level of a pilot-call position type (<c>GND</c>, <c>TWR</c>, <c>APP</c>, <c>CTR</c>); null for any other.</summary>
    /// <param name="positionType">The position type a pilot calls.</param>
    /// <returns>The level, or null when the type is not one of the four.</returns>
    public static AtcPositionLevel? LevelOfType(string positionType) =>
        positionType.ToUpperInvariant() switch
        {
            "GND" => AtcPositionLevel.Ground,
            "TWR" => AtcPositionLevel.Tower,
            "APP" => AtcPositionLevel.Approach,
            "CTR" => AtcPositionLevel.Center,
            _ => null,
        };

    /// <summary>Parses a level code (<c>DEL</c>, <c>GND</c>, <c>TWR</c>, <c>APP</c>, <c>CTR</c>), case-insensitive.</summary>
    /// <param name="text">The code to parse.</param>
    /// <param name="level">The parsed level; <see cref="AtcPositionLevel.Delivery"/> when parsing fails.</param>
    /// <returns>True when <paramref name="text"/> is one of the five codes.</returns>
    public static bool TryParse(string text, out AtcPositionLevel level)
    {
        AtcPositionLevel? parsed = text.ToUpperInvariant() switch
        {
            "DEL" => AtcPositionLevel.Delivery,
            "GND" => AtcPositionLevel.Ground,
            "TWR" => AtcPositionLevel.Tower,
            "APP" => AtcPositionLevel.Approach,
            "CTR" => AtcPositionLevel.Center,
            _ => null,
        };
        level = parsed ?? AtcPositionLevel.Delivery;
        return parsed is not null;
    }

    /// <summary>The level's code, the reverse of <see cref="TryParse"/>.</summary>
    /// <param name="level">The level.</param>
    /// <returns><c>DEL</c>, <c>GND</c>, <c>TWR</c>, <c>APP</c> or <c>CTR</c>.</returns>
    public static string Code(AtcPositionLevel level) =>
        level switch
        {
            AtcPositionLevel.Delivery => "DEL",
            AtcPositionLevel.Ground => "GND",
            AtcPositionLevel.Tower => "TWR",
            AtcPositionLevel.Approach => "APP",
            AtcPositionLevel.Center => "CTR",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown position level"),
        };
}

/// <summary>A scenario position that is online (<c>AutoConnect</c>): its level and the facility it works.</summary>
/// <param name="Level">The position's level.</param>
/// <param name="FacilityId">The scenario's facility id for it; for a tower-cab level, the airport it works.</param>
public sealed record OnlinePosition(AtcPositionLevel Level, string? FacilityId);

/// <summary>
/// The position types a solo student works: its own and every type below it down to <see cref="Lowest"/>, less any
/// type an online scenario position works at the airport in question.
/// </summary>
/// <param name="Own">The level of the student's own position.</param>
/// <param name="Lowest">The lowest level the student works.</param>
/// <param name="Online">The online scenario positions, which take their type out of the student's range where they cover.</param>
public sealed record StudentWorkedRange(AtcPositionLevel Own, AtcPositionLevel Lowest, IReadOnlyList<OnlinePosition> Online)
{
    /// <summary>
    /// Whether the student works <paramref name="level"/> at <paramref name="atAirportId"/>: the level is in
    /// [<see cref="Lowest"/>, <see cref="Own"/>] and no online position of that level covers the airport. An online
    /// delivery, ground or tower covers its own airport only; an online approach or center covers every airport.
    /// </summary>
    /// <param name="level">The position type a pilot calls.</param>
    /// <param name="atAirportId">The airport the call is made at; null when unknown.</param>
    /// <returns>True when the student answers for that level there.</returns>
    public bool Works(AtcPositionLevel level, string? atAirportId) =>
        (level >= Lowest) && (level <= Own) && !Online.Any(online => (online.Level == level) && Covers(online, atAirportId));

    /// <summary>
    /// The student's range, or null when there is no range to apply (no lowest level chosen, no student, or a student
    /// callsign that names no controller position type).
    /// </summary>
    /// <param name="student">The solo student's position.</param>
    /// <param name="lowest">The lowest level the student works; null when the setting is unset.</param>
    /// <param name="atcPositions">The scenario's resolved positions; those with <c>AutoConnect</c> are online.</param>
    /// <returns>The range, or null.</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="lowest"/> is above the student's own level.</exception>
    public static StudentWorkedRange? For(TrackOwner? student, AtcPositionLevel? lowest, IReadOnlyList<ResolvedAtcPosition> atcPositions)
    {
        if ((lowest is not { } lowestLevel) || (student is null) || (AtcPositionLevels.LevelOf(student.Callsign) is not { } own))
        {
            return null;
        }

        if (lowestLevel > own)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lowest),
                lowestLevel,
                $"The lowest worked level {lowestLevel} is above the student's own level {own} ({student.Callsign})"
            );
        }

        var online = new List<OnlinePosition>();
        foreach (ResolvedAtcPosition position in atcPositions)
        {
            if (!position.Source.AutoConnect || string.Equals(position.Owner.Callsign, student.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AtcPositionLevels.LevelOf(position.Owner.Callsign) is { } level)
            {
                online.Add(new OnlinePosition(level, position.Source.FacilityId));
            }
        }

        return new StudentWorkedRange(own, lowestLevel, online);
    }

    private static bool Covers(OnlinePosition online, string? atAirportId)
    {
        if (online.Level >= AtcPositionLevel.Approach)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(online.FacilityId)
            && !string.IsNullOrWhiteSpace(atAirportId)
            && NavigationDatabase.AirportIdsMatch(online.FacilityId, atAirportId);
    }
}
