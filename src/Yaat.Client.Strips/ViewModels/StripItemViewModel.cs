using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Find;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Wraps a <see cref="StripItemDto"/> for binding. Typed accessors over
/// <see cref="StripItemDto.FieldValues"/> match the field layout used by the
/// server (<c>StripMutations.FieldIdx*</c>): printed fields at [0..9]
/// (callsign, rev, equip, CID, beacon, prop-dep, req-alt, dep airport, 8A, route/remarks),
/// annotation boxes 10..18 at [10..18] (server writes <c>FieldValues[box + 9]</c>).
/// Instances are updated in place (via <see cref="UpdateFromDto"/>) so DataTemplate
/// bindings stay stable across reconciliation.
/// </summary>
public partial class StripItemViewModel : ObservableObject, IFindableItem
{
    [ObservableProperty]
    private StripItemDto _dto;

    // In-view Find (Ctrl+F) highlight flags. Written only by the shared FindController;
    // FlightStripControl binds a cyan overlay to them (distinct from the yellow selection ring).
    [ObservableProperty]
    private bool _isFindMatch;

    [ObservableProperty]
    private bool _isCurrentFindMatch;

    // Selection is a purely client-side flag — not broadcast to the server.
    // Flipped by <see cref="VStripsViewModel.SelectedStrip"/> whenever a strip
    // becomes the keyboard-focus target so FlightStripControl can render a
    // highlight ring (Round 4 selection navigation).
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// One-shot flag: when a half-strip was just created by this client's own
    /// HSC, <see cref="VStripsViewModel.ReconcileItems"/> sets this true so the
    /// rendered <c>FlightStripControl</c> moves keyboard focus into the first
    /// inline cell on attach. The control reads it once and resets it — it is a
    /// plain property (not <c>[ObservableProperty]</c>) because nothing binds to
    /// it. Never set for remote/CRC-created strips, so focus is not stolen.
    /// </summary>
    public bool RequestFocusFirstCell { get; set; }

    public StripItemViewModel(StripItemDto dto)
    {
        _dto = dto;
    }

    public string Id => Dto.Id;
    public string? AircraftId => Dto.AircraftId;
    public StripItemType Type => Dto.Type;
    public bool IsOffset => Dto.IsOffset;
    public bool IsDisconnected => Dto.IsDisconnected;
    public string[] FieldValues => Dto.FieldValues;

    public bool IsFullStrip => Type is StripItemType.DepartureStrip or StripItemType.ArrivalStrip or StripItemType.BlankStrip;
    public bool IsHalfStrip => Type is StripItemType.HalfStripLeft or StripItemType.HalfStripRight;
    public bool IsHalfStripRight => Type == StripItemType.HalfStripRight;
    public bool IsSeparator =>
        Type is StripItemType.HandwrittenSeparator or StripItemType.WhiteSeparator or StripItemType.RedSeparator or StripItemType.GreenSeparator;
    public bool IsHandwrittenSeparator => Type == StripItemType.HandwrittenSeparator;
    public bool IsWhiteSeparator => Type == StripItemType.WhiteSeparator;
    public bool IsRedSeparator => Type == StripItemType.RedSeparator;
    public bool IsGreenSeparator => Type == StripItemType.GreenSeparator;
    public bool IsBlank => Type == StripItemType.BlankStrip;
    public bool IsDeparture => Type == StripItemType.DepartureStrip;
    public bool IsArrival => Type == StripItemType.ArrivalStrip;

    // Printed fields (departure strip layout). Server emits beacon as 4-digit
    // octal (StripMutations.FormatBeacon) and prop-dep as bare HHmm — we add the
    // "P" prefix here so the UI matches the CRC "P1200" glyph.
    public string AircraftIdField => Field(0);
    public string Revision => Field(1);
    public string Equipment => Field(2);
    public string Cid => Field(3);
    public string BeaconCode => Field(4);
    public string PropDep
    {
        get
        {
            var raw = Field(5);
            return string.IsNullOrEmpty(raw) ? "" : "P" + raw;
        }
    }
    public string ReqAltitude => Field(6);
    public string Departure => Field(7);

    /// <summary>
    /// Col 3 row 1 display — server packs "{dep} {dest}" (or just "{dep}" when
    /// destination is unset) into field 8 in
    /// <see cref="Yaat.Server.Simulation.StripMutations.BuildDepartureStripFields"/>.
    /// Rendered as a single TextBlock on the strip.
    /// </summary>
    public string Field8A => Field(8);
    public string RouteRemarks => Field(9);

    // Field 9 is packed as "route/dest head\nremarks tail" by the server
    // (StripMutations.FormatRouteField/FormatDestRemarks). The client splits on
    // the first newline so row 3 can host remarks separately from the route.
    // When no remarks are present, RouteText contains the whole field and
    // Remarks is empty.
    public string RouteText
    {
        get
        {
            var raw = Field(9);
            var nl = raw.IndexOf('\n');
            return nl < 0 ? raw : raw[..nl];
        }
    }
    public string Remarks
    {
        get
        {
            var raw = Field(9);
            var nl = raw.IndexOf('\n');
            return nl < 0 ? "" : raw[(nl + 1)..];
        }
    }
    public bool HasRemarks => !string.IsNullOrEmpty(Remarks);

