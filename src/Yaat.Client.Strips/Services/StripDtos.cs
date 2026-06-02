namespace Yaat.Client.Services;

// Client-side mirrors of the server flight-strip DTOs. These match the JSON
// shapes SignalR delivers for the "FlightStripsStateChanged" and
// "StripItemsChanged" broadcast events, plus the FlightStripsConfig the server
// piggybacks on ScenarioLoaded / RoomState so the client gets bay layout
// without a separate RPC round-trip.
//
// Naming rule: keep the property names identical to the server DTOs — System.
// Text.Json is case-insensitive by default so this round-trips without custom
// converters. DO NOT repurpose fields or diverge from the server layout.

public enum StripItemType
{
    DepartureStrip = 0,
    ArrivalStrip = 1,
    HandwrittenSeparator = 2,
    WhiteSeparator = 3,
    RedSeparator = 4,
    GreenSeparator = 5,
    HalfStripLeft = 6,
    HalfStripRight = 7,
    BlankStrip = 8,
}

public record StripItemDto(
    string Id,
    string? AircraftId,
    bool IsDisconnected,
    StripItemType Type,
    bool IsOffset,
    string[] FieldValues,
    // Ownership fields (YAAT-only, JSON over SignalR). Empty strings when the
    // strip predates the multi-facility change or arrived via a CRC
    // MessagePack path where they aren't serialized. Used by
    // VStripsViewModel to filter broadcasts to its own facility scope.
    string FacilityId = "",
    string BayId = ""
);

public record StripBayContentsDto(string BayId, string[][] ItemIds);

public record FlightStripsStateDto(
    string[] PrinterItems,
    StripBayContentsDto[] BayItems,
    bool NewItemInPrinter,
    bool NewItemInArrivalPrinter,
    string? NewItemInBayId,
    string? ItemMovedOrCreatedBySessionId
);

public record StripBayConfigDto(string Id, string Name, int NumberOfRacks, bool IsExternal = false);

public record FlightStripsConfigDto(
    string FacilityId,
    string FacilityName,
    StripBayConfigDto[] Bays,
    bool HasTwoPrinters,
    bool SeparatorsLocked,
    // Airports this facility controls — the airport for a tower, all underlying
    // airports for a TRACON. Scopes the current-METAR display to the facility
    // currently being viewed. Empty when the facility has no resolvable airports.
    string[] UnderlyingAirports
);
