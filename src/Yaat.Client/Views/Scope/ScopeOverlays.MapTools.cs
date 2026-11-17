using SkiaSharp;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>The map tools' part of the overlays: the range ring, the weather lines and the Ctrl-hover MVA label.</summary>
public sealed partial record ScopeOverlays
{
    /// <summary>The MVA label under the pointer while Control is held over an MVA sector, or null.</summary>
    public ScopeMvaHover? MvaHover { get; init; }

    /// <summary>The range ring, drawn under the tracks; null while it is off or has no centre.</summary>
    public ScopeRangeRing? RangeRing { get; init; }

    /// <summary>The weather lines, drawn over everything at the bottom left; null while the overlay is off or has no line.</summary>
    public ScopeWeatherText? Weather { get; init; }
}

/// <summary>The Ctrl-hover MVA label.</summary>
/// <param name="Text">The readout (<see cref="ScopeMvaReadout.TextAt"/>).</param>
/// <param name="At">The pointer, in canvas pixels; the label sits below and right of it.</param>
public readonly record struct ScopeMvaHover(string Text, SKPoint At);

/// <summary>The range ring: concentric circles <paramref name="SizeNm"/> apart around <paramref name="Center"/>.</summary>
/// <param name="Center">The ring's centre.</param>
/// <param name="SizeNm">The spacing of its circles, in NM.</param>
public readonly record struct ScopeRangeRing(LatLon Center, double SizeNm);

/// <summary>The weather overlay: one line per METAR, or the note that stands in for them.</summary>
/// <param name="Lines">The lines, top to bottom; the last sits lowest.</param>
/// <param name="BottomInset">How far above the canvas's bottom edge the lowest line ends, in pixels.</param>
public sealed record ScopeWeatherText(IReadOnlyList<string> Lines, float BottomInset);
