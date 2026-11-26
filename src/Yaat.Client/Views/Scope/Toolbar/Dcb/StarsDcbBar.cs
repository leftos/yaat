using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>
/// The Scope's bar under the STARS profile: CRC's STARS DCB Main page (<see cref="StarsMainPage"/>) on a <see cref="DcbBar"/>,
/// over the <see cref="ScopeViewModel"/> in its DataContext. Each live tile runs the classic bar's action for its Scope button
/// (<see cref="ScopeViewModel.Press"/>), a map tile toggles the map in its slot, and RANGE and RR step the view model's
/// own value live while latched, Esc putting back the value they had when the latch began.
/// </summary>
public sealed class StarsDcbBar : UserControl
{
    private readonly DcbBar _bar = new();
    private ScopeViewModel? _viewModel;
    private int _mapsShown;

    // The latched spinner, the view model it was latched on and the command that puts its value back there, all set when the
    // latch began; null while none is latched.
    private ScopeButtonId? _latched;
    private ScopeViewModel? _latchedOn;
    private string? _restoreCommand;

    /// <summary>A Main page with no Scope behind it until its DataContext is a ScopeViewModel.</summary>
    public StarsDcbBar()
    {
        Content = _bar;
        Rebuild();
    }

    /// <summary>The page's tiles, left to right, one per <see cref="StarsMainPage.Entries"/> item in its order.</summary>
    public IReadOnlyList<DcbTile> Tiles => _bar.Tiles;

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A latch held over the old view model ends there, its value put back, before the bar takes the new one.
        CancelLatch();
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

    private int MapsAvailable => Math.Min(_viewModel?.MapToggles.Count ?? 0, StarsMainPage.MapSlots);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => RebuildOrRefresh();

    private void OnMapTogglesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildOrRefresh();

    /// <summary>Rebuilds the tiles when a map slot gains or loses its map, which changes its kind; else redraws them.</summary>
    private void RebuildOrRefresh()
    {
        if (MapsAvailable != _mapsShown)
        {
            Rebuild();
        }
        else
        {
            Refresh();
        }
    }

    private void Rebuild()
    {
        _mapsShown = MapsAvailable;
        _bar.SetTiles([.. StarsMainPage.Entries.Select(BuildTile)]);
        Refresh();
    }

    /// <summary>The tile for <paramref name="entry"/>, wired to its action; a map slot with no map is disabled.</summary>
    private DcbTile BuildTile(StarsDcbEntry entry)
    {
        bool emptySlot = (entry.MapSlot >= 0) && (entry.MapSlot >= _mapsShown);
        var tile = new DcbTile(entry.Shape, emptySlot ? DcbTileKind.Disabled : entry.Kind, "");
        if (emptySlot || (entry.Kind == DcbTileKind.Disabled))
        {
            return tile;
        }

        if (entry.MapSlot >= 0)
        {
            tile.OnToggle = _ => PressMap(entry.MapSlot);
        }
        else if (entry.Button is { } id)
        {
            Wire(tile, id);
        }

        return tile;
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

    /// <summary>A spinner latched: Esc now goes to it, and its value at this moment is the one a cancel puts back.</summary>
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
    }

    /// <summary>The command that sets the spinner with <paramref name="id"/> to the value it shows now.</summary>
    private static string? RestoreCommand(ScopeViewModel vm, ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.Range => ScopeToolbarButtons.RangeCommand(vm.RangeNm),
            ScopeButtonId.RrSize => ScopeToolbarButtons.RrSizeCommand(vm.RangeRingSizeNm),
            _ => null,
        };

    private void EndLatch()
    {
        _latchedOn?.IsBarSpinnerLatched = false;
        _latched = null;
        _latchedOn = null;
        _restoreCommand = null;
    }

    /// <summary>
    /// Cancels the latch, if one is held: puts the value back on the view model it was latched on, unless it is already the
    /// value the latch took.
    /// </summary>
    private void CancelLatch()
    {
        (ScopeButtonId? id, ScopeViewModel? on, string? restore) = (_latched, _latchedOn, _restoreCommand);
        EndLatch();
        if ((on is { } vm) && (id is { } latched) && (restore is not null) && (restore != RestoreCommand(vm, latched)))
        {
            vm.RunCommand(restore);
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

    /// <summary>Redraws every tile's label and pushed state from the view model.</summary>
    private void Refresh()
    {
        for (int i = 0; i < _bar.Tiles.Count; i++)
        {
            RefreshTile(_bar.Tiles[i], StarsMainPage.Entries[i]);
        }
    }

    private void RefreshTile(DcbTile tile, StarsDcbEntry entry)
    {
        if (entry.MapSlot >= 0)
        {
            ScopeMapToggle? toggle = (entry.MapSlot < _mapsShown) ? _viewModel?.MapToggles[entry.MapSlot] : null;
            tile.Label = (toggle is null) ? "" : $"{toggle.StarsId}\n{toggle.ShortName}";
            tile.IsPushed = toggle?.IsOn ?? false;
            return;
        }

        ScopeButton? button = (entry.Button is { } id) ? ScopeToolbarButtons.Find(id) : null;
        tile.Label = LabelOf(entry, button);
        if ((tile.Kind == DcbTileKind.Toggle) && (button is not null) && (_viewModel is { } vm))
        {
            tile.IsPushed = vm.IsActive(button);
        }
    }

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
