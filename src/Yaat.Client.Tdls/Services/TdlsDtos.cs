namespace Yaat.Client.Services;

// Client-side mirrors of the server vTDLS DTOs. These match the JSON shapes
// SignalR delivers for "TdlsItemChanged", "TdlsItemRemoved", and
// "TdlsStateChanged" broadcasts, plus the bootstrap config returned by the
// GetAccessibleTdlsFacilities / GetTdlsFacilityView RPCs.
//
// Naming rule mirrors StripDtos.cs: keep the property names identical to the
// server DTOs — System.Text.Json is case-insensitive by default so this
// round-trips without custom converters. DO NOT repurpose fields or diverge
// from the server layout.

public enum TdlsStatus
{
    Pending = 0,
    Sent = 1,
    Wilco = 2,
}

public record TdlsItemDto(
    string Id,
    string AircraftId,
    string? Cid,
    string FacilityId,
    TdlsStatus Status,
    int Sequence,
    DateTime CreatedUtc,
    DateTime? SentUtc,
    DateTime? WilcoUtc,
    DateTime ExpiresUtc,
    ClearanceDto? SentPayload,
    TdlsFlightPlanInfoDto? FlightPlan
);

// Read-only snapshot of the filed flight plan that the vTDLS editor's
// header strip renders next to the dropdowns (docs/vtdls/vtdls.md §Flight
// Plan Layout, fields A-H). Resolved server-side at DTO-build time so
// amendments through the radar client are picked up automatically.
public record TdlsFlightPlanInfoDto(
    int? AssignedBeaconCode,
    string Departure,
    string Destination,
    string Route,
    string AircraftType,
    string EquipmentSuffix,
    string Remarks,
    string Cid,
    int CruiseAltitude
)
{
    /// <summary>Cruise altitude divided by 100 — what the upstream "FL" indicator displays in the editor header.</summary>
    public int CruiseFlightLevel => CruiseAltitude / 100;

    /// <summary>Aircraft type + equipment suffix as a single display string ("B738/L").</summary>
    public string TypeAndEquipment => string.IsNullOrEmpty(EquipmentSuffix) ? AircraftType : $"{AircraftType}/{EquipmentSuffix}";

    /// <summary>
    /// Route line as upstream renders it: <c>DEP.{route}.DEST</c>. Skips the
    /// leading dot when no departure is set so a pre-fill row never looks
    /// like it starts with a missing token. ETE (/HHMM) is omitted because
    /// the sim doesn't track filed ETE.
    /// </summary>
    public string RouteDisplay
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(Departure))
            {
                parts.Add(Departure);
            }
            if (!string.IsNullOrEmpty(Route))
            {
                parts.Add(Route);
            }
            if (!string.IsNullOrEmpty(Destination))
            {
                parts.Add(Destination);
            }
            return string.Join('.', parts);
        }
    }

    /// <summary>Remarks line as upstream renders it: <c>RMK: {remarks}</c>. Empty when no remarks are filed.</summary>
    public string RemarksDisplay => string.IsNullOrEmpty(Remarks) ? "" : $"RMK: {Remarks}";
}

public record TdlsDumpedEntryDto(string FacilityId, string Callsign);

public record TdlsActiveOpConfigDto(string FacilityId, string OpConfigId);

public record TdlsStateDto(TdlsItemDto[] Items, TdlsDumpedEntryDto[] Dumped)
{
    /// <summary>Active ops config per facility. Additive — a server that predates ops configs sends none.</summary>
    public TdlsActiveOpConfigDto[] ActiveOpConfigs { get; init; } = [];
}

public record TdlsItemRemovedDto(string ItemId, string FacilityId, string Callsign, bool Dumped);

// ── ClearanceDto ───────────────────────────────────────────────────────
// Mirrors the wire shape of yaat-server's ClearanceDto. The vTDLS editor
// reads and writes this — it's both the snapshot payload on Sent items
// (TdlsItemDto.SentPayload) and the body of every TDLSS canonical command.

public record ClearanceDto(
    string? Expect,
    string? Sid,
    string? Transition,
    string? Climbout,
    string? Climbvia,
    string? InitialAlt,
    string? ContactInfo,
    string? LocalInfo,
    string? DepFreq
);

