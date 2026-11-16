namespace Yaat.Client.Views.Scope.Profiles;

/// <summary>
/// The band values the user pinned by hand: each non-null value stands over the band's until AUTO clears every pin. Range
/// never pins, since it picks the band.
/// </summary>
/// <param name="OthersLevel">The pinned block level of others' tracks (<c>full</c> or <c>partial</c>), or null.</param>
/// <param name="VectorMin">The pinned vector length in minutes, or null.</param>
/// <param name="History">The pinned history dot count, or null.</param>
/// <param name="Rings">The pinned ring kind (<c>jring</c> or <c>dri</c>), or null.</param>
public sealed record ScopeBandPins(string? OthersLevel, double? VectorMin, int? History, string? Rings)
{
    /// <summary>No value pinned: every value follows the band.</summary>
    public static ScopeBandPins None { get; } = new(null, null, null, null);

    /// <summary>Whether any value is pinned; AUTO lights while one is.</summary>
    public bool Any => (OthersLevel is not null) || (VectorMin is not null) || (History is not null) || (Rings is not null);
}

/// <summary>Pure: which zoom band a range selects, and the values in effect once the pins are laid over it.</summary>
public static class ScopeZoomBands
{
    /// <summary>
    /// The band <paramref name="rangeNm"/> selects: the first whose <see cref="ScopeZoomBand.MaxNm"/> is null or at least the
    /// range (the edge is inclusive); the last band when none is.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bands"/> is empty.</exception>
    public static ScopeZoomBand Resolve(IReadOnlyList<ScopeZoomBand> bands, double rangeNm)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count == 0)
        {
            throw new ArgumentException("A profile has at least one zoom band.", nameof(bands));
        }

        foreach (ScopeZoomBand band in bands)
        {
            if ((band.MaxNm is null) || (band.MaxNm.Value >= rangeNm))
            {
                return band;
            }
        }

        return bands[^1];
    }

    /// <summary>The values in effect: each pinned value of <paramref name="pins"/>, else <paramref name="band"/>'s.</summary>
    public static ScopeZoomBand Effective(ScopeZoomBand band, ScopeBandPins pins)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentNullException.ThrowIfNull(pins);
        return band with
        {
            OthersLevel = pins.OthersLevel ?? band.OthersLevel,
            VectorMin = pins.VectorMin ?? band.VectorMin,
            History = pins.History ?? band.History,
            Rings = pins.Rings ?? band.Rings,
        };
    }
}
