using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar;

public partial class RadarView : UserControl
{
    private RadarCanvas? _canvas;
    private ContextMenu? _activeContextMenu;

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
        }
    }

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

        // Heading submenu
        var headingMenu = new MenuItem { Header = "Heading" };
        headingMenu.Items.Add(CreateMenuItem("Present heading",
            () => vm.PresentHeadingAsync(callsign, initials)));
        headingMenu.Items.Add(CreateInputMenuItem(
            "Fly heading...", "Heading (1-360)",
            input => vm.FlyHeadingAsync(callsign, initials,
                int.Parse(input))));
        headingMenu.Items.Add(CreateInputMenuItem(
            "Turn left...", "Heading (1-360)",
            input => vm.TurnLeftAsync(callsign, initials,
                int.Parse(input))));
        headingMenu.Items.Add(CreateInputMenuItem(
            "Turn right...", "Heading (1-360)",
            input => vm.TurnRightAsync(callsign, initials,
                int.Parse(input))));
        menu.Items.Add(headingMenu);

        // Altitude submenu
        var altMenu = new MenuItem { Header = "Altitude" };
        AddCommonAltitudes(altMenu, vm, callsign, initials, true);
        AddCommonAltitudes(altMenu, vm, callsign, initials, false);
        altMenu.Items.Add(CreateInputMenuItem(
            "Climb and maintain...", "Altitude",
            input => vm.ClimbAndMaintainAsync(callsign, initials,
                int.Parse(input))));
        altMenu.Items.Add(CreateInputMenuItem(
            "Descend and maintain...", "Altitude",
            input => vm.DescendAndMaintainAsync(callsign, initials,
                int.Parse(input))));
        menu.Items.Add(altMenu);

        // Speed submenu
        var spdMenu = new MenuItem { Header = "Speed" };
        spdMenu.Items.Add(CreateMenuItem("Speed normal",
            () => vm.SpeedNormalAsync(callsign, initials)));
        spdMenu.Items.Add(CreateInputMenuItem(
            "Speed...", "Speed (knots)",
            input => vm.SpeedAsync(callsign, initials,
                int.Parse(input))));
        menu.Items.Add(spdMenu);

        // Approach submenu (runway-based)
        var approachMenu = new MenuItem { Header = "Approach" };
        AddApproachItems(approachMenu, vm, callsign, initials);
        if (approachMenu.Items.Count > 0)
        {
            menu.Items.Add(approachMenu);
        }

        // Track operations
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Track",
            () => vm.TrackAsync(callsign, initials)));
        menu.Items.Add(CreateMenuItem("Drop track",
            () => vm.DropTrackAsync(callsign, initials)));
        menu.Items.Add(CreateMenuItem("Accept handoff",
            () => vm.AcceptHandoffAsync(callsign, initials)));
        menu.Items.Add(CreateMenuItem("Ident",
            () => vm.IdentAsync(callsign, initials)));

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Delete",
            () => vm.DeleteAsync(callsign, initials)));

        ShowContextMenu(menu, screenPos);
    }

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

        // Compute heading from aircraft to click point
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
            () => vm.FlyHeadingAsync(callsign, initials, heading)));

        // Direct to nearest fix (if fixes are available)
        if (vm.Fixes is not null)
        {
            var nearest = FindNearestFix(vm.Fixes, lat, lon, 5.0);
            if (nearest is not null)
            {
                var fixName = nearest.Value.Name;
                menu.Items.Add(CreateMenuItem(
                    $"Direct {fixName}",
                    () => vm.DirectToAsync(
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

    private static void AddCommonAltitudes(
        MenuItem parent, RadarViewModel vm,
        string callsign, string initials, bool climb)
    {
        var alts = climb
            ? new[] { 30, 40, 50, 60, 70, 80, 100, 110, 120 }
            : new[] { 120, 110, 100, 80, 70, 60, 50, 40, 30 };

        foreach (var alt in alts)
        {
            var label = climb
                ? $"Climb {alt * 100}"
                : $"Descend {alt * 100}";
            var a = alt * 100;
            parent.Items.Add(CreateMenuItem(label,
                climb
                    ? () => vm.ClimbAndMaintainAsync(
                        callsign, initials, a)
                    : () => vm.DescendAndMaintainAsync(
                        callsign, initials, a)));
        }
    }

    private static void AddApproachItems(
        MenuItem parent, RadarViewModel vm,
        string callsign, string initials)
    {
        // Common approach types; the user can choose
        string[] types = ["ILS", "RNAV", "VIS"];
        foreach (var t in types)
        {
            var type = t;
            parent.Items.Add(CreateInputMenuItem(
                $"{type}...", "Runway (e.g., 28L)",
                input => vm.ClearedApproachAsync(
                    callsign, initials, $"{type} {input}")));
        }
    }

    private static MenuItem CreateMenuItem(
        string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static MenuItem CreateInputMenuItem(
        string header, string placeholder,
        Func<string, Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) =>
        {
            // For now, use a simple prompt approach
            // In the future, this could show a TextBox popup
            // The context menu item header indicates the expected input
            // TODO: Implement input popup
        };
        return item;
    }

    private void OnCanvasPointerPressed(
        object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(_canvas!).Properties;
        if (props.IsLeftButtonPressed)
        {
            CloseActiveContextMenu();
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
