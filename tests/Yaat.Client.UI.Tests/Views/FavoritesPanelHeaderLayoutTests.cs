using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Layout coverage for issue #359: the "Cols" NumericUpDown in the Favorites Panel header was
/// 58px wide while the Fluent spinner's two RepeatButtons alone need 68px, so the value text was
/// squeezed to zero width and the decrease (down) button overflowed the control's border. The
/// spinner also had no margin, so the "Blank" button sat flush against it while every other
/// header control keeps an 8px gap.
/// </summary>
public class FavoritesPanelHeaderLayoutTests
{
    private const double Epsilon = 0.5;

    private static (Window Window, FavoritesBarView View, NumericUpDown ColumnsBox) ShowPalette()
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

        var columnsBox = view.GetVisualDescendants().OfType<NumericUpDown>().Single();
        return (window, view, columnsBox);
    }

    [AvaloniaFact]
    public void ColumnsSpinner_ButtonsFitInsideControlBounds()
    {
        var (window, _, columnsBox) = ShowPalette();
        try
        {
            var spinnerButtons = columnsBox.GetVisualDescendants().OfType<RepeatButton>().ToList();
            Assert.Equal(2, spinnerButtons.Count);

            foreach (var button in spinnerButtons)
            {
                var origin = button.TranslatePoint(new Point(0, 0), columnsBox);
                Assert.NotNull(origin);
                var rightEdge = origin.Value.X + button.Bounds.Width;
                Assert.True(
                    (origin.Value.X >= -Epsilon) && (rightEdge <= columnsBox.Bounds.Width + Epsilon),
                    $"Spinner button '{button.Name}' spans x=[{origin.Value.X:F1}, {rightEdge:F1}] "
                        + $"but the control is only {columnsBox.Bounds.Width:F1} wide"
                );
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ColumnsSpinner_ValueTextIsVisible()
    {
        var (window, _, columnsBox) = ShowPalette();
        try
        {
            var textBox = columnsBox.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(textBox.Bounds.Width >= 20, $"Value TextBox is only {textBox.Bounds.Width:F1}px wide — the column count is not visible");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ColumnsSpinner_HasSameGapFromBlankButtonAsOtherHeaderControls()
    {
        var (window, view, columnsBox) = ShowPalette();
        try
        {
            var blankButton = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Blank");

            var blankOrigin = blankButton.TranslatePoint(new Point(0, 0), view);
            var boxOrigin = columnsBox.TranslatePoint(new Point(0, 0), view);
            Assert.NotNull(blankOrigin);
            Assert.NotNull(boxOrigin);

            var gap = boxOrigin.Value.X - (blankOrigin.Value.X + blankButton.Bounds.Width);
            Assert.True(gap >= 8 - Epsilon, $"Gap between the Blank button and the Cols spinner is {gap:F1}px; header controls keep an 8px gap");
        }
        finally
        {
            window.Close();
        }
    }
}
