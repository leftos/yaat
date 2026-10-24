using Yaat.Sim.Commands;

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

    /// <summary>
    /// The row may be spoken only when the command's altitude is from 10,000 to 17,999 ft, the only range where the busy
    /// group form (<c>{alt:group}</c>) says different words from the digit form.
    /// </summary>
    GroupFormRange = 2,

    /// <summary>
    /// The row ("slowing to {spd} knots") may be spoken only when the aircraft's current indicated airspeed is more than
    /// <see cref="PilotVariant.SpeedChangeThresholdKnots"/> above the assigned speed (AIM 4-4-12.c).
    /// </summary>
    SlowingOnly = 4,

    /// <summary>
    /// The row ("speeding up to {spd} knots") may be spoken only when the aircraft's current indicated airspeed is more than
    /// <see cref="PilotVariant.SpeedChangeThresholdKnots"/> below the assigned speed (AIM 4-4-12.c).
    /// </summary>
    SpeedingOnly = 8,

    /// <summary>
    /// The row ("maintaining {spd} knots") may be spoken only when the aircraft's current indicated airspeed is within
    /// <see cref="PilotVariant.SpeedChangeThresholdKnots"/> of the assigned speed.
    /// </summary>
    MaintainingOnly = 16,
}

/// <summary>
/// One spoken wording of a rule's readback: a template over the rule pattern's own <c>{capture}</c> placeholders and literal
/// words, so the variant speaks the same values as the canonical readback by construction.
/// </summary>
/// <param name="Template">Space-separated literal words and <c>{capture}</c> placeholders, e.g. <c>"left heading {hdg}"</c>.</param>
/// <param name="Tags">The conditions under which the row may be spoken.</param>
public sealed record PilotVariant(string Template, PilotVariantTags Tags)
{
    /// <summary>
    /// The capture modifier that speaks a value in its busy group form: <c>{alt:group}</c> is capture <c>alt</c>, rendered
    /// from the same value as the digit form.
    /// </summary>
    public const string GroupFormSuffix = ":group";

    /// <summary>
    /// The placeholder a row uses to speak the pilot's callsign inside the row ("…, {cs}, thanks"), at most once. The readback
    /// speaks the same callsign form there that it would otherwise close with; a row without it is closed by the callsign.
    /// </summary>
    public const string CallsignPlaceholder = "{cs}";

    /// <summary>
    /// How far, in knots, the current speed must be from the assigned speed for a readback to say it is slowing or speeding up
    /// (AIM 4-4-12.c); within it the readback says it is maintaining.
    /// </summary>
    public const int SpeedChangeThresholdKnots = 10;

    /// <summary>The tags that make a row depend on the aircraft's current speed against the assigned one.</summary>
    public const PilotVariantTags SpeedChangeTags = PilotVariantTags.SlowingOnly | PilotVariantTags.SpeedingOnly | PilotVariantTags.MaintainingOnly;

    /// <summary>Whether the row places the callsign itself (<see cref="CallsignPlaceholder"/>).</summary>
    public bool PlacesCallsign => Template.Contains(CallsignPlaceholder, StringComparison.Ordinal);

    /// <summary>
    /// Whether the row is "wilco" and nothing else but, at most, the callsign: an acknowledgement that stands only for a whole
    /// readback, never for one clause among others.
    /// </summary>
    public bool IsBareWilco
    {
        get
        {
            string[] words =
            [
                .. Template
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(word => word.Trim(',', '.').ToLowerInvariant())
                    .Where(word => (word.Length > 0) && (word != CallsignPlaceholder)),
            ];
            return words is ["wilco"];
        }
    }
}

/// <summary>
/// The spoken variants of one rule's readback, per frequency load band. The canonical pattern is always a Quiet-band and a
/// Normal-band candidate, in addition to the <see cref="Quiet"/> or <see cref="Normal"/> rows, every candidate equally likely.
/// Busy speaks only its listed rows, and speaks the canonical pattern when its list is empty or none of its rows can be spoken.
/// A rule with Quiet rows replaces the quiet frequency's "alright, " / ", thanks" flavour with them.
/// </summary>
/// <param name="Quiet">The rows a pilot may speak on a quiet frequency, besides the canonical pattern.</param>
/// <param name="Normal">The rows a pilot may speak at normal load, besides the canonical pattern.</param>
/// <param name="Busy">The rows a pilot may speak on a busy frequency.</param>
public sealed record PilotVariantSet(IReadOnlyList<PilotVariant> Quiet, IReadOnlyList<PilotVariant> Normal, IReadOnlyList<PilotVariant> Busy)
{
    /// <summary>A set whose only rows are busy-band rows: quiet and normal load speak the canonical pattern.</summary>
    /// <param name="busy">The busy-band rows.</param>
    /// <returns>The variant set.</returns>
    public static PilotVariantSet BusyOnly(params PilotVariant[] busy) => new([], [], busy);
}

/// <summary>
/// The spoken variants of one readback shape that a clause builder renders directly rather than from a rule (an expedite, an
/// altitude restriction, a cross-fix restriction). The builder speaks its own canonical text and picks among
/// <see cref="Variants"/> as the rule path does; <see cref="Pattern"/> is the canonical's template, which the aviation floor
/// compares every row with.
/// </summary>
/// <param name="Name">The shape's name, for test messages.</param>
/// <param name="Pattern">The canonical readback as a template over the shape's captures, e.g. <c>maintain at or above {alt}</c>.</param>
/// <param name="Type">The command type the builder reads back.</param>
/// <param name="Variants">The rows per frequency load band.</param>
public sealed record BuilderVariantSet(string Name, string[] Pattern, CanonicalCommandType Type, PilotVariantSet Variants);
