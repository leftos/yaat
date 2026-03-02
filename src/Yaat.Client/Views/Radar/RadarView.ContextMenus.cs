using Avalonia;
using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Context menu builders for aircraft and map right-clicks.
/// </summary>
public partial class RadarView
{
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

        var ac = mainVm.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            mainVm.SelectedAircraft = ac;
            vm.SelectedAircraft = ac;
        }
    }

    private void OnAircraftRightClicked(string callsign, Point screenPos)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        var mainVm = FindMainViewModel();
        var ac = mainVm?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null && mainVm is not null)
        {
            mainVm.SelectedAircraft = ac;
            vm.SelectedAircraft = ac;
        }

        var initials = GetInitials();
        var menu = new ContextMenu();

        var typeText = ac is not null ? $"{callsign} - {ac.AircraftType}" : callsign;
        menu.Items.Add(
            new MenuItem
            {
                Header = typeText,
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        menu.Items.Add(BuildHeadingSubmenu(vm, callsign, initials, ac));
        menu.Items.Add(BuildAltitudeSubmenu(vm, callsign, initials, ac));
        menu.Items.Add(BuildSpeedSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildNavigationSubmenu(vm, callsign, initials, ac));
        menu.Items.Add(BuildHoldSubmenu(vm, callsign, initials));

        AddTrackItems(menu, vm, callsign, initials);

        menu.Items.Add(BuildDataBlockSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildCommunicationSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildSquawkSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildCoordinationSubmenu(vm, callsign, initials));

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Delete", () => vm.DeleteAsync(callsign, initials)));

        ShowContextMenu(menu, screenPos);
    }

    private MenuItem BuildHeadingSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Heading" };
        menu.Items.Add(CreateMenuItem("Present heading", () => vm.PresentHeadingAsync(cs, init)));

        var headings = BuildHeadingList();
        var currentHdg = ac is not null ? (int)(Math.Round(ac.Heading / 5.0) * 5) : 360;
        if (currentHdg <= 0)
        {
            currentHdg = 360;
        }

        menu.Items.Add(CreateListMenuItem("Fly heading", headings, currentHdg, val => vm.FlyHeadingAsync(cs, init, (int)val)));
        menu.Items.Add(CreateListMenuItem("Turn left", headings, currentHdg, val => vm.TurnLeftAsync(cs, init, (int)val)));
        menu.Items.Add(CreateListMenuItem("Turn right", headings, currentHdg, val => vm.TurnRightAsync(cs, init, (int)val)));

        return menu;
    }

    private MenuItem BuildAltitudeSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Altitude" };
        var currentAlt = ac?.Altitude ?? 0;
        var fieldElev = vm.GetFieldElevation(ac?.Destination);

        var climbAlts = BuildAltitudeList(fieldElev, currentAlt, true);
        if (climbAlts.Count > 0)
        {
            menu.Items.Add(
                CreateListMenuItem(
                    "Climb and maintain",
                    climbAlts,
                    (int)currentAlt,
                    val => vm.ClimbAndMaintainAsync(cs, init, (int)val),
                    FormatAltitude
                )
            );
        }

        var descAlts = BuildAltitudeList(fieldElev, currentAlt, false);
        if (descAlts.Count > 0)
        {
            menu.Items.Add(
                CreateListMenuItem(
                    "Descend and maintain",
                    descAlts,
                    (int)currentAlt,
                    val => vm.DescendAndMaintainAsync(cs, init, (int)val),
                    FormatAltitude
                )
            );
        }

        return menu;
    }

    private MenuItem BuildSpeedSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Speed" };
        menu.Items.Add(CreateInputMenuItem("Speed...", "Speed (knots)", input => vm.SpeedAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Resume normal speed", () => vm.SpeedNormalAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildNavigationSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Navigation" };

        if (ac is not null)
        {
            var fixes = BuildRouteFixList(ac);
            if (fixes.Count > 0)
            {
                menu.Items.Add(CreateListMenuItem("Direct to", fixes, fixes[0], val => vm.DirectToAsync(cs, init, (string)val)));
            }
            else
            {
                menu.Items.Add(CreateInputMenuItem("Direct to...", "Fix name", input => vm.DirectToAsync(cs, init, input)));
            }
        }
        else
        {
            menu.Items.Add(CreateInputMenuItem("Direct to...", "Fix name", input => vm.DirectToAsync(cs, init, input)));
        }

        return menu;
    }

    private MenuItem BuildHoldSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Hold" };
        menu.Items.Add(CreateMenuItem("Hold present position (left)", () => vm.HoldPresentLeftAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Hold present position (right)", () => vm.HoldPresentRightAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem("Hold at fix (left)...", "Fix name", input => vm.HoldAtFixLeftAsync(cs, init, input)));
        menu.Items.Add(CreateInputMenuItem("Hold at fix (right)...", "Fix name", input => vm.HoldAtFixRightAsync(cs, init, input)));
        return menu;
    }

    private void AddTrackItems(ContextMenu menu, RadarViewModel vm, string cs, string init)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Track", () => vm.TrackAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Drop track", () => vm.DropTrackAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Accept handoff", () => vm.AcceptHandoffAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem("Initiate handoff...", "Position ID", input => vm.InitiateHandoffAsync(cs, init, input)));
        menu.Items.Add(CreateMenuItem("Cancel handoff", () => vm.CancelHandoffAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem("Point out...", "Position ID", input => vm.PointOutAsync(cs, init, input)));
        menu.Items.Add(CreateMenuItem("Acknowledge", () => vm.AcknowledgeAsync(cs, init)));
    }

    private MenuItem BuildDataBlockSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Data Block" };
        menu.Items.Add(CreateInputMenuItem("Scratchpad...", "Text", input => vm.ScratchpadAsync(cs, init, input)));
        menu.Items.Add(CreateInputMenuItem("Temporary altitude...", "Altitude", input => vm.TemporaryAltitudeAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateInputMenuItem("Cruise...", "Altitude", input => vm.CruiseAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Annotate", () => vm.AnnotateAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildCommunicationSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Communication" };
        menu.Items.Add(CreateMenuItem("Frequency change", () => vm.FrequencyChangeAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem("Contact...", "TCP / Position ID", input => vm.ContactTcpAsync(cs, init, input)));
        menu.Items.Add(CreateMenuItem("Contact tower", () => vm.ContactTowerAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Ident", () => vm.IdentAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildSquawkSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Squawk" };
        menu.Items.Add(CreateInputMenuItem("Squawk...", "Code (0000-7777)", input => vm.SquawkAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Squawk VFR", () => vm.SquawkVfrAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk normal", () => vm.SquawkNormalAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk standby", () => vm.SquawkStandbyAsync(cs, init)));
        return menu;
    }

    private static MenuItem BuildCoordinationSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Coordination" };
        menu.Items.Add(CreateMenuItem("Release", () => vm.CoordinationReleaseAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Hold", () => vm.CoordinationHoldAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Recall", () => vm.CoordinationRecallAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Acknowledge", () => vm.CoordinationAcknowledgeAsync(cs, init)));
        return menu;
    }

    private void OnMapRightClicked(double lat, double lon, Point screenPos)
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

        var heading = (int)Math.Round(GeoMath.BearingTo(vm.SelectedAircraft.Latitude, vm.SelectedAircraft.Longitude, lat, lon));
        if (heading <= 0)
        {
            heading += 360;
        }

        menu.Items.Add(CreateMenuItem($"Fly heading {heading:D3}", () => vm.FlyHeadingAsync(callsign, initials, heading)));

        if (vm.Fixes is not null)
        {
            var nearest = FindNearestFix(vm.Fixes, lat, lon, 5.0);
            if (nearest is not null)
            {
                var fixName = nearest.Value.Name;
                menu.Items.Add(CreateMenuItem($"Direct {fixName}", () => vm.DirectToAsync(callsign, initials, fixName)));
                menu.Items.Add(CreateMenuItem($"Hold at {fixName} (left)", () => vm.HoldAtFixLeftAsync(callsign, initials, fixName)));
                menu.Items.Add(CreateMenuItem($"Hold at {fixName} (right)", () => vm.HoldAtFixRightAsync(callsign, initials, fixName)));
            }
        }

        ShowContextMenu(menu, screenPos);
    }

    private static (string Name, double Lat, double Lon)? FindNearestFix(
        IReadOnlyList<(string Name, double Lat, double Lon)> fixes,
        double lat,
        double lon,
        double maxNm
    )
    {
        (string Name, double Lat, double Lon)? best = null;
        double bestDist = maxNm;

        foreach (var fix in fixes)
        {
            var dist = GeoMath.DistanceNm(lat, lon, fix.Lat, fix.Lon);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = fix;
            }
        }

        return best;
    }

    // --- Menu item factories ---

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private MenuItem CreateInputMenuItem(string header, string placeholder, Func<string, Task> action)
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
        Func<int, string>? formatLabel = null
    )
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            if (formatLabel is not null)
            {
                var labeled = new List<object>(items.Count);
                foreach (var i in items)
                {
                    labeled.Add(new LabeledValue(formatLabel((int)i), (int)i));
                }

                ShowListPopup(
                    labeled,
                    null,
                    val =>
                    {
                        var lv = (LabeledValue)val;
                        return action(lv.Value);
                    }
                );
            }
            else
            {
                ShowListPopup(items, selectedValue, action);
            }
        };
        return item;
    }
}
