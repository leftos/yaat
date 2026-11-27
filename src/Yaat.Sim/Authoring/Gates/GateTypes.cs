using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Authoring.Gates;

/// <summary>
/// Why a stand ranks where it does for one aircraft (<see cref="GateRanking.Rank"/>): the two fitting tiers first, best
/// first, then the reasons a stand is left out, in the order they are judged.
/// </summary>
public enum GateReason
{
    /// <summary>The stand fits, is free, and its parking area is tagged for the aircraft's airline.</summary>
    Preferred,

    /// <summary>The stand fits, is free, and its parking area is untagged or it lies in no area.</summary>
    Fits,

    /// <summary>The stand's span cap is smaller than the aircraft needs.</summary>
    TooSmall,

    /// <summary>The stand's parking area is tagged only for other airlines, or the aircraft has no airline callsign.</summary>
    OtherAirline,

    /// <summary>Another aircraft holds the stand while this one would.</summary>
    Occupied,
}

/// <summary>Which stands a ranking covers: every parking stand of the layout, or one sidecar parking area's.</summary>
/// <param name="AreaName">The parking area as an author names it (case and spaces ignored), or null for every stand.</param>
public sealed record GateScope(string? AreaName)
{
    /// <summary>Gets the scope of every parking stand of the layout.</summary>
    public static GateScope Any { get; } = new((string?)null);

    /// <summary>The scope of one sidecar parking area's stands.</summary>
    /// <param name="name">The area as an author names it, e.g. <c>"Terminal 1"</c>.</param>
    /// <returns>The scope.</returns>
    public static GateScope Area(string name) => new(name);
}

/// <summary>
/// The time an aircraft holds a stand, scenario seconds: from its spawn, up to but not including the end, or to the
/// scenario's end when the end is null.
/// </summary>
/// <param name="StartSeconds">The spawn delay, seconds.</param>
/// <param name="EndSeconds">
/// The spawn delay plus the taxi time plus <see cref="GateRanking.StandClearBufferSeconds"/>, seconds; null holds the stand
/// to the scenario's end.
/// </param>
public readonly record struct GateWindow(int StartSeconds, int? EndSeconds)
{
    /// <summary>Whether the two windows share any second.</summary>
    /// <param name="other">The other window.</param>
    /// <returns>True when they overlap.</returns>
    public bool Overlaps(GateWindow other) =>
        (StartSeconds < (other.EndSeconds ?? long.MaxValue)) && (other.StartSeconds < (EndSeconds ?? long.MaxValue));
}

/// <summary>
/// Another scenario aircraft that starts at a stand, and the time it holds it: until its taxi ends plus the stand-clear
/// buffer (<see cref="GateRanking.StandClearBufferSeconds"/>).
/// </summary>
/// <param name="AircraftId">The other aircraft's callsign.</param>
/// <param name="Window">The time it holds the stand.</param>
public sealed record GateOccupant(string AircraftId, GateWindow Window);

/// <summary>One stand as ranked for one aircraft (<see cref="GateRanking.Rank"/>).</summary>
/// <param name="Stand">The stand's name, trimmed.</param>
/// <param name="Node">The stand's parking node.</param>
/// <param name="Area">
/// The sidecar parking area the stand lies in, as normalized, with a templated area's number filled in (<c>PLOT2</c>), or
/// null for none.
/// </param>
/// <param name="Reason">Where it ranks and why.</param>
/// <param name="MaxWingspanFt">The largest wingspan the stand takes, feet.</param>
/// <param name="Occupant">The aircraft holding it when <paramref name="Reason"/> is <see cref="GateReason.Occupied"/>, else null.</param>
public sealed record GateCandidate(string Stand, GroundNode Node, string? Area, GateReason Reason, double MaxWingspanFt, GateOccupant? Occupant)
{
    /// <summary>
    /// Gets a value indicating whether the stand fits and is free: <see cref="GateReason.Preferred"/> or
    /// <see cref="GateReason.Fits"/>.
    /// </summary>
    public bool IsFit => Reason is GateReason.Preferred or GateReason.Fits;
}
