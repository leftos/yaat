namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The result of <c>wait_until</c>, whether the conditions were met or the wait ran out: <paramref name="Held"/> lists the
/// indices of the conditions that held at the deciding poll (the last one on a timeout), <paramref name="SimSeconds"/> is the
/// scenario-elapsed time that poll saw, and <paramref name="Last"/> carries every condition's last observed value. When met,
/// <paramref name="Screenshot"/> is the target captured at the matching poll (or <paramref name="ScreenshotError"/> says why it
/// could not be), and <paramref name="Then"/> how each action run at that poll ended; all three are null otherwise.
/// </summary>
public sealed record WaitUntilResult(
    bool Met,
    IReadOnlyList<int> Held,
    double SimSeconds,
    int ElapsedMs,
    IReadOnlyList<WaitUntilLastValue> Last,
    ScreenshotResult? Screenshot,
    string? ScreenshotError,
    IReadOnlyList<WaitUntilActionResult>? Then
);

/// <summary>
/// The value a condition last observed, e.g. <c>on ground</c>, <c>phase Approach</c>, <c>absent</c>, <c>queue: …</c>,
/// <c>matched: …</c> or <c>sim 123.4</c>.
/// </summary>
public sealed record WaitUntilLastValue(int Index, string Kind, string Value);

/// <summary>How one <c>then</c> action ended: <paramref name="Error"/> is the refusal or failure when <paramref name="Ok"/> is false.</summary>
public sealed record WaitUntilActionResult(string Action, bool Ok, string? Error);
