using SkiaSharp;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Views.Map;

/// <summary>
/// The target and datablock colours the Radar, the Scope and the Ground view share, with the beacon-mismatch line
/// they all draw the same way.
/// </summary>
internal static class TargetColors
{
    // STARS unowned datablock colour mirrored from CRC (DisplayElementTracks): LimeGreen.
    internal static readonly SKColor StarsUnownedColor = new(50, 205, 50);

    // MVA datablock altitude tint: red when below the charted sector floor.
    internal static readonly SKColor MvaBelowColor = new(255, 90, 90);

    // Filed-vs-actual aircraft type hint: a deeper amber than the MVA "at" tint, which sits on the
    // altitude token at the other end of the same line.
    internal static readonly SKColor TypeMismatchColor = new(0xFF, 0xB0, 0x30);

    // STARS TPA graphics color (CRC's TCW value, DisplayElementTracks: Color.FromArgb(90, 180, 255)) —
    // used for the instructor J-Ring / Cone overlay so it reads like the TPA graphics a controller sees.
    internal static readonly SKColor TpaColor = new(90, 180, 255);

    // Automatic ATPA cone / in-trail line, escalating off the TPA blue: yellow once the pair is inside
    // the warning band, red-orange once it is in alert — CRC's STARS ATPA cone palette.
    private static readonly SKColor AtpaWarningColor = SKColors.Yellow;
    private static readonly SKColor AtpaAlertColor = new(255, 55, 0);

    /// <summary>Alpha applied to a coasting live-traffic shadow's symbol and datablock (feed sweeps missed, position dead-reckoned).</summary>
    internal const byte StaleAlpha = 128;

    /// <summary>
    /// The colour of an ATPA cone and its in-trail distance line for a pairing's advisory state:
    /// Monitor keeps the STARS TPA blue, Warning goes yellow, Alert goes red-orange (CRC's STARS ATPA
    /// palette). A Monitor datablock line uses the block's own colour instead — see
    /// <c>TargetRenderer.DrawLeaderAndDataBlock</c>.
    /// </summary>
    internal static SKColor AtpaConeColorFor(AtpaConeState state) =>
        state switch
        {
            AtpaConeState.Warning => AtpaWarningColor,
            AtpaConeState.Alert => AtpaAlertColor,
            _ => TpaColor,
        };

    /// <summary>
    /// Draws the beacon-code mismatch line <c>"{reported} {assigned}"</c>: the reported code solid in the
    /// block color, then the assigned code dim-pulsing to its right on the 500 ms off-phase — emulating
    /// CRC STARS (reported solid, assigned blinking via <c>ApplyColorBrightness(color, 25)</c>). Shared
    /// with the ground datablock so both views give the identical indication; <paramref name="paint"/>
    /// is left in <paramref name="color"/> on return.
    /// </summary>
    internal static void DrawSquawkMismatchLine(SKCanvas canvas, string line, float x, float baseline, SKFont font, SKPaint paint, SKColor color)
    {
        int space = line.IndexOf(' ');
        string reported = space < 0 ? line : line[..space];
        paint.Color = color;
        canvas.DrawText(reported, x, baseline, SKTextAlign.Left, font, paint);
        if (space < 0)
        {
            return;
        }

        float reportedWidth = font.MeasureText(reported);
        bool blinkOff = !MapBlink.IsOn;
        paint.Color = blinkOff ? DimColor(color, 0.25f) : color;
        canvas.DrawText(line[space..], x + reportedWidth, baseline, SKTextAlign.Left, font, paint);
        paint.Color = color;
    }

    /// <summary>Scales a color's RGB toward black by <paramref name="factor"/> (alpha preserved), matching
    /// CRC's <c>RenderUtils.ApplyColorBrightness</c> used for the dim-pulse of a blinking datablock token.</summary>
    private static SKColor DimColor(SKColor color, float factor) =>
        new((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor), color.Alpha);
}
