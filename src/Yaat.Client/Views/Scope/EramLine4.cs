using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The ERAM FDB line 4 (CRC's Field F, <c>FdbRenderObject.GetFieldF</c>): the HSF free text, heading and speed when set,
/// unless the viewer's sector suppressed the fourth line (CRC <c>SuppressFourthLine</c>); else the aircraft type or the
/// destination the display flags allow. Pure: it reads only its arguments.
/// </summary>
internal static class EramLine4
{
    // CRC: a speed without a heading sits in the speed column, after the heading's four blank characters and the space.
    private const string HeadingColumn = "    ";

    /// <summary>Whether any HSF annotation (heading, speed or free text) is set: the line-3 HSF indicator shows.</summary>
    internal static bool HasHsf(AircraftModel ac) =>
        !string.IsNullOrEmpty(ac.EramAssignedHeading) || !string.IsNullOrEmpty(ac.EramAssignedSpeed) || !string.IsNullOrEmpty(ac.EramFreeText);

    /// <summary>The line-4 text, in CRC <c>GetFieldF</c> order; empty when nothing applies.</summary>
    /// <param name="ac">The track.</param>
    /// <param name="showType">The type/suffix fallback is shown.</param>
    /// <param name="showDestination">The destination fallback (four characters or fewer) is shown.</param>
    /// <param name="hsfSuppressed">The viewer's sector suppressed the fourth line (CRC <c>SuppressFourthLine</c>): the HSF
    /// branches are skipped, so the fallback shows.</param>
    internal static string Text(AircraftModel ac, bool showType, bool showDestination, bool hsfSuppressed) =>
        Parts(ac, showType, showDestination, hsfSuppressed).Text;

    /// <summary>
    /// The line-4 tokens <see cref="Text"/> joins: exactly one of the free text, the heading and/or speed (with the pad before a
    /// speed shown alone), or the fallback holds text; every other part is empty.
    /// </summary>
    /// <param name="ac">The track.</param>
    /// <param name="showType">The type/suffix fallback is shown.</param>
    /// <param name="showDestination">The destination fallback (four characters or fewer) is shown.</param>
    /// <param name="hsfSuppressed">The viewer's sector suppressed the fourth line: the fallback shows.</param>
    internal static Line4Parts Parts(AircraftModel ac, bool showType, bool showDestination, bool hsfSuppressed) =>
        (hsfSuppressed ? null : Hsf(ac)) ?? Line4Parts.Empty with { Fallback = Fallback(ac, showType, showDestination) };

    private static Line4Parts? Hsf(AircraftModel ac)
    {
        if (!string.IsNullOrEmpty(ac.EramFreeText))
        {
            return Line4Parts.Empty with { FreeText = ac.EramFreeText };
        }
        string? heading = string.IsNullOrEmpty(ac.EramAssignedHeading) ? null : ac.EramAssignedHeading;
        string? speed = string.IsNullOrEmpty(ac.EramAssignedSpeed) ? null : ac.EramAssignedSpeed;
        return (heading, speed) switch
        {
            ({ } h, { } s) => Line4Parts.Empty with { Heading = $"{h, -4}", Speed = s },
            ({ } h, null) => Line4Parts.Empty with { Heading = h },
            (null, { } s) => Line4Parts.Empty with { Pad = HeadingColumn, Speed = s },
            _ => null,
        };
    }

    private static string Fallback(AircraftModel ac, bool showType, bool showDestination)
    {
        // The type as filed may carry a weight-class prefix and an equipment suffix (H/A306/L); line 4 shows the bare type.
        string type = AircraftState.StripTypePrefix(ac.DisplayAircraftType).Trim();
        if (showType && !string.IsNullOrEmpty(type))
        {
            return string.IsNullOrEmpty(ac.EquipmentSuffix) ? type : $"{type}/{ac.EquipmentSuffix}";
        }
        return (showDestination && (ac.Destination is { Length: >= 1 and <= 4 })) ? ac.Destination : "";
    }
}

/// <summary>
/// The ERAM line 4 as its tokens, each its own click region: the free text; the heading; the blank heading column before a
/// speed shown alone; the speed, one space after the heading or the pad; and the type/destination fallback. Empty is absent.
/// </summary>
internal sealed record Line4Parts(string FreeText, string Heading, string Pad, string Speed, string Fallback)
{
    /// <summary>No token.</summary>
    public static readonly Line4Parts Empty = new("", "", "", "", "");

    /// <summary>The line as drawn: the speed a space after the heading or the pad.</summary>
    public string Text =>
        string.Concat(FreeText, Heading, Pad, ((Speed.Length > 0) && ((Heading.Length > 0) || (Pad.Length > 0))) ? " " : "", Speed, Fallback);
}
