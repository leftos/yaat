namespace Yaat.Client.Services;

// DTOs that cross the strip transport surface. AccessibleFacilityDto is the
// return shape of GetAccessibleFacilitiesAsync; CommandResultDto comes back
// from RequestFlightStripForAircraftAsync (and every other RPC the desktop
// client submits — kept here because the strip view consumes it directly).
//
// Naming rule mirrors StripDtos.cs: keep property names identical to the
// server DTOs so System.Text.Json round-trips without custom converters.

// IsConsolidated marks a vTDLS parent page that aggregates its child TDLS
// facilities' DCL/PDC lists; always false for strips, which have no such view.
public record AccessibleFacilityDto(string FacilityId, string FacilityName, bool IsStudentFacility, bool IsConsolidated = false);

public record CommandResultDto(bool Success, string? Message, double ServerElapsedSeconds = 0);

// Narrow projection of the server's WeatherChangedDto — only the raw METAR
// strings the strip view needs. Extra weather fields (wind layers, precip,
// source JSON) are ignored on deserialize. Property name matches the server DTO
// so System.Text.Json round-trips without a custom converter.
public record StripsWeatherDto(List<string>? Metars);

// One METAR shown in the strip view's METAR bar: the parsed station id (for the
// label) and the raw METAR string (displayed verbatim, monospace).
public record StripMetarEntry(string StationId, string Raw);
