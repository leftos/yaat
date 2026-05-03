using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Handoff text-entry popup. Focused TextBox accepts a position alias (Enter to dispatch
/// HO &lt;position&gt;, Esc to cancel). Includes an "Accept handoff" action button when an
/// inbound handoff is pending.
/// </summary>
internal static class HandoffFlyout
{
    public static Popup Build(Control anchor, AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var subtitle = string.IsNullOrEmpty(aircraft.HandoffDisplay) ? null : $"Pending: {aircraft.HandoffDisplay}";

        var actions = new List<(string Label, Func<Task> Action)>();
        if (!string.IsNullOrEmpty(aircraft.HandoffDisplay))
        {
            actions.Add(("Accept handoff", () => radarVm.AcceptHandoffAsync(aircraft.Callsign, initials)));
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
                var trimmed = value.Trim();
                if (trimmed.Length == 0)
                {
                    return;
                }
                await radarVm.InitiateHandoffAsync(aircraft.Callsign, initials, trimmed);
            }
        );
    }
}
