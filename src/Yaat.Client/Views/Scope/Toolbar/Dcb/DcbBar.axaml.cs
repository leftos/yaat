using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// A STARS DCB bar: a black band of <see cref="DcbTile"/>s laid out left to right by <see cref="DcbLayout"/>, in the order
/// they were set, the band as wide as its widest column ends; a group of them may sit together at the bar's right edge instead
/// when the bar is wider than that. A bar narrower than its tiles scrolls them sideways, with no scrollbar: the wheel over it
/// moves them by <see cref="WheelStep"/> a notch, as CRC's DCB does, and never reaches the Scope behind it.
/// </summary>
public partial class DcbBar : UserControl
{
    private readonly List<DcbTile> _tiles = [];
    private IReadOnlyList<Rect> _flow = [];
    private double _flowWidth;
    private IReadOnlyList<DcbTile> _rightGroup = [];

    /// <summary>How far one wheel notch scrolls the tiles of a bar narrower than them, in DIP (CRC's DCB step).</summary>
    public const double WheelStep = 25;

    /// <summary>A bar holding no tiles.</summary>
    public DcbBar()
    {
        InitializeComponent();
        TilesHost.Height = DcbTileSizes.BarHeight;
        TilesHost.Background = new ImmutableSolidColorBrush(DcbColors.BarBackground);
        // Tunnelling, so the scroller's own wheel handling never runs; a latched spinner's top-level handler takes the wheel first.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>The tiles on the bar, left to right.</summary>
    public IReadOnlyList<DcbTile> Tiles => _tiles;

    /// <summary>
    /// Replaces the bar's tiles with <paramref name="tiles"/> and lays them out in the order given; <paramref name="rightGroup"/>,
    /// the last of them in their order (empty for none), moves to the bar's right edge in its own order, its last tile rightmost, while the bar is
    /// wider than the laid-out tiles, the band then as wide as the bar, and keeps its places in the flow otherwise.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rightGroup"/> is not the tail of <paramref name="tiles"/>, in order.</exception>
    public void SetTiles(IEnumerable<DcbTile> tiles, IReadOnlyList<DcbTile> rightGroup)
    {
        DcbTile[] given = [.. tiles];
        if ((rightGroup.Count > given.Length) || !given[^rightGroup.Count..].SequenceEqual(rightGroup))
        {
            throw new ArgumentException("The right group is not the tail of the tiles set, in order.", nameof(rightGroup));
        }

        TilesHost.Children.Clear();
        _tiles.Clear();
        _tiles.AddRange(given);
        IReadOnlyList<Rect> rects = DcbLayout.Flow([.. _tiles.Select(tile => tile.Shape)]);
        for (int i = 0; i < _tiles.Count; i++)
        {
            Canvas.SetLeft(_tiles[i], rects[i].X);
            Canvas.SetTop(_tiles[i], rects[i].Y);
            TilesHost.Children.Add(_tiles[i]);
        }

        _flow = rects;
        _flowWidth = (_tiles.Count == 0) ? 0 : rects.Max(rect => rect.Right);
        _rightGroup = [.. rightGroup];
        TilesHost.Width = _flowWidth;
        TilesHost.Height = DcbTileSizes.BarHeight;
        InvalidateArrange();
    }

    /// <summary>
    /// Scrolls the tiles the least that shows all of <paramref name="tile"/> in the bar, the left edge first when it is
    /// wider than the bar; nothing when it is already in view or is not on the bar.
    /// </summary>
    public void ScrollIntoView(DcbTile tile)
    {
        int index = _tiles.IndexOf(tile);
        if (index < 0)
        {
            return;
        }

        double left = Canvas.GetLeft(tile);
        double right = left + _flow[index].Width;
        double offset = Scroller.Offset.X;
        double viewport = Scroller.Viewport.Width;
        if (right > offset + viewport)
        {
            offset = right - viewport;
        }

        ScrollTo(Math.Min(offset, left));
    }

    /// <summary>The wheel over the bar scrolls its tiles a step a notch, the wheel down moving them left; the Scope never sees it.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        ScrollTo(Scroller.Offset.X - (e.Delta.Y * WheelStep));
    }

    /// <summary>Scrolls the tiles to <paramref name="offset"/>, kept between the first tile and the last one at the bar's right edge.</summary>
    private void ScrollTo(double offset)
    {
        double max = Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width);
        Scroller.Offset = new Vector(Math.Clamp(offset, 0, max), 0);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        PlaceRightGroup(finalSize.Width);
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>
    /// Puts the right group's tiles side by side, in order, against the right of a bar <paramref name="width"/> wide when that
    /// is wider than the flow, else each in its flow place.
    /// </summary>
    private void PlaceRightGroup(double width)
    {
        if (_rightGroup.Count == 0)
        {
            return;
        }

        bool wide = width > _flowWidth;
        double left = width - _rightGroup.Sum(tile => _flow[_tiles.IndexOf(tile)].Width);
        foreach (DcbTile tile in _rightGroup)
        {
            Rect flowed = _flow[_tiles.IndexOf(tile)];
            Canvas.SetLeft(tile, wide ? left : flowed.X);
            left += flowed.Width;
        }

        TilesHost.Width = wide ? width : _flowWidth;
    }
}
