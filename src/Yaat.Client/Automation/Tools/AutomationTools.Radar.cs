using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Sim.Data;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    /// <summary>The Scope range limits, the ones its range spinner keeps to (<see cref="ScopeViewModel.StepRange"/>).</summary>
    private const double MinRangeNm = 1;

    private const double MaxRangeNm = 256;

    /// <summary>The vector lengths <c>set_ptl</c> and <c>prepare_take</c> take, as the Scope's VECTOR spinner offers them.</summary>
    private const string VectorMinutesText = "0, 1, 2, 4 or 8";

    private static readonly JsonSerializerOptions FramingJsonOptions = new(JsonSerializerDefaults.Web);

    [AutomationTool("center_radar", "Centres the primary Scope on an aircraft at a range.", nameof(ScopeNotReady))]
    public Task<AppToolOutcome> CenterRadar(
        [Description("The aircraft's callsign, matched case-insensitively.")] string callsign,
        [Description("The Scope range to show, in nm, from 1 to 256.")] double rangeNm
    )
    {
        ValidateRange(rangeNm);
        if (FindAircraft(callsign) is not { } aircraft)
        {
            return Task.FromResult(NoAircraft(callsign));
        }

        CentreScope(aircraft.Position.Lat, aircraft.Position.Lon, rangeNm);
        return Task.FromResult(
            AppToolOutcome.Done(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Primary Scope centred on {aircraft.Callsign} ({aircraft.Position.Lat:F4}, {aircraft.Position.Lon:F4}) at {rangeNm} nm."
                )
            )
        );
    }

    [AutomationTool("center_radar_at", "Centres the primary Scope on a latitude and longitude at a range.", nameof(ScopeNotReady))]
    public Task<AppToolOutcome> CenterRadarAt(
        [Description("The centre's latitude in degrees, from -90 to 90.")] double lat,
        [Description("The centre's longitude in degrees, from -180 to 180.")] double lon,
        [Description("The Scope range to show, in nm, from 1 to 256.")] double rangeNm
    )
    {
        ValidateLatitude("lat", lat);
        ValidateLongitude("lon", lon);
        ValidateRange(rangeNm);
        CentreScope(lat, lon, rangeNm);
        return Task.FromResult(
            AppToolOutcome.Done(string.Create(CultureInfo.InvariantCulture, $"Primary Scope centred on ({lat:F4}, {lon:F4}) at {rangeNm} nm."))
        );
    }

    /// <summary>
    /// Resolves the name as the <c>.rbl</c> command and <c>place_rbl</c> resolve a fix endpoint (<see cref="FrdResolver"/> over the
    /// loaded navigation data), so a fix, an airport or an FRD all work.
    /// </summary>
    [AutomationTool("center_radar_on_fix", "Centres the primary Scope on a fix or airport at a range.", nameof(ScopeNotReady))]
    public Task<AppToolOutcome> CenterRadarOnFix(
        [Description("The fix or airport identifier, e.g. OAK or KSFO; an FRD such as OAK090010 also works.")] string fix,
        [Description("The Scope range to show, in nm, from 1 to 256.")] double rangeNm
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

        CentreScope(centre.Lat, centre.Lon, rangeNm);
        return Task.FromResult(
            AppToolOutcome.Done(
                string.Create(CultureInfo.InvariantCulture, $"Primary Scope centred on {name} ({centre.Lat:F4}, {centre.Lon:F4}) at {rangeNm} nm.")
            )
        );
    }

    /// <summary>Pins the primary Scope's vector length and sets the tracks it draws on, as VECTOR and its OWN/ALL click do.</summary>
    [AutomationTool(
        "set_ptl",
        "Sets the primary Scope's vector (predicted track line) length and whether it draws on all tracks or the viewer's own.",
        nameof(ScopeNotReady)
    )]
    public Task<AppToolOutcome> SetPtl(
        [Description("The vector length in minutes: 0, 1, 2, 4 or 8; 0 draws no vector line.")] double lengthMinutes,
        [Description("True to draw vectors on all tracks (ALL), false on the viewer's own tracks only (OWN).")] bool all
    )
    {
        ValidateVectorMinutes("lengthMinutes", lengthMinutes);
        SetScopeVector(lengthMinutes, all);
        string tracks = all ? "all" : "own";
        return Task.FromResult(AppToolOutcome.Done(string.Create(CultureInfo.InvariantCulture, $"Vector {lengthMinutes} min on {tracks} tracks.")));
    }

    [AutomationTool(
        "get_framing",
        "Reports the primary Scope's framing and the sim clock as one JSON object: centre, range, enabled video maps, vector "
            + "(ptl), the Scope's RBL slots, sim rate, pause state and sim time.",
        nameof(ScopeNotReady)
    )]
    public Task<AppToolOutcome> GetFraming() => Task.FromResult(AppToolOutcome.Done(FramingJson(_state.SimRate, _state.IsPaused)));

    /// <summary>The framing answer for <paramref name="simRate"/> and <paramref name="paused"/>; the rest is the client's state now.</summary>
    private string FramingJson(int simRate, bool paused)
    {
        ScopeViewModel scope = Scope;
        bool all = scope.VectorOwnership == ScopeVectorOwnership.All;
        Framing framing = new(
            scope.CenterLat,
            scope.CenterLon,
            scope.RangeNm,
            [
                .. scope
                    .MapToggles.Where(toggle => toggle.IsOn)
                    .OrderBy(toggle => toggle.StarsId)
                    .Select(toggle => new FramingMap(toggle.StarsId, toggle.Name)),
            ],
            new FramingPtl(scope.EffectiveBand.VectorMin, all, !all),
            [.. PrimaryScopeRbls().Select(line => line.Slot).Order()],
            simRate,
            paused,
            _state.ScenarioElapsedSeconds
        );
        return JsonSerializer.Serialize(framing, FramingJsonOptions);
    }

    /// <summary>
    /// The Scope view's range-bearing lines, which every Scope window shows: the shared store also holds the Radar's and the
    /// Ground view's, and <c>place_rbl</c> places its lines on the Scope view.
    /// </summary>
    private IEnumerable<RangeBearingLine> PrimaryScopeRbls() => _viewModel.Measure.Lines.Where(line => line.View == ScopeViewModel.MeasureView);

    private void CentreScope(double lat, double lon, double rangeNm)
    {
        ScopeViewModel scope = Scope;
        scope.CenterLat = lat;
        scope.CenterLon = lon;
        scope.RangeNm = rangeNm;
    }

    /// <summary>Pins the primary Scope's vector to <paramref name="minutes"/> and draws it on all tracks or the viewer's own.</summary>
    private void SetScopeVector(double minutes, bool all)
    {
        ScopeViewModel scope = Scope;
        scope.PinVectorMin(minutes);
        scope.VectorOwnership = all ? ScopeVectorOwnership.All : ScopeVectorOwnership.Own;
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

    /// <summary>Rejects a vector length the Scope's VECTOR spinner cannot set (<see cref="ScopeViewModel.IsVectorMinutes"/>).</summary>
    private static void ValidateVectorMinutes(string parameter, double minutes)
    {
        if (!ScopeViewModel.IsVectorMinutes(minutes))
        {
            throw new AppToolArgumentException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"'{parameter}' must be {VectorMinutesText} minutes, not {minutes}.")
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

    /// <summary>The Scope's vector: its length in minutes, and whether it draws on all tracks or the viewer's own.</summary>
    private sealed record FramingPtl(double LengthMinutes, bool All, bool Own);

    [AutomationTool("set_video_map", "Turns one video map on the primary Scope on or off.", nameof(MapsNotLoaded))]
    public Task<AppToolOutcome> SetVideoMap(
        [Description("The map's STARS id, the number its MAPS button shows.")] int starsId,
        [Description("True to show the map, false to hide it.")] bool enabled
    )
    {
        if (Scope.MapToggles.FirstOrDefault(toggle => toggle.StarsId == starsId) is not { } toggle)
        {
            return Task.FromResult(AppToolOutcome.Unavailable($"No video map with STARS id {starsId} in this scenario."));
        }

        // The toggle's own change handler redraws the Scope's maps, as a MAPS click does through .scope map.
        Scope.SetMapOn(starsId, enabled);
        string state = enabled ? "on" : "off";
        return Task.FromResult(AppToolOutcome.Done($"Video map {starsId} {toggle.ShortName} is {state}."));
    }
}
