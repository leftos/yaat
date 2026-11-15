using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim.Data;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The <c>.scope</c> dot commands: client-only, never sent to the server. They act on the open YAAT Scope; its toolbar
/// runs the same commands, so a typed command and a button click change the scope and echo in the status line alike.
/// </summary>
public partial class MainViewModel
{
    private const string ScopeUsage =
        "Usage: .scope range <1-256> | .scope template <stars|eram|mixed|euroscope> | .scope brite <group> <value>"
        + " | .scope map <id> <on|off> | .scope db <item> <value>"
        + " | .scope tearoff <button> <corner> <dx> <dy> | .scope tearoff delete <button>...";

    private const string ScopeTearoffUsage = "Usage: .scope tearoff <button> <tl|tr|bl|br> <dx> <dy> | .scope tearoff delete <button>...";

    private const string ScopeRangeUsage = "Usage: .scope range <1-256>";

    private const string ScopeBriteUsage = "Usage: .scope brite <group> <value>: mapa, mapb, targets, fdb, alerts 0-100; line4 -20-0; dwell 0-20";

    private const string ScopeMapUsage = "Usage: .scope map <id> <on|off> | .scope map clear";

    private const string ScopeDbUsage = "Usage: .scope db <4thline|dwell|atpa> <on|off> | .scope db char <8-24>";

    /// <summary>The open YAAT Scope's view model, or null when no Scope window is open.</summary>
    public ScopeViewModel? OpenScope { get; set; }

    /// <summary>Runs a <c>.scope</c> command from the Scope's toolbar; nothing is added to the command history.</summary>
    public void RunScopeCommand(string text) => TryHandleScopeCommand(text);

    /// <summary>The parsed data of the video map with <paramref name="mapId"/> from the video-map cache, or null when it is not loaded.</summary>
    public VideoMapData? GetCachedVideoMap(string mapId) => _videoMapService.GetCached(mapId);

    /// <summary>Puts <paramref name="data"/> in the video-map cache, as a facility's map load would. Test seam.</summary>
    internal void CacheVideoMapForTesting(VideoMapData data) => _videoMapService.CacheForTesting(data);

    /// <summary>
    /// Handles <c>.scope range</c>, <c>.scope template</c>, <c>.scope brite</c>, <c>.scope map</c>, <c>.scope db</c> and
    /// <c>.scope tearoff</c> against <see cref="OpenScope"/>, writing the echo or the error to the status line.
    /// </summary>
    /// <returns>False when the first token is not <c>.scope</c>, so the caller can fall through to CRC aliases.</returns>
    private bool TryHandleScopeCommand(string text)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if ((parts.Length == 0) || !string.Equals(parts[0], ".scope", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        StatusText = RunScopeVerb(parts);
        return true;
    }

    /// <summary>Runs the verb in <paramref name="parts"/>[1] and returns the status text.</summary>
    private string RunScopeVerb(string[] parts)
    {
        string verb = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
        Func<ScopeViewModel, string[], string>? handler = verb switch
        {
            "RANGE" => SetScopeRange,
            "TEMPLATE" => PickScopeTemplate,
            "BRITE" => SetScopeBrightness,
            "MAP" => SetScopeMap,
            "DB" => SetScopeDb,
            "TEAROFF" => SetScopeTearoff,
            _ => null,
        };
        if (handler is null)
        {
            return ScopeUsage;
        }

        return OpenScope is { } scope ? handler(scope, parts) : "No YAAT Scope is open";
    }

    /// <summary><c>.scope map &lt;starsId&gt; on|off</c> or <c>.scope map clear</c>.</summary>
    private static string SetScopeMap(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length == 3) && string.Equals(parts[2], "clear", StringComparison.OrdinalIgnoreCase))
        {
            scope.ClearMaps();
            return ScopeToolbarButtons.MapClearCommand;
        }

