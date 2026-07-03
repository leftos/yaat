using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Find;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// View-model for a single TDLS list entry. One instance per server-side
/// <c>TdlsItemRecord</c>. Lives in either the DCL (Pending) or PDC (Sent / Wilco)
/// list — partition is decided by <see cref="VTdlsViewModel"/> based on
/// <see cref="Status"/>. Instance identity is preserved across reconciliation so
/// Avalonia bindings stay stable when the server pushes a state diff.
/// </summary>
public partial class TdlsItemViewModel : ObservableObject, IFindableItem
{
    public string Id { get; }

    // In-view Find (Ctrl+F) highlight flags. Written only by the shared FindController;
    // the DCL/PDC list templates bind a cyan overlay to them.
    [ObservableProperty]
    private bool _isFindMatch;

    [ObservableProperty]
    private bool _isCurrentFindMatch;

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
    private TdlsFlightPlanInfoDto? _flightPlan;

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
        FlightPlan = dto.FlightPlan;
    }

    /// <summary>
    /// All searchable text for in-view Find: callsign, CID, facility, the filed flight-plan
    /// fields (route/dep/dest/type/equipment/remarks/beacon) and the sent clearance fields,
    /// space-joined.
    /// </summary>
    public string GetFindText()
    {
        var parts = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add(value);
            }
        }

        Add(AircraftId);
        Add(Cid);
        Add(FacilityId);

        if (FlightPlan is { } fp)
        {
            Add(fp.Departure);
            Add(fp.Destination);
            Add(fp.Route);
            Add(fp.AircraftType);
            Add(fp.EquipmentSuffix);
            Add(fp.Remarks);
            if (fp.AssignedBeaconCode is { } beacon)
            {
                Add(beacon.ToString("D4"));
            }
        }

        if (SentPayload is { } clearance)
        {
            Add(clearance.Expect);
            Add(clearance.Sid);
            Add(clearance.Transition);
            Add(clearance.Climbout);
            Add(clearance.Climbvia);
            Add(clearance.InitialAlt);
            Add(clearance.ContactInfo);
            Add(clearance.LocalInfo);
            Add(clearance.DepFreq);
        }

        return string.Join(' ', parts);
    }
}
