using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The popup a middle click on a drawn waypoint opens: the waypoint's crossing altitude and the commands run at
/// it, with OK, Clear and Cancel. Enter submits from either box; a click outside it closes it and still reaches the Scope. Opened
/// in the canvas's overlay layer at the click point.
/// </summary>
internal sealed class ScopeWaypointConditionPopup
{
    private readonly Action<string?, string?> _submit;
    private OverlayLayer? _overlay;

    /// <param name="fixName">The waypoint's name, in the header.</param>
    /// <param name="altitude">The waypoint's altitude condition, or null.</param>
    /// <param name="commands">The waypoint's commands, or null.</param>
    /// <param name="submit">Takes the altitude and the commands, each null when empty; both null clears the condition.</param>
    public ScopeWaypointConditionPopup(string fixName, string? altitude, string? commands, Action<string?, string?> submit)
    {
        _submit = submit;
        Header = new TextBlock
        {
            Text = $"Conditions at {fixName}",
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("#DDD"),
        };
        AltitudeBox = NewBox("e.g. 100, A100, B100", altitude);
        CommandsBox = NewBox("e.g. SPD 250, FH 270", commands);
        Popup = new Popup
        {
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true,
            ShouldUseOverlayLayer = true,
            Child = BuildContent(),
        };
        Popup.Opened += (_, _) => AltitudeBox.Focus();
    }

    /// <summary>The popup itself.</summary>
    public Popup Popup { get; }

    /// <summary>The header naming the waypoint.</summary>
    public TextBlock Header { get; }

    /// <summary>The crossing-altitude box.</summary>
    public TextBox AltitudeBox { get; }

    /// <summary>The commands box.</summary>
    public TextBox CommandsBox { get; }

    /// <summary>Whether the popup is open.</summary>
    public bool IsOpen => Popup.IsOpen;

    /// <summary>Opens the popup over <paramref name="anchor"/> at <paramref name="at"/>, in the anchor's coordinates.</summary>
    /// <returns>False when the anchor has no overlay layer.</returns>
    public bool Open(Control anchor, Point at)
    {
        if (OverlayLayer.GetOverlayLayer(anchor) is not { } overlay)
        {
            return false;
        }

        Popup.Placement = PlacementMode.AnchorAndGravity;
        Popup.PlacementRect = new Rect(at, new Size(1, 1));
        Popup.PlacementAnchor = PopupAnchor.TopLeft;
        Popup.PlacementGravity = PopupGravity.BottomRight;
        Popup.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY;
        Popup.PlacementTarget = anchor;
        overlay.Children.Add(Popup);
        _overlay = overlay;
        Popup.Closed += LeaveOverlay;
        Popup.IsOpen = true;
        return true;
    }

    /// <summary>Closes the popup.</summary>
    public void Close() => Popup.IsOpen = false;

    /// <summary>Hands the boxes' text to the submit, each null when empty, and closes the popup: OK's and Enter's action.</summary>
    public void Submit()
    {
        _submit(EmptyAsNull(AltitudeBox.Text), EmptyAsNull(CommandsBox.Text));
        Close();
    }

    /// <summary>Clears the waypoint's condition and closes the popup: Clear's action.</summary>
    public void Clear()
    {
        _submit(null, null);
        Close();
    }

    private static string? EmptyAsNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void LeaveOverlay(object? sender, EventArgs e)
    {
        Popup.Closed -= LeaveOverlay;
        _overlay?.Children.Remove(Popup);
        _overlay = null;
    }

    private Border BuildContent()
    {
        var panel = new StackPanel { Spacing = 6, Width = 280 };
        panel.Children.Add(Header);
        panel.Children.Add(Caption("Crossing altitude", new Thickness(0)));
        panel.Children.Add(AltitudeBox);
        panel.Children.Add(
            new TextBlock
            {
                Text = "A=at or above, B=at or below, plain=exact. Hundreds of feet.",
                FontSize = 10,
                Foreground = Brush("#777"),
                TextWrapping = TextWrapping.Wrap,
            }
        );
        panel.Children.Add(Caption("Commands (executed as AT condition)", new Thickness(0, 4, 0, 0)));
        panel.Children.Add(CommandsBox);
        panel.Children.Add(Buttons());
        return new Border
        {
            Background = Brush("#222"),
            BorderBrush = Brush("#555"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(2),
            Child = panel,
        };
    }

    private StackPanel Buttons()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        row.Children.Add(NewButton("OK", Submit));
        row.Children.Add(NewButton("Clear", Clear));
        row.Children.Add(NewButton("Cancel", Close));
        return row;
    }

    private static Button NewButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(8, 2),
            FontSize = 11,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Caption(string text, Thickness margin) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = Brush("#999"),
            Margin = margin,
        };

    private TextBox NewBox(string placeholder, string? text)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            FontSize = 12,
            Text = text ?? "",
        };
        if ((Application.Current?.TryGetResource("MonoFont", null, out object? font) == true) && (font is FontFamily family))
        {
            box.FontFamily = family;
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        return box;
    }

    private static ImmutableSolidColorBrush Brush(string color) => new(Color.Parse(color));
}
