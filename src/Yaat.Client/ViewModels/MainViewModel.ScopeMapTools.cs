using System.Globalization;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The map tools' <c>.scope</c> verbs: <c>.scope mva</c>, <c>.scope lock</c>, <c>.scope center</c>, <c>.scope rr</c> and
/// <c>.scope wx</c>; and the weather lines the Scope's overlay draws.
/// </summary>
public partial class MainViewModel
{
    private const string ScopeMvaUsage = "Usage: .scope mva <on|off>";

    private const string ScopeLockUsage = "Usage: .scope lock <on|off>";

    private const string ScopeCenterUsage = "Usage: .scope center";

    private const string ScopeRrUsage = "Usage: .scope rr <on|off|place|center> | .scope rr size <2|5|10|15|20>";

    private const string ScopeWxUsage = "Usage: .scope wx <on|off>";

    /// <summary>The answer of <c>.scope center</c> while the Scope's map source has no centre to return to.</summary>
    internal const string ScopeNoHomeCenter = "The Scope has no home centre yet";

    // The map tools' verbs, upper-cased, each with its handler.
    private static readonly Dictionary<string, Func<ScopeViewModel, string[], string>> MapToolVerbs = new(StringComparer.Ordinal)
    {
        ["MVA"] = SetScopeMva,
        ["LOCK"] = SetScopeLock,
        ["CENTER"] = RecenterScope,
        ["RR"] = SetScopeRr,
        ["WX"] = SetScopeWx,
    };

    /// <summary>
    /// The Scope's weather lines: one per METAR of the map source's position airports (every station when no position is
    /// set), or the "No METAR for" note when none matches; empty while no weather is loaded.
    /// </summary>
    internal IReadOnlyList<string> ScopeWeatherLines()
    {
        IReadOnlyList<string> airports = ScopeMaps.WeatherAirports;
        if (RadarWeatherNote(_allWeatherInfo, airports) is { } note)
        {
            return [note];
        }

        return FilterWeatherForPosition(_allWeatherInfo, airports) is { } stations ? [.. stations.Select(s => s.ToDisplayString())] : [];
    }

    partial void RunMapToolsVerb(string verb, string[] parts, ref string? result)
    {
        if ((result is null) && MapToolVerbs.TryGetValue(verb, out Func<ScopeViewModel, string[], string>? handler))
        {
            result = OpenScope is { } scope ? handler(scope, parts) : "No YAAT Scope is open";
        }
    }

    /// <summary><c>.scope mva on|off</c>: turns the MVA altitude tint on or off.</summary>
    private static string SetScopeMva(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ParseOnOff(parts[2]) is not { } on))
        {
            return ScopeMvaUsage;
        }

        scope.ShowMvaHints = on;
        return on ? "Scope MVA ON" : "Scope MVA OFF";
    }

    /// <summary><c>.scope lock on|off</c>: locks or unlocks wheel zoom and right-drag pan.</summary>
    private static string SetScopeLock(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ParseOnOff(parts[2]) is not { } on))
        {
            return ScopeLockUsage;
        }

        scope.IsPanZoomLocked = on;
        return on ? "Scope LOCK ON" : "Scope LOCK OFF";
    }

    /// <summary><c>.scope center</c>: pans back to the facility's home centre, keeping the range (OFF CNTR).</summary>
    private static string RecenterScope(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 2)
        {
            return ScopeCenterUsage;
        }

        return scope.Recenter() ? "Scope centred" : ScopeNoHomeCenter;
    }

    /// <summary><c>.scope wx on|off</c>: turns the weather overlay on or off.</summary>
    private static string SetScopeWx(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ParseOnOff(parts[2]) is not { } on))
        {
            return ScopeWxUsage;
        }

        scope.ShowWeather = on;
        return on ? "Scope WX ON" : "Scope WX OFF";
    }

    /// <summary><c>.scope rr on|off|place|center</c> and <c>.scope rr size 2|5|10|15|20</c>: the range ring.</summary>
    private static string SetScopeRr(ScopeViewModel scope, string[] parts) =>
        parts.Length switch
        {
            3 => RunScopeRrAction(scope, parts[2]),
            4 when string.Equals(parts[2], "size", StringComparison.OrdinalIgnoreCase) => SetScopeRrSize(scope, parts[3]),
            _ => ScopeRrUsage,
        };

    /// <summary>
    /// <c>.scope rr on|off</c> turns the ring on or off, <c>place</c> arms PLACE RR and <c>center</c> returns the ring to the
    /// facility's home centre (RR CNTR).
    /// </summary>
    private static string RunScopeRrAction(ScopeViewModel scope, string action)
    {
        if (ParseOnOff(action) is { } on)
        {
            scope.ShowRangeRing = on;
            return on ? "Scope RR ON" : "Scope RR OFF";
        }

        switch (action.ToUpperInvariant())
        {
            case "PLACE":
                scope.ArmRangeRingPlacement();
                return "Scope RR PLACE: click the map";
            case "CENTER":
                scope.CenterRangeRing();
                return "Scope RR CNTR";
            default:
                return ScopeRrUsage;
        }
    }

    /// <summary><c>.scope rr size N</c>: sets the ring's spacing to one of <see cref="ScopeViewModel.RangeRingSizes"/>.</summary>
    private static string SetScopeRrSize(ScopeViewModel scope, string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int sizeNm) || !ScopeViewModel.RangeRingSizes.Contains(sizeNm))
        {
            return ScopeRrUsage;
        }

        scope.RangeRingSizeNm = sizeNm;
        return $"Scope RR SIZE {sizeNm.ToString(CultureInfo.InvariantCulture)}";
    }
}
