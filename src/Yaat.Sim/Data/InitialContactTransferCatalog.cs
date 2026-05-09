namespace Yaat.Sim.Data;

public sealed class InitialContactTransferCatalog
{
    public static InitialContactTransferCatalog Empty { get; } = new([]);

    private readonly List<InitialContactTransferRule> _rules;

    public InitialContactTransferCatalog(IEnumerable<InitialContactTransferRule> rules)
    {
        _rules = rules.ToList();
    }

    public bool AllowsWithoutTrackHandoff(string? artccId, string? airportId, string? fromPositionType, string? toPositionType)
    {
        if (
            string.IsNullOrWhiteSpace(artccId)
            || string.IsNullOrWhiteSpace(airportId)
            || string.IsNullOrWhiteSpace(fromPositionType)
            || string.IsNullOrWhiteSpace(toPositionType)
        )
        {
            return false;
        }

        var normalizedAirport = NavigationDatabase.NormalizeAirport(airportId);
        var normalizedArtcc = artccId.Trim().ToUpperInvariant();
        var normalizedFrom = fromPositionType.Trim().ToUpperInvariant();
        var normalizedTo = toPositionType.Trim().ToUpperInvariant();

        return _rules.Any(rule =>
            rule.AllowsWithoutTrackHandoff
            && rule.ArtccId.Equals(normalizedArtcc, StringComparison.OrdinalIgnoreCase)
            && rule.AirportId.Equals(normalizedAirport, StringComparison.OrdinalIgnoreCase)
            && rule.FromPositionType.Equals(normalizedFrom, StringComparison.OrdinalIgnoreCase)
            && rule.ToPositionType.Equals(normalizedTo, StringComparison.OrdinalIgnoreCase)
        );
    }
}
