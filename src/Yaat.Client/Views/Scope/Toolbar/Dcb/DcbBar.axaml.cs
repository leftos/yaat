using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// A STARS DCB bar: a black band of <see cref="DcbTile"/>s laid out left to right by <see cref="DcbLayout"/>, in the order
/// they were set, the band as wide as its widest column ends; one tile may sit at the bar's right edge instead when the bar is
/// wider than that.
/// </summary>
public partial class DcbBar : UserControl
{
    private readonly List<DcbTile> _tiles = [];
    private IReadOnlyList<Rect> _flow = [];
    private double _flowWidth;
    private DcbTile? _rightEdge;

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
    /// Replaces the bar's tiles with <paramref name="tiles"/> and lays them out in the order given; <paramref name="rightEdge"/>,
    /// one of them, moves to the bar's right edge while the bar is wider than the laid-out tiles, the band then as wide as the
    /// bar, and keeps its place in the flow otherwise.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="rightEdge"/> is not null and not one of <paramref name="tiles"/>.</exception>
    public void SetTiles(IEnumerable<DcbTile> tiles, DcbTile? rightEdge)
    {
        DcbTile[] given = [.. tiles];
        if ((rightEdge is not null) && !given.Contains(rightEdge))
        {
            throw new ArgumentException("The right-edge tile is not one of the tiles set.", nameof(rightEdge));
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
        _rightEdge = rightEdge;
        TilesHost.Width = _flowWidth;
        TilesHost.Height = DcbTileSizes.BarHeight;
        InvalidateArrange();
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        PlaceRightEdge(finalSize.Width);
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>
    /// Puts the right-edge tile at the right of a bar <paramref name="width"/> wide when that is wider than the flow, else in
    /// its flow place.
    /// </summary>
    private void PlaceRightEdge(double width)
    {
        if (_rightEdge is not { } tile)
        {
            return;
        }

        Rect flowed = _flow[_tiles.IndexOf(tile)];
        bool wide = width > _flowWidth;
        Canvas.SetLeft(tile, wide ? (width - flowed.Width) : flowed.X);
        TilesHost.Width = wide ? width : _flowWidth;
    }
}
