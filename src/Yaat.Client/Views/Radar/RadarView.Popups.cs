using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Input popup, list popup, and list builder helpers for radar interactions.
/// </summary>
public partial class RadarView
{
    // --- Input popup ---

    private void ShowInputPopup(string watermark, Func<string, Task> action)
    {
        _pendingInputAction = action;
        Popup? popup = this.FindControl<Popup>("InputPopup");
        TextBox? textBox = this.FindControl<TextBox>("InputPopupText");
        if (popup is null || textBox is null)
        {
            return;
        }

        textBox.Text = "";
        textBox.PlaceholderText = watermark;
        popup.IsOpen = true;
        textBox.Focus();
    }

    private void OnInputPopupSubmit(object? sender, RoutedEventArgs e) => SubmitInputPopup();

    private void OnInputPopupCancel(object? sender, RoutedEventArgs e) => CloseInputPopup();

    private void OnInputPopupKeyDown(object? sender, KeyEventArgs e)
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
        TextBox? textBox = this.FindControl<TextBox>("InputPopupText");
        string? text = textBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(text) && _pendingInputAction is not null)
        {
            Func<string, Task> action = _pendingInputAction;
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
        Popup? popup = this.FindControl<Popup>("InputPopup");
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    // --- List popup ---

    private void ShowListPopup(IReadOnlyList<object> items, object? selectedValue, Func<object, Task> action)
    {
        _pendingListAction = action;
        _listPopupInitializing = true;
        Popup? popup = this.FindControl<Popup>("ListPopup");
        ListBox? listBox = this.FindControl<ListBox>("ListPopupItems");
        if (popup is null || listBox is null)
        {
            _listPopupInitializing = false;
            return;
        }

        listBox.ItemsSource = items;
        popup.IsOpen = true;

        if (selectedValue is not null)
        {
            int idx = FindExactIndex(items, selectedValue);
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

        _listPopupInitializing = false;
    }

    private static int FindExactIndex(IReadOnlyList<object> items, object target)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (Equals(items[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindClosestIndex(IReadOnlyList<object> items, object target)
    {
        if (target is not int targetInt)
        {
            return -1;
        }

        int bestIdx = -1;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is int val)
            {
                int diff = Math.Abs(val - targetInt);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }
        }

        return bestIdx;
    }

    private void OnListPopupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_listPopupInitializing)
        {
            return;
        }

        if (e.AddedItems.Count == 0 || _pendingListAction is null)
        {
            return;
        }

        object? selected = e.AddedItems[0];
        if (selected is null)
        {
            return;
        }

        Func<object, Task> action = _pendingListAction;
        CloseListPopup();
        _ = action(selected);
    }

    private void CloseListPopup()
    {
        _pendingListAction = null;
        Popup? popup = this.FindControl<Popup>("ListPopup");
        ListBox? listBox = this.FindControl<ListBox>("ListPopupItems");
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

    // --- Filtered list popup ---

    private void ShowFilteredListPopup(string[] sortedNames, Func<string, Task> action, IReadOnlyList<object>? priorityItems = null)
    {
        _pendingFilteredListAction = action;
        _filteredListAllNames = sortedNames;
        Popup? popup = this.FindControl<Popup>("FilteredListPopup");
        TextBox? textBox = this.FindControl<TextBox>("FilteredListText");
        ListBox? listBox = this.FindControl<ListBox>("FilteredListItems");
        if (popup is null || textBox is null || listBox is null)
        {
            return;
        }

        textBox.Text = "";
        listBox.ItemsSource = priorityItems ?? Array.Empty<object>();
        popup.IsOpen = true;
        textBox.Focus();
    }

    private void OnFilteredListTextChanged(object? sender, TextChangedEventArgs e)
    {
        TextBox? textBox = this.FindControl<TextBox>("FilteredListText");
        ListBox? listBox = this.FindControl<ListBox>("FilteredListItems");
        if (textBox is null || listBox is null || _filteredListAllNames is null)
        {
            return;
        }

        string prefix = textBox.Text?.Trim().ToUpperInvariant() ?? "";
        if (prefix.Length == 0)
        {
            listBox.ItemsSource = Array.Empty<object>();
            return;
        }

        IReadOnlyList<object> results = PrefixSearch(_filteredListAllNames, prefix, 50);
        listBox.ItemsSource = results;
        if (results.Count > 0)
        {
            listBox.SelectedIndex = 0;
        }
    }

    private void OnFilteredListKeyDown(object? sender, KeyEventArgs e)
    {
        ListBox? listBox = this.FindControl<ListBox>("FilteredListItems");
        if (listBox is null)
        {
            return;
        }

        if (e.Key == Key.Down)
        {
            if (listBox.ItemCount > 0)
            {
                listBox.SelectedIndex = Math.Min(listBox.SelectedIndex + 1, listBox.ItemCount - 1);
                listBox.ScrollIntoView(listBox.SelectedItem!);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (listBox.ItemCount > 0)
            {
                listBox.SelectedIndex = Math.Max(listBox.SelectedIndex - 1, 0);
                listBox.ScrollIntoView(listBox.SelectedItem!);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SubmitFilteredListPopup();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFilteredListPopup();
            e.Handled = true;
        }
    }

    private void OnFilteredListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || _pendingFilteredListAction is null)
        {
            return;
        }

        TextBox? textBox = this.FindControl<TextBox>("FilteredListText");
        if (textBox is not null && textBox.IsFocused)
        {
            return;
        }

        string? selected = e.AddedItems[0]?.ToString();
        if (!string.IsNullOrEmpty(selected))
        {
            Func<string, Task> action = _pendingFilteredListAction;
            CloseFilteredListPopup();
            _ = action(selected);
        }
    }

    private void SubmitFilteredListPopup()
    {
        TextBox? textBox = this.FindControl<TextBox>("FilteredListText");
        ListBox? listBox = this.FindControl<ListBox>("FilteredListItems");
        if (_pendingFilteredListAction is null)
        {
            CloseFilteredListPopup();
            return;
        }

        string? value = null;
        if (listBox?.SelectedItem is not null)
        {
            value = listBox.SelectedItem.ToString();
        }

        if (string.IsNullOrEmpty(value))
        {
            value = textBox?.Text?.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrEmpty(value))
        {
            Func<string, Task> action = _pendingFilteredListAction;
            CloseFilteredListPopup();
            _ = action(value);
        }
        else
        {
            CloseFilteredListPopup();
        }
    }

    private void CloseFilteredListPopup()
    {
        _pendingFilteredListAction = null;
        _filteredListAllNames = null;
        Popup? popup = this.FindControl<Popup>("FilteredListPopup");
        ListBox? listBox = this.FindControl<ListBox>("FilteredListItems");
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

    private static IReadOnlyList<object> PrefixSearch(string[] sortedNames, string prefix, int maxResults)
    {
        var results = new List<object>();
        int idx = Array.BinarySearch(sortedNames, prefix, StringComparer.OrdinalIgnoreCase);
        if (idx < 0)
        {
            idx = ~idx;
        }

        for (int i = idx; i < sortedNames.Length && results.Count < maxResults; i++)
        {
            if (sortedNames[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(sortedNames[i]);
            }
            else
            {
                break;
            }
        }

        return results;
    }

    // --- Waypoint condition popup ---

    private Action<string?, string?>? _pendingWaypointConditionAction;

    private void ShowWaypointConditionPopup(string fixName, string? existingAltitude, string? existingCommands, Action<string?, string?> onSubmit)
    {
        _pendingWaypointConditionAction = onSubmit;
        Popup? popup = this.FindControl<Popup>("WaypointConditionPopup");
        TextBlock? header = this.FindControl<TextBlock>("WaypointConditionHeader");
        TextBox? altBox = this.FindControl<TextBox>("WaypointConditionAltitude");
        TextBox? cmdBox = this.FindControl<TextBox>("WaypointConditionCommands");
        if (popup is null || header is null || altBox is null || cmdBox is null)
        {
            return;
        }

        header.Text = $"Conditions at {fixName}";
        altBox.Text = existingAltitude ?? "";
        cmdBox.Text = existingCommands ?? "";
        popup.IsOpen = true;
        altBox.Focus();
    }

    private void OnWaypointConditionSubmit(object? sender, RoutedEventArgs e) => SubmitWaypointConditionPopup();

    private void OnWaypointConditionCancel(object? sender, RoutedEventArgs e) => CloseWaypointConditionPopup();

    private void OnWaypointConditionClear(object? sender, RoutedEventArgs e)
    {
        _pendingWaypointConditionAction?.Invoke(null, null);
        CloseWaypointConditionPopup();
    }

    private void OnWaypointConditionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitWaypointConditionPopup();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseWaypointConditionPopup();
            e.Handled = true;
        }
    }

    private void SubmitWaypointConditionPopup()
    {
        TextBox? altBox = this.FindControl<TextBox>("WaypointConditionAltitude");
        TextBox? cmdBox = this.FindControl<TextBox>("WaypointConditionCommands");
        string? altitude = altBox?.Text?.Trim();
        string? commands = cmdBox?.Text?.Trim();

        if (string.IsNullOrEmpty(altitude))
        {
            altitude = null;
        }

        if (string.IsNullOrEmpty(commands))
        {
            commands = null;
        }

        _pendingWaypointConditionAction?.Invoke(altitude, commands);
        CloseWaypointConditionPopup();
    }

    private void CloseWaypointConditionPopup()
    {
        _pendingWaypointConditionAction = null;
        Popup? popup = this.FindControl<Popup>("WaypointConditionPopup");
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    // --- Warp popup ---

    private void ShowWarpPopup(
        string callsign,
        string defaultFrd,
        int defaultHeading,
        int defaultAltitude,
        int defaultSpeed,
        Action<string, int, int, int> onSubmit
    )
    {
        _pendingWarpAction = onSubmit;
        Popup? popup = this.FindControl<Popup>("WarpPopup");
        TextBlock? header = this.FindControl<TextBlock>("WarpPopupHeader");
        TextBox? frdBox = this.FindControl<TextBox>("WarpPopupFrd");
        TextBox? hdgBox = this.FindControl<TextBox>("WarpPopupHeading");
        TextBox? altBox = this.FindControl<TextBox>("WarpPopupAltitude");
        TextBox? spdBox = this.FindControl<TextBox>("WarpPopupSpeed");
        if (popup is null || header is null || frdBox is null || hdgBox is null || altBox is null || spdBox is null)
        {
            return;
        }

        header.Text = $"Warp {callsign}";
        frdBox.Text = defaultFrd;
        hdgBox.Text = defaultHeading > 0 ? defaultHeading.ToString() : "";
        altBox.Text = defaultAltitude > 0 ? defaultAltitude.ToString() : "";
        spdBox.Text = defaultSpeed > 0 ? defaultSpeed.ToString() : "";
        popup.IsOpen = true;
        frdBox.Focus();
    }

    private void OnWarpPopupSubmit(object? sender, RoutedEventArgs e) => SubmitWarpPopup();

    private void OnWarpPopupCancel(object? sender, RoutedEventArgs e) => CloseWarpPopup();

    private void OnWarpPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitWarpPopup();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseWarpPopup();
            e.Handled = true;
        }
    }

    private void SubmitWarpPopup()
    {
        TextBox? frdBox = this.FindControl<TextBox>("WarpPopupFrd");
        TextBox? hdgBox = this.FindControl<TextBox>("WarpPopupHeading");
        TextBox? altBox = this.FindControl<TextBox>("WarpPopupAltitude");
        TextBox? spdBox = this.FindControl<TextBox>("WarpPopupSpeed");

        string? frd = frdBox?.Text?.Trim();
        if (string.IsNullOrEmpty(frd) || _pendingWarpAction is null)
        {
            CloseWarpPopup();
            return;
        }

        if (
            !int.TryParse(hdgBox?.Text?.Trim(), out int heading)
            || !int.TryParse(altBox?.Text?.Trim(), out int altitude)
            || !int.TryParse(spdBox?.Text?.Trim(), out int speed)
        )
        {
            CloseWarpPopup();
            return;
        }

        Action<string, int, int, int> action = _pendingWarpAction;
        CloseWarpPopup();
        action(frd, heading, altitude, speed);
    }

    private void CloseWarpPopup()
    {
        _pendingWarpAction = null;
        Popup? popup = this.FindControl<Popup>("WarpPopup");
        if (popup is not null)
        {
            popup.IsOpen = false;
        }
    }

    // --- Heading/altitude/route list builders ---

    private static IReadOnlyList<object> BuildHeadingList()
    {
        var items = new List<object>(72);
        for (int h = 5; h <= 360; h += 5)
        {
            items.Add(h);
        }

        return items;
    }

    private static IReadOnlyList<object> BuildRelativeTurnList() => new List<object> { 5, 10, 15, 20, 30, 45, 60, 90 };

    private static IReadOnlyList<object> BuildSpeedList()
    {
        var items = new List<object>(21);
        for (int s = 150; s <= 350; s += 10)
        {
            items.Add(s);
        }

        return items;
    }

    private static IReadOnlyList<object> BuildFullAltitudeList(double fieldElevation)
    {
        var items = new List<object>();
        int lowThreshold = (int)(fieldElevation + 5000);

        int roundedLow = (int)(Math.Ceiling(fieldElevation / 100.0) * 100);
        if (roundedLow < 100)
        {
            roundedLow = 100;
        }

        for (int alt = roundedLow; alt < lowThreshold; alt += 100)
        {
            items.Add(alt);
        }

        int start500 = (int)(Math.Ceiling(lowThreshold / 500.0) * 500);
        for (int alt = start500; alt <= 60000; alt += 500)
        {
            items.Add(alt);
        }

        return items;
    }

    private static string FormatAltitude(int alt) => alt >= 18000 ? $"FL{alt / 100}" : $"{alt}";

    private static IReadOnlyList<object> BuildRouteFixList(AircraftModel ac) => FixSuggester.CollectRouteFixNames(ac).Cast<object>().ToList();
}
