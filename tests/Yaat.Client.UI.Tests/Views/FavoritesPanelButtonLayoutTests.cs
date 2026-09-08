using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Layout coverage for issue #425: in the pop-out Favorites Panel the buttons sit edge to edge —
/// each one fills its <see cref="UniformGrid"/> cell horizontally and vertically, so a row reads as
/// a strip separated only by the 1px button borders instead of differently sized buttons with gaps
/// between them.
/// </summary>
public class FavoritesPanelButtonLayoutTests
{
    private const double Epsilon = 0.5;

    private const int Columns = 2;

    private static (Window Window, MainViewModel Vm, UniformGrid Grid, int OriginalColumns) ShowGroundPalette(FavoriteCommand[] favorites)
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var view = new FavoritesBarView { IsPaletteMode = true };
        var window = new Window
        {
            Width = 800,
            Height = 450,
            Content = view,
            DataContext = vm,
        };
        window.ShowAndRunLayout();

        var originalColumns = vm.Preferences.FavoritePanelColumns;
        var columnsBox = view.GetVisualDescendants().OfType<NumericUpDown>().Single();
        columnsBox.Value = Columns;
        Dispatcher.UIThread.RunJobs();

        foreach (var favorite in favorites)
        {
            vm.AddFavorite(favorite, [vm.FavoriteStore.GlobalSet.Id]);
        }
        Dispatcher.UIThread.RunJobs();

        var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Ground");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var scroll = Assert.IsType<ScrollViewer>((tabs.SelectedItem as TabItem)?.Content);
        var grid = Assert.IsType<UniformGrid>(scroll.Content);
        return (window, vm, grid, originalColumns);
    }

    private static void ClosePalette(Window window, MainViewModel vm, int originalColumns, FavoriteCommand[] favorites)
    {
        vm.Preferences.SetFavoritePanelColumns(originalColumns);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (var favorite in favorites.Where(f => !string.IsNullOrEmpty(f.Id)))
        {
            vm.DeleteFavorite(favorite.Id);
        }
    }

    private static Button FindButton(UniformGrid grid, string label) =>
        grid.Children.OfType<Button>().Single(b => b.Tag is FavoriteDisplayEntry entry && entry.Favorite.Label == label);

    [AvaloniaFact]
    public void PaletteButtons_FillTheirCells_AndTouchHorizontally()
    {
        var favorites = new[]
        {
            new FavoriteCommand
            {
                Label = "LayoutFavA425",
                CommandText = "TAXI A",
                Category = FavoriteCommandCategory.Ground,
            },
            new FavoriteCommand
            {
                Label = "LayoutFavB425",
                CommandText = "TAXI B",
                Category = FavoriteCommandCategory.Ground,
            },
        };
        var (window, vm, grid, originalColumns) = ShowGroundPalette(favorites);
        try
        {
            // Two columns, so the first two children are the two cells of the top row.
            var buttons = grid.Children.OfType<Button>().Take(2).ToList();
            Assert.Equal(2, buttons.Count);
            Assert.All(buttons, b => Assert.IsType<FavoriteDisplayEntry>(b.Tag));

            var cellWidth = grid.Bounds.Width / Columns;
            Assert.True(cellWidth > 0, "The palette grid was never given a width — the layout pass did not run");

            foreach (var button in buttons)
            {
                Assert.True(
                    Math.Abs(button.Bounds.Width - cellWidth) <= Epsilon,
                    $"Button '{(button.Tag as FavoriteDisplayEntry)?.Favorite.Label}' is {button.Bounds.Width:F1}px wide "
                        + $"but its cell is {cellWidth:F1}px — it does not fill the cell"
                );
            }

            var firstOrigin = buttons[0].TranslatePoint(new Point(0, 0), grid);
            var secondOrigin = buttons[1].TranslatePoint(new Point(0, 0), grid);
            Assert.NotNull(firstOrigin);
            Assert.NotNull(secondOrigin);

            var firstRightEdge = firstOrigin.Value.X + buttons[0].Bounds.Width;
            Assert.True(
                Math.Abs(firstRightEdge - secondOrigin.Value.X) <= Epsilon,
                $"The first button ends at x={firstRightEdge:F1} but the second starts at x={secondOrigin.Value.X:F1} — they do not touch"
            );
        }
        finally
        {
            ClosePalette(window, vm, originalColumns, favorites);
        }
    }

    /// <summary>
    /// Two favorites with different per-favorite heights still render the same height: the setting
    /// is a floor on the row height (the grid's rows are uniform), and each button stretches to
    /// fill its cell, so no horizontal gap opens up between a short button and its taller neighbour.
    /// </summary>
    [AvaloniaFact]
    public void PaletteButtons_OfDifferentHeights_FillTheirCellsVertically()
    {
        var favorites = new[]
        {
            new FavoriteCommand
            {
                Label = "LayoutShort425",
                CommandText = "TAXI A",
                Category = FavoriteCommandCategory.Ground,
                ButtonHeight = 32,
            },
            new FavoriteCommand
            {
                Label = "LayoutTall425",
                CommandText = "TAXI B",
                Category = FavoriteCommandCategory.Ground,
                ButtonHeight = 48,
            },
        };
        var (window, vm, grid, originalColumns) = ShowGroundPalette(favorites);
        try
        {
            var shortButton = FindButton(grid, "LayoutShort425");
            var tallButton = FindButton(grid, "LayoutTall425");

            // UniformGrid rows are uniform, so every cell is this tall regardless of which row a
            // button landed in (other Ground favorites in the store may precede these two).
            var rows = (int)Math.Ceiling(grid.Children.Count / (double)Columns);
            var cellHeight = grid.Bounds.Height / rows;
            Assert.True(cellHeight > 0, "The palette grid was never given a height — the layout pass did not run");

            Assert.True(
                Math.Abs(shortButton.Bounds.Height - tallButton.Bounds.Height) <= Epsilon,
                $"The 32px favorite rendered {shortButton.Bounds.Height:F1}px tall and the 48px one {tallButton.Bounds.Height:F1}px "
                    + "— they leave a vertical gap in the row"
            );

            foreach (var button in new[] { shortButton, tallButton })
            {
                Assert.True(
                    Math.Abs(button.Bounds.Height - cellHeight) <= Epsilon,
                    $"Button '{(button.Tag as FavoriteDisplayEntry)?.Favorite.Label}' is {button.Bounds.Height:F1}px tall "
                        + $"but its cell is {cellHeight:F1}px — it does not fill the cell"
                );
            }
        }
        finally
        {
            ClosePalette(window, vm, originalColumns, favorites);
        }
    }
}
