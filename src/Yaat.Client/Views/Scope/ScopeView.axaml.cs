using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The YAAT Scope: a scope canvas drawing traffic with the datablock template engine. A left click on a target or block
/// selects that aircraft; a right click opens the shared aircraft menu. Ctrl+click (Cmd+click on macOS) on a target, block
/// or indicator opens the flight-plan editor without selecting, and Ctrl+Shift leaves the press to Shift. A left click on
/// empty map clears the selection, except while the Rings or measuring tool is armed or when the click fills the entry. A
/// right click over two or more stacked targets opens a picker first. A left click on an ERAM block's callsign (Field A)
/// also toggles the instructor's local dwell lock (<see cref="ScopeViewModel.ToggleDwellLock"/>). A left click on a field
/// with an action (<see cref="ScopeFieldAction"/>) selects the aircraft and opens that field's pick menu at the pointer
/// (<see cref="FieldMenus.ScopeFieldMenus"/>), the note box, or takes control (the EuroScope owner); a row of the menu sends,
/// or, picked with Shift held, stages <c>PILOT {callsign} {command}</c> in the entry (<see cref="ScopeViewModel.StageEntry"/>)
/// and gives the canvas the focus, so Enter runs it. On an ERAM block the altitude opens the two-column popup
/// (<see cref="ScopeDataAltitudePopup"/>): a SCOPE DATA row runs its ERAM entry at once, or with Shift stages the bare entry
/// (<see cref="ScopeViewModel.StageEramText"/>); a PILOT row sends or stages as a menu row does. The HSF indicator and VCI cell
/// run their toggle, or with Shift stage it. Only one field menu, popup or aircraft menu is open at a time.
/// While the Rings tool is armed a left click places a ring instead (<see cref="ScopeViewModel.PlaceRing"/>), selecting
/// nothing, opening no menu and toggling no lock. With text in the preview (and the Rings tool not armed) a left click on a
/// target or block fills that track into the entry and selects it (<see cref="ScopeViewModel.FillTrack"/>), opening no menu
/// and toggling no lock, unless the entry already names that track (its pilot entry first, or any entry last), where the
/// click acts as with an empty preview (<see cref="ScopeViewModel.FillsOnLeftClick"/>); a left click on the map fills the
/// point (<see cref="ScopeViewModel.FillPoint"/>). A middle click takes the row <see cref="ScopeMiddleClick"/> picks: on an
/// ERAM block it fills the track into a non-empty preview and runs it, or with an empty preview runs <c>QF</c> on Field A and
/// acts as a plain left click on the HSF indicator, the VCI cell or a field with a menu;
/// on a STARS or EuroScope block it toggles the highlight; on the map with the ERAM layout it fills the point into a
/// non-empty preview and runs it. The measuring tool comes before every left-click rung above: Alt+left-drag measures between
/// the press and the release whether or not the tool is armed (an Alt-click anchors and arms it); while it is armed (Ctrl+M,
/// Cmd+M on macOS, which also says so in the response) a left click picks an endpoint, latching an aircraft under it, and a
/// right click cancels it, opening no menu; each pick's status shows in the response (<see cref="ScopeViewModel.PickMeasurePoint"/>).
/// Ctrl+Shift+T (Cmd+Shift+T on macOS) turns top-down mode on or off through
/// <c>.scope tdm</c>, as the TDM button does. Typing goes to the Scope's entry (<see cref="ScopeEntryLayer"/>) while the canvas has the focus,
/// which any press on it gives: printable text adds to the entry, Backspace edits it, Up and Down walk its history, Ctrl+R
/// (Cmd+R on macOS) recalls the last entry on ERAM and Mixed, and Enter runs it; other chords, Tab and function keys pass on.
/// Esc, in order: leaves a tear-off move to the tear-off layer, else takes the measuring tool's own prompt with the
/// measurement, else clears the entry and its response, else drops an Alt-drag in flight, else cancels the Scope's measurement,
/// else disarms the Rings tool.
/// Esc and Ctrl+Shift+T are taken by a window-level handler that runs in the tunnel, so it acts before any control
/// inside the window sees the key.
/// </summary>
public partial class ScopeView : UserControl
{
    private static readonly ILogger Log = AppLog.CreateLogger("ScopeView");

