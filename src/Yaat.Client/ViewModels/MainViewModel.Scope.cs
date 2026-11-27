using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yaat.Client.Models;
using Yaat.Client.Services;
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
    // The HIST range text, built from the largest count the scope takes, with the word that unpins it.
    private static readonly string ScopeHistRange = $"<0-{ScopeViewModel.MaxHistory.ToString(CultureInfo.InvariantCulture)}|{ScopeUnpinWord}>";

    // The word that unpins one band value: .scope hist auto, .scope vector auto.
    private const string ScopeUnpinWord = "auto";

    private static readonly string ScopeUsage =
        "Usage: .scope range <0.125-256> | .scope brite <group> <value>"
        + " | .scope map <id> <on|off> | .scope db <item> <value>"
        + " | .scope pref <save|saveas|load|rename|delete> | .scope profile <stars|eram|mixed|euroscope>"
        + $" | .scope others <full|partial> | .scope auto | .scope vector <0|0.5|1|1.5|2|2.5|3|4|8|own|all|auto> | .scope hist {ScopeHistRange}"
        + " | .scope rings <jring|cone> <size> <callsign> | .scope rings <dri5|dri3> <callsign> | .scope rings clear [callsign]"
        + " | .scope tdm <on|off> | .scope ptl <callsign> | .scope ptl clear [callsign]"
        + " | .scope lens <on|off> | .scope lens path <on|off|callsign> | .scope lens <queue|checkin> <on|off>"
        + " | .scope mva <on|off> | .scope lock <on|off> | .scope center"
        + " | .scope rr <on|off|place|center> | .scope rr size <2|5|10|15|20> | .scope wx <on|off> | .scope fix <on|off>"
        + " | .scope dcnf <off|snap|free> | .scope bar <hide|show>";

    private const string ScopeLensUsage = "Usage: .scope lens <on|off> | .scope lens <path|queue|checkin> <on|off> | .scope lens path <callsign>";

    private const string ScopeTdmUsage = "Usage: .scope tdm <on|off>";

    private const string ScopeBarUsage = "Usage: .scope bar <hide|show>";

    private const string ScopePtlUsage = "Usage: .scope ptl <callsign> | .scope ptl clear [callsign]";

    private const string ScopeRingsUsage =
        "Usage: .scope rings <jring|cone> <1.0-9.9|10-30> <callsign> | .scope rings <dri5|dri3> <callsign> | .scope rings clear [callsign]";

    /// <summary>The refusal of a DRI 3 on a track above FL230.</summary>
    internal const string ReducedDriRefusal = "DRI 3 needs FL230 or below";

    private const string ScopeOthersUsage = "Usage: .scope others <full|partial>";

    private const string ScopeVectorUsage = "Usage: .scope vector <0|0.5|1|1.5|2|2.5|3|4|8|auto> | .scope vector <own|all>";

    private static readonly string ScopeHistUsage = $"Usage: .scope hist {ScopeHistRange}";

    private const string ScopeAutoUsage = "Usage: .scope auto";

    private const string PrefUsage =
        "Usage: .scope pref save | .scope pref saveas <name> | .scope pref load <name>"
        + " | .scope pref rename <old> <new> | .scope pref delete <name>";

    private const string ScopeRangeUsage = "Usage: .scope range <0.125-256>";

    private const string ScopeBriteUsage =
        "Usage: .scope brite <group> <value>: mapa, mapb, targets, fdb, alerts, hist, vector, rings, lens 0-100; line4 -20-0; dwell 0-20";

    private const string ScopeMapUsage = "Usage: .scope map <id> <on|off> | .scope map clear";

    private const string ScopeDbUsage = "Usage: .scope db <4thline|dwell|atpa|alertcones|monitorcones|caring> <on|off> | .scope db char <8-24>";

    private const string ScopeProfileUsage = "Usage: .scope profile <stars|eram|mixed|euroscope>";

    private ScopeProfileKind? _scopeProfile;

    /// <summary>
    /// Whether <see cref="ScopeProfile"/> was picked before the student position type was known, so it is picked once more
    /// when it arrives.
    /// </summary>
    private bool _isScopeProfileProvisional;

    /// <summary>
    /// The main facility's map and display source for the Scope: video maps, map group, weather airports, primary airport,
    /// centre, fixes and MVA facility, fed on the scenario, recording and room paths.
    /// </summary>
    public ScopeMapSource ScopeMaps { get; }

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
    /// Picks <paramref name="kind"/> by hand, as <c>.scope profile</c> does, saving it for the room's student position type; a
    /// room with no student position type saves it as the no-type pick, which the next such room gets.
    /// </summary>
    internal void PickScopeProfileByHand(ScopeProfileKind kind)
    {
        _isScopeProfileProvisional = false;
        Preferences.SetScopeProfileOverride(StudentPositionType, ScopeProfilePicker.NameOf(kind));
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

    /// <summary>
    /// The profile picked by hand for the student position type (or the no-type pick while there is none), else the active
    /// position's kind.
    /// </summary>
    private ScopeProfileKind AutomaticScopeProfile() =>
        ScopeProfilePicker.TryParseKind(Preferences.GetScopeProfileOverride(StudentPositionType), out ScopeProfileKind kind)
            ? kind
            : ScopeProfilePicker.KindFor(ActivePosition);

    /// <summary>
    /// Runs a <c>.scope</c> command against <see cref="OpenScope"/>, as typed; nothing is added to the command history. Test
    /// seam: the app's own paths name their Scope.
    /// </summary>
    internal void RunScopeCommand(string text) => RunScopeCommand(OpenScope, text);

    /// <summary>
    /// Runs a <c>.scope</c> command from <paramref name="scope"/>'s toolbar or cards against that Scope, whichever
    /// Scope has the focus; nothing is added to the command history.
    /// </summary>
    public void RunScopeCommand(ScopeViewModel scope, string text) => TryHandleScopeCommand(text, new CommandLineSink(this), scope);

    /// <summary>The parsed data of the video map with <paramref name="mapId"/> from the video-map cache, or null when it is not loaded.</summary>
    public VideoMapData? GetCachedVideoMap(string mapId) => _videoMapService.GetCached(mapId);

    /// <summary>Puts <paramref name="data"/> in the video-map cache, as a facility's map load would. Test seam.</summary>
    internal void CacheVideoMapForTesting(VideoMapData data) => _videoMapService.CacheForTesting(data);

    /// <summary>
    /// Handles <c>.scope range</c>, <c>.scope template</c>, <c>.scope brite</c>, <c>.scope map</c>, <c>.scope db</c>,
    /// <c>.scope pref</c>, <c>.scope profile</c>, <c>.scope others</c>, <c>.scope auto</c>,
    /// <c>.scope vector</c>, <c>.scope hist</c>, <c>.scope rings</c>, <c>.scope tdm</c>, <c>.scope ptl</c> and <c>.scope lens</c> against
    /// <paramref name="scope"/> (<see cref="OpenScope"/> for the typed line), reporting the echo or the error to <paramref name="sink"/>.
    /// </summary>
    /// <returns>False when the first token is not <c>.scope</c>, so the caller can fall through to CRC aliases.</returns>
    private bool TryHandleScopeCommand(string text, ICommandTextSink sink, ScopeViewModel scope)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if ((parts.Length == 0) || !string.Equals(parts[0], ".scope", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        sink.ReportStatus(RunScopeVerb(scope, parts));
        return true;
    }

    /// <summary>Runs the verb in <paramref name="parts"/>[1] against <paramref name="scope"/> and returns the status text.</summary>
    private string RunScopeVerb(ScopeViewModel scope, string[] parts)
    {
        string verb = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
        Func<ScopeViewModel, string[], string>? handler = verb switch
        {
            "RANGE" => SetScopeRange,
            "TEMPLATE" => static (_, _) => ScopeTemplateRetired,
            "BRITE" => SetScopeBrightness,
            "MAP" => SetScopeMap,
            "DB" => SetScopeDb,
            "PREF" => RunScopePref,
            "PROFILE" => PickScopeProfile,
            "OTHERS" => PinScopeOthers,
            "AUTO" => ClearScopePins,
            "VECTOR" => SetScopeVector,
            "HIST" => PinScopeHist,
            "RINGS" => RunScopeRings,
            "TDM" => SetScopeTdm,
            "BAR" => SetScopeBar,
            "PTL" => RunScopePtl,
            "LENS" => RunScopeLens,
            _ => null,
        };
        if (handler is null)
        {
            return RunFeatureVerb(scope, verb, parts) ?? ScopeUsage;
        }

        return handler(scope, parts);
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

    /// <summary><c>.scope db 4thline|dwell|atpa|alertcones|monitorcones|caring on|off</c> or <c>.scope db char &lt;8-24&gt;</c>.</summary>
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
            return $"Unknown Radar DB item: {parts[2]}";
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
    /// The placement that ends a command's <paramref name="parts"/> at <paramref name="index"/>, as in
    /// <c>.view &lt;name&gt; tear &lt;corner&gt; &lt;dx&gt; &lt;dy&gt;</c>: exactly three last parts, a corner name and two
    /// non-negative whole offsets.
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

    /// <summary><c>.scope tdm on|off</c>: turns top-down mode, which shows ground targets, on or off.</summary>
    private static string SetScopeTdm(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length != 3) || (ParseOnOff(parts[2]) is not { } on))
        {
            return ScopeTdmUsage;
        }

        scope.IsTdm = on;
        return on ? "Radar TDM ON" : "Radar TDM OFF";
    }

    /// <summary><c>.scope bar hide|show</c>: hides the Scope's bar to its SHOW tile or shows it again.</summary>
    private static string SetScopeBar(ScopeViewModel scope, string[] parts)
    {
        bool? hide =
            (parts.Length == 3)
                ? parts[2].ToLowerInvariant() switch
                {
                    "hide" => true,
                    "show" => false,
                    _ => null,
                }
                : null;
        if (hide is not { } hidden)
        {
            return ScopeBarUsage;
        }

        scope.SetBarHidden(hidden);
        return hidden ? ScopeToolbarButtons.BarHiddenEcho : ScopeToolbarButtons.BarShownEcho;
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
            || !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double rangeNm)
            || (rangeNm is not (>= ScopeViewModel.MinRangeNm and <= ScopeViewModel.MaxRangeNm))
        )
        {
            return ScopeRangeUsage;
        }

        scope.RangeNm = rangeNm;
        return ScopeToolbarButtons.RangeCommand(rangeNm);
    }

    /// <summary>What <c>.scope template</c>, with any arguments, answers: the profile alone picks the look now.</summary>
    internal const string ScopeTemplateRetired = "retired: use .scope profile";

    /// <summary>
    /// <c>.scope profile stars|eram|mixed|euroscope</c>: the room's profile, picked by hand and saved for its student position
    /// type.
    /// </summary>
    private static string PickScopeProfile(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 3)
        {
            return ScopeProfileUsage;
        }

        if (!ScopeProfilePicker.TryParseKind(parts[2], out ScopeProfileKind kind))
        {
            return $"Unknown Radar profile '{parts[2]}'";
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
        return $"Radar others {level.ToUpperInvariant()} (pinned)";
    }

    /// <summary>
    /// <c>.scope vector 0|0.5|1|1.5|2|2.5|3|4|8</c>: pins the vector minutes until <c>.scope auto</c> or <c>.scope vector auto</c>,
    /// which unpins them alone; <c>.scope vector own|all</c>: the tracks vectors draw on, a setting that never pins.
    /// </summary>
    private static string SetScopeVector(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 3)
        {
            return ScopeVectorUsage;
        }

        if (ScopeVectorOwnerships.TryParse(parts[2], out ScopeVectorOwnership ownership))
        {
            scope.VectorOwnership = ownership;
            return $"Radar vector {ScopeVectorOwnerships.NameOf(ownership).ToUpperInvariant()}";
        }

        if (string.Equals(parts[2], ScopeUnpinWord, StringComparison.OrdinalIgnoreCase))
        {
            scope.UnpinVectorMin();
            return "Radar vector AUTO";
        }

        if (
            (!double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes))
            || (!ScopeViewModel.IsVectorMinutes(minutes))
        )
        {
            return ScopeVectorUsage;
        }

        scope.PinVectorMin(minutes);
        return $"Radar vector {minutes.ToString(CultureInfo.InvariantCulture)} MIN (pinned)";
    }

    /// <summary>
    /// <c>.scope hist &lt;0-10&gt;</c>: pins the history dot count until <c>.scope auto</c>; <c>.scope hist auto</c> unpins it
    /// alone.
    /// </summary>
    private static string PinScopeHist(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length == 3) && string.Equals(parts[2], ScopeUnpinWord, StringComparison.OrdinalIgnoreCase))
        {
            scope.UnpinHistory();
            return "Radar history AUTO";
        }

        if (
            (parts.Length != 3)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || !ScopeViewModel.IsHistoryCount(count)
        )
        {
            return ScopeHistUsage;
        }

        scope.PinHistory(count);
        return $"Radar history {count.ToString(CultureInfo.InvariantCulture)} (pinned)";
    }

    /// <summary><c>.scope auto</c>: clears every pinned band value, so each follows the zoom band again.</summary>
    private static string ClearScopePins(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length != 2)
        {
            return ScopeAutoUsage;
        }

        scope.ClearPins();
        return "Radar AUTO";
    }

    /// <summary>
    /// <c>.scope rings jring|cone &lt;size&gt; &lt;callsign&gt;</c>, <c>.scope rings dri5|dri3 &lt;callsign&gt;</c> or
    /// <c>.scope rings clear [callsign]</c>: the Scope-local rings, never sent to the server. A kind the track already has at
    /// the same size is removed, at another size resized; a DRI 3 above FL230 is refused with a terminal warning.
    /// </summary>
    private static string RunScopeRings(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length is 3 or 4) && string.Equals(parts[2], "clear", StringComparison.OrdinalIgnoreCase))
        {
            return ClearScopeRings(scope, parts);
        }

        if (!TryParseRingCommand(parts, out ScopeRingTool tool, out double sizeNm))
        {
            return ScopeRingsUsage;
        }

        return (FindTypedAircraft(scope, parts[^1]) is { } ac) ? ToggleScopeRing(scope, ac, tool, sizeNm) : $"Unknown aircraft: {parts[^1]}";
    }

    /// <summary>
    /// Parses <c>jring|cone &lt;size&gt; &lt;callsign&gt;</c> or <c>dri5|dri3 &lt;callsign&gt;</c> after <c>.scope rings</c>: the
    /// kind and its size in NM (a DRI's radius for a DRI).
    /// </summary>
    /// <returns>False when the parts are no such command or the size is off CRC's grid.</returns>
    private static bool TryParseRingCommand(string[] parts, out ScopeRingTool tool, out double sizeNm)
    {
        sizeNm = 0;
        if ((parts.Length < 3) || !ScopeRingTools.TryParse(parts[2], out tool))
        {
            tool = ScopeRingTool.None;
            return false;
        }

        bool sized = tool is ScopeRingTool.JRing or ScopeRingTool.Cone;
        sizeNm = sized ? ParseRingSize(parts) : DriRadius(tool);
        return (parts.Length == (sized ? 5 : 4)) && (sizeNm > 0);
    }

    /// <summary>The aircraft whose callsign is <paramref name="typed"/>, case-insensitive, or null.</summary>
    private static AircraftModel? FindTypedAircraft(ScopeViewModel scope, string typed) =>
        scope.Aircraft.FirstOrDefault(a => string.Equals(a.Callsign, typed, StringComparison.OrdinalIgnoreCase));

    /// <summary>Places, resizes or removes the ring <paramref name="tool"/> places on <paramref name="ac"/>, and echoes it.</summary>
    private static string ToggleScopeRing(ScopeViewModel scope, AircraftModel ac, ScopeRingTool tool, double sizeNm)
    {
        ScopeTrackRings next = ScopeViewModel.Toggled(scope.RingsOf(ac.Callsign), tool, sizeNm);
        bool placed = ScopeViewModel.Carries(next, tool);
        if (placed && (tool == ScopeRingTool.Dri3) && !ScopeRingGeometry.AllowsReducedDri(ac.Altitude))
        {
            scope.Main.AddWarningEntry(ReducedDriRefusal);
            return ReducedDriRefusal;
        }

        scope.SetRings(ac.Callsign, next);
        return ScopeRingEcho(tool, sizeNm, ac.Callsign, placed);
    }

    /// <summary>The size of a <c>.scope rings jring|cone</c> command, or 0 when it is missing or off CRC's grid.</summary>
    private static double ParseRingSize(string[] parts) =>
        (
            (parts.Length > 3)
            && double.TryParse(parts[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double nm)
            && ScopeRingGeometry.IsRingSize(nm)
        )
            ? nm
            : 0;

    private static double DriRadius(ScopeRingTool tool) =>
        (tool == ScopeRingTool.Dri3) ? ScopeRingGeometry.ReducedDriNm : ScopeRingGeometry.StandardDriNm;

    /// <summary><c>.scope rings clear</c> removes every ring; <c>.scope rings clear &lt;callsign&gt;</c> one track's.</summary>
    private static string ClearScopeRings(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length == 3)
        {
            scope.ClearRings();
            return "Radar rings cleared";
        }

        if (FindTypedAircraft(scope, parts[3]) is not { } ac)
        {
            return $"Unknown aircraft: {parts[3]}";
        }

        scope.ClearRings(ac.Callsign);
        return $"Radar rings cleared {ac.Callsign}";
    }

    /// <summary>
    /// <c>.scope ptl &lt;callsign&gt;</c> toggles the track's PTL and <c>.scope ptl clear [callsign]</c> removes one track's
    /// or every PTL: the Scope-local per-track vector, never sent to the server.
    /// </summary>
    private static string RunScopePtl(ScopeViewModel scope, string[] parts)
    {
        if ((parts.Length is 3 or 4) && string.Equals(parts[2], "clear", StringComparison.OrdinalIgnoreCase))
        {
            return ClearScopePtl(scope, parts);
        }

        if (parts.Length != 3)
        {
            return ScopePtlUsage;
        }

        if (FindTypedAircraft(scope, parts[2]) is not { } ac)
        {
            return $"Unknown aircraft: {parts[2]}";
        }

        return ScopePtlEcho(ac.Callsign, scope.TogglePtl(ac.Callsign));
    }

    /// <summary><c>.scope ptl clear</c> removes every PTL; <c>.scope ptl clear &lt;callsign&gt;</c> one track's.</summary>
    private static string ClearScopePtl(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length == 3)
        {
            scope.ClearPtl();
            return "Radar PTL cleared";
        }

        if (FindTypedAircraft(scope, parts[3]) is not { } ac)
        {
            return $"Unknown aircraft: {parts[3]}";
        }

        scope.ClearPtl(ac.Callsign);
        return $"Radar PTL cleared {ac.Callsign}";
    }

    /// <summary>
    /// <c>.scope lens on|off</c> turns the pilot lens on or off; <c>.scope lens &lt;overlay&gt; on|off</c> turns one of its
    /// overlays on or off; <c>.scope lens path &lt;callsign&gt;</c> toggles that track's intent path. An unknown overlay or
    /// any other shape answers the usage line. Scope-local, never sent to the server.
    /// </summary>
    private static string RunScopeLens(ScopeViewModel scope, string[] parts)
    {
        if (parts.Length == 3)
        {
            return (ParseOnOff(parts[2]) is { } enabled) ? SetScopeLens(scope, enabled) : ScopeLensUsage;
        }

        if (parts.Length != 4)
        {
            return ScopeLensUsage;
        }

        string item = parts[2].ToLowerInvariant();
        return ScopeViewModel.IsLensOverlay(item) ? RunScopeLensOverlay(scope, item, parts[3]) : ScopeLensUsage;
    }

    /// <summary><c>.scope lens on|off</c>: the pilot lens's master switch.</summary>
    private static string SetScopeLens(ScopeViewModel scope, bool enabled)
    {
        scope.SetLensEnabled(enabled);
        return $"Radar lens {(enabled ? "ON" : "OFF")}";
    }

    /// <summary>
    /// <c>.scope lens &lt;item&gt; on|off</c> turns the overlay <paramref name="item"/> on or off; for <c>path</c> any other
    /// last word is a callsign whose intent path toggles, and for any other overlay it answers the usage line.
    /// </summary>
    private static string RunScopeLensOverlay(ScopeViewModel scope, string item, string word)
    {
        if (ParseOnOff(word) is { } on)
        {
            scope.SetLensOverlay(item, on);
            return $"Radar lens {item} {(on ? "ON" : "OFF")}";
        }

        if (item != ScopeToolbarButtons.LensPathItem)
        {
            return ScopeLensUsage;
        }

        if (FindTypedAircraft(scope, word) is not { } ac)
        {
            return $"Unknown aircraft: {word}";
        }

        bool shown = scope.ToggleLensPath(ac.Callsign);
        return $"Radar lens path {ac.Callsign}{(shown ? "" : " removed")}";
    }

    /// <summary>The status line's echo of a PTL change: <c>Scope PTL N123AB</c>, with <c> removed</c> on a removal.</summary>
    internal static string ScopePtlEcho(string callsign, bool on) => $"Radar PTL {callsign}{(on ? "" : " removed")}";

    /// <summary>The status line's echo of a ring change: <c>Scope J-ring 3.0 NM N123AB</c>, with <c> removed</c> on a removal.</summary>
    internal static string ScopeRingEcho(ScopeRingTool tool, double sizeNm, string callsign, bool placed)
    {
        string what = tool switch
        {
            ScopeRingTool.JRing => $"J-ring {ScopeRingGeometry.FormatSize(sizeNm)}",
            ScopeRingTool.Cone => $"cone {ScopeRingGeometry.FormatSize(sizeNm)}",
            _ => $"DRI {sizeNm.ToString("0", CultureInfo.InvariantCulture)}",
        };
        return $"Radar {what} NM {callsign}{(placed ? "" : " removed")}";
    }

    /// <summary>The status line's echo of a profile change: <c>Scope profile ERAM</c>.</summary>
    internal static string ScopeProfileEcho(ScopeProfileKind kind) => $"Radar profile {ScopeProfilePicker.NameOf(kind).ToUpperInvariant()}";

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

        // The set's profile goes first, so the set's own values win over the profile's.
        string profileNote = scope.ApplyPrefSetProfile(set.ProfileKindToApply());
        int missing = scope.ApplyPrefSet(set);
        scope.ActivePrefSet = name;
        return $".scope pref load {name}{PrefLoadNote(scope, missing)}{profileNote}";
    }

    /// <summary>The note a load adds when some of the set's maps did not reach this scope.</summary>
    private static string PrefLoadNote(ScopeViewModel scope, int missing)
    {
        if (scope.IsWaitingForMapSource)
        {
            return " (its maps apply once the Radar's maps load)";
        }

        return missing switch
        {
            0 => "",
            1 => " (1 map not on this Radar)",
            _ => $" ({missing} maps not on this Radar)",
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
