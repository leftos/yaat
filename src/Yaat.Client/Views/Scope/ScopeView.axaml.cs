using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope: a scope canvas drawing traffic with the datablock template engine. A left click on a target or block
/// selects that aircraft; a right click opens the shared aircraft menu. A left click on an ERAM block's callsign (Field A)
/// also toggles the instructor's local dwell lock (<see cref="ScopeViewModel.ToggleDwellLock"/>). A left click on a field
/// with an action (<see cref="ScopeFieldAction"/>) selects the aircraft and opens that field's pick menu at the pointer
/// (<see cref="FieldMenus.ScopeFieldMenus"/>), the note box, or takes control (the EuroScope owner); a row of the menu sends,
/// or, picked with Shift held, stages <c>PILOT {callsign} {command}</c> in the entry (<see cref="ScopeViewModel.StageEntry"/>)
/// and gives the canvas the focus, so Enter runs it.
/// While the Rings tool is armed a left click places a ring instead (<see cref="ScopeViewModel.PlaceRing"/>), selecting
/// nothing, opening no menu and toggling no lock. Ctrl+Shift+T (Cmd+Shift+T on macOS) turns top-down mode on or off through <c>.scope tdm</c>,
/// as the TDM button does. Typing goes to the Scope's entry (<see cref="ScopeEntryLayer"/>) while the canvas has the focus,
/// which any press on it gives: printable text adds to the entry, Backspace edits it, Up and Down walk its history, Ctrl+R
/// (Cmd+R on macOS) recalls the last entry on ERAM and Mixed, and Enter runs it; other chords, Tab and function keys pass on.
/// Esc, in order: leaves a tear-off move to the tear-off layer, else clears the entry and its response, else disarms the Rings
/// tool. Esc and Ctrl+Shift+T are taken by a window-level handler that runs in the tunnel, so it acts before any control
/// inside the window sees the key.
/// </summary>
public partial class ScopeView : UserControl
{
    private ContextMenu? _activeContextMenu;
    private ScopeViewModel? _vm;
    private TopLevel? _topLevel;

    // Whether Shift was held at the last pointer press, release or key press inside the open field menu.
    private bool _fieldMenuShift;

    public ScopeView()
    {
        InitializeComponent();
        Canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        Canvas.AircraftRightClicked += OnAircraftRightClicked;
        Canvas.AircraftDwellLockToggleRequested += OnAircraftDwellLockToggleRequested;
        Canvas.FieldClicked += OnFieldClicked;
        Canvas.EntryTextInput += OnCanvasTextInput;
        Canvas.EntryKeyDown += OnCanvasKeyDown;
        Canvas.GotFocus += OnCanvasGotFocus;
    }

    // The canvas taking the focus makes the Scope the active measure surface: a typed .rbl A B places here.
    private void OnCanvasGotFocus(object? sender, FocusChangedEventArgs e) => (DataContext as ScopeViewModel)?.ActivateMeasureSurface();

    /// <summary>The aircraft menu a right click opened, while it is open. Test seam.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <summary>
    /// The aircraft menu for <paramref name="callsign"/>: the shared aircraft menu, with relative actions aimed at
    /// <paramref name="prevSelected"/>, with no view section of the scope's own.
    /// </summary>
    internal ContextMenu BuildAircraftMenu(ScopeViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        var host = new ClientMenuHost(vm.Main, ac, Canvas, vm.Main.Radar.MvaFacilityId);
        return AircraftMenuBuilder.Build(ac, new MenuClick(callsign, prevSelected, null, []), host, _ => []);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as ScopeViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Canvas.Preferences = _vm.Preferences;
        Canvas.DataBlockState = _vm.DataBlockState;
        PushMaps(_vm);
    }