    private ContextMenu? _activeContextMenu;
    private ScopeDataPopupBase? _activeFieldPopup;
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
        Canvas.AircraftMiddleClicked += OnAircraftMiddleClicked;
        Canvas.MapMiddleClicked += OnMapMiddleClicked;
        Canvas.AircraftFillRequested += OnAircraftFillRequested;
        Canvas.MapFillRequested += OnMapFillRequested;
        Canvas.FillsOnLeftClick = callsign => (DataContext is ScopeViewModel vm) && vm.FillsOnLeftClick(callsign);
        Canvas.EntryTextInput += OnCanvasTextInput;
        Canvas.EntryKeyDown += OnCanvasKeyDown;
        Canvas.GotFocus += OnCanvasGotFocus;
        Canvas.MeasurePointPicked += endpoint => (DataContext as ScopeViewModel)?.PickMeasurePoint(endpoint);
        Canvas.MeasureDragCompleted += (from, to) => (DataContext as ScopeViewModel)?.PlaceMeasurement(from, to);
        Canvas.MeasureCancelled += () => (DataContext as ScopeViewModel)?.CancelMeasureFromPointer();
        WireFeatures();
    }

    // The canvas taking the focus makes the Scope the active measure surface: a typed .rbl A B places here.
    private void OnCanvasGotFocus(object? sender, FocusChangedEventArgs e) => (DataContext as ScopeViewModel)?.ActivateMeasureSurface();

    /// <summary>The aircraft menu a right click opened, while it is open. Test seam.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <summary>The ERAM altitude or fourth-line popup a field click opened, while it is open. Test seam.</summary>
    internal ScopeDataPopupBase? ActiveFieldPopup => _activeFieldPopup;

    /// <summary>
    /// The aircraft menu for <paramref name="callsign"/>: the shared aircraft menu, with relative actions aimed at
    /// <paramref name="prevSelected"/>, with the Scope's own view section (<see cref="BuildViewSection"/>) after the shared
    /// groups and "Settings for this view…" at its end.
    /// </summary>
    internal ContextMenu BuildAircraftMenu(ScopeViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        var host = new ClientMenuHost(vm.Main, ac, Canvas, vm.Main.ScopeMaps.MvaFacilityId);
        ContextMenu menu = AircraftMenuBuilder.Build(
            ac,
            new MenuClick(callsign, prevSelected, null, []),
            host,
            context => BuildViewSection(vm, ac, context)
        );
        AppendViewSettings(menu, vm);
        return menu;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;
        if (_vm is not null)
        {
            DetachFeatures(_vm);
        }

        _vm = DataContext as ScopeViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Canvas.Preferences = _vm.Preferences;
        Canvas.DataBlockState = _vm.DataBlockState;
        PushMaps(_vm);
        AttachFeatures(_vm);
    }

    // The maps arrive later, at each seed from the Scope's map source.
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
        _activeFieldPopup?.Close();
        _activeContextMenu?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    // Esc, unless a tear-off move is on (the tear-off layer cancels it), runs HandleEscape's chain.
    // Ctrl+M arms the measuring tool; Ctrl+Shift+T flips top-down mode. Any other key, or Esc with nothing to clear, passes on.
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScopeViewModel vm)
        {
            return;
        }

        if ((e.Key == Key.Escape) && (_activeFieldPopup is { IsOpen: true } popup))
        {
            // The popup's keys tunnel through the window first: Esc closes the popup, leaving the preview's text.
            popup.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = (!TearoffLayer.IsMoving) && HandleEscape(vm);
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
        if (RunKeyHooks(e))
        {
            return;
        }

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
    // action runs, its menu sending through the same host as the right-click menu. Shift stages an HSF or VCI toggle in the
    // preview instead of running it, and changes no menu's opening. Internal so a test can open a surface with no pointer press.
    internal void OnFieldClicked(string callsign, ScopeFieldAction action, Point __, KeyModifiers modifiers)
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

        if (action is ScopeFieldAction.ToggleHsf or ScopeFieldAction.ToggleVci)
        {
            RunOrStageEramText(vm, callsign, ScopeDataEntries.Toggle(action, callsign), modifiers.HasFlag(KeyModifiers.Shift));
            return;
        }

        if (vm.FindAircraft(callsign) is { } ac)
        {
            OpenFieldSurface(vm, ac, action);
        }
    }

    // The surface a field with a menu opens: the note box, the ERAM altitude popup, an ERAM fourth-line popup or the field's
    // pick menu.
    private void OpenFieldSurface(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        if (action == ScopeFieldAction.OpenNoteBox)
        {
            ScopeFieldMenus.ShowNoteBox(ac, new ClientMenuHost(vm.Main, ac, Canvas, vm.Main.ScopeMaps.MvaFacilityId));
        }
        else if (action == ScopeFieldAction.OpenEramAltitudeMenu)
        {
            OpenEramAltitudePopup(vm, ac, action);
        }
        else if (HsfKindFor(action) is { } kind)
        {
            OpenEramHsfPopup(vm, ac, kind, action);
        }
        else if (action == ScopeFieldAction.StartDrawRoute)
        {
            StartFieldDrawRoute(vm, ac);
        }
        else
        {
            OpenFieldMenu(vm, ac, action);
        }
    }

    // The fourth-line popup an ERAM HSF action opens; null for any other action.
    private static HsfPopupKind? HsfKindFor(ScopeFieldAction action) =>
        action switch
        {
            ScopeFieldAction.OpenEramHeadingMenu => HsfPopupKind.Heading,
            ScopeFieldAction.OpenEramSpeedMenu => HsfPopupKind.Speed,
            ScopeFieldAction.OpenEramFreeTextMenu => HsfPopupKind.FreeText,
            ScopeFieldAction.OpenEramHsfMenu => HsfPopupKind.Combined,
            _ => null,
        };

    // The host a field menu's pilot rows send through: while Shift is held on a row it stages the command in the entry instead.
    private ClientMenuHost FieldMenuHost(ScopeViewModel vm, AircraftModel ac) =>
        new(vm.Main, ac, Canvas, vm.Main.ScopeMaps.MvaFacilityId)
        {
            Stage = (callsign, command) => _fieldMenuShift && StageEntry(vm, callsign, command),
        };

    // The field's pick menu, whose rows stage their command in the entry instead of sending it while Shift is held on them.
    private void OpenFieldMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        _fieldMenuShift = false;
        if (ScopeFieldMenus.Build(action, ac, FieldMenuHost(vm, ac)) is { } menu)
        {
            ScopeFieldMenus.RecordShift(menu, shift => _fieldMenuShift = shift);
            ShowContextMenu(menu);
        }
    }

    // The ERAM altitude popup: a SCOPE DATA row runs its entry, or with Shift stages it; a PILOT row sends, or with Shift stages,
    // as a field menu's row does.
    private void OpenEramAltitudePopup(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        _fieldMenuShift = false;
        string callsign = ac.Callsign;
        var popup = new ScopeDataAltitudePopup(
            ac,
            FieldMenuHost(vm, ac),
            shift => _fieldMenuShift = shift,
            entry => PickScopeData(vm, callsign, entry)
        );
        ShowFieldPopup(popup, callsign, action);
    }

    // An ERAM fourth-line popup of the kind: its rows run or stage as the altitude popup's do.
    private void OpenEramHsfPopup(ScopeViewModel vm, AircraftModel ac, HsfPopupKind kind, ScopeFieldAction action)
    {
        _fieldMenuShift = false;
        string callsign = ac.Callsign;
        var popup = new ScopeDataHsfPopup(
            ac,
            FieldMenuHost(vm, ac),
            shift => _fieldMenuShift = shift,
            entry => PickScopeData(vm, callsign, entry),
            kind
        );
        ShowFieldPopup(popup, callsign, action);
    }

    // Closes any open aircraft menu or field popup, then opens the popup as the one open, until it closes: below the track's
    // block, or at the pointer when the frame draws none for it.
    private void ShowFieldPopup(ScopeDataPopupBase popup, string callsign, ScopeFieldAction action)
    {
        _activeContextMenu?.Close();
        _activeFieldPopup?.Close();
        Rect? block = Canvas.BlockBoundsOf(callsign);
        if (block is null)
        {
            Log.LogWarning("ScopeView: no block drawn for {Callsign}; the {Action} popup opens at the pointer", callsign, action);
        }

        if (!popup.Open(Canvas, block))
        {
            Log.LogWarning("ScopeView: no overlay layer for the canvas; the ERAM field popup for {Callsign} is not shown", callsign);
            return;
        }

        _activeFieldPopup = popup;
        popup.Closed += (_, _) =>
        {
            if (_activeFieldPopup == popup)
            {
                _activeFieldPopup = null;
            }
        };
    }

    // A SCOPE DATA row picked: the popup closes, then the entry runs, or with Shift is staged in the preview.
    private void PickScopeData(ScopeViewModel vm, string callsign, string entry)
    {
        bool stage = _fieldMenuShift;
        _activeFieldPopup?.Close();
        RunOrStageEramText(vm, callsign, entry, stage);
    }

    // A bare ERAM entry a click on the track produced: run at once, or with Shift staged in the preview with the track as the
    // pick, the canvas given the focus so Enter sends it.
    private void RunOrStageEramText(ScopeViewModel vm, string callsign, string entry, bool stage)
    {
        if (!stage)
        {
            _ = vm.RunEramText(callsign, entry);
            return;
        }

        vm.StageEramText(callsign, entry);
        Canvas.Focus();
    }

    // Stages the row's command in the entry, closes the menu and gives the canvas the focus, so Enter runs the entry.
    private bool StageEntry(ScopeViewModel vm, string callsign, string command)
    {
        vm.StageEntry(callsign, command);
        _activeContextMenu?.Close();
        _activeFieldPopup?.Close();
        Canvas.Focus();
        return true;
    }

    // A middle click on a track: the row its resolved block kind picks (ScopeMiddleClick). A field or indicator cell click acts
    // as a plain left click, Shift or not.
    private void OnAircraftMiddleClicked(string callsign, ScopeMiddleHit hit)
    {
        if ((DataContext is not ScopeViewModel vm) || (vm.FindAircraft(callsign) is not { } ac))
        {
            return;
        }

        switch (ScopeMiddleClick.OnTrack(vm.TemplateId, ac, hit, vm.EntryText.Length > 0, vm.IsRingToolArmed))
        {
            case ScopeMiddleClickAction.ToggleHighlight:
                vm.ToggleHighlight(callsign);
                break;
            case ScopeMiddleClickAction.FillTrackAndExecute:
                vm.FillTrackAndRun(callsign);
                break;
            case ScopeMiddleClickAction.RunQuickFlight:
                vm.RunQuickFlight(callsign);
                break;
            case ScopeMiddleClickAction.ClickField:
                OnFieldClicked(callsign, hit.Action, default, KeyModifiers.None);
                break;
            default:
                break;
        }
    }

    // A middle click on the map: with the ERAM layout and text in the preview, the point fills the entry and it runs.
    private void OnMapMiddleClicked(LatLon point)
    {
        if (
            (DataContext is ScopeViewModel vm)
            && (
                ScopeMiddleClick.OnMap(vm.IsEramEntryLayout, vm.EntryText.Length > 0, vm.IsRingToolArmed)
                == ScopeMiddleClickAction.FillPointAndExecute
            )
        )
        {
            vm.FillPoint(point, DescribePoint(vm, point));
            vm.ExecuteEntry();
        }
    }

    // A left click on a track with text in the preview: the track fills the entry (the second track of a MIN entry runs it) and
    // is selected.
    private void OnAircraftFillRequested(string callsign)
    {
        if (DataContext is ScopeViewModel vm)
        {
            vm.FillTrackFromClick(callsign);
            vm.SelectAircraft(callsign);
        }
    }

    // A left click on the map with text in the preview: the point fills the entry.
    private void OnMapFillRequested(LatLon point)
    {
        if (DataContext is ScopeViewModel vm)
        {
            vm.FillPoint(point, DescribePoint(vm, point));
        }
    }

    // The point's FRD from the Scope map source's fixes, as the aircraft menu names a point; null before they load.
    private string? DescribePoint(ScopeViewModel vm, LatLon point) =>
        new ClientMenuHost(vm.Main, null, Canvas, vm.Main.ScopeMaps.MvaFacilityId).DescribePoint(point);

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

    // One menu or popup at a time: opening one closes the other.
    private void ShowContextMenu(ContextMenu menu)
    {
        _activeFieldPopup?.Close();
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
