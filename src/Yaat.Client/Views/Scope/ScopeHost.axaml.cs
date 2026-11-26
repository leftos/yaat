using System.ComponentModel;
using Avalonia.Controls;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;
using Yaat.Client.Views.Scope.Toolbar.Eram;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope as one control: its bar, its view and its views dock, over the <see cref="ViewModels.ScopeViewModel"/>
/// in its DataContext. The main window's Scope tab and the Scope window each build their own host over the same view model,
/// never both at once, since a control has one parent. The bar slot shows the bar <see cref="ScopeViewModel.BarKind"/>
/// names, so the bar follows the room's profile kind: the STARS DCB Main page under the STARS profile, the ERAM Master
/// Toolbar under the ERAM profile and the classic <see cref="Toolbar.ScopeToolbar"/> under Mixed; the bar's row is as tall
/// as the bar shown. While <see cref="ScopeViewModel.IsBarHidden"/> the bar row collapses, the bar stays in its slot unseen,
/// and the show slot holds the bar kind's SHOW tile at the scope's top-left, which runs
/// <see cref="ScopeToolbarButtons.BarShowCommand"/>.
/// </summary>
public partial class ScopeHost : UserControl
{
    private const string NoBarMessage = "The Scope host shows no classic bar: its DataContext is no ScopeViewModel, or its profile shows another bar";

    /// <summary>
    /// The height of the classic bar's row. ScopeHost.axaml's 45 is the same height for the designer and the first layout;
    /// the constructor and every bar change set the row from here.
    /// </summary>
    private const double ClassicBarHeight = 45;

    /// <summary>The STARS SHOW tile's label, a DCB half tile's two lines.</summary>
    private const string ShowDcbLabel = "SHOW\nDCB";

    private ScopeViewModel? _viewModel;
    private (ScopeBarKind Kind, bool Hidden)? _builtFor;

    public ScopeHost()
    {
        InitializeComponent();
        HostGrid.RowDefinitions[0].Height = new GridLength(ClassicBarHeight);
    }

    /// <summary>The Scope's view: its canvas and its key handling.</summary>
    internal ScopeView ScopeView => View;

    /// <summary>The Scope's classic bar, while its bar slot shows it.</summary>
    /// <exception cref="InvalidOperationException">The slot shows another bar (the STARS or the ERAM bar) or none yet.</exception>
    internal ScopeToolbar ScopeToolbar => (BarSlot.Content as ScopeToolbar) ?? throw new InvalidOperationException(NoBarMessage);

    /// <summary>The Scope's views dock.</summary>
    internal Dock.ScopeDock ScopeDock => Dock;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ScopeViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        if (_viewModel is { } viewModel)
        {
            ShowBar(viewModel.BarKind, viewModel.IsBarHidden);
        }
    }

    /// <summary>Shows the bar, or its SHOW tile, for the view model's bar kind when the kind or the hidden flag changed.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName is nameof(ScopeViewModel.BarKind) or nameof(ScopeViewModel.IsBarHidden)) && (_viewModel is { } viewModel))
        {
            ShowBar(viewModel.BarKind, viewModel.IsBarHidden);
        }
    }

    /// <summary>
    /// Shows the bar for <paramref name="kind"/> in the slot, its row as tall as the bar so the bar never covers the scope's
    /// top; a kind whose bar type is already shown keeps that instance. While <paramref name="hidden"/> the bar stays in its
    /// slot unseen, its row is 0 so the scope grows up into it, and the show slot holds the kind's SHOW tile.
    /// </summary>
    private void ShowBar(ScopeBarKind kind, bool hidden)
    {
        if ((_builtFor == (kind, hidden)) && (BarSlot.Content is not null))
        {
            return;
        }

        if (BarSlot.Content?.GetType() != BarTypeFor(kind))
        {
            BarSlot.Content = BuildBar(kind);
        }

        BarSlot.IsVisible = !hidden;
        ShowSlot.Content = hidden ? BuildShowTile(kind) : null;
        ShowSlot.IsVisible = hidden;
        HostGrid.RowDefinitions[0].Height = new GridLength(hidden ? 0 : BarHeightOf(kind));
        _builtFor = (kind, hidden);
    }

    /// <summary>The height of the row the bar of <paramref name="kind"/> takes.</summary>
    private static double BarHeightOf(ScopeBarKind kind) => (kind == ScopeBarKind.Eram) ? EramMasterLayout.BarHeight : ClassicBarHeight;

    /// <summary>
    /// The SHOW tile a hidden bar of <paramref name="kind"/> leaves, in the bar's own style: a DCB half tile SHOW DCB on
    /// STARS, an ERAM command button SHOW TOOLBAR on ERAM and a navy half tile SHOW BAR on Mixed.
    /// </summary>
    private Control BuildShowTile(ScopeBarKind kind) =>
        kind switch
        {
            ScopeBarKind.Classic => ClassicShowTile(),
            ScopeBarKind.Stars => new DcbTile(DcbTileShape.Half, DcbTileKind.Normal, ShowDcbLabel) { OnClick = RunShow },
            ScopeBarKind.Eram => EramShowTile(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Scope bar kind"),
        };

    /// <summary>The classic bar's SHOW BAR, a navy half tile drawn by the bar's styles.</summary>
    private ScopeButtonControl ClassicShowTile()
    {
        var tile = new ScopeButtonControl(ScopeToolbarButtons.BarShow) { IsHalf = true };
        tile.Button.Click += (_, _) => RunShow();
        tile.Refresh();
        return tile;
    }

    /// <summary>The ERAM bar's SHOW TOOLBAR, a command button as wide as the Master Toolbar's; its gold strip is drawn, inert.</summary>
    private EramToolbarButton EramShowTile()
    {
        var button = new EramToolbarButton(
            EramButtonType.ShowToolbar,
            EramMasterToolbar.PickAreaWidth(ShowSlot.FontFamily, ShowSlot.FontSize),
            null,
            null
        );
        button.Invoked += (_, _) => RunShow();
        return button;
    }

    /// <summary>Shows the bar again through the typed command's path.</summary>
    private void RunShow() => _viewModel?.RunCommand(ScopeToolbarButtons.BarShowCommand);

    /// <summary>The type of the bar a Scope of <paramref name="kind"/> shows.</summary>
    private static Type BarTypeFor(ScopeBarKind kind) =>
        kind switch
        {
            ScopeBarKind.Classic => typeof(ScopeToolbar),
            ScopeBarKind.Stars => typeof(StarsDcbBar),
            ScopeBarKind.Eram => typeof(EramBar),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Scope bar kind"),
        };

    /// <summary>The bar a Scope of <paramref name="kind"/> shows.</summary>
    private static Control BuildBar(ScopeBarKind kind) =>
        kind switch
        {
            ScopeBarKind.Classic => new ScopeToolbar(),
            ScopeBarKind.Stars => new StarsDcbBar(),
            ScopeBarKind.Eram => new EramBar(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Scope bar kind"),
        };
}
