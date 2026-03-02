using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar;

public partial class RadarView : UserControl
{
    private RadarCanvas? _canvas;
    private ContextMenu? _activeContextMenu;
    private Func<string, Task>? _pendingInputAction;
    private Func<object, Task>? _pendingListAction;

    public static readonly FuncValueConverter<bool, IBrush>
        BoolToMapColor = new(v => v
            ? Brushes.Lime
            : new SolidColorBrush(Color.Parse("#888")));

    public static readonly FuncValueConverter<bool, string>
        BoolToLockLabel = new(v => v ? "LOCK" : "UNLK");

    public static readonly FuncValueConverter<bool, IBrush>
        BoolToLockColor = new(v => v
            ? new SolidColorBrush(Color.Parse("#888"))
            : Brushes.Yellow);

    public static readonly FuncValueConverter<bool, IBrush>
        BoolToLatchColor = new(v => v
            ? Brushes.Cyan
            : new SolidColorBrush(Color.Parse("#CCC")));

    public RadarView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _canvas = this.FindControl<RadarCanvas>("Canvas");
        if (_canvas is null)
        {
            return;
        }

        _canvas.AircraftRightClicked += OnAircraftRightClicked;
        _canvas.MapRightClicked += OnMapRightClicked;
        _canvas.AircraftLeftClicked += OnAircraftLeftClicked;
        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.RangeRingPlaced += OnRangeRingPlaced;

        // Sync brightness from ViewModel
        if (DataContext is RadarViewModel vm)
        {
            _canvas.SetBrightnessLookup(vm.BrightnessLookup);
            _canvas.BrightnessA = vm.MapBrightnessA;
            _canvas.BrightnessB = vm.MapBrightnessB;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (_canvas is not null)
        {
            _canvas.AircraftRightClicked -= OnAircraftRightClicked;
            _canvas.MapRightClicked -= OnMapRightClicked;
            _canvas.AircraftLeftClicked -= OnAircraftLeftClicked;
            _canvas.PointerPressed -= OnCanvasPointerPressed;
            _canvas.RangeRingPlaced -= OnRangeRingPlaced;
        }
    }

    // --- DCB button handlers ---

