using Avalonia.Media;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>CRC's STARS DCB palette (DcbButton.cs), with the factor every disabled or dimmed tile's colours are scaled by.</summary>
public static class DcbColors
{
    /// <summary>A tile's background while it is neither pushed nor latched.</summary>
    public static readonly Color Back = Color.FromRgb(0, 44, 0);

    /// <summary>A tile's background while it is pushed or latched.</summary>
    public static readonly Color Pushed = Color.FromRgb(0, 78, 0);

    /// <summary>A tile's label.</summary>
    public static readonly Color Text = Color.FromRgb(200, 200, 200);

    /// <summary>A tile's label while the pointer is over it.</summary>
    public static readonly Color HoverText = Color.FromRgb(255, 255, 153);

    /// <summary>The bevel line on a tile's top and left, and on its bottom and right while it is pushed.</summary>
    public static readonly Color Highlight = Color.FromRgb(85, 85, 85);

    /// <summary>The bevel line on a tile's bottom and right, and on its top and left while it is pushed.</summary>
    public static readonly Color Shadow = Color.FromRgb(0, 0, 0);

    /// <summary>The background of a weather tile whose data is available, unpushed.</summary>
    public static readonly Color WeatherAvailable = Color.FromRgb(50, 50, 100);

    /// <summary>The background of a weather tile whose data is available, pushed.</summary>
    public static readonly Color WeatherSelected = Color.FromRgb(70, 70, 100);

    /// <summary>The bar's background.</summary>
    public static readonly Color BarBackground = Color.FromRgb(0, 0, 0);

    /// <summary>The factor every colour of a disabled or dimmed tile is scaled by.</summary>
    public const double DimFactor = 0.45;

    /// <summary><paramref name="color"/> scaled by <see cref="DimFactor"/> on each of its channels, as CRC dims a disabled tile.</summary>
    public static Color Dim(Color color) =>
        Color.FromArgb(color.A, (byte)(color.R * DimFactor), (byte)(color.G * DimFactor), (byte)(color.B * DimFactor));
}
