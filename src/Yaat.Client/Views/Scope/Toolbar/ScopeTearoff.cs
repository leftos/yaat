using Avalonia;
using Avalonia.Layout;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>The corner of the scope a floating view card anchors to.</summary>
public enum ScopeCorner
{
    /// <summary>The top-left corner, <c>tl</c>.</summary>
    TopLeft,

    /// <summary>The top-right corner, <c>tr</c>.</summary>
    TopRight,

    /// <summary>The bottom-left corner, <c>bl</c>.</summary>
    BottomLeft,

    /// <summary>The bottom-right corner, <c>br</c>.</summary>
    BottomRight,
}

/// <summary>
/// Where a floating view card sits: the corner of the scope it anchors to, and the whole-pixel distances from that corner
/// to the card's nearest edges. Layout keeps the distances when the scope resizes.
/// </summary>
/// <param name="Corner">The corner the card anchors to.</param>
/// <param name="Dx">The distance from the corner's side edge to the card's nearest side, in pixels, 0 or more.</param>
/// <param name="Dy">The distance from the corner's top or bottom edge to the card's nearest edge, in pixels, 0 or more.</param>
public sealed record ScopePlacement(ScopeCorner Corner, int Dx, int Dy)
{
    /// <summary>Whether the corner is on the left side.</summary>
    public bool IsLeft => Corner is ScopeCorner.TopLeft or ScopeCorner.BottomLeft;

    /// <summary>Whether the corner is on the top side.</summary>
    public bool IsTop => Corner is ScopeCorner.TopLeft or ScopeCorner.TopRight;

    /// <summary>The horizontal alignment that puts a card on the corner's side.</summary>
    public HorizontalAlignment HorizontalAlignment => IsLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;

    /// <summary>The vertical alignment that puts a card on the corner's side.</summary>
    public VerticalAlignment VerticalAlignment => IsTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

    /// <summary>The margin that holds a card at the distances, on the corner's two sides.</summary>
    public Thickness ToMargin() => new(IsLeft ? Dx : 0, IsTop ? Dy : 0, IsLeft ? 0 : Dx, IsTop ? 0 : Dy);

    /// <summary>
    /// The placement of a card whose bounds are <paramref name="rect"/> in a scope of <paramref name="area"/>: the
    /// corner of the quadrant the rect's centre is in (a centre on a midline goes left or top), with the distances rounded
    /// to whole pixels and clamped to 0 or more.
    /// </summary>
    public static ScopePlacement Nearest(Rect rect, Size area)
    {
        bool left = rect.Center.X <= (area.Width / 2);
        bool top = rect.Center.Y <= (area.Height / 2);
        double dx = left ? rect.Left : (area.Width - rect.Right);
        double dy = top ? rect.Top : (area.Height - rect.Bottom);
        ScopeCorner corner = (left, top) switch
        {
            (true, true) => ScopeCorner.TopLeft,
            (false, true) => ScopeCorner.TopRight,
            (true, false) => ScopeCorner.BottomLeft,
            _ => ScopeCorner.BottomRight,
        };
        return new ScopePlacement(corner, Whole(dx), Whole(dy));
    }

    /// <summary>The text name of <paramref name="corner"/>: <c>tl</c>, <c>tr</c>, <c>bl</c> or <c>br</c>.</summary>
    public static string NameOf(ScopeCorner corner) =>
        corner switch
        {
            ScopeCorner.TopLeft => "tl",
            ScopeCorner.TopRight => "tr",
            ScopeCorner.BottomLeft => "bl",
            _ => "br",
        };

    /// <summary>Parses a corner's text name (<see cref="NameOf"/>), case-insensitive.</summary>
    /// <returns>False when <paramref name="name"/> names no corner.</returns>
    public static bool TryParseCorner(string name, out ScopeCorner corner)
    {
        foreach (ScopeCorner candidate in Enum.GetValues<ScopeCorner>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                corner = candidate;
                return true;
            }
        }

        corner = default;
        return false;
    }

    private static int Whole(double distance) => Math.Max(0, (int)Math.Round(distance, MidpointRounding.AwayFromZero));
}
