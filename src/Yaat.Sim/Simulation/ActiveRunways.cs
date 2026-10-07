using System.Collections.Immutable;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Simulation;

/// <summary>How a room is using one active runway end, from the instructor's runway list.</summary>
public enum ActiveRunwayUse
{
    /// <summary>The end is used for both departures and arrivals (the bare designator, e.g. <c>30</c>).</summary>
    Both,

    /// <summary>Departures only (the <c>D</c> prefix, e.g. <c>D28L</c>).</summary>
    Departure,

    /// <summary>Arrivals only (the <c>A</c> prefix, e.g. <c>A28R</c>).</summary>
    Arrival,
}

/// <summary>
/// One active runway end and how the room is using it. The designator is the end's own form, normalized to the
/// zero-padded one the navigation database stores and matches (<c>1L</c> → <c>01L</c>), so two entries built from
/// different spellings of the same end are equal.
/// </summary>
public sealed record ActiveRunway
{
    public ActiveRunway(string designator, ActiveRunwayUse use)
    {
        Designator = RunwayIdentifier.NormalizeDesignator(designator.Trim().ToUpperInvariant());
        Use = use;
    }

    /// <summary>The runway end, e.g. <c>28L</c> (zero-padded: <c>01L</c>).</summary>
    public string Designator { get; }

    public ActiveRunwayUse Use { get; }

    /// <summary>
    /// The token form the command, the scenario sidecar and the snapshot carry: the bare designator for
    /// <see cref="ActiveRunwayUse.Both"/>, prefixed with <c>D</c>/<c>A</c> otherwise (<c>30</c>, <c>D28L</c>, <c>A28R</c>).
    /// </summary>
    public string ToToken() =>
        Use switch
        {
            ActiveRunwayUse.Departure => "D" + Designator,
            ActiveRunwayUse.Arrival => "A" + Designator,
            _ => Designator,
        };
}

/// <summary>
/// The active runways of every airport in a room: an ordered list of runway ends and their use per airport. Immutable
/// and value-equal — a change returns a new instance, so a snapshot or a reader on another thread holds one without it
/// moving underneath. Airports are keyed by the FAA id the navigation database uses, so <c>KOAK</c>, <c>oak</c> and
/// <c>OAK</c> all name one airport, and the keys are ordinal-sorted so every enumeration is in the same order.
/// </summary>
public sealed class ActiveRunways : IEquatable<ActiveRunways>
{
    /// <summary>A room that has named no active runways.</summary>
    public static ActiveRunways Empty { get; } = new(ImmutableSortedDictionary.Create<string, ImmutableArray<ActiveRunway>>(StringComparer.Ordinal));

    private readonly ImmutableSortedDictionary<string, ImmutableArray<ActiveRunway>> _byAirport;

    private ActiveRunways(ImmutableSortedDictionary<string, ImmutableArray<ActiveRunway>> byAirport) => _byAirport = byAirport;

    /// <summary>The airports that name at least one runway, ordinal-sorted.</summary>
    public IEnumerable<string> Airports => _byAirport.Keys;

    /// <summary>The airport's runways in the order they were listed, or an empty list when the airport names none.</summary>
    public IReadOnlyList<ActiveRunway> For(string airportId) =>
        _byAirport.TryGetValue(Normalize(airportId), out ImmutableArray<ActiveRunway> runways) ? runways : [];

    /// <summary>A new instance with <paramref name="airportId"/>'s list replaced; an empty list removes the airport.</summary>
    public ActiveRunways With(string airportId, IReadOnlyList<ActiveRunway> runways)
    {
        string key = Normalize(airportId);
        return new(runways.Count == 0 ? _byAirport.Remove(key) : _byAirport.SetItem(key, [.. runways]));
    }

    public bool Equals(ActiveRunways? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_byAirport.Count != other._byAirport.Count)
        {
            return false;
        }

        foreach ((string airport, ImmutableArray<ActiveRunway> runways) in _byAirport)
        {
            if (!other._byAirport.TryGetValue(airport, out ImmutableArray<ActiveRunway> otherRunways) || !runways.SequenceEqual(otherRunways))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ActiveRunways);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach ((string airport, ImmutableArray<ActiveRunway> runways) in _byAirport)
        {
            hash.Add(airport, StringComparer.Ordinal);
            foreach (ActiveRunway runway in runways)
            {
                hash.Add(runway);
            }
        }

        return hash.ToHashCode();
    }

    private static string Normalize(string airportId) => NavigationDatabase.NormalizeAirport(airportId);
}
