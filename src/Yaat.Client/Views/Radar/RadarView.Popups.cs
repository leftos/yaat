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

    private static IReadOnlyList<object> BuildAltitudeList(double fieldElevation, double currentAlt, bool climb)
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
