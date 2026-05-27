using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// View-model for a single TDLS list entry. One instance per server-side
/// <c>TdlsItemRecord</c>. Lives in either the DCL (Pending) or PDC (Sent / Wilco)
/// list — partition is decided by <see cref="VTdlsViewModel"/> based on
/// <see cref="Status"/>. Instance identity is preserved across reconciliation so
/// Avalonia bindings stay stable when the server pushes a state diff.
/// </summary>
public partial class TdlsItemViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty]
    private string _aircraftId = "";

    [ObservableProperty]
    private string? _cid;

    [ObservableProperty]
    private string _facilityId = "";

    [ObservableProperty]
    private TdlsStatus _status;

    [ObservableProperty]
    private int _sequence;

    [ObservableProperty]
    private DateTime _createdUtc;

    [ObservableProperty]
    private DateTime? _sentUtc;

    [ObservableProperty]
    private DateTime? _wilcoUtc;

    [ObservableProperty]
    private DateTime _expiresUtc;

    [ObservableProperty]
    private ClearanceDto? _sentPayload;

    [ObservableProperty]
    private bool _isSelected;

    public TdlsItemViewModel(TdlsItemDto dto)
    {
        Id = dto.Id;
        Apply(dto);
    }

    /// <summary>Updates every observable field from a freshly broadcast DTO. Caller already verified item ids match.</summary>
    public void Apply(TdlsItemDto dto)
    {
        AircraftId = dto.AircraftId;
        Cid = dto.Cid;
        FacilityId = dto.FacilityId;
        Status = dto.Status;
        Sequence = dto.Sequence;
        CreatedUtc = dto.CreatedUtc;
        SentUtc = dto.SentUtc;
        WilcoUtc = dto.WilcoUtc;
        ExpiresUtc = dto.ExpiresUtc;
        SentPayload = dto.SentPayload;
    }
}
