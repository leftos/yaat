using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The ERAM FDB line 4 (CRC's Field F, <c>FdbRenderObject.GetFieldF</c>): the HSF free text, heading and speed when set,
/// else the aircraft type or the destination the display flags allow. Pure: it reads only its arguments.
/// </summary>
internal static class EramLine4
{
    // CRC: a speed without a heading sits in the speed column, after the heading's four characters and the space.
    private const string SpeedColumn = "     ";

    /// <summary>Whether any HSF annotation (heading, speed or free text) is set: the line-3 HSF indicator shows.</summary>
    internal static bool HasHsf(AircraftModel ac) =>
        !string.IsNullOrEmpty(ac.EramAssignedHeading) || !string.IsNullOrEmpty(ac.EramAssignedSpeed) || !string.IsNullOrEmpty(ac.EramFreeText);

    /// <summary>The line-4 text, in CRC <c>GetFieldF</c> order; empty when nothing applies.</summary>
    /// <param name="ac">The track.</param>
    /// <param name="showType">The type/suffix fallback is shown.</param>
    /// <param name="showDestination">The destination fallback (four characters or fewer) is shown.</param>
    internal static string Text(AircraftModel ac, bool showType, bool showDestination) => Hsf(ac) ?? Fallback(ac, showType, showDestination);

    private static string? Hsf(AircraftModel ac)
    {
        if (!string.IsNullOrEmpty(ac.EramFreeText))
        {
            return ac.EramFreeText;
        }
        string? heading = string.IsNullOrEmpty(ac.EramAssignedHeading) ? null : ac.EramAssignedHeading;
        string? speed = string.IsNullOrEmpty(ac.EramAssignedSpeed) ? null : ac.EramAssignedSpeed;
        return (heading, speed) switch
        {
            ({ } h, { } s) => $"{h, -4} {s}",
            ({ } h, null) => h,
            (null, { } s) => SpeedColumn + s,
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