// ── TdlsConfigDto + nested ─────────────────────────────────────────────
// Per-facility bootstrap. Drives the flight-plan editor's SID/transition
// pickers, default field values, and mandatory-field gating. Shipped once
// per member facility inside the TdlsFacilityViewDto returned by GetTdlsFacilityView.

public record TdlsConfigDto(
    string FacilityId,
    string FacilityName,
    bool MandatorySid,
    bool MandatoryClimbout,
    bool MandatoryClimbvia,
    bool MandatoryInitialAlt,
    bool MandatoryDepFreq,
    bool MandatoryExpect,
    bool MandatoryContactInfo,
    bool MandatoryLocalInfo,
    TdlsSidDto[] Sids,
    TdlsClearanceValueDto[] Climbouts,
    TdlsClearanceValueDto[] Climbvias,
    TdlsClearanceValueDto[] InitialAlts,
    TdlsClearanceValueDto[] DepFreqs,
    TdlsClearanceValueDto[] Expects,
    TdlsClearanceValueDto[] ContactInfos,
    TdlsClearanceValueDto[] LocalInfos,
    string? DefaultSidId,
    string? DefaultTransitionId
)
{
    /// <summary>True when the facility splits its SIDs across <see cref="OpConfigs"/> — the footer Ops Config menu renders only then.</summary>
    public bool DclOpConfigsEnabled { get; init; }

    /// <summary>Operational configurations. When enabled, <see cref="Sids"/> is empty and the SID list comes from the active config.</summary>
    public TdlsOpConfigDto[] OpConfigs { get; init; } = [];

    /// <summary>The config in force when this bootstrap was fetched; live changes arrive via <see cref="TdlsStateDto.ActiveOpConfigs"/>.</summary>
    public string? ActiveOpConfigId { get; init; }

    /// <summary>Resolves the ops config the id names, falling back to the first. Null when ops configs are off or none exist.</summary>
    public TdlsOpConfigDto? ResolveOpConfig(string? opConfigId)
    {
        if (!DclOpConfigsEnabled || (OpConfigs.Length == 0))
        {
            return null;
        }
        return OpConfigs.FirstOrDefault(c => string.Equals(c.Id, opConfigId, StringComparison.Ordinal)) ?? OpConfigs[0];
    }

    /// <summary>
    /// The SID list actually in force. Every caller must go through this — reading <see cref="Sids"/>
    /// directly yields an empty list at any facility that enabled ops configs (SFO, OAK, BOS).
    /// </summary>
    public IReadOnlyList<TdlsSidDto> ResolveSids(string? opConfigId) => ResolveOpConfig(opConfigId)?.Sids ?? Sids;

    /// <summary>Default SID id in force — the ops config's when enabled, otherwise the facility's.</summary>
    public string? ResolveDefaultSidId(string? opConfigId) => DclOpConfigsEnabled ? ResolveOpConfig(opConfigId)?.DefaultSidId : DefaultSidId;

    /// <summary>Default transition id in force — the ops config's when enabled, otherwise the facility's.</summary>
    public string? ResolveDefaultTransitionId(string? opConfigId) =>
        DclOpConfigsEnabled ? ResolveOpConfig(opConfigId)?.DefaultTransitionId : DefaultTransitionId;
}

public record TdlsOpConfigDto(string Id, string Name, TdlsSidDto[] Sids, string? DefaultSidId, string? DefaultTransitionId);

// ── TdlsFacilityViewDto ────────────────────────────────────────────────
// What one vTDLS page shows. A leaf facility carries a single member config
// (itself); a consolidated parent carries one per child TDLS facility, because
// each item on that page is edited and sent against its own facility's SIDs,
// mandatory fields and ops config.

public record TdlsFacilityViewDto(string FacilityId, string FacilityName, IReadOnlyList<TdlsConfigDto> MemberConfigs);

public record TdlsSidDto(string Id, string Name, TdlsSidTransitionDto[] Transitions);

public record TdlsSidTransitionDto(
    string Id,
    string Name,
    string? FirstRoutePoint,
    string? DefaultExpect,
    string? DefaultClimbout,
    string? DefaultClimbvia,
    string? DefaultInitialAlt,
    string? DefaultDepFreq,
    string? DefaultContactInfo,
    string? DefaultLocalInfo
);

public record TdlsClearanceValueDto(string Id, string Value);
