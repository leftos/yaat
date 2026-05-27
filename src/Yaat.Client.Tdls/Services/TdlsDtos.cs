namespace Yaat.Client.Services;

// Client-side mirrors of the server vTDLS DTOs. These match the JSON shapes
// SignalR delivers for "TdlsItemChanged", "TdlsItemRemoved", and
// "TdlsStateChanged" broadcasts, plus the bootstrap config returned by the
// GetAccessibleTdlsFacilities / GetTdlsConfigForFacility RPCs.
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
    ClearanceDto? SentPayload
);

public record TdlsDumpedEntryDto(string FacilityId, string Callsign);

public record TdlsStateDto(TdlsItemDto[] Items, TdlsDumpedEntryDto[] Dumped);

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
// per facility via GetTdlsConfigForFacility.

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
);

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
