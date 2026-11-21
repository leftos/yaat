using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>What CRC's speed menu reads from the track and flight plan (ViewSpeedMenu.OnOpen).</summary>
/// <param name="AssignedSpeed">The track's assigned speed in CRC's form (<c>280</c>, <c>250+</c>, <c>M79</c>, <c>.79</c>); null for none.</param>
/// <param name="InterimAltitude">The track's interim altitude in hundreds of feet, which picks Mach at 290 and above.</param>
/// <param name="FlightPlanAltitude">The flight plan's altitude in hundreds of feet, the same test without an interim.</param>
public sealed record CrcSpeedValues(string? AssignedSpeed, int? InterimAltitude, int? FlightPlanAltitude);

/// <summary>Every pick area of CRC's speed menu (ViewSpeedMenu), named after its CRC label.</summary>
public enum CrcSpeedPickArea
{
    /// <summary>
    /// A list value with neither + nor - lit: CRC sends <c>QS /S{kt:D3}</c>, or <c>QS /M{mach:D2}</c> with KT toggled to M
    /// (HandleSpeedPicked); KT itself only toggles knots and Mach (HandleKnotMachPickAreaPicked).
    /// </summary>
    Kt,

    /// <summary>
    /// A list value with + lit, "or greater": CRC sends <c>QS /{kt:D3}+</c> or <c>QS /M{mach:D2}+</c> (HandleSpeedPicked);
    /// + itself only toggles (HandlePlusPickAreaPicked).
    /// </summary>
    Plus,

    /// <summary>
    /// A list value with - lit, "or less": CRC sends <c>QS /{kt:D3}-</c> or <c>QS /M{mach:D2}-</c> (HandleSpeedPicked);
    /// - itself only toggles (HandleMinusPickAreaPicked).
    /// </summary>
    Minus,

    /// <summary>DEL, shown with an assigned speed: CRC sends <c>QS /*</c>, deleting it (HandleDeletePickAreaPicked).</summary>
    Del,
}

/// <summary>A pick on the CRC speed menu.</summary>
/// <param name="Speed">Knots, or Mach in hundredths (79 for .79); 0 for DEL.</param>
/// <param name="IsMach">Whether the list on show was Mach.</param>
/// <param name="Area">The pick area, with the + or - lit at the time folded in.</param>
/// <param name="Shift">Whether Shift was held.</param>
public sealed record CrcSpeedPick(int Speed, bool IsMach, CrcSpeedPickArea Area, bool Shift);

/// <summary>
/// A clone of CRC's ERAM speed menu (ViewSpeedMenu): KT (or M), + and - over the knots (400..110) or Mach (.96...62) list,
/// seven lines at a time centred on the assigned speed or the default, and DEL with an assigned speed.
/// </summary>
public sealed partial class CrcSpeedMenu : CrcFdbMenu
{
    private const int KnotMachAltitude = 290;
    private const int DefaultMach = 79;
    private const int DefaultKnots = 280;

    private readonly CrcSpeedValues _values;
    private readonly List<SpeedEntry> _speeds;
    private readonly char? _openModifier;
    private readonly CrcPageScroll _scroll = new();
    private bool _isMach;
    private char? _modifier;

    /// <summary>Opens the menu as CRC does: knots or Mach from the assignment or the altitude, the list centred.</summary>
    public CrcSpeedMenu(string callsign, CrcSpeedValues values, CrcMenuMode mode, bool showModeChip)
        : this(callsign, values, mode, showModeChip, CrcPalette.Eram) { }

    /// <summary>
    /// Opens the menu for PILOT picks alone, as the STARS field menus do: no ERAM | PILOT chip, in <paramref name="palette"/>.
    /// </summary>
    public CrcSpeedMenu(string callsign, CrcSpeedValues values, CrcPalette palette)
        : this(callsign, values, CrcMenuMode.Pilot, showModeChip: false, palette) { }

    private CrcSpeedMenu(string callsign, CrcSpeedValues values, CrcMenuMode mode, bool showModeChip, CrcPalette palette)
        : base(callsign, 14, mode, showModeChip, palette)
    {
        _values = values;
        _isMach = InitialIsMach(values);
        _openModifier = OpenModifier(values.AssignedSpeed);
        _modifier = _openModifier;
        int? assigned = AssignedDigits(values.AssignedSpeed);
        _speeds = [.. Scale(isMach: true, _isMach ? assigned : null), .. Scale(isMach: false, _isMach ? null : assigned)];
        _scroll.Reset(CurrentList().Count, CurrentIndex() - 3);
        Relayout();
    }

    /// <inheritdoc/>
    public override int FdbAlignLine => 4;

    /// <summary>Raised on a pick.</summary>
    public event Action<CrcSpeedPick>? Picked;

    /// <summary>One value of a list: the speed, its unit, and whether it is the assigned one.</summary>
    private sealed record SpeedEntry(int Speed, bool IsMach, bool Selected)
    {
        public string Text => IsMach ? "." + Speed.ToString("D2", CultureInfo.InvariantCulture) : Speed.ToString("D3", CultureInfo.InvariantCulture);
    }

    internal IReadOnlyList<string> VisibleSpeeds => [.. _scroll.Window(CurrentList()).Select(s => s.Text)];

