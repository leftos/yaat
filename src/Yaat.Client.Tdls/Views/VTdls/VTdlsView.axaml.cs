using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Yaat.Client.Find;
using Yaat.Client.Services;
using Yaat.Client.Tdls.Services;
using Yaat.Client.Tdls.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Tdls.Views.VTdls;

/// <summary>
/// Code-behind for the vTDLS view. Handles user input the XAML can't express
/// cleanly: keyboard shortcuts (F4 Dump, F10 close-editor, F12 Send, and upstream's
/// keyboard model: Up/Down highlight, Enter open, Tab/backtick lists and fields, Esc
/// Facility Menu, Ctrl+Alt+Left/Right facility), facility
/// switcher MenuFlyout, and the Zulu-clock tick. The view never mutates state
/// directly — every action funnels through <see cref="VTdlsViewModel"/> which
/// emits canonical commands.
/// </summary>
public partial class VTdlsView : UserControl
{
    private static readonly ILogger Log = SimLog.CreateLogger("VTdlsView");

    /// <summary>
    /// The wall clock behind the footer's Zulu clock. <see cref="TimeProvider.System"/> in the apps; the user-guide
    /// screenshot harness pins it so its captures come out identical run to run.
    /// </summary>
    public static TimeProvider WallClock { get; set; } = TimeProvider.System;

    private DispatcherTimer? _clockTimer;

    // Shared in-view Find (Ctrl+F). Snapshot = DCL then PDC items; scrollTo brings the
    // matched row into view in whichever list holds it. Its DataContext drives the FindBar.
    private readonly FindController _findController;

    /// <summary>Test hook: the in-view Find controller backing the FindBar overlay.</summary>
    internal FindController FindController => _findController;

    /// <summary>Test hook: the rendered footer status line, read off the live control rather than the view-model.</summary>
    internal string? FooterStatusText => FooterStatus.Text;

    /// <summary>Test hook: whether the footer status carries the warning class that repaints it.</summary>
    internal bool FooterStatusIsWarning => FooterStatus.Classes.Contains("warning");

    public VTdlsView()
    {
        InitializeComponent();
        // Light until a view-model says otherwise.
        ApplyTheme(dark: false);

        _findController = new FindController(BuildFindSnapshot, ScrollFindMatchIntoView);
        FindBar.DataContext = _findController;

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        KeyDown += OnKeyDown;
        // Tunnelled, so the list and navigation keys reach the view before a focused ListBox selects a row with them
        // (which would open its editor) or the window moves focus with Tab.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        DclListBox.ContainerPrepared += OnRowContainerPrepared;
        PdcListBox.ContainerPrepared += OnRowContainerPrepared;
        DataContextChanged += OnDataContextChanged;
    }

    // ── Keyboard model (upstream vTDLS): highlight, Enter to open, Tab between lists ──

