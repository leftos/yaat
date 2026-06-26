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
    // The vNAS data-api emits long-form rating names ("Student3", "Controller1");
    // VATSIM Connect emits short forms ("S3", "C1"). Both are kept so scenario JSON,
    // command arguments, and live VATSIM identity all resolve against one table.
    private static readonly Dictionary<string, int> RatingOrdinal = new(StringComparer.OrdinalIgnoreCase)
    {
        // Short forms.
        ["OBS"] = 0,
        ["S1"] = 1,
        ["S2"] = 2,
        ["S3"] = 3,
        ["C1"] = 4,
        ["C3"] = 5,
        ["I1"] = 6,
        ["I2"] = 7,
        ["I3"] = 8,
        ["SUP"] = 9,
        ["ADM"] = 10,
        // Long forms (what the vNAS data-api emits).
        ["Observer"] = 0,
        ["Student1"] = 1,
        ["Student2"] = 2,
        ["Student3"] = 3,
        ["Controller1"] = 4,
        ["Controller3"] = 5,
        ["Instructor1"] = 6,
        ["Instructor2"] = 7,
        ["Instructor3"] = 8,
        ["Supervisor"] = 9,
        ["Administrator"] = 10,
    };

    // I1 (Instructor 1) is the lowest instructor rating; everything at or above it (I1/I2/I3/SUP/ADM)
    // is treated as instructor-or-above for the training-hub connection gate.
    private static readonly int InstructorThreshold = OrdinalOf("I1") ?? int.MaxValue;

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

        return RatingOrdinal.TryGetValue(rating.Trim(), out var ordinal) ? ordinal : null;
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

        var required = OrdinalOf(requiredRating);
        if (required is null)
        {
            return false;
        }

        var have = OrdinalOf(rating);
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
        var ordinal = OrdinalOf(rating);
        return ordinal is not null && ordinal.Value >= InstructorThreshold;
    }
}
