using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// A STARS DCB bar: a black band of <see cref="DcbTile"/>s laid out left to right by <see cref="DcbLayout"/>, in the order
/// they were set, the band as wide as its widest column ends; a group of them may sit together at the bar's right edge instead
/// when the bar is wider than that.
/// </summary>
public partial class DcbBar : UserControl
{
    private readonly List<DcbTile> _tiles = [];
    private IReadOnlyList<Rect> _flow = [];
    private double _flowWidth;
    private IReadOnlyList<DcbTile> _rightGroup = [];

    /// <summary>A bar holding no tiles.</summary>
    public DcbBar()
    {
        InitializeComponent();
        TilesHost.Height = DcbTileSizes.BarHeight;
        TilesHost.Background = new ImmutableSolidColorBrush(DcbColors.BarBackground);
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