    // The maps arrive later when the scope opened before the radar had any (its one-time seed).
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((sender is ScopeViewModel vm) && (e.PropertyName is nameof(ScopeViewModel.VideoMaps) or nameof(ScopeViewModel.BrightnessLookup)))
        {
            PushMaps(vm);
        }
    }

    private void PushMaps(ScopeViewModel vm)
    {
        Canvas.VideoMaps = vm.VideoMaps;
        Canvas.BrightnessLookup = vm.BrightnessLookup;
        Canvas.MarkDirty();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Esc, unless a tear-off move is on (the tear-off layer cancels it): takes the measure prompt with the measurement (when
    // nothing is typed), else clears the entry, else cancels the Scope's measurement, else disarms an armed Rings tool. Ctrl+M
    // arms the measuring tool; Ctrl+Shift+T flips top-down mode. Any other key, or Esc with nothing to clear, passes on.
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = (!TearoffLayer.IsMoving) && (vm.TakeMeasurePrompt() || vm.ClearEntry() || vm.CancelMeasure() || vm.DisarmRingTool());
        }
        else if ((e.Key == Key.M) && (e.KeyModifiers == ActionModifier))
        {
            vm.ArmMeasure();
            e.Handled = true;
        }
        else if ((e.Key == Key.T) && (e.KeyModifiers == (ActionModifier | KeyModifiers.Shift)))
        {
            vm.RunCommand(ScopeToolbarButtons.TdmCommand(!vm.IsTdm));
            e.Handled = true;
        }
    }

    private static KeyModifiers ActionModifier => PlatformHelper.IsMacOS ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnCanvasTextInput(object? sender, TextInputEventArgs e)
    {
        if ((DataContext is ScopeViewModel vm) && vm.TypeEntryText(e.Text))
        {
            e.Handled = true;
        }
    }

    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if ((DataContext is ScopeViewModel vm) && RunEntryKey(vm, e))
        {
            e.Handled = true;
        }
    }

    // An unmodified F3, F4, F5, F7 or F11 on STARS starts its function's entry; the same keys with a modifier pass on.
    private static bool RunStarsFunctionKey(ScopeViewModel vm, KeyEventArgs e) =>
        (e.KeyModifiers == KeyModifiers.None) && vm.StartStarsFunctionEntry(e.Key);

    // The entry's keys: a STARS function key, then the editing keys, where a chord with Ctrl, Alt or Meta other than the recall
    // passes on.
    private static bool RunEntryKey(ScopeViewModel vm, KeyEventArgs e) => RunStarsFunctionKey(vm, e) || RunEditingKey(vm, e);

    private static bool RunEditingKey(ScopeViewModel vm, KeyEventArgs e)
    {
        if ((e.Key == Key.R) && (e.KeyModifiers == ActionModifier))
        {
            return vm.RecallLastEntry();
        }

        if ((e.KeyModifiers != KeyModifiers.None) && (e.KeyModifiers != KeyModifiers.Shift))
        {
            return false;
        }

        return e.Key switch
        {
            Key.Enter => vm.ExecuteEntry(),
            Key.Back => vm.BackspaceEntry(),
            Key.Up => vm.WalkEntryHistory(older: true),
            Key.Down => vm.WalkEntryHistory(older: false),
            _ => false,
        };
    }

    private void OnAircraftLeftClicked(string callsign)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        if (vm.IsRingToolArmed)
        {
            vm.PlaceRing(callsign);
        }
        else
        {
            vm.SelectAircraft(callsign);
        }
    }

    // A field with an action: the armed Rings tool places a ring as on any click; else the track is selected and the field's
    // action runs, its menu sending through the same host as the right-click menu.
    private void OnFieldClicked(string callsign, ScopeFieldAction action, Point __)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        if (vm.IsRingToolArmed)
        {
            vm.PlaceRing(callsign);
            return;
        }

        vm.SelectAircraft(callsign);
        if (action == ScopeFieldAction.TakeControl)
        {
            _ = vm.Main.TakeControlAsync(callsign);
            return;
        }

        if (vm.FindAircraft(callsign) is not { } ac)
        {
            return;
        }

        if (action == ScopeFieldAction.OpenNoteBox)
        {
            ScopeFieldMenus.ShowNoteBox(ac, new ClientMenuHost(vm.Main, ac, Canvas, vm.Main.Radar.MvaFacilityId));
        }
        else
        {
            OpenFieldMenu(vm, ac, action);
        }
    }

    // The field's pick menu, whose rows stage their command in the entry instead of sending it while Shift is held on them.
    private void OpenFieldMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        _fieldMenuShift = false;
        var host = new ClientMenuHost(vm.Main, ac, Canvas, vm.Main.Radar.MvaFacilityId)
        {
            Stage = (callsign, command) => _fieldMenuShift && StageEntry(vm, callsign, command),
        };
        if (ScopeFieldMenus.Build(action, ac, host) is { } menu)
        {
            ScopeFieldMenus.RecordShift(menu, shift => _fieldMenuShift = shift);
            ShowContextMenu(menu);
        }
    }

    // Stages the row's command in the entry, closes the menu and gives the canvas the focus, so Enter runs the entry.
    private bool StageEntry(ScopeViewModel vm, string callsign, string command)
    {
        vm.StageEntry(callsign, command);
        _activeContextMenu?.Close();
        Canvas.Focus();
        return true;
    }

    private void OnAircraftDwellLockToggleRequested(string callsign)
    {
        if ((DataContext is ScopeViewModel vm) && !vm.IsRingToolArmed)
        {
            vm.ToggleDwellLock(callsign);
        }
    }

    private void OnAircraftRightClicked(string callsign, Point _)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        // As on the radar: keep the selected aircraft as the recipient of relative actions when a different aircraft is
        // right-clicked; adopt the right-clicked one only when nothing is selected or it is the selected one.
        AircraftModel? ac = vm.FindAircraft(callsign);
        AircraftModel? prevSelected = vm.SelectedAircraft;
        if ((ac is not null) && ((prevSelected is null) || string.Equals(prevSelected.Callsign, callsign, StringComparison.OrdinalIgnoreCase)))
        {
            vm.SelectedAircraft = ac;
        }

        ShowContextMenu(BuildAircraftMenu(vm, ac, prevSelected, callsign));
    }

    private void ShowContextMenu(ContextMenu menu)
    {
        _activeContextMenu?.Close();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (_activeContextMenu == menu)
            {
                _activeContextMenu = null;
            }
        };
        menu.PlacementTarget = Canvas;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(Canvas);
    }
}
