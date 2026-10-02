using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// The waypoint-condition popup for radar route drawing.
/// </summary>
public partial class RadarView
{
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
        popup?.IsOpen = false;
    }
}
