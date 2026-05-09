namespace Yaat.Sim.Data;

public sealed class WakeDirectiveCatalog
{
    public static WakeDirectiveCatalog Empty { get; } = new([]);

    private readonly List<WakeDirectiveRule> _rules;

    public WakeDirectiveCatalog(IEnumerable<WakeDirectiveRule> rules)
    {
        _rules = rules.ToList();
    }

    public IReadOnlyList<WakeDirectiveRule> FindMatches(WakeDirectiveContext context)
    {
        if (_rules.Count == 0)
        {
            return [];
        }

        return _rules.Where(rule => Matches(rule, context)).ToList();
    }

    private static bool Matches(WakeDirectiveRule rule, WakeDirectiveContext context)
    {
        if (!string.IsNullOrWhiteSpace(rule.ArtccId))
        {
            if (string.IsNullOrWhiteSpace(context.ArtccId) || !rule.ArtccId.Equals(context.ArtccId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.AirportId))
        {
            if (string.IsNullOrWhiteSpace(context.AirportId) || !NavigationDatabase.AirportIdsMatch(rule.AirportId, context.AirportId))
            {
                return false;
            }
        }

        if (rule.Runways.Count > 0)
        {
            bool runwayMatches = rule.Runways.Any(runway =>
                runway.Equals(context.PrecedingRunwayId, StringComparison.OrdinalIgnoreCase)
                || runway.Equals(context.SucceedingRunwayId, StringComparison.OrdinalIgnoreCase)
            );
            if (!runwayMatches)
            {
                return false;
            }
        }

        if (rule.Operation != WakeDirectiveOperation.Any && rule.Operation != context.Operation)
        {
            return false;
        }

        if (rule.Relation != WakeDirectiveRelation.Any && rule.Relation != context.Relation)
        {
            return false;
        }

        if (rule.PrecedingCwt.Count > 0 && !rule.PrecedingCwt.Contains(char.ToUpperInvariant(context.PrecedingCwt)))
        {
            return false;
        }

        if (rule.SucceedingCwt.Count > 0 && !rule.SucceedingCwt.Contains(char.ToUpperInvariant(context.SucceedingCwt)))
        {
            return false;
        }

        if (rule.SourceRuleReferences.Count > 0)
        {
            bool ruleReferenceMatches = rule.SourceRuleReferences.Any(reference =>
                context.SourceRuleReference.Contains(reference, StringComparison.OrdinalIgnoreCase)
                || reference.Contains(context.SourceRuleReference, StringComparison.OrdinalIgnoreCase)
            );
            if (!ruleReferenceMatches)
            {
                return false;
            }
        }

        return true;
    }
}
