using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Wraps a <see cref="StripItemDto"/> for binding. Typed accessors over
/// <see cref="StripItemDto.FieldValues"/> match the field layout in
/// docs/crc/vstrips.md — printed fields at [0..8], annotation boxes 10-18 at [9..17].
/// Instances are updated in place (via <see cref="UpdateFromDto"/>) so DataTemplate
/// bindings stay stable across reconciliation.
/// </summary>
public partial class StripItemViewModel : ObservableObject
{
    [ObservableProperty]
    private StripItemDto _dto;

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

    public bool IsFullStrip => Type is StripItemType.DepartureStrip or StripItemType.ArrivalStrip;
    public bool IsHalfStrip => Type is StripItemType.HalfStripLeft or StripItemType.HalfStripRight;
    public bool IsSeparator =>
        Type is StripItemType.HandwrittenSeparator or StripItemType.WhiteSeparator or StripItemType.RedSeparator or StripItemType.GreenSeparator;
    public bool IsBlank => Type == StripItemType.BlankStrip;
    public bool IsDeparture => Type == StripItemType.DepartureStrip;
    public bool IsArrival => Type == StripItemType.ArrivalStrip;

    // Printed fields (departure strip layout).
    public string AircraftIdField => Field(0);
    public string Revision => Field(1);
    public string Equipment => Field(2);
    public string Cid => Field(3);
    public string BeaconCode => Field(4);
    public string PropDep => Field(5);
    public string ReqAltitude => Field(6);
    public string Departure => Field(7);
    public string Field8A => Field(8);
    public string RouteRemarks => Field(9);

    // Annotation boxes 10..18 — FieldValues[9..17] by convention. Callers invoke
    // VStripsCanonicalBuilder.BuildAnnotate(box, text) where box is 1..9, matching
    // the server-side StripAnnotateCommand handler's FieldValues[box + 9] layout.
    public string Annotation10 => Field(9);
    public string Annotation11 => Field(10);
    public string Annotation12 => Field(11);
    public string Annotation13 => Field(12);
    public string Annotation14 => Field(13);
    public string Annotation15 => Field(14);
    public string Annotation16 => Field(15);
    public string Annotation17 => Field(16);
    public string Annotation18 => Field(17);

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
        OnPropertyChanged(nameof(IsSeparator));
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
        OnPropertyChanged(nameof(Annotation10));
        OnPropertyChanged(nameof(Annotation11));
        OnPropertyChanged(nameof(Annotation12));
        OnPropertyChanged(nameof(Annotation13));
        OnPropertyChanged(nameof(Annotation14));
        OnPropertyChanged(nameof(Annotation15));
        OnPropertyChanged(nameof(Annotation16));
        OnPropertyChanged(nameof(Annotation17));
        OnPropertyChanged(nameof(Annotation18));
    }

    /// <summary>
    /// Lookup key used by half-strip canonical verbs (HSA / HSD / HSM / HSO / HSS).
    /// Half-strips identify themselves by their first field value; fall back to the
    /// strip id if the first field is empty (still unique across all items).
    /// </summary>
    public string LookupKey => FieldValues.Length > 0 && !string.IsNullOrEmpty(FieldValues[0]) ? FieldValues[0] : Id;

    private string Field(int idx) => idx < FieldValues.Length ? FieldValues[idx] ?? "" : "";
}
