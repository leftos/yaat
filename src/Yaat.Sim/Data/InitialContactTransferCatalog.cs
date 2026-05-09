namespace Yaat.Sim.Data;

public sealed class InitialContactTransferCatalog
{
    public static InitialContactTransferCatalog Empty { get; } = new([]);

    private readonly List<InitialContactTransferRule> _rules;
    private readonly HashSet<string> _customArtccIds;

    public InitialContactTransferCatalog(IEnumerable<InitialContactTransferRule> rules)
    {
        _rules = rules.ToList();
        _customArtccIds = _rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.ArtccId))
            .Select(rule => rule.ArtccId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool AllowsInitialContact(
        string? artccId,
        IReadOnlyList<string> airportIds,
        string? fromPositionType,
        string? fromCallsign,
        string? toPositionType,
        string? toCallsign,
        InitialContactTransferTiming observedTiming
    )
    {
        if (string.IsNullOrWhiteSpace(fromPositionType) && string.IsNullOrWhiteSpace(fromCallsign))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(toPositionType) && string.IsNullOrWhiteSpace(toCallsign))
        {
            return false;
        }

        var candidateRules = SelectRulesForArtcc(artccId);
        foreach (var rule in candidateRules)
        {
            if (!TimingAllows(rule.Timing, observedTiming))
            {
                continue;
            }

            if (!MatchesOptionalAirport(rule.AirportId, airportIds))
            {
                continue;
            }

            if (!MatchesOptional(rule.FromPositionType, fromPositionType) || !MatchesOptional(rule.FromCallsign, fromCallsign))
            {
                continue;
            }

            if (!MatchesOptional(rule.ToPositionType, toPositionType) || !MatchesOptional(rule.ToCallsign, toCallsign))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private IEnumerable<InitialContactTransferRule> SelectRulesForArtcc(string? artccId)
    {
        if (!string.IsNullOrWhiteSpace(artccId) && _customArtccIds.Contains(artccId))
        {
            return _rules.Where(rule => rule.ArtccId.Equals(artccId, StringComparison.OrdinalIgnoreCase));
        }

        return DefaultRules;
    }

    private static bool TimingAllows(InitialContactTransferTiming ruleTiming, InitialContactTransferTiming observedTiming) =>
        ruleTiming switch
        {
            InitialContactTransferTiming.NoHandoffNecessary => true,
            InitialContactTransferTiming.HandoffInitiated => observedTiming is InitialContactTransferTiming.HandoffInitiated,
            InitialContactTransferTiming.HandoffAccepted => observedTiming is InitialContactTransferTiming.HandoffAccepted,
            _ => false,
        };

    private static bool MatchesOptionalAirport(string? ruleAirportId, IReadOnlyList<string> airportIds)
    {
        if (string.IsNullOrWhiteSpace(ruleAirportId))
        {
            return true;
        }

        foreach (var airportId in airportIds)
        {
            if (NavigationDatabase.AirportIdsMatch(ruleAirportId, airportId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesOptional(string? ruleValue, string? actualValue)
    {
        if (string.IsNullOrWhiteSpace(ruleValue))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(actualValue) && ruleValue.Equals(actualValue, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<InitialContactTransferRule> DefaultRules { get; } =
    [
        new()
        {
            FromPositionType = "APP",
            ToPositionType = "TWR",
            Timing = InitialContactTransferTiming.HandoffInitiated,
            ContactAllowedWhen = "handoffInitiated",
            Notes = "Fallback default: approach-originated tower communication transfer may occur once a handoff is initiated.",
        },
        new()
        {
            FromPositionType = "CTR",
            ToPositionType = "TWR",
            Timing = InitialContactTransferTiming.HandoffInitiated,
            ContactAllowedWhen = "handoffInitiated",
            Notes = "Fallback default: center-originated tower communication transfer may occur once a handoff is initiated.",
        },
        new()
        {
            FromPositionType = "APP",
            ToPositionType = "APP",
            Timing = InitialContactTransferTiming.HandoffAccepted,
            ContactAllowedWhen = "handoffAccepted",
            Notes = "Fallback default: approach-to-approach communication transfer requires accepted track ownership.",
        },
        new()
        {
            FromPositionType = "CTR",
            ToPositionType = "APP",
            Timing = InitialContactTransferTiming.HandoffAccepted,
            ContactAllowedWhen = "handoffAccepted",
            Notes = "Fallback default: center-to-approach communication transfer requires accepted track ownership.",
        },
    ];
}
