using System.Globalization;
using Avalonia.Media;

namespace Yaat.Client.Views.Scope.FieldMenus.Crc;

/// <summary>
/// The altitudes CRC's altitude menu reads from the track and flight plan (ViewAltitudeMenu.OnOpen), in hundreds of feet.
/// </summary>
/// <param name="FlightPlanAltitude">The flight plan's single altitude, or null for a block, VFR without one, or none.</param>
/// <param name="InterimAltitude">The track's interim altitude.</param>
/// <param name="LocalInterimAltitude">The track's local interim altitude.</param>
/// <param name="ProcedureAltitude">The track's procedure altitude.</param>
public sealed record CrcAltitudeValues(int? FlightPlanAltitude, int? InterimAltitude, int? LocalInterimAltitude, int? ProcedureAltitude)
{
    /// <summary>The flight plan's raw altitude text, shown on the FP pick area when it is not a single altitude.</summary>
    public string? FlightPlanAltitudeText { get; init; }

    /// <summary>Whether the flight plan is VFR or VFR-on-top: the list then centres on 100 when nothing else is set.</summary>
    public bool IsVfr { get; init; }

    /// <summary>Whether the aircraft is RVSM capable; when not, unselected altitudes at or above 290 show in FdbMenuInvalid grey.</summary>
    public bool IsRvsmCapable { get; init; } = true;
}

/// <summary>Every pick area of CRC's altitude menu (ViewAltitudeMenu), named after its CRC label.</summary>
public enum CrcAltitudePickArea
{
    /// <summary>
    /// A list row's altitude: CRC sends <c>QZ {alt:D3}</c> (VFR/ or OTP/ before it on a VFR flight plan), amending the
    /// assigned altitude (HandleAltitudePicked).
    /// </summary>
    Altitude,

    /// <summary>
    /// The T column beside a list row: CRC sends <c>QQ {alt:D3}</c>, the interim altitude (HandleAltitudePicked with
    /// isInterim).
    /// </summary>
    T,

    /// <summary>
    /// The FP pick area, shown with an interim, local or procedure altitude: CRC sends <c>QQ</c> to clear an interim or
    /// procedure altitude and <c>QQ L</c> to clear a local one (HandleFlightPlanAssignedAltitudePickAreaPicked).
    /// </summary>
    Fp,

    /// <summary>
    /// The <c>nnn T</c> pick area, shown with both an interim and a local interim: CRC sends <c>QQ L</c>, clearing the
    /// local interim (HandleInterimAltitudePickAreaPicked).
    /// </summary>
    InterimT,

    /// <summary>
    /// A list row picked with LOCAL TALT lit: CRC sends <c>QQ L{alt:D3}</c>, a local interim (HandleAltitudePicked);
    /// LOCAL TALT itself only toggles (HandleLocalInterimAltitudePickAreaPicked).
    /// </summary>
    LocalTalt,

    /// <summary>
    /// A list row picked with PROCEDURE lit: CRC sends <c>QQ P{alt:D3}</c>, a procedure altitude (HandleAltitudePicked);
    /// PROCEDURE itself only toggles (HandleProcedureAltitudePickAreaPicked).
    /// </summary>
    Procedure,
}

/// <summary>A pick on the CRC altitude menu.</summary>
/// <param name="AltitudeHundreds">
/// The altitude picked, in hundreds of feet: the row's, the FP altitude for Fp (0 without one), the interim for InterimT.
/// </param>
/// <param name="Area">The pick area, with the toggle lit at the time folded in.</param>
/// <param name="Shift">Whether Shift was held.</param>
public sealed record CrcAltitudePick(int AltitudeHundreds, CrcAltitudePickArea Area, bool Shift);

/// <summary>
/// A clone of CRC's ERAM altitude menu (ViewAltitudeMenu): FP and <c>nnn T</c> when they apply, LOCAL TALT, PROCEDURE, and
/// the 590..010 list seven lines at a time, opened centred on the selected altitude with its T column.
/// </summary>
public sealed class CrcAltitudeMenu : CrcFdbMenu
{
    private readonly CrcAltitudeValues _values;
    private readonly List<(int Value, bool Selected)> _altitudes;
    private readonly SelectedKind _kind;
    private readonly CrcPageScroll _scroll = new();
    private readonly bool _pilotOnly;
    private bool _localTaltLit;
    private bool _procedureLit;

