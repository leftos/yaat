namespace Yaat.Sim.Scenarios;

public enum ScenarioRatingTier
{
    Ungated = 0,
    S3 = 1,
    I1 = 2,
}

public static class ScenarioRatingClassifier
{
    private const int S3Ordinal = 3;
    private const int I1Ordinal = 6;

    private static readonly Dictionary<string, int> RatingOrdinal = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OBS"] = 0,
        ["S1"] = 1,
        ["S2"] = 2,
        ["S3"] = 3,
        ["C1"] = 4,
        ["C3"] = 5,
        ["I1"] = 6,
        ["I3"] = 7,
        ["SUP"] = 8,
        ["ADM"] = 9,
    };

    public static ScenarioRatingTier Classify(string? minimumRating)
    {
        if (string.IsNullOrWhiteSpace(minimumRating))
        {
            return ScenarioRatingTier.Ungated;
        }

        if (!RatingOrdinal.TryGetValue(minimumRating.Trim(), out var ordinal))
        {
            // Unknown rating string: fail closed. We'd rather gate an unrecognized
            // scenario than leak access if vNAS adds a new rating value we haven't mapped.
            return ScenarioRatingTier.S3;
        }

        if (ordinal < S3Ordinal)
        {
            return ScenarioRatingTier.Ungated;
        }

        if (ordinal < I1Ordinal)
        {
            return ScenarioRatingTier.S3;
        }

        return ScenarioRatingTier.I1;
    }

    public static bool IsAccessible(ScenarioRatingTier required, IReadOnlySet<ScenarioRatingTier> unlocked)
    {
        return required switch
        {
            ScenarioRatingTier.Ungated => true,
            ScenarioRatingTier.S3 => unlocked.Contains(ScenarioRatingTier.S3) || unlocked.Contains(ScenarioRatingTier.I1),
            ScenarioRatingTier.I1 => unlocked.Contains(ScenarioRatingTier.I1),
            _ => false,
        };
    }
}
