using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Handoff quick-action picker. Accept inbound handoff if one is pending; otherwise the
/// user must type 'HO &lt;position&gt;' in the command bar to initiate.
/// </summary>
internal static class HandoffFlyout
{
    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();
        menu.Items.Add(
            new MenuItem
            {
                Header = $"Handoff — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        if (!string.IsNullOrEmpty(aircraft.HandoffDisplay))
        {
            menu.Items.Add(
                new MenuItem
                {
                    Header = $"Pending: {aircraft.HandoffDisplay}",
                    IsEnabled = false,
                    FontStyle = FontStyle.Italic,
                }
            );
        }
        menu.Items.Add(new Separator());

        var accept = new MenuItem { Header = "Accept handoff" };
        accept.Click += async (_, _) => await radarVm.AcceptHandoffAsync(aircraft.Callsign, initials);
        menu.Items.Add(accept);

        menu.Items.Add(new Separator());
        menu.Items.Add(
            new MenuItem
            {
                Header = "(To initiate: type 'HO <position>' in the command bar)",
                IsEnabled = false,
                FontStyle = FontStyle.Italic,
            }
        );
        return menu;
    }
}