    // Annotation boxes 10..18 live at FieldValues[10..18] — the server writes
    // <c>FieldValues[box + 9]</c> in StripMutations.SetAnnotationBox for box 1..9,
    // so box 10 → index 10 … box 18 → index 18. Route/remarks sits at [9] and
    // must not leak into these cells.
    public string Annotation10 => Field(10);
    public string Annotation11 => Field(11);
    public string Annotation12 => Field(12);
    public string Annotation13 => Field(13);
    public string Annotation14 => Field(14);
    public string Annotation15 => Field(15);
    public string Annotation16 => Field(16);
    public string Annotation17 => Field(17);
    public string Annotation18 => Field(18);

    // Col-3 freeform annotation slots: 8a (FieldValues[19]) and 8b ([20]) live
    // below field 8's dep/dest display. Unlike the 3×3 grid they're
    // left-aligned on the strip and written via AN 8a / AN 8b commands.
    public string Annotation8A => Field(19);
    public string Annotation8B => Field(20);

    // Half-strip cell grid: FieldValues[0..5] mapped row-major into a 3×2
    // inline-edit grid (cell 0 = row 0 col 0, cell 1 = row 0 col 1, …,
    // cell 5 = row 2 col 1). Edits dispatch HSE so empty cells are
    // preserved without ambiguity around FieldValues[0] doubling as the
    // half-strip lookup key.
    public string HalfCell0 => Field(0);
    public string HalfCell1 => Field(1);
    public string HalfCell2 => Field(2);
    public string HalfCell3 => Field(3);
    public string HalfCell4 => Field(4);
    public string HalfCell5 => Field(5);

    /// <summary>
    /// Separator label rendered as the band's centered text. Stored in
    /// <c>FieldValues[0]</c> by the server (SEP/SEPE handlers) — matches
    /// the layout in <c>StripCommandHandler.HandleSeparatorEditAsync</c>.
    /// </summary>
    public string SeparatorLabel => Field(0);

    /// <summary>
    /// Replaces the underlying DTO with a new server snapshot and raises property
    /// changes for every field-derived accessor. Preserves the wrapping instance
    /// so Avalonia bindings don't rebuild on reconcile.
    /// </summary>
    public void UpdateFromDto(StripItemDto updated)
    {
        Dto = updated;
        OnPropertyChanged(nameof(AircraftId));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(IsOffset));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(FieldValues));
        OnPropertyChanged(nameof(IsFullStrip));
        OnPropertyChanged(nameof(IsHalfStrip));
        OnPropertyChanged(nameof(IsHalfStripRight));
        OnPropertyChanged(nameof(IsSeparator));
        OnPropertyChanged(nameof(IsHandwrittenSeparator));
        OnPropertyChanged(nameof(IsWhiteSeparator));
        OnPropertyChanged(nameof(IsRedSeparator));
        OnPropertyChanged(nameof(IsGreenSeparator));
        OnPropertyChanged(nameof(IsBlank));
        OnPropertyChanged(nameof(IsDeparture));
        OnPropertyChanged(nameof(IsArrival));
        OnPropertyChanged(nameof(AircraftIdField));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(Equipment));
        OnPropertyChanged(nameof(Cid));
        OnPropertyChanged(nameof(BeaconCode));
        OnPropertyChanged(nameof(PropDep));
        OnPropertyChanged(nameof(ReqAltitude));
        OnPropertyChanged(nameof(Departure));
        OnPropertyChanged(nameof(Field8A));
        OnPropertyChanged(nameof(RouteRemarks));
        OnPropertyChanged(nameof(RouteText));
        OnPropertyChanged(nameof(Remarks));
        OnPropertyChanged(nameof(HasRemarks));
        OnPropertyChanged(nameof(Annotation10));
        OnPropertyChanged(nameof(Annotation11));
        OnPropertyChanged(nameof(Annotation12));
        OnPropertyChanged(nameof(Annotation13));
        OnPropertyChanged(nameof(Annotation14));
        OnPropertyChanged(nameof(Annotation15));
        OnPropertyChanged(nameof(Annotation16));
        OnPropertyChanged(nameof(Annotation17));
        OnPropertyChanged(nameof(Annotation18));
        OnPropertyChanged(nameof(Annotation8A));
        OnPropertyChanged(nameof(Annotation8B));
        OnPropertyChanged(nameof(HalfCell0));
        OnPropertyChanged(nameof(HalfCell1));
        OnPropertyChanged(nameof(HalfCell2));
        OnPropertyChanged(nameof(HalfCell3));
        OnPropertyChanged(nameof(HalfCell4));
        OnPropertyChanged(nameof(HalfCell5));
        OnPropertyChanged(nameof(SeparatorLabel));
    }

    private string Field(int idx) => idx < FieldValues.Length ? FieldValues[idx] ?? "" : "";

    /// <summary>
    /// All searchable text for in-view Find: every non-empty field (callsign, beacon, route,
    /// remarks, annotations, half-strip cells, separator label…), space-joined. Newlines in the
    /// packed route/remarks field become spaces so route and remarks tokens split cleanly.
    /// </summary>
    public string GetFindText() => string.Join(' ', FieldValues.Where(v => !string.IsNullOrEmpty(v))).Replace('\n', ' ');
}
