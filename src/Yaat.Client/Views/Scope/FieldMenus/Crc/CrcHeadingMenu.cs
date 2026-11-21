using System.Globalization;
using System.Text.RegularExpressions;

namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>What CRC's heading menu reads from the track (ViewHeadingMenu.OnOpen).</summary>
/// <param name="GroundTrack">The track's ground track in degrees, which the list centres on with no compass heading assigned.</param>
/// <param name="AssignedHeading">The track's assigned heading in CRC's form: <c>H270</c>, <c>20L</c> or <c>20R</c>; null for none.</param>
public sealed record CrcHeadingValues(int? GroundTrack, string? AssignedHeading);

/// <summary>Every pick area of CRC's heading menu (ViewHeadingMenu), named after its CRC label.</summary>
public enum CrcHeadingPickArea
{
    /// <summary>A compass heading in the list: CRC sends <c>QS H{hdg:D3}</c> (HandleHeadingPicked).</summary>
    Heading,

    /// <summary>
    /// A value in the LT list, degrees to turn left: CRC sends <c>QS {n}L</c> (HandleHeadingPicked); LT itself only swaps the
    /// list (HandleLeftTurnPickAreaPicked).
    /// </summary>
    Lt,

    /// <summary>
    /// A value in the RT list, degrees to turn right: CRC sends <c>QS {n}R</c> (HandleHeadingPicked); RT itself only swaps the
    /// list (HandleRightTurnPickAreaPicked).
    /// </summary>
    Rt,

    /// <summary>PH, present heading: CRC sends <c>QS PH</c> (HandlePresentHeadingPickAreaPicked).</summary>
    Ph,

    /// <summary>DELETE, shown with an assigned heading: CRC sends <c>QS */</c>, deleting it (HandleDeletePickAreaPicked).</summary>
    Delete,
}

/// <summary>A pick on the CRC heading menu.</summary>
/// <param name="Heading">The compass heading or the turn's degrees; 0 for PH and DELETE.</param>
/// <param name="Area">The pick area, with the LT or RT list on show folded in.</param>
/// <param name="Shift">Whether Shift was held.</param>
public sealed record CrcHeadingPick(int Heading, CrcHeadingPickArea Area, bool Shift);

/// <summary>
/// A clone of CRC's ERAM heading menu (ViewHeadingMenu): LT, PH and RT over a two-column list (the compass in 5° steps, or
/// the left or right turn degrees), seven lines at a time, and DELETE with an assigned heading.
/// </summary>
public sealed partial class CrcHeadingMenu : CrcFdbMenu
{
    private readonly CrcHeadingValues _values;
    private readonly List<HeadingCell[]> _compassRows = BuildCompassRows();
    private readonly List<HeadingCell[]> _leftRows = BuildTurnRows('L');
    private readonly List<HeadingCell[]> _rightRows = BuildTurnRows('R');
    private readonly CrcPageScroll _scroll = new();
    private readonly string? _circled;
    private bool _leftLit;
    private bool _rightLit;

    /// <summary>Opens the menu as CRC does: on the assigned turn's list, or on the compass centred on the heading.</summary>
    public CrcHeadingMenu(string callsign, CrcHeadingValues values, CrcMenuMode mode, bool showModeChip)
        : base(callsign, 10, mode, showModeChip)
    {
        _values = values;
        _circled = Open(values.AssignedHeading);
        Relayout();
    }

    /// <inheritdoc/>
    public override int FdbAlignLine => 4;

    /// <summary>Raised on a pick.</summary>
    public event Action<CrcHeadingPick>? Picked;

    /// <summary>One cell of the list: its heading, the value CRC enters (H270, 20L) and the text it shows.</summary>
    private sealed record HeadingCell(int Heading, string Value, string Display, bool Hidden);

    /// <summary>CRC's BuildCompassHeadings: for 360 down to 010, the heading 5° past it, then it.</summary>
    private static List<HeadingCell[]> BuildCompassRows()
    {
        var rows = new List<HeadingCell[]>();
        for (int heading = 360; heading >= 10; heading -= 10)
        {
            rows.Add([CompassCell((heading + 5) % 360), CompassCell(heading)]);
        }
        return rows;
    }

    private static HeadingCell CompassCell(int heading)
    {
        string digits = heading.ToString("D3", CultureInfo.InvariantCulture);
        return new HeadingCell(heading, "H" + digits, digits, false);
    }

    /// <summary>CRC's BuildTurnHeadings: 50/55 down to 0/5, the 0 cell hidden.</summary>
    private static List<HeadingCell[]> BuildTurnRows(char direction)
    {
        var rows = new List<HeadingCell[]>();
        for (int degrees = 55; degrees >= 5; degrees -= 10)
        {
            rows.Add([TurnCell(degrees - 5, direction), TurnCell(degrees, direction)]);
        }
        return rows;
    }

    private static HeadingCell TurnCell(int degrees, char direction)
    {
        string value = degrees.ToString(CultureInfo.InvariantCulture) + direction;
        return new HeadingCell(degrees, value, value, degrees == 0);
    }

