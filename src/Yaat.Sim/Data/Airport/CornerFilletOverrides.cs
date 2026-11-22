namespace Yaat.Sim.Data.Airport;

/// <summary>
/// One validated entry of an airport sidecar's <c>cornerFillets</c> section: an edit to the fillet of one intersection
/// corner, keyed by the intersection's pre-fillet apex position, the corner's two taxiway names and the true bearing of each
/// arm from the apex. <see cref="BearingA"/> and <see cref="TangentAFt"/> belong to <see cref="TaxiwayA"/>, and
/// <see cref="BearingB"/> and <see cref="TangentBFt"/> to <see cref="TaxiwayB"/>.
/// </summary>
/// <param name="ApexLat">The corner's pre-fillet apex latitude, degrees.</param>
/// <param name="ApexLon">The corner's pre-fillet apex longitude, degrees.</param>
/// <param name="TaxiwayA">The first arm's taxiway name, trimmed and upper-cased.</param>
/// <param name="TaxiwayB">The second arm's taxiway name, trimmed and upper-cased.</param>
/// <param name="BearingA">The first arm's true bearing from the apex, degrees in [0, 360).</param>
/// <param name="BearingB">The second arm's true bearing from the apex, degrees in [0, 360).</param>
/// <param name="Suppress">
/// True to remove the corner's fillet; it wins over a radius, force or tangents on the same entry, which the pass ignores
/// (<see cref="SuppressIgnoredEditsWarning"/>).
/// </param>
/// <param name="RadiusFt">The radius the corner's fillet asks for, feet, or null for the generator's own.</param>
/// <param name="Force">True to fillet the corner where the generator would skip it.</param>
/// <param name="TangentAFt">The first arm's pinned tangent distance from the apex, feet, or null when not pinned.</param>
/// <param name="TangentBFt">The second arm's pinned tangent distance from the apex, feet, or null when not pinned.</param>
public sealed record CornerFilletOverride(
    double ApexLat,
    double ApexLon,
    string TaxiwayA,
    string TaxiwayB,
    double BearingA,
    double BearingB,
    bool Suppress,
    double? RadiusFt,
    bool Force,
    double? TangentAFt,
    double? TangentBFt
)
{
    /// <summary>How far apart two apexes may be, in degrees of latitude and of longitude, and still name the same corner.</summary>
    public const double ApexToleranceDeg = 1e-7;

    /// <summary>How far apart two arm bearings may be, degrees, and still name the same arm.</summary>
    public const double BearingToleranceDeg = 1e-6;

    /// <summary>
    /// True when <paramref name="other"/> names the same corner: apexes within <see cref="ApexToleranceDeg"/>, the same
    /// taxiway pair in either order (case-insensitive), and each taxiway's bearing within <see cref="BearingToleranceDeg"/>
    /// of its counterpart's. The edits themselves are not compared.
    /// </summary>
    /// <param name="other">The entry to compare with.</param>
    /// <returns>Whether both entries key the same corner.</returns>
    public bool HasSameKey(CornerFilletOverride other) =>
        SameApex(other)
        && (
            SameArms(other.TaxiwayA, other.BearingA, other.TaxiwayB, other.BearingB)
            || SameArms(other.TaxiwayB, other.BearingB, other.TaxiwayA, other.BearingA)
        );

    /// <summary>
    /// The warning for a suppress that wins over the entry's other edits, naming each one it ignores, or null when the entry
    /// does not suppress or has no other edit.
    /// </summary>
    /// <returns>The warning, e.g. "suppress wins: radiusFt and tangentFt ignored".</returns>
    public string? SuppressIgnoredEditsWarning()
    {
        if (!Suppress)
        {
            return null;
        }

        List<string> ignored = [];
        if (RadiusFt is not null)
        {
            ignored.Add("radiusFt");
        }

        if (Force)
        {
            ignored.Add("force");
        }

        if ((TangentAFt is not null) || (TangentBFt is not null))
        {
            ignored.Add("tangentFt");
        }

        return (ignored.Count == 0) ? null : $"suppress wins: {JoinNames(ignored)} ignored";
    }

    /// <summary>This entry as the fillet pass applies it: a suppress drops the entry's radius, force and pinned tangents.</summary>
    /// <returns>The entry, its other edits cleared when it suppresses.</returns>
    public CornerFilletOverride AsApplied() => Suppress ? (this with { RadiusFt = null, Force = false, TangentAFt = null, TangentBFt = null }) : this;

    private static string JoinNames(List<string> names) =>
        (names.Count == 1) ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    private bool SameApex(CornerFilletOverride other) =>
        (Math.Abs(ApexLat - other.ApexLat) <= ApexToleranceDeg) && (Math.Abs(ApexLon - other.ApexLon) <= ApexToleranceDeg);

    /// <summary>True when arm A matches (<paramref name="taxiwayA"/>, <paramref name="bearingA"/>) and arm B the other.</summary>
    private bool SameArms(string taxiwayA, double bearingA, string taxiwayB, double bearingB) =>
        SameName(TaxiwayA, taxiwayA) && SameBearing(BearingA, bearingA) && SameName(TaxiwayB, taxiwayB) && SameBearing(BearingB, bearingB);

    private static bool SameName(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool SameBearing(double first, double second) =>
        Math.Abs(((((first - second) % 360.0) + 540.0) % 360.0) - 180.0) <= BearingToleranceDeg;
}

/// <summary>
/// An airport's per-corner fillet overrides (<see cref="AirportSidecarCatalog.GetCornerFillets"/>), carried on
/// <see cref="LayoutInputs.CornerFillets"/> to the fillet generator (<see cref="IFilletArcGenerator.Apply"/>). Immutable.
/// </summary>
public sealed class CornerFilletOverrides
{
    /// <summary>No overrides: every corner is filleted as the generator decides.</summary>
    public static CornerFilletOverrides None { get; } = new([]);

    /// <summary>Wraps a copy of <paramref name="entries"/>, in their order.</summary>
    /// <param name="entries">The airport's validated overrides, one per corner.</param>
    public CornerFilletOverrides(IReadOnlyList<CornerFilletOverride> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = [.. entries];
    }

    /// <summary>The overrides, in sidecar load order, one per corner key.</summary>
    public IReadOnlyList<CornerFilletOverride> Entries { get; }
}
