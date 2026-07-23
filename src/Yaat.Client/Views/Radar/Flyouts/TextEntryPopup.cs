using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Builds a small Popup with a focused text box plus preset/action buttons. Used by
/// ScratchpadFlyout and HandoffFlyout where the user needs to enter free-form text but
/// also wants quick access to common values. Enter submits, Esc cancels, click outside
/// dismisses (Avalonia light dismiss).
/// </summary>
internal static class TextEntryPopup
{
    public static Popup Build(
        Control anchor,
        string title,
        string? subtitle,
        string initialText,
        string watermark,
        IEnumerable<(string Label, string Value)> presets,
        IEnumerable<(string Label, Func<Task> Action)> extraActions,
        Func<string, Task> onSubmit
    )
    {
        var popup = new Popup
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = false,
        };

        var textBox = new TextBox
        {
            Text = initialText,
            PlaceholderText = watermark,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };

        async Task Submit(string value)
        {
            popup.Close();
            await onSubmit(value);
        }

        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Submit(textBox.Text ?? "");
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                popup.Close();
            }
        };

        var okBtn = new Button { Content = "OK" };
        okBtn.Click += async (_, _) => await Submit(textBox.Text ?? "");

        var clearBtn = new Button { Content = "Clear" };
        clearBtn.Click += async (_, _) => await Submit("");

        var topRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { textBox, okBtn, clearBtn },
        };

        var presetWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, value) in presets)
        {
            var v = value;
            var btn = new Button { Content = label, Margin = new Thickness(0, 0, 4, 4) };
            btn.Click += async (_, _) => await Submit(v);
            presetWrap.Children.Add(btn);
        }

        var actionWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, action) in extraActions)
        {
            var act = action;
            var btn = new Button { Content = label, Margin = new Thickness(0, 0, 4, 4) };
            btn.Click += async (_, _) =>
            {
                popup.Close();
                await act();
            };
            actionWrap.Children.Add(btn);
        }

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            },
        };
        if (!string.IsNullOrEmpty(subtitle))
        {
            stack.Children.Add(
                new TextBlock
                {
                    Text = subtitle,
                    FontStyle = FontStyle.Italic,
                    FontSize = 11,
                    Opacity = 0.75,
                }
            );
        }
        stack.Children.Add(topRow);
        if (presetWrap.Children.Count > 0)
        {
            stack.Children.Add(presetWrap);
        }
        if (actionWrap.Children.Count > 0)
        {
            stack.Children.Add(actionWrap);
        }

        popup.Child = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80)),
            Background = new SolidColorBrush(Color.FromArgb(240, 24, 24, 24)),
            Padding = new Thickness(8),
            Child = stack,
        };

        popup.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        return popup;
    }
}
