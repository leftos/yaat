using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Evaluates STARS scratchpad rules against aircraft state and assigns
/// scratchpad1/scratchpad2 values when a rule matches.
/// </summary>
public static class ScratchpadRuleEngine
{
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

        if (string.IsNullOrEmpty(ac.Scratchpad1))
        {
            foreach (var rule in starsConfig.PrimaryScratchpadRules)
            {
                if (Matches(rule, ac))
                {
                    ac.Scratchpad1 = rule.Template;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(ac.Scratchpad2))
        {
            foreach (var rule in starsConfig.SecondaryScratchpadRules)
            {
                if (Matches(rule, ac))
                {
                    ac.Scratchpad2 = rule.Template;
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
                if (ac.Departure.Equals(apt, StringComparison.OrdinalIgnoreCase) || ac.Destination.Equals(apt, StringComparison.OrdinalIgnoreCase))
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
            var altHundreds = ac.CruiseAltitude / 100;
            if (altHundreds < rule.MinAltitude)
            {
                return false;
            }
        }

        if (rule.MaxAltitude is not null)
        {
            var altHundreds = ac.CruiseAltitude / 100;
            if (altHundreds > rule.MaxAltitude)
            {
                return false;
            }
        }

        // Search pattern: match against route
        // '#' is wildcard suffix (e.g., "SEGUL#" matches "SEGUL1", "SEGUL2", etc.)
        if (!string.IsNullOrEmpty(rule.SearchPattern))
        {
            return MatchesPattern(rule.SearchPattern, ac.Route);
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
