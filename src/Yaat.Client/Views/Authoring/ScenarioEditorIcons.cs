using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Authoring;

/// <summary>The Scenario Editor's icons (16 x 16, drawn as lines unless noted) and colours.</summary>
public static class ScenarioEditorIcons
{
    /// <summary>The amber of a modified badge and of warnings.</summary>
    public static readonly IBrush Amber = Brush("#E0A33A");

    /// <summary>The blue of a server-changed badge.</summary>
    public static readonly IBrush Blue = Brush("#5AA9E6");

    /// <summary>The red of a corrupt badge.</summary>
    public static readonly IBrush Red = Brush("#E06464");

    /// <summary>The green of a local-only badge.</summary>
    public static readonly IBrush Green = Brush("#63B97D");

    /// <summary>The grey of a gone-from-server badge.</summary>
    public static readonly IBrush Grey = Brush("#9A9A9A");

    /// <summary>The tint of a corrupt row.</summary>
    public static readonly IBrush CorruptTint = Brush("#3A2222");

    /// <summary>No background.</summary>
    public static readonly IBrush None = Brushes.Transparent;

    /// <summary>A row name's colour.</summary>
    public static readonly IBrush Text = Brush("#E6E6E6");

    /// <summary>A dimmed row name's colour.</summary>
    public static readonly IBrush Dim = Brush("#8A8A8A");

    /// <summary>A filled dot: modified.</summary>
    public static readonly Geometry Dot = Geometry.Parse("M4,8 A4,4 0 1 1 12,8 A4,4 0 1 1 4,8 Z");

    /// <summary>A cloud with a down arrow: server changed.</summary>
    public static readonly Geometry CloudArrow = Geometry.Parse(Cloud + " M8,7 V12 M6,10 L8,12 L10,10");

    /// <summary>A crossed-out cloud: gone from server.</summary>
    public static readonly Geometry CloudCrossed = Geometry.Parse(Cloud + " M2.5,2.5 L13.5,13.5");

    /// <summary>A warning triangle: corrupt, notices and the offline status.</summary>
    public static readonly Geometry Warning = Geometry.Parse("M8,2 L14.5,13.5 L1.5,13.5 Z M8,6.5 V10 M8,11.3 V12.3");

    /// <summary>A laptop: local only.</summary>
    public static readonly Geometry Laptop = Geometry.Parse(
        "M4,3.5 H12 A1,1 0 0 1 13,4.5 V9.5 A1,1 0 0 1 12,10.5 H4 A1,1 0 0 1 3,9.5 V4.5 A1,1 0 0 1 4,3.5 Z M1.5,12.5 H14.5"
    );

    /// <summary>A circular arrow: Refresh.</summary>
    public static readonly Geometry Refresh = Geometry.Parse("M13,8 A5,5 0 1 1 11.5,4.4 M12,1.8 V5 H8.8");

    private const string Cloud = "M4.5,11.5 H11.5 A2.8,2.8 0 0 0 11.8,5.9 A4,4 0 0 0 4.2,6.6 A2.5,2.5 0 0 0 4.5,11.5 Z";

    private static ImmutableSolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
