using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// The Scope's bar under the STARS profile: CRC's STARS DCB on a <see cref="DcbBar"/>, over the <see cref="ScopeViewModel"/>
/// in its DataContext, showing its Main page (<see cref="StarsMainPage"/>), one of the submenus MAPS, BRITE, CHAR SIZE and
/// PREF the Main page opens, the Aux page SHIFT shows, the TPA/ATPA submenu the Aux page opens, or the YAAT page the YAAT tile
/// shows and the pages it opens (<see cref="StarsSubmenus"/>); the YAAT tile sits at the bar's right edge when the bar is
/// wider than the Main page's tiles. While the YAAT TOOLS or PILOT LENS page shows, the Scope's open menu is that tray, as on
/// the classic bar, so TOOLS arms the Rings tool.
/// Each live tile runs the classic bar's action for its Scope button (<see cref="ScopeViewModel.Press"/>), a map tile toggles
/// the map in its slot, a pref-set tile loads the set in its slot, PTL OWN and PTL ALL set the vector ownership, and a
/// spinner steps the view model's own value live while latched, Esc putting back the value it had when the latch began.
/// DONE, or Esc with no spinner latched, returns from a submenu to the page it opens from; the Aux page leaves Esc to the
/// Scope.
/// </summary>
public sealed class StarsDcbBar : UserControl
{
    private readonly DcbBar _bar = new();
    private ScopeViewModel? _viewModel;
    private StarsDcbPage _page = StarsDcbPage.Main;
    private IReadOnlyList<StarsDcbEntry> _entries = StarsMainPage.Entries;
    private int _mapsShown;
    private int _prefsShown;

    // The view model the open submenu's flag was set on and the top level whose Esc closes it; null on the Main and Aux pages.
    private ScopeViewModel? _submenuOn;
    private TopLevel? _escapeOn;

    // The latched spinner, the view model it was latched on, the command that puts its value back there and, for a band value
    // that was not pinned, the command that unpins it again, all set when the latch began; null while none is latched.
    private ScopeButtonId? _latched;
    private ScopeViewModel? _latchedOn;
    private string? _restoreCommand;
    private string? _unpinCommand;

    // The TOOLS SIZE value when its latch began, which a cancel puts back: SIZE has no command, so it is set directly.
    private double? _restoreRingSizeNm;

    /// <summary>A Main page with no Scope behind it until its DataContext is a ScopeViewModel.</summary>
    public StarsDcbBar()
    {
        Content = _bar;
        Rebuild();
    }

    /// <summary>The page the bar shows.</summary>
    public StarsDcbPage Page => _page;

    /// <summary>The entries of the shown page, left to right, one per tile in <see cref="Tiles"/>.</summary>
    public IReadOnlyList<StarsDcbEntry> Entries => _entries;

    /// <summary>The shown page's tiles, left to right, one per <see cref="Entries"/> item in its order.</summary>
    public IReadOnlyList<DcbTile> Tiles => _bar.Tiles;

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A latch held over the old view model ends there, its value put back, and an open submenu closes there, before the
        // bar takes the new one.
        CancelLatch();
        LeaveSubmenu();
        if (_viewModel is { } old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
            old.MapToggles.CollectionChanged -= OnMapTogglesChanged;
        }

