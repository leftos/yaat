using Yaat.Client.Models;

namespace Yaat.Client.Views;

/// <summary>
/// Pure text-to-<see cref="FlightPlanAmendment"/> conversion used by
/// <see cref="FlightPlanEditorWindow"/>. Cleared user-editable fields map to
/// the "clear this value" sentinel that the server's <c>SimulationEngine.AmendFlightPlan</c>
/// distinguishes from <c>null</c> ("don't touch"): empty string for textual fields
/// and zero for the numeric Speed/Altitude pair. Matches CRC's
/// <c>FlightPlanEditorViewModel.BuildFlightPlan</c> behaviour, where bound
/// string properties are sent verbatim and <c>int.TryParse</c> falls back to 0.
/// </summary>
internal static class FlightPlanEditorAmendmentBuilder
{
    internal static FlightPlanAmendment Build(
        string? typText,
        string? eqText,
        string? depText,
        string? destText,
        string? spdText,
        string? altText,
        string? rteText,
        string? rmkText,
        string strippedRemarksPrefix
    )
    {
        var typ = (typText ?? "").Trim().ToUpperInvariant();
        var eq = (eqText ?? "").Trim().ToUpperInvariant();
        // CRC compat: when the type is set but the equipment suffix is left blank, default to A.
        if (!string.IsNullOrEmpty(typ) && string.IsNullOrEmpty(eq))
        {
            eq = "A";
        }

        var dep = (depText ?? "").Trim().ToUpperInvariant();
        var dest = (destText ?? "").Trim().ToUpperInvariant();
        var rte = (rteText ?? "").Trim().ToUpperInvariant();

        // CruiseSpeed: blank/unparseable → 0 (CRC's BuildFlightPlan does int.TryParse(...) ? r : 0).
        int cruiseSpeed = int.TryParse(spdText, out var parsedSpd) ? parsedSpd : 0;

        // Altitude parses into (FlightRules, CruiseAltitude). Blank/unparseable → ("", 0)
        // so the user can wipe the altitude line. The non-null pair is what tells the server
        // "the user explicitly cleared this," distinct from null = "leave alone".
        var trimmedAlt = (altText ?? "").Trim();
        var parsedAlt = AircraftModel.ParseAltitudeField(trimmedAlt);
        string flightRules = parsedAlt?.FlightRules ?? "";
        int cruiseAlt = parsedAlt?.CruiseAltitude ?? 0;

        // Re-glue any RMK/ prefix that was hidden during editing so the protocol header
        // (+/V/PILOT/, etc.) round-trips intact.
        var rmk = (rmkText ?? "").Trim();
        var rebuiltRemarks = string.IsNullOrEmpty(strippedRemarksPrefix) ? rmk : strippedRemarksPrefix + "RMK/" + rmk;

        return new FlightPlanAmendment(
            AircraftType: typ,
            EquipmentSuffix: eq,
            Departure: dep,
            Destination: dest,
            CruiseSpeed: cruiseSpeed,
            CruiseAltitude: cruiseAlt,
            FlightRules: flightRules,
            Route: rte,
            Remarks: rebuiltRemarks,
            Scratchpad1: null,
            Scratchpad2: null,
            BeaconCode: null
        );
    }
}
