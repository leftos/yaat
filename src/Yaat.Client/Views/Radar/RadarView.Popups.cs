using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Models;

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

    private void OnInputPopupSubmit(object? sender, RoutedEventArgs e)
    {
        SubmitInputPopup();
    }

    private void OnInputPopupCancel(object? sender, RoutedEventArgs e)
    {
        CloseInputPopup();
    }

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

    private void ShowListPopup(IReadOnlyList<object> items, object? selectedValue, Func<object, Task> action)
    {
        _pendingListAction = action;
        _listPopupInitializing = true;
        var popup = this.FindControl<Popup>("ListPopup");
        var listBox = this.FindControl<ListBox>("ListPopupItems");
        if (popup is null || listBox is null)
        {
            _listPopupInitializing = false;
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

        _listPopupInitializing = false;
    }

    private static int FindExactIndex(IReadOnlyList<object> items, object target)
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

    private static int FindClosestIndex(IReadOnlyList<object> items, object target)
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

    // --- Filtered list popup ---

    private void ShowFilteredListPopup(string[] sortedNames, Func<string, Task> action, IReadOnlyList<object>? priorityItems = null)
    {
        _pendingFilteredListAction = action;
        _filteredListAllNames = sortedNames;
        var popup = this.FindControl<Popup>("FilteredListPopup");
        var textBox = this.FindControl<TextBox>("FilteredListText");
        var listBox = this.FindControl<ListBox>("FilteredListItems");
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
        var textBox = this.FindControl<TextBox>("FilteredListText");
        var listBox = this.FindControl<ListBox>("FilteredListItems");
        if (textBox is null || listBox is null || _filteredListAllNames is null)
        {
            return;
        }

        var prefix = textBox.Text?.Trim().ToUpperInvariant() ?? "";
        if (prefix.Length == 0)
        {
            listBox.ItemsSource = Array.Empty<object>();
            return;
        }

        var results = PrefixSearch(_filteredListAllNames, prefix, 50);
        listBox.ItemsSource = results;
        if (results.Count > 0)
        {
            listBox.SelectedIndex = 0;
        }
    }

    private void OnFilteredListKeyDown(object? sender, KeyEventArgs e)
    {
        var listBox = this.FindControl<ListBox>("FilteredListItems");
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

        var textBox = this.FindControl<TextBox>("FilteredListText");
        if (textBox is not null && textBox.IsFocused)
        {
            return;
        }

        var selected = e.AddedItems[0]?.ToString();
        if (!string.IsNullOrEmpty(selected))
        {
            var action = _pendingFilteredListAction;
            CloseFilteredListPopup();
            _ = action(selected);
        }
    }

    private void SubmitFilteredListPopup()
    {
        var textBox = this.FindControl<TextBox>("FilteredListText");
        var listBox = this.FindControl<ListBox>("FilteredListItems");
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
            var action = _pendingFilteredListAction;
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
        var popup = this.FindControl<Popup>("FilteredListPopup");
        var listBox = this.FindControl<ListBox>("FilteredListItems");
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
        var idx = Array.BinarySearch(sortedNames, prefix, StringComparer.OrdinalIgnoreCase);
        if (idx < 0)
        {
            idx = ~idx;
        }

        for (var i = idx; i < sortedNames.Length && results.Count < maxResults; i++)
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

    private static IReadOnlyList<object> BuildFullAltitudeList(double fieldElevation)
    {
        var items = new List<object>();
        var lowThreshold = (int)(fieldElevation + 5000);

        var roundedLow = (int)(Math.Ceiling(fieldElevation / 100.0) * 100);
        if (roundedLow < 100)
        {
            roundedLow = 100;
        }

        for (var alt = roundedLow; alt < lowThreshold; alt += 100)
        {
            items.Add(alt);
        }

        var start500 = (int)(Math.Ceiling(lowThreshold / 500.0) * 500);
        for (var alt = start500; alt <= 60000; alt += 500)
        {
            items.Add(alt);
        }

        return items;
    }

    private static string FormatAltitude(int alt)
    {
        return alt >= 18000 ? $"FL{alt / 100}" : $"{alt}";
    }

    private static IReadOnlyList<object> BuildRouteFixList(AircraftModel ac)
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
}