    /// <summary>CRC's GetInitialIsMach: the assignment's form, else the interim or flight plan altitude at or above 290.</summary>
    private static bool InitialIsMach(CrcSpeedValues values)
    {
        if (values.AssignedSpeed is { } assigned)
        {
            return MachRegex().IsMatch(assigned);
        }
        return (values.InterimAltitude ?? values.FlightPlanAltitude ?? 0) >= KnotMachAltitude;
    }

    /// <summary>The + or - an assignment ends with, which CRC lights at open.</summary>
    private static char? OpenModifier(string? assigned) =>
        (assigned is null) ? null
        : assigned.EndsWith('+') ? '+'
        : assigned.EndsWith('-') ? '-'
        : null;

    private static int? AssignedDigits(string? assigned) =>
        ((assigned is not null) && int.TryParse(DigitsRegex().Match(assigned).Value, CultureInfo.InvariantCulture, out int value)) ? value : null;

    /// <summary>CRC's BuildMachSpeeds / BuildKnotSpeeds: the scale top down, an off-scale assignment inserted where it falls.</summary>
    private static IEnumerable<SpeedEntry> Scale(bool isMach, int? assigned)
    {
        int max = isMach ? 96 : 400;
        int min = isMach ? 62 : 110;
        int step = isMach ? 1 : 10;
        if ((assigned is { } above) && (above > max))
        {
            yield return new SpeedEntry(above, isMach, true);
        }
        for (int speed = max; speed >= min; speed -= step)
        {
            yield return new SpeedEntry(speed, isMach, assigned == speed);
            if ((assigned is { } value) && (value < speed) && ((value > speed - step) || (speed - step < min)))
            {
                yield return new SpeedEntry(value, isMach, true);
            }
        }
    }

    private List<SpeedEntry> CurrentList() => [.. _speeds.Where(s => s.IsMach == _isMach)];

    /// <summary>CRC's GetCurrentSpeedIndex: the assigned value, else .79 or 280.</summary>
    private int CurrentIndex()
    {
        List<SpeedEntry> list = CurrentList();
        int selected = list.FindIndex(s => s.Selected);
        return (selected != -1) ? selected : list.FindIndex(s => s.Speed == (_isMach ? DefaultMach : DefaultKnots));
    }

    private protected override IEnumerable<CrcNode> BuildContents()
    {
        _scroll.Reset(CurrentList().Count, _scroll.TopLine);
        var top = new CrcRow();
        top.Add(
            PickArea(_isMach ? "M" : "KT", _ => ToggleUnit(), true, CrcGrid.Chars(4)),
            PickArea("+", _ => ToggleModifier('+'), _modifier == '+', CrcGrid.Chars(4, 1), new CrcEdges(-1, 0, 0, -1)),
            PickArea("-", _ => ToggleModifier('-'), _modifier == '-', CrcGrid.Chars(4, 1), new CrcEdges(-1, 0, 0, -1))
        );
        yield return top;
        yield return ScrollWrapper(_scroll.Window(CurrentList()).Select(SpeedRow), _scroll);
        if (_values.AssignedSpeed is { } assigned)
        {
            yield return PickArea("DEL " + assigned, s => Picked?.Invoke(new CrcSpeedPick(0, _isMach, CrcSpeedPickArea.Del, s)), false);
        }
    }

    /// <summary>CRC's SpeedValuePickArea: indented three and a half characters, the modifier after the value.</summary>
    private CrcNode SpeedRow(SpeedEntry entry)
    {
        char? modifier = entry.Selected ? _openModifier : _modifier;
        var text = new CrcText(entry.Text + modifier, Palette.Text)
        {
            CircleFill = entry.Selected ? Palette.Emphasis : null,
            PickKey = entry.Text,
            Pick = s => PickSpeed(entry, s),
        };
        return new CrcRow { Margin = new CrcEdges(0, 0, 0, CrcGrid.Chars(3.5)) }.Add(text);
    }

    /// <summary>CRC's HandleKnotMachPickAreaPicked: swaps the list and centres it.</summary>
    private void ToggleUnit()
    {
        _isMach = !_isMach;
        _scroll.Reset(CurrentList().Count, CurrentIndex() - 3);
    }

    /// <summary>CRC's HandlePlusPickAreaPicked / HandleMinusPickAreaPicked: lights one modifier or neither.</summary>
    private void ToggleModifier(char modifier) => _modifier = (_modifier == modifier) ? null : modifier;

    private void PickSpeed(SpeedEntry entry, bool shift)
    {
        CrcSpeedPickArea area = _modifier switch
        {
            '+' => CrcSpeedPickArea.Plus,
            '-' => CrcSpeedPickArea.Minus,
            _ => CrcSpeedPickArea.Kt,
        };
        Picked?.Invoke(new CrcSpeedPick(entry.Speed, entry.IsMach, area, shift));
    }

    [GeneratedRegex("^\\.?\\d{2}[+-]?$|^\\d\\.\\d{2}$|^M\\d{2}[+-]?$|^M\\.\\d{2}|^M\\d{3}$")]
    private static partial Regex MachRegex();

    [GeneratedRegex("\\d+")]
    private static partial Regex DigitsRegex();
}
