namespace Yaat.Sim.Speech;

/// <summary>Conditions under which a <see cref="PilotVariant"/> may be spoken.</summary>
[Flags]
public enum PilotVariantTags
{
    /// <summary>The row may be spoken for any value of its captures.</summary>
    None = 0,

    /// <summary>
    /// The row may be spoken only when the command's altitude is a flight level: at least 18000 ft and a multiple of 100.
    /// Readbacks never put "to" before an altitude below FL180.
    /// </summary>
    FlightLevelOnly = 1,
}

/// <summary>
/// One spoken wording of a rule's readback: a template over the rule pattern's own <c>{capture}</c> placeholders and literal
/// words, so the variant speaks the same values as the canonical readback by construction.
/// </summary>
/// <param name="Template">Space-separated literal words and <c>{capture}</c> placeholders, e.g. <c>"left heading {hdg}"</c>.</param>
/// <param name="Tags">The conditions under which the row may be spoken.</param>
public sealed record PilotVariant(string Template, PilotVariantTags Tags);

/// <summary>
/// The spoken variants of one rule's readback, per frequency load band. The canonical pattern is always a Normal-band candidate
/// in addition to the <see cref="Normal"/> rows. Quiet and Busy speak only their listed rows, and speak the canonical pattern
/// when their list is empty or none of its rows can be spoken.
/// </summary>
/// <param name="Quiet">The rows a pilot may speak on a quiet frequency.</param>
/// <param name="Normal">The rows a pilot may speak at normal load, besides the canonical pattern.</param>
/// <param name="Busy">The rows a pilot may speak on a busy frequency.</param>
public sealed record PilotVariantSet(IReadOnlyList<PilotVariant> Quiet, IReadOnlyList<PilotVariant> Normal, IReadOnlyList<PilotVariant> Busy)
{
    /// <summary>A set whose only rows are busy-band rows: quiet and normal load speak the canonical pattern.</summary>
    /// <param name="busy">The busy-band rows.</param>
    /// <returns>The variant set.</returns>
    public static PilotVariantSet BusyOnly(params PilotVariant[] busy) => new([], [], busy);
}
