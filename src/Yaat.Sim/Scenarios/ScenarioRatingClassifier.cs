namespace Yaat.Sim.Scenarios;

/// <summary>
/// Maps VATSIM controller rating strings to a comparable ordinal and answers
/// "does this controller's rating meet the requirement?". Shared by the server's
/// connection gate (minimum rating to connect to a deployed server) and the
/// scenario rating gate (a scenario's <c>minimumRating</c> against the
/// controller's verified VATSIM rating).
/// </summary>
public static class ScenarioRatingClassifier
{
    // One row per rating, lowest first; the row's index is its ordinal. The vNAS data-api emits the long form
    // ("Student3", "Controller1"); VATSIM Connect emits the short form ("S3", "C1"). Both resolve against this one
    // table, so scenario JSON, command arguments and live VATSIM identity agree.
    private static readonly (string Short, string Long)[] Ratings =
    [
        ("OBS", "Observer"),
        ("S1", "Student1"),
        ("S2", "Student2"),
        ("S3", "Student3"),
        ("C1", "Controller1"),
        ("C3", "Controller3"),
        ("I1", "Instructor1"),
        ("I2", "Instructor2"),
        ("I3", "Instructor3"),
        ("SUP", "Supervisor"),
        ("ADM", "Administrator"),
    ];

    /// <summary>
    /// Gets the long-form rating names (what the vNAS data-api emits and scenario JSON stores), lowest first: Observer
    /// through Administrator, one per ordinal.
    /// </summary>
    public static IReadOnlyList<string> LongFormRatings { get; } = [.. Ratings.Select(r => r.Long)];

    private static readonly Dictionary<string, int> RatingOrdinal = BuildOrdinals();

    // I1 (Instructor 1) is the lowest instructor rating; everything at or above it (I1/I2/I3/SUP/ADM)
    // is treated as instructor-or-above for the training-hub connection gate.
    private static readonly int InstructorThreshold = OrdinalOf("I1") ?? int.MaxValue;

    private static Dictionary<string, int> BuildOrdinals()
    {
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Ratings.Length; i++)
        {
            ordinals[Ratings[i].Short] = i;
            ordinals[Ratings[i].Long] = i;
        }

        return ordinals;
    }

    /// <summary>
    /// Returns the comparable ordinal for a rating string (short or long form),
    /// or <c>null</c> if the value is blank or unrecognised.
    /// </summary>
    public static int? OrdinalOf(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        return RatingOrdinal.TryGetValue(rating.Trim(), out int ordinal) ? ordinal : null;
    }

    /// <summary>
    /// Returns whether <paramref name="rating"/> satisfies <paramref name="requiredRating"/>.
    /// A blank requirement is ungated (always allowed). An unrecognised non-blank requirement,
    /// or a blank/unrecognised caller rating against a real requirement, fails closed (denied)
    /// so a rating value we don't map yet can never leak access.
    /// </summary>
    public static bool IsRatingSufficient(string? rating, string? requiredRating)
    {
        if (string.IsNullOrWhiteSpace(requiredRating))
        {
            return true;
        }

        int? required = OrdinalOf(requiredRating);
        if (required is null)
        {
            return false;
        }

        int? have = OrdinalOf(rating);
        if (have is null)
        {
            return false;
        }

        return have.Value >= required.Value;
    }

    /// <summary>
    /// Returns whether <paramref name="rating"/> is an instructor rating (I1/I2/I3) or higher
    /// (SUP/ADM). Used by the training-hub connection gate: instructors may always connect even
    /// when they hold no VATUSA mentor role. A blank/unrecognised rating is not instructor-or-above.
    /// </summary>
    public static bool IsInstructorOrAbove(string? rating)
    {
        int? ordinal = OrdinalOf(rating);
        return ordinal is not null && ordinal.Value >= InstructorThreshold;
    }
}
