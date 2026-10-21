namespace Yaat.Client.Services;

/// <summary>
/// What a solo traffic advisory starts with: the target's true clock and miles, or nothing, so the student works them out
/// from the scope. Stored by name in the preferences file; the Settings and session-flyout choices follow this order.
/// </summary>
public enum SoloAdvisoryPrefill
{
    TrueValues,
    LeftForStudent,
}

/// <summary>The words every "Pre-fill traffic advisories" choice shows for each <see cref="SoloAdvisoryPrefill"/>.</summary>
public static class SoloAdvisoryPrefillLabels
{
    /// <summary>One label per <see cref="SoloAdvisoryPrefill"/> value, indexed by the value.</summary>
    public static IReadOnlyList<string> Options { get; } = ["True values", "Left for the student"];
}
