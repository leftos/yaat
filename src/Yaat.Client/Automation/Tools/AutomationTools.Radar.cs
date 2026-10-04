using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim.Data;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    /// <summary>The radar range limits, the ones the range buttons and scroll keep to (<see cref="RadarViewModel.AdjustRange"/>).</summary>
    private const double MinRangeNm = 1;

    private const double MaxRangeNm = 256;

    /// <summary>The PTL length limits, the ones the radar's PTL control keeps to (<see cref="RadarViewModel.AdjustPtlLength"/>).</summary>
    private const double MinPtlMinutes = 0.5;

    private const double MaxPtlMinutes = 3.0;

    private static readonly JsonSerializerOptions FramingJsonOptions = new(JsonSerializerDefaults.Web);

    [AutomationTool("center_radar", "Centres the primary radar view on an aircraft at a range.", nameof(RadarNotReady))]
    public Task<AppToolOutcome> CenterRadar(
        [Description("The aircraft's callsign, matched case-insensitively.")] string callsign,
        [Description("The radar range to show, in nm, from 1 to 256.")] double rangeNm
    )
    {
        ValidateRange(rangeNm);
        if (FindAircraft(callsign) is not { } aircraft)
        {
            return Task.FromResult(NoAircraft(callsign));
        }

        CentreRadar(aircraft.Position.Lat, aircraft.Position.Lon, rangeNm);
        return Task.FromResult(
            AppToolOutcome.Done(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Primary radar centred on {aircraft.Callsign} ({aircraft.Position.Lat:F4}, {aircraft.Position.Lon:F4}) at {rangeNm} nm."
                )
            )
        );
    }

    [AutomationTool("center_radar_at", "Centres the primary radar view on a latitude and longitude at a range.", nameof(RadarNotReady))]
    public Task<AppToolOutcome> CenterRadarAt(
        [Description("The centre's latitude in degrees, from -90 to 90.")] double lat,
        [Description("The centre's longitude in degrees, from -180 to 180.")] double lon,
        [Description("The radar range to show, in nm, from 1 to 256.")] double rangeNm
    )
    {
        ValidateLatitude("lat", lat);
        ValidateLongitude("lon", lon);
        ValidateRange(rangeNm);
        CentreRadar(lat, lon, rangeNm);
        return Task.FromResult(
            AppToolOutcome.Done(string.Create(CultureInfo.InvariantCulture, $"Primary radar centred on ({lat:F4}, {lon:F4}) at {rangeNm} nm."))
        );
    }

    /// <summary>
    /// Resolves the name as the <c>.rbl</c> command and <c>place_rbl</c> resolve a fix endpoint (<see cref="FrdResolver"/> over the
    /// loaded navigation data), so a fix, an airport or an FRD all work.
    /// </summary>
    [AutomationTool("center_radar_on_fix", "Centres the primary radar view on a fix or airport at a range.", nameof(RadarNotReady))]
    public Task<AppToolOutcome> CenterRadarOnFix(
        [Description("The fix or airport identifier, e.g. OAK or KSFO; an FRD such as OAK090010 also works.")] string fix,
        [Description("The radar range to show, in nm, from 1 to 256.")] double rangeNm
    )
    {
        ValidateRange(rangeNm);
        if (!_viewModel.CommandInput.NavDbReady)
        {
            return Task.FromResult(AppToolOutcome.Unavailable("The navigation data is still loading."));
        }

        string name = fix.Trim().ToUpperInvariant();
        if (FrdResolver.Resolve(name, NavigationDatabase.Instance) is not { } centre)
        {
            return Task.FromResult(AppToolOutcome.Unavailable($"No fix or airport named {fix} in the navigation data."));
        }

        CentreRadar(centre.Lat, centre.Lon, rangeNm);
        return Task.FromResult(
            AppToolOutcome.Done(
                string.Create(CultureInfo.InvariantCulture, $"Primary radar centred on {name} ({centre.Lat:F4}, {centre.Lon:F4}) at {rangeNm} nm.")
            )
        );
    }

    /// <summary>Sets the primary radar's PTL length and its all-tracks switch; the own-tracks switch is left as it is.</summary>
    [AutomationTool(
        "set_ptl",
        "Sets the primary radar's predicted track line length and turns PTLs for all tracks on or off.",
        nameof(RadarNotReady)
    )]
    public Task<AppToolOutcome> SetPtl(
        [Description("The PTL length in minutes, from 0.5 to 3.0 in 0.5 steps.")] double lengthMinutes,
        [Description("True to show a PTL on every track, false to turn the all-tracks PTL off.")] bool all
    )
    {
        ValidatePtlLength("lengthMinutes", lengthMinutes);
        RadarViewModel radar = _viewModel.Radar;
        radar.PtlLengthMinutes = lengthMinutes;
        radar.PtlAll = all;
        string state = all ? "on" : "off";
        return Task.FromResult(AppToolOutcome.Done(string.Create(CultureInfo.InvariantCulture, $"PTL {lengthMinutes:F1} min, all tracks {state}.")));
    }

    [AutomationTool(
        "get_framing",
        "Reports the primary radar's framing and the sim clock as one JSON object: centre, range, enabled video maps, PTL, the "
            + "primary radar's RBL slots, sim rate, pause state and sim time.",
        nameof(RadarNotReady)
    )]
    public Task<AppToolOutcome> GetFraming() => Task.FromResult(AppToolOutcome.Done(FramingJson(_state.SimRate, _state.IsPaused)));

    /// <summary>The framing answer for <paramref name="simRate"/> and <paramref name="paused"/>; the rest is the client's state now.</summary>
    private string FramingJson(int simRate, bool paused)
    {
        RadarViewModel radar = _viewModel.Radar;
        Framing framing = new(
            radar.CenterLat,
            radar.CenterLon,
            radar.RangeNm,
            [
                .. radar
                    .MapToggles.Where(toggle => toggle.IsEnabled)
                    .OrderBy(toggle => toggle.StarsId)
                    .Select(toggle => new FramingMap(toggle.StarsId, toggle.Name)),
            ],
            new FramingPtl(radar.PtlLengthMinutes, radar.PtlAll, radar.PtlOwn),
            [.. PrimaryRadarRbls().Select(line => line.Slot).Order()],
            simRate,
            paused,
            _state.ScenarioElapsedSeconds
        );
        return JsonSerializer.Serialize(framing, FramingJsonOptions);
    }

    /// <summary>
    /// The primary radar's range-bearing lines: the shared store also holds the Ground view's, and <c>place_rbl</c> places
    /// its lines on the radar view.
    /// </summary>
    private IEnumerable<RangeBearingLine> PrimaryRadarRbls() => _viewModel.Measure.Lines.Where(line => line.View == RadarViewModel.MeasureView);

    private void CentreRadar(double lat, double lon, double rangeNm)
    {
        RadarViewModel radar = _viewModel.Radar;
        radar.CenterLat = lat;
        radar.CenterLon = lon;
        radar.RangeNm = rangeNm;
    }

    private static void ValidateRange(double rangeNm)
    {
        if (rangeNm is < MinRangeNm or > MaxRangeNm)
        {
            throw new AppToolArgumentException(
                "rangeNm",
                string.Create(CultureInfo.InvariantCulture, $"'rangeNm' must be from {MinRangeNm} to {MaxRangeNm} nm, not {rangeNm}.")
            );
        }
    }

    private static void ValidateLatitude(string parameter, double lat)
    {
        if (lat is < -90 or > 90)
        {
            throw new AppToolArgumentException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"'{parameter}' must be from -90 to 90 degrees, not {lat}.")
            );
        }
    }

    private static void ValidateLongitude(string parameter, double lon)
    {
        if (lon is < -180 or > 180)
        {
            throw new AppToolArgumentException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"'{parameter}' must be from -180 to 180 degrees, not {lon}.")
            );
        }
    }

    /// <summary>Rejects a PTL length the radar's own PTL control cannot set (<see cref="RadarViewModel.AdjustPtlLength"/>).</summary>
    private static void ValidatePtlLength(string parameter, double lengthMinutes)
    {
        double halfMinutes = lengthMinutes * 2;
        if ((lengthMinutes is < MinPtlMinutes or > MaxPtlMinutes) || (halfMinutes != Math.Round(halfMinutes)))
        {
            throw new AppToolArgumentException(
                parameter,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{parameter}' must be from {MinPtlMinutes:F1} to {MaxPtlMinutes:F1} minutes in 0.5 steps, not {lengthMinutes}."
                )
            );
        }
    }

    /// <summary>The <c>get_framing</c> object; its property names are the JSON keys, camel-cased, in this order.</summary>
    private sealed record Framing(
        double CenterLat,
        double CenterLon,
        double RangeNm,
        IReadOnlyList<FramingMap> VideoMaps,
        FramingPtl Ptl,
        IReadOnlyList<int> Rbls,
        int SimRate,
        bool Paused,
        double SimSeconds
    );

    private sealed record FramingMap(int StarsId, string Name);

    private sealed record FramingPtl(double LengthMinutes, bool All, bool Own);

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