    /// <summary>Opens the menu as CRC does: the selected altitude in the list, the list centred on it.</summary>
    public CrcAltitudeMenu(string callsign, CrcAltitudeValues values, CrcMenuMode mode, bool showModeChip)
        : this(callsign, values, CrcPalette.Eram, new Form(mode, showModeChip, PilotOnly: false)) { }

    /// <summary>
    /// Opens the menu for PILOT picks alone, as the STARS field menus do: no ERAM | PILOT chip, none of the pick areas above the
    /// list (FP, <c>nnn T</c>, LOCAL TALT, PROCEDURE) and no T column, in <paramref name="palette"/>.
    /// </summary>
    public CrcAltitudeMenu(string callsign, CrcAltitudeValues values, CrcPalette palette)
        : this(callsign, values, palette, new Form(CrcMenuMode.Pilot, ShowModeChip: false, PilotOnly: true)) { }

    private CrcAltitudeMenu(string callsign, CrcAltitudeValues values, CrcPalette palette, Form form)
        : base(callsign, 14, form.Mode, form.ShowModeChip, palette)
    {
        _values = values;
        _pilotOnly = form.PilotOnly;
        (int selected, _kind) = SelectedAltitude(values);
        _altitudes = BuildAltitudes(selected, out int selectedIndex);
        _scroll.Reset(_altitudes.Count, selectedIndex - 3);
        Relayout();
    }

    /// <inheritdoc/>
    public override int FdbAlignLine => 2;

    /// <summary>Raised on a pick.</summary>
    public event Action<CrcAltitudePick>? Picked;

    // The chip mode at open, whether the chip row shows, and whether the menu is PILOT-only (no ERAM-only parts).
    private sealed record Form(CrcMenuMode Mode, bool ShowModeChip, bool PilotOnly);

    private enum SelectedKind
    {
        FlightPlan,
        Interim,
        LocalInterim,
        Procedure,
    }

    internal IReadOnlyList<int> Altitudes => [.. _altitudes.Select(a => a.Value)];

    internal IReadOnlyList<int> VisibleAltitudes => [.. _scroll.Window(_altitudes).Select(a => a.Value)];

    /// <summary>CRC's GetSelectedAltitude: local interim, then interim, procedure, the flight plan, 100 for VFR, else 290.</summary>
    private static (int Altitude, SelectedKind Kind) SelectedAltitude(CrcAltitudeValues values)
    {
        if (values.LocalInterimAltitude is { } local)
        {
            return (local, SelectedKind.LocalInterim);
        }
        if (values.InterimAltitude is { } interim)
        {
            return (interim, SelectedKind.Interim);
        }
        if (values.ProcedureAltitude is { } procedure)
        {
            return (procedure, SelectedKind.Procedure);
        }
        return (values.FlightPlanAltitude ?? (values.IsVfr ? 100 : 290), SelectedKind.FlightPlan);
    }

    /// <summary>CRC's BuildAltitudes: 590 down to 010, by 20 above 410, an off-list selection inserted where it falls.</summary>
    private static List<(int Value, bool Selected)> BuildAltitudes(int selected, out int selectedIndex)
    {
        selectedIndex = 0;
        bool placed = false;
        var list = new List<(int Value, bool Selected)>();
        for (int value = 590; value >= 10; value = (value <= 410) ? value - 10 : value - 20)
        {
            if (!placed && (selected > value))
            {
                list.Add((selected, true));
                placed = true;
                selectedIndex = list.Count - 1;
            }
            list.Add((value, selected == value));
            if (selected == value)
            {
                placed = true;
                selectedIndex = list.Count - 1;
            }
        }
        return list;
    }

    private protected override IEnumerable<CrcNode> BuildContents()
    {
        if (!_pilotOnly)
        {
            foreach (CrcNode area in PreListAreas())
            {
                yield return area;
            }
        }
        yield return ScrollWrapper(_scroll.Window(_altitudes).Select(AltitudeRow), _scroll);
    }

