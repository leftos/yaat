using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Evaluates STARS scratchpad rules against aircraft state and assigns
/// scratchpad1/scratchpad2 values when a rule matches.
/// </summary>
public static class ScratchpadRuleEngine
{
    /// <summary>
    /// STARS primary/secondary scratchpad character limit: 3, or 4 when the facility enables
    /// <c>Allow4CharacterScratchpad</c>. ERAM and ASDE-X scratchpads are not bound by this rule.
    /// </summary>
    public static int MaxScratchpadLength(StarsConfig? starsConfig) => (starsConfig?.Allow4CharacterScratchpad ?? false) ? 4 : 3;

    /// <summary>
    /// Evaluates primary and secondary scratchpad rules against the aircraft.
    /// Only sets scratchpad if no value is already assigned.
    /// </summary>
    public static void Apply(AircraftState ac, StarsConfig? starsConfig)
    {
        if (starsConfig is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(ac.Stars.Scratchpad1))
        {
            foreach (var rule in starsConfig.PrimaryScratchpadRules)
            {
                if (Matches(rule, ac))
                {
                    ac.Stars.Scratchpad1 = rule.Template;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(ac.Stars.Scratchpad2))
        {
            foreach (var rule in starsConfig.SecondaryScratchpadRules)
            {
                if (Matches(rule, ac))
                {
                    ac.Stars.Scratchpad2 = rule.Template;
                    break;
                }
            }
        }
    }

    private static bool Matches(ScratchpadRuleConfig rule, AircraftState ac)
    {
        if (rule.AirportIds.Count > 0)
        {
            bool airportMatch = false;
            foreach (var apt in rule.AirportIds)
            {
                if (
                    NavigationDatabase.AirportIdsMatch(ac.FlightPlan.Departure, apt)
                    || NavigationDatabase.AirportIdsMatch(ac.FlightPlan.Destination, apt)
                )
                {
                    airportMatch = true;
                    break;
                }
            }

            if (!airportMatch)
            {
                return false;
            }
        }

        // Altitude filter — rule altitudes are in hundreds of feet, CruiseAltitude is in feet
        if (rule.MinAltitude is not null)
        {
            var altHundreds = (ac.FlightPlan.Altitude.CruiseFeet ?? 0) / 100;
            if (altHundreds < rule.MinAltitude)
            {
                return false;
            }
        }

        if (rule.MaxAltitude is not null)
        {
            var altHundreds = (ac.FlightPlan.Altitude.CruiseFeet ?? 0) / 100;
            if (altHundreds > rule.MaxAltitude)
            {
                return false;
            }
        }

        // Search pattern: match against route
        // '#' is wildcard suffix (e.g., "SEGUL#" matches "SEGUL1", "SEGUL2", etc.)
        if (!string.IsNullOrEmpty(rule.SearchPattern))
        {
            return MatchesPattern(rule.SearchPattern, ac.FlightPlan.Route);
        }

        return true;
    }

    private static bool MatchesPattern(string pattern, string route)
    {
        if (pattern.EndsWith('#'))
        {
            var prefix = pattern[..^1];
            return route.Contains(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return route.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
