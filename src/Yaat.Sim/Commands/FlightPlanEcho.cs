namespace Yaat.Sim.Commands;

/// <summary>
/// The two-line readout a filed flight plan echoes — callsign / type-equipment / beacon, then departure /
/// destination / altitude or <c>NO ROUTE</c> — as STARS shows it in the readout area and the terminal repeats it.
/// A pure function of the aircraft, so a caller that needs the lines separately (the CRC readout) rebuilds them
/// after the flight-plan arm has applied the command.
/// </summary>
public static class FlightPlanEcho
{
    public static (string Line1, string Line2) Build(AircraftState ac, bool hasRoute)
    {
        // The echo represents the *filed* plan — the flight-plan type, so a blanked type shows "????" here the way
        // STARS/ASDE-X read the same source.
        var typeEquip = !string.IsNullOrEmpty(ac.FlightPlan.AircraftType) ? ac.FlightPlan.AircraftType : "????";
        if (!string.IsNullOrEmpty(ac.FlightPlan.EquipmentSuffix) && !typeEquip.Contains('/'))
        {
            typeEquip += $"/{ac.FlightPlan.EquipmentSuffix}";
        }

        var line1 = $"{ac.Callsign} {typeEquip} {ac.Transponder.AssignedCode:D4}";

        string line2;
        if (hasRoute)
        {
            var dep = ac.FlightPlan.Departure ?? "";
            var dest = ac.FlightPlan.Destination ?? "";
            var cruiseFeet = ac.FlightPlan.Altitude.CruiseFeet ?? 0;
            var altStr = cruiseFeet > 0 ? $" {cruiseFeet / 100:D3}" : "";
            line2 = $"{dep} {dest}{altStr}".Trim();
            if (string.IsNullOrWhiteSpace(line2))
            {
                line2 = "NO ROUTE";
            }
        }
        else
        {
            line2 = "NO ROUTE";
        }

        return (line1, line2);
    }

    /// <summary>Whether a typed create command carries a route (departure or destination) for the second echo line.</summary>
    public static bool HasRoute(ParsedCommand command) =>
        command is CreateFlightPlanCommand create && FlightPlanNormalization.SplitRoute(create.Route) is not (null, null, _);
}