    /// <summary>
    /// The pick areas above the list: FP and <c>nnn T</c> when they apply, LOCAL TALT and PROCEDURE. A PILOT-only menu shows
    /// none of them, since none sends a pilot command.
    /// </summary>
    private IEnumerable<CrcNode> PreListAreas()
    {
        bool anyTemporary =
            (_values.InterimAltitude is not null) || (_values.LocalInterimAltitude is not null) || (_values.ProcedureAltitude is not null);
        if (anyTemporary)
        {
            yield return PickArea(FlightPlanLabel(), s => Raise(_values.FlightPlanAltitude ?? 0, CrcAltitudePickArea.Fp, s), false);
        }
        if ((_values.InterimAltitude is { } interim) && (_values.LocalInterimAltitude is not null))
        {
            yield return PickArea($"{Format(interim)} T", s => Raise(interim, CrcAltitudePickArea.InterimT, s), false);
        }
        yield return PickArea("LOCAL TALT", _ => ToggleLocalTalt(), _localTaltLit);
        yield return PickArea("PROCEDURE", _ => ToggleProcedure(), _procedureLit);
    }

    /// <summary>CRC's GetFlightPlanAssignedAltitudeLabel.</summary>
    private string FlightPlanLabel() =>
        (_values.FlightPlanAltitude is { } altitude)
            ? $"FP {altitude.ToString(CultureInfo.InvariantCulture)}"
            : (_values.FlightPlanAltitudeText ?? "NO ALT");

    /// <summary>CRC's AltitudeValuePickArea: the altitude, and its T column drawn over the row from the same left edge.</summary>
    private CrcNode AltitudeRow((int Value, bool Selected) altitude)
    {
        Color color = (!altitude.Selected && !_values.IsRvsmCapable && (altitude.Value >= 290)) ? Palette.Invalid : Palette.Text;
        string text = Format(altitude.Value);
        var value = new CrcText(text, color)
        {
            CircleFill = altitude.Selected ? Palette.Emphasis : null,
            PickKey = text,
            Pick = s => PickAltitude(altitude.Value, false, s),
        };
        var interimColumn = new CrcText(InterimColumnText(altitude.Selected), color)
        {
            Absolute = true,
            CircleFill = (altitude.Selected && (_kind != SelectedKind.FlightPlan)) ? Palette.Emphasis : null,
            Visibility = (_pilotOnly || _localTaltLit || _procedureLit) ? CrcVisibility.Collapsed : CrcVisibility.Visible,
            PickKey = text + " T",
            Pick = s => PickAltitude(altitude.Value, true, s),
        };
        return new CrcRow().Add(value, interimColumn);
    }

    /// <summary>CRC's GetInterimAltitudeValue: L or P on the selected row when that is what is set, T otherwise.</summary>
    private string InterimColumnText(bool selected) =>
        (selected, _kind) switch
        {
            (true, SelectedKind.LocalInterim) => "    L",
            (true, SelectedKind.Procedure) => "    P",
            _ => "    T",
        };

    private void ToggleLocalTalt()
    {
        _localTaltLit = !_localTaltLit;
        _procedureLit = false;
    }

    private void ToggleProcedure()
    {
        _procedureLit = !_procedureLit;
        _localTaltLit = false;
    }

    /// <summary>CRC's HandleAltitudePicked: a lit LOCAL TALT or PROCEDURE decides the entry, else the column picked.</summary>
    private void PickAltitude(int value, bool interimColumn, bool shift)
    {
        CrcAltitudePickArea area =
            _localTaltLit ? CrcAltitudePickArea.LocalTalt
            : _procedureLit ? CrcAltitudePickArea.Procedure
            : interimColumn ? CrcAltitudePickArea.T
            : CrcAltitudePickArea.Altitude;
        Raise(value, area, shift);
    }

    private void Raise(int value, CrcAltitudePickArea area, bool shift) => Picked?.Invoke(new CrcAltitudePick(value, area, shift));

    private static string Format(int hundreds) => hundreds.ToString("D3", CultureInfo.InvariantCulture);
}
