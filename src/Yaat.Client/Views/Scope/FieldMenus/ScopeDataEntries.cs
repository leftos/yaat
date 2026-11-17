using System.Globalization;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// The ERAM scope-data entries a datablock field click produces, as the text typed in the entry would be: the flight ID
/// last, altitudes in hundreds of feet as three zero-padded digits (QQ.yaml field 76 <c>ddd</c>/<c>Lddd</c>/<c>Pddd</c>/
/// <c>Rddd</c>, QR.yaml field 54 <c>ddd</c>, and QZ's single-altitude form), the server's ERAM parsers deciding the rest.
/// </summary>
internal static class ScopeDataEntries
{
    /// <summary>Amends the flight-plan (assigned) altitude: <c>QZ ddd FLID</c>.</summary>
    public static string FpAltitude(int hundreds, string flid) => $"QZ {Altitude(hundreds)} {flid}";

    /// <summary>Sets the interim altitude: <c>QQ ddd FLID</c>.</summary>
    public static string Interim(int hundreds, string flid) => $"QQ {Altitude(hundreds)} {flid}";

    /// <summary>Sets the local interim altitude: <c>QQ Lddd FLID</c>.</summary>
    public static string Local(int hundreds, string flid) => $"QQ L{Altitude(hundreds)} {flid}";

    /// <summary>Sets the procedure altitude: <c>QQ Pddd FLID</c>.</summary>
    public static string Procedure(int hundreds, string flid) => $"QQ P{Altitude(hundreds)} {flid}";

    /// <summary>Sets the interim altitude and the controller-entered reported altitude: <c>QQ Rddd FLID</c>.</summary>
    public static string InterimWithCera(int hundreds, string flid) => $"QQ R{Altitude(hundreds)} {flid}";

    /// <summary>Sets only the controller-entered reported altitude: <c>QR ddd FLID</c>.</summary>
    public static string CeraOnly(int hundreds, string flid) => $"QR {Altitude(hundreds)} {flid}";

    /// <summary>Clears the interim or procedure altitude: <c>QQ FLID</c>.</summary>
    public static string ClearInterimOrProcedure(string flid) => $"QQ {flid}";

    /// <summary>Clears the local interim altitude: <c>QQ L FLID</c>.</summary>
    public static string ClearLocal(string flid) => $"QQ L {flid}";

    /// <summary>Sets the HSF heading: <c>QS heading FLID</c>.</summary>
    public static string HsfHeading(string heading, string flid) => $"QS {heading} {flid}";

    /// <summary>Sets the HSF speed: <c>QS /speed FLID</c>.</summary>
    public static string HsfSpeed(string speed, string flid) => $"QS /{speed} {flid}";

    /// <summary>Deletes the HSF heading: <c>QS */ FLID</c>.</summary>
    public static string HsfClearHeading(string flid) => $"QS */ {flid}";

    /// <summary>Deletes the HSF speed: <c>QS /* FLID</c>.</summary>
    public static string HsfClearSpeed(string flid) => $"QS /* {flid}";

    /// <summary>Deletes the HSF heading, speed and free text: <c>QS * FLID</c>.</summary>
    public static string HsfClearAll(string flid) => $"QS * {flid}";

    /// <summary>
    /// Sets the HSF free text: <c>QS `text FLID</c>, the backtick as typed, which <see cref="Entries.ScopeEramKeys.MapTyped"/>
    /// sends as ERAM's clear-weather symbol.
    /// </summary>
    public static string HsfFreeText(string text, string flid) => $"QS `{text} {flid}";

    /// <summary>Toggles the track's HSF display: <c>QS FLID</c>.</summary>
    public static string ToggleHsf(string flid) => $"QS {flid}";

    /// <summary>
    /// Toggles the track's VCI: <c>QN // FLID</c>, as CRC's on-frequency indicator pick sends it, the FLID a token of its own so
    /// a clicked track goes as a pick.
    /// </summary>
    public static string ToggleVci(string flid) => $"QN // {flid}";

    /// <summary>The entry a direct-action field runs: <see cref="ToggleHsf"/> or <see cref="ToggleVci"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is neither toggle.</exception>
    public static string Toggle(ScopeFieldAction action, string flid) =>
        action switch
        {
            ScopeFieldAction.ToggleHsf => ToggleHsf(flid),
            ScopeFieldAction.ToggleVci => ToggleVci(flid),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a direct-action toggle."),
        };

    // Three digits, 001-999 hundreds of feet: QQ refuses 000 and no altitude field takes four digits.
    private static string Altitude(int hundreds) =>
        hundreds is >= 1 and <= 999
            ? hundreds.ToString("D3", CultureInfo.InvariantCulture)
            : throw new ArgumentOutOfRangeException(nameof(hundreds), hundreds, "An altitude is 1-999 hundreds of feet.");
}
