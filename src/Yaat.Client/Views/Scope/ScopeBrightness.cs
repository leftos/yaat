using SkiaSharp;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope's BRITE groups, as CRC's ERAM brightness works (<c>RenderUtils.ApplyColorBrightness</c>,
/// <c>Style.CalculateBrightness</c>): each percent group scales the colour channels of the layer it names; LINE 4 and DWELL
/// are modifiers. Display state: kept in pref sets and the saved state, never sent to the server.
/// </summary>
/// <param name="MapA">The A-category video maps, 0 to 100.</param>
/// <param name="MapB">The B-category video maps, 0 to 100.</param>
/// <param name="Targets">The position symbols, 0 to 100.</param>
/// <param name="Fdb">The datablocks: their leaders, dwell boxes and every field that is not an alert, 0 to 100.</param>
/// <param name="Line4">The modifier added to <paramref name="Fdb"/> for the ERAM line 4, -20 to 0.</param>
/// <param name="Alerts">The conflict, no-landing-clearance and ATPA tokens, 0 to 100.</param>
/// <param name="Dwell">The modifier that lifts everything drawn for a dwelled track toward 100, 0 to 20.</param>
/// <param name="Hist">The history dots, 0 to 100; DWELL never lifts them.</param>
/// <param name="Vector">The vector lines, 0 to 100; DWELL never lifts them.</param>
/// <param name="Rings">The Rings tool's J-rings, cones and DRI halos and the ATPA cones, 0 to 100; DWELL never lifts them.</param>
/// <param name="Lens">The pilot lens's overlays (the intent path), 0 to 100; DWELL never lifts them.</param>
public sealed record ScopeBrightness(
    int MapA,
    int MapB,
    int Targets,
    int Fdb,
    int Line4,
    int Alerts,
    int Dwell,
    int Hist,
    int Vector,
    int Rings,
    int Lens
)
{
    /// <summary>The DWELL value at which a dwelled track draws at full brightness (CRC's modifier maximum).</summary>
    private const double FullDwell = 20.0;

    /// <summary>The values a Scope opens with; they draw today's pixels.</summary>
    public static ScopeBrightness Default { get; } = new(100, 60, 100, 100, 0, 100, 20, 100, 100, 100, 100);

    /// <summary>The groups the <c>.scope brite</c> command and the BRITE spinners set, in spinner order.</summary>
    public static IReadOnlyList<ScopeBrightnessGroup> Groups { get; } =
    [
        new("mapa", 0, 100, 5, b => b.MapA, (b, v) => b with { MapA = v }),
        new("mapb", 0, 100, 5, b => b.MapB, (b, v) => b with { MapB = v }),
        new("targets", 0, 100, 5, b => b.Targets, (b, v) => b with { Targets = v }),
        new("fdb", 0, 100, 5, b => b.Fdb, (b, v) => b with { Fdb = v }),
        new("line4", -20, 0, 1, b => b.Line4, (b, v) => b with { Line4 = v }),
        new("alerts", 0, 100, 5, b => b.Alerts, (b, v) => b with { Alerts = v }),
        new("dwell", 0, 20, 1, b => b.Dwell, (b, v) => b with { Dwell = v }),
        new("hist", 0, 100, 5, b => b.Hist, (b, v) => b with { Hist = v }),
        new("vector", 0, 100, 5, b => b.Vector, (b, v) => b with { Vector = v }),
        new("rings", 0, 100, 5, b => b.Rings, (b, v) => b with { Rings = v }),
        new("lens", 0, 100, 5, b => b.Lens, (b, v) => b with { Lens = v }),
    ];

    /// <summary>The group named <paramref name="name"/>, case-insensitive, or null.</summary>
    public static ScopeBrightnessGroup? FindGroup(string name) =>
        Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="color"/> with each channel scaled by <paramref name="percent"/>/100 (clamped to 0 to 100), truncated,
    /// and its alpha kept.
    /// </summary>
    internal static SKColor Apply(SKColor color, int percent)
    {
        int p = Math.Clamp(percent, 0, 100);
        return new SKColor((byte)(color.Red * p / 100), (byte)(color.Green * p / 100), (byte)(color.Blue * p / 100), color.Alpha);
    }

    /// <summary><paramref name="percent"/> lifted toward 100 by the DWELL modifier <paramref name="dwell"/>, as CRC does.</summary>
    internal static int Lift(int percent, int dwell) => percent + (int)Math.Round(dwell / FullDwell * (100 - percent));
}

/// <summary>One BRITE group: its command name, range and spinner step, and how it reads and writes its value.</summary>
/// <param name="Name">The lowercase group name <c>.scope brite</c> takes.</param>
/// <param name="Min">The lowest value.</param>
/// <param name="Max">The highest value.</param>
/// <param name="Step">How far one wheel step moves the value.</param>
/// <param name="Get">Reads the group's value.</param>
/// <param name="With">A copy with the group's value replaced.</param>
public sealed record ScopeBrightnessGroup(
    string Name,
    int Min,
    int Max,
    int Step,
    Func<ScopeBrightness, int> Get,
    Func<ScopeBrightness, int, ScopeBrightness> With
)
{
    /// <summary>Whether <paramref name="value"/> lies in the group's range.</summary>
    public bool Contains(int value) => (value >= Min) && (value <= Max);
}
