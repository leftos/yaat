using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// The popups every view's menu host opens its pickers through: free-text input, a list, the warp popup, and the Command…
/// and Note… flyouts. Each popup is code-built into its anchor's overlay layer with <see cref="PlacementMode.Pointer"/>, so
/// it opens at the pointer on the radar, the ground view and the aircraft list alike. Every popup light-dismisses on a click
/// outside it; the input and warp popups also close on Escape, while the list popup light-dismisses only. A popup opens on
/// the next dispatcher turn so the context menu closing in the same message does not dismiss it at once.
/// </summary>
internal static class MenuPopups
{
    private static readonly ILogger Log = AppLog.CreateLogger("MenuPopups");

    private static readonly IBrush InputBackground = new SolidColorBrush(Color.FromArgb(240, 24, 24, 24));
    private static readonly IBrush InputBorderBrush = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80));
    private static readonly IBrush InputRefusalForeground = new SolidColorBrush(Color.Parse("#e08a8a"));
    private static readonly IBrush PickerBackground = new SolidColorBrush(Color.Parse("#222"));
    private static readonly IBrush PickerBorderBrush = new SolidColorBrush(Color.Parse("#555"));
    private static readonly IBrush PickerForeground = new SolidColorBrush(Color.Parse("#CCC"));
    private static readonly IBrush WarpHeaderForeground = new SolidColorBrush(Color.Parse("#DDD"));
    private static readonly IBrush WarpLabelForeground = new SolidColorBrush(Color.Parse("#999"));

    /// <summary>The values the warp popup opens with; a heading, altitude or speed of zero or less opens blank.</summary>
    internal sealed record WarpSeed(string Callsign, string Frd, int Heading, int Altitude, int Speed);

    /// <summary>
    /// Opens a focused free-text box showing <paramref name="placeholder"/> as its prompt, holding
    /// <paramref name="initialText"/> with the caret at <paramref name="caretIndex"/> (clamped to the text). Enter or OK
    /// hands the trimmed text to <paramref name="onSubmit"/>: a null return closes the popup, and a returned reason
    /// keeps it open and shows that one line under the box, the typed text left as it was. A blank submit follows
    /// <paramref name="blank"/> — <see cref="BlankInput.Submits"/> calls it with <c>""</c>, <see cref="BlankInput.Closes"/>
    /// does not call it and closes. Clear and Escape close it without calling it. While a submit is in flight, another
    /// Enter or OK does nothing, so a slow send is never fired twice.
    /// </summary>
    public static void ShowInput(
        Control anchor,
        string placeholder,
        string initialText,
        int caretIndex,
        BlankInput blank,
        Func<string, Task<string?>> onSubmit
    )
    {
        Popup popup = NewPopup(anchor);
        var textBox = new TextBox
        {
            Text = initialText,
            PlaceholderText = placeholder,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var refusal = new TextBlock
        {
            Foreground = InputRefusalForeground,
            FontSize = 11,
            MaxWidth = 240,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        bool submitting = false;

        async Task Submit()
        {
            if (submitting)
            {
                return;
            }

            string text = textBox.Text?.Trim() ?? "";
            if ((text.Length == 0) && (blank == BlankInput.Closes))
            {
                popup.Close();
                return;
            }

            submitting = true;
            try
            {
                if (await onSubmit(text) is { } reason)
                {
                    refusal.Text = reason;
                    refusal.IsVisible = true;
                    return;
                }
            }
            finally
            {
                submitting = false;
            }

            popup.Close();
        }

        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Submit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                popup.Close();
            }
        };

        var okButton = new Button { Content = "OK" };
        okButton.Click += async (_, _) => await Submit();
        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += (_, _) => popup.Close();

        popup.Child = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = InputBorderBrush,
            Background = InputBackground,
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        Children = { textBox, okButton, clearButton },
                    },
                    refusal,
                },
            },
        };

        int caret = Math.Clamp(caretIndex, 0, initialText.Length);
        popup.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectionStart = caret;
            textBox.SelectionEnd = caret;
            textBox.CaretIndex = caret;
        };

        Open(anchor, popup, "input");
    }

    /// <summary>
    /// Opens a list of <paramref name="items"/> with <paramref name="selected"/> (else the nearest integer item to it)
    /// selected and scrolled into view. Selecting another item closes the list and hands it to <paramref name="onPick"/>.
    /// </summary>
    public static void ShowList(Control anchor, IReadOnlyList<object> items, object? selected, Func<object, Task> onPick)
    {
        Popup popup = NewPopup(anchor);
        var listBox = new ListBox
        {
            MaxHeight = 300,
            Width = 120,
            FontSize = 12,
            Background = PickerBackground,
            Foreground = PickerForeground,
            ItemsSource = items,
        };
        ApplyMonoFont(anchor, listBox);

        bool ignoreSelection = true;
        listBox.SelectionChanged += (_, e) =>
        {
            if (ignoreSelection || (e.AddedItems.Count == 0) || (e.AddedItems[0] is not { } picked))
            {
                return;
            }

            ignoreSelection = true;
            popup.Close();
            _ = onPick(picked);
        };

        popup.Child = PickerBorder(listBox, new Thickness(0));
        popup.Opened += (_, _) =>
        {
            int index = SeedIndex(items, selected);
            if (index >= 0)
            {
                listBox.SelectedIndex = index;
                listBox.ScrollIntoView(items[index]);
            }

            ignoreSelection = false;
        };

        Open(anchor, popup, "list");
    }

    /// <summary>
    /// Opens the warp form for <see cref="WarpSeed.Callsign"/>: fix or FRD, heading, altitude (feet) and speed (knots
    /// IAS), seeded from <paramref name="seed"/>, the FRD box focused. Enter in any box or OK closes it and, when the FRD
    /// is not blank and the three numbers parse, hands them to <paramref name="onSubmit"/>; Cancel and Escape close it.
    /// </summary>
    public static void ShowWarp(Control anchor, WarpSeed seed, Func<string, int, int, int, Task> onSubmit)
    {
        Popup popup = NewPopup(anchor);
        TextBox frdBox = WarpBox(anchor, "e.g. SFO or SFO180010", seed.Frd);
        TextBox headingBox = WarpBox(anchor, "0-360", PositiveOrBlank(seed.Heading));
        TextBox altitudeBox = WarpBox(anchor, "e.g. 5000", PositiveOrBlank(seed.Altitude));
        TextBox speedBox = WarpBox(anchor, "e.g. 250", PositiveOrBlank(seed.Speed));

        void Submit()
        {
            string? frd = frdBox.Text?.Trim();
            popup.Close();
            if (
                string.IsNullOrEmpty(frd)
                || !int.TryParse(headingBox.Text?.Trim(), out int heading)
                || !int.TryParse(altitudeBox.Text?.Trim(), out int altitude)
                || !int.TryParse(speedBox.Text?.Trim(), out int speed)
            )
            {
                return;
            }

            _ = onSubmit(frd, heading, altitude, speed);
        }

        TextBox[] boxes = [frdBox, headingBox, altitudeBox, speedBox];
        foreach (TextBox box in boxes)
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Submit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    popup.Close();
                    e.Handled = true;
                }
            };
        }

        var okButton = new Button
        {
            Content = "OK",
            Padding = new Thickness(8, 2),
            FontSize = 11,
        };
        okButton.Click += (_, _) => Submit();
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(8, 2),
            FontSize = 11,
        };
        cancelButton.Click += (_, _) => popup.Close();

        var form = new StackPanel { Spacing = 6, Width = 280 };
        form.Children.Add(
            new TextBlock
            {
                Text = $"Warp {seed.Callsign}",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = WarpHeaderForeground,
            }
        );
        AddWarpField(form, "Fix or FRD", frdBox);
        AddWarpField(form, "Heading", headingBox);
        AddWarpField(form, "Altitude (feet)", altitudeBox);
        AddWarpField(form, "Speed (knots IAS)", speedBox);
        form.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
                Children = { okButton, cancelButton },
            }
        );

        popup.Child = PickerBorder(form, new Thickness(8));
        popup.Opened += (_, _) => frdBox.Focus();

        Open(anchor, popup, "warp");
    }

    /// <summary>Opens the Command… flyout for <paramref name="callsign"/> on <paramref name="anchor"/>.</summary>
    public static void ShowCommand(Control anchor, string callsign, Func<string, Task> onSubmit) =>
        Open(anchor, CommandFlyout.Build(anchor, callsign, onSubmit), "command");

    /// <summary>Opens the Note… flyout for <paramref name="callsign"/> on <paramref name="anchor"/>.</summary>
    public static void ShowNote(Control anchor, string callsign, string currentNote, Func<string, Task> sendCommand) =>
        Open(anchor, NoteFlyout.Build(anchor, callsign, currentNote, sendCommand), "note");

    private static Popup NewPopup(Control anchor) =>
        new()
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = false,
        };

    private static void Open(Control anchor, Popup popup, string kind)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        if (overlay is null)
        {
            Log.LogWarning("MenuPopups: no overlay layer for anchor {Anchor}; {Kind} popup not shown", anchor.GetType().Name, kind);
            return;
        }

        overlay.Children.Add(popup);
        popup.Closed += (s, _) =>
        {
            if (s is Popup p)
            {
                overlay.Children.Remove(p);
            }
        };
        Dispatcher.UIThread.Post(() => popup.IsOpen = true);
    }

    private static Border PickerBorder(Control child, Thickness padding) =>
        new()
        {
            Background = PickerBackground,
            BorderBrush = PickerBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = padding,
            Child = child,
        };

    private static void ApplyMonoFont(Control anchor, TemplatedControl target)
    {
        if (anchor.TryFindResource("MonoFont", out object? font) && (font is FontFamily family))
        {
            target.FontFamily = family;
        }
    }

    private static TextBox WarpBox(Control anchor, string placeholder, string text)
    {
        var box = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            FontSize = 12,
        };
        ApplyMonoFont(anchor, box);
        return box;
    }

    private static void AddWarpField(StackPanel form, string label, TextBox box)
    {
        form.Children.Add(
            new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = WarpLabelForeground,
            }
        );
        form.Children.Add(box);
    }

    private static string PositiveOrBlank(int value) => value > 0 ? value.ToString() : "";

    /// <summary>The index of <paramref name="selected"/> in <paramref name="items"/>, else -1.</summary>
    private static int SeedIndex(IReadOnlyList<object> items, object? selected)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (Equals(items[i], selected))
            {
                return i;
            }
        }

        return -1;
    }
}
