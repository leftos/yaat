using System.ComponentModel;
using System.Globalization;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    /// <summary>The radar range limits, the ones the range buttons and scroll keep to (<see cref="RadarViewModel.AdjustRange"/>).</summary>
    private const double MinRangeNm = 1;

    private const double MaxRangeNm = 256;

    [AutomationTool("center_radar", "Centres the primary radar view on an aircraft at a range.", nameof(RadarNotReady))]
    public Task<AppToolOutcome> CenterRadar(
        [Description("The aircraft's callsign, matched case-insensitively.")] string callsign,
        [Description("The radar range to show, in nm, from 1 to 256.")] double rangeNm
    )
    {
        if (rangeNm is < MinRangeNm or > MaxRangeNm)
        {
            throw new AppToolArgumentException(
                "rangeNm",
                string.Create(CultureInfo.InvariantCulture, $"'rangeNm' must be from {MinRangeNm} to {MaxRangeNm} nm, not {rangeNm}.")
            );
        }

        AircraftModel? aircraft = _viewModel.Aircraft.FirstOrDefault(candidate =>
            string.Equals(candidate.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
        );
        if (aircraft is null)
        {
            return Task.FromResult(AppToolOutcome.Unavailable($"No aircraft with callsign {callsign} in the client."));
        }

        RadarViewModel radar = _viewModel.Radar;
        radar.CenterLat = aircraft.Position.Lat;
        radar.CenterLon = aircraft.Position.Lon;
        radar.RangeNm = rangeNm;
        return Task.FromResult(
            AppToolOutcome.Done(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Primary radar centred on {aircraft.Callsign} ({aircraft.Position.Lat:F4}, {aircraft.Position.Lon:F4}) at {rangeNm} nm."
                )
            )
        );
    }

    [AutomationTool("set_video_map", "Turns one video map on the primary radar on or off.", nameof(MapsNotLoaded))]
    public Task<AppToolOutcome> SetVideoMap(
        [Description("The map's STARS id, the number its DCB button shows.")] int starsId,
        [Description("True to show the map, false to hide it.")] bool enabled
    )
    {
        RadarViewModel radar = _viewModel.Radar;
        VideoMapToggleItem? toggle = radar.FindToggleByStarsId(starsId);
        if (toggle is null)
        {
            return Task.FromResult(AppToolOutcome.Unavailable($"No video map with STARS id {starsId} in this scenario."));
        }

        // The toggle's own change handler redraws and saves the maps, as a DCB click does through ToggleMapByStarsId.
        toggle.IsEnabled = enabled;
        radar.SyncShortcutState(starsId, enabled);
        string state = enabled ? "on" : "off";
        return Task.FromResult(AppToolOutcome.Done($"Video map {starsId} {toggle.ShortName} is {state}."));
    }
}
