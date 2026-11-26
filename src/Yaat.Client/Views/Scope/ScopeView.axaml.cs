using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.EuroScopeFlyouts;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.FieldMenus.Crc;
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
/// or, picked with Shift held, stages <c>$ {callsign} {command}</c> in the entry (<see cref="ScopeViewModel.StageEntry"/>)
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
/// Esc, in order: leaves a view-card move to the floating-view layer, else takes the measuring tool's own prompt with the
/// measurement, else clears the entry and its response, else drops an Alt-drag in flight, else cancels the Scope's measurement,
/// else disarms the Rings tool.
/// Esc and Ctrl+Shift+T are taken by a window-level handler that runs in the tunnel, so it acts before any control
/// inside the window sees the key.
/// </summary>
public partial class ScopeView : UserControl
{
    private static readonly ILogger Log = AppLog.CreateLogger("ScopeView");

    private ContextMenu? _activeContextMenu;
    private IScopeFieldSurface? _activeFieldPopup;

    // The EuroScope scratchpad or handoff entry popup a field click opened, while it is open.
    private Popup? _activeEntryPopup;
    private ScopeViewModel? _vm;
    private UserPreferences? _watchedPrefs;
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
        Canvas.EntryTakesMeasurePress = () => (DataContext is ScopeViewModel vm) && vm.RangeBearingEntryTakesPress;
        Canvas.EntryTextInput += OnCanvasTextInput;
        Canvas.EntryKeyDown += OnCanvasKeyDown;
        Canvas.GotFocus += OnCanvasGotFocus;
        Canvas.MeasurePointPicked += endpoint => (DataContext as ScopeViewModel)?.PickMeasurePoint(endpoint);
        Canvas.MeasureDragCompleted += (from, to) => (DataContext as ScopeViewModel)?.PlaceMeasurement(from, to);
        Canvas.MeasureCancelled += () => (DataContext as ScopeViewModel)?.CancelMeasureFromPointer();
        WireFeatures();
    }

    // The canvas taking the focus makes the Scope the active measure surface, so a typed .rbl A B places here, and the
    // active Scope, so a typed Scope verb acts on it.
    private void OnCanvasGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is ScopeViewModel vm)
        {
            vm.Main.ActiveScope = vm;
            vm.ActivateMeasureSurface();
        }
    }

    /// <summary>The aircraft menu a right click opened, while it is open. Test seam.</summary>
    internal ContextMenu? ActiveContextMenu => _activeContextMenu;

    /// <summary>
    /// The surface a field click opened, while it is open: the ERAM altitude or fourth-line popup, or a CRC FDB menu. Test seam.
    /// </summary>
    internal IScopeFieldSurface? ActiveFieldPopup => _activeFieldPopup;

    /// <summary>The EuroScope scratchpad or handoff entry popup a field click opened, while it is open. Test seam.</summary>
    internal Popup? ActiveEntryPopup => _activeEntryPopup;

    /// <summary>
    /// The aircraft menu for <paramref name="callsign"/>: the shared aircraft menu, with relative actions aimed at
    /// <paramref name="prevSelected"/>, with the Scope's own view section (<see cref="BuildViewSection"/>) after the shared
    /// groups and "Settings for this view…" at its end.
    /// </summary>
    internal ContextMenu BuildAircraftMenu(ScopeViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        var host = new ClientMenuHost(vm.Main, ac, Canvas);
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
            _watchedPrefs?.FontSizesChanged -= ApplyFlyoutFontSize;
            _watchedPrefs?.ScopeTextChanged -= OnScopeTextChanged;
            _watchedPrefs = null;
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        WatchPreferences(_vm.Preferences);
        Canvas.Preferences = _vm.Preferences;
        Canvas.DataBlockState = _vm.DataBlockState;
        PushMaps(_vm);
        AttachFeatures(_vm);
    }

    // Watches the preferences this Scope draws from: the EuroScope tag flyouts take the "Radar Tag Flyouts" font size now and at
    // each font-size change, as the Radar view did, and a Scope text change rebuilds the frame.
    private void WatchPreferences(UserPreferences prefs)
    {
        _watchedPrefs?.FontSizesChanged -= ApplyFlyoutFontSize;
        _watchedPrefs?.ScopeTextChanged -= OnScopeTextChanged;
        _watchedPrefs = prefs;
        prefs.FontSizesChanged += ApplyFlyoutFontSize;
        prefs.ScopeTextChanged += OnScopeTextChanged;
        ApplyFlyoutFontSize();
    }

    // The STARS and ERAM datablock text settings are read at frame build, so a change rebuilds the frame.
    private void OnScopeTextChanged()
    {
        Canvas.DropCachedFrame();
        Canvas.MarkDirty();
    }

    private void ApplyFlyoutFontSize()
    {
        if (_watchedPrefs is { } prefs)
        {
            FlyoutAppearance.FontSize = prefs.RadarFlyoutFontSize;
        }
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
        if (_vm is not null)
        {
            WatchPreferences(_vm.Preferences);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _topLevel = null;
        _watchedPrefs?.FontSizesChanged -= ApplyFlyoutFontSize;
        _watchedPrefs?.ScopeTextChanged -= OnScopeTextChanged;
        _watchedPrefs = null;
        _activeFieldPopup?.Close();
        _activeEntryPopup?.Close();
        _activeContextMenu?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    // Esc while a DCB spinner is latched is the spinner's, which cancels it. An Esc that no popup takes while a DCB submenu is
    // open is the bar's, which returns to its Main page. Any other Esc, unless a view-card move is on (the floating-view
    // layer cancels it), runs HandleEscape's chain.
    // Ctrl+M arms the measuring tool; Ctrl+Shift+T flips top-down mode. Any other key, or Esc with nothing to clear, passes on.
    // Only while the Scope has the keyboard focus (IsKeyFocusInside): docked in the main window, Esc, Ctrl+M and
    // Ctrl+Shift+T typed in the command box or a grid stay theirs.
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsBarSpinnerEscape(e))
        {
            return;
        }

        if (TakeEntryPopupKey(e))
        {
            return;
        }

        if ((DataContext is not ScopeViewModel vm) || ((_activeFieldPopup is not { IsOpen: true }) && (!IsKeyFocusInside())))
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
            // While a DCB submenu is open the Esc is left unhandled for the bar's own handler, which returns to the Main page.
            if (!vm.IsBarSubmenuOpen)
            {
                e.Handled = (!TearoffLayer.IsMoving) && HandleEscape(vm);
            }
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

    // Esc while the Scope's DCB has a spinner latched: left unhandled, so it reaches the focused spinner, which cancels.
    private bool IsBarSpinnerEscape(KeyEventArgs e) => (e.Key == Key.Escape) && (DataContext is ScopeViewModel { IsBarSpinnerLatched: true });

    // While the EuroScope scratchpad or handoff popup is open its keys are its box's: Esc closes it, leaving the preview's text,
    // and no Scope shortcut runs. False while none is open.
    private bool TakeEntryPopupKey(KeyEventArgs e)
    {
        if (_activeEntryPopup is not { IsOpen: true } popup)
        {
            return false;
        }

        if (e.Key == Key.Escape)
        {
            popup.Close();
            e.Handled = true;
        }

        return true;
    }

    /// <summary>
    /// Whether the Scope's keys are its own now: always in a window of the Scope's own (its pop-out), else, docked in the
    /// main window, while the keyboard focus is on the Scope (its toolbar, view or dock) or inside it.
    /// </summary>
    internal bool IsKeyFocusInside()
    {
        if (_topLevel is not MainWindow)
        {
            return true;
        }

        Visual scope = (Visual?)this.FindAncestorOfType<ScopeHost>() ?? this;
        return (_topLevel?.FocusManager?.GetFocusedElement() is Visual focused) && ((focused == scope) || scope.IsVisualAncestorOf(focused));
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
    internal void OnFieldClicked(string callsign, ScopeFieldAction action, Point pos, KeyModifiers modifiers)
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
            OpenFieldSurface(vm, ac, action, pos);
        }
    }

    // The surface a field with an action opens: the note box, the ERAM altitude popup, an ERAM fourth-line popup, the elastic
    // heading vector, the draw route or the field's pick menu.
    private void OpenFieldSurface(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action, Point pos)
    {
        if (action == ScopeFieldAction.OpenNoteBox)
        {
            ScopeFieldMenus.ShowNoteBox(ac, new ClientMenuHost(vm.Main, ac, Canvas));
        }
        else if (action == ScopeFieldAction.OpenEramAltitudeMenu)
        {
            OpenEramAltitudeMenu(vm, ac, action);
        }
        else if (HsfKindFor(action) is { } kind)
        {
            OpenEramHsfMenu(vm, ac, kind, action);
        }
        else if (action == ScopeFieldAction.StartDrawRoute)
        {
            StartFieldDrawRoute(vm, ac);
        }
        else if (action == ScopeFieldAction.StartHeadingVector)
        {
            StartFieldHeadingVector(vm, ac, pos);
        }
        else
        {
            OpenFieldMenu(vm, ac, action, pos);
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
        new(vm.Main, ac, Canvas) { Stage = (callsign, command) => _fieldMenuShift && StageEntry(vm, callsign, command) };

    // The field's pick menu, whose rows stage their command in the entry instead of sending it while Shift is held on them. A
    // EuroScope tag opens its own flyout for the fields that have one (OpenEuroScopeFlyout); a STARS block's altitude and ground
    // speed open the menu of the field-menu style (OpenStarsStyledMenu).
    private void OpenFieldMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action, Point pos)
    {
        _fieldMenuShift = false;
        string kind = vm.TrackTemplateId(ac);
        if ((kind == ScopeTemplates.EuroScopeId) && OpenEuroScopeFlyout(vm, ac, action, pos))
        {
            return;
        }

        if ((kind == ScopeTemplates.StarsId) && OpenStarsStyledMenu(vm, ac, action))
        {
            return;
        }

        if (ScopeFieldMenus.Build(action, ac, FieldMenuHost(vm, ac)) is { } menu)
        {
            ScopeFieldMenus.RecordShift(menu, shift => _fieldMenuShift = shift);
            ShowContextMenu(menu);
        }
    }

    // The EuroScope tag's own flyout for the action, as the Radar view opened it: a pick menu for the altitude, speed, runway
    // and squawk, an entry popup for the scratchpad and handoff; false for an action with none. Its rows and buttons send
    // through the field menu's host, so Shift held on one stages its command as a field menu's row does.
    private bool OpenEuroScopeFlyout(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action, Point pos)
    {
        ClientMenuHost host = FieldMenuHost(vm, ac);
        if (EuroScopeMenu(action, ac, host) is { } menu)
        {
            ScopeFieldMenus.RecordShift(menu, shift => _fieldMenuShift = shift);
            ShowContextMenu(menu);
            return true;
        }

        if (EuroScopePopup(action, ac, host, pos) is { } popup)
        {
            ShowEntryPopup(popup);
            return true;
        }

        return false;
    }

    private static ContextMenu? EuroScopeMenu(ScopeFieldAction action, AircraftModel ac, IMenuHost host) =>
        action switch
        {
            ScopeFieldAction.OpenAltitudeMenu => EuroScopeAltitudeFlyout.Build(ac, host),
            ScopeFieldAction.OpenSpeedMenu => EuroScopeSpeedFlyout.Build(ac, host),
            ScopeFieldAction.OpenRunwayMenu => EuroScopeRunwayFlyout.Build(ac, host),
            ScopeFieldAction.OpenSquawkMenu => EuroScopeSquawkFlyout.Build(ac, host),
            _ => null,
        };

    private Popup? EuroScopePopup(ScopeFieldAction action, AircraftModel ac, IMenuHost host, Point pos) =>
        action switch
        {
            ScopeFieldAction.OpenScratchpadMenu => EuroScopeScratchpadFlyout.Build(Canvas, ac, host, ScratchpadSlotAt(pos)),
            ScopeFieldAction.OpenHandoffMenu => EuroScopeHandoffFlyout.Build(Canvas, ac, host),
            _ => null,
        };

    // The scratchpad the click hit: 2 on the tag's second scratchpad, else 1 (a click with no field under it included).
    private int ScratchpadSlotAt(Point pos) =>
        Canvas.CurrentFrame.FieldAt((float)pos.X, (float)pos.Y) is { Field.Slot: ScopeFieldSlot.Scratchpad2 } ? 2 : 1;

    // Closes any open menu or popup, then opens the entry popup on the canvas's overlay layer as the one open, until it closes.
    // Shift held at a press or key inside it is recorded, so a button or Enter stages through the field menu's host.
    private void ShowEntryPopup(Popup popup)
    {
        _activeContextMenu?.Close();
        _activeFieldPopup?.Close();
        _activeEntryPopup?.Close();
        if (OverlayLayer.GetOverlayLayer(Canvas) is not { } overlay)
        {
            Log.LogWarning("ScopeView: no overlay layer for the canvas; the EuroScope entry popup is not shown");
            return;
        }

        if (popup.Child is { } content)
        {
            ScopeFieldMenus.RecordShift(content, shift => _fieldMenuShift = shift);
        }

        overlay.Children.Add(popup);
        _activeEntryPopup = popup;
        popup.Closed += (_, _) =>
        {
            overlay.Children.Remove(popup);
            if (_activeEntryPopup == popup)
            {
                _activeEntryPopup = null;
            }
        };
        popup.IsOpen = true;
    }

    // The ERAM altitude menu the "ERAM field menus" setting picks: CRC's simple menu, or the popup tabbed or side by side.
    private void OpenEramAltitudeMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        ScopeFieldMenuStyle style = vm.Main.Preferences.ScopeFieldMenuStyle;
        if (style == ScopeFieldMenuStyle.Simple)
        {
            OpenCrcAltitudeMenu(vm, ac, action);
            return;
        }

        OpenEramAltitudePopup(vm, ac, action, style);
    }

    // An ERAM fourth-line menu: heading and speed as the setting picks, as the altitude's; free text and the HSF fallback their
    // side-by-side popup whatever the setting says.
    private void OpenEramHsfMenu(ScopeViewModel vm, AircraftModel ac, HsfPopupKind kind, ScopeFieldAction action)
    {
        ScopeFieldMenuStyle style = vm.Main.Preferences.ScopeFieldMenuStyle;
        if (kind is not (HsfPopupKind.Heading or HsfPopupKind.Speed))
        {
            OpenEramHsfPopup(vm, ac, kind, action, null);
        }
        else if (style != ScopeFieldMenuStyle.Simple)
        {
            OpenEramHsfPopup(vm, ac, kind, action, style);
        }
        else if (kind == HsfPopupKind.Heading)
        {
            OpenCrcHeadingMenu(vm, ac, action);
        }
        else
        {
            OpenCrcSpeedMenu(vm, ac, action);
        }
    }

    // CRC's altitude menu, its chip on the remembered mode: on ERAM a pick runs CRC's entries, on PILOT it sends the climb or
    // descent (CrcMenuCommands).
    private void OpenCrcAltitudeMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        var menu = new CrcAltitudeMenu(ac.Callsign, CrcMenuCommands.AltitudeValues(ac), CrcModeNow(vm), showModeChip: true);
        menu.Picked += pick =>
            ActOnCrcPick(
                vm,
                ac,
                (menu.Mode == CrcMenuMode.Eram)
                    ? CrcPickAction.Eram(CrcMenuCommands.EramAltitude(pick, ac), pick.Area, pick.Shift)
                    : CrcPickAction.Pilot(CrcMenuCommands.PilotAltitude(pick, ac.AltitudeFeet, ac.AssignedAltitude), pick.Area, pick.Shift)
            );
        ShowCrcMenu(menu, ac.Callsign, action, Canvas.FdbLineMiddleY(ac.Callsign, menu.FdbAlignLine));
    }

    // CRC's heading menu, as the altitude menu (CrcMenuCommands.EramHeading, PilotHeading).
    private void OpenCrcHeadingMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        var menu = new CrcHeadingMenu(ac.Callsign, CrcMenuCommands.HeadingValues(ac), CrcModeNow(vm), showModeChip: true);
        menu.Picked += pick =>
            ActOnCrcPick(
                vm,
                ac,
                (menu.Mode == CrcMenuMode.Eram)
                    ? CrcPickAction.Eram([CrcMenuCommands.EramHeading(pick, ac.Callsign)], pick.Area, pick.Shift)
                    : CrcPickAction.Pilot(CrcMenuCommands.PilotHeading(pick), pick.Area, pick.Shift)
            );
        ShowCrcMenu(menu, ac.Callsign, action, Canvas.FdbLineMiddleY(ac.Callsign, menu.FdbAlignLine));
    }

    // CRC's speed menu, as the altitude menu (CrcMenuCommands.EramSpeed, PilotSpeed).
    private void OpenCrcSpeedMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        var menu = new CrcSpeedMenu(ac.Callsign, CrcMenuCommands.SpeedValues(ac), CrcModeNow(vm), showModeChip: true);
        menu.Picked += pick =>
            ActOnCrcPick(
                vm,
                ac,
                (menu.Mode == CrcMenuMode.Eram)
                    ? CrcPickAction.Eram([CrcMenuCommands.EramSpeed(pick, ac.Callsign)], pick.Area, pick.Shift)
                    : CrcPickAction.Pilot(CrcMenuCommands.PilotSpeed(pick), pick.Area, pick.Shift)
            );
        ShowCrcMenu(menu, ac.Callsign, action, Canvas.FdbLineMiddleY(ac.Callsign, menu.FdbAlignLine));
    }

    // A CRC menu as the one open surface, its list level with `alignY` (CrcMenuPopup); at the pointer when that is null.
    private void ShowCrcMenu(CrcFdbMenu menu, string callsign, ScopeFieldAction action, double? alignY) =>
        ShowFieldPopup(new CrcMenuPopup(menu, alignY), callsign, action);

    // The field a STARS CRC-style menu aligns with: the altitude or the ground speed, whatever line the drawn block puts it on
    // (line 2 of a full block, line 1 of a limited or partial one).
    private static ScopeFieldSlot StarsSlotFor(ScopeFieldAction action) =>
        (action == ScopeFieldAction.OpenAltitudeMenu) ? ScopeFieldSlot.Altitude : ScopeFieldSlot.GroundSpeed;

    // The vertical middle of the callsign's drawn field in the slot, in canvas coordinates; null when the frame draws none.
    private double? FieldMiddleY(string callsign, ScopeFieldSlot slot) => FieldMiddleY(Canvas.CurrentFrame, callsign, slot);

    /// <summary>
    /// The vertical middle of <paramref name="callsign"/>'s field in <paramref name="slot"/> as <paramref name="frame"/> draws it,
    /// on whatever line its block puts it; null when the frame draws no such field.
    /// </summary>
    internal static double? FieldMiddleY(ScopeFrame frame, string callsign, ScopeFieldSlot slot) =>
        frame
            .Tracks.Where(t => t.Aircraft.Callsign == callsign)
            .SelectMany(t => t.Block.Fields)
            .Where(f => f.Slot == slot)
            .Select(f => (double?)f.Rect.MidY)
            .FirstOrDefault();

    // A STARS block's altitude or ground speed with the "STARS field menus" setting on, in the "ERAM field menus" style: Tabbed
    // opens a popup with one PILOT tab, Simple CRC's compact menu in STARS colours, PILOT picks only. False for Side by side and
    // any other field, which open their catalog menu as before, and for an altitude with no PILOT panel to show.
    private bool OpenStarsStyledMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        if (action is not (ScopeFieldAction.OpenAltitudeMenu or ScopeFieldAction.OpenSpeedMenu))
        {
            return false;
        }

        bool altitude = (action == ScopeFieldAction.OpenAltitudeMenu);
        switch (vm.Main.Preferences.ScopeFieldMenuStyle)
        {
            case ScopeFieldMenuStyle.Tabbed:
                return OpenStarsPilotPopup(vm, ac, action, altitude ? StarsPilotPopupKind.Altitude : StarsPilotPopupKind.Speed);
            case ScopeFieldMenuStyle.Simple when altitude:
                OpenStarsCrcAltitudeMenu(vm, ac, action);
                return true;
            case ScopeFieldMenuStyle.Simple:
                OpenStarsCrcSpeedMenu(vm, ac, action);
                return true;
            default:
                return false;
        }
    }

    // The STARS tabbed popup: its PILOT panel sends, or with Shift stages, as a field menu's row does; it remembers no column.
    private bool OpenStarsPilotPopup(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action, StarsPilotPopupKind kind)
    {
        var hooks = new ScopeDataPopupHooks(shift => _fieldMenuShift = shift, _ => { });
        if (ScopeStarsPilotPopup.Create(ac, FieldMenuHost(vm, ac), hooks, kind) is not { } popup)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("ScopeView: no PILOT {Kind} panel for {Callsign}; the catalog menu opens instead", kind, ac.Callsign);
            }

            return false;
        }

        ShowFieldPopup(popup, ac.Callsign, action);
        return true;
    }

    // CRC's compact altitude menu in STARS colours, PILOT only: a pick sends the climb or descent (CrcMenuCommands.PilotAltitude).
    private void OpenStarsCrcAltitudeMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        var menu = new CrcAltitudeMenu(ac.Callsign, CrcMenuCommands.AltitudeValues(ac), CrcPalette.Stars);
        menu.Picked += pick =>
            SendStarsPilotPick(vm, ac, CrcMenuCommands.PilotAltitude(pick, ac.AltitudeFeet, ac.AssignedAltitude), pick.Area, pick.Shift);
        ShowCrcMenu(menu, ac.Callsign, action, FieldMiddleY(ac.Callsign, StarsSlotFor(action)));
    }

    // CRC's compact speed menu in STARS colours, PILOT only: a pick sends the speed (CrcMenuCommands.PilotSpeed).
    private void OpenStarsCrcSpeedMenu(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action)
    {
        var menu = new CrcSpeedMenu(ac.Callsign, CrcMenuCommands.SpeedValues(ac), CrcPalette.Stars);
        menu.Picked += pick => SendStarsPilotPick(vm, ac, CrcMenuCommands.PilotSpeed(pick), pick.Area, pick.Shift);
        ShowCrcMenu(menu, ac.Callsign, action, FieldMiddleY(ac.Callsign, StarsSlotFor(action)));
    }

    // A STARS CRC-style menu pick: the pilot command sent, or with Shift staged, the menu closing either way; the remembered
    // ERAM | PILOT mode is the ERAM menus' and stays as it was. A pick with no pilot command leaves the menu open.
    private void SendStarsPilotPick(ScopeViewModel vm, AircraftModel ac, string? command, Enum area, bool stage)
    {
        if (command is null)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("ScopeView: the STARS pick {Area} for {Callsign} has no pilot command", area, ac.Callsign);
            }

            return;
        }

        SendOrStagePilot(vm, ac, command, stage);
    }

    // The chip mode a CRC menu opens on: the remembered one.
    private static CrcMenuMode CrcModeNow(ScopeViewModel vm) =>
        (vm.Main.Preferences.ScopeFieldMenuLastMode == ScopeFieldMenuMode.Pilot) ? CrcMenuMode.Pilot : CrcMenuMode.Eram;

    // What a CRC menu pick does: in its mode, the ERAM entries to run in order or the one pilot command to send (none for a pick
    // with no pilot command), staged with Shift; the pick area names it in the log.
    private sealed record CrcPickAction(ScopeFieldMenuMode Mode, IReadOnlyList<string> Commands, Enum Area, bool Stage)
    {
        public static CrcPickAction Eram(IReadOnlyList<string> entries, Enum area, bool stage) => new(ScopeFieldMenuMode.Eram, entries, area, stage);

        public static CrcPickAction Pilot(string? command, Enum area, bool stage) =>
            new(ScopeFieldMenuMode.Pilot, (command is null) ? [] : [command], area, stage);
    }

    // A CRC menu pick: the menu closes and the mode is remembered, then the ERAM entries run (with Shift the first is staged), or
    // the pilot command is sent (with Shift staged). A pick with nothing to send leaves the menu open and the mode as it was.
    private void ActOnCrcPick(ScopeViewModel vm, AircraftModel ac, CrcPickAction act)
    {
        if (act.Commands.Count == 0)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("ScopeView: the CRC {Mode} pick {Area} for {Callsign} has nothing to send", act.Mode, act.Area, ac.Callsign);
            }

            return;
        }

        vm.Main.Preferences.SetScopeFieldMenuLastMode(act.Mode);
        if (act.Mode == ScopeFieldMenuMode.Eram)
        {
            _activeFieldPopup?.Close();
            _activeEntryPopup?.Close();
            RunCrcEramEntries(vm, ac.Callsign, act.Commands, act.Stage);
        }
        else
        {
            SendOrStagePilot(vm, ac, act.Commands[0], act.Stage);
        }
    }

    // A CRC menu's ERAM entries: staged (the first alone) with Shift, else run one after another.
    private void RunCrcEramEntries(ScopeViewModel vm, string callsign, IReadOnlyList<string> entries, bool stage)
    {
        if (stage || (entries.Count == 1))
        {
            RunOrStageEramText(vm, callsign, entries[0], stage);
            return;
        }

        _ = RunEramEntriesAsync(vm, callsign, entries);
    }

    private static async Task RunEramEntriesAsync(ScopeViewModel vm, string callsign, IReadOnlyList<string> entries)
    {
        foreach (string entry in entries)
        {
            await vm.RunEramText(callsign, entry);
        }
    }

    // A pilot command from a CRC menu: staged in the entry with Shift, else sent for the aircraft; the menu closes either way.
    private void SendOrStagePilot(ScopeViewModel vm, AircraftModel ac, string command, bool stage)
    {
        if (stage)
        {
            StageEntry(vm, ac.Callsign, command);
            return;
        }

        _activeFieldPopup?.Close();
        _activeEntryPopup?.Close();
        var host = new ClientMenuHost(vm.Main, ac, Canvas);
        _ = host.SendAsync(ac.Callsign, command, host.Session.Initials);
    }

    // What a field popup tells this view: Shift, and a SCOPE DATA pick runs or stages its entry. With a style (an altitude,
    // heading or speed popup) the column of each send or stage is remembered, and a Tabbed popup opens on the remembered one.
    private ScopeDataPopupHooks FieldPopupHooks(ScopeViewModel vm, string callsign, ScopeFieldMenuStyle? style)
    {
        var hooks = new ScopeDataPopupHooks(shift => _fieldMenuShift = shift, entry => PickScopeData(vm, callsign, entry));
        if (style is not { } chosen)
        {
            return hooks;
        }

        UserPreferences preferences = vm.Main.Preferences;
        return hooks with
        {
            TabbedOpening = (chosen == ScopeFieldMenuStyle.Tabbed) ? preferences.ScopeFieldMenuLastMode : null,
            ModeSent = preferences.SetScopeFieldMenuLastMode,
        };
    }

    // The ERAM altitude popup in the style: a SCOPE DATA row runs its entry, or with Shift stages it; a PILOT row sends, or with
    // Shift stages, as a field menu's row does.
    private void OpenEramAltitudePopup(ScopeViewModel vm, AircraftModel ac, ScopeFieldAction action, ScopeFieldMenuStyle style)
    {
        _fieldMenuShift = false;
        string callsign = ac.Callsign;
        var popup = new ScopeDataAltitudePopup(ac, FieldMenuHost(vm, ac), FieldPopupHooks(vm, callsign, style));
        ShowFieldPopup(popup, callsign, action);
    }

    // An ERAM fourth-line popup of the kind, in the style (none: side by side, remembering nothing): its rows run or stage as
    // the altitude popup's do.
    private void OpenEramHsfPopup(ScopeViewModel vm, AircraftModel ac, HsfPopupKind kind, ScopeFieldAction action, ScopeFieldMenuStyle? style)
    {
        _fieldMenuShift = false;
        string callsign = ac.Callsign;
        var popup = new ScopeDataHsfPopup(ac, FieldMenuHost(vm, ac), FieldPopupHooks(vm, callsign, style), kind);
        ShowFieldPopup(popup, callsign, action);
    }

    // Closes any open aircraft menu or field popup, then opens the popup as the one open, until it closes: below the track's
    // block, or at the pointer when the frame draws none for it.
    private void ShowFieldPopup(IScopeFieldSurface popup, string callsign, ScopeFieldAction action)
    {
        _activeContextMenu?.Close();
        _activeFieldPopup?.Close();
        _activeEntryPopup?.Close();
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
        _activeEntryPopup?.Close();
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
        _activeEntryPopup?.Close();
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

        switch (ScopeMiddleClick.OnTrack(vm.TrackTemplateId(ac), hit, vm.EntryText.Length > 0, vm.IsRingToolArmed))
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
    private string? DescribePoint(ScopeViewModel vm, LatLon point) => new ClientMenuHost(vm.Main, null, Canvas).DescribePoint(point);

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
        _activeEntryPopup?.Close();
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
