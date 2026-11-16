using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
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
        + " | .scope tearoff <button> <corner> <dx> <dy> | .scope tearoff delete <button>..."
        + " | .scope pref <save|saveas|load|rename|delete> | .scope profile <stars|eram|mixed>"
        + " | .scope others <full|partial> | .scope auto";

    private const string ScopeOthersUsage = "Usage: .scope others <full|partial>";

    private const string ScopeAutoUsage = "Usage: .scope auto";

    private const string PrefUsage =
        "Usage: .scope pref save | .scope pref saveas <name> | .scope pref load <name>"
        + " | .scope pref rename <old> <new> | .scope pref delete <name>";

    private const string ScopeTearoffUsage = "Usage: .scope tearoff <button> <tl|tr|bl|br> <dx> <dy> | .scope tearoff delete <button>...";

    private const string ScopeRangeUsage = "Usage: .scope range <1-256>";

    private const string ScopeBriteUsage = "Usage: .scope brite <group> <value>: mapa, mapb, targets, fdb, alerts 0-100; line4 -20-0; dwell 0-20";

    private const string ScopeMapUsage = "Usage: .scope map <id> <on|off> | .scope map clear";

    private const string ScopeDbUsage = "Usage: .scope db <4thline|dwell|atpa> <on|off> | .scope db char <8-24>";

    private const string ScopeProfileUsage = "Usage: .scope profile <stars|eram|mixed>";

    private ScopeProfileKind? _scopeProfile;

    /// <summary>
    /// Whether <see cref="ScopeProfile"/> was picked before the student position type was known, so it is picked once more
    /// when it arrives.
    /// </summary>
    private bool _isScopeProfileProvisional;

    /// <summary>The open YAAT Scope's view model, or null when no Scope window is open.</summary>
    public ScopeViewModel? OpenScope { get; set; }

    /// <summary>
    /// The room's Scope profile, which every Scope window follows: picked once, when the first student position arrives, from
    /// the profile picked by hand for the room's student position type, else by the position's kind
    /// (<see cref="ScopeProfilePicker.KindFor"/>). A later position change keeps it; <c>.scope profile</c> and a pref-set load
    /// change it. Null until the first position, and again after an unload or at a new scenario load.
    /// </summary>
    public ScopeProfileKind? ScopeProfile
    {
        get => _scopeProfile;
        private set => SetProperty(ref _scopeProfile, value);
    }

    /// <summary>
    /// Picks <paramref name="kind"/> by hand, as <c>.scope profile</c> does, saving it for the room's student position type
    /// when that is known.
    /// </summary>
    internal void PickScopeProfileByHand(ScopeProfileKind kind)
    {
        _isScopeProfileProvisional = false;
        if (StudentPositionType is { } type)
        {
            Preferences.SetScopeProfileOverride(type, ScopeProfilePicker.NameOf(kind));
        }

        ScopeProfile = kind;
    }

    /// <summary>Switches to <paramref name="kind"/> for a pref-set load: as a pick by hand, but saved for no position type.</summary>
    internal void SetScopeProfileFromPrefSet(ScopeProfileKind kind)
    {
        _isScopeProfileProvisional = false;
        ScopeProfile = kind;
    }

    /// <summary>Clears the room's profile, so the next student position picks it again.</summary>
    private void ResetScopeProfile()
    {
        _isScopeProfileProvisional = false;
        ScopeProfile = null;
    }

    /// <summary>
    /// After the server set the active position: a cleared position (<paramref name="cleared"/>) clears the profile; the first
    /// position after a clear picks it, provisionally while the student position type is unknown.
    /// </summary>
    private void OnActivePositionForScopeProfile(bool cleared)
    {
        if (cleared)
        {
            ResetScopeProfile();
            return;
        }

        if ((ScopeProfile is not null) || (ActivePosition is null))
        {
            return;
        }

        _isScopeProfileProvisional = StudentPositionType is null;
        ScopeProfile = AutomaticScopeProfile();
    }

    /// <summary>
    /// After the student position type changed: a known type picks a provisional profile once more, now that the type's hand
    /// pick can be read. An unknown type changes nothing: a scenario without a student position has none, and the server
    /// may send it after the position.
    /// </summary>
    private void OnStudentPositionTypeForScopeProfile()
    {
        if ((StudentPositionType is not null) && _isScopeProfileProvisional)
        {
            _isScopeProfileProvisional = false;
            ScopeProfile = AutomaticScopeProfile();
        }
    }

    /// <summary>The profile picked by hand for the student position type, else the active position's kind.</summary>
    private ScopeProfileKind AutomaticScopeProfile() =>
        ScopeProfilePicker.TryParseKind(Preferences.GetScopeProfileOverride(StudentPositionType), out ScopeProfileKind kind)
            ? kind
            : ScopeProfilePicker.KindFor(ActivePosition);

    /// <summary>Runs a <c>.scope</c> command from the Scope's toolbar; nothing is added to the command history.</summary>
    public void RunScopeCommand(string text) => TryHandleScopeCommand(text);

    /// <summary>The parsed data of the video map with <paramref name="mapId"/> from the video-map cache, or null when it is not loaded.</summary>
    public VideoMapData? GetCachedVideoMap(string mapId) => _videoMapService.GetCached(mapId);

    /// <summary>Puts <paramref name="data"/> in the video-map cache, as a facility's map load would. Test seam.</summary>
    internal void CacheVideoMapForTesting(VideoMapData data) => _videoMapService.CacheForTesting(data);

    /// <summary>
    /// Handles <c>.scope range</c>, <c>.scope template</c>, <c>.scope brite</c>, <c>.scope map</c>, <c>.scope db</c>,
    /// <c>.scope tearoff</c>, <c>.scope pref</c> and <c>.scope profile</c> against <see cref="OpenScope"/>, writing the echo or the error to the
    /// status line.
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
            "PREF" => RunScopePref,
            "PROFILE" => PickScopeProfile,
            "OTHERS" => PinScopeOthers,
            "AUTO" => ClearScopePins,
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

        if (!TryParsePlacement(parts, 3, out ScopePlacement? place))
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
    /// The placement that ends a command's <paramref name="parts"/> at <paramref name="index"/>, as in <c>.scope tearoff
    /// &lt;button&gt; &lt;corner&gt; &lt;dx&gt; &lt;dy&gt;</c> and <c>.view &lt;name&gt; tear &lt;corner&gt; &lt;dx&gt; &lt;dy&gt;</c>:
    /// exactly three last parts, a corner name and two non-negative whole offsets.
    /// </summary>
    /// <returns>False when the parts are no such placement.</returns>
    private static bool TryParsePlacement(string[] parts, int index, [NotNullWhen(true)] out ScopePlacement? place)
    {
        place =
            (
                (parts.Length == (index + 3))
                && ScopePlacement.TryParseCorner(parts[index], out ScopeCorner corner)
                && int.TryParse(parts[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int dx)
                && int.TryParse(parts[index + 2], NumberStyles.None, CultureInfo.InvariantCulture, out int dy)
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

    /// <summary><c>.scope profile stars|eram|mixed</c>: the room's profile, picked by hand and saved for its student position type.</summary>
    private static string PickScopeProfile(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 3)
        {
            return ScopeProfileUsage;
        }

        if (!ScopeProfilePicker.TryParseKind(parts[2], out ScopeProfileKind kind))
        {
            return $"Unknown scope profile '{parts[2]}'";
        }

        scope.Main.PickScopeProfileByHand(kind);
        return ScopeProfileEcho(kind);
    }

    /// <summary><c>.scope others full|partial</c>: pins others' block level until <c>.scope auto</c>.</summary>
    private static string PinScopeOthers(ScopeViewModel scope, string[] parts)
    {
        string? level = parts.Length == 3 ? parts[2].ToLowerInvariant() : null;
        if ((level is null) || !ScopeViewModel.IsOthersLevel(level))
        {
            return ScopeOthersUsage;
        }

        scope.PinOthersLevel(level);
        return $"Scope others {level.ToUpperInvariant()} (pinned)";
    }

    /// <summary><c>.scope auto</c>: clears every pinned band value, so each follows the zoom band again.</summary>
    private static string ClearScopePins(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 2)
        {
            return ScopeAutoUsage;
        }

        scope.ClearPins();
        return "Scope AUTO";
    }

    /// <summary>The status line's echo of a profile change: <c>Scope profile ERAM</c>.</summary>
    internal static string ScopeProfileEcho(ScopeProfileKind kind) => $"Scope profile {ScopeProfilePicker.NameOf(kind).ToUpperInvariant()}";

    /// <summary>
    /// <c>.scope pref save</c>, <c>saveas &lt;name&gt;</c>, <c>load &lt;name&gt;</c>, <c>rename &lt;old&gt; &lt;new&gt;</c>
    /// or <c>delete &lt;name&gt;</c>: the scope's state as a named set, kept per the viewer's facility.
    /// </summary>
    private static string RunScopePref(ScopeViewModel scope, string[] parts)
    {
        if (scope.PrefStore is not { } store)
        {
            return "Pref sets are not available";
        }

        if (parts.Length < 3)
        {
            return PrefUsage;
        }

        Func<ScopeViewModel, string, ScopePrefSetStore, string[], string>? handler = parts[2].ToLowerInvariant() switch
        {
            "save" => SaveActivePrefSet,
            "saveas" => SavePrefSetAs,
            "load" => LoadPrefSet,
            "rename" => RenamePrefSet,
            "delete" => DeletePrefSet,
            _ => null,
        };
        if (handler is null)
        {
            return PrefUsage;
        }

        string? facility = scope.Viewer.EramFacilityId;
        if (!ScopePrefSetStore.IsSafeFacility(facility))
        {
            return "No facility: pref sets need an active position";
        }

        return handler(scope, facility, store, parts);
    }

    /// <summary><c>.scope pref save</c>: saves the active set again from the scope's current state.</summary>
    private static string SaveActivePrefSet(ScopeViewModel scope, string facility, ScopePrefSetStore store, string[] parts)
    {
        if (parts.Length != 3)
        {
            return PrefUsage;
        }

        if (scope.ActivePrefSet is not { } active)
        {
            return ScopeViewModel.NoActivePrefSetMessage;
        }

        return RunStoreWrite(() =>
        {
            store.SaveSet(facility, scope.CapturePrefSet(active));
            scope.NotifyPrefSetsChanged();
            return ".scope pref save";
        });
    }

    /// <summary><c>.scope pref saveas &lt;name&gt;</c>: saves the scope's state as a new set, which becomes the active one.</summary>
    private static string SavePrefSetAs(ScopeViewModel scope, string facility, ScopePrefSetStore store, string[] parts)
    {
        if (parts.Length != 4)
        {
            return PrefUsage;
        }

        string typed = parts[3];
        if (!ScopePrefSetStore.TryNormalizeName(typed, out string? name))
        {
            return BadPrefSetName(typed);
        }

        if (store.FindSet(facility, name) is not null)
        {
            return $"Pref set exists: {name}";
        }

        return RunStoreWrite(() =>
        {
            store.SaveSet(facility, scope.CapturePrefSet(name));
            scope.ActivePrefSet = name;
            scope.NotifyPrefSetsChanged();
            return $".scope pref saveas {name}";
        });
    }

    /// <summary><c>.scope pref load &lt;name&gt;</c>: applies the set's state and makes it the active one.</summary>
    private static string LoadPrefSet(ScopeViewModel scope, string facility, ScopePrefSetStore store, string[] parts)
    {
        if (parts.Length != 4)
        {
            return PrefUsage;
        }

        string typed = parts[3];
        if (!ScopePrefSetStore.TryNormalizeName(typed, out string? name) || (store.FindSet(facility, name) is not { } set))
        {
            return $"Unknown pref set: {typed}";
        }

        // The set's profile goes first, so the set's own template wins over the profile's.
        string profileNote = scope.ApplyPrefSetProfile(set.Kind);
        int missing = scope.ApplyPrefSet(set);
        scope.ActivePrefSet = name;
        return $".scope pref load {name}{PrefLoadNote(scope, missing)}{profileNote}";
    }

    /// <summary>The note a load adds when some of the set's maps did not reach this scope.</summary>
    private static string PrefLoadNote(ScopeViewModel scope, int missing)
    {
        if ((scope.IsWaitingForRadarSeed) && (scope.MapToggles.Count == 0))
        {
            return " (its maps apply once the scope's maps load)";
        }

        return missing switch
        {
            0 => "",
            1 => " (1 map not on this scope)",
            _ => $" ({missing} maps not on this scope)",
        };
    }

    /// <summary><c>.scope pref rename &lt;old&gt; &lt;new&gt;</c>: renames a set, keeping it whenever it is the active one.</summary>
    private static string RenamePrefSet(ScopeViewModel scope, string facility, ScopePrefSetStore store, string[] parts)
    {
        if (parts.Length != 5)
        {
            return PrefUsage;
        }

        string typedOld = parts[3];
        if (store.FindSet(facility, typedOld) is not { } found)
        {
            return $"Unknown pref set: {typedOld}";
        }

        string typedNew = parts[4];
        if (!ScopePrefSetStore.TryNormalizeName(typedNew, out string? name))
        {
            return BadPrefSetName(typedNew);
        }

        return RunStoreWrite(() =>
        {
            if (!store.RenameSet(facility, found.Name!, name))
            {
                return $"Pref set exists: {name}";
            }

            if (string.Equals(scope.ActivePrefSet, found.Name, StringComparison.OrdinalIgnoreCase))
            {
                scope.ActivePrefSet = name;
            }

            scope.NotifyPrefSetsChanged();
            return $".scope pref rename {found.Name} {name}";
        });
    }

    /// <summary><c>.scope pref delete &lt;name&gt;</c>: deletes a set, and clears the active name when it was that set.</summary>
    private static string DeletePrefSet(ScopeViewModel scope, string facility, ScopePrefSetStore store, string[] parts)
    {
        if (parts.Length != 4)
        {
            return PrefUsage;
        }

        string typed = parts[3];
        if (store.FindSet(facility, typed) is not { } found)
        {
            return $"Unknown pref set: {typed}";
        }

        return RunStoreWrite(() =>
        {
            store.DeleteSet(facility, found.Name!);
            if (string.Equals(scope.ActivePrefSet, found.Name, StringComparison.OrdinalIgnoreCase))
            {
                scope.ActivePrefSet = null;
            }

            scope.NotifyPrefSetsChanged();
            return $".scope pref delete {found.Name}";
        });
    }

    /// <summary>Runs a store write, turning the two failures the store can raise into the status line's message.</summary>
    private static string RunStoreWrite(Func<string> write)
    {
        try
        {
            return write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not save pref sets: {ex.Message}";
        }
    }

    private static string BadPrefSetName(string typed) => $"Bad pref set name: {typed} (1-16 of A-Z 0-9 - _)";
}
