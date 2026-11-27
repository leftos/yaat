using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// A bar's sideways scroller that leaves every key alone: a plain <see cref="ScrollViewer"/> takes PageUp and PageDown
/// while a tile inside it has focus and marks them handled, so the window's own key bindings (push to talk, bookmarks)
/// never see them. The bar moves it by code instead (its wheel, a chevron, a tile scrolled into view).
/// </summary>
public class KeyIgnoringScrollViewer : ScrollViewer
{
    // How far above and below its viewport a scroller that does not clip vertically still draws its content, in DIP.
    private const double Overhang = 100_000;

    private ScrollContentPresenter? _presenter;

    /// <summary>
    /// Whether the content is clipped to the viewport's height as well as its width (the default). A bar whose menus hang
    /// below it over the Scope clips them sideways only, so they scroll with the bar and still show below it.
    /// </summary>
    public bool ClipsVertically { get; init; } = true;

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    /// <summary>Lets every key pass to the window, unhandled.</summary>
    protected override void OnKeyDown(KeyEventArgs e) { }

    /// <inheritdoc/>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _presenter?.SizeChanged -= OnPresenterSizeChanged;
        _presenter = ClipsVertically ? null : e.NameScope.Find<ScrollContentPresenter>("PART_ContentPresenter");
        if (_presenter is { } presenter)
        {
            presenter.ClipToBounds = false;
            presenter.SizeChanged += OnPresenterSizeChanged;
            ClipSideways(presenter);
        }
    }

    private void OnPresenterSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollContentPresenter presenter)
        {
            ClipSideways(presenter);
        }
    }

    /// <summary>Clips <paramref name="presenter"/>'s content to its width alone, leaving it free to draw above and below.</summary>
    private static void ClipSideways(ScrollContentPresenter presenter) =>
        presenter.Clip = new RectangleGeometry(new Rect(0, -Overhang, presenter.Bounds.Width, presenter.Bounds.Height + (2 * Overhang)));
}
