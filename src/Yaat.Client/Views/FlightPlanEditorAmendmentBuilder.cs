using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views;

/// <summary>
/// Pure text-to-<see cref="FlightPlanAmendment"/> conversion used by
/// <see cref="FlightPlanEditorWindow"/>. Cleared user-editable fields map to
/// the "clear this value" sentinel that the server's <c>SimulationEngine.AmendFlightPlan</c>
/// distinguishes from <c>null</c> ("don't touch"): empty string for textual fields
/// and zero for the numeric Speed/Altitude pair. Matches CRC's
/// <c>FlightPlanEditorViewModel.BuildFlightPlan</c> behaviour, where bound
/// string properties are sent verbatim and <c>int.TryParse</c> falls back to 0.
/// The equipment suffix is the exception: it is a separate field that a type change
/// never resets, so a blank or whitespace suffix box maps to <c>null</c> (not edited).
/// The /A default for a new plan filed without a suffix belongs to
/// <c>SimulationEngine.AmendFlightPlan</c>.
/// </summary>
internal static class FlightPlanEditorAmendmentBuilder
{
    internal static FlightPlanAmendment Build(
        string? typText,
        string? eqText,
        string? icaoEqText,
        string? depText,
        string? destText,
        string? spdText,
        string? altText,
        string? rteText,
        string? rmkText,
        string strippedRemarksPrefix
    )
    {
        string typ = (typText ?? "").Trim().ToUpperInvariant();
        string? eq = string.IsNullOrWhiteSpace(eqText) ? null : eqText.Trim().ToUpperInvariant();
        string icaoEq = (icaoEqText ?? "").Trim().ToUpperInvariant();
        string dep = (depText ?? "").Trim().ToUpperInvariant();
        string dest = (destText ?? "").Trim().ToUpperInvariant();
        string rte = (rteText ?? "").Trim().ToUpperInvariant();

        // CruiseSpeed: blank/unparseable → 0 (CRC's BuildFlightPlan does int.TryParse(...) ? r : 0).
        int cruiseSpeed = int.TryParse(spdText, out int parsedSpd) ? parsedSpd : 0;

        // Altitude parses into (Rules, PlannedAltitude). Blank/unparseable → ("", None) so the
        // user can wipe the altitude line. The non-null value is what tells the server "the user
        // explicitly cleared this," distinct from null = "leave alone".
        string trimmedAlt = (altText ?? "").Trim();
        (string Rules, PlannedAltitude Altitude)? parsedAlt = AircraftModel.ParseAltitudeField(trimmedAlt);
        string flightRules = parsedAlt?.Rules ?? "";
        PlannedAltitude altitude = parsedAlt?.Altitude ?? PlannedAltitude.None;

        // Re-glue any RMK/ prefix that was hidden during editing so the protocol header
        // (+/V/PILOT/, etc.) round-trips intact.
        string rmk = (rmkText ?? "").Trim();
        string rebuiltRemarks = string.IsNullOrEmpty(strippedRemarksPrefix) ? rmk : strippedRemarksPrefix + "RMK/" + rmk;

        return new FlightPlanAmendment(
            AircraftType: typ,
            EquipmentSuffix: eq,
            IcaoEquipmentCodes: icaoEq,
            Departure: dep,
            Destination: dest,
            CruiseSpeed: cruiseSpeed,
            Altitude: altitude,
            FlightRules: flightRules,
            Route: rte,
            Remarks: rebuiltRemarks,
            Scratchpad1: null,
            Scratchpad2: null,
            BeaconCode: null
        );
    }
}