    private void OnMapShortcutClick(
        object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MapShortcutItem shortcut })
        {
            return;
        }

        if (DataContext is RadarViewModel vm)
        {
            vm.ToggleMapShortcut(shortcut);
        }
    }

    private void OnMapButtonClick(
        object? sender, RoutedEventArgs e)
    {
        var popup = this.FindControl<Popup>("MapPopup");
        if (popup is not null)
        {
            popup.IsOpen = !popup.IsOpen;
            if (popup.IsOpen)
            {
                var input = this.FindControl<TextBox>("MapIdInput");
                input?.Focus();
            }
        }
    }

    private void OnMapIdSubmit(
        object? sender, RoutedEventArgs e)
    {
        var input = this.FindControl<TextBox>("MapIdInput");
        var popup = this.FindControl<Popup>("MapPopup");

        if (input?.Text is not null
            && int.TryParse(input.Text, CultureInfo.InvariantCulture,
                out var starsId)
            && DataContext is RadarViewModel vm)
        {
            vm.ToggleMapByStarsId(starsId);
            input.Text = "";
        }

        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    private void OnMapToggleClick(
        object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        if (sender is CheckBox { DataContext: VideoMapToggleItem toggle })
        {
            vm.SyncShortcutState(toggle.StarsId, toggle.IsEnabled);
        }
    }

    private void OnRrSizeClick(
        object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        vm.IsAdjustingRangeRingSize = !vm.IsAdjustingRangeRingSize;
        if (vm.IsAdjustingRangeRingSize)
        {
            _canvas?.Focus();
        }
    }

    private void OnRangeRingPlaced(double lat, double lon)
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.PlaceRangeRing(lat, lon);
        }
    }

    // --- Input popup ---

    private void ShowInputPopup(
        string watermark, Func<string, Task> action)
    {
        _pendingInputAction = action;
        var popup = this.FindControl<Popup>("InputPopup");
        var textBox = this.FindControl<TextBox>("InputPopupText");
        if (popup is null || textBox is null)
        {
            return;
        }

        textBox.Text = "";
        textBox.Watermark = watermark;
        popup.IsOpen = true;
        textBox.Focus();
    }

    private void OnInputPopupSubmit(
        object? sender, RoutedEventArgs e)
    {
        SubmitInputPopup();
    }

    private void OnInputPopupCancel(
        object? sender, RoutedEventArgs e)
    {
        CloseInputPopup();
    }

    private void OnInputPopupKeyDown(
        object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitInputPopup();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseInputPopup();
            e.Handled = true;
        }
    }

    private void SubmitInputPopup()
    {
        var textBox = this.FindControl<TextBox>("InputPopupText");
        var text = textBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(text) && _pendingInputAction is not null)
        {
            var action = _pendingInputAction;
            CloseInputPopup();
            _ = action(text);
        }
        else
        {
            CloseInputPopup();
        }
    }

    private void CloseInputPopup()
    {
        _pendingInputAction = null;
        var popup = this.FindControl<Popup>("InputPopup");
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    // --- List popup ---

    private void ShowListPopup(
        IReadOnlyList<object> items,
        object? selectedValue,
        Func<object, Task> action)
    {
        _pendingListAction = action;
        var popup = this.FindControl<Popup>("ListPopup");
        var listBox = this.FindControl<ListBox>("ListPopupItems");
        if (popup is null || listBox is null)
        {
            return;
        }

        listBox.ItemsSource = items;
        popup.IsOpen = true;

        if (selectedValue is not null)
        {
            var idx = FindExactIndex(items, selectedValue);
            if (idx < 0)
            {
                idx = FindClosestIndex(items, selectedValue);
            }

            if (idx >= 0)
            {
                listBox.SelectedIndex = idx;
                listBox.ScrollIntoView(items[idx]);
            }
        }
    }

    private static int FindExactIndex(
        IReadOnlyList<object> items, object target)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (Equals(items[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindClosestIndex(
        IReadOnlyList<object> items, object target)
    {
        if (target is not int targetInt)
        {
            return -1;
        }

        var bestIdx = -1;
        var bestDiff = int.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is int val)
            {
                var diff = Math.Abs(val - targetInt);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }
        }

        return bestIdx;
    }

    private void OnListPopupSelectionChanged(
        object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || _pendingListAction is null)
        {
            return;
        }

        var selected = e.AddedItems[0];
        if (selected is null)
        {
            return;
        }

        var action = _pendingListAction;
        CloseListPopup();
        _ = action(selected);
    }

    private void CloseListPopup()
    {
        _pendingListAction = null;
        var popup = this.FindControl<Popup>("ListPopup");
        var listBox = this.FindControl<ListBox>("ListPopupItems");
        if (listBox is not null)
        {
            listBox.SelectedIndex = -1;
            listBox.ItemsSource = null;
        }

        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    // --- Heading/altitude/route list builders ---

    private static IReadOnlyList<object> BuildHeadingList()
    {
        var items = new List<object>(72);
        for (var h = 5; h <= 360; h += 5)
        {
            items.Add(h);
        }

        return items;
    }

    private static IReadOnlyList<object> BuildAltitudeList(
        double fieldElevation, double currentAlt, bool climb)
    {
        var items = new List<object>();
        var lowThreshold = (int)(fieldElevation + 5000);

        // Round to nearest 100
        var roundedLow = (int)(Math.Ceiling(fieldElevation / 100.0) * 100);
        if (roundedLow < 100)
        {
            roundedLow = 100;
        }

        // Below threshold: every 100ft
        for (var alt = roundedLow; alt < lowThreshold; alt += 100)
        {
            if (climb && alt > (int)currentAlt)
            {
                items.Add(alt);
            }
            else if (!climb && alt < (int)currentAlt)
            {
                items.Add(alt);
            }
        }

        // At/above threshold: every 500ft up to FL600
        var start500 = (int)(Math.Ceiling(lowThreshold / 500.0) * 500);
        for (var alt = start500; alt <= 60000; alt += 500)
        {
            if (climb && alt > (int)currentAlt)
            {
                items.Add(alt);
            }
            else if (!climb && alt < (int)currentAlt)
            {
                items.Add(alt);
            }
        }

        if (!climb)
        {
            items.Reverse();
        }

        return items;
    }

    private static string FormatAltitude(int alt)
    {
        return alt >= 18000 ? $"FL{alt / 100}" : $"{alt}";
    }

    private static IReadOnlyList<object> BuildRouteFixList(
        AircraftModel ac)
    {
        if (string.IsNullOrEmpty(ac.NavigationRoute))
        {
            return [];
        }

        var parts = ac.NavigationRoute.Split(" > ");
        var items = new List<object>();
        var started = string.IsNullOrEmpty(ac.NavigatingTo);
        foreach (var part in parts)
        {
            var fix = part.Trim();
            if (string.IsNullOrEmpty(fix))
            {
                continue;
            }

            if (!started && fix == ac.NavigatingTo)
            {
                started = true;
            }

            if (started)
            {
                items.Add(fix);
            }
        }

        return items;
    }

    // --- Aircraft/map interactions ---

    private void OnAircraftLeftClicked(string callsign)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        var mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }

        var ac = mainVm.Aircraft.FirstOrDefault(
            a => a.Callsign == callsign);
        if (ac is not null)
        {
            mainVm.SelectedAircraft = ac;
            vm.SelectedAircraft = ac;
        }
    }

    private void OnAircraftRightClicked(
        string callsign, Point screenPos)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        var mainVm = FindMainViewModel();
        var ac = mainVm?.Aircraft.FirstOrDefault(
            a => a.Callsign == callsign);
        if (ac is not null && mainVm is not null)
        {
            mainVm.SelectedAircraft = ac;
            vm.SelectedAircraft = ac;
        }

        var initials = GetInitials();
        var menu = new ContextMenu();

        var typeText = ac is not null
            ? $"{callsign} - {ac.AircraftType}"
            : callsign;
        menu.Items.Add(new MenuItem
        {
            Header = typeText,
            IsEnabled = false,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        menu.Items.Add(new Separator());

        menu.Items.Add(BuildHeadingSubmenu(
            vm, callsign, initials, ac));
        menu.Items.Add(BuildAltitudeSubmenu(
            vm, callsign, initials, ac));
        menu.Items.Add(BuildSpeedSubmenu(
            vm, callsign, initials));
        menu.Items.Add(BuildNavigationSubmenu(
            vm, callsign, initials, ac));
        menu.Items.Add(BuildHoldSubmenu(
            vm, callsign, initials));

        AddTrackItems(menu, vm, callsign, initials);

        menu.Items.Add(BuildDataBlockSubmenu(
            vm, callsign, initials));
        menu.Items.Add(BuildCommunicationSubmenu(
            vm, callsign, initials));
        menu.Items.Add(BuildSquawkSubmenu(
            vm, callsign, initials));
        menu.Items.Add(BuildCoordinationSubmenu(
            vm, callsign, initials));

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Delete",
            () => vm.DeleteAsync(callsign, initials)));

        ShowContextMenu(menu, screenPos);
    }

    // --- Submenu builders ---

    private MenuItem BuildHeadingSubmenu(
        RadarViewModel vm, string cs, string init,
        AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Heading" };
        menu.Items.Add(CreateMenuItem("Present heading",
            () => vm.PresentHeadingAsync(cs, init)));

        var headings = BuildHeadingList();
        var currentHdg = ac is not null
            ? (int)(Math.Round(ac.Heading / 5.0) * 5)
            : 360;
        if (currentHdg <= 0)
        {
            currentHdg = 360;
        }

        menu.Items.Add(CreateListMenuItem(
            "Fly heading", headings, currentHdg,
            val => vm.FlyHeadingAsync(cs, init, (int)val)));
        menu.Items.Add(CreateListMenuItem(
            "Turn left", headings, currentHdg,
            val => vm.TurnLeftAsync(cs, init, (int)val)));
        menu.Items.Add(CreateListMenuItem(
            "Turn right", headings, currentHdg,
            val => vm.TurnRightAsync(cs, init, (int)val)));

        return menu;
    }

    private MenuItem BuildAltitudeSubmenu(
        RadarViewModel vm, string cs, string init,
        AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Altitude" };
        var currentAlt = ac?.Altitude ?? 0;
        var fieldElev = vm.GetFieldElevation(ac?.Destination);

        var climbAlts = BuildAltitudeList(
            fieldElev, currentAlt, true);
        if (climbAlts.Count > 0)
        {
            menu.Items.Add(CreateListMenuItem(
                "Climb and maintain", climbAlts,
                (int)currentAlt,
                val => vm.ClimbAndMaintainAsync(
                    cs, init, (int)val),
                FormatAltitude));
        }

        var descAlts = BuildAltitudeList(
            fieldElev, currentAlt, false);
        if (descAlts.Count > 0)
        {
            menu.Items.Add(CreateListMenuItem(
                "Descend and maintain", descAlts,
                (int)currentAlt,
                val => vm.DescendAndMaintainAsync(
                    cs, init, (int)val),
                FormatAltitude));
        }

        return menu;
    }

    private MenuItem BuildSpeedSubmenu(
        RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Speed" };
        menu.Items.Add(CreateInputMenuItem(
            "Speed...", "Speed (knots)",
            input => vm.SpeedAsync(
                cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Resume normal speed",
            () => vm.SpeedNormalAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildNavigationSubmenu(
        RadarViewModel vm, string cs, string init,
        AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Navigation" };

        if (ac is not null)
        {
            var fixes = BuildRouteFixList(ac);
            if (fixes.Count > 0)
            {
                menu.Items.Add(CreateListMenuItem(
                    "Direct to", fixes, fixes[0],
                    val => vm.DirectToAsync(
                        cs, init, (string)val)));
            }
            else
            {
                menu.Items.Add(CreateInputMenuItem(
                    "Direct to...", "Fix name",
                    input => vm.DirectToAsync(
                        cs, init, input)));
            }
        }
        else
        {
            menu.Items.Add(CreateInputMenuItem(
                "Direct to...", "Fix name",
                input => vm.DirectToAsync(
                    cs, init, input)));
        }

        return menu;
    }

    private MenuItem BuildHoldSubmenu(
        RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Hold" };
        menu.Items.Add(CreateMenuItem(
            "Hold present position (left)",
            () => vm.HoldPresentLeftAsync(cs, init)));
        menu.Items.Add(CreateMenuItem(
            "Hold present position (right)",
            () => vm.HoldPresentRightAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem(
            "Hold at fix (left)...", "Fix name",
            input => vm.HoldAtFixLeftAsync(
                cs, init, input)));
        menu.Items.Add(CreateInputMenuItem(
            "Hold at fix (right)...", "Fix name",
            input => vm.HoldAtFixRightAsync(
                cs, init, input)));
        return menu;
    }

    private void AddTrackItems(
        ContextMenu menu, RadarViewModel vm,
        string cs, string init)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Track",
            () => vm.TrackAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Drop track",
            () => vm.DropTrackAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Accept handoff",
            () => vm.AcceptHandoffAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem(
            "Initiate handoff...", "Position ID",
            input => vm.InitiateHandoffAsync(
                cs, init, input)));
        menu.Items.Add(CreateMenuItem("Cancel handoff",
            () => vm.CancelHandoffAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem(
            "Point out...", "Position ID",
            input => vm.PointOutAsync(cs, init, input)));
        menu.Items.Add(CreateMenuItem("Acknowledge",
            () => vm.AcknowledgeAsync(cs, init)));
    }

    private MenuItem BuildDataBlockSubmenu(
        RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Data Block" };
        menu.Items.Add(CreateInputMenuItem(
            "Scratchpad...", "Text",
            input => vm.ScratchpadAsync(cs, init, input)));
        menu.Items.Add(CreateInputMenuItem(
            "Temporary altitude...", "Altitude",
            input => vm.TemporaryAltitudeAsync(
                cs, init, int.Parse(input))));
        menu.Items.Add(CreateInputMenuItem(
            "Cruise...", "Altitude",
            input => vm.CruiseAsync(
                cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Annotate",
            () => vm.AnnotateAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildCommunicationSubmenu(
        RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Communication" };
        menu.Items.Add(CreateMenuItem("Frequency change",
            () => vm.FrequencyChangeAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem(
            "Contact...", "TCP / Position ID",
            input => vm.ContactTcpAsync(
                cs, init, input)));
        menu.Items.Add(CreateMenuItem("Contact tower",
            () => vm.ContactTowerAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Ident",
            () => vm.IdentAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildSquawkSubmenu(
        RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Squawk" };
        menu.Items.Add(CreateInputMenuItem(
            "Squawk...", "Code (0000-7777)",
            input => vm.SquawkAsync(
                cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Squawk VFR",
            () => vm.SquawkVfrAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk normal",
            () => vm.SquawkNormalAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk standby",
            () => vm.SquawkStandbyAsync(cs, init)));
        return menu;
    }

    private static MenuItem BuildCoordinationSubmenu(
        RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Coordination" };
        menu.Items.Add(CreateMenuItem("Release",
            () => vm.CoordinationReleaseAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Hold",
            () => vm.CoordinationHoldAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Recall",
            () => vm.CoordinationRecallAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Acknowledge",
            () => vm.CoordinationAcknowledgeAsync(
                cs, init)));
        return menu;
    }

    // --- Map right-click ---

    private void OnMapRightClicked(
        double lat, double lon, Point screenPos)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        if (vm.SelectedAircraft is null)
        {
            return;
        }

        var callsign = vm.SelectedAircraft.Callsign;
        var initials = GetInitials();
        var menu = new ContextMenu();

        var heading = (int)Math.Round(GeoMath.BearingTo(
            vm.SelectedAircraft.Latitude,
            vm.SelectedAircraft.Longitude,
            lat, lon));
        if (heading <= 0)
        {
            heading += 360;
        }

        menu.Items.Add(CreateMenuItem(
            $"Fly heading {heading:D3}",
            () => vm.FlyHeadingAsync(
                callsign, initials, heading)));

        if (vm.Fixes is not null)
        {
            var nearest = FindNearestFix(
                vm.Fixes, lat, lon, 5.0);
            if (nearest is not null)
            {
                var fixName = nearest.Value.Name;
                menu.Items.Add(CreateMenuItem(
                    $"Direct {fixName}",
                    () => vm.DirectToAsync(
                        callsign, initials, fixName)));
                menu.Items.Add(CreateMenuItem(
                    $"Hold at {fixName} (left)",
                    () => vm.HoldAtFixLeftAsync(
                        callsign, initials, fixName)));
                menu.Items.Add(CreateMenuItem(
                    $"Hold at {fixName} (right)",
                    () => vm.HoldAtFixRightAsync(
                        callsign, initials, fixName)));
            }
        }

        ShowContextMenu(menu, screenPos);
    }

    private static (string Name, double Lat, double Lon)?
        FindNearestFix(
            IReadOnlyList<(string Name, double Lat, double Lon)> fixes,
            double lat, double lon, double maxNm)
    {
        (string Name, double Lat, double Lon)? best = null;
        double bestDist = maxNm;

        foreach (var fix in fixes)
        {
            var dist = GeoMath.DistanceNm(
                lat, lon, fix.Lat, fix.Lon);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = fix;
            }
        }

        return best;
    }

    // --- Menu item factories ---

    private static MenuItem CreateMenuItem(
        string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private MenuItem CreateInputMenuItem(
        string header, string placeholder,
        Func<string, Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            ShowInputPopup(placeholder, action);
        };
        return item;
    }

    private MenuItem CreateListMenuItem(
        string header,
        IReadOnlyList<object> items,
        object? selectedValue,
        Func<object, Task> action,
        Func<int, string>? formatLabel = null)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            if (formatLabel is not null)
            {
                var labeled = new List<object>(items.Count);
                foreach (var i in items)
                {
                    labeled.Add(
                        new LabeledValue(
                            formatLabel((int)i), (int)i));
                }

                ShowListPopup(labeled, null, val =>
                {
                    var lv = (LabeledValue)val;
                    return action(lv.Value);
                });
            }
            else
            {
                ShowListPopup(items, selectedValue, action);
            }
        };
        return item;
    }

    // --- Canvas interaction ---

    private void OnCanvasPointerPressed(
        object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_canvas!).Properties;
        if (props.IsLeftButtonPressed)
        {
            CloseActiveContextMenu();
            CloseInputPopup();
            CloseListPopup();
        }
    }

    private void CloseActiveContextMenu()
    {
        if (_activeContextMenu is not null)
        {
            _activeContextMenu.Close();
            _activeContextMenu = null;
        }
    }

    private void ShowContextMenu(ContextMenu menu, Point pos)
    {
        if (_canvas is null)
        {
            return;
        }

        CloseActiveContextMenu();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (_activeContextMenu == menu)
            {
                _activeContextMenu = null;
            }
        };
        menu.PlacementTarget = _canvas;
        menu.Placement = PlacementMode.Pointer;
        menu.Open(_canvas);
    }

    private string GetInitials()
    {
        var mainVm = FindMainViewModel();
        return mainVm?.Preferences.UserInitials ?? "";
    }

    private MainViewModel? FindMainViewModel()
    {
        var parent = this.Parent;
        while (parent is not null)
        {
            if (parent.DataContext is MainViewModel vm)
            {
                return vm;
            }

            parent = (parent as Control)?.Parent;
        }

        return null;
    }
}

/// <summary>
/// Wraps an int value with a display label for the list popup.
/// </summary>
internal sealed record LabeledValue(string Label, int Value)
{
    public override string ToString() => Label;
}
