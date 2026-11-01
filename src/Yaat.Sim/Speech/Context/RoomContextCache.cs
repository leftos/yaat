using System.Runtime.CompilerServices;
using Yaat.Sim.Data;

namespace Yaat.Sim.Speech.Context;

/// <summary>
/// Holds each airport's entities, built once per airport, approach flag and ground-layout instance rather than once per
/// transmission. Safe to call from several threads.
/// </summary>
public sealed class RoomContextCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AirportContextKey, IReadOnlyList<ContextEntity>> _entries = new(new KeyComparer());

    /// <summary>
    /// The airport's entities (<see cref="RoomContextFactory.AirportEntities"/> with the airport's own landmarks,
    /// <see cref="RoomContextFactory.Landmarks"/>), built on the first call for the airport, approach flag and layout
    /// instance and returned as the same list afterwards.
    /// </summary>
    /// <param name="key">The airport, whether the position is approach, and the airport's ground layout (compared by reference).</param>
    /// <param name="nav">The navigation database.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>The airport entities.</returns>
    /// <exception cref="InvalidOperationException">The navigation database has no reference point for the airport.</exception>
    public IReadOnlyList<ContextEntity> AirportEntities(AirportContextKey key, NavigationDatabase nav, DecoderSettings settings)
    {
        AirportContextKey normalized = key with { AirportIcao = key.AirportIcao.Trim().ToUpperInvariant() };
        lock (_gate)
        {
            if (_entries.TryGetValue(normalized, out IReadOnlyList<ContextEntity>? cached))
            {
                return cached;
            }

            IReadOnlyList<ContextEntity> built = RoomContextFactory.AirportEntities(
                key,
                RoomContextFactory.Landmarks(key.AirportIcao, nav),
                nav,
                settings
            );
            _entries[normalized] = built;
            return built;
        }
    }

    /// <summary>Compares the airport and approach flag by value and the layout by reference.</summary>
    private sealed class KeyComparer : IEqualityComparer<AirportContextKey>
    {
        public bool Equals(AirportContextKey? x, AirportContextKey? y) =>
            ReferenceEquals(x, y)
            || (
                (x is not null)
                && (y is not null)
                && string.Equals(x.AirportIcao, y.AirportIcao, StringComparison.Ordinal)
                && (x.ApproachPosition == y.ApproachPosition)
                && ReferenceEquals(x.Layout, y.Layout)
            );

        public int GetHashCode(AirportContextKey obj) =>
            HashCode.Combine(obj.AirportIcao, obj.ApproachPosition, RuntimeHelpers.GetHashCode(obj.Layout));
    }
}
