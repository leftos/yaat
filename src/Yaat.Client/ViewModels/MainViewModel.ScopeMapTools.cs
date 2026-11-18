using System.Globalization;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The map tools' <c>.scope</c> verbs: <c>.scope mva</c>, <c>.scope lock</c>, <c>.scope center</c>, <c>.scope rr</c>,
/// <c>.scope wx</c>, <c>.scope fix</c> and <c>.scope dcnf</c>; the marker verbs <c>.ff</c>, <c>.marker</c>, <c>.markers</c> and
/// <c>.nomarkers</c> on the open Scope; and the weather lines the Scope's overlay draws.
/// </summary>
public partial class MainViewModel
{
    private const string ScopeFixUsage = "Usage: .scope fix <on|off>";

    private const string ScopeMvaUsage = "Usage: .scope mva <on|off>";

    private const string ScopeLockUsage = "Usage: .scope lock <on|off>";

    private const string ScopeCenterUsage = "Usage: .scope center";

    private const string ScopeRrUsage = "Usage: .scope rr <on|off|place|center> | .scope rr size <2|5|10|15|20>";

    private const string ScopeWxUsage = "Usage: .scope wx <on|off>";

    private const string ScopeDcnfUsage = "Usage: .scope dcnf <off|snap|free>";

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
        ["FIX"] = SetScopeFix,
        ["DCNF"] = SetScopeDcnf,
    };

    /// <summary>Runs a marker verb as typed into the command line would. Test seam.</summary>
    internal void RunScopeMarkerCommand(string text) => TryHandleScopeMarkerCommand(text, new CommandLineSink(this));

    /// <summary>
    /// Handles the client-only marker dot commands (CRC parity) on the open Scope: <c>.ff</c>, <c>.marker</c> and
    /// <c>.markers</c> toggle one or more fix, NAVAID or FRD markers, and <c>.nomarkers</c> clears them all; with no Scope
    /// open, an error. Never sent to the server.
    /// </summary>
    /// <returns>False when the verb is none of these, so the caller can fall through to CRC aliases.</returns>
    private bool TryHandleScopeMarkerCommand(string text, ICommandTextSink sink)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0].ToUpperInvariant();
        if (verb is not (".NOMARKERS" or ".FF" or ".MARKER" or ".MARKERS"))
        {
            return false;
        }

        ScopeViewModel scope = OpenScope;
        if (verb == ".NOMARKERS")
        {
            scope.ClearMarkers();
            sink.ReportStatus("Scope markers cleared");
        }
        else
        {
            ToggleScopeMarkers(scope, parts, sink);
        }

        return true;
    }

    /// <summary>Toggles the markers <paramref name="parts"/> name after the verb, naming those no point answers to.</summary>
    private static void ToggleScopeMarkers(ScopeViewModel scope, string[] parts, ICommandTextSink sink)
    {
        if (parts.Length < 2)
        {
            sink.ReportError($"Usage: {parts[0]} <fix> [fix ...]");
            return;
        }

        List<string> unresolved = [.. parts.Skip(1).Where(token => !scope.ToggleMarker(token)).Select(token => token.ToUpperInvariant())];
        sink.ReportStatus(unresolved.Count > 0 ? $"Scope markers updated; unknown fix: {string.Join(", ", unresolved)}" : "Scope markers updated");
    }

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

    partial void RunMapToolsVerb(ScopeViewModel scope, string verb, string[] parts, ref string? result)
    {
        if ((result is null) && MapToolVerbs.TryGetValue(verb, out Func<ScopeViewModel, string[], string>? handler))
        {
            result = handler(scope, parts);
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

    /// <summary><c>.scope fix on|off</c>: turns the FIX overlay on or off.</summary>
    private static string SetScopeFix(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ParseOnOff(parts[2]) is not { } on))
        {
            return ScopeFixUsage;
        }

        scope.ShowFixes = on;
        return on ? "Scope FIX ON" : "Scope FIX OFF";
    }

    /// <summary><c>.scope dcnf off|snap|free</c>: sets datablock deconfliction, as DISPLAY's DCNF steps it.</summary>
    private static string SetScopeDcnf(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ScopeDeconfliction.ParseMode(parts[2]) is not { } mode))
        {
            return ScopeDcnfUsage;
        }

        scope.DeconflictMode = mode;
        return $"Scope DCNF {ScopeDeconfliction.NameOf(mode).ToUpperInvariant()}";
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
