namespace Yaat.Sim.Scenarios;

public enum ScenarioRatingTier
{
    Ungated = 0,
    S3 = 1,
    C1 = 2,
    I1 = 3,
}

public static class ScenarioRatingClassifier
{
    private const int S3Ordinal = 3;
    private const int C1Ordinal = 4;
    private const int I1Ordinal = 6;

    // The vNAS data-api returns long-form rating names ("Student3", "Controller1", "Instructor1").
    // Short forms are kept in the table too so manually-authored scenarios and forward-compat with
    // any future API shape still resolve cleanly.
    private static readonly Dictionary<string, int> RatingOrdinal = new(StringComparer.OrdinalIgnoreCase)
    {
        // Short forms (legacy / future).
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
        // Long forms (what the vNAS data-api actually emits).
        ["Observer"] = 0,
        ["Student1"] = 1,
        ["Student2"] = 2,
        ["Student3"] = 3,
        ["Controller1"] = 4,
        ["Controller3"] = 5,
        ["Instructor1"] = 6,
        ["Instructor3"] = 7,
        ["Supervisor"] = 8,
        ["Administrator"] = 9,
    };

    public static ScenarioRatingTier Classify(string? minimumRating)
    {
        if (string.IsNullOrWhiteSpace(minimumRating))
        {
            return ScenarioRatingTier.Ungated;
        }

        if (!RatingOrdinal.TryGetValue(minimumRating.Trim(), out var ordinal))
        {
            // Unknown rating string: fail closed to the highest tier. We'd rather block a scenario
            // we don't recognize than leak access if vNAS adds a new rating value before we map it.
            return ScenarioRatingTier.I1;
        }

        if (ordinal < S3Ordinal)
        {
            return ScenarioRatingTier.Ungated;
        }

        if (ordinal < C1Ordinal)
        {
            return ScenarioRatingTier.S3;
        }

        if (ordinal < I1Ordinal)
        {
            return ScenarioRatingTier.C1;
        }

        return ScenarioRatingTier.I1;
    }

    public static bool IsAccessible(ScenarioRatingTier required, IReadOnlySet<ScenarioRatingTier> unlocked)
    {
        return required switch
        {
            ScenarioRatingTier.Ungated => true,
            ScenarioRatingTier.S3 => unlocked.Contains(ScenarioRatingTier.S3) || unlocked.Contains(ScenarioRatingTier.C1) || unlocked.Contains(ScenarioRatingTier.I1),
            ScenarioRatingTier.C1 => unlocked.Contains(ScenarioRatingTier.C1) || unlocked.Contains(ScenarioRatingTier.I1),
            ScenarioRatingTier.I1 => unlocked.Contains(ScenarioRatingTier.I1),
            _ => false,
        };
    }
}