    /// <summary>CRC's OnOpen: lights LT or RT for an assigned turn, else centres the compass; returns the value circled.</summary>
    private string? Open(string? assigned)
    {
        if (assigned is null)
        {
            CentreCompass(null);
            return null;
        }
        Match left = LeftHeadingRegex().Match(assigned);
        if (left.Success)
        {
            return OpenTurn(left, leftList: true);
        }
        Match right = RightHeadingRegex().Match(assigned);
        if (right.Success)
        {
            return OpenTurn(right, leftList: false);
        }
        return OpenCompass(assigned);
    }

    /// <summary>An assigned turn: its list lit, the nearest value circled only when it is the value assigned.</summary>
    private string? OpenTurn(Match match, bool leftList)
    {
        _leftLit = leftList;
        _rightLit = !leftList;
        int degrees = int.Parse(match.Groups["heading"].Value, CultureInfo.InvariantCulture);
        HeadingCell nearest = Nearest((leftList ? _leftRows : _rightRows).SelectMany(r => r), degrees);
        return (nearest.Heading == degrees) ? nearest.Value : null;
    }

    /// <summary>An assigned compass heading circled when listed and centred on; any other text centres on the ground track.</summary>
    private string? OpenCompass(string assigned)
    {
        Match compass = CompassHeadingRegex().Match(assigned);
        if (!compass.Success)
        {
            CentreCompass(null);
            return null;
        }
        int heading = int.Parse(compass.Groups["heading"].Value, CultureInfo.InvariantCulture);
        CentreCompass(heading);
        return _compassRows.SelectMany(r => r).Any(c => c.Heading == heading) ? CompassCell(heading).Value : null;
    }

    /// <summary>
    /// CRC's SetCurrentCompassHeading: the row of the heading nearest the assigned heading, the ground track or 180, three
    /// lines down.
    /// </summary>
    private void CentreCompass(int? assigned)
    {
        int heading = assigned ?? _values.GroundTrack ?? 180;
        List<HeadingCell> flat = [.. _compassRows.SelectMany(r => r)];
        int index = flat.IndexOf(Nearest(flat, heading));
        _scroll.Reset(_compassRows.Count, (index / 2) - 3);
    }

    private static HeadingCell Nearest(IEnumerable<HeadingCell> cells, int heading) => cells.OrderBy(c => Math.Abs(c.Heading - heading)).First();

    private protected override IEnumerable<CrcNode> BuildContents()
    {
        List<HeadingCell[]> rows =
            _leftLit ? _leftRows
            : _rightLit ? _rightRows
            : _compassRows;
        _scroll.Reset(rows.Count, _scroll.TopLine);
        var top = new CrcRow();
        top.Add(
            PickArea("LT", _ => ToggleTurn(left: true), _leftLit, CrcGrid.Chars(3)),
            PickArea("PH", s => Raise(0, CrcHeadingPickArea.Ph, s), false, CrcGrid.Chars(3, 1), new CrcEdges(-1, 0, 0, -1)),
            PickArea("RT", _ => ToggleTurn(left: false), _rightLit, CrcGrid.Chars(3, 1), new CrcEdges(-1, 0, 0, -1))
        );
        yield return top;
        yield return ScrollWrapper(_scroll.Window(rows).Select(HeadingRow), _scroll);
        if (_values.AssignedHeading is not null)
        {
            yield return PickArea("DELETE", s => Raise(0, CrcHeadingPickArea.Delete, s), false);
        }
    }

    private CrcNode HeadingRow(HeadingCell[] cells) => new CrcRow().Add([.. cells.Select(HeadingValue)]);

    /// <summary>CRC's HeadingValuePickArea: the value centred in three characters, circled when it is the assigned one.</summary>
    private CrcNode HeadingValue(HeadingCell cell)
    {
        var text = new CrcText(cell.Display, CrcPalette.Text)
        {
            MinWidth = CrcGrid.Chars(3),
            HAlign = CrcAlign.Center,
            CircleFill = (cell.Value == _circled) ? CrcPalette.Emphasis : null,
            PickKey = cell.Value,
            Pick = s => PickHeading(cell, s),
        };
        return new CrcRow { Visibility = cell.Hidden ? CrcVisibility.Hidden : CrcVisibility.Visible }.Add(text);
    }

    /// <summary>CRC's HandleLeftTurnPickAreaPicked / HandleRightTurnPickAreaPicked: toggles one turn list, the other off.</summary>
    private void ToggleTurn(bool left)
    {
        if (left)
        {
            _leftLit = !_leftLit;
            _rightLit = false;
        }
        else
        {
            _rightLit = !_rightLit;
            _leftLit = false;
        }
    }

    private void PickHeading(HeadingCell cell, bool shift)
    {
        CrcHeadingPickArea area =
            _leftLit ? CrcHeadingPickArea.Lt
            : _rightLit ? CrcHeadingPickArea.Rt
            : CrcHeadingPickArea.Heading;
        Raise(cell.Heading, area, shift);
    }

    private void Raise(int heading, CrcHeadingPickArea area, bool shift) => Picked?.Invoke(new CrcHeadingPick(heading, area, shift));

    [GeneratedRegex("^(?<heading>\\d{1,2})L$")]
    private static partial Regex LeftHeadingRegex();

    [GeneratedRegex("^(?<heading>^\\d{1,2})R$")]
    private static partial Regex RightHeadingRegex();

    [GeneratedRegex("^H(?<heading>\\d{3})$")]
    private static partial Regex CompassHeadingRegex();
}
