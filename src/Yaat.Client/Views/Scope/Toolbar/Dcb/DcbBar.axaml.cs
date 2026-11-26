using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// A STARS DCB bar: a black band of <see cref="DcbTile"/>s laid out left to right by <see cref="DcbLayout"/>, in the order
/// they were set, the band as wide as its widest column ends.
/// </summary>
public partial class DcbBar : UserControl
{
    private readonly List<DcbTile> _tiles = [];

    /// <summary>A bar holding no tiles.</summary>
    public DcbBar()
    {
        InitializeComponent();
        TilesHost.Height = DcbTileSizes.BarHeight;
        TilesHost.Background = new ImmutableSolidColorBrush(DcbColors.BarBackground);
    }

    /// <summary>The tiles on the bar, left to right.</summary>
    public IReadOnlyList<DcbTile> Tiles => _tiles;

    /// <summary>Replaces the bar's tiles with <paramref name="tiles"/> and lays them out in the order given.</summary>
    public void SetTiles(IEnumerable<DcbTile> tiles)
    {
        TilesHost.Children.Clear();
        _tiles.Clear();
        _tiles.AddRange(tiles);
        IReadOnlyList<Rect> rects = DcbLayout.Flow([.. _tiles.Select(tile => tile.Shape)]);
        for (int i = 0; i < _tiles.Count; i++)
        {
            Canvas.SetLeft(_tiles[i], rects[i].X);
            Canvas.SetTop(_tiles[i], rects[i].Y);
            TilesHost.Children.Add(_tiles[i]);
        }

        TilesHost.Width = (_tiles.Count == 0) ? 0 : rects.Max(rect => rect.Right);
        TilesHost.Height = DcbTileSizes.BarHeight;
    }
}
