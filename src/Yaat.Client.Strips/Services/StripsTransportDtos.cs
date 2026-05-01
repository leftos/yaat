namespace Yaat.Client.Services;

// DTOs that cross the strip transport surface. AccessibleFacilityDto is the
// return shape of GetAccessibleFacilitiesAsync; CommandResultDto comes back
// from RequestFlightStripForAircraftAsync (and every other RPC the desktop
// client submits — kept here because the strip view consumes it directly).
//
// Naming rule mirrors StripDtos.cs: keep property names identical to the
// server DTOs so System.Text.Json round-trips without custom converters.

public record AccessibleFacilityDto(string FacilityId, string FacilityName, bool IsStudentFacility);

public record CommandResultDto(bool Success, string? Message);