        _viewModel = DataContext as ScopeViewModel;
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.MapToggles.CollectionChanged += OnMapTogglesChanged;
        }

        Rebuild();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // A bar that leaves the tree, as on a profile change or a re-parent, closes its submenu on the view model it was opened
        // on, showing the nearest page up from it that is no submenu (the Main or the Aux page), so the submenu flag, the Esc
        // handler and any tray a YAAT page opened are all cleared; the Main and Aux pages stay.
        ShowPage(NearestPageOf(_page));
    }

    /// <summary>The nearest page up from <paramref name="page"/> that is no submenu: Main or Aux, the page itself when it is one.</summary>
    private static StarsDcbPage NearestPageOf(StarsDcbPage page)
    {
        while (StarsSubmenus.IsSubmenu(page))
        {
            page = StarsSubmenus.ParentOf(page);
        }

        return page;
    }

    private int MapsAvailable => Math.Min(_viewModel?.MapToggles.Count ?? 0, StarsSubmenus.MapSlotsOf(_page));

    private int PrefsAvailable => (StarsSubmenus.PrefSlotsOf(_page) == 0) ? 0 : Math.Min(PrefSets().Count, StarsSubmenus.PrefSlotsOf(_page));

    /// <summary>The Scope's pref-set buttons in file order, as the classic bar's PREF tray lists them.</summary>
    private IReadOnlyList<ScopeButton> PrefSets() =>
        ((_viewModel is { } vm) && (ScopeToolbarButtons.Find(ScopeButtonId.Pref) is { } pref))
            ? [.. vm.TrayChildren(pref).Where(b => b.Id == ScopeButtonId.PrefSet)]
            : [];

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScopeViewModel.ProfileKind))
        {
            ShowMain();
        }
        else if (e.PropertyName == nameof(ScopeViewModel.OpenMenu))
        {
            FollowTray();
        }
        else if ((e.PropertyName == nameof(ScopeViewModel.IsBarHidden)) && (_viewModel is { IsBarHidden: true }))
        {
            CloseForHide();
        }

        RebuildOrRefresh();
    }

    /// <summary>
    /// Closes what the bar holds open as it hides: a submenu, leaving for the nearest page up that is no submenu (Main or Aux,
    /// its flag, Esc handler and tray cleared), and a latched spinner (its value put back), so nothing waits behind the SHOW
    /// tile and the bar comes back on the page it left from.
    /// </summary>
    private void CloseForHide()
    {
        StarsDcbPage page = NearestPageOf(_page);
        if (page == _page)
        {
            // ShowPage keeps a page that is already shown, so the rebuild is what ends a latch on it.
            Rebuild();
        }
        else
        {
            ShowPage(page);
        }
    }

    /// <summary>
    /// Returns from the YAAT TOOLS or PILOT LENS page to the YAAT page when the Scope's open menu leaves that page's tray by
    /// another route, so the page never shows with its tray closed (the Rings tool disarmed).
    /// </summary>
    private void FollowTray()
    {
        if ((TrayOf(_page) is { } tray) && (_viewModel?.OpenMenu != tray))
        {
            ShowPage(StarsDcbPage.Yaat);
        }
    }

    private void OnMapTogglesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildOrRefresh();

    /// <summary>Rebuilds the tiles when a map or pref-set slot gains or loses what it shows, which changes its kind; else redraws them.</summary>
    private void RebuildOrRefresh()
    {
        if ((MapsAvailable != _mapsShown) || (PrefsAvailable != _prefsShown))
        {
            Rebuild();
        }
        else
        {
            Refresh();
        }
    }

    /// <summary>Builds the shown page's tiles afresh, ending any latch first so its value goes back while its tiles stand.</summary>
    private void Rebuild()
    {
        CancelLatch();
        _entries = StarsSubmenus.EntriesOf(_page);
        _mapsShown = MapsAvailable;
        _prefsShown = PrefsAvailable;
        DcbTile[] tiles = [.. _entries.Select(BuildTile)];
        int yaat = (_page == StarsDcbPage.Main) ? _entries.ToList().FindIndex(e => e.Opens == StarsDcbPage.Yaat) : -1;
        _bar.SetTiles(tiles, (yaat >= 0) ? tiles[yaat] : null);
        Refresh();
    }

    /// <summary>The tile for <paramref name="entry"/>, wired to its action; a map or pref-set slot with nothing in it is disabled.</summary>
    private DcbTile BuildTile(StarsDcbEntry entry)
    {
        bool emptySlot = ((entry.MapSlot >= 0) && (entry.MapSlot >= _mapsShown)) || ((entry.PrefSlot >= 0) && (entry.PrefSlot >= _prefsShown));
        var tile = new DcbTile(entry.Shape, emptySlot ? DcbTileKind.Disabled : entry.Kind, "");
        if (!emptySlot && (entry.Kind != DcbTileKind.Disabled))
        {
            WireEntry(tile, entry);
        }

        return tile;
    }

    private void WireEntry(DcbTile tile, StarsDcbEntry entry)
    {
        if (entry.Opens is { } page)
        {
            tile.OnClick = () => ShowPage(page);
        }
        else if (entry.MapSlot >= 0)
        {
            tile.OnToggle = _ => PressMap(entry.MapSlot);
        }
        else if (entry.PrefSlot >= 0)
        {
            tile.OnClick = () => PressPrefSet(entry.PrefSlot);
        }
        else if (entry.Ownership is { } ownership)
        {
            tile.OnToggle = _ => PressOwnership(ownership);
        }
        else if (entry.Button is { } id)
        {
            Wire(tile, id);
        }
    }

    /// <summary>Wires <paramref name="tile"/> to the Scope button with <paramref name="id"/>, by the tile's kind.</summary>
    private void Wire(DcbTile tile, ScopeButtonId id)
    {
        switch (tile.Kind)
        {
            case DcbTileKind.Spinner:
                tile.OnLatch = () => Latch(id);
                tile.OnSpin = sign => _viewModel?.StepSpinner(id, sign);
                tile.OnAccept = _ => EndLatch();
                tile.OnCancel = CancelLatch;
                break;
            case DcbTileKind.Toggle:
                tile.OnToggle = _ => PressButton(id);
                break;
            default:
                tile.OnClick = () => PressButton(id);
                break;
        }
    }

    /// <summary>
    /// Shows <paramref name="page"/>, closing any open submenu first: a submenu sets the view model's submenu flag and takes
    /// Esc through the top level, after the Scope view's own handler, which steps aside for it; the Main and the Aux page
    /// leave Esc to the Scope.
    /// </summary>
    private void ShowPage(StarsDcbPage page)
    {
        if (page == _page)
        {
            return;
        }

        LeaveSubmenu();
        _page = page;
        if (StarsSubmenus.IsSubmenu(page))
        {
            _submenuOn = _viewModel;
            _submenuOn?.IsBarSubmenuOpen = true;
            _escapeOn = TopLevel.GetTopLevel(this);
            _escapeOn?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        }

        Rebuild();
        if (TrayOf(page) is { } tray)
        {
            _submenuOn?.OpenMenu = tray;
        }
    }

    /// <summary>
    /// The classic tray whose open state the page <paramref name="page"/> stands for while it shows: TOOLS, whose opening arms
    /// the Rings tool, and PILOT LENS, which lights its tile; null for any other page.
    /// </summary>
    private static ScopeButtonId? TrayOf(StarsDcbPage page) =>
        page switch
        {
            StarsDcbPage.YaatTools => ScopeButtonId.Tools,
            StarsDcbPage.YaatLens => ScopeButtonId.Lens,
            _ => null,
        };

    /// <summary>Returns to the Main page from any other page.</summary>
    private void ShowMain() => ShowPage(StarsDcbPage.Main);

    /// <summary>
    /// Ends the open submenu's state without rebuilding: the tray a YAAT page opened closed and the flag cleared where they were
    /// set, the Esc handler removed, and the page back to Main.
    /// </summary>
    private void LeaveSubmenu()
    {
        // The page is Main before the tray closes, so the open-menu change it raises finds no tray to follow.
        ScopeButtonId? tray = TrayOf(_page);
        _page = StarsDcbPage.Main;
        if ((tray is not null) && (_submenuOn?.OpenMenu == tray))
        {
            _submenuOn.OpenMenu = null;
        }

        _submenuOn?.IsBarSubmenuOpen = false;
        _submenuOn = null;
        _escapeOn?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _escapeOn = null;
    }

    // Esc while a submenu is open returns to the page it opens from, unless a spinner is latched (that Esc is the spinner's), another
    // handler took it, a Scope popup is open to take it, or the keyboard focus is outside the Scope (an Esc typed in the
    // command box or a grid stays theirs).
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key != Key.Escape) || e.Handled || (_latched is not null) || !IsBarsEscape())
        {
            return;
        }

        ShowPage(StarsSubmenus.ParentOf(_page));
        e.Handled = true;
    }

    // Whether an Esc is the bar's: the keyboard focus is on the Scope, by the Scope view's own test, and no field or entry
    // popup of the Scope's is open, whose Esc closes it first whichever top-level handler runs first.
    private bool IsBarsEscape() =>
        (this.FindAncestorOfType<ScopeHost>()?.ScopeView is { } view)
        && view.IsKeyFocusInside()
        && (view.ActiveFieldPopup is not { IsOpen: true })
        && (view.ActiveEntryPopup is not { IsOpen: true });

    /// <summary>
    /// A spinner latched: Esc now goes to it, and its value at this moment, and for HISTORY and PTL LNTH whether that value is
    /// pinned, is what a cancel puts back.
    /// </summary>
    private void Latch(ScopeButtonId id)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        vm.IsBarSpinnerLatched = true;
        _latched = id;
        _latchedOn = vm;
        _restoreCommand = RestoreCommand(vm, id);
        _unpinCommand = IsPinned(vm, id) ? null : UnpinCommand(id);
        _restoreRingSizeNm = (id == ScopeButtonId.ToolSize) ? vm.RingSizeNm : null;
    }

    /// <summary>Whether the band value the spinner with <paramref name="id"/> steps is pinned; false for a spinner that never pins.</summary>
    private static bool IsPinned(ScopeViewModel vm, ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.Hist => vm.Pins.History is not null,
            ScopeButtonId.Vector => vm.Pins.VectorMin is not null,
            _ => false,
        };

    /// <summary>The command that unpins the band value the spinner with <paramref name="id"/> steps, or null for one that never pins.</summary>
    private static string? UnpinCommand(ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.Hist => ScopeToolbarButtons.HistAutoCommand(),
            ScopeButtonId.Vector => ScopeToolbarButtons.VectorAutoCommand(),
            _ => null,
        };

    /// <summary>The command that sets the spinner with <paramref name="id"/> to the value it shows now.</summary>
    private static string? RestoreCommand(ScopeViewModel vm, ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.Range => ScopeToolbarButtons.RangeCommand(vm.RangeNm),
            ScopeButtonId.RrSize => ScopeToolbarButtons.RrSizeCommand(vm.RangeRingSizeNm),
            ScopeButtonId.DbChar => ScopeToolbarButtons.DbCharCommand(vm.DbSettings.CharSize),
            ScopeButtonId.Hist => ScopeToolbarButtons.HistCommand(vm.EffectiveBand.History),
            ScopeButtonId.Vector => ScopeToolbarButtons.VectorCommand(vm.EffectiveBand.VectorMin),
            _ => (ScopeToolbarButtons.BriteGroupOf(id) is { } group) ? ScopeToolbarButtons.BriteCommand(group.Name, group.Get(vm.Brightness)) : null,
        };

    private void EndLatch()
    {
        _latchedOn?.IsBarSpinnerLatched = false;
        _latched = null;
        _latchedOn = null;
        _restoreCommand = null;
        _unpinCommand = null;
        _restoreRingSizeNm = null;
    }

    /// <summary>
    /// Cancels the latch, if one is held: puts the value back on the view model it was latched on, unless it is already the
    /// value the latch took, then unpins it again when it was not pinned at the latch and is now.
    /// </summary>
    private void CancelLatch()
    {
        (ScopeButtonId? id, ScopeViewModel? on, string? restore, string? unpin) = (_latched, _latchedOn, _restoreCommand, _unpinCommand);
        double? ringSizeNm = _restoreRingSizeNm;
        EndLatch();
        if ((on is not { } vm) || (id is not { } latched))
        {
            return;
        }

        PutBack(vm, latched, restore, unpin);
        if (ringSizeNm is { } nm)
        {
            vm.RingSizeNm = nm;
        }
    }

    /// <summary>Runs a cancelled latch's restore command unless the value is already back, then its unpin command when it is pinned now.</summary>
    private static void PutBack(ScopeViewModel vm, ScopeButtonId latched, string? restore, string? unpin)
    {
        if ((restore is not null) && (restore != RestoreCommand(vm, latched)))
        {
            vm.RunCommand(restore);
        }

        if ((unpin is not null) && IsPinned(vm, latched))
        {
            vm.RunCommand(unpin);
        }
    }

    private void PressButton(ScopeButtonId id)
    {
        if ((_viewModel is { } vm) && (ScopeToolbarButtons.Find(id) is { } button))
        {
            vm.Press(button);
        }

        Refresh();
    }

    private void PressMap(int slot)
    {
        if ((_viewModel is { } vm) && (slot < vm.MapToggles.Count))
        {
            ScopeMapToggle toggle = vm.MapToggles[slot];
            vm.Press(ScopeToolbarButtons.MapToggle(toggle.StarsId, toggle.ShortName));
        }

        Refresh();
    }

    /// <summary>Sets the vector ownership to <paramref name="ownership"/> unless it already is: YAAT has no ownership of none.</summary>
    private void PressOwnership(ScopeVectorOwnership ownership)
    {
        if ((_viewModel is { } vm) && (vm.VectorOwnership != ownership))
        {
            vm.RunCommand(ScopeToolbarButtons.VectorOwnershipCommand(ownership));
        }

        Refresh();
    }

    private void PressPrefSet(int slot)
    {
        IReadOnlyList<ScopeButton> sets = PrefSets();
        if ((_viewModel is { } vm) && (slot < sets.Count))
        {
            vm.Press(sets[slot]);
        }

        Refresh();
    }

    /// <summary>Redraws every tile's label and pushed state from the view model.</summary>
    private void Refresh()
    {
        IReadOnlyList<ScopeButton> sets = (_prefsShown > 0) ? PrefSets() : [];
        for (int i = 0; i < Math.Min(_bar.Tiles.Count, _entries.Count); i++)
        {
            RefreshTile(_bar.Tiles[i], _entries[i], sets);
        }
    }

    private void RefreshTile(DcbTile tile, StarsDcbEntry entry, IReadOnlyList<ScopeButton> prefSets)
    {
        if (entry.MapSlot >= 0)
        {
            RefreshMapTile(tile, entry.MapSlot);
        }
        else if (entry.PrefSlot >= 0)
        {
            RefreshPrefSetTile(tile, entry, prefSets);
        }
        else if (entry.Ownership is { } ownership)
        {
            tile.Label = entry.Label;
            tile.IsPushed = _viewModel?.VectorOwnership == ownership;
        }
        else
        {
            RefreshButtonTile(tile, entry);
        }
    }

    private void RefreshMapTile(DcbTile tile, int slot)
    {
        ScopeMapToggle? toggle = (slot < _mapsShown) ? _viewModel?.MapToggles[slot] : null;
        tile.Label = (toggle is null) ? "" : $"{toggle.StarsId}\n{toggle.ShortName}";
        tile.IsPushed = toggle?.IsOn ?? false;
    }

    private void RefreshPrefSetTile(DcbTile tile, StarsDcbEntry entry, IReadOnlyList<ScopeButton> prefSets)
    {
        ScopeButton? set = (entry.PrefSlot < Math.Min(prefSets.Count, _prefsShown)) ? prefSets[entry.PrefSlot] : null;
        tile.Label = set?.Label ?? entry.Label;
        tile.IsPushed = (set is not null) && (_viewModel?.IsActive(set) ?? false);
    }

    private void RefreshButtonTile(DcbTile tile, StarsDcbEntry entry)
    {
        ScopeButton? button = (entry.Button is { } id) ? ScopeToolbarButtons.Find(id) : null;
        tile.Label = LabelOf(entry, button);
        if (ShowsActive(tile, entry) && (button is not null) && (_viewModel is { } vm))
        {
            tile.IsPushed = vm.IsActive(button);
        }
    }

    /// <summary>
    /// Whether the tile of <paramref name="entry"/> is lit by its Scope button's active state, as on the classic bar: a toggle,
    /// a page opener over a button (PILOT LENS), and a PROFILE or TEMPLATE pick.
    /// </summary>
    private static bool ShowsActive(DcbTile tile, StarsDcbEntry entry) =>
        (tile.Kind == DcbTileKind.Toggle)
        || ((tile.Kind == DcbTileKind.Normal) && (entry.Opens is not null))
        || ((entry.Button is { } id) && (ScopeToolbarButtons.IsProfilePick(id) || IsTemplatePick(id)));

    private static bool IsTemplatePick(ScopeButtonId id) =>
        id is ScopeButtonId.TemplateStars or ScopeButtonId.TemplateEram or ScopeButtonId.TemplateMixed or ScopeButtonId.TemplateEuroScope;

    /// <summary>
    /// The label <paramref name="entry"/> draws: CRC's, its <c>%value%</c> filled with the view model's value for
    /// <paramref name="button"/>, or the value line left out where there is no value to show.
    /// </summary>
    private string LabelOf(StarsDcbEntry entry, ScopeButton? button)
    {
        const string ValuePart = "%value%";
        if (!entry.Label.Contains(ValuePart, StringComparison.Ordinal))
        {
            return entry.Label;
        }

        string? value = (button is not null) ? _viewModel?.SpinnerValue(button) : null;
        return (value is null)
            ? entry.Label.Replace("\n" + ValuePart, "", StringComparison.Ordinal)
            : entry.Label.Replace(ValuePart, value, StringComparison.Ordinal);
    }
}
