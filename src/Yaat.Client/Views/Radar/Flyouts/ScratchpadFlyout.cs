using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Scratchpad text-entry popup. Focused TextBox pre-filled with the current value;
/// Enter submits, Esc cancels, click-outside dismisses. Below the input are
/// EuroScope-convention preset buttons (CLEA / NOTC / ST-UP / PUSH / TAXI / DEPA -- see
/// docs/euroscope/flight-data.md).
/// </summary>
internal static class ScratchpadFlyout
{
    private static readonly (string Label, string Value)[] Presets =
    [
        ("CLEA", "CLEA"),
        ("NOTC", "NOTC"),
        ("ST-UP", "ST-UP"),
        ("PUSH", "PUSH"),
        ("TAXI", "TAXI"),
        ("DEPA", "DEPA"),
    ];

    public static Popup Build(Control anchor, AircraftModel aircraft, RadarViewModel radarVm, string initials, int slot)
    {
        var verb = slot == 2 ? "SP2" : "SP1";
        var current = slot == 2 ? aircraft.Scratchpad2 : aircraft.Scratchpad1;
        var subtitle = string.IsNullOrEmpty(current) ? null : $"Current: \"{current}\"";

        return TextEntryPopup.Build(
            anchor,
            title: $"Scratchpad {slot} — {aircraft.Callsign}",
            subtitle: subtitle,
            initialText: current ?? "",
            watermark: $"Scratchpad {slot}",
            presets: Presets,
            extraActions: [],
            onSubmit: async value =>
            {
                var trimmed = value.Trim();
                var command = trimmed.Length == 0 ? verb : $"{verb} {trimmed}";
                await radarVm.SendRawCommandAsync(aircraft.Callsign, initials, command);
            }
        );
    }
}