    private const string HighlightedClass = "highlighted";

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsViewKey(e, out VTdlsViewModel? vm))
        {
            return;
        }
        if (IsFacilityCycleKey(e))
        {
            e.Handled = true;
            await vm.CycleFacilityAsync((e.Key == Key.Right) ? 1 : -1);
            return;
        }
        if (vm.Editor is { } editor)
        {
            HandleEditorKeys(e, editor);
        }
        else
        {
            HandleListKeys(e, vm);
        }
    }

    /// <summary>
    /// True when the key belongs to this view's keyboard model, giving the view model that drives it: it came from
    /// inside this view, a view model is present, and the list-editing find bar does not hold focus.
    /// </summary>
    private bool IsViewKey(KeyEventArgs e, [NotNullWhen(true)] out VTdlsViewModel? vm)
    {
        vm = null;
        // A popup passes its events to the window it belongs to, so keys typed in the Facility Menu route through the
        // Facility button and this view; the menu's contents are a separate visual tree, and their keys are the menu's.
        if ((e.Source is not Visual source) || ((source != this) && !this.IsVisualAncestorOf(source)))
        {
            return false;
        }
        if ((DataContext is not VTdlsViewModel viewModel) || (_findController.IsVisible && FindBar.IsKeyboardFocusWithin))
        {
            return false;
        }
        vm = viewModel;
        return true;
    }

    private static bool IsFacilityCycleKey(KeyEventArgs e) =>
        (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt)) && (e.Key is Key.Left or Key.Right);

    /// <summary>Tab steps forward, Shift+Tab back; backtick, upstream's other cycling key, steps back.</summary>
    private static int CycleStep(KeyEventArgs e) => ((e.Key == Key.Tab) && (e.KeyModifiers != KeyModifiers.Shift)) ? 1 : -1;

    private static bool HasNoOrShiftModifier(KeyEventArgs e) => e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift;

    /// <summary>
    /// With no editor open: Up/Down move the highlight, Enter opens it, Tab and backtick cycle the active list. The
    /// list's other navigation keys are swallowed: a focused ListBox would select a row with them, which opens its editor.
    /// </summary>
    private static void HandleListKeys(KeyEventArgs e, VTdlsViewModel vm)
    {
        if (!HasNoOrShiftModifier(e))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                vm.MoveHighlight(1);
                break;
            case Key.Up:
                vm.MoveHighlight(-1);
                break;
            case Key.Enter:
                e.Handled = vm.OpenHighlighted();
                return;
            case Key.Tab or Key.OemTilde:
                vm.CycleActiveList(CycleStep(e));
                break;
            case Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// With an editor open: Tab and backtick move focus through its fields in upstream's order (Shift+Tab and backtick
    /// backwards), and Left/Right are swallowed, as upstream's are, so they never change a dropdown.
    /// </summary>
    private void HandleEditorKeys(KeyEventArgs e, TdlsFlightPlanEditorViewModel editor)
    {
        if (!HasNoOrShiftModifier(e))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Tab or Key.OemTilde:
                e.Handled = true;
                FocusNextEditorField(editor, CycleStep(e));
                break;
            case Key.Left or Key.Right:
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    /// <summary>The editor's fields in upstream's Tab order, which is not their layout order (Expect sits above SID).</summary>
    private ComboBox[] EditorFields =>
        [SidCombo, TransitionCombo, ClimboutCombo, ClimbviaCombo, InitialAltCombo, DepFreqCombo, ExpectCombo, ContactInfoCombo, LocalInfoCombo];

    /// <summary>
    /// Moves focus <paramref name="step"/> fields on from the focused one, wrapping; from outside the fields, forward
    /// starts at SID and backward at Local info. A read-only review has no field to focus.
    /// </summary>
    private void FocusNextEditorField(TdlsFlightPlanEditorViewModel editor, int step)
    {
        if (editor.IsReadOnly)
        {
            return;
        }
        ComboBox[] fields = EditorFields;
        int index = Array.IndexOf(fields, TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());
        int next = (index >= 0) ? VTdlsViewModel.Wrap(index + step, fields.Length) : ((step > 0) ? 0 : fields.Length - 1);
        fields[next].Focus(NavigationMethod.Tab);
    }

    /// <summary>
    /// An editable editor opens with SID focused, as upstream's does. A closed one hands focus back to the view when it
    /// held it, so the list keys keep working (Enter, F12, Enter...) without a click.
    /// </summary>
    private void OnEditorShown(TdlsFlightPlanEditorViewModel? editor)
    {
        if (editor is { IsEditable: true })
        {
            SidCombo.Focus(NavigationMethod.Tab);
            return;
        }
        IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        bool focusInView = (focused is null) || ((focused is Visual visual) && this.IsVisualAncestorOf(visual));
        if ((editor is null) && focusInView && !FindBar.IsKeyboardFocusWithin)
        {
            Focus();
        }
    }

    private void OnRowContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        object? item = (sender as ItemsControl)?.ItemFromContainer(e.Container);
        e.Container.Classes.Set(HighlightedClass, (item is not null) && ReferenceEquals(item, (DataContext as VTdlsViewModel)?.HighlightedItem));
    }

    /// <summary>Marks the highlighted row, and only it, in both lists; the list styles draw the mark.</summary>
    private void RefreshRowHighlights()
    {
        TdlsItemViewModel? highlighted = (DataContext as VTdlsViewModel)?.HighlightedItem;
        foreach (ListBox list in (ListBox[])[DclListBox, PdcListBox])
        {
            foreach (Control row in list.GetRealizedContainers())
            {
                row.Classes.Set(HighlightedClass, (highlighted is not null) && ReferenceEquals(list.ItemFromContainer(row), highlighted));
            }
        }
    }

    private VTdlsViewModel? _trackedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged -= OnVmPropertyChanged;
            _trackedVm.DclItems.CollectionChanged -= OnFindItemsChanged;
            _trackedVm.PdcItems.CollectionChanged -= OnFindItemsChanged;
        }
        _trackedVm = DataContext as VTdlsViewModel;
        if (_trackedVm is not null)
        {
            _trackedVm.PropertyChanged += OnVmPropertyChanged;
            _trackedVm.DclItems.CollectionChanged += OnFindItemsChanged;
            _trackedVm.PdcItems.CollectionChanged += OnFindItemsChanged;
            ApplyTheme(_trackedVm.IsDarkMode);
        }
        _findController.Refresh();
    }

    // ── In-view Find (Ctrl+F) ───────────────────────────────────

    private void OnFindItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => _findController.Refresh();

    /// <summary>Searchable rows in display order: DCL list then PDC list (CPDLC is always empty).</summary>
    private IReadOnlyList<IFindableItem> BuildFindSnapshot()
    {
        var result = new List<IFindableItem>();
        if (DataContext is VTdlsViewModel vm)
        {
            result.AddRange(vm.DclItems);
            result.AddRange(vm.PdcItems);
        }
        return result;
    }

    private void ScrollFindMatchIntoView(IFindableItem item)
    {
        if (item is not TdlsItemViewModel target)
        {
            return;
        }
        Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is not VTdlsViewModel vm)
                {
                    return;
                }
                if (vm.DclItems.Contains(target))
                {
                    DclListBox.ScrollIntoView(target);
                }
                else if (vm.PdcItems.Contains(target))
                {
                    PdcListBox.ScrollIntoView(target);
                }
            },
            DispatcherPriority.Loaded
        );
    }

    /// <summary>Handles the Find keys (Ctrl+F / F3 / Shift+F3 / Esc); returns true if consumed.</summary>
    private bool HandleFindKeys(KeyEventArgs e)
    {
        // Exact modifiers: Ctrl+Shift+F is the favorites-bar window hotkey, not Find.
        bool ctrl = e.KeyModifiers == KeyModifiers.Control;
        bool shift = e.KeyModifiers == KeyModifiers.Shift;

        if (ctrl && (e.Key == Key.F))
        {
            _findController.Open();
            FindBar.FocusInput();
            e.Handled = true;
            return true;
        }
        if ((e.Key == Key.F3) && (shift || (e.KeyModifiers == KeyModifiers.None)))
        {
            if (shift)
            {
                _findController.Previous();
            }
            else
            {
                _findController.Next();
            }
            e.Handled = true;
            return true;
        }
        if ((e.Key == Key.Escape) && _findController.IsVisible)
        {
            _findController.Close();
            Focus();
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not VTdlsViewModel vm)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(VTdlsViewModel.IsDarkMode):
                Dispatcher.UIThread.Post(() => ApplyTheme(vm.IsDarkMode));
                break;
            case nameof(VTdlsViewModel.HighlightedItem):
                RefreshRowHighlights();
                break;
            case nameof(VTdlsViewModel.Editor):
                // Loaded: after the editor's visibility binding and layout have run, so its fields can take focus.
                Dispatcher.UIThread.Post(() => OnEditorShown(vm.Editor), DispatcherPriority.Loaded);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Swaps the runtime brush values backing the view's <c>UserControl.Resources</c>
    /// dictionary. Avalonia <c>DynamicResource</c> bindings re-evaluate when
    /// the dictionary entry is replaced, so every panel/border/label that pulls a
    /// VTdls* brush updates automatically. Light = the realistic upstream palette
    /// (white panels, green list headers); Dark = darker variant for use alongside
    /// YAAT's other dark views. The controller toggles via the Facility Menu.
    /// The view also pins its own theme variant to the mode, so the Fluent parts it
    /// does not repaint (focus states, scroll bars, the Ops Config flyout) follow the
    /// view rather than the host: the desktop app runs Fluent Dark, the web app Light.
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        // Avalonia shares one RequestedThemeVariant property across every styled element and exposes it
        // publicly only through ThemeVariantScope's owner field.
        SetValue(ThemeVariantScope.RequestedThemeVariantProperty, dark ? ThemeVariant.Dark : ThemeVariant.Light);
        if (dark)
        {
            Resources["VTdlsChromeBg"] = new SolidColorBrush(Color.Parse("#0a0a0a"));
            Resources["VTdlsListHeaderBg"] = new SolidColorBrush(Color.Parse("#5fa05f"));
            Resources["VTdlsListHeaderActiveBg"] = new SolidColorBrush(Color.Parse("#1c8a1c"));
            // Keyboard highlight: upstream's dark theme inverts a row to black text on white.
            Resources["VTdlsHighlightBg"] = new SolidColorBrush(Color.Parse("#ffffff"));
            Resources["VTdlsHighlightFg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsListBg"] = new SolidColorBrush(Color.Parse("#181818"));
            Resources["VTdlsListFg"] = new SolidColorBrush(Color.Parse("#5aa9ff"));
            Resources["VTdlsFooterBg"] = new SolidColorBrush(Color.Parse("#181818"));
            Resources["VTdlsFooterFg"] = new SolidColorBrush(Color.Parse("#d8d8d8"));
            Resources["VTdlsFacilityButtonBg"] = new SolidColorBrush(Color.Parse("#1c8a1c"));
            Resources["VTdlsSelectedBg"] = new SolidColorBrush(Color.Parse("#5aa9ff"));
            Resources["VTdlsSelectedFg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsListBorder"] = new SolidColorBrush(Color.Parse("#3a3a3a"));
            Resources["VTdlsEditorTextFg"] = new SolidColorBrush(Color.Parse("#d8d8d8"));
            // Hover: slightly lighter than list bg so the highlight reads;
            // keep the same blue foreground so text stays legible against it.
            Resources["VTdlsHoverBg"] = new SolidColorBrush(Color.Parse("#2a2a2a"));
            Resources["VTdlsHoverFg"] = new SolidColorBrush(Color.Parse("#8ec5ff"));
            // Editor dropdowns and buttons: one step lighter than the dark panel.
            Resources["VTdlsControlBg"] = new SolidColorBrush(Color.Parse("#2a2a2a"));
            Resources["VTdlsControlFg"] = new SolidColorBrush(Color.Parse("#d8d8d8"));
            Resources["VTdlsControlBorder"] = new SolidColorBrush(Color.Parse("#6a6a6a"));
            Resources["VTdlsControlHoverBg"] = new SolidColorBrush(Color.Parse("#3a3a3a"));
            Resources["VTdlsControlPressedBg"] = new SolidColorBrush(Color.Parse("#4a4a4a"));
            Resources["VTdlsControlDisabledBg"] = new SolidColorBrush(Color.Parse("#202020"));
            Resources["VTdlsControlDisabledBorder"] = new SolidColorBrush(Color.Parse("#3a3a3a"));
            Resources["VTdlsControlDisabledFg"] = new SolidColorBrush(Color.Parse("#909090"));
        }
        else
        {
            Resources["VTdlsChromeBg"] = new SolidColorBrush(Color.Parse("#000000"));
            // Upstream's header greens: the inactive lists' pale one, the active list's darker one.
            Resources["VTdlsListHeaderBg"] = new SolidColorBrush(Color.Parse("#80ee80"));
            Resources["VTdlsListHeaderActiveBg"] = new SolidColorBrush(Color.Parse("#11be11"));
            // Keyboard highlight: upstream's light theme inverts a row to white text on black.
            Resources["VTdlsHighlightBg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsHighlightFg"] = new SolidColorBrush(Color.Parse("#ffffff"));
            Resources["VTdlsListBg"] = new SolidColorBrush(Color.Parse("#ffffff"));
            Resources["VTdlsListFg"] = new SolidColorBrush(Color.Parse("#0000bb"));
            Resources["VTdlsFooterBg"] = new SolidColorBrush(Color.Parse("#f0f0f0"));
            Resources["VTdlsFooterFg"] = new SolidColorBrush(Color.Parse("#202020"));
            Resources["VTdlsFacilityButtonBg"] = new SolidColorBrush(Color.Parse("#1ec51e"));
            Resources["VTdlsSelectedBg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsSelectedFg"] = new SolidColorBrush(Color.Parse("#ffffff"));
            Resources["VTdlsListBorder"] = new SolidColorBrush(Color.Parse("#a0a0a0"));
            Resources["VTdlsEditorTextFg"] = new SolidColorBrush(Color.Parse("#000000"));
            // Hover: lighter shade than the white list bg so it still reads
            // as a highlight against the white surface.
            Resources["VTdlsHoverBg"] = new SolidColorBrush(Color.Parse("#e0e0e8"));
            Resources["VTdlsHoverFg"] = new SolidColorBrush(Color.Parse("#0000bb"));
            // Editor dropdowns and buttons: grey on the white panel. The disabled text
            // keeps at least 4.5:1 against both the disabled fill and the white panel.
            Resources["VTdlsControlBg"] = new SolidColorBrush(Color.Parse("#e8e8e8"));
            Resources["VTdlsControlFg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["VTdlsControlBorder"] = new SolidColorBrush(Color.Parse("#7a7a7a"));
            Resources["VTdlsControlHoverBg"] = new SolidColorBrush(Color.Parse("#d6d6d6"));
            Resources["VTdlsControlPressedBg"] = new SolidColorBrush(Color.Parse("#c4c4c4"));
            Resources["VTdlsControlDisabledBg"] = new SolidColorBrush(Color.Parse("#f0f0f0"));
            Resources["VTdlsControlDisabledBorder"] = new SolidColorBrush(Color.Parse("#b0b0b0"));
            Resources["VTdlsControlDisabledFg"] = new SolidColorBrush(Color.Parse("#666666"));
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, TickClock);
        _clockTimer.Start();
        TickClock(null, EventArgs.Empty);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _clockTimer?.Stop();
        _clockTimer = null;
    }

    private void TickClock(object? sender, EventArgs e)
    {
        // Upstream renders only one clock (footer, HH:MM). We render seconds too
        // so the controller can verify the page is live; the format still fits
        // the same footer slot.
        DateTime now = WallClock.GetUtcNow().UtcDateTime;
        FooterZuluClock.Text = now.ToString(@"HH\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void OnFacilityButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VTdlsViewModel vm)
        {
            await ShowFacilityMenuAsync(vm);
        }
    }

    /// <summary>The Facility Menu while it is (or was last) shown; Esc closes it through this.</summary>
    private MenuFlyout? _facilityFlyout;

    /// <summary>Esc: opens the Facility Menu, as upstream's Esc does, or closes it when it is open.</summary>
    private async Task ToggleFacilityMenuAsync(KeyEventArgs e, VTdlsViewModel vm)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        e.Handled = true;
        if (_facilityFlyout is { IsOpen: true } open)
        {
            open.Hide();
            return;
        }
        await ShowFacilityMenuAsync(vm);
    }

    /// <summary>True while a Facility Menu is being opened, so a second Esc or click in that time opens no second menu.</summary>
    private bool _facilityMenuOpening;

    private async Task ShowFacilityMenuAsync(VTdlsViewModel vm)
    {
        if (_facilityMenuOpening)
        {
            return;
        }
        _facilityMenuOpening = true;
        try
        {
            // Refresh the accessible-facility list on each opening so a long-running window
            // picks up new TDLS facilities that came online mid-session.
            await vm.RefreshAccessibleFacilitiesAsync();
        }
        finally
        {
            _facilityMenuOpening = false;
        }

        MenuFlyout flyout = BuildFacilityFlyout(vm);
        _facilityFlyout = flyout;
        // Attached as well as shown, so the menu that is open can be found from its button.
        FlyoutBase.SetAttachedFlyout(FacilityButton, flyout);
        flyout.ShowAt(FacilityButton);
    }

    private static MenuFlyout BuildFacilityFlyout(VTdlsViewModel vm)
    {
        var flyout = new MenuFlyout();
        foreach (AccessibleFacilityDto facility in vm.AccessibleFacilities)
        {
            // A consolidated parent (upstream's merged page over its child TDLS
            // facilities) is marked so it reads apart from the children beside it.
            string suffix = facility.IsConsolidated ? " (consolidated)" : "";
            var item = new MenuItem { Header = $"{facility.FacilityId} — {facility.FacilityName}{suffix}" };
            string capturedId = facility.FacilityId;
            item.Click += async (_, _) => await vm.SwitchFacilityAsync(capturedId);
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(no TDLS facilities accessible)", IsEnabled = false });
        }

        // Upstream's Facility Menu hangs the Dark Mode toggle below the
        // facility-switcher entries — same layout here. Checkable so the user
        // sees the current state at a glance.
        flyout.Items.Add(new Separator());
        var darkModeItem = new MenuItem
        {
            Header = "Dark Mode",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = vm.IsDarkMode,
        };
        darkModeItem.Click += (_, _) => vm.IsDarkMode = !vm.IsDarkMode;
        flyout.Items.Add(darkModeItem);

        flyout.Items.Add(new Separator());
        AddDepFreqItems(flyout, vm);
        return flyout;
    }

    /// <summary>Upstream's "None" entry of the Override Dep Freq select.</summary>
    private const string NoOverrideDepFreq = "None";

    /// <summary>
    /// Upstream's three departure-frequency items of the Facility Menu, in its order and with its labels: the Temp. Dep
    /// Freqs text box, the Override Dep Freq select, and the "Add current frequency to Temp. Dep Freqs" checkbox. The two
    /// controls sit in rows that stay open on click, as the traffic flyouts host their search box.
    /// </summary>
    private static void AddDepFreqItems(MenuFlyout flyout, VTdlsViewModel vm)
    {
        flyout.Items.Add(HostRow("Temp. Dep Freqs:", TempDepFreqsEditor(flyout, vm)));
        flyout.Items.Add(HostRow("Override Dep Freq:", OverrideDepFreqPicker(flyout, vm)));

        var addCurrent = new MenuItem
        {
            Header = "Add current frequency to Temp. Dep Freqs",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = vm.AddCurrentFrequencyToTempDepFreqs,
        };
        addCurrent.Click += (_, _) => vm.AddCurrentFrequencyToTempDepFreqs = !vm.AddCurrentFrequencyToTempDepFreqs;
        flyout.Items.Add(addCurrent);
    }

    /// <summary>A menu row hosting a control behind its label; clicking it neither closes the menu nor takes focus from the control.</summary>
    private static MenuItem HostRow(string label, Control control)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(control);
        var row = new MenuItem
        {
            Header = header,
            StaysOpenOnClick = true,
            Focusable = false,
        };
        row.Click += (_, e) => e.Handled = true;
        return row;
    }

    /// <summary>
    /// The Temp. Dep Freqs box. As upstream's input does on blur, an edit is committed when the box loses focus — and on
    /// Enter, or when the menu closes — then shown back as the accepted list, with "Invalid Frequency" under a rejected
    /// one. Only a typed edit is committed, so a list the checkbox changed is never overwritten by stale text.
    /// </summary>
    private static Control TempDepFreqsEditor(MenuFlyout flyout, VTdlsViewModel vm)
    {
        var box = new TextBox { PlaceholderText = "134.7, 123.75", MinWidth = 140 };
        var invalid = new TextBlock { Text = "Invalid Frequency", Foreground = Brushes.Red };
        // The text Show() last wrote. The box counts as edited only while its text differs from it: TextChanged fires
        // asynchronously in Avalonia 12, so a flag set from it cannot tell Show()'s own write from typing.
        string shown = "";

        bool Edited() => !string.Equals(box.Text ?? "", shown, StringComparison.Ordinal);

        void Show()
        {
            shown = string.Join(", ", vm.TempDepFreqs);
            box.Text = shown;
            invalid.IsVisible = vm.IsTempDepFreqsInvalid;
        }

        void Commit()
        {
            if (Edited())
            {
                vm.CommitTempDepFreqs(box.Text);
                Show();
            }
        }

        Show();
        box.LostFocus += (_, _) => Commit();
        box.AddHandler(InputElement.KeyDownEvent, (_, e) => CommitOnEnter(e, Commit), RoutingStrategies.Tunnel);
        flyout.Closed += (_, _) => Commit();
        RefreshWhileOpen(
            flyout,
            vm,
            () =>
            {
                if (!Edited())
                {
                    Show();
                }
            }
        );

        var editor = new StackPanel { Spacing = 2 };
        editor.Children.Add(box);
        editor.Children.Add(invalid);
        return editor;
    }

    private static void CommitOnEnter(KeyEventArgs e, Action commit)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            commit();
        }
    }

    /// <summary>The Override Dep Freq select: upstream's "None", then the temporary frequencies.</summary>
    private static ComboBox OverrideDepFreqPicker(MenuFlyout flyout, VTdlsViewModel vm)
    {
        var picker = new ComboBox { MinWidth = 100 };
        bool filling = false;

        void Fill()
        {
            filling = true;
            picker.ItemsSource = (string[])[NoOverrideDepFreq, .. vm.TempDepFreqs];
            picker.SelectedIndex = vm.OverrideDepFreq is { } chosen ? vm.TempDepFreqs.IndexOf(chosen) + 1 : 0;
            filling = false;
        }

        Fill();
        picker.SelectionChanged += (_, _) =>
        {
            if (!filling)
            {
                vm.OverrideDepFreq = picker.SelectedIndex > 0 ? picker.SelectedItem as string : null;
            }
        };
        RefreshWhileOpen(flyout, vm, Fill);
        return picker;
    }

    /// <summary>Runs <paramref name="refresh"/> whenever the temp list or the override changes, until the menu closes.</summary>
    private static void RefreshWhileOpen(MenuFlyout flyout, VTdlsViewModel vm, Action refresh)
    {
        void OnListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => refresh();
        void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(VTdlsViewModel.OverrideDepFreq) or nameof(VTdlsViewModel.IsTempDepFreqsInvalid))
            {
                refresh();
            }
        }

        vm.TempDepFreqs.CollectionChanged += OnListChanged;
        vm.PropertyChanged += OnVmChanged;
        flyout.Closed += (_, _) =>
        {
            vm.TempDepFreqs.CollectionChanged -= OnListChanged;
            vm.PropertyChanged -= OnVmChanged;
        };
    }

    /// <summary>
    /// Opens the DCL Operational Configuration picker. Upstream commits on Save, not on
    /// selection, so the dropdown here is local until Save fires the server call — and the
    /// active config only changes once the resulting broadcast arrives.
    /// </summary>
    private void OnOpsConfigButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VTdlsViewModel vm)
        {
            return;
        }

        FontFamily mono =
            this.TryFindResource("MonoFont", out object? monoResource) && monoResource is FontFamily family ? family : FontFamily.Default;

        var picker = new ComboBox
        {
            ItemsSource = vm.OpConfigs.ToList(),
            ItemTemplate = new FuncDataTemplate<TdlsOpConfigDto>((config, _) => new TextBlock { Text = config.Name }),
            SelectedItem = vm.OpConfigs.FirstOrDefault(c => string.Equals(c.Id, vm.ActiveOpConfigId, StringComparison.Ordinal)),
            MinWidth = 140,
            FontFamily = mono,
            FontSize = 11,
        };

        var flyout = new Flyout { Placement = PlacementMode.Top };
        var save = new Button
        {
            Content = "Save",
            FontSize = 11,
            Padding = new Thickness(14, 3),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            FontSize = 11,
            Padding = new Thickness(14, 3),
        };

        save.Click += async (_, _) =>
        {
            flyout.Hide();
            if (picker.SelectedItem is TdlsOpConfigDto chosen)
            {
                await vm.SaveOpConfigAsync(chosen.Id);
            }
        };
        cancel.Click += (_, _) => flyout.Hide();

        flyout.Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(12),
            Children =
            {
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "DCL\nOperational\nConfiguration",
                            FontFamily = mono,
                            FontSize = 11,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        },
                        picker,
                    },
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 24,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    Children = { cancel, save },
                },
            },
        };

        flyout.ShowAt(OpsConfigButton);
    }

    private async void OnDumpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VTdlsViewModel vm)
        {
            await vm.DumpSelectedCommand.ExecuteAsync(null);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // Upstream's Cancel button closes the editor without sending. Mirror
        // the F10 key handler — clearing SelectedItem dismisses the editor.
        if (DataContext is VTdlsViewModel vm)
        {
            vm.SelectedItem = null;
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // In-view Find (Ctrl+F / F3 / Shift+F3 / Esc) takes priority over TDLS shortcuts, and while the find box owns
        // keyboard focus its editing keys must not trigger TDLS actions.
        if ((DataContext is not VTdlsViewModel vm) || HandleFindKeys(e) || (_findController.IsVisible && FindBar.IsKeyboardFocusWithin))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F4:
                await DumpByKeyAsync(e, vm);
                break;
            case Key.F12:
                await SendByKeyAsync(e, vm);
                break;
            case Key.F10:
                CancelByKey(e, vm);
                break;
            case Key.Escape:
                await ToggleFacilityMenuAsync(e, vm);
                break;
            default:
                break;
        }
    }

    // Dump and Send go nowhere while disconnected, so their keys do nothing then, like their disabled buttons.
    private static async Task DumpByKeyAsync(KeyEventArgs e, VTdlsViewModel vm)
    {
        if (!vm.IsConnected)
        {
            return;
        }
        e.Handled = true;
        await vm.DumpSelectedCommand.ExecuteAsync(null);
    }

    private static async Task SendByKeyAsync(KeyEventArgs e, VTdlsViewModel vm)
    {
        if ((vm.IsConnected) && (vm.Editor is { IsSendEnabled: true } editor))
        {
            e.Handled = true;
            await editor.SendCommand.ExecuteAsync(null);
        }
    }

    private static void CancelByKey(KeyEventArgs e, VTdlsViewModel vm)
    {
        if (vm.Editor is not null)
        {
            e.Handled = true;
            vm.SelectedItem = null;
        }
    }
}