        if ((parts.Length != 4) || (ParseOnOff(parts[3]) is not { } on))
        {
            return ScopeMapUsage;
        }

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int starsId) || !scope.SetMapOn(starsId, on))
        {
            return $"Unknown video map: {parts[2]}";
        }

        return ScopeToolbarButtons.MapCommand(starsId, on);
    }

    /// <summary><c>.scope db 4thline|dwell|atpa on|off</c> or <c>.scope db char &lt;8-24&gt;</c>.</summary>
    private static string SetScopeDb(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length < 3)
        {
            return ScopeDbUsage;
        }

        string item = parts[2].ToLowerInvariant();
        if (item == ScopeToolbarButtons.DbCharItem)
        {
            return SetScopeDbChar(scope, parts);
        }

        if (!ScopeViewModel.IsDbToggleItem(item))
        {
            return $"Unknown scope DB item: {parts[2]}";
        }

        if ((parts.Length != 4) || (ParseOnOff(parts[3]) is not { } on))
        {
            return ScopeDbUsage;
        }

        scope.SetDbToggle(item, on);
        return ScopeToolbarButtons.DbToggleCommand(item, on);
    }

    private static string SetScopeDbChar(ScopeViewModel scope, string[] parts)
    {
        if (
            (parts.Length != 4)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int size)
            || !ScopeDbSettings.IsCharSize(size)
        )
        {
            return ScopeDbUsage;
        }

        scope.SetDbCharSize(size);
        return ScopeToolbarButtons.DbCharCommand(size);
    }

    /// <summary>
    /// <c>.scope tearoff &lt;button&gt; tl|tr|bl|br &lt;dx&gt; &lt;dy&gt;</c> (tear the button off, or move its tear-off) or
    /// <c>.scope tearoff delete &lt;button&gt;...</c>.
    /// </summary>
    private static string SetScopeTearoff(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length >= 3) && string.Equals(parts[2], "delete", StringComparison.OrdinalIgnoreCase))
        {
            return DeleteScopeTearoffs(scope, parts);
        }

        if (!TryParsePlacement(parts, out ScopePlacement? place))
        {
            return ScopeTearoffUsage;
        }

        if (!ScopeToolbarButtons.TryParseKey(parts[2], out ScopeButtonKey? key))
        {
            return $"Unknown scope button: {parts[2]}";
        }

        if ((key.Id == ScopeButtonId.MapToggle) && (scope.FindMapToggle(key.Value ?? "") is null))
        {
            return $"Unknown video map: {key.Value}";
        }

        scope.PlaceTearoff(key, place);
        return ScopeToolbarButtons.TearoffCommand(key, place);
    }

    /// <summary>
    /// The placement in <c>.scope tearoff &lt;button&gt; &lt;corner&gt; &lt;dx&gt; &lt;dy&gt;</c>: six parts, a corner name and
    /// two non-negative whole offsets.
    /// </summary>
    /// <returns>False when the parts are no such placement.</returns>
    private static bool TryParsePlacement(string[] parts, [NotNullWhen(true)] out ScopePlacement? place)
    {
        place =
            (
                (parts.Length == 6)
                && ScopePlacement.TryParseCorner(parts[3], out ScopeCorner corner)
                && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out int dx)
                && int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out int dy)
            )
                ? new ScopePlacement(corner, dx, dy)
                : null;
        return place is not null;
    }

    /// <summary><c>.scope tearoff delete &lt;button&gt;...</c>: all or nothing, every name torn off.</summary>
    private static string DeleteScopeTearoffs(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length < 4)
        {
            return ScopeTearoffUsage;
        }

        List<ScopeButtonKey> keys = [];
        foreach (string name in parts.Skip(3))
        {
            if (!ScopeToolbarButtons.TryParseKey(name, out ScopeButtonKey? key))
            {
                return $"Unknown scope button: {name}";
            }

            if (!scope.IsTornOff(key))
            {
                return $"No tear-off: {name}";
            }

            keys.Add(key);
        }

        scope.DeleteTearoffs(keys);
        return ScopeToolbarButtons.TearoffDeleteCommand(keys);
    }

    /// <summary><c>on</c> is true and <c>off</c> false (case-insensitive); any other word is null.</summary>
    private static bool? ParseOnOff(string word) =>
        word.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };

    private static string SetScopeBrightness(ScopeViewModel scope, string[] parts)
    {
        if (
            (parts.Length != 4)
            || (ScopeBrightness.FindGroup(parts[2]) is not { } group)
            || !int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            || !group.Contains(value)
        )
        {
            return ScopeBriteUsage;
        }

        scope.SetBrightness(group.Name, value);
        return ScopeToolbarButtons.BriteCommand(group.Name, value);
    }

    private static string SetScopeRange(ScopeViewModel scope, string[] parts)
    {
        if (
            (parts.Length != 3)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int rangeNm)
            || (rangeNm is < 1 or > 256)
        )
        {
            return ScopeRangeUsage;
        }

        scope.RangeNm = rangeNm;
        return ScopeToolbarButtons.RangeCommand(rangeNm);
    }

    private static string PickScopeTemplate(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 3)
        {
            return ScopeUsage;
        }

        string id = parts[2].ToLowerInvariant();
        if (!ScopeTemplates.IsSelectable(id))
        {
            return $"Unknown scope template: {parts[2]}";
        }

        scope.PickTemplateCommand.Execute(id);
        return ScopeToolbarButtons.TemplateCommand(id);
    }
}
