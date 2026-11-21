using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.EuroScopeFlyouts;

/// <summary>
/// Handoff text-entry popup. Focused TextBox accepts a position alias (Enter to dispatch
/// HO &lt;position&gt;, Esc to cancel). Includes an "Accept handoff" action button when an
/// inbound handoff is pending.
/// </summary>
internal static class EuroScopeHandoffFlyout
{
    public static Popup Build(Control anchor, AircraftModel aircraft, IMenuHost host)
    {
        string initials = host.Session.Initials;
        string? subtitle = string.IsNullOrEmpty(aircraft.HandoffDisplay) ? null : $"Pending: {aircraft.HandoffDisplay}";

        var actions = new List<(string Label, Func<Task> Action)>();
        if (!string.IsNullOrEmpty(aircraft.HandoffDisplay))
        {
            actions.Add(("Accept handoff", () => host.SendAsync(aircraft.Callsign, "ACCEPT", initials)));
        }

        return TextEntryPopup.Build(
            anchor,
            title: $"Handoff — {aircraft.Callsign}",
            subtitle: subtitle,
            initialText: "",
            watermark: "Position alias",
            presets: [],
            extraActions: actions,
            onSubmit: async value =>
            {
                string trimmed = value.Trim();
                if (trimmed.Length == 0)
                {
                    return;
                }
                await host.SendAsync(aircraft.Callsign, $"HO {trimmed}", initials);
            }
        );
    }
}
